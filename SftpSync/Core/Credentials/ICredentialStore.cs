// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace dir2site.SftpSync.Core.Credentials;

/// <summary>How a read of the underlying store turned out.</summary>
public enum CredentialStatus
{
    /// <summary>A secret was stored and read back.</summary>
    Found,

    /// <summary>Nothing is stored under this key. Normal for a target with no saved secret.</summary>
    NotFound,

    /// <summary>
    /// Something is stored but could not be read — a damaged file, or on Windows a DPAPI blob
    /// whose master key no longer exists. Callers must not treat this as "no secret": overwriting
    /// or deleting on a failed read destroys a secret that was merely unreadable.
    /// </summary>
    Failed,
}

/// <summary>
/// Outcome of <see cref="ICredentialStore.Read"/>. <paramref name="Secret"/> is non-null only when
/// <paramref name="Status"/> is <see cref="CredentialStatus.Found"/>; <paramref name="Error"/> is a
/// short, user-facing reason, set only when the status is <see cref="CredentialStatus.Failed"/>.
/// </summary>
public readonly record struct CredentialResult(CredentialStatus Status, string? Secret, string? Error)
{
    public static CredentialResult Found(string secret) => new(CredentialStatus.Found, secret, null);
    public static readonly CredentialResult NotFound = new(CredentialStatus.NotFound, null, null);
    public static CredentialResult Failed(string error) => new(CredentialStatus.Failed, null, error);
}

/// <summary>
/// Stores per-target secrets (password or key passphrase) in OS-protected storage.
/// Implementations are selected at runtime by <see cref="CredentialStoreFactory"/>.
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// Reads the secret for <paramref name="key"/>, distinguishing "nothing stored" from "stored
    /// but unreadable". Prefer this over <see cref="Get"/> anywhere the result decides whether to
    /// overwrite or delete.
    /// </summary>
    CredentialResult Read(string key);

    /// <summary>
    /// Returns the stored secret for <paramref name="key"/>, or null if there is none or it could
    /// not be read. Convenience over <see cref="Read"/> for callers that only need the value and
    /// have nothing to lose by conflating the two cases.
    /// </summary>
    string? Get(string key);

    /// <summary>Stores (or replaces) the secret for <paramref name="key"/>.</summary>
    void Set(string key, string secret);

    /// <summary>Removes any secret stored for <paramref name="key"/>. No-op if absent.</summary>
    void Delete(string key);

    /// <summary>
    /// True when secrets are kept in OS-managed secure storage. False for the encrypted-file
    /// fallback, which the UI surfaces as a warning.
    /// </summary>
    bool IsSecure { get; }
}
