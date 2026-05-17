using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
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

    public static DirectoryTreeItem BuildTree(string rootPath, IList<string> allFiles)
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

                var child = BuildTree(dir, allFiles);
                node.Children.Add(child);
            }

            foreach (var file in Directory.GetFiles(rootPath).OrderBy(f => f))
            {
                if (ShouldIgnoreFile(file))
                    continue;

                var child = new DirectoryTreeItem(file);
                
                var artifact = YamlParser.TryParseYamlMeta(file, child.YamlErrors);
                artifact?.RootFolder = rootPath;
                // if(artifact?.PreviewPath != null)
                // {
                //     artifact.PreviewBitmap = new Bitmap(artifact.PreviewPath);
                // }
                //
                child.Artifact = artifact;

                allFiles.Add(file);
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

        // Skip hidden directories (dot-prefix on mac/linux, Hidden attribute on Windows)
        if (name.StartsWith('.'))
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

        // Skip YAML meta files — they are metadata for adjacent media files, not content nodes
        var ext = Path.GetExtension(name);
        if (ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".yml",  StringComparison.OrdinalIgnoreCase))
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