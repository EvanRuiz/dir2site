// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using dir2site.SftpSync.Core;
using dir2site.SftpSync.Core.Credentials;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Existing installs have their secrets under the old project|host|port|user key. Changing where
/// secrets live has to move them, not orphan them — otherwise this fix would itself lose the
/// password it exists to stop losing.
/// </summary>
public class LegacyCredentialMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "d2s-migrate-" + Guid.NewGuid().ToString("N"));
    private readonly string _project = Path.Combine(
        Path.GetTempPath(), "d2s-migrate-proj-" + Guid.NewGuid().ToString("N"));

    private readonly EncryptedFileCredentialStore _store;

    public LegacyCredentialMigrationTests()
    {
        Directory.CreateDirectory(_project);
        _store = new EncryptedFileCredentialStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { Directory.Delete(_project, recursive: true); } catch { }
    }

    private static SftpProfile Profile(int port = 22) => new()
    {
        Host = "example.com", Username = "deploy", Port = port, AuthMethod = SftpAuthMethod.Password,
    };

    [Fact]
    public void ASecretUnderTheOldKey_IsFoundAndCopiedForward()
    {
        var profile = Profile();
        _store.Set(CredentialKeys.Legacy(_project, profile), "s3cret");

        var result = TargetSecret.Read(_store, _project, profile);

        Assert.Equal(CredentialStatus.Found, result.Status);
        Assert.Equal("s3cret", result.Secret);
        Assert.Equal("s3cret", _store.Get(CredentialKeys.For(profile)));

        // Copied, not moved. Removing the original is the one destructive step available here and
        // it gains nothing, while breaking a downgrade to a build that reads the legacy key.
        Assert.Equal("s3cret", _store.Get(CredentialKeys.Legacy(_project, profile)));
    }

    [Fact]
    public void MigrationIsIdempotent()
    {
        var profile = Profile();
        _store.Set(CredentialKeys.Legacy(_project, profile), "s3cret");

        TargetSecret.Read(_store, _project, profile);
        var again = TargetSecret.Read(_store, _project, profile);

        Assert.Equal("s3cret", again.Secret);
    }

    [Fact]
    public void AfterMigrating_TheOldPortNoLongerMatters()
    {
        // The whole point: the legacy key included the port, the new one doesn't. Once moved, the
        // secret survives the very edit that used to lose it.
        _store.Set(CredentialKeys.Legacy(_project, Profile()), "s3cret");
        TargetSecret.Read(_store, _project, Profile());

        Assert.Equal("s3cret", TargetSecret.Read(_store, _project, Profile(port: 2222)).Secret);
    }

    [Fact]
    public void ANewKeyWins_AndTheLegacyEntryIsLeftAlone()
    {
        var profile = Profile();
        _store.Set(CredentialKeys.For(profile), "current");
        _store.Set(CredentialKeys.Legacy(_project, profile), "stale");

        Assert.Equal("current", TargetSecret.Read(_store, _project, profile).Secret);
        Assert.Equal("stale", _store.Get(CredentialKeys.Legacy(_project, profile)));
    }

    [Fact]
    public void AHitAtTheAccountKey_CostsOneStoreRead()
    {
        // Every deploy goes through Read, and on macOS and Linux each store read is a subprocess.
        // A hit at the account key settles it on its own; the legacy key is only worth consulting
        // when nothing is there yet.
        var profile = Profile();
        var counting = new CountingStore(_store);
        counting.Set(CredentialKeys.For(profile)!, "s3cret");
        counting.Reads = 0;

        Assert.Equal("s3cret", TargetSecret.Read(counting, _project, profile).Secret);
        Assert.Equal(1, counting.Reads);

        // A miss does need both: the account key, then the legacy key migration reads from.
        var unsaved = new SftpProfile
        {
            Host = "nothing-saved.invalid", Username = "nobody", AuthMethod = SftpAuthMethod.Password,
        };
        counting.Reads = 0;
        TargetSecret.Read(counting, _project, unsaved);
        Assert.Equal(2, counting.Reads);
    }

    private sealed class CountingStore(ICredentialStore inner) : ICredentialStore
    {
        public int Reads;
        public bool IsSecure => inner.IsSecure;
        public CredentialResult Read(string key) { Reads++; return inner.Read(key); }
        public string? Get(string key) => Read(key).Secret;
        public void Set(string key, string secret) => inner.Set(key, secret);
        public void Delete(string key) => inner.Delete(key);
    }

    [Fact]
    public void ClearingASecretSticks_EvenThoughTheLegacyCopySurvives()
    {
        // Migration keys on "nothing stored here", so removing the entry outright reads as never
        // having had one, and the next open copies the old value back out of the legacy key —
        // silently undoing the clearing on exactly the installs migration exists for.
        var profile = Profile();
        _store.Set(CredentialKeys.Legacy(_project, profile), "old-password");
        Assert.Equal("old-password", TargetSecret.Read(_store, _project, profile).Secret);

        // What the dialog does when the user empties the box: record an empty secret.
        _store.Set(CredentialKeys.For(profile)!, string.Empty);

        var reopened = TargetSecret.Read(_store, _project, profile);
        Assert.Equal(CredentialStatus.Found, reopened.Status);
        Assert.Equal(string.Empty, reopened.Secret);

        // Still empty on the read after that, and the legacy copy is untouched for a downgrade.
        Assert.Equal(string.Empty, TargetSecret.Read(_store, _project, profile).Secret);
        Assert.Equal("old-password", _store.Get(CredentialKeys.Legacy(_project, profile)));
    }

    [Fact]
    public void NothingStoredAnywhere_IsStillNotFound()
    {
        var result = TargetSecret.Read(_store, _project, Profile());

        Assert.Equal(CredentialStatus.NotFound, result.Status);
        Assert.Null(result.Error);
    }

    [Fact]
    public void AnUnreadableLegacyEntry_IsReportedRatherThanTreatedAsAbsent()
    {
        var profile = Profile();
        _store.Set(CredentialKeys.Legacy(_project, profile), "s3cret");

        var file = Assert.Single(Directory.GetFiles(_dir, "*.aes"));
        File.WriteAllBytes(file, [7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7]);

        var result = TargetSecret.Read(_store, _project, profile);

        // Reporting NotFound here would let the dialog show an empty box and then overwrite it.
        Assert.Equal(CredentialStatus.Failed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void AnUnreadableCurrentEntry_IsNotOverwrittenFromTheLegacyOne()
    {
        var profile = Profile();
        _store.Set(CredentialKeys.For(profile), "current-but-broken");

        var file = Assert.Single(Directory.GetFiles(_dir, "*.aes"));
        File.WriteAllBytes(file, [8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8]);
        _store.Set(CredentialKeys.Legacy(_project, profile), "older");

        var result = TargetSecret.Read(_store, _project, profile);

        // Something is there and broken. Quietly replacing it with the older entry would lose
        // whichever one the user actually meant.
        Assert.Equal(CredentialStatus.Failed, result.Status);
        Assert.Equal("older", _store.Get(CredentialKeys.Legacy(_project, profile)));
    }
}
