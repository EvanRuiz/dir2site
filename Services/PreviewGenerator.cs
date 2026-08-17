// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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

    public static bool IsMarkdownFile(string filePath) =>
        Path.GetExtension(filePath).Equals(".md", StringComparison.OrdinalIgnoreCase);

    public static bool IsUrlFile(string filePath) =>
        Path.GetExtension(filePath).Equals(".url", StringComparison.OrdinalIgnoreCase);

    // previewRelativePath already includes the .dir2site/ segment (e.g. ".dir2site/preview-foo.webp")
    public static bool PreviewFileExists(string sourceFileDir, string previewRelativePath) =>
        File.Exists(Path.Combine(sourceFileDir, previewRelativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// The names this class would give an artifact's two thumbnails, relative to its source folder.
    /// </summary>
    /// <remarks>
    /// Every generator here generates the same pair independently, and
    /// <see cref="MarkdownPreviewRenderer"/> generates it a fourth time — so this is the one place the
    /// convention is written down, and the one place <see cref="IsCanonicalPreview"/> can check
    /// against.
    /// </remarks>
    public static (string Preview, string PreviewLarge) CanonicalPreviewNames(string stem) =>
        ($".dir2site/{stem}/preview-{stem}.webp", $".dir2site/{stem}/preview-lg-{stem}.webp");

    /// <summary>
    /// Whether a stored preview path is one we generated, rather than one the user chose.
    /// </summary>
    /// <remarks>
    /// The difference decides whether staleness is even a meaningful question. A generated thumbnail
    /// is derived from its source, so a newer source means a wrong thumbnail. A path the user wrote
    /// by hand points at an image of their own choosing, which has no relationship to the source's
    /// timestamp at all — re-rendering on their behalf would burn the work and then, thanks to
    /// <c>NeedsPath</c>, leave the yaml still pointing at their file, so nothing would even change.
    /// It would simply happen again on every run.
    /// </remarks>
    public static bool IsCanonicalPreview(string sourceFile, string? previewRelativePath)
    {
        if (string.IsNullOrEmpty(previewRelativePath)) return false;

        var (preview, previewLarge) = CanonicalPreviewNames(Path.GetFileNameWithoutExtension(sourceFile));
        var normalised = previewRelativePath.Replace('\\', '/');

        return normalised.Equals(preview,      StringComparison.OrdinalIgnoreCase)
            || normalised.Equals(previewLarge, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="previewRelativePath"/> is older than <paramref name="sourceFile"/> —
    /// i.e. the source has been replaced or edited since the preview was rendered.
    /// </summary>
    /// <remarks>
    /// Worth asking of every artifact type. It used to be asked only of markdown and video, on the
    /// grounds that a photo or a PDF is replaced rather than revised and so its thumbnail cannot
    /// drift — but replacing is exactly the case: drop a corrected scan over the old one, keeping
    /// the filename, and the file on disk is new while the thumbnail beside it is a picture of what
    /// used to be there. Existence alone kept showing the old one for good.
    ///
    /// Known limitation: this only notices a source whose timestamp moves *forward*. Restoring from
    /// a backup or syncing down from cloud storage can land a file with its original, older
    /// timestamp, and the stale thumbnail survives. Catching that needs a recorded fingerprint of
    /// the source, which conflicts with hand-written preview paths and would add keys to every
    /// sidecar; the manual Rescan and a touched file both remain a way out.
    /// </remarks>
    public static bool PreviewIsOlderThanSource(string sourceFileDir, string previewRelativePath, string sourceFile) =>
        IsOlderThan(
            Path.Combine(sourceFileDir, previewRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            sourceFile);

    /// <summary>
    /// Whether <paramref name="derived"/> was written before <paramref name="source"/> was last
    /// changed. Answers false if either can't be read, so an unreadable file doesn't cause endless
    /// regeneration.
    /// </summary>
    internal static bool IsOlderThan(string derived, string source)
    {
        try
        {
            return File.GetLastWriteTimeUtc(derived) < File.GetLastWriteTimeUtc(source);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a derived file has to be made: it isn't there, or the source has moved on since.
    /// </summary>
    /// <remarks>
    /// The question every generator here asks before writing, and the one several of them used to
    /// ask as "is it there". <see cref="DirectoryTraverser"/> decides whether an artifact needs
    /// visiting on this same rule, so a job that answered a narrower one was enqueued for work it
    /// then declined to do — and did nothing, on every run, for as long as the project existed.
    /// </remarks>
    internal static bool Stale(string derived, string source) =>
        !File.Exists(derived) || IsOlderThan(derived, source);

    /// <summary>
    /// Makes an artifact's <c>.dir2site</c> folder — and refuses to make the artifact's own folder,
    /// or the project, along with it. Answers false when the artifact has gone.
    /// </summary>
    /// <remarks>
    /// <c>Directory.CreateDirectory</c> is <c>mkdir -p</c>, and this stage only ever means
    /// <c>mkdir</c>. It matters because previews run unattended and in parallel: a folder renamed
    /// while a generate is under way leaves a hundred jobs mid-flight, and each one rebuilt every
    /// missing segment on the way to its own output — resurrecting the project as a shell holding
    /// nothing but hidden preview directories.
    ///
    /// The guard is on the <em>source file</em>, not on the folder it sits in, and that is the whole
    /// of it. Guarding the folder looks equivalent and isn't: it is a check followed by a create, so
    /// the first job to win the race fabricates the folder for everyone, and every job behind it
    /// then finds the folder present and carries on. One winner unblocks the rest, which is why the
    /// folder came back full of preview directories rather than not at all. Nothing recreates the
    /// source file, so asking after it cannot cascade.
    ///
    /// Cancelling does not cover this either. The jobs are already running when the folder goes, and
    /// the token stops the ones that haven't started rather than the ones that have.
    /// </remarks>
    internal static bool TryCreatePreviewDir(string sourceFile, params string[] directories)
    {
        if (!File.Exists(sourceFile)) return false;

        foreach (var directory in directories) Directory.CreateDirectory(directory);
        return true;
    }

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
        if (!TryCreatePreviewDir(sourceFile, dir2site)) return null;

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

        // Missing or older than the photo, not merely missing. Dropping a corrected scan over the
        // old one under the same name is the ordinary way to replace a photo, and existence alone
        // kept every derived file from the picture that used to be there — including
        // {stem}_q90.webp, which is what the viewer displays, so the wrong picture was published
        // rather than merely a wrong thumbnail.
        //
        // It never settled either: the survey enqueues this artifact for exactly this reason, and
        // the job then did nothing, so the same work was proposed and counted on every single run.
        if (Stale(previewPath, sourceFile))
        {
            progress?.Report($"Generating preview: {fileName}");
            GenerateThumbnail(sourceFile, previewPath, 800, 600);
        }

        if (Stale(previewLargePath, sourceFile))
        {
            progress?.Report($"Generating preview (large): {fileName}");
            GenerateThumbnail(sourceFile, previewLargePath, 1200, 900);
        }

        if (Stale(imagePath, sourceFile))
        {
            progress?.Report($"Generating web image: {fileName}");
            GenerateWebImage(sourceFile, imagePath);
        }

        return (previewFileName, previewLargeFileName, imageFileName);
    }

    // One shared client for the whole process — a per-call HttpClient exhausts sockets under the
    // parallel preview pass, which is exactly where this gets called from.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Highest quality first. Not every video has a maxres frame, and YouTube answers a missing one
    // with 404, so hqdefault is the guaranteed fallback rather than a preference.
    private static readonly string[] YouTubePosterNames = ["maxresdefault.jpg", "hqdefault.jpg"];

    /// <summary>
    /// Downloads a video's poster frame and writes the same pair of WebP thumbnails every other
    /// artifact type produces, into <c>.dir2site/{stem}/</c>. Returns
    /// (previewFileName, previewLargeFileName), or null if the poster could not be fetched.
    /// </summary>
    /// <remarks>
    /// Keeping the poster local rather than hotlinking img.youtube.com is what lets a video act as a
    /// folder tile and an OpenGraph image through the ordinary preview path, and means a published
    /// page makes no third-party request until the visitor actually presses play.
    /// </remarks>
    public static (string Preview, string PreviewLarge)? GenerateVideoPreviews(
        string sourceFile,
        string videoId,
        IProgress<string>? progress = null)
    {
        var fileDir = Path.GetDirectoryName(sourceFile) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(sourceFile);

        var dir2site = Path.GetFullPath(Path.Combine(fileDir, ".dir2site", stem));
        if (!TryCreatePreviewDir(sourceFile, dir2site)) return null;

        var previewPath      = Path.Combine(dir2site, $"preview-{stem}.webp");
        var previewLargePath = Path.Combine(dir2site, $"preview-lg-{stem}.webp");

        var previewFileName      = $".dir2site/{stem}/preview-{stem}.webp";
        var previewLargeFileName = $".dir2site/{stem}/preview-lg-{stem}.webp";

        var fileName = Path.GetFileName(sourceFile);
        progress?.Report($"Fetching video poster: {fileName}");

        var poster = DownloadYouTubePoster(videoId);
        if (poster is null)
        {
            progress?.Report($"No poster available for {fileName} ({videoId})");
            return null;
        }

        // 16:9 rather than the 4:3 the other types use — a video frame cropped to 4:3 loses the
        // sides of the shot, and the player that replaces this poster is 16:9 anyway. maxresdefault
        // is exactly 1280x720, so the large size is a straight copy with no crop at all.
        GenerateThumbnail(poster, previewPath, 800, 450);
        GenerateThumbnail(poster, previewLargePath, 1200, 675);

        return (previewFileName, previewLargeFileName);
    }

    private static byte[]? DownloadYouTubePoster(string videoId)
    {
        foreach (var name in YouTubePosterNames)
        {
            try
            {
                using var response = Http.GetAsync($"https://i.ytimg.com/vi/{videoId}/{name}")
                    .GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode) continue;

                var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (bytes.Length > 0) return bytes;
            }
            catch (Exception)
            {
                // Offline, DNS failure, timeout — try the next name, then give up and let the
                // caller fall back to the placeholder card.
            }
        }

        return null;
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
        if (!TryCreatePreviewDir(sourceFile, dir2site, pagesDir)) return null;

        var previewFile      = $"preview-{stem}.webp";
        var previewLargeFile = $"preview-lg-{stem}.webp";
        var previewPath      = Path.Combine(dir2site, previewFile);
        var previewLargePath = Path.Combine(dir2site, previewLargeFile);
        var bookReaderJsonPath = Path.Combine(dir2site, $"{stem}.bookreader.json");

        // Returned names are relative paths from the artifact's source folder
        var previewFileName      = $".dir2site/{stem}/preview-{stem}.webp";
        var previewLargeFileName = $".dir2site/{stem}/preview-lg-{stem}.webp";

        // Everything already rendered, and rendered from this PDF rather than an earlier one that
        // had the same name. Without the second half, replacing a document in place kept the old
        // document's page images — the whole reader would still be showing the previous version.
        if (!Stale(previewPath, sourceFile)
            && !Stale(previewLargePath, sourceFile)
            && !Stale(bookReaderJsonPath, sourceFile))
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

            // Same reasoning as the short-circuit above, one page at a time: a page image older than
            // the PDF it came from is a page of the document that used to be here.
            if (Stale(pagePath, sourceFile))
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

    private static void GenerateThumbnail(byte[] source, string dest, uint width, uint height)
    {
        using var image = new MagickImage(source);
        Thumbnail(image, dest, width, height);
    }

    private static void GenerateThumbnail(string source, string dest, uint width, uint height)
    {
        using var image = new MagickImage(source);
        Thumbnail(image, dest, width, height);
    }

    private static void Thumbnail(MagickImage image, string dest, uint width, uint height)
    {
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
