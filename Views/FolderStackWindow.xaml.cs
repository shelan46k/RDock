using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RDock;

/// <summary>
/// 資料夾抽屜：網格顯示最近修改的檔案，點擊開啟，失焦自動關閉。
/// </summary>
public partial class FolderStackWindow : Window
{
    private const int MaxItems = 32;
    private bool _closing;

    public FolderStackWindow()
    {
        InitializeComponent();
        ShowActivated = false;
    }

    public static FolderStackWindow? ShowForFolder(string folderPath, Point screenAnchorBottomCenter, Window? owner)
    {
        if (!Directory.Exists(folderPath))
            return null;

        var popup = new FolderStackWindow();
        if (owner is not null)
            popup.Owner = owner;

        popup.TitleText.Text = new DirectoryInfo(folderPath).Name;
        popup.LoadFiles(folderPath);

        popup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        popup.Arrange(new Rect(popup.DesiredSize));
        popup.UpdateLayout();

        double width = popup.DesiredSize.Width;
        double height = popup.DesiredSize.Height;
        popup.Left = screenAnchorBottomCenter.X - width / 2.0;
        popup.Top = screenAnchorBottomCenter.Y - height - 8;

        popup.Show();
        // 不主動 Activate，避免搶走前景焦點（ShowActivated=false）
        return popup;
    }

    private void LoadFiles(string folderPath)
    {
        FileGrid.Children.Clear();

        List<FileSystemInfo> entries;
        try
        {
            var dir = new DirectoryInfo(folderPath);
            entries = dir.EnumerateFileSystemInfos()
                .Where(i => (i.Attributes & FileAttributes.Hidden) == 0
                            && (i.Attributes & FileAttributes.System) == 0)
                .OrderByDescending(i => i.LastWriteTimeUtc)
                .Take(MaxItems)
                .ToList();
        }
        catch (Exception ex)
        {
            FileGrid.Children.Add(new TextBlock
            {
                Text = $"無法讀取：{ex.Message}",
                Foreground = Brushes.OrangeRed,
                Margin = new Thickness(8),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        if (entries.Count == 0)
        {
            FileGrid.Children.Add(new TextBlock
            {
                Text = "此資料夾是空的",
                Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(8)
            });
            return;
        }

        foreach (FileSystemInfo info in entries)
            FileGrid.Children.Add(CreateTile(info));

        // 背景補圖示，避免阻塞彈出
        _ = LoadTileIconsAsync(entries);
    }

    private Border CreateTile(FileSystemInfo info)
    {
        var image = new Image
        {
            Width = 40,
            Height = 40,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true
        };

        var label = new TextBlock
        {
            Text = info.Name,
            FontSize = 10,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 78,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { image, label }
        };

        var tile = new Border
        {
            Width = 88,
            Height = 88,
            Margin = new Thickness(4),
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
            ToolTip = info.FullName,
            Tag = info,
            Child = panel
        };

        tile.MouseLeftButtonUp += Tile_Click;
        tile.MouseEnter += (_, _) =>
            tile.Background = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
        tile.MouseLeave += (_, _) =>
            tile.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

        return tile;
    }

    private async Task LoadTileIconsAsync(List<FileSystemInfo> entries)
    {
        for (int i = 0; i < entries.Count && i < FileGrid.Children.Count; i++)
        {
            if (_closing)
                break;

            FileSystemInfo info = entries[i];
            if (FileGrid.Children[i] is not Border { Child: StackPanel panel })
                continue;

            if (panel.Children[0] is not Image image)
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

    private void Tile_Click(object sender, MouseButtonEventArgs e)
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

    private void Window_Deactivated(object? sender, EventArgs e) => CloseSafe();

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
}
