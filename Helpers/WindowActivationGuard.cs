using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RDock;

/// <summary>
/// 視窗擴展樣式：WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW。
/// 互動狀態以「每個視窗」追蹤，避免多螢幕互相干擾。
/// </summary>
public static class WindowActivationGuard
{
    public const int GwlExStyle = -20;
    public const int WsExNoActivate = 0x08000000;
    public const int WsExToolWindow = 0x00000080;

    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;

    private static readonly ConditionalWeakTable<Window, StrongBox<bool>> InteractiveFlags = new();

    public static bool IsInteractive(Window window) =>
        InteractiveFlags.TryGetValue(window, out StrongBox<bool>? box) && box.Value;

    public static HwndSourceHook Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.ShowActivated = false;
        InteractiveFlags.GetOrCreateValue(window).Value = false;

        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        ApplyExStyles(hwnd, window);

        var source = HwndSource.FromHwnd(hwnd)
                     ?? throw new InvalidOperationException("無法取得 HwndSource。");

        HwndSourceHook hook = (IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            WndProc(window, h, msg, wParam, lParam, ref handled);
        source.AddHook(hook);
        return hook;
    }

    public static void EnsureInteractive(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        InteractiveFlags.GetOrCreateValue(window).Value = true;
        ClearNoActivate(hwnd);
    }

    public static void EndInteractive(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!InteractiveFlags.TryGetValue(window, out StrongBox<bool>? box) || !box.Value)
            return;

        box.Value = false;
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        ApplyExStyles(hwnd, window);
    }

    public static void BeginInteractive(Window window) => EnsureInteractive(window);

    public static void ApplyExStyles(IntPtr hwnd) => ApplyExStyles(hwnd, window: null);

    public static void ApplyExStyles(IntPtr hwnd, Window? window)
    {
        if (hwnd == IntPtr.Zero)
            return;

        if (window is not null && IsInteractive(window))
            return;

        int ex = GetWindowLong(hwnd, GwlExStyle);
        int updated = ex | WsExNoActivate | WsExToolWindow;
        if (updated != ex)
            SetWindowLong(hwnd, GwlExStyle, updated);
    }

    public static void ClearNoActivate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        int ex = GetWindowLong(hwnd, GwlExStyle);
        int updated = (ex | WsExToolWindow) & ~WsExNoActivate;
        if (updated != ex)
            SetWindowLong(hwnd, GwlExStyle, updated);
    }

    private static IntPtr WndProc(
        Window window,
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == WmMouseActivate)
        {
            if (IsInteractive(window))
                return IntPtr.Zero;

            handled = true;
            return new IntPtr(MaNoActivate);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
