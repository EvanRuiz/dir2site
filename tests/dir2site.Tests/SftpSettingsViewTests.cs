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
using dir2site.SftpSync.Ui;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Exercises the settings dialog through its real XAML, so a binding that doesn't resolve — a
/// renamed property, a missing converter — fails here rather than showing up as a control that
/// silently never appears.
/// </summary>
public class SftpSettingsViewTests : IDisposable
{
    private readonly string _project = Path.Combine(
        Path.GetTempPath(), "d2s-dlg-" + Guid.NewGuid().ToString("N"));

    public SftpSettingsViewTests() => Directory.CreateDirectory(_project);

    public void Dispose()
    {
        try { Directory.Delete(_project, recursive: true); } catch { }
    }

    // The view is itself a Window, and its real constructor wires up the view model exactly as
    // the app does — so these tests exercise the production path, not a stand-in.
    private (SftpSettingsView view, SftpSettingsViewModel vm) Show()
    {
        var view = new SftpSettingsView(
            _project, new Dir2SiteModel(), Path.Combine(_project, "dir2site.yaml"));
        view.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, (SftpSettingsViewModel)view.DataContext!);
    }

    /// <summary>Opens the Advanced expander — a collapsed one doesn't build its content.</summary>
    private static void Expand(Visual root)
    {
        root.GetVisualDescendants().OfType<Expander>().First().IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
    }

    private static T Find<T>(Visual root, string text) where T : ContentControl =>
        root.GetVisualDescendants().OfType<T>().First(c => (c.Content as string) == text);

    private static bool Exists<T>(Visual root, string text) where T : ContentControl =>
        root.GetVisualDescendants().OfType<T>().Any(c => (c.Content as string) == text);

    [AvaloniaFact]
    public void TheDialogOpens_AndBindsToItsViewModel()
    {
        var (view, vm) = Show();

        vm.Host = "127.0.0.1";
        Dispatcher.UIThread.RunJobs();

        var hostBox = view.GetVisualDescendants().OfType<TextBox>()
                          .First(t => t.Watermark == "sftp.example.com");
        Assert.Equal("127.0.0.1", hostBox.Text);
    }

    [AvaloniaFact]
    public void CreateRemoteFolder_IsHiddenUntilATestFindsThePathMissing()
    {
        var (view, vm) = Show();
        Dispatcher.UIThread.RunJobs();

        var button = Find<Button>(view, "Create remote folder");
        Assert.False(button.IsEffectivelyVisible);

        vm.CanCreateRemotePath = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(button.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void HostKeyRow_ShowsNotTrusted_AndHidesForgetUntilOneIsPinned()
    {
        var (view, vm) = Show();
        Expand(view);

        var fingerprint = view.GetVisualDescendants().OfType<SelectableTextBlock>()
                              .First(t => t.Text == "Not yet trusted");
        Assert.NotNull(fingerprint);
        Assert.False(Find<Button>(view, "Forget").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void AdvancedSettings_AreCollapsedUntilAskedFor()
    {
        var (view, _) = Show();

        // Manifest path and the host key belong behind Advanced; the essentials shouldn't compete
        // with them for attention. A collapsed expander doesn't build its content at all.
        Assert.Empty(view.GetVisualDescendants().OfType<SelectableTextBlock>()
                         .Where(t => t.Text == "Not yet trusted"));

        Expand(view);

        Assert.NotEmpty(view.GetVisualDescendants().OfType<SelectableTextBlock>()
                            .Where(t => t.Text == "Not yet trusted"));
    }

    [AvaloniaFact]
    public void KeyAuthFields_AppearOnlyWhenKeyAuthIsChosen()
    {
        var (view, vm) = Show();
        Dispatcher.UIThread.RunJobs();

        var browse = Find<Button>(view, "Browse…");
        Assert.False(browse.IsEffectivelyVisible);          // password auth is the default

        vm.IsKeyAuth = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(browse.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void ForgettingAPinnedKey_UpdatesWhatTheDialogShows()
    {
        var (view, vm) = Show();

        Assert.False(vm.HasPinnedHostKey);
        Assert.Equal("Not yet trusted", vm.HostKeyFingerprintDisplay);

        vm.ForgetHostKeyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("forgotten", vm.Status);
    }

    [AvaloniaFact]
    public void ChangingTheHost_ClearsAnyPendingCreateOffer()
    {
        var (view, vm) = Show();
        vm.CanCreateRemotePath = true;

        // A different server says nothing about the old server's path.
        vm.Host = "somewhere.else.invalid";

        Assert.False(vm.CanCreateRemotePath);
    }
}
