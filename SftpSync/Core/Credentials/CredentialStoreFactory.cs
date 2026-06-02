// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace dir2site.SftpSync.Core.Credentials;

/// <summary>Selects the best available credential store for the current OS.</summary>
public static class CredentialStoreFactory
{
    /// <summary>Directory backing the file-based stores, e.g. <c>%AppData%/dir2site/credentials</c>.</summary>
    public static string CredentialsDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "dir2site", "credentials");

    public static ICredentialStore Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsCredentialStore(CredentialsDir);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && MacCredentialStore.IsAvailable())
            return new MacCredentialStore();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && LinuxCredentialStore.IsAvailable())
            return new LinuxCredentialStore();

        // Last resort on any platform without an OS keychain reachable.
        return new EncryptedFileCredentialStore(CredentialsDir);
    }
}
