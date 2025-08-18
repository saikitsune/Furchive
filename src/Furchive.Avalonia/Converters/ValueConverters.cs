using System;
using System.Globalization;
using System.Collections.Generic;
using Avalonia.Data.Converters;
using Avalonia.Controls;

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
        return ext is "gif" or "webm" or "mp4" or "avi" or "mov" or "mkv";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
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
