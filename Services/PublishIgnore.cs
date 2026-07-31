// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;

namespace dir2site.Services;

/// <summary>
/// What is never part of a published site: every dot-directory, plus OS clutter and tool state by
/// name. Shared by the source-tree walk (<see cref="DirectoryTraverser"/>) and the SFTP upload, so
/// the two can't drift apart.
///
/// Dot-<i>directories</i> are excluded wholesale — they are somebody's tooling, not content. Dot-
/// <i>files</i> are not, because <c>.htaccess</c> is real site configuration that has to reach the
/// server; the ones that are junk (<c>.DS_Store</c>) are named below instead.
///
/// The cost of the directory rule is <c>.well-known/</c>, which a site would use for certificate
/// renewal. That is normally written on the server by certbot rather than generated here, and
/// server-side dot-entries are protected from the stale-file deletion list, so an existing one is
/// left alone — it just can't be deployed <i>from</i> <c>_site</c>.
/// </summary>
public static class PublishIgnore
{
    /// <summary>Files that are clutter wherever they appear.</summary>
    public static readonly IReadOnlySet<string> JunkFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // macOS
        ".DS_Store",
        ".AppleDouble",
        ".LSOverride",
        "Icon\r",          // macOS custom folder icon (has a carriage return in the name)
        ".Spotlight-V100",
        ".Trashes",
        ".fseventsd",
        ".VolumeIcon.icns",
        ".com.apple.timemachine.donotpresent",

        // Windows
        "Thumbs.db",
        "Thumbs.db:encryptable",
        "ehthumbs.db",
        "ehthumbs_vista.db",
        "Desktop.ini",
        "desktop.ini",
        "$RECYCLE.BIN",
        "RECYCLER",
        "RECYCLED",
        "System Volume Information",

        // Linux / general
        ".directory",      // KDE folder settings
        ".Trash-1000",
        ".nfs",            // NFS lock files (prefix match handled by IsJunkFile)
    };

    /// <summary>Directories that are clutter wherever they appear, along with everything inside them.</summary>
    public static readonly IReadOnlySet<string> JunkDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // macOS
        ".Spotlight-V100",
        ".Trashes",
        ".fseventsd",
        ".TemporaryItems",
        ".AppleDB",
        ".AppleDesktop",

        // Windows
        "$RECYCLE.BIN",
        "RECYCLER",
        "RECYCLED",
        "System Volume Information",

        // Version control / tooling. The dot-prefixed ones are already covered by the blanket rule
        // in IsJunkDirectory; they stay named so IsKnownClutter can recognise them on a server.
        ".git",
        ".svn",
        ".hg",
        ".idea",
        ".vscode",
        ".claude",
        ".dir2site",
        "node_modules",
        "__pycache__",
        ".mypy_cache",
        ".pytest_cache",
    };

    public static bool IsJunkDirectory(string name) =>
        name.StartsWith('.') || JunkDirectoryNames.Contains(name);

    public static bool IsJunkFile(string name) =>
        JunkFileNames.Contains(name) || name.StartsWith(".nfs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a forward-slash relative path is not publishable — either the file itself is
    /// clutter, or it sits under a directory that is.
    /// </summary>
    public static bool ShouldExclude(string relativePath)
    {
        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
            if (IsJunkDirectory(segments[i]))
                return true;

        return IsJunkFile(segments[^1]);
    }

    /// <summary>
    /// True when a path is recognisably somebody's tooling or OS clutter <i>by name</i> — not merely
    /// dot-prefixed.
    ///
    /// This is the stricter question, and it is the one to ask about files already on a server.
    /// dir2site does not manage server-side dot-entries and should not propose deleting them, but a
    /// <c>.claude/</c> that reached the server before it stopped deploying those is worth offering
    /// to clean up.
    /// </summary>
    public static bool IsKnownClutter(string relativePath)
    {
        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
            if (JunkDirectoryNames.Contains(segments[i]))
                return true;

        return IsJunkFile(segments[^1]);
    }
}
