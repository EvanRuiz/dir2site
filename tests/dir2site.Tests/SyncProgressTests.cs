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

    /// <summary>
    /// Collects reports on whichever thread raised them.
    ///
    /// Progress&lt;T&gt; posts to the thread pool when there is no synchronization context, so a
    /// report can still be in flight when the sync returns — which is a race these assertions lose
    /// now that uploads and deletes run on several connections. The real app keeps Progress&lt;T&gt;:
    /// it has a UI context to marshal to, which is the whole point of using it there.
    /// </summary>
    private sealed class Collector : IProgress<SyncProgress>
    {
        private readonly List<SyncProgress> _items = [];

        public void Report(SyncProgress value)
        {
            lock (_items) _items.Add(value);
        }

        public IReadOnlyList<SyncProgress> Items
        {
            get { lock (_items) return _items.ToList(); }
        }
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

        var seen = new Collector();
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null, false, seen);

        var uploads = seen.Items.Where(p => p.Phase == SyncPhase.Uploading).ToList();
        Assert.Equal(6, uploads.Count);
        // Every position is reported exactly once, but uploads run on several connections now, so
        // which one finishes third is not fixed — assert the set, not the arrival order.
        Assert.Equal(Enumerable.Range(1, 6), uploads.Select(u => u.Index).OrderBy(i => i));
        Assert.All(uploads, u => Assert.Equal(6, u.Total));
        Assert.All(uploads, u => Assert.False(string.IsNullOrEmpty(u.CurrentFile)));
        Assert.Equal(100.0, uploads.Max(u => u.Percent!.Value), 6);
    }

    [SkippableFact]
    public void TheListingPhaseIsReported_SoTheBarCanStartIndeterminate()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        Write(d.SiteDir, "index.html", "home");
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);   // seed a manifest

        var seen = new Collector();
        SftpSyncService.VerifyAndRepair(d.SiteDir, d.Profile, null, seen);

        var listing = Assert.Single(seen.Items, p => p.Phase == SyncPhase.Listing);
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

        var seen = new Collector();
        SftpSyncService.DeleteRemote(d.SiteDir, d.Profile, null,
            ["stray1.html", "stray2.html"], seen);

        var deletes = seen.Items.Where(p => p.Phase == SyncPhase.Deleting).ToList();
        Assert.Equal(2, deletes.Count);
        Assert.All(deletes, x => Assert.Equal(2, x.Total));
        // Same as uploading: several connections, so the order they finish in isn't fixed.
        Assert.Equal([1, 2], deletes.Select(x => x.Index).OrderBy(i => i));
    }
}
