using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;

namespace Furchive.Avalonia.Converters;

public class PathToFileNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return System.IO.Path.GetFileName(path); } catch { return path; }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}