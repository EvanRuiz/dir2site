// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dir2site.SftpSync.Core;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// A deploy can run for minutes. "Uploading css/site.css" doesn't tell you whether to wait;
/// "142 of 380" does. These check the engine reports enough to draw a real bar.
/// </summary>
public class SyncProgressTests(SftpServerFixture fx) : IClassFixture<SftpServerFixture>
{
    private static void Write(string siteDir, string rel, string content)
    {
        var p = Path.Combine(siteDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    [Fact]
    public void PercentIsNullWhenThereIsNothingToCount()
    {
        var connecting = new SyncProgress(SyncPhase.Listing, "Listing remote files…");

        Assert.False(connecting.HasCount);
        Assert.Null(connecting.Percent);
        Assert.Equal("Listing remote files…", connecting.ToString());
    }

    [Fact]
    public void ACountedReport_CarriesPositionAndReadsWell()
    {
        var p = new SyncProgress(SyncPhase.Uploading, "Uploading", 142, 380, "css/site.css");

        Assert.True(p.HasCount);
        Assert.Equal(142 * 100.0 / 380, p.Percent!.Value, 6);
        Assert.Equal("css/site.css", p.CurrentFile);
        Assert.Equal("Uploading (142/380)", p.ToString());
    }

    [SkippableFact]
    public void UploadingReportsEveryFile_WithARisingCountAndAStableTotal()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        for (var i = 0; i < 6; i++) Write(d.SiteDir, $"page{i}.html", "x");

        var seen = new List<SyncProgress>();
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null, false,
            new Progress<SyncProgress>(seen.Add));

        var uploads = seen.Where(p => p.Phase == SyncPhase.Uploading).ToList();
        Assert.Equal(6, uploads.Count);
        Assert.Equal(Enumerable.Range(1, 6), uploads.Select(u => u.Index));
        Assert.All(uploads, u => Assert.Equal(6, u.Total));
        Assert.All(uploads, u => Assert.False(string.IsNullOrEmpty(u.CurrentFile)));
        Assert.Equal(100.0, uploads.Last().Percent!.Value, 6);
    }

    [SkippableFact]
    public void TheListingPhaseIsReported_SoTheBarCanStartIndeterminate()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        Write(d.SiteDir, "index.html", "home");
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);   // seed a manifest

        var seen = new List<SyncProgress>();
        SftpSyncService.VerifyAndRepair(d.SiteDir, d.Profile, null,
            new Progress<SyncProgress>(seen.Add));

        var listing = Assert.Single(seen, p => p.Phase == SyncPhase.Listing);
        Assert.False(listing.HasCount);   // nothing countable yet, so the bar stays a marquee
    }

    [SkippableFact]
    public void DeletingReportsProgressToo()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        Write(d.SiteDir, "index.html", "home");
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);
        File.WriteAllText(Path.Combine(d.RemoteDir, "stray1.html"), "junk");
        File.WriteAllText(Path.Combine(d.RemoteDir, "stray2.html"), "junk");

        var seen = new List<SyncProgress>();
        SftpSyncService.DeleteRemote(d.SiteDir, d.Profile, null,
            ["stray1.html", "stray2.html"], new Progress<SyncProgress>(seen.Add));

        var deletes = seen.Where(p => p.Phase == SyncPhase.Deleting).ToList();
        Assert.Equal(2, deletes.Count);
        Assert.All(deletes, x => Assert.Equal(2, x.Total));
    }
}
