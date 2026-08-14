// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia.Platform;
using ImageMagick;
using SkiaSharp;

namespace dir2site.Services;

/// <summary>
/// Renders a Markdown file to catalog thumbnails (the same WebP sizes/paths the image and PDF
/// previews use) by laying the article out with a small custom text-flow engine drawn on SkiaSharp.
/// The same layout also backs the in-app preview pane via <see cref="RenderArticlePng"/>.
/// </summary>
/// <remarks>
/// Avalonia is a box-layout system with no inline-exclusion flow, so it cannot wrap text around a
/// floated figure the way CSS does — which is why Markdown.Avalonia was dropped rather than kept
/// for the in-app view. Rather than approximate with columns, this implements the classic "line box
/// with exclusion rectangle" algorithm: the leading heading is full width, body text wraps beside
/// the floated figure while its <c>y</c> overlaps the figure, then reflows to full width once it
/// clears it. It is an impression of the published page, not an exact reproduction; the published
/// HTML is rendered separately and properly by <see cref="MarkdownRenderer"/>.
/// </remarks>
public static partial class MarkdownPreviewRenderer
{
    private const int LargeWidth = 1200, LargeHeight = 900;
    private const int SmallWidth = 800, SmallHeight = 600;
    private const float Pad = 44f;

    // The gutter between a floated figure and the text beside it, and the narrowest that text
    // column is allowed to get before the figure stops floating — about six words at the 24px
    // body size, below which a card reads as a ragged sliver rather than a paragraph.
    private const float FloatGap = 26f, MinTextBand = 330f;

    // The column the published article is laid out in (.markdown-body's max-width in
    // Assets/templates/site-css.html). An authored figure width is read as a fraction of it, so
    // this preview and the page agree about how big a figure looks. SiteColumnWidthTests fails if
    // the two drift apart.
    internal const float SiteColumnWidth = 820f;

    /// <summary>
    /// Renders <paramref name="mdFile"/> to <c>.dir2site/{stem}/preview-{stem}.webp</c> (800×600)
    /// and <c>preview-lg-{stem}.webp</c> (1200×900). Returns the two relative filenames, or null.
    /// </summary>
    public static (string Preview, string PreviewLarge)? RenderToWebpPreviews(
        string mdFile, string traversalRoot, IProgress<string>? progress = null)
    {
        var fileDir = Path.GetDirectoryName(mdFile) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(mdFile);

        var outDir = Path.GetFullPath(Path.Combine(fileDir, ".dir2site", stem));
        Directory.CreateDirectory(outDir);

        var previewFile      = $"preview-{stem}.webp";
        var previewLargeFile = $"preview-lg-{stem}.webp";
        var previewPath      = Path.Combine(outDir, previewFile);
        var previewLargePath = Path.Combine(outDir, previewLargeFile);

        var previewFileName      = $".dir2site/{stem}/{previewFile}";
        var previewLargeFileName = $".dir2site/{stem}/{previewLargeFile}";

        string text;
        try { text = File.ReadAllText(mdFile); }
        catch { return null; }

        progress?.Report($"Rendering markdown preview: {Path.GetFileName(mdFile)}");

        byte[]? png;
        try
        {
            var figure = ExtractLeadFigure(ref text, fileDir);
            var blocks = ParseBlocks(Clean(text));
            png = RenderToPng(blocks, figure, LargeHeight);
        }
        catch
        {
            return null;
        }
        if (png is null) return null;

        WriteWebp(png, previewLargePath, LargeWidth, LargeHeight);
        WriteWebp(png, previewPath, SmallWidth, SmallHeight);

        return (previewFileName, previewLargeFileName);
    }

    /// <summary>
    /// Renders the full article (auto height, not cropped) to PNG bytes for the in-app preview pane.
    /// Returns null on failure. Uses the same float-flow layout as the catalog thumbnail.
    /// </summary>
    public static byte[]? RenderArticlePng(string mdFile)
    {
        var fileDir = Path.GetDirectoryName(mdFile) ?? string.Empty;
        string text;
        try { text = File.ReadAllText(mdFile); }
        catch { return null; }
        try
        {
            var figure = ExtractLeadFigure(ref text, fileDir);
            var blocks = ParseBlocks(Clean(text));
            return RenderToPng(blocks, figure, 0); // 0 = auto height
        }
        catch { return null; }
    }

    // ---- Document model ---------------------------------------------------------------------

    private enum FigureAlign { Left, Right, Center }
    private sealed record LeadFigure(string ImagePath, int? Width, FigureAlign Align, string Caption);

    private enum BlockKind { H1, H2, H3, Paragraph, Bullet, Quote, Code, Rule }
    private sealed record Run(string Text, bool Bold, bool Italic, bool Code, bool Link);
    private sealed record Block(BlockKind Kind, List<Run> Runs, string Raw = "");

    // ---- Lead figure extraction (from the source markdown) ----------------------------------

    private static LeadFigure? ExtractLeadFigure(ref string markdown, string mdFolder)
    {
        // 1. Custom container: ":::figure-right|left|center … :::"
        var cc = ContainerFigureRegex().Match(markdown);
        if (cc.Success)
        {
            var fig = BuildFigure(cc.Groups["inner"].Value, ParseAlign(cc.Groups["side"].Value), null, mdFolder);
            if (fig != null) { markdown = markdown.Remove(cc.Index, cc.Length); return fig; }
        }

        // 2. Markdig figure block: "^^^ … ^^^ caption". Alignment comes from a .figure-* class on
        //    the inner image (default center, as a plain figure is centered); caption is the text on
        //    the closing fence line, falling back to anything inside the block.
        var fb = FigureBlockRegex().Match(markdown);
        if (fb.Success)
        {
            var inner = fb.Groups["inner"].Value;
            var align =
                inner.Contains("figure-right", StringComparison.OrdinalIgnoreCase) ? FigureAlign.Right :
                inner.Contains("figure-left", StringComparison.OrdinalIgnoreCase) ? FigureAlign.Left : FigureAlign.Center;
            var baseFig = BuildFigure(inner, align, null, mdFolder);
            if (baseFig != null)
            {
                var caption = CleanCaption(fb.Groups["caption"].Value);
                if (string.IsNullOrEmpty(caption)) caption = baseFig.Caption;
                markdown = markdown.Remove(fb.Index, fb.Length);
                return baseFig with { Caption = caption };
            }
        }

        // 3. Raw HTML float: <div style="…float: right…"> … </div> (center has no float)
        foreach (Match m in DivFloatRegex().Matches(markdown))
        {
            var sm = StyleFloatRegex().Match(m.Groups["style"].Value);
            if (!sm.Success) continue;
            var align = sm.Groups["side"].Value.Equals("right", StringComparison.OrdinalIgnoreCase) ? FigureAlign.Right : FigureAlign.Left;
            var fig = BuildFigure(m.Groups["inner"].Value, align, StyleWidth(m.Groups["style"].Value), mdFolder);
            if (fig != null) { markdown = markdown.Remove(m.Index, m.Length); return fig; }
        }

        // 4. Markdown image carrying a {.figure-right|left|center …} attribute block.
        var im = AttrFigureRegex().Match(markdown);
        if (im.Success)
        {
            var attrs = im.Groups["attrs"].Value;
            FigureAlign? align =
                attrs.Contains("figure-right", StringComparison.OrdinalIgnoreCase) ? FigureAlign.Right :
                attrs.Contains("figure-left", StringComparison.OrdinalIgnoreCase) ? FigureAlign.Left :
                attrs.Contains("figure-center", StringComparison.OrdinalIgnoreCase) ? FigureAlign.Center : null;
            if (align is { } a)
            {
                var path = ResolveImage(im.Groups["src"].Value, mdFolder);
                if (path != null)
                {
                    int? width = AttrWidthRegex().Match(attrs) is { Success: true } wm ? int.Parse(wm.Groups[1].Value) : null;
                    markdown = markdown.Remove(im.Index, im.Length);
                    return new LeadFigure(path, width, a, im.Groups["alt"].Value.Trim());
                }
            }
        }

        return null;
    }

    private static FigureAlign ParseAlign(string side) => side.ToLowerInvariant() switch
    {
        "left" => FigureAlign.Left,
        "center" => FigureAlign.Center,
        _ => FigureAlign.Right,
    };

    private static LeadFigure? BuildFigure(string inner, FigureAlign align, int? styleWidth, string mdFolder)
    {
        string? src = null;
        int? width = styleWidth;

        var raw = ImgTagRegex().Match(inner);
        if (raw.Success)
        {
            src = raw.Groups["src"].Value;
            if (int.TryParse(raw.Groups["w"].Value, out var w)) width = w;
        }
        else
        {
            var md = MdImageRegex().Match(inner);
            if (md.Success)
            {
                src = md.Groups["src"].Value;
                if (AttrWidthRegex().Match(inner) is { Success: true } am) width = int.Parse(am.Groups[1].Value);
            }
        }

        var path = src == null ? null : ResolveImage(src, mdFolder);
        return path == null ? null : new LeadFigure(path, width, align, CleanCaption(inner));
    }

    private static string? ResolveImage(string src, string mdFolder)
    {
        if (string.IsNullOrWhiteSpace(src) || src.Contains("://")) return null;
        var path = Path.IsPathRooted(src) ? src : Path.GetFullPath(Path.Combine(mdFolder, src));
        return PreviewGenerator.IsImageFile(path) && File.Exists(path) ? path : null;
    }

    private static string CleanCaption(string inner)
    {
        inner = ImgTagRegex().Replace(inner, string.Empty);
        inner = MdImageRegex().Replace(inner, string.Empty);
        inner = HtmlTagRegex().Replace(inner, string.Empty);
        inner = Regex.Replace(inner, @"[*_`>#]|\{[^}]*\}|\^\^\^", string.Empty);
        return Regex.Replace(inner, @"\s+", " ").Trim();
    }

    private static int? StyleWidth(string style) =>
        Regex.Match(style, @"width\s*:\s*(\d+)\s*px", RegexOptions.IgnoreCase) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    // Removes leftover figure fences, HTML, image markup, and attribute blocks before block parsing.
    private static string Clean(string md)
    {
        md = FenceRegex().Replace(md, string.Empty);
        md = ImgTagRegex().Replace(md, string.Empty);
        md = MdImageRegex().Replace(md, string.Empty);
        md = HtmlTagRegex().Replace(md, string.Empty);
        md = AttrBlockRegex().Replace(md, string.Empty);
        return md;
    }

    // ---- Light block + inline parsing -------------------------------------------------------

    private static List<Block> ParseBlocks(string md)
    {
        var blocks = new List<Block>();
        var lines = md.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) { i++; continue; }

            // fenced code
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                i++;
                var code = new List<string>();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) code.Add(lines[i++]);
                if (i < lines.Length) i++; // closing fence
                blocks.Add(new Block(BlockKind.Code, [], string.Join('\n', code)));
                continue;
            }

            var trimmed = line.TrimStart();

            if (Regex.IsMatch(trimmed, @"^#{1,6}\s"))
            {
                var level = trimmed.Length - trimmed.TrimStart('#').Length;
                var kind = level == 1 ? BlockKind.H1 : level == 2 ? BlockKind.H2 : BlockKind.H3;
                blocks.Add(new Block(kind, ParseRuns(trimmed.TrimStart('#').Trim())));
                i++; continue;
            }

            if (Regex.IsMatch(trimmed, @"^([-*_])\1{2,}\s*$"))
            {
                blocks.Add(new Block(BlockKind.Rule, []));
                i++; continue;
            }

            if (Regex.IsMatch(trimmed, @"^([-*+]|\d+\.)\s+"))
            {
                var item = Regex.Replace(trimmed, @"^([-*+]|\d+\.)\s+", "");
                blocks.Add(new Block(BlockKind.Bullet, ParseRuns(item)));
                i++; continue;
            }

            if (trimmed.StartsWith('>'))
            {
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                    quote.Add(lines[i++].TrimStart().TrimStart('>').Trim());
                blocks.Add(new Block(BlockKind.Quote, ParseRuns(string.Join(' ', quote))));
                continue;
            }

            // paragraph: gather consecutive plain lines
            var para = new List<string>();
            while (i < lines.Length && lines[i].Trim().Length != 0 &&
                   !Regex.IsMatch(lines[i].TrimStart(), @"^(#{1,6}\s|```|>|([-*+]|\d+\.)\s|([-*_])\3{2,}\s*$)"))
                para.Add(lines[i++].Trim());
            blocks.Add(new Block(BlockKind.Paragraph, ParseRuns(string.Join(' ', para))));
        }
        return blocks;
    }

    // Minimal inline parser: **bold**, *italic*, `code`, [text](url). Other markers are literal.
    private static List<Run> ParseRuns(string text)
    {
        var runs = new List<Run>();
        var buf = new System.Text.StringBuilder();
        void Flush() { if (buf.Length > 0) { runs.Add(new Run(buf.ToString(), false, false, false, false)); buf.Clear(); } }

        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > 0) { Flush(); runs.Add(new Run(text[(i + 2)..end], true, false, false, false)); i = end + 2; continue; }
            }
            if (text[i] == '*')
            {
                var end = text.IndexOf('*', i + 1);
                if (end > 0) { Flush(); runs.Add(new Run(text[(i + 1)..end], false, true, false, false)); i = end + 1; continue; }
            }
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > 0) { Flush(); runs.Add(new Run(text[(i + 1)..end], false, false, true, false)); i = end + 1; continue; }
            }
            if (text[i] == '[')
            {
                var m = Regex.Match(text[i..], @"^\[([^\]]*)\]\([^)]*\)");
                if (m.Success) { Flush(); runs.Add(new Run(m.Groups[1].Value, false, false, false, true)); i += m.Length; continue; }
            }
            buf.Append(text[i]);
            i++;
        }
        Flush();
        return runs;
    }

    // ---- Skia rendering with float-flow -----------------------------------------------------

    private static readonly Lazy<SKTypeface> Regular = new(() => LoadFont("Inter-Regular.ttf"));
    private static readonly Lazy<SKTypeface> Bold = new(() => LoadFont("Inter-Bold.ttf"));
    private static readonly Lazy<SKTypeface> Mono = new(() =>
        SKFontManager.Default.MatchFamily("Menlo") ?? SKFontManager.Default.MatchFamily("Consolas") ??
        SKFontManager.Default.MatchFamily("monospace") ?? SKTypeface.Default);

    private static SKTypeface LoadFont(string name)
    {
        try
        {
            using var s = AssetLoader.Open(new Uri($"avares://Avalonia.Fonts.Inter/Assets/{name}"));
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            return SKTypeface.FromStream(ms);
        }
        catch { return SKTypeface.Default; }
    }

    private static readonly SKColor Ink = new(0x21, 0x25, 0x29);
    private static readonly SKColor Muted = new(0x55, 0x55, 0x55);
    private static readonly SKColor LinkColor = new(0x0d, 0x6e, 0xfd);
    private static readonly SKColor CodeBg = new(0xf2, 0xf3, 0xf5);

    // height > 0 renders a fixed canvas (the catalog thumbnail, top-cropped); height <= 0 renders
    // the full article into an auto-sized canvas (the in-app preview pane). The cap is only a
    // backstop against a pathologically long article, not the size normally allocated.
    private const int AutoHeightCap = 8000;

    private static byte[] RenderToPng(List<Block> blocks, LeadFigure? figure, int height)
    {
        // The figure is decoded once and shared by both passes below — decoding is the expensive
        // part of a render, and laying out twice must not mean paying for it twice.
        SKBitmap? figureBmp = null;
        try
        {
            if (figure != null)
                figureBmp = DecodeImage(figure.ImagePath, (int)((LargeWidth - 2 * Pad) * 0.5));

            int surfaceH;
            if (height > 0)
            {
                surfaceH = height;
            }
            else
            {
                // Auto height means "as tall as the article turns out to be". Laying out onto a
                // canvas that draws nothing yields that number for the price of the layout alone,
                // so the real surface is allocated at the size actually needed instead of always
                // reserving AutoHeightCap — 1200×8000, ~38 MB, however short the article is.
                using var probe = new SKNoDrawCanvas(LargeWidth, AutoHeightCap);
                var used = Paint(probe, blocks, figure, figureBmp, AutoHeightCap);
                surfaceH = Math.Clamp((int)Math.Ceiling(used + Pad), 1, AutoHeightCap);
            }

            using var surface = SKSurface.Create(new SKImageInfo(LargeWidth, surfaceH));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White);
            Paint(canvas, blocks, figure, figureBmp, surfaceH);

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 95);
            return data.ToArray();
        }
        finally { figureBmp?.Dispose(); }
    }

    // Lays the article out onto <paramref name="canvas"/>, stopping at <paramref name="surfaceH"/>,
    // and returns the y the content actually reached. Called twice in auto-height mode: once on a
    // no-draw canvas to measure, once for real.
    private static float Paint(SKCanvas canvas, List<Block> blocks, LeadFigure? figure, SKBitmap? figureBmp, int surfaceH)
    {
        float x0 = Pad, x1 = LargeWidth - Pad, y = Pad;
        var contentWidth = x1 - x0;

        int idx = 0;
        // Leading headings stay full width above the figure.
        while (idx < blocks.Count && blocks[idx].Kind is BlockKind.H1 or BlockKind.H2 or BlockKind.H3)
            y = DrawBlock(canvas, blocks[idx++], x0, x1, y, null);

        (float top, float bottom, float edge, bool right)? floatRect = null;

        if (figure != null)
        {
            var bmp = figureBmp;
            if (bmp != null)
            {
                // An authored width is a fraction of the site's column, so it means the same
                // fraction here. The old fixed ×1.45 with a 240 floor landed width=150 and
                // width=200 on the same value, which is the one thing a stated width shouldn't do.
                // Without a width there is nothing to honour, so the original default stands. The
                // page bounds an authored width by the column and nothing else, so the upper clamp
                // here is the full content width — a 0.45 cap would shrink what the page honours.
                float wf = figure.Width is { } authored
                    ? Math.Clamp(authored / SiteColumnWidth * contentWidth, 120f, contentWidth)
                    : Math.Clamp(230f * 1.45f, 240f, contentWidth * 0.42f);
                float hf = wf * bmp.Height / bmp.Width;

                // Floating only helps while a readable column survives beside the picture. Past
                // that the band degrades to two words a line, and a figure wide enough to push its
                // edge beyond x1 would spill words into the right margin, since FlowText always
                // places at least one word. So a figure that wide stops floating and takes the
                // text below it instead — which is how the page reads at that size anyway.
                var align = figure.Align != FigureAlign.Center
                            && contentWidth - wf - FloatGap < MinTextBand
                    ? FigureAlign.Center
                    : figure.Align;

                float fx = align == FigureAlign.Left ? x0
                         : align == FigureAlign.Right ? x1 - wf
                         : x0 + (contentWidth - wf) / 2;
                float top = y + 4;

                canvas.Save();
                canvas.ClipRoundRect(new SKRoundRect(new SKRect(fx, top, fx + wf, top + hf), 8), antialias: true);
                canvas.DrawBitmap(bmp, new SKRect(fx, top, fx + wf, top + hf));
                canvas.Restore();

                float capBottom = DrawCaption(canvas, figure.Caption, fx, wf, top + hf + 22);
                float bottom = capBottom + 12;

                if (align == FigureAlign.Center)
                {
                    y = bottom; // centered block in flow; text continues full width below
                }
                else
                {
                    floatRect = (top, bottom,
                        align == FigureAlign.Right ? fx - FloatGap : fx + wf + FloatGap,
                        align == FigureAlign.Right);
                }
            }
        }

        for (; idx < blocks.Count && y < surfaceH; idx++)
            y = DrawBlock(canvas, blocks[idx], x0, x1, y, floatRect);

        return Math.Max(y, floatRect?.bottom ?? 0);
    }

    private static float DrawBlock(SKCanvas c, Block b, float x0, float x1, float y, (float top, float bottom, float edge, bool right)? fl)
    {
        switch (b.Kind)
        {
            case BlockKind.Rule:
                using (var p = new SKPaint { Color = new SKColor(0xDD, 0xDD, 0xDD), StrokeWidth = 2, IsAntialias = true })
                    c.DrawLine(x0, y + 10, x1, y + 10, p);
                return y + 26;

            case BlockKind.Code:
            {
                var lines = b.Raw.Split('\n');
                float size = 19, lh = 26, padIn = 12;
                float h = lines.Length * lh + padIn * 2;
                using (var bg = new SKPaint { Color = CodeBg, IsAntialias = true })
                    c.DrawRoundRect(new SKRoundRect(new SKRect(x0, y, x1, y + h), 6), bg);
                var font = new SKFont(Mono.Value, size);
                using var ink = new SKPaint { Color = Ink, IsAntialias = true };
                float cy = y + padIn + size;
                foreach (var ln in lines) { c.DrawText(ln, x0 + padIn, cy, font, ink); cy += lh; }
                return y + h + 10;
            }

            case BlockKind.H1: return FlowText(c, b.Runs, x0, x1, y + 6, 46, 56, Ink, fl) + 12;
            case BlockKind.H2: return FlowText(c, b.Runs, x0, x1, y + 8, 33, 42, Ink, fl) + 8;
            case BlockKind.H3: return FlowText(c, b.Runs, x0, x1, y + 6, 27, 35, Ink, fl) + 6;

            case BlockKind.Bullet:
            {
                using var ink = new SKPaint { Color = Ink, IsAntialias = true };
                c.DrawText("•", x0 + 6, y + 24, new SKFont(Regular.Value, 24), ink);
                return FlowText(c, b.Runs, x0 + 30, x1, y, 24, 33, Ink, fl) + 6;
            }

            case BlockKind.Quote:
            {
                using (var bar = new SKPaint { Color = new SKColor(0xDF, 0xE2, 0xE5), IsAntialias = true })
                    c.DrawRect(new SKRect(x0, y, x0 + 4, y + 30), bar);
                var end = FlowText(c, b.Runs, x0 + 20, x1, y, 24, 33, Muted, fl, italic: true);
                return end + 8;
            }

            default:
                return FlowText(c, b.Runs, x0, x1, y, 24, 33, Ink, fl) + 14;
        }
    }

    // The float-flow core: wraps runs into lines whose available width shrinks while the line's y
    // overlaps the figure's vertical band, then returns to full width once it clears the figure.
    private static float FlowText(SKCanvas c, List<Run> runs, float x0, float x1, float y,
        float size, float lineH, SKColor color, (float top, float bottom, float edge, bool right)? fl, bool italic = false)
    {
        // Expand runs to per-word tokens that remember their style.
        var words = new List<Run>();
        foreach (var r in runs)
            foreach (var w in r.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                words.Add(r with { Text = w });
        if (words.Count == 0) return y;

        using var ink = new SKPaint { Color = color, IsAntialias = true };
        using var link = new SKPaint { Color = LinkColor, IsAntialias = true };
        float space = new SKFont(Regular.Value, size).MeasureText(" ");

        int i = 0;
        while (i < words.Count)
        {
            float lx = x0, rx = x1;
            if (fl is { } f && y < f.bottom && y + lineH > f.top)
            {
                if (f.right) rx = f.edge; else lx = f.edge;
            }

            var line = new List<Run>();
            float lw = 0;
            while (i < words.Count)
            {
                var w = words[i];
                float ww = FontFor(w, size, italic).MeasureText(w.Text);
                float add = line.Count == 0 ? ww : lw + space + ww;
                if (add <= rx - lx || line.Count == 0) { line.Add(w); lw = add; i++; }
                else break;
            }

            float x = lx, baseline = y + size;
            foreach (var w in line)
            {
                var font = FontFor(w, size, italic);
                c.DrawText(w.Text, x, baseline, font, w.Link ? link : ink);
                x += font.MeasureText(w.Text) + space;
            }
            y += lineH;
        }
        return y;
    }

    private static SKFont FontFor(Run r, float size, bool italic)
    {
        var tf = r.Code ? Mono.Value : r.Bold ? Bold.Value : Regular.Value;
        var f = new SKFont(tf, size);
        if (r.Italic || italic) f.SkewX = -0.22f;
        return f;
    }

    private static float DrawCaption(SKCanvas c, string caption, float bx, float bw, float y)
    {
        if (string.IsNullOrWhiteSpace(caption)) return y - 22;
        float size = 18, lh = 24;
        var font = new SKFont(Regular.Value, size) { SkewX = -0.2f };
        using var paint = new SKPaint { Color = Muted, IsAntialias = true };

        // wrap caption within the figure width, centered
        var words = caption.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        float space = font.MeasureText(" ");
        var line = new System.Text.StringBuilder();
        float lineW = 0; float cy = y;
        void Emit()
        {
            var t = line.ToString();
            if (t.Length == 0) return;
            float w = font.MeasureText(t);
            c.DrawText(t, bx + (bw - w) / 2, cy, font, paint);
            cy += lh;
            line.Clear(); lineW = 0;
        }
        foreach (var word in words)
        {
            float ww = font.MeasureText(word);
            float add = line.Length == 0 ? ww : lineW + space + ww;
            if (add <= bw || line.Length == 0) { if (line.Length > 0) line.Append(' '); line.Append(word); lineW = add; }
            else { Emit(); line.Append(word); lineW = ww; }
        }
        Emit();
        return cy;
    }

    private static SKBitmap? DecodeImage(string path, int maxWidth)
    {
        try
        {
            using var mi = new MagickImage(path);
            if (mi.Width > (uint)maxWidth)
                mi.Resize((uint)maxWidth, (uint)((long)mi.Height * maxWidth / mi.Width));
            var png = mi.ToByteArray(MagickFormat.Png);
            return SKBitmap.Decode(png);
        }
        catch { return null; }
    }

    private static void WriteWebp(byte[] pngBytes, string destPath, int width, int height)
    {
        using var image = new MagickImage(pngBytes);
        if (image.Width != (uint)width || image.Height != (uint)height)
            image.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
        image.Quality = 82;
        image.Settings.SetDefine(MagickFormat.WebP, "method", "6");
        image.Write(destPath, MagickFormat.WebP);
    }

    // ---- Regexes ----------------------------------------------------------------------------

    [GeneratedRegex("""<img\b[^>]*?\bsrc\s*=\s*(['"])(?<src>.*?)\1(?:[^>]*?\bwidth\s*=\s*['"]?(?<w>\d+))?[^>]*?>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ImgTagRegex();

    [GeneratedRegex("""!\[(?<alt>[^\]]*)\]\((?<src>[^)\s]+)(?:\s+"[^"]*")?\)(?:\{[^}]*\})?""", RegexOptions.Singleline)]
    private static partial Regex MdImageRegex();

    [GeneratedRegex("""!\[(?<alt>[^\]]*)\]\((?<src>[^)\s]+)(?:\s+"[^"]*")?\)\{(?<attrs>[^}]*)\}""", RegexOptions.Singleline)]
    private static partial Regex AttrFigureRegex();

    [GeneratedRegex(@"\bwidth\s*=\s*['""]?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AttrWidthRegex();

    [GeneratedRegex(@"(?ms)^:::+[ \t]*figure-(?<side>right|left|center)[ \t]*\r?\n(?<inner>.*?)^:::+[ \t]*$", RegexOptions.IgnoreCase)]
    private static partial Regex ContainerFigureRegex();

    [GeneratedRegex(@"(?ms)^\^\^\^[ \t]*\r?\n(?<inner>.*?)\r?\n\^\^\^[ \t]*(?<caption>[^\r\n]*)$")]
    private static partial Regex FigureBlockRegex();

    [GeneratedRegex("""<div\b[^>]*\bstyle\s*=\s*(['"])(?<style>[^'"]*)\1[^>]*>(?<inner>.*?)</div>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DivFloatRegex();

    [GeneratedRegex(@"float\s*:\s*(?<side>left|right)", RegexOptions.IgnoreCase)]
    private static partial Regex StyleFloatRegex();

    [GeneratedRegex(@"(?m)^[ \t]*(?::::+|\^\^\^).*$")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"(?<=\))\{[^}]*\}")]
    private static partial Regex AttrBlockRegex();

    [GeneratedRegex("""</?[a-zA-Z][a-zA-Z0-9]*(?:\s[^>]*)?>""", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();
}
