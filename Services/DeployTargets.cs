// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dir2site.Models;
using dir2site.SftpSync.Core;
using dir2site.SftpSync.Core.Credentials;

namespace dir2site.Services;

/// <summary>
/// Reads and writes the deploy targets in <c>dir2site.yaml</c>, and joins them back up with the
/// per-machine bits (<see cref="DeployLocalStore"/>) and the OS keychain to produce something the
/// sync engine can use.
/// </summary>
public static class DeployTargets
{
    /// <summary>
    /// Targets for a project, importing a pre-yaml profile the first time if one exists so nobody
    /// has to reconfigure a working deployment.
    /// </summary>
    public static DeployConfig Resolve(string projectRoot, Dir2SiteModel config)
    {
        if (config.Deploy is { Targets.Count: > 0 } existing)
            return existing;

        var imported = ImportLegacyProfile(projectRoot);
        if (imported != null)
        {
            config.Deploy = imported;
            return imported;
        }

        return config.Deploy ??= new DeployConfig();
    }

    /// <summary>The target the UI is acting on, or null when none are configured.</summary>
    public static DeployTarget? Active(DeployConfig deploy)
    {
        if (deploy.Targets.Count == 0) return null;
        return deploy.Targets.FirstOrDefault(
                   t => string.Equals(t.Name, deploy.Active, StringComparison.OrdinalIgnoreCase))
               ?? deploy.Targets[0];
    }

    /// <summary>Combines the portable target with this machine's key path into a usable profile.</summary>
    public static SftpProfile ToProfile(string projectRoot, DeployTarget target) =>
        target.ToProfile(DeployLocalStore.GetPrivateKeyPath(projectRoot, target.Name));

    /// <summary>
    /// Credential-store key for a target's secret, or null when it has nowhere to keep one — a
    /// key-auth target whose key file isn't chosen or isn't readable. The private key path is
    /// consulted because a passphrase is addressed by the key it unlocks, not by the server.
    /// </summary>
    public static string? CredentialKey(string projectRoot, DeployTarget target) =>
        CredentialKeys.For(ToProfile(projectRoot, target));

    /// <summary>
    /// The target's secret, moved off the older project-scoped key if it is still stored there.
    /// </summary>
    public static CredentialResult ReadSecret(ICredentialStore store, string projectRoot, DeployTarget target) =>
        TargetSecret.Read(store, projectRoot, ToProfile(projectRoot, target));

    /// <summary>
    /// Writes the deploy block into the project's YAML, leaving everything outside it untouched.
    /// </summary>
    public static void Save(string configPath, DeployConfig deploy)
    {
        string existing;
        try { existing = File.Exists(configPath) ? File.ReadAllText(configPath) : ""; }
        catch { existing = ""; }

        var editor = YamlDocumentEditor.TryLoad(existing);
        if (editor == null) return;   // caller's full-config save will deal with a broken file

        var applied = deploy.Targets.Count == 0
            ? editor.RemoveKey("deploy")
            : editor.SetBlock("deploy", YamlParser.SerializeToYaml(deploy));

        if (applied && editor.IsModified)
            File.WriteAllText(configPath, editor.Text);
    }

    // A profile written before deploy config moved into dir2site.yaml. The JSON is left in place
    // rather than deleted: if the user downgrades or the import is wrong, their settings are still
    // there to fall back on.
    private static DeployConfig? ImportLegacyProfile(string projectRoot)
    {
        var legacy = SftpProfileStore.Load(projectRoot);
        if (legacy == null || string.IsNullOrWhiteSpace(legacy.Host)) return null;

        const string name = "default";
        if (!string.IsNullOrWhiteSpace(legacy.PrivateKeyPath))
            DeployLocalStore.SetPrivateKeyPath(projectRoot, name, legacy.PrivateKeyPath);

        return new DeployConfig
        {
            Active = name,
            Targets = [DeployTarget.FromProfile(name, legacy)],
        };
    }

    /// <summary>A name not already taken, for the "add target" button.</summary>
    public static string UniqueName(DeployConfig deploy, string desired)
    {
        var taken = new HashSet<string>(deploy.Targets.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(desired)) return desired;

        for (var i = 2; ; i++)
        {
            var candidate = $"{desired} {i}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}
