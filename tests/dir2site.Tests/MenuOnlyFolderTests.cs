// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using dir2site.Models;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Folders named with a leading '-' are navigation-only: "-About" is a section you reach from the
/// menu, not one of the collections the site is presenting. It keeps its page and its menu entry,
/// loses its card on the parent page, and sits after the ordinary folders in the nav.
///
/// The marker is an instruction to the generator, so nothing a visitor sees should contain it.
/// </summary>
public class MenuOnlyFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-menu-" + Guid.NewGuid().ToString("N"));

    public MenuOnlyFolderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string SitePath(params string[] parts) => Path.Combine([_root, "_site", .. parts]);

    private string ReadPage(params string[] parts) =>
        File.ReadAllText(SitePath([.. parts, "index.html"]));

    private string MakeFolder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private void MakeArtifact(string folder, string fileName, string caption)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        File.WriteAllText(Path.Combine(folder, fileName), "not really a jpeg");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"""
             type: photo
             caption: {caption}
             preview: .dir2site/{stem}/{stem}-preview.jpg
             previewLarge: .dir2site/{stem}/{stem}-preview-large.jpg
             """);
    }

    private void Generate()
    {
        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        SiteGenerator.Generate(_root, tree, new Dir2SiteModel
        {
            Title = "My Site",
            Footer = "© 2026",
            SiteUrl = "https://example.test",
        });
    }

    [AvaloniaFact]
    public void ItGetsAPageAtTheNameWithoutTheMarker()
    {
        MakeFolder("-About");
        MakeFolder("Photographs");

        Generate();

        Assert.True(File.Exists(SitePath("About", "index.html")));
        Assert.False(Directory.Exists(SitePath("-About")));
    }

    [AvaloniaFact]
    public void ItIsInTheMenuButNotOnTheHomePage()
    {
        MakeFolder("-About");
        var photos = MakeFolder("Photographs");
        MakeArtifact(photos, "Portrait.jpg", "A Portrait");

        Generate();
        var home = ReadPage();

        // In the nav…
        Assert.Contains("<a class=\"nav-link\" href=\"About/\">About</a>", home);
        // …but no card linking to it.
        Assert.DoesNotContain("stretched-link\" href=\"About/\"", home);
        // An ordinary folder still gets both.
        Assert.Contains("Photographs/", home);
    }

    [AvaloniaFact]
    public void ItSitsAfterTheOrdinaryFoldersInTheMenu()
    {
        // Created in an order where plain alphabetical sorting would put the marked folder first,
        // since '-' sorts ahead of letters.
        MakeFolder("-About");
        MakeFolder("Photographs");
        MakeFolder("Zoology");

        Generate();
        var home = ReadPage();

        var photographs = home.IndexOf("href=\"Photographs/\"", StringComparison.Ordinal);
        var zoology = home.IndexOf("href=\"Zoology/\"", StringComparison.Ordinal);
        var about = home.IndexOf("href=\"About/\"", StringComparison.Ordinal);

        Assert.True(photographs < zoology, "ordinary folders keep their A-Z order");
        Assert.True(zoology < about, "the marked folder comes last");
    }

    [AvaloniaFact]
    public void TheMarkerNeverReachesTheVisitor()
    {
        var about = MakeFolder("-About");
        MakeArtifact(about, "Team.jpg", "The Team");

        Generate();

        foreach (var page in Directory.EnumerateFiles(SitePath(), "*.html", SearchOption.AllDirectories))
            Assert.DoesNotContain("-About", File.ReadAllText(page));
    }

    [AvaloniaFact]
    public void ItsOwnPageAndArtifactsStillWork()
    {
        var about = MakeFolder("-About");
        MakeArtifact(about, "Team.jpg", "The Team");
        // Two, so the folder stays a collection — one artifact on its own is published as the
        // folder's index instead, which SingleItemFolderTests covers.
        MakeArtifact(about, "Office.jpg", "The Office");

        Generate();

        var page = ReadPage("About");
        Assert.Contains("About", page);
        Assert.Contains("The Team", page);
        // The artifact page underneath it is generated and reachable.
        Assert.True(File.Exists(SitePath("About", "Team", "index.html")));
    }

    [AvaloniaFact]
    public void ItsPreviewsLandWhereItsPageLooksForThem()
    {
        // The thumbnail src is built from the source path, so a marked folder is where page and
        // asset paths would most easily drift apart.
        var about = MakeFolder("-About");
        MakeArtifact(about, "Team.jpg", "The Team");
        MakeArtifact(about, "Office.jpg", "The Office");
        var previewDir = Path.Combine(about, ".dir2site", "Team");
        Directory.CreateDirectory(previewDir);
        File.WriteAllText(Path.Combine(previewDir, "Team-preview.jpg"), "thumb");

        Generate();

        Assert.True(File.Exists(SitePath("About", "Team", "Team-preview.jpg")));
        Assert.Contains("Team/Team-preview.jpg", ReadPage("About"));
    }

    [AvaloniaFact]
    public void ASingleDashFolderIsLeftAlone()
    {
        // The marker needs a name after it; a folder called "-" is just an oddly named folder.
        MakeFolder("-");

        Generate();

        Assert.True(Directory.Exists(SitePath("-")));
    }

    [AvaloniaFact]
    public void ItWorksBelowTheTopLevel()
    {
        var photos = MakeFolder("Photographs");
        MakeArtifact(photos, "Cover.jpg", "Cover");
        MakeFolder("Photographs", "-Credits");

        Generate();

        Assert.True(File.Exists(SitePath("Photographs", "Credits", "index.html")));
        Assert.DoesNotContain("stretched-link\" href=\"Credits/\"", ReadPage("Photographs"));
    }
}
