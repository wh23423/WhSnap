namespace WhSnap.Core;

/// <summary>放大镜的取源区域与放大倍率。</summary>
public readonly record struct MagnifierLayout(PhysicalRect Source, int SampleSize, int Zoom)
{
    /// <summary>屏幕上一个源像素占多少个物理像素。</summary>
    public int PixelSize => Zoom;

    /// <summary>放大镜在屏幕上占据的物理像素边长。</summary>
    public int DisplaySize => SampleSize * Zoom;
}

/// <summary>
/// 放大镜几何计算。
/// 取源区域以光标为中心，边长取奇数以保证"中心像素"唯一（正是光标所在的那个像素）。
/// </summary>
public static class Magnifier
{
    /// <summary>
    /// 取源边长（物理像素，奇数以保证中心像素唯一）。11×11 @12x = 132px 放大镜，
    /// 来自 Focal 设计规范 §8：放大镜读的是"单个物理像素"，格数太多反而看不清格子。
    /// </summary>
    public const int DefaultSampleSize = 11;

    /// <summary>放大倍率。11×12 = 132px 的卢浮镜面。</summary>
    public const int DefaultZoom = 12;

    public static MagnifierLayout Compute(
        PhysicalPoint cursor,
        PhysicalRect monitorBounds,
        int sampleSize = DefaultSampleSize,
        int zoom = DefaultZoom)
    {
        if (sampleSize < 3)
        {
            sampleSize = 3;
        }

        if (sampleSize % 2 == 0)
        {
            sampleSize++;
        }

        if (zoom < 1)
        {
            zoom = 1;
        }

        int half = sampleSize / 2;

        int left = Math.Clamp(
            cursor.X - half,
            monitorBounds.X,
            Math.Max(monitorBounds.Right - sampleSize, monitorBounds.X));

        int top = Math.Clamp(
            cursor.Y - half,
            monitorBounds.Y,
            Math.Max(monitorBounds.Bottom - sampleSize, monitorBounds.Y));

        return new MagnifierLayout(new PhysicalRect(left, top, sampleSize, sampleSize), sampleSize, zoom);
    }

    /// <summary>光标所在像素在取源区域内的中心偏移（用于绘制中心高亮格）。</summary>
    public static (int X, int Y) CenterCell(MagnifierLayout layout, PhysicalPoint cursor) =>
        (cursor.X - layout.Source.X, cursor.Y - layout.Source.Y);
}
