// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Headless.XUnit;
using dir2site.Models;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The deserializer ignores keys it doesn't recognise, which keeps an unfamiliar sidecar readable
/// but means a misspelled setting is accepted and then quietly does nothing — the artifact looks
/// exactly as if the line had never been written. These pin the warning that says otherwise, and
/// the route it takes to somewhere a person will see it.
/// </summary>
public class UnknownYamlKeyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-keys-" + Guid.NewGuid().ToString("N"));

    public UnknownYamlKeyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Writes a photo and its sidecar, and returns what parsing had to say about it. The artifact
    /// itself must parse — everything here is about a file that loaded and still isn't doing what
    /// it says, so anything landing in the error list would mean the test set itself up wrong.
    /// </summary>
    private List<string> ParseWarnings(string yaml, string fileName = "Apple.jpg")
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, "not really a jpeg");
        File.WriteAllText(path + ".yaml", yaml);

        var errors = new List<string>();
        var warnings = new List<string>();
        Assert.NotNull(YamlParser.TryParseYamlMeta(path, errors, warnings));
        Assert.Empty(errors);
        return warnings;
    }

    [Fact]
    public void AMisspelledSettingIsReported()
    {
        var warnings = ParseWarnings("type: photo\ncaption: Apple\nparentcover: true\n");

        Assert.Contains(warnings, w => w.Contains("parentcover") && w.Contains("Apple.jpg.yaml"));
    }

    [Fact]
    public void EveryKeyTheModelDeclaresIsAccepted()
    {
        // Plain, camelCase, hyphen-aliased, and a subtype's own key.
        var warnings = ParseWarnings(
            """
            type: photo
            caption: Apple
            credit: A. Nother
            url-text: Watch on YouTube
            date: 1890
            preview: .dir2site/Apple/Apple-preview.jpg
            previewLarge: .dir2site/Apple/Apple-preview-large.jpg
            parent-cover: true
            grandparent-cover: false
            home: true
            photographer: A. Nother
            """);

        Assert.Empty(warnings);
    }

    [Fact]
    public void ACommentedOutSettingSaysNothing()
    {
        // A comment is not a key, so the reader that finds unknown keys never sees it.
        var warnings = ParseWarnings("type: photo\ncaption: Apple\n# parentcover: true\n");

        Assert.Empty(warnings);
    }

    [Fact]
    public void SeveralUnknownKeysAreReportedTogether()
    {
        var warnings = ParseWarnings("type: photo\ncaption: Apple\nfoo: 1\nbar: 2\n");

        var report = Assert.Single(warnings);
        Assert.Contains("foo", report);
        Assert.Contains("bar", report);
    }

    [Fact]
    public void AKeyBelongingToAnotherTypeIsStillUnknownHere()
    {
        // publishOriginal is real, but only on a PDF — on a photo it does nothing.
        var warnings = ParseWarnings("type: photo\ncaption: Apple\npublishOriginal: true\n");

        Assert.Contains(warnings, w => w.Contains("publishOriginal"));
    }

    [Fact]
    public void AModelThatDidNotFitIsNotReportedWhenAnotherOneDoes()
    {
        // Without a type token the parser tries each model in turn; the ones that don't fit are how
        // it finds the one that does, and are nobody's problem.
        var warnings = ParseWarnings("caption: Apple\n");

        Assert.Empty(warnings);
    }

    [AvaloniaFact]
    public void GenerateReportsWhatTheTraverserFoundWrong_AsAWarningNotAnError()
    {
        var folder = Path.Combine(_root, "Photographs");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Apple.jpg"), "not really a jpeg");
        File.WriteAllText(Path.Combine(folder, "Apple.jpg.yaml"),
            "type: photo\ncaption: Apple\ngrandparent_cover: true\n");

        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        var (_, _, warnings) = SiteGenerator.Generate(_root, tree, new Dir2SiteModel
        {
            Title = "My Site",
            Footer = "© 2026",
            SiteUrl = "https://example.test",
        });

        Assert.Contains(warnings, w => w.Contains("grandparent_cover"));
    }
}
