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
        // Unreachable — Skip.If throws — but the CA1416 analyzer doesn't recognise it as a
        // platform guard, so the narrowing check has to be visible in the code.
        if (OperatingSystem.IsWindows()) return;

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
    public void EncryptedFile_MissingKey_ReadsAsNotFound()
    {
        var result = new EncryptedFileCredentialStore(_dir).Read("nope");
        Assert.Equal(CredentialStatus.NotFound, result.Status);
        Assert.Null(result.Secret);
        Assert.Null(result.Error);
    }

    [Fact]
    public void EncryptedFile_RoundTrip_ReadsAsFound()
    {
        var store = new EncryptedFileCredentialStore(_dir);
        store.Set("key1", "hunter2");

        var result = store.Read("key1");
        Assert.Equal(CredentialStatus.Found, result.Status);
        Assert.Equal("hunter2", result.Secret);
    }

    [Fact]
    public void EncryptedFile_DamagedFile_ReadsAsFailed_AndIsLeftAlone()
    {
        // A secret that exists but won't decrypt must never look like one that was never saved:
        // the settings dialog deletes on an empty box, so conflating the two destroys the secret.
        var store = new EncryptedFileCredentialStore(_dir);
        store.Set("key1", "hunter2");

        var file = Assert.Single(Directory.GetFiles(_dir, "*.aes"));
        var damaged = new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 };
        File.WriteAllBytes(file, damaged);

        var result = store.Read("key1");
        Assert.Equal(CredentialStatus.Failed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Null(store.Get("key1"));

        // The store must not have "cleaned up" what it couldn't read.
        Assert.Equal(damaged, File.ReadAllBytes(file));
    }

    [Fact]
    public void EncryptedFile_Delete_AlsoRemovesAStrandedTempFile()
    {
        // Set writes to a temp file and renames. A crash in between leaves that temp holding a
        // perfectly decryptable secret, so deleting only the final path tells the user the secret
        // is forgotten while a copy of it is still on disk.
        var store = new EncryptedFileCredentialStore(_dir);
        store.Set("key1", "hunter2");

        var live = Assert.Single(Directory.GetFiles(_dir, "*.aes"));
        var stranded = live + ".tmp";
        File.Copy(live, stranded);

        store.Delete("key1");

        Assert.False(File.Exists(live));
        Assert.False(File.Exists(stranded));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void EncryptedFile_Set_LeavesNoTempFileBehind()
    {
        var store = new EncryptedFileCredentialStore(_dir);
        store.Set("key1", "hunter2");
        store.Set("key1", "hunter3");

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Equal("hunter3", store.Get("key1"));
    }

    [Fact]
    public void Factory_ReturnsAUsableStore()
    {
        // The platform store (Keychain/DPAPI/libsecret or the encrypted-file fallback) must be non-null.
        var store = CredentialStoreFactory.Create();
        Assert.NotNull(store);
    }
}
