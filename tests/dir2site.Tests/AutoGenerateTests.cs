// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using dir2site.Models;
using dir2site.Services;
using dir2site.ViewModels;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Rebuilding without anyone pressing anything, and the two things that has to not do: interrupt,
/// and lose the settings that used to be saved by the button it takes away.
/// </summary>
public class AutoGenerateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-auto-" + Guid.NewGuid().ToString("N"));

    public AutoGenerateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string ConfigPath => Path.Combine(_root, "dir2site.yaml");

    // ---- the button it replaces --------------------------------------------

    [AvaloniaFact]
    public void WithAutoGenerateOn_TheGenerateButtonIsDisabled()
    {
        var vm = new MainWindowViewModel
        {
            DirectoryRoot = _root,
            Dir2SiteConfig = new Dir2SiteModel { Title = "S" },
        };
        vm.DirItems.Add(new DirectoryTreeItem(_root));

        Assert.True(vm.GenerateSiteCommand.CanExecute(null));

        vm.AutoGenerate = true;
        Assert.False(vm.GenerateSiteCommand.CanExecute(null));

        vm.AutoGenerate = false;
        Assert.True(vm.GenerateSiteCommand.CanExecute(null));
    }

    // ---- the settings that button used to save -----------------------------

    [AvaloniaFact]
    public void EditingASetting_WritesTheConfigWithoutAGenerate()
    {
        // dir2site.yaml was only ever written from inside Generate Site. Disabling that button would
        // have meant Title, colors and the PDF settings were never saved at all.
        var vm = new MainWindowViewModel { DirectoryRoot = _root };
        vm.Dir2SiteConfig = new Dir2SiteModel { Title = "Before", Footer = "© 2026" };

        vm.Dir2SiteConfig.Title = "After";

        Assert.True(File.Exists(ConfigPath));
        Assert.Contains("After", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void EditingASetting_LeavesTheUsersCommentsAlone()
    {
        File.WriteAllText(ConfigPath,
            """
            # my notes about this project
            title: Before
            primaryColor: '#123456'
            """);

        var vm = new MainWindowViewModel { DirectoryRoot = _root };
        vm.Dir2SiteConfig = YamlParser.DeserializeAs<Dir2SiteModel>(File.ReadAllText(ConfigPath))!;

        vm.Dir2SiteConfig.Title = "After";

        var written = File.ReadAllText(ConfigPath);
        Assert.Contains("# my notes about this project", written, StringComparison.Ordinal);
        Assert.Contains("After", written, StringComparison.Ordinal);
        Assert.Contains("#123456", written, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task OpeningAProject_DoesNotRewriteTheUsersConfig()
    {
        // Making the config observable so the settings panel can save on edit turned every
        // programmatic assignment into an edit — including the one that resolving deploy targets
        // does on open. A hand-written file came back with eleven keys it never had, purely from
        // being opened, and the watcher saw its own write.
        File.WriteAllText(ConfigPath,
            """
            # my notes
            title: My Site
            primaryColor: '#333333'
            """);
        var before = File.ReadAllText(ConfigPath);

        var vm = new MainWindowViewModel { DirectoryRoot = _root };
        await vm.LoadDirectoryCommand.ExecuteAsync(null);

        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    [AvaloniaFact]
    public void ConfigWritesStopWhenTheProjectIsClosed()
    {
        var vm = new MainWindowViewModel { DirectoryRoot = _root };
        var config = new Dir2SiteModel { Title = "Before" };
        vm.Dir2SiteConfig = config;
        vm.Dir2SiteConfig.Title = "After";

        // Swapping projects has to detach the old config, or editing a stale object would write into
        // whichever folder happens to be open now.
        vm.Dir2SiteConfig = new Dir2SiteModel { Title = "Other project" };
        File.Delete(ConfigPath);

        config.Title = "Edited after the swap";

        Assert.False(File.Exists(ConfigPath));
    }

    /// <summary>
    /// A rescan after we wrote the config ourselves must not replace the object the panel is bound to.
    /// </summary>
    /// <remarks>
    /// The stamp that tells our own write from a hand edit was set by the settings panel's save and
    /// by nothing else — so the deploy writers, which splice <c>deploy:</c> straight into the file,
    /// left it stale. The next scan then read the file back as though someone had edited it by hand,
    /// swapped in a freshly deserialized config, and took anything half-typed in another box with
    /// it. Switching deploy target mid-edit was enough to lose a colour someone was partway through.
    ///
    /// This is the failure the footer dialog's explicit save was removed for; it was still reachable
    /// from three other writers.
    /// </remarks>
    [AvaloniaFact]
    public async Task WritingDeploySettings_DoesNotMakeTheNextScanRereadTheConfig()
    {
        File.WriteAllText(ConfigPath, "title: My Site\n");

        var vm = new MainWindowViewModel { DirectoryRoot = _root };
        await vm.LoadDirectoryCommand.ExecuteAsync(null);

        var bound = vm.Dir2SiteConfig!;
        var target = new DeployTarget { Name = "production", Host = "example.test" };
        bound.Deploy = new DeployConfig { Active = "production", Targets = [target] };

        // A host key accepted during a deploy — the config write furthest from the settings panel.
        vm.PersistAcceptedHostKey(target, "SHA256:abc", ConfigPath);

        await vm.LoadDirectoryCommand.ExecuteAsync(null);

        Assert.Same(bound, vm.Dir2SiteConfig);
    }

    // ---- never interrupt ----------------------------------------------------

    [AvaloniaFact]
    public async Task WithAutoGenerateOn_LeftoversAreHeldRatherThanAskedAbout()
    {
        // The failure this is here to prevent: a modal appearing because somebody moved a folder in
        // Finder, with nobody at the keyboard. There is no owner window in a headless test, so a
        // dialog would throw or hang rather than open — either way this test would not pass.
        var photos = Directory.CreateDirectory(Path.Combine(_root, "Photographs")).FullName;
        MakeArtifact(photos, "Portrait.jpg", "A Portrait");
        MakeArtifact(photos, "Landscape.jpg", "A Landscape");

        var vm = new MainWindowViewModel { DirectoryRoot = _root };
        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);

        // Something in _site nobody can account for.
        File.WriteAllText(Path.Combine(_root, "_site", "stray.html"), "<html></html>");

        vm.AutoGenerate = true;
        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        // Executed directly, the way the watcher reaches it — the command refuses from the UI while
        // auto-generate is on, which is the point of it.
        await vm.GenerateSiteCommand.ExecuteAsync(null);

        Assert.Contains(vm.PendingSiteOrphans, o => o.Contains("stray.html", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_root, "_site", "stray.html")));
    }

    [AvaloniaFact]
    public async Task AManualGenerateThatFindsNothing_ClearsWhatWasHeld()
    {
        var photos = Directory.CreateDirectory(Path.Combine(_root, "Photographs")).FullName;
        MakeArtifact(photos, "Portrait.jpg", "A Portrait");
        MakeArtifact(photos, "Landscape.jpg", "A Landscape");

        var vm = new MainWindowViewModel { DirectoryRoot = _root };
        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);

        vm.PendingSiteOrphans = ["something/stale.html"];
        await vm.GenerateSiteCommand.ExecuteAsync(null);

        Assert.Empty(vm.PendingSiteOrphans);
    }

    // ---- and what happens instead ------------------------------------------

    [AvaloniaFact]
    public async Task ADeployAsksAboutWhatWasHeld_BeforeItConnects()
    {
        // The offer is deferred, not dropped. Asked at the one moment these files would stop being
        // a local curiosity and start being a published page.
        var photos = Directory.CreateDirectory(Path.Combine(_root, "Photographs")).FullName;
        MakeArtifact(photos, "Portrait.jpg", "A Portrait");
        MakeArtifact(photos, "Landscape.jpg", "A Landscape");

        var vm = new MainWindowViewModel { DirectoryRoot = _root };
        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);

        File.WriteAllText(Path.Combine(_root, "_site", "stray.html"), "<html></html>");

        vm.AutoGenerate = true;
        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.PendingSiteOrphans);

        GiveItADeployTarget(vm);

        IReadOnlyList<string>? asked = null;
        vm.AskAboutOrphans = orphans =>
        {
            asked = orphans;
            return Task.FromResult<IReadOnlyList<string>?>(null);
        };

        await vm.QuickSyncCommand.ExecuteAsync(null);

        Assert.NotNull(asked);
        Assert.Contains(asked!, o => o.Contains("stray.html", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task DecliningAtTheDeploy_LeavesTheFilesAndDoesNotAskAgainThatRun()
    {
        // Declining is a real answer. The deploy goes ahead with the leftovers in place, and they
        // come back next deploy rather than being raised again straight away.
        var photos = Directory.CreateDirectory(Path.Combine(_root, "Photographs")).FullName;
        MakeArtifact(photos, "Portrait.jpg", "A Portrait");
        MakeArtifact(photos, "Landscape.jpg", "A Landscape");

        var vm = new MainWindowViewModel { DirectoryRoot = _root };
        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);

        File.WriteAllText(Path.Combine(_root, "_site", "stray.html"), "<html></html>");

        vm.AutoGenerate = true;
        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);

        GiveItADeployTarget(vm);

        var asks = 0;
        vm.AskAboutOrphans = _ =>
        {
            asks++;
            return Task.FromResult<IReadOnlyList<string>?>(null);
        };

        await vm.QuickSyncCommand.ExecuteAsync(null);
        await vm.QuickSyncCommand.ExecuteAsync(null);

        Assert.Equal(1, asks);
        Assert.True(File.Exists(Path.Combine(_root, "_site", "stray.html")));
    }

    [AvaloniaFact]
    public async Task AcceptingAtTheDeploy_TakesThemAway()
    {
        var photos = Directory.CreateDirectory(Path.Combine(_root, "Photographs")).FullName;
        MakeArtifact(photos, "Portrait.jpg", "A Portrait");
        MakeArtifact(photos, "Landscape.jpg", "A Landscape");

        var vm = new MainWindowViewModel { DirectoryRoot = _root };
        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);

        File.WriteAllText(Path.Combine(_root, "_site", "stray.html"), "<html></html>");

        vm.AutoGenerate = true;
        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);

        GiveItADeployTarget(vm);
        vm.AskAboutOrphans = orphans => Task.FromResult<IReadOnlyList<string>?>([.. orphans]);
        await vm.QuickSyncCommand.ExecuteAsync(null);

        Assert.False(File.Exists(Path.Combine(_root, "_site", "stray.html")));
    }

    [AvaloniaFact]
    public async Task SourceLeftoversNeverBlockADeploy()
    {
        // A sidecar and a hidden preview folder stay on the machine, so gating a deploy on them
        // would be a prompt with nothing behind it.
        var photos = Directory.CreateDirectory(Path.Combine(_root, "Photographs")).FullName;
        MakeArtifact(photos, "Portrait.jpg", "A Portrait");
        MakeArtifact(photos, "Landscape.jpg", "A Landscape");
        MakeArtifact(photos, "Memo.jpg", "A Memo");

        var vm = new MainWindowViewModel { DirectoryRoot = _root };
        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);

        // Deleting the source but not its sidecar, with nothing watching — a leftover in the
        // project folder, and one in _site that the sweep will offer separately.
        File.Delete(Path.Combine(photos, "Portrait.jpg"));

        vm.AutoGenerate = true;
        await vm.LoadDirectoryCommand.ExecuteAsync(null);
        await vm.GenerateSiteCommand.ExecuteAsync(null);

        // The sidecar survives: nothing saw the deletion, so it is offered rather than assumed —
        // and under auto-generate that offer waits for a generate the user asked for.
        Assert.True(File.Exists(Path.Combine(photos, "Portrait.jpg.yaml")));

        GiveItADeployTarget(vm);

        var asked = new List<IReadOnlyList<string>>();
        vm.AskAboutOrphans = orphans =>
        {
            asked.Add(orphans);
            return Task.FromResult<IReadOnlyList<string>?>(null);
        };

        await vm.QuickSyncCommand.ExecuteAsync(null);

        // Whatever the deploy asked about, none of it was the sidecar.
        Assert.DoesNotContain(asked.SelectMany(a => a), o => o.Contains(".yaml", StringComparison.Ordinal));
    }

    /// <summary>
    /// Gives the project a deploy target, so a sync gets as far as the leftovers check. It never
    /// connects: the check runs before the credential lookup, which is where these tests stop.
    /// </summary>
    private static void GiveItADeployTarget(MainWindowViewModel vm)
    {
        // Assigned as a whole config rather than mutated in place: the target list is rebuilt when
        // the config object changes, which is what the app does after the SFTP dialog saves.
        //
        // The host is deliberately blank. These tests are about what happens *before* anything is
        // sent, and a target that resolves would have them waiting on a network timeout to find out
        // — a real hostname made the suite hang. An empty one is refused the moment the connection
        // is built, well after the check being tested.
        var config = vm.Dir2SiteConfig!;
        vm.Dir2SiteConfig = new Dir2SiteModel
        {
            Title = config.Title,
            Footer = config.Footer,
            SiteUrl = config.SiteUrl,
            Deploy = new DeployConfig
            {
                Targets = [new DeployTarget { Name = "test", Host = "", Username = "nobody" }],
            },
        };
    }

    private static void MakeArtifact(string folder, string fileName, string caption)
    {
        File.WriteAllText(Path.Combine(folder, fileName), "not really a jpeg");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"type: photo\ncaption: {caption}\n");
    }
}
