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

    /// <param name="updatedYamls">
    /// Collects every yaml the walk brought up to the current key set. The app writes to the user's
    /// files here, so it can say so once when the scan finishes rather than leave them to find out
    /// from a diff.
    /// </param>
    public static DirectoryTreeItem BuildTree(
        string rootPath,
        IList<string> allFiles,
        IList<string> allArtifacts,
        IProgress<string>? progress = null,
        IList<string>? updatedYamls = null)
    {
        return BuildTree(rootPath, allFiles, allArtifacts, rootPath, progress, updatedYamls);
    }

    /// <summary>
    /// Walks the in-memory tree, checks which previews are missing, and generates them
    /// using the provided site config (for PDF resize/quality settings).
    /// Call this during the Generate step so settings affect output.
    /// </summary>
    public static void GeneratePreviews(DirectoryTreeItem root, dir2site.Models.Dir2SiteModel config, GenerateProgressTracker? progressTracker)
    {
        // A no-op tracker rather than null, so no call site has to reach for `?.` — see the same
        // note in SiteGenerator.Generate for what that costs when the argument does real work.
        var tracker = progressTracker ?? new GenerateProgressTracker();

        var survey = new PreviewSurvey([], [], []);
        CollectPreviewJobs(root, survey);

        // Every artifact is accounted for the moment the scan finishes. Whether any of them is new
        // or updated isn't decided here: that is what the site's own output says, so it is settled
        // later, as each artifact's page is written (see SiteGenerator.GeneratePage).
        tracker.SetArtifactTotal(survey.All.Count);
        tracker.AddArtifactsDone(survey.All.Count, Change.None);

        // Previews carry the progress: the collect pass has just decided, artifact by artifact,
        // which assets are missing or stale, and the rest are current before the stage starts.
        tracker.SetPreviewTotal(survey.Jobs.Count + survey.PreviewsCurrent.Count);
        tracker.AddPreviewsDone(survey.PreviewsCurrent.Count, Change.None);

        ExecutePreviewJobs(survey.Jobs, config, tracker);
    }

    // Walks the directory tree and parses YAML. Preview generation is deferred to GeneratePreviews().
    private static DirectoryTreeItem BuildTree(string rootPath, IList<string> allFiles, IList<string> allArtifacts, string traversalRoot, IProgress<string>? progress, IList<string>? updatedYamls)
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

                var child = BuildTree(dir, allFiles, allArtifacts, traversalRoot, progress, updatedYamls);
                node.Children.Add(child);
            }

            foreach (var file in Directory.GetFiles(rootPath).OrderBy(f => f))
            {
                if (ShouldIgnoreFile(file))
                    continue;

                var child = new DirectoryTreeItem(file);

                var artifact = YamlParser.TryParseYamlMeta(
                    file, child.YamlErrors, child.YamlWarnings, updatedYamls);
                if (artifact != null)
                {
                    artifact.RootFolder    = rootPath;
                    artifact.TraversalRoot = traversalRoot;

                    if (!ResolveVideoTarget(file, artifact, child.YamlErrors))
                        artifact = null;
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

    /// <summary>
    /// Re-reads a video's .url file and overlays its target onto the artifact. Returns false when
    /// the result isn't a usable video, in which case the caller drops it from the tree.
    /// </summary>
    /// <remarks>
    /// The shortcut is the source of truth for <em>which</em> video this is, so re-pointing the
    /// .url and re-generating moves the card to the new video instead of leaving a stale id in the
    /// yaml. The start offset is the exception: it is read from the URL only when the yaml
    /// doesn't already have one, so an offset the user tuned by hand isn't overwritten on every run.
    /// </remarks>
    private static bool ResolveVideoTarget(string file, Artifact artifact, List<string> errors)
    {
        if (artifact.Type != ArtifactType.Video) return true;

        if (artifact is not Video video)
        {
            errors.Add($"'{Path.GetFileName(file)}' is typed video but did not parse as one.");
            return false;
        }

        if (InternetShortcutParser.TryReadVideo(file) is { } shortcut)
        {
            video.SourceUrl = shortcut.Url;
            video.Provider  = shortcut.Video.Provider;
            video.VideoId   = shortcut.Video.VideoId;
            video.Start   ??= shortcut.Video.Start;
        }

        // Without an id there is nothing to embed, and a card with an empty player is worse than
        // no card — this is the hand-written-yaml path, since an auto-created one can't get here.
        if (string.IsNullOrWhiteSpace(video.VideoId))
        {
            errors.Add($"'{Path.GetFileName(file)}' has no video id and does not point at a supported video.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether this preview job is making thumbnails that never existed, or replacing ones that
    /// have gone stale — an edited article, a re-pointed video, or assets deleted from .dir2site.
    /// </summary>
    private static Change PreviewChange(string rootPath, Artifact artifact) =>
        !string.IsNullOrEmpty(artifact.Preview) && PreviewGenerator.PreviewFileExists(rootPath, artifact.Preview)
            ? Change.Updated
            : Change.New;

    private sealed record PreviewJob(string FilePath, string TraversalRoot, Artifact Artifact, ArtifactType Type, Change Change);

    /// <summary>
    /// What the collect pass learned about the catalogue: every artifact in the tree, and — among
    /// those that can have previews at all — which still need generating and which are current.
    /// </summary>
    private sealed record PreviewSurvey(List<Artifact> All, List<PreviewJob> Jobs, List<Artifact> PreviewsCurrent);

    /// <summary>
    /// Whether an artifact's thumbnails are both there and still say what the source says.
    /// </summary>
    /// <remarks>
    /// One rule for all four types, where there used to be four that agreed on the easy half. They
    /// differed on staleness: markdown and video were checked against the source's timestamp,
    /// photos and PDFs only for existence, on the reasoning that those are replaced rather than
    /// revised and so cannot drift. Replacing is exactly the case that breaks it — drop a corrected
    /// scan over the old one under the same name and the thumbnail beside it is a picture of what
    /// used to be there, kept for good because the file exists.
    ///
    /// The other half of the rule is new in the opposite direction. A hand-written
    /// <c>preview:</c> points at an image the user chose, so the source's timestamp says nothing
    /// about it; re-rendering would burn the work and change nothing, because <c>NeedsPath</c>
    /// rightly leaves their value alone — and it would do so again on every single run.
    /// </remarks>
    private static bool IsCurrent(string rootPath, string file, Artifact artifact)
    {
        if (string.IsNullOrEmpty(artifact.Preview) || string.IsNullOrEmpty(artifact.PreviewLarge))
            return false;

        foreach (var declared in new[] { artifact.Preview, artifact.PreviewLarge })
        {
            if (!PreviewGenerator.PreviewFileExists(rootPath, declared)) return false;

            if (PreviewGenerator.IsCanonicalPreview(file, declared)
                && PreviewGenerator.PreviewIsOlderThanSource(rootPath, declared, file))
                return false;
        }

        return true;
    }

    private static void CollectPreviewJobs(DirectoryTreeItem node, PreviewSurvey survey)
    {
        var jobs = survey.Jobs;

        if (!node.IsDirectory && node.Artifact is { } artifact)
        {
            survey.All.Add(artifact);

            var before   = jobs.Count;
            var file     = node.FullPath;
            var rootPath = artifact.RootFolder ?? Path.GetDirectoryName(file) ?? string.Empty;

            if (PreviewGenerator.IsImageFile(file))
            {
                // A photo also carries a full-resolution web copy, which has to be there too.
                var photo = artifact as dir2site.Models.Photo;
                var imageIsCurrent = photo == null
                    || (!string.IsNullOrEmpty(photo.Image)
                        && PreviewGenerator.PreviewFileExists(rootPath, photo.Image));

                if (!IsCurrent(rootPath, file, artifact) || !imageIsCurrent)
                    jobs.Add(new PreviewJob(file, artifact.TraversalRoot ?? rootPath, artifact,
                        artifact.Type, PreviewChange(rootPath, artifact)));
            }

            if (PreviewGenerator.IsPdfFile(file) && !IsCurrent(rootPath, file, artifact))
                jobs.Add(new PreviewJob(file, artifact.TraversalRoot ?? rootPath, artifact,
                    ArtifactType.Pdf, PreviewChange(rootPath, artifact)));

            if (PreviewGenerator.IsMarkdownFile(file) && !IsCurrent(rootPath, file, artifact))
                jobs.Add(new PreviewJob(file, artifact.TraversalRoot ?? rootPath, artifact,
                    ArtifactType.Markdown, PreviewChange(rootPath, artifact)));

            if (PreviewGenerator.IsUrlFile(file) && artifact.Type == ArtifactType.Video
                && !IsCurrent(rootPath, file, artifact))
                jobs.Add(new PreviewJob(file, artifact.TraversalRoot ?? rootPath, artifact,
                    ArtifactType.Video, PreviewChange(rootPath, artifact)));

            // Only the four types above can have previews at all; anything else is outside the
            // previews stage rather than complete within it.
            var canHavePreviews = PreviewGenerator.IsImageFile(file)
                || PreviewGenerator.IsPdfFile(file)
                || PreviewGenerator.IsMarkdownFile(file)
                || (PreviewGenerator.IsUrlFile(file) && artifact.Type == ArtifactType.Video);

            if (canHavePreviews && jobs.Count == before)
                survey.PreviewsCurrent.Add(artifact);
        }

        foreach (var child in node.Children)
            CollectPreviewJobs(child, survey);
    }

    /// <summary>
    /// True when a preview field has to be (re)pointed at what was just generated: it was blank, or
    /// it names a file that isn't there.
    /// </summary>
    /// <remarks>
    /// Keeping a hand-written value is right until the file it names goes missing — and a missing
    /// file is the only reason this job ran. Left alone, the stale name was written straight back
    /// to the yaml, so the same thumbnails were rebuilt on every single generate and the pages went
    /// on pointing at an image that was never there.
    /// </remarks>
    private static bool NeedsPath(string rootPath, string? current) =>
        string.IsNullOrEmpty(current) || !PreviewGenerator.PreviewFileExists(rootPath, current);

    private static void ExecutePreviewJobs(List<PreviewJob> jobs, dir2site.Models.Dir2SiteModel config, GenerateProgressTracker tracker)
    {
        IProgress<string> progress = tracker;

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

                        var rootPath = job.Artifact.RootFolder ?? Path.GetDirectoryName(job.FilePath) ?? string.Empty;
                        if (NeedsPath(rootPath, job.Artifact.Preview))      job.Artifact.Preview      = result.Value.Preview;
                        if (NeedsPath(rootPath, job.Artifact.PreviewLarge)) job.Artifact.PreviewLarge = result.Value.PreviewLarge;
                        if (job.Artifact is Photo photo && NeedsPath(rootPath, photo.Image))
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

                        var pdfRoot = job.Artifact.RootFolder ?? Path.GetDirectoryName(job.FilePath) ?? string.Empty;
                        if (NeedsPath(pdfRoot, job.Artifact.Preview))      job.Artifact.Preview      = result.Value.Preview;
                        if (NeedsPath(pdfRoot, job.Artifact.PreviewLarge)) job.Artifact.PreviewLarge = result.Value.PreviewLarge;

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

                        var mdRoot = job.Artifact.RootFolder ?? Path.GetDirectoryName(job.FilePath) ?? string.Empty;
                        if (NeedsPath(mdRoot, job.Artifact.Preview))      job.Artifact.Preview      = result.Value.Preview;
                        if (NeedsPath(mdRoot, job.Artifact.PreviewLarge)) job.Artifact.PreviewLarge = result.Value.PreviewLarge;

                        var yaml = YamlParser.FindYamlMetaPath(job.FilePath);
                        if (yaml != null)
                            YamlParser.UpdatePreviewFields(yaml, job.Artifact.Preview!, job.Artifact.PreviewLarge!);
                        break;
                    }
                    case ArtifactType.Video:
                    {
                        // The only case that does network I/O in here. That's fine — it is
                        // I/O-bound and the degree of parallelism is already capped — but it is a
                        // departure from the pure-CPU assumption the markdown case notes above.
                        if (job.Artifact is not Video video || string.IsNullOrEmpty(video.VideoId)) return;

                        var result = PreviewGenerator.GenerateVideoPreviews(job.FilePath, video.VideoId, progress);
                        if (!result.HasValue) return;

                        // Unlike the other types these are assigned unconditionally: the whole
                        // reason we got here is that the existing poster is missing or stale.
                        job.Artifact.Preview      = result.Value.Preview;
                        job.Artifact.PreviewLarge = result.Value.PreviewLarge;

                        var yaml = YamlParser.FindYamlMetaPath(job.FilePath);
                        if (yaml != null)
                        {
                            // The id comes back down with the poster so the yaml keeps agreeing
                            // with the page after the shortcut has been re-pointed.
                            var fields = new List<KeyValuePair<string, string>>
                            {
                                new("videoId", video.VideoId),
                                new("provider", video.Provider ?? InternetShortcutParser.YouTube),
                            };
                            if (video.Start is { } start)
                                fields.Add(new("start", start.ToString()));

                            YamlParser.UpdatePreviewFields(
                                yaml, job.Artifact.Preview!, job.Artifact.PreviewLarge!, extra: fields);
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"Preview failed: {Path.GetFileName(job.FilePath)} — {ex.Message}");
            }
            finally
            {
                // A preview that failed is still one we're done with — leaving it uncounted would
                // strand the stage short of its total forever.
                tracker.PreviewDone(job.Change);
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
