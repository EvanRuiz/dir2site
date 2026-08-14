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
/// An artifact's link out to where it came from: <c>url</c> with <c>url-text</c> for the words.
/// It shows on the artifact's own page, under the credit line — not on the card, where the only
/// link is the one to the artifact itself.
/// </summary>
public class ArtifactSourceLinkTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-link-" + Guid.NewGuid().ToString("N"));

    public ArtifactSourceLinkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void MakePhoto(string fileName, string extra)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        File.WriteAllText(Path.Combine(_root, fileName), "not really a jpeg");
        File.WriteAllText(Path.Combine(_root, fileName + ".yaml"),
            $"""
             type: photo
             caption: {stem}
             credit: A. Nother
             preview: .dir2site/{stem}/{stem}-preview.jpg
             previewLarge: .dir2site/{stem}/{stem}-preview-large.jpg
             {extra}
             """);
    }

    private void Generate()
    {
        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        var result = SiteGenerator.Generate(_root, tree, new Dir2SiteModel
        {
            Title = "My Site",
            Footer = "© 2026",
            SiteUrl = "https://example.test",
            SecondaryColor = "#AA3311",
        });
        Assert.Empty(result.Errors);
    }

    private string ArtifactPage(string stem) =>
        File.ReadAllText(Path.Combine(_root, "_site", stem, "index.html"));

    private string CollectionPage() =>
        File.ReadAllText(Path.Combine(_root, "_site", "index.html"));

    [AvaloniaFact]
    public void APhotoWithAUrlLinksOutFromItsOwnPage()
    {
        MakePhoto("Apple.jpg", "url: https://example.org/apple\nurl-text: See the original");
        Generate();

        var page = ArtifactPage("Apple");
        Assert.Contains("href=\"https://example.org/apple\"", page);
        Assert.Contains("See the original", page);
        Assert.Contains("rel=\"noopener noreferrer\"", page);
        Assert.Contains("bi-box-arrow-up-right", page);
    }

    /// The icon has to sit inside the anchor, or it is neither the link's colour nor its click.
    [AvaloniaFact]
    public void TheIconIsPartOfTheLink()
    {
        MakePhoto("Apple.jpg", "url: https://example.org/apple\nurl-text: See the original");
        Generate();

        Assert.Contains(
            "See the original<i class=\"bi bi-box-arrow-up-right\" aria-hidden=\"true\"></i></a>",
            ArtifactPage("Apple"));
    }

    /// Silently dropping a url because its text is blank is the bug this feature came from.
    [AvaloniaFact]
    public void ABlankUrlTextFallsBackToTheAddress()
    {
        MakePhoto("Apple.jpg", "url: https://example.org/apple\nurl-text:");
        Generate();

        var page = ArtifactPage("Apple");
        Assert.Contains("href=\"https://example.org/apple\"", page);
        Assert.Contains(">https://example.org/apple<", page);
    }

    [AvaloniaFact]
    public void NoUrlMeansNoLink()
    {
        MakePhoto("Apple.jpg", "url:\nurl-text: See the original");
        Generate();

        var page = ArtifactPage("Apple");
        Assert.DoesNotContain("artifact-link", page);
        Assert.DoesNotContain("See the original", page);
    }

    /// The card's one link is the artifact itself; a second one competing with it is why this
    /// lives on the page instead.
    [AvaloniaFact]
    public void TheCardDoesNotCarryTheLink()
    {
        MakePhoto("Apple.jpg", "url: https://example.org/apple\nurl-text: See the original");
        Generate();

        var collection = CollectionPage();
        Assert.DoesNotContain("https://example.org/apple", collection);
        Assert.Contains("href=\"Apple/\"", collection);
    }

    [AvaloniaFact]
    public void TheLinkTakesTheSitesSecondaryColour()
    {
        MakePhoto("Apple.jpg", "url: https://example.org/apple\nurl-text: See the original");
        Generate();

        var css = File.ReadAllText(Path.Combine(_root, "_site", "css", "site.css"));
        Assert.Contains(".artifact-link { color: #AA3311", css);
    }

    [AvaloniaFact]
    public void AUrlFromAYamlIsEscapedLikeAnyOtherValue()
    {
        MakePhoto("Apple.jpg", "url: \"https://example.org/a?x=1&y=2\"\nurl-text: \"Bell & Co\"");
        Generate();

        var page = ArtifactPage("Apple");
        Assert.Contains("x=1&amp;y=2", page);
        Assert.Contains("Bell &amp; Co", page);
    }

    [AvaloniaFact]
    public void APdfPageCarriesTheLinkToo()
    {
        File.WriteAllText(Path.Combine(_root, "Report.pdf"), "not really a pdf");
        File.WriteAllText(Path.Combine(_root, "Report.pdf.yaml"),
            """
            type: pdf
            caption: Report
            url: https://example.org/report
            url-text: Read the filing
            """);
        Generate();

        Assert.Contains("https://example.org/report", ArtifactPage("Report"));
    }
}
