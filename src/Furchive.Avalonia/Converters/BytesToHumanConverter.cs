using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Furchive.Avalonia.Converters;

public class BytesToHumanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long l) return Format(l);
        if (value is int i) return Format(i);
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    private static string Format(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double d = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        int idx = -1;
        while (d >= 1024 && idx < units.Length - 1)
        {
            d /= 1024;
            idx++;
        }
        return $"{d:0.##} {units[idx]}";
    }
}