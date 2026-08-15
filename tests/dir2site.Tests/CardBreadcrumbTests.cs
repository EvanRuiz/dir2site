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
/// The trail above a card's name. With <c>cardBreadcrumbs</c> on — the default — a card carries the
/// folders its item sits in on a quieter line above its title, the same labels its breadcrumb bar
/// shows, so a card says what the thing is and not merely what it is called. Turned off, a card is
/// its bare name again — except a card featured on the home page, whose trail is the point of it.
/// </summary>
public class CardBreadcrumbTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-crumb-" + Guid.NewGuid().ToString("N"));

    public CardBreadcrumbTests() => Directory.CreateDirectory(_root);

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
             {(home ? "home: true" : "")}
             """);
    }

    /// <param name="cardBreadcrumbs">Null leaves the setting at whatever a fresh config defaults to.</param>
    private void Generate(bool? cardBreadcrumbs = true)
    {
        var config = new Dir2SiteModel
        {
            Title = "My Site",
            Footer = "© 2026",
            SiteUrl = "https://example.test",
        };
        if (cardBreadcrumbs.HasValue) config.CardBreadcrumbs = cardBreadcrumbs.Value;

        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        var result = SiteGenerator.Generate(_root, tree, config);
        Assert.Empty(result.Errors);
    }

    /// A tree deep enough to have something to say: Photographs/1890s/{Portrait,Landscape}.jpg.
    private void MakeNestedTree()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakePhoto(nested, "Portrait.jpg", "A Portrait");
        // A second artifact keeps 1890s a collection rather than a folder published as its one item.
        MakePhoto(nested, "Landscape.jpg", "A Landscape");
    }

    /// The trail as the card writes it — its own line, above the title and not inside it.
    private static string Crumb(string trail) =>
        $"<p class=\"card-breadcrumb text-body-secondary small mb-1\">{trail}</p>";

    [AvaloniaFact]
    public void ACardCarriesTheFoldersAboveWhatItPointsAt()
    {
        MakeNestedTree();

        Generate();
        var page = ReadPage("Photographs", "1890s");

        Assert.Contains(Crumb("Photographs › 1890s"), page);
        // The name stays the name: the trail is a line above it, not part of the title.
        Assert.Contains(">A Portrait</a></h5>", page);
        Assert.Contains(Crumb("Photographs"), ReadPage("Photographs"));
    }

    [AvaloniaFact]
    public void ATopLevelCardHasNoTrailToShow()
    {
        // The home page is the only ancestor a top-level folder has, and it isn't worth saying.
        MakeNestedTree();

        Generate();

        Assert.Contains(">Photographs</a></h5>", ReadPage());
        Assert.DoesNotContain("card-breadcrumb", ReadPage());
    }

    [AvaloniaFact]
    public void TurnedOffACardIsItsBareNameAgain()
    {
        MakeNestedTree();

        Generate(cardBreadcrumbs: false);
        var page = ReadPage("Photographs", "1890s");

        Assert.Contains(">A Portrait</a></h5>", page);
        Assert.DoesNotContain("card-breadcrumb", page);
    }

    [AvaloniaFact]
    public void ByDefaultOnlyAFeaturedCardShowsATrail()
    {
        // The default is off: what a folder page's cards would say, its breadcrumb bar has just said.
        var nested = MakeFolder("Photographs", "1890s");
        MakePhoto(nested, "Landscape.jpg", "A Landscape");
        MakePhoto(nested, "Portrait.jpg", "A Portrait", home: true);

        Generate(cardBreadcrumbs: null);

        Assert.Contains(Crumb("Photographs › 1890s"), ReadPage());
        Assert.DoesNotContain("card-breadcrumb", ReadPage("Photographs", "1890s"));
    }

    [AvaloniaFact]
    public void TurnedOffAFeaturedCardKeepsItsTrailAnyway()
    {
        // What the setting turns off is the repetition of the breadcrumb bar on a folder page. The
        // home page has no such bar to repeat, and a card pulled up from three levels down is the
        // whole reason the trail exists — so it is not the setting's to take away.
        var nested = MakeFolder("Photographs", "1890s");
        MakePhoto(nested, "Landscape.jpg", "A Landscape");
        MakePhoto(nested, "Portrait.jpg", "A Portrait", home: true);

        Generate(cardBreadcrumbs: false);

        Assert.Contains(Crumb("Photographs › 1890s"), ReadPage());
        Assert.DoesNotContain("card-breadcrumb", ReadPage("Photographs", "1890s"));
    }

    [AvaloniaFact]
    public void TheMarkersNeverReachACard()
    {
        // '-' and '+' instruct the generator; a card is somewhere a visitor can see.
        var nested = MakeFolder("-Archive", "Newspapers+");
        MakePhoto(nested, "Portrait.jpg", "A Portrait");
        MakePhoto(nested, "Landscape.jpg", "A Landscape");

        Generate();
        var page = ReadPage("Archive", "Newspapers");

        Assert.Contains(Crumb("Archive › Newspapers"), page);
        Assert.DoesNotContain("+", page);
    }

    [AvaloniaFact]
    public void AVideoCardGetsTheSameTrailAsAnythingElse()
    {
        // A video plays in place instead of linking anywhere, so its title is written by a different
        // branch of the card template — the one place this could quietly not apply.
        var nested = MakeFolder("Talks", "2026");
        File.WriteAllText(Path.Combine(nested, "Keynote.url"),
            "[InternetShortcut]\nURL=https://www.youtube.com/watch?v=dQw4w9WgXcQ\n");
        File.WriteAllText(Path.Combine(nested, "Keynote.url.yaml"),
            "type: video\ncaption: The Keynote\nprovider: youtube\nvideoId: dQw4w9WgXcQ\n");
        MakePhoto(nested, "Portrait.jpg", "A Portrait");

        Generate();
        var page = ReadPage("Talks", "2026");

        // Two cards, two trails: the video's is the one that isn't the photo's own.
        Assert.Equal(2, Regex.Matches(page, Regex.Escape(Crumb("Talks › 2026"))).Count);
        Assert.Contains(">The Keynote</h5>", page);
    }
}
