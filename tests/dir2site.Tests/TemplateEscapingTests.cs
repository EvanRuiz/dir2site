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
    public void AColorThatIsNotOneIsReportedAndTheDefaultUsed()
    {
        // No escape makes a value safe in a CSS declaration and still a color, so the guard is an
        // allow-list. Straight through, this wrote rules of its own onto every page of the site.
        var folder = Path.Combine(_root, "Photographs");
        Directory.CreateDirectory(folder);
        MakePhoto(folder, "Ordinary");

        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        var result = SiteGenerator.Generate(_root, tree, new Dir2SiteModel
        {
            Title = "My Site",
            BackgroundColor = "#fff; } body { display: none; } x {",
            PrimaryColor = "#33333",              // a typo, which is the everyday version of this
            SecondaryColor = "rebeccapurple",     // a color, and not a hex one
        });

        var css = File.ReadAllText(Path.Combine(_root, "_site", "css", "site.css"));
        Assert.DoesNotContain("display: none; } x {", css);
        Assert.Contains("background-color: #ffffff;", css);
        Assert.Contains("rebeccapurple", css);

        Assert.Contains(result.Warnings, w => w.Contains("backgroundColor") && w.Contains("#ffffff"));
        Assert.Contains(result.Warnings, w => w.Contains("primaryColor") && w.Contains("#33333"));
        // Once for the run, not once per page.
        Assert.Single(result.Warnings, w => w.Contains("backgroundColor"));
    }

    [AvaloniaFact]
    public void ALightFooterGetsDarkTextHoweverItsColorIsWritten()
    {
        // The text color is chosen by reading the band's color, so every form the generator
        // accepts has to be one it can read. Accepting a form it couldn't read is how a footer
        // could be white with white text on it — allowed, unreadable, and unwarned.
        foreach (var (color, dark) in new[]
                 {
                     ("#ffffff", false), ("#000000", true),
                     ("white", false), ("black", true), ("lightyellow", false),
                     ("rebeccapurple", true),                    // CSS knows it, Avalonia doesn't
                     ("rgb(255, 255, 255)", false), ("rgb(0,0,0)", true),
                     ("hsl(0, 0%, 100%)", false), ("hsl(210, 50%, 20%)", true),
                 })
        {
            var root = Path.Combine(_root, "site-" + color.GetHashCode().ToString("x"));
            var folder = Path.Combine(root, "Photographs");
            Directory.CreateDirectory(folder);
            MakePhoto(folder, "Ordinary");

            var tree = DirectoryTraverser.BuildTree(root, new List<string>(), new List<string>());
            var result = SiteGenerator.Generate(root, tree, new Dir2SiteModel
            {
                Title = "My Site",
                Footer = "©",
                FooterColor = color,
                FooterItems = [new FooterItem { Title = "Home", Link = "/", Column = 1 }],
            });

            Assert.Empty(result.Warnings);
            var css = File.ReadAllText(Path.Combine(root, "_site", "css", "site.css"));
            Assert.Contains($"background-color: {color};", css);
            // The light-on-dark rules are the ones a dark band gets.
            Assert.Equal(dark, css.Contains("rgba(255, 255, 255, 0.72)"));
        }
    }

    /// <summary>
    /// The color is published as written, so whatever the readers accept is what lands in the
    /// stylesheet. The alpha slot is where that has gone wrong twice: it is optional and it is last,
    /// so a reader that stops early there leaves the rest of the value to be written out.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("rgb(0,0,0,x);}body{display:none}.x{color:rgb(0,0,0)")]
    [InlineData("hsl(0,0%,0%,x);}body{display:none}.x{color:hsl(0,0%,0%)")]
    [InlineData("rgb(0,0,0,/);}html{background:url(http://evil/x)}.y{color:rgb(0,0,0)")]
    [InlineData("#fff; } body { display: none; } x {")]
    [InlineData("red; background-image: url(http://evil/x)")]
    [InlineData("}</style><script>alert(1)</script><style>{")]
    [InlineData("rgb(0,0,0) /* } body { display:none */")]
    public void NothingCarryingCssSyntaxReachesTheStylesheet(string color)
    {
        var folder = Path.Combine(_root, "Photographs");
        Directory.CreateDirectory(folder);
        MakePhoto(folder, "Ordinary");

        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        var result = SiteGenerator.Generate(_root, tree, new Dir2SiteModel
        {
            Title = "My Site", PrimaryColor = color,
        });

        var css = File.ReadAllText(Path.Combine(_root, "_site", "css", "site.css"));
        // Nothing of it survives — not the rule it was trying to write, and not the color it wore
        // to get in. (The site's own stylesheet has a "display:none" of its own, so the payload has
        // to be looked for as itself rather than by what it was going to say.)
        Assert.DoesNotContain(color, css);
        Assert.DoesNotContain("evil", css);
        Assert.DoesNotContain("<script", css);
        Assert.Contains("background-color: #333333;", css);   // the default, in its place
        Assert.Contains(result.Warnings, w => w.Contains("primaryColor"));
    }

    [AvaloniaTheory]
    [InlineData("rgb(255, 255, 255)")]
    [InlineData("rgba(0,0,0,0.5)")]
    [InlineData("rgb(0 0 0 / 50%)")]
    [InlineData("rgb(100%, 100%, 100%)")]
    [InlineData("hsl(0,0%,100%)")]
    [InlineData("hsla(240,100%,20%,0.8)")]
    [InlineData("hsl(0 0% 100% / 50%)")]
    public void TheWaysAColorIsActuallyWrittenAllStillWork(string color)
    {
        var folder = Path.Combine(_root, "Photographs");
        Directory.CreateDirectory(folder);
        MakePhoto(folder, "Ordinary");

        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        var result = SiteGenerator.Generate(_root, tree, new Dir2SiteModel
        {
            Title = "My Site", PrimaryColor = color,
        });

        Assert.Empty(result.Warnings);
        Assert.Contains(
            $"background-color: {color}",
            File.ReadAllText(Path.Combine(_root, "_site", "css", "site.css")));
    }

    [AvaloniaFact]
    public void AColorNobodyCanReadIsNotAccepted()
    {
        // It would be safe in the stylesheet — an unknown name is a declaration the browser drops —
        // but the footer's text color would be a guess, and the author would never hear about it.
        var folder = Path.Combine(_root, "Photographs");
        Directory.CreateDirectory(folder);
        MakePhoto(folder, "Ordinary");

        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        var result = SiteGenerator.Generate(_root, tree, new Dir2SiteModel
        {
            Title = "My Site", PrimaryColor = "bananas",
        });

        Assert.Contains(result.Warnings, w => w.Contains("primaryColor") && w.Contains("bananas"));
        Assert.DoesNotContain("bananas", File.ReadAllText(Path.Combine(_root, "_site", "css", "site.css")));
    }

    [AvaloniaFact]
    public void ANameOnTwoLinesDoesNotBreakTheViewerScript()
    {
        // A newline is no way out of a javascript string, but it ends one just the same — and the
        // syntax error takes the whole script with it, so the viewer never starts and the page
        // arrives with nothing on it. A filename is allowed a newline, and a caption more so.
        var folder = Path.Combine(_root, "Books");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Atlas.pdf"), "not really a pdf");
        File.WriteAllText(Path.Combine(folder, "Atlas.pdf.yaml"),
            "type: pdf\ncaption: \"An Atlas\\nof Everywhere\"\nauthor: \"A. Cartographer\"\n");
        MakePhoto(folder, "Ordinary");

        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        Assert.Empty(SiteGenerator.Generate(_root, tree, new Dir2SiteModel { Title = "My Site" }).Errors);

        var page = File.ReadAllText(Path.Combine(_root, "_site", "Books", "Atlas", "index.html"));
        var title = Regex.Match(page, "bookTitle: \"([^\"]*)\"").Groups[1].Value;

        Assert.Equal("An Atlas\\nof Everywhere", title);
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
