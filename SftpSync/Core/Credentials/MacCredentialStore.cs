// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.Versioning;

namespace dir2site.SftpSync.Core.Credentials;

/// <summary>macOS credential store backed by the login Keychain via the <c>security</c> CLI.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacCredentialStore : ICredentialStore
{
    private const string Service = "dir2site";

    public bool IsSecure => true;

    public static bool IsAvailable()
    {
        try { return ProcessHelper.Run("/usr/bin/security", ["help"]).ExitCode is 0 or 1; }
        catch { return false; }
    }

    public string? Get(string key)
    {
        try
        {
            var r = ProcessHelper.Run("/usr/bin/security",
                ["find-generic-password", "-s", Service, "-a", key, "-w"]);
            return r.ExitCode == 0 ? r.StdOut.TrimEnd('\n') : null;
        }
        catch
        {
            return null;
        }
    }

    public void Set(string key, string secret)
    {
        // -U updates the item in place if it already exists.
        ProcessHelper.Run("/usr/bin/security",
            ["add-generic-password", "-U", "-s", Service, "-a", key, "-w", secret]);
    }

    public void Delete(string key)
    {
        try
        {
            ProcessHelper.Run("/usr/bin/security",
                ["delete-generic-password", "-s", Service, "-a", key]);
        }
        catch
        {
            // ignore — nothing to delete
        }
    }
}
