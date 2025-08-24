using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Data.Converters;
using Furchive.Core.Interfaces;
using Furchive.Core.Models;

namespace Furchive.Avalonia.Converters;

public class IsMediaDownloadedConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is not MediaItem item) return false;
            var settings = App.Services?.GetService(typeof(ISettingsService)) as ISettingsService;
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive");
            var defaultDir = settings?.GetSetting<string>("DefaultDownloadDirectory", fallback)
                ?? fallback;
            bool HasPool(MediaItem it) => it.TagCategories != null && (it.TagCategories.ContainsKey("page_number") || it.TagCategories.ContainsKey("pool_name"));
            string Sanitize(string s)
            {
                var invalid = Path.GetInvalidFileNameChars();
                var clean = new string((s ?? string.Empty).Where(c => !invalid.Contains(c)).ToArray());
                return clean.Replace(" ", "_");
            }
            string? ExtFromUrl(string? url)
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
            var hasPoolContext = HasPool(item);
            var template = hasPoolContext
                ? (settings?.GetSetting<string>("PoolFilenameTemplate", "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") ?? "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}")
                : (settings?.GetSetting<string>("FilenameTemplate", "{source}/{artist}/{id}.{ext}") ?? "{source}/{artist}/{id}.{ext}");
            var ext = string.IsNullOrWhiteSpace(item.FileExtension) ? ExtFromUrl(item.FullImageUrl) ?? "bin" : item.FileExtension;
            var rel = template
                .Replace("{source}", item.Source)
                .Replace("{artist}", Sanitize(item.Artist))
                .Replace("{id}", item.Id)
                .Replace("{safeTitle}", Sanitize(item.Title))
                .Replace("{ext}", ext)
                .Replace("{pool_name}", Sanitize(item.TagCategories != null && item.TagCategories.TryGetValue("pool_name", out var poolNameList) && poolNameList.Count > 0 ? poolNameList[0] : string.Empty))
                .Replace("{page_number}", Sanitize(item.TagCategories != null && item.TagCategories.TryGetValue("page_number", out var pageList) && pageList.Count > 0 ? pageList[0] : string.Empty));
            // Normalize path separators and Unicode for cross-platform consistency
            var relNorm = rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                relNorm = relNorm.Normalize(System.Text.NormalizationForm.FormC);
            }
            var full = Path.Combine(defaultDir, relNorm);
            if (File.Exists(full)) return true;
            try
            {
                var poolsRoot = Path.Combine(defaultDir, item.Source, "pools", Sanitize(item.Artist));
                if (Directory.Exists(poolsRoot))
                {
                    bool match(string file)
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                        {
                            name = name?.Normalize(System.Text.NormalizationForm.FormC);
                        }
                        return name != null && (name.Equals(item.Id, StringComparison.OrdinalIgnoreCase) || name.EndsWith("_" + item.Id, StringComparison.OrdinalIgnoreCase) || name.Contains(item.Id, StringComparison.OrdinalIgnoreCase));
                    }
                    foreach (var file in Directory.EnumerateFiles(poolsRoot, "*", SearchOption.AllDirectories))
                    {
                        if (match(file)) return true;
                    }
                }
            }
            catch { }
            return false;
        }
        catch { return false; }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
