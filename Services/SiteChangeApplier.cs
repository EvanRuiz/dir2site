// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dir2site.Services;

namespace dir2site.Services;

/// <summary>
/// Carries changes the user made in the source folder through to <c>_site</c>, before the generate
/// that follows has to make sense of them.
/// </summary>
/// <remarks>
/// A generate is a full re-walk with no memory of the previous layout, so a moved folder looks to it
/// like pages appearing at one address and unaccounted-for files at another. Those files then land
/// in the confirmation dialog, and the user is asked whether to delete two hundred things they never
/// deleted. Answering no leaves the old tree live in <c>_site</c> and still being deployed, so the
/// site is published at both addresses.
///
/// Applying the move first makes the question go away rather than answering it: the generate that
/// follows runs over a tree that is already right, so <c>WriteIfChanged</c> rewrites only the pages
/// whose breadcrumbs really changed, <c>CopyFileIfDifferent</c> skips every asset, and the sweep
/// finds nothing orphaned. Moving also keeps the mtimes, so the next deploy sends a delta instead of
/// re-uploading the whole subtree that a delete-and-rebuild would have made look new.
///
/// Only ever called with changes the watcher witnessed. What nobody saw happen is not knowledge, and
/// deletions inferred after the fact stay with the dialog — see <see cref="SourceChangeBatch"/>.
/// </remarks>
public static class SiteChangeApplier
{
    public sealed record Result(int Moved, int Removed, IReadOnlyList<string> Errors)
    {
        public bool DidAnything => Moved > 0 || Removed > 0;
    }

    /// <summary>
    /// Applies <paramref name="changes"/> to the <c>_site</c> under <paramref name="directoryRoot"/>.
    /// </summary>
    public static Result Apply(
        string directoryRoot,
        IReadOnlyList<SourceChange> changes,
        IProgress<string>? progress = null)
    {
        var siteRoot = Path.Combine(directoryRoot, "_site");
        if (!Directory.Exists(siteRoot)) return new Result(0, 0, []);

        var moved = 0;
        var removed = 0;
        var errors = new List<string>();

        foreach (var change in changes)
        {
            try
            {
                switch (change.Kind)
                {
                    case SourceChangeKind.Moved when change.From is { } from:
                        if (ApplyMove(directoryRoot, siteRoot, from, change.Path, progress)) moved++;
                        break;

                    case SourceChangeKind.Removed:
                        if (ApplyRemoval(directoryRoot, siteRoot, change.Path, progress)) removed++;
                        break;
                }
            }
            catch (Exception ex)
            {
                // One failure is not a reason to abandon the rest, and it is not a reason to guess
                // either: whatever this change wanted done is simply left undone, so the generate
                // that follows sees it as an unaccounted-for difference and offers it in the dialog.
                // That is the same place it would have gone with no watcher at all.
                errors.Add($"{Path.GetFileName(change.Path)}: {ex.Message}");
            }
        }

        if (removed > 0)
            SiteGenerator.RemoveEmptyDirectories(siteRoot, siteRoot);

        return new Result(moved, removed, errors);
    }

    /// <summary>
    /// Works out which moves must have happened while nobody was watching, and applies those.
    /// </summary>
    /// <remarks>
    /// Reorganizing a project in Finder with dir2site closed is entirely normal, and the resulting
    /// batch is unwitnessed — so the change list is empty and there is nothing for
    /// <see cref="Apply"/> to carry through. A move is still recoverable here, because the evidence
    /// is in the shapes themselves: a subtree sitting in <c>_site</c> that this run does not want,
    /// alongside a place this run does want that is not there, both under the same name.
    ///
    /// Deletions are deliberately <em>not</em> recovered this way. An unwanted subtree with nothing
    /// to pair against is just an unwanted subtree; whether the user deleted its source or something
    /// else is going on is exactly what we cannot tell, and that question belongs in the
    /// confirmation dialog rather than in a guess made here.
    /// </remarks>
    public static Result ReconcileMoves(
        string directoryRoot,
        ViewModels.DirectoryTreeItem rootItem,
        IProgress<string>? progress = null)
    {
        var siteRoot = Path.Combine(directoryRoot, "_site");
        if (!Directory.Exists(siteRoot)) return new Result(0, 0, []);

        var intended = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectIntended(rootItem, directoryRoot, siteRoot, intended);

        var unclaimed = TopmostUnclaimed(siteRoot, siteRoot, intended);
        var missing   = intended.Where(d => !Directory.Exists(d)).ToList();

        var moved  = 0;
        var errors = new List<string>();

        // Paired on the entry's own name and only where that name is unique on both sides — the same
        // rule, and the same reasoning, as pairing a delete against a create in a live batch. Two
        // folders called "1890s" leaving and two arriving has no single right answer, and a move
        // invented here would publish pages at an address the user never chose.
        // Both sides worked out once. Neither list is touched by the loop, so rebuilding the second
        // on every pass was the same GroupBy over the same items for the same answer.
        var wanted = Unique(missing);

        foreach (var (name, from) in Unique(unclaimed))
        {
            if (!wanted.TryGetValue(name, out var to)) continue;
            if (!Contains(siteRoot, from) || !Contains(siteRoot, to)) continue;
            if (Directory.Exists(to) || File.Exists(to)) continue;
            if (IsProtectedUnder(siteRoot, from) || IsProtectedUnder(siteRoot, to)) continue;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Directory.Move(from, to);
                progress?.Report($"Moved {name} in _site");
                moved++;
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }

        return new Result(moved, 0, errors);
    }

    /// <summary>
    /// Which parts of <c>_site</c> a witnessed change is answerable for, as site-relative prefixes.
    /// </summary>
    /// <remarks>
    /// A change does not only affect its own page. Deleting one of two photos from a folder leaves
    /// that folder holding a single item, and a folder holding a single item publishes it as its own
    /// index rather than as a card pointing at it — so the surviving photo's page moves up a level
    /// and the one it used to occupy is stranded. Nobody deleted it; the layout rules moved it.
    ///
    /// Those knock-on pages would otherwise reach the confirmation dialog, and asking about them is
    /// the same failure as asking about a moved folder: a question about something the user did not
    /// do and cannot usefully answer. So a witnessed change vouches for its own folder — and, for a
    /// move, for both ends of the journey.
    ///
    /// Deliberately the folder rather than the whole site. "Everything since the last generate was
    /// witnessed, so anything unclaimed must be our doing" is very nearly true and not worth
    /// relying on: <c>_site</c> is not watched, so a file placed there by hand or by another tool is
    /// exactly the thing we would not have seen. Keeping the vouching local means such a file still
    /// gets asked about, which is what should happen to something nobody can account for.
    /// </remarks>
    public static IReadOnlyList<string> ExplainedBy(
        string directoryRoot, IReadOnlyList<SourceChange> changes)
    {
        var siteRoot = Path.Combine(directoryRoot, "_site");
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in changes)
        {
            Add(change.Path);
            if (change.From is { } from) Add(from);
        }

        return [.. prefixes];

        void Add(string sourcePath)
        {
            var parent = Path.GetDirectoryName(sourcePath);
            if (parent == null) return;

            var rel = Path.GetRelativePath(siteRoot, AsFolder(directoryRoot, siteRoot, parent));
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return;

            prefixes.Add(rel == "." ? string.Empty : rel.Replace(Path.DirectorySeparatorChar, '/'));
        }
    }

    /// <summary>
    /// Whether a site-relative orphan path falls inside something a witnessed change vouches for.
    /// </summary>
    /// <remarks>
    /// Reaches one directory below the folder, and no further. That is enough for what a change
    /// actually strands — the folder's own index, an artifact's page and the assets beside it — and
    /// it stops a single edit vouching for an entire subtree. Vouching for the subtree meant a file
    /// somebody had placed by hand several folders down was removed without a word, where before it
    /// would have been offered in the dialog.
    ///
    /// Not airtight: a hand-placed file sitting directly inside a nested folder is still covered,
    /// because telling a nested published folder from an artifact's own directory needs the source
    /// tree, which the sweep does not have. Narrower than it was, and in the safe direction.
    /// </remarks>
    public static bool IsExplained(string siteRelativePath, IReadOnlyList<string> explained)
    {
        var path = siteRelativePath.Replace(Path.DirectorySeparatorChar, '/');
        var dir = path.LastIndexOf('/') is var cut && cut > 0 ? path[..cut] : string.Empty;
        var parent = dir.LastIndexOf('/') is var up && up > 0 ? dir[..up] : string.Empty;

        foreach (var prefix in explained)
        {
            // The folder's own files, and those one level in. An empty prefix is the site root, so
            // it reaches the root's own pages and the directories directly beneath it — never the
            // whole tree, or one change at top level would vouch for everything.
            if (Same(dir, prefix) || Same(parent, prefix)) return true;
        }

        return false;

        static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> Unique(List<string> paths) =>
        paths.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
             .Where(g => g.Count() == 1)
             .ToDictionary(g => g.Key!, g => g.Single(), StringComparer.OrdinalIgnoreCase);

    private static void CollectIntended(
        ViewModels.DirectoryTreeItem node, string directoryRoot, string siteRoot, HashSet<string> into)
    {
        if (node.IsDirectory)
        {
            into.Add(AsFolder(directoryRoot, siteRoot, node.FullPath));
            foreach (var child in node.Children)
                CollectIntended(child, directoryRoot, siteRoot, into);
            return;
        }

        if (node.Artifact != null)
            into.Add(AsArtifact(directoryRoot, siteRoot, node.FullPath));
    }

    /// <summary>
    /// The highest directories in <c>_site</c> this run has no use for. Stopping at the top of each
    /// unwanted subtree is what makes a moved folder one candidate rather than one per page inside it.
    /// </summary>
    private static List<string> TopmostUnclaimed(string dir, string siteRoot, HashSet<string> intended)
    {
        var found = new List<string>();

        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(dir); }
        catch { return found; }

        foreach (var child in children)
        {
            var full = Path.GetFullPath(child);
            if (IsProtectedUnder(siteRoot, full)) continue;

            if (intended.Contains(full))
                found.AddRange(TopmostUnclaimed(full, siteRoot, intended));
            else
                found.Add(full);
        }

        return found;
    }

    private static bool ApplyMove(
        string directoryRoot, string siteRoot, string from, string to, IProgress<string>? progress)
    {
        // Which of the two shapes this is can't be asked of the source any more — the old path is
        // gone by definition — so both are tried and at most one will be there. A folder publishes
        // under its own name; an artifact publishes under its stem, in a directory of its own.
        foreach (var (src, dest) in Candidates(directoryRoot, siteRoot, from, to))
        {
            if (!Directory.Exists(src)) continue;

            // Never overwrite. A destination that already exists means the site holds something we
            // did not expect, and merging two trees on a guess is not a recoverable mistake.
            if (Directory.Exists(dest) || File.Exists(dest)) return false;

            if (!Contains(siteRoot, src) || !Contains(siteRoot, dest)) return false;
            if (IsProtectedUnder(siteRoot, src) || IsProtectedUnder(siteRoot, dest)) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            Directory.Move(src, dest);
            progress?.Report($"Moved {Path.GetFileName(from)} in _site");
            return true;
        }

        return false;
    }

    private static bool ApplyRemoval(
        string directoryRoot, string siteRoot, string path, IProgress<string>? progress)
    {
        foreach (var target in RemovalCandidates(directoryRoot, siteRoot, path))
        {
            if (!Directory.Exists(target)) continue;
            if (!Contains(siteRoot, target)) continue;
            if (IsProtectedUnder(siteRoot, target)) continue;

            Directory.Delete(target, recursive: true);
            progress?.Report($"Removed {Path.GetFileName(path)} from _site");
            return true;
        }

        return false;
    }

    /// <summary>Both readings of a source path, as folder and as artifact, in that order.</summary>
    private static IEnumerable<(string Src, string Dest)> Candidates(
        string directoryRoot, string siteRoot, string from, string to)
    {
        yield return (AsFolder(directoryRoot, siteRoot, from), AsFolder(directoryRoot, siteRoot, to));
        yield return (AsArtifact(directoryRoot, siteRoot, from), AsArtifact(directoryRoot, siteRoot, to));
    }

    private static IEnumerable<string> RemovalCandidates(string directoryRoot, string siteRoot, string path)
    {
        yield return AsFolder(directoryRoot, siteRoot, path);
        yield return AsArtifact(directoryRoot, siteRoot, path);
    }

    /// <summary>Where a source folder publishes — its own name, with the markers stripped.</summary>
    private static string AsFolder(string directoryRoot, string siteRoot, string sourcePath) =>
        Path.GetFullPath(Path.Combine(
            siteRoot,
            Rel(SiteGenerator.PublicRelativePath(Path.GetRelativePath(directoryRoot, sourcePath)))));

    /// <summary>
    /// Where a source file publishes — a directory of its own, named for its stem.
    /// </summary>
    /// <remarks>
    /// The stem deliberately does not go through <c>PublicName</c>, matching
    /// <c>SiteGenerator.ArtifactHref</c>: the leading-dash and trailing-plus markers are a property
    /// of folders, and a file called <c>-notes.md</c> publishes under that name rather than losing a
    /// character to a convention that was never about it.
    /// </remarks>
    private static string AsArtifact(string directoryRoot, string siteRoot, string sourcePath)
    {
        var dir  = Path.GetDirectoryName(sourcePath) ?? directoryRoot;
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var relDir = SiteGenerator.PublicRelativePath(Path.GetRelativePath(directoryRoot, dir));

        return Path.GetFullPath(Path.Combine(siteRoot, Rel(relDir), stem));
    }

    // PublicRelativePath answers "." for the root itself, which Path.Combine would treat as a
    // segment rather than as "here".
    private static string Rel(string relative) =>
        relative is "." or "" ? string.Empty : relative.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>Whether <paramref name="path"/> really sits under <paramref name="root"/>.</summary>
    /// <remarks>
    /// A source path that has escaped the project — through a symlink, or a batch built from
    /// somewhere unexpected — would otherwise map to somewhere outside <c>_site</c>, and this class
    /// deletes directories recursively. Checked even though the watcher only reports paths beneath
    /// the root, because the cost of being wrong here is unbounded.
    /// </remarks>
    private static bool Contains(string root, string path)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return full.Length > prefix.Length
            && full.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the path holds a dot-segment, which <see cref="SiteGenerator"/> never writes and
    /// therefore never removes — a hand-placed <c>.htaccess</c>, a <c>.well-known/</c> challenge.
    /// The same rule the confirmation dialog's sweep applies, borrowed rather than restated.
    /// </summary>
    private static bool IsProtectedUnder(string siteRoot, string path) =>
        SiteGenerator.IsProtected(Path.GetRelativePath(siteRoot, path));
}
