// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;

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
            if (!local.Files.ContainsKey(path))
                stale.Add(path);

        toUpload.Sort(StringComparer.Ordinal);
        stale.Sort(StringComparer.Ordinal);
        return new Diff(toUpload, stale);
    }
}
