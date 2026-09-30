using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RDock;

/// <summary>將視窗置於目前／擁有者所在螢幕的工作區正中央。</summary>
public static class WindowPlacement
{
    public static void CenterOnScreen(Window window, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.UpdateLayout();

        double width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        double height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        if (double.IsNaN(width) || width <= 0) width = 480;
        if (double.IsNaN(height) || height <= 0) height = 360;

        double dpiX = 1, dpiY = 1;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(owner ?? window);
            dpiX = dpi.DpiScaleX;
            dpiY = dpi.DpiScaleY;
        }
        catch
        {
            // ignore
        }

        Rect work = GetWorkAreaDip(owner, dpiX, dpiY);
        window.Left = work.Left + Math.Max(0, (work.Width - width) / 2.0);
        window.Top = work.Top + Math.Max(0, (work.Height - height) / 2.0);
    }

    private static Rect GetWorkAreaDip(Window? owner, double dpiX, double dpiY)
    {
        IntPtr hwnd = IntPtr.Zero;
        if (owner is not null)
            hwnd = new WindowInteropHelper(owner).Handle;

        if (hwnd == IntPtr.Zero)
        {
            GetCursorPos(out POINT pt);
            IntPtr mon = MonitorFromPoint(pt, 2);
            return MonitorWorkAreaDip(mon, dpiX, dpiY);
        }

        IntPtr monitor = MonitorFromWindow(hwnd, 2);
        return MonitorWorkAreaDip(monitor, dpiX, dpiY);
    }

    private static Rect MonitorWorkAreaDip(IntPtr monitor, double dpiX, double dpiY)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            return new Rect(
                info.rcWork.Left / dpiX,
                info.rcWork.Top / dpiY,
                (info.rcWork.Right - info.rcWork.Left) / dpiX,
                (info.rcWork.Bottom - info.rcWork.Top) / dpiY);
        }

        return SystemParameters.WorkArea;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
