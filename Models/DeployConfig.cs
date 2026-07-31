// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Generic;
using dir2site.SftpSync.Core;
using YamlDotNet.Serialization;

namespace dir2site.Models;

/// <summary>
/// Deploy targets, stored in <c>dir2site.yaml</c> alongside the rest of the project config.
///
/// These settings are portable and meant to be committed: host, path and username are ordinary
/// deployment config, the same things a CI config or an rsync script would carry. Secrets are not
/// here — passwords and key passphrases live in the OS keychain — and neither is the private key
/// path, which names a file on one machine and would be wrong for anyone else
/// (see <see cref="DeployLocalStore"/>).
/// </summary>
public class DeployConfig
{
    /// <summary>Name of the target the UI acts on. Empty means the first one.</summary>
    public string Active { get; set; } = string.Empty;

    public List<DeployTarget> Targets { get; set; } = [];
}

/// <summary>
/// One named deploy destination. A YAML-facing view of <see cref="SftpProfile"/>, kept separate so
/// <c>SftpSync.Core</c> stays free of any serialization concern and can still be extracted as a
/// standalone package.
/// </summary>
public class DeployTarget
{
    public string Name { get; set; } = "default";
    public string Host { get; set; } = string.Empty;
    public int    Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;

    /// <summary>Remote directory the contents of <c>_site/</c> are written into.</summary>
    public string RemotePath { get; set; } = string.Empty;

    /// <summary>Optional manifest location; empty keeps it inside <see cref="RemotePath"/>.</summary>
    public string ManifestPath { get; set; } = string.Empty;

    /// <summary><c>key</c> or <c>password</c>.</summary>
    public string Auth { get; set; } = "password";

    /// <summary>
    /// The trusted server host key, <c>SHA256:base64</c>. Public key material, so committing it is
    /// safe — and desirable: a change to it then shows up in a diff, which is the point of pinning.
    /// </summary>
    public string HostKeyFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// Connections used to upload in parallel. Per-target because it depends on the server, not on
    /// the machine deploying: shared hosting often caps concurrent sessions per account.
    /// </summary>
    public int UploadConcurrency { get; set; } = SftpProfile.DefaultUploadConcurrency;

    public SftpProfile ToProfile(string privateKeyPath) => new()
    {
        Host = Host,
        Port = Port <= 0 ? 22 : Port,
        Username = Username,
        RemotePath = RemotePath,
        ManifestPath = ManifestPath,
        AuthMethod = IsKeyAuth ? SftpAuthMethod.Key : SftpAuthMethod.Password,
        PrivateKeyPath = privateKeyPath,
        HostKeyFingerprint = HostKeyFingerprint,
        UploadConcurrency = UploadConcurrency,
    };

    // Derived from Auth — without this it would serialize as a redundant isKeyAuth key.
    [YamlIgnore]
    public bool IsKeyAuth =>
        string.Equals(Auth, "key", System.StringComparison.OrdinalIgnoreCase);

    public static DeployTarget FromProfile(string name, SftpProfile p) => new()
    {
        Name = name,
        Host = p.Host,
        Port = p.Port,
        Username = p.Username,
        RemotePath = p.RemotePath,
        ManifestPath = p.ManifestPath,
        Auth = p.AuthMethod == SftpAuthMethod.Key ? "key" : "password",
        HostKeyFingerprint = p.HostKeyFingerprint,
        UploadConcurrency = p.UploadConcurrency,
    };
}
