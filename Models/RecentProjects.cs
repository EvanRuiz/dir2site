// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;

namespace dir2site.Models;

/// <summary>One project folder the user has opened, and when they last opened it.</summary>
public sealed class RecentProjectEntry
{
    /// <summary>Absolute path to the project root, normalized but with its original casing.</summary>
    public string Path { get; set; } = string.Empty;

    public DateTime LastOpenedUtc { get; set; }
}

/// <summary>
/// The project folders offered on the welcome screen, newest first.
///
/// Only paths and timestamps are stored. Each project's title and logo are read from its own
/// <c>dir2site.yaml</c> when the tiles are built, so renaming a site or swapping its logo shows up
/// immediately rather than waiting for the project to be opened again.
/// </summary>
public sealed class RecentProjects
{
    /// <summary>Shape version, so a future change can be ignored rather than misread.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// How many folders are remembered. A visual cap rather than a storage one — past a dozen
    /// tiles the welcome screen stops being a shortcut and becomes a list to read.
    /// </summary>
    public const int MaxEntries = 12;

    public int Version { get; set; } = CurrentVersion;

    public List<RecentProjectEntry> Projects { get; set; } = [];
}
