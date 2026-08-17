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

    [Fact]
    public void ADisposedWatcherDeliversNothing()
    {
        // Disposing does not wait for a settle already running, so a batch could arrive after the
        // watcher was let go. The caller disposes when the project changes — having just cleared
        // the lists the batch would be added to — so the previous project's paths landed in the new
        // one's, and were carried through: sidecars moved and preview folders deleted in a project
        // nobody had open.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var watcher = new SourceWatcher(_root, DebounceMs);
            var delivered = 0;
            watcher.Changed += (_, _) => Interlocked.Increment(ref delivered);
            watcher.Start();
            Thread.Sleep(20);

            // Make changes, then dispose while the settle for them is due.
            MakeFile($"churn{attempt}/a.md", "# A");
            MakeFile($"churn{attempt}/b.md", "# B");
            Thread.Sleep(DebounceMs / 2);
            watcher.Dispose();

            Thread.Sleep(DebounceMs * 3);
            Assert.Equal(0, delivered);
        }
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

        Assert.Empty(changes);
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
