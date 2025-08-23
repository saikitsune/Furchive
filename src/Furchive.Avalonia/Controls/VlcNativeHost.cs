using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Media;

namespace Furchive.Avalonia.Controls;

/// <summary>
/// Minimal Win32 child window host for LibVLC rendering. Avoids reliance on VideoView's
/// NativeControlHost path (which was spawning an external VLC window) by explicitly
/// creating a WS_CHILD window and handing its HWND to LibVLC.
/// On non-Windows platforms this control is inert (no native handle).
/// </summary>
public class VlcNativeHost : NativeControlHost
{
    public IntPtr ChildHandle { get; private set; } = IntPtr.Zero;
    public event Action<IntPtr>? HandleCreated;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("VlcNativeHost currently implemented only for Windows");

        // Use STATIC class (always registered) for a simple child; could register custom class if needed later.
        const int WS_CHILD = 0x40000000;
        const int WS_VISIBLE = 0x10000000;
        IntPtr hwnd = CreateWindowExW(0, "STATIC", string.Empty, WS_CHILD | WS_VISIBLE, 0, 0, 10, 10,
            parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create child window for LibVLC host");
        }
        ChildHandle = hwnd;
        HandleCreated?.Invoke(hwnd);
        return new PlatformHandle(hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        try
        {
            if (ChildHandle != IntPtr.Zero)
            {
                DestroyWindow(ChildHandle);
            }
        }
        catch { }
        finally { ChildHandle = IntPtr.Zero; }
        // Intentionally do NOT call base.DestroyNativeControlCore(control) because base attempts
        // to cast the handle to INativeControlHostDestroyableControlHandle which PlatformHandle does not implement.
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        // Ensure child stays resized to our bounds
        if (ChildHandle != IntPtr.Zero && OperatingSystem.IsWindows())
        {
            var b = Bounds;
            SetWindowPos(ChildHandle, IntPtr.Zero, 0, 0, Math.Max(1, (int)b.Width), Math.Max(1, (int)b.Height), 0x0004 /*SWP_NOZORDER*/ | 0x0020 /*SWP_NOACTIVATE*/);
        }
    }

    #region Native
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    #endregion
}
