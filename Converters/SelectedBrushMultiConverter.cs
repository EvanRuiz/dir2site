using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OpenSeadragonOverlayEditor.Converters;

public class SelectedBrushMultiConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        // values[0] = IsSelected (bool)
        // values[1] = default fill brush
        // values[2] = selected fill brush
        return values[0] is true ? values[2] : values[1];
    }
}