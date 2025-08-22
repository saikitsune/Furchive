using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Furchive.Avalonia.Converters;

public class TagCategoryKeyToDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrWhiteSpace(key)) return value;
        return key.ToLowerInvariant() switch
        {
            "pool_id" => "Pool ID",
            "pool_name" => "Pool Name",
            "page_number" => "Page Number",
            _ => char.ToUpperInvariant(key[0]) + key[1..]
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
