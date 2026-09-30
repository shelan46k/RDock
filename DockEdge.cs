namespace RDock;

/// <summary>Dock 停靠螢幕邊緣。</summary>
public enum DockEdge
{
    Bottom = 0,
    Top = 1,
    Left = 2,
    Right = 3
}

public static class DockEdgeExtensions
{
    public static bool IsHorizontal(this DockEdge edge) =>
        edge is DockEdge.Top or DockEdge.Bottom;

    public static string ToDisplayName(this DockEdge edge) => edge switch
    {
        DockEdge.Top => "上方",
        DockEdge.Bottom => "下方",
        DockEdge.Left => "左側",
        DockEdge.Right => "右側",
        _ => edge.ToString()
    };
}
