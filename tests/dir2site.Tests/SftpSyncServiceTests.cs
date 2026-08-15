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
/// End-to-end tests against a real (throwaway) SFTP server. They skip automatically when no
/// vendored rclone matches this platform (see <see cref="SftpServerFixture"/>).
/// </summary>
public class SftpSyncServiceTests : IClassFixture<SftpServerFixture>
{
    private readonly SftpServerFixture _fx;
    public SftpSyncServiceTests(SftpServerFixture fx) => _fx = fx;

    private static void Write(string siteDir, string rel, string content)
    {
        var p = Path.Combine(siteDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private static bool RemoteHas(string remoteDir, string rel) =>
        File.Exists(Path.Combine(remoteDir, rel.Replace('/', Path.DirectorySeparatorChar)));

    private static string RemoteText(string remoteDir, string rel) =>
        File.ReadAllText(Path.Combine(remoteDir, rel.Replace('/', Path.DirectorySeparatorChar)));

    private SftpServerFixture.Deployment Seeded(params (string rel, string content)[] files)
    {
        Skip.IfNot(_fx.Available, _fx.Reason);
        var d = _fx.NewDeployment();
        foreach (var (rel, content) in files) Write(d.SiteDir, rel, content);
        return d;
    }

    [SkippableFact]
    public void FirstSync_UploadsAllFiles_AndWritesManifest()
    {
        var d = Seeded(("index.html", "home"), ("about/index.html", "about"), ("css/site.css", "body{}"));

        var r = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);

        Assert.Equal(3, r.Uploaded);
        Assert.Empty(r.Errors);
        Assert.True(RemoteHas(d.RemoteDir, "index.html"));
        Assert.True(RemoteHas(d.RemoteDir, "about/index.html"));
        Assert.True(RemoteHas(d.RemoteDir, ".ht-dir2site"));
    }

    [SkippableFact]
    public void ADotFolderInTheSiteNeverReachesTheServer()
    {
        // Generating leaves dot-entries alone on purpose, so a tool run with _site as its working
        // directory leaves its state behind for good — and every later deploy would publish it.
        var d = Seeded(
            ("index.html", "home"),
            (".claude/settings.json", "{}"),
            (".claude/agents/reviewer.md", "notes"),
            (".DS_Store", "junk"),
            (".htaccess", "Deny from all"));

        var r = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);

        Assert.Empty(r.Errors);
        Assert.False(Directory.Exists(Path.Combine(d.RemoteDir, ".claude")),
            ".claude should never be created on the server");
        Assert.False(RemoteHas(d.RemoteDir, ".DS_Store"));

        // Dot-files are still content: .htaccess has to arrive.
        Assert.True(RemoteHas(d.RemoteDir, "index.html"));
        Assert.True(RemoteHas(d.RemoteDir, ".htaccess"));
        Assert.Equal(2, r.Uploaded);
    }

    [SkippableFact]
    public void ReSync_WithNoChanges_UploadsNothing()
    {
        var d = Seeded(("index.html", "home"), ("css/site.css", "body{}"));
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);

        var r = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);

        Assert.Equal(0, r.Uploaded);
        Assert.Empty(r.StaleRemote);
    }

    /// <summary>
    /// Quick Sync compares against the file list it wrote last time, never against the server, so
    /// it cannot say how many files up there don't belong — only that its own records name none.
    /// Printing "0 stale" stated the stronger of those, and it is the one people act on: a file
    /// removed on the server by other means keeps its local size and mtime, so it is skipped every
    /// time while the summary reads as though the two sides had been compared.
    /// </summary>
    [SkippableFact]
    public void QuickSync_CountsStaleFilesOnlyWhenItFoundSome()
    {
        var d = Seeded(("index.html", "home"), ("_media/figure.webp", "a figure"));

        var first = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);
        Assert.DoesNotContain("stale", first.Summary);

        // Something the server has and the site doesn't — the one case Quick Sync can speak to,
        // because its own records name it.
        Write(d.SiteDir, "gone.html", "temporary");
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);
        File.Delete(Path.Combine(d.SiteDir, "gone.html"));

        var withStale = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);
        Assert.Contains("gone.html", withStale.StaleRemote);
        Assert.Contains("1 stale", withStale.Summary);
    }

    [SkippableFact]
    public void EditedFile_UploadsOnlyThatFile()
    {
        var d = Seeded(("index.html", "home"), ("css/site.css", "body{}"));
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);

        Write(d.SiteDir, "css/site.css", "body{color:red}"); // size differs → detected

        var r = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);

        Assert.Equal(1, r.Uploaded);
        Assert.Equal("body{color:red}", RemoteText(d.RemoteDir, "css/site.css"));
    }

    [SkippableFact]
    public void QuickSync_IsBlindToManualRemoteDeletion()
    {
        var d = Seeded(("index.html", "home"), ("about/index.html", "about"));
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);

        File.Delete(Path.Combine(d.RemoteDir, "about", "index.html")); // manual server-side delete

        var r = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);

        Assert.Equal(0, r.Uploaded);                         // manifest still lists it → skipped
        Assert.False(RemoteHas(d.RemoteDir, "about/index.html"));
    }

    [SkippableFact]
    public void VerifyAndRepair_RestoresMissingFile_AndRepairsOnlyThatOne()
    {
        var d = Seeded(("index.html", "home"), ("about/index.html", "about"), ("css/site.css", "body{}"));
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);

        File.Delete(Path.Combine(d.RemoteDir, "about", "index.html"));

        var r = SftpSyncService.VerifyAndRepair(d.SiteDir, d.Profile, null);

        Assert.Equal(1, r.Uploaded); // proves mtime preservation: untouched files aren't re-uploaded
        Assert.True(RemoteHas(d.RemoteDir, "about/index.html"));
    }

    [SkippableFact]
    public void VerifyAndRepair_ReportsStrayServerFileAsStale()
    {
        var d = Seeded(("index.html", "home"));
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);

        File.WriteAllText(Path.Combine(d.RemoteDir, "stray.html"), "junk");

        var r = SftpSyncService.VerifyAndRepair(d.SiteDir, d.Profile, null);

        Assert.Contains("stray.html", r.StaleRemote);
        Assert.DoesNotContain(".ht-dir2site", r.StaleRemote); // manifest must be ignored
    }

    [SkippableFact]
    public void DeleteRemote_RemovesSelectedFiles()
    {
        var d = Seeded(("index.html", "home"));
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);
        File.WriteAllText(Path.Combine(d.RemoteDir, "stray.html"), "junk");

        var r = SftpSyncService.DeleteRemote(d.SiteDir, d.Profile, null, ["stray.html"]);

        Assert.Empty(r.Errors);
        Assert.False(RemoteHas(d.RemoteDir, "stray.html"));
        Assert.True(RemoteHas(d.RemoteDir, "index.html"));
    }

    private static string[] ManifestPaths(string remoteDir)
    {
        var p = Path.Combine(remoteDir, ".ht-dir2site");
        if (!File.Exists(p)) return [];
        return [.. System.Text.Json.JsonDocument.Parse(File.ReadAllText(p))
            .RootElement.GetProperty("Files").EnumerateObject().Select(x => x.Name).Order()];
    }

    /// <summary>
    /// A cancelled deploy has still put files on the server, and the manifest has to say so.
    /// </summary>
    /// <remarks>
    /// It used to be written only after a run that finished, from the local folder rather than
    /// from what arrived — so cancelling recorded nothing. Re-uploading is the harmless half of
    /// that; the other half is that a file on the server which the manifest has never heard of can
    /// never be reported stale either, so deleting it from the site later stranded it, published,
    /// with nothing able to find it but Verify & Repair.
    /// </remarks>
    [SkippableFact]
    public void ACancelledDeploy_StillRecordsWhatReachedTheServer()
    {
        var d = Seeded(("index.html", "home"));
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);
        for (var i = 0; i < 40; i++) Write(d.SiteDir, $"_media/f{i}.webp", new string('x', 3000));

        var cts = new CancellationTokenSource();
        var seen = 0;
        var progress = new Progress<SyncProgress>(_ =>
        {
            if (Interlocked.Increment(ref seen) == 5) cts.Cancel();
        });

        Assert.Throws<OperationCanceledException>(() =>
            SftpSyncService.QuickSync(d.SiteDir, d.Profile, null, false, progress, cts.Token));

        var onServer = Directory.EnumerateFiles(Path.Combine(d.RemoteDir, "_media"))
            .Select(f => "_media/" + Path.GetFileName(f)).Order().ToArray();
        var recorded = ManifestPaths(d.RemoteDir).Where(k => k.StartsWith("_media/")).Order().ToArray();

        Assert.NotEmpty(onServer);                       // the cancel has to land mid-run
        Assert.Equal(onServer, recorded);                // and the record matches, exactly

        // Which is what lets them be found again once they leave the site.
        Directory.Delete(Path.Combine(d.SiteDir, "_media"), recursive: true);
        var after = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);
        Assert.Equal(onServer.Length, after.StaleRemote.Count);
    }

    /// <summary>
    /// Forcing a full upload says what to send. It says nothing about what is already up there, so
    /// a file this run doesn't touch has to stay in the record — and stay reportable.
    /// </summary>
    /// <remarks>
    /// Forcing works by comparing against an empty reference, so everything counts as needing to
    /// be sent. Once that same empty reference became the manifest's starting point, a forced run
    /// recorded only what it uploaded and forgot every other file on the server — reintroducing,
    /// through the one button someone presses when they suspect the server has drifted, exactly
    /// the fault the record was corrected to remove.
    /// </remarks>
    [SkippableFact]
    public void AForcedFullUpload_StillRemembersWhatItDidNotSend()
    {
        var d = Seeded(("index.html", "home"), ("old/index.html", "an old page"));
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);
        File.Delete(Path.Combine(d.SiteDir, "old", "index.html"));   // stale on the server now

        var forced = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null, forceFull: true);

        // Forcing decides what is sent, not what is reported as not belonging.
        Assert.Contains("old/index.html", forced.StaleRemote);
        Assert.True(RemoteHas(d.RemoteDir, "old/index.html"));
        Assert.Contains("old/index.html", ManifestPaths(d.RemoteDir));

        // And it is still there to be offered afterwards, rather than stranded and invisible.
        Assert.Contains("old/index.html",
            SftpSyncService.QuickSync(d.SiteDir, d.Profile, null).StaleRemote);
    }

    /// <summary>
    /// A file on the server that isn't in the site is a standing condition, not a one-off event.
    /// Reporting it once and forgetting meant anyone who cancelled, or chose to keep them, or
    /// closed the dialog, lost the offer for good while the file stayed published.
    /// </summary>
    [SkippableFact]
    public void AStaleFile_KeepsBeingReportedUntilItIsDealtWith()
    {
        var d = Seeded(("index.html", "home"), ("_media/figure.webp", "a figure"));
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);
        File.Delete(Path.Combine(d.SiteDir, "_media", "figure.webp"));

        for (var i = 0; i < 3; i++)
        {
            var r = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);
            Assert.Contains("_media/figure.webp", r.StaleRemote);
        }

        // And stops the moment it is actually gone.
        var stale = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null).StaleRemote;
        SftpSyncService.DeleteRemote(d.SiteDir, d.Profile, null, stale);

        Assert.Empty(SftpSyncService.QuickSync(d.SiteDir, d.Profile, null).StaleRemote);
    }

    /// <summary>
    /// A file that failed to upload must still be retried after the stale-file dialog has been
    /// used in the same session.
    /// </summary>
    /// <remarks>
    /// <see cref="SftpSyncService.UploadFiles"/> drops a failed upload from the manifest so the
    /// next sync picks it up again. DeleteRemote used to rebuild the manifest from the local
    /// folder, which put it straight back as though it had arrived — and since the local copy
    /// never changes, its size and mtime kept matching that record and every later Quick Sync
    /// skipped it. The file stayed missing from the server indefinitely, with nothing said.
    /// </remarks>
    [SkippableFact]
    public void AFailedUpload_IsStillRetriedAfterDeletingStaleFiles()
    {
        var d = Seeded(("index.html", "home"), ("gone.html", "temporary"));
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);
        File.Delete(Path.Combine(d.SiteDir, "gone.html"));      // now stale on the server

        // A file that cannot be uploaded: the server has a file where its folder needs to go.
        // Stands in for any transient upload failure — a lock, a dropped link, a quota.
        Write(d.SiteDir, "_media/figure.webp", "a figure");
        File.WriteAllText(Path.Combine(d.RemoteDir, "_media"), "in the way");

        var sync = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);
        Assert.Single(sync.Errors);
        Assert.Contains("gone.html", sync.StaleRemote);

        // The user accepts the stale-file dialog, in that same session.
        SftpSyncService.DeleteRemote(d.SiteDir, d.Profile, null, sync.StaleRemote);
        Assert.False(RemoteHas(d.RemoteDir, "gone.html"));

        // Whatever blocked the upload goes away.
        File.Delete(Path.Combine(d.RemoteDir, "_media"));

        var next = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);
        Assert.Equal(1, next.Uploaded);
        Assert.True(RemoteHas(d.RemoteDir, "_media/figure.webp"));

        // And having arrived, it isn't sent again.
        Assert.Equal(0, SftpSyncService.QuickSync(d.SiteDir, d.Profile, null).Uploaded);
    }

    [SkippableFact]
    public void DeleteRemote_PrunesEmptiedNestedDirectories()
    {
        var d = Seeded(("index.html", "home"));
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);
        // A stray, remote-only file buried two directories deep.
        Directory.CreateDirectory(Path.Combine(d.RemoteDir, "old", "deep"));
        File.WriteAllText(Path.Combine(d.RemoteDir, "old", "deep", "legacy.html"), "x");

        SftpSyncService.DeleteRemote(d.SiteDir, d.Profile, null, ["old/deep/legacy.html"]);

        Assert.False(Directory.Exists(Path.Combine(d.RemoteDir, "old")), "emptied parent dirs should be pruned");
        Assert.True(RemoteHas(d.RemoteDir, "index.html"));
    }

    [SkippableFact]
    public void ForceFull_ReuploadsEverything()
    {
        var d = Seeded(("index.html", "home"), ("a.html", "a"), ("b.html", "b"));
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);

        var r = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null, forceFull: true);

        Assert.Equal(3, r.Uploaded);
    }

    [SkippableFact]
    public void ColdStart_NoRemoteManifest_UploadsAll()
    {
        var d = Seeded(("index.html", "home"), ("a.html", "a"));
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);
        File.Delete(Path.Combine(d.RemoteDir, ".ht-dir2site")); // simulate new machine / lost manifest

        var r = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);

        Assert.Equal(2, r.Uploaded);
        Assert.True(RemoteHas(d.RemoteDir, ".ht-dir2site"));
    }

    [SkippableFact]
    public void LocalDeletion_VerifyReportsStale()
    {
        var d = Seeded(("index.html", "home"), ("css/site.css", "body{}"));
        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);

        File.Delete(Path.Combine(d.SiteDir, "css", "site.css")); // removed locally

        var r = SftpSyncService.VerifyAndRepair(d.SiteDir, d.Profile, null);

        Assert.Contains("css/site.css", r.StaleRemote);
    }

    [SkippableFact]
    public void Sync_HandlesUnicodePathsAndBinaryContent()
    {
        var d = Seeded(("café/résumé.html", "<h1>café</h1>"));
        var blob = new byte[2048];
        new Random(1234).NextBytes(blob);
        var blobPath = Path.Combine(d.SiteDir, "img", "blob.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
        File.WriteAllBytes(blobPath, blob);

        var r1 = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);
        Assert.Equal(2, r1.Uploaded);
        Assert.True(RemoteHas(d.RemoteDir, "café/résumé.html"));
        Assert.Equal(blob, File.ReadAllBytes(Path.Combine(d.RemoteDir, "img", "blob.bin")));

        // Unicode keys must round-trip through the manifest so a re-sync is a no-op.
        var r2 = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);
        Assert.Equal(0, r2.Uploaded);
    }

    [SkippableFact]
    public void CustomManifestPath_IsKeptOutOfDeployedTree()
    {
        Skip.IfNot(_fx.Available, _fx.Reason);
        var d = _fx.NewDeployment();
        d.Profile.ManifestPath = "/manifests/" + Guid.NewGuid().ToString("N") + ".json";
        Write(d.SiteDir, "index.html", "home");

        SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);

        Assert.True(File.Exists(_fx.LocalPathFor(d.Profile.ManifestPath)));
        Assert.False(RemoteHas(d.RemoteDir, ".ht-dir2site")); // not in the web root
        Assert.Equal(0, SftpSyncService.QuickSync(d.SiteDir, d.Profile, null).Uploaded); // custom manifest is used
    }

    [SkippableFact]
    public void TestConnection_WithValidKey_Succeeds()
    {
        Skip.IfNot(_fx.Available, _fx.Reason);
        var d = _fx.NewDeployment();
        SftpSyncService.TestConnection(d.Profile, null); // must not throw
    }

    [SkippableFact]
    public void TestConnection_WithWrongKey_Throws()
    {
        Skip.IfNot(_fx.Available, _fx.Reason);
        var d = _fx.NewDeployment();
        d.Profile.PrivateKeyPath = _fx.WrongKeyPath;

        Assert.ThrowsAny<Exception>(() => SftpSyncService.TestConnection(d.Profile, null));
    }

    // ---- host key verification ---------------------------------------------

    [SkippableFact]
    public void HostKeyFingerprint_MatchesWhatSshKeygenReports()
    {
        Skip.IfNot(_fx.Available, _fx.Reason);
        // Guards the fingerprint format: if ours drifts from OpenSSH's, a user comparing the
        // prompt against `ssh-keygen -lf` would see a mismatch and distrust a genuine server.
        Assert.StartsWith("SHA256:", _fx.HostKeyFingerprint);
        Assert.Equal(_fx.SshKeygenHostKeyFingerprint(), _fx.HostKeyFingerprint);
    }

    [SkippableFact]
    public void UnknownHostKey_WithNoVerifier_IsRefused()
    {
        Skip.IfNot(_fx.Available, _fx.Reason);
        var d = _fx.NewDeployment();
        d.Profile.HostKeyFingerprint = ""; // never trusted before

        // Nobody can answer the question, so this must fail closed rather than silently
        // trusting whatever the server offered.
        Assert.ThrowsAny<Exception>(() => SftpSyncService.TestConnection(d.Profile, null));
    }

    [SkippableFact]
    public void UnknownHostKey_WhenVerifierAccepts_ConnectsAndPinsFingerprint()
    {
        Skip.IfNot(_fx.Available, _fx.Reason);
        var d = _fx.NewDeployment();
        d.Profile.HostKeyFingerprint = "";

        var verifier = new FakeVerifier(accept: true);
        SftpSyncService.TestConnection(d.Profile, null, verifier);

        Assert.NotNull(verifier.Seen);
        Assert.False(verifier.Seen!.IsChanged);   // first contact, not a key change
        Assert.Null(verifier.Seen.KnownFingerprint);
        Assert.Equal(_fx.HostKeyFingerprint, verifier.Seen.Fingerprint);
        Assert.Equal(_fx.HostKeyFingerprint, d.Profile.HostKeyFingerprint); // so we don't ask again
    }

    [SkippableFact]
    public void UnknownHostKey_WhenVerifierDeclines_IsRefusedAndNotPinned()
    {
        Skip.IfNot(_fx.Available, _fx.Reason);
        var d = _fx.NewDeployment();
        d.Profile.HostKeyFingerprint = "";

        Assert.Throws<SftpHostKeyRejectedException>(
            () => SftpSyncService.TestConnection(d.Profile, null, new FakeVerifier(accept: false)));
        Assert.Equal("", d.Profile.HostKeyFingerprint);
    }

    [SkippableFact]
    public void ChangedHostKey_IsReportedAsChanged_AndRefusedWhenDeclined()
    {
        Skip.IfNot(_fx.Available, _fx.Reason);
        var d = _fx.NewDeployment();
        const string stale = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        d.Profile.HostKeyFingerprint = stale;

        var verifier = new FakeVerifier(accept: false);
        var ex = Assert.Throws<SftpHostKeyRejectedException>(
            () => SftpSyncService.TestConnection(d.Profile, null, verifier));

        Assert.NotNull(verifier.Seen);
        Assert.True(verifier.Seen!.IsChanged);              // flagged as a change, not first contact
        Assert.Equal(stale, verifier.Seen.KnownFingerprint);
        Assert.Contains("CHANGED", ex.Message);
        Assert.Equal(stale, d.Profile.HostKeyFingerprint);  // declining must not overwrite the pin
    }

    [SkippableFact]
    public void PinnedHostKey_ConnectsWithoutConsultingVerifier()
    {
        Skip.IfNot(_fx.Available, _fx.Reason);
        var d = _fx.NewDeployment();   // fixture pins the server's real fingerprint

        var verifier = new FakeVerifier(accept: true);
        SftpSyncService.TestConnection(d.Profile, null, verifier);

        Assert.False(verifier.WasAsked); // an already-trusted key must not re-prompt on every sync
    }

    [SkippableFact]
    public void ChangedHostKey_WhenAccepted_IsRepinnedToTheNewKey()
    {
        Skip.IfNot(_fx.Available, _fx.Reason);
        var d = _fx.NewDeployment();
        const string stale = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        d.Profile.HostKeyFingerprint = stale;

        // A legitimately rebuilt server: the user is warned, accepts, and must not be asked again.
        var verifier = new FakeVerifier(accept: true);
        SftpSyncService.TestConnection(d.Profile, null, verifier);

        Assert.True(verifier.Seen!.IsChanged);
        Assert.Equal(_fx.HostKeyFingerprint, d.Profile.HostKeyFingerprint);
        Assert.NotEqual(stale, d.Profile.HostKeyFingerprint);
    }

    /// <summary>Answers a fixed way and records what it was shown.</summary>
    private sealed class FakeVerifier(bool accept) : IHostKeyVerifier
    {
        public HostKeyInfo? Seen { get; private set; }
        public bool WasAsked => Seen is not null;

        public bool Verify(HostKeyInfo info)
        {
            Seen = info;
            return accept;
        }
    }
}
