using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace RDock;

public enum DockItemKind
{
    Application = 0,
    Folder = 1,
    RecycleBin = 2,
    Separator = 3,
    Clock = 4
}

/// <summary>Dock 上的單一啟動項目（可序列化至 JSON）。</summary>
public sealed class DockItem
{
    public const string RecycleBinItemId = "docklet-recycle-bin";
    public const string ClockItemId = "docklet-clock";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    public string? Arguments { get; set; }

    public string? IconCachePath { get; set; }

    public string? WorkingDirectory { get; set; }

    public string? SourcePath { get; set; }

    public DockItemKind Kind { get; set; } = DockItemKind.Application;

    [JsonIgnore]
    public ImageSource? Icon { get; set; }

    public bool IsRecycleBin => Kind == DockItemKind.RecycleBin || Id == RecycleBinItemId;

    public bool IsClock => Kind == DockItemKind.Clock || Id == ClockItemId;

    public bool IsSeparator => Kind == DockItemKind.Separator;

    public bool IsDocklet => IsRecycleBin || IsClock || IsSeparator;

    public bool IsFolder =>
        Kind == DockItemKind.Folder ||
        (!IsDocklet && !string.IsNullOrWhiteSpace(TargetPath) && Directory.Exists(TargetPath));

    public override string ToString() => IsSeparator ? "—" : $"{Title} → {TargetPath}";
}

/// <summary>整個 Dock 的持久化設定。</summary>
public sealed class DockConfig
{
    /// <summary>所有螢幕（每個顯示器各一個 Dock）。</summary>
    public const int AllScreens = -1;

    public bool IsLocked { get; set; }

    public bool AutoStart { get; set; }

    /// <summary>>=0 單一螢幕；<see cref="AllScreens"/> 表示所有螢幕。</summary>
    public int ScreenIndex { get; set; }

    public DockEdge DockEdge { get; set; } = DockEdge.Bottom;

    /// <summary>是否自動隱藏。</summary>
    public bool AutoHideEnabled { get; set; } = true;

    /// <summary>圖示邊長（DIP），建議 32~72。</summary>
    public double IconSize { get; set; } = 48;

    /// <summary>魚眼最大倍率，建議 1.2~2.2。</summary>
    public double MaxScale { get; set; } = 1.8;

    /// <summary>滑出後隱藏延遲（毫秒）。</summary>
    public int HideDelayMs { get; set; } = 800;

    /// <summary>Dock 背景不透明度 0.35~0.95。</summary>
    public double DockOpacity { get; set; } = 0.80;

    /// <summary>是否顯示資源回收筒。</summary>
    public bool ShowRecycleBin { get; set; } = true;

    /// <summary>是否顯示時鐘 Docklet。</summary>
    public bool ShowClock { get; set; } = false;

    /// <summary>是否向系統登記 AppBar 保留工作區。</summary>
    public bool EnableAppBar { get; set; } = false;

    /// <summary>是否啟用魚眼鄰圖推擠。</summary>
    public bool FisheyePushEnabled { get; set; } = true;

    public List<DockItem> Items { get; set; } = [];

    public void Clamp()
    {
        IconSize = Math.Clamp(IconSize, 32, 72);
        MaxScale = Math.Clamp(MaxScale, 1.2, 2.4);
        HideDelayMs = Math.Clamp(HideDelayMs, 200, 5000);
        DockOpacity = Math.Clamp(DockOpacity, 0.35, 0.95);
    }
}
