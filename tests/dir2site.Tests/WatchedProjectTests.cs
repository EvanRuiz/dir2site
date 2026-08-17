// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using dir2site.Services;
using dir2site.ViewModels;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The whole thing joined up: a real folder, a real watcher, and the view model that has to turn
/// what the user did into a site.
/// </summary>
/// <remarks>
/// Every part of this is covered on its own — classification, applying changes, generating. What
/// isn't, until here, is that they are wired to each other: the watcher's batch reaching the view
/// model, being carried into <c>_site</c>, and the generate that follows finding nothing to ask
/// about. That last clause is the claim the branch exists for, and it is the one that would survive
/// every unit test while being broken in the app.
/// </remarks>
public class WatchedProjectTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-watched-" + Guid.NewGuid().ToString("N"));

    // Every view model these tests start watching with, so none is left holding a handle on a
    // folder about to be deleted out from under it.
    private readonly List<MainWindowViewModel> _watching = [];

    public WatchedProjectTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var vm in _watching) vm.StopWatching();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Starts watching, and makes sure it stops when the test is done.</summary>
    private void Watch(MainWindowViewModel vm)
    {
        _watching.Add(vm);
        vm.StartWatching();
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);
    private string SitePath(params string[] parts) => Path.Combine([_root, "_site", .. parts]);

    private static void MakeArtifact(string folder, string fileName, string caption)
    {
        File.WriteAllText(Path.Combine(folder, fileName), "not really a jpeg");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"type: photo\ncaption: {caption}\n");
    }

    /// <summary>
    /// Pumps the Avalonia dispatcher until <paramref name="done"/> holds, because the watcher posts
    /// its work there and a headless test has nothing else driving that queue.
    /// </summary>
    private static async Task<bool> Until(Func<bool> done, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (done()) return true;
            await Task.Delay(50);
        }
        return done();
    }

    [AvaloniaFact]
    public async Task MovingAFolderWhileWatching_RebuildsTheSiteAndAsksNothing()
    {
        var nested = Directory.CreateDirectory(At("Photographs", "1890s")).FullName;
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");
        var documents = Directory.CreateDirectory(At("Documents")).FullName;
        MakeArtifact(documents, "Letter.jpg", "A Letter");
        MakeArtifact(documents, "Memo.jpg", "A Memo");

        var vm = new MainWindowViewModel { DirectoryRoot = _root };

        var asked = 0;
        vm.AskAboutOrphans = orphans =>
        {
            asked++;
            return Task.FromResult<IReadOnlyList<string>?>(null);
        };

        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);
        Assert.True(File.Exists(SitePath("Photographs", "1890s", "Portrait", "index.html")));

        Watch(vm);
        vm.AutoGenerate = true;

        // Give the watcher a moment to register before moving anything, or the move can happen
        // before it is listening — which reads as a flaky test rather than a race.
        await Task.Delay(300);

        Directory.CreateDirectory(At("Archive"));
        Directory.Move(At("Photographs", "1890s"), At("Archive", "1890s"));

        var arrived = await Until(() => File.Exists(SitePath("Archive", "1890s", "Portrait", "index.html")));

        Assert.True(arrived, "the moved folder never reached _site");
        Assert.False(Directory.Exists(SitePath("Photographs", "1890s")));

        // Nobody was asked to confirm deleting anything, which is the point of the whole exercise.
        Assert.Equal(0, asked);

        // And the rest of the site is untouched.
        Assert.True(File.Exists(SitePath("Documents", "Letter", "index.html")));
        Assert.True(File.Exists(SitePath("index.html")));
    }

    [AvaloniaFact]
    public async Task RenamingAPhotoWhileWatching_CarriesItsCaptionAndPage()
    {
        var photos = Directory.CreateDirectory(At("Photographs")).FullName;
        MakeArtifact(photos, "Portrait.jpg", "Grandmother, 1912");
        MakeArtifact(photos, "Landscape.jpg", "A Landscape");

        var vm = new MainWindowViewModel { DirectoryRoot = _root };
        vm.AskAboutOrphans = _ => Task.FromResult<IReadOnlyList<string>?>(null);

        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);

        Watch(vm);
        vm.AutoGenerate = true;
        await Task.Delay(300);

        File.Move(At("Photographs", "Portrait.jpg"), At("Photographs", "Headshot.jpg"));

        var arrived = await Until(() => File.Exists(At("Photographs", "Headshot.jpg.yaml")));

        Assert.True(arrived, "the sidecar never followed the rename");

        // The caption the user wrote came with it rather than being re-derived from the new name.
        Assert.Contains("Grandmother, 1912",
            File.ReadAllText(At("Photographs", "Headshot.jpg.yaml")), StringComparison.Ordinal);

        await Until(() => File.Exists(SitePath("Photographs", "Headshot", "index.html")));
        Assert.True(File.Exists(SitePath("Photographs", "Headshot", "index.html")));
    }

    [AvaloniaFact]
    public async Task APhotoAddedWhileAGenerateIsRunning_StillGetsAPage()
    {
        // Anything arriving mid-run used to be discarded outright — not recorded, so the next run's
        // narrowed scope skipped it too. The file ended up with no page and no card, and nothing
        // anywhere said so.
        //
        // The window has to be hit precisely. A generate re-scans from disk when it starts, so a
        // photo added at the top of a run is caught by that scan regardless; the one that gets lost
        // arrives after it. Hooking the "Generating site..." report puts the write on the far side
        // of the scan, which is the case that used to fail.
        // Enough pages that writing them all outlasts the watcher's quiet period, so the batch is
        // genuinely delivered mid-run rather than after it.
        var photos = Directory.CreateDirectory(At("Photographs")).FullName;
        for (var i = 0; i < 120; i++) MakeArtifact(photos, $"Photo{i:D3}.jpg", $"Photo {i}");
        var documents = Directory.CreateDirectory(At("Documents")).FullName;
        MakeArtifact(documents, "Letter.jpg", "A Letter");
        MakeArtifact(documents, "Memo.jpg", "A Memo");

        var vm = new MainWindowViewModel { DirectoryRoot = _root, WatchDebounceMs = 50 };
        vm.AskAboutOrphans = _ => Task.FromResult<IReadOnlyList<string>?>(null);

        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);

        var added = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(vm.StatusText) || added) return;
            if (!vm.StatusText.StartsWith("Generating site", StringComparison.Ordinal)) return;

            added = true;
            MakeArtifact(documents, "Postcard.jpg", "A Postcard");
        };

        Watch(vm);
        vm.AutoGenerate = true;
        await Task.Delay(300);

        // Sets a run going; the handler drops the new photo in once that run is past its own scan.
        File.WriteAllText(Path.Combine(photos, "Photo000.jpg"), "a different jpeg");

        var arrived = await Until(() => File.Exists(SitePath("Documents", "Postcard", "index.html")));

        Assert.True(added, "the test never managed to add the photo mid-run");
        Assert.True(arrived, "the photo added during the run never got a page");
        Assert.Contains("Postcard", File.ReadAllText(SitePath("Documents", "index.html")),
            StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task APhotoAddedDuringAManualGenerate_StillGetsAPage()
    {
        // IsLoading is held by far more than the scan-and-generate loop — a manual Generate, a
        // manual Rescan, the leftovers dialog, every deploy. A batch arriving during any of them set
        // a flag only that loop ever read, so the change sat there until something else happened to
        // sweep it up: the site quietly out of date with the checkbox still ticked.
        //
        // A manual Generate stands in for all of them, because it is the one whose length is easy to
        // arrange. Nothing here goes through RespondToChanges, so if the flag isn't drained when the
        // app falls idle, nothing ever reads it.
        var photos = Directory.CreateDirectory(At("Photographs")).FullName;
        for (var i = 0; i < 120; i++) MakeArtifact(photos, $"Photo{i:D3}.jpg", $"Photo {i}");
        var documents = Directory.CreateDirectory(At("Documents")).FullName;
        MakeArtifact(documents, "Letter.jpg", "A Letter");
        MakeArtifact(documents, "Memo.jpg", "A Memo");

        var vm = new MainWindowViewModel { DirectoryRoot = _root, WatchDebounceMs = 50 };
        vm.AskAboutOrphans = _ => Task.FromResult<IReadOnlyList<string>?>(null);

        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);

        Watch(vm);
        vm.AutoGenerate = true;
        await Task.Delay(300);

        var added = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(vm.StatusText) || added) return;
            if (!vm.StatusText.StartsWith("Generating site", StringComparison.Ordinal)) return;

            added = true;
            MakeArtifact(documents, "Postcard.jpg", "A Postcard");
        };

        // Invoked directly, the way a button press reaches it — not through RespondToChanges.
        await vm.GenerateSiteCommand.ExecuteAsync(null);

        var arrived = await Until(() => File.Exists(SitePath("Documents", "Postcard", "index.html")));

        Assert.True(added, "the test never managed to add the photo mid-run");
        Assert.True(arrived, "the photo added during a manual Generate never got a page");
    }

    [AvaloniaFact]
    public async Task RescanBringsWatchingBackAfterItStops()
    {
        // The warning tells the user to press Rescan. Rescan runs LoadDirectory, which reads the
        // folder and touches nothing to do with watching — so following the instruction refreshed
        // the tree and left watching just as dead, with nothing on screen to say so.
        var photos = Directory.CreateDirectory(At("Photographs")).FullName;
        MakeArtifact(photos, "Portrait.jpg", "A Portrait");
        MakeArtifact(photos, "Landscape.jpg", "A Landscape");

        var vm = new MainWindowViewModel { DirectoryRoot = _root, WatchDebounceMs = 50 };
        vm.AskAboutOrphans = _ => Task.FromResult<IReadOnlyList<string>?>(null);

        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);

        Watch(vm);
        vm.AutoGenerate = true;
        await Task.Delay(300);

        vm.PretendWatchingStopped();

        // The warning is posted to the dispatcher, so it has to be pumped before the Rescan sees it.
        Dispatcher.UIThread.RunJobs();

        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await Task.Delay(300);

        MakeArtifact(photos, "Sunset.jpg", "A Sunset");

        var arrived = await Until(() => File.Exists(SitePath("Photographs", "Sunset", "index.html")));

        Assert.True(arrived, "Rescan did not put watching back, so the new photo never arrived");
    }

    /// <summary>
    /// Rescanning a folder that has gone must leave the last good view of the project alone.
    /// </summary>
    /// <remarks>
    /// This is the state the "stopped watching" warning is reported in — the folder was renamed,
    /// moved, or is on a volume that went away — and Rescan is the natural thing to try next. It
    /// opened by emptying the tree and then replacing the loaded config with a scaffolded default,
    /// so pressing it turned a folder that was temporarily unreachable into an empty window and a
    /// settings panel holding somebody else's defaults. Reconnect the drive, touch any setting, and
    /// those defaults are what gets written over the real config.
    ///
    /// Nothing is recoverable from that by rescanning again, so the scan has to refuse.
    /// </remarks>
    [AvaloniaFact]
    public async Task RescanningAFolderThatHasGone_KeepsTheTreeAndTheConfig()
    {
        var photos = Directory.CreateDirectory(At("Photographs")).FullName;
        MakeArtifact(photos, "Portrait.jpg", "A Portrait");
        File.WriteAllText(At("dir2site.yaml"), "title: Riverbend\nfooter: © 2026\n");

        var vm = new MainWindowViewModel { DirectoryRoot = _root };
        vm.AskAboutOrphans = _ => Task.FromResult<IReadOnlyList<string>?>(null);

        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.DirItems);
        Assert.Equal("Riverbend", vm.Dir2SiteConfig!.Title);

        // The folder goes — renamed in Finder, or a volume unmounted.
        var moved = _root + "-moved";
        Directory.Move(_root, moved);
        try
        {
            await vm.LoadDirectoryCommand.ExecuteAsync(null);

            Assert.NotEmpty(vm.DirItems);
            Assert.Equal("Riverbend", vm.Dir2SiteConfig!.Title);
        }
        finally
        {
            Directory.Move(moved, _root);
        }
    }

    /// <summary>
    /// Generating with the project folder gone must not recreate it.
    /// </summary>
    /// <remarks>
    /// The other half of the guard on <c>LoadDirectory</c>, and the one that does something rather
    /// than merely showing nothing: <c>SiteGenerator.Generate</c> opens with a
    /// <c>CreateDirectory</c> of <c>_site</c>, which builds every missing segment on the way — the
    /// project folder included. So pressing Generate after Rescan had just reported it could not
    /// read the folder left a phantom at the old path holding a complete site, and said "Site
    /// generated" directly underneath "Nothing has been changed".
    /// </remarks>
    [AvaloniaFact]
    public async Task GeneratingWithTheFolderGone_DoesNotRecreateIt()
    {
        var photos = Directory.CreateDirectory(At("Photographs")).FullName;
        MakeArtifact(photos, "Portrait.jpg", "A Portrait");

        var vm = new MainWindowViewModel { DirectoryRoot = _root };
        vm.AskAboutOrphans = _ => Task.FromResult<IReadOnlyList<string>?>(null);

        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);

        var moved = _root + "-moved";
        Directory.Move(_root, moved);
        try
        {
            await vm.LoadDirectoryCommand.ExecuteAsync(null);
            await vm.GenerateSiteCommand.ExecuteAsync(null);

            Assert.False(Directory.Exists(_root), "Generate recreated the project folder");
        }
        finally
        {
            Directory.Move(moved, _root);
        }
    }

    [AvaloniaFact]
    public async Task TheScanWritingYaml_DoesNotSetTheWatcherOffForever()
    {
        // The hazard of watching a folder you also write to. Scanning brings sidecars up to the
        // current key set, and those writes land in the folder being watched — so without the guard
        // the app would scan, write, notice its own write, and scan again without end.
        var photos = Directory.CreateDirectory(At("Photographs")).FullName;

        // Deliberately sparse sidecars, so the scan has keys to add and really does write.
        File.WriteAllText(Path.Combine(photos, "Portrait.jpg"), "jpeg");
        File.WriteAllText(Path.Combine(photos, "Portrait.jpg.yaml"), "type: photo\n");
        File.WriteAllText(Path.Combine(photos, "Landscape.jpg"), "jpeg");
        File.WriteAllText(Path.Combine(photos, "Landscape.jpg.yaml"), "type: photo\n");

        // A configured footer, which is what made this test pass while the app looped. footerItems
        // is the only setting written as a block rather than a scalar, and the block path rewrote
        // the file whether or not it had changed — so every project with a footer rebuilt forever,
        // and every fixture in this branch built a config without one. Twenty-two rebuilds in
        // twelve seconds, against the four this asserts.
        File.WriteAllText(At("dir2site.yaml"),
            "title: Riverbend\nfooterItems:\n- column: 1\n  title: Contact\n  link: /contact/\n");

        var vm = new MainWindowViewModel { DirectoryRoot = _root, AutoGenerate = true };
        vm.AskAboutOrphans = _ => Task.FromResult<IReadOnlyList<string>?>(null);

        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        Watch(vm);
        await Task.Delay(300);

        // One real change to start things off.
        File.WriteAllText(Path.Combine(photos, "Portrait.jpg"), "a different jpeg");

        var scans = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsLoading) && vm.IsLoading) scans++;
        };

        // Long enough for several debounce windows to pass, so a loop would have shown itself.
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }

        // A couple of settling passes are fine — the yaml writers are idempotent, so it converges.
        // What must not happen is it never stopping.
        Assert.InRange(scans, 0, 4);
    }
}
