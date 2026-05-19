using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
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
    public static string Generate(
        string directoryRoot,
        DirectoryTreeItem rootItem,
        Dir2SiteModel config,
        IProgress<string>? progress = null)
    {
        var siteRoot = Path.Combine(directoryRoot, "_site");
        Directory.CreateDirectory(siteRoot);

        var topLevelFolders = rootItem.Children
            .Where(c => c.IsDirectory)
            .ToList();

        CopyBootstrapAssets(siteRoot, progress);
        CopyOpenSeaDragonAssets(siteRoot, progress);
        CopyBookReaderAssets(siteRoot, progress);

        var loader = new AvaloniaTemplateLoader();
        CopySiteAssets(siteRoot, config, loader, progress);
        var pageTemplate = Template.Parse(loader.LoadByName("page"), "page.html");

        int pageCount = 0;
        GeneratePage(rootItem, siteRoot, directoryRoot, config, topLevelFolders, 0,
            [], ref pageCount, pageTemplate, loader, progress);

        int assetCount = CopyPreviewAssets(rootItem, directoryRoot, siteRoot, progress);
        CopyLogoAsset(directoryRoot, siteRoot, config.Logo);

        return $"Site generated: {pageCount} pages, {assetCount} assets → _site/";
    }

    private static void GeneratePage(
        DirectoryTreeItem node,
        string outputDir,
        string directoryRoot,
        Dir2SiteModel config,
        IList<DirectoryTreeItem> topLevelFolders,
        int depth,
        IList<string> ancestorNames,
        ref int pageCount,
        Template pageTemplate,
        AvaloniaTemplateLoader loader,
        IProgress<string>? progress)
    {
        var label = depth == 0 ? "index.html" : $"{node.Name}/index.html";
        progress?.Report($"Generating {label}...");

        Directory.CreateDirectory(outputDir);

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

        var globals = new ScriptObject();
        globals.SetValue("site", siteObj, readOnly: true);
        globals.SetValue("page_title", pageTitle, readOnly: true);
        globals.SetValue("prefix", prefix, readOnly: true);
        globals.SetValue("nav_folders", navFolders, readOnly: true);
        globals.SetValue("breadcrumbs", breadcrumbs, readOnly: true);
        globals.SetValue("items", items, readOnly: true);

        var context = new TemplateContext { TemplateLoader = loader };
        context.PushGlobal(globals);

        var html = pageTemplate.Render(context);
        File.WriteAllText(Path.Combine(outputDir, "index.html"), html, Encoding.UTF8);
        pageCount++;

        // Depth-0 children don't carry the root node name — "Home" is the implicit root
        var childAncestors = depth == 0
            ? (IList<string>)[]
            : [.. ancestorNames, node.Name];

        foreach (var child in node.Children.Where(c => c.IsDirectory))
        {
            var childOutputDir = Path.Combine(outputDir, child.Name);
            GeneratePage(child, childOutputDir, directoryRoot, config, topLevelFolders,
                depth + 1, childAncestors, ref pageCount, pageTemplate, loader, progress);
        }

        var artifactChildren = node.Children.Where(c => !c.IsDirectory && c.Artifact != null).ToList();
        int artifactPageCount = 0;
        Parallel.ForEach(artifactChildren, child =>
        {
            GenerateArtifactPage(child, outputDir, directoryRoot, config, topLevelFolders,
                depth + 1, childAncestors, loader, progress);
            Interlocked.Increment(ref artifactPageCount);
        });
        pageCount += artifactPageCount;
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
            badge = item.Artifact?.Type.ToString() ?? "File";
            var stem = Path.GetFileNameWithoutExtension(item.Name);
            href = $"{stem}/";
            imgSrc = item.Artifact != null ? GetPreviewSrc(item.Artifact, directoryRoot, prefix, stem) : "";
        }

        var obj = new ScriptObject();
        obj.SetValue("caption", caption, readOnly: true);
        obj.SetValue("badge", badge, readOnly: true);
        obj.SetValue("href", href, readOnly: true);
        obj.SetValue("img_src", imgSrc, readOnly: true);
        obj.SetValue("is_folder", item.IsDirectory, readOnly: true);
        return obj;
    }

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

    private static int CopyPreviewAssets(DirectoryTreeItem rootItem, string directoryRoot, string siteRoot, IProgress<string>? progress)
    {
        int count = 0;
        CopyFolderPreviews(rootItem, directoryRoot, siteRoot, ref count, progress);
        return count;
    }

    // Walks the tree one directory at a time. Each artifact's previews live in .dir2site/{stem}/
    // so they are self-contained — copy the whole subfolder straight into the artifact's output dir.
    private static void CopyFolderPreviews(DirectoryTreeItem node, string directoryRoot, string siteRoot, ref int count, IProgress<string>? progress)
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
                var dest = Path.Combine(destDir, fileRel);
                CopyFileIfNewer(file, dest, progress, fileRel);
                count++;
            }
        }

        foreach (var child in node.Children.Where(c => c.IsDirectory))
            CopyFolderPreviews(child, directoryRoot, siteRoot, ref count, progress);
    }

    private static void CopyLogoAsset(string directoryRoot, string siteRoot, string logoFilename)
    {
        if (string.IsNullOrEmpty(logoFilename)) return;
        var src = Path.Combine(directoryRoot, logoFilename);
        var dest = Path.Combine(siteRoot, logoFilename);
        if (File.Exists(src))
            CopyFileIfNewer(src, dest, progress: null);
    }

    private static void GenerateArtifactPage(
        DirectoryTreeItem item,
        string parentOutputDir,
        string directoryRoot,
        Dir2SiteModel config,
        IList<DirectoryTreeItem> topLevelFolders,
        int depth,
        IList<string> ancestorNames,
        AvaloniaTemplateLoader loader,
        IProgress<string>? progress)
    {
        var artifact = item.Artifact!;
        var stem = Path.GetFileNameWithoutExtension(item.Name);
        var outputDir = Path.Combine(parentOutputDir, stem);
        Directory.CreateDirectory(outputDir);

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
        artifactObj.SetValue("badge", artifact.Type.ToString(), readOnly: true);
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

            default:
                templateName = "artifact-default";
                break;
        }

        var globals = new ScriptObject();
        globals.SetValue("site", siteObj, readOnly: true);
        globals.SetValue("prefix", prefix, readOnly: true);
        globals.SetValue("nav_folders", navFolders, readOnly: true);
        globals.SetValue("breadcrumbs", breadcrumbs, readOnly: true);
        globals.SetValue("artifact", artifactObj, readOnly: true);

        var template = Template.Parse(loader.LoadByName(templateName), $"{templateName}.html");
        var context = new TemplateContext { TemplateLoader = loader };
        context.PushGlobal(globals);

        var html = template.Render(context);
        File.WriteAllText(Path.Combine(outputDir, "index.html"), html, Encoding.UTF8);
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

        var dest = Path.Combine(siteRoot, "css", "site.css");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, css);
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

    private static void CopyFileIfNewer(string src, string dest, IProgress<string>? progress, string? label = null)
    {
        if (File.Exists(dest) && File.GetLastWriteTimeUtc(dest) >= File.GetLastWriteTimeUtc(src)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        progress?.Report($"Copying {label ?? Path.GetFileName(dest)}...");
        File.Copy(src, dest, overwrite: true);
    }

    private static readonly DateTime _assemblyTime = GetAssemblyTime();

    private static DateTime GetAssemblyTime()
    {
        var loc = typeof(SiteGenerator).Assembly.Location;
        if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
            return File.GetLastWriteTimeUtc(loc);
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
