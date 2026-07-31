// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using Avalonia;

namespace dir2site.Services;

/// <summary>
/// Decides where a remembered window may actually be placed. This is the part of the restore path
/// with no Avalonia window involved — just rectangles — so it is the part worth unit testing.
///
/// The problem it solves: a window saved on a second monitor that is no longer plugged in, or on a
/// monitor whose resolution changed, would otherwise reopen somewhere the user cannot reach it.
/// </summary>
public static class WindowGeometryPolicy
{
    /// <summary>
    /// A restored window must keep at least this much of itself inside a work area, or we treat the
    /// saved placement as belonging to a screen that is gone. Roughly "enough title bar to grab".
    /// </summary>
    private const int MinVisibleWidth = 120;
    private const int MinVisibleHeight = 40;

    /// <summary>
    /// Fits <paramref name="saved"/> into whichever of <paramref name="workAreas"/> it mostly
    /// occupies, or centres it on the first entry when its original screen is gone.
    /// </summary>
    /// <param name="saved">The remembered frame, in desktop pixels.</param>
    /// <param name="workAreas">
    /// Screen work areas, <b>primary first</b> — that ordering is what makes the fallback land
    /// somewhere sensible. Empty means the platform couldn't tell us about any screens.
    /// </param>
    /// <param name="minSize">Floor for the result, normally the window's own MinWidth/MinHeight.</param>
    /// <returns>
    /// The rectangle to use, or null if the saved rectangle is unusable or there are no screens to
    /// validate it against. Null means "ignore the saved geometry and open at the default".
    /// </returns>
    public static PixelRect? Fit(PixelRect saved, IReadOnlyList<PixelRect> workAreas, PixelSize minSize)
    {
        if (workAreas == null || workAreas.Count == 0) return null;
        if (saved.Width <= 0 || saved.Height <= 0) return null;

        var target = workAreas[0];
        var bestArea = 0L;
        foreach (var area in workAreas)
        {
            if (area.Width <= 0 || area.Height <= 0) continue;
            var overlap = area.Intersect(saved);
            var overlapArea = (long)Math.Max(0, overlap.Width) * Math.Max(0, overlap.Height);
            if (overlapArea > bestArea)
            {
                bestArea = overlapArea;
                target = area;
            }
        }

        var visible = target.Intersect(saved);
        var strandedOffscreen = visible.Width < MinVisibleWidth || visible.Height < MinVisibleHeight;

        // Cap to the screen, but never below the caller's minimum unless the screen itself is smaller.
        var width = Math.Min(Math.Max(saved.Width, minSize.Width), target.Width);
        var height = Math.Min(Math.Max(saved.Height, minSize.Height), target.Height);

        int x, y;
        if (strandedOffscreen)
        {
            // The screen this window lived on is gone or has moved; start over, centred.
            x = target.X + (target.Width - width) / 2;
            y = target.Y + (target.Height - height) / 2;
        }
        else
        {
            // Shift back inside. Clamping the low edge last matters on macOS, where sliding a window
            // down off the bottom must not then push its title bar under the menu bar.
            x = Math.Max(target.X, Math.Min(saved.X, target.Right - width));
            y = Math.Max(target.Y, Math.Min(saved.Y, target.Bottom - height));
        }

        return new PixelRect(x, y, width, height);
    }
}
