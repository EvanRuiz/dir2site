// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Threading;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using dir2site.SftpSync.Ui;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Drives the browse dialog against the real SFTP server, so navigation is exercised end to end
/// rather than against a stub that agrees with whatever the code does.
/// </summary>
public class RemoteBrowseViewTests(SftpServerFixture fx) : IClassFixture<SftpServerFixture>
{
    private (RemoteBrowseView view, RemoteBrowseViewModel vm) Show(SftpServerFixture.Deployment d)
    {
        var view = new RemoteBrowseView(d.Profile, null, null);
        view.Show();
        var vm = (RemoteBrowseViewModel)view.DataContext!;
        PumpUntil(() => !vm.IsBusy && vm.CurrentPath.Length > 0, "the first listing");
        return (view, vm);
    }

    /// <summary>
    /// Listing runs on a background task, so the UI thread has to be pumped until the result lands.
    /// Waits on the condition rather than a fixed number of iterations: a fixed count passes on a
    /// fast machine and fails on a loaded CI runner, which is exactly what it did.
    /// </summary>
    private static void PumpUntil(Func<bool> done, string what, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (done()) return;
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
        if (!done()) throw new TimeoutException($"Timed out waiting for {what}.");
    }

    [AvaloniaFact]
    public void ItOpensOnTheProfilesPath_AndListsSubfolders()
    {
        if (!fx.Available) return;
        var d = fx.NewDeployment();
        Directory.CreateDirectory(Path.Combine(d.RemoteDir, "public_html"));
        Directory.CreateDirectory(Path.Combine(d.RemoteDir, "logs"));

        var (view, vm) = Show(d);

        Assert.Equal(d.Profile.RemotePath, vm.CurrentPath);
        Assert.Equal(["logs", "public_html"], vm.Directories);

        var list = view.GetVisualDescendants().OfType<ListBox>().First();
        Assert.Equal(2, list.ItemCount);
    }

    [AvaloniaFact]
    public void OpeningAFolder_DescendsIntoIt_AndUpComesBack()
    {
        if (!fx.Available) return;
        var d = fx.NewDeployment();
        Directory.CreateDirectory(Path.Combine(d.RemoteDir, "sites", "example.com"));

        var (_, vm) = Show(d);
        vm.SelectedDirectory = "sites";
        vm.OpenCommand.Execute(null);
        PumpUntil(() => vm.CurrentPath.EndsWith("/sites", StringComparison.Ordinal), "the descent");

        Assert.EndsWith("/sites", vm.CurrentPath);
        Assert.Equal(["example.com"], vm.Directories);

        vm.GoUpCommand.Execute(null);
        PumpUntil(() => vm.CurrentPath == d.Profile.RemotePath, "the way back up");

        Assert.Equal(d.Profile.RemotePath, vm.CurrentPath);
        Assert.Contains("sites", vm.Directories);
    }

    [AvaloniaFact]
    public void CreatingAFolder_ShowsItAndSelectsIt()
    {
        if (!fx.Available) return;
        var d = fx.NewDeployment();
        var (_, vm) = Show(d);

        vm.NewFolderName = "deploy-here";
        vm.NewFolderCommand.Execute(null);
        PumpUntil(() => vm.Directories.Contains("deploy-here"), "the new folder to appear");

        Assert.Contains("deploy-here", vm.Directories);
        Assert.Equal("deploy-here", vm.SelectedDirectory);
        Assert.True(Directory.Exists(Path.Combine(d.RemoteDir, "deploy-here")));
    }

    [AvaloniaFact]
    public void ChoosingWithAFolderSelected_ReturnsThatFolder()
    {
        if (!fx.Available) return;
        var d = fx.NewDeployment();
        Directory.CreateDirectory(Path.Combine(d.RemoteDir, "public_html"));

        var (_, vm) = Show(d);
        vm.SelectedDirectory = "public_html";

        vm.ChooseCommand.Execute(null);

        Assert.Equal(d.Profile.RemotePath.TrimEnd('/') + "/public_html", vm.ChosenPath);
    }

    [AvaloniaFact]
    public void ChoosingWithNothingSelected_ReturnsTheFolderBeingViewed()
    {
        if (!fx.Available) return;
        var d = fx.NewDeployment();

        var (_, vm) = Show(d);

        vm.ChooseCommand.Execute(null);

        Assert.Equal(d.Profile.RemotePath, vm.ChosenPath);
    }

    [AvaloniaFact]
    public void UpIsUnavailableAtTheRoot()
    {
        if (!fx.Available) return;
        var d = fx.NewDeployment();
        var (_, vm) = Show(d);

        Assert.True(vm.CanGoUp);   // a deployment dir is well below /

        while (vm.CanGoUp)
        {
            var from = vm.CurrentPath;
            vm.GoUpCommand.Execute(null);
            PumpUntil(() => vm.CurrentPath != from, $"the move up from {from}");
        }

        Assert.Equal("/", vm.CurrentPath);
        Assert.False(vm.CanGoUp);
    }

    [AvaloniaFact]
    public void AnEmptyFolder_SaysSoRatherThanLookingBroken()
    {
        if (!fx.Available) return;
        var d = fx.NewDeployment();

        var (_, vm) = Show(d);

        Assert.Empty(vm.Directories);
        Assert.Equal("No subfolders here.", vm.Status);
    }
}
