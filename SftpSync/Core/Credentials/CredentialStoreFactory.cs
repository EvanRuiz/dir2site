// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace dir2site.SftpSync.Core.Credentials;

/// <summary>Selects the best available credential store for the current OS.</summary>
public static class CredentialStoreFactory
{
    /// <summary>Directory backing the file-based stores, e.g. <c>%AppData%/dir2site/credentials</c>.</summary>
    public static string CredentialsDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "dir2site", "credentials");

    // AsyncLocal rather than a plain static, matching SourceListing: xunit runs test classes in
    // parallel, and a plain static would leak a substituted store into an unrelated run — or need
    // the whole collection serialised to stop it.
    private static readonly AsyncLocal<ICredentialStore?> _substitute = new();

    /// <summary>
    /// Makes <see cref="Create"/> hand back <paramref name="store"/> until the returned scope is
    /// disposed.
    /// </summary>
    /// <remarks>
    /// The view models build their store through <see cref="Create"/> rather than taking one as a
    /// dependency, so without a seam there is no way to test how they behave when a read fails —
    /// which is the case that silently deleted a user's saved password.
    /// </remarks>
    internal static IDisposable UseForTesting(ICredentialStore store) => new Scope(store);

    public static ICredentialStore Create()
    {
        if (_substitute.Value is { } stub) return stub;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsCredentialStore(CredentialsDir);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && MacCredentialStore.IsAvailable())
            return new MacCredentialStore();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && LinuxCredentialStore.IsAvailable())
            return new LinuxCredentialStore();

        // Last resort on any platform without an OS keychain reachable.
        return new EncryptedFileCredentialStore(CredentialsDir);
    }

    private sealed class Scope : IDisposable
    {
        private readonly ICredentialStore? _previous;

        public Scope(ICredentialStore store)
        {
            _previous = _substitute.Value;
            _substitute.Value = store;
        }

        public void Dispose() => _substitute.Value = _previous;
    }
}
