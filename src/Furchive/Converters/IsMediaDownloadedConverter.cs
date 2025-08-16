using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using Furchive.Core.Interfaces;
using Furchive.Core.Models;

namespace Furchive.Converters;

public class IsMediaDownloadedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            if (value is not MediaItem item) return false;
            var settings = App.Services?.GetService(typeof(ISettingsService)) as ISettingsService;
            var defaultDir = settings?.GetSetting<string>("DefaultDownloadDirectory",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Downloads"))
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Downloads");
            var template = settings?.GetSetting<string>("FilenameTemplate", "{source}/{artist}/{id}_{safeTitle}.{ext}")
                ?? "{source}/{artist}/{id}_{safeTitle}.{ext}";
            static string Sanitize(string s)
            {
                var invalid = Path.GetInvalidFileNameChars();
                var clean = new string((s ?? string.Empty).Where(c => !invalid.Contains(c)).ToArray());
                return clean.Replace(" ", "_");
            }
            static string? ExtFromUrl(string? url)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(url)) return null;
                    var uri = new Uri(url);
                    var ext = Path.GetExtension(uri.AbsolutePath).Trim('.').ToLowerInvariant();
                    return string.IsNullOrEmpty(ext) ? null : ext;
                }
                catch { return null; }
            }
            var ext = string.IsNullOrWhiteSpace(item.FileExtension) ? ExtFromUrl(item.FullImageUrl) ?? "bin" : item.FileExtension;
            var rel = template
                .Replace("{source}", item.Source)
                .Replace("{artist}", Sanitize(item.Artist))
                .Replace("{id}", item.Id)
                .Replace("{safeTitle}", Sanitize(item.Title))
                .Replace("{ext}", ext);
            var full = Path.Combine(defaultDir, rel);
            return File.Exists(full) ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            return Visibility.Collapsed;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
