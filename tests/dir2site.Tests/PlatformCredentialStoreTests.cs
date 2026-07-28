// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using dir2site.SftpSync.Core.Credentials;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Round-trips for the OS-backed credential stores. Each skips off its own platform, so the
/// CI matrix is what gives these coverage: Windows exercises DPAPI, macOS the Keychain.
/// </summary>
public class PlatformCredentialStoreTests : IDisposable
{
    // Distinctive enough to identify as test residue if a run is killed before cleanup.
    private const string KeyPrefix = "d2s-test-";

    private readonly string _dir;

    public PlatformCredentialStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "d2s-plat-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---- Windows (DPAPI) ----------------------------------------------------

    [SkippableFact]
    public void Windows_RoundTripsIncludingUnicodeAndEmpty()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is Windows-only.");
        // Unreachable — Skip.IfNot throws — but the CA1416 analyzer doesn't recognise it as a
        // platform guard, so the narrowing check has to be visible in the code.
        if (!OperatingSystem.IsWindows()) return;

        var store = new WindowsCredentialStore(_dir);

        store.Set(KeyPrefix + "plain", "hunter2");
        Assert.Equal("hunter2", store.Get(KeyPrefix + "plain"));

        // Passphrases are free text; a non-ASCII one must survive the encrypt/decrypt round trip.
        store.Set(KeyPrefix + "unicode", "pä sswörd→✓");
        Assert.Equal("pä sswörd→✓", store.Get(KeyPrefix + "unicode"));

        store.Set(KeyPrefix + "empty", "");
        Assert.Equal("", store.Get(KeyPrefix + "empty"));

        Assert.True(store.IsSecure);
    }

    [SkippableFact]
    public void Windows_OverwriteDeleteAndMissingKeyBehaveLikeTheOtherStores()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is Windows-only.");
        if (!OperatingSystem.IsWindows()) return;

        var store = new WindowsCredentialStore(_dir);

        Assert.Null(store.Get(KeyPrefix + "absent"));

        store.Set(KeyPrefix + "k", "first");
        store.Set(KeyPrefix + "k", "second");
        Assert.Equal("second", store.Get(KeyPrefix + "k"));

        store.Delete(KeyPrefix + "k");
        Assert.Null(store.Get(KeyPrefix + "k"));

        store.Delete(KeyPrefix + "k"); // deleting what isn't there must not throw
    }

    [SkippableFact]
    public void Windows_SecretIsNotStoredInPlaintext()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is Windows-only.");
        if (!OperatingSystem.IsWindows()) return;

        const string secret = "correct-horse-battery-staple";
        new WindowsCredentialStore(_dir).Set(KeyPrefix + "k", secret);

        var onDisk = File.ReadAllBytes(Directory.GetFiles(_dir)[0]);
        Assert.DoesNotContain(secret, System.Text.Encoding.UTF8.GetString(onDisk), StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Windows_CipherIsScopedToThisUser_SoAnotherAccountCannotRead()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DPAPI is Windows-only.");
        if (!OperatingSystem.IsWindows()) return;

        // Can't impersonate another account here, so assert the property that makes the scope
        // meaningful: the blob is not a deterministic function of the plaintext, i.e. it is
        // genuinely keyed rather than encoded.
        var store = new WindowsCredentialStore(_dir);
        store.Set(KeyPrefix + "a", "same-secret");
        var first = File.ReadAllBytes(Path.Combine(_dir, KeyPrefix + "a.bin"));
        store.Set(KeyPrefix + "b", "same-secret");
        var second = File.ReadAllBytes(Path.Combine(_dir, KeyPrefix + "b.bin"));

        Assert.NotEqual(first, second);
    }

    // ---- macOS (login Keychain) ---------------------------------------------
    //
    // These touch the real login Keychain — MacCredentialStore has no way to target another —
    // so they run only when DIR2SITE_TEST_KEYCHAIN=1, which CI sets on the macOS runner. That
    // keeps a plain local `dotnet test` from writing to a developer's own keychain.

    private static bool MacKeychainTestsEnabled =>
        OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("DIR2SITE_TEST_KEYCHAIN") == "1";

    [SkippableFact]
    public void Mac_RoundTripsAwkwardSecrets()
    {
        Skip.IfNot(MacKeychainTestsEnabled, "Set DIR2SITE_TEST_KEYCHAIN=1 to run login-keychain tests.");
        if (!OperatingSystem.IsMacOS()) return;

        var store = new MacCredentialStore();
        var key = KeyPrefix + Guid.NewGuid().ToString("N");

        // The secret reaches `security` through a command line it re-splits itself, and comes
        // back through a text report that hex-encodes anything non-ASCII. Both directions have
        // to survive quoting, backslashes, shell metacharacters and unicode.
        string[] secrets =
        [
            "hunter2",
            "correct horse battery",
            "he said \"hi\"",
            "back\\slash",
            "a$b`c",
            "pä sswörd→✓",
            "!@#%^&*()[]{};:,.<>/?|~=+-_",
            "",
        ];

        try
        {
            foreach (var secret in secrets)
            {
                store.Set(key, secret);
                Assert.Equal(secret, store.Get(key));
            }
        }
        finally
        {
            store.Delete(key);
        }

        Assert.Null(store.Get(key));
        Assert.True(store.IsSecure);
    }

    [SkippableFact]
    public void Mac_MissingKeyReturnsNull_AndDeletingItDoesNotThrow()
    {
        Skip.IfNot(MacKeychainTestsEnabled, "Set DIR2SITE_TEST_KEYCHAIN=1 to run login-keychain tests.");
        if (!OperatingSystem.IsMacOS()) return;

        var store = new MacCredentialStore();
        var key = KeyPrefix + Guid.NewGuid().ToString("N");

        Assert.Null(store.Get(key));
        store.Delete(key);
    }
}
