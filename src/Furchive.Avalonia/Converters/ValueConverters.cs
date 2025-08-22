using System;
using System.Globalization;
using System.Collections.Generic;
using Avalonia.Data.Converters;
using Avalonia.Controls;
using Avalonia.Media;

namespace Furchive.Avalonia.Converters;

public class InverseBooleanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : (object)false;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : (object)true;
}

public class NullToBooleanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value != null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BytesToStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0; double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1) { order++; size /= 1024; }
            return $"{size:0.##} {sizes[order]}";
        }
        return "0 B";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class UppercaseConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(s)) return s;
        return s.Trim('.').ToUpperInvariant();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString()?.ToLowerInvariant() ?? string.Empty;
}

public class StringEqualsConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values == null || values.Count < 2) return false;
        var a = values[0]?.ToString()?.Trim();
        var b = values[1]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}

// For visibility in Avalonia prefer binding to IsVisible (bool) with a simple boolean converter.
public class NullToBoolVisibleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value != null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class FileTypeIsPlayableConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ext = (value?.ToString() ?? string.Empty).Trim('.').ToLowerInvariant();
        // Consider common video types as playable; treat GIF as an image so it shows in Image mode.
        bool isPlayable = ext is "webm" or "mp4" or "avi" or "mov" or "mkv";
        // Support optional inversion via ConverterParameter: pass False to get non-playable.
        if (parameter is string ps && bool.TryParse(ps, out var pBool))
            return pBool ? isPlayable : !isPlayable;
        if (parameter is bool pb)
            return pb ? isPlayable : !isPlayable;
        return isPlayable;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Returns the first non-empty string from a list of bindings.
public class FirstNonEmptyConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values == null) return null;
        foreach (var v in values)
        {
            var s = v?.ToString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return null;
    }
}

// Converts null to GridLength(0) and non-null to the provided pixel width (ConverterParameter) or Auto if not provided.
public class NullToGridLengthConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
            return new GridLength(0);

        if (parameter is double d)
            return new GridLength(d);
        var p = parameter?.ToString();
        if (string.IsNullOrWhiteSpace(p))
            return GridLength.Auto;
        if (string.Equals(p, "Auto", StringComparison.OrdinalIgnoreCase))
            return GridLength.Auto;
        if (p == "*")
            return new GridLength(1, GridUnitType.Star);
        if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels))
            return new GridLength(pixels);
        return GridLength.Auto;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Maps a tag category string to a background brush for tag chips.
public class TagCategoryToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> _map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["artist"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x2d, 0x6c, 0xdf)),
        ["character"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x9c, 0x27, 0xb0)),
        ["species"] = new SolidColorBrush(Color.FromArgb(0xFF, 0xff, 0x98, 0x00)),
        ["general"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x55, 0x55, 0x55)),
        ["meta"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x60, 0x7d, 0x8b)),
        ["lore"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x4c, 0xaf, 0x50)),
        ["copyright"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x79, 0x55, 0x48)),
    };

    // Slightly rounded fallback neutral color.
    private static readonly SolidColorBrush _default = new(Color.FromArgb(0xFF, 0x44, 0x44, 0x44));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value == null) return _default;
            var key = value.ToString() ?? string.Empty;
            if (_map.TryGetValue(key, out var brush)) return brush;
            return _default;
        }
        catch { return _default; }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Determines a tag's category using the currently selected media's TagCategories dictionary
// (value[0] = tag string, value[1] = Dictionary<string,List<string>> TagCategories) then maps to brush.
public class TagStringToCategoryBrushConverter : IMultiValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> _map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["artist"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x2d, 0x6c, 0xdf)),
        ["character"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x9c, 0x27, 0xb0)),
        ["species"] = new SolidColorBrush(Color.FromArgb(0xFF, 0xff, 0x98, 0x00)),
        ["general"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x55, 0x55, 0x55)),
        ["meta"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x60, 0x7d, 0x8b)),
        ["lore"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x4c, 0xaf, 0x50)),
        ["copyright"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x79, 0x55, 0x48)),
    };
    private static readonly SolidColorBrush _default = new(Color.FromArgb(0xFF, 0x44, 0x44, 0x44));

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (values == null || values.Count < 2) return _default;
            var tag = values[0]?.ToString();
            if (string.IsNullOrWhiteSpace(tag)) return _default;
            var dict = values[1] as IDictionary<string, List<string>>;
            if (dict != null)
            {
                foreach (var kvp in dict)
                {
                    try
                    {
                        if (kvp.Value != null && kvp.Value.Any(v => string.Equals(v, tag, StringComparison.OrdinalIgnoreCase)))
                        {
                            if (_map.TryGetValue(kvp.Key, out var brush)) return brush;
                            break; // matched category but no color mapping -> fallback
                        }
                    }
                    catch { }
                }
            }
            return _default;
        }
        catch { return _default; }
    }
}
