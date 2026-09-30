using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RDock;

/// <summary>
/// 將已在執行的應用程式主視窗拉到前景（避免重複 Process.Start）。
/// 刻意不讀取 MainModule，以免點擊時卡頓 1~2 秒。
/// </summary>
public static class WindowFocusHelper
{
    private const int SwRestore = 9;
    private const int SwShow = 5;
    private const uint GwOwner = 4;

    /// <summary>
    /// 在背景執行緒尋找視窗；回傳 hwnd（找不到則 Zero）。
    /// 請於 UI 執行緒再呼叫 <see cref="ActivateWindow"/>。
    /// </summary>
    public static IntPtr FindMainWindow(string targetPath)
    {
        if (!ProcessWatcher.CanTrackAsProcess(targetPath))
            return IntPtr.Zero;

        string? processName = ProcessWatcher.GetProcessNameKey(targetPath);
        if (processName is null)
            return IntPtr.Zero;

        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(processName);
        }
        catch
        {
            return IntPtr.Zero;
        }

        if (candidates.Length == 0)
            return IntPtr.Zero;

        var pidSet = new HashSet<uint>(candidates.Length);
        foreach (Process p in candidates)
        {
            try
            {
                pidSet.Add((uint)p.Id);
            }
            finally
            {
                p.Dispose();
            }
        }

        return pidSet.Count == 0 ? IntPtr.Zero : FindBestWindow(pidSet);
    }

    /// <summary>同步尋找並激活（若已在 UI 執行緒且確定很快可用）。</summary>
    public static bool TryBringToFront(string targetPath)
    {
        IntPtr hwnd = FindMainWindow(targetPath);
        if (hwnd == IntPtr.Zero)
            return false;

        ActivateWindow(hwnd);
        return true;
    }

    /// <summary>列出目標程式目前可見／最小化的頂層視窗（供「視窗」子選單）。</summary>
    public static IReadOnlyList<(IntPtr Hwnd, string Title)> ListWindows(string targetPath)
    {
        if (!ProcessWatcher.CanTrackAsProcess(targetPath))
            return [];

        string? processName = ProcessWatcher.GetProcessNameKey(targetPath);
        if (processName is null)
            return [];

        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(processName);
        }
        catch
        {
            return [];
        }

        if (candidates.Length == 0)
            return [];

        var pidSet = new HashSet<uint>(candidates.Length);
        foreach (Process p in candidates)
        {
            try { pidSet.Add((uint)p.Id); }
            finally { p.Dispose(); }
        }

        if (pidSet.Count == 0)
            return [];

        var results = new List<(IntPtr, string)>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!pidSet.Contains(pid))
                return true;

            if (GetWindow(hwnd, GwOwner) != IntPtr.Zero)
                return true;

            bool visible = IsWindowVisible(hwnd);
            bool iconic = IsIconic(hwnd);
            if (!visible && !iconic)
                return true;

            int length = GetWindowTextLength(hwnd);
            if (length <= 0)
                return true;

            var sb = new StringBuilder(length + 1);
            _ = GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title))
                return true;

            results.Add((hwnd, title));
            return true;
        }, IntPtr.Zero);

        return results
            .OrderBy(r => r.Item2, StringComparer.CurrentCultureIgnoreCase)
            .Take(24)
            .ToList();
    }

    private static IntPtr FindBestWindow(HashSet<uint> pids)
    {
        IntPtr best = IntPtr.Zero;
        int bestScore = -1;

        EnumWindows((hwnd, lParam) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!pids.Contains(pid))
                return true;

            if (GetWindow(hwnd, GwOwner) != IntPtr.Zero)
                return true;

            bool visible = IsWindowVisible(hwnd);
            bool iconic = IsIconic(hwnd);
            if (!visible && !iconic)
                return true;

            int length = GetWindowTextLength(hwnd);
            if (length <= 0)
                return true;

            int score = (visible ? 1000 : 0) + (iconic ? 100 : 0) + Math.Min(length, 200);
            if (score > bestScore)
            {
                bestScore = score;
                best = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return best;
    }

    public static void ActivateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        if (IsIconic(hwnd))
            ShowWindow(hwnd, SwRestore);
        else
            ShowWindow(hwnd, SwShow);

        IntPtr foreground = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(foreground, out _);
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        uint currentThread = GetCurrentThreadId();

        bool attachedFg = false;
        bool attachedTarget = false;

        try
        {
            if (fgThread != 0 && fgThread != currentThread)
                attachedFg = AttachThreadInput(currentThread, fgThread, true);

            if (targetThread != 0 && targetThread != currentThread && targetThread != fgThread)
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);

            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetActiveWindow(hwnd);
        }
        finally
        {
            if (attachedTarget)
                AttachThreadInput(currentThread, targetThread, false);
            if (attachedFg)
                AttachThreadInput(currentThread, fgThread, false);
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
