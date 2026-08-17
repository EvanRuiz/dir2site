// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The coalescer is where the three watcher backends stop disagreeing, so it is tested without one.
/// Windows reports a rename with both paths, macOS commonly reports the same action as a delete and
/// a create, and a drag between folders is a delete and a create everywhere — driving those from a
/// real filesystem would only ever prove whichever backend the test machine happens to have.
/// </summary>
public class SourceChangeCoalescerTests
{
    private static string P(params string[] parts) => Path.Combine([Path.GetTempPath(), .. parts]);

    /// <summary>
    /// Coalesces with an explicit picture of what is on disk once the burst settles, which is what
    /// the real thing asks the filesystem. Stating it here is the point: these tests are about the
    /// rules, and a rule that reads "gone from disk" should be given a disk to read.
    /// </summary>
    private static SourceChangeBatch Coalesce(
        IEnumerable<RawSourceEvent> events, string[] present, bool witnessed = true)
    {
        var set = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
        return SourceChangeCoalescer.Coalesce(events, witnessed, set.Contains);
    }

    private static SourceChange Single(SourceChangeBatch batch)
    {
        Assert.True(batch.Witnessed);
        return Assert.Single(batch.Changes);
    }

    // ---- the two spellings of a rename ------------------------------------

    [Fact]
    public void ANativeRename_IsAMove()
    {
        var batch = Coalesce([
            new RawSourceEvent(RawChangeKind.Renamed, P("Photos", "Headshot.jpg"), P("Photos", "Portrait.jpg")),
        ], present: [P("Photos", "Headshot.jpg")]);

        var change = Single(batch);
        Assert.Equal(SourceChangeKind.Moved, change.Kind);
        Assert.Equal(P("Photos", "Headshot.jpg"), change.Path);
        Assert.Equal(P("Photos", "Portrait.jpg"), change.From);
    }

    [Fact]
    public void ADeleteAndACreateOfTheSameName_IsTheSameMove()
    {
        // What macOS hands us for the rename above, and what every platform hands us for a drag
        // between folders. It has to reach the identical classification or the feature works on one
        // operating system.
        var batch = Coalesce([
            new RawSourceEvent(RawChangeKind.Deleted, P("Photos", "Portrait.jpg")),
            new RawSourceEvent(RawChangeKind.Created, P("Archive", "Portrait.jpg")),
        ], present: [P("Archive", "Portrait.jpg")]);

        var change = Single(batch);
        Assert.Equal(SourceChangeKind.Moved, change.Kind);
        Assert.Equal(P("Archive", "Portrait.jpg"), change.Path);
        Assert.Equal(P("Photos", "Portrait.jpg"), change.From);
    }

    // ---- ambiguity is not guessed at --------------------------------------

    [Fact]
    public void TwoDeparturesAndTwoArrivalsOfOneName_ArePairedWithNeither()
    {
        // Nothing here says which index.md went where, and inventing an answer would publish a page
        // at an address the user never chose. A move we fail to spot costs a regenerated page; a
        // move we invent costs a wrong site.
        var batch = Coalesce([
            new RawSourceEvent(RawChangeKind.Deleted, P("A", "index.md")),
            new RawSourceEvent(RawChangeKind.Deleted, P("B", "index.md")),
            new RawSourceEvent(RawChangeKind.Created, P("C", "index.md")),
            new RawSourceEvent(RawChangeKind.Created, P("D", "index.md")),
        ], present: [P("C", "index.md"), P("D", "index.md")]);

        Assert.DoesNotContain(batch.Changes, c => c.Kind == SourceChangeKind.Moved);
        Assert.Equal(2, batch.Changes.Count(c => c.Kind == SourceChangeKind.Removed));
        Assert.Equal(2, batch.Changes.Count(c => c.Kind == SourceChangeKind.Updated));
    }

    [Fact]
    public void AnInPlaceRenameSpeltAsADeleteAndACreate_IsNotGuessedAtAsAMove()
    {
        // The name is what changed, so nothing here distinguishes a rename from "one photo deleted,
        // a different one added" — which is a thing people do constantly. Pairing on the folder
        // alone would rewrite a caption and shuffle a preview folder on that evidence. A native
        // rename states both paths and is taken at its word (see ANativeRename_IsAMove); this shape
        // does not, so it is read literally.
        var batch = Coalesce([
            new RawSourceEvent(RawChangeKind.Deleted, P("Photos", "Portrait.jpg")),
            new RawSourceEvent(RawChangeKind.Created, P("Photos", "Headshot.jpg")),
        ], present: [P("Photos", "Headshot.jpg")]);

        Assert.DoesNotContain(batch.Changes, c => c.Kind == SourceChangeKind.Moved);
        Assert.Contains(batch.Changes, c => c.Kind == SourceChangeKind.Removed);
        Assert.Contains(batch.Changes, c => c.Kind == SourceChangeKind.Updated);
    }

    [Fact]
    public void ARenameFollowedByASave_IsStillJustTheMove()
    {
        var batch = Coalesce([
            new RawSourceEvent(RawChangeKind.Renamed, P("notes.md"), P("draft.md")),
            new RawSourceEvent(RawChangeKind.Changed, P("notes.md")),
        ], present: [P("notes.md")]);

        var change = Single(batch);
        Assert.Equal(SourceChangeKind.Moved, change.Kind);
        Assert.Equal(P("draft.md"), change.From);
    }

    [Fact]
    public void ADeleteAndACreateOfDifferentNames_StayApart()
    {
        var batch = Coalesce([
            new RawSourceEvent(RawChangeKind.Deleted, P("Photos", "Portrait.jpg")),
            new RawSourceEvent(RawChangeKind.Created, P("Photos", "Landscape.jpg")),
        ], present: [P("Photos", "Landscape.jpg")]);

        Assert.Equal(2, batch.Changes.Count);
        Assert.Contains(batch.Changes, c => c.Kind == SourceChangeKind.Removed);
        Assert.Contains(batch.Changes, c => c.Kind == SourceChangeKind.Updated);
    }

    // ---- a folder move is one change, not one per file --------------------

    [Fact]
    public void AFolderMove_DoesNotAlsoClaimAMoveForEveryFileInside()
    {
        // Some backends report the folder and everything under it. The folder move already says all
        // of it: the contents went along, and so did their sidecars and previews. Left in, the batch
        // would ask for two hundred yaml files to be shuffled between paths that no longer exist.
        var batch = Coalesce([
            new RawSourceEvent(RawChangeKind.Renamed, P("Archive", "1890s"), P("Photos", "1890s")),
            new RawSourceEvent(RawChangeKind.Renamed, P("Archive", "1890s", "Portrait.jpg"),  P("Photos", "1890s", "Portrait.jpg")),
            new RawSourceEvent(RawChangeKind.Renamed, P("Archive", "1890s", "Landscape.jpg"), P("Photos", "1890s", "Landscape.jpg")),
        ], present: [
            P("Archive", "1890s"),
            P("Archive", "1890s", "Portrait.jpg"),
            P("Archive", "1890s", "Landscape.jpg"),
        ]);

        var change = Single(batch);
        Assert.Equal(SourceChangeKind.Moved, change.Kind);
        Assert.Equal(P("Archive", "1890s"), change.Path);
        Assert.Equal(P("Photos", "1890s"), change.From);
    }

    [Fact]
    public void AFolderMoveReportedAsDeletesAndCreates_IsAlsoJustOneMove()
    {
        var batch = Coalesce([
            new RawSourceEvent(RawChangeKind.Deleted, P("Photos", "1890s")),
            new RawSourceEvent(RawChangeKind.Deleted, P("Photos", "1890s", "Portrait.jpg")),
            new RawSourceEvent(RawChangeKind.Created, P("Archive", "1890s")),
            new RawSourceEvent(RawChangeKind.Created, P("Archive", "1890s", "Portrait.jpg")),
        ], present: [P("Archive", "1890s"), P("Archive", "1890s", "Portrait.jpg")]);

        var change = Single(batch);
        Assert.Equal(SourceChangeKind.Moved, change.Kind);
        Assert.Equal(P("Archive", "1890s"), change.Path);
    }

    // ---- repeated writes ---------------------------------------------------

    [Fact]
    public void AStreamOfWritesToOneFile_IsOneUpdate()
    {
        var batch = Coalesce([
            new RawSourceEvent(RawChangeKind.Changed, P("article.md")),
            new RawSourceEvent(RawChangeKind.Changed, P("article.md")),
            new RawSourceEvent(RawChangeKind.Changed, P("article.md")),
        ], present: [P("article.md")]);

        var change = Single(batch);
        Assert.Equal(SourceChangeKind.Updated, change.Kind);
    }

    [Fact]
    public void ASafeSave_ReadsAsAnUpdateRatherThanADeleteAndAnAdd()
    {
        // Editors routinely write a temp file and swap it in, which arrives as the original being
        // deleted and re-created. The site had that page before and has it now.
        var batch = Coalesce([
            new RawSourceEvent(RawChangeKind.Deleted, P("article.md")),
            new RawSourceEvent(RawChangeKind.Created, P("article.md")),
        ], present: [P("article.md")]);

        var change = Single(batch);
        Assert.Equal(SourceChangeKind.Updated, change.Kind);
        Assert.Equal(P("article.md"), change.Path);
    }

    [Fact]
    public void AFileThatAppearedAndVanishedWithinOneBurst_ReadsAsARemoval()
    {
        // An editor's temp file. The disk says it isn't there, which is all we ask it.
        //
        // Calling this a removal rather than nothing at all is deliberate. The tempting rule —
        // "created and then deleted inside one burst never really existed" — is wrong on macOS, and
        // the next test is why. Reporting a removal that costs nothing is much the cheaper error: a
        // file nothing was ever generated from has no page, no sidecar and no previews, so every
        // consequence of "removed" is a no-op.
        var batch = Coalesce([
            new RawSourceEvent(RawChangeKind.Created, P("article.md.tmp")),
            new RawSourceEvent(RawChangeKind.Changed, P("article.md.tmp")),
            new RawSourceEvent(RawChangeKind.Deleted, P("article.md.tmp")),
        ], present: []);

        Assert.Equal(SourceChangeKind.Removed, Single(batch).Kind);
    }

    [Fact]
    public void ADeleteReportedAlongsideItsOwnHistory_IsStillADelete()
    {
        // Taken from a probe of a real macOS delete: one File.Delete of a photo the watcher had seen
        // created reports Changed, Created *and* Deleted for the same path, because FSEvents replays
        // a path's accumulated history rather than streaming it. Read as a sequence that is a temp
        // file and the deletion disappears; read against the disk it is what the user actually did.
        var batch = Coalesce([
            new RawSourceEvent(RawChangeKind.Changed, P("Photos", "Portrait.jpg")),
            new RawSourceEvent(RawChangeKind.Created, P("Photos", "Portrait.jpg")),
            new RawSourceEvent(RawChangeKind.Deleted, P("Photos", "Portrait.jpg")),
        ], present: []);

        var change = Single(batch);
        Assert.Equal(SourceChangeKind.Removed, change.Kind);
        Assert.Equal(P("Photos", "Portrait.jpg"), change.Path);
    }

    [Fact]
    public void ACreateFollowedByWrites_IsStillAnAdd()
    {
        var batch = Coalesce([
            new RawSourceEvent(RawChangeKind.Created, P("new.md")),
            new RawSourceEvent(RawChangeKind.Changed, P("new.md")),
        ], present: [P("new.md")]);

        Assert.Equal(SourceChangeKind.Updated, Single(batch).Kind);
    }

    // ---- plain removals ----------------------------------------------------

    [Fact]
    public void ADeleteWithNoCounterpart_IsARemoval()
    {
        var batch = Coalesce([
            new RawSourceEvent(RawChangeKind.Deleted, P("Photos", "Portrait.jpg")),
        ], present: []);

        var change = Single(batch);
        Assert.Equal(SourceChangeKind.Removed, change.Kind);
        Assert.Null(change.From);
    }

    // ---- the unwitnessed flag ---------------------------------------------

    [Fact]
    public void AnUnwitnessedBurst_ClassifiesNothing()
    {
        // Losing events means some of what happened was never seen, so the classifications we could
        // still draw from the rest would be guesses dressed as knowledge. Saying nothing is what
        // sends deletions back to the confirmation dialog where they belong.
        var batch = Coalesce([
            new RawSourceEvent(RawChangeKind.Deleted, P("Photos", "Portrait.jpg")),
        ], present: [], witnessed: false);

        Assert.False(batch.Witnessed);
        Assert.Empty(batch.Changes);
    }

    [Fact]
    public void AWitnessedButEmptyBurst_IsStillWitnessed()
    {
        var batch = Coalesce([], present: []);

        Assert.True(batch.Witnessed);
        Assert.True(batch.IsEmpty);
    }
}
