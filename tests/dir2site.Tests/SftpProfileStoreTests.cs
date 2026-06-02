// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dir2site.SftpSync.Core;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Exercises the per-user profile store. Profiles live under %AppData%/dir2site/profiles keyed by a
/// hash of the project path, so each test uses a unique fake project root and removes any file it created.
/// </summary>
public class SftpProfileStoreTests : IDisposable
{
    private readonly string _projectRoot = Path.Combine(Path.GetTempPath(), "d2s-proj-" + Guid.NewGuid().ToString("N"));
    private readonly HashSet<string> _preexisting;

    public SftpProfileStoreTests()
    {
        _preexisting = Snapshot();
    }

    public void Dispose()
    {
        foreach (var f in Snapshot().Except(_preexisting))
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    private static HashSet<string> Snapshot() =>
        Directory.Exists(SftpProfileStore.ProfilesDir)
            ? Directory.GetFiles(SftpProfileStore.ProfilesDir).ToHashSet()
            : new HashSet<string>();

    private static SftpProfile Sample() => new()
    {
        Host = "sftp.example.com",
        Port = 2222,
        Username = "deploy",
        RemotePath = "/var/www/html",
        ManifestPath = "/var/manifests/site.json",
        AuthMethod = SftpAuthMethod.Key,
        PrivateKeyPath = "/home/u/.ssh/id_ed25519",
    };

    [Fact]
    public void Load_WhenAbsent_ReturnsNull()
    {
        Assert.False(SftpProfileStore.Exists(_projectRoot));
        Assert.Null(SftpProfileStore.Load(_projectRoot));
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsAllFields()
    {
        var saved = Sample();
        SftpProfileStore.Save(_projectRoot, saved);

        Assert.True(SftpProfileStore.Exists(_projectRoot));
        var loaded = SftpProfileStore.Load(_projectRoot);

        Assert.NotNull(loaded);
        Assert.Equal(saved.Host, loaded!.Host);
        Assert.Equal(saved.Port, loaded.Port);
        Assert.Equal(saved.Username, loaded.Username);
        Assert.Equal(saved.RemotePath, loaded.RemotePath);
        Assert.Equal(saved.ManifestPath, loaded.ManifestPath);
        Assert.Equal(saved.AuthMethod, loaded.AuthMethod);
        Assert.Equal(saved.PrivateKeyPath, loaded.PrivateKeyPath);
    }

    [Fact]
    public void CredentialKey_IsStableForSameInputs()
    {
        var p = Sample();
        Assert.Equal(
            SftpProfileStore.CredentialKey(_projectRoot, p),
            SftpProfileStore.CredentialKey(_projectRoot, p));
    }

    [Fact]
    public void CredentialKey_DiffersByHostAndUser()
    {
        var a = Sample();
        var b = Sample(); b.Host = "other.example.com";
        var c = Sample(); c.Username = "someoneelse";

        var ka = SftpProfileStore.CredentialKey(_projectRoot, a);
        Assert.NotEqual(ka, SftpProfileStore.CredentialKey(_projectRoot, b));
        Assert.NotEqual(ka, SftpProfileStore.CredentialKey(_projectRoot, c));
    }
}
