// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace dir2site.Services;

/// <summary>
/// Which pages a run has any reason to re-render.
/// </summary>
/// <remarks>
/// Every page is rendered on every run and written only when the bytes differ, and the reasoning is
/// sound: the menu, the site config and a folder's item set are all global, so no per-folder
/// timestamp can decide whether a page is stale. Pressed once in a while that costs nothing much.
/// Under auto-generate it is paid on every save — a five-hundred page site re-renders five hundred
/// pages, and reads five hundred files back to compare, because somebody edited one article.
///
/// What makes narrowing safe is that we are no longer guessing. The watcher says what changed, so
/// this is not staleness inferred from timestamps but a change set taken at its word. When there
/// isn't one — the app was closed, events were lost, a setting changed — <see cref="All"/> renders
/// everything, exactly as before.
///
/// Deliberately coarse. Over-rendering costs a render; under-rendering ships a page that is quietly
/// wrong, and stays wrong until something else happens to touch it.
/// </remarks>
public sealed class RenderScope
{
    // Null means no restriction: render the lot.
    private readonly IReadOnlyList<string>? _folders;

    /// <summary>
    /// Individual pages to render, where the folder around them can be left alone.
    /// </summary>
    /// <remarks>
    /// Site-relative directories, one per page — <c>Archive/P042</c>, not <c>Archive</c>. Their
    /// ancestors come along through the same rule that serves <see cref="_folders"/>, so the folder
    /// index and the pages above still get written; what is skipped is the several hundred siblings
    /// that would render byte-identical.
    /// </remarks>
    private readonly IReadOnlyList<string> _pages;

    private RenderScope(IReadOnlyList<string>? folders, IReadOnlyList<string>? pages = null)
    {
        _folders = folders;
        _pages = pages ?? [];
    }

    /// <summary>Render everything, which is what every run did before this existed.</summary>
    public static readonly RenderScope All = new(null);

    public bool IsEverything => _folders == null;

    /// <summary>
    /// What the given changes can reach, or <see cref="All"/> when that can't be usefully bounded.
    /// </summary>
    /// <remarks>
    /// The unit is the folder, not the page, and a folder's pages genuinely depend on each other —
    /// this is not caution standing in for analysis:
    ///
    /// <list type="bullet">
    /// <item><description>
    /// <c>CaptionRows</c> is computed across the whole chain, so every page in a folder reserves the
    /// same band of caption and the picture holds still as you arrow through. Give one photo a
    /// credit line and that value flips for all of them — re-render only the edited page and its
    /// neighbours keep the old band, which is the jumping this was written to stop.
    /// </description></item>
    /// <item><description>
    /// Deleting one of two photos leaves the folder holding a single item, which publishes as the
    /// folder's own index rather than as a card — so the <em>survivor's</em> page moves a level up.
    /// </description></item>
    /// <item><description>
    /// Prev/Next links a page to its neighbours by name, so anything joining, leaving or being
    /// renamed changes the pages either side of it.
    /// </description></item>
    /// </list>
    ///
    /// Every one of those turns on the folder's <em>membership</em>, though — on something joining,
    /// leaving or being renamed. An edit to an artifact already in the folder moves nothing: the
    /// links point at stems it did not change, no collapse can fire, and its siblings render byte
    /// for byte what they rendered before. The one exception is <c>CaptionRows</c>, which flips when
    /// a folder goes from nothing having a subtitle to something having one, or back — so that is
    /// the question worth asking, rather than assuming the answer is yes.
    ///
    /// Which matters more than it sounds. A photo archive is often one large folder, so the folder
    /// <em>is</em> the site: measured on four hundred photos, one caption edit cost 505ms of the
    /// 525ms a full build takes. Folder granularity saved four per cent, on every save.
    ///
    /// Anything touching a directory, or the project config, still falls back to everything: both
    /// change the menu, and the menu is on every page in the site.
    /// </remarks>
    /// <param name="tree">
    /// The freshly scanned project, for asking what a folder's caption band would now be. Without it
    /// there is no way to tell an edit from a membership change, and every change takes the folder.
    /// </param>
    public static RenderScope For(
        string directoryRoot,
        IReadOnlyList<SourceChange> changes,
        ViewModels.DirectoryTreeItem? tree = null)
    {
        if (changes.Count == 0) return All;

        foreach (var change in changes)
        {
            if (IsConfig(change.Path) || (change.From is { } from && IsConfig(from)))
                return All;

            // A folder that came or went or moved rearranges the menu, and the menu is everywhere.
            // Judged by what is on disk now, plus the reading that a path with no extension is a
            // folder — a deleted directory can no longer be asked what it was.
            if (LooksLikeDirectory(change.Path) || (change.From is { } f && LooksLikeDirectory(f)))
                return All;
        }

        var folders = SiteChangeApplier.ExplainedBy(directoryRoot, changes);
        if (folders.Count == 0) return All;

        if (tree == null) return new RenderScope(folders);

        var siteRoot = Path.Combine(directoryRoot, "_site");
        var wide = new List<string>();
        var pages = new List<string>();

        foreach (var folder in folders)
        {
            var touched = changes
                .Where(c => SameFolder(directoryRoot, siteRoot, c, folder))
                .ToList();

            if (CanNarrow(directoryRoot, siteRoot, folder, touched, tree))
                pages.AddRange(touched.Select(c => PageDir(directoryRoot, siteRoot, c)).Where(p => p != null)!);
            else
                wide.Add(folder);
        }

        return new RenderScope(wide, pages);
    }

    /// <summary>
    /// Whether this folder's other pages can be left alone.
    /// </summary>
    /// <remarks>
    /// Two questions, and both have to answer yes. Did the folder's membership hold still — nothing
    /// added, removed or renamed, which is what moves prev/next links and can collapse a folder onto
    /// its single item? And does its caption band still read the same, which is the one value every
    /// page in the chain shares?
    ///
    /// The previous band is recovered from a sibling that is already on disk: it is rendered into
    /// every artifact page as a body class, so one file read answers for the whole folder. Anything
    /// unreadable, missing, or not yet built means we do not know, and not knowing takes the folder.
    /// </remarks>
    private static bool CanNarrow(
        string directoryRoot,
        string siteRoot,
        string folder,
        IReadOnlyList<SourceChange> touched,
        ViewModels.DirectoryTreeItem tree)
    {
        // A move or a removal is a membership change by definition.
        if (touched.Any(c => c.Kind != SourceChangeKind.Updated)) return false;

        // As is an artifact that has no page yet — something that has only just arrived.
        foreach (var change in touched)
        {
            var dir = PageDir(directoryRoot, siteRoot, change);
            if (dir == null) return false;
            if (!Directory.Exists(Path.Combine(siteRoot, dir.Replace('/', Path.DirectorySeparatorChar))))
                return false;
        }

        if (FindFolderNode(tree, directoryRoot, siteRoot, folder) is not { } node) return false;

        // And so is an artifact that has changed sides. `type:` is a sidecar key, and which side of
        // the prev/next flag an artifact falls on is decided by its type — so editing it moves the
        // chain without adding, removing or renaming anything. Every refusal above passes: the path
        // exists, its page exists, and the watcher calls it an ordinary update.
        //
        // Left alone, the neighbours keep the arrows they were built with. A photo newly on the
        // chain is skipped straight past in both directions; one newly off it leaves its neighbour
        // pointing at a page that no longer carries arrows at all. Neither corrects itself, because
        // as far as the ledger is concerned every page is exactly what this run asked for.
        if (touched.Any(c => ChangedSides(siteRoot, directoryRoot, c, node))) return false;

        var now = SiteGenerator.CaptionRowsFor(node);
        var before = CaptionRowsOnDisk(siteRoot, folder, touched, directoryRoot, node);

        return before != null && before == now;
    }

    /// <summary>
    /// Whether this artifact has joined or left the folder's prev/next chain since it was built.
    /// </summary>
    /// <remarks>
    /// Asked of the page rather than of the type, because the page is what the neighbours were built
    /// against and the previous type is not recorded anywhere. A page carries a nav link for each
    /// neighbour it had, and none at all when it was off the chain — the partial is deliberately
    /// silent in both cases, so a link means "was on the chain" while its absence is either that or
    /// "was the only one". Reading the absence as a change costs a folder render for a chain of one,
    /// where there are no neighbours to put right anyway.
    /// </remarks>
    private static bool ChangedSides(
        string siteRoot, string directoryRoot, SourceChange change, ViewModels.DirectoryTreeItem node)
    {
        var artifact = ArtifactPathOf(change.Path);
        var onChainNow = SiteGenerator.ChainIn(node).Any(c =>
            string.Equals(c.FullPath, artifact, StringComparison.OrdinalIgnoreCase));

        if (PageDir(directoryRoot, siteRoot, change) is not { } dir) return true;

        string html;
        try
        {
            html = File.ReadAllText(Path.Combine(
                siteRoot, dir.Replace('/', Path.DirectorySeparatorChar), "index.html"));
        }
        catch
        {
            return true;
        }

        var wasOnChain = html.Contains("artifact-nav-link", StringComparison.Ordinal);
        return onChainNow != wasOnChain;
    }

    /// <summary>
    /// The caption band this folder's <em>chain</em> was last built with, or null when it can't be
    /// read.
    /// </summary>
    /// <remarks>
    /// Read from a named chain sibling, never from whatever the directory listing offers first. The
    /// class is on every artifact page, but it does not say the same thing on all of them: a page
    /// off the chain is rendered with a band computed from itself alone, deliberately, so one PDF's
    /// author line doesn't cost every photo a row. Taking the first page found therefore answered
    /// with a PDF's band, or a single-photo sub-album's, whenever the listing happened to return one
    /// first — and the folder was then narrowed on a comparison against the wrong number, leaving its
    /// photos disagreeing about how much caption to reserve. Which is the picture-jumping the band
    /// exists to prevent, reintroduced by the check meant to protect it.
    ///
    /// It reproduced on some names and not others, because directory order is the filesystem's to
    /// choose and APFS does not sort.
    ///
    /// The changed artifact is skipped: its own page is the one whose content just moved, so it says
    /// nothing about what the rest of the folder agreed on.
    /// </remarks>
    private static int? CaptionRowsOnDisk(
        string siteRoot,
        string folder,
        IReadOnlyList<SourceChange> touched,
        string directoryRoot,
        ViewModels.DirectoryTreeItem node)
    {
        var changed = new HashSet<string>(
            touched.Select(c => PageDir(directoryRoot, siteRoot, c)).Where(d => d != null)!,
            StringComparer.OrdinalIgnoreCase);

        foreach (var sibling in SiteGenerator.ChainIn(node))
        {
            var stem = Path.GetFileNameWithoutExtension(sibling.Name);
            var rel = folder.Length == 0 ? stem : $"{folder}/{stem}";
            if (changed.Contains(rel)) continue;

            var page = Path.Combine(
                siteRoot, rel.Replace('/', Path.DirectorySeparatorChar), "index.html");

            string html;
            try { html = File.ReadAllText(page); }
            catch { continue; }

            if (html.Contains("artifact-meta-rows-2", StringComparison.Ordinal)) return 2;
            if (html.Contains("artifact-meta-rows-1", StringComparison.Ordinal)) return 1;
        }

        return null;
    }

    /// <summary>Where an artifact's own page lives, site-relative, or null if it isn't one.</summary>
    /// <remarks>
    /// The sidecar is the case to get right, because it is the case: there is no caption editor in
    /// the app, so editing a caption <em>is</em> writing <c>Portrait.jpg.yaml</c>, and the watcher
    /// reports the path that was written. Taking the stem of that name straight off gives
    /// "Portrait.jpg", which is not a page, so every caption edit failed the "has a page already"
    /// test and took its whole folder — the one case the narrowing was measured against.
    /// </remarks>
    private static string? PageDir(string directoryRoot, string siteRoot, SourceChange change)
    {
        var parent = Path.GetDirectoryName(change.Path);
        if (parent == null) return null;

        var relParent = Path.GetRelativePath(directoryRoot, parent).Replace(Path.DirectorySeparatorChar, '/');
        if (relParent.StartsWith("..", StringComparison.Ordinal)) return null;

        var stem = Path.GetFileNameWithoutExtension(ArtifactPathOf(change.Path));
        var folder = SiteGenerator.PublicRelativePath(relParent);

        return folder is "." or "" ? stem : $"{folder}/{stem}";
    }

    /// <summary>The artifact a written path belongs to — itself, or the file its sidecar names.</summary>
    private static string ArtifactPathOf(string path)
    {
        var ext = Path.GetExtension(path);

        return ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".yml", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetDirectoryName(path) ?? string.Empty,
                           Path.GetFileNameWithoutExtension(path))
            : path;
    }

    private static bool SameFolder(
        string directoryRoot, string siteRoot, SourceChange change, string folder)
    {
        var parent = Path.GetDirectoryName(change.Path);
        if (parent == null) return false;

        var rel = SiteGenerator.PublicRelativePath(
            Path.GetRelativePath(directoryRoot, parent).Replace(Path.DirectorySeparatorChar, '/'));
        if (rel is ".") rel = string.Empty;

        return rel.Equals(folder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The tree node that publishes to <paramref name="folder"/>, or null.</summary>
    private static ViewModels.DirectoryTreeItem? FindFolderNode(
        ViewModels.DirectoryTreeItem node, string directoryRoot, string siteRoot, string folder)
    {
        if (!node.IsDirectory) return null;

        var rel = SiteGenerator.PublicRelativePath(
            Path.GetRelativePath(directoryRoot, node.FullPath).Replace(Path.DirectorySeparatorChar, '/'));
        if (rel is ".") rel = string.Empty;

        if (rel.Equals(folder, StringComparison.OrdinalIgnoreCase)) return node;

        foreach (var child in node.Children)
            if (FindFolderNode(child, directoryRoot, siteRoot, folder) is { } found)
                return found;

        return null;
    }

    private static bool IsConfig(string path) =>
        Path.GetFileName(path).Equals("dir2site.yaml", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeDirectory(string path) =>
        Directory.Exists(path) || Path.GetExtension(path).Length == 0;

    /// <summary>
    /// Whether the page at <paramref name="sitePath"/> is worth rendering this run.
    /// </summary>
    /// <param name="siteRoot">Used to place the page relative to the site.</param>
    public bool ShouldRender(string siteRoot, string sitePath)
    {
        if (_folders == null) return true;

        var rel = Path.GetRelativePath(siteRoot, Path.GetDirectoryName(sitePath) ?? siteRoot)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (rel == ".") rel = string.Empty;

        // A page named on its own: render it, and nothing around it.
        foreach (var page in _pages)
            if (rel.Equals(page, StringComparison.OrdinalIgnoreCase)) return true;

        // And the indexes above any such page, which list it and carry the trail down to it.
        foreach (var page in _pages)
            if (page.Length > rel.Length
                && page.StartsWith(rel, StringComparison.OrdinalIgnoreCase)
                && (rel.Length == 0 || page[rel.Length] == '/'))
                return true;

        foreach (var folder in _folders)
        {
            // The folder itself and what sits inside it — the changed artifact's page and its
            // siblings, which a layout rule may have moved.
            //
            // The empty prefix is the site root, and it reaches the root's own pages rather than
            // every page there is. It used to match everything, so editing a readme at the top level
            // re-rendered all five hundred pages of a site — which is safe but gives up the whole
            // saving for exactly the content a small project has most of. This now reads the same
            // way as SiteChangeApplier.IsExplained, which had it right.
            if (Within(rel, folder)) return true;

            // And every index above it, because each one lists what is underneath and carries the
            // breadcrumb trail down to it.
            if (folder.Length > rel.Length
                && folder.StartsWith(rel, StringComparison.OrdinalIgnoreCase)
                && (rel.Length == 0 || folder[rel.Length] == '/'))
                return true;
        }

        return false;

        static bool Within(string dir, string folder)
        {
            if (folder.Length == 0) return !dir.Contains('/');

            return dir.Equals(folder, StringComparison.OrdinalIgnoreCase)
                || dir.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
