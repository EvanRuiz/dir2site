// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using dir2site.SftpSync.Core;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// A key passphrase is addressed by the key pair's fingerprint, so that moving, renaming or
/// re-encrypting the file doesn't orphan it. These check our reading of <c>openssh-key-v1</c>
/// against what OpenSSH itself reports, since agreeing with <c>ssh-keygen -lf</c> is the whole
/// claim — and check it without ever supplying the passphrase, which is what makes lookup possible.
/// </summary>
public class PrivateKeyIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "d2s-keyid-" + Guid.NewGuid().ToString("N"));

    private static readonly string? Keygen = FindKeygen();

    public PrivateKeyIdentityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string? FindKeygen()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh-keygen.exe")]
            : ["/usr/bin/ssh-keygen", "/usr/local/bin/ssh-keygen", "/opt/homebrew/bin/ssh-keygen"];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string Keygen_(params string[] args)
    {
        var psi = new ProcessStartInfo(Keygen!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return stdout;
    }

    /// <summary>The fingerprint OpenSSH reports, which is the string we have to agree with.</summary>
    private static string SshKeygenFingerprint(string path) =>
        Keygen_("-lf", path)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .First(f => f.StartsWith("SHA256:", StringComparison.Ordinal));

    // "-m PEM" refuses a passphrase shorter than five characters, and ssh-keygen reports that on
    // stderr while still exiting quietly under -q. A test whose key was never written would then
    // fall back to the path and assert that happily, so check the file landed.
    private string Generate(string name, string passphrase, string type = "ed25519", params string[] extra)
    {
        var path = Path.Combine(_dir, name);
        string[] args = ["-q", "-t", type, "-N", passphrase, "-C", "test-key", "-f", path, .. extra];
        Keygen_(args);

        Assert.True(File.Exists(path), $"ssh-keygen did not write {path}; the test would prove nothing.");
        return path;
    }

    [SkippableFact]
    public void AnEncryptedKey_IsIdentifiedWithoutItsPassphrase()
    {
        Skip.If(Keygen is null, "ssh-keygen not found on this platform.");

        var path = Generate("encrypted", "a-real-passphrase");
        // The .pub sibling is what ssh-keygen wrote; delete it so only the private key can answer.
        File.Delete(path + ".pub");

        var identity = PrivateKeyIdentity.For(path);

        Assert.True(PrivateKeyIdentity.IsFingerprint(identity));
        Assert.Equal(SshKeygenFingerprint(Regenerate(path)), identity);
    }

    [SkippableFact]
    public void AnUnencryptedKey_IsIdentifiedTheSameWay()
    {
        Skip.If(Keygen is null, "ssh-keygen not found on this platform.");

        var path = Generate("plain", "");
        var expected = SshKeygenFingerprint(path + ".pub");
        File.Delete(path + ".pub");

        Assert.Equal(expected, PrivateKeyIdentity.For(path));
    }

    [SkippableFact]
    public void AnRsaKey_IsIdentifiedToo()
    {
        Skip.If(Keygen is null, "ssh-keygen not found on this platform.");

        var path = Generate("rsa", "pw", "rsa", "-b", "2048");
        var expected = SshKeygenFingerprint(path + ".pub");
        File.Delete(path + ".pub");

        Assert.Equal(expected, PrivateKeyIdentity.For(path));
    }

    [SkippableFact]
    public void MovingOrRenamingTheFile_DoesNotChangeTheIdentity()
    {
        Skip.If(Keygen is null, "ssh-keygen not found on this platform.");

        var path = Generate("original", "pw");
        File.Delete(path + ".pub");
        var before = PrivateKeyIdentity.For(path);

        var moved = Path.Combine(_dir, "renamed-and-moved");
        File.Move(path, moved);

        // This is the point of fingerprinting rather than keying on the path.
        Assert.Equal(before, PrivateKeyIdentity.For(moved));
    }

    [SkippableFact]
    public void ChangingThePassphrase_DoesNotChangeTheIdentity()
    {
        Skip.If(Keygen is null, "ssh-keygen not found on this platform.");

        var path = Generate("rotating", "old-passphrase");
        File.Delete(path + ".pub");
        var before = PrivateKeyIdentity.For(path);

        // Re-encrypting rewrites the whole file, so a hash of its bytes would change here — and
        // the passphrase entry would be orphaned every time someone rotated it.
        Keygen_("-q", "-p", "-P", "old-passphrase", "-N", "new-passphrase", "-f", path);

        Assert.Equal(before, PrivateKeyIdentity.For(path));
        Assert.NotEqual(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))),
            string.Empty);
    }

    [SkippableFact]
    public void ALegacyPemKey_FallsBackToThePath_RatherThanFailing()
    {
        Skip.If(Keygen is null, "ssh-keygen not found on this platform.");

        // "-m PEM" with a passphrase encrypts the whole body, so there is no public half to read.
        var path = Generate("legacy", "pem-needs-five", "rsa", "-b", "2048", "-m", "PEM");
        File.Delete(path + ".pub");

        var identity = PrivateKeyIdentity.For(path);

        Assert.False(PrivateKeyIdentity.IsFingerprint(identity));
        Assert.False(string.IsNullOrEmpty(identity));
    }

    [SkippableFact]
    public void ThePublicSibling_AnswersWhenThePrivateKeyCannotBeRead()
    {
        Skip.If(Keygen is null, "ssh-keygen not found on this platform.");

        var path = Generate("with-sibling", "pem-needs-five", "rsa", "-b", "2048", "-m", "PEM");
        var expected = SshKeygenFingerprint(path + ".pub");

        // The legacy private key gives us nothing, but its .pub sits right next to it.
        Assert.Equal(expected, PrivateKeyIdentity.For(path));
    }

    [Fact]
    public void AnEmptyPathHasNoIdentity()
    {
        Assert.Equal(string.Empty, PrivateKeyIdentity.For(""));
        Assert.Equal(string.Empty, PrivateKeyIdentity.For("   "));
    }

    [Fact]
    public void AMissingFileHasNoIdentityAtAll()
    {
        // Not a path fallback: see the round trip below for why inventing one would lose the
        // passphrase as soon as the file turned up.
        Assert.Equal(string.Empty, PrivateKeyIdentity.For(Path.Combine(_dir, "does-not-exist")));
    }

    [SkippableFact]
    public void AKeyThatAppearsLater_DoesNotChangeIdentityUnderneathItsPassphrase()
    {
        Skip.If(Keygen is null, "ssh-keygen not found on this platform.");

        // The unmounted-volume case. If an absent file were given a path-based identity, the
        // passphrase would be filed under it, and mounting the volume would silently move the
        // identity to the fingerprint — leaving the stored passphrase unreachable.
        var path = Path.Combine(_dir, "appears-later");
        Assert.Equal(string.Empty, PrivateKeyIdentity.For(path));

        var generated = Generate("appears-later", "a-real-passphrase");
        Assert.Equal(path, generated);

        Assert.True(PrivateKeyIdentity.IsFingerprint(PrivateKeyIdentity.For(path)));
    }

    // Only used where the .pub was deleted: regenerate it from the private key to ask ssh-keygen
    // what it thinks, which needs the passphrase — fine in a test, never in the app.
    private string Regenerate(string privateKeyPath)
    {
        var pub = Keygen_("-y", "-P", "a-real-passphrase", "-f", privateKeyPath);
        var path = privateKeyPath + ".regenerated.pub";
        File.WriteAllText(path, pub);
        return path;
    }
}
