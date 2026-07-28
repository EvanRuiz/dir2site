// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
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
        Assert.True(RemoteHas(d.RemoteDir, ".dir2site-manifest.json"));
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
        Assert.DoesNotContain(".dir2site-manifest.json", r.StaleRemote); // manifest must be ignored
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
        File.Delete(Path.Combine(d.RemoteDir, ".dir2site-manifest.json")); // simulate new machine / lost manifest

        var r = SftpSyncService.QuickSync(d.SiteDir, d.Profile, null);

        Assert.Equal(2, r.Uploaded);
        Assert.True(RemoteHas(d.RemoteDir, ".dir2site-manifest.json"));
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
        Assert.False(RemoteHas(d.RemoteDir, ".dir2site-manifest.json")); // not in the web root
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
