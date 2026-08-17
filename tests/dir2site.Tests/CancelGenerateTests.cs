// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using dir2site.Models;
using dir2site.Services;
using dir2site.ViewModels;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Stopping a generate part-way, and what a half-finished run must not then claim.
/// </summary>
/// <remarks>
/// The saving is obvious and the danger is not. The orphan sweep takes away everything in
/// <c>_site</c> the run did not claim, and a run stopped half way has claimed only the pages it
/// reached — so the naive reading of "cancel" proposes deleting most of the site. These are mostly
/// about that.
/// </remarks>
public class CancelGenerateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-cancel-" + Guid.NewGuid().ToString("N"));

    public CancelGenerateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);
    private string SitePath(params string[] parts) => Path.Combine([_root, "_site", .. parts]);

    private static Dir2SiteModel Config() => new() { Title = "My Site", Footer = "© 2026" };

    private static void MakeArtifact(string folder, string fileName, string caption)
    {
        File.WriteAllText(Path.Combine(folder, fileName), "not really a jpeg");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"type: photo\ncaption: {caption}\n");
    }

    /// <summary>Enough pages that a generate outlasts the moment we ask it to stop.</summary>
    private void MakeProject(int photos = 120)
    {
        var folder = Directory.CreateDirectory(At("Photographs")).FullName;
        for (var i = 0; i < photos; i++) MakeArtifact(folder, $"Photo{i:D3}.jpg", $"Photo {i}");

        var documents = Directory.CreateDirectory(At("Documents")).FullName;
        MakeArtifact(documents, "Letter.jpg", "A Letter");
        MakeArtifact(documents, "Memo.jpg", "A Memo");
    }

    private IReadOnlyList<string> PagesInSite() =>
        Directory.Exists(SitePath())
            ? [.. Directory.GetFiles(SitePath(), "index.html", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)]
            : [];

    // ---- the generator's own contract ---------------------------------------

    [AvaloniaFact]
    public void ACancelledRunThrowsRatherThanReturningWhatItHad()
    {
        // The whole safety argument in one assertion. A run that returned its partial result would
        // hand back an orphan list naming every page it hadn't reached yet, and the caller has no
        // way to tell that from a real one.
        MakeProject(photos: 4);
        var tree = DirectoryTraverser.BuildTree(_root, [], []);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            SiteGenerator.Generate(_root, tree, Config(), null, null, cancelled.Token));
    }

    [AvaloniaFact]
    public void ACancelledScanThrowsRatherThanReturningHalfATree()
    {
        // Same reasoning one stage earlier: a partial tree is indistinguishable from a project that
        // has lost most of its files, and everything downstream believes the walk.
        MakeProject(photos: 4);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            DirectoryTraverser.BuildTree(_root, [], [], null, null, cancelled.Token));
    }

    // ---- what the window does with it ---------------------------------------

    private async Task<MainWindowViewModel> BuiltProject()
    {
        var vm = new MainWindowViewModel { DirectoryRoot = _root };
        vm.AskAboutOrphans = _ => Task.FromResult<IReadOnlyList<string>?>(null);

        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);
        return vm;
    }

    /// <summary>Runs a generate and cancels it once it is past the scan and into writing pages.</summary>
    private static async Task CancelMidGenerate(MainWindowViewModel vm)
    {
        var cancelled = false;
        void Watch(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (cancelled || e.PropertyName != nameof(vm.StatusText)) return;
            if (!vm.StatusText.StartsWith("Generating ", StringComparison.Ordinal)) return;

            cancelled = true;
            vm.CancelGenerateCommand.Execute(null);
        }

        vm.PropertyChanged += Watch;
        try { await vm.GenerateSiteCommand.ExecuteAsync(null); }
        finally { vm.PropertyChanged -= Watch; }

        Assert.True(cancelled, "the generate finished before the test could cancel it");
    }

    [AvaloniaFact]
    public async Task CancellingLeavesEveryPageStanding()
    {
        // The failure this exists to catch: a half-finished run sweeping the site it didn't finish.
        MakeProject();
        var vm = await BuiltProject();
        var before = PagesInSite();

        // A change, so the run has something to do rather than finding everything current.
        File.WriteAllText(At("Photographs", "Photo000.jpg.yaml"), "type: photo\ncaption: Changed\n");

        await CancelMidGenerate(vm);

        Assert.Equal(before, PagesInSite());
    }

    [AvaloniaFact]
    public async Task CancellingAsksAboutNothing()
    {
        MakeProject();
        var vm = await BuiltProject();

        var asked = 0;
        vm.AskAboutOrphans = orphans =>
        {
            asked++;
            return Task.FromResult<IReadOnlyList<string>?>(null);
        };

        File.WriteAllText(At("Photographs", "Photo000.jpg.yaml"), "type: photo\ncaption: Changed\n");
        await CancelMidGenerate(vm);

        Assert.Equal(0, asked);
        Assert.Empty(vm.PendingSiteOrphans);
    }

    [AvaloniaFact]
    public async Task CancellingIsNotFailing()
    {
        MakeProject();
        var vm = await BuiltProject();

        File.WriteAllText(At("Photographs", "Photo000.jpg.yaml"), "type: photo\ncaption: Changed\n");
        await CancelMidGenerate(vm);

        Assert.Equal("Generate cancelled", vm.StatusText);
        Assert.DoesNotContain("Generate failed", vm.StatusText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task CancellingGivesUpTheAccountOfTheProject()
    {
        // ApplySourceChanges has already carried this batch through to _site and cleared it, and the
        // pages that explains were only partly written. Leaving the flag standing would let the next
        // run act on an account that no longer describes anything.
        MakeProject();
        var vm = await BuiltProject();
        Assert.True(vm.SiteIsAccountedFor, "a completed generate should account for the site");

        File.WriteAllText(At("Photographs", "Photo000.jpg.yaml"), "type: photo\ncaption: Changed\n");
        await CancelMidGenerate(vm);

        Assert.False(vm.SiteIsAccountedFor);
    }

    [AvaloniaFact]
    public async Task TheAppComesBackAfterwards()
    {
        // IsLoading gates every button on the window, so a cancel that left it set would be
        // indistinguishable from a hang.
        MakeProject();
        var vm = await BuiltProject();

        File.WriteAllText(At("Photographs", "Photo000.jpg.yaml"), "type: photo\ncaption: Changed\n");
        await CancelMidGenerate(vm);

        Assert.False(vm.IsLoading);
        Assert.False(vm.IsGenerating);
        Assert.False(vm.CancelGenerateCommand.CanExecute(null));

        // And a generate still works after one was stopped.
        await vm.GenerateSiteCommand.ExecuteAsync(null);
        Assert.True(File.Exists(SitePath("Documents", "Letter", "index.html")));
    }

    [AvaloniaFact]
    public void CancelIsOfferedOnlyWhileAGenerateIsRunning()
    {
        var vm = new MainWindowViewModel { DirectoryRoot = _root };
        Assert.False(vm.CancelGenerateCommand.CanExecute(null));
    }
}
