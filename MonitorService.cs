using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RDock;

/// <summary>
/// 多螢幕列舉與邊緣吸附（Per-Monitor DPI）。
/// </summary>
public static class MonitorService
{
    public const int WmDpiChanged = 0x02E0;
    public const int WmDisplayChange = 0x007E;

    public readonly record struct MonitorInfo(
        int Index,
        bool IsPrimary,
        RectPx BoundsPx,
        RectPx WorkAreaPx,
        double DpiX,
        double DpiY);

    public readonly record struct RectPx(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        int index = 0;

        MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(hMonitor, ref mi))
                return true;

            uint dpiX = 96, dpiY = 96;
            if (GetDpiForMonitor(hMonitor, MonitorDpiType.EffectiveDpi, out uint x, out uint y) == 0)
            {
                dpiX = x == 0 ? 96u : x;
                dpiY = y == 0 ? 96u : y;
            }

            bool primary = (mi.dwFlags & MonitorInfoPrimary) != 0;
            list.Add(new MonitorInfo(
                index,
                primary,
                new RectPx(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
                new RectPx(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom),
                dpiX,
                dpiY));
            index++;
            return true;
        };

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        if (list.Count == 0)
        {
            int w = (int)SystemParameters.PrimaryScreenWidth;
            int h = (int)SystemParameters.PrimaryScreenHeight;
            list.Add(new MonitorInfo(0, true, new RectPx(0, 0, w, h), new RectPx(0, 0, w, h), 96, 96));
        }

        return list;
    }

    public static MonitorInfo Resolve(int screenIndex)
    {
        var monitors = GetMonitors();
        if (screenIndex >= 0 && screenIndex < monitors.Count)
            return monitors[screenIndex];

        foreach (var m in monitors)
        {
            if (m.IsPrimary)
                return m;
        }

        return monitors[0];
    }

    /// <summary>將視窗吸附到指定螢幕的指定邊緣中央（SWP_NOACTIVATE）。</summary>
    public static void PlaceDock(
        Window window,
        int screenIndex,
        DockEdge edge,
        double windowWidthDip,
        double windowHeightDip)
    {
        MonitorInfo monitor = Resolve(screenIndex);
        double sx = monitor.DpiX / 96.0;
        double sy = monitor.DpiY / 96.0;

        int widthPx = Math.Max(1, (int)Math.Round(windowWidthDip * sx));
        int heightPx = Math.Max(1, (int)Math.Round(windowHeightDip * sy));
        RectPx work = monitor.WorkAreaPx;

        int x;
        int y;
        switch (edge)
        {
            case DockEdge.Top:
                x = work.Left + (work.Width - widthPx) / 2;
                y = work.Top;
                break;
            case DockEdge.Left:
                x = work.Left;
                y = work.Top + (work.Height - heightPx) / 2;
                break;
            case DockEdge.Right:
                x = work.Right - widthPx;
                y = work.Top + (work.Height - heightPx) / 2;
                break;
            case DockEdge.Bottom:
            default:
                x = work.Left + (work.Width - widthPx) / 2;
                y = work.Bottom - heightPx;
                break;
        }

        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            x,
            y,
            0,
            0,
            SwpNosize | SwpNozorder | SwpNoactivate);

        try
        {
            var source = PresentationSource.FromVisual(window);
            if (source?.CompositionTarget is not null)
            {
                var dip = source.CompositionTarget.TransformFromDevice
                    .Transform(new System.Windows.Point(x, y));
                window.Left = dip.X;
                window.Top = dip.Y;
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>游標是否位於指定螢幕的指定邊緣感應區。</summary>
    public static bool IsCursorAtMonitorEdge(int screenIndex, DockEdge edge, double edgeTriggerDip)
    {
        if (!GetCursorPos(out POINT cursorPx))
            return false;

        MonitorInfo monitor = Resolve(screenIndex);
        RectPx b = monitor.BoundsPx;
        int triggerX = Math.Max(1, (int)Math.Round(edgeTriggerDip * monitor.DpiX / 96.0));
        int triggerY = Math.Max(1, (int)Math.Round(edgeTriggerDip * monitor.DpiY / 96.0));

        return edge switch
        {
            DockEdge.Top =>
                cursorPx.X >= b.Left && cursorPx.X <= b.Right &&
                cursorPx.Y <= b.Top + triggerY && cursorPx.Y >= b.Top,

            DockEdge.Left =>
                cursorPx.Y >= b.Top && cursorPx.Y <= b.Bottom &&
                cursorPx.X <= b.Left + triggerX && cursorPx.X >= b.Left,

            DockEdge.Right =>
                cursorPx.Y >= b.Top && cursorPx.Y <= b.Bottom &&
                cursorPx.X >= b.Right - triggerX && cursorPx.X <= b.Right,

            _ => // Bottom
                cursorPx.X >= b.Left && cursorPx.X <= b.Right &&
                cursorPx.Y >= b.Bottom - triggerY && cursorPx.Y <= b.Bottom
        };
    }

    public static string Describe(MonitorInfo m)
    {
        string tag = m.IsPrimary ? "主螢幕" : "螢幕";
        double wDip = m.BoundsPx.Width * 96.0 / m.DpiX;
        double hDip = m.BoundsPx.Height * 96.0 / m.DpiY;
        return $"{tag} {m.Index}  ({(int)wDip}×{(int)hDip} @ {m.DpiX:0} DPI)";
    }

    /// <summary>由 HMONITOR 對應到目前列舉順序的螢幕索引。</summary>
    public static int IndexFromMonitorHandle(IntPtr hMonitor)
    {
        if (hMonitor == IntPtr.Zero)
            return 0;

        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(hMonitor, ref mi))
            return 0;

        var monitors = GetMonitors();
        for (int i = 0; i < monitors.Count; i++)
        {
            RectPx b = monitors[i].BoundsPx;
            if (b.Left == mi.rcMonitor.Left &&
                b.Top == mi.rcMonitor.Top &&
                b.Right == mi.rcMonitor.Right &&
                b.Bottom == mi.rcMonitor.Bottom)
            {
                return monitors[i].Index;
            }
        }

        return monitors.FirstOrDefault(m => m.IsPrimary).Index;
    }

    private const uint MonitorInfoPrimary = 1;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private enum MonitorDpiType
    {
        EffectiveDpi = 0
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
}
