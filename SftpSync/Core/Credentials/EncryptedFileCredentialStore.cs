// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace dir2site.SftpSync.Core.Credentials;

/// <summary>
/// Fallback store for platforms without a reachable OS keychain. Secrets are AES-GCM encrypted to
/// a per-key file. The key is derived from a stable per-user/machine identifier, so this protects
/// against casual disk inspection but is weaker than an OS keychain — <see cref="IsSecure"/> is
/// false so the UI can warn.
/// </summary>
public sealed class EncryptedFileCredentialStore : ICredentialStore
{
    private const int NonceSize = 12;
    private const int TagSize   = 16;

    private readonly string _dir;
    private readonly byte[] _key;

    public EncryptedFileCredentialStore(string dir)
    {
        _dir = dir;
        var material = $"{Environment.UserName}|{Environment.MachineName}|dir2site-credential-store-v1";
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(material)); // 32 bytes
    }

    public bool IsSecure => false;

    private string PathFor(string key) => Path.Combine(_dir, key + ".aes");

    /// <summary>Where <see cref="Set"/> stages a write before renaming it over the real path.</summary>
    private static string TempFor(string path) => path + ".tmp";

    public string? Get(string key) => Read(key).Secret;

    public CredentialResult Read(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return CredentialResult.NotFound;
        try
        {
            var blob   = File.ReadAllBytes(path);
            var nonce  = blob.AsSpan(0, NonceSize);
            var tag    = blob.AsSpan(NonceSize, TagSize);
            var cipher = blob.AsSpan(NonceSize + TagSize);
            var plain  = new byte[cipher.Length];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return CredentialResult.Found(Encoding.UTF8.GetString(plain));
        }
        catch (Exception ex)
        {
            // A truncated file trips the AsSpan slicing, a tampered one fails the GCM tag, and a
            // changed username or machine name derives a different key. All of them mean "there is
            // a secret here we can't read", never "there is no secret".
            return CredentialResult.Failed($"Could not read the saved secret: {ex.Message}");
        }
    }

    public void Set(string key, string secret)
    {
        CreateDirRestricted(_dir);
        var plain  = Encoding.UTF8.GetBytes(secret);
        var nonce  = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag    = new byte[TagSize];
        using (var aes = new AesGcm(_key, TagSize))
            aes.Encrypt(nonce, plain, cipher, tag);

        var blob = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce,  0, blob, 0,                    NonceSize);
        Buffer.BlockCopy(tag,    0, blob, NonceSize,            TagSize);
        Buffer.BlockCopy(cipher, 0, blob, NonceSize + TagSize,  cipher.Length);
        WriteRestricted(PathFor(key), blob);
    }

    public void Delete(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);

        // A write interrupted before its rename leaves the temp file holding the secret, fully
        // decryptable. Forgetting a secret has to mean forgetting every copy of it — someone
        // clearing a credential because it leaked, or because they're passing the machine on, has
        // been told it is gone.
        var tmp = TempFor(path);
        if (File.Exists(tmp)) File.Delete(tmp);
    }

    // The key is derived from non-secret material, so the file's own permissions are what keeps
    // other accounts on the machine out. Create it empty and lock it down before the ciphertext
    // goes in, so it is never briefly world-readable.
    //
    // Write to a temp file and rename over the target: dying midway through would otherwise leave a
    // truncated blob, which reads back as a secret that exists but can't be decrypted.
    private static void WriteRestricted(string path, byte[] bytes)
    {
        var tmp = TempFor(path);
        File.Create(tmp).Dispose();
        RestrictToOwner(tmp);
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    private static void CreateDirRestricted(string dir)
    {
        var created = !Directory.Exists(dir);
        Directory.CreateDirectory(dir);
        if (created) RestrictToOwner(dir, directory: true);
    }

    private static void RestrictToOwner(string path, bool directory = false)
    {
        if (OperatingSystem.IsWindows()) return; // NTFS ACLs already inherit per-user AppData

        try
        {
            // 0700 for directories (needs execute to traverse), 0600 for files.
            File.SetUnixFileMode(path, directory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best effort — a filesystem that can't represent Unix modes shouldn't break saving.
        }
    }
}
