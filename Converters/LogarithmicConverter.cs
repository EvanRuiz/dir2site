using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace dir2site.Converters;

public class LogarithmicConverter : IValueConverter
{
    public double MinValue { get; set; } = 0.0;
    public double MaxValue { get; set; } = 1.0;
    public double SliderMin { get; set; } = 0;
    public double SliderMax { get; set; } = 100;
    
    public double MinValueLog => Math.Log10(MinValue);
    public double MaxValueLog => Math.Log10(MaxValue);
    public double ValueRangeLog => MaxValueLog - MinValueLog;
    
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double logValue)
        {
            // Logarithmic -> linear value
            return (Math.Log10(logValue) - MinValueLog) / ValueRangeLog * (SliderMax - SliderMin) + SliderMin;
        }
        return SliderMin;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double sliderValue)
        {
            // Linear -> logarithmic
            var normalizedSliderValue = (sliderValue - SliderMin) / (SliderMax - SliderMin);
            var logValue = MinValueLog + (normalizedSliderValue * ValueRangeLog);
            return Math.Pow(10, logValue);
        }
        return MinValue;
    }
}