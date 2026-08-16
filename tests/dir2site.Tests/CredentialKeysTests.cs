// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using dir2site.SftpSync.Core;
using dir2site.SftpSync.Core.Credentials;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// A secret is addressed by what it belongs to: a password by the server account it opens, a
/// passphrase by the key pair it decrypts. These pin the drift that keying on the project path,
/// the port and the target's identity used to cause — each of these was a way to silently lose a
/// saved password with no way to reach it again.
/// </summary>
public class CredentialKeysTests
{
    private static SftpProfile Password(string host = "example.com", string user = "deploy", int port = 22) =>
        new() { Host = host, Username = user, Port = port, AuthMethod = SftpAuthMethod.Password };

    [Fact]
    public void TheSameAccountGivesTheSameKey()
    {
        Assert.Equal(CredentialKeys.For(Password()), CredentialKeys.For(Password()));
    }

    [Fact]
    public void ChangingThePort_KeepsTheKey()
    {
        // Moving SSH to 2222 is the same account on the same machine. Nobody keeps a different
        // password per port, and including it meant the password vanished when someone did this.
        Assert.Equal(
            CredentialKeys.For(Password(port: 22)),
            CredentialKeys.For(Password(port: 2222)));
    }

    [Fact]
    public void TheProjectIsNotPartOfTheKey()
    {
        // Two projects deploying to one server share the password, so changing it once is enough —
        // and moving or re-casing a project's directory can no longer orphan it.
        var shared = CredentialKeys.ForPassword("example.com", "deploy");

        Assert.Equal(shared, CredentialKeys.For(Password()));
        Assert.Equal(shared, CredentialKeys.ForPassword("example.com", "deploy"));
    }

    [Fact]
    public void HostCaseAndSurroundingSpaceDoNotMatter()
    {
        // Hostnames are case-insensitive, and a pasted host often arrives with whitespace.
        Assert.Equal(
            CredentialKeys.ForPassword("example.com", "deploy"),
            CredentialKeys.ForPassword("  EXAMPLE.CoM  ", " deploy "));
    }

    [Fact]
    public void UsernameCaseDoesMatter()
    {
        // POSIX accounts are case-sensitive: "deploy" and "Deploy" are two different logins, and
        // folding them would hand one account's password to the other.
        Assert.NotEqual(
            CredentialKeys.ForPassword("example.com", "deploy"),
            CredentialKeys.ForPassword("example.com", "Deploy"));
    }

    [Fact]
    public void DifferentAccountsOrHostsGetDifferentKeys()
    {
        var baseline = CredentialKeys.ForPassword("example.com", "deploy");

        Assert.NotEqual(baseline, CredentialKeys.ForPassword("other.example.com", "deploy"));
        Assert.NotEqual(baseline, CredentialKeys.ForPassword("example.com", "someoneelse"));
    }

    [Fact]
    public void APassphraseIsNotStoredWhereAPasswordWouldBe()
    {
        // They are different secrets. Sharing a slot meant a key-auth target and a password-auth
        // target on the same account overwrote each other, and the loser was handed a passphrase
        // to use as a password.
        var profile = Password();
        var keyAuth = new SftpProfile
        {
            Host = profile.Host,
            Username = profile.Username,
            AuthMethod = SftpAuthMethod.Key,
            PrivateKeyPath = "/home/me/.ssh/id_ed25519",
        };

        Assert.NotEqual(CredentialKeys.For(profile), CredentialKeys.For(keyAuth));
    }

    [Fact]
    public void APassphraseKeyIgnoresTheServerEntirely()
    {
        // One key routinely opens many hosts; its passphrase is one secret, stored once.
        var a = new SftpProfile { Host = "a.invalid", Username = "one", AuthMethod = SftpAuthMethod.Key, PrivateKeyPath = "/k/id_ed25519" };
        var b = new SftpProfile { Host = "b.invalid", Username = "two", AuthMethod = SftpAuthMethod.Key, PrivateKeyPath = "/k/id_ed25519" };

        Assert.Equal(CredentialKeys.For(a), CredentialKeys.For(b));
    }

    [Fact]
    public void TargetsWithNoReadableKeyFile_DoNotShareAPassphraseSlot()
    {
        // The dangerous shape: hashing an empty identity would give every key-auth target with no
        // key file the same entry, so the first to save would hand its passphrase to all of them.
        // A key path is per-machine and kept out of the yaml, so a freshly cloned project with
        // "auth: key" starts here.
        var a = new SftpProfile { Host = "a.invalid", Username = "one", AuthMethod = SftpAuthMethod.Key, PrivateKeyPath = "" };
        var b = new SftpProfile { Host = "b.invalid", Username = "two", AuthMethod = SftpAuthMethod.Key, PrivateKeyPath = "" };

        Assert.Null(CredentialKeys.For(a));
        Assert.Null(CredentialKeys.For(b));
    }

    [Fact]
    public void AKeyFileThatIsNotThereRightNow_HasNoKeyRatherThanAPathKey()
    {
        // An unmounted volume or a path typed before the file was copied over. Minting a
        // path-based key here would orphan the passphrase the moment the file appeared and the
        // identity became its fingerprint.
        var missing = new SftpProfile
        {
            Host = "a.invalid",
            Username = "one",
            AuthMethod = SftpAuthMethod.Key,
            PrivateKeyPath = Path.Combine(Path.GetTempPath(), "d2s-not-here-" + Guid.NewGuid().ToString("N")),
        };

        Assert.Null(CredentialKeys.For(missing));
    }

    [Fact]
    public void TheLegacyKeyStillReproducesTheOldHash()
    {
        // Migration reads this exact string, so it has to keep matching what older builds wrote.
        var projectRoot = Path.Combine(Path.GetTempPath(), "d2s-legacy-shape");
        var profile = Password();

        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                $"{Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot))}|example.com|22|deploy")))[..16];

        Assert.Equal(expected, CredentialKeys.Legacy(projectRoot, profile));
    }

    [Fact]
    public void TheLegacyKeyIsNotTheNewOne()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "d2s-legacy-differs");
        var profile = Password();

        Assert.NotEqual(CredentialKeys.Legacy(projectRoot, profile), CredentialKeys.For(profile));
    }
}
