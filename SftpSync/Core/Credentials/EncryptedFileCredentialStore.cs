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

    public string? Get(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        try
        {
            var blob   = File.ReadAllBytes(path);
            var nonce  = blob.AsSpan(0, NonceSize);
            var tag    = blob.AsSpan(NonceSize, TagSize);
            var cipher = blob.AsSpan(NonceSize + TagSize);
            var plain  = new byte[cipher.Length];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
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
        File.WriteAllBytes(PathFor(key), blob);
    }

    public void Delete(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
    }
}
