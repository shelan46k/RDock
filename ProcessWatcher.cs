using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace RDock;

/// <summary>
/// 背景輪詢執行中進程（優先完整路徑，回退 ProcessName）。
/// 由 DockManager 共用一份，避免 AllScreens 倍增 CPU。
/// </summary>
public sealed class ProcessWatcher : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly TimeSpan _idleInterval;
    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private HashSet<string> _runningNames = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _runningPaths = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private int _suspendCount;
    private TimeSpan _currentDelay;

    public ProcessWatcher(TimeSpan? interval = null, Dispatcher? dispatcher = null)
    {
        _interval = interval ?? TimeSpan.FromSeconds(2);
        _idleInterval = TimeSpan.FromSeconds(20);
        _currentDelay = _interval;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public event EventHandler? Updated;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_cts is not null)
            return;

        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    /// <summary>引用計數式暫停：所有 Dock 都隱藏時才真正降頻。</summary>
    public void SetSuspended(bool suspended)
    {
        lock (_gate)
        {
            if (suspended)
                _suspendCount++;
            else
                _suspendCount = Math.Max(0, _suspendCount - 1);

            bool allSuspended = _suspendCount > 0;
            _currentDelay = allSuspended ? _idleInterval : _interval;
        }
    }

    public void ForceResume()
    {
        lock (_gate)
        {
            _suspendCount = 0;
            _currentDelay = _interval;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    public bool IsRunning(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return false;

        string? full = TryNormalizePath(targetPath);
        string? name = GetProcessNameKey(targetPath);

        lock (_gate)
        {
            if (full is not null && _runningPaths.Contains(full))
                return true;
            if (name is not null && _runningNames.Contains(name))
                return true;
        }

        return false;
    }

    public static bool CanTrackAsProcess(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string ext = Path.GetExtension(path);
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".com", StringComparison.OrdinalIgnoreCase);
    }

    public static string? GetProcessNameKey(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || !CanTrackAsProcess(targetPath))
            return null;

        try
        {
            string name = Path.GetFileNameWithoutExtension(targetPath);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    public static string? TryNormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')))
                .ToLowerInvariant();
        }
        catch
        {
            return path.Trim().ToLowerInvariant();
        }
    }

    /// <summary>
    /// 列出適合釘選的執行中程式：需有可見標題視窗，並帶出檔案描述（常為中文顯示名稱）。
    /// </summary>
    public IReadOnlyList<RunningAppInfo> GetRunningAppsSnapshot()
    {
        // 在背景收集可能較慢，但僅開設定時呼叫
        try
        {
            return BuildPinCandidateList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessWatcher] GetRunningAppsSnapshot failed: {ex.Message}");
            return [];
        }
    }

    private static List<RunningAppInfo> BuildPinCandidateList()
    {
        var byPath = new Dictionary<string, RunningAppInfo>(StringComparer.OrdinalIgnoreCase);

        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero)
                    return true;

                int len = GetWindowTextLength(hwnd);
                if (len <= 0)
                    return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0)
                    return true;

                string? path = TryGetProcessPathById((int)pid);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return true;
                if (IsExcludedPinPath(path))
                    return true;

                string key = path;
                if (byPath.ContainsKey(key))
                    return true;

                string display = ResolveFriendlyAppName(path);
                byPath[key] = new RunningAppInfo(display, path);
            }
            catch
            {
                // ignore per-window failures
            }

            return true;
        }, IntPtr.Zero);

        return byPath.Values
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(50)
            .ToList();
    }

    private static string ResolveFriendlyAppName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
                return info.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(info.ProductName))
                return info.ProductName.Trim();
        }
        catch
        {
            // ignore
        }

        try
        {
            string name = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch
        {
            return path;
        }
    }

    private static bool IsExcludedPinPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string file = Path.GetFileNameWithoutExtension(full);
            string dir = Path.GetDirectoryName(full) ?? string.Empty;

            // Windows 系統目錄內的背景元件通常不該釘到 Dock
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(windows) &&
                full.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            {
                // 允許少數使用者常釘的系統工具
                if (file is not ("explorer" or "notepad" or "calc" or "mspaint" or "cmd" or "powershell" or "WindowsTerminal"))
                    return true;
            }

            return ExcludedProcessNames.Contains(file);
        }
        catch
        {
            return true;
        }
    }

    private static readonly HashSet<string> ExcludedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost", "runtimebroker", "searchhost", "shellexperiencehost",
        "startmenuexperiencehost", "textinputhost", "systemsettings", "conhost",
        "dllhost", "svchost", "taskhostw", "sihost", "fontdrvhost", "audiodg",
        "dwm", "csrss", "smss", "winlogon", "services", "lsass", "idle",
        "securityhealthservice", "securityhealthsystray", "widgetservice",
        "phonexperiencehost", "lockapp", "logonui", "userinit", "rdock",
        "chksrv", "comppkgsrv", "appvshnotify", "backgroundtaskhost",
        "pickerhost", "openwith", "consent", "werfault"
    };

    private static string? TryGetProcessPathById(int pid)
    {
        const uint processQueryLimitedInformation = 0x1000;
        IntPtr handle = OpenProcess(processQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (!QueryFullProcessImageName(handle, 0, sb, ref size) || size <= 0)
                return null;

            return TryNormalizePath(sb.ToString());
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            bool suspended;
            lock (_gate)
            {
                delay = _currentDelay;
                suspended = _suspendCount > 0;
            }

            if (!suspended)
            {
                (HashSet<string> names, HashSet<string> paths) snapshot;
                try
                {
                    snapshot = await Task.Run(CollectProcesses, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ProcessWatcher] Collect failed: {ex.Message}");
                    snapshot = (new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }

                bool changed;
                lock (_gate)
                {
                    changed = !_runningNames.SetEquals(snapshot.names) ||
                              !_runningPaths.SetEquals(snapshot.paths);
                    if (changed)
                    {
                        _runningNames = snapshot.names;
                        _runningPaths = snapshot.paths;
                    }
                }

                if (changed)
                {
                    try
                    {
                        await _dispatcher.InvokeAsync(() => Updated?.Invoke(this, EventArgs.Empty));
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }
                }
            }

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static (HashSet<string> names, HashSet<string> paths) CollectProcesses()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return (names, paths);
        }

        foreach (Process process in processes)
        {
            try
            {
                string name = process.ProcessName;
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);

                string? path = TryGetProcessPath(process);
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
            }
            catch
            {
                // ignore
            }
            finally
            {
                process.Dispose();
            }
        }

        return (names, paths);
    }

    /// <summary>不碰 MainModule：用 QueryFullProcessImageName。</summary>
    private static string? TryGetProcessPath(Process process)
    {
        const uint processQueryLimitedInformation = 0x1000;
        IntPtr handle = OpenProcess(processQueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (!QueryFullProcessImageName(handle, 0, sb, ref size) || size <= 0)
                return null;

            return TryNormalizePath(sb.ToString());
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess,
        int dwFlags,
        StringBuilder lpExeName,
        ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint GwOwner = 4;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}

public readonly record struct RunningAppInfo(string DisplayName, string ExecutablePath);
