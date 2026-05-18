using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ImageMagick;
using PDFtoImage;
using SkiaSharp;

namespace dir2site.Services;

public static class PreviewGenerator
{
    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif", ".webp", ".heic", ".avif"];

    public static bool IsImageFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return Array.Exists(ImageExtensions, e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsPdfFile(string filePath) =>
        Path.GetExtension(filePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    // previewRelativePath already includes the .dir2site/ segment (e.g. ".dir2site/preview-foo.webp")
    public static bool PreviewFileExists(string sourceFileDir, string previewRelativePath) =>
        File.Exists(Path.Combine(sourceFileDir, previewRelativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// Generates preview, preview-large, and full-resolution web WebP images into the .dir2site mirror tree.
    /// Returns (previewFileName, previewLargeFileName, imageFileName), or null if generation was skipped/failed.
    /// </summary>
    public static (string Preview, string PreviewLarge, string Image)? GeneratePreviews(
        string sourceFile,
        string traversalRoot,
        IProgress<string>? progress = null)
    {
        if (!IsImageFile(sourceFile))
            return null;

        var fileDir = Path.GetDirectoryName(sourceFile) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(sourceFile);

        var dir2site = Path.GetFullPath(Path.Combine(fileDir, ".dir2site"));
        Directory.CreateDirectory(dir2site);

        var previewFile      = $"preview-{stem}.webp";
        var previewLargeFile = $"preview-lg-{stem}.webp";
        var imageFile        = $"{stem}_q90.webp";
        var previewPath      = Path.Combine(dir2site, previewFile);
        var previewLargePath = Path.Combine(dir2site, previewLargeFile);
        var imagePath        = Path.Combine(dir2site, imageFile);

        // Returned names include the .dir2site/ segment so they are valid relative paths from the artifact's folder
        var previewFileName      = $".dir2site/preview-{stem}.webp";
        var previewLargeFileName = $".dir2site/preview-lg-{stem}.webp";
        var imageFileName        = $".dir2site/{imageFile}";

        var fileName = Path.GetFileName(sourceFile);

        if (!File.Exists(previewPath))
        {
            progress?.Report($"Generating preview: {fileName}");
            GenerateThumbnail(sourceFile, previewPath, 800, 600);
        }

        if (!File.Exists(previewLargePath))
        {
            progress?.Report($"Generating preview (large): {fileName}");
            GenerateThumbnail(sourceFile, previewLargePath, 1200, 900);
        }

        if (!File.Exists(imagePath))
        {
            progress?.Report($"Generating web image: {fileName}");
            GenerateWebImage(sourceFile, imagePath);
        }

        return (previewFileName, previewLargeFileName, imageFileName);
    }

    /// <summary>
    /// Renders all PDF pages to JPEG, writes a BookReader JSON, and generates WebP catalog thumbnails
    /// from the first page. Returns (previewFileName, previewLargeFileName), or null on failure.
    /// </summary>
    public static (string Preview, string PreviewLarge)? GeneratePdfPreviewsAndPages(
        string sourceFile,
        string traversalRoot,
        IProgress<string>? progress = null)
    {
        if (!IsPdfFile(sourceFile)) return null;

        var fileDir  = Path.GetDirectoryName(sourceFile) ?? string.Empty;
        var stem     = Path.GetFileNameWithoutExtension(sourceFile);
        var dir2site = Path.GetFullPath(Path.Combine(fileDir, ".dir2site"));
        var pagesDir = Path.Combine(dir2site, $"{stem}_pages");
        Directory.CreateDirectory(dir2site);
        Directory.CreateDirectory(pagesDir);

        var previewFile      = $"preview-{stem}.webp";
        var previewLargeFile = $"preview-lg-{stem}.webp";
        var previewPath      = Path.Combine(dir2site, previewFile);
        var previewLargePath = Path.Combine(dir2site, previewLargeFile);
        var bookReaderJsonPath = Path.Combine(dir2site, $"{stem}.bookreader.json");

        // Returned names include the .dir2site/ segment so they are valid relative paths from the artifact's folder
        var previewFileName      = $".dir2site/preview-{stem}.webp";
        var previewLargeFileName = $".dir2site/preview-lg-{stem}.webp";

        if (File.Exists(previewPath) && File.Exists(previewLargePath) && File.Exists(bookReaderJsonPath))
            return (previewFileName, previewLargeFileName);

        var fileName      = Path.GetFileName(sourceFile);
        var parentName    = Path.GetFileName(fileDir);
        var grandParent   = Path.GetFileName(Path.GetDirectoryName(fileDir) ?? string.Empty);
        var displayName   = (string.IsNullOrEmpty(grandParent), string.IsNullOrEmpty(parentName)) switch
        {
            (_, true)      => fileName,
            (true, false)  => $"{parentName}/{fileName}",
            (false, false) => $"{grandParent}/{parentName}/{fileName}",
        };
        var pages = new List<BookReaderPage>();

        using var pdfPigDoc = UglyToad.PdfPig.PdfDocument.Open(sourceFile);
        int pageCount = pdfPigDoc.NumberOfPages;

        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var pageNum  = pageIndex + 1;
            var pageName = $"page-{pageNum:D4}.jpg";
            var pagePath = Path.Combine(pagesDir, pageName);

            int imgWidth, imgHeight;

            if (TryExtractJpegPage(pdfPigDoc, pageIndex, pagePath, out imgWidth, out imgHeight))
            {
                progress?.Report($"Extracting original jpeg {pageNum}/{pageCount}: {displayName}");
            }
            else if (TryGetJp2Info(pdfPigDoc, pageIndex, out imgWidth, out imgHeight,
                         out bool singleLayer, out var jp2Raw))
            {
                if (singleLayer)
                {
                    progress?.Report($"Extracting original JP2 resaving as JPEG {pageNum}/{pageCount}: {displayName}");

                    // Single JP2 — no other layers to composite, transcode directly
                    using var magick = new MagickImage(jp2Raw.ToArray());
                    magick.Quality = 90;
                    magick.Write(pagePath, MagickFormat.Jpg);
                }
                else
                {
                    progress?.Report($"Rendering layers at original image dimensions {pageNum}/{pageCount}: {displayName}");

                    // MRC multi-layer — must composite via PDFtoImage at JP2 pixel dimensions
                    using var pageStream = File.OpenRead(sourceFile);
                    using var bitmap = Conversion.ToImage(pageStream, pageIndex, leaveOpen: false,
                        password: null, options: new RenderOptions(Dpi: 72, Width: imgWidth, Height: imgHeight));
                    using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 85);
                    File.WriteAllBytes(pagePath, encoded.ToArray());
                }
            }
            else
            {
                // Vector or mixed page — render via PDFtoImage at standard DPI
                progress?.Report($"Rendering page {pageNum}/{pageCount}: {displayName}");
                using var pageStream = File.OpenRead(sourceFile);
                using var bitmap = Conversion.ToImage(pageStream, pageIndex, leaveOpen: false,
                    password: null, options: new RenderOptions(Dpi: 150));
                using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 85);
                File.WriteAllBytes(pagePath, encoded.ToArray());
                imgWidth = bitmap.Width; imgHeight = bitmap.Height;
            }

            pages.Add(new BookReaderPage(imgWidth, imgHeight, $"{stem}_pages/{pageName}", pageNum.ToString()));

            if (pageIndex == 0)
            {
                progress?.Report($"Generating preview: {displayName}");
                GenerateThumbnail(pagePath, previewPath, 800, 600);
                progress?.Report($"Generating preview (large): {displayName}");
                GenerateThumbnail(pagePath, previewLargePath, 1200, 900);
            }
        }

        WriteBookReaderJson(bookReaderJsonPath, pages);
        return (previewFileName, previewLargeFileName);
    }

    // Writes pagePath if the page has exactly one JPEG image (FF D8 magic). No re-encoding.
    private static bool TryExtractJpegPage(
        UglyToad.PdfPig.PdfDocument doc, int pageIndex,
        string pagePath, out int width, out int height)
    {
        width = height = 0;
        var page   = doc.GetPage(pageIndex + 1);
        var images = page.GetImages().ToList();
        if (images.Count != 1)
            return false;

        var img  = images[0];
        var raw  = img.RawMemory;
        var span = raw.Span;

        // JPEG magic: FF D8
        if (span.Length < 2 || span[0] != 0xFF || span[1] != 0xD8)
            return false;

        File.WriteAllBytes(pagePath, raw.ToArray());
        width  = img.WidthInSamples;
        height = img.HeightInSamples;
        return true;
    }

    // Returns JP2 image info for the page without saving anything.
    // singleLayer=true means the page has exactly one image — safe to transcode directly.
    // singleLayer=false means MRC multi-layer — caller must composite via PDFtoImage.
    private static bool TryGetJp2Info(
        UglyToad.PdfPig.PdfDocument doc, int pageIndex,
        out int width, out int height, out bool singleLayer, out ReadOnlyMemory<byte> rawBytes)
    {
        width = height = 0;
        singleLayer = false;
        rawBytes = ReadOnlyMemory<byte>.Empty;

        var page   = doc.GetPage(pageIndex + 1);
        var images = page.GetImages().ToList();
        if (images.Count == 0)
            return false;

        var main = images.OrderByDescending(img => img.RawMemory.Length).First();
        var span = main.RawMemory.Span;

        // JP2 container signature: 00 00 00 0C
        if (span.Length < 4 || span[0] != 0x00 || span[1] != 0x00 || span[2] != 0x00 || span[3] != 0x0C)
            return false;

        width       = main.WidthInSamples;
        height      = main.HeightInSamples;
        singleLayer = images.Count == 1;
        rawBytes    = main.RawMemory;
        return true;
    }

    /// <summary>
    /// Resolves a preview filename stored in YAML to its full path under the .www tree.
    /// </summary>
    public static string? ResolvePreviewPath(string traversalRoot, string fileDir, string? previewFileName)
    {
        if (previewFileName == null) return null;
        return Path.GetFullPath(Path.Combine(fileDir, ".dir2site", previewFileName));
    }

    private static void GenerateWebImage(string source, string dest)
    {
        using var image = new MagickImage(source);
        image.Quality = 90;
        image.Settings.SetDefine(MagickFormat.WebP, "method", "6");
        image.Write(dest, MagickFormat.WebP);
    }

    private static void GenerateThumbnail(string source, string dest, uint width, uint height)
    {
        using var image = new MagickImage(source);
        var geometry = new MagickGeometry(width, height) { FillArea = true };
        image.Thumbnail(geometry);
        image.Crop(width, height, Gravity.Center);
        image.Quality = 80;
        image.Write(dest, MagickFormat.WebP);
    }

    private static void WriteBookReaderJson(string path, List<BookReaderPage> pages)
    {
        var data = pages.Select(p => new[] { new
        {
            width   = p.Width,
            height  = p.Height,
            uri     = p.Uri,
            pageNum = p.PageNum,
        }}).ToArray();

        var json = JsonSerializer.Serialize(
            new { data },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private record BookReaderPage(int Width, int Height, string Uri, string PageNum);
}
