using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;

namespace Furchive.Avalonia.Converters;

public class StringNotEmptyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s);
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class EnumerableHasItemsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IEnumerable en)
        {
            foreach (var _ in en) return true; // any item
            return false;
        }
        return false;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Expects a KeyValuePair<string,List<string>>. Returns true if list has items and (optionally) key matches filter list (if parameter provided as comma list).
/// </summary>
public class TagCategoryGroupVisibleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value == null) return false;
            var t = value.GetType();
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                var key = t.GetProperty("Key")?.GetValue(value) as string;
                var listObj = t.GetProperty("Value")?.GetValue(value);
                if (listObj is IEnumerable en)
                {
                    bool any = en.GetEnumerator().MoveNext();
                    if (!any) return false;
                    if (key != null && key.Equals("pool_name", StringComparison.OrdinalIgnoreCase))
                        return false; // always hide pool_name group (surfaced separately)
                    if (parameter is string filter && !string.IsNullOrWhiteSpace(filter))
                    {
                        var set = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToLowerInvariant()).ToHashSet();
                        if (key == null) return false;
                        return set.Contains(key.ToLowerInvariant());
                    }
                    return true;
                }
            }
        }
        catch { }
        return false;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Accepts a dictionary (IDictionary or IEnumerable of KeyValuePair) of tag categories and returns true if any non-pool_name category has at least one tag.
/// </summary>
public class TagCategoriesAnyVisibleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IEnumerable en)
        {
            foreach (var item in en)
            {
                try
                {
                    var t = item.GetType();
                    if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                    {
                        var key = t.GetProperty("Key")?.GetValue(item) as string;
                        if (string.Equals(key, "pool_name", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var listObj = t.GetProperty("Value")?.GetValue(item) as IEnumerable;
                        if (listObj != null)
                        {
                            var enumerator = listObj.GetEnumerator();
                            if (enumerator.MoveNext())
                                return true;
                        }
                    }
                }
                catch { }
            }
        }
        return false;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
