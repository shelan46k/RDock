using System.Diagnostics;

namespace RDock;

/// <summary>「本機／此電腦」Shell 資料夾常數與開啟。</summary>
public static class ShellThisPC
{
    public const string ShellFolderPath = "shell:MyComputerFolder";
    public const string ParsingName = @"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    public const string DisplayTitle = "本機";

    public static void OpenInExplorer()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = ShellFolderPath,
            UseShellExecute = true
        });
    }
}
