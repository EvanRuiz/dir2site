// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using dir2site.Models;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// A folder or a file may legally be named with a quote or an angle bracket, and its name becomes a
/// link, an image source and a caption on pages all over the site. The captions were escaped from
/// the start; the addresses beside them were not, so such a name closed the attribute it sat in and
/// the rest of the tag was read as markup. These pin the addresses.
/// </summary>
public class TemplateEscapingTests : IDisposable
{
    /// <summary>
    /// A name carrying what would end an attribute. Windows reserves " &lt; and &gt; in a filename,
    /// so there it can only be an ampersand — which is the honest bound: a name that cannot exist is
    /// not a case that needs guarding, and the escaping still has to be right for the one that can.
    /// On Unix all three are legal and the fixture uses them.
    /// </summary>
    private static readonly string AwkwardFolder = OperatingSystem.IsWindows() ? "A&B" : "A\"B<script>&";
    private static readonly string AwkwardStem = OperatingSystem.IsWindows() ? "Q&x" : "Q\"<x>&";

    /// The same name as HTML spells it, so an expectation follows the fixture rather than repeating it.
    private static string Escaped(string name) =>
        name.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-esc-" + Guid.NewGuid().ToString("N"));

    public TemplateEscapingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string ReadPage(params string[] parts) =>
        File.ReadAllText(Path.Combine([_root, "_site", .. parts, "index.html"]));

    private static void MakePhoto(string folder, string stem)
    {
        File.WriteAllText(Path.Combine(folder, stem + ".jpg"), "not really a jpeg");
        File.WriteAllText(Path.Combine(folder, stem + ".jpg.yaml"),
            $"""
             type: photo
             caption: {stem}
             preview: .dir2site/{stem}/{stem}-preview.jpg
             previewLarge: .dir2site/{stem}/{stem}-preview-large.jpg
             image: .dir2site/{stem}/{stem}.webp
             """);
    }

    /// <summary>
    /// Every attribute value on the page, so a test can ask what is in them rather than guessing
    /// which tag a name reached. Attributes are double-quoted throughout these templates.
    /// </summary>
    private static IEnumerable<string> AttributeValues(string html)
    {
        foreach (Match m in Regex.Matches(html, "[a-zA-Z-]+=\"([^\"]*)\""))
            yield return m.Groups[1].Value;
    }

    private void Generate()
    {
        var folder = Path.Combine(_root, AwkwardFolder);
        Directory.CreateDirectory(folder);
        MakePhoto(folder, AwkwardStem);
        // A second artifact keeps the folder a collection with a page of its own.
        MakePhoto(folder, "Ordinary");

        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        var result = SiteGenerator.Generate(_root, tree, new Dir2SiteModel
        {
            Title = "My Site",
            Footer = "© 2026",
            SiteUrl = "https://example.test",
        });
        Assert.Empty(result.Errors);
    }

    [AvaloniaFact]
    public void AnAwkwardNameNeverEscapesTheAttributeItSitsIn()
    {
        Generate();

        foreach (var page in Directory.EnumerateFiles(
                     Path.Combine(_root, "_site"), "index.html", SearchOption.AllDirectories))
        {
            var html = File.ReadAllText(page);
            var where = Path.GetRelativePath(_root, page);

            // A raw '<' inside an attribute means the quote before it already closed something.
            foreach (var value in AttributeValues(html))
                Assert.DoesNotContain("<", value);

            // And the name is still there, spelled the way HTML spells it.
            Assert.DoesNotContain($"\"{AwkwardFolder}", html);
        }
    }

    [AvaloniaFact]
    public void TheLinksStillPointAtTheRightPlace()
    {
        Generate();

        var folder = Escaped(AwkwardFolder);
        var stem = Escaped(AwkwardStem);

        // The card and the menu entry on the home page, and the card inside the folder.
        Assert.Contains($"href=\"{folder}/\"", ReadPage());
        Assert.Contains($"href=\"{stem}/\"", ReadPage(AwkwardFolder));
        // The folder card's picture is one of the photos inside it, whichever was chosen as cover.
        Assert.Contains($"src=\"{folder}/", ReadPage());
        // Inside the folder the picture is addressed from a page one level up, hence the "../".
        Assert.Contains($"src=\"../{folder}/{stem}/{stem}-preview.jpg\"", ReadPage(AwkwardFolder));
    }

    [AvaloniaFact]
    public void AnAwkwardNameCannotCloseTheViewerScript()
    {
        // The photo page hands the image path to OpenSeadragon inside a <script>, where an HTML
        // escape would corrupt the path instead of guarding it.
        Generate();
        var page = File.ReadAllText(
            Path.Combine(_root, "_site", AwkwardFolder, AwkwardStem, "index.html"));
        var script = page[page.IndexOf("OpenSeadragon(", StringComparison.Ordinal)..];

        var url = Regex.Match(script, "url: \"(.*)\" \\}").Groups[1].Value;

        // Whatever the platform let into the name, none of it can still end the string or the script.
        Assert.DoesNotContain("\"", url.Replace("\\\"", ""));
        Assert.DoesNotContain("<", url);
        Assert.Contains(AwkwardStem.Replace("\"", "\\\"").Replace("<", "\\x3C"), url);
    }
}
