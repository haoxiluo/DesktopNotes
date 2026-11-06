using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DesktopNotes.Converters;

/// <summary>"#RRGGBB" 字符串 → IBrush</summary>
public class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try { return new SolidColorBrush(Color.Parse(hex)); }
            catch { /* fall through */ }
        }
        return new SolidColorBrush(Color.Parse("#FFF6B3"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
