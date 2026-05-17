using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dir2site.ViewModels;

namespace dir2site.Services;

public static class DirectoryTraverser
{
    // Explicit filenames to always skip, regardless of platform
    private static readonly HashSet<string> IgnoredFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // macOS
        ".DS_Store",
        ".AppleDouble",
        ".LSOverride",
        "Icon\r",          // macOS custom folder icon (has a carriage return in the name)
        ".Spotlight-V100",
        ".Trashes",
        ".fseventsd",
        ".VolumeIcon.icns",
        ".com.apple.timemachine.donotpresent",

        // Windows
        "Thumbs.db",
        "Thumbs.db:encryptable",
        "ehthumbs.db",
        "ehthumbs_vista.db",
        "Desktop.ini",
        "desktop.ini",
        "$RECYCLE.BIN",
        "RECYCLER",
        "RECYCLED",
        "System Volume Information",

        // Linux / general
        ".directory",      // KDE folder settings
        ".Trash-1000",
        ".nfs",            // NFS lock files (prefix match handled below)
    };

    // Directory names to skip entirely (won't recurse into them)
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // macOS
        ".Spotlight-V100",
        ".Trashes",
        ".fseventsd",
        ".TemporaryItems",
        ".AppleDB",
        ".AppleDesktop",

        // Windows
        "$RECYCLE.BIN",
        "RECYCLER",
        "RECYCLED",
        "System Volume Information",

        // Version control / tooling (commonly wanted to skip)
        ".git",
        ".svn",
        ".hg",
        ".idea",
        ".vscode",
        "node_modules",
        "__pycache__",
        ".mypy_cache",
        ".pytest_cache",
    };

    public static DirectoryTreeItem BuildTree(string rootPath, IList<string> allFiles, IList<string> allArtifacts, IProgress<string>? progress = null)
        => BuildTree(rootPath, allFiles, allArtifacts, rootPath, progress);

    private static DirectoryTreeItem BuildTree(string rootPath, IList<string> allFiles, IList<string> allArtifacts, string traversalRoot, IProgress<string>? progress)
    {
        var node = new DirectoryTreeItem(rootPath);

        if (!node.IsDirectory)
        {
            if (!ShouldIgnoreFile(rootPath))
                allFiles.Add(rootPath);
            return node;
        }

        try
        {
            foreach (var dir in Directory.GetDirectories(rootPath).OrderBy(d => d))
            {
                if (ShouldIgnoreDirectory(dir))
                    continue;

                var child = BuildTree(dir, allFiles, allArtifacts, traversalRoot, progress);
                node.Children.Add(child);
            }

            foreach (var file in Directory.GetFiles(rootPath).OrderBy(f => f))
            {
                if (ShouldIgnoreFile(file))
                    continue;

                var child = new DirectoryTreeItem(file);

                var artifact = YamlParser.TryParseYamlMeta(file, child.YamlErrors);
                if (artifact != null)
                {
                    artifact.RootFolder = rootPath;
                    artifact.TraversalRoot = traversalRoot;
                }

                // Generate previews for any image file where they are missing,
                // regardless of whether a yaml meta file exists.
                if (PreviewGenerator.IsImageFile(file))
                {
                    var alreadyHasBoth = artifact != null
                        && !string.IsNullOrEmpty(artifact.Preview)
                        && !string.IsNullOrEmpty(artifact.PreviewLarge);

                    if (!alreadyHasBoth)
                    {
                        try
                        {
                            var previews = PreviewGenerator.GeneratePreviews(file, traversalRoot, progress);
                            if (previews.HasValue && artifact != null)
                            {
                                if (string.IsNullOrEmpty(artifact.Preview))
                                    artifact.Preview = previews.Value.Preview;
                                if (string.IsNullOrEmpty(artifact.PreviewLarge))
                                    artifact.PreviewLarge = previews.Value.PreviewLarge;

                                var yamlPath = YamlParser.FindYamlMetaPath(file);
                                if (yamlPath != null)
                                    YamlParser.UpdatePreviewFields(yamlPath, artifact.Preview!, artifact.PreviewLarge!);
                            }
                        }
                        catch (Exception ex)
                        {
                            progress?.Report($"Preview failed: {Path.GetFileName(file)} — {ex.Message}");
                        }
                    }
                }

                if (PreviewGenerator.IsPdfFile(file))
                {
                    var alreadyHasBoth = artifact != null
                        && !string.IsNullOrEmpty(artifact.Preview)
                        && !string.IsNullOrEmpty(artifact.PreviewLarge);

                    if (!alreadyHasBoth)
                    {
                        try
                        {
                            var previews = PreviewGenerator.GeneratePdfPreviewsAndPages(file, traversalRoot, progress);
                            if (previews.HasValue && artifact != null)
                            {
                                if (string.IsNullOrEmpty(artifact.Preview))
                                    artifact.Preview = previews.Value.Preview;
                                if (string.IsNullOrEmpty(artifact.PreviewLarge))
                                    artifact.PreviewLarge = previews.Value.PreviewLarge;

                                var yamlPath = YamlParser.FindYamlMetaPath(file);
                                if (yamlPath != null)
                                    YamlParser.UpdatePreviewFields(yamlPath, artifact.Preview!, artifact.PreviewLarge!);
                            }
                        }
                        catch (Exception ex)
                        {
                            progress?.Report($"Preview failed: {Path.GetFileName(file)} — {ex.Message}");
                        }
                    }
                }

                allFiles.Add(file);

                // Only surface files that have a parsed artifact — others are not yet catalogued
                if (artifact == null)
                    continue;

                child.Artifact = artifact;
                allArtifacts.Add(file);
                node.Children.Add(child);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return node;
    }

    private static bool ShouldIgnoreDirectory(string path)
    {
        var name = Path.GetFileName(path);

        // Skip hidden/private directories (dot-prefix on mac/linux, underscore-prefix convention, Hidden attribute on Windows)
        if (name.StartsWith('.'))
            return true;

        if (name.StartsWith('_'))
            return true;

        if (HasHiddenAttribute(path))
            return true;

        return IgnoredDirectoryNames.Contains(name);
    }

    private static bool ShouldIgnoreFile(string path)
    {
        var name = Path.GetFileName(path);

        // Skip hidden files
        if (name.StartsWith('.'))
            return true;

        if (HasHiddenAttribute(path))
            return true;

        // Skip NFS temporary lock files (.nfsXXXXXX)
        if (name.StartsWith(".nfs", StringComparison.OrdinalIgnoreCase))
            return true;

        // Skip metadata sidecar files — they are not content nodes
        var ext = Path.GetExtension(name);
        if (ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".yml",  StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return true;

        return IgnoredFileNames.Contains(name);
    }

    private static bool HasHiddenAttribute(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.Hidden);
        }
        catch
        {
            return false;
        }
    }
}
