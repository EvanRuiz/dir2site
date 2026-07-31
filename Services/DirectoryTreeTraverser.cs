// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using dir2site.Models;
using dir2site.ViewModels;

namespace dir2site.Services;

public static class DirectoryTraverser
{
    // The name lists live in PublishIgnore, shared with the SFTP upload so a folder this walk
    // refuses to read can't reach a server by some other route.

    public static DirectoryTreeItem BuildTree(string rootPath, IList<string> allFiles, IList<string> allArtifacts, IProgress<string>? progress = null)
    {
        return BuildTree(rootPath, allFiles, allArtifacts, rootPath, progress);
    }

    /// <summary>
    /// Walks the in-memory tree, checks which previews are missing, and generates them
    /// using the provided site config (for PDF resize/quality settings).
    /// Call this during the Generate step so settings affect output.
    /// </summary>
    public static void GeneratePreviews(DirectoryTreeItem root, dir2site.Models.Dir2SiteModel config, IProgress<string>? progress)
    {
        var jobs = new List<PreviewJob>();
        CollectPreviewJobs(root, jobs);
        ExecutePreviewJobs(jobs, config, progress);
    }

    // Walks the directory tree and parses YAML. Preview generation is deferred to GeneratePreviews().
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
                    artifact.RootFolder    = rootPath;
                    artifact.TraversalRoot = traversalRoot;
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

    private sealed record PreviewJob(string FilePath, string TraversalRoot, Artifact Artifact, ArtifactType Type);

    private static void CollectPreviewJobs(DirectoryTreeItem node, List<PreviewJob> jobs)
    {
        if (!node.IsDirectory && node.Artifact is { } artifact)
        {
            var file     = node.FullPath;
            var rootPath = artifact.RootFolder ?? Path.GetDirectoryName(file) ?? string.Empty;

            if (PreviewGenerator.IsImageFile(file))
            {
                var photo = artifact as dir2site.Models.Photo;
                var alreadyHasAll = !string.IsNullOrEmpty(artifact.Preview)
                    && !string.IsNullOrEmpty(artifact.PreviewLarge)
                    && PreviewGenerator.PreviewFileExists(rootPath, artifact.Preview)
                    && PreviewGenerator.PreviewFileExists(rootPath, artifact.PreviewLarge)
                    && (photo == null || (
                        !string.IsNullOrEmpty(photo.Image)
                        && PreviewGenerator.PreviewFileExists(rootPath, photo.Image)));

                if (!alreadyHasAll)
                    jobs.Add(new PreviewJob(file, artifact.TraversalRoot ?? rootPath, artifact, artifact.Type));
            }

            if (PreviewGenerator.IsPdfFile(file))
            {
                var alreadyHasBoth = !string.IsNullOrEmpty(artifact.Preview)
                    && !string.IsNullOrEmpty(artifact.PreviewLarge)
                    && PreviewGenerator.PreviewFileExists(rootPath, artifact.Preview)
                    && PreviewGenerator.PreviewFileExists(rootPath, artifact.PreviewLarge);

                if (!alreadyHasBoth)
                    jobs.Add(new PreviewJob(file, artifact.TraversalRoot ?? rootPath, artifact, ArtifactType.Pdf));
            }

            if (PreviewGenerator.IsMarkdownFile(file))
            {
                // An article's thumbnail is a rendering of its body, so editing the .md makes the
                // existing thumbnail wrong — "it exists" isn't enough here the way it is for a
                // photo or a PDF, which get replaced rather than revised.
                var alreadyHasBoth = !string.IsNullOrEmpty(artifact.Preview)
                    && !string.IsNullOrEmpty(artifact.PreviewLarge)
                    && PreviewGenerator.PreviewFileExists(rootPath, artifact.Preview)
                    && PreviewGenerator.PreviewFileExists(rootPath, artifact.PreviewLarge)
                    && !PreviewGenerator.PreviewIsOlderThanSource(rootPath, artifact.Preview, file)
                    && !PreviewGenerator.PreviewIsOlderThanSource(rootPath, artifact.PreviewLarge, file);

                if (!alreadyHasBoth)
                    jobs.Add(new PreviewJob(file, artifact.TraversalRoot ?? rootPath, artifact, ArtifactType.Markdown));
            }
        }

        foreach (var child in node.Children)
            CollectPreviewJobs(child, jobs);
    }

    private static void ExecutePreviewJobs(List<PreviewJob> jobs, dir2site.Models.Dir2SiteModel config, IProgress<string>? progress)
    {
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, job =>
        {
            try
            {
                switch (job.Type)
                {
                    case ArtifactType.Photo:
                    case ArtifactType.Deepzoom:
                    {
                        var result = PreviewGenerator.GeneratePreviews(job.FilePath, job.TraversalRoot, progress);
                        if (!result.HasValue) return;

                        if (string.IsNullOrEmpty(job.Artifact.Preview))      job.Artifact.Preview      = result.Value.Preview;
                        if (string.IsNullOrEmpty(job.Artifact.PreviewLarge)) job.Artifact.PreviewLarge = result.Value.PreviewLarge;
                        if (job.Artifact is Photo photo && string.IsNullOrEmpty(photo.Image))
                            photo.Image = result.Value.Image;

                        var yaml = YamlParser.FindYamlMetaPath(job.FilePath);
                        if (yaml != null)
                            YamlParser.UpdatePreviewFields(yaml, job.Artifact.Preview!, job.Artifact.PreviewLarge!, (job.Artifact as Photo)?.Image);
                        break;
                    }
                    case ArtifactType.Pdf:
                    {
                        var result = PreviewGenerator.GeneratePdfPreviewsAndPages(
                            job.FilePath, job.TraversalRoot,
                            config.PdfResizeEnabled, config.PdfMaxWidth, config.PdfQuality,
                            progress);
                        if (!result.HasValue) return;

                        if (string.IsNullOrEmpty(job.Artifact.Preview))      job.Artifact.Preview      = result.Value.Preview;
                        if (string.IsNullOrEmpty(job.Artifact.PreviewLarge)) job.Artifact.PreviewLarge = result.Value.PreviewLarge;

                        var yaml = YamlParser.FindYamlMetaPath(job.FilePath);
                        if (yaml != null)
                            YamlParser.UpdatePreviewFields(yaml, job.Artifact.Preview!, job.Artifact.PreviewLarge!);
                        break;
                    }
                    case ArtifactType.Markdown:
                    {
                        // Pure CPU (SkiaSharp) — safe to run in the parallel pass.
                        var result = MarkdownPreviewRenderer.RenderToWebpPreviews(job.FilePath, job.TraversalRoot, progress);
                        if (!result.HasValue) return;

                        if (string.IsNullOrEmpty(job.Artifact.Preview))      job.Artifact.Preview      = result.Value.Preview;
                        if (string.IsNullOrEmpty(job.Artifact.PreviewLarge)) job.Artifact.PreviewLarge = result.Value.PreviewLarge;

                        var yaml = YamlParser.FindYamlMetaPath(job.FilePath);
                        if (yaml != null)
                            YamlParser.UpdatePreviewFields(yaml, job.Artifact.Preview!, job.Artifact.PreviewLarge!);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"Preview failed: {Path.GetFileName(job.FilePath)} — {ex.Message}");
            }
        });
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

        return PublishIgnore.IsJunkDirectory(name);
    }

    private static bool ShouldIgnoreFile(string path)
    {
        var name = Path.GetFileName(path);

        // Skip hidden files
        if (name.StartsWith('.'))
            return true;

        if (HasHiddenAttribute(path))
            return true;

        // Skip metadata sidecar files — they are not content nodes
        var ext = Path.GetExtension(name);
        if (ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".yml",  StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return true;

        return PublishIgnore.IsJunkFile(name);
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
