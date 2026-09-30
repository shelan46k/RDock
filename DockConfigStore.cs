using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace RDock;

/// <summary>
/// 讀寫 %AppData%/RDock/dock_config.json（自動從舊版 MyDock 遷移）。
/// 圖示磁碟快取由 <see cref="IconCacheService"/> 負責。
/// </summary>
public static class DockConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string ConfigDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RDock");

    private static string LegacyConfigDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyDock");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "dock_config.json");

    private static string LegacyConfigPath => Path.Combine(LegacyConfigDirectory, "dock_config.json");

    public static string IconCacheDirectory => IconCacheService.CacheDirectory;

    public static async Task<DockConfig> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            MigrateFromLegacyIfNeeded();

            if (!File.Exists(ConfigPath))
                return new DockConfig();

            await using var stream = File.OpenRead(ConfigPath);
            var config = await JsonSerializer.DeserializeAsync<DockConfig>(stream, JsonOptions, ct);
            config ??= new DockConfig();
            config.Clamp();
            return config;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DockConfigStore] Load failed: {ex.Message}");
            return new DockConfig();
        }
    }

    public static async Task SaveAsync(DockConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Clamp();

        Directory.CreateDirectory(ConfigDirectory);

        string tempPath = ConfigPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, config, JsonOptions, ct);
        }

        File.Copy(tempPath, ConfigPath, overwrite: true);
        File.Delete(tempPath);
    }

    private static void MigrateFromLegacyIfNeeded()
    {
        try
        {
            if (File.Exists(ConfigPath) || !File.Exists(LegacyConfigPath))
                return;

            Directory.CreateDirectory(ConfigDirectory);
            File.Copy(LegacyConfigPath, ConfigPath, overwrite: false);

            string legacyIcons = Path.Combine(LegacyConfigDirectory, "icons");
            if (Directory.Exists(legacyIcons))
            {
                Directory.CreateDirectory(IconCacheService.CustomDirectory);
                foreach (string file in Directory.EnumerateFiles(legacyIcons))
                {
                    string dest = Path.Combine(
                        IconCacheService.CustomDirectory,
                        Path.GetFileName(file));
                    if (!File.Exists(dest))
                        File.Copy(file, dest);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DockConfigStore] Migrate failed: {ex.Message}");
        }
    }

    public static Task<string> SaveIconCacheAsync(
        BitmapSource source,
        string itemId,
        CancellationToken ct = default,
        string? sourcePath = null)
    {
        _ = itemId;
        return IconCacheService.SaveAsync(source, sourcePath, IconCacheService.DefaultSize, ct);
    }

    public static BitmapImage? LoadIconFromCache(string? path) =>
        IconCacheService.LoadFromFile(path);

    public static Task<string> ImportCustomIconAsync(string sourceFile, string itemId, CancellationToken ct = default) =>
        IconCacheService.ImportCustomAsync(sourceFile, itemId, ct);

    public static void TryDeleteIconCache(string? path) =>
        IconCacheService.TryDelete(path);
}
