// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Generic;

namespace dir2site.SftpSync.Core;

/// <summary>One tracked file: enough to cheaply detect a change without reading its bytes.</summary>
public sealed class SyncEntry
{
    public long Size { get; set; }

    /// <summary>Last-write time as Unix seconds (UTC). Second resolution matches SFTP.</summary>
    public long Mtime { get; set; }

    // Reserved for a future content-hash fast-path upgrade; unused in v1.
    public string? Hash { get; set; }
}

/// <summary>
/// Snapshot of what was last uploaded: a map of forward-slash relative path → <see cref="SyncEntry"/>.
/// This is a fast-path accelerator only — it reflects the last upload, not the live server state
/// (see <see cref="SftpSyncService.VerifyAndRepair"/> for the source-of-truth reconciliation).
/// </summary>
public sealed class SyncManifest
{
    public int Version { get; set; } = 1;
    public Dictionary<string, SyncEntry> Files { get; set; } = new();
}
