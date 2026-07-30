// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO;
using System.Linq;
using dir2site.SftpSync.Core;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Seeing what a deploy will do before it does it. The diff already existed inside QuickSync; the
/// point here is that looking must not change anything, and that applying re-checks rather than
/// trusting a plan the server may have outgrown.
/// </summary>
public class PreviewTests(SftpServerFixture fx) : IClassFixture<SftpServerFixture>
{
    private static void Write(string siteDir, string rel, string content)
    {
        var p = Path.Combine(siteDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    [SkippableFact]
    public void PreviewingChangesNothingOnTheServer()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        Write(d.SiteDir, "index.html", "home");
        Write(d.SiteDir, "about.html", "about");

        var plan = SftpSyncService.Preview(d.SiteDir, d.Profile, null);

        Assert.Equal(2, plan.ToUpload.Count);
        Assert.Empty(Directory.GetFileSystemEntries(d.RemoteDir));   // looked, touched nothing
    }

    [SkippableFact]
    public void ThePlanCountsBytes_SoTheUserKnowsWhatTheyAreCommittingTo()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        Write(d.SiteDir, "big.html", new string('x', 5000));

        var plan = SftpSyncService.Preview(d.SiteDir, d.Profile, null);

        Assert.Equal(5000, plan.BytesToUpload);
        Assert.Contains("4.9 KB", plan.Summary);
    }

    [SkippableFact]
    public void AnUpToDateSiteHasAnEmptyPlan()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        Write(d.SiteDir, "index.html", "home");
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);

        var plan = SftpSyncService.Preview(d.SiteDir, d.Profile, null);

        Assert.True(plan.IsEmpty);
        Assert.Equal("Everything is already up to date.", plan.Summary);
    }

    [SkippableFact]
    public void ThePlanMatchesWhatQuickSyncThenDoes()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        for (var i = 0; i < 5; i++) Write(d.SiteDir, $"p{i}.html", "x");

        var plan = SftpSyncService.Preview(d.SiteDir, d.Profile, null);
        var result = SftpSyncService.Apply(plan, d.SiteDir, d.Profile, null);

        Assert.Equal(plan.ToUpload.Count, result.Uploaded);
        Assert.DoesNotContain("changed in between", result.Summary);
        Assert.All(plan.ToUpload, rel => Assert.True(File.Exists(Path.Combine(d.RemoteDir, rel))));
    }

    [SkippableFact]
    public void WhenTheSiteChangesAfterPreviewing_ApplyDeploysTheNewStateAndSaysSo()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        Write(d.SiteDir, "index.html", "home");

        var plan = SftpSyncService.Preview(d.SiteDir, d.Profile, null);
        Assert.Single(plan.ToUpload);

        // The user regenerates the site while the preview dialog is open.
        Write(d.SiteDir, "extra.html", "added later");

        var result = SftpSyncService.Apply(plan, d.SiteDir, d.Profile, null);

        // Current local state wins — that is what they meant to publish — but they are told.
        Assert.Equal(2, result.Uploaded);
        Assert.Contains("changed in between", result.Summary);
        Assert.True(File.Exists(Path.Combine(d.RemoteDir, "extra.html")));
    }

    [SkippableFact]
    public void ForceFull_PreviewsEverything()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        Write(d.SiteDir, "index.html", "home");
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);

        var plan = SftpSyncService.Preview(d.SiteDir, d.Profile, null, forceFull: true);

        Assert.Single(plan.ToUpload);
        Assert.Contains("forced full upload", plan.Note);
    }

    [SkippableFact]
    public void PreviewReportsStaleFilesWithoutRemovingThem()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        Write(d.SiteDir, "index.html", "home");
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);
        File.Delete(Path.Combine(d.SiteDir, "index.html"));
        Write(d.SiteDir, "other.html", "x");

        var plan = SftpSyncService.Preview(d.SiteDir, d.Profile, null);

        Assert.Contains("index.html", plan.StaleRemote);
        Assert.True(File.Exists(Path.Combine(d.RemoteDir, "index.html")));   // still there
        Assert.Contains("stale on the server", plan.Summary);
    }

    [Fact]
    public void AnEmptySitePreviewsAsNothingToDo()
    {
        var plan = new SyncPlan([], [], 0, "_site/ is empty — nothing to deploy.");

        Assert.True(plan.IsEmpty);
        Assert.Equal("Everything is already up to date.", plan.Summary);
    }
}
