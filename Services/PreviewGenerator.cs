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

        var dir2site = Path.GetFullPath(Path.Combine(fileDir, ".dir2site", stem));
        Directory.CreateDirectory(dir2site);

        var previewFile      = $"preview-{stem}.webp";
        var previewLargeFile = $"preview-lg-{stem}.webp";
        var imageFile        = $"{stem}_q90.webp";
        var previewPath      = Path.Combine(dir2site, previewFile);
        var previewLargePath = Path.Combine(dir2site, previewLargeFile);
        var imagePath        = Path.Combine(dir2site, imageFile);

        // Returned names are relative paths from the artifact's source folder
        var previewFileName      = $".dir2site/{stem}/preview-{stem}.webp";
        var previewLargeFileName = $".dir2site/{stem}/preview-lg-{stem}.webp";
        var imageFileName        = $".dir2site/{stem}/{imageFile}";

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
    /// Renders all PDF pages, writes a BookReader JSON, and generates WebP catalog thumbnails
    /// from the first page. Returns (previewFileName, previewLargeFileName), or null on failure.
    /// Pages are kept as JPEG only when the original binary JPEG is extracted without re-encoding;
    /// all other cases (JP2, vector, MRC, or any resize) produce WebP.
    /// </summary>
    public static (string Preview, string PreviewLarge)? GeneratePdfPreviewsAndPages(
        string sourceFile,
        string traversalRoot,
        bool resizeEnabled,
        int maxWidth,
        int quality,
        IProgress<string>? progress = null)
    {
        if (!IsPdfFile(sourceFile)) return null;

        var fileDir  = Path.GetDirectoryName(sourceFile) ?? string.Empty;
        var stem     = Path.GetFileNameWithoutExtension(sourceFile);
        var dir2site = Path.GetFullPath(Path.Combine(fileDir, ".dir2site", stem));
        var pagesDir = Path.Combine(dir2site, $"{stem}_pages");
        Directory.CreateDirectory(dir2site);
        Directory.CreateDirectory(pagesDir);

        var previewFile      = $"preview-{stem}.webp";
        var previewLargeFile = $"preview-lg-{stem}.webp";
        var previewPath      = Path.Combine(dir2site, previewFile);
        var previewLargePath = Path.Combine(dir2site, previewLargeFile);
        var bookReaderJsonPath = Path.Combine(dir2site, $"{stem}.bookreader.json");

        // Returned names are relative paths from the artifact's source folder
        var previewFileName      = $".dir2site/{stem}/preview-{stem}.webp";
        var previewLargeFileName = $".dir2site/{stem}/preview-lg-{stem}.webp";

        if (File.Exists(previewPath) && File.Exists(previewLargePath) && File.Exists(bookReaderJsonPath))
            return (previewFileName, previewLargeFileName);

        var fileName    = Path.GetFileName(sourceFile);
        var parentName  = Path.GetFileName(fileDir);
        var grandParent = Path.GetFileName(Path.GetDirectoryName(fileDir) ?? string.Empty);
        var displayName = (string.IsNullOrEmpty(grandParent), string.IsNullOrEmpty(parentName)) switch
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
            var pageNum = pageIndex + 1;
            int imgWidth, imgHeight;

            // Determine whether to keep the original JPEG binary or re-encode as WebP.
            // JPEG is kept only when: there is a single embedded JPEG AND no resize is needed.
            bool keepJpeg = TryGetOriginalJpeg(pdfPigDoc, pageIndex,
                                out var jpegBytes, out imgWidth, out imgHeight)
                            && (!resizeEnabled || imgWidth <= maxWidth);

            var pageName = keepJpeg ? $"page-{pageNum:D4}.jpg" : $"page-{pageNum:D4}.webp";
            var pagePath = Path.Combine(pagesDir, pageName);

            if (!File.Exists(pagePath))
            {
                if (keepJpeg)
                {
                    progress?.Report($"Extracting original JPEG {pageNum}/{pageCount}: {displayName}");
                    File.WriteAllBytes(pagePath, jpegBytes.ToArray());
                }
                else if (!jpegBytes.IsEmpty)
                {
                    // Embedded JPEG that needs resizing — re-encode as WebP
                    progress?.Report($"Resizing JPEG page {pageNum}/{pageCount}: {displayName}");
                    using var magick = new MagickImage(jpegBytes.ToArray());
                    if (resizeEnabled && magick.Width > maxWidth)
                        magick.Resize((uint)maxWidth, (uint)((long)magick.Height * maxWidth / (long)magick.Width));
                    imgWidth  = (int)magick.Width;
                    imgHeight = (int)magick.Height;
                    magick.Quality = (uint)quality;
                    magick.Settings.SetDefine(MagickFormat.WebP, "method", "6");
                    magick.Write(pagePath, MagickFormat.WebP);
                }
                else if (TryGetJp2Info(pdfPigDoc, pageIndex, out imgWidth, out imgHeight,
                             out bool singleLayer, out var jp2Raw))
                {
                    if (singleLayer)
                    {
                        progress?.Report($"Transcoding JP2 page {pageNum}/{pageCount}: {displayName}");
                        // JP2 single-layer requires re-encoding — always WebP
                        using var magick = new MagickImage(jp2Raw.ToArray());
                        if (resizeEnabled && magick.Width > maxWidth)
                            magick.Resize((uint)maxWidth, (uint)((long)magick.Height * maxWidth / (long)magick.Width));
                        imgWidth  = (int)magick.Width;
                        imgHeight = (int)magick.Height;
                        magick.Quality = (uint)quality;
                        magick.Settings.SetDefine(MagickFormat.WebP, "method", "6");
                        magick.Write(pagePath, MagickFormat.WebP);
                    }
                    else
                    {
                        progress?.Report($"Rendering layers at original image dimensions {pageNum}/{pageCount}: {displayName}");
                        // MRC multi-layer — composite via PDFtoImage at JP2 pixel dimensions
                        using var pageStream = File.OpenRead(sourceFile);
#pragma warning disable CA1416
                        using var bitmap = Conversion.ToImage(pageStream, pageIndex, leaveOpen: false,
                            password: null, options: new RenderOptions(Dpi: 72, Width: imgWidth, Height: imgHeight));
#pragma warning restore CA1416
                        SaveBitmapAsWebP(bitmap, pagePath, resizeEnabled, maxWidth, quality, out imgWidth, out imgHeight);
                    }
                }
                else
                {
                    // Vector or mixed page — render via PDFtoImage at standard DPI
                    progress?.Report($"Rendering page {pageNum}/{pageCount}: {displayName}");
                    using var pageStream = File.OpenRead(sourceFile);
#pragma warning disable CA1416
                    using var bitmap = Conversion.ToImage(pageStream, pageIndex, leaveOpen: false,
                        password: null, options: new RenderOptions(Dpi: 150));
#pragma warning restore CA1416
                    imgWidth  = bitmap.Width;
                    imgHeight = bitmap.Height;
                    SaveBitmapAsWebP(bitmap, pagePath, resizeEnabled, maxWidth, quality, out imgWidth, out imgHeight);
                }
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

    // Returns the raw JPEG bytes and dimensions if the page has exactly one embedded JPEG (FF D8 magic).
    // Does not write anything — caller decides filename and whether to resize.
    private static bool TryGetOriginalJpeg(
        UglyToad.PdfPig.PdfDocument doc, int pageIndex,
        out ReadOnlyMemory<byte> jpegBytes, out int width, out int height)
    {
        width = height = 0;
        jpegBytes = ReadOnlyMemory<byte>.Empty;
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

        jpegBytes = raw;
        width     = img.WidthInSamples;
        height    = img.HeightInSamples;
        return true;
    }

    // Encodes an SKBitmap to WebP via a lossless PNG intermediate so ImageMagick handles
    // the resize and WebP encode (method=6) consistently with other WebP output in this project.
    private static void SaveBitmapAsWebP(SKBitmap bitmap, string destPath,
        bool resizeEnabled, int maxWidth, int quality,
        out int outWidth, out int outHeight)
    {
        using var pngEncoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var magick = new MagickImage(pngEncoded.ToArray());

        if (resizeEnabled && magick.Width > maxWidth)
            magick.Resize((uint)maxWidth, (uint)((long)magick.Height * maxWidth / (long)magick.Width));

        outWidth  = (int)magick.Width;
        outHeight = (int)magick.Height;
        magick.Quality = (uint)quality;
        magick.Settings.SetDefine(MagickFormat.WebP, "method", "6");
        magick.Write(destPath, MagickFormat.WebP);
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
        var scale = Math.Min((double)image.Width / width, (double)image.Height / height);
        if (scale < 1.0)
        {
            width  = (uint)Math.Floor(width  * scale);
            height = (uint)Math.Floor(height * scale);
        }
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
