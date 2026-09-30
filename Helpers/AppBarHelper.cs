using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RDock;

/// <summary>
/// 選配：透過 Win32 SHAppBarMessage 將視窗註冊為系統 AppBar，
/// 讓 Windows 為其保留工作區空間（類似工作列），避免被最大化視窗完全覆蓋。
/// <para>
/// 預設以永久保留（非 ABS_AUTOHIDE）註冊。若使用 ABS_AUTOHIDE，
/// 與系統工作列同邊時容易互相干擾（例如工作列被擠壓／自動縮小）。
/// 與 Dock 視覺自動隱藏並用時，該邊緣會留下空白保留區。
/// </para>
/// </summary>
public sealed class AppBarHelper : IDisposable
{
    private readonly Window _window;
    private readonly int _callbackId;
    private bool _registered;
    private bool _disposed;
    private HwndSource? _hwndSource;

    public AppBarHelper(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _callbackId = RegisterWindowMessage("RDock_AppBarCallback");
    }

    public bool IsRegistered => _registered;

    /// <summary>
    /// 註冊為底部 AppBar。
    /// </summary>
    /// <param name="autoHide">
    /// true = ABS_AUTOHIDE（系統感知自動隱藏，不永久佔用工作區）；
    /// false = 永久保留高度（像傳統工作列）。
    /// </param>
    public void Register(bool autoHide = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registered)
            return;

        var hwnd = new WindowInteropHelper(_window).EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);

        var abd = CreateAppBarData(hwnd);
        // ABM_NEW：向 Shell 註冊
        SHAppBarMessage(ABM.NEW, ref abd);

        abd.uEdge = ABE.BOTTOM;
        abd.rc = GetBottomBarRect(hwnd, (int)Math.Ceiling(_window.ActualHeight));

        // ABM_QUERYPOS → ABM_SETPOS：確認並套用位置 / 保留區
        SHAppBarMessage(ABM.QUERYPOS, ref abd);
        SHAppBarMessage(ABM.SETPOS, ref abd);

        var state = AppBarStates.AlwaysOnTop;
        if (autoHide)
            state |= AppBarStates.AutoHide;

        abd.lParam = (IntPtr)state;
        SHAppBarMessage(ABM.SETSTATE, ref abd);

        ApplyRect(abd.rc);
        _registered = true;
    }

    /// <summary>依目前視窗高度重新宣告保留區（拖放增減圖示後可呼叫）。</summary>
    public void UpdatePosition()
    {
        if (!_registered)
            return;

        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var abd = CreateAppBarData(hwnd);
        abd.uEdge = ABE.BOTTOM;
        abd.rc = GetBottomBarRect(hwnd, (int)Math.Ceiling(_window.ActualHeight));
        SHAppBarMessage(ABM.QUERYPOS, ref abd);
        SHAppBarMessage(ABM.SETPOS, ref abd);
        ApplyRect(abd.rc);
    }

    /// <summary>解除 AppBar 註冊，還原系統工作區。</summary>
    public void Unregister()
    {
        if (!_registered)
            return;

        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var abd = CreateAppBarData(hwnd);
            SHAppBarMessage(ABM.REMOVE, ref abd);
        }

        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        _registered = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Unregister();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private APPBARDATA CreateAppBarData(IntPtr hwnd) => new()
    {
        cbSize = Marshal.SizeOf<APPBARDATA>(),
        hWnd = hwnd,
        uCallbackMessage = (uint)_callbackId
    };

    /// <summary>以螢幕底部為基準，建立建議的 AppBar 矩形（實體像素）。</summary>
    private static RECT GetBottomBarRect(IntPtr hwnd, int heightPx)
    {
        // 取得該視窗所在螢幕的工作區（實體像素）
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);

        RECT screen = info.rcMonitor;
        int h = Math.Max(heightPx, 1);

        return new RECT
        {
            Left = screen.Left,
            Right = screen.Right,
            Top = screen.Bottom - h,
            Bottom = screen.Bottom
        };
    }

    private void ApplyRect(RECT rc)
    {
        // AppBar 座標是實體像素；WPF 使用 DIP，需依 DPI 換算
        var dpi = VisualTreeHelperDpi(_window);
        _window.Left = rc.Left / dpi.DpiScaleX;
        _window.Top = rc.Top / dpi.DpiScaleY;
        // 寬度由 SizeToContent 管理時，僅對齊位置即可；若要全寬可取消註解：
        // _window.Width = (rc.Right - rc.Left) / dpi.DpiScaleX;
    }

    private static DpiScale VisualTreeHelperDpi(Window window)
    {
        try
        {
            return System.Windows.Media.VisualTreeHelper.GetDpi(window);
        }
        catch
        {
            return new DpiScale(1, 1);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Shell 透過自訂 callback 通知工作區 / 全螢幕狀態變更
        if (msg == _callbackId)
        {
            switch ((ABN)wParam.ToInt32())
            {
                case ABN.POSCHANGED:
                    UpdatePosition();
                    break;
                case ABN.FULLSCREENAPP:
                    // lParam != 0 → 有全螢幕應用；可在此暫時隱藏 Dock
                    break;
                case ABN.WINDOWARRANGE:
                    break;
            }
        }

        return IntPtr.Zero;
    }

    // ── Win32 ────────────────────────────────────────────────

    private enum ABM : uint
    {
        NEW = 0x00000000,
        REMOVE = 0x00000001,
        QUERYPOS = 0x00000002,
        SETPOS = 0x00000003,
        GETSTATE = 0x00000004,
        GETTASKBARPOS = 0x00000005,
        ACTIVATE = 0x00000006,
        GETAUTOHIDEBAR = 0x00000007,
        SETAUTOHIDEBAR = 0x00000008,
        WINDOWPOSCHANGED = 0x00000009,
        SETSTATE = 0x0000000A
    }

    private enum ABE : uint
    {
        LEFT = 0,
        TOP = 1,
        RIGHT = 2,
        BOTTOM = 3
    }

    private enum ABN : int
    {
        STATECHANGE = 0,
        POSCHANGED = 1,
        FULLSCREENAPP = 2,
        WINDOWARRANGE = 3
    }

    [Flags]
    public enum AppBarStates
    {
        None = 0,
        AutoHide = 0x01,
        AlwaysOnTop = 0x02
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public ABE uEdge;
        public RECT rc;
        public IntPtr lParam;
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

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr SHAppBarMessage(ABM dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
