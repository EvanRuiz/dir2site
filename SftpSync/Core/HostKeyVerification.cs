// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Security.Cryptography;

namespace dir2site.SftpSync.Core;

/// <summary>The server host key offered during a connection, for the user to accept or refuse.</summary>
/// <param name="KnownFingerprint">
/// The fingerprint this profile already trusts, or <c>null</c> if the host has never been accepted.
/// When non-null it differs from <paramref name="Fingerprint"/>, i.e. the key has <em>changed</em> —
/// either the server was rebuilt or someone is impersonating it.
/// </param>
public sealed record HostKeyInfo(
    string Host,
    int Port,
    string KeyAlgorithm,
    int KeyLength,
    string Fingerprint,
    string? KnownFingerprint)
{
    /// <summary>True when a previously trusted key exists and the server is now offering a different one.</summary>
    public bool IsChanged => KnownFingerprint is not null;
}

/// <summary>
/// Decides whether to trust <paramref name="info"/>. Returning true connects and pins the key.
/// Implementations run on the connection's background thread and may block (e.g. to prompt).
/// </summary>
public delegate bool HostKeyVerifier(HostKeyInfo info);

/// <summary>Thrown when a host key was not trusted, so the connection was refused.</summary>
public sealed class SftpHostKeyRejectedException(string message) : Exception(message);

/// <summary>Formats an SSH host key blob the way OpenSSH does: <c>SHA256:</c> + unpadded base64.</summary>
public static class HostKeyFingerprintFormatter
{
    public static string Format(byte[] hostKey) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');
}
