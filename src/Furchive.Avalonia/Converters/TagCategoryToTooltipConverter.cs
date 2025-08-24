using System;
using Avalonia.Data.Converters;

namespace Furchive.Avalonia.Converters;

public class TagCategoryToTooltipConverter : IValueConverter
{
    private const string PoolTooltip = "View this pool";
    private const string DefaultTooltip = "Left-click: include tag; Right-click: exclude tag";

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string key)
        {
            if (string.Equals(key, "pool_name", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "pool_id", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "page_number", StringComparison.OrdinalIgnoreCase))
            {
                return PoolTooltip;
            }
        }
        return DefaultTooltip;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
