using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Furchive.Avalonia.Converters;

/// <summary>
/// Similar to BytesToHumanConverter but never shows units smaller than MB.
/// Values under 1 MB are rendered as fractional MB (e.g., 0.74 MB). Larger units (GB, TB) still used.
/// </summary>
public sealed class BytesToHumanMinMbConverter : IValueConverter
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
        const double KB = 1024d;
        const double MB = KB * 1024d;
        const double GB = MB * 1024d;
        const double TB = GB * 1024d;

        if (bytes <= 0) return "0 MB";
        if (bytes < MB)
        {
            var mb = bytes / MB; // fractional MB
            return mb < 0.01 ? "0.01 MB" : $"{mb:0.##} MB"; // clamp tiny values
        }
        if (bytes < GB)
        {
            var mb = bytes / MB;
            return $"{mb:0.##} MB";
        }
        if (bytes < TB)
        {
            var gb = bytes / GB;
            return $"{gb:0.##} GB";
        }
        var tb = bytes / TB;
        return $"{tb:0.##} TB";
    }
}