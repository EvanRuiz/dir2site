// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using dir2site.SftpSync.Core;
using dir2site.SftpSync.Ui;
using Xunit;

namespace dir2site.Tests;

public class SyncPreviewViewTests
{
    private static (SyncPreviewView view, SyncPreviewViewModel vm) Show(SyncPlan plan)
    {
        var view = new SyncPreviewView(plan);
        view.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, (SyncPreviewViewModel)view.DataContext!);
    }

    [AvaloniaFact]
    public void ItListsEveryFileThatWouldBeUploaded()
    {
        var plan = new SyncPlan(["index.html", "css/site.css", "img/logo.png"], [], 4096, "");

        var (view, vm) = Show(plan);

        Assert.Equal(3, vm.Uploads.Count);
        Assert.Contains("3 files to upload", vm.Summary);
        Assert.Contains("4 KB", vm.Summary);

        var items = view.GetVisualDescendants().OfType<ItemsControl>().First();
        Assert.Equal(3, items.ItemCount);
    }

    [AvaloniaFact]
    public void StaleFilesAreMentionedButNotOfferedForDeletionHere()
    {
        var plan = new SyncPlan(["index.html"], ["old.html", "gone.html"], 100, "");

        var (_, vm) = Show(plan);

        Assert.True(vm.HasStale);
        Assert.Contains("2 file(s) on the server", vm.StaleHeading);
        // Removing them stays a separate, deliberate step.
        Assert.Contains("asked about removing them afterwards", vm.StaleHeading);
    }

    [AvaloniaFact]
    public void AnEmptyPlanCannotBeDeployed()
    {
        var (view, vm) = Show(new SyncPlan([], [], 0, ""));

        Assert.False(vm.CanDeploy);
        var deploy = view.GetVisualDescendants().OfType<Button>()
                         .First(b => (b.Content as string) == "Deploy");
        Assert.False(deploy.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void ConfirmAndCancel_ReportDifferentAnswers()
    {
        var plan = new SyncPlan(["index.html"], [], 10, "");

        var (_, confirmVm) = Show(plan);
        Assert.Null(confirmVm.Answer);          // nothing decided yet
        confirmVm.ConfirmCommand.Execute(null);
        Assert.True(confirmVm.Answer);

        var (_, cancelVm) = Show(plan);
        cancelVm.CancelCommand.Execute(null);
        Assert.False(cancelVm.Answer);
    }

    [AvaloniaFact]
    public void TheNoteIsShownWhenThereIsOne()
    {
        var (view, vm) = Show(new SyncPlan(["a.html"], [], 5, "(forced full upload)"));

        Assert.Equal("(forced full upload)", vm.Note);
        var note = view.GetVisualDescendants().OfType<TextBlock>()
                       .First(t => t.Text == "(forced full upload)");
        Assert.True(note.IsEffectivelyVisible);
    }
}
