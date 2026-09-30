using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RDock;

/// <summary>
/// 圖示磁碟快取：首次從 EXE/LNK 提取後存成 PNG，之後啟動直接讀本機檔。
/// 路徑：%AppData%/RDock/Cache/{hash}.png
/// </summary>
public static class IconCacheService
{
    public const int DefaultSize = 256;

    public static string RootDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RDock");

    public static string CacheDirectory { get; } = Path.Combine(RootDirectory, "Cache");

    public static string CustomDirectory { get; } = Path.Combine(RootDirectory, "Custom");

    /// <summary>
    /// 依來源路徑 + 檔案時間戳 + 尺寸產生穩定雜湊（來源更新時自動失效）。
    /// </summary>
    public static string ComputeHash(string? sourcePath, int size = DefaultSize)
    {
        string normalized = NormalizePath(sourcePath);
        long stamp = 0;
        long length = 0;

        try
        {
            if (File.Exists(normalized))
            {
                var fi = new FileInfo(normalized);
                stamp = fi.LastWriteTimeUtc.Ticks;
                length = fi.Length;
            }
            else if (Directory.Exists(normalized))
            {
                stamp = Directory.GetLastWriteTimeUtc(normalized).Ticks;
            }
        }
        catch
        {
            // 忽略狀態讀取失敗，仍用路徑雜湊
        }

        string payload = $"{normalized}|{stamp}|{length}|{size}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }

    public static string GetCachePath(string hash) =>
        Path.Combine(CacheDirectory, $"{hash}.png");

    public static bool TryGetCachePath(string? sourcePath, int size, out string cachePath)
    {
        string hash = ComputeHash(sourcePath, size);
        cachePath = GetCachePath(hash);
        return File.Exists(cachePath);
    }

    /// <summary>從磁碟載入 PNG（可於背景執行緒；回傳已 Freeze）。</summary>
    public static BitmapImage? LoadFromFile(string? path, int decodePixelWidth = DefaultSize)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            // 解碼寬度預留魚眼放大餘裕；至少 256 以免小圖糊掉
            int decode = Math.Max(256, decodePixelWidth);
            bitmap.DecodePixelWidth = decode;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IconCacheService] Load failed: {ex.Message}");
            return null;
        }
    }

    public static BitmapImage? TryLoadCached(string? sourcePath, int size = DefaultSize) =>
        TryGetCachePath(sourcePath, size, out string path) ? LoadFromFile(path, size) : null;

    /// <summary>將 BitmapSource 寫入雜湊檔名 PNG，回傳絕對路徑。</summary>
    public static async Task<string> SaveAsync(
        BitmapSource source,
        string? sourcePath,
        int size = DefaultSize,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(CacheDirectory);
        string hash = ComputeHash(sourcePath, size);
        string path = GetCachePath(hash);

        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            WritePng(source, path);
        }, ct).ConfigureAwait(false);

        return path;
    }

    /// <summary>以指定雜湊／檔名儲存（自訂圖示用）。</summary>
    public static async Task<string> SaveToPathAsync(
        BitmapSource source,
        string absolutePath,
        CancellationToken ct = default)
    {
        string? dir = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            WritePng(source, absolutePath);
        }, ct).ConfigureAwait(false);

        return absolutePath;
    }

    /// <summary>複製使用者自訂圖示到 Custom 目錄。</summary>
    public static async Task<string> ImportCustomAsync(
        string sourceFile,
        string itemId,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(CustomDirectory);
        string ext = Path.GetExtension(sourceFile);
        if (string.IsNullOrWhiteSpace(ext))
            ext = ".png";

        string dest = Path.Combine(CustomDirectory, $"{itemId}_custom{ext}");
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(sourceFile, dest, overwrite: true);
        }, ct).ConfigureAwait(false);

        return dest;
    }

    /// <summary>
    /// 解析圖示：自訂快取路徑 → 雜湊磁碟快取 → 現場提取並寫入快取。
    /// 可於背景執行緒呼叫。
    /// </summary>
    public static ImageSource? Resolve(
        DockItem item,
        Func<string, int, BitmapSource?> extract,
        int size = DefaultSize,
        bool writeCacheIfMissing = true)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(extract);

        // 1) 使用者自訂 / 舊版 IconCachePath
        if (!string.IsNullOrWhiteSpace(item.IconCachePath) &&
            IsLikelyCustomOrLegacyCache(item.IconCachePath))
        {
            var custom = LoadFromFile(item.IconCachePath, size);
            if (custom is not null)
                return custom;
        }

        string? extractPath = PreferExtractPath(item);

        // 2) 雜湊磁碟快取（秒開）
        if (!string.IsNullOrWhiteSpace(extractPath))
        {
            var cached = TryLoadCached(extractPath, size);
            if (cached is not null)
            {
                item.IconCachePath = GetCachePath(ComputeHash(extractPath, size));
                return cached;
            }
        }

        // 3) Shell 提取
        BitmapSource? extracted = null;
        if (!string.IsNullOrWhiteSpace(extractPath))
            extracted = extract(extractPath, size);

        if (extracted is null &&
            !string.IsNullOrWhiteSpace(item.TargetPath) &&
            !string.Equals(item.TargetPath, extractPath, StringComparison.OrdinalIgnoreCase))
        {
            extracted = extract(item.TargetPath, size);
            extractPath = item.TargetPath;
        }

        if (extracted is null)
            return null;

        if (writeCacheIfMissing && !string.IsNullOrWhiteSpace(extractPath))
        {
            try
            {
                Directory.CreateDirectory(CacheDirectory);
                string hash = ComputeHash(extractPath, size);
                string path = GetCachePath(hash);
                if (!File.Exists(path))
                    WritePng(extracted, path);
                item.IconCachePath = path;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IconCacheService] Write cache failed: {ex.Message}");
            }
        }

        return extracted;
    }

    public static void TryDelete(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            bool underRDock =
                path.StartsWith(RootDirectory, StringComparison.OrdinalIgnoreCase);
            bool underLegacyMyDock = path.Contains(
                Path.Combine("MyDock", "icons"),
                StringComparison.OrdinalIgnoreCase);

            if (underRDock || underLegacyMyDock)
                File.Delete(path);
        }
        catch
        {
            // 忽略清理失敗
        }
    }

    private static bool IsLikelyCustomOrLegacyCache(string path)
    {
        if (path.Contains("_custom", StringComparison.OrdinalIgnoreCase))
            return true;
        if (path.StartsWith(CustomDirectory, StringComparison.OrdinalIgnoreCase))
            return true;
        // 舊版 %AppData%/MyDock/icons/{id}.png
        if (path.Contains(Path.Combine("MyDock", "icons"), StringComparison.OrdinalIgnoreCase))
            return true;
        return File.Exists(path) &&
               !path.StartsWith(CacheDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string? PreferExtractPath(DockItem item)
    {
        string? extractPath = item.SourcePath;
        if (string.IsNullOrWhiteSpace(extractPath) ||
            (!File.Exists(extractPath) && !Directory.Exists(extractPath)))
        {
            extractPath = item.TargetPath;
        }

        return string.IsNullOrWhiteSpace(extractPath) ? null : NormalizePath(extractPath);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')))
                .ToLowerInvariant();
        }
        catch
        {
            return path.Trim().ToLowerInvariant();
        }
    }

    private static void WritePng(BitmapSource source, string path)
    {
        BitmapSource toSave = source;
        if (!source.IsFrozen && source.CanFreeze)
        {
            source.Freeze();
            toSave = source;
        }

        if (toSave.Format != PixelFormats.Bgra32 && toSave.Format != PixelFormats.Pbgra32)
        {
            var converted = new FormatConvertedBitmap(toSave, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            toSave = converted;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(toSave));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
