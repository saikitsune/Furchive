using System.Diagnostics;
using System.Runtime.InteropServices;
using Furchive.Core.Interfaces;

namespace Furchive.Core.Services;

public class PlatformShellService : IPlatformShellService
{
    public void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        TryOpen(path, isUrl: false, treatAsFolder: false);
    }

    public void OpenFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        TryOpen(folderPath, isUrl: false, treatAsFolder: true);
    }

    public void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        TryOpen(url, isUrl: true, treatAsFolder: false);
    }

    private static void TryOpen(string target, bool isUrl, bool treatAsFolder)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                });
                return;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    ArgumentList = { target }
                });
                return;
            }
            // Linux and others
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                ArgumentList = { target }
            });
        }
        catch
        {
            // swallow; caller can log if needed
        }
    }
}
