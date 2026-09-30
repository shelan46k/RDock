using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace RDock;

/// <summary>
/// 開機自動啟動：寫入 / 刪除 HKCU\...\Run 機碼（值名 RDock；會清掉舊 MyDock）。
/// </summary>
public static class AutostartHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RDock";
    private const string LegacyValueName = "MyDock";

    public static string GetExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
            return processPath;

        return Process.GetCurrentProcess().MainModule?.FileName
               ?? Path.Combine(AppContext.BaseDirectory, "RDock.exe");
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key?.GetValue(ValueName) is string)
                return true;

            // 相容舊機碼
            return key?.GetValue(LegacyValueName) is string;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutostartHelper] IsEnabled failed: {ex.Message}");
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (key is null)
                throw new InvalidOperationException("無法開啟登錄機碼 Run。");

            // 清掉舊名稱，避免雙開
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);

            if (enabled)
            {
                string exe = GetExecutablePath();
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutostartHelper] SetEnabled failed: {ex.Message}");
            throw;
        }
    }
}
