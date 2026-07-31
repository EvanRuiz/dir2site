// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using System.Threading;
using dir2site.SftpSync.Core;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The engine has always taken a CancellationToken; until now nothing passed a real one, so this
/// behaviour was never exercised. A deploy can run for minutes over a slow link, and a user who
/// hits Cancel needs it to stop and to leave the server in a sane state.
/// </summary>
public class SyncCancellationTests(SftpServerFixture fx) : IClassFixture<SftpServerFixture>
{
    private static void Write(string siteDir, string rel, string content)
    {
        var p = Path.Combine(siteDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    [SkippableFact]
    public void CancelledBeforeItStarts_UploadsNothing()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        Write(d.SiteDir, "index.html", "home");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => SftpSyncService.QuickSync(d.SiteDir, d.Profile, null, false, null, cts.Token));

        Assert.Empty(Directory.GetFileSystemEntries(d.RemoteDir));
    }

    [SkippableFact]
    public void CancelledPartWayThrough_StopsEarly_AndKeepsWhatItAlreadySent()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        for (var i = 0; i < 40; i++)
            Write(d.SiteDir, $"page{i:D3}.html", new string('x', 2048));

        using var cts = new CancellationTokenSource();
        // Cancel once the engine reports it is a few files in, so the run is genuinely interrupted
        // mid-flight rather than before it began.
        var progress = new Progress<SyncProgress>(p =>
        {
            if (p.Phase == SyncPhase.Uploading && p.Index == 5) cts.Cancel();
        });

        Assert.ThrowsAny<OperationCanceledException>(
            () => SftpSyncService.QuickSync(d.SiteDir, d.Profile, null, false, progress, cts.Token));

        var uploaded = Directory.GetFiles(d.RemoteDir, "*.html").Length;
        Assert.InRange(uploaded, 1, 39);   // some progress, but it did not run to completion
    }

    [SkippableFact]
    public void AfterCancelling_ANormalSyncCompletesTheRest()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        for (var i = 0; i < 40; i++)
            Write(d.SiteDir, $"page{i:D3}.html", new string('x', 2048));

        using var cts = new CancellationTokenSource();
        var progress = new Progress<SyncProgress>(p =>
        {
            if (p.Phase == SyncPhase.Uploading && p.Index == 5) cts.Cancel();
        });
        Assert.ThrowsAny<OperationCanceledException>(
            () => SftpSyncService.QuickSync(d.SiteDir, d.Profile, null, false, progress, cts.Token));

        // Cancelling must not poison the deployment: the next run finishes the job.
        var result = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);

        Assert.Empty(result.Errors);
        Assert.Equal(40, Directory.GetFiles(d.RemoteDir, "*.html").Length);
        Assert.All(Directory.GetFiles(d.RemoteDir, "*.html"),
                   f => Assert.Equal(2048, new FileInfo(f).Length));
    }
}
