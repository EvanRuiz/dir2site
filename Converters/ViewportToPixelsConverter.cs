using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace OpenSeadragonOverlayEditor.Converters;

public class ViewportToPixelsConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        // values[0] = (double) Viewport x value or y value, or width/height
        // values[1] = (double) Viewport Width in Pixels
        // values[2] = (double or null) Start of Viewport in Pixels (optional, not used for width/height)
        if(values[0] is double xy && values[1] is double width)
        {
            if(values.Count > 2 && values[2] is double start)
            {
                return xy * width + start;    
            }

            return xy * width;
        } 
        
        return 0;
    }
}