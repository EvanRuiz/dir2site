// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using System.Threading.Tasks;
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
/// The update banners and the restart prompt. Velopack is never initialised in the test host, so
/// the view model's UpdateManager is null and no network call fires — the banners are driven purely
/// by flipping the observable flags.
/// </summary>
public class UpdateBannerTests
{
    private static (MainWindow window, MainWindowViewModel vm) Show()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static Button Button(Visual root, string content) =>
        root.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == content);

    [AvaloniaFact]
    public void TheDownloadStepIsLabelledUpdateNow()
    {
        var (window, vm) = Show();
        vm.UpdateVersion = "1.4.0";
        vm.UpdateAvailable = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(Button(window, "Update Now").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void DecliningTheRestartLeavesTheInstallBannerAvailable()
    {
        // The prompt is the normal path, but UpdateReady stays set either way so the banner
        // remains as the fallback for someone who said "Later".
        var (window, vm) = Show();
        vm.UpdateVersion = "1.4.0";
        vm.UpdateReady = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(Button(window, "Restart & Install").IsEffectivelyVisible);
    }

    /// <summary>
    /// The VS Code extension updates through the same banners as the app, and only when there is
    /// something to update — see VsCodeExtensionDetectionTests for what decides that.
    /// </summary>
    [AvaloniaFact]
    public async Task TheExtensionUpdateBannerFollowsItsFlag()
    {
        var (window, vm) = Show();

        // The startup scan reads the real machine; let it land before asserting on the flag.
        await vm.VsCodeExtensionStateReady;
        Dispatcher.UIThread.RunJobs();

        vm.VsCodeExtensionUpdateAvailable = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(Button(window, "Update Extension").IsEffectivelyVisible);

        // The banner names the version on offer, as the app's own banners do.
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text != null && t.Text.Contains($"extension update available: v{vm.VsCodeExtensionVersion}"));

        vm.VsCodeExtensionUpdateAvailable = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(Button(window, "Update Extension").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void RestartNowAndLater_ReportDifferentAnswers()
    {
        var restartView = new UpdateConfirmView("1.4.0");
        restartView.Show();
        Dispatcher.UIThread.RunJobs();
        var restartVm = (UpdateConfirmViewModel)restartView.DataContext!;
        Assert.Null(restartVm.Answer);              // nothing decided yet
        restartVm.RestartCommand.Execute(null);
        Assert.True(restartVm.Answer);

        var laterView = new UpdateConfirmView("1.4.0");
        laterView.Show();
        Dispatcher.UIThread.RunJobs();
        var laterVm = (UpdateConfirmViewModel)laterView.DataContext!;
        laterVm.LaterCommand.Execute(null);
        Assert.False(laterVm.Answer);
    }

    [AvaloniaFact]
    public void ThePromptNamesTheVersionBeingInstalled()
    {
        var view = new UpdateConfirmView("1.4.0");
        view.Show();
        Dispatcher.UIThread.RunJobs();

        var vm = (UpdateConfirmViewModel)view.DataContext!;
        Assert.Contains("1.4.0", vm.Title);
        Assert.Contains("1.4.0", vm.Explanation);
    }
}
