// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace dir2site.SftpSync.Core.Credentials;

/// <summary>macOS credential store backed by the login Keychain via the <c>security</c> CLI.</summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacCredentialStore : ICredentialStore
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
            // -g rather than -w: with -w, `security` prints a bare hex string for any password
            // that isn't printable ASCII, which is indistinguishable from a password that just
            // happens to look like hex. -g prefixes it with 0x, so non-ASCII secrets round-trip.
            // It reports on stderr.
            var r = ProcessHelper.Run("/usr/bin/security",
                ["find-generic-password", "-g", "-s", Service, "-a", key]);
            if (r.ExitCode != 0) return null;

            var m = PasswordLineRegex().Match(r.StdErr);
            if (!m.Success) return null;

            var hex = m.Groups["hex"].Value;
            if (hex.Length > 0)
                return Encoding.UTF8.GetString(Convert.FromHexString(hex));

            return m.Groups["text"].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        catch
        {
            return null;
        }
    }

    public void Set(string key, string secret)
    {
        // The secret must not appear in the ArgumentList: argv is world-readable via `ps`, so
        // any other user on the machine could read the SSH password. `security -i` reads the
        // whole command from stdin instead, keeping it out of the process list.
        // -U updates the item in place if it already exists.
        var command =
            $"add-generic-password -U -s {Quote(Service)} -a {Quote(key)} -w {Quote(secret)}";
        ProcessHelper.Run("/usr/bin/security", ["-i"], stdin: command + "\n");
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

    // `security -i` re-splits each line into arguments itself, so values need quoting on the way in.
    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // Either: password: 0x70C3A4...  "p\303\244..."   (non-ASCII, hex is authoritative)
    // Or:     password: "plain-ascii"                 (printable ASCII)
    // Or:     password:                               (empty secret — both groups stay empty)
    [GeneratedRegex("""^password:\s*(?:0x(?<hex>[0-9A-Fa-f]+)\s+)?(?:"(?<text>.*)")?\s*$""", RegexOptions.Multiline)]
    private static partial Regex PasswordLineRegex();
}
