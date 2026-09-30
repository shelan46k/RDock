using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RDock;

/// <summary>
/// Windows 系統「變更圖示」對話框（PickIconDlg）與圖示擷取。
/// </summary>
public static class ShellIconPicker
{
    /// <summary>
    /// 開啟系統圖示挑選視窗。預設從 imageres.dll 列出圖示，也可在對話框內瀏覽其他檔案。
    /// </summary>
    public static bool TryPick(Window owner, out string iconFile, out int iconIndex)
    {
        iconFile = string.Empty;
        iconIndex = 0;

        string initial = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "imageres.dll");
        if (!File.Exists(initial))
            initial = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "shell32.dll");

        var pathBuf = new StringBuilder(initial, 260);
        int index = 0;

        IntPtr hwnd = owner is null
            ? IntPtr.Zero
            : new WindowInteropHelper(owner).Handle;

        if (!PickIconDlg(hwnd, pathBuf, (uint)pathBuf.Capacity, ref index))
            return false;

        iconFile = pathBuf.ToString().Trim().Trim('"');
        iconIndex = index;
        return !string.IsNullOrWhiteSpace(iconFile);
    }

    public static BitmapSource? ExtractIcon(string iconFile, int iconIndex, int size = 256)
    {
        if (string.IsNullOrWhiteSpace(iconFile) || !File.Exists(iconFile))
            return null;

        IntPtr[] icons = new IntPtr[1];
        uint[] ids = new uint[1];

        try
        {
            uint extracted = PrivateExtractIcons(
                iconFile,
                iconIndex,
                size,
                size,
                icons,
                ids,
                1,
                0);

            if (extracted == 0 || icons[0] == IntPtr.Zero)
            {
                // 部分檔案用 ExtractIconEx 較穩
                extracted = ExtractIconEx(iconFile, iconIndex, icons, null!, 1);
                if (extracted == uint.MaxValue || icons[0] == IntPtr.Zero)
                    return null;
            }

            var raw = Imaging.CreateBitmapSourceFromHIcon(
                icons[0],
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            // 去掉透明邊並放大到目標尺寸，避免「格子很大、圖示很小」
            var normalized = IconImageHelper.NormalizeToSquare(raw, size);
            normalized.Freeze();
            return normalized;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (icons[0] != IntPtr.Zero)
                DestroyIcon(icons[0]);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PickIconDlg(
        IntPtr hwnd,
        StringBuilder pszIconPath,
        uint cchIconPath,
        ref int piIconIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIcons(
        string szFileName,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        [Out] IntPtr[] phicon,
        [Out] uint[] piconid,
        uint nIcons,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string lpszFile,
        int nIconIndex,
        [Out] IntPtr[]? phiconLarge,
        [Out] IntPtr[]? phiconSmall,
        uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
