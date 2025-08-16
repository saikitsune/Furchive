using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace Furchive.Converters;

public class CategoryKeyToHeaderConverter : IValueConverter
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["artist"] = "Artist",
        ["copyright"] = "Copyright",
        ["character"] = "Character",
        ["species"] = "Species",
        ["general"] = "General",
        ["meta"] = "Meta",
        ["invalid"] = "Invalid",
        ["lore"] = "Lore"
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value?.ToString() ?? string.Empty;
        if (Map.TryGetValue(key, out var header)) return header;
        // Fallback: TitleCase
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(key.Replace('_', ' '));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class FilterAndOrderCategoriesConverter : IValueConverter
{
    private static readonly string[] Order = new[]
    {
        "artist","copyright","character","species","general","meta","lore","invalid"
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IDictionary<string, List<string>> dict) return Array.Empty<KeyValuePair<string, List<string>>>();
        var filtered = dict
            .Where(kv => kv.Value != null && kv.Value.Count > 0)
            .Select(kv => new KeyValuePair<string, List<string>>(kv.Key, kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList()));

        var ordered = filtered
            .OrderBy(kv => Array.IndexOf(Order, kv.Key) is int idx && idx >= 0 ? idx : int.MaxValue)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ordered;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
