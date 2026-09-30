using System.IO;
using System.Text;

namespace RDock;

/// <summary>拖放檔案到應用程式圖示時的啟動參數組裝。</summary>
public static class DropLaunchHelper
{
    /// <summary>
    /// Process.Start(appPath, arguments)：保留原有 Arguments，並附加被拖入的檔案路徑。
    /// </summary>
    public static string BuildArguments(string? existingArguments, IEnumerable<string> droppedPaths)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(existingArguments))
            sb.Append(existingArguments.Trim());

        foreach (string path in droppedPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (sb.Length > 0)
                sb.Append(' ');

            sb.Append(Quote(path));
        }

        return sb.ToString();
    }

    public static string Quote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value;

        return value.Contains(' ') || value.Contains('\t')
            ? $"\"{value}\""
            : value;
    }

    public static void LaunchWithFiles(string appPath, string? existingArguments, IReadOnlyList<string> files)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = appPath,
            Arguments = BuildArguments(existingArguments, files),
            UseShellExecute = true
        };

        try
        {
            string? dir = Path.GetDirectoryName(appPath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                psi.WorkingDirectory = dir;
        }
        catch
        {
            // ignore
        }

        System.Diagnostics.Process.Start(psi);
    }
}
