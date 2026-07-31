// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Platform;
using dir2site.Models;
using dir2site.ViewModels;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;

namespace dir2site.Services;

public static class SiteGenerator
{
    public static (string Summary, IReadOnlyList<string> Errors) Generate(
        string directoryRoot,
        DirectoryTreeItem rootItem,
        Dir2SiteModel config,
        GenerateProgressTracker? progressTracker = null)
    {
        // A no-op tracker rather than null: `tracker?.Method(Write(...))` would skip the write
        // itself, which is a silent and very confusing way to generate nothing.
        var tracker = progressTracker ?? new GenerateProgressTracker();
        IProgress<string> progress = tracker;

        var siteRoot = Path.Combine(directoryRoot, "_site");
        Directory.CreateDirectory(siteRoot);

        var topLevelFolders = rootItem.Children
            .Where(c => c.IsDirectory)
            .ToList();

        // A fixed handful of files that ship with the app rather than with the project, so they're
        // one step rather than a counted stage.
        progress.Report("Copying framework assets...");
        CopyBootstrapAssets(siteRoot, progress);
        CopyOpenSeaDragonAssets(siteRoot, progress);
        CopyBookReaderAssets(siteRoot, progress);

        var loader = new AvaloniaTemplateLoader();
        CopySiteAssets(siteRoot, config, loader, progress);
        var templates = new TemplateSet(loader);

        var errors = new ConcurrentBag<string>();
        tracker.SetPageTotal(CountPages(rootItem));
        GeneratePage(rootItem, siteRoot, directoryRoot, config, topLevelFolders, 0,
            [], templates, progress, errors, tracker);

        var copyJobs = new List<CopyJob>();
        CollectFolderPreviewCopyJobs(rootItem, directoryRoot, siteRoot, copyJobs);
        CollectUnderscoreFolderCopyJobs(directoryRoot, directoryRoot, siteRoot, copyJobs);
        CollectLogoCopyJob(directoryRoot, siteRoot, config.Logo, copyJobs);

        tracker.SetFileTotal(copyJobs.Count);
        foreach (var job in copyJobs)
        {
            tracker.FileDone(CopyFileIfNewer(job.Src, job.Dest, progress, job.Label));
        }

        return ("Site generated → _site/", [.. errors]);
    }

    /// <summary>
    /// How many pages <see cref="GeneratePage"/> is about to write — one per directory node plus one
    /// per artifact that gets a page of its own — using the same predicates it recurses on, so the
    /// total can't drift from what actually gets rendered. Videos play inline and get no page.
    /// </summary>
    private static int CountPages(DirectoryTreeItem node)
    {
        var count = 1;
        foreach (var child in node.Children)
        {
            if (child.IsDirectory) count += CountPages(child);
            else if (child.Artifact != null && child.Artifact.Type != ArtifactType.Video) count++;
        }
        return count;
    }

    private static void GeneratePage(
        DirectoryTreeItem node,
        string outputDir,
        string directoryRoot,
        Dir2SiteModel config,
        IList<DirectoryTreeItem> topLevelFolders,
        int depth,
        IList<string> ancestorNames,
        TemplateSet templates,
        IProgress<string> progress,
        ConcurrentBag<string> errors,
        GenerateProgressTracker tracker)
    {
        var label = depth == 0 ? "index.html" : $"{node.Name}/index.html";

        Directory.CreateDirectory(outputDir);

        // Depth-0 children don't carry the root node name — "Home" is the implicit root
        var childAncestors = depth == 0
            ? (IList<string>)[]
            : [.. ancestorNames, node.Name];

        var indexHtmlPath = Path.Combine(outputDir, "index.html");

        progress.Report($"Generating {label}...");

        var pageTitle = depth == 0 ? config.Title : node.Name;
        var prefix = RelativePrefix(depth);

        var siteObj = new ScriptObject();
        siteObj.SetValue("title", config.Title, readOnly: true);
        siteObj.SetValue("footer", config.Footer, readOnly: true);
        siteObj.SetValue("logo", config.Logo, readOnly: true);
        siteObj.SetValue("primary_color", config.PrimaryColor, readOnly: true);
        siteObj.SetValue("secondary_color", config.SecondaryColor, readOnly: true);
        siteObj.SetValue("background_color", config.BackgroundColor, readOnly: true);
        siteObj.SetValue("navbar_dark", config.NavbarDark, readOnly: true);
        siteObj.SetValue("url", config.SiteUrl.TrimEnd('/'), readOnly: true);

        var navFolders = topLevelFolders
            .Select(f =>
            {
                var obj = new ScriptObject();
                obj.SetValue("name", f.Name, readOnly: true);
                obj.SetValue("href", $"{prefix}{f.Name}/", readOnly: true);
                return (object)obj;
            })
            .ToList();

        var breadcrumbs = BuildBreadcrumbs(prefix, depth, ancestorNames, node.Name);

        var items = node.Children
            .Select(child => (object)BuildCardModel(child, prefix, directoryRoot))
            .ToList();

        // Only pages that actually embed a player pull in the YouTube glue.
        var hasVideo = node.Children.Any(c => !c.IsDirectory && c.Artifact?.Type == ArtifactType.Video);

        var ogTitle = depth == 0
            ? config.Title
            : string.Join(" > ", ancestorNames.Concat([node.Name]));

        var ogImageResult = FindFirstArtifactWithPreview(node);
        var ogImage = ogImageResult.HasValue
            ? GetOgImageRootRelative(ogImageResult.Value.Item1, directoryRoot, ogImageResult.Value.Item2)
            : "";

        var globals = new ScriptObject();
        globals.SetValue("site", siteObj, readOnly: true);
        globals.SetValue("page_title", pageTitle, readOnly: true);
        globals.SetValue("prefix", prefix, readOnly: true);
        globals.SetValue("nav_folders", navFolders, readOnly: true);
        globals.SetValue("breadcrumbs", breadcrumbs, readOnly: true);
        globals.SetValue("items", items, readOnly: true);
        globals.SetValue("has_video", hasVideo, readOnly: true);
        globals.SetValue("og_title", ogTitle, readOnly: true);
        globals.SetValue("og_description", ogTitle, readOnly: true);
        globals.SetValue("og_image", ogImage, readOnly: true);

        var context = new TemplateContext { TemplateLoader = templates.Loader };
        context.PushGlobal(globals);

        var html = templates.Collection.Render(context);
        tracker.PageDone(WriteIfChanged(indexHtmlPath, html, Encoding.UTF8));

        foreach (var child in node.Children.Where(c => c.IsDirectory))
        {
            var childOutputDir = Path.Combine(outputDir, child.Name);
            GeneratePage(child, childOutputDir, directoryRoot, config, topLevelFolders,
                depth + 1, childAncestors, templates, progress, errors, tracker);
        }

        // Videos play inline on this page, so they get no page of their own — generating one would
        // produce an orphan that nothing links to.
        var artifactChildren = node.Children
            .Where(c => !c.IsDirectory && c.Artifact != null && c.Artifact.Type != ArtifactType.Video)
            .ToList();
        foreach (var child in artifactChildren)
        {
            try
            {
                var change = GenerateArtifactPage(child, outputDir, directoryRoot, config, topLevelFolders,
                    depth + 1, childAncestors, templates, progress);
                tracker.PageDone(change);
                // What happened to an artifact's own page is what "new" and "updated" mean for the
                // artifact: a photo the site had never rendered, or one whose page now reads
                // differently. Nothing else can say it — a yaml file's timestamp only records when
                // it was written, not whether the site had already taken it in.
                tracker.ArtifactChanged(change);
            }
            catch (Exception ex)
            {
                errors.Add($"{child.Name}: {ex.Message}");
                // A page that failed is still a page accounted for; the error is reported separately.
                tracker.PageDone(Change.None);
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="content"/> only when it differs from what is already on disk.
    /// Every page is re-rendered on every generate — the menu, the site config and a collection's
    /// item set are all global, so no local mtime can tell us whether a page is stale — but leaving
    /// byte-identical files untouched keeps their mtime stable, which is what SftpSync uses to
    /// decide what needs re-uploading.
    /// </summary>
    /// <returns>
    /// What happened to the page: <see cref="Change.New"/> when the site had no such page at all,
    /// <see cref="Change.Updated"/> when it had one and the render differs, and
    /// <see cref="Change.None"/> when the output is identical.
    /// </returns>
    private static Change WriteIfChanged(string path, string content, Encoding? encoding = null)
    {
        var existed = File.Exists(path);
        if (existed)
        {
            try
            {
                // ReadAllText strips any byte-order mark, so this compares text to text
                // regardless of which encoding overload originally wrote the file.
                if (File.ReadAllText(path) == content) return Change.None;
            }
            catch
            {
                // Unreadable for any reason — fall through and overwrite it.
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (encoding != null)
            File.WriteAllText(path, content, encoding);
        else
            File.WriteAllText(path, content);
        return existed ? Change.Updated : Change.New;
    }

    private static ScriptObject MakeCrumb(string name, string href, bool isActive)
    {
        var obj = new ScriptObject();
        obj.SetValue("name", name, readOnly: true);
        obj.SetValue("href", href, readOnly: true);
        obj.SetValue("is_active", isActive, readOnly: true);
        return obj;
    }

    private static List<object> BuildBreadcrumbs(
        string prefix, int depth, IList<string> ancestorNames, string activeName)
    {
        var crumbs = new List<object>();
        if (depth == 0) return crumbs;
        crumbs.Add(MakeCrumb("Home", prefix, isActive: false));
        for (int i = 0; i < ancestorNames.Count; i++)
        {
            var href = string.Concat(Enumerable.Repeat("../", depth - i - 1));
            crumbs.Add(MakeCrumb(ancestorNames[i], href, isActive: false));
        }
        crumbs.Add(MakeCrumb(activeName, "", isActive: true));
        return crumbs;
    }

    private static ScriptObject BuildCardModel(
        DirectoryTreeItem item,
        string prefix,
        string directoryRoot)
    {
        string caption, badge, href, imgSrc;
        var video = item.Artifact as Video;

        if (item.IsDirectory)
        {
            caption = item.Name;
            badge = "Folder";
            href = $"{item.Name}/";
            var firstArtifactResult = FindFirstArtifactWithPreview(item);
            imgSrc = firstArtifactResult.HasValue
                ? GetPreviewSrc(firstArtifactResult.Value.Item1, directoryRoot, prefix, firstArtifactResult.Value.Item2)
                : "";
        }
        else
        {
            caption = item.Artifact?.Caption ?? item.Name;
            badge = item.Artifact != null ? TypeBadge(item.Artifact.Type) : "File";
            var stem = Path.GetFileNameWithoutExtension(item.Name);
            // A video has no page of its own, so linking to one would be a dead link.
            href = video != null ? "" : $"{stem}/";
            imgSrc = item.Artifact != null ? GetPreviewSrc(item.Artifact, directoryRoot, prefix, stem) : "";
        }

        var obj = new ScriptObject();
        obj.SetValue("caption", caption, readOnly: true);
        obj.SetValue("badge", badge, readOnly: true);
        obj.SetValue("href", href, readOnly: true);
        obj.SetValue("img_src", imgSrc, readOnly: true);
        obj.SetValue("is_folder", item.IsDirectory, readOnly: true);
        obj.SetValue("is_video", video != null, readOnly: true);
        obj.SetValue("video_id", video?.VideoId ?? "", readOnly: true);
        obj.SetValue("video_start", video?.Start?.ToString() ?? "", readOnly: true);
        obj.SetValue("video_url", video?.SourceUrl ?? "", readOnly: true);
        obj.SetValue("url_text", video != null ? item.Artifact?.UrlText ?? "" : "", readOnly: true);
        obj.SetValue("credit", item.Artifact?.Credit ?? "", readOnly: true);
        return obj;
    }

    // Human-friendly label shown on cards and artifact pages in the generated site.
    private static string TypeBadge(ArtifactType type) => type switch
    {
        ArtifactType.Markdown => "Article",
        _ => type.ToString(),
    };

    private static (Artifact, string)? FindFirstArtifactWithPreview(DirectoryTreeItem node)
    {
        // Prefer direct file children over anything in subdirectories.
        // Among direct children: photos/deepzooms first, then alphabetical by caption.
        var direct = node.Children
            .Where(c => !c.IsDirectory && c.Artifact?.Preview != null)
            .OrderBy(c => c.Artifact!.Type is ArtifactType.Photo or ArtifactType.Deepzoom ? 0 : 1)
            .ThenBy(c => c.Artifact!.Caption ?? c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => (c.Artifact!, Path.GetFileNameWithoutExtension(c.Name)))
            .FirstOrDefault();

        if (direct.Item1 != null) return direct;

        foreach (var child in node.Children.Where(c => c.IsDirectory))
        {
            var found = FindFirstArtifactWithPreview(child);
            if (found != null) return found;
        }

        return null;
    }

    private static string GetPreviewSrc(Artifact artifact, string directoryRoot, string prefix, string stem)
    {
        if (artifact.Preview == null || artifact.RootFolder == null) return "";
        var rel = Path.GetRelativePath(directoryRoot, artifact.RootFolder).Replace('\\', '/');
        var filename = StripDir2SitePrefix(artifact.Preview, stem);
        return rel == "." ? $"{prefix}{stem}/{filename}" : $"{prefix}{rel}/{stem}/{filename}";
    }

    // Strips the ".dir2site/{stem}/" prefix from a stored preview path, leaving the bare filename (or subpath).
    private static string StripDir2SitePrefix(string path, string stem)
    {
        var normalized = path.Replace('\\', '/');
        var prefix = $".dir2site/{stem}/";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[prefix.Length..]
            : Path.GetFileName(normalized);
    }

    private static string RelativePrefix(int depth) =>
        string.Concat(Enumerable.Repeat("../", depth));

    /// <summary>One file to place in _site. Collected up front so the copy stage knows its total.</summary>
    private sealed record CopyJob(string Src, string Dest, string Label);

    // Walks the tree one directory at a time. Each artifact's previews live in .dir2site/{stem}/
    // so they are self-contained — copy the whole subfolder straight into the artifact's output dir.
    private static void CollectFolderPreviewCopyJobs(DirectoryTreeItem node, string directoryRoot, string siteRoot, List<CopyJob> jobs)
    {
        var folderRel = Path.GetRelativePath(directoryRoot, node.FullPath);

        foreach (var child in node.Children.Where(c => !c.IsDirectory && c.Artifact != null))
        {
            var stem = Path.GetFileNameWithoutExtension(child.Name);
            var stemDir = Path.Combine(node.FullPath, ".dir2site", stem);
            if (!Directory.Exists(stemDir)) continue;

            var destDir = folderRel == "."
                ? Path.Combine(siteRoot, stem)
                : Path.Combine(siteRoot, folderRel, stem);

            foreach (var file in Directory.EnumerateFiles(stemDir, "*", SearchOption.AllDirectories))
            {
                var fileRel = Path.GetRelativePath(stemDir, file);
                jobs.Add(new CopyJob(file, Path.Combine(destDir, fileRel), fileRel));
            }
        }

        foreach (var child in node.Children.Where(c => c.IsDirectory))
            CollectFolderPreviewCopyJobs(child, directoryRoot, siteRoot, jobs);
    }

    // Copies every "_"-prefixed folder (e.g. _media) verbatim into _site at its relative path, so
    // markdown articles can reference static includes. _site itself and "."-prefixed folders
    // (.git, .dir2site, …) are skipped. Underscore folders are not scanned as artifacts.
    private static void CollectUnderscoreFolderCopyJobs(string current, string directoryRoot, string siteRoot, List<CopyJob> jobs)
    {
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(current); }
        catch { return; }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.')) continue;          // .git, .dir2site, …
            if (name.Equals("_site", StringComparison.OrdinalIgnoreCase)) continue;

            if (name.StartsWith('_'))
            {
                var rel = Path.GetRelativePath(directoryRoot, dir);
                var destRoot = Path.Combine(siteRoot, rel);
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var fileRel = Path.GetRelativePath(dir, file);
                    jobs.Add(new CopyJob(file, Path.Combine(destRoot, fileRel), Path.Combine(rel, fileRel)));
                }
                continue; // don't descend further — the whole subtree was copied
            }

            CollectUnderscoreFolderCopyJobs(dir, directoryRoot, siteRoot, jobs);
        }
    }

    private static void CollectLogoCopyJob(string directoryRoot, string siteRoot, string logoFilename, List<CopyJob> jobs)
    {
        if (string.IsNullOrEmpty(logoFilename)) return;
        var src = Path.Combine(directoryRoot, logoFilename);
        if (File.Exists(src))
            jobs.Add(new CopyJob(src, Path.Combine(siteRoot, logoFilename), logoFilename));
    }

    /// <summary>Reports whether this artifact's page was newly created, rewritten, or unchanged.</summary>
    private static Change GenerateArtifactPage(
        DirectoryTreeItem item,
        string parentOutputDir,
        string directoryRoot,
        Dir2SiteModel config,
        IList<DirectoryTreeItem> topLevelFolders,
        int depth,
        IList<string> ancestorNames,
        TemplateSet templates,
        IProgress<string>? progress)
    {
        var artifact = item.Artifact!;
        var stem = Path.GetFileNameWithoutExtension(item.Name);
        var outputDir = Path.Combine(parentOutputDir, stem);
        Directory.CreateDirectory(outputDir);

        var indexHtmlPath = Path.Combine(outputDir, "index.html");

        progress?.Report($"Generating {stem}/index.html...");

        var prefix = RelativePrefix(depth);

        var siteObj = new ScriptObject();
        siteObj.SetValue("title", config.Title, readOnly: true);
        siteObj.SetValue("footer", config.Footer, readOnly: true);
        siteObj.SetValue("logo", config.Logo, readOnly: true);
        siteObj.SetValue("primary_color", config.PrimaryColor, readOnly: true);
        siteObj.SetValue("secondary_color", config.SecondaryColor, readOnly: true);
        siteObj.SetValue("background_color", config.BackgroundColor, readOnly: true);
        siteObj.SetValue("navbar_dark", config.NavbarDark, readOnly: true);
        siteObj.SetValue("url", config.SiteUrl.TrimEnd('/'), readOnly: true);

        var navFolders = topLevelFolders
            .Select(f =>
            {
                var obj = new ScriptObject();
                obj.SetValue("name", f.Name, readOnly: true);
                obj.SetValue("href", $"{prefix}{f.Name}/", readOnly: true);
                return (object)obj;
            })
            .ToList();

        var breadcrumbs = BuildBreadcrumbs(prefix, depth, ancestorNames, artifact.Caption ?? stem);

        var caption = artifact.Caption ?? stem;
        var previewSrc = GetPreviewSrc(artifact, directoryRoot, prefix, stem);
        var previewLargeSrc = GetPreviewLargeSrc(artifact, stem);

        var artifactObj = new ScriptObject();
        artifactObj.SetValue("caption", caption, readOnly: true);
        artifactObj.SetValue("credit", artifact.Credit ?? "", readOnly: true);
        artifactObj.SetValue("date", artifact.Date ?? "", readOnly: true);
        artifactObj.SetValue("badge", TypeBadge(artifact.Type), readOnly: true);
        artifactObj.SetValue("preview_src", previewSrc, readOnly: true);

        string templateName;
        switch (artifact.Type)
        {
            case ArtifactType.Photo:
            case ArtifactType.Deepzoom:
                // Prefer the full-res WebP; fall back to large preview if image not yet generated
                var osdSrc = GetImageSrc(artifact, stem);
                if (string.IsNullOrEmpty(osdSrc))
                    osdSrc = previewLargeSrc;
                artifactObj.SetValue("image_src", osdSrc, readOnly: true);
                templateName = "artifact-photo";
                break;

            case ArtifactType.Pdf:
                artifactObj.SetValue("author", (artifact as Document)?.Author ?? "", readOnly: true);
                artifactObj.SetValue("bookreader_data", BuildBookReaderData(artifact, stem), readOnly: true);
                templateName = "artifact-pdf";
                break;

            case ArtifactType.Markdown:
                artifactObj.SetValue("html_content", MarkdownRenderer.ToHtml(item.FullPath), readOnly: true);
                templateName = "artifact-markdown";
                break;

            default:
                templateName = "artifact-default";
                break;
        }

        var ogTitle = string.Join(" > ", ancestorNames.Concat([caption]));
        var ogImage = GetOgImageRootRelative(artifact, directoryRoot, stem);

        var globals = new ScriptObject();
        globals.SetValue("site", siteObj, readOnly: true);
        globals.SetValue("prefix", prefix, readOnly: true);
        globals.SetValue("nav_folders", navFolders, readOnly: true);
        globals.SetValue("breadcrumbs", breadcrumbs, readOnly: true);
        globals.SetValue("artifact", artifactObj, readOnly: true);
        globals.SetValue("og_title", ogTitle, readOnly: true);
        globals.SetValue("og_description", caption, readOnly: true);
        globals.SetValue("og_image", ogImage, readOnly: true);

        var context = new TemplateContext { TemplateLoader = templates.Loader };
        context.PushGlobal(globals);

        var html = templates.Artifact(templateName).Render(context);
        return WriteIfChanged(indexHtmlPath, html, Encoding.UTF8);
    }

    private static string GetOgImageRootRelative(Artifact artifact, string directoryRoot, string stem)
    {
        var src = artifact.PreviewLarge ?? artifact.Preview;
        if (src == null || artifact.RootFolder == null) return "";
        var rel = Path.GetRelativePath(directoryRoot, artifact.RootFolder).Replace('\\', '/');
        var filename = StripDir2SitePrefix(src, stem);
        return rel == "." ? $"{stem}/{filename}" : $"{rel}/{stem}/{filename}";
    }

    private static string GetPreviewLargeSrc(Artifact artifact, string stem)
    {
        if (artifact.PreviewLarge == null) return "";
        return StripDir2SitePrefix(artifact.PreviewLarge, stem);
    }

    // Full-resolution web WebP for the OSD viewer — co-located with the artifact detail page
    private static string GetImageSrc(Artifact artifact, string stem)
    {
        if (artifact is not Photo photo || photo.Image == null) return "";
        return StripDir2SitePrefix(photo.Image, stem);
    }

    private static string BuildBookReaderData(Artifact artifact, string stem)
    {
        if (artifact.RootFolder == null) return "[]";
        var jsonPath = Path.Combine(artifact.RootFolder, ".dir2site", stem, $"{stem}.bookreader.json");
        if (!File.Exists(jsonPath)) return "[]";

        try
        {
            var raw = File.ReadAllText(jsonPath);
            var doc = JsonNode.Parse(raw);
            var dataArray = doc?["data"]?.AsArray();
            if (dataArray == null) return "[]";

            // Remap URIs: detail page is one level below the folder, so prepend ../
            foreach (var spread in dataArray)
            {
                if (spread is not JsonArray pages) continue;
                foreach (var page in pages)
                {
                    if (page?["uri"] is JsonValue uriVal)
                    {
                        var uri = uriVal.GetValue<string>();
                        page["uri"] = uri;
                    }
                }
            }

            return dataArray.ToJsonString();
        }
        catch
        {
            return "[]";
        }
    }

    private static void CopyOpenSeaDragonAssets(string siteRoot, IProgress<string>? progress)
    {
        const string baseUri = "avares://dir2site/Assets/js/openseadragon-bin-6.0.2/";
        var destBase = Path.Combine(siteRoot, "js", "openseadragon");

        CopyEmbeddedFile(
            $"{baseUri}openseadragon.min.js",
            Path.Combine(destBase, "openseadragon.min.js"),
            progress);
        CopyEmbeddedFile(
            $"{baseUri}openseadragon.min.js.map",
            Path.Combine(destBase, "openseadragon.min.js.map"),
            progress);

        CopyEmbeddedDirectory($"{baseUri}images/", Path.Combine(destBase, "images"), progress);
    }

    private static void CopyBookReaderAssets(string siteRoot, IProgress<string>? progress)
    {
        const string baseUri = "avares://dir2site/Assets/js/bookreader-5.0.0-111/BookReader/";
        var destBase = Path.Combine(siteRoot, "js", "bookreader");

        foreach (var file in new[] { "BookReader.js", "BookReader.css", "jquery-3.js" })
        {
            CopyEmbeddedFile($"{baseUri}{file}", Path.Combine(destBase, file), progress);
        }

        CopyEmbeddedDirectory($"{baseUri}images/", Path.Combine(destBase, "images"), progress);
    }

    private static void CopyEmbeddedFile(string avaloniaUri, string dest, IProgress<string>? progress) =>
        CopyEmbeddedIfStale(avaloniaUri, dest, progress);

    private static void CopyEmbeddedDirectory(
        string avaloniaBaseUri,
        string destDir,
        IProgress<string>? progress)
    {
        var baseUri = new Uri(avaloniaBaseUri.TrimEnd('/') + "/");
        var assets = AssetLoader.GetAssets(baseUri, null);
        foreach (var assetUri in assets)
        {
            var dest = Path.Combine(destDir, Path.GetFileName(assetUri.LocalPath));
            CopyEmbeddedIfStale(assetUri.ToString(), dest, progress);
        }
    }

    private static void CopySiteAssets(string siteRoot, Dir2SiteModel config, AvaloniaTemplateLoader loader, IProgress<string>? progress)
    {
        var siteObj = new ScriptObject();
        siteObj.SetValue("primary_color",   config.PrimaryColor,   readOnly: true);
        siteObj.SetValue("secondary_color", config.SecondaryColor, readOnly: true);
        siteObj.SetValue("background_color",config.BackgroundColor, readOnly: true);
        siteObj.SetValue("navbar_dark",     config.NavbarDark,     readOnly: true);

        var globals = new ScriptObject();
        globals.SetValue("site", siteObj, readOnly: true);

        var context = new TemplateContext { TemplateLoader = loader };
        context.PushGlobal(globals);

        var template = Template.Parse(loader.LoadByName("site-css"), "site-css.html");
        var css = template.Render(context);
        WriteIfChanged(Path.Combine(siteRoot, "css", "site.css"), css);

        var jsTemplate = Template.Parse(loader.LoadByName("site-js"), "site-js.html");
        var js = jsTemplate.Render(context);
        WriteIfChanged(Path.Combine(siteRoot, "js", "site.js"), js);

        // Written unconditionally, like every other asset; only pages with a video reference it.
        var videoTemplate = Template.Parse(loader.LoadByName("video-js"), "video-js.html");
        var videoJs = videoTemplate.Render(context);
        WriteIfChanged(Path.Combine(siteRoot, "js", "video.js"), videoJs);
    }

    private static void CopyBootstrapAssets(string siteRoot, IProgress<string>? progress)
    {
        var files = new[]
        {
            ("avares://dir2site/Assets/js/bootstrap-5.3.8-dist/css/bootstrap.min.css",
             Path.Combine(siteRoot, "js", "bootstrap", "bootstrap.min.css")),
            ("avares://dir2site/Assets/js/bootstrap-5.3.8-dist/js/bootstrap.bundle.min.js",
             Path.Combine(siteRoot, "js", "bootstrap", "bootstrap.bundle.min.js")),
        };

        foreach (var (uri, dest) in files)
            CopyEmbeddedIfStale(uri, dest, progress);
    }

    /// <returns>
    /// New when the site had no such file, Updated when it had a stale one, None when it was
    /// already current and nothing was copied.
    /// </returns>
    private static Change CopyFileIfNewer(string src, string dest, IProgress<string>? progress, string? label = null)
    {
        var existed = File.Exists(dest);
        if (existed && File.GetLastWriteTimeUtc(dest) >= File.GetLastWriteTimeUtc(src)) return Change.None;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        progress?.Report($"Copying {label ?? Path.GetFileName(dest)}...");
        File.Copy(src, dest, overwrite: true);
        return existed ? Change.Updated : Change.New;
    }

    private static readonly DateTime _assemblyTime = GetAssemblyTime();

    private static DateTime GetAssemblyTime()
    {
        var proc = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(proc) && File.Exists(proc))
            return File.GetLastWriteTimeUtc(proc);
        return DateTime.UtcNow;
    }

    private static void CopyEmbeddedIfStale(string avaloniaUri, string dest, IProgress<string>? progress)
    {
        if (File.Exists(dest) && File.GetLastWriteTimeUtc(dest) >= _assemblyTime) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        progress?.Report($"Copying {Path.GetFileName(dest)}...");
        using var stream = AssetLoader.Open(new Uri(avaloniaUri));
        using var outFile = File.Create(dest);
        stream.CopyTo(outFile);
    }

    // Every page is rendered on every generate, so each template is parsed once per run
    // rather than once per page.
    private sealed class TemplateSet(AvaloniaTemplateLoader loader)
    {
        private readonly Dictionary<string, Template> _parsed = [];

        public AvaloniaTemplateLoader Loader { get; } = loader;

        public Template Collection => Get("collection");

        public Template Artifact(string templateName) => Get(templateName);

        private Template Get(string name)
        {
            if (_parsed.TryGetValue(name, out var template)) return template;
            template = Template.Parse(Loader.LoadByName(name), $"{name}.html");
            _parsed[name] = template;
            return template;
        }
    }

    // Loads Scriban templates from Avalonia embedded resources under Assets/templates/
    private sealed class AvaloniaTemplateLoader : ITemplateLoader
    {
        private const string BaseUri = "avares://dir2site/Assets/templates/";

        public string LoadByName(string name) => Load(null!, default, name);

        public string GetPath(TemplateContext context, SourceSpan callerSpan, string templateName) =>
            templateName;

        public string Load(TemplateContext context, SourceSpan callerSpan, string templatePath)
        {
            var uri = new Uri($"{BaseUri}{templatePath}.html");
            using var stream = AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public ValueTask<string?> LoadAsync(TemplateContext context, SourceSpan callerSpan, string templatePath) =>
            new(Load(context, callerSpan, templatePath));
    }
}
