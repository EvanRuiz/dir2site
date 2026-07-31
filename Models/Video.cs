// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using YamlDotNet.Serialization;

namespace dir2site.Models;

/// <summary>
/// A video embedded from an external provider, sourced from a Windows internet shortcut (.url).
/// </summary>
/// <remarks>
/// Unlike every other artifact type, a video gets no page of its own — it plays inline on the
/// collection index. <see cref="Provider"/> and <see cref="VideoId"/> are written into the yaml
/// so they are visible and diffable, but the .url file is the source of truth and overwrites them
/// on every traversal; <see cref="Start"/> is left alone once set, so a hand-tuned offset survives.
/// </remarks>
public class Video : Artifact
{
    public string? Provider {get; set;}
    public string? VideoId {get; set;}

    /// Playback offset in seconds. Null means start at the beginning.
    public int? Start {get; set;}

    // Runtime Only — read back from the .url file, not persisted to the yaml
    [YamlIgnore] public string? SourceUrl {get; set;}
}
