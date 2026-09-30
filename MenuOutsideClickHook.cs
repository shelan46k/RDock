using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace RDock;

/// <summary>
/// 低階滑鼠鉤子：在 ContextMenu 開啟期間偵測「點在選單外」的按下事件。
/// 比 DispatcherTimer + GetAsyncKeyState 更可靠（尤其第一次開啟）。
/// </summary>
internal sealed class MenuOutsideClickHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;

    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _isMenuOpen;
    private readonly Func<bool> _isCursorOverMenu;
    private readonly Action _onOutsideClick;

    private IntPtr _hook;
    private LowLevelMouseProc? _proc;
    private DateTime _armIgnoreUntilUtc = DateTime.MinValue;
    private bool _disposed;

    public MenuOutsideClickHook(
        Dispatcher dispatcher,
        Func<bool> isMenuOpen,
        Func<bool> isCursorOverMenu,
        Action onOutsideClick)
    {
        _dispatcher = dispatcher;
        _isMenuOpen = isMenuOpen;
        _isCursorOverMenu = isCursorOverMenu;
        _onOutsideClick = onOutsideClick;
    }

    public void Start(TimeSpan ignoreOpeningClickFor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _armIgnoreUntilUtc = DateTime.UtcNow + ignoreOpeningClickFor;

        if (_hook != IntPtr.Zero)
            return;

        _proc = HookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule
            ?? throw new InvalidOperationException("無法取得 MainModule。");

        _hook = SetWindowsHookEx(
            WhMouseLl,
            _proc,
            GetModuleHandle(curModule.ModuleName),
            0);

        if (_hook == IntPtr.Zero)
            Debug.WriteLine($"[MenuOutsideClickHook] SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
    }

    public void Stop()
    {
        _armIgnoreUntilUtc = DateTime.MinValue;

        if (_hook == IntPtr.Zero)
            return;

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is WmLButtonDown or WmRButtonDown or WmMButtonDown)
                HandleButtonDown();
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void HandleButtonDown()
    {
        try
        {
            // 略過開啟選單當下的那次按鍵
            if (DateTime.UtcNow < _armIgnoreUntilUtc)
                return;

            if (!_isMenuOpen())
                return;

            if (_isCursorOverMenu())
                return;

            _dispatcher.BeginInvoke(() =>
            {
                try { _onOutsideClick(); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MenuOutsideClickHook] onOutsideClick failed: {ex.Message}");
                }
            }, DispatcherPriority.Send);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MenuOutsideClickHook] HandleButtonDown failed: {ex.Message}");
        }
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
