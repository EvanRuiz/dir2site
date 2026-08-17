// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Drives a real <see cref="FileSystemWatcher"/> against a real folder, which is the only way to
/// learn what each platform actually reports. <see cref="SourceChangeCoalescerTests"/> proves the
/// classification rules; these prove that real events feed them the way we assumed.
/// </summary>
/// <remarks>
/// Deliberately free of any <c>OperatingSystem.IsX()</c> branch. The CI matrix is Windows and macOS
/// today and Linux is paused behind #88, so the value of these tests is that turning that row back
/// on runs them unchanged — which only stays true if nothing here knows which platform it is on.
///
/// Assertions are on the <em>union of classifications once the folder goes quiet</em>, never on how
/// many batches arrived. A backend that splits one logical move across two batches is still correct,
/// and pinning the batch count would fail on that difference rather than on a bug. What is left is
/// robust: either the event arrives and the assertion is exact, or nothing arrives and the test
/// times out.
///
/// A race is <em>caused</em> here, never waited for — worth saying plainly, because the obvious way
/// round is the wrong one and was tried first. Where the bug and the correct behaviour make the same
/// observation, no assertion can separate them: a delivery arriving about the moment <c>Dispose</c>
/// returns is either a missing guard or a settle that legitimately started earlier. A test that hopes
/// to land in that window is measuring how loaded the machine is, and the first attempt at
/// <see cref="ASettleInterruptedByDisposal_DeliversNothing"/> proved it — sixty attempts per run to
/// hit the window at all, then three red CI runs, every one of them the code working. Reach for a
/// seam like <c>SourceWatcher.SettlingForTests</c> so the interleaving is chosen rather than won. If
/// no seam is possible, verify the guard once by deleting it and watching a throwaway test fail, then
/// keep the guard and the reasoning and leave the suite out of it.
/// </remarks>
public class SourceWatcherTests : IDisposable
{
    // Short enough that a test costs milliseconds rather than a second, long enough that a burst
    // still settles as one. The interval is a constructor argument precisely for this.
    private const int DebounceMs = 60;

    // Generous on purpose: FSEvents is not prompt, and a slow CI runner is not a failure. Nothing
    // is asserted about how long an event takes, only about what it says when it comes.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dir2site-watch-" + Guid.NewGuid().ToString("N"));

    public SourceWatcherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Path_(params string[] parts) => Path.Combine([_root, .. parts]);

    private string MakeFile(string relative, string content = "x")
    {
        var full = Path_(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>
    /// Runs <paramref name="act"/> against a started watcher and returns every change reported once
    /// the folder has gone quiet. Returns empty rather than throwing on timeout, so a test asserting
    /// that nothing is reported doesn't have to distinguish "nothing" from "nothing yet".
    /// </summary>
    private List<SourceChange> Observe(Action act, bool expectSomething = true)
    {
        using var watcher = new SourceWatcher(_root, DebounceMs);

        var collected = new List<SourceChange>();
        var quiet = new ManualResetEventSlim(false);
        var gate = new object();

        watcher.Changed += (_, batch) =>
        {
            lock (gate) collected.AddRange(batch.Changes);
            // Re-arm rather than finish: a platform is allowed to split one action across batches,
            // and the union is what is being asserted.
            quiet.Set();
        };

        watcher.Start();

        // Let the watcher settle before acting. Without this the first event can be missed on
        // backends that register asynchronously, which reads as a flaky test rather than a race.
        Thread.Sleep(200);

        act();

        if (expectSomething)
        {
            quiet.Wait(Timeout);
            // Once something has arrived, wait out one more quiet window to collect the rest of a
            // split delivery before asserting on the union.
            Thread.Sleep(DebounceMs * 6);
        }
        else
        {
            // Nothing is expected, so there is no signal to wait for — only a window to prove empty.
            Thread.Sleep(DebounceMs * 10);
        }

        lock (gate) return [.. collected];
    }

    // ---- the three operations that matter --------------------------------

    [Fact]
    public void MovingAFolder_IsReportedAsOneMoveOfTheFolder()
    {
        MakeFile("Photos/1890s/Portrait.jpg");
        MakeFile("Photos/1890s/Landscape.jpg");
        Directory.CreateDirectory(Path_("Archive"));

        var changes = Observe(() =>
            Directory.Move(Path_("Photos", "1890s"), Path_("Archive", "1890s")));

        var move = Assert.Single(changes, c => c.Kind == SourceChangeKind.Moved);
        Assert.Equal(Path_("Archive", "1890s"), move.Path);
        Assert.Equal(Path_("Photos", "1890s"), move.From);

        // The point of the exercise: two hundred photos must not become two hundred renames, each
        // wanting a sidecar and a preview folder shuffled.
        Assert.DoesNotContain(changes, c => c.Path.Contains("Portrait.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public void MovingAFileBetweenFolders_IsReportedAsAMove()
    {
        MakeFile("Photos/Portrait.jpg");
        Directory.CreateDirectory(Path_("Archive"));

        var changes = Observe(() =>
            File.Move(Path_("Photos", "Portrait.jpg"), Path_("Archive", "Portrait.jpg")));

        var move = Assert.Single(changes, c => c.Kind == SourceChangeKind.Moved);
        Assert.Equal(Path_("Archive", "Portrait.jpg"), move.Path);
        Assert.Equal(Path_("Photos", "Portrait.jpg"), move.From);
    }

    [Fact]
    public void RenamingAFileInPlace_IsReportedAsAMove()
    {
        // The case that needs a native rename to be classifiable at all: the name is what changed,
        // so a delete-and-create spelling could not be paired without guessing. If a platform ever
        // stops reporting this as a rename, this test is where that shows up rather than in a user's
        // site quietly gaining a duplicate page.
        MakeFile("Photos/Portrait.jpg");

        var changes = Observe(() =>
            File.Move(Path_("Photos", "Portrait.jpg"), Path_("Photos", "Headshot.jpg")));

        var move = Assert.Single(changes, c => c.Kind == SourceChangeKind.Moved);
        Assert.Equal(Path_("Photos", "Headshot.jpg"), move.Path);
        Assert.Equal(Path_("Photos", "Portrait.jpg"), move.From);
    }

    [Fact]
    public void DeletingAFile_IsReportedAsARemoval()
    {
        MakeFile("Photos/Portrait.jpg");

        var changes = Observe(() => File.Delete(Path_("Photos", "Portrait.jpg")));

        var removed = Assert.Single(changes, c => c.Kind == SourceChangeKind.Removed);
        Assert.Equal(Path_("Photos", "Portrait.jpg"), removed.Path);
    }

    [Fact]
    public void AddingAFile_IsReported()
    {
        Directory.CreateDirectory(Path_("Photos"));

        var changes = Observe(() => MakeFile("Photos/New.jpg"));

        Assert.Contains(changes, c => c.Path == Path_("Photos", "New.jpg")
                                   && c.Kind == SourceChangeKind.Updated);
    }

    [Fact]
    public void EditingAYamlByHand_IsReported()
    {
        // Sidecars are excluded from the tree walk as metadata, and deliberately not excluded here:
        // a hand-edited yaml is exactly the change #62 exists to surface.
        MakeFile("Photos/Portrait.jpg.yaml", "caption: Portrait\n");

        var changes = Observe(() =>
            File.WriteAllText(Path_("Photos", "Portrait.jpg.yaml"), "caption: Grandmother\n"));

        Assert.Contains(changes, c => c.Path == Path_("Photos", "Portrait.jpg.yaml"));
    }

    // ---- shutting down safely ---------------------------------------------

    [Fact]
    public void DisposingWhileEventsAreArriving_DoesNotThrow()
    {
        // Dispose runs on the UI thread when the project changes or the window closes, while events
        // arrive on the watcher's own callback thread. One landing between the timer being read and
        // being started threw ObjectDisposedException on a background thread — which is not caught
        // anywhere and takes the process down.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var watcher = new SourceWatcher(_root, DebounceMs);
            watcher.Start();
            Thread.Sleep(20);

            // Keep the folder busy while it is being disposed out from under the events.
            var writing = new Thread(() =>
            {
                for (var i = 0; i < 60; i++)
                {
                    try { MakeFile($"churn/file{i}.md", "x"); } catch { return; }
                }
            });

            writing.Start();
            watcher.Dispose();
            writing.Join();
        }

        // Reaching here at all is the assertion: an unhandled exception on the callback thread ends
        // the test host rather than failing a test.
        Assert.True(true);
    }

    // ---- shutting down safely ---------------------------------------------

    [Fact]
    public void ASettleInterruptedByDisposal_DeliversNothing()
    {
        // Disposing does not wait for a settle already running, so one can be part-way through when
        // the watcher is let go. That matters because the caller disposes when the project changes,
        // having just cleared the lists the batch would land in — so the previous project's paths
        // were carried through in the new one: sidecars moved and preview folders deleted in a
        // project nobody had open.
        //
        // The disposal is placed inside the window rather than raced into it, so this asserts the
        // guard rather than the speed of the machine.
        using var watcher = new SourceWatcher(_root, DebounceMs);

        var delivered = 0;
        watcher.Changed += (_, _) => Interlocked.Increment(ref delivered);
        watcher.SettlingForTests = () => watcher.Dispose();

        watcher.Start();
        Thread.Sleep(200);

        MakeFile("Photos/Portrait.jpg");
        Thread.Sleep(DebounceMs * 10);

        Assert.Equal(0, delivered);
    }

    // ---- what must stay silent -------------------------------------------

    [Fact]
    public void WritingInsideDir2site_IsNotReported()
    {
        // The generator writes here on every run. Reacting would make each generate trigger the
        // next one, forever.
        MakeFile("Photos/Portrait.jpg");
        Directory.CreateDirectory(Path_("Photos", ".dir2site", "Portrait"));

        var changes = Observe(
            () => File.WriteAllText(
                Path_("Photos", ".dir2site", "Portrait", "preview-Portrait.webp"), "fake"),
            expectSomething: false);

        // Nothing from .dir2site, rather than nothing at all. The setup above writes Portrait.jpg,
        // which is watchable, and FSEvents reports a path's accumulated history rather than only
        // what happened after the watch began — so on a loaded machine those setup writes arrive
        // inside the observation window and an empty-collection assertion fails on them. That is
        // the test's own noise, not the thing it exists to catch: what must never be reported is a
        // write the generator makes to its own output.
        Assert.DoesNotContain(changes, c => c.Path.Contains(".dir2site", StringComparison.Ordinal));
    }

    [Fact]
    public void WritingInsideTheSiteFolder_IsNotReported()
    {
        Directory.CreateDirectory(Path_("_site", "Photos"));

        var changes = Observe(
            () => File.WriteAllText(Path_("_site", "Photos", "index.html"), "<html></html>"),
            expectSomething: false);

        Assert.Empty(changes);
    }
}
