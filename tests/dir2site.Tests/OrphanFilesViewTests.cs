// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using dir2site.ViewModels;
using dir2site.Views;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The dialog a generate raises when it finds files in _site that nothing asks for any more.
/// Deliberately not the remote stale-files dialog: these are generated files that come back on the
/// next generate, so the choice is pre-made rather than opted into.
/// </summary>
public class OrphanFilesViewTests
{
    private static (OrphanFilesView view, OrphanFilesViewModel vm) Show(params string[] orphans)
    {
        var view = new OrphanFilesView(orphans);
        view.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, (OrphanFilesViewModel)view.DataContext!);
    }

    [AvaloniaFact]
    public void EveryLeftoverIsListedAndAlreadyTicked()
    {
        var (view, vm) = Show("Photographs/1890s/index.html", "Photographs/1890s/Portrait/index.html");

        Assert.Equal(2, vm.Items.Count);
        // Removing them is the outcome someone reaches this dialog wanting; nothing here is
        // unrecoverable, unlike deleting on the server, so it doesn't ask twice.
        Assert.All(vm.Items, item => Assert.True(item.IsSelected));
        Assert.Contains("2 files", vm.Headline);

        var items = view.GetVisualDescendants().OfType<ItemsControl>().First();
        Assert.Equal(2, items.ItemCount);
    }

    [AvaloniaFact]
    public void ASidecarIsOfferedButNotTicked()
    {
        // A sidecar holds a caption, credit and date somebody typed. If the artifact was renamed
        // rather than deleted, it is the only surviving copy of them — the new file gets a
        // scaffolded one with the filename as its caption and nothing else. Removing it is still
        // offered, because after a real deletion it is clutter; it just isn't the default.
        var view = new OrphanFilesView(
            ["Photographs/Portrait.jpg.yaml", "Photographs/.dir2site/Portrait"],
            OrphanKind.Source);
        var vm = (OrphanFilesViewModel)view.DataContext!;

        var sidecar = vm.Items.Single(i => i.Path.EndsWith(".yaml", StringComparison.Ordinal));
        var previews = vm.Items.Single(i => !i.Path.EndsWith(".yaml", StringComparison.Ordinal));

        Assert.False(sidecar.IsSelected);

        // Previews are pure output — we can make those again, so they keep the old default.
        Assert.True(previews.IsSelected);
    }

    [AvaloniaFact]
    public void RemovingReturnsOnlyWhatIsStillTicked()
    {
        var (_, vm) = Show("leftover.html", "old/index.html", "_media/gone.png");

        Assert.False(vm.Decided);
        vm.Items[1].IsSelected = false;
        vm.RemoveSelectedCommand.Execute(null);

        Assert.True(vm.Decided);
        Assert.Equal<IReadOnlyList<string>>(["leftover.html", "_media/gone.png"], vm.Chosen!);
    }

    [AvaloniaFact]
    public void KeepingThemReturnsNothing()
    {
        var (_, vm) = Show("leftover.html");

        vm.KeepCommand.Execute(null);

        Assert.True(vm.Decided);
        Assert.Null(vm.Chosen);
    }

    /// <summary>
    /// Unticking everything and pressing Remove means the same thing as keeping them — the caller
    /// must not read an empty list as "remove all".
    /// </summary>
    [AvaloniaFact]
    public void RemovingWithNothingTickedIsTheSameAsKeepingThem()
    {
        var (_, vm) = Show("leftover.html", "old/index.html");

        vm.DeselectAllCommand.Execute(null);
        vm.RemoveSelectedCommand.Execute(null);

        Assert.True(vm.Decided);
        Assert.Null(vm.Chosen);
    }
}
