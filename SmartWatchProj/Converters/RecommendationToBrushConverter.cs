using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SmartWatchProj.Converters;

public sealed class RecommendationToBrushConverter : IValueConverter
{
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#2E7D32"));
    private static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#FFCC40")); // фирменный жёлтый
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#E64517"));    // фирменный красный
    private static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#B0BEC5"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value?.ToString() ?? "").ToLowerInvariant();

        if (s.Contains("риск")) return Red;
        if (s.Contains("вниман")) return Yellow;
        if (s.Contains("норм")) return Green;

        return Neutral;
    }


    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
