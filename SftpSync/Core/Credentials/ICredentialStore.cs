// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace dir2site.SftpSync.Core.Credentials;

/// <summary>
/// Stores per-target secrets (password or key passphrase) in OS-protected storage.
/// Implementations are selected at runtime by <see cref="CredentialStoreFactory"/>.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Returns the stored secret for <paramref name="key"/>, or null if none.</summary>
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
