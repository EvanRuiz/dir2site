// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace dir2site.SftpSync.Core;

/// <summary>How the SFTP connection authenticates.</summary>
public enum SftpAuthMethod
{
    Password,
    Key,
}

/// <summary>
/// Non-secret SFTP connection settings. Persisted by <see cref="SftpProfileStore"/>.
/// Secrets (password / key passphrase) are never stored here — they live in the
/// platform credential store (see <c>Credentials/ICredentialStore</c>).
/// </summary>
public sealed class SftpProfile
{
    public string Host { get; set; } = string.Empty;
    public int    Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;

    /// <summary>Remote directory that the contents of <c>_site/</c> are written into.</summary>
    public string RemotePath { get; set; } = string.Empty;

    /// <summary>
    /// Optional explicit path for the manifest file. When empty, the manifest is written as
    /// <c>.dir2site-manifest.json</c> inside <see cref="RemotePath"/>. Set this to a location
    /// outside the public web root to avoid serving it.
    /// </summary>
    public string ManifestPath { get; set; } = string.Empty;

    public SftpAuthMethod AuthMethod { get; set; } = SftpAuthMethod.Password;

    /// <summary>Local path to the SSH private key file (only used when <see cref="AuthMethod"/> is Key).</summary>
    public string PrivateKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// The server host key this profile trusts, as <c>SHA256:base64</c> (the OpenSSH format).
    /// Empty until the user accepts a key. Once set, a server presenting a different key is
    /// refused until the user explicitly accepts the change — this is what makes the password
    /// safe to send, since without it any machine on the path could impersonate the server.
    /// </summary>
    public string HostKeyFingerprint { get; set; } = string.Empty;
}
