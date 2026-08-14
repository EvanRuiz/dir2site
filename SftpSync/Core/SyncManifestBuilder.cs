// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dir2site.Services;

namespace dir2site.SftpSync.Core;

/// <summary>Builds local manifests and diffs them against a reference (last-uploaded) manifest.</summary>
public static class SyncManifestBuilder
{
    /// <summary>The result of comparing the current local site against a reference manifest.</summary>
    public sealed record Diff(IReadOnlyList<string> ToUpload, IReadOnlyList<string> StaleRemote);

    /// <summary>
    /// Walks <paramref name="siteRoot"/> recording size + mtime for every file (stat only, no reads).
    /// Relative paths use forward slashes so they match remote SFTP paths.
    /// </summary>
    public static SyncManifest BuildLocal(string siteRoot)
    {
        var manifest = new SyncManifest();
        if (!Directory.Exists(siteRoot)) return manifest;

        foreach (var full in Directory.EnumerateFiles(siteRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(siteRoot, full).Replace(Path.DirectorySeparatorChar, '/');

            // Generating offers to remove what it didn't put there, but deliberately not
            // dot-entries — it doesn't delete what it didn't create — so .DS_Store and a stray
            // .claude/ stay until someone clears them by hand. Refusing to publish known clutter
            // is cheaper than noticing it on a live server.
            if (PublishIgnore.ShouldExclude(rel)) continue;

            var info = new FileInfo(full);
            manifest.Files[rel] = new SyncEntry
            {
                Size  = info.Length,
                Mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
            };
        }

        return manifest;
    }

    /// <summary>
    /// Compares the current local manifest against a reference. A file is uploaded when it is new
    /// or its size/mtime differs; a file present in the reference but missing locally is "stale".
    /// </summary>
    public static Diff Compare(SyncManifest local, SyncManifest reference, long mtimeToleranceSeconds = 2)
    {
        var toUpload = new List<string>();
        foreach (var (path, entry) in local.Files)
        {
            if (!reference.Files.TryGetValue(path, out var refEntry) ||
                refEntry.Size != entry.Size ||
                Math.Abs(refEntry.Mtime - entry.Mtime) > mtimeToleranceSeconds)
            {
                toUpload.Add(path);
            }
        }

        var stale = new List<string>();
        foreach (var path in reference.Files.Keys)
            if (!local.Files.ContainsKey(path) && MayBeDeleted(path))
                stale.Add(path);

        toUpload.Sort(StringComparer.Ordinal);
        stale.Sort(StringComparer.Ordinal);
        return new Diff(toUpload, stale);
    }

    /// <summary>
    /// Whether a remote-only file may be offered for deletion.
    ///
    /// Server-side dot-entries are excluded: <c>.htaccess</c> and <c>.well-known/</c> are managed on
    /// the server (the latter is how certificate renewal proves domain control), and dir2site has no
    /// business proposing to delete something it never created. Known clutter is still offered, so a
    /// <c>.claude/</c> that reached the server before this filter existed can be cleaned up.
    /// </summary>
    private static bool MayBeDeleted(string relativePath) =>
        PublishIgnore.IsKnownClutter(relativePath) ||
        !relativePath.Split('/').Any(segment => segment.StartsWith('.'));
}
