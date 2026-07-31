// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using dir2site.Services;
using SkiaSharp;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The in-app preview renders at "auto height". Sizing it now takes a measuring pass over a
/// no-draw canvas before the real one is allocated, so these pin the property that pass exists to
/// preserve: the image comes out as tall as the article and no taller, and a longer article gives
/// a taller image. They do not observe the allocation itself, only the result it has to match.
/// </summary>
public class MarkdownPreviewRendererTests : IDisposable
{
    private readonly string _dir;

    public MarkdownPreviewRendererTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "d2s-mdpreview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteArticle(string body)
    {
        var path = Path.Combine(_dir, "article.md");
        File.WriteAllText(path, body);
        return path;
    }

    private static SKImageInfo Measure(byte[] png)
    {
        using var bmp = SKBitmap.Decode(png);
        return bmp.Info;
    }

    [AvaloniaFact]
    public void AShortArticle_GetsAShortCanvas()
    {
        var png = MarkdownPreviewRenderer.RenderArticlePng(WriteArticle("# Title\n\nOne short paragraph."));

        Assert.NotNull(png);
        var info = Measure(png);
        Assert.Equal(1200, info.Width);
        Assert.InRange(info.Height, 1, 600);
    }

    [AvaloniaFact]
    public void ALongerArticle_GetsATallerCanvasThanAShortOne()
    {
        var shortPng = MarkdownPreviewRenderer.RenderArticlePng(WriteArticle("# Title\n\nOne short paragraph."));
        var longPng = MarkdownPreviewRenderer.RenderArticlePng(
            WriteArticle("# Title\n\n" + string.Join("\n\n", new string[40].Select(_ => "Another paragraph of body copy."))));

        Assert.NotNull(shortPng);
        Assert.NotNull(longPng);
        Assert.True(Measure(longPng).Height > Measure(shortPng).Height);
    }

    [AvaloniaFact]
    public void AnUnreadableFile_RendersNothingRatherThanThrowing()
    {
        Assert.Null(MarkdownPreviewRenderer.RenderArticlePng(Path.Combine(_dir, "missing.md")));
    }
}
