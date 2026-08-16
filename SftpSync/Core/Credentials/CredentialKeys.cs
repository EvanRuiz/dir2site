// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace dir2site.SftpSync.Core.Credentials;

/// <summary>
/// Decides where a target's secret lives in the credential store, by asking what the secret
/// actually belongs to.
/// </summary>
/// <remarks>
/// A password belongs to an account on a server, so it is addressed by host and username — the same
/// way <c>ssh</c> and every password manager think about it. A key passphrase belongs to the key
/// pair it unlocks, so it is addressed by that key's fingerprint.
///
/// Neither is addressed by the project, the port, or the target's name. Keying on those meant the
/// same server's password was stored once per project and lost whenever any of them changed:
/// moving a project, opening it by a differently-cased path, or moving SSH to port 2222 each
/// orphaned the password with no way to reach it again. <see cref="Legacy"/> exists only so those
/// existing entries can be found once and moved.
/// </remarks>
public static class CredentialKeys
{
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    /// <summary>Where a server password lives: with the account it opens.</summary>
    /// <remarks>
    /// Hostnames are case-insensitive, so they are folded. Usernames are not — POSIX accounts are
    /// case-sensitive, and folding them would merge two genuinely different logins.
    /// </remarks>
    public static string ForPassword(string host, string username) =>
        Hash($"password|{host.Trim().ToLowerInvariant()}|{username.Trim()}");

    /// <summary>
    /// Where a key passphrase lives: with the key pair it decrypts. Null when there is no key to
    /// identify — no file chosen yet, or one we can't currently read.
    /// </summary>
    /// <remarks>
    /// Null rather than a key over the empty identity. Hashing "nothing" would give every key-auth
    /// target with no readable key file the same entry, so the first one to save would hand its
    /// passphrase to all the others. A target with no key file has no passphrase to store anyway.
    /// </remarks>
    public static string? ForPassphrase(string privateKeyPath)
    {
        var identity = PrivateKeyIdentity.For(privateKeyPath);
        return string.IsNullOrEmpty(identity) ? null : Hash($"passphrase|{identity}");
    }

    /// <summary>
    /// The key for whichever secret this profile uses, or null when it has nowhere to keep one yet.
    /// </summary>
    public static string? For(SftpProfile profile) =>
        profile.AuthMethod == SftpAuthMethod.Key
            ? ForPassphrase(profile.PrivateKeyPath)
            : ForPassword(profile.Host, profile.Username);

    /// <summary>
    /// The pre-existing key: a hash of project path, host, port and username. Read-only — used to
    /// find a secret saved by an older build so it can be moved to its proper home. The exact
    /// string matters, so this must keep producing byte-identical output.
    /// </summary>
    public static string Legacy(string projectRoot, SftpProfile profile) =>
        Hash($"{NormalizeProject(projectRoot)}|{profile.Host}|{profile.Port}|{profile.Username}");

    private static string NormalizeProject(string projectRoot) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
}
