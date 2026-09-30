using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RDock;

/// <summary>圖示影像後處理：去掉透明邊距並縮放到目標邊長。</summary>
public static class IconImageHelper
{
    /// <summary>
    /// 裁掉全透明邊緣，再等比放大到填滿 <paramref name="size"/>×<paramref name="size"/>。
    /// </summary>
    public static BitmapSource NormalizeToSquare(BitmapSource source, int size = 256)
    {
        ArgumentNullException.ThrowIfNull(source);
        size = Math.Clamp(size, 16, 512);

        BitmapSource bgra = EnsureBgra32(source);
        BitmapSource trimmed = TrimTransparent(bgra) ?? bgra;
        return FitToSquare(trimmed, size);
    }

    private static BitmapSource EnsureBgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32)
            return source;

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static BitmapSource? TrimTransparent(BitmapSource source)
    {
        int w = source.PixelWidth;
        int h = source.PixelHeight;
        if (w <= 1 || h <= 1)
            return null;

        int stride = w * 4;
        byte[] pixels = new byte[stride * h];
        source.CopyPixels(pixels, stride, 0);

        int minX = w, minY = h, maxX = -1, maxY = -1;
        const byte alphaThreshold = 12;

        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                byte a = pixels[row + x * 4 + 3];
                if (a < alphaThreshold)
                    continue;

                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < minX || maxY < minY)
            return null;

        // 留一點邊，避免貼邊過緊
        int pad = Math.Max(1, Math.Min(w, h) / 64);
        minX = Math.Max(0, minX - pad);
        minY = Math.Max(0, minY - pad);
        maxX = Math.Min(w - 1, maxX + pad);
        maxY = Math.Min(h - 1, maxY + pad);

        int cw = maxX - minX + 1;
        int ch = maxY - minY + 1;

        // 幾乎沒裁到就不處理
        if (cw >= w * 0.92 && ch >= h * 0.92)
            return null;

        var cropped = new CroppedBitmap(source, new Int32Rect(minX, minY, cw, ch));
        cropped.Freeze();
        return cropped;
    }

    private static BitmapSource FitToSquare(BitmapSource source, int size)
    {
        double scale = Math.Min(
            (double)size / Math.Max(1, source.PixelWidth),
            (double)size / Math.Max(1, source.PixelHeight));

        BitmapSource scaled = source;
        if (Math.Abs(scale - 1.0) > 0.01)
        {
            var tb = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            tb.Freeze();
            scaled = tb;
        }

        int sw = scaled.PixelWidth;
        int sh = scaled.PixelHeight;
        if (sw == size && sh == size)
            return scaled;

        // 置中畫到正方形畫布
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, size, size));
            double x = (size - sw) / 2.0;
            double y = (size - sh) / 2.0;
            dc.DrawImage(scaled, new Rect(x, y, sw, sh));
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
