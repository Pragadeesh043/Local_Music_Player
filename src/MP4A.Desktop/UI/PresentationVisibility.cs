using System.Runtime.InteropServices;

namespace Soundroom;

internal static class WindowPresentation
{
    private const int ExtendedFrameBounds = 9;
    private const int Cloaked = 14;
    private const uint PreviousWindow = 3;
    private const int ExtendedStyle = -20;
    private const int LayeredOrTransparent = 0x80000 | 0x20;
    private const int RectSize = 4 * sizeof(int);

    public static double DpiScale(IntPtr window)
    {
        uint dpi = GetDpiForWindow(window);
        return (dpi == 0 ? 96 : dpi) / 96d;
    }

    public static bool IsExposed(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return true;
        if (!IsWindowVisible(window) || IsIconic(window) || IsCloaked(window))
            return false;
        if (GetFrame(window, ExtendedFrameBounds, out var frame, RectSize) != 0)
            return true;

        var above = GetWindow(window, PreviousWindow);
        for (int count = 0; above != IntPtr.Zero && count < 64; count++, above = GetWindow(above, PreviousWindow))
        {
            if (IsWindowVisible(above) && !IsIconic(above) && !IsCloaked(above) &&
                (GetWindowLong(above, ExtendedStyle) & LayeredOrTransparent) == 0 &&
                GetFrame(above, ExtendedFrameBounds, out var bounds, RectSize) == 0 && bounds.Covers(frame))
                return false;
        }
        return true;
    }

    private static bool IsCloaked(IntPtr window) =>
        GetCloak(window, Cloaked, out uint flags, sizeof(uint)) == 0 && flags != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly bool Covers(Rect other) =>
            Left <= other.Left && Top <= other.Top && Right >= other.Right && Bottom >= other.Bottom;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int GetCloak(IntPtr window, int attribute, out uint flags, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int GetFrame(IntPtr window, int attribute, out Rect bounds, int size);
}
