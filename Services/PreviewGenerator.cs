using System;
using System.IO;
using ImageMagick;

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

    /// <summary>
    /// Generates preview and preview-large webp images into the .www mirror tree.
    /// Returns (previewFileName, previewLargeFileName), or null if generation was skipped/failed.
    /// </summary>
    public static (string Preview, string PreviewLarge)? GeneratePreviews(
        string sourceFile,
        string traversalRoot,
        IProgress<string>? progress = null)
    {
        if (!IsImageFile(sourceFile))
            return null;

        var fileDir = Path.GetDirectoryName(sourceFile) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(sourceFile);

        var relativeDir = Path.GetRelativePath(traversalRoot, fileDir);
        var wwwDir = Path.GetFullPath(Path.Combine(traversalRoot, ".www", relativeDir));
        Directory.CreateDirectory(wwwDir);

        var previewFileName = $"preview-{stem}.webp";
        var previewLargeFileName = $"preview-lg-{stem}.webp";

        var previewPath = Path.Combine(wwwDir, previewFileName);
        var previewLargePath = Path.Combine(wwwDir, previewLargeFileName);

        if (File.Exists(previewPath) && File.Exists(previewLargePath))
            return (previewFileName, previewLargeFileName);

        var fileName = Path.GetFileName(sourceFile);
        progress?.Report($"Generating preview: {fileName}");
        GenerateThumbnail(sourceFile, previewPath, 800, 600);

        progress?.Report($"Generating preview (large): {fileName}");
        GenerateThumbnail(sourceFile, previewLargePath, 1200, 900);

        return (previewFileName, previewLargeFileName);
    }

    /// <summary>
    /// Resolves a preview filename stored in YAML to its full path under the .www tree.
    /// </summary>
    public static string? ResolvePreviewPath(string traversalRoot, string fileDir, string? previewFileName)
    {
        if (previewFileName == null) return null;
        var relativeDir = Path.GetRelativePath(traversalRoot, fileDir);
        return Path.GetFullPath(Path.Combine(traversalRoot, ".www", relativeDir, previewFileName));
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
}
