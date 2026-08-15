// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Runtime.Versioning;

namespace dir2site.SftpSync.Core.Credentials;

/// <summary>Linux credential store backed by libsecret via the <c>secret-tool</c> CLI.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxCredentialStore : ICredentialStore
{
    private const string Service = "dir2site";

    public bool IsSecure => true;

    public static bool IsAvailable() => ProcessHelper.OnPath("secret-tool");

    public string? Get(string key) => Read(key).Secret;

    public CredentialResult Read(string key)
    {
        try
        {
            var r = ProcessHelper.Run("secret-tool", ["lookup", "service", Service, "account", key]);

            if (r.ExitCode == 0)
                return r.StdOut.Length > 0
                    ? CredentialResult.Found(r.StdOut.TrimEnd('\n'))
                    : CredentialResult.NotFound;

            // secret-tool's exit code for a missing item has varied across libsecret versions —
            // some return 0 and print nothing, others exit 1 silently. Treat a quiet failure as
            // "absent" and only report a real failure when it told us something, so a user who has
            // simply never saved a secret isn't warned about a broken keyring.
            var reason = r.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries) is [var line, ..]
                ? line.Trim()
                : string.Empty;

            return reason.Length > 0
                ? CredentialResult.Failed(reason)
                : CredentialResult.NotFound;
        }
        catch (Exception ex)
        {
            return CredentialResult.Failed($"Could not reach the keyring: {ex.Message}");
        }
    }

    public void Set(string key, string secret)
    {
        // secret-tool reads the secret from stdin, keeping it off the process argument list.
        ProcessHelper.Run("secret-tool",
            ["store", "--label=dir2site", "service", Service, "account", key], stdin: secret);
    }

    public void Delete(string key)
    {
        try
        {
            ProcessHelper.Run("secret-tool", ["clear", "service", Service, "account", key]);
        }
        catch
        {
            // ignore — nothing to delete
        }
    }
}
