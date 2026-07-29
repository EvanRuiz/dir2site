// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using dir2site.Models;
using dir2site.ViewModels;
using dir2site.Views;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The deploy row's Cancel button and blocked-reason label are pure XAML bindings, so nothing in
/// C# would fail if they were wrong — the control would simply never appear. These drive the real
/// MainWindow to check they respond.
/// </summary>
public class DeployRowTests : IDisposable
{
    private readonly string _project = Path.Combine(
        Path.GetTempPath(), "d2s-ui-" + Guid.NewGuid().ToString("N"));

    public DeployRowTests() => Directory.CreateDirectory(_project);

    public void Dispose()
    {
        try { Directory.Delete(_project, recursive: true); } catch { }
    }

    // The deploy row only exists once a project is open — the welcome panel is shown otherwise.
    private (MainWindow window, MainWindowViewModel vm) ShowWithProject()
    {
        var vm = new MainWindowViewModel
        {
            DirectoryRoot = _project,
            Dir2SiteConfig = new Dir2SiteModel { Title = "Test" },
        };
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static Button Button(Visual root, string content) =>
        root.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == content);

    private static TextBlock? Label(Visual root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == text);

    [AvaloniaFact]
    public void Cancel_IsHiddenUntilASyncIsRunning()
    {
        var (window, vm) = ShowWithProject();

        var cancel = Button(window, "Cancel");
        Assert.False(cancel.IsEffectivelyVisible);

        vm.IsSyncing = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(cancel.IsEffectivelyVisible);
        Assert.True(cancel.IsEffectivelyEnabled);   // CanCancelSync follows IsSyncing
    }

    [AvaloniaFact]
    public void CancelCommand_RequestsCancellation()
    {
        var (window, vm) = ShowWithProject();
        vm.IsSyncing = true;
        Dispatcher.UIThread.RunJobs();

        Button(window, "Cancel").Command!.Execute(null);

        Assert.Equal("Cancelling…", vm.StatusText);
    }

    [AvaloniaFact]
    public void WithNoSiteGenerated_TheReasonIsShown_AndDeployIsDisabled()
    {
        var (window, vm) = ShowWithProject();

        Assert.Equal("Generate the site first — there is no _site folder to deploy.",
                     vm.SyncBlockedReason);
        Assert.False(Button(window, "Quick Sync").IsEffectivelyEnabled);
        Assert.False(Button(window, "Verify & Repair").IsEffectivelyEnabled);

        var label = Label(window, vm.SyncBlockedReason);
        Assert.NotNull(label);
        Assert.True(label!.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void OnceTheSiteExistsButNoProfileIsSet_TheReasonSaysSo()
    {
        Directory.CreateDirectory(Path.Combine(_project, "_site"));
        var (window, vm) = ShowWithProject();

        // Re-evaluate now that _site exists.
        vm.DirectoryRoot = _project + Path.DirectorySeparatorChar;
        vm.DirectoryRoot = _project;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("No SFTP profile configured. Use Configure… first.", vm.SyncBlockedReason);
        Assert.False(Button(window, "Quick Sync").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void TheReasonLabel_DisappearsWhenNothingIsBlocking()
    {
        var (window, vm) = ShowWithProject();
        var label = Label(window, vm.SyncBlockedReason);
        Assert.True(label!.IsEffectivelyVisible);

        vm.SyncBlockedReason = string.Empty;
        Dispatcher.UIThread.RunJobs();

        Assert.False(label.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void ConfigureIsAlwaysAvailable_SoTheUserCanFixWhateverIsBlocking()
    {
        var (window, _) = ShowWithProject();

        Assert.True(Button(window, "Configure…").IsEffectivelyEnabled);
    }
}
