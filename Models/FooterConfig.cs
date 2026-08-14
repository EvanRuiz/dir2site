// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace dir2site.Models;

/// <summary>
/// One row in the site footer, stored in <c>dir2site.yaml</c> alongside the rest of the project
/// config. A list of these replaces the hand-written HTML a multi-column footer used to need.
/// </summary>
public class FooterItem
{
    /// <summary>
    /// 1-based column this row belongs to. Out-of-range values clamp and empty columns close up, so
    /// a footer never renders a gap where a mistyped number used to be.
    /// </summary>
    public int Column { get; set; } = 1;

    /// <summary>
    /// Bootstrap Icons name, with or without the <c>bi-</c> prefix. Empty renders no glyph.
    /// </summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>Hex colour for the glyph. Empty inherits the footer's link colour.</summary>
    public string IconColor { get; set; } = string.Empty;

    /// <summary>
    /// Hex colour shown through a brand glyph's knockout — the white in a YouTube mark.
    ///
    /// Bootstrap's brand glyphs are a single shape with the inner symbol cut out, so on a dark
    /// footer that cut-out shows the band rather than white and the logo reads as wrong. Setting
    /// this paints a patch behind the glyph which the glyph's own ink then masks, leaving colour
    /// visible only in the cut-out. Empty leaves the knockout transparent.
    /// </summary>
    public string IconBackground { get; set; } = string.Empty;

    /// <summary>The link text. Escaped — this is a label, not markup.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Where the row goes, in one of three forms:
    /// <list type="bullet">
    /// <item><c>https://…</c>, <c>http://…</c> or <c>mailto:…</c> — left alone, opened in a new tab</item>
    /// <item>a leading <c>/</c> — a site-relative path, for a page dir2site didn't generate</item>
    /// <item>anything else — a project-relative path to an artifact or folder, resolved to wherever
    /// that artifact publishes</item>
    /// </list>
    /// </summary>
    public string Link { get; set; } = string.Empty;

    /// <summary>
    /// Muted caption under the link — a maintainer's name, a view count. Escaped, so it is a
    /// sentence rather than a place to put more markup; <c>footer:</c> remains the raw-HTML setting.
    /// </summary>
    public string Note { get; set; } = string.Empty;
}
