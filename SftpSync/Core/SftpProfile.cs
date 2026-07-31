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
    /// <c>.ht-dir2site</c> inside <see cref="RemotePath"/>. Set this to a location
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

    /// <summary>
    /// How many connections upload at once. A site is mostly small files, and each one costs a
    /// couple of serialized round trips, so the wall-clock time is latency times file count rather
    /// than anything to do with bandwidth — running several in parallel is most of the win.
    ///
    /// Raise it for a distant server, lower it for one that caps concurrent sessions per account
    /// (a failure to open the extra connections is reported and the deploy continues at whatever
    /// it managed, so a too-high value costs a warning rather than a failed deploy).
    /// </summary>
    public int UploadConcurrency { get; set; } = DefaultUploadConcurrency;

    public const int DefaultUploadConcurrency = 8;
    public const int MaxUploadConcurrency = 32;

    /// <summary>
    /// <see cref="UploadConcurrency"/> brought into range. Profiles arrive from YAML that anyone
    /// can hand-edit, so this is clamped at the point of use rather than trusted.
    /// </summary>
    public int EffectiveUploadConcurrency =>
        UploadConcurrency <= 0 ? DefaultUploadConcurrency
        : UploadConcurrency > MaxUploadConcurrency ? MaxUploadConcurrency
        : UploadConcurrency;
}
