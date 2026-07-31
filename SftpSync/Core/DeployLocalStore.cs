// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace dir2site.SftpSync.Core;

/// <summary>
/// The per-machine half of a deploy target's settings, kept out of <c>dir2site.yaml</c> because it
/// would be wrong on anyone else's computer.
///
/// Today that is the SSH private key path: <c>/Users/someone/.ssh/id_ed25519</c> is meaningful only
/// on the machine it names, so committing it would hand collaborators a path that doesn't exist.
/// Everything portable about a target lives in the project's YAML instead.
/// </summary>
public static class DeployLocalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>e.g. <c>%AppData%/dir2site/local</c>.</summary>
    public static string LocalDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "dir2site", "local");

    private static string PathFor(string projectRoot) =>
        Path.Combine(LocalDir, SftpProfileStore.ProjectKey(projectRoot) + ".json");

    private static Dictionary<string, TargetLocal> Load(string projectRoot)
    {
        var path = PathFor(projectRoot);
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, TargetLocal>>(File.ReadAllText(path))
                   ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>The private key path recorded for a target on this machine, or empty.</summary>
    public static string GetPrivateKeyPath(string projectRoot, string targetName) =>
        Load(projectRoot).TryGetValue(targetName, out var local) ? local.PrivateKeyPath : "";

    public static void SetPrivateKeyPath(string projectRoot, string targetName, string privateKeyPath)
    {
        var all = Load(projectRoot);
        if (string.IsNullOrWhiteSpace(privateKeyPath))
            all.Remove(targetName);
        else
            all[targetName] = new TargetLocal { PrivateKeyPath = privateKeyPath };

        Directory.CreateDirectory(LocalDir);
        File.WriteAllText(PathFor(projectRoot), JsonSerializer.Serialize(all, JsonOptions));
    }

    public static void Remove(string projectRoot, string targetName) =>
        SetPrivateKeyPath(projectRoot, targetName, "");

    public sealed class TargetLocal
    {
        public string PrivateKeyPath { get; set; } = string.Empty;
    }
}
