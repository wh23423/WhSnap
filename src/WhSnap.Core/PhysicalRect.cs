namespace WhSnap.Core;

/// <summary>
/// 物理像素坐标下的矩形（左上角 + 宽高）。
/// 整个截图流程的唯一坐标口径：采集、选择、裁剪、导出全部使用物理像素，
/// DIP 只在 WPF 界面布局时临时换算，避免混合 DPI 下的选区错位。
/// </summary>
public readonly record struct PhysicalRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>由两个对角点构造，自动归一化（支持任意方向拖拽）。</summary>
    public static PhysicalRect FromPoints(int x1, int y1, int x2, int y2)
    {
        int left = Math.Min(x1, x2);
        int top = Math.Min(y1, y2);
        int width = Math.Abs(x2 - x1);
        int height = Math.Abs(y2 - y1);
        return new PhysicalRect(left, top, width, height);
    }

    /// <summary>把当前矩形限制在 bounds 之内（用于把拖拽限制在显示器范围内）。</summary>
    public PhysicalRect ClampTo(PhysicalRect bounds)
    {
        int left = Math.Clamp(Math.Min(X, Right), bounds.X, bounds.Right);
        int right = Math.Clamp(Math.Max(X, Right), bounds.X, bounds.Right);
        int top = Math.Clamp(Math.Min(Y, Bottom), bounds.Y, bounds.Bottom);
        int bottom = Math.Clamp(Math.Max(Y, Bottom), bounds.Y, bounds.Bottom);
        return new PhysicalRect(left, top, right - left, bottom - top);
    }

    public PhysicalRect Intersect(PhysicalRect other)
    {
        int left = Math.Max(X, other.X);
        int top = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right);
        int bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top
            ? new PhysicalRect(left, top, 0, 0)
            : new PhysicalRect(left, top, right - left, bottom - top);
    }

    /// <summary>把显示器局部坐标换算成虚拟桌面（全屏位图）坐标。</summary>
    public PhysicalRect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}
