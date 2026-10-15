namespace WhSnap.Core;

public readonly record struct PhysicalPoint(int X, int Y);

/// <summary>
/// 拖拽选区状态机。所有输入输出都是物理像素，且被限制在指定显示器范围内。
/// 纯逻辑，无 UI 依赖，可直接单测。
/// </summary>
public sealed class SelectionModel(PhysicalRect bounds)
{
    private readonly PhysicalRect _bounds = bounds;
    private PhysicalPoint? _anchor;

    public PhysicalPoint? Anchor => _anchor;

    public PhysicalPoint? Cursor { get; private set; }

    public bool IsDragging { get; private set; }

    public PhysicalRect Current { get; private set; }

    /// <summary>选区是否达到最小可用尺寸（1x1 视为有效，0 视为未选中）。</summary>
    public bool HasSelection => !Current.IsEmpty;

    public void Begin(PhysicalPoint point)
    {
        var clamped = Clamp(point);
        _anchor = clamped;
        Cursor = clamped;
        IsDragging = true;
        Current = new PhysicalRect(clamped.X, clamped.Y, 0, 0);
    }

    /// <summary>拖动中更新选区，返回归一化并裁剪后的矩形。</summary>
    public PhysicalRect Update(PhysicalPoint point)
    {
        if (_anchor is not { } anchor)
        {
            return Current;
        }

        var clamped = Clamp(point);
        Cursor = clamped;
        Current = PhysicalRect
            .FromPoints(anchor.X, anchor.Y, clamped.X, clamped.Y)
            .ClampTo(_bounds);
        return Current;
    }

    public void End()
    {
        IsDragging = false;
    }

    public void Cancel()
    {
        _anchor = null;
        Cursor = null;
        IsDragging = false;
        Current = new PhysicalRect(0, 0, 0, 0);
    }

    private PhysicalPoint Clamp(PhysicalPoint point) => new(
        Math.Clamp(point.X, _bounds.X, _bounds.Right),
        Math.Clamp(point.Y, _bounds.Y, _bounds.Bottom));
}
