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

/// <summary>
/// The manifest lists every deployed path, so it shouldn't be readable over HTTP. The default name
/// handles Apache for free; everyone else needs a rule, and the rules must name the real file.
/// </summary>
public class ManifestPrivacyTests
{
    [Fact]
    public void TheDefaultName_IsOneApacheAlreadyRefusesToServe()
    {
        // Apache's stock config denies anything matching ^\.ht — that is the whole reason for the
        // prefix, so it is worth a test rather than a comment someone can rename away.
        Assert.StartsWith(".ht", SftpSyncService.DefaultManifestFileName);
    }

    [AvaloniaFact]
    public void EverySnippet_NamesTheActualManifestFile()
    {
        var window = new Window();
        var vm = new ManifestPrivacyViewModel(window);
        var name = SftpSyncService.DefaultManifestFileName;

        Assert.Equal(name, vm.ManifestName);
        Assert.Contains(name, vm.ApacheSnippet);
        Assert.Contains(name, vm.CaddySnippet);
        Assert.Contains(name, vm.IisSnippet);
        // nginx escapes the dot for its regex, so match on the escaped form.
        Assert.Contains(name.Replace(".", "\\."), vm.NginxSnippet);
    }

    [AvaloniaFact]
    public void TheGuidanceIsBehindAButton_NotOnTheMainForm()
    {
        var view = new SftpSettingsView();
        view.Show();
        Dispatcher.UIThread.RunJobs();

        // Present, but inside the collapsed Advanced expander — so it costs nothing to the people
        // who will never need it.
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(),
                              b => (b.Content as string) == "Privacy…");

        view.GetVisualDescendants().OfType<Expander>().First().IsExpanded = true;
        Dispatcher.UIThread.RunJobs();

        var button = view.GetVisualDescendants().OfType<Button>()
                         .First(b => (b.Content as string) == "Privacy…");
        Assert.True(button.IsEffectivelyVisible);
    }
}
