// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using dir2site.ViewModels;
using dir2site.Views;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The last-chance prompt in front of a remote delete. Deleting files off a server is the one thing
/// in the app that nothing can undo, so what matters here is that only the explicit button says yes.
/// </summary>
public class ConfirmViewTests
{
    private static (ConfirmView view, ConfirmViewModel vm) Show(int count = 47)
    {
        var view = new ConfirmView(
            "Delete Remote Files",
            $"Permanently delete {count} file(s) on the server?",
            "They will be removed from example.com immediately.",
            $"Delete {count} File(s)");
        view.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, (ConfirmViewModel)view.DataContext!);
    }

    [AvaloniaFact]
    public void NothingIsDecidedUntilAButtonIsPressed()
    {
        var (_, vm) = Show();

        Assert.Null(vm.Answer);
    }

    [AvaloniaFact]
    public void ConfirmAndCancel_ReportDifferentAnswers()
    {
        var (_, confirmVm) = Show();
        confirmVm.ConfirmCommand.Execute(null);
        Assert.True(confirmVm.Answer);

        var (_, cancelVm) = Show();
        cancelVm.CancelCommand.Execute(null);
        Assert.False(cancelVm.Answer);
    }

    [AvaloniaFact]
    public void TheCountIsOnTheButtonAndInTheQuestion()
    {
        var (view, vm) = Show(count: 12);

        Assert.Contains("12", vm.Heading);
        var button = view.GetVisualDescendants().OfType<Button>()
                         .First(b => (b.Content as string) == "Delete 12 File(s)");
        Assert.True(button.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void CancelIsTheDefaultButton()
    {
        // Enter should back out, not delete.
        var (view, _) = Show();

        var cancel = view.GetVisualDescendants().OfType<Button>()
                         .First(b => (b.Content as string) == "Cancel");
        Assert.True(cancel.IsDefault);
    }
}
