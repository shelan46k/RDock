using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RDock;

/// <summary>進階設定視窗：外觀參數與釘選執行中程式。</summary>
public partial class SettingsWindow : Window
{
    private readonly MainWindow _dock;
    private bool _loading = true;

    public SettingsWindow(MainWindow dock)
    {
        _dock = dock;
        InitializeComponent();
        LoadFromDock();
        _loading = false;
    }

    private void LoadFromDock()
    {
        ComboIconSize.ItemsSource = new[] { 40, 48, 56, 64 };
        ComboIconSize.SelectedItem = (int)_dock.SettingsIconSize;

        ComboMaxScale.ItemsSource = new[] { "1.5×", "1.8×", "2.0×" };
        ComboMaxScale.SelectedIndex = _dock.SettingsMaxScale switch
        {
            <= 1.6 => 0,
            <= 1.9 => 1,
            _ => 2
        };

        ComboHideDelay.ItemsSource = new[] { "400 ms", "800 ms", "1500 ms" };
        ComboHideDelay.SelectedIndex = (int)_dock.SettingsHideDelayMs switch
        {
            <= 500 => 0,
            <= 1000 => 1,
            _ => 2
        };

        SliderOpacity.Value = Math.Round(_dock.SettingsDockOpacity * 100);
        OpacityValueText.Text = $"{(int)SliderOpacity.Value}%";

        CheckRecycleBin.IsChecked = _dock.SettingsShowRecycleBin;
        CheckClock.IsChecked = _dock.SettingsShowClock;
        CheckAppBar.IsChecked = _dock.SettingsEnableAppBar;
        CheckFisheyePush.IsChecked = _dock.SettingsFisheyePush;

        RefreshRunningApps();
    }

    private void RefreshRunningApps()
    {
        ListRunningApps.Items.Clear();
        foreach (var app in _dock.GetPinCandidates())
        {
            bool pinned = _dock.IsPathPinned(app.ExecutablePath);
            ListRunningApps.Items.Add(new RunningAppListItem(
                app.DisplayName,
                app.ExecutablePath,
                pinned));
        }
    }

    private void ComboIconSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ComboIconSize.SelectedItem is not int size)
            return;
        _dock.SetIconSize(size);
    }

    private void ComboMaxScale_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;

        double scale = ComboMaxScale.SelectedIndex switch
        {
            0 => 1.5,
            1 => 1.8,
            _ => 2.0
        };
        _dock.SetMaxScale(scale);
    }

    private void ComboHideDelay_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;

        int ms = ComboHideDelay.SelectedIndex switch
        {
            0 => 400,
            1 => 800,
            _ => 1500
        };
        _dock.SetHideDelay(ms);
    }

    private void SliderOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _loading)
            return;

        OpacityValueText.Text = $"{(int)SliderOpacity.Value}%";
        _dock.SetDockOpacity(SliderOpacity.Value / 100.0);
    }

    private void CheckFlags_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        _dock.SetShowRecycleBin(CheckRecycleBin.IsChecked == true);
        _dock.SetShowClock(CheckClock.IsChecked == true);
        _dock.SetEnableAppBar(CheckAppBar.IsChecked == true);
        _dock.SetFisheyePush(CheckFisheyePush.IsChecked == true);
    }

    private void PinSelected_Click(object sender, RoutedEventArgs e) => PinSelected();

    private void ListRunningApps_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PinSelected();

    private void PinSelected()
    {
        if (ListRunningApps.SelectedItem is not RunningAppListItem item)
            return;
        if (item.IsPinned)
            return;

        _dock.PinExecutable(item.ExecutablePath);
        RefreshRunningApps();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Window_Closed(object? sender, EventArgs e) =>
        _dock.OnSettingsWindowClosed();
}

internal sealed class RunningAppListItem
{
    public string DisplayName { get; }
    public string ExecutablePath { get; }
    public bool IsPinned { get; }

    public RunningAppListItem(string displayName, string executablePath, bool isPinned)
    {
        DisplayName = displayName;
        ExecutablePath = executablePath;
        IsPinned = isPinned;
    }

    public override string ToString() =>
        IsPinned ? $"{DisplayName}（已釘選）" : DisplayName;
}
