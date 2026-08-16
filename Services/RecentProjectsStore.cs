// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dir2site.Models;

namespace dir2site.Services;

/// <summary>
/// Reads and writes the recently opened project folders as JSON under the per-user app config
/// directory, next door to <see cref="WindowGeometryStore"/>'s <c>window.json</c>. Like that store
/// this is app-global rather than keyed by project — the whole point is to list projects before one
/// is open.
///
/// Every failure is swallowed. Forgetting which folders you had open is a papercut; refusing to
/// open a project because a shortcut list wouldn't parse is not.
///
/// Nothing stops two copies of the app running at once, and <see cref="Remember"/> is a
/// read-modify-write, so two instances opening different projects at the same moment can race and
/// the later write wins. That is accepted: the loser reappears the next time that project is
/// opened. What is not accepted is a reader seeing a half-written file, so <see cref="Save"/>
/// stages through a temp file and renames. No lock file — one orphaned by a crash would be a worse
/// failure than a lost entry.
/// </summary>
public sealed class RecentProjectsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The real store, e.g. <c>%AppData%/dir2site/ui</c>.</summary>
    public static RecentProjectsStore Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "dir2site", "ui"));

    /// <summary>
    /// Windows and macOS reach the same folder through different casings; Linux does not.
    /// </summary>
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private readonly string _directory;

    /// <summary>Tests point this at a temp directory; production uses <see cref="Default"/>.</summary>
    public RecentProjectsStore(string directory) => _directory = directory;

    private string FilePath => Path.Combine(_directory, "recent.json");

    private string TempFilePath => FilePath + ".tmp";

    /// <summary>
    /// The same normalization <c>SftpProfileStore</c> uses to key a project, so the two agree on
    /// when two paths are the same folder. Returns null for a path the filesystem can't make sense
    /// of, which a hand-edited file can easily contain.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The remembered folders, newest first. Empty — never null — when there are none, the file is
    /// unreadable, or it was written by a version whose shape we can't trust.
    /// </summary>
    public IReadOnlyList<RecentProjectEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var recents = JsonSerializer.Deserialize<RecentProjects>(File.ReadAllText(FilePath));
            if (recents?.Projects == null) return [];
            if (recents.Version != RecentProjects.CurrentVersion) return [];

            return recents.Projects
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
                .OrderByDescending(entry => entry.LastOpenedUtc)
                .Take(RecentProjects.MaxEntries)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public void Save(IEnumerable<RecentProjectEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var recents = new RecentProjects
            {
                Projects = entries.Take(RecentProjects.MaxEntries).ToList(),
            };

            // Staged and renamed so a second instance reading mid-write sees the old list rather
            // than a truncated one.
            File.WriteAllText(TempFilePath, JsonSerializer.Serialize(recents, JsonOptions));
            File.Move(TempFilePath, FilePath, overwrite: true);
        }
        catch
        {
            // A shortcut list that can't be written is not worth interrupting anything over.
        }
    }

    /// <summary>Moves <paramref name="projectPath"/> to the front of the list, deduplicated.</summary>
    public void Remember(string projectPath)
    {
        var normalized = Normalize(projectPath);
        if (normalized == null) return;

        var entries = Without(normalized);

        entries.Insert(0, new RecentProjectEntry
        {
            Path = normalized,
            LastOpenedUtc = DateTime.UtcNow,
        });

        Save(entries);
    }

    /// <summary>
    /// Drops <paramref name="projectPath"/> from the list. Only the shortcut goes — nothing is
    /// touched inside the project itself, and opening it again brings the tile back.
    /// </summary>
    public void Forget(string projectPath)
    {
        var normalized = Normalize(projectPath);
        if (normalized == null) return;

        Save(Without(normalized));
    }

    private List<RecentProjectEntry> Without(string normalizedPath) =>
        Load()
            .Where(entry => !PathComparer.Equals(Normalize(entry.Path), normalizedPath))
            .ToList();
}
