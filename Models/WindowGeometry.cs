// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace dir2site.Models;

/// <summary>
/// The main window's last known size and position, as persisted between runs.
///
/// The two halves are in different units, which is the whole reason <see cref="Scaling"/> exists:
/// <see cref="X"/>/<see cref="Y"/> come from <c>Window.Position</c>, which is physical desktop
/// pixels on Windows, while <see cref="Width"/>/<see cref="Height"/> come from
/// <c>Window.Width</c>/<c>Height</c>, which are device-independent. <see cref="Scaling"/> records
/// the <c>DesktopScaling</c> in force when the record was written, so the off-screen check can put
/// both into the same space. (<c>DesktopScaling</c>, not <c>RenderScaling</c>: on a Retina Mac the
/// latter is 2.0 while Cocoa still positions windows in points.)
///
/// Only normal-state bounds are ever stored — never a maximized, minimized or full-screen frame.
/// </summary>
public sealed class WindowGeometry
{
    /// <summary>Shape version, so a future change can be ignored rather than misread.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Left edge of the window frame, in desktop pixels.</summary>
    public int X { get; set; }

    /// <summary>Top edge of the window frame, in desktop pixels.</summary>
    public int Y { get; set; }

    /// <summary>Client width in device-independent pixels.</summary>
    public double Width { get; set; }

    /// <summary>Client height in device-independent pixels.</summary>
    public double Height { get; set; }

    /// <summary>The <c>DesktopScaling</c> that related the position and the size when saved.</summary>
    public double Scaling { get; set; } = 1.0;
}
