using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace Furchive.Converters;

public class FileExtensionToVisibilityConverter : IValueConverter
{
    // parameter: "image" | "video" | "swf"
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var ext = (value as string ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
        var kind = (parameter as string ?? string.Empty).ToLowerInvariant();

        string[] imageExts = new[] { "jpg", "jpeg", "png", "gif" };
        string[] videoExts = new[] { "mp4", "webm", "mov", "m4v" };

        bool isImage = imageExts.Contains(ext);
        bool isVideo = videoExts.Contains(ext);
        bool isSwf = ext == "swf";

        bool show = kind switch
        {
            "image" => isImage || string.IsNullOrEmpty(ext), // default to image if unknown
            "video" => isVideo,
            "swf" => isSwf,
            _ => false
        };

        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
