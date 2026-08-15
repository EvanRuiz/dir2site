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

    /// <summary>Where <see cref="Set"/> stages a write before renaming it over the real path.</summary>
    private static string TempFor(string path) => path + ".tmp";

    public string? Get(string key) => Read(key).Secret;

    public CredentialResult Read(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return CredentialResult.NotFound;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            return CredentialResult.Found(Encoding.UTF8.GetString(plain));
        }
        catch (CryptographicException)
        {
            // DPAPI CurrentUser blobs are sealed with a master key derived from the Windows
            // credential. Removing or resetting the account password destroys that master key, and
            // every blob with it — permanently, which is what Windows warns about when you remove a
            // password. The ciphertext is still on disk and still unreadable, so say so rather than
            // reporting "no password" and letting the caller overwrite it.
            return CredentialResult.Failed(
                "Windows can no longer decrypt this saved secret. This happens after a Windows " +
                "password reset or removal, and cannot be undone — please enter it again.");
        }
        catch (Exception ex)
        {
            return CredentialResult.Failed($"Could not read the saved secret: {ex.Message}");
        }
    }

    public void Set(string key, string secret)
    {
        Directory.CreateDirectory(_dir);
        var cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);

        // Write-then-rename: a half-written blob is indistinguishable from a dead one, and would
        // strand the user in the unrecoverable case above for what was only a torn write.
        var path = PathFor(key);
        var tmp  = TempFor(path);
        File.WriteAllBytes(tmp, cipher);
        File.Move(tmp, path, overwrite: true);
    }

    public void Delete(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);

        // A write interrupted before its rename leaves the temp file holding a blob this same user
        // can still unprotect. Forgetting a secret has to mean forgetting every copy of it.
        var tmp = TempFor(path);
        if (File.Exists(tmp)) File.Delete(tmp);
    }
}
