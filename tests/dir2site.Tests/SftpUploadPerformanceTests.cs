// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using dir2site.SftpSync.Core;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The parallel upload path, against a real SFTP server. It is an optimisation, so what these
/// guard is that the site still arrives intact and that Verify stays quiet afterwards — the symptom
/// that started this was Verify re-uploading a site it had just uploaded.
/// </summary>
public class SftpUploadPerformanceTests : IClassFixture<SftpServerFixture>
{
    private readonly SftpServerFixture _fx;
    public SftpUploadPerformanceTests(SftpServerFixture fx) => _fx = fx;

    private static void Write(string siteDir, string rel, string content)
    {
        var p = Path.Combine(siteDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    [SkippableFact]
    public void UploadedFilesKeepTheirLocalModifiedTime()
    {
        Skip.IfNot(_fx.Available, _fx.Reason);
        var d = _fx.NewDeployment();
        Write(d.SiteDir, "index.html", "home");

        // Distinctly not "now", so a server that ignored us is obvious.
        var stamp = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(d.SiteDir, "index.html"), stamp);

        SftpSyncService.QuickSync(d.SiteDir, d.Profile, secret: null);

        var remote = File.GetLastWriteTimeUtc(Path.Combine(d.RemoteDir, "index.html"));
        Assert.True(Math.Abs((remote - stamp).TotalSeconds) <= 2,
            $"remote mtime {remote:o} should match local {stamp:o}");
    }

    [SkippableFact]
    public void AfterASyncThereIsNothingLeftToRepair()
    {
        Skip.IfNot(_fx.Available, _fx.Reason);
        var d = _fx.NewDeployment();
        foreach (var i in Enumerable.Range(0, 12))
            Write(d.SiteDir, $"page{i}/index.html", $"page {i}");

        SftpSyncService.QuickSync(d.SiteDir, d.Profile, secret: null);
        var verify = SftpSyncService.VerifyAndRepair(d.SiteDir, d.Profile, secret: null);

        Assert.Equal(0, verify.Uploaded);
        Assert.Empty(verify.Errors);
        Assert.Empty(verify.StaleRemote);
    }

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(8)]
    public void EveryFileArrivesIntactWhateverTheConcurrency(int concurrency)
    {
        Skip.IfNot(_fx.Available, _fx.Reason);
        var d = _fx.NewDeployment();
        d.Profile.UploadConcurrency = concurrency;

        // Enough files, spread over enough directories, that the workers genuinely contend for the
        // shared work queue and for EnsureDir on the same parents.
        var expected = Enumerable.Range(0, 40)
            .Select(i => ($"section{i % 5}/page{i}.html", $"content {i}"))
            .ToArray();
        foreach (var (rel, content) in expected) Write(d.SiteDir, rel, content);

        var result = SftpSyncService.QuickSync(d.SiteDir, d.Profile, secret: null);

        Assert.Empty(result.Errors);
        Assert.Equal(expected.Length, result.Uploaded);
        foreach (var (rel, content) in expected)
        {
            var landed = Path.Combine(d.RemoteDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(landed), $"{rel} never arrived");
            Assert.Equal(content, File.ReadAllText(landed));
        }
    }

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(8)]
    public void DeletingStaleFilesRemovesThemAllWhateverTheConcurrency(int concurrency)
    {
        Skip.IfNot(_fx.Available, _fx.Reason);
        var d = _fx.NewDeployment();
        d.Profile.UploadConcurrency = concurrency;

        var all = Enumerable.Range(0, 30).Select(i => $"old{i % 4}/file{i}.html").ToArray();
        foreach (var rel in all) Write(d.SiteDir, rel, "x");
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, secret: null);

        // Now they are stale: gone locally, still on the server.
        Directory.Delete(d.SiteDir, recursive: true);
        Directory.CreateDirectory(d.SiteDir);
        Write(d.SiteDir, "index.html", "home");

        var result = SftpSyncService.DeleteRemote(d.SiteDir, d.Profile, secret: null, all);

        Assert.Empty(result.Errors);
        foreach (var rel in all)
            Assert.False(
                File.Exists(Path.Combine(d.RemoteDir, rel.Replace('/', Path.DirectorySeparatorChar))),
                rel + " should be gone");
    }

    [SkippableFact]
    public void ASecondSyncWithNoLocalChangesUploadsNothing()
    {
        Skip.IfNot(_fx.Available, _fx.Reason);
        var d = _fx.NewDeployment();
        foreach (var i in Enumerable.Range(0, 20))
            Write(d.SiteDir, $"a{i}.html", $"{i}");

        SftpSyncService.QuickSync(d.SiteDir, d.Profile, secret: null);
        var again = SftpSyncService.QuickSync(d.SiteDir, d.Profile, secret: null);

        Assert.Equal(0, again.Uploaded);
    }
}
