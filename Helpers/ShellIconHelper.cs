using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RDock;

/// <summary>
/// Win32 Shell 封裝：高解析度圖示提取 + .lnk 捷徑解析。
/// 優先使用 IShellItemImageFactory（可取 128/256），避免 Icon.ExtractAssociatedIcon 的 32×32 模糊。
/// </summary>
public static class ShellIconHelper
{
    private const int DefaultIconSize = 256;

    /// <summary>
    /// 取得路徑對應的高 DPI Shell 圖示，轉為可供 WPF Image 使用的 BitmapSource。
    /// </summary>
    /// <param name="path">檔案、資料夾、.exe 或 .lnk 路徑。</param>
    /// <param name="size">請求邊長（像素），建議 128 或 256。</param>
    public static BitmapSource? GetHighDpiIcon(string path, int size = DefaultIconSize)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        size = Math.Max(48, size);

        try
        {
            // 捷徑：優先用捷徑內指定的圖示檔＋索引（跟桌面顯示一致）
            if (IsShortcut(path) && TryResolveShortcut(path, out var shortcut))
            {
                BitmapSource? fromShortcutIcon = null;
                if (!string.IsNullOrWhiteSpace(shortcut.IconPath) && File.Exists(shortcut.IconPath))
                {
                    fromShortcutIcon = ShellIconPicker.ExtractIcon(
                        shortcut.IconPath, shortcut.IconIndex, size);
                }

                // 沒有自訂圖示路徑時，直接從 .lnk 擷取
                fromShortcutIcon ??= ShellIconPicker.ExtractIcon(path, 0, size);

                if (fromShortcutIcon is not null)
                    return IconImageHelper.NormalizeToSquare(fromShortcutIcon, size);

                if (!string.IsNullOrWhiteSpace(shortcut.TargetPath))
                {
                    var fromTarget = ExtractViaShellItemImageFactory(shortcut.TargetPath, size);
                    if (fromTarget is not null)
                        return IconImageHelper.NormalizeToSquare(fromTarget, size);
                }
            }

            var bitmap = ExtractViaShellItemImageFactory(path, size);
            if (bitmap is not null)
                return IconImageHelper.NormalizeToSquare(bitmap, size);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ShellIconHelper] GetHighDpiIcon failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 解析 .lnk 捷徑，取得真實目標路徑、參數與工作目錄。
    /// </summary>
    public static bool TryResolveShortcut(string lnkPath, out ShortcutInfo info)
    {
        info = default;

        if (!IsShortcut(lnkPath) || !File.Exists(lnkPath))
            return false;

        IShellLinkW? link = null;
        IPersistFile? persist = null;

        try
        {
            link = (IShellLinkW)new ShellLinkCoClass();
            persist = (IPersistFile)link;
            persist.Load(lnkPath, 0);

            var pathBuf = new StringBuilder(260);
            var argsBuf = new StringBuilder(1024);
            var dirBuf = new StringBuilder(260);
            var descBuf = new StringBuilder(260);

            // SLGP_RAWPATH = 0x4：盡量回傳未展開的原始路徑
            link.GetPath(pathBuf, pathBuf.Capacity, IntPtr.Zero, 0);
            link.GetArguments(argsBuf, argsBuf.Capacity);
            link.GetWorkingDirectory(dirBuf, dirBuf.Capacity);
            link.GetDescription(descBuf, descBuf.Capacity);

            var iconBuf = new StringBuilder(260);
            link.GetIconLocation(iconBuf, iconBuf.Capacity, out int iconIndex);

            info = new ShortcutInfo(
                TargetPath: pathBuf.ToString(),
                Arguments: argsBuf.ToString(),
                WorkingDirectory: dirBuf.ToString(),
                Description: descBuf.ToString(),
                IconPath: iconBuf.ToString(),
                IconIndex: iconIndex);

            return !string.IsNullOrWhiteSpace(info.TargetPath) ||
                   !string.IsNullOrWhiteSpace(info.IconPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ShellIconHelper] ResolveShortcut failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (persist is not null)
                Marshal.ReleaseComObject(persist);
            if (link is not null)
                Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>
    /// 從拖入路徑建立 DockItem（僅解析路徑／捷徑／Shell 虛擬項目，圖示交由非同步載入）。
    /// </summary>
    public static DockItem CreateDockItemFromPath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

        string title = GetFriendlyName(path);
        string targetPath = path;
        string? arguments = null;
        string? workingDirectory = null;
        string sourcePath = path;

        if (IsShortcut(path) && TryResolveShortcut(path, out var shortcut))
        {
            targetPath = shortcut.TargetPath;
            arguments = NullIfEmpty(shortcut.Arguments);
            workingDirectory = NullIfEmpty(shortcut.WorkingDirectory);

            if (!string.IsNullOrWhiteSpace(shortcut.Description))
                title = shortcut.Description!;
        }
        else if (IsShellItemPath(path))
        {
            targetPath = path;
            title = TryGetShellDisplayName(path) ?? title;
        }
        else if (Directory.Exists(path))
        {
            targetPath = path;
            workingDirectory = path;
        }
        else if (File.Exists(path))
        {
            targetPath = path;
            workingDirectory = Path.GetDirectoryName(path);
        }

        var kind = Directory.Exists(targetPath) && !File.Exists(targetPath)
            ? DockItemKind.Folder
            : DockItemKind.Application;

        // 捷徑若指向資料夾
        if (kind == DockItemKind.Application && Directory.Exists(targetPath))
            kind = DockItemKind.Folder;

        return new DockItem
        {
            Title = title,
            TargetPath = targetPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            SourcePath = sourcePath,
            Kind = kind
        };
    }

    /// <summary>Shell 命名空間路徑（本機、控制台等），非一般檔案系統路徑。</summary>
    public static bool IsShellItemPath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (path.StartsWith("::", StringComparison.Ordinal) ||
         path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("::{", StringComparison.Ordinal));

    public static string? TryGetShellDisplayName(string parsingName)
    {
        object? shellItemObj = null;
        try
        {
            var iid = typeof(IShellItem).GUID;
            int hr = SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out shellItemObj);
            if (hr < 0 || shellItemObj is null)
                return null;

            var item = (IShellItem)shellItemObj;
            item.GetDisplayName(SIGDN.NORMALDISPLAY, out IntPtr namePtr);
            if (namePtr == IntPtr.Zero)
                return null;

            try
            {
                return Marshal.PtrToStringUni(namePtr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(namePtr);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shellItemObj is not null)
                Marshal.ReleaseComObject(shellItemObj);
        }
    }

    /// <summary>
    /// 解析圖示：優先 IconCacheService 磁碟快取 → Shell 提取並寫入快取。
    /// 可於背景執行緒呼叫（回傳的 BitmapSource 已 Freeze）。
    /// </summary>
    /// <param name="preferredDecodeSize">解碼邊長；建議 max(256, iconSize*maxScale*2)。</param>
    public static ImageSource? ResolveIcon(DockItem item, int preferredDecodeSize = IconCacheService.DefaultSize)
    {
        if (item.IsRecycleBin)
        {
            bool full = ShellRecycleBin.QueryStatus().HasItems;
            return ShellRecycleBin.GetIcon(isFull: full);
        }

        if (item.IsThisPC)
        {
            return GetHighDpiIcon(ShellThisPC.ParsingName, Math.Max(256, preferredDecodeSize))
                   ?? GetHighDpiIcon(ShellThisPC.ShellFolderPath, Math.Max(256, preferredDecodeSize));
        }

        int size = Math.Max(256, preferredDecodeSize);
        return IconCacheService.Resolve(item, GetHighDpiIcon, size, writeCacheIfMissing: true);
    }

    public static bool IsShortcut(string path) =>
        path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

    // ── IShellItemImageFactory ────────────────────────────────

    private static BitmapSource? ExtractViaShellItemImageFactory(string path, int size)
    {
        object? shellItemObj = null;
        IntPtr hBitmap = IntPtr.Zero;

        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out shellItemObj);
            if (hr < 0 || shellItemObj is null)
                return null;

            var factory = (IShellItemImageFactory)shellItemObj;
            var sz = new SIZE(size, size);

            // ICONONLY：只要圖示不要縮圖內容；BIGGERSIZEOK：允許回傳更大尺寸再縮放
            hr = factory.GetImage(sz, SIIGBF.ICONONLY | SIIGBF.BIGGERSIZEOK, out hBitmap);
            if (hr < 0 || hBitmap == IntPtr.Zero)
            {
                // 部分資料夾/檔案類型不接受 ICONONLY，退回預設旗標
                hr = factory.GetImage(sz, SIIGBF.RESIZETOFIT | SIIGBF.BIGGERSIZEOK, out hBitmap);
                if (hr < 0 || hBitmap == IntPtr.Zero)
                    return null;
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
                DeleteObject(hBitmap);

            if (shellItemObj is not null)
                Marshal.ReleaseComObject(shellItemObj);
        }
    }

    private static string GetFriendlyName(string path)
    {
        if (IsShellItemPath(path))
            return TryGetShellDisplayName(path) ?? path;

        if (Directory.Exists(path))
            return new DirectoryInfo(path).Name;

        string name = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    // ── P/Invoke & COM ────────────────────────────────────────

    private enum SIGDN : uint
    {
        NORMALDISPLAY = 0,
        DESKTOPABSOLUTEPARSING = 0x80028000
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;

        public SIZE(int cx, int cy)
        {
            this.cx = cx;
            this.cy = cy;
        }
    }

    [Flags]
    private enum SIIGBF : uint
    {
        RESIZETOFIT = 0x00,
        BIGGERSIZEOK = 0x01,
        MEMORYONLY = 0x02,
        ICONONLY = 0x04,
        THUMBNAILONLY = 0x08,
        INCACHEONLY = 0x10
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    /// <summary>CLSID_ShellLink</summary>
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass
    {
    }

    /// <summary>
    /// IShellLinkW 完整 vtable（順序不可更動）。
    /// 僅實作我們會呼叫的方法，其餘以 stub 佔位。
    /// </summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cchMaxPath,
            IntPtr pfd,
            uint fFlags);

        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);

        void GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName,
            int cchMaxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir,
            int cchMaxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs,
            int cchMaxPath);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);

        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
            int cchIconPath,
            out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        void Load(
            [MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
            uint dwMode);

        void Save(
            [MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
            [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}

/// <summary>.lnk 解析結果。</summary>
public readonly record struct ShortcutInfo(
    string TargetPath,
    string Arguments,
    string WorkingDirectory,
    string Description,
    string IconPath = "",
    int IconIndex = 0);
