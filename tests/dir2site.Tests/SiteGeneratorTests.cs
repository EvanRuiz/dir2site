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
/// The menu, the site config and a collection's item set are properties of the whole tree, not of
/// the folder being rendered — so the generator can't decide from a folder's own mtime whether its
/// page is stale. It re-renders everything, every time, and only writes what actually changed.
/// These tests pin both halves of that: the freshness, and the untouched mtimes deploys rely on.
/// </summary>
public class SiteGeneratorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-gen-" + Guid.NewGuid().ToString("N"));

    public SiteGeneratorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string SitePath(params string[] parts) =>
        Path.Combine([_root, "_site", .. parts]);

    private string ReadPage(params string[] parts) =>
        File.ReadAllText(SitePath([.. parts, "index.html"]));

    private static Dir2SiteModel Config(string title = "My Site") => new()
    {
        Title = title,
        Footer = "© 2026",
        SiteUrl = "https://example.test",
    };

    private string MakeFolder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Writes a fake artifact plus the sidecar YAML that makes it show up in the tree. The preview
    /// paths are the ones preview generation would have written, so cards get thumbnails without
    /// these tests having to decode an image.
    /// </summary>
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

    private (string Summary, IReadOnlyList<string> Errors) Generate(Dir2SiteModel config)
    {
        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        return SiteGenerator.Generate(_root, tree, config);
    }

    private Dictionary<string, DateTime> PageMtimes() =>
        Directory.EnumerateFiles(SitePath(), "index.html", SearchOption.AllDirectories)
                 .ToDictionary(p => p, File.GetLastWriteTimeUtc);

    [AvaloniaFact]
    public void ANewTopLevelFolder_ReachesTheMenuOnEveryExistingPage()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        // A second artifact keeps 1890s a collection: a folder holding one is published as that
        // artifact, and these tests are about the menu and staleness, not about that.
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");
        MakeFolder("Documents");

        Generate(Config());
        Assert.DoesNotContain("Maps/", ReadPage("Photographs", "1890s"));

        // Nothing under Photographs/1890s is touched, so the old mtime check kept its stale menu.
        MakeFolder("Maps");
        Generate(Config());

        Assert.Contains("Maps/", ReadPage("Photographs", "1890s"));
        Assert.Contains("Maps/", ReadPage("Photographs"));
        Assert.Contains("Maps/", ReadPage("Documents"));
        Assert.Contains("Maps/", ReadPage("Photographs", "1890s", "Portrait"));
    }

    [AvaloniaFact]
    public void AConfigChange_ReachesCollectionAndArtifactPagesAlike()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        // A second artifact keeps 1890s a collection: a folder holding one is published as that
        // artifact, and these tests are about the menu and staleness, not about that.
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");

        Generate(Config("Old Title"));
        Generate(Config("New Title"));

        Assert.Contains("New Title", ReadPage("Photographs", "1890s"));
        Assert.Contains("New Title", ReadPage("Photographs", "1890s", "Portrait"));
        Assert.DoesNotContain("Old Title", ReadPage("Photographs", "1890s", "Portrait"));
    }

    [AvaloniaFact]
    public void AddingAnItem_UpdatesTheFolderAndTheAncestorAboveIt()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        // Two to begin with, so 1890s is a collection before and after: a folder holding a single
        // artifact publishes it as the folder's own index, and the arrival of a second would move
        // that page rather than simply adding a card.
        MakeArtifact(nested, "Sketch.jpg", "A Sketch");

        Generate(Config());
        Assert.DoesNotContain("A Landscape", ReadPage("Photographs", "1890s"));

        MakeArtifact(nested, "Landscape.jpg", "A Landscape");
        Generate(Config());

        Assert.Contains("A Landscape", ReadPage("Photographs", "1890s"));
        // The ancestor's folder card takes its thumbnail from the first artifact found beneath it,
        // and Landscape now sorts ahead of Portrait — so a page two levels up went stale too.
        Assert.Contains("Landscape-preview.jpg", ReadPage("Photographs"));
    }

    [AvaloniaFact]
    public void RegeneratingWithNoChanges_LeavesEveryPageMtimeAlone()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        // A second artifact keeps 1890s a collection: a folder holding one is published as that
        // artifact, and these tests are about the menu and staleness, not about that.
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");
        MakeFolder("Documents");

        Generate(Config());
        var before = PageMtimes();

        Generate(Config());
        var after = PageMtimes();

        // Deploys diff on size + mtime, so an unchanged page must not be rewritten — otherwise
        // every generate would queue the whole site for re-upload.
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var (path, mtime) in before)
            Assert.Equal(mtime, after[path]);
    }

    [AvaloniaFact]
    public void OnlyThePagesThatChanged_GetRewritten()
    {
        MakeFolder("Photographs", "1890s");
        var documents = MakeFolder("Documents");
        MakeArtifact(documents, "Letter.jpg", "A Letter");
        // Keeps Documents a collection, so Letter has a page of its own whose mtime can be compared.
        MakeArtifact(documents, "Memo.jpg", "A Memo");

        Generate(Config());
        var before = PageMtimes();

        MakeArtifact(Path.Combine(_root, "Photographs", "1890s"), "Portrait.jpg", "A Portrait");
        Generate(Config());
        var after = PageMtimes();

        // The 1890s page gained a card and Photographs' folder card gained a thumbnail; Documents
        // is untouched by any of it.
        Assert.NotEqual(before[SitePath("Photographs", "1890s", "index.html")],
                        after[SitePath("Photographs", "1890s", "index.html")]);
        Assert.NotEqual(before[SitePath("Photographs", "index.html")],
                        after[SitePath("Photographs", "index.html")]);
        Assert.Equal(before[SitePath("Documents", "index.html")],
                     after[SitePath("Documents", "index.html")]);
        Assert.Equal(before[SitePath("Documents", "Letter", "index.html")],
                     after[SitePath("Documents", "Letter", "index.html")]);
    }
}
