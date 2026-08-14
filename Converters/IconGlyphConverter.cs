// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Globalization;
using Avalonia.Data.Converters;
using dir2site.Services;

namespace dir2site.Converters;

/// <summary>
/// An icon name to the character that draws it, so a field holding a name can show the mark beside
/// it. A name that isn't one converts to nothing, which is also the answer while one is half-typed.
/// </summary>
public class IconGlyphConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BootstrapIcons.GlyphFor(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A glyph does not identify its name; several share one.");
}
