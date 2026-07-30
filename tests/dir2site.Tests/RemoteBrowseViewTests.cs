// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
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
        Pump();
        return (view, (RemoteBrowseViewModel)view.DataContext!);
    }

    // Listing happens on a background task; give it a moment to land on the UI thread.
    private static void Pump()
    {
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
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
        Pump();

        Assert.EndsWith("/sites", vm.CurrentPath);
        Assert.Equal(["example.com"], vm.Directories);

        vm.GoUpCommand.Execute(null);
        Pump();

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
        Pump();

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
            vm.GoUpCommand.Execute(null);
            Pump();
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
