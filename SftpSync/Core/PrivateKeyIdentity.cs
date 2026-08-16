// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Buffers.Binary;
using System.IO;

namespace dir2site.SftpSync.Core;

/// <summary>
/// A stable identity for an SSH private key file, used to address its passphrase in the credential
/// store.
/// </summary>
/// <remarks>
/// The passphrase belongs to the key pair, not to the file holding it and not to any server the key
/// is used with — one key is routinely used for many hosts. So the identity is the public key
/// fingerprint, the same <c>SHA256:base64</c> string <c>ssh-keygen -lf</c> prints, which survives
/// the file being moved, renamed, re-encrypted under the same passphrase, converted between
/// formats, or having its comment changed. Hashing the file's bytes instead would change on every
/// one of those and orphan the passphrase for no reason.
///
/// It has to be derivable *without* the passphrase, or looking a passphrase up would require
/// already having it. That works because <c>openssh-key-v1</c> stores the public key in the clear
/// even when the private half is encrypted — which is also why <c>ssh-keygen -lf</c> doesn't prompt.
/// </remarks>
public static class PrivateKeyIdentity
{
    private const string OpenSshMagic = "openssh-key-v1\0";
    private const string BeginOpenSsh = "-----BEGIN OPENSSH PRIVATE KEY-----";
    private const string EndOpenSsh   = "-----END OPENSSH PRIVATE KEY-----";

    /// <summary>
    /// The key's fingerprint; or the full path for a key whose format hides its public half; or
    /// empty when there is no key to identify, which means the passphrase has no home yet.
    /// </summary>
    /// <remarks>
    /// The empty case has to stay distinct from the others. Every key with no identity would
    /// otherwise hash to the same string, and one passphrase slot would be shared by every key-auth
    /// target on the machine — handing one project's passphrase to another.
    ///
    /// Reading the file is also what separates "this format has no public half" from "this file
    /// isn't there right now". The first is a legacy PEM key, where falling back to the path is a
    /// fair trade because the path is stable. The second is an unmounted volume or a path typed
    /// before the file was copied over, where falling back would mint an identity that changes the
    /// moment the file appears — orphaning the passphrase stored under it.
    /// </remarks>
    public static string For(string privateKeyPath)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPath)) return string.Empty;

        string text;
        try
        {
            text = File.ReadAllText(privateKeyPath);
        }
        catch
        {
            return string.Empty;   // can't open it — don't invent an identity that will change
        }

        return FingerprintFromOpenSsh(text)
            ?? FingerprintFromPublicSibling(privateKeyPath)
            ?? PathFallback(privateKeyPath);
    }

    /// <summary>True when the identity is a real fingerprint rather than the path fallback.</summary>
    public static bool IsFingerprint(string identity) =>
        identity.StartsWith("SHA256:", StringComparison.Ordinal);

    // Legacy PEM keys ("-----BEGIN RSA PRIVATE KEY-----" with DEK-Info) encrypt the whole body, so
    // there is no public half to read without the passphrase. The path is wrong in principle — it
    // changes when the file moves — but it is stable while the file sits still, which is the common
    // case, and the alternative is having nowhere to put the passphrase at all.
    private static string PathFallback(string privateKeyPath)
    {
        try
        {
            var full = Path.GetFullPath(privateKeyPath);
            // Windows and macOS default to case-insensitive filesystems, so the same file reached
            // by differently-cased paths must not produce two identities.
            return OperatingSystem.IsLinux() ? full : full.ToLowerInvariant();
        }
        catch
        {
            return privateKeyPath;
        }
    }

    private static string? FingerprintFromPublicSibling(string privateKeyPath)
    {
        try
        {
            var pub = privateKeyPath + ".pub";
            if (!File.Exists(pub)) return null;

            // "ssh-ed25519 AAAAC3Nz... user@host" — the middle field is the wire-format blob.
            var fields = File.ReadAllText(pub)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return fields.Length >= 2 ? Fingerprint(Convert.FromBase64String(fields[1])) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FingerprintFromOpenSsh(string text)
    {
        try
        {
            var start = text.IndexOf(BeginOpenSsh, StringComparison.Ordinal);
            if (start < 0) return null;

            start += BeginOpenSsh.Length;
            var end = text.IndexOf(EndOpenSsh, start, StringComparison.Ordinal);
            if (end < 0) return null;

            var blob = Convert.FromBase64String(
                text[start..end].Replace("\r", "").Replace("\n", "").Trim());

            return ReadPublicKey(blob) is { } publicKey ? Fingerprint(publicKey) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Pulls the public key out of a decoded <c>openssh-key-v1</c> body. The layout is the magic,
    /// then the cipher name, KDF name and KDF options as length-prefixed strings, then the key
    /// count, then the public key — all before anything encrypted begins.
    /// </summary>
    private static byte[]? ReadPublicKey(byte[] blob)
    {
        var at = 0;

        foreach (var c in OpenSshMagic)
            if (at >= blob.Length || blob[at++] != (byte)c) return null;

        if (!SkipString(blob, ref at)) return null;   // ciphername
        if (!SkipString(blob, ref at)) return null;   // kdfname
        if (!SkipString(blob, ref at)) return null;   // kdfoptions

        if (at + 4 > blob.Length) return null;
        var keyCount = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(at));
        at += 4;
        if (keyCount == 0) return null;

        return ReadString(blob, ref at);              // the first public key
    }

    private static bool SkipString(byte[] blob, ref int at) => ReadString(blob, ref at) != null;

    private static byte[]? ReadString(byte[] blob, ref int at)
    {
        if (at + 4 > blob.Length) return null;

        var length = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(at));
        at += 4;

        // Guards a malformed or truncated file from being read as a huge allocation.
        if (length > int.MaxValue || at + (int)length > blob.Length) return null;

        var value = blob[at..(at + (int)length)];
        at += (int)length;
        return value;
    }

    private static string Fingerprint(byte[] publicKey) =>
        HostKeyFingerprintFormatter.Format(publicKey);
}
