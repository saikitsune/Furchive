using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Furchive.Core.Models;

namespace Furchive.Avalonia.Converters;

public class DownloadEtaConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is not DownloadJob job) return string.Empty;
            if (job.Status != DownloadStatus.Downloading || job.TotalBytes <= 0 || job.BytesDownloaded <= 0 || job.StartedAt == null)
                return string.Empty;
            var elapsed = DateTime.UtcNow - job.StartedAt.Value;
            if (elapsed.TotalSeconds <= 0.5) return string.Empty;
            var rate = job.BytesDownloaded / Math.Max(1.0, elapsed.TotalSeconds); // bytes/sec
            var remaining = Math.Max(0, job.TotalBytes - job.BytesDownloaded);
            var seconds = remaining / Math.Max(1.0, rate);
            if (double.IsInfinity(seconds) || double.IsNaN(seconds)) return string.Empty;
            var eta = TimeSpan.FromSeconds(seconds);
            string Format(TimeSpan ts)
            {
                if (ts.TotalHours >= 1) return $"ETA {Math.Floor(ts.TotalHours):0}:{ts.Minutes:00}:{ts.Seconds:00}";
                return $"ETA {ts.Minutes:0}:{ts.Seconds:00}";
            }
            return Format(eta);
        }
        catch { return string.Empty; }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
