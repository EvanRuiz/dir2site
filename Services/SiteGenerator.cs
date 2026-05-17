using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

        var loader = new AvaloniaTemplateLoader();
        var pageTemplate = Template.Parse(loader.LoadByName("page"), "page.html");

        int pageCount = 0;
        GeneratePage(rootItem, siteRoot, directoryRoot, config, topLevelFolders, 0,
            [], ref pageCount, pageTemplate, loader, progress);

        int assetCount = CopyPreviewAssets(directoryRoot, siteRoot, progress);
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

        var navFolders = topLevelFolders
            .Select(f =>
            {
                var obj = new ScriptObject();
                obj.SetValue("name", f.Name, readOnly: true);
                obj.SetValue("href", $"{prefix}{f.Name}/index.html", readOnly: true);
                return (object)obj;
            })
            .ToList();

        // Build breadcrumb trail — empty on the home page (depth 0)
        var breadcrumbs = new List<object>();
        if (depth > 0)
        {
            breadcrumbs.Add(MakeCrumb("Home", $"{prefix}index.html", isActive: false));
            for (int i = 0; i < ancestorNames.Count; i++)
            {
                // Ancestor at list index i is at tree depth i+1; hops up = depth - (i+1)
                var ancestorHref = string.Concat(Enumerable.Repeat("../", depth - i - 1)) + "index.html";
                breadcrumbs.Add(MakeCrumb(ancestorNames[i], ancestorHref, isActive: false));
            }
            breadcrumbs.Add(MakeCrumb(node.Name, "", isActive: true));
        }

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
    }

    private static ScriptObject MakeCrumb(string name, string href, bool isActive)
    {
        var obj = new ScriptObject();
        obj.SetValue("name", name, readOnly: true);
        obj.SetValue("href", href, readOnly: true);
        obj.SetValue("is_active", isActive, readOnly: true);
        return obj;
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
            href = $"{item.Name}/index.html";
            var firstArtifact = FindFirstArtifactWithPreview(item);
            imgSrc = firstArtifact != null ? GetPreviewSrc(firstArtifact, directoryRoot, prefix) : "";
        }
        else
        {
            caption = item.Artifact?.Caption ?? item.Name;
            badge = item.Artifact?.Type.ToString() ?? "File";
            href = "#";
            imgSrc = item.Artifact != null ? GetPreviewSrc(item.Artifact, directoryRoot, prefix) : "";
        }

        var obj = new ScriptObject();
        obj.SetValue("caption", caption, readOnly: true);
        obj.SetValue("badge", badge, readOnly: true);
        obj.SetValue("href", href, readOnly: true);
        obj.SetValue("img_src", imgSrc, readOnly: true);
        obj.SetValue("is_folder", item.IsDirectory, readOnly: true);
        return obj;
    }

    private static Artifact? FindFirstArtifactWithPreview(DirectoryTreeItem node)
    {
        // Prefer direct file children over anything in subdirectories.
        // Among direct children: photos/deepzooms first, then alphabetical by caption.
        var direct = node.Children
            .Where(c => !c.IsDirectory && c.Artifact?.Preview != null)
            .OrderBy(c => c.Artifact!.Type is ArtifactType.Photo or ArtifactType.Deepzoom ? 0 : 1)
            .ThenBy(c => c.Artifact!.Caption ?? c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Artifact)
            .FirstOrDefault();

        if (direct != null) return direct;

        foreach (var child in node.Children.Where(c => c.IsDirectory))
        {
            var found = FindFirstArtifactWithPreview(child);
            if (found != null) return found;
        }

        return null;
    }

    private static string GetPreviewSrc(Artifact artifact, string directoryRoot, string prefix)
    {
        if (artifact.Preview == null || artifact.RootFolder == null) return "";
        var rel = Path.GetRelativePath(directoryRoot, artifact.RootFolder).Replace('\\', '/');
        // Strip the leading .dir2site/ segment — previews are flattened into the folder when copied to _site/
        var filename = artifact.Preview.Replace(".dir2site/", "").Replace(".dir2site\\", "");
        return rel == "." ? $"{prefix}{filename}" : $"{prefix}{rel}/{filename}";
    }

    private static string RelativePrefix(int depth) =>
        string.Concat(Enumerable.Repeat("../", depth));

    private static int CopyPreviewAssets(string directoryRoot, string siteRoot, IProgress<string>? progress)
    {
        int count = 0;
        foreach (var dir2siteDir in Directory.EnumerateDirectories(directoryRoot, ".dir2site", SearchOption.AllDirectories))
        {
            if (dir2siteDir.StartsWith(siteRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            var parentDir = Path.GetDirectoryName(dir2siteDir)!;
            var rel = Path.GetRelativePath(directoryRoot, parentDir);
            var destDir = rel == "." ? siteRoot : Path.Combine(siteRoot, rel);
            Directory.CreateDirectory(destDir);

            foreach (var file in Directory.EnumerateFiles(dir2siteDir))
            {
                var dest = Path.Combine(destDir, Path.GetFileName(file));
                if (File.Exists(dest)) continue;
                progress?.Report($"Copying {Path.GetFileName(file)}...");
                File.Copy(file, dest);
                count++;
            }
        }
        return count;
    }

    private static void CopyLogoAsset(string directoryRoot, string siteRoot, string logoFilename)
    {
        if (string.IsNullOrEmpty(logoFilename)) return;
        var src = Path.Combine(directoryRoot, logoFilename);
        var dest = Path.Combine(siteRoot, logoFilename);
        if (File.Exists(src) && !File.Exists(dest))
            File.Copy(src, dest);
    }

    private static void CopyBootstrapAssets(string siteRoot, IProgress<string>? progress)
    {
        var files = new[]
        {
            ("avares://dir2site/Assets/js/bootstrap-5.3.8-dist/css/bootstrap.min.css",
             Path.Combine(siteRoot, "bootstrap", "css", "bootstrap.min.css")),
            ("avares://dir2site/Assets/js/bootstrap-5.3.8-dist/js/bootstrap.bundle.min.js",
             Path.Combine(siteRoot, "bootstrap", "js", "bootstrap.bundle.min.js")),
        };

        foreach (var (uri, dest) in files)
        {
            if (File.Exists(dest)) continue;
            progress?.Report($"Copying {Path.GetFileName(dest)}...");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var stream = AssetLoader.Open(new Uri(uri));
            using var outFile = File.Create(dest);
            stream.CopyTo(outFile);
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
