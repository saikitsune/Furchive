using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Furchive.Converters;

public class CategoryToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value?.ToString()?.ToLowerInvariant() ?? string.Empty;
        // Define distinct, accessible colors per category
        return key switch
        {
            "artist" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00)), // gold tint
            "copyright" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xAD, 0xD8, 0xE6)), // light blue
            "character" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x98, 0xFB, 0x98)), // pale green
            "species" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD8, 0xBF, 0xD8)), // thistle
            "general" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD3, 0xD3, 0xD3)), // light gray
            "meta" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xB6, 0xC1)), // light pink
            "lore" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE6, 0xE6, 0xFA)), // lavender
            "invalid" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFA, 0x80, 0x72)), // salmon
            _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDD, 0xDD, 0xDD))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
