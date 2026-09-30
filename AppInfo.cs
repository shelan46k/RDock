using System.Reflection;

namespace RDock;

/// <summary>組件版本與顯示名稱（與 csproj 的 Version 同步）。</summary>
public static class AppInfo
{
    public const string ProductName = "RDock";
    public const string AuthorName = "Rank";

    /// <summary>顯示用版本，例如 1.0.0。</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>選單用，例如「版本 1.0.0」。</summary>
    public static string VersionMenuHeader => $"版本 {Version}";

    public static string AboutTitle => $"關於 {ProductName}";

    public static string AboutMessage =>
        $"{ProductName}  {Version}\n\n" +
        $"作者：{AuthorName}\n" +
        "授權：MIT License";

    private static string ResolveVersion()
    {
        var asm = Assembly.GetExecutingAssembly();

        string? informational = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+');
            if (plus >= 0)
                informational = informational[..plus];
            return informational.Trim();
        }

        Version? v = asm.GetName().Version;
        if (v is null)
            return "1.0.0";

        return v.Revision > 0
            ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}"
            : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
