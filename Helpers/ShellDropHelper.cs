using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using IComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace RDock;

/// <summary>
/// 從 Explorer 拖放資料取出可釘選路徑：一般檔案／資料夾，以及「本機」等 Shell 虛擬項目。
/// </summary>
public static class ShellDropHelper
{
    private const string ShellIdListFormat = "Shell IDList Array";

    public static bool CanAccept(System.Windows.IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop, autoConvert: true))
                return true;

            if (data.GetDataPresent(ShellIdListFormat, autoConvert: false))
                return true;

            foreach (string format in data.GetFormats(autoConvert: false))
            {
                if (format.Contains("Shell IDList", StringComparison.OrdinalIgnoreCase) ||
                    format.Contains("FileGroupDescriptor", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    /// <summary>回傳解析名稱（檔案路徑、資料夾，或 <c>::{GUID}</c> / <c>shell:...</c>）。</summary>
    public static IReadOnlyList<string> ExtractPaths(System.Windows.IDataObject data)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            path = path.Trim().Trim('"');
            if (seen.Add(path))
                result.Add(path);
        }

        // 1) 原生 Shell API（最可靠，含本機／控制台等虛擬項目）
        foreach (string path in ExtractViaShellItemArray(data))
            Add(path);

        // 2) 手動解析 CIDA（WPF GetData 可能失敗，改走 COM IDataObject）
        if (result.Count == 0)
        {
            foreach (string path in ExtractFromShellIdListNative(data))
                Add(path);
        }

        // 3) 一般檔案／資料夾／捷徑
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop, autoConvert: true) &&
                data.GetData(DataFormats.FileDrop, autoConvert: true) is string[] files)
            {
                foreach (string file in files)
                    Add(file);
            }
        }
        catch
        {
            // ignore
        }

        return result;
    }

    private static IEnumerable<string> ExtractViaShellItemArray(System.Windows.IDataObject data)
    {
        if (data is not IComDataObject comData)
            yield break;

        object? arrayObj = null;
        try
        {
            var iid = typeof(IShellItemArray).GUID;
            int hr = SHCreateShellItemArrayFromDataObject(comData, ref iid, out arrayObj);
            if (hr < 0 || arrayObj is null)
                yield break;

            var array = (IShellItemArray)arrayObj;
            array.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                IShellItem? item = null;
                try
                {
                    array.GetItemAt(i, out item);
                    string? parsing = GetItemParsingName(item);
                    if (!string.IsNullOrWhiteSpace(parsing))
                        yield return parsing!;
                }
                finally
                {
                    if (item is not null)
                        Marshal.ReleaseComObject(item);
                }
            }
        }
        finally
        {
            if (arrayObj is not null)
                Marshal.ReleaseComObject(arrayObj);
        }
    }

    private static string? GetItemParsingName(IShellItem item)
    {
        // 實體路徑優先
        if (TryGetDisplayName(item, SIGDN.FILESYSPATH, out string? fs) &&
            !string.IsNullOrWhiteSpace(fs))
            return fs;

        if (TryGetDisplayName(item, SIGDN.DESKTOPABSOLUTEPARSING, out string? parsing) &&
            !string.IsNullOrWhiteSpace(parsing))
            return parsing;

        if (TryGetDisplayName(item, SIGDN.URL, out string? url) &&
            !string.IsNullOrWhiteSpace(url))
            return url;

        return null;
    }

    private static bool TryGetDisplayName(IShellItem item, SIGDN sigdn, out string? name)
    {
        name = null;
        try
        {
            item.GetDisplayName(sigdn, out IntPtr ptr);
            if (ptr == IntPtr.Zero)
                return false;

            try
            {
                name = Marshal.PtrToStringUni(ptr);
                return !string.IsNullOrWhiteSpace(name);
            }
            finally
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> ExtractFromShellIdListNative(System.Windows.IDataObject data)
    {
        if (data is not IComDataObject comData)
            yield break;

        short formatId;
        try
        {
            formatId = (short)DataFormats.GetDataFormat(ShellIdListFormat).Id;
        }
        catch
        {
            yield break;
        }

        var formatEtc = new FORMATETC
        {
            cfFormat = formatId,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL
        };

        STGMEDIUM medium = default;
        try
        {
            comData.GetData(ref formatEtc, out medium);
        }
        catch
        {
            yield break;
        }

        try
        {
            if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == IntPtr.Zero)
                yield break;

            IntPtr hglobal = medium.unionmember;
            IntPtr locked = GlobalLock(hglobal);
            if (locked == IntPtr.Zero)
                yield break;

            try
            {
                int size = GlobalSize(hglobal).ToInt32();
                if (size < 8)
                    yield break;

                foreach (string path in ParseCida(locked, size))
                    yield return path;
            }
            finally
            {
                GlobalUnlock(hglobal);
            }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static IEnumerable<string> ParseCida(IntPtr basePtr, int byteLength)
    {
        int cidl = Marshal.ReadInt32(basePtr, 0);
        if (cidl < 0 || cidl > 256)
            yield break;

        int parentOffset = Marshal.ReadInt32(basePtr, sizeof(int));
        if (parentOffset < 0 || parentOffset >= byteLength)
            yield break;

        IntPtr parentPidl = IntPtr.Add(basePtr, parentOffset);

        for (int i = 0; i < cidl; i++)
        {
            int childOffset = Marshal.ReadInt32(basePtr, sizeof(int) * (i + 2));
            if (childOffset < 0 || childOffset >= byteLength)
                continue;

            IntPtr relativePidl = IntPtr.Add(basePtr, childOffset);
            IntPtr fullPidl = IntPtr.Zero;

            try
            {
                fullPidl = IsEmptyPidl(relativePidl)
                    ? ILClone(parentPidl)
                    : ILCombine(parentPidl, relativePidl);

                if (fullPidl == IntPtr.Zero)
                    continue;

                string? parsing = GetParsingNameFromPidl(fullPidl);
                if (!string.IsNullOrWhiteSpace(parsing))
                    yield return parsing!;
            }
            finally
            {
                if (fullPidl != IntPtr.Zero)
                    ILFree(fullPidl);
            }
        }
    }

    private static string? GetParsingNameFromPidl(IntPtr pidl)
    {
        var fsPath = new StringBuilder(1024);
        if (SHGetPathFromIDListEx(pidl, fsPath, fsPath.Capacity, 0) && fsPath.Length > 0)
            return fsPath.ToString();

        if (SHGetNameFromIDList(pidl, SIGDN.DESKTOPABSOLUTEPARSING, out IntPtr namePtr) >= 0 &&
            namePtr != IntPtr.Zero)
        {
            try
            {
                return Marshal.PtrToStringUni(namePtr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(namePtr);
            }
        }

        return null;
    }

    private static bool IsEmptyPidl(IntPtr pidl) =>
        pidl == IntPtr.Zero || Marshal.ReadInt16(pidl) == 0;

    // ── COM / Win32 ──────────────────────────────────────────

    private enum SIGDN : uint
    {
        NORMALDISPLAY = 0,
        DESKTOPABSOLUTEPARSING = 0x80028000,
        FILESYSPATH = 0x80058000,
        URL = 0x80068000
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
    [Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(ref PROPERTYKEY keyType, ref Guid riid, out IntPtr ppv);
        void GetAttributes(int AttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHCreateShellItemArrayFromDataObject(
        IComDataObject pdo,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListEx(
        IntPtr pidl,
        StringBuilder pszPath,
        int cchPath,
        uint uOpts);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetNameFromIDList(
        IntPtr pidl,
        SIGDN sigdnName,
        out IntPtr ppszName);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILClone(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalSize(IntPtr hMem);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);
}
