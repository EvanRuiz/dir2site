// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.Versioning;

namespace dir2site.SftpSync.Core.Credentials;

/// <summary>Linux credential store backed by libsecret via the <c>secret-tool</c> CLI.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxCredentialStore : ICredentialStore
{
    private const string Service = "dir2site";

    public bool IsSecure => true;

    public static bool IsAvailable() => ProcessHelper.OnPath("secret-tool");

    public string? Get(string key)
    {
        try
        {
            var r = ProcessHelper.Run("secret-tool", ["lookup", "service", Service, "account", key]);
            return r.ExitCode == 0 && r.StdOut.Length > 0 ? r.StdOut.TrimEnd('\n') : null;
        }
        catch
        {
            return null;
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
