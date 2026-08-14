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

    /// Writes a plain PNG for a figure to point at.
    private string WriteImage(int w, int h)
    {
        var dir = Path.Combine(_dir, "_media");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "p.png");

        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(new SKColor(0x8A, 0x6D, 0x5A));
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 95);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    /// <summary>
    /// An authored figure width is honoured as a fraction of the site's column, so a wide one can
    /// ask for more room than the card has. The text then has to go below the figure: laid out
    /// beside it, the band is narrower than a single word, and the wrapper always places at least
    /// one word per line — so the body copy was drawn straight through the right margin.
    /// </summary>
    [AvaloniaFact]
    public void AFigureTooWideToWrapBeside_DoesNotPushTextIntoTheMargin()
    {
        WriteImage(1200, 900);
        var png = MarkdownPreviewRenderer.RenderArticlePng(WriteArticle(
            "# Title\n\n^^^\n![](_media/p.png){.figure-left width=820}\n^^^ Caption\n\n"
            + string.Join(" ", new string[60].Select(_ => "body copy that has to fit somewhere"))));

        Assert.NotNull(png);
        using var bmp = SKBitmap.Decode(png);

        // Pad is 44, so everything from x = width - 44 rightwards is margin and stays white.
        for (var x = bmp.Width - 44; x < bmp.Width; x++)
            for (var y = 0; y < bmp.Height; y++)
                Assert.True(bmp.GetPixel(x, y) == SKColors.White,
                    $"Ink at ({x}, {y}) — content is being drawn into the right margin.");
    }
}
