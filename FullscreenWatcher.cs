using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace RDock;

/// <summary>
/// 後台輪詢前景全螢幕視窗，並記錄所在螢幕索引（供各 Dock 各自判斷）。
/// 由 DockManager 共用一份。
/// </summary>
public sealed class FullscreenWatcher : IDisposable
{
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(4);

    private readonly DispatcherTimer _timer;
    private bool _disposed;
    private int _idleVotes;

    public FullscreenWatcher(TimeSpan? interval = null)
    {
        _timer = new DispatcherTimer
        {
            Interval = interval ?? ActiveInterval
        };
        _timer.Tick += (_, _) => Refresh();
    }

    /// <summary>目前是否偵測到全螢幕前景視窗（任一螢幕）。</summary>
    public bool IsFullscreenActive { get; private set; }

    /// <summary>全螢幕所在螢幕索引；無則 -1。</summary>
    public int FullscreenScreenIndex { get; private set; } = -1;

    public event EventHandler? Changed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Refresh();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    /// <summary>引用計數：所有 Dock 隱藏才降頻。</summary>
    public void SetIdle(bool idle)
    {
        if (idle)
            _idleVotes++;
        else
            _idleVotes = Math.Max(0, _idleVotes - 1);

        _timer.Interval = _idleVotes > 0 ? IdleInterval : ActiveInterval;
    }

    public void ForceActive()
    {
        _idleVotes = 0;
        _timer.Interval = ActiveInterval;
    }

    /// <summary>指定螢幕是否被全螢幕占用。</summary>
    public bool IsFullscreenOnScreen(int screenIndex) =>
        IsFullscreenActive && FullscreenScreenIndex == screenIndex;

    public void Dispose()
    {
        if (_disposed)
            return;

        _timer.Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void Refresh()
    {
        bool now = TryDetect(out int screenIndex);
        if (now == IsFullscreenActive && screenIndex == FullscreenScreenIndex)
            return;

        IsFullscreenActive = now;
        FullscreenScreenIndex = now ? screenIndex : -1;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool TryDetect(out int screenIndex)
    {
        screenIndex = -1;
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || IsIconic(hwnd))
            return false;

        string className = GetClass(hwnd);
        if (IsExcludedShellClass(className))
            return false;

        if (!GetWindowRect(hwnd, out RECT windowRect))
            return false;

        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return false;

        RECT screen = info.rcMonitor;
        const int tolerance = 2;
        bool covers =
            windowRect.Left <= screen.Left + tolerance &&
            windowRect.Top <= screen.Top + tolerance &&
            windowRect.Right >= screen.Right - tolerance &&
            windowRect.Bottom >= screen.Bottom - tolerance;

        if (!covers)
        {
            long windowArea = (long)Math.Max(0, windowRect.Right - windowRect.Left) *
                              Math.Max(0, windowRect.Bottom - windowRect.Top);
            long screenArea = (long)Math.Max(0, screen.Right - screen.Left) *
                              Math.Max(0, screen.Bottom - screen.Top);
            if (screenArea <= 0 || (double)windowArea / screenArea < 0.98)
                return false;

            int style = GetWindowLong(hwnd, GwlStyle);
            const int wsCaption = 0x00C00000;
            if ((style & wsCaption) != 0)
                return false;
        }

        screenIndex = MonitorService.IndexFromMonitorHandle(monitor);
        return true;
    }

    private static bool IsExcludedShellClass(string className) =>
        className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "DV2ControlHost" or "ForegroundStaging";

    private static string GetClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        _ = GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private const int GwlStyle = -16;
    private const uint MonitorDefaultToNearest = 2;

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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
}
