using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace RDock;

/// <summary>單一 Dock 圖示的視覺狀態（縮放 / 彈跳 / 推擠 / 指示點）。</summary>
internal sealed class IconViewState
{
    public required DockItem Item { get; init; }
    public required Border Host { get; init; }
    public required Border IconSurface { get; init; }
    public required Ellipse Indicator { get; init; }
    public required ScaleTransform Scale { get; init; }
    public required TranslateTransform Bounce { get; init; }
    /// <summary>魚眼鄰圖推擠（沿 Dock 軸向）。</summary>
    public required TranslateTransform Slide { get; init; }
    public TextBlock? ClockText { get; init; }
}

/// <summary>macOS 風格 Launch Bounce 動畫。</summary>
internal static class IconBounceAnimation
{
    /// <summary>
    /// 向上彈跳約 3 下後復原（作用於 TranslateTransform.Y）。
    /// </summary>
    public static void Play(TranslateTransform bounce)
    {
        // 先清掉舊動畫，避免重疊
        bounce.BeginAnimation(TranslateTransform.YProperty, null);
        bounce.Y = 0;

        var anim = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(920)),
            FillBehavior = FillBehavior.Stop
        };

        // 關鍵影格：0 → 上 → 回 → 上 → 回 → 小跳 → 歸零
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(-34,
            KeyTime.FromPercent(0.16),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0,
            KeyTime.FromPercent(0.34),
            new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(-22,
            KeyTime.FromPercent(0.50),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0,
            KeyTime.FromPercent(0.66),
            new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(-12,
            KeyTime.FromPercent(0.80),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0,
            KeyTime.FromPercent(1.0),
            new QuadraticEase { EasingMode = EasingMode.EaseIn }));

        anim.Completed += (_, _) =>
        {
            bounce.BeginAnimation(TranslateTransform.YProperty, null);
            bounce.Y = 0;
        };

        bounce.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    public static Ellipse CreateIndicatorDot()
    {
        var dot = new Ellipse
        {
            Width = 6,
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.35, 0.35),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.55,
                RadiusY = 0.55,
                GradientStops =
                [
                    new GradientStop(Color.FromRgb(0xF0, 0xF4, 0xFF), 0),
                    new GradientStop(Color.FromRgb(0x93, 0xC5, 0xFD), 0.55),
                    new GradientStop(Color.FromRgb(0x3B, 0x82, 0xF6), 1)
                ]
            },
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0x60, 0xA5, 0xFA),
                BlurRadius = 4,
                ShadowDepth = 0,
                Opacity = 0.85,
                RenderingBias = RenderingBias.Performance
            }
        };

        return dot;
    }

    public static void SetRunning(Ellipse indicator, bool running)
    {
        indicator.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }
}
