// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Text.Json;
using dir2site.Models;

namespace dir2site.Services;

/// <summary>
/// Reads and writes the main window's <see cref="WindowGeometry"/> as JSON under the per-user app
/// config directory. Unlike the SFTP stores next door this is app-global rather than keyed by
/// project — there is one main window, and it should reopen where you left it regardless of which
/// site you were working on.
///
/// Every failure is swallowed: a window that can't remember its position is a papercut, but one
/// that refuses to close because it couldn't write a file is not.
/// </summary>
public sealed class WindowGeometryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The real store, e.g. <c>%AppData%/dir2site/ui</c>.</summary>
    public static WindowGeometryStore Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "dir2site", "ui"));

    private readonly string _directory;

    /// <summary>Tests point this at a temp directory; production uses <see cref="Default"/>.</summary>
    public WindowGeometryStore(string directory) => _directory = directory;

    private string FilePath => Path.Combine(_directory, "window.json");

    /// <summary>The saved geometry, or null if there is none, it is unreadable, or it is nonsense.</summary>
    public WindowGeometry? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var geometry = JsonSerializer.Deserialize<WindowGeometry>(File.ReadAllText(FilePath));
            if (geometry == null) return null;

            // A record from a future (or corrupted) shape can't be trusted field by field.
            if (geometry.Version != WindowGeometry.CurrentVersion) return null;
            if (geometry.Width <= 0 || geometry.Height <= 0) return null;
            if (geometry.Scaling <= 0) return null;

            return geometry;
        }
        catch
        {
            return null;
        }
    }

    public void Save(WindowGeometry geometry)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(geometry, JsonOptions));
        }
        catch
        {
            // Losing the window position is not worth interrupting a shutdown over.
        }
    }
}
