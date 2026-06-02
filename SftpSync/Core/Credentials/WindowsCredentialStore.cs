// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace dir2site.SftpSync.Core.Credentials;

/// <summary>
/// Windows credential store: secrets are DPAPI-encrypted (CurrentUser scope) and written to a
/// per-key file under the app-config credentials directory.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ICredentialStore
{
    private readonly string _dir;
    public WindowsCredentialStore(string dir) => _dir = dir;

    public bool IsSecure => true;

    private string PathFor(string key) => Path.Combine(_dir, key + ".bin");

    public string? Get(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    public void Set(string key, string secret)
    {
        Directory.CreateDirectory(_dir);
        var cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(key), cipher);
    }

    public void Delete(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
    }
}
