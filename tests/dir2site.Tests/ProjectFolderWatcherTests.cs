// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Noticing that the project folder itself has gone.
/// </summary>
/// <remarks>
/// The reason this class exists is measurable: watching from inside the project answers none of
/// these. On macOS a <c>FileSystemWatcher</c> whose own root is renamed, deleted or moved raises no
/// error and delivers nothing — it falls silent, and silence is what an untouched folder looks like
/// too. Everything below is the same three events seen from one level up, where the directory being
/// watched is still there to report them.
/// </remarks>
public class ProjectFolderWatcherTests : IDisposable
{
    private readonly string _base = Path.Combine(
        Path.GetTempPath(), "d2s-folderwatch-" + Guid.NewGuid().ToString("N"));

    public ProjectFolderWatcherTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Watches <paramref name="project"/>, runs <paramref name="act"/>, returns what arrived.</summary>
    private static List<ProjectFolderEvent> Observe(string project, Action act, int settleMs = 2000)
    {
        var seen = new List<ProjectFolderEvent>();

        using var watcher = new ProjectFolderWatcher(project);
        watcher.Changed += (_, e) => { lock (seen) seen.Add(e); };
        watcher.Start();

        // The platform needs a moment to establish the watch, or the act races it — which reads as
        // a flaky test rather than as the race it is.
        Thread.Sleep(400);
        act();
        Thread.Sleep(settleMs);

        lock (seen) return [.. seen];
    }

    private string Make(string name)
    {
        var path = Path.Combine(_base, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "dir2site.yaml"), "title: Riverbend\n");
        return path;
    }

    [Fact]
    public void ARenameSaysWhereItWent()
    {
        // The whole reason to watch from above rather than give up: the new name is right there, so
        // the app can offer to follow instead of only reporting a loss.
        var project = Make("riverbend");
        var renamed = Path.Combine(_base, "riverbend-2024");

        var seen = Observe(project, () => Directory.Move(project, renamed));

        var change = Assert.Single(seen);
        Assert.Equal(ProjectFolderChange.Renamed, change.Kind);
        Assert.Equal(renamed, change.NewPath);
    }

    [Fact]
    public void ADeleteIsGone()
    {
        var project = Make("riverbend");

        var seen = Observe(project, () => Directory.Delete(project, recursive: true));

        var change = Assert.Single(seen);
        Assert.Equal(ProjectFolderChange.Gone, change.Kind);
        Assert.Null(change.NewPath);
    }

    [Fact]
    public void AMoveIntoASiblingFolderIsReportedOnceAndUsably()
    {
        // Deliberately not asserting which kind. Dragging the project into a folder beside it lands
        // outside the directory being watched, and the platforms disagree about what that is:
        // FSEvents calls it a rename and hands over the destination, while a Windows or Linux watch
        // scoped to one directory sees only that the entry left, and reports it gone.
        //
        // Both are fine, and the app is written to take either — so what is worth pinning is what it
        // relies on rather than which platform it is running on: one report for one departure, and a
        // new path that is real whenever there is one. Asserting "renamed" here would have passed on
        // this machine and failed the moment CI ran it on windows-latest.
        var project = Make("riverbend");
        var elsewhere = Directory.CreateDirectory(Path.Combine(_base, "elsewhere")).FullName;
        var destination = Path.Combine(elsewhere, "riverbend");

        var seen = Observe(project, () => Directory.Move(project, destination));

        var change = Assert.Single(seen);
        if (change.Kind == ProjectFolderChange.Renamed)
            Assert.True(Directory.Exists(change.NewPath!),
                "a rename that names a new location has to name one that is there");
        else
            Assert.Null(change.NewPath);
    }

    [Fact]
    public void AMoveRightOutOfTheTreeIsGone()
    {
        // Somewhere the watched directory has no view of at all. There is nothing to follow, so
        // reporting a rename with no new path would be a worse answer than saying it went.
        var project = Make("riverbend");
        var faraway = Path.Combine(Path.GetTempPath(), "d2s-folderwatch-dest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(faraway);

        try
        {
            var seen = Observe(project, () => Directory.Move(project, Path.Combine(faraway, "riverbend")));

            var change = Assert.Single(seen);
            Assert.Equal(ProjectFolderChange.Gone, change.Kind);
            Assert.Null(change.NewPath);
        }
        finally
        {
            try { Directory.Delete(faraway, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ASwapReportsTheMoveAndNotTheImpostor()
    {
        // Renamed away, and a new folder put back under the same name. The old watcher inside the
        // project carried on delivering events for the newcomer as though it were this project;
        // from up here the project's departure is what gets reported, and the arrival is somebody
        // else's business.
        var project = Make("riverbend");
        var seen = Observe(project, () =>
        {
            Directory.Move(project, Path.Combine(_base, "riverbend-old"));
            Directory.CreateDirectory(project);
        });

        var change = Assert.Single(seen);
        Assert.Equal(ProjectFolderChange.Renamed, change.Kind);
        Assert.Equal(Path.Combine(_base, "riverbend-old"), change.NewPath);
    }

    // ---- what must stay quiet ----------------------------------------------

    [Fact]
    public void WorkingInsideTheProjectSaysNothing()
    {
        // Otherwise every save would ask whether the project had moved.
        var project = Make("riverbend");

        var seen = Observe(project, () =>
        {
            Directory.CreateDirectory(Path.Combine(project, "Photographs"));
            File.WriteAllText(Path.Combine(project, "Photographs", "a.md"), "# A");
            File.WriteAllText(Path.Combine(project, "dir2site.yaml"), "title: Changed\n");
        });

        Assert.Empty(seen);
    }

    [Fact]
    public void ASiblingComingAndGoingSaysNothing()
    {
        var project = Make("riverbend");

        var seen = Observe(project, () =>
        {
            var sibling = Path.Combine(_base, "some-other-project");
            Directory.CreateDirectory(sibling);
            Directory.Delete(sibling);
            File.WriteAllText(Path.Combine(_base, "notes.txt"), "x");
        });

        Assert.Empty(seen);
    }

    [Fact]
    public void AProjectWithNoParentIsSimplyNotWatched()
    {
        // A folder at the root of a volume has nothing above it. That is a fact about the path, not
        // a failure, and it must not throw on the way to being useless.
        var separator = Path.DirectorySeparatorChar.ToString();
        using var watcher = new ProjectFolderWatcher(separator);

        watcher.Changed += (_, _) => Assert.Fail("nothing should be reported for a rootless path");
        watcher.Start();
        Thread.Sleep(200);
    }

    [Fact]
    public void DisposingBeforeAnythingHappensIsQuiet()
    {
        var project = Make("riverbend");

        var watcher = new ProjectFolderWatcher(project);
        watcher.Changed += (_, _) => Assert.Fail("a disposed watcher should say nothing");
        watcher.Start();
        Thread.Sleep(300);
        watcher.Dispose();

        Directory.Move(project, Path.Combine(_base, "riverbend-gone"));
        Thread.Sleep(1000);
    }
}
