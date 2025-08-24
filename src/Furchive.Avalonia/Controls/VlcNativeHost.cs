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

        EnsureWindowClassRegistered();

        const int WS_CHILD = 0x40000000;
        const int WS_VISIBLE = 0x10000000;
        IntPtr hwnd = CreateWindowExW(0, WindowClassName, string.Empty, WS_CHILD | WS_VISIBLE, 0, 0, 10, 10,
            parent.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
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
    private const string WindowClassName = "FurchiveVlcHostWnd";
    private static bool _classRegistered;
    private static WndProcDelegate? _wndProcDelegate; // keep ref so GC doesn't collect

    private static void EnsureWindowClassRegistered()
    {
        if (_classRegistered) return;
        _wndProcDelegate = WndProc; // assign once
        var wc = new WNDCLASSEXW();
        wc.cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>();
        wc.style = 0x0002 | 0x0001; // CS_VREDRAW | CS_HREDRAW
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        wc.cbClsExtra = 0;
        wc.cbWndExtra = 0;
        wc.hInstance = GetModuleHandle(null);
        wc.hIcon = IntPtr.Zero;
        wc.hCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512); // IDC_ARROW
        wc.hbrBackground = GetStockObject(4); // BLACK_BRUSH
        wc.lpszMenuName = IntPtr.Zero;
        wc.lpszClassName = Marshal.StringToHGlobalUni(WindowClassName);
        wc.hIconSm = IntPtr.Zero;
        ushort atom = RegisterClassExW(ref wc);
        // free allocated class name string AFTER registration; class keeps its own copy internally
        try { Marshal.FreeHGlobal(wc.lpszClassName); } catch { }
        if (atom == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to register VLC host window class");
        _classRegistered = true;
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WM_ERASEBKGND = 0x0014;
        if (msg == WM_ERASEBKGND)
        {
            // Return 1 to indicate background erased (solid black via class brush) to avoid flicker/white flashes
            return (IntPtr)1;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);
    #endregion
}
