using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RDock;

/// <summary>
/// RDock — 魚眼縮放、拖放、自動隱藏、JSON 持久化、不搶焦點 / 全螢幕避讓 / 多螢幕 DPI。
/// </summary>
public partial class MainWindow : Window
{
    private const string DockItemDragFormat = "RDock.DockItemId";

    // ── 魚眼參數（可由設定覆寫）──────────────────────────────
    private double _maxScale = 1.8;
    private double _iconSize = 48.0;
    private double IconStep => _iconSize + 12.0;
    private double InfluenceRadius => IconStep * 2.5;
    private static readonly TimeSpan ResetDuration = TimeSpan.FromMilliseconds(220);

    // ── 自動隱藏參數 ──────────────────────────────────────────
    private TimeSpan _hideDelay = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(280);
    private const double EdgeTriggerDip = 4.0;

    private static readonly TimeSpan EdgeProbeActive = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan EdgeProbeIdle = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan RecycleBinActive = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RecycleBinIdle = TimeSpan.FromSeconds(30);

    private bool _autoHideEnabled = true;
    private double _dockOpacity = 0.80;
    private bool _showRecycleBin = true;
    private bool _showThisPC;
    private bool _showClock;
    private bool _enableAppBar;
    private bool _fisheyePush = true;

    private bool EnableAppBarReservation => _enableAppBar;

    private readonly List<Border> _icons = [];
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _edgeProbeTimer;
    private readonly List<DockItem> _items = [];
    private readonly DispatcherTimer _recycleBinTimer;
    private readonly DispatcherTimer _clockTimer;

    private AppBarHelper? _appBar;
    private HwndSource? _hwndSource;
    private CancellationTokenSource? _iconLoadCts;
    private FolderStackWindow? _folderStack;
    private bool _recycleBinWasFull;
    private DockConfig? _bootConfig;

    private bool _isHidden;
    private bool _isAnimating;
    private bool _isLocked;
    private bool _suppressClick;
    private bool _fisheyeTracking;
    private bool _powerSaving;
    private Border? _labeledIcon;
    private SettingsWindow? _settingsWindow;
    private int _openContextMenus;
    private readonly DispatcherTimer _outsideClickTimer;
    private readonly MenuOutsideClickHook _outsideClickHook;
    /// <summary>右鍵選單工作階段（含 Opening→Opened 之間）。</summary>
    private bool _menuSessionActive;
    /// <summary>本工作階段是否曾出現 IsOpen=true（避免開啟前誤 EndInteractive）。</summary>
    private bool _menuWasOpen;

    /// <summary>此視窗實際綁定的螢幕索引。</summary>
    private readonly int _boundScreenIndex;

    /// <summary>使用者偏好（可為 AllScreens = -1）。</summary>
    private int _preferredScreenIndex;

    private DockEdge _dockEdge;
    private Point _dragStart;
    private Border? _dragSource;
    private double[] _iconCenters = [];

    /// <summary>重建視窗時略過關閉存檔，避免互相覆寫。</summary>
    public bool AllowCloseWithoutSave { get; set; }

    public int PreferredScreenIndex => _preferredScreenIndex;

    public DockEdge CurrentDockEdge => _dockEdge;

    public MainWindow()
        : this(0, DockEdge.Bottom, preferredScreenIndex: 0)
    {
    }

    public MainWindow(int boundScreenIndex, DockEdge edge, int preferredScreenIndex)
        : this(boundScreenIndex, edge, preferredScreenIndex, new DockConfig
        {
            ScreenIndex = preferredScreenIndex,
            DockEdge = edge
        })
    {
    }

    public MainWindow(int boundScreenIndex, DockEdge edge, int preferredScreenIndex, DockConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Clamp();

        _boundScreenIndex = boundScreenIndex;
        _dockEdge = edge;
        _preferredScreenIndex = preferredScreenIndex;
        ApplyConfigFields(config);
        _bootConfig = config;

        InitializeComponent();
        ShowActivated = false;
        TryApplyWindowIcon();
        ApplyEdgeLayout();
        ApplyTheme();
        ApplyRenderTuning();

        _hideTimer = new DispatcherTimer { Interval = _hideDelay };
        _hideTimer.Tick += HideTimer_Tick;

        _edgeProbeTimer = new DispatcherTimer { Interval = EdgeProbeActive };
        _edgeProbeTimer.Tick += EdgeProbeTimer_Tick;

        _recycleBinTimer = new DispatcherTimer { Interval = RecycleBinActive };
        _recycleBinTimer.Tick += (_, _) => _ = RefreshRecycleBinAsync();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clockTimer.Tick += (_, _) => RefreshClockTexts();

        // 選單開啟時輪詢外部點擊（NOACTIVATE 時系統不會自動關 Popup）
        _outsideClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _outsideClickTimer.Tick += OutsideClickTimer_Tick;
        _outsideClickHook = new MenuOutsideClickHook(
            Dispatcher,
            IsAnyContextMenuReallyOpen,
            IsCursorOverOpenContextMenu,
            () => CloseAllContextMenus(hideDockIfAway: true));

        // 必須在選單真正開啟前記得解除 NOACTIVATE（Opened 太晚）
        AddHandler(ContextMenuOpeningEvent, new ContextMenuEventHandler(OnAnyContextMenuOpening), true);
        PreviewMouseRightButtonDown += Window_PreviewMouseRightButtonDown;

        DockManager.Register(this);
    }

    public DockConfig ExportConfig() => BuildConfig();

    private void ApplyConfigFields(DockConfig config)
    {
        _autoHideEnabled = config.AutoHideEnabled;
        _iconSize = config.IconSize;
        _maxScale = config.MaxScale;
        _hideDelay = TimeSpan.FromMilliseconds(config.HideDelayMs);
        _dockOpacity = config.DockOpacity;
        _showRecycleBin = config.ShowRecycleBin;
        _showThisPC = config.ShowThisPC;
        _showClock = config.ShowClock;
        _enableAppBar = config.EnableAppBar;
        _fisheyePush = config.FisheyePushEnabled;
        _isLocked = config.IsLocked;
    }

    private int IconDecodeSize => Math.Max(256, (int)(_iconSize * _maxScale * 2));

    private void TryApplyWindowIcon()
    {
        try
        {
            Icon = BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/RDock-Icon.png", UriKind.Absolute),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RDock] Apply window icon failed: {ex.Message}");
        }
    }

    private void ApplyRenderTuning()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);

        // 魚眼最大約 _maxScale：快取以更高解析度點陣，避免放大後糊成馬賽克
        DockSlideHost.CacheMode = CreateDockBitmapCache(forIdle: false);
        RenderOptions.SetBitmapScalingMode(DockSlideHost, BitmapScalingMode.HighQuality);
    }

    private BitmapCache CreateDockBitmapCache(bool forIdle) =>
        new()
        {
            EnableClearType = false,
            // 隱藏時降解析度省電；顯示時預留魚眼放大餘裕
            RenderAtScale = forIdle ? 0.5 : Math.Max(1.5, _maxScale)
        };

    private void ApplyTheme()
    {
        byte alpha = (byte)Math.Clamp((int)Math.Round(_dockOpacity * 255.0), 0, 255);
        DockContainer.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x1A, 0x1A, 0x1E));
    }

    public void HandleDisplayChanged()
    {
        RecenterOnWorkArea();
        ApplyTheme();
    }

    // ── 生命週期 ─────────────────────────────────────────────

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        // 1) WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW + WM_MOUSEACTIVATE
        WindowActivationGuard.Apply(this);

        // 2) 攔截 WM_DPICHANGED / WM_DISPLAYCHANGE
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        WindowActivationGuard.ApplyExStyles(hwnd);

        RecenterOnWorkArea();

        if (EnableAppBarReservation)
        {
            _appBar = new AppBarHelper(this);
            // false = 真正保留工作區；true(AutoHide) 容易讓系統工作列也跟著縮
            _appBar.Register(autoHide: false);
            WindowActivationGuard.ApplyExStyles(hwnd);
        }

        DockManager.SharedFullscreenWatcher.Changed += OnFullscreenChanged;
        DockManager.SharedProcessWatcher.Updated += OnProcessesUpdated;

        await LoadConfigAndIconsAsync();
        EnsureThisPCItem();
        EnsureRecycleBinItem();
        EnsureClockItem();
        RefreshRunningIndicators();
        RefreshClockTexts();
        _recycleBinTimer.Start();
        if (_showClock)
            _clockTimer.Start();
        _ = RefreshRecycleBinAsync();

        // 啟動後短暫顯示；若游標從未進入 Dock，仍要自動縮回
        ScheduleInitialAutoHide();
    }

    /// <summary>啟動完成後排程隱藏，不依賴先經過 MouseEnter/Leave。</summary>
    private void ScheduleInitialAutoHide()
    {
        if (!_autoHideEnabled)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (!_autoHideEnabled || _isHidden || IsAnyContextMenuOpen())
                return;
            if (DockContainer.IsMouseOver || HotEdge.IsMouseOver)
                return;
            StartHideTimer();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 重建過程中舊視窗關閉：立刻取消背景存檔，避免寫入半殘設定
        _iconLoadCts?.Cancel();

        DockManager.Unregister(this);

        _hideTimer.Stop();
        _edgeProbeTimer.Stop();
        _recycleBinTimer.Stop();
        _clockTimer.Stop();
        _outsideClickTimer.Stop();
        _outsideClickHook.Stop();
        DockManager.SharedFullscreenWatcher.Changed -= OnFullscreenChanged;
        DockManager.SharedProcessWatcher.Updated -= OnProcessesUpdated;
        _folderStack?.Close();
        _folderStack = null;

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        if (!AllowCloseWithoutSave && !DockManager.IsRestarting && !DockManager.IsShuttingDown)
        {
            // 避免 UI 執行緒同步等待造成死鎖；結束流程改走 DockManager.ShutdownAsync
            try
            {
                _ = DockConfigStore.SaveAsync(BuildConfig());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RDock] Final save failed: {ex.Message}");
            }
        }

        _appBar?.Dispose();
        _appBar = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is MonitorService.WmDpiChanged or MonitorService.WmDisplayChange)
        {
            Dispatcher.BeginInvoke(() =>
            {
                WindowActivationGuard.ApplyExStyles(hwnd);
                DockManager.OnDisplayChanged();
            }, DispatcherPriority.Loaded);
        }

        return IntPtr.Zero;
    }

    /// <summary>依綁定螢幕與停靠邊緣吸附。</summary>
    private void RecenterOnWorkArea()
    {
        UpdateLayout();

        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : Height;
        if (double.IsNaN(width) || width <= 0) width = _dockEdge.IsHorizontal() ? MinWidth : 152;
        if (double.IsNaN(height) || height <= 0) height = _dockEdge.IsHorizontal() ? 152 : MinWidth;

        MonitorService.PlaceDock(this, _boundScreenIndex, _dockEdge, width, height);

        if (_isHidden)
            ApplyHiddenOffset(immediate: true);

        _appBar?.UpdatePosition();
    }

    /// <summary>依邊緣調整面板方向、HotEdge、視窗尺寸策略。</summary>
    private void ApplyEdgeLayout()
    {
        bool horizontal = _dockEdge.IsHorizontal();

        SizeToContent = horizontal ? SizeToContent.Width : SizeToContent.Height;
        MinWidth = horizontal ? 200 : 80;
        MinHeight = horizontal ? 80 : 200;

        if (horizontal)
        {
            // 預留圖示名稱標籤空間
            Height = 180;
            Width = double.NaN;
        }
        else
        {
            Width = 180;
            Height = double.NaN;
        }

        IconPanel.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;

        // Dock 容器對齊
        switch (_dockEdge)
        {
            case DockEdge.Top:
                DockSlideHost.VerticalAlignment = VerticalAlignment.Top;
                DockSlideHost.HorizontalAlignment = HorizontalAlignment.Center;
                DockContainer.Margin = new Thickness(0, 12, 0, 0);
                HotEdge.Height = 3;
                HotEdge.Width = double.NaN;
                HotEdge.VerticalAlignment = VerticalAlignment.Top;
                HotEdge.HorizontalAlignment = HorizontalAlignment.Stretch;
                break;

            case DockEdge.Left:
                DockSlideHost.VerticalAlignment = VerticalAlignment.Center;
                DockSlideHost.HorizontalAlignment = HorizontalAlignment.Left;
                DockContainer.Margin = new Thickness(12, 0, 0, 0);
                HotEdge.Width = 3;
                HotEdge.Height = double.NaN;
                HotEdge.HorizontalAlignment = HorizontalAlignment.Left;
                HotEdge.VerticalAlignment = VerticalAlignment.Stretch;
                break;

            case DockEdge.Right:
                DockSlideHost.VerticalAlignment = VerticalAlignment.Center;
                DockSlideHost.HorizontalAlignment = HorizontalAlignment.Right;
                DockContainer.Margin = new Thickness(0, 0, 12, 0);
                HotEdge.Width = 3;
                HotEdge.Height = double.NaN;
                HotEdge.HorizontalAlignment = HorizontalAlignment.Right;
                HotEdge.VerticalAlignment = VerticalAlignment.Stretch;
                break;

            default: // Bottom
                DockSlideHost.VerticalAlignment = VerticalAlignment.Bottom;
                DockSlideHost.HorizontalAlignment = HorizontalAlignment.Center;
                DockContainer.Margin = new Thickness(0, 0, 0, 12);
                HotEdge.Height = 3;
                HotEdge.Width = double.NaN;
                HotEdge.VerticalAlignment = VerticalAlignment.Bottom;
                HotEdge.HorizontalAlignment = HorizontalAlignment.Stretch;
                break;
        }

        Point origin = GetIconTransformOrigin();
        foreach (Border host in _icons)
        {
            host.RenderTransformOrigin = origin;
            if (host.Tag is IconViewState state)
                state.IconSurface.RenderTransformOrigin = origin;
        }
    }

    private Point GetIconTransformOrigin() => _dockEdge switch
    {
        DockEdge.Top => new Point(0.5, 0.0),
        DockEdge.Left => new Point(0.0, 0.5),
        DockEdge.Right => new Point(1.0, 0.5),
        _ => new Point(0.5, 1.0)
    };

    private double GetHiddenOffset()
    {
        double hostSize = _dockEdge.IsHorizontal()
            ? (DockSlideHost.ActualHeight > 0 ? DockSlideHost.ActualHeight : ActualHeight)
            : (DockSlideHost.ActualWidth > 0 ? DockSlideHost.ActualWidth : ActualWidth);
        return hostSize;
    }

    private void ApplyHiddenOffset(bool immediate)
    {
        double offset = GetHiddenOffset();
        double x = 0, y = 0;
        switch (_dockEdge)
        {
            case DockEdge.Top: y = -offset; break;
            case DockEdge.Left: x = -offset; break;
            case DockEdge.Right: x = offset; break;
            default: y = offset; break;
        }

        if (immediate)
        {
            DockTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            DockTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            DockTranslate.X = x;
            DockTranslate.Y = y;
        }
        else
        {
            AnimateDockTranslate(x, y, null);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  設定載入 / 儲存
    // ══════════════════════════════════════════════════════════

    private async Task LoadConfigAndIconsAsync()
    {
        DockConfig config = _bootConfig ?? await DockConfigStore.LoadAsync();
        _bootConfig = null;
        config.Clamp();

        ApplyConfigFields(config);
        _preferredScreenIndex = config.ScreenIndex;
        _dockEdge = config.DockEdge;
        _hideTimer.Interval = _hideDelay;
        ApplyEdgeLayout();
        ApplyTheme();
        ApplyRenderTuning();

        bool registryAutoStart = AutostartHelper.IsEnabled();
        if (config.AutoStart != registryAutoStart)
            config.AutoStart = registryAutoStart;

        if (config.Items.Count == 0)
        {
            SeedDefaultItems();
            ScheduleSave();
        }
        else
        {
            foreach (DockItem item in config.Items)
                InsertDockItem(item, _icons.Count, persist: false);
        }

        RecenterOnWorkArea();
        await LoadIconsAsync();
    }

    private void SeedDefaultItems()
    {
        string system32 = Environment.SystemDirectory;
        string[] defaults =
        [
            Path.Combine(system32, "explorer.exe"),
            Path.Combine(system32, "notepad.exe"),
            Path.Combine(system32, "calc.exe"),
            Path.Combine(system32, "cmd.exe"),
            Path.Combine(system32, "control.exe"),
        ];

        foreach (var path in defaults)
        {
            if (!File.Exists(path))
                continue;

            try
            {
                InsertDockItem(ShellIconHelper.CreateDockItemFromPath(path), _icons.Count, persist: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RDock] Seed failed for {path}: {ex.Message}");
            }
        }
    }

    private static DockItem CreateRecycleBinDockItem() => new()
    {
        Id = DockItem.RecycleBinItemId,
        Title = "資源回收筒",
        TargetPath = ShellRecycleBin.ShellFolderPath,
        Kind = DockItemKind.RecycleBin,
        SourcePath = ShellRecycleBin.ParsingName
    };

    private static DockItem CreateThisPCDockItem() => new()
    {
        Id = DockItem.ThisPCItemId,
        Title = ShellThisPC.DisplayTitle,
        TargetPath = ShellThisPC.ShellFolderPath,
        Kind = DockItemKind.ThisPC,
        SourcePath = ShellThisPC.ParsingName
    };

    private static DockItem CreateClockDockItem() => new()
    {
        Id = DockItem.ClockItemId,
        Title = "時鐘",
        TargetPath = string.Empty,
        Kind = DockItemKind.Clock
    };

    private static DockItem CreateSeparatorDockItem() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Title = "—",
        TargetPath = string.Empty,
        Kind = DockItemKind.Separator
    };

    /// <summary>依設定確保／移除本機。</summary>
    private void EnsureThisPCItem()
    {
        int existing = _items.FindIndex(i => i.IsThisPC);
        if (!_showThisPC)
        {
            if (existing >= 0)
                RemoveItemAt(existing, persist: true, allowDocklets: true);
            return;
        }

        if (existing >= 0)
        {
            _items[existing].Kind = DockItemKind.ThisPC;
            _items[existing].Id = DockItem.ThisPCItemId;
            _items[existing].Title = ShellThisPC.DisplayTitle;
            _items[existing].TargetPath = ShellThisPC.ShellFolderPath;
            _items[existing].SourcePath = ShellThisPC.ParsingName;
            return;
        }

        int insertAt = _icons.Count;
        int recycleIdx = _items.FindIndex(i => i.IsRecycleBin);
        int clockIdx = _items.FindIndex(i => i.IsClock);
        if (recycleIdx >= 0)
            insertAt = recycleIdx;
        else if (clockIdx >= 0)
            insertAt = clockIdx;

        InsertDockItem(CreateThisPCDockItem(), insertAt, persist: true);
        _ = LoadSingleIconAsync(_items[Math.Min(insertAt, _items.Count - 1)]);
    }

    /// <summary>依設定確保／移除資源回收筒。</summary>
    private void EnsureRecycleBinItem()
    {
        int existing = _items.FindIndex(i => i.IsRecycleBin);
        if (!_showRecycleBin)
        {
            if (existing >= 0)
                RemoveItemAt(existing, persist: true, allowDocklets: true);
            return;
        }

        if (existing >= 0)
        {
            _items[existing].Kind = DockItemKind.RecycleBin;
            _items[existing].Id = DockItem.RecycleBinItemId;
            return;
        }

        int insertAt = _icons.Count;
        int clockIdx = _items.FindIndex(i => i.IsClock);
        if (clockIdx >= 0)
            insertAt = clockIdx;

        InsertDockItem(CreateRecycleBinDockItem(), insertAt, persist: true);
        _ = LoadSingleIconAsync(_items[Math.Min(insertAt, _items.Count - 1)]);
    }

    /// <summary>依設定確保／移除時鐘 Docklet（固定置於最右側）。</summary>
    private void EnsureClockItem()
    {
        int existing = _items.FindIndex(i => i.IsClock);
        if (!_showClock)
        {
            if (existing >= 0)
                RemoveItemAt(existing, persist: true, allowDocklets: true);
            _clockTimer.Stop();
            return;
        }

        if (existing >= 0)
        {
            _items[existing].Kind = DockItemKind.Clock;
            _items[existing].Id = DockItem.ClockItemId;
            if (!_clockTimer.IsEnabled)
                _clockTimer.Start();
            RefreshClockTexts();
            return;
        }

        InsertDockItem(CreateClockDockItem(), _icons.Count, persist: true);
        if (!_clockTimer.IsEnabled)
            _clockTimer.Start();
        RefreshClockTexts();
    }

    private void RefreshClockTexts()
    {
        string text = DateTime.Now.ToString("HH:mm");
        foreach (Border host in _icons)
        {
            if (host.Tag is IconViewState { ClockText: { } clock })
                clock.Text = text;
        }
    }

    private async Task RefreshRecycleBinAsync()
    {
        try
        {
            if (!_showRecycleBin)
                return;

            var status = await Task.Run(ShellRecycleBin.QueryStatus).ConfigureAwait(true);
            bool isFull = status.HasItems;
            if (isFull == _recycleBinWasFull &&
                _items.Any(i => i.IsRecycleBin && i.Icon is not null))
            {
                return;
            }

            _recycleBinWasFull = isFull;
            DockItem? item = _items.FirstOrDefault(i => i.IsRecycleBin);
            if (item is null)
                return;

            ImageSource? icon = await Task.Run(() => ShellRecycleBin.GetIcon(isFull)).ConfigureAwait(true);
            if (icon is null)
                return;

            item.Icon = icon;
            Border? host = _icons.FirstOrDefault(b => GetItem(b)?.IsRecycleBin == true);
            if (host is not null)
                ApplyIconToBorder(host, item);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RDock] RecycleBin refresh failed: {ex.Message}");
        }
    }

    private DockConfig BuildConfig() => new()
    {
        IsLocked = _isLocked,
        AutoStart = AutostartHelper.IsEnabled(),
        ScreenIndex = _preferredScreenIndex,
        DockEdge = _dockEdge,
        AutoHideEnabled = _autoHideEnabled,
        IconSize = _iconSize,
        MaxScale = _maxScale,
        HideDelayMs = (int)_hideDelay.TotalMilliseconds,
        DockOpacity = _dockOpacity,
        ShowRecycleBin = _showRecycleBin,
        ShowThisPC = _showThisPC,
        ShowClock = _showClock,
        EnableAppBar = _enableAppBar,
        FisheyePushEnabled = _fisheyePush,
        Items = _items.Select(CloneForPersist).ToList()
    };

    private static DockItem CloneForPersist(DockItem item) => new()
    {
        Id = item.Id,
        Title = item.Title,
        TargetPath = item.TargetPath,
        Arguments = item.Arguments,
        IconCachePath = item.IconCachePath,
        WorkingDirectory = item.WorkingDirectory,
        SourcePath = item.SourcePath,
        Kind = item.Kind
    };

    private void ScheduleSave() => DockManager.ScheduleSave(this);

    private async Task LoadIconsAsync()
    {
        _iconLoadCts?.Cancel();
        _iconLoadCts = new CancellationTokenSource();
        var token = _iconLoadCts.Token;
        var snapshot = _icons.ToArray();

        // 平行讀取磁碟快取／提取，啟動時有快取可接近瞬間完成
        var tasks = new List<Task>(snapshot.Length);
        int cachePathChanged = 0;

        foreach (Border border in snapshot)
        {
            if (border.Tag is not IconViewState state)
                continue;

            DockItem item = state.Item;
            if (item.IsSeparator || item.IsClock)
                continue;

            string? before = item.IconCachePath;

            tasks.Add(Task.Run(() =>
            {
                if (token.IsCancellationRequested)
                    return;

                try
                {
                    ImageSource? icon = ShellIconHelper.ResolveIcon(item, IconDecodeSize);
                    if (icon is null || token.IsCancellationRequested)
                        return;

                    item.Icon = icon;
                    if (!string.Equals(before, item.IconCachePath, StringComparison.OrdinalIgnoreCase))
                        Interlocked.Exchange(ref cachePathChanged, 1);

                    Dispatcher.BeginInvoke(() =>
                    {
                        if (token.IsCancellationRequested)
                            return;
                        ApplyIconToBorder(border, item);
                    }, DispatcherPriority.Background);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RDock] Icon load failed for {item.Title}: {ex.Message}");
                }
            }, token));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }

        if (cachePathChanged != 0)
            ScheduleSave();

        RefreshRunningIndicators();
    }

    private static void ApplyIconToBorder(Border host, DockItem item)
    {
        if (item.IsSeparator || item.IsClock)
            return;

        if (item.Icon is null || host.Tag is not IconViewState state)
            return;

        // 不要對 Image 使用 BitmapCache：魚眼 ScaleTransform 會把 1x 快取放大成馬賽克
        var image = new Image
        {
            Source = item.Icon,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(2),
            SnapsToDevicePixels = false,
            UseLayoutRounding = false
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(image, EdgeMode.Unspecified);

        state.IconSurface.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        state.IconSurface.Child = image;
    }

    private void OnProcessesUpdated(object? sender, EventArgs e) => RefreshRunningIndicators();

    private void RefreshRunningIndicators()
    {
        foreach (Border host in _icons)
        {
            if (host.Tag is not IconViewState state)
                continue;

            bool running = !state.Item.IsDocklet &&
                           DockManager.SharedProcessWatcher.IsRunning(state.Item.TargetPath);
            IconBounceAnimation.SetRunning(state.Indicator, running);
        }
    }

    private static IconViewState? GetState(DependencyObject? element) =>
        element is FrameworkElement { Tag: IconViewState state } ? state : null;

    private static DockItem? GetItem(DependencyObject? element) => GetState(element)?.Item;

    // ══════════════════════════════════════════════════════════
    //  自動隱藏 / 邊緣感應 / 全螢幕避讓
    // ══════════════════════════════════════════════════════════

    private void OnFullscreenChanged(object? sender, EventArgs e)
    {
        if (DockManager.SharedFullscreenWatcher.IsFullscreenOnScreen(_boundScreenIndex))
        {
            CancelHideTimer();
            if (!_isHidden)
                HideDock();
        }
    }

    private bool ShouldSuppressEdgeWake() =>
        DockManager.SharedFullscreenWatcher.IsFullscreenOnScreen(_boundScreenIndex);

    private void DockContainer_MouseEnter(object sender, MouseEventArgs e)
    {
        CancelHideTimer();
        ShowDock();
        BeginFisheyeTracking();
    }

    private void HotEdge_MouseEnter(object sender, MouseEventArgs e)
    {
        if (ShouldSuppressEdgeWake())
            return;

        CancelHideTimer();
        ShowDock();
    }

    private void HideTimer_Tick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        if (IsAnyContextMenuOpen())
            return;
        if (DockContainer.IsMouseOver || HotEdge.IsMouseOver)
            return;
        HideDock();
    }

    private void EdgeProbeTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isHidden || _isAnimating)
            return;

        if (ShouldSuppressEdgeWake())
            return;

        if (MonitorService.IsCursorAtMonitorEdge(_boundScreenIndex, _dockEdge, EdgeTriggerDip))
        {
            CancelHideTimer();
            ShowDock();
        }
    }

    private void StartHideTimer()
    {
        if (!_autoHideEnabled || IsAnyContextMenuOpen())
            return;

        _hideTimer.Stop();
        _hideTimer.Interval = _hideDelay;
        _hideTimer.Start();
    }

    private void CancelHideTimer() => _hideTimer.Stop();

    private bool IsAnyContextMenuOpen()
    {
        if (DockContainer.ContextMenu?.IsOpen == true)
            return true;

        foreach (Border host in _icons)
        {
            if (host.ContextMenu?.IsOpen == true)
                return true;
        }

        return _openContextMenus > 0;
    }

    /// <summary>右鍵按下當下就解除 NOACTIVATE，讓即將開啟的 Popup 能正常收合。</summary>
    private void Window_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        BeginContextMenuSession();
    }

    /// <summary>ContextMenu 真正顯示前（比 Opened 早），確保互動模式已開。</summary>
    private void OnAnyContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        BeginContextMenuSession();
        CancelHideTimer();
        ShowDock();
    }

    private void ContextMenu_OpenedKeepVisible(object sender, RoutedEventArgs e)
    {
        _openContextMenus++;
        BeginContextMenuSession();
        CancelHideTimer();
        ShowDock();

        if (sender is ContextMenu menu)
        {
            menu.StaysOpen = true;
            _menuWasOpen = true;
        }

        ArmOutsideClickWatcher();
    }

    /// <summary>進入選單工作階段：解除 NOACTIVATE、啟動外部點擊監看。</summary>
    private void BeginContextMenuSession()
    {
        if (!_menuSessionActive)
        {
            _menuSessionActive = true;
            _menuWasOpen = false;
        }

        WindowActivationGuard.EnsureInteractive(this);
        CancelHideTimer();
        ArmOutsideClickWatcher();
    }

    private void ContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        _openContextMenus = Math.Max(0, _openContextMenus - 1);
        Dispatcher.BeginInvoke(AfterContextMenusChanged, DispatcherPriority.Input);
    }

    private void AfterContextMenusChanged()
    {
        if (IsAnyContextMenuReallyOpen())
            return;

        // 只有確認曾開啟過，才結束工作階段（避免 Opening 前誤關）
        if (_menuSessionActive && _menuWasOpen)
            EndContextMenuSessionAndMaybeHide();
    }

    private bool IsAnyContextMenuReallyOpen()
    {
        if (DockContainer.ContextMenu?.IsOpen == true)
            return true;

        foreach (Border host in _icons)
        {
            if (host.ContextMenu?.IsOpen == true)
                return true;
        }

        return false;
    }

    /// <summary>選單全部關閉後：恢復 NOACTIVATE，並在滑鼠不在 Dock 上時啟動隱藏。</summary>
    private void EndContextMenuSessionAndMaybeHide(bool hideImmediatelyIfAway = false)
    {
        _openContextMenus = 0;
        _menuSessionActive = false;
        _menuWasOpen = false;
        DisarmOutsideClickWatcher();
        WindowActivationGuard.EndInteractive(this);

        bool overDock = DockContainer.IsMouseOver || HotEdge.IsMouseOver;
        if (overDock)
            return;

        if (!_autoHideEnabled)
            return;

        CancelHideTimer();
        if (hideImmediatelyIfAway)
            HideDock();
        else
            StartHideTimer();
    }

    private void ArmOutsideClickWatcher()
    {
        // 低階鉤子：略過開啟後短時間內的按鍵，之後點選單外即關
        _outsideClickTimer.Stop();
        if (_menuWasOpen || IsAnyContextMenuReallyOpen())
            _outsideClickHook.Start(TimeSpan.FromMilliseconds(150));
    }

    private void DisarmOutsideClickWatcher()
    {
        _outsideClickTimer.Stop();
        _outsideClickHook.Stop();
    }

    private void OutsideClickTimer_Tick(object? sender, EventArgs e)
    {
        // 實際關閉改由 MenuOutsideClickHook 處理
    }

    private bool IsCursorOverOpenContextMenu()
    {
        if (!GetCursorPos(out POINT cursor))
            return false;

        // 只依 Win32 游標座標／HWND 判斷。
        // 不可用 Mouse.DirectlyOver：Popup 有滑鼠捕捉時，游標在桌面仍會回報「在選單上」，
        // 造成「點桌面關不掉、點其他程式可以關」的現象。
        if (IsCursorOverOwnedPopup(cursor))
            return true;

        if (DockContainer.ContextMenu is { IsOpen: true } dockMenu &&
            IsPointInsideMenuTree(dockMenu, cursor))
            return true;

        foreach (Border host in _icons)
        {
            if (host.ContextMenu is { IsOpen: true } iconMenu &&
                IsPointInsideMenuTree(iconMenu, cursor))
                return true;
        }

        return false;
    }

    /// <summary>游標下是否為本行程、且不是主 Dock 視窗的 Popup（ContextMenu / 子選單）。</summary>
    private bool IsCursorOverOwnedPopup(POINT cursor)
    {
        IntPtr hit = WindowFromPoint(cursor);
        if (hit == IntPtr.Zero)
            return false;

        GetWindowThreadProcessId(hit, out uint pid);
        if (pid != (uint)Environment.ProcessId)
            return false;

        IntPtr main = new WindowInteropHelper(this).Handle;
        if (main == IntPtr.Zero)
            return false;

        IntPtr root = GetAncestor(hit, GaRoot);
        if (root == IntPtr.Zero)
            root = hit;

        // 點在主 Dock 視窗上 → 不算選單（可關閉）
        if (root == main || hit == main)
            return false;

        // 本行程其他頂層視窗（選單 Popup / FolderStack 等）→ 視為選單區
        return true;
    }

    private static bool IsInMenuVisualTree(DependencyObject? node)
    {
        for (DependencyObject? cur = node; cur is not null; cur = GetVisualOrLogicalParent(cur))
        {
            if (cur is System.Windows.Controls.ContextMenu
                or System.Windows.Controls.MenuItem
                or System.Windows.Controls.Separator)
                return true;
        }

        return false;
    }

    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject d)
    {
        if (d is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            DependencyObject? visualParent = VisualTreeHelper.GetParent(d);
            if (visualParent is not null)
                return visualParent;
        }

        return LogicalTreeHelper.GetParent(d);
    }

    /// <summary>根選單或任一已開啟子選單的畫面矩形。</summary>
    private static bool IsPointInsideMenuTree(ItemsControl menu, POINT cursor)
    {
        if (IsPointInsideFrameworkElement(menu, cursor))
            return true;

        foreach (object obj in menu.Items)
        {
            if (obj is not MenuItem { IsSubmenuOpen: true } item)
                continue;

            if (IsPointInsideFrameworkElement(item, cursor))
                return true;

            // 子選單 Popup 內的項目容器
            if (item.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
            {
                for (int i = 0; i < item.Items.Count; i++)
                {
                    if (item.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement child &&
                        IsPointInsideFrameworkElement(child, cursor))
                        return true;
                }
            }

            // 遞迴更深層（理論上我們只有一層，仍保險）
            if (IsPointInsideMenuTree(item, cursor))
                return true;
        }

        return false;
    }

    private static bool IsPointInsideFrameworkElement(FrameworkElement element, POINT cursor)
    {
        try
        {
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                return false; // 不可回傳 IsMouseOver：捕捉時桌面上也會是 true

            // PointToScreen 已是螢幕像素；ActualWidth 是 DIP，需乘上 DPI
            Point topLeft = element.PointToScreen(new Point(0, 0));
            double dpiX = 1.0, dpiY = 1.0;
            PresentationSource? source = PresentationSource.FromVisual(element);
            if (source?.CompositionTarget is { } ct)
            {
                dpiX = ct.TransformToDevice.M11;
                dpiY = ct.TransformToDevice.M22;
            }

            double widthPx = element.ActualWidth * dpiX;
            double heightPx = element.ActualHeight * dpiY;

            return cursor.X >= topLeft.X && cursor.X <= topLeft.X + widthPx &&
                   cursor.Y >= topLeft.Y && cursor.Y <= topLeft.Y + heightPx;
        }
        catch
        {
            return false;
        }
    }

    private void CloseAllContextMenus(bool hideDockIfAway = false)
    {
        try
        {
            Mouse.Capture(null);
            WindowActivationGuard.EnsureInteractive(this);

            void ForceClose(ContextMenu? menu)
            {
                if (menu is null)
                    return;
                menu.StaysOpen = false;
                menu.IsOpen = false;
            }

            ForceClose(DockContainer.ContextMenu);
            foreach (Border host in _icons)
                ForceClose(host.ContextMenu);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RDock] CloseAllContextMenus failed: {ex.Message}");
        }

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (DockContainer.ContextMenu is { } m)
                {
                    m.StaysOpen = false;
                    m.IsOpen = false;
                }
                foreach (Border host in _icons)
                {
                    if (host.ContextMenu is { } im)
                    {
                        im.StaysOpen = false;
                        im.IsOpen = false;
                    }
                }
            }
            catch { /* ignore */ }

            EndContextMenuSessionAndMaybeHide(hideImmediatelyIfAway: hideDockIfAway);
        }, DispatcherPriority.Send);
    }

    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const uint GaRoot = 2;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    private void ShowDock()
    {
        // 多螢幕：此 Dock 出現時，其他螢幕立刻收起
        DockManager.NotifyDockActivated(this);
        ExitPowerSavingMode();

        if (!_isHidden && DockTranslate.X == 0 && DockTranslate.Y == 0)
            return;

        _isHidden = false;
        _edgeProbeTimer.Stop();
        AnimateDockTranslate(0, 0, onCompleted: null);
    }

    /// <summary>其他螢幕 Dock 被喚醒時呼叫，強制收起自己。</summary>
    public void RequestHideFromPeer()
    {
        if (!_autoHideEnabled)
            return;

        if (IsAnyContextMenuOpen())
            return;

        CancelHideTimer();
        _fisheyeTracking = false;

        foreach (var icon in _icons)
            AnimateScaleTo(icon, 1.0);

        if (!_isHidden)
            HideDock();
    }

    private void HideDock()
    {
        if (_isHidden || IsAnyContextMenuOpen())
            return;

        _isHidden = true;
        _fisheyeTracking = false;
        foreach (var icon in _icons)
        {
            AnimateScaleTo(icon, 1.0);
            ApplySlide(icon, 0);
        }

        double offset = GetHiddenOffset();
        double x = 0, y = 0;
        switch (_dockEdge)
        {
            case DockEdge.Top: y = -offset; break;
            case DockEdge.Left: x = -offset; break;
            case DockEdge.Right: x = offset; break;
            default: y = offset; break;
        }

        AnimateDockTranslate(x, y, onCompleted: () =>
        {
            _edgeProbeTimer.Start();
            EnterPowerSavingMode();
        });
    }

    /// <summary>完全隱藏後：降頻計時器、暫停進程輪詢與魚眼，讓背景 CPU 趨近 0。</summary>
    private void EnterPowerSavingMode()
    {
        if (_powerSaving || !_isHidden)
            return;

        _powerSaving = true;
        _fisheyeTracking = false;
        _edgeProbeTimer.Interval = EdgeProbeIdle;
        _recycleBinTimer.Interval = RecycleBinIdle;
        _recycleBinTimer.Stop();
        _clockTimer.Stop();
        DockManager.SharedProcessWatcher.SetSuspended(true);
        DockManager.SharedFullscreenWatcher.SetIdle(true);

        // 隱藏時不必維持昂貴的 BitmapCache 即時合成
        DockSlideHost.CacheMode = CreateDockBitmapCache(forIdle: true);
    }

    /// <summary>喚醒 Dock：恢復正常輪詢與渲染品質。</summary>
    private void ExitPowerSavingMode()
    {
        if (!_powerSaving)
            return;

        _powerSaving = false;
        _edgeProbeTimer.Interval = EdgeProbeActive;
        _recycleBinTimer.Interval = RecycleBinActive;
        if (!_recycleBinTimer.IsEnabled)
            _recycleBinTimer.Start();
        DockManager.SharedProcessWatcher.SetSuspended(false);
        DockManager.SharedFullscreenWatcher.SetIdle(false);

        DockSlideHost.CacheMode = CreateDockBitmapCache(forIdle: false);
        _ = RefreshRecycleBinAsync();
        RefreshRunningIndicators();
        if (_showClock)
        {
            if (!_clockTimer.IsEnabled)
                _clockTimer.Start();
            RefreshClockTexts();
        }
    }

    private void AnimateDockTranslate(double toX, double toY, Action? onCompleted)
    {
        _isAnimating = true;

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var duration = new Duration(SlideDuration);

        var animX = new DoubleAnimation
        {
            To = toX,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        var animY = new DoubleAnimation
        {
            To = toY,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };

        int remaining = 2;
        void OnOneCompleted(object? s, EventArgs e)
        {
            remaining--;
            if (remaining > 0)
                return;

            DockTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            DockTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            DockTranslate.X = toX;
            DockTranslate.Y = toY;
            _isAnimating = false;
            onCompleted?.Invoke();
        }

        animX.Completed += OnOneCompleted;
        animY.Completed += OnOneCompleted;

        DockTranslate.BeginAnimation(TranslateTransform.XProperty, animX);
        DockTranslate.BeginAnimation(TranslateTransform.YProperty, animY);
    }

    // ══════════════════════════════════════════════════════════
    //  Drag & Drop
    // ══════════════════════════════════════════════════════════

    private void Window_DragEnter(object sender, DragEventArgs e) => HandleDragEnter(e);
    private void DockContainer_DragEnter(object sender, DragEventArgs e) => HandleDragEnter(e);
    private void Window_DragOver(object sender, DragEventArgs e) => HandleDragOver(e);
    private void DockContainer_DragOver(object sender, DragEventArgs e) => HandleDragOver(e);
    private void Window_Drop(object sender, DragEventArgs e) => HandleDrop(e);
    private void DockContainer_Drop(object sender, DragEventArgs e) => HandleDrop(e);

    private void HandleDragEnter(DragEventArgs e)
    {
        // NOACTIVATE 會干擾 OLE 拖放；拖入時暫時允許互動
        WindowActivationGuard.EnsureInteractive(this);
        CancelHideTimer();
        ShowDock();
        HandleDragOver(e);
    }

    private void HandleDragOver(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DockItemDragFormat))
        {
            e.Effects = _isLocked ? DragDropEffects.None : DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (ShellDropHelper.CanAccept(e.Data))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void HandleDrop(DragEventArgs e)
    {
        // 僅「真實檔案」拖到圖示上時交給 Icon_Drop（開啟／丟回收筒）；
        // Shell 虛擬項目（本機等）一律釘到 Dock。
        if (e.OriginalSource is DependencyObject source)
        {
            var host = FindAncestorBorderWithState(source);
            if (host is not null &&
                e.Data.GetDataPresent(DataFormats.FileDrop) &&
                !e.Data.GetDataPresent(DockItemDragFormat) &&
                !HasShellOnlyItems(e.Data))
            {
                return;
            }
        }

        e.Handled = true;
        CancelHideTimer();
        ShowDock();

        Point posInPanel = e.GetPosition(IconPanel);
        int insertIndex = GetInsertIndex(posInPanel);

        if (e.Data.GetDataPresent(DockItemDragFormat))
        {
            if (_isLocked)
                return;

            if (e.Data.GetData(DockItemDragFormat) is not string itemId)
                return;

            MoveItem(itemId, insertIndex);
            return;
        }

        var paths = ShellDropHelper.ExtractPaths(e.Data);
        if (paths.Count == 0)
        {
            Debug.WriteLine("[RDock] Drop: no extractable paths. Formats=" +
                            string.Join(", ", SafeGetFormats(e.Data)));
            return;
        }

        if (_isLocked)
            insertIndex = _icons.Count;

        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            bool existsOnDisk = File.Exists(path) || Directory.Exists(path);
            if (!existsOnDisk && !ShellIconHelper.IsShellItemPath(path) &&
                !LooksLikeShellUrl(path))
                continue;

            try
            {
                string normalized = NormalizeDroppedPath(path);
                DockItem item = ShellIconHelper.CreateDockItemFromPath(normalized);
                InsertDockItem(item, insertIndex, persist: true);
                insertIndex++;
                _ = LoadSingleIconAsync(item);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RDock] Drop failed for {path}: {ex.Message}");
                MessageBox.Show(
                    $"無法加入項目：\n{path}\n\n{ex.Message}",
                    "RDock",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        RecenterOnWorkArea();
    }

    private static bool HasShellOnlyItems(System.Windows.IDataObject data)
    {
        var paths = ShellDropHelper.ExtractPaths(data);
        if (paths.Count == 0)
            return false;

        return paths.All(p =>
            ShellIconHelper.IsShellItemPath(p) || LooksLikeShellUrl(p) ||
            (!File.Exists(p) && !Directory.Exists(p)));
    }

    private static bool LooksLikeShellUrl(string path) =>
        path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("::{", StringComparison.Ordinal) ||
        path.Contains("::{", StringComparison.Ordinal);

    private static string NormalizeDroppedPath(string path)
    {
        // file:///C:/... → C:\...
        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) &&
            uri.IsFile)
        {
            return uri.LocalPath;
        }

        return path;
    }

    private static IEnumerable<string> SafeGetFormats(System.Windows.IDataObject data)
    {
        try
        {
            return data.GetFormats(autoConvert: false);
        }
        catch
        {
            return [];
        }
    }

    private static Border? FindAncestorBorderWithState(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is Border { Tag: IconViewState } border)
                return border;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void MoveItem(string itemId, int insertIndex)
    {
        int from = _icons.FindIndex(b => GetItem(b)?.Id == itemId);
        if (from < 0)
            return;

        insertIndex = Math.Clamp(insertIndex, 0, _icons.Count);
        if (from < insertIndex)
            insertIndex--;

        if (from == insertIndex)
            return;

        Border border = _icons[from];
        DockItem item = _items[from];

        _icons.RemoveAt(from);
        _items.RemoveAt(from);
        IconPanel.Children.RemoveAt(from);

        _icons.Insert(insertIndex, border);
        _items.Insert(insertIndex, item);
        IconPanel.Children.Insert(insertIndex, border);

        ScheduleSave();
        RecenterOnWorkArea();
        InvalidateIconCenters();
    }

    private int GetInsertIndex(Point cursorInPanel)
    {
        bool horizontal = _dockEdge.IsHorizontal();
        double cursor = horizontal ? cursorInPanel.X : cursorInPanel.Y;

        for (int i = 0; i < _icons.Count; i++)
        {
            Border icon = _icons[i];
            Point mid = horizontal
                ? new Point(icon.ActualWidth / 2.0, 0)
                : new Point(0, icon.ActualHeight / 2.0);
            Point center = icon.TransformToAncestor(IconPanel).Transform(mid);
            double centerAxis = horizontal ? center.X : center.Y;

            if (cursor < centerAxis)
                return i;
        }

        return _icons.Count;
    }

    // ══════════════════════════════════════════════════════════
    //  動態圖示
    // ══════════════════════════════════════════════════════════

    private void InsertDockItem(DockItem item, int index, bool persist)
    {
        index = Math.Clamp(index, 0, _icons.Count);

        Border iconBorder = CreateIconBorder(item);
        _icons.Insert(index, iconBorder);
        _items.Insert(index, item);
        IconPanel.Children.Insert(index, iconBorder);

        if (persist)
        {
            ScheduleSave();
            RefreshRunningIndicators();
        }

        InvalidateIconCenters();
    }

    private Border CreateIconBorder(DockItem item)
    {
        var scale = new ScaleTransform(1, 1);
        var bounce = new TranslateTransform(0, 0);
        var slide = new TranslateTransform(0, 0);
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(scale);
        transformGroup.Children.Add(slide);

        if (item.IsSeparator)
            return CreateSeparatorBorder(item, scale, bounce, slide, transformGroup);

        TextBlock? clockText = null;
        UIElement surfaceChild;
        if (item.IsClock)
        {
            clockText = new TextBlock
            {
                Text = DateTime.Now.ToString("HH:mm"),
                FontSize = Math.Max(11, _iconSize * 0.28),
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            surfaceChild = clockText;
        }
        else
        {
            surfaceChild = new TextBlock
            {
                Text = "⏳",
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var iconSurface = new Border
        {
            Width = _iconSize,
            Height = _iconSize,
            CornerRadius = new CornerRadius(12),
            Background = item.IsClock
                ? new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            RenderTransformOrigin = GetIconTransformOrigin(),
            RenderTransform = bounce,
            ClipToBounds = false,
            Child = surfaceChild
        };

        var indicator = IconBounceAnimation.CreateIndicatorDot();
        if (item.IsClock || item.IsRecycleBin || item.IsThisPC)
            indicator.Visibility = Visibility.Collapsed;

        var layout = new Grid
        {
            Width = _iconSize,
            Height = _iconSize + 12
        };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_iconSize) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        Grid.SetRow(iconSurface, 0);
        Grid.SetRow(indicator, 1);
        layout.Children.Add(iconSurface);
        layout.Children.Add(indicator);

        var host = new Border
        {
            Width = _iconSize,
            Height = _iconSize + 12,
            Margin = new Thickness(6, 0, 6, 0),
            Background = Brushes.Transparent,
            RenderTransformOrigin = GetIconTransformOrigin(),
            RenderTransform = transformGroup,
            Cursor = Cursors.Hand,
            ClipToBounds = false,
            ContextMenu = BuildIconContextMenu(item),
            Child = layout
        };
        ToolTipService.SetIsEnabled(host, false);

        var state = new IconViewState
        {
            Item = item,
            Host = host,
            IconSurface = iconSurface,
            Indicator = indicator,
            Scale = scale,
            Bounce = bounce,
            Slide = slide,
            ClockText = clockText
        };
        host.Tag = state;

        if (item.Icon is not null)
            ApplyIconToBorder(host, item);

        host.AllowDrop = !item.IsClock;
        if (!item.IsClock)
        {
            host.PreviewDragOver += Icon_PreviewDragOver;
            host.Drop += Icon_Drop;
        }

        host.PreviewMouseLeftButtonDown += Icon_PreviewMouseLeftButtonDown;
        host.PreviewMouseMove += Icon_PreviewMouseMove;
        host.MouseLeftButtonUp += Icon_Click;
        host.MouseEnter += Icon_MouseEnter;
        host.MouseLeave += Icon_MouseLeave;
        return host;
    }

    private Border CreateSeparatorBorder(
        DockItem item,
        ScaleTransform scale,
        TranslateTransform bounce,
        TranslateTransform slide,
        TransformGroup transformGroup)
    {
        bool horizontal = _dockEdge.IsHorizontal();
        double thickness = 2;
        double length = _iconSize * 0.55;

        var bar = new Border
        {
            Width = horizontal ? thickness : length,
            Height = horizontal ? length : thickness,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        var iconSurface = new Border
        {
            Width = horizontal ? 10 : _iconSize,
            Height = horizontal ? _iconSize : 10,
            Background = Brushes.Transparent,
            RenderTransformOrigin = GetIconTransformOrigin(),
            RenderTransform = bounce,
            Child = bar
        };

        var indicator = IconBounceAnimation.CreateIndicatorDot();
        indicator.Visibility = Visibility.Collapsed;

        var host = new Border
        {
            Width = horizontal ? 14 : _iconSize,
            Height = horizontal ? _iconSize + 12 : 14,
            Margin = horizontal ? new Thickness(4, 0, 4, 0) : new Thickness(0, 4, 0, 4),
            Background = Brushes.Transparent,
            RenderTransformOrigin = GetIconTransformOrigin(),
            RenderTransform = transformGroup,
            Cursor = Cursors.Arrow,
            ClipToBounds = false,
            ContextMenu = BuildIconContextMenu(item),
            Child = iconSurface,
            AllowDrop = false
        };
        ToolTipService.SetIsEnabled(host, false);

        host.Tag = new IconViewState
        {
            Item = item,
            Host = host,
            IconSurface = iconSurface,
            Indicator = indicator,
            Scale = scale,
            Bounce = bounce,
            Slide = slide
        };

        host.PreviewMouseLeftButtonDown += Icon_PreviewMouseLeftButtonDown;
        host.PreviewMouseMove += Icon_PreviewMouseMove;
        return host;
    }

    private ContextMenu BuildIconContextMenu(DockItem item)
    {
        var menu = new ContextMenu { StaysOpen = true };
        menu.Opened += ContextMenu_OpenedKeepVisible;
        menu.Closed += ContextMenu_Closed;

        if (item.IsRecycleBin)
        {
            var open = new MenuItem { Header = "開啟資源回收筒", Tag = item };
            open.Click += (_, _) => ShellRecycleBin.OpenInExplorer();

            var empty = new MenuItem { Header = "清空資源回收筒", Tag = item };
            empty.Click += MenuEmptyRecycleBin_Click;

            menu.Items.Add(open);
            menu.Items.Add(new Separator());
            menu.Items.Add(empty);
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildRDockSettingsSubMenu());
            return menu;
        }

        if (item.IsThisPC)
        {
            var openPc = new MenuItem { Header = "開啟本機", Tag = item };
            openPc.Click += (_, _) => ShellThisPC.OpenInExplorer();
            menu.Items.Add(openPc);
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildRDockSettingsSubMenu());
            return menu;
        }

        if (item.IsClock)
        {
            menu.Items.Add(BuildRDockSettingsSubMenu());
            return menu;
        }

        if (item.IsSeparator)
        {
            var removeSep = new MenuItem { Header = "移除分隔線", Tag = item };
            removeSep.Click += MenuRemove_Click;
            menu.Items.Add(removeSep);
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildRDockSettingsSubMenu());
            return menu;
        }

        if (ProcessWatcher.CanTrackAsProcess(item.TargetPath) &&
            DockManager.SharedProcessWatcher.IsRunning(item.TargetPath))
        {
            // 平鋪視窗清單，避免第三層子選單
            AppendWindowMenuItems(menu.Items, item);
            menu.Items.Add(new Separator());
        }

        var openLocation = new MenuItem { Header = "開啟檔案位置", Tag = item };
        openLocation.Click += MenuOpenLocation_Click;

        var customIcon = new MenuItem { Header = "自訂圖示…", Tag = item };
        customIcon.Click += MenuCustomIcon_Click;

        var remove = new MenuItem { Header = "從 Dock 移除", Tag = item };
        remove.Click += MenuRemove_Click;

        menu.Items.Add(openLocation);
        menu.Items.Add(customIcon);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildRDockSettingsSubMenu());
        return menu;
    }

    private void AppendWindowMenuItems(ItemCollection items, DockItem item)
    {
        var windows = WindowFocusHelper.ListWindows(item.TargetPath);
        if (windows.Count == 0)
        {
            items.Add(new MenuItem { Header = "視窗：（無可見視窗）", IsEnabled = false });
            return;
        }

        int added = 0;
        foreach (var (hwnd, title) in windows)
        {
            if (added >= 10)
                break;

            string shortTitle = title.Length > 40 ? title[..37] + "…" : title;
            var entry = new MenuItem { Header = "視窗：" + shortTitle, Tag = hwnd };
            entry.Click += (_, _) => WindowFocusHelper.ActivateWindow(hwnd);
            items.Add(entry);
            added++;
        }
    }

    private void WindowsSubmenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: DockItem item } menu)
            return;

        menu.Items.Clear();
        AppendWindowMenuItems(menu.Items, item);
    }

    /// <summary>圖示右鍵用的「RDock 設定」子選單（單層項目，避免三層巢狀無法展開）。</summary>
    private MenuItem BuildRDockSettingsSubMenu()
    {
        var root = new MenuItem { Header = "RDock 設定" };
        root.SubmenuOpened += (_, _) => RebuildRDockSettingsSubMenu(root);
        RebuildRDockSettingsSubMenu(root);
        return root;
    }

    private void RebuildRDockSettingsSubMenu(MenuItem root)
    {
        root.Items.Clear();

        var lockItem = new MenuItem
        {
            Header = "鎖定項目（禁止拖曳改變順序）",
            IsCheckable = true,
            IsChecked = _isLocked
        };
        lockItem.Click += MenuLockItems_Click;

        var autoHide = new MenuItem
        {
            Header = "自動隱藏",
            IsCheckable = true,
            IsChecked = _autoHideEnabled
        };
        autoHide.Click += MenuAutoHide_Click;

        var autoStart = new MenuItem
        {
            Header = "開機自動啟動",
            IsCheckable = true,
            IsChecked = AutostartHelper.IsEnabled()
        };
        autoStart.Click += MenuAutoStart_Click;

        root.Items.Add(lockItem);
        root.Items.Add(autoHide);
        root.Items.Add(autoStart);
        root.Items.Add(new Separator());

        var more = new MenuItem { Header = "更多設定…" };
        more.Click += MenuOpenSettings_Click;
        root.Items.Add(more);
        root.Items.Add(new Separator());

        // 螢幕選項平鋪（不再做第三層子選單）
        var allScreens = new MenuItem
        {
            Header = "螢幕：所有螢幕",
            IsCheckable = true,
            IsChecked = _preferredScreenIndex == DockConfig.AllScreens,
            Tag = DockConfig.AllScreens
        };
        allScreens.Click += MenuDockScreenItem_Click;
        root.Items.Add(allScreens);

        foreach (var monitor in MonitorService.GetMonitors())
        {
            var item = new MenuItem
            {
                Header = "螢幕：" + MonitorService.Describe(monitor),
                IsCheckable = true,
                IsChecked = _preferredScreenIndex == monitor.Index,
                Tag = monitor.Index
            };
            item.Click += MenuDockScreenItem_Click;
            root.Items.Add(item);
        }

        root.Items.Add(new Separator());

        foreach (DockEdge edge in Enum.GetValues<DockEdge>())
        {
            var item = new MenuItem
            {
                Header = "位置：" + edge.ToDisplayName(),
                IsCheckable = true,
                IsChecked = edge == _dockEdge,
                Tag = edge
            };
            item.Click += MenuDockEdgeItem_Click;
            root.Items.Add(item);
        }

        root.Items.Add(new Separator());

        var version = new MenuItem
        {
            Header = AppInfo.VersionMenuHeader,
            IsEnabled = false
        };
        root.Items.Add(version);

        var about = new MenuItem { Header = "關於我" };
        about.Click += MenuAbout_Click;
        about.PreviewMouseLeftButtonUp += MenuAbout_PreviewMouseLeftButtonUp;
        root.Items.Add(about);

        var exit = new MenuItem { Header = "結束程式" };
        exit.Click += MenuExit_Click;
        root.Items.Add(exit);
    }

    private void AppendAppearanceMenuItems(ItemCollection items)
    {
        // 全部平鋪成「分類：選項」，避免 ContextMenu → 設定 → 分類 → 值 的第三層無法展開
        AppendChoiceItems(items, "圖示大小",
            [40, 48, 56, 64],
            v => (int)_iconSize == v,
            v => ApplyIconSize(v),
            v => $"{v}");

        AppendChoiceItems(items, "魚眼倍率",
            [1.5, 1.8, 2.0],
            v => Math.Abs(_maxScale - v) < 0.01,
            v => ApplyMaxScale(v),
            v => $"{v:0.0}×");

        AppendChoiceItems(items, "隱藏延遲",
            [400, 800, 1500],
            v => (int)_hideDelay.TotalMilliseconds == v,
            v => ApplyHideDelay(v),
            v => $"{v} ms");

        AppendChoiceItems(items, "不透明度",
            [60, 80, 90],
            v => (int)Math.Round(_dockOpacity * 100) == v,
            v => ApplyDockOpacity(v / 100.0),
            v => $"{v}%");

        var recycle = new MenuItem
        {
            Header = "顯示資源回收筒",
            IsCheckable = true,
            IsChecked = _showRecycleBin
        };
        recycle.Click += MenuShowRecycleBin_Click;
        items.Add(recycle);

        var thisPc = new MenuItem
        {
            Header = "顯示本機",
            IsCheckable = true,
            IsChecked = _showThisPC
        };
        thisPc.Click += MenuShowThisPC_Click;
        items.Add(thisPc);

        var clock = new MenuItem
        {
            Header = "顯示時鐘",
            IsCheckable = true,
            IsChecked = _showClock
        };
        clock.Click += MenuShowClock_Click;
        items.Add(clock);

        var appBar = new MenuItem
        {
            Header = "保留工作區（最大化不蓋住 Dock）",
            IsCheckable = true,
            IsChecked = _enableAppBar
        };
        appBar.Click += MenuEnableAppBar_Click;
        items.Add(appBar);

        var push = new MenuItem
        {
            Header = "魚眼推擠",
            IsCheckable = true,
            IsChecked = _fisheyePush
        };
        push.Click += MenuFisheyePush_Click;
        items.Add(push);
    }

    private void AppendChoiceItems<T>(
        ItemCollection items,
        string category,
        T[] values,
        Func<T, bool> isCurrent,
        Action<T> apply,
        Func<T, string> format)
    {
        foreach (T value in values)
        {
            var item = new MenuItem
            {
                Header = $"{category}：{format(value)}",
                IsCheckable = true,
                IsChecked = isCurrent(value),
                Tag = value
            };
            item.Click += (_, _) => apply(value);
            items.Add(item);
        }
    }

    private void AppendPinRunningMenuItems(ItemCollection items)
    {
        var apps = DockManager.SharedProcessWatcher.GetRunningAppsSnapshot();
        if (apps.Count == 0)
        {
            items.Add(new MenuItem { Header = "釘選：（目前沒有可釘選程式）", IsEnabled = false });
            return;
        }

        int added = 0;
        foreach (var app in apps)
        {
            if (added >= 12)
                break;

            bool already = _items.Any(i =>
                !i.IsDocklet &&
                string.Equals(
                    ProcessWatcher.TryNormalizePath(i.TargetPath),
                    ProcessWatcher.TryNormalizePath(app.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase));

            var entry = new MenuItem
            {
                Header = already ? $"釘選：{app.DisplayName}（已釘選）" : $"釘選：{app.DisplayName}",
                IsEnabled = !already,
                Tag = app.ExecutablePath
            };
            entry.Click += MenuPinRunningApp_Click;
            items.Add(entry);
            added++;
        }
    }

    private void PinRunningSubmenu_Opened(object sender, RoutedEventArgs e)
    {
        // 保留相容；現已改平鋪，不再使用巢狀子選單
        if (sender is not MenuItem menu)
            return;

        menu.Items.Clear();
        AppendPinRunningMenuItems(menu.Items);
    }

    private void MenuPinRunningApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string path } || string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            DockItem item = ShellIconHelper.CreateDockItemFromPath(path);
            int insertAt = _icons.Count;
            int recycleIdx = _items.FindIndex(i => i.IsRecycleBin);
            int clockIdx = _items.FindIndex(i => i.IsClock);
            if (recycleIdx >= 0) insertAt = recycleIdx;
            else if (clockIdx >= 0) insertAt = clockIdx;

            InsertDockItem(item, insertAt, persist: true);
            _ = LoadSingleIconAsync(item);
            RecenterOnWorkArea();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"無法釘選程式：\n{ex.Message}", "RDock",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task LoadSingleIconAsync(DockItem item)
    {
        try
        {
            string? before = item.IconCachePath;
            ImageSource? icon = await Task.Run(() => ShellIconHelper.ResolveIcon(item, IconDecodeSize));
            if (icon is null)
                return;

            item.Icon = icon;
            if (!string.Equals(before, item.IconCachePath, StringComparison.OrdinalIgnoreCase))
                ScheduleSave();

            Border? border = _icons.FirstOrDefault(b => GetItem(b)?.Id == item.Id);
            if (border is not null)
            {
                ApplyIconToBorder(border, item);
                RefreshRunningIndicators();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RDock] LoadSingleIcon failed: {ex.Message}");
        }
    }

    private static string BuildTooltip(DockItem item) => item.Title;

    private void Icon_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border host || GetItem(host) is not DockItem item)
            return;

        if (item.IsSeparator)
            return;

        _labeledIcon = host;
        ShowIconNameLabel(item.IsClock ? DateTime.Now.ToString("HH:mm") : item.Title);
        UpdateIconNameLabelPosition();
    }

    private void Icon_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border host && ReferenceEquals(_labeledIcon, host))
        {
            _labeledIcon = null;
            HideIconNameLabel();
        }
    }

    private void ShowIconNameLabel(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            HideIconNameLabel();
            return;
        }

        IconNameText.Text = title.Trim();
        IconNameBadge.Visibility = Visibility.Visible;
        IconNameBadge.UpdateLayout();
    }

    private void HideIconNameLabel()
    {
        IconNameBadge.Visibility = Visibility.Collapsed;
        IconNameTranslate.X = 0;
        IconNameTranslate.Y = 0;
    }

    private void UpdateIconNameLabelPosition()
    {
        if (_labeledIcon is null || IconNameBadge.Visibility != Visibility.Visible)
            return;

        IconNameBadge.UpdateLayout();
        double labelW = IconNameBadge.ActualWidth;
        double labelH = IconNameBadge.ActualHeight;
        if (labelW <= 0 || labelH <= 0)
            return;

        // 以圖示中心為基準，依停靠邊把名稱放在外側
        Point center = _labeledIcon.TranslatePoint(
            new Point(_labeledIcon.ActualWidth / 2, _labeledIcon.ActualHeight / 2),
            this);

        const double gap = 10;
        double x;
        double y;

        switch (_dockEdge)
        {
            case DockEdge.Top:
                x = center.X - labelW / 2;
                y = center.Y + _labeledIcon.ActualHeight * 0.55 + gap;
                break;
            case DockEdge.Left:
                x = center.X + _labeledIcon.ActualWidth * 0.55 + gap;
                y = center.Y - labelH / 2;
                break;
            case DockEdge.Right:
                x = center.X - _labeledIcon.ActualWidth * 0.55 - labelW - gap;
                y = center.Y - labelH / 2;
                break;
            default: // Bottom
                x = center.X - labelW / 2;
                y = center.Y - _labeledIcon.ActualHeight * 0.55 - labelH - gap;
                break;
        }

        // 避免超出視窗左右
        x = Math.Clamp(x, 4, Math.Max(4, ActualWidth - labelW - 4));
        y = Math.Max(2, y);

        IconNameTranslate.X = x;
        IconNameTranslate.Y = y;
    }

    // ══════════════════════════════════════════════════════════
    //  圖示拖曳重排 / 點擊啟動
    // ══════════════════════════════════════════════════════════

    private void Icon_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border)
            return;

        _dragSource = border;
        _dragStart = e.GetPosition(this);
        _suppressClick = false;
    }

    private void Icon_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isLocked || e.LeftButton != MouseButtonState.Pressed || _dragSource is null)
            return;

        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (_dragSource.Tag is not IconViewState state || state.Item.IsClock)
            return;

        _suppressClick = true;
        var data = new DataObject(DockItemDragFormat, state.Item.Id);
        DragDrop.DoDragDrop(_dragSource, data, DragDropEffects.Move);
        _dragSource = null;
    }

    private void Icon_Click(object sender, MouseButtonEventArgs e)
    {
        if (_suppressClick)
        {
            _suppressClick = false;
            return;
        }

        if (sender is not Border host || host.Tag is not IconViewState state)
            return;

        if (e.ChangedButton != MouseButton.Left)
            return;

        if (state.Item.IsSeparator || state.Item.IsClock)
            return;

        // 先播放彈跳，再依類型處理
        IconBounceAnimation.Play(state.Bounce);
        _ = ActivateDockItemAsync(state);
    }

    private async Task ActivateDockItemAsync(IconViewState state)
    {
        DockItem item = state.Item;

        if (item.IsSeparator || item.IsClock)
            return;

        if (item.IsRecycleBin)
        {
            await Task.Run(ShellRecycleBin.OpenInExplorer).ConfigureAwait(true);
            return;
        }

        if (item.IsThisPC)
        {
            await Task.Run(ShellThisPC.OpenInExplorer).ConfigureAwait(true);
            return;
        }

        if (item.IsFolder || item.Kind == DockItemKind.Folder)
        {
            ShowFolderStack(state);
            return;
        }

        await LaunchOrActivateAsync(item).ConfigureAwait(true);
    }

    private void ShowFolderStack(IconViewState state)
    {
        try
        {
            _folderStack?.Close();
        }
        catch
        {
            // ignore
        }

        _folderStack = null;

        string folder = state.Item.TargetPath;
        if (!Directory.Exists(folder))
        {
            MessageBox.Show("找不到此資料夾。", "RDock", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CancelHideTimer();
        ShowDock();

        Point topLeft = state.Host.PointToScreen(new Point(0, 0));
        Point bottomCenter = new(
            topLeft.X + state.Host.ActualWidth / 2.0,
            topLeft.Y);

        // PointToScreen 回傳的是裝置像素；WPF Window Left/Top 要 DIP
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        var anchorDip = new Point(bottomCenter.X / dpi.DpiScaleX, bottomCenter.Y / dpi.DpiScaleY);

        _folderStack = FolderStackWindow.ShowForFolder(folder, anchorDip, this);
        if (_folderStack is not null)
            _folderStack.Closed += (_, _) => _folderStack = null;
    }

    /// <summary>
    /// 智慧啟動：已在執行 → 背景尋窗後置前；否則 Process.Start。
    /// </summary>
    private async Task LaunchOrActivateAsync(DockItem item)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(item.TargetPath))
            {
                MessageBox.Show("此項目沒有有效的目標路徑。", "RDock",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ProcessWatcher.CanTrackAsProcess(item.TargetPath) &&
                DockManager.SharedProcessWatcher.IsRunning(item.TargetPath))
            {
                IntPtr hwnd = await Task.Run(() => WindowFocusHelper.FindMainWindow(item.TargetPath))
                    .ConfigureAwait(true);

                if (hwnd != IntPtr.Zero)
                {
                    WindowFocusHelper.ActivateWindow(hwnd);
                    return;
                }
            }

            string targetPath = item.TargetPath;
            string arguments = item.Arguments ?? string.Empty;
            string? preferredWorkDir = item.WorkingDirectory;

            await Task.Run(() =>
            {
                string? workingDirectory = null;
                if (!string.IsNullOrWhiteSpace(preferredWorkDir) &&
                    Directory.Exists(preferredWorkDir))
                {
                    workingDirectory = preferredWorkDir;
                }
                else if (File.Exists(targetPath))
                {
                    workingDirectory = Path.GetDirectoryName(targetPath);
                }

                var psi = new ProcessStartInfo
                {
                    FileName = targetPath,
                    Arguments = arguments,
                    UseShellExecute = true
                };

                if (!string.IsNullOrWhiteSpace(workingDirectory))
                    psi.WorkingDirectory = workingDirectory;

                Process.Start(psi);
            }).ConfigureAwait(true);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show(
                $"無法啟動「{item.Title}」\n\n{ex.Message}",
                "RDock",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"啟動失敗：{item.Title}\n\n{ex.Message}",
                "RDock",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // ── 拖放到特定圖示 ───────────────────────────────────────

    private void Icon_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DockItemDragFormat))
        {
            // 內部重排交給 Dock 容器處理
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            sender is not Border { Tag: IconViewState state })
        {
            return;
        }

        e.Effects = state.Item.IsRecycleBin
            ? DragDropEffects.Move
            : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Icon_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        if (sender is not Border { Tag: IconViewState state })
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
            return;

        e.Handled = true;
        CancelHideTimer();
        ShowDock();
        IconBounceAnimation.Play(state.Bounce);

        string[] valid = paths
            .Where(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
            .ToArray();

        if (valid.Length == 0)
            return;

        _ = HandleDropOnIconAsync(state.Item, valid);
    }

    private async Task HandleDropOnIconAsync(DockItem item, string[] paths)
    {
        try
        {
            if (item.IsRecycleBin)
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                bool ok = await Task.Run(() => ShellRecycleBin.SendToRecycleBin(paths, hwnd))
                    .ConfigureAwait(true);
                if (!ok)
                {
                    MessageBox.Show("無法將項目移至資源回收筒。", "RDock",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                await RefreshRecycleBinAsync().ConfigureAwait(true);
                return;
            }

            if (item.IsFolder || item.Kind == DockItemKind.Folder)
            {
                await Task.Run(() => CopyIntoFolder(item.TargetPath, paths)).ConfigureAwait(true);
                return;
            }

            // 應用程式：以拖入檔案作為參數啟動
            if (!File.Exists(item.TargetPath) && !ProcessWatcher.CanTrackAsProcess(item.TargetPath))
            {
                MessageBox.Show("此圖示不是可執行程式，無法用來開啟檔案。", "RDock",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await Task.Run(() =>
                DropLaunchHelper.LaunchWithFiles(item.TargetPath, item.Arguments, paths))
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"拖放處理失敗：\n{ex.Message}", "RDock",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void CopyIntoFolder(string folder, string[] paths)
    {
        Directory.CreateDirectory(folder);
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            string dest = Path.Combine(folder, name);

            if (Directory.Exists(path))
            {
                CopyDirectory(path, dest);
            }
            else if (File.Exists(path))
            {
                File.Copy(path, dest, overwrite: true);
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string target = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
        }

        foreach (string dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    private void MenuEmptyRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "確定要清空資源回收筒嗎？此操作無法復原。",
            "RDock",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        _ = Task.Run(() =>
        {
            ShellRecycleBin.Empty(hwnd, silent: false);
        }).ContinueWith(_ => Dispatcher.InvokeAsync(() => _ = RefreshRecycleBinAsync()));
    }

    // ══════════════════════════════════════════════════════════
    //  右鍵選單
    // ══════════════════════════════════════════════════════════

    private void DockContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        ContextMenu_OpenedKeepVisible(sender, e);
        MenuLockItems.IsChecked = _isLocked;
        MenuAutoHide.IsChecked = _autoHideEnabled;
        MenuAutoStart.IsChecked = AutostartHelper.IsEnabled();
        MenuVersion.Header = AppInfo.VersionMenuHeader;
        RebuildScreenMenu();
        RebuildEdgeMenu();
    }

    private bool _aboutDialogPending;

    private void MenuAbout_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowAboutDialogDeferred();
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ShowAboutDialogDeferred();
    }

    private void ShowAboutDialogDeferred()
    {
        if (_aboutDialogPending)
            return;

        _aboutDialogPending = true;
        CloseAllContextMenus(hideDockIfAway: false);

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                MessageBox.Show(
                    AppInfo.AboutMessage,
                    AppInfo.AboutTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            finally
            {
                _aboutDialogPending = false;
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private void RebuildScreenMenu()
    {
        MenuDockScreen.Items.Clear();
        PopulateScreenMenuItems(MenuDockScreen.Items);
    }

    private void RebuildEdgeMenu()
    {
        MenuDockEdge.Items.Clear();
        PopulateEdgeMenuItems(MenuDockEdge.Items);
    }

    private void PopulateScreenMenuItems(ItemCollection items)
    {
        var allItem = new MenuItem
        {
            Header = "所有螢幕",
            IsCheckable = true,
            IsChecked = _preferredScreenIndex == DockConfig.AllScreens,
            Tag = DockConfig.AllScreens
        };
        allItem.Click += MenuDockScreenItem_Click;
        items.Add(allItem);
        items.Add(new Separator());

        var monitors = MonitorService.GetMonitors();
        foreach (var monitor in monitors)
        {
            var item = new MenuItem
            {
                Header = MonitorService.Describe(monitor),
                IsCheckable = true,
                IsChecked = _preferredScreenIndex == monitor.Index,
                Tag = monitor.Index
            };
            item.Click += MenuDockScreenItem_Click;
            items.Add(item);
        }

        if (monitors.Count == 0)
            items.Add(new MenuItem { Header = "（無可用螢幕）", IsEnabled = false });
    }

    private void PopulateEdgeMenuItems(ItemCollection items)
    {
        foreach (DockEdge edge in Enum.GetValues<DockEdge>())
        {
            var item = new MenuItem
            {
                Header = edge.ToDisplayName(),
                IsCheckable = true,
                IsChecked = edge == _dockEdge,
                Tag = edge
            };
            item.Click += MenuDockEdgeItem_Click;
            items.Add(item);
        }
    }

    private void MenuDockScreenItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: int index })
            return;

        if (DockManager.IsRestarting)
            return;

        _preferredScreenIndex = index;
        _ = DockManager.RestartAsync(_preferredScreenIndex, _dockEdge);
    }

    private void MenuDockEdgeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: DockEdge edge })
            return;

        if (DockManager.IsRestarting)
            return;

        _dockEdge = edge;
        _ = DockManager.RestartAsync(_preferredScreenIndex, _dockEdge);
    }

    private void MenuLockItems_Click(object sender, RoutedEventArgs e)
    {
        bool locked = sender is MenuItem { IsCheckable: true } mi
            ? mi.IsChecked
            : MenuLockItems.IsChecked;

        _isLocked = locked;
        MenuLockItems.IsChecked = locked;
        ScheduleSave();
    }

    private void MenuAutoHide_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = sender is MenuItem { IsCheckable: true } mi
            ? mi.IsChecked
            : MenuAutoHide.IsChecked;

        _autoHideEnabled = enabled;
        MenuAutoHide.IsChecked = enabled;
        ScheduleSave();

        if (!_autoHideEnabled)
        {
            CancelHideTimer();
            ShowDock();
        }
        else if (!DockContainer.IsMouseOver && !HotEdge.IsMouseOver)
        {
            StartHideTimer();
        }
    }

    private void MenuAddSeparator_Click(object sender, RoutedEventArgs e)
    {
        if (_isLocked)
        {
            MessageBox.Show("Dock 已鎖定，請先解除鎖定再新增分隔線。", "RDock",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int insertAt = _icons.Count;
        int recycleIdx = _items.FindIndex(i => i.IsRecycleBin);
        int clockIdx = _items.FindIndex(i => i.IsClock);
        if (recycleIdx >= 0) insertAt = recycleIdx;
        else if (clockIdx >= 0) insertAt = clockIdx;

        InsertDockItem(CreateSeparatorDockItem(), insertAt, persist: true);
        RecenterOnWorkArea();
    }


    // ── 設定視窗 API ─────────────────────────────────────────

    public double SettingsIconSize => _iconSize;
    public double SettingsMaxScale => _maxScale;
    public int SettingsHideDelayMs => (int)_hideDelay.TotalMilliseconds;
    public double SettingsDockOpacity => _dockOpacity;
    public bool SettingsShowRecycleBin => _showRecycleBin;
    public bool SettingsShowThisPC => _showThisPC;
    public bool SettingsShowClock => _showClock;
    public bool SettingsEnableAppBar => _enableAppBar;
    public bool SettingsFisheyePush => _fisheyePush;

    public void SetIconSize(double size) => ApplyIconSize(size);
    public void SetMaxScale(double scale) => ApplyMaxScale(scale);
    public void SetHideDelay(int ms) => ApplyHideDelay(ms);
    public void SetDockOpacity(double opacity) => ApplyDockOpacity(opacity);

    public void SetShowRecycleBin(bool show)
    {
        if (_showRecycleBin == show) return;
        _showRecycleBin = show;
        EnsureRecycleBinItem();
        ScheduleSave();
        RecenterOnWorkArea();
    }

    public void SetShowThisPC(bool show)
    {
        if (_showThisPC == show) return;
        _showThisPC = show;
        EnsureThisPCItem();
        ScheduleSave();
        RecenterOnWorkArea();
    }

    public void SetShowClock(bool show)
    {
        if (_showClock == show) return;
        _showClock = show;
        EnsureClockItem();
        ScheduleSave();
        RecenterOnWorkArea();
    }

    public void SetEnableAppBar(bool enable)
    {
        if (_enableAppBar == enable) return;
        _enableAppBar = enable;
        ScheduleSave();
        _ = DockManager.RestartAsync(_preferredScreenIndex, _dockEdge);
    }

    public void SetFisheyePush(bool enabled)
    {
        _fisheyePush = enabled;
        ScheduleSave();
        foreach (Border icon in _icons)
            ApplySlide(icon, 0);
    }

    public IReadOnlyList<RunningAppInfo> GetPinCandidates() =>
        DockManager.SharedProcessWatcher.GetRunningAppsSnapshot();

    public bool IsPathPinned(string path)
    {
        string? norm = ProcessWatcher.TryNormalizePath(path);
        return _items.Any(i =>
            !i.IsDocklet &&
            string.Equals(ProcessWatcher.TryNormalizePath(i.TargetPath), norm, StringComparison.OrdinalIgnoreCase));
    }

    public void PinExecutable(string path)
    {
        try
        {
            if (IsPathPinned(path))
                return;

            DockItem item = ShellIconHelper.CreateDockItemFromPath(path);
            int insertAt = _icons.Count;
            int recycleIdx = _items.FindIndex(i => i.IsRecycleBin);
            int clockIdx = _items.FindIndex(i => i.IsClock);
            if (recycleIdx >= 0) insertAt = recycleIdx;
            else if (clockIdx >= 0) insertAt = clockIdx;

            InsertDockItem(item, insertAt, persist: true);
            _ = LoadSingleIconAsync(item);
            RecenterOnWorkArea();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"無法釘選程式：\n{ex.Message}", "RDock",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MenuOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseAllContextMenus(hideDockIfAway: false);
        Dispatcher.BeginInvoke(OpenSettingsWindow, DispatcherPriority.ApplicationIdle);
    }

    public void OpenSettingsWindow()
    {
        WindowActivationGuard.EnsureInteractive(this);

        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Owner = this;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void OnSettingsWindowClosed()
    {
        _settingsWindow = null;
        if (!IsAnyContextMenuReallyOpen())
            WindowActivationGuard.EndInteractive(this);
    }

    private void ApplyIconSize(double size)
    {
        size = Math.Clamp(size, 32, 72);
        if (Math.Abs(_iconSize - size) < 0.01)
            return;

        _iconSize = size;
        ScheduleSave();
        RebuildIconsLayout();
    }

    private void ApplyMaxScale(double scale)
    {
        scale = Math.Clamp(scale, 1.2, 2.4);
        if (Math.Abs(_maxScale - scale) < 0.01)
            return;

        _maxScale = scale;
        ApplyRenderTuning();
        ScheduleSave();
    }

    private void ApplyHideDelay(int ms)
    {
        ms = Math.Clamp(ms, 200, 5000);
        if ((int)_hideDelay.TotalMilliseconds == ms)
            return;

        _hideDelay = TimeSpan.FromMilliseconds(ms);
        _hideTimer.Interval = _hideDelay;
        ScheduleSave();
    }

    private void ApplyDockOpacity(double opacity)
    {
        opacity = Math.Clamp(opacity, 0.35, 0.95);
        if (Math.Abs(_dockOpacity - opacity) < 0.001)
            return;

        _dockOpacity = opacity;
        ApplyTheme();
        ScheduleSave();
    }

    private void MenuShowRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        bool show = sender is MenuItem { IsCheckable: true } mi && mi.IsChecked;
        _showRecycleBin = show;
        EnsureRecycleBinItem();
        ScheduleSave();
        RecenterOnWorkArea();
    }

    private void MenuShowThisPC_Click(object sender, RoutedEventArgs e)
    {
        bool show = sender is MenuItem { IsCheckable: true } mi && mi.IsChecked;
        _showThisPC = show;
        EnsureThisPCItem();
        ScheduleSave();
        RecenterOnWorkArea();
    }

    private void MenuShowClock_Click(object sender, RoutedEventArgs e)
    {
        bool show = sender is MenuItem { IsCheckable: true } mi && mi.IsChecked;
        _showClock = show;
        EnsureClockItem();
        ScheduleSave();
        RecenterOnWorkArea();
    }

    private void MenuEnableAppBar_Click(object sender, RoutedEventArgs e)
    {
        bool enable = sender is MenuItem { IsCheckable: true } mi && mi.IsChecked;
        if (_enableAppBar == enable)
            return;

        _enableAppBar = enable;
        ScheduleSave();
        _ = DockManager.RestartAsync(_preferredScreenIndex, _dockEdge);
    }

    private void MenuFisheyePush_Click(object sender, RoutedEventArgs e)
    {
        _fisheyePush = sender is MenuItem { IsCheckable: true } mi && mi.IsChecked;
        ScheduleSave();
        foreach (Border icon in _icons)
            ApplySlide(icon, 0);
    }

    /// <summary>圖示大小變更後重建所有圖示外框。</summary>
    private void RebuildIconsLayout()
    {
        var snapshot = _items.Select(CloneForPersist).ToList();
        foreach (DockItem item in snapshot)
        {
            // 保留已載入圖示
            DockItem? live = _items.FirstOrDefault(i => i.Id == item.Id);
            if (live?.Icon is not null)
                item.Icon = live.Icon;
        }

        _icons.Clear();
        _items.Clear();
        IconPanel.Children.Clear();
        InvalidateIconCenters();

        foreach (DockItem item in snapshot)
            InsertDockItem(item, _icons.Count, persist: false);

        EnsureThisPCItem();
        EnsureRecycleBinItem();
        EnsureClockItem();
        RefreshRunningIndicators();
        RefreshClockTexts();
        RecenterOnWorkArea();
        ApplyRenderTuning();
        _ = LoadIconsAsync();
    }

    private void RemoveItemAt(int index, bool persist, bool allowDocklets)
    {
        if (index < 0 || index >= _items.Count)
            return;

        DockItem item = _items[index];
        if (!allowDocklets && (item.IsRecycleBin || item.IsThisPC))
            return;

        _items.RemoveAt(index);
        Border border = _icons[index];
        _icons.RemoveAt(index);
        IconPanel.Children.RemoveAt(index);

        if (!item.IsDocklet)
            DockConfigStore.TryDeleteIconCache(item.IconCachePath);

        if (persist)
            ScheduleSave();

        RecenterOnWorkArea();
        RefreshRunningIndicators();
        InvalidateIconCenters();
    }

    private void MenuAutoStart_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = sender is MenuItem { IsCheckable: true } mi
            ? mi.IsChecked
            : MenuAutoStart.IsChecked;

        try
        {
            AutostartHelper.SetEnabled(enabled);
            MenuAutoStart.IsChecked = enabled;
            ScheduleSave();
        }
        catch (Exception ex)
        {
            bool actual = AutostartHelper.IsEnabled();
            MenuAutoStart.IsChecked = actual;
            if (sender is MenuItem checkable)
                checkable.IsChecked = actual;

            MessageBox.Show(
                $"無法變更開機自動啟動設定：\n{ex.Message}",
                "RDock",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        // 必須非同步結束：Closing 裡 GetResult() 會在多視窗時卡死 UI
        _ = DockManager.ShutdownAsync();
    }

    private void MenuOpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: DockItem item })
            return;

        try
        {
            string path = item.SourcePath ?? item.TargetPath;
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
                return;
            }

            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
                return;
            }

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                return;
            }

            MessageBox.Show("找不到檔案位置。", "RDock", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"無法開啟檔案位置：\n{ex.Message}", "RDock",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void MenuCustomIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: DockItem item })
            return;

        var dialog = new OpenFileDialog
        {
            Title = "選擇自訂圖示",
            Filter = "影像檔|*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.gif|所有檔案|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            string oldCache = item.IconCachePath ?? string.Empty;
            string imported = await DockConfigStore.ImportCustomIconAsync(dialog.FileName, item.Id);
            item.IconCachePath = imported;

            ImageSource? icon = await Task.Run(() => DockConfigStore.LoadIconFromCache(imported));
            if (icon is null)
                icon = await Task.Run(() => ShellIconHelper.GetHighDpiIcon(imported));

            if (icon is not null)
            {
                item.Icon = icon;
                Border? border = _icons.FirstOrDefault(b => GetItem(b)?.Id == item.Id);
                if (border is not null)
                    ApplyIconToBorder(border, item);
            }

            if (!string.Equals(oldCache, imported, StringComparison.OrdinalIgnoreCase))
                DockConfigStore.TryDeleteIconCache(oldCache);

            ScheduleSave();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"無法套用自訂圖示：\n{ex.Message}", "RDock",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MenuRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: DockItem item })
            return;

        if (item.IsRecycleBin || item.IsThisPC || item.IsClock)
        {
            MessageBox.Show("此為固定 Docklet，請從設定開關顯示。", "RDock",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int index = _items.FindIndex(i => i.Id == item.Id);
        if (index < 0)
            return;

        RemoveItemAt(index, persist: true, allowDocklets: true);
    }

    // ══════════════════════════════════════════════════════════
    //  魚眼縮放（快取中心點、避免每幀清動畫）
    // ══════════════════════════════════════════════════════════

    private void InvalidateIconCenters()
    {
        _iconCenters = [];
        if (_fisheyeTracking)
            RebuildIconCenters();
    }

    private void BeginFisheyeTracking()
    {
        if (_fisheyeTracking)
            return;

        _fisheyeTracking = true;
        RebuildIconCenters();

        foreach (Border icon in _icons)
            ClearScaleAnimation(icon);
    }

    private void RebuildIconCenters()
    {
        if (_icons.Count == 0)
        {
            _iconCenters = [];
            return;
        }

        DockContainer.UpdateLayout();
        bool horizontal = _dockEdge.IsHorizontal();
        var centers = new double[_icons.Count];
        for (int i = 0; i < _icons.Count; i++)
        {
            Border icon = _icons[i];
            try
            {
                Point mid = horizontal
                    ? new Point(icon.ActualWidth / 2.0, 0)
                    : new Point(0, icon.ActualHeight / 2.0);
                Point mapped = icon.TransformToAncestor(DockContainer).Transform(mid);
                centers[i] = horizontal ? mapped.X : mapped.Y;
            }
            catch
            {
                centers[i] = (i + 0.5) * IconStep;
            }
        }

        _iconCenters = centers;
    }

    private void DockContainer_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isHidden || _isAnimating)
            return;

        if (!_fisheyeTracking)
            BeginFisheyeTracking();

        Point pos = e.GetPosition(DockContainer);
        double cursor = _dockEdge.IsHorizontal() ? pos.X : pos.Y;
        double[] centers = _iconCenters;
        int count = Math.Min(_icons.Count, centers.Length);
        if (count == 0)
            return;

        var scales = new double[count];
        for (int i = 0; i < count; i++)
        {
            DockItem? item = GetItem(_icons[i]);
            double distance = Math.Abs(cursor - centers[i]);

            // 只更新 InfluenceRadius 內的圖示；分隔線不參與魚眼
            if (item?.IsSeparator == true || distance >= InfluenceRadius)
            {
                scales[i] = 1.0;
                ApplyScale(_icons[i], 1.0);
                continue;
            }

            scales[i] = ComputeFisheyeScale(distance);
            ApplyScale(_icons[i], scales[i]);
        }

        if (_fisheyePush)
        {
            for (int i = 0; i < count; i++)
            {
                if (GetItem(_icons[i])?.IsSeparator == true)
                {
                    ApplySlide(_icons[i], 0);
                    continue;
                }

                double push = 0;
                for (int j = 0; j < count; j++)
                {
                    if (i == j || scales[j] <= 1.001)
                        continue;

                    double dir = centers[i] >= centers[j] ? 1.0 : -1.0;
                    push += dir * (scales[j] - 1.0) * _iconSize * 0.42;
                }

                ApplySlide(_icons[i], push);
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
                ApplySlide(_icons[i], 0);
        }

        // 魚眼縮放時同步更新名稱位置
        if (_labeledIcon is not null)
            UpdateIconNameLabelPosition();
    }

    private void DockContainer_MouseLeave(object sender, MouseEventArgs e)
    {
        _fisheyeTracking = false;
        _labeledIcon = null;
        HideIconNameLabel();

        foreach (var icon in _icons)
        {
            AnimateScaleTo(icon, 1.0);
            ApplySlide(icon, 0);
        }

        // 右鍵選單開啟時不要觸發自動隱藏，否則選單會卡住關不掉
        if (!IsAnyContextMenuOpen())
            StartHideTimer();
    }

    private double ComputeFisheyeScale(double distance)
    {
        if (distance >= InfluenceRadius)
            return 1.0;

        double normalized = distance / InfluenceRadius;
        double falloff = 0.5 * (1.0 + Math.Cos(Math.PI * normalized));
        return 1.0 + (_maxScale - 1.0) * falloff;
    }

    private static void ClearScaleAnimation(Border icon)
    {
        ScaleTransform? st = GetScaleTransform(icon);
        if (st is null)
            return;

        double x = st.ScaleX;
        double y = st.ScaleY;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        st.ScaleX = x;
        st.ScaleY = y;
    }

    private static void ApplyScale(Border icon, double scale)
    {
        ScaleTransform? st = GetScaleTransform(icon);
        if (st is null)
            return;

        // 略過微小變化，減少每幀無效寫入
        if (Math.Abs(st.ScaleX - scale) < 0.008 && Math.Abs(st.ScaleY - scale) < 0.008)
            return;

        st.ScaleX = scale;
        st.ScaleY = scale;
    }

    private void ApplySlide(Border icon, double offset)
    {
        if (icon.Tag is not IconViewState state)
            return;

        TranslateTransform slide = state.Slide;
        bool horizontal = _dockEdge.IsHorizontal();
        double targetX = horizontal ? offset : 0;
        double targetY = horizontal ? 0 : offset;

        if (Math.Abs(slide.X - targetX) < 0.2 && Math.Abs(slide.Y - targetY) < 0.2)
            return;

        slide.X = targetX;
        slide.Y = targetY;
    }

    private static void AnimateScaleTo(Border icon, double target)
    {
        ScaleTransform? st = GetScaleTransform(icon);
        if (st is null)
            return;

        var anim = new DoubleAnimation
        {
            To = target,
            Duration = new Duration(ResetDuration),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };

        anim.Completed += (_, _) =>
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            st.ScaleX = target;
            st.ScaleY = target;
        };

        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private static ScaleTransform? GetScaleTransform(Border icon)
    {
        if (icon.Tag is IconViewState state)
            return state.Scale;

        return icon.RenderTransform as ScaleTransform
               ?? (icon.RenderTransform as TransformGroup)?.Children.OfType<ScaleTransform>().FirstOrDefault();
    }
}
