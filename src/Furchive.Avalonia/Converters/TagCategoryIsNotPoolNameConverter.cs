using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Furchive.Avalonia.Converters;

/// <summary>
/// Returns true (keep visible) for any tag category key except "pool_name".
/// Used to suppress the duplicated pool_name category after surfacing
/// pool names earlier in the preview panel.
/// </summary>
public class TagCategoryIsNotPoolNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrWhiteSpace(key))
            return true; // show by default
        return !string.Equals(key, "pool_name", StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
