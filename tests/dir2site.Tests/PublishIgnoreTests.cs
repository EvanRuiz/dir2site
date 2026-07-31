// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using dir2site.Services;
using dir2site.SftpSync.Core;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// What may and may not be published. Dot-directories are excluded wholesale — they are tooling,
/// not content — but dot-<i>files</i> are not, because <c>.htaccess</c> has to reach the server.
/// Both halves of that need holding in place.
/// </summary>
public class PublishIgnoreTests : IDisposable
{
    private readonly string _site = Path.Combine(Path.GetTempPath(), "d2s-pub-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_site, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string rel)
    {
        var p = Path.Combine(_site, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "x");
    }

    [Theory]
    [InlineData(".claude/settings.json")]
    [InlineData(".claude/agents/thing.md")]
    [InlineData(".git/config")]
    [InlineData(".anything-at-all/file.txt")]
    [InlineData("css/.sass-cache/x.scssc")]
    [InlineData(".well-known/acme-challenge/token")]
    [InlineData(".DS_Store")]
    [InlineData("css/.DS_Store")]
    [InlineData("node_modules/pkg/index.js")]
    public void NoDotDirectoryIsEverPublished(string rel) =>
        Assert.True(PublishIgnore.ShouldExclude(rel), rel + " should be excluded");

    [Theory]
    [InlineData("index.html")]
    [InlineData(".htaccess")]
    [InlineData("blog/.htaccess")]
    [InlineData(".nojekyll")]
    [InlineData("css/site.css")]
    public void DotFilesAreStillPublished(string rel) =>
        Assert.False(PublishIgnore.ShouldExclude(rel), rel + " should be published");

    [Fact]
    public void BuildLocal_LeavesClutterOutOfTheManifest()
    {
        Write("index.html");
        Write(".htaccess");
        Write(".claude/settings.json");
        Write(".DS_Store");

        var manifest = SyncManifestBuilder.BuildLocal(_site);

        Assert.Equal(
            [".htaccess", "index.html"],
            manifest.Files.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void ServerSideDotEntriesAreNotOfferedForDeletion()
    {
        var local = new SyncManifest();
        local.Files["index.html"] = new SyncEntry { Size = 1, Mtime = 100 };

        var remote = new SyncManifest();
        remote.Files["index.html"] = new SyncEntry { Size = 1, Mtime = 100 };
        remote.Files[".htaccess"] = new SyncEntry { Size = 1, Mtime = 100 };
        remote.Files[".well-known/acme-challenge/token"] = new SyncEntry { Size = 1, Mtime = 100 };
        remote.Files["old.html"] = new SyncEntry { Size = 1, Mtime = 100 };

        var diff = SyncManifestBuilder.Compare(local, remote);

        // dir2site never created these, so it has no business proposing to delete them — and it
        // matters more now that .well-known/ is no longer deployable and so always looks remote-only.
        Assert.DoesNotContain(".htaccess", diff.StaleRemote);
        Assert.DoesNotContain(".well-known/acme-challenge/token", diff.StaleRemote);
        Assert.Contains("old.html", diff.StaleRemote);
    }

    [Fact]
    public void ClutterAlreadyOnTheServerCanStillBeCleanedUp()
    {
        var remote = new SyncManifest();
        remote.Files[".claude/settings.json"] = new SyncEntry { Size = 1, Mtime = 100 };
        remote.Files[".DS_Store"] = new SyncEntry { Size = 1, Mtime = 100 };

        var diff = SyncManifestBuilder.Compare(new SyncManifest(), remote);

        Assert.Contains(".claude/settings.json", diff.StaleRemote);
        Assert.Contains(".DS_Store", diff.StaleRemote);
    }
}
