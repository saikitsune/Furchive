using System;
using System.Globalization;
using System.Windows.Data;

namespace Furchive.Converters;

public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolean)
            return !boolean;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolean)
            return !boolean;
        return true;
    }
}

public class NullToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value != null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class BytesToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size = size / 1024;
            }
            
            return $"{size:0.##} {sizes[order]}";
        }
        
        return "0 B";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class UppercaseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(s)) return s;
        return s.Trim('.').ToUpperInvariant();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString()?.ToLowerInvariant() ?? string.Empty;
}

public class StringEqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2) return false;
        var a = values[0]?.ToString()?.Trim();
        var b = values[1]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
