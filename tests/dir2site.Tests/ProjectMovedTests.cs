// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using dir2site.ViewModels;
using dir2site.Views;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// What happens when the project folder itself is renamed, moved or deleted.
/// </summary>
/// <remarks>
/// A question rather than a rule, because all three answers are reasonable and the app cannot know
/// which is meant: a folder renamed to tidy it up wants following, one renamed by accident wants
/// putting back, one deleted on purpose wants closing.
/// </remarks>
public class ProjectMovedTests : IDisposable
{
    private readonly string _base = Path.Combine(
        Path.GetTempPath(), "d2s-moved-" + Guid.NewGuid().ToString("N"));

    private readonly List<MainWindowViewModel> _watching = [];
    private readonly string _root;

    public ProjectMovedTests()
    {
        _root = Path.Combine(_base, "riverbend");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var vm in _watching) vm.StopWatching();
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private static void MakeArtifact(string folder, string fileName, string caption)
    {
        File.WriteAllText(Path.Combine(folder, fileName), "not really a jpeg");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"type: photo\ncaption: {caption}\n");
    }

    /// <summary>A watched project, built once, with the dialog answered by <paramref name="answer"/>.</summary>
    private async Task<MainWindowViewModel> Watching(ProjectMovedAnswer? answer)
    {
        var photos = Directory.CreateDirectory(At("Photographs")).FullName;
        MakeArtifact(photos, "Portrait.jpg", "A Portrait");
        MakeArtifact(photos, "Landscape.jpg", "A Landscape");

        var vm = new MainWindowViewModel { DirectoryRoot = _root, WatchDebounceMs = 100 };
        vm.AskAboutOrphans = _ => Task.FromResult<IReadOnlyList<string>?>(null);
        vm.AskAboutMovedProject = (_, _) => Task.FromResult(answer);

        _watching.Add(vm);
        vm.StartWatching();
        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);
        vm.AutoGenerate = true;
        await Task.Delay(400);

        return vm;
    }

    private static async Task<bool> Until(Func<bool> done, int seconds = 12)
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

    // ---- the dialog's own behaviour -----------------------------------------

    private static ProjectMovedViewModel Show(string oldPath, string? newPath)
    {
        var view = new ProjectMovedView(oldPath, newPath);
        view.Show();
        Dispatcher.UIThread.RunJobs();
        return (ProjectMovedViewModel)view.DataContext!;
    }

    [AvaloniaFact]
    public void FollowingIsOfferedOnlyWhenWeKnowWhereItWent()
    {
        // A rename hands over the new name; a delete does not. Offering to follow a folder nobody
        // can point at would be offering something the app cannot do.
        Assert.True(Show(_root, Path.Combine(_base, "riverbend-2024")).CanFollow);
        Assert.False(Show(_root, null).CanFollow);
    }

    [AvaloniaFact]
    public void TryAgainStaysOpenUntilTheFolderIsBack()
    {
        // The one answer the app can check, so it does. Closing on the press and finding out later
        // would put the user back in front of a window pointed at nothing, with no way to tell that
        // from having been believed.
        var missing = Path.Combine(_base, "not-here");
        var vm = Show(missing, null);

        vm.TryAgainCommand.Execute(null);
        Assert.Null(vm.Chosen);
        Assert.Contains(missing, vm.RetryMessage, StringComparison.Ordinal);

        Directory.CreateDirectory(missing);
        vm.TryAgainCommand.Execute(null);

        Assert.Equal(ProjectMovedAnswer.StayedPut, vm.Chosen);
    }

    [AvaloniaFact]
    public void TryAgainChecksAndNothingElse()
    {
        // Putting the folder back is not a request to rebuild. The dialog's job ends at confirming
        // it is there.
        var vm = Show(_root, null);
        vm.TryAgainCommand.Execute(null);

        Assert.Equal(ProjectMovedAnswer.StayedPut, vm.Chosen);
        Assert.False(Directory.Exists(Path.Combine(_root, "_site")),
            "the dialog must not have generated anything");
    }

    // ---- what the window does with the answer -------------------------------

    [AvaloniaFact]
    public async Task FollowingTakesTheProjectToItsNewHome()
    {
        var vm = await Watching(ProjectMovedAnswer.Follow);
        var moved = Path.Combine(_base, "riverbend-2024");

        Directory.Move(_root, moved);

        Assert.True(await Until(() => vm.DirectoryRoot == moved),
            "the project never followed its folder");
        Assert.True(await Until(() => vm.DirItems.Count > 0), "the tree never reloaded");
    }

    [AvaloniaFact]
    public async Task ClosingGoesBackToTheLaunchScreen()
    {
        // An empty DirectoryRoot is what the window watches to show the welcome panel, so this is
        // the whole of "closed".
        var vm = await Watching(ProjectMovedAnswer.Close);

        Directory.Move(_root, Path.Combine(_base, "riverbend-2024"));

        Assert.True(await Until(() => string.IsNullOrEmpty(vm.DirectoryRoot)),
            "the project was never closed");
        Assert.Empty(vm.DirItems);
        Assert.False(vm.AutoGenerate);
    }

    [AvaloniaFact]
    public async Task DismissingStopsTheUnattendedPart()
    {
        // Nobody answered and the folder is still gone. Whatever else carries on, the part that
        // works without being asked must not.
        var vm = await Watching(answer: null);

        Directory.Move(_root, Path.Combine(_base, "riverbend-2024"));

        Assert.True(await Until(() => !vm.AutoGenerate), "auto-generate was left running");
        Assert.Equal(_root, vm.DirectoryRoot);
    }

    [AvaloniaFact]
    public async Task ComingBackDoesNotMeanNothingHappenedWhileItWasAway()
    {
        // The folder being away is a window nothing was watching — on macOS a watcher whose root
        // goes raises no error and delivers nothing, so a photo deleted while it was gone leaves no
        // trace at all. Keeping the belief that the site is accounted for would let the next run
        // narrow against an account that stops describing the folder part way through, and leave a
        // card pointing at a page the sweep then offers to delete.
        var photos = Directory.CreateDirectory(At("Photographs")).FullName;
        MakeArtifact(photos, "Portrait.jpg", "A Portrait");
        MakeArtifact(photos, "Landscape.jpg", "A Landscape");

        var vm = new MainWindowViewModel { DirectoryRoot = _root, WatchDebounceMs = 100 };
        vm.AskAboutOrphans = _ => Task.FromResult<IReadOnlyList<string>?>(null);
        var asked = 0;
        vm.AskAboutMovedProject = (_, _) =>
        {
            asked++;
            return Task.FromResult<ProjectMovedAnswer?>(ProjectMovedAnswer.StayedPut);
        };

        _watching.Add(vm);
        vm.StartWatching();
        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);
        Assert.True(vm.SiteIsAccountedFor, "a completed generate should account for the site");

        // The platform needs a moment to establish the watch on the folder above, or the move races
        // it and nothing is reported — which reads as the app failing to notice rather than as the
        // test moving first.
        await Task.Delay(400);

        // Away, changed behind our back, and back again — with the departure given time to be
        // reported. Renaming away and back inside the same instant leaves no net change at the path,
        // and the platform delivers nothing at all: a race the test would win and a user never could.
        var parked = Path.Combine(_base, "parked");
        Directory.Move(_root, parked);
        Assert.True(await Until(() => asked > 0), "the app never noticed the folder go");

        File.Delete(Path.Combine(parked, "Photographs", "Landscape.jpg"));
        File.Delete(Path.Combine(parked, "Photographs", "Landscape.jpg.yaml"));
        Directory.Move(parked, _root);

        Assert.True(await Until(() => !vm.SiteIsAccountedFor),
            "the app still claims to know what happened to a folder it could not see");
    }

    [AvaloniaFact]
    public async Task ADeletedFolderAsksTheSameQuestion()
    {
        // The dialog is the same one; only the option that has nothing to point at is missing.
        string? offeredNewPath = "not asked";
        var vm = await Watching(ProjectMovedAnswer.Close);
        vm.AskAboutMovedProject = (_, newPath) =>
        {
            offeredNewPath = newPath;
            return Task.FromResult<ProjectMovedAnswer?>(ProjectMovedAnswer.Close);
        };

        Directory.Delete(_root, recursive: true);

        Assert.True(await Until(() => string.IsNullOrEmpty(vm.DirectoryRoot)),
            "a deleted folder never raised the question");
        Assert.Null(offeredNewPath);
    }

    [AvaloniaFact]
    public async Task TheFolderMovingStopsWhateverIsRunning()
    {
        // Every one of the three answers makes a generate in flight wrong: it is working from a tree
        // that no longer describes anything. Letting it finish would write a site for a project
        // that moved out from under it, and then report success.
        var photos = Directory.CreateDirectory(At("Photographs")).FullName;
        for (var i = 0; i < 120; i++) MakeArtifact(photos, $"Photo{i:D3}.jpg", $"Photo {i}");

        var vm = new MainWindowViewModel { DirectoryRoot = _root, WatchDebounceMs = 100 };
        vm.AskAboutOrphans = _ => Task.FromResult<IReadOnlyList<string>?>(null);
        vm.AskAboutMovedProject = (_, _) => Task.FromResult<ProjectMovedAnswer?>(null);

        _watching.Add(vm);
        vm.StartWatching();
        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await Task.Delay(400);   // as above: let the watch be established before moving anything

        var moved = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (moved || e.PropertyName != nameof(vm.StatusText)) return;
            if (!vm.StatusText.StartsWith("Generating ", StringComparison.Ordinal)) return;

            moved = true;
            Directory.Move(_root, Path.Combine(_base, "riverbend-2024"));
        };

        // Started rather than awaited, and pumped while it runs: the watcher posts to the UI thread,
        // and a test that simply awaits the generate never lets that post through — so the cancel would
        // arrive after the run it was meant to stop.
        var generate = vm.GenerateSiteCommand.ExecuteAsync(null);
        await Until(() => generate.IsCompleted, seconds: 30);
        await generate;
        await Until(() => !vm.IsGenerating && !vm.IsLoading);

        Assert.True(moved, "the test never managed to move the folder mid-generate");
        Assert.False(vm.IsGenerating);
        Assert.DoesNotContain("Site generated", vm.StatusText, StringComparison.Ordinal);

        // Deliberately not asserting that the folder stayed gone, though it nearly always does.
        // Every guard against recreating it is a check followed by a create, and a hundred preview
        // jobs are already in flight when the folder moves — so one that checked a moment before it
        // went will still create afterwards. "Nearly always" is the honest description and not
        // something to hang a suite on.
        //
        // What is deterministic is pinned as such: CancelGenerateTests holds both the generate and the
        // preview stage to refusing a folder that has gone, with no race in sight. This test is
        // about the stopping, which is what its name says.
    }
}
