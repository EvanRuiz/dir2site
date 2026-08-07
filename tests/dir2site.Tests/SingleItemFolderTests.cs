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
/// A folder holding one article and nothing else has no collection to present — its page would be
/// a single card pointing at the thing you already asked for. It publishes the article as its own
/// index instead, so clicking "About" in the menu lands on the article rather than on a page about
/// the article.
///
/// The page then sits level with its source rather than one below, which is what most of these
/// guard: every path that assumed the deeper location has to come back a level, and getting one
/// wrong shows up as a broken image rather than as a failure.
/// </summary>
public class SingleItemFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-single-" + Guid.NewGuid().ToString("N"));

    public SingleItemFolderTests() => Directory.CreateDirectory(_root);

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

    private void MakeArticle(string folder, string fileName, string caption, string body = "Hello.")
    {
        File.WriteAllText(Path.Combine(folder, fileName), body);
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"type: markdown\ncaption: {caption}\n");
    }

    private void MakePhoto(string folder, string fileName, string caption)
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
    public void TheArticleIsTheFoldersOwnPage()
    {
        var about = MakeFolder("-About");
        MakeArticle(about, "Our Story.md", "Our Story", "# Our Story\n\nWe began in 1994.");

        Generate();

        // The article is served at /About/, and there is no page-about-a-page below it.
        var page = ReadPage("About");
        Assert.Contains("We began in 1994", page);
        Assert.False(File.Exists(SitePath("About", "Our Story", "index.html")));
    }

    [AvaloniaFact]
    public void TheMenuLinkIsUnchanged()
    {
        // The folder's URL doesn't move, so nothing that links to it needs to know about any of this.
        var about = MakeFolder("-About");
        MakeArticle(about, "Our Story.md", "Our Story");
        MakeFolder("Photographs");

        Generate();

        Assert.Contains("href=\"About/\"", ReadPage());
    }

    [AvaloniaFact]
    public void RelativeMediaInTheArticleStillResolves()
    {
        // The ../ the renderer adds for a nested page would be one level too many here.
        var about = MakeFolder("-About");
        MakeArticle(about, "Our Story.md", "Our Story", "![team](_media/team.webp)");
        var media = MakeFolder("-About", "_media");
        File.WriteAllText(Path.Combine(media, "team.webp"), "not really a webp");

        Generate();
        var page = ReadPage("About");

        Assert.Contains("src=\"_media/team.webp\"", page);
        Assert.DoesNotContain("../_media/team.webp", page);
        Assert.True(File.Exists(SitePath("About", "_media", "team.webp")));
    }

    [AvaloniaFact]
    public void APhotosViewerAssetsStillResolve()
    {
        // A photo's generated assets live in {folder}/{stem}/, addressed by bare filename from the
        // page's own directory — which the page has just left.
        var gallery = MakeFolder("Gallery");
        MakePhoto(gallery, "Portrait.jpg", "A Portrait");
        var previews = MakeFolder("Gallery", ".dir2site", "Portrait");
        File.WriteAllText(Path.Combine(previews, "Portrait-preview-large.jpg"), "thumb");

        Generate();
        var page = ReadPage("Gallery");

        Assert.Contains("Portrait/Portrait-preview-large.jpg", page);
    }

    [AvaloniaFact]
    public void TwoItemsStillGetACollectionPage()
    {
        var folder = MakeFolder("Writing");
        MakeArticle(folder, "First.md", "First");
        MakeArticle(folder, "Second.md", "Second");

        Generate();

        // Cards, and each article keeps its own page.
        Assert.True(File.Exists(SitePath("Writing", "First", "index.html")));
        Assert.True(File.Exists(SitePath("Writing", "Second", "index.html")));
        Assert.Contains("First", ReadPage("Writing"));
    }

    [AvaloniaFact]
    public void ALoneVideoKeepsItsCollectionPage()
    {
        // A video plays inline and has no page of its own, so there is nothing to promote.
        var folder = MakeFolder("Talks");
        File.WriteAllText(Path.Combine(folder, "Talk.url"),
            "[InternetShortcut]\r\nURL=https://youtu.be/AbCdEfGhIjK\r\n");

        Generate();

        var page = ReadPage("Talks");
        Assert.Contains("AbCdEfGhIjK", page);
    }

    [AvaloniaFact]
    public void ALoneSubfolderIsNotFollowed()
    {
        // Collapsing chains of folders gets surprising quickly, so a folder containing only another
        // folder keeps its own page.
        MakeFolder("Archive");
        var inner = MakeFolder("Archive", "1890s");
        MakeArticle(inner, "Notes.md", "Notes");

        Generate();

        Assert.Contains("1890s", ReadPage("Archive"));
        Assert.Contains("Notes", ReadPage("Archive", "1890s"));
    }

    [AvaloniaFact]
    public void ACrossArticleLinkResolvesFromTheFoldersOwnIndex()
    {
        // The published page is level with its source here, so it needs no ../ — but the .md target
        // is still wrong, because the site publishes the article as a folder and never as a file.
        var about = MakeFolder("About");
        MakeArticle(about, "Our Story.md", "Our Story", "See the [notes](../Notes/Colophon.md).");
        var notes = MakeFolder("Notes");
        MakeArticle(notes, "Colophon.md", "Colophon");
        MakeArticle(notes, "Other.md", "Other");

        Generate();
        var page = ReadPage("About");

        Assert.Contains("href=\"../Notes/Colophon/\"", page);
        Assert.DoesNotContain("Colophon.md", page);
        Assert.True(File.Exists(SitePath("Notes", "Colophon", "index.html")));
    }

    [AvaloniaFact]
    public void TheSiteRootAlwaysKeepsItsHomePage()
    {
        // Even a site with one article needs somewhere for the menu and the title to live.
        MakeArticle(_root, "Only.md", "The Only Thing");

        Generate();

        Assert.Contains("My Site", ReadPage());
        Assert.True(File.Exists(SitePath("Only", "index.html")));
    }
}
