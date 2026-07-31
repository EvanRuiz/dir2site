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
/// Preview generation skips work that is already done, which for a photo or a PDF is simply "does
/// the file exist" — those get replaced, never revised, so an existing thumbnail can't be wrong.
/// An article's thumbnail is a rendering of its body, and revising the body is the whole workflow,
/// so the same shortcut left every edited article showing a picture of its old draft.
/// </summary>
public class MarkdownPreviewStalenessTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "d2s-mdstale-" + Guid.NewGuid().ToString("N"));

    public MarkdownPreviewStalenessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string ArticlePath => Path.Combine(_dir, "article.md");

    private string PreviewPath => Path.Combine(_dir, ".dir2site", "article", "preview-article.webp");

    /// <summary>
    /// Writes the body only. The sidecar is created once and then left alone, because generating
    /// previews records their paths in it — and it is exactly those recorded paths that make the
    /// second pass consider the work already done.
    /// </summary>
    private void WriteBody(string body)
    {
        if (!File.Exists(ArticlePath + ".yaml"))
            File.WriteAllText(ArticlePath + ".yaml", "type: markdown\ncaption: An Article\n");
        File.WriteAllText(ArticlePath, body);
    }

    private void GeneratePreviews()
    {
        var tree = DirectoryTraverser.BuildTree(_dir, new List<string>(), new List<string>());
        DirectoryTraverser.GeneratePreviews(tree, new Dir2SiteModel(), progress: null);
    }

    [AvaloniaFact]
    public void EditingTheArticle_RerendersItsThumbnail()
    {
        WriteBody("# Title\n\nThe first draft.");
        GeneratePreviews();

        var firstDraft = File.ReadAllBytes(PreviewPath);

        // Push both thumbnails behind the edit we are about to make. Rendering and editing back to
        // back would otherwise land inside the filesystem's timestamp granularity.
        foreach (var name in new[] { "preview-article.webp", "preview-lg-article.webp" })
            File.SetLastWriteTimeUtc(Path.Combine(_dir, ".dir2site", "article", name),
                                     DateTime.UtcNow.AddMinutes(-5));

        WriteBody("# Title\n\nA thoroughly rewritten body, saying something else entirely.");
        GeneratePreviews();

        Assert.NotEqual(firstDraft, File.ReadAllBytes(PreviewPath));
    }

    [AvaloniaFact]
    public void LeavingTheArticleAlone_LeavesTheThumbnailAlone()
    {
        WriteBody("# Title\n\nThe first draft.");
        GeneratePreviews();

        var before = File.GetLastWriteTimeUtc(PreviewPath);
        GeneratePreviews();

        // Re-rendering every article on every generate would be slow and would churn the mtimes
        // the deploy diff reads, so an untouched article must still be skipped.
        Assert.Equal(before, File.GetLastWriteTimeUtc(PreviewPath));
    }
}
