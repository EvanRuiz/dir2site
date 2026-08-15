// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    /// <returns>
    /// What happened, what failed, and what merely didn't do anything. Warnings are kept apart
    /// from errors because a misspelled setting or two folders competing for one address leave a
    /// site that generated perfectly well — reporting them as errors would make every typo read
    /// like a failed build. Orphans are files the site no longer has any reason to contain; they
    /// are reported rather than deleted, because generating happens off the UI thread and taking
    /// files away is the user's call.
    /// </returns>
    public static (string Summary, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Orphans) Generate(
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
        var ledger = new SiteLedger(siteRoot);

        // Only ever read to build the menu — page generation walks each node's own children — so the
        // '--' folders are dropped here rather than filtered again on every page.
        var menuFolders = rootItem.Children
            .Where(c => c.IsDirectory && !IsUnlisted(c))
            .OrderBy(c => IsMenuOnly(c) ? 1 : 0)
            .ToList();

        // A fixed handful of files that ship with the app rather than with the project, so they're
        // one step rather than a counted stage.
        progress.Report("Copying framework assets...");
        CopyBootstrapAssets(siteRoot, ledger, progress);
        CopyBootstrapIconsAssets(siteRoot, ledger, progress);
        CopyOpenSeaDragonAssets(siteRoot, ledger, progress);
        CopyBookReaderAssets(siteRoot, ledger, progress);

        var loader = new AvaloniaTemplateLoader();
        CopySiteAssets(siteRoot, config, loader, ledger, progress);
        var templates = new TemplateSet(loader);

        var errors = new ConcurrentBag<string>();
        var warnings = new ConcurrentBag<string>();
        ReportYamlNotes(rootItem, errors, warnings);
        tracker.SetPageTotal(CountPages(rootItem));
        var homePromotions = CollectHomePromotions(rootItem, directoryRoot);
        var footerColumns = BuildFooterColumns(config, directoryRoot, rootItem, warnings);
        GeneratePage(rootItem, siteRoot, directoryRoot, config, menuFolders, 0,
            [], templates, progress, errors, warnings, tracker, homePromotions, ledger, footerColumns);

        var copyJobs = new List<CopyJob>();
        CollectFolderPreviewCopyJobs(rootItem, directoryRoot, siteRoot, copyJobs, ledger);
        CollectUnderscoreFolderCopyJobs(directoryRoot, directoryRoot, siteRoot, copyJobs, ledger);
        CollectLogoCopyJob(directoryRoot, siteRoot, config.Logo, copyJobs);

        tracker.SetFileTotal(copyJobs.Count);
        foreach (var job in copyJobs)
        {
            // A file that won't copy is reported and the rest still go. Letting it escape took the
            // whole generate down with it — and since the caller resets its "busy" flag after the
            // await, the app was left spinning with every button disabled and nothing said. Locked
            // files are ordinary on Windows: an indexer, a virus scanner, the preview server, or a
            // cloud-synced original that won't come back down on demand.
            try
            {
                tracker.FileDone(CopyFileIfDifferent(job.Src, job.Dest, ledger, progress, job.Label));
            }
            catch (Exception ex)
            {
                // The destination is already in the ledger — registered before the copy was
                // attempted — so a file that failed to copy keeps whatever is in the site rather
                // than being offered for deletion on top of the error.
                errors.Add($"{job.Label}: {ex.Message}");
                tracker.FileDone(Change.None);
            }
        }

        // Last, so that everything this run meant to put in the site has been registered. A run
        // that couldn't read part of the project offers nothing: it can't tell a folder that was
        // emptied from one it simply never saw, and guessing wrong here deletes the user's site.
        IReadOnlyList<string> orphans = [];
        if (ledger.IsIncomplete)
        {
            warnings.Add(
                "Part of the project folder could not be read, so nothing was offered for removal " +
                "this time. Files left over from deleted content are still in _site — generate " +
                "again once the folder is readable.");
        }
        else
        {
            orphans = FindOrphans(ledger);
        }

        var summary = orphans.Count == 0
            ? "Site generated → _site/"
            : $"Site generated → _site/ — {orphans.Count} file(s) no longer part of the site";
        return (summary, [.. errors], [.. warnings], orphans);
    }

    /// <summary>
    /// How many pages <see cref="GeneratePage"/> is about to write — one per directory node plus one
    /// per artifact that gets a page of its own — using the same predicates it recurses on, so the
    /// total can't drift from what actually gets rendered. Videos play inline and get no page.
    /// </summary>
    private static int CountPages(DirectoryTreeItem node)
    {
        // A folder published as its single artifact writes one page, not two.
        if (SoleArtifact(node) != null) return 1;

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
        IList<DirectoryTreeItem> menuFolders,
        int depth,
        IList<string> ancestorNames,
        TemplateSet templates,
        IProgress<string> progress,
        ConcurrentBag<string> errors,
        ConcurrentBag<string> warnings,
        GenerateProgressTracker tracker,
        IReadOnlyList<HomePromotion> homePromotions,
        SiteLedger ledger,
        List<List<FooterLink>> footerColumns)
    {
        // The site root always gets a home page, however little is in it.
        if (depth > 0 && SoleArtifact(node) is { } soleArtifact)
        {
            try
            {
                var soleChange = GenerateArtifactPage(soleArtifact, outputDir, directoryRoot, config,
                    menuFolders, depth, ancestorNames, templates, footerColumns, progress, ledger,
                    atFolderIndex: true);
                tracker.PageDone(soleChange);
                tracker.ArtifactChanged(soleChange);
            }
            catch (Exception ex)
            {
                errors.Add($"{soleArtifact.Name}: {ex.Message}");
            }
            return;
        }

        var label = depth == 0 ? "index.html" : $"{PublicName(node.Name)}/index.html";

        Directory.CreateDirectory(outputDir);

        // Depth-0 children don't carry the root node name — "Home" is the implicit root
        var childAncestors = depth == 0
            ? (IList<string>)[]
            : [.. ancestorNames, PublicName(node.Name)];

        var indexHtmlPath = Path.Combine(outputDir, "index.html");

        // Registered before the render rather than after it. The ledger records what this run
        // means the site to contain, not what it managed to write — so a page whose render throws
        // keeps the copy already on disk instead of being swept away, which would turn a reported
        // error into quiet data loss.
        ledger.Keep(indexHtmlPath);

        progress.Report($"Generating {label}...");

        var pageTitle = depth == 0 ? config.Title : PublicName(node.Name);
        var prefix = RelativePrefix(depth);

        var siteObj = BuildSiteObject(config, footerColumns);

        var navFolders = menuFolders
            .Select(f =>
            {
                var obj = new ScriptObject();
                obj.SetValue("name", PublicName(f.Name), readOnly: true);
                obj.SetValue("href", $"{prefix}{PublicName(f.Name)}/", readOnly: true);
                return (object)obj;
            })
            .ToList();

        var breadcrumbs = BuildBreadcrumbs(prefix, depth, ancestorNames, PublicName(node.Name));

        var items = node.Children
            .Where(child => !IsMenuOnly(child))
            // childAncestors is the chain this page's children live under — the same list their own
            // pages take their breadcrumbs from, so a card's title and the page it opens agree.
            .Select(child => (object)BuildCardModel(
                child, prefix, directoryRoot, childAncestors, config.CardBreadcrumbs))
            .ToList();

        // Cards for things that live deeper but asked to be reachable from the front door. They go
        // after the root's own children so the home page still opens with what the site is.
        var promoted = depth == 0 ? homePromotions : [];
        foreach (var promotion in promoted)
            items.Add(BuildCardModel(
                promotion.Item, prefix, directoryRoot, promotion.Ancestors, config.CardBreadcrumbs,
                promotion.Href));

        // Only pages that actually embed a player pull in the YouTube glue.
        var hasVideo = node.Children.Concat(promoted.Select(p => p.Item))
            .Any(c => !c.IsDirectory && c.Artifact?.Type == ArtifactType.Video);

        var ogTitle = depth == 0
            ? config.Title
            : string.Join(" > ", ancestorNames.Concat([PublicName(node.Name)]));

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
        tracker.PageDone(WriteIfChanged(indexHtmlPath, html, ledger, Encoding.UTF8));

        ReportPublicNameCollisions(node, warnings);

        foreach (var child in node.Children.Where(c => c.IsDirectory))
        {
            var childOutputDir = Path.Combine(outputDir, PublicName(child.Name));
            GeneratePage(child, childOutputDir, directoryRoot, config, menuFolders,
                depth + 1, childAncestors, templates, progress, errors, warnings, tracker,
                homePromotions, ledger, footerColumns);
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
                var change = GenerateArtifactPage(child, outputDir, directoryRoot, config, menuFolders,
                    depth + 1, childAncestors, templates, footerColumns, progress, ledger);
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
    private static Change WriteIfChanged(string path, string content, SiteLedger ledger, Encoding? encoding = null)
    {
        ledger.Keep(path);

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

    /// <summary>
    /// The line above a card's title: the folders its item sits in, "Archive › Newspapers". A card on
    /// the home page can be for something three levels down, and its own name alone doesn't say what
    /// it is. The labels and their order are the breadcrumb bar's, so the trail a card shows is the
    /// trail its page shows — with one deliberate exception, an artifact standing in for its folder,
    /// whose card stops where the folder stood rather than naming a page it replaced.
    /// </summary>
    private static string CardBreadcrumb(IList<string> ancestors) => string.Join(" › ", ancestors);

    /// <param name="ancestors">
    /// The folders between the home page and this card's item, already published names. Empty for a
    /// top-level item, whose only ancestor is Home.
    /// </param>
    /// <param name="cardBreadcrumbs">
    /// Whether an ordinary card shows its trail. A promoted card shows one regardless — see below.
    /// </param>
    /// <param name="hrefOverride">
    /// Where the card points when it isn't a sibling of the page showing it — a home page card for
    /// something further down the tree. A video keeps its empty href either way: it plays in place.
    /// </param>
    private static ScriptObject BuildCardModel(
        DirectoryTreeItem item,
        string prefix,
        string directoryRoot,
        IList<string> ancestors,
        bool cardBreadcrumbs,
        string? hrefOverride = null)
    {
        string caption, badge, badgeIcon, href, imgSrc;
        var video = item.Artifact as Video;

        if (item.IsDirectory)
        {
            caption = PublicName(item.Name);
            badge = ItemCountBadge(item);
            badgeIcon = "bi-folder-fill";
            href = $"{PublicName(item.Name)}/";
            var firstArtifactResult = FindFirstArtifactWithPreview(item);
            imgSrc = firstArtifactResult.HasValue
                ? GetPreviewSrc(firstArtifactResult.Value.Item1, directoryRoot, prefix, firstArtifactResult.Value.Item2)
                : "";
        }
        else
        {
            caption = item.Artifact?.Caption ?? item.Name;
            badge = item.Artifact != null ? TypeBadge(item.Artifact.Type) : "File";
            badgeIcon = item.Artifact != null ? TypeIcon(item.Artifact.Type) : "bi-file-earmark";
            var stem = Path.GetFileNameWithoutExtension(item.Name);
            // A video has no page of its own, so linking to one would be a dead link.
            href = video != null ? "" : $"{stem}/";
            imgSrc = item.Artifact != null ? GetPreviewSrc(item.Artifact, directoryRoot, prefix, stem) : "";
        }

        if (hrefOverride != null && video == null) href = hrefOverride;

        var obj = new ScriptObject();
        obj.SetValue("caption", caption, readOnly: true);
        // A promoted card keeps its trail whatever the setting says: the page showing it is not on
        // its item's path, so nothing else there says what the thing is. What the setting turns off
        // is the ordinary card's trail, which repeats the breadcrumb bar directly above it.
        obj.SetValue(
            "breadcrumb",
            cardBreadcrumbs || hrefOverride != null ? CardBreadcrumb(ancestors) : "",
            readOnly: true);
        obj.SetValue("badge", badge, readOnly: true);
        obj.SetValue("badge_icon", badgeIcon, readOnly: true);
        obj.SetValue("href", href, readOnly: true);
        obj.SetValue("img_src", imgSrc, readOnly: true);
        obj.SetValue("is_folder", item.IsDirectory, readOnly: true);
        obj.SetValue("is_video", video != null, readOnly: true);
        obj.SetValue("video_id", video?.VideoId ?? "", readOnly: true);
        obj.SetValue("video_start", video?.Start?.ToString() ?? "", readOnly: true);
        // A video's link out is the shortcut's own target, unless the yaml names somewhere better
        // to send people — the talk's page rather than the upload, say. Everywhere else `url` is a
        // link on the artifact's page; a video has no page, so here it lands on the card.
        var chosenUrl = video == null ? "" : LinkableUrl(item.Artifact?.Url);
        obj.SetValue(
            "video_url",
            video == null ? "" : chosenUrl.Length > 0 ? chosenUrl : video.SourceUrl ?? "",
            readOnly: true);
        // The shortcut's own address stays opt-in — the player already offers YouTube's — but a url
        // the owner typed reads as a link they want, so it falls back to itself for the text the
        // same way an artifact page does.
        obj.SetValue(
            "url_text",
            video == null ? "" : item.Artifact?.UrlText is { Length: > 0 } text ? text : chosenUrl,
            readOnly: true);
        obj.SetValue("credit", item.Artifact?.Credit ?? "", readOnly: true);
        return obj;
    }

    /// <summary>
    /// An artifact's <c>url</c>, or blank if it isn't something we are willing to put behind an
    /// anchor. Escaping keeps a value inside the attribute but says nothing about what happens when
    /// it is followed, and <c>javascript:</c> in a yaml would otherwise become a live link in the
    /// published site. A relative address has no scheme to judge and is left alone.
    /// </summary>
    private static string LinkableUrl(string? url)
    {
        if (url is not { Length: > 0 }) return "";

        var scheme = url.AsSpan(0, url.IndexOfAny([':', '/', '?', '#']) is var i && i > 0 ? i : 0);
        if (scheme.Length == 0) return url;  // relative: no scheme to object to
        if (url[scheme.Length] != ':') return url;

        return scheme.ToString().ToLowerInvariant() switch
        {
            "http" or "https" or "mailto" => url,
            _ => "",
        };
    }

    // Human-friendly label shown on cards and artifact pages in the generated site.
    private static string TypeBadge(ArtifactType type) => type switch
    {
        ArtifactType.Markdown => "Article",
        ArtifactType.Pdf => "PDF",
        _ => type.ToString(),
    };

    /// <summary>
    /// What a folder's card says in place of a type. The count is of the cards its own page will
    /// show — the same filter GeneratePage uses — so clicking through never contradicts the badge.
    /// </summary>
    private static string ItemCountBadge(DirectoryTreeItem folder)
    {
        var count = folder.Children.Count(c => !IsMenuOnly(c));
        return count == 1 ? "1 item" : $"{count} items";
    }

    // Bootstrap Icons class paired with the label above. Never user-supplied, so templates emit it
    // unescaped.
    private static string TypeIcon(ArtifactType type) => type switch
    {
        ArtifactType.Video     => "bi-play-btn-fill",
        ArtifactType.Pdf       => "bi-file-earmark-pdf-fill",
        ArtifactType.Photo     => "bi-image",
        ArtifactType.Deepzoom  => "bi-zoom-in",
        ArtifactType.Markdown  => "bi-file-text",
        ArtifactType.Directory => "bi-folder-fill",
        _ => "bi-file-earmark",
    };

    private static (Artifact, string)? FindFirstArtifactWithPreview(DirectoryTreeItem node)
    {
        // Prefer direct file children over anything in subdirectories.
        // Among direct children: an explicit cover wins, then photos/deepzooms, then alphabetical
        // by caption. The automatic order is a fallback for folders nobody has chosen a cover for.
        var direct = node.Children
            .Where(c => !c.IsDirectory && c.Artifact?.Preview != null)
            .OrderBy(c => c.Artifact!.IsParentCover ? 0 : 1)
            .ThenBy(c => c.Artifact!.Type is ArtifactType.Photo or ArtifactType.Deepzoom ? 0 : 1)
            .ThenBy(c => c.Artifact!.Caption ?? c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => (c.Artifact!, Path.GetFileNameWithoutExtension(c.Name)))
            .FirstOrDefault();

        if (direct.Item1 != null) return direct;

        // Nothing directly here to show. A grandchild marked grandparent-cover is a deliberate
        // answer to exactly this — a folder of folders, which can never have a parent-cover of its
        // own — so it is asked before falling through to "whatever turns up first below".
        var grandchild = node.Children
            .Where(c => c.IsDirectory)
            .SelectMany(sub => sub.Children
                .Where(c => !c.IsDirectory && c.Artifact?.Preview != null && c.Artifact.GrandparentCover)
                .OrderBy(c => c.Artifact!.Type is ArtifactType.Photo or ArtifactType.Deepzoom ? 0 : 1)
                .ThenBy(c => c.Artifact!.Caption ?? c.Name, StringComparer.OrdinalIgnoreCase))
            .Select(c => (c.Artifact!, Path.GetFileNameWithoutExtension(c.Name)))
            .FirstOrDefault();

        if (grandchild.Item1 != null) return grandchild;

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
        var rel = PublicRelativePath(Path.GetRelativePath(directoryRoot, artifact.RootFolder));
        var filename = StripDir2SitePrefix(artifact.Preview, stem);
        return rel == "." ? $"{prefix}{stem}/{filename}" : $"{prefix}{rel}/{stem}/{filename}";
    }

    /// <summary>
    /// Registers where a preview the yaml declares would land in the site, so it counts as wanted
    /// even on a run that couldn't produce or read it. Blank means the artifact type has no such
    /// file, which is a real answer rather than a missing one.
    /// </summary>
    private static void KeepDeclared(string? declared, string destDir, string stem, SiteLedger ledger)
    {
        if (declared is not { Length: > 0 }) return;

        var relative = StripDir2SitePrefix(declared, stem)
            .Replace('/', Path.DirectorySeparatorChar);
        if (relative.Length == 0) return;

        ledger.Keep(Path.Combine(destDir, relative));
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

    /// <summary>
    /// Folders whose name starts with '-' are navigation-only: "-About" gets its page and its menu
    /// entry, but no card in its parent's listing, and it sits after the ordinary folders in the
    /// menu. It is somewhere you go from the nav, not one of the collections the site is presenting.
    /// </summary>
    private const char MenuOnlyPrefix = '-';

    /// <summary>
    /// Doubling it takes the menu entry away too: "--Footer" gets its page and nothing else — no
    /// card, no nav. It is somewhere you only arrive from a link, which is what the footer's own
    /// pages are. The recommended arrangement is one "--Footer" folder holding all of them rather
    /// than a marked folder each.
    /// </summary>
    private const string UnlistedPrefix = "--";

    /// <summary>
    /// Folders whose name ends in '+' also get a card on the home page, on top of the one in their
    /// parent's listing: "Newspapers+" three levels down is still one click from the front door.
    /// It is the folder-shaped counterpart of an artifact's "home: true".
    /// </summary>
    private const char HomePromotedSuffix = '+';

    /// <summary>
    /// Passes on what the traverser found wrong with each artifact's yaml. It collected these while
    /// building the tree and nothing had ever read them, so a sidecar that failed to parse — or a
    /// setting spelled slightly wrong — was silent everywhere.
    /// </summary>
    private static void ReportYamlNotes(
        DirectoryTreeItem node, ConcurrentBag<string> errors, ConcurrentBag<string> warnings)
    {
        foreach (var error in node.YamlErrors) errors.Add(error);
        foreach (var warning in node.YamlWarnings) warnings.Add(warning);

        // A url we won't publish is the same shape of problem as a misspelled key: written in good
        // faith, and then nothing on the page to show for it. Saying so beats leaving the owner to
        // wonder where their link went.
        if (node.Artifact?.Url is { Length: > 0 } url && LinkableUrl(url).Length == 0)
        {
            warnings.Add(
                $"{Path.GetFileName(node.FullPath)}: url is not an address the site will link to " +
                "(http, https, mailto or somewhere within the site), so no link was published.");
        }

        foreach (var child in node.Children) ReportYamlNotes(child, errors, warnings);
    }

    /// <summary>
    /// Warns when two siblings publish to the same place. A folder's markers are stripped from its
    /// published name, so "Newspapers+" and a plain "Newspapers" beside it both become
    /// /Newspapers/; and an artifact publishes under its stem, so "Foo.jpg" lands on a sibling
    /// folder "Foo" and "Foo.pdf" lands on "Foo.jpg". Either way one silently overwrites the other
    /// — a page's worth of work disappearing with nothing said. Reporting is all this does: which
    /// one the author meant isn't ours to guess.
    /// </summary>
    private static void ReportPublicNameCollisions(DirectoryTreeItem node, ConcurrentBag<string> warnings)
    {
        // Videos are left out: they play on the page they sit in and publish nothing of their own.
        var published = node.Children
            .Where(c => c.IsDirectory || (c.Artifact != null && c.Artifact.Type != ArtifactType.Video));

        var clashes = published
            .GroupBy(
                c => c.IsDirectory ? PublicName(c.Name) : Path.GetFileNameWithoutExtension(c.Name),
                StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var clash in clashes)
        {
            var names = string.Join(", ", clash.Select(c => c.Name));
            warnings.Add($"{names}: these all publish as \"{clash.Key}/\" — only one of them will survive.");
        }
    }

    // A bare "--" is excluded for the same reason a bare "-" is: a marker needs a name after it,
    // and without this the folder called "--" would be treated as menu-only while PublicName —
    // which leaves it alone — kept it addressed at "--".
    private static bool IsMenuOnly(DirectoryTreeItem item) =>
        item.IsDirectory && item.Name.Length > 1 && item.Name[0] == MenuOnlyPrefix
        && item.Name != UnlistedPrefix;

    /// <summary>
    /// Kept out of the menu as well as the cards. A superset of <see cref="IsMenuOnly"/>, which
    /// already answers true for these — so the card filter needs no change for them.
    /// </summary>
    private static bool IsUnlisted(DirectoryTreeItem item) =>
        item.IsDirectory && item.Name.Length > 2 && item.Name.StartsWith(UnlistedPrefix, StringComparison.Ordinal);

    private static bool IsHomePromoted(DirectoryTreeItem item) =>
        item.IsDirectory && item.Name.Length > 1 && item.Name[^1] == HomePromotedSuffix;

    /// <summary>
    /// The markers instruct the generator; they are not part of the name. None appears in a menu
    /// label, page title, breadcrumb or URL — "-About" is published as "About", "--Footer" as
    /// "Footer", "Newspapers+" as "Newspapers". They are independent, so "-Newspapers+" is both and
    /// is published as "Newspapers". A folder named only "-", "--" or "+" is a name, not a marker,
    /// and is left alone.
    /// </summary>
    /// <remarks>
    /// The double marker has to be tested first: stripping one '-' from "--Footer" would publish it
    /// at "-Footer", leaving the second dash in every URL and breadcrumb.
    /// </remarks>
    private static string PublicName(string name)
    {
        if (name.StartsWith(UnlistedPrefix, StringComparison.Ordinal))
        {
            // A bare "--" is a name, so it keeps both dashes; stripping one would publish it at "-"
            // and quietly claim the single-dash marker on the author's behalf.
            if (name.Length > 2) name = name[2..];
        }
        else if (name.Length > 1 && name[0] == MenuOnlyPrefix)
        {
            name = name[1..];
        }

        if (name.Length > 1 && name[^1] == HomePromotedSuffix) name = name[..^1];
        return name;
    }

    /// <summary>
    /// Where a source path's content is published, with the marker stripped from every segment.
    /// Everything that turns a source path into a site path goes through here, so a page, its
    /// previews and its og:image can't disagree about where they live.
    /// </summary>
    private static string PublicRelativePath(string relativePath)
    {
        if (relativePath == "." || relativePath.Length == 0) return relativePath;
        var parts = relativePath.Split('/', '\\');
        for (var i = 0; i < parts.Length; i++) parts[i] = PublicName(parts[i]);
        return string.Join('/', parts);
    }

    /// <summary>
    /// The single artifact a folder exists to show, or null when the folder is a collection.
    ///
    /// A folder holding one article and nothing else has no collection to present — a page whose
    /// only content is one card pointing at the thing you already asked for. That folder publishes
    /// the artifact as its own index instead, so clicking "About" in the menu lands on the article.
    /// </summary>
    /// <remarks>
    /// A lone video is not promoted: videos play inline and have no page of their own, so there
    /// would be nothing to put at the folder's index. A lone sub-folder is not followed either —
    /// collapsing chains of folders gets surprising quickly.
    /// </remarks>
    private static DirectoryTreeItem? SoleArtifact(DirectoryTreeItem node)
    {
        if (node.Children.Count != 1) return null;

        var only = node.Children[0];
        if (only.IsDirectory || only.Artifact == null) return null;
        return only.Artifact.Type == ArtifactType.Video ? null : only;
    }

    /// <summary>
    /// Something from deeper in the tree that the home page also shows, and the root-relative href
    /// that reaches it. The href has to be carried alongside because a card's own href is written
    /// for a sibling — "Japan/" means something different on the home page than it does in Trips.
    /// The folders it sits under come along for the same reason: the home page is not on its path,
    /// so it is the walk down here, not the page, that knows where the thing lives.
    /// </summary>
    private sealed record HomePromotion(DirectoryTreeItem Item, string Href, IList<string> Ancestors);

    /// <summary>
    /// Everything below the root that asked to appear on the home page: folders marked with the
    /// '+' suffix and artifacts marked "home: true". Depth-0 children are skipped — they are the
    /// home page already — and the tree's own order is kept, so the extra cards read like the site.
    /// </summary>
    private static List<HomePromotion> CollectHomePromotions(DirectoryTreeItem root, string directoryRoot)
    {
        var promoted = new List<HomePromotion>();
        // The root's own children are the home page already; only what lies under them can be
        // promoted onto it, so the walk starts inside each of them rather than at them.
        foreach (var child in root.Children.Where(c => c.IsDirectory))
            CollectHomePromotions(child, [], directoryRoot, promoted);
        return promoted;
    }

    /// <param name="ancestors">
    /// The published names of the folders above <paramref name="node"/>, home downwards, not
    /// counting <paramref name="node"/> itself — what the breadcrumb bar on its page shows.
    /// </param>
    private static void CollectHomePromotions(
        DirectoryTreeItem node, IList<string> ancestors, string directoryRoot, List<HomePromotion> promoted)
    {
        // A folder published as its single artifact has no page beneath it, so a promoted artifact
        // there is reached at the folder's own address.
        var sole = SoleArtifact(node);
        IList<string> childAncestors = [.. ancestors, PublicName(node.Name)];

        foreach (var child in node.Children)
        {
            if (child.IsDirectory)
            {
                if (IsHomePromoted(child))
                    promoted.Add(new HomePromotion(child, FolderHref(child, directoryRoot), childAncestors));
                CollectHomePromotions(child, childAncestors, directoryRoot, promoted);
            }
            else if (child.Artifact?.Home == true)
            {
                // Standing in for its folder, it stands where the folder stood: one level up.
                var atFolderIndex = ReferenceEquals(child, sole);
                var href = atFolderIndex
                    ? FolderHref(node, directoryRoot)
                    : ArtifactHref(child, directoryRoot);
                promoted.Add(new HomePromotion(child, href, atFolderIndex ? ancestors : childAncestors));
            }
        }
    }

    /// <summary>One row of the footer, resolved to something a template can print.</summary>
    /// <param name="Absolute">
    /// Whether <paramref name="Href"/> stands on its own. Everything else is root-relative and gets
    /// the page's own <c>prefix</c>, which differs by depth — so the distinction has to survive to
    /// the template rather than being baked in here.
    /// </param>
    /// <param name="NewTab">
    /// Separate from <paramref name="Absolute"/> because a mailto: is also absolute but hands off to
    /// a mail client — opening a tab for it just leaves an empty one behind.
    /// </param>
    private sealed record FooterLink(
        string Href, bool Absolute, bool NewTab,
        string Icon, string IconColor, string IconBackground,
        string Title, string Note);

    /// <summary>The most columns a footer can have — past this the row stops reading as columns.</summary>
    private const int MaxFooterColumns = 4;

    private static readonly Regex IconNamePattern = new(@"^bi-[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    /// <summary>
    /// The house colors of the brand glyphs, and the fill their cut-out wants.
    /// </summary>
    /// <remarks>
    /// A brand mark has one right answer and everybody knows what it is, so <c>icon: bi-youtube</c>
    /// on its own produces it rather than a monochrome badge with the footer showing through the
    /// play triangle — which is the one result nobody wants and the easiest to get by accident.
    /// Setting either color on the row turns this off completely, so an author who wants the mark
    /// to match the rest of the column says so and gets exactly that.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, (string Color, string Background)> BrandColors =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            { "bi-youtube",   ("#ff0000", "#ffffff") },
            { "bi-facebook",  ("#1877f2", "#ffffff") },
            { "bi-instagram", ("#e4405f", "#ffffff") },
            { "bi-linkedin",  ("#0a66c2", "#ffffff") },
            { "bi-twitter",   ("#1da1f2", "#ffffff") },
            { "bi-twitter-x", ("#000000", "#ffffff") },
            { "bi-github",    ("#181717", "#ffffff") },
            { "bi-mastodon",  ("#6364ff", "#ffffff") },
            { "bi-tiktok",    ("#000000", "#ffffff") },
            { "bi-whatsapp",  ("#25d366", "#ffffff") },
            { "bi-telegram",  ("#26a5e4", "#ffffff") },
            { "bi-pinterest", ("#bd081c", "#ffffff") },
            { "bi-reddit",    ("#ff4500", "#ffffff") },
            { "bi-vimeo",     ("#1ab7ea", "#ffffff") },
            { "bi-twitch",    ("#9146ff", "#ffffff") },
            { "bi-discord",   ("#5865f2", "#ffffff") },
            { "bi-spotify",   ("#1db954", "#ffffff") },
            { "bi-threads",   ("#000000", "#ffffff") },
            { "bi-bluesky",   ("#0285ff", "#ffffff") },
            { "bi-slack",     ("#4a154b", "#ffffff") },
            { "bi-medium",    ("#000000", "#ffffff") },
        };
    // The lengths CSS actually has: #rgb, #rgba, #rrggbb, #rrggbbaa. A flat 3-to-8 range also let
    // through #12345, which is not a color at all and which IsDarkColor could not read either.
    private static readonly Regex HexColorPattern =
        new(@"^#([0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);

    /// <summary>
    /// The configured footer rows, grouped into columns and resolved to hrefs. Empty columns close
    /// up, so numbering the columns 1 and 3 gives two columns rather than a gap.
    /// </summary>
    private static List<List<FooterLink>> BuildFooterColumns(
        Dir2SiteModel config, string directoryRoot, DirectoryTreeItem root, ConcurrentBag<string> warnings)
    {
        if (config.FooterItems.Count == 0) return [];

        var targets = BuildLinkTargets(root, directoryRoot);
        var columns = new SortedDictionary<int, List<FooterLink>>();

        foreach (var item in config.FooterItems)
        {
            var title = item.Title ?? string.Empty;
            var (href, absolute, newTab) = ResolveFooterLink(item, title, targets, warnings);

            // A row whose link doesn't resolve still appears, as plain text. Dropping it made a
            // typo look like the row had never been written, which is the hardest kind of mistake
            // to find — the warning says what went wrong, and the gap in the footer says where.
            href ??= string.Empty;

            var column = Math.Clamp(item.Column, 1, MaxFooterColumns);
            if (column != item.Column)
                warnings.Add($"Footer item \"{title}\" asks for column {item.Column}, which isn't between 1 and {MaxFooterColumns}, so it went in column {column}.");

            if (!columns.TryGetValue(column, out var rows)) columns[column] = rows = [];

            var icon = SanitizeIcon(item.Icon, title, warnings);
            var color = SanitizeColor(item.IconColor, "iconColor", title, warnings);
            var background = SanitizeColor(item.IconBackground, "iconBackground", title, warnings);

            // Only when the row says nothing about color at all: naming either one is the author
            // taking charge of how the mark looks, and half a brand's colors is nobody's intent.
            if (color.Length == 0 && background.Length == 0 && BrandColors.TryGetValue(icon, out var brand))
                (color, background) = brand;

            rows.Add(new FooterLink(
                href, absolute, newTab,
                icon, color, background,
                title,
                item.Note ?? string.Empty));
        }

        return [.. columns.Values];
    }

    /// <summary>
    /// Where a footer row points, and whether that href stands alone. Null href means the row can't
    /// be published and has already been warned about.
    /// </summary>
    private static (string? Href, bool Absolute, bool NewTab) ResolveFooterLink(
        FooterItem item, string title, IReadOnlyDictionary<string, string?> targets, ConcurrentBag<string> warnings)
    {
        var link = (item.Link ?? string.Empty).Trim();
        if (link.Length == 0)
        {
            warnings.Add($"Footer item \"{title}\" has no link, so it is shown without a link.");
            return (null, false, false);
        }

        // Off-site: taken as written. The site knows nothing about where it goes.
        if (link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            link.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return (link, true, true);

        // Also absolute, but it hands off to a mail client rather than loading a page.
        if (link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return (link, true, false);

        // A page dir2site didn't generate. Root-relative, so it still takes the page's prefix.
        if (link[0] == '/') return (link.TrimStart('/'), false, false);

        var key = link.Replace('\\', '/').TrimStart('.', '/');
        if (!targets.TryGetValue(key, out var href))
        {
            warnings.Add($"Footer item \"{title}\" points at {link}, which isn't in the project, so it is shown without a link.");
            return (null, false, false);
        }

        if (href == null)
        {
            warnings.Add($"Footer item \"{title}\" points at {link}, which is a video and has no page of its own, so it is shown without a link.");
            return (null, false, false);
        }

        return (href, false, false);
    }

    /// <summary>
    /// Every project path that can be linked to, mapped to where it publishes. Built by the same
    /// rules the pages themselves are — a folder shown as its single artifact resolves to the
    /// folder's own address — so a footer link and the page it names can't disagree.
    /// </summary>
    private static Dictionary<string, string?> BuildLinkTargets(DirectoryTreeItem root, string directoryRoot)
    {
        // Case-insensitive because a project path typed by hand is not a filesystem lookup, and the
        // filesystems this runs on mostly aren't either.
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Walk(root, isRoot: true);
        return map;

        void Walk(DirectoryTreeItem node, bool isRoot)
        {
            // The site root always keeps its own home page, so its children are never promoted to it.
            var sole = isRoot ? null : SoleArtifact(node);

            foreach (var child in node.Children)
            {
                var key = Path.GetRelativePath(directoryRoot, child.FullPath).Replace('\\', '/');

                if (child.IsDirectory)
                {
                    map[key] = FolderHref(child, directoryRoot);
                    Walk(child, isRoot: false);
                }
                else if (child.Artifact != null)
                {
                    // A video plays inline on its collection page and has nowhere to link to.
                    map[key] = child.Artifact.Type == ArtifactType.Video
                        ? null
                        : ReferenceEquals(child, sole)
                            ? FolderHref(node, directoryRoot)
                            : ArtifactHref(child, directoryRoot);
                }
            }
        }
    }

    /// <summary>
    /// A Bootstrap Icons class name, or empty. Every other icon class in the site comes from
    /// <see cref="TypeIcon"/> and is emitted unescaped on that basis; this one is written by hand in
    /// yaml and lands in a class attribute, so it has to be checked rather than trusted.
    /// </summary>
    private static string SanitizeIcon(string? icon, string title, ConcurrentBag<string> warnings)
    {
        var name = (icon ?? string.Empty).Trim();
        if (name.Length == 0) return string.Empty;

        // "envelope" and "bi-envelope" both mean the same thing to whoever wrote it.
        if (!name.StartsWith("bi-", StringComparison.Ordinal)) name = "bi-" + name;
        if (IconNamePattern.IsMatch(name)) return name;

        warnings.Add($"Footer item \"{title}\" has an icon of \"{icon}\", which is not a Bootstrap Icons name, so it was left off.");
        return string.Empty;
    }

    /// <summary>A hex color, or empty. Stricter than the icon check because it lands in a style attribute.</summary>
    private static string SanitizeColor(string? color, string setting, string title, ConcurrentBag<string> warnings)
    {
        var value = (color ?? string.Empty).Trim();
        if (value.Length == 0) return string.Empty;
        if (HexColorPattern.IsMatch(value)) return value;

        warnings.Add($"Footer item \"{title}\" has a {setting} of \"{color}\", which is not a hex color like #ff0000, so it was left off.");
        return string.Empty;
    }

    /// <summary>
    /// Whether the footer band needs light text on it. The navbar settles this with an explicit
    /// setting; the footer takes a color instead, so it has to work the answer out.
    /// </summary>
    private static bool IsDarkColor(string hex)
    {
        var value = hex.TrimStart('#');
        // Shorthand doubles each digit; the alpha one expands to eight, whose first six are the
        // color. Opacity doesn't change which way the text has to go, so it is ignored either way.
        if (value.Length is 3 or 4)
            value = string.Concat(value.Select(c => new string(c, 2)));
        if (value.Length < 6 ||
            !int.TryParse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !int.TryParse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !int.TryParse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return true;   // The default primary color is dark, so dark is the safer guess.

        // Rec. 601 luma — enough to answer "does this want white text on it".
        return (0.299 * r) + (0.587 * g) + (0.114 * b) < 140;
    }

    /// <summary>
    /// The <c>site</c> object every template gets. One builder because a page and an artifact page
    /// have to agree about what the site is; they used to hold two copies of this list.
    /// </summary>
    private static ScriptObject BuildSiteObject(Dir2SiteModel config, List<List<FooterLink>> footerColumns)
    {
        var footerColor = string.IsNullOrWhiteSpace(config.FooterColor)
            ? config.PrimaryColor
            : config.FooterColor;

        var siteObj = new ScriptObject();
        siteObj.SetValue("title", config.Title, readOnly: true);
        siteObj.SetValue("footer", config.Footer, readOnly: true);
        siteObj.SetValue("logo", config.Logo, readOnly: true);
        siteObj.SetValue("primary_color", config.PrimaryColor, readOnly: true);
        siteObj.SetValue("secondary_color", config.SecondaryColor, readOnly: true);
        siteObj.SetValue("background_color", config.BackgroundColor, readOnly: true);
        siteObj.SetValue("footer_color", footerColor, readOnly: true);
        siteObj.SetValue("footer_dark", IsDarkColor(footerColor), readOnly: true);
        siteObj.SetValue("navbar_dark", config.NavbarDark, readOnly: true);
        siteObj.SetValue("url", config.SiteUrl.TrimEnd('/'), readOnly: true);
        siteObj.SetValue("footer_columns", ToScriptColumns(footerColumns), readOnly: true);
        return siteObj;
    }

    private static List<object> ToScriptColumns(List<List<FooterLink>> columns) =>
        [.. columns.Select(column => (object)column.Select(link =>
        {
            var obj = new ScriptObject();
            obj.SetValue("href", link.Href, readOnly: true);
            obj.SetValue("absolute", link.Absolute, readOnly: true);
            obj.SetValue("new_tab", link.NewTab, readOnly: true);
            obj.SetValue("icon", link.Icon, readOnly: true);
            obj.SetValue("icon_color", link.IconColor, readOnly: true);
            obj.SetValue("icon_background", link.IconBackground, readOnly: true);
            obj.SetValue("title", link.Title, readOnly: true);
            obj.SetValue("note", link.Note, readOnly: true);
            return (object)obj;
        }).ToList())];

    private static string FolderHref(DirectoryTreeItem folder, string directoryRoot) =>
        $"{PublicRelativePath(Path.GetRelativePath(directoryRoot, folder.FullPath))}/";

    private static string ArtifactHref(DirectoryTreeItem file, string directoryRoot)
    {
        var dir = PublicRelativePath(
            Path.GetRelativePath(directoryRoot, Path.GetDirectoryName(file.FullPath) ?? directoryRoot));
        var stem = Path.GetFileNameWithoutExtension(file.Name);
        return dir == "." ? $"{stem}/" : $"{dir}/{stem}/";
    }

    private static string RelativePrefix(int depth) =>
        string.Concat(Enumerable.Repeat("../", depth));

    /// <summary>One file to place in _site. Collected up front so the copy stage knows its total.</summary>
    private sealed record CopyJob(string Src, string Dest, string Label);

    /// <summary>
    /// Every path this run intends <c>_site</c> to contain. Recorded inside the three primitives
    /// that write there — <see cref="WriteIfChanged"/>, <see cref="CopyFileIfDifferent"/> and
    /// <see cref="CopyEmbeddedIfStale"/> — before each one's "already current, nothing to do"
    /// early return, because a file left alone for being up to date is still a file the site
    /// wants. Registering at the call sites instead would put the safety of a destructive pass in
    /// the hands of whoever adds the next call.
    /// </summary>
    private sealed class SiteLedger(string siteRoot)
    {
        // Case-insensitively, always, on every platform. The two mistakes aren't symmetric: on a
        // case-folding filesystem an overwritten file keeps its original spelling, so comparing
        // exactly would put live pages on the orphan list. Comparing loosely on a case-sensitive
        // one only means a rename that changes nothing but case leaves its old copy behind, which
        // is what generating did with everything until now. Err towards keeping.
        private readonly ConcurrentDictionary<string, byte> _kept = new(StringComparer.OrdinalIgnoreCase);

        public string Root { get; } = Path.GetFullPath(siteRoot);

        public void Keep(string path) => _kept[Path.GetFullPath(path)] = 0;

        public bool Contains(string fullPath) => _kept.ContainsKey(fullPath);

        /// <summary>
        /// Set when a source folder could not be read, so this run never learned what was in it.
        /// </summary>
        /// <remarks>
        /// The ledger's meaning is "everything the site should contain", and the sweep reads
        /// anything missing from it as deletable. A folder that couldn't be listed contributes
        /// nothing, which is indistinguishable from a folder the user emptied — so an unreadable
        /// <c>_media</c> would have offered every static include in the site for deletion, and the
        /// deploy would then have taken them off the server as stale. A cloud-synced project with
        /// dehydrated files, a network share that blinks, or a scanner holding a handle is enough.
        /// One unreadable directory therefore takes the whole report off the table: the sweep can
        /// only speak for a run that saw everything.
        /// </remarks>
        public bool IsIncomplete { get; private set; }

        public void MarkIncomplete() => IsIncomplete = true;
    }

    /// <summary>
    /// Files sitting in <c>_site</c> that this run never asked for — what a deleted or renamed
    /// source leaves behind. Returned as site-relative paths with forward slashes, sorted, ready
    /// to show someone.
    /// </summary>
    /// <remarks>
    /// Deliberately without a "that's too many, something must have gone wrong" cut-off: the site
    /// is regenerable from the source folder, nothing here was put in <c>_site</c> by anyone but
    /// the generator, and the user confirms before any of it goes. A threshold would eventually
    /// refuse a perfectly real "I deleted most of my site" run.
    /// </remarks>
    private static IReadOnlyList<string> FindOrphans(SiteLedger ledger)
    {
        List<string> files;
        try { files = [.. Directory.EnumerateFiles(ledger.Root, "*", SearchOption.AllDirectories)]; }
        catch { return []; }

        var orphans = new List<string>();
        foreach (var file in files)
        {
            if (ledger.Contains(Path.GetFullPath(file))) continue;
            var rel = Path.GetRelativePath(ledger.Root, file);
            if (IsProtected(rel)) continue;
            orphans.Add(rel.Replace(Path.DirectorySeparatorChar, '/'));
        }

        orphans.Sort(StringComparer.Ordinal);
        return orphans;
    }

    /// <summary>
    /// Whether a site-relative path is something dir2site should never take away. The generator
    /// writes no dot-entries into <c>_site</c>, so anything with a dot-segment got there from a
    /// person or a server — a hand-placed <c>.htaccess</c>, a <c>.well-known/</c> challenge — and
    /// dir2site doesn't delete what it didn't create. The deploy already applies this same rule to
    /// the far end, in <c>SyncManifestBuilder.MayBeDeleted</c>, and the two halves agree on
    /// purpose: a protected <c>.htaccess</c> stays in the local manifest, so it is never offered
    /// up as stale on the server either.
    /// </summary>
    private static bool IsProtected(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.StartsWith('.'));

    /// <summary>
    /// Deletes files reported by a previous <see cref="Generate"/>, then any directory their going
    /// left empty. Takes site-relative paths — the same ones <c>Generate</c> handed out.
    /// </summary>
    /// <returns>How many files went, and anything that refused to.</returns>
    public static (int Removed, IReadOnlyList<string> Errors) RemoveOrphans(
        string siteRoot, IReadOnlyList<string> relativePaths, IProgress<string>? progress = null)
    {
        var root = Path.GetFullPath(siteRoot);
        var removed = 0;
        var done = 0;
        var errors = new List<string>();

        // One report per file would post tens of thousands of updates to the UI thread, which is
        // its own kind of freeze; a couple of hundred is more than a status line can show.
        var step = Math.Max(1, relativePaths.Count / 200);

        foreach (var rel in relativePaths)
        {
            // Belt and braces: these come back from Generate, which has already filtered both of
            // these out. But this is a public entry point and the one thing it must never do is
            // delete outside the site — and a refusal says so rather than reporting "removed 0"
            // and offering the same file again next time with no explanation.
            //
            // Containment first: "..", being a dot-segment, would otherwise be turned away as a
            // dot-file, which is true but not the thing worth saying about a path escaping _site.
            var full = Path.GetFullPath(Path.Combine(root, rel));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{rel}: not removed — it resolves outside _site.");
                continue;
            }

            if (IsProtected(rel))
            {
                errors.Add($"{rel}: not removed — dir2site doesn't delete dot-files it didn't create.");
                continue;
            }

            try
            {
                File.Delete(full);
                removed++;
            }
            catch (Exception ex)
            {
                // One locked file — the preview server holding it open on Windows, say — shouldn't
                // stop the rest going. It gets reported, and offered again next generate.
                errors.Add($"{rel}: {ex.Message}");
            }

            if (++done % step == 0 || done == relativePaths.Count)
                progress?.Report($"Removing files… ({done}/{relativePaths.Count})");
        }

        progress?.Report("Tidying up empty folders…");
        RemoveEmptyDirectories(root, root);
        return (removed, errors);
    }

    // Depth-first so a folder emptied only by its children going still gets swept — this is what
    // takes _site/Photographs/1890s/ away once the pages inside it are gone. The site root itself
    // stays. Failures are ignored: a directory that won't go is harmless, and one holding nothing
    // but a protected dot-file is correctly not empty.
    private static void RemoveEmptyDirectories(string dir, string root)
    {
        IEnumerable<string> children;
        try { children = [.. Directory.EnumerateDirectories(dir)]; }
        catch { return; }

        foreach (var child in children)
            RemoveEmptyDirectories(child, root);

        if (string.Equals(dir, root, StringComparison.Ordinal)) return;

        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { /* benign */ }
    }

    // Walks the tree one directory at a time. Each artifact's previews live in .dir2site/{stem}/
    // so they are self-contained — copy the whole subfolder straight into the artifact's output dir.
    private static void CollectFolderPreviewCopyJobs(
        DirectoryTreeItem node, string directoryRoot, string siteRoot, List<CopyJob> jobs, SiteLedger ledger)
    {
        var folderRel = Path.GetRelativePath(directoryRoot, node.FullPath);

        foreach (var child in node.Children.Where(c => !c.IsDirectory && c.Artifact != null))
        {
            var stem = Path.GetFileNameWithoutExtension(child.Name);

            var destDir = folderRel == "."
                ? Path.Combine(siteRoot, stem)
                : Path.Combine(siteRoot, PublicRelativePath(folderRel), stem);

            // `publishOriginal: true` puts the source PDF itself in the site, next to the page
            // images, so the artifact page can offer it for download. It is the source file rather
            // than a generated one, so it doesn't depend on previews having been generated.
            // Turning the flag back off takes the PDF down again by simply not asking for it: an
            // already-published copy then belongs to no one and the sweep offers it up.
            if (child.Artifact is Pdf { PublishOriginal: true })
                jobs.Add(new CopyJob(child.FullPath, Path.Combine(destDir, child.Name), child.Name));

            // What the yaml says this artifact has, registered whether or not the file is there to
            // copy right now. A preview that failed to regenerate — or a previews folder that has
            // gone missing — is a failure, not a deletion, and the copy already in the site should
            // survive it rather than be offered up while the yaml still claims it. This is what
            // tells "these previews have gone" apart from "this artifact never had any": a video
            // declares none, so it is unaffected, and an artifact whose yaml no longer names a
            // preview still has the old one swept.


            KeepDeclared(child.Artifact!.Preview, destDir, stem, ledger);
            KeepDeclared(child.Artifact.PreviewLarge, destDir, stem, ledger);
            if (child.Artifact is Photo photo) KeepDeclared(photo.Image, destDir, stem, ledger);

            var stemDir = Path.Combine(node.FullPath, ".dir2site", stem);
            // No previews folder at all is a real answer — a video has none — so it isn't a gap in
            // itself. One that exists but won't be read is, and is marked below.
            if (!Directory.Exists(stemDir)) continue;

            try
            {
                foreach (var file in SourceListing.FilesRecursive(stemDir))
                {
                    var fileRel = Path.GetRelativePath(stemDir, file);
                    jobs.Add(new CopyJob(file, Path.Combine(destDir, fileRel), fileRel));
                }
            }
            catch { ledger.MarkIncomplete(); }
        }

        foreach (var child in node.Children.Where(c => c.IsDirectory))
            CollectFolderPreviewCopyJobs(child, directoryRoot, siteRoot, jobs, ledger);
    }

    // Copies every "_"-prefixed folder (e.g. _media) verbatim into _site at its relative path, so
    // markdown articles can reference static includes. _site itself and "."-prefixed folders
    // (.git, .dir2site, …) are skipped. Underscore folders are not scanned as artifacts.
    private static void CollectUnderscoreFolderCopyJobs(
        string current, string directoryRoot, string siteRoot, List<CopyJob> jobs, SiteLedger ledger)
    {
        List<string> dirs;
        try { dirs = SourceListing.Directories(current); }
        catch { ledger.MarkIncomplete(); return; }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.')) continue;          // .git, .dir2site, …
            if (name.Equals("_site", StringComparison.OrdinalIgnoreCase)) continue;

            if (name.StartsWith('_'))
            {
                var rel = Path.GetRelativePath(directoryRoot, dir);
                var destRoot = Path.Combine(siteRoot, PublicRelativePath(rel));
                // Enumerating can fail part-way through, which would leave the rest of the folder
                // unregistered and so up for deletion — the jobs already added are kept, but the
                // run no longer knows the whole picture.
                try
                {
                    foreach (var file in SourceListing.FilesRecursive(dir))
                    {
                        var fileRel = Path.GetRelativePath(dir, file);
                        jobs.Add(new CopyJob(file, Path.Combine(destRoot, fileRel), Path.Combine(rel, fileRel)));
                    }
                }
                catch { ledger.MarkIncomplete(); }
                continue; // don't descend further — the whole subtree was copied
            }

            CollectUnderscoreFolderCopyJobs(dir, directoryRoot, siteRoot, jobs, ledger);
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
        IList<DirectoryTreeItem> menuFolders,
        int depth,
        IList<string> ancestorNames,
        TemplateSet templates,
        List<List<FooterLink>> footerColumns,
        IProgress<string>? progress,
        SiteLedger ledger,
        bool atFolderIndex = false)
    {
        var artifact = item.Artifact!;
        var stem = Path.GetFileNameWithoutExtension(item.Name);

        // Normally the page goes in a folder of its own, one level below its source. When it is
        // the only thing in its folder it takes the folder's own index, level with the source —
        // which is what the depth-dependent paths below have to account for.
        var outputDir = atFolderIndex ? parentOutputDir : Path.Combine(parentOutputDir, stem);
        Directory.CreateDirectory(outputDir);

        var indexHtmlPath = Path.Combine(outputDir, "index.html");

        // Before the render, not after: a page whose render throws is caught by the caller and
        // reported, and the copy already in the site should survive that rather than be swept.
        ledger.Keep(indexHtmlPath);

        progress?.Report($"Generating {stem}/index.html...");

        var prefix = RelativePrefix(depth);

        var siteObj = BuildSiteObject(config, footerColumns);

        var navFolders = menuFolders
            .Select(f =>
            {
                var obj = new ScriptObject();
                obj.SetValue("name", PublicName(f.Name), readOnly: true);
                obj.SetValue("href", $"{prefix}{PublicName(f.Name)}/", readOnly: true);
                return (object)obj;
            })
            .ToList();

        var breadcrumbs = BuildBreadcrumbs(prefix, depth, ancestorNames, artifact.Caption ?? stem);

        var caption = artifact.Caption ?? stem;
        var previewSrc = GetPreviewSrc(artifact, directoryRoot, prefix, stem);

        // An artifact's generated assets are copied to {folder}/{stem}/ and are normally addressed
        // by bare filename, because the page sits in that same directory. Published at the folder's
        // index the page is one level up, so they need the segment back. (preview_src is built from
        // the site root and is already right either way.)
        var assetPrefix = atFolderIndex ? $"{stem}/" : "";
        var previewLargeSrc = WithAssetPrefix(assetPrefix, GetPreviewLargeSrc(artifact, stem));

        var artifactObj = new ScriptObject();
        artifactObj.SetValue("caption", caption, readOnly: true);
        artifactObj.SetValue("credit", artifact.Credit ?? "", readOnly: true);
        artifactObj.SetValue("date", artifact.Date ?? "", readOnly: true);

        // Blank link text falls back to the address itself, so a url the site owner typed is never
        // silently dropped. Both keys are always set: the artifact templates share this object and
        // Scriban reads members per template, so a missing one is an error rather than a blank.
        var url = LinkableUrl(artifact.Url);
        artifactObj.SetValue("url", url, readOnly: true);
        artifactObj.SetValue(
            "url_text",
            url.Length == 0 ? "" : artifact.UrlText is { Length: > 0 } text ? text : url,
            readOnly: true);

        artifactObj.SetValue("badge", TypeBadge(artifact.Type), readOnly: true);
        artifactObj.SetValue("badge_icon", TypeIcon(artifact.Type), readOnly: true);
        artifactObj.SetValue("preview_src", previewSrc, readOnly: true);

        string templateName;
        switch (artifact.Type)
        {
            case ArtifactType.Photo:
            case ArtifactType.Deepzoom:
                // Prefer the full-res WebP; fall back to large preview if image not yet generated
                var osdSrc = WithAssetPrefix(assetPrefix, GetImageSrc(artifact, stem));
                if (string.IsNullOrEmpty(osdSrc))
                    osdSrc = previewLargeSrc;
                artifactObj.SetValue("image_src", osdSrc, readOnly: true);
                templateName = "artifact-photo";
                break;

            case ArtifactType.Pdf:
                artifactObj.SetValue("author", (artifact as Document)?.Author ?? "", readOnly: true);
                artifactObj.SetValue(
                    "bookreader_data", BuildBookReaderData(artifact, stem, assetPrefix), readOnly: true);
                // Empty unless the source PDF was published alongside the page images, so the
                // template can't offer a download of a file that isn't there.
                artifactObj.SetValue(
                    "original_src",
                    artifact is Pdf { PublishOriginal: true }
                        ? WithAssetPrefix(assetPrefix, Uri.EscapeDataString(item.Name))
                        : "",
                    readOnly: true);
                templateName = "artifact-pdf";
                break;

            case ArtifactType.Markdown:
                artifactObj.SetValue(
                    "html_content",
                    MarkdownRenderer.FileToHtml(item.FullPath, pageIsNested: !atFolderIndex),
                    readOnly: true);
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
        return WriteIfChanged(indexHtmlPath, html, ledger, Encoding.UTF8);
    }

    private static string GetOgImageRootRelative(Artifact artifact, string directoryRoot, string stem)
    {
        var src = artifact.PreviewLarge ?? artifact.Preview;
        if (src == null || artifact.RootFolder == null) return "";
        var rel = PublicRelativePath(Path.GetRelativePath(directoryRoot, artifact.RootFolder));
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

    private static string WithAssetPrefix(string assetPrefix, string src) =>
        assetPrefix.Length == 0 || src.Length == 0 ? src : assetPrefix + src;

    private static string BuildBookReaderData(Artifact artifact, string stem, string assetPrefix)
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

            // Page images are addressed relative to the page, so they only need adjusting when the
            // page has moved up to the folder's index.
            if (assetPrefix.Length > 0)
            {
                foreach (var spread in dataArray)
                {
                    if (spread is not JsonArray pages) continue;
                    foreach (var page in pages)
                    {
                        if (page?["uri"] is JsonValue uriVal)
                            page["uri"] = assetPrefix + uriVal.GetValue<string>();
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

    private static void CopyOpenSeaDragonAssets(string siteRoot, SiteLedger ledger, IProgress<string>? progress)
    {
        const string baseUri = "avares://dir2site/Assets/js/openseadragon-bin-6.0.2/";
        var destBase = Path.Combine(siteRoot, "js", "openseadragon");

        CopyEmbeddedFile(
            $"{baseUri}openseadragon.min.js",
            Path.Combine(destBase, "openseadragon.min.js"),
            ledger, progress);
        CopyEmbeddedFile(
            $"{baseUri}openseadragon.min.js.map",
            Path.Combine(destBase, "openseadragon.min.js.map"),
            ledger, progress);

        CopyEmbeddedDirectory($"{baseUri}images/", Path.Combine(destBase, "images"), ledger, progress);
    }

    private static void CopyBookReaderAssets(string siteRoot, SiteLedger ledger, IProgress<string>? progress)
    {
        const string baseUri = "avares://dir2site/Assets/js/bookreader-5.0.0-111/BookReader/";
        var destBase = Path.Combine(siteRoot, "js", "bookreader");

        foreach (var file in new[] { "BookReader.js", "BookReader.css", "jquery-3.js" })
        {
            CopyEmbeddedFile($"{baseUri}{file}", Path.Combine(destBase, file), ledger, progress);
        }

        CopyEmbeddedDirectory($"{baseUri}images/", Path.Combine(destBase, "images"), ledger, progress);
    }

    private static void CopyEmbeddedFile(
        string avaloniaUri, string dest, SiteLedger ledger, IProgress<string>? progress) =>
        CopyEmbeddedIfStale(avaloniaUri, dest, ledger, progress);

    private static void CopyEmbeddedDirectory(
        string avaloniaBaseUri,
        string destDir,
        SiteLedger ledger,
        IProgress<string>? progress)
    {
        var baseUri = new Uri(avaloniaBaseUri.TrimEnd('/') + "/");
        var assets = AssetLoader.GetAssets(baseUri, null);
        foreach (var assetUri in assets)
        {
            var dest = Path.Combine(destDir, Path.GetFileName(assetUri.LocalPath));
            CopyEmbeddedIfStale(assetUri.ToString(), dest, ledger, progress);
        }
    }

    private static void CopySiteAssets(
        string siteRoot, Dir2SiteModel config, AvaloniaTemplateLoader loader,
        SiteLedger ledger, IProgress<string>? progress)
    {
        // The stylesheet needs the colors, not the footer's rows — but it does need the footer
        // color and whether that color is dark, so it goes through the same builder as the pages
        // rather than keeping a third hand-maintained copy of what "site" means.
        var siteObj = BuildSiteObject(config, []);

        var globals = new ScriptObject();
        globals.SetValue("site", siteObj, readOnly: true);

        var context = new TemplateContext { TemplateLoader = loader };
        context.PushGlobal(globals);

        var template = Template.Parse(loader.LoadByName("site-css"), "site-css.html");
        var css = template.Render(context);
        WriteIfChanged(Path.Combine(siteRoot, "css", "site.css"), css, ledger);

        var jsTemplate = Template.Parse(loader.LoadByName("site-js"), "site-js.html");
        var js = jsTemplate.Render(context);
        WriteIfChanged(Path.Combine(siteRoot, "js", "site.js"), js, ledger);

        // Written unconditionally, like every other asset; only pages with a video reference it.
        var videoTemplate = Template.Parse(loader.LoadByName("video-js"), "video-js.html");
        var videoJs = videoTemplate.Render(context);
        WriteIfChanged(Path.Combine(siteRoot, "js", "video.js"), videoJs, ledger);
    }

    private static void CopyBootstrapAssets(string siteRoot, SiteLedger ledger, IProgress<string>? progress)
    {
        var files = new[]
        {
            ("avares://dir2site/Assets/js/bootstrap-5.3.8-dist/css/bootstrap.min.css",
             Path.Combine(siteRoot, "js", "bootstrap", "bootstrap.min.css")),
            ("avares://dir2site/Assets/js/bootstrap-5.3.8-dist/js/bootstrap.bundle.min.js",
             Path.Combine(siteRoot, "js", "bootstrap", "bootstrap.bundle.min.js")),
        };

        foreach (var (uri, dest) in files)
            CopyEmbeddedIfStale(uri, dest, ledger, progress);
    }

    private static void CopyBootstrapIconsAssets(string siteRoot, SiteLedger ledger, IProgress<string>? progress)
    {
        const string baseUri = "avares://dir2site/Assets/icons/bootstrap-icons-1.13.1/font/";
        var destBase = Path.Combine(siteRoot, "js", "bootstrap-icons");

        // The stylesheet reaches its fonts as ./fonts/..., so the two have to keep this layout.
        CopyEmbeddedFile(
            $"{baseUri}bootstrap-icons.css",
            Path.Combine(destBase, "bootstrap-icons.css"),
            ledger, progress);

        CopyEmbeddedDirectory($"{baseUri}fonts/", Path.Combine(destBase, "fonts"), ledger, progress);
    }

    /// <returns>
    /// New when the site had no such file, Updated when it had a stale one, None when it was
    /// already current and nothing was copied.
    /// </returns>
    private static Change CopyFileIfDifferent(
        string src, string dest, SiteLedger ledger, IProgress<string>? progress, string? label = null)
    {
        ledger.Keep(dest);

        var existed = File.Exists(dest);
        if (existed && SameFile(src, dest)) return Change.None;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        progress?.Report($"Copying {label ?? Path.GetFileName(dest)}...");
        File.Copy(src, dest, overwrite: true);
        return existed ? Change.Updated : Change.New;
    }

    /// <summary>
    /// Whether the site's copy already matches the source, by size and modified time.
    /// </summary>
    /// <remarks>
    /// Deliberately "the same" rather than "not older". These copies mirror a source file —
    /// <c>_media</c> most of all — and a mirror reproduces what is there. Asking whether the source
    /// was <i>newer</i> silently kept the old copy whenever a file was replaced by one carrying an
    /// earlier timestamp: restoring from a backup, copying off another drive or a camera,
    /// <c>rsync -t</c>, checking out an older revision. The site then published the stale copy
    /// indefinitely, and because the server agreed with <c>_site</c>, Verify and Repair saw nothing
    /// wrong either. Size-and-mtime is the same test the deploy diff uses, so the two agree.
    ///
    /// <see cref="File.Copy(string,string,bool)"/> carries the source's modified time across, so a
    /// file that was copied stays equal on the next run and is left alone — which is what keeps an
    /// unchanged site off the upload queue.
    /// </remarks>
    private static bool SameFile(string src, string dest)
    {
        try
        {
            var s = new FileInfo(src);
            var d = new FileInfo(dest);
            return s.Length == d.Length && s.LastWriteTimeUtc == d.LastWriteTimeUtc;
        }
        catch
        {
            // Unreadable for any reason — copy rather than assume it's current.
            return false;
        }
    }

    private static readonly DateTime _assemblyTime = GetAssemblyTime();

    private static DateTime GetAssemblyTime()
    {
        var proc = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(proc) && File.Exists(proc))
            return File.GetLastWriteTimeUtc(proc);
        return DateTime.UtcNow;
    }

    private static void CopyEmbeddedIfStale(
        string avaloniaUri, string dest, SiteLedger ledger, IProgress<string>? progress)
    {
        ledger.Keep(dest);

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
