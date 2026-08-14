// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using dir2site.Models;
using dir2site.Services;
using dir2site.ViewModels;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Bringing a yaml up to the current key set writes to a file the user owns — and very likely has
/// in version control. Nothing has gone wrong when it happens, but they should hear it from the app
/// rather than from a diff, once for the whole scan however many files it touched.
/// </summary>
public class YamlBackfillNoticeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-notice-" + Guid.NewGuid().ToString("N"));

    public YamlBackfillNoticeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void MakePhoto(string stem, string yaml)
    {
        File.WriteAllText(Path.Combine(_root, stem + ".jpg"), "not really a jpeg");
        File.WriteAllText(Path.Combine(_root, stem + ".jpg.yaml"), yaml);
    }

    private async Task<MainWindowViewModel> Generated()
    {
        var vm = new MainWindowViewModel
        {
            DirectoryRoot = _root,
            Dir2SiteConfig = new Dir2SiteModel { Title = "My Site", SiteUrl = "https://example.test" },
        };

        // Enough of a tree for the command to be enabled — Generate re-scans from disk first, and
        // that walk is the one that does the backfilling. Scanning here instead would do it early
        // and leave the run under test with nothing to report.
        vm.DirItems.Add(new DirectoryTreeItem(_root));
        await vm.GenerateSiteCommand.ExecuteAsync(null);
        return vm;
    }

    [AvaloniaFact]
    public async Task TheScanSaysHowManyFilesItBroughtUpToDate()
    {
        MakePhoto("Apple", "type: photo\ncaption: Apple\n");
        MakePhoto("Pear", "type: photo\ncaption: Pear\n");

        var vm = await Generated();

        Assert.True(vm.HasWarnings);
        Assert.Contains("2 yaml files", vm.WarningText);
        Assert.Contains("unchanged", vm.WarningText);
    }

    [AvaloniaFact]
    public async Task OneFileIsNamed()
    {
        MakePhoto("Apple", "type: photo\ncaption: Apple\n");

        Assert.Contains("Apple.jpg.yaml", (await Generated()).WarningText);
    }

    /// The notice is for the scan that changed something. A project already up to date is quiet,
    /// which is every scan after the first.
    [AvaloniaFact]
    public async Task AProjectAlreadyUpToDateSaysNothing()
    {
        MakePhoto("Apple", "type: photo\ncaption: Apple\n");
        DirectoryTraverser.BuildTree(_root, [], []);

        var vm = await Generated();

        Assert.DoesNotContain("yaml file", vm.WarningText);
    }
}
