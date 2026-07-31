// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using dir2site.SftpSync.Core;
using Xunit;

namespace dir2site.Tests;

public class SyncManifestBuilderTests : IDisposable
{
    private readonly string _dir;

    public SyncManifestBuilderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "d2s-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string rel, string content)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    [Fact]
    public void BuildLocal_OnMissingRoot_ReturnsEmpty()
    {
        var manifest = SyncManifestBuilder.BuildLocal(Path.Combine(_dir, "does-not-exist"));
        Assert.Empty(manifest.Files);
    }

    [Fact]
    public void BuildLocal_UsesForwardSlashRelativePaths_AndRecordsSize()
    {
        Write("index.html", "abc");
        Write(Path.Combine("css", "site.css"), "body{}");

        var manifest = SyncManifestBuilder.BuildLocal(_dir);

        Assert.Equal(2, manifest.Files.Count);
        Assert.Contains("index.html", manifest.Files.Keys);
        Assert.Contains("css/site.css", manifest.Files.Keys);          // forward slash, not OS separator
        Assert.DoesNotContain("css\\site.css", manifest.Files.Keys);
        Assert.Equal(3, manifest.Files["index.html"].Size);
    }

    [Fact]
    public void Compare_NewFile_IsUpload()
    {
        var local = new SyncManifest { Files = { ["a.html"] = new SyncEntry { Size = 1, Mtime = 100 } } };
        var reference = new SyncManifest();

        var diff = SyncManifestBuilder.Compare(local, reference);

        Assert.Equal(["a.html"], diff.ToUpload);
        Assert.Empty(diff.StaleRemote);
    }

    [Fact]
    public void Compare_SizeChange_IsUpload()
    {
        var local = new SyncManifest { Files = { ["a.html"] = new SyncEntry { Size = 2, Mtime = 100 } } };
        var reference = new SyncManifest { Files = { ["a.html"] = new SyncEntry { Size = 1, Mtime = 100 } } };

        var diff = SyncManifestBuilder.Compare(local, reference);

        Assert.Equal(["a.html"], diff.ToUpload);
    }

    [Fact]
    public void Compare_MtimeBeyondTolerance_IsUpload()
    {
        var local = new SyncManifest { Files = { ["a.html"] = new SyncEntry { Size = 1, Mtime = 110 } } };
        var reference = new SyncManifest { Files = { ["a.html"] = new SyncEntry { Size = 1, Mtime = 100 } } };

        var diff = SyncManifestBuilder.Compare(local, reference);

        Assert.Equal(["a.html"], diff.ToUpload);
    }

    [Fact]
    public void Compare_MtimeWithinTolerance_IsNotUpload()
    {
        var local = new SyncManifest { Files = { ["a.html"] = new SyncEntry { Size = 1, Mtime = 101 } } };
        var reference = new SyncManifest { Files = { ["a.html"] = new SyncEntry { Size = 1, Mtime = 100 } } };

        var diff = SyncManifestBuilder.Compare(local, reference); // default tolerance 2s

        Assert.Empty(diff.ToUpload);
    }

    [Fact]
    public void Compare_MissingLocally_IsStale()
    {
        var local = new SyncManifest();
        var reference = new SyncManifest { Files = { ["gone.html"] = new SyncEntry { Size = 1, Mtime = 100 } } };

        var diff = SyncManifestBuilder.Compare(local, reference);

        Assert.Empty(diff.ToUpload);
        Assert.Equal(["gone.html"], diff.StaleRemote);
    }

    [Fact]
    public void Compare_ResultsAreSorted()
    {
        var local = new SyncManifest
        {
            Files =
            {
                ["b.html"] = new SyncEntry { Size = 1, Mtime = 1 },
                ["a.html"] = new SyncEntry { Size = 1, Mtime = 1 },
            },
        };
        var diff = SyncManifestBuilder.Compare(local, new SyncManifest());
        Assert.Equal(diff.ToUpload.OrderBy(x => x, StringComparer.Ordinal), diff.ToUpload);
    }
}
