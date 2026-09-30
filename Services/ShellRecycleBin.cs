using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RDock;

/// <summary>
/// 資源回收筒 Shell API 封裝：查詢狀態、丟入回收筒、清空、取得空/滿圖示。
/// </summary>
public static class ShellRecycleBin
{
    public const string ShellFolderPath = "shell:RecycleBinFolder";
    public const string ParsingName = @"::{645FF040-5081-101B-9F08-00AA002F954E}";

    public readonly record struct RecycleBinStatus(long ItemCount, long TotalBytes)
    {
        public bool HasItems => ItemCount > 0;
    }

    /// <summary>查詢回收筒檔案數與總大小。</summary>
    public static RecycleBinStatus QueryStatus()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        int hr = SHQueryRecycleBin(null, ref info);
        if (hr < 0)
            return new RecycleBinStatus(0, 0);

        return new RecycleBinStatus(info.i64NumItems, info.i64Size);
    }

    /// <summary>將檔案／資料夾送進資源回收筒（可還原）。</summary>
    public static bool SendToRecycleBin(IReadOnlyList<string> paths, IntPtr ownerHwnd = default)
    {
        if (paths.Count == 0)
            return false;

        // SHFileOperation 需要 double-null 結尾的路徑清單
        string from = string.Join("\0", paths.Where(p => !string.IsNullOrWhiteSpace(p))) + "\0\0";

        var op = new SHFILEOPSTRUCT
        {
            hwnd = ownerHwnd,
            wFunc = FO_DELETE,
            pFrom = from,
            pTo = null!,
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT),
            fAnyOperationsAborted = false,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = string.Empty
        };

        int result = SHFileOperation(ref op);
        return result == 0 && !op.fAnyOperationsAborted;
    }

    /// <summary>清空資源回收筒。</summary>
    public static bool Empty(IntPtr ownerHwnd = default, bool silent = false)
    {
        uint flags = SHERB_NOSOUND;
        if (silent)
            flags |= SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI;

        int hr = SHEmptyRecycleBin(ownerHwnd, null, flags);
        return hr >= 0;
    }

    public static void OpenInExplorer()
    {
        ProcessStartShell(ShellFolderPath);
    }

    /// <summary>
    /// 取得空／滿圖示。優先 Shell 解析名稱；失敗則退回 Stock Icon。
    /// </summary>
    public static ImageSource? GetIcon(bool isFull, int size = 256)
    {
        // Shell 項目圖示會隨回收筒狀態變化
        var shellIcon = ShellIconHelper.GetHighDpiIcon(ParsingName, size);
        if (shellIcon is not null)
            return shellIcon;

        return GetStockIcon(isFull ? SHSTOCKICONID.SIID_RECYCLERFULL : SHSTOCKICONID.SIID_RECYCLER);
    }

    private static ImageSource? GetStockIcon(SHSTOCKICONID id)
    {
        var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
        int hr = SHGetStockIconInfo(id, SHGSI_ICON | SHGSI_LARGEICON, ref info);
        if (hr < 0 || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            ImageSource source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static void ProcessStartShell(string target)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = target,
            UseShellExecute = true
        });
    }

    // ── Win32 ────────────────────────────────────────────────

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    private const uint SHGSI_ICON = 0x000000100;
    private const uint SHGSI_LARGEICON = 0x000000000;

    private enum SHSTOCKICONID : uint
    {
        SIID_RECYCLER = 31,
        SIID_RECYCLERFULL = 32
    }

    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysIconIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetStockIconInfo(SHSTOCKICONID siid, uint uFlags, ref SHSTOCKICONINFO psii);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
