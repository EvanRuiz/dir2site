// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using Avalonia.Headless.XUnit;
using dir2site.Models;
using dir2site.Services;
using dir2site.ViewModels;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Markdown articles reference static includes out of "_"-prefixed folders, which the traverser
/// deliberately never scans as artifacts — so site generation has to copy them across verbatim
/// instead. The relative-path cases matter because MarkdownRenderer rewrites a reference to
/// _media/x.webp as ../_media/x.webp, which only resolves if the folder lands beside the article's
/// output directory rather than at the site root.
/// </summary>
public class UnderscoreFolderCopyTests : IDisposable
{
    private readonly string _root;

    public UnderscoreFolderCopyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "d2s-underscore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string rel, string content = "x")
    {
        var p = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private bool InSite(string rel) =>
        File.Exists(Path.Combine(_root, "_site", rel.Replace('/', Path.DirectorySeparatorChar)));

    private void Generate()
    {
        var tree = new DirectoryTreeItem(_root);
        foreach (var dir in Directory.EnumerateDirectories(_root))
            tree.Children.Add(new DirectoryTreeItem(dir));

        SiteGenerator.Generate(_root, tree, new Dir2SiteModel { Title = "Test" });
    }

    [AvaloniaFact]
    public void UnderscoreFolderAtTheRoot_LandsAtTheSiteRoot()
    {
        Write("_media/figure.webp");

        Generate();

        Assert.True(InSite("_media/figure.webp"));
    }

    // The case the ../ rewrite depends on: an article at sub/article.md is emitted to
    // _site/sub/article/index.html, so its ../_media/ must be _site/sub/_media/.
    [AvaloniaFact]
    public void NestedUnderscoreFolder_KeepsItsRelativePosition()
    {
        Write("sub/_media/figure.webp");

        Generate();

        Assert.True(InSite("sub/_media/figure.webp"));
        Assert.False(InSite("_media/figure.webp"));
    }

    [AvaloniaFact]
    public void TheWholeSubtreeIsCopied_NotJustTheTopLevel()
    {
        Write("_media/icons/deep/pin.svg");

        Generate();

        Assert.True(InSite("_media/icons/deep/pin.svg"));
    }

    // _site is where output goes, so copying it into itself would nest a copy of the site on every
    // generate; .dir2site holds previews, which CopyPreviewAssets places deliberately.
    [AvaloniaFact]
    public void SiteAndDotFolders_AreNotCopiedIn()
    {
        Directory.CreateDirectory(Path.Combine(_root, "_site"));
        Write("_site/stale.txt");
        Write(".dir2site/preview-thing.webp");

        Generate();

        Assert.False(InSite("_site/stale.txt"));
        Assert.False(InSite(".dir2site/preview-thing.webp"));
    }

    [AvaloniaFact]
    public void OrdinaryFolders_AreNotCopiedVerbatim()
    {
        Write("photos/holiday.jpg");

        Generate();

        Assert.False(InSite("photos/holiday.jpg"));
    }
}
