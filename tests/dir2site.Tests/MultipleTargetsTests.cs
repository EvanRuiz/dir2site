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
using dir2site.Services;
using dir2site.SftpSync.Ui;
using dir2site.ViewModels;
using dir2site.Views;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// One project, several deploy targets — staging and production being the obvious case. Before
/// this, configuring a second server meant overwriting the first.
/// </summary>
public class MultipleTargetsTests : IDisposable
{
    private readonly string _project = Path.Combine(
        Path.GetTempPath(), "d2s-multi-" + Guid.NewGuid().ToString("N"));

    public MultipleTargetsTests()
    {
        Directory.CreateDirectory(_project);
        File.WriteAllText(ConfigPath, "title: Test Site\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_project, recursive: true); } catch { }
    }

    private string ConfigPath => Path.Combine(_project, "dir2site.yaml");

    private Dir2SiteModel Config() =>
        YamlParser.DeserializeAs<Dir2SiteModel>(File.ReadAllText(ConfigPath));

    private (SftpSettingsView view, SftpSettingsViewModel vm) ShowDialog(Dir2SiteModel config)
    {
        var view = new SftpSettingsView(_project, config, ConfigPath);
        view.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, (SftpSettingsViewModel)view.DataContext!);
    }

    [AvaloniaFact]
    public void AddingASecondTarget_KeepsTheFirst()
    {
        var (_, vm) = ShowDialog(Config());
        vm.TargetName = "production";
        vm.Host = "127.0.0.1";
        vm.Username = "deploy";
        vm.RemotePath = "/var/www";

        vm.AddTargetCommand.Execute(null);
        vm.TargetName = "staging";
        vm.Host = "127.0.0.1";
        vm.Port = 2222;
        vm.Username = "stage";
        vm.RemotePath = "/srv/staging";
        vm.SaveCommand.Execute(null);

        var deploy = Config().Deploy!;
        Assert.Equal(2, deploy.Targets.Count);
        Assert.Contains(deploy.Targets, t => t.Name == "production" && t.RemotePath == "/var/www");
        Assert.Contains(deploy.Targets, t => t.Name == "staging" && t.Port == 2222);
        Assert.Equal("staging", deploy.Active);   // the one being edited becomes active
    }

    [AvaloniaFact]
    public void SwitchingTargets_DoesNotLoseUnsavedEdits()
    {
        var (_, vm) = ShowDialog(Config());
        vm.TargetName = "production";
        vm.Host = "127.0.0.1";
        vm.Username = "deploy";
        vm.AddTargetCommand.Execute(null);
        var staging = vm.SelectedTarget;
        vm.Host = "10.0.0.9";

        // Flip back and forth; what was typed must still be there.
        vm.SelectedTarget = vm.Targets.First(t => t.Name == "production");
        Assert.Equal("127.0.0.1", vm.Host);

        vm.SelectedTarget = staging;
        Assert.Equal("10.0.0.9", vm.Host);
    }

    [AvaloniaFact]
    public void TheLastTarget_CannotBeDeleted()
    {
        var (_, vm) = ShowDialog(Config());

        vm.DeleteTargetCommand.Execute(null);

        Assert.Single(vm.Targets);
        Assert.Contains("at least one target", vm.Status);
    }

    [AvaloniaFact]
    public void DuplicateNames_AreRefusedRatherThanSilentlyMerged()
    {
        var (_, vm) = ShowDialog(Config());
        vm.TargetName = "same";
        vm.Host = "127.0.0.1";
        vm.Username = "u";
        vm.AddTargetCommand.Execute(null);
        vm.TargetName = "same";
        vm.Host = "127.0.0.1";
        vm.Username = "u";

        vm.SaveCommand.Execute(null);

        Assert.Contains("unique", vm.Status);
        Assert.Null(Config().Deploy);   // nothing was written
    }

    [AvaloniaFact]
    public void HostAndUsername_AreStillRequired()
    {
        var (_, vm) = ShowDialog(Config());
        vm.TargetName = "production";
        vm.Host = "";

        vm.SaveCommand.Execute(null);

        Assert.Contains("required", vm.Status);
    }

    [AvaloniaFact]
    public void ThePicker_AppearsOnlyOnceThereIsAChoice()
    {
        var config = new Dir2SiteModel
        {
            Deploy = new DeployConfig
            {
                Active = "production",
                Targets = [new DeployTarget { Name = "production", Host = "127.0.0.1", Username = "u" }],
            },
        };
        var vm = new MainWindowViewModel { Dir2SiteConfig = config, DirectoryRoot = _project };
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var combo = window.GetVisualDescendants().OfType<ComboBox>()
                          .First(c => c.ItemsSource == vm.DeployTargetList);
        Assert.False(vm.HasMultipleTargets);
        Assert.False(combo.IsEffectivelyVisible);

        // A second target arrives — e.g. the config was re-read after a hand edit.
        vm.Dir2SiteConfig = new Dir2SiteModel
        {
            Deploy = new DeployConfig
            {
                Active = "production",
                Targets =
                [
                    new DeployTarget { Name = "production", Host = "127.0.0.1", Username = "u" },
                    new DeployTarget { Name = "staging", Host = "127.0.0.1", Username = "u" },
                ],
            },
        };
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.HasMultipleTargets);
        Assert.True(combo.IsEffectivelyVisible);
        Assert.Equal(2, vm.DeployTargetList.Count);
    }
}
