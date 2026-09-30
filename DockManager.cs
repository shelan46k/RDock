using System.Windows;
using System.Windows.Threading;

namespace RDock;

/// <summary>
/// 依設定建立 / 重建各螢幕上的 Dock 視窗；共用背景服務與單一設定寫入點。
/// </summary>
public static class DockManager
{
    private static readonly List<MainWindow> Windows = [];
    private static readonly object SaveGate = new();
    private static CancellationTokenSource? _saveCts;
    private static bool _restarting;
    private static bool _shuttingDown;
    private static string _monitorFingerprint = string.Empty;

    public static ProcessWatcher SharedProcessWatcher { get; } = new();
    public static FullscreenWatcher SharedFullscreenWatcher { get; } = new();

    public static IReadOnlyList<MainWindow> OpenWindows => Windows;

    public static bool IsRestarting => _restarting;

    public static bool IsShuttingDown => _shuttingDown;

    public static async Task StartAsync()
    {
        SharedProcessWatcher.Start();
        SharedFullscreenWatcher.Start();
        DockConfig config = await DockConfigStore.LoadAsync();
        Spawn(config);
        RefreshMonitorFingerprint();
    }

    public static async Task ShutdownAsync()
    {
        if (_shuttingDown)
            return;

        _shuttingDown = true;
        var app = Application.Current;
        if (app is null)
            return;

        try
        {
            await FlushSaveAsync().ConfigureAwait(true);

            foreach (MainWindow window in Windows.ToArray())
                window.AllowCloseWithoutSave = true;

            SharedProcessWatcher.Dispose();
            SharedFullscreenWatcher.Dispose();

            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Shutdown();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DockManager] Shutdown failed: {ex.Message}");
            try { Application.Current?.Shutdown(); } catch { /* ignore */ }
        }
    }

    public static async Task RestartAsync(int screenIndex, DockEdge edge)
    {
        if (_restarting)
            return;

        _restarting = true;
        var app = Application.Current;
        if (app is null)
        {
            _restarting = false;
            return;
        }

        var previousShutdownMode = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            DockConfig config = CaptureConfig(screenIndex, edge);
            await Task.Run(async () => await DockConfigStore.SaveAsync(config).ConfigureAwait(false))
                .ConfigureAwait(true);

            await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            var closing = Windows.ToArray();
            Windows.Clear();

            foreach (MainWindow window in closing)
            {
                try
                {
                    window.AllowCloseWithoutSave = true;
                    window.Close();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DockManager] Close failed: {ex.Message}");
                }
            }

            await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            SharedProcessWatcher.ForceResume();
            SharedFullscreenWatcher.ForceActive();
            Spawn(config);
            RefreshMonitorFingerprint();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DockManager] Restart failed: {ex.Message}");
            if (Windows.Count == 0)
            {
                try
                {
                    var fallback = await DockConfigStore.LoadAsync();
                    fallback.ScreenIndex = screenIndex;
                    fallback.DockEdge = edge;
                    Spawn(fallback);
                }
                catch
                {
                    app.Shutdown();
                }
            }
        }
        finally
        {
            if (app.ShutdownMode == ShutdownMode.OnExplicitShutdown)
                app.ShutdownMode = previousShutdownMode == ShutdownMode.OnExplicitShutdown
                    ? ShutdownMode.OnLastWindowClose
                    : previousShutdownMode;

            _restarting = false;
        }
    }

    /// <summary>各窗變更後統一排程存檔（避免多窗互蓋）。</summary>
    public static void ScheduleSave(MainWindow? preferred = null)
    {
        lock (SaveGate)
        {
            _saveCts?.Cancel();
            _saveCts = new CancellationTokenSource();
            var token = _saveCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(400, token).ConfigureAwait(false);
                    DockConfig config = preferred is not null && Windows.Contains(preferred)
                        ? preferred.ExportConfig()
                        : CaptureConfig(preferred?.PreferredScreenIndex ?? 0, preferred?.CurrentDockEdge ?? DockEdge.Bottom);

                    // 若 Capture 用了錯的 screen，改用目前任一窗匯出再覆寫偏好
                    if (Windows.Count > 0)
                    {
                        MainWindow source = preferred is not null && Windows.Contains(preferred)
                            ? preferred
                            : Windows[0];
                        config = source.ExportConfig();
                    }

                    await DockConfigStore.SaveAsync(config, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DockManager] Save failed: {ex.Message}");
                }
            }, token);
        }
    }

    public static async Task FlushSaveAsync()
    {
        try
        {
            if (Windows.Count == 0)
                return;

            DockConfig config = Windows[0].ExportConfig();
            await DockConfigStore.SaveAsync(config).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DockManager] FlushSave failed: {ex.Message}");
        }
    }

    public static void Register(MainWindow window)
    {
        if (!Windows.Contains(window))
            Windows.Add(window);
    }

    public static void Unregister(MainWindow window) => Windows.Remove(window);

    public static void NotifyDockActivated(MainWindow source)
    {
        foreach (MainWindow window in Windows.ToArray())
        {
            if (ReferenceEquals(window, source))
                continue;

            try { window.RequestHideFromPeer(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DockManager] Hide peer failed: {ex.Message}");
            }
        }
    }

    /// <summary>顯示器變更時：若螢幕集合改變且為 AllScreens，重建；否則只重定位。</summary>
    public static void OnDisplayChanged()
    {
        if (_restarting || _shuttingDown || Windows.Count == 0)
            return;

        string fingerprint = BuildMonitorFingerprint();
        bool allScreens = Windows[0].PreferredScreenIndex == DockConfig.AllScreens;
        bool changed = !string.Equals(fingerprint, _monitorFingerprint, StringComparison.Ordinal);

        if (allScreens && changed)
        {
            DockConfig config = Windows[0].ExportConfig();
            _ = RestartAsync(config.ScreenIndex, config.DockEdge);
            return;
        }

        _monitorFingerprint = fingerprint;
        foreach (MainWindow window in Windows.ToArray())
        {
            try { window.HandleDisplayChanged(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DockManager] Recenter failed: {ex.Message}");
            }
        }
    }

    private static void RefreshMonitorFingerprint() =>
        _monitorFingerprint = BuildMonitorFingerprint();

    private static string BuildMonitorFingerprint()
    {
        var monitors = MonitorService.GetMonitors();
        return string.Join("|", monitors.Select(m =>
            $"{m.Index}:{m.BoundsPx.Left},{m.BoundsPx.Top},{m.BoundsPx.Right},{m.BoundsPx.Bottom}@{m.DpiX}"));
    }

    private static DockConfig CaptureConfig(int screenIndex, DockEdge edge)
    {
        if (Windows.Count > 0)
        {
            DockConfig live = Windows[0].ExportConfig();
            live.ScreenIndex = screenIndex;
            live.DockEdge = edge;
            return live;
        }

        return new DockConfig
        {
            ScreenIndex = screenIndex,
            DockEdge = edge
        };
    }

    private static void Spawn(DockConfig config)
    {
        config.Clamp();
        var monitors = MonitorService.GetMonitors();
        DockEdge edge = config.DockEdge;

        if (config.ScreenIndex == DockConfig.AllScreens)
        {
            foreach (var monitor in monitors)
            {
                var window = new MainWindow(monitor.Index, edge, preferredScreenIndex: DockConfig.AllScreens, config);
                Register(window);
                window.Show();
            }
        }
        else
        {
            int index = config.ScreenIndex;
            if (index < 0 || index >= monitors.Count)
            {
                index = 0;
                foreach (var m in monitors)
                {
                    if (m.IsPrimary)
                    {
                        index = m.Index;
                        break;
                    }
                }
            }

            var window = new MainWindow(index, edge, preferredScreenIndex: index, config);
            Register(window);
            window.Show();
        }
    }
}
