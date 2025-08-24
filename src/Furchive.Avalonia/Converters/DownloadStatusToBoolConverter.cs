using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Furchive.Core.Models;

namespace Furchive.Avalonia.Converters;

// Converts DownloadStatus to a bool for button visibility (true => visible)
public class DownloadStatusToBoolConverter : IValueConverter
{
    public string? Target { get; set; }
    public string? Targets { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is not DownloadStatus status) return false;
            var set = (Targets ?? Target ?? string.Empty)
                .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var t in set)
            {
                if (Enum.TryParse<DownloadStatus>(t, ignoreCase: true, out var s) && s == status)
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
