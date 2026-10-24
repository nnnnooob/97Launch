using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Launcher.App;

/// <summary>
/// The few Win32 entry points the shell needs.
/// </summary>
/// <remarks>
/// Scoped deliberately: the launcher is a normal WinUI 3 app, and these exist
/// only for shell operations WinAppSDK does not expose — the window handle,
/// window-procedure hook, and sending a directory to the Recycle Bin.
/// </remarks>
internal static class Win32
{
    private const int GWLP_WNDPROC = -4;

    internal const uint FO_DELETE = 3;
    internal const ushort FOF_SILENT = 0x0004;
    internal const ushort FOF_NOCONFIRMATION = 0x0010;
    internal const ushort FOF_ALLOWUNDO = 0x0040;
    internal const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr itemIdList, uint attributes, out uint attributesFound);

    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int SHOpenFolderAndSelectItems(IntPtr itemIdList, uint itemCount, IntPtr items, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    internal delegate IntPtr SubclassProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        IntPtr idSubclass,
        IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    internal static extern bool SetWindowSubclass(
        IntPtr hwnd,
        SubclassProc callback,
        IntPtr idSubclass,
        IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    internal static extern IntPtr DefSubclassProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    /// <summary>
    /// Current physical pixel bounds of the window, including its frame.
    /// </summary>
    /// <remarks>
    /// Used instead of AppWindow.Size because that property is DPI-independent
    /// while SetWindowPos/GetWindowRect work in physical pixels. Saving one and
    /// restoring through the other silently scales the window by the monitor DPI
    /// (a 1.5x display reopened 1.5x too large), so both directions stay in
    /// physical pixels here.
    /// </remarks>
    internal static bool TryGetBounds(IntPtr hwnd, out RECT rect)
    {
        rect = default;
        return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out rect);
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// Scale factor of the display the window currently sits on (1.0 at 96 DPI,
    /// 1.5 at 144 DPI).
    /// </summary>
    /// <remarks>
    /// Bridge between the two unit systems in play: XAML lays out in DIPs, while
    /// SetWindowPos and WM_GETMINMAXINFO work in physical pixels. Converting
    /// explicitly beats assuming 100% scaling — on this 150% display an
    /// unconverted 1032 would render a window only 688 DIPs wide, too narrow for
    /// the sidebar, form and log pane to coexist.
    /// </remarks>
    internal static double GetDisplayScale(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
        {
            return 1.0;
        }

        var centre = new POINT
        {
            X = (rect.Left + rect.Right) / 2,
            Y = (rect.Top + rect.Bottom) / 2,
        };

        var monitor = MonitorFromPoint(centre, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero || GetDpiForMonitor(monitor, 0, out var dpiX, out _) != 0)
        {
            return 1.0;
        }

        return dpiX / 96.0;
    }

    /// <summary>Converts a DIP measurement to physical pixels.</summary>
    internal static int DipsToPixels(IntPtr hwnd, int dips)
        => (int)Math.Round(dips * GetDisplayScale(hwnd));

    /// <summary>Converts physical pixels to DIPs.</summary>
    internal static int PixelsToDips(IntPtr hwnd, int pixels)
    {
        var scale = GetDisplayScale(hwnd);
        return scale <= 0 ? pixels : (int)Math.Round(pixels / scale);
    }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    /// <summary>
    /// Resolves the Win32 handle backing a WinUI <see cref="Window"/>.
    /// </summary>
    /// <remarks>
    /// WinAppSDK exposes no HWND property on Window; WindowNative is the
    /// supported interop helper for getting at it.
    /// </remarks>
    internal static IntPtr GetWindowHandle(Window window)
    {
        try
        {
            return WinRT.Interop.WindowNative.GetWindowHandle(window);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not resolve the window handle");
            return IntPtr.Zero;
        }
    }
}
