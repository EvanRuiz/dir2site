// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace dir2site.SftpSync.Core;

/// <summary>
/// Loads and saves <see cref="SftpProfile"/> records as JSON under the per-user app-config
/// directory, keyed by a hash of the project root. Nothing is written into the project or the
/// generated site, so credentials/connection info never end up in version control or on the web.
/// </summary>
public static class SftpProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Directory where profile JSON files are stored, e.g. <c>%AppData%/dir2site/profiles</c>.</summary>
    public static string ProfilesDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "dir2site", "profiles");

    private static string NormalizeProject(string projectRoot) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private static string PathFor(string projectRoot) =>
        Path.Combine(ProfilesDir, Hash(NormalizeProject(projectRoot)) + ".json");

    public static bool Exists(string projectRoot) => File.Exists(PathFor(projectRoot));

    public static SftpProfile? Load(string projectRoot)
    {
        var path = PathFor(projectRoot);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<SftpProfile>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string projectRoot, SftpProfile profile)
    {
        Directory.CreateDirectory(ProfilesDir);
        File.WriteAllText(PathFor(projectRoot), JsonSerializer.Serialize(profile, JsonOptions));
    }

    /// <summary>
    /// Stable key used to store/look up the secret in the platform credential store.
    /// Bound to the project, host and username so distinct targets don't collide.
    /// </summary>
    public static string CredentialKey(string projectRoot, SftpProfile profile) =>
        Hash($"{NormalizeProject(projectRoot)}|{profile.Host}|{profile.Port}|{profile.Username}");
}
