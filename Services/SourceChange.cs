// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace dir2site.Services;

/// <summary>What the user did to one entry in the source folder.</summary>
/// <remarks>
/// There is deliberately no separate "added" — a path that exists now is <see cref="Updated"/>
/// whether it is new or was merely written to. The two cannot be told apart reliably from watcher
/// events (macOS reports a Created for a file that has existed for hours, because it replays the
/// path's history rather than streaming it), and nothing downstream wants to know: both mean
/// "rescan this and re-render what it reaches". Inventing a distinction we cannot support would put
/// a guess in the type system where a fact belongs.
///
/// <see cref="Removed"/> and <see cref="Moved"/> are the load-bearing ones, because only they cause
/// anything to be taken away — and both are settled against the disk rather than inferred from
/// event order.
/// </remarks>
public enum SourceChangeKind
{
    /// <summary>Something is there now: created, written to, or replaced.</summary>
    Updated,

    /// <summary>Something that was there is gone, and went nowhere we can see.</summary>
    Removed,

    /// <summary>Something is now at a different path. Covers renames and drags alike.</summary>
    Moved,
}

/// <summary>
/// One classified change. <see cref="From"/> is set only for <see cref="SourceChangeKind.Moved"/>,
/// where it is where the entry used to be.
/// </summary>
public sealed record SourceChange(SourceChangeKind Kind, string Path, string? From = null);

/// <summary>
/// A settled burst of changes, and whether we actually saw all of it.
/// </summary>
/// <param name="Witnessed">
/// False when events were lost — a buffer overflow, a watcher error, or a scan that ran while
/// nothing was watching. It is the difference between acting and asking: a move or a delete we
/// witnessed is the user's stated intent and needs no confirmation, whereas the same conclusion
/// reached by inference from an unwitnessed batch is a guess, and guesses about deletion get
/// offered rather than applied.
/// </param>
public sealed record SourceChangeBatch(IReadOnlyList<SourceChange> Changes, bool Witnessed)
{
    public static readonly SourceChangeBatch Empty = new([], true);

    /// <summary>An unwitnessed batch carries no classifications — that is what unwitnessed means.</summary>
    public static readonly SourceChangeBatch Unwitnessed = new([], false);

    public bool IsEmpty => Changes.Count == 0;
}

/// <summary>The raw event kinds a <see cref="FileSystemWatcher"/> reports, before we make sense of them.</summary>
public enum RawChangeKind { Created, Changed, Deleted, Renamed }

/// <summary>One event as it arrived. <see cref="OldPath"/> is set only for a native rename.</summary>
public sealed record RawSourceEvent(RawChangeKind Kind, string Path, string? OldPath = null);

/// <summary>
/// Turns a burst of raw filesystem events into what the user actually did.
/// </summary>
/// <remarks>
/// This is the whole of the platform-portability story, and it is a pure function so it can be
/// proven without a filesystem. The backends behind <see cref="FileSystemWatcher"/> disagree on how
/// they spell a move: Windows reports <see cref="RawChangeKind.Renamed"/> with both paths, while a
/// drag between two folders arrives as a delete and a create on every platform, Windows included.
///
/// So there are two sources of evidence for one conclusion, and they are not equally good. A native
/// rename <em>states</em> both paths; a delete-and-create pair only implies them, and the implication
/// rests on the name being unchanged. Rewriting the former into the latter — which reads as pleasing
/// symmetry — throws away the only evidence that can pair an in-place rename, where the name is
/// precisely what changed. Both are therefore taken as they come.
///
/// Where a platform reports an in-place rename <em>only</em> as a delete and a create with different
/// names, this reads it as a removal and an addition. That is deliberate. Pairing them would mean
/// guessing from nothing more than "one file left this folder and another arrived", which is equally
/// true of deleting one photo and adding a different one — and the costs are not symmetric: an
/// unspotted move regenerates a page, an invented one publishes a page at an address the user never
/// chose and rewrites a caption to match.
/// </remarks>
public static class SourceChangeCoalescer
{
    /// <summary>
    /// Collapses <paramref name="events"/> into the smallest set of changes that explains them,
    /// resolving each touched path against what is actually on disk now.
    /// </summary>
    /// <param name="exists">
    /// Whether a path is present now that the burst has settled. Injectable so the rules can be
    /// proven without a filesystem; production has no reason to pass it.
    /// </param>
    /// <remarks>
    /// The event <em>order</em> is deliberately not used to decide whether something still exists,
    /// because on macOS it cannot bear that weight. FSEvents reports a path's accumulated history
    /// rather than an ordered stream, so deleting a file that this watcher saw created earlier
    /// arrives as Changed, Created <em>and</em> Deleted together. Read as a sequence that is a temp
    /// file appearing and vanishing; read against the disk it is what it really is, a deletion.
    ///
    /// So the events are used only to say <em>which paths to look at</em>, and the filesystem is
    /// asked what became of them. That matches how the rest of the feature already works — the
    /// watcher is a hint, the disk is the truth — and it makes the answer identical on every
    /// platform regardless of how each spells the journey.
    /// </remarks>
    public static SourceChangeBatch Coalesce(
        IEnumerable<RawSourceEvent> events,
        bool witnessed = true,
        Func<string, bool>? exists = null)
    {
        if (!witnessed) return SourceChangeBatch.Unwitnessed;

        var raw     = events as IReadOnlyList<RawSourceEvent> ?? [.. events];
        var present = exists ?? (p => File.Exists(p) || Directory.Exists(p));

        var changes  = new List<SourceChange>();
        var resolved = new HashSet<string>(PathComparer);

        // Stated moves first: a native rename names both ends, and nothing downstream could
        // reconstruct that once the pair is broken up — an in-place rename changes the very name
        // that any later pairing would have to match on. Still checked against the disk, so a
        // rename that was undone or deleted again inside the same burst doesn't survive as a move.
        foreach (var e in raw)
        {
            if (e.Kind != RawChangeKind.Renamed || e.OldPath is not { Length: > 0 } old) continue;
            if (!present(e.Path) || present(old)) continue;

            changes.Add(new SourceChange(SourceChangeKind.Moved, e.Path, old));
            resolved.Add(e.Path);
            resolved.Add(old);
        }

        var gone    = new List<string>();
        var arrived = new List<string>();

        foreach (var path in TouchedPaths(raw))
        {
            if (!resolved.Add(path)) continue;
            (present(path) ? arrived : gone).Add(path);
        }

        // Then implied ones, from whatever is left over.
        changes.AddRange(PairMoves(gone, arrived));
        changes.AddRange(gone.Select(p => new SourceChange(SourceChangeKind.Removed, p)));
        changes.AddRange(arrived.Select(p => new SourceChange(SourceChangeKind.Updated, p)));

        return new SourceChangeBatch(DropRedundant(changes), Witnessed: true);
    }

    /// <summary>
    /// Every path the burst mentioned, in the order first seen and without repeats — an editor
    /// writing a large file emits a stream of events about one path, and macOS emits several kinds
    /// about one path for a single action.
    /// </summary>
    private static IEnumerable<string> TouchedPaths(IReadOnlyList<RawSourceEvent> events)
    {
        var seen = new HashSet<string>(PathComparer);
        foreach (var e in events)
        {
            if (e.OldPath is { Length: > 0 } old && seen.Add(old)) yield return old;
            if (seen.Add(e.Path)) yield return e.Path;
        }
    }

    /// <summary>
    /// Matches what left against what arrived. A move keeps the entry's name, so that is the
    /// evidence — but only where it is unambiguous.
    /// </summary>
    /// <remarks>
    /// Requiring exactly one departure and one arrival of a given name is the whole safety argument.
    /// Two files called <c>index.md</c> deleted from two folders while two more appear elsewhere has
    /// no single right pairing, and inventing one would move a page to an address the user never
    /// chose. Ambiguity falls through to plain removes and adds, which is the conservative reading:
    /// a wrongly-unpaired move costs a regenerated page, a wrongly-paired one costs a wrong site.
    ///
    /// Matched entries are removed from both lists, so the caller is left holding only the genuine
    /// removals and additions.
    /// </remarks>
    private static List<SourceChange> PairMoves(List<string> deleted, List<string> created)
    {
        var moves = new List<SourceChange>();

        var arrivalsByName = created
            .GroupBy(Path.GetFileName, PathComparer)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key!, g => g.Single(), PathComparer);

        var departuresByName = deleted
            .GroupBy(Path.GetFileName, PathComparer)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key!, g => g.Single(), PathComparer);

        foreach (var (name, from) in departuresByName)
        {
            if (!arrivalsByName.TryGetValue(name, out var to)) continue;
            if (PathComparer.Equals(from, to)) continue;

            moves.Add(new SourceChange(SourceChangeKind.Moved, to, from));
            deleted.Remove(from);
            created.Remove(to);
        }

        return moves;
    }

    /// <summary>
    /// Drops changes that a move already accounts for.
    /// </summary>
    /// <remarks>
    /// Moving a folder of two hundred photos can report the folder <em>and</em> every file inside
    /// it, depending on the platform. The folder move already says everything: its contents went
    /// with it, their sidecars and previews went with them, and nothing inside needs handling of its
    /// own. Left in, the batch would claim two hundred renames that each want a yaml and a preview
    /// folder shuffled — work that has already happened, against paths that no longer exist.
    ///
    /// The same applies to a write landing on a path that has just moved: renaming a file and then
    /// saving it is one move, not a move and an edit.
    /// </remarks>
    private static List<SourceChange> DropRedundant(List<SourceChange> changes)
    {
        var moves = changes.Where(c => c.Kind == SourceChangeKind.Moved).ToList();
        if (moves.Count == 0) return changes;

        // A move nested inside another move is itself redundant, so moves are filtered too — but
        // never against themselves.
        return [.. changes.Where(c => !moves.Any(m => m != c && CoveredBy(c, m)))];
    }

    private static bool CoveredBy(SourceChange change, SourceChange move) =>
        IsUnder(change.Path, move.Path) ||
        (change.From is { } from && IsUnder(from, move.From ?? move.Path)) ||
        (change.Kind != SourceChangeKind.Moved && PathComparer.Equals(change.Path, move.Path));

    private static bool IsUnder(string path, string ancestor)
    {
        var prefix = ancestor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Length > prefix.Length
            && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (path[prefix.Length] == Path.DirectorySeparatorChar ||
                path[prefix.Length] == Path.AltDirectorySeparatorChar);
    }

    // Case-insensitively, matching the ledger's reasoning at SiteGenerator.SiteLedger: on a
    // case-folding filesystem the same entry can be reported under either spelling, and treating
    // those as two entries would pair a move against itself.
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
}
