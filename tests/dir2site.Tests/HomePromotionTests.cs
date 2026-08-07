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
/// Getting something onto the home page from deeper in the tree: <c>home: true</c> on an artifact,
/// and the '+' suffix on a folder. Both are shortcuts rather than moves — the item keeps its
/// ordinary card and its real address, and the extra card on the home page points at it there.
/// </summary>
public class HomePromotionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-home-" + Guid.NewGuid().ToString("N"));

    public HomePromotionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string ReadPage(params string[] parts) =>
        File.ReadAllText(Path.Combine([_root, "_site", .. parts, "index.html"]));

    private string MakeFolder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void MakePhoto(string folder, string fileName, string caption, bool home = false)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        File.WriteAllText(Path.Combine(folder, fileName), "not really a jpeg");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"""
             type: photo
             caption: {caption}
             preview: .dir2site/{stem}/{stem}-preview.jpg
             previewLarge: .dir2site/{stem}/{stem}-preview-large.jpg
             {(home ? "home: true" : "")}
             """);
    }

    private static void MakeVideo(string folder, string fileName, string caption, bool home = false)
    {
        File.WriteAllText(Path.Combine(folder, fileName),
            "[InternetShortcut]\nURL=https://www.youtube.com/watch?v=dQw4w9WgXcQ\n");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"""
             type: video
             caption: {caption}
             provider: youtube
             videoId: dQw4w9WgXcQ
             {(home ? "home: true" : "")}
             """);
    }

    /// Generates, and hands back what the run had to say short of failing.
    private IReadOnlyList<string> Generate()
    {
        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        var result = SiteGenerator.Generate(_root, tree, new Dir2SiteModel
        {
            Title = "My Site",
            Footer = "© 2026",
            SiteUrl = "https://example.test",
        });
        Assert.Empty(result.Errors);
        return result.Warnings;
    }

    [AvaloniaFact]
    public void AMarkedArtifactGetsAHomeCardPointingAtWhereItActuallyLives()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakePhoto(nested, "Apple.jpg", "Apple");
        MakePhoto(nested, "Zebra.jpg", "Zebra", home: true);

        Generate();
        var home = ReadPage();

        Assert.Contains("href=\"Photographs/1890s/Zebra/\"", home);
        Assert.Contains("Zebra", home);
        Assert.DoesNotContain("href=\"Photographs/1890s/Apple/\"", home);
    }

    [AvaloniaFact]
    public void APromotedArtifactKeepsItsOrdinaryCard()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakePhoto(nested, "Apple.jpg", "Apple");
        MakePhoto(nested, "Zebra.jpg", "Zebra", home: true);

        Generate();

        // Still listed where it lives, still linked as a sibling from there.
        Assert.Contains("href=\"Zebra/\"", ReadPage("Photographs", "1890s"));
    }

    [AvaloniaFact]
    public void APromotedArtifactStillHasItsRealBreadcrumbTrail()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakePhoto(nested, "Zebra.jpg", "Zebra", home: true);
        MakePhoto(nested, "Apple.jpg", "Apple");

        Generate();
        var page = File.ReadAllText(
            Path.Combine(_root, "_site", "Photographs", "1890s", "Zebra", "index.html"));

        Assert.Contains("Photographs", page);
        Assert.Contains("1890s", page);
    }

    [AvaloniaFact]
    public void APlusFolderGetsAHomeCardAndLosesThePlusEverywhere()
    {
        var newspapers = MakeFolder("Archive", "Newspapers+");
        MakePhoto(newspapers, "Apple.jpg", "Apple");
        MakePhoto(newspapers, "Zebra.jpg", "Zebra");

        Generate();
        var home = ReadPage();

        Assert.Contains("href=\"Archive/Newspapers/\"", home);
        Assert.DoesNotContain("+", home);
        Assert.True(Directory.Exists(Path.Combine(_root, "_site", "Archive", "Newspapers")));
    }

    [AvaloniaFact]
    public void APlusFolderKeepsItsCardInItsParent()
    {
        var newspapers = MakeFolder("Archive", "Newspapers+");
        MakePhoto(newspapers, "Apple.jpg", "Apple");

        Generate();

        Assert.Contains("href=\"Newspapers/\"", ReadPage("Archive"));
    }

    [AvaloniaFact]
    public void ThePlusSuffixAndTheMinusPrefixAreIndependent()
    {
        // Nav-only in its parent, but still one click from the front door.
        var newspapers = MakeFolder("Archive", "-Newspapers+");
        MakePhoto(newspapers, "Apple.jpg", "Apple");
        MakePhoto(newspapers, "Zebra.jpg", "Zebra");

        Generate();

        Assert.Contains("href=\"Archive/Newspapers/\"", ReadPage());
        Assert.DoesNotContain("href=\"Newspapers/\"", ReadPage("Archive"));
    }

    [AvaloniaFact]
    public void APromotedVideoPlaysOnTheHomePageRatherThanLinkingAnywhere()
    {
        var nested = MakeFolder("Talks", "2026");
        MakeVideo(nested, "Keynote.url", "The Keynote", home: true);

        Generate();
        var home = ReadPage();

        Assert.Contains("dQw4w9WgXcQ", home);
        Assert.Contains("js/video.js", home);
        Assert.DoesNotContain("Keynote/\"", home);
    }

    [AvaloniaFact]
    public void APromotedSoleArtifactLinksToItsFolderRatherThanAPageThatIsNotThere()
    {
        // A folder holding one artifact publishes it as its own index, so there is no About/Story/.
        var about = MakeFolder("Pages", "About");
        File.WriteAllText(Path.Combine(about, "Story.md"), "# Story\n\nHello.\n");
        File.WriteAllText(Path.Combine(about, "Story.md.yaml"),
            "type: markdown\ncaption: Our Story\nhome: true\n");

        Generate();

        Assert.Contains("href=\"Pages/About/\"", ReadPage());
        Assert.False(Directory.Exists(Path.Combine(_root, "_site", "Pages", "About", "Story")));
    }

    [AvaloniaFact]
    public void TwoSiblingsThatPublishToTheSamePlaceAreReported()
    {
        // The markers are stripped from the published name, so these both become /Archive/News/
        // and one overwrites the other. Which one the author meant isn't ours to guess — but going
        // quiet about a folder's worth of pages disappearing isn't an option either.
        MakePhoto(MakeFolder("Archive", "News+"), "Apple.jpg", "Apple");
        MakePhoto(MakeFolder("Archive", "News"), "Zebra.jpg", "Zebra");

        var warnings = Generate();

        Assert.Contains(warnings, w => w.Contains("News+") && w.Contains("publish as \"News/\""));
    }

    [AvaloniaFact]
    public void AnArtifactAndAFolderCompetingForOneAddressAreReported()
    {
        // Foo.jpg publishes to Foo/ and so does a sibling folder Foo — the same overwrite, in the
        // shape that has nothing to do with the markers.
        var archive = MakeFolder("Archive");
        MakePhoto(archive, "News.jpg", "The News");
        MakePhoto(MakeFolder("Archive", "News"), "Zebra.jpg", "Zebra");

        var warnings = Generate();

        Assert.Contains(warnings, w => w.Contains("News.jpg") && w.Contains("publish as \"News/\""));
    }

    [AvaloniaFact]
    public void TwoArtifactsSharingAStemAreReported()
    {
        var archive = MakeFolder("Archive");
        MakePhoto(archive, "News.jpg", "The News");
        File.WriteAllText(Path.Combine(archive, "News.pdf"), "not really a pdf");
        File.WriteAllText(Path.Combine(archive, "News.pdf.yaml"), "type: pdf\ncaption: The News\n");

        var warnings = Generate();

        Assert.Contains(warnings, w => w.Contains("News.pdf") && w.Contains("publish as \"News/\""));
    }

    [AvaloniaFact]
    public void FoldersThatPublishToDifferentPlacesAreNotReported()
    {
        MakePhoto(MakeFolder("Archive", "News+"), "Apple.jpg", "Apple");
        MakePhoto(MakeFolder("Archive", "Letters"), "Zebra.jpg", "Zebra");

        Assert.Empty(Generate());
    }

    [AvaloniaFact]
    public void NothingIsPromotedTwiceWhenItAlreadySitsOnTheHomePage()
    {
        MakeFolder("Newspapers+");
        MakePhoto(Path.Combine(_root, "Newspapers+"), "Apple.jpg", "Apple");

        Generate();
        var home = ReadPage();

        // The nav links there too, so count cards: stretched-link is the card's own anchor.
        const string cardLink = "href=\"Newspapers/\" class=\"stretched-link";
        var first = home.IndexOf(cardLink, StringComparison.Ordinal);
        Assert.True(first >= 0);
        Assert.Equal(-1, home.IndexOf(cardLink, first + 1, StringComparison.Ordinal));
    }
}
