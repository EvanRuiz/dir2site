// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using dir2site.Models;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The reasoning in this project's templates is for whoever edits them next. A visitor downloads
/// the result, and should not be paying to carry the argument.
///
/// Most templates get this for free: their comments are Scriban's <c>{{~ # … ~}}</c>, stripped as
/// the page renders, so a page's markup costs nothing however much is written about it. The
/// stylesheet and the two scripts are the exception — CSS and JavaScript comments are ordinary text
/// and pass straight through. Two thirds of the generated stylesheet was prose, on a
/// render-blocking link in the head of every page.
///
/// These pin both halves: the templates keep every word, and the generated assets carry none of it.
/// </summary>
public class GeneratedAssetsCarryNoCommentsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-comments-" + Guid.NewGuid().ToString("N"));

    public GeneratedAssetsCarryNoCommentsTests()
    {
        Directory.CreateDirectory(_root);

        var album = Path.Combine(_root, "Album");
        Directory.CreateDirectory(album);
        File.WriteAllText(Path.Combine(album, "Apple.jpg"), "not really a jpeg");
        File.WriteAllText(Path.Combine(album, "Apple.jpg.yaml"), "type: photo\ncaption: An Apple\n");
        // A video, so video.js is worth looking at as something a page actually loads.
        File.WriteAllText(Path.Combine(album, "Talk.url"),
            "[InternetShortcut]\r\nURL=https://www.youtube.com/watch?v=AbCdEfGhIjK\r\n");
        File.WriteAllText(Path.Combine(album, "Talk.url.yaml"), "type: video\ncaption: A Talk\n");

        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        var result = SiteGenerator.Generate(_root, tree, new Dir2SiteModel
        {
            Title = "My Site",
            Footer = "© 2026",
            SiteUrl = "https://example.test",
        });
        Assert.Empty(result.Errors);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Generated(params string[] parts) =>
        File.ReadAllText(Path.Combine([_root, "_site", .. parts]));

    private static IEnumerable<int> Occurrences(string text, string needle)
    {
        for (var at = text.IndexOf(needle, StringComparison.Ordinal);
             at >= 0;
             at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            yield return at;
        }
    }

    private static string Template(string name)
    {
        using var stream = AssetLoader.Open(
            new Uri($"avares://dir2site/Assets/templates/{name}.html"));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [AvaloniaTheory]
    [InlineData("site-css", "css", "site.css")]
    [InlineData("site-js", "js", "site.js")]
    [InlineData("video-js", "js", "video.js")]
    public void TheTemplateExplainsItselfAndTheGeneratedAssetDoesNot(
        string template, string folder, string file)
    {
        var generated = Generated(folder, file);

        Assert.DoesNotContain("/*", generated);

        // Every surviving `//` is part of a URL — `https://…` — and nothing else. Stated this way
        // rather than as "no line starts with //", because the strip understands where the strings
        // are: it takes trailing comments too, and the only `//` it must leave alone is one inside
        // a literal.
        foreach (var at in Occurrences(generated, "//"))
        {
            Assert.True(
                at >= 1 && generated[at - 1] == ':',
                $"a `//` survived in {file} at offset {at} that isn't part of a URL: "
                + generated.Substring(Math.Max(0, at - 40), Math.Min(80, generated.Length - at)));
        }

        // The other half, and the reason this is a strip rather than an instruction to write less:
        // the source keeps its reasoning. Either comment form counts — the stylesheet uses /* */
        // and video-js uses //. site-js has no commentary today, so it is exempt from this half.
        if (template != "site-js")
        {
            var source = Template(template);
            Assert.True(
                source.Contains("/*", StringComparison.Ordinal)
                || source.Contains("\n//", StringComparison.Ordinal),
                $"{template}.html has no comments left to strip, which makes the other half of "
                + "this test prove nothing.");
        }
    }

    /// <summary>
    /// The rule is whole-line, because video.js loads the player from an address with a `//` in it.
    /// A strip that ate from any `//` to the end of the line would take the source with it, and the
    /// only sign would be videos that never start.
    /// </summary>
    [AvaloniaFact]
    public void AUrlInsideAStringSurvivesTheStrip()
    {
        Assert.Contains("https://www.youtube.com/iframe_api", Generated("js", "video.js"));
    }

    /// <summary>Stripping text out of a stylesheet is only safe if the rules come through it.</summary>
    [AvaloniaFact]
    public void TheStylesheetStillSaysEverythingItSaid()
    {
        var css = Generated("css", "site.css");

        Assert.Contains(".site-header", css);
        Assert.Contains(".breadcrumb-bar", css);
        Assert.Contains(".artifact-meta", css);
        Assert.Contains(".site-footer", css);
        Assert.Contains("@media", css);
        // Braces balance, so nothing was eaten mid-rule.
        Assert.Equal(css.Split('{').Length, css.Split('}').Length);
    }

    /// <summary>A label on four meta tags, repeated on every page of every site.</summary>
    [AvaloniaFact]
    public void NoPageCarriesTheOpenGraphMarker()
    {
        Assert.DoesNotContain("<!-- Open Graph -->", Generated("index.html"));
        Assert.DoesNotContain("<!-- Open Graph -->", Generated("Album", "index.html"));
        Assert.Contains("og:title", Generated("Album", "index.html"));
    }

    /// <summary>
    /// The size of it, as a number rather than an impression — and a floor under any future
    /// regression: the stylesheet was 31KB of which 20KB was prose.
    /// </summary>
    [AvaloniaFact]
    public void TheStylesheetIsSubstantiallySmallerThanItsTemplate()
    {
        var generated = Generated("css", "site.css").Length;
        var source = Template("site-css").Length;

        Assert.True(
            generated < source / 2,
            $"generated stylesheet is {generated} bytes against a {source}-byte template; "
            + "the comments are supposed to be the difference.");
    }
}
