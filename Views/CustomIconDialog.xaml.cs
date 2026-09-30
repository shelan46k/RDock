using System.Windows;
using Microsoft.Win32;

namespace RDock;

public enum CustomIconChoice
{
    Cancel = 0,
    SystemIcons = 1,
    ImageFile = 2,
    ResetDefault = 3
}

public partial class CustomIconDialog : Window
{
    public CustomIconChoice Choice { get; private set; } = CustomIconChoice.Cancel;
    public string? SelectedImagePath { get; private set; }

    private Window? _ownerRef;

    public CustomIconDialog(bool canReset)
    {
        InitializeComponent();
        ResetButton.IsEnabled = canReset;
        ResetButton.Opacity = canReset ? 1.0 : 0.45;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) =>
        WindowPlacement.CenterOnScreen(this, Owner ?? _ownerRef);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _ownerRef = Owner;
    }

    private void SystemIcons_Click(object sender, RoutedEventArgs e)
    {
        Choice = CustomIconChoice.SystemIcons;
        DialogResult = true;
    }

    private void ImageFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "選擇自訂圖示影像",
            Filter = "影像檔|*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.gif|所有檔案|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        SelectedImagePath = dialog.FileName;
        Choice = CustomIconChoice.ImageFile;
        DialogResult = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!ResetButton.IsEnabled)
            return;

        Choice = CustomIconChoice.ResetDefault;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = CustomIconChoice.Cancel;
        DialogResult = false;
    }
}
