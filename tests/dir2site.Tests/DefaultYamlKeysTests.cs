// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dir2site.Models;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// A yaml should say what it can hold. Settings that appear in no default file — <c>home</c> and
/// the cover markers went years like this — are findable only by reading the docs or the source,
/// so every scaffolded yaml lists its type's full key set, and a file written before a feature
/// existed picks the new keys up on the next scan.
/// </summary>
public class DefaultYamlKeysTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-defaults-" + Guid.NewGuid().ToString("N"));

    public DefaultYamlKeysTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// Writes the artifact (and its yaml, when given one), parses it, and hands back the yaml as
    /// it stands afterwards — which for a file that had none is the scaffold, and for one that did
    /// is whatever the backfill made of it.
    private string ParseAndReadYaml(string fileName, string? yaml = null, string? body = null)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, body ?? "not really a media file");
        if (yaml != null) File.WriteAllText(path + ".yaml", yaml);

        var errors = new List<string>();
        Assert.NotNull(YamlParser.TryParseYamlMeta(path, errors, new List<string>()));
        Assert.Empty(errors);
        return File.ReadAllText(path + ".yaml");
    }

    private static IEnumerable<string> KeysOf(string yaml) =>
        yaml.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(':')[0]);

    private const string ShortcutBody =
        "[InternetShortcut]\nURL=https://www.youtube.com/watch?v=dQw4w9WgXcQ\n";

    // ---- the scaffold ------------------------------------------------------

    [Fact]
    public void AScaffoldedPhotoListsEverySettingAPhotoHas()
    {
        var yaml = ParseAndReadYaml("Apple.jpg");

        Assert.Equal(
            ["type", "caption", "credit", "photographer",
             "date", "url", "url-text", "home", "parent-cover", "grandparent-cover"],
            KeysOf(yaml));
    }

    [Fact]
    public void AScaffoldedPdfKeepsItsOwnSettingsToo()
    {
        var yaml = ParseAndReadYaml("Report.pdf");

        Assert.Contains("author:", yaml);
        Assert.Contains("publishOriginal: false", yaml);
        Assert.Contains("url:", yaml);
        Assert.Contains("home: false", yaml);
        Assert.DoesNotContain("photographer", yaml);
    }

    [Fact]
    public void AScaffoldedVideoStillLeadsWithWhatTheShortcutSays()
    {
        var yaml = ParseAndReadYaml("Talk.url", body: ShortcutBody);

        Assert.Contains("provider: youtube", yaml);
        Assert.Contains("videoId: dQw4w9WgXcQ", yaml);
        Assert.Contains("url-text:", yaml);
        Assert.Contains("home: false", yaml);
    }

    /// The keys the tool writes for itself stay out: a blank one only invites hand-editing a value
    /// the next generate overwrites.
    [Fact]
    public void AScaffoldDoesNotAdvertiseTheKeysTheToolOwns()
    {
        var keys = KeysOf(ParseAndReadYaml("Apple.jpg"));

        foreach (var owned in new[] { "id", "preview", "previewLarge", "image", "overlays", "cover" })
            Assert.DoesNotContain(owned, keys);
    }

    [Theory]
    [InlineData("Apple.jpg", null)]
    [InlineData("Report.pdf", null)]
    [InlineData("Notes.md", null)]
    [InlineData("Talk.url", ShortcutBody)]
    public void AScaffoldedYamlSaysNothingTheParserDoesNotUnderstand(string fileName, string? body)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, body ?? "not really a media file");

        // First pass writes the scaffold; the second reads it back and judges its keys.
        var errors = new List<string>();
        Assert.NotNull(YamlParser.TryParseYamlMeta(path, errors, new List<string>()));

        var warnings = new List<string>();
        Assert.NotNull(YamlParser.TryParseYamlMeta(path, errors, warnings));
        Assert.Empty(errors);
        Assert.Empty(warnings);
    }

    // ---- the backfill ------------------------------------------------------

    [Fact]
    public void AYamlWrittenBeforeTheseSettingsExistedGainsThem()
    {
        var yaml = ParseAndReadYaml("Apple.jpg", "type: photo\ncaption: Apple\n");

        foreach (var key in new[]
                 { "credit", "photographer", "date", "url", "url-text", "home", "parent-cover", "grandparent-cover" })
            Assert.Contains(key, KeysOf(yaml));
    }

    [Fact]
    public void TheBackfilledFileStillParses()
    {
        var path = Path.Combine(_root, "Apple.jpg");
        File.WriteAllText(path, "not really a jpeg");
        File.WriteAllText(path + ".yaml", "type: photo\ncaption: Apple\n");

        var errors = new List<string>();
        Assert.NotNull(YamlParser.TryParseYamlMeta(path, errors, new List<string>()));

        var warnings = new List<string>();
        var artifact = YamlParser.TryParseYamlMeta(path, errors, warnings);
        Assert.Empty(errors);
        Assert.Empty(warnings);
        Assert.Equal("Apple", artifact!.Caption);
    }

    /// The whole reason this goes through YamlDocumentEditor rather than a rewrite.
    [Fact]
    public void CommentsAndOrderSurviveTheBackfill()
    {
        var original =
            """
            # The one my grandmother kept.
            caption: Apple
            type: photo

            # Filled in from the album's flyleaf.
            credit: A. Nother
            """;

        var yaml = ParseAndReadYaml("Apple.jpg", original);

        Assert.StartsWith(original.TrimEnd(), yaml.TrimEnd()[..original.TrimEnd().Length]);
        Assert.Contains("# The one my grandmother kept.", yaml);
        Assert.Contains("# Filled in from the album's flyleaf.", yaml);
    }

    [Fact]
    public void TheBackfillNeverRewritesWhatTheFileAlreadySays()
    {
        var yaml = ParseAndReadYaml(
            "Apple.jpg",
            "type: photo\ncaption: Apple\nhome: true\ncredit:\nurl: https://example.org/apple\n");

        Assert.Contains("home: true", yaml);
        Assert.Contains("url: https://example.org/apple", yaml);
        // A blank the site owner left blank is a decision, not a gap.
        Assert.Contains("credit:", yaml);
        Assert.DoesNotContain("credit: ", yaml);
    }

    /// A scan touches every yaml in a project; one that is already complete must come back byte for
    /// byte, or every scan dirties the whole tree.
    [Fact]
    public void AFileThatIsAlreadyCompleteIsNotWrittenAtAll()
    {
        var path = Path.Combine(_root, "Apple.jpg");
        File.WriteAllText(path, "not really a jpeg");

        var errors = new List<string>();
        Assert.NotNull(YamlParser.TryParseYamlMeta(path, errors, new List<string>()));

        var scaffolded = File.ReadAllText(path + ".yaml");
        var writtenAt = File.GetLastWriteTimeUtc(path + ".yaml");

        Assert.NotNull(YamlParser.TryParseYamlMeta(path, errors, new List<string>()));

        Assert.Equal(scaffolded, File.ReadAllText(path + ".yaml"));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(path + ".yaml"));
    }

    /// <summary>
    /// The one key where blank and false are different answers. <c>parent-cover</c> is nullable so
    /// that a pre-rename project's <c>cover: true</c> still decides; writing false would take the
    /// folder picture away from a file that never asked to give it up.
    /// </summary>
    [Fact]
    public void TheBackfillLeavesALegacyCoverStillChoosing()
    {
        var path = Path.Combine(_root, "Apple.jpg");
        File.WriteAllText(path, "not really a jpeg");
        File.WriteAllText(path + ".yaml", "type: photo\ncaption: Apple\ncover: true\n");

        var errors = new List<string>();
        Assert.NotNull(YamlParser.TryParseYamlMeta(path, errors, new List<string>()));

        // Read back from the file the backfill left, which is what the next scan sees.
        var artifact = YamlParser.TryParseYamlMeta(path, errors, new List<string>());
        Assert.Empty(errors);
        Assert.True(artifact!.IsParentCover);
        Assert.Null(artifact.ParentCover);
    }

    /// The type token is matched case-insensitively everywhere else, so a file that parses as a
    /// photo has to be backfilled as one.
    [Fact]
    public void AMixedCaseTypeStillGetsItsOwnSettings()
    {
        var yaml = ParseAndReadYaml("Apple.jpg", "type: Photo\ncaption: Apple\n");

        Assert.Contains("photographer", KeysOf(yaml));
    }

    /// A yaml old enough to have no type: at all is the likeliest to be missing settings.
    [Fact]
    public void AYamlWithNoTypeIsBackfilledToo()
    {
        var yaml = ParseAndReadYaml("Apple.jpg", "caption: Apple\ncredit: A. Nother\n");

        Assert.Contains("home", KeysOf(yaml));
        Assert.Contains("url", KeysOf(yaml));
    }

    [Fact]
    public void ABackfillOnlyAddsKeysThatTypeActuallyHas()
    {
        var photo = ParseAndReadYaml("Apple.jpg", "type: photo\ncaption: Apple\n");
        Assert.DoesNotContain("author", KeysOf(photo));
        Assert.DoesNotContain("publishOriginal", KeysOf(photo));

        var pdf = ParseAndReadYaml("Report.pdf", "type: pdf\ncaption: Report\n");
        Assert.DoesNotContain("photographer", KeysOf(pdf));
        Assert.Contains("author", KeysOf(pdf));
    }

    /// Housekeeping is never worth a file. A yaml that doesn't read is a problem to report, not one
    /// to start editing — and least of all one to rewrite from a template.
    [Fact]
    public void ABrokenYamlIsLeftExactlyAsFound()
    {
        var path = Path.Combine(_root, "Apple.jpg");
        File.WriteAllText(path, "not really a jpeg");
        var broken = "type: photo\ncaption: [Apple\ncredit: A. Nother\n";
        File.WriteAllText(path + ".yaml", broken);

        var errors = new List<string>();
        Assert.Null(YamlParser.TryParseYamlMeta(path, errors, new List<string>()));

        Assert.NotEmpty(errors);
        Assert.Equal(broken, File.ReadAllText(path + ".yaml"));
    }

    // ---- the two lists agreeing -------------------------------------------

    /// <summary>
    /// The guard that keeps this honest: add a property to a model and forget the key set, and the
    /// setting ships invisible again. Fails here instead.
    /// </summary>
    [Theory]
    [InlineData("photo", typeof(Photo))]
    [InlineData("deepzoom", typeof(Deepzoom))]
    [InlineData("pdf", typeof(Pdf))]
    [InlineData("markdown", typeof(MarkdownPage))]
    [InlineData("video", typeof(Video))]
    public void EverySettingAModelHasIsInThatTypesDefaultKeys(string token, Type modelType)
    {
        var authored = YamlParser.DeclaredKeys(modelType)
            .Where(k => !YamlParser.ToolOwnedKeys.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal);

        var defaults = YamlParser.DefaultKeys(token)
            .Select(kv => kv.Key)
            .Append("caption")  // written by the scaffolder from the filename, not defaulted
            .OrderBy(k => k, StringComparer.Ordinal);

        Assert.Equal(authored, defaults);
    }
}
