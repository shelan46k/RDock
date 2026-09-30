using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RDock;

/// <summary>
/// 資料夾抽屜：網格／清單顯示，可拖曳；點空白處／Esc／關閉鈕可關閉。
/// </summary>
public partial class FolderStackWindow : Window
{
    private const int MaxItems = 48;
    private bool _closing;
    private bool _dragging;
    private bool _listMode;
    private List<FileSystemInfo> _entries = [];

    public FolderStackWindow()
    {
        InitializeComponent();
    }

    public static FolderStackWindow? ShowForFolder(string folderPath, Point iconTopCenterDip, Window? owner)
    {
        if (!Directory.Exists(folderPath))
            return null;

        var popup = new FolderStackWindow();
        if (owner is not null)
            popup.Owner = owner;

        popup.TitleText.Text = new DirectoryInfo(folderPath).Name;
        popup.LoadFiles(folderPath);

        double dpiX = 1, dpiY = 1;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(owner ?? popup);
            dpiX = dpi.DpiScaleX;
            dpiY = dpi.DpiScaleY;
        }
        catch
        {
            // ignore
        }

        Rect workDip = GetWorkAreaDipNear(iconTopCenterDip, dpiX, dpiY);

        // 放大視窗，但仍不超出工作區
        double width = Math.Min(620, Math.Max(420, workDip.Width * 0.45));
        double height = Math.Min(520, Math.Max(360, workDip.Height * 0.55));
        popup.Width = width;
        popup.Height = height;
        popup.Show();
        WindowPlacement.CenterOnScreen(popup, owner);

        try
        {
            popup.Activate();
            popup.Focus();
        }
        catch
        {
            // ignore
        }

        return popup;
    }

    private static Rect GetWorkAreaDipNear(Point dipPoint, double dpiX, double dpiY)
    {
        int px = (int)Math.Round(dipPoint.X * dpiX);
        int py = (int)Math.Round(dipPoint.Y * dpiY);

        IntPtr monitor = MonitorFromPoint(new POINT { X = px, Y = py }, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            return new Rect(
                info.rcWork.Left / dpiX,
                info.rcWork.Top / dpiY,
                (info.rcWork.Right - info.rcWork.Left) / dpiX,
                (info.rcWork.Bottom - info.rcWork.Top) / dpiY);
        }

        return SystemParameters.WorkArea;
    }

    private void LoadFiles(string folderPath)
    {
        _entries = [];
        ItemsHost.Children.Clear();

        try
        {
            var dir = new DirectoryInfo(folderPath);
            _entries = dir.EnumerateFileSystemInfos()
                .Where(i => (i.Attributes & FileAttributes.Hidden) == 0
                            && (i.Attributes & FileAttributes.System) == 0)
                .OrderByDescending(i => i.LastWriteTimeUtc)
                .Take(MaxItems)
                .ToList();
        }
        catch (Exception ex)
        {
            ItemsHost.Children.Add(new TextBlock
            {
                Text = $"無法讀取：{ex.Message}",
                Foreground = Brushes.OrangeRed,
                Margin = new Thickness(8),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        RebuildItems();
    }

    private void RebuildItems()
    {
        ItemsHost.Children.Clear();

        if (_entries.Count == 0)
        {
            ItemsHost.Children.Add(new TextBlock
            {
                Text = "此資料夾是空的",
                Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(8),
                FontSize = 14
            });
            return;
        }

        if (_listMode)
        {
            var list = new StackPanel();
            foreach (FileSystemInfo info in _entries)
                list.Children.Add(CreateListRow(info));
            ItemsHost.Children.Add(list);
        }
        else
        {
            var grid = new UniformGrid { Columns = 5 };
            foreach (FileSystemInfo info in _entries)
                grid.Children.Add(CreateTile(info));
            ItemsHost.Children.Add(grid);
        }

        ViewModeButton.Content = _listMode ? "網格" : "清單";
        ViewModeButton.ToolTip = _listMode ? "切換為網格檢視" : "切換為清單檢視";
        _ = LoadItemIconsAsync();
    }

    private Border CreateTile(FileSystemInfo info)
    {
        var image = new Image
        {
            Width = 48,
            Height = 48,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            Tag = "icon"
        };

        var label = new TextBlock
        {
            Text = info.Name,
            FontSize = 11,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 96,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { image, label }
        };

        var tile = new Border
        {
            Width = 104,
            Height = 104,
            Margin = new Thickness(6),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
            ToolTip = info.FullName,
            Tag = info,
            Child = panel
        };

        WireItemChrome(tile);
        return tile;
    }

    private Border CreateListRow(FileSystemInfo info)
    {
        var image = new Image
        {
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = "icon"
        };

        var label = new TextBlock
        {
            Text = info.Name,
            FontSize = 13,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(12, 0, 0, 0)
        };

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(image, Dock.Left);
        row.Children.Add(image);
        row.Children.Add(label);

        var tile = new Border
        {
            Height = 44,
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
            ToolTip = info.FullName,
            Tag = info,
            Child = row,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        WireItemChrome(tile);
        return tile;
    }

    private void WireItemChrome(Border tile)
    {
        tile.MouseLeftButtonUp += Item_Click;
        tile.MouseEnter += (_, _) =>
            tile.Background = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
        tile.MouseLeave += (_, _) =>
            tile.Background = new SolidColorBrush(
                _listMode
                    ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    }

    private async Task LoadItemIconsAsync()
    {
        if (ItemsHost.Children.Count == 0)
            return;

        IEnumerable<Border> tiles;
        if (ItemsHost.Children[0] is UniformGrid grid)
            tiles = grid.Children.OfType<Border>();
        else if (ItemsHost.Children[0] is StackPanel list)
            tiles = list.Children.OfType<Border>();
        else
            return;

        foreach (Border tile in tiles)
        {
            if (_closing)
                break;

            if (tile.Tag is not FileSystemInfo info)
                continue;

            Image? image = FindIconImage(tile);
            if (image is null)
                continue;

            ImageSource? icon = await Task.Run(() => ShellIconHelper.GetHighDpiIcon(info.FullName, 128));
            if (icon is null || _closing)
                continue;

            await Dispatcher.InvokeAsync(() =>
            {
                image.Source = icon;
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            });
        }
    }

    private static Image? FindIconImage(DependencyObject root)
    {
        if (root is Image { Tag: "icon" } img)
            return img;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            Image? found = FindIconImage(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
                return found;
        }

        // Logical children (before arranged)
        if (root is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                Image? found = FindIconImage(child);
                if (found is not null)
                    return found;
            }
        }
        else if (root is Border { Child: not null } border)
        {
            return FindIconImage(border.Child);
        }
        else if (root is ContentControl { Content: DependencyObject content })
        {
            return FindIconImage(content);
        }

        return null;
    }

    private void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: FileSystemInfo info })
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = info.FullName,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"無法開啟：\n{ex.Message}", "RDock",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CloseSafe();
    }

    private void ViewModeButton_Click(object sender, RoutedEventArgs e)
    {
        _listMode = !_listMode;
        RebuildItems();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        // 點到按鈕時不要拖曳
        if (e.OriginalSource is DependencyObject src &&
            (FindAncestor<Button>(src) is not null))
            return;

        try
        {
            _dragging = true;
            DragMove();
        }
        catch
        {
            // ignore
        }
        finally
        {
            _dragging = false;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseSafe();

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_dragging)
            return;
        CloseSafe();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseSafe();
        }
    }

    private void CloseSafe()
    {
        if (_closing)
            return;

        _closing = true;
        try
        {
            Close();
        }
        catch
        {
            // ignore
        }
    }

    // ── Win32 work area ──────────────────────────────────────

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

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
