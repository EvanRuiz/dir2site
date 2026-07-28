// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using dir2site.SftpSync.Core.Credentials;
using Xunit;

namespace dir2site.Tests;

public class CredentialStoreTests : IDisposable
{
    private readonly string _dir;

    public CredentialStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "d2s-cred-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void EncryptedFile_RoundTrips()
    {
        var store = new EncryptedFileCredentialStore(_dir);
        store.Set("key1", "hunter2");
        Assert.Equal("hunter2", store.Get("key1"));
    }

    [Fact]
    public void EncryptedFile_Overwrite_ReplacesValue()
    {
        var store = new EncryptedFileCredentialStore(_dir);
        store.Set("key1", "first");
        store.Set("key1", "second");
        Assert.Equal("second", store.Get("key1"));
    }

    [Fact]
    public void EncryptedFile_MissingKey_ReturnsNull()
    {
        var store = new EncryptedFileCredentialStore(_dir);
        Assert.Null(store.Get("nope"));
    }

    [Fact]
    public void EncryptedFile_Delete_RemovesValue()
    {
        var store = new EncryptedFileCredentialStore(_dir);
        store.Set("key1", "secret");
        store.Delete("key1");
        Assert.Null(store.Get("key1"));
    }

    [Fact]
    public void EncryptedFile_DeleteMissing_DoesNotThrow()
    {
        var store = new EncryptedFileCredentialStore(_dir);
        store.Delete("never-existed"); // should be a no-op
    }

    [Fact]
    public void EncryptedFile_HandlesUnicodeAndEmpty()
    {
        var store = new EncryptedFileCredentialStore(_dir);
        store.Set("u", "pä$$wörd 🔐");
        Assert.Equal("pä$$wörd 🔐", store.Get("u"));
        store.Set("e", "");
        Assert.Equal("", store.Get("e"));
    }

    [Fact]
    public void EncryptedFile_IsNotMarkedSecure()
    {
        Assert.False(new EncryptedFileCredentialStore(_dir).IsSecure);
    }

    [Fact]
    public void EncryptedFile_PersistsAcrossInstances()
    {
        new EncryptedFileCredentialStore(_dir).Set("k", "v");
        Assert.Equal("v", new EncryptedFileCredentialStore(_dir).Get("k"));
    }

    [SkippableFact]
    public void EncryptedFile_IsNotReadableByOtherUsers()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes only; Windows relies on AppData ACLs.");

        // The AES key is derived from non-secret material (username + machine name), so these
        // permissions are the actual barrier between another local account and the SSH password.
        var store = new EncryptedFileCredentialStore(_dir);
        store.Set("key1", "hunter2");

        var files = Directory.GetFiles(_dir, "*.aes");
        Assert.Single(files);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(files[0]));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(_dir));
    }

    [Fact]
    public void Factory_ReturnsAUsableStore()
    {
        // The platform store (Keychain/DPAPI/libsecret or the encrypted-file fallback) must be non-null.
        var store = CredentialStoreFactory.Create();
        Assert.NotNull(store);
    }
}
