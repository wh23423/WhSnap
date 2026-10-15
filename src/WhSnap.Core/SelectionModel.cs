namespace WhSnap.Core;

public readonly record struct PhysicalPoint(int X, int Y);

/// <summary>选区的可交互部位：8 个手柄 + 内部 + 外部。</summary>
public enum SelectionHandle
{
    None,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
    Inside,
}

/// <summary>方向键微调方向。</summary>
public enum NudgeDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>
/// 选区状态机。所有输入输出都是虚拟桌面绝对物理像素，并被限制在 bounds 内。
///
/// 支持三种编辑方式（对应鼠标与键盘两套交互）：
///   * 新建：Begin + Update（拖拽框选，方向任意）
///   * 调整尺寸：BeginResize + Update（抓住 8 向手柄之一）
///   * 平移：BeginMove + Update（抓住选区内部拖动）
/// 以及不依赖鼠标的 Nudge（方向键 1px / Shift 10px 微调）。
///
/// 纯逻辑、无 UI 依赖，可直接单测——多屏与混合 DPI 下的坐标正确性全靠这一层保证。
/// </summary>
public sealed class SelectionModel(PhysicalRect bounds)
{
    private readonly PhysicalRect _bounds = bounds;

    private enum Mode
    {
        None,
        Create,
        Resize,
        Move,
    }

    private Mode _mode;
    private PhysicalPoint? _anchor;
    private PhysicalPoint _moveStart;
    private PhysicalRect _moveOrigin;
    private SelectionHandle _resizeHandle;

    public const int MinSize = 1;

    public PhysicalPoint? Anchor => _anchor;

    public PhysicalPoint? Cursor { get; private set; }

    public bool IsDragging => _mode != Mode.None;

    public PhysicalRect Current { get; private set; }

    public PhysicalRect Bounds => _bounds;

    /// <summary>当前抓住的手柄（未在调整尺寸时为 None）。</summary>
    public SelectionHandle ActiveHandle => _mode == Mode.Resize ? _resizeHandle : SelectionHandle.None;

    public bool HasSelection => !Current.IsEmpty;

    /// <summary>开始框选新区域。</summary>
    public void Begin(PhysicalPoint point)
    {
        var clamped = Clamp(point);
        _mode = Mode.Create;
        _anchor = clamped;
        Cursor = clamped;
        Current = new PhysicalRect(clamped.X, clamped.Y, 0, 0);
    }

    /// <summary>开始抓着手柄调整尺寸。</summary>
    public void BeginResize(SelectionHandle handle, PhysicalPoint point)
    {
        if (handle is SelectionHandle.None or SelectionHandle.Inside)
        {
            throw new ArgumentOutOfRangeException(nameof(handle), "调整尺寸需要具体的手柄。");
        }

        _mode = Mode.Resize;
        _resizeHandle = handle;
        Cursor = Clamp(point);
    }

    /// <summary>开始拖动选区本身（保持尺寸平移）。</summary>
    public void BeginMove(PhysicalPoint point)
    {
        _mode = Mode.Move;
        _moveStart = Clamp(point);
        _moveOrigin = Current;
        Cursor = _moveStart;
    }

    /// <summary>拖拽中更新选区，返回归一化并裁剪后的矩形。</summary>
    public PhysicalRect Update(PhysicalPoint point)
    {
        var clamped = Clamp(point);
        Cursor = clamped;

        switch (_mode)
        {
            case Mode.Create:
                if (_anchor is { } anchor)
                {
                    Current = PhysicalRect
                        .FromPoints(anchor.X, anchor.Y, clamped.X, clamped.Y)
                        .ClampTo(_bounds);
                }

                break;

            case Mode.Resize:
                Current = ResizeTo(clamped);
                break;

            case Mode.Move:
                Current = MoveTo(clamped);
                break;
        }

        return Current;
    }

    public void End() => _mode = Mode.None;

    public void Cancel()
    {
        _mode = Mode.None;
        _anchor = null;
        Cursor = null;
        _resizeHandle = SelectionHandle.None;
        Current = new PhysicalRect(0, 0, 0, 0);
    }

    /// <summary>直接设定选区（用于测试与程序化调整），自动裁剪到 bounds。</summary>
    public void SetSelection(PhysicalRect rect)
    {
        Current = rect.ClampTo(_bounds);
        _anchor = Current.IsEmpty ? null : new PhysicalPoint(Current.X, Current.Y);
    }

    /// <summary>
    /// 方向键微调。
    /// resize = false：整体平移（保持尺寸），限位在 bounds 内。
    /// resize = true ：调整右下边界——左/上收窄，右/下加宽，最小 1px。
    /// </summary>
    public bool Nudge(NudgeDirection direction, int step, bool resize)
    {
        if (step < 1)
        {
            step = 1;
        }

        if (Current.IsEmpty)
        {
            return false;
        }

        var rect = Current;

        if (!resize)
        {
            int dx = direction switch
            {
                NudgeDirection.Left => -step,
                NudgeDirection.Right => step,
                _ => 0,
            };
            int dy = direction switch
            {
                NudgeDirection.Up => -step,
                NudgeDirection.Down => step,
                _ => 0,
            };

            int maxX = Math.Max(_bounds.Right - rect.Width, _bounds.X);
            int maxY = Math.Max(_bounds.Bottom - rect.Height, _bounds.Y);

            Current = new PhysicalRect(
                Math.Clamp(rect.X + dx, _bounds.X, maxX),
                Math.Clamp(rect.Y + dy, _bounds.Y, maxY),
                rect.Width,
                rect.Height);
        }
        else
        {
            int left = rect.X;
            int top = rect.Y;
            int right = rect.Right;
            int bottom = rect.Bottom;

            switch (direction)
            {
                case NudgeDirection.Left:
                    right -= step;
                    break;
                case NudgeDirection.Right:
                    right += step;
                    break;
                case NudgeDirection.Up:
                    bottom -= step;
                    break;
                case NudgeDirection.Down:
                    bottom += step;
                    break;
            }

            right = Math.Clamp(right, left + MinSize, _bounds.Right);
            bottom = Math.Clamp(bottom, top + MinSize, _bounds.Bottom);

            Current = new PhysicalRect(left, top, right - left, bottom - top);
        }

        return true;
    }

    /// <summary>命中测试：判断光标落在哪个手柄、选区内还是外部。tolerance 为物理像素容差。</summary>
    public static SelectionHandle HitTest(PhysicalRect selection, PhysicalPoint point, int tolerance)
    {
        if (selection.IsEmpty)
        {
            return SelectionHandle.None;
        }

        if (tolerance < 1)
        {
            tolerance = 1;
        }

        bool withinX = point.X >= selection.X - tolerance && point.X <= selection.Right + tolerance;
        bool withinY = point.Y >= selection.Y - tolerance && point.Y <= selection.Bottom + tolerance;
        if (!withinX || !withinY)
        {
            return SelectionHandle.None;
        }

        bool nearLeft = Math.Abs(point.X - selection.X) <= tolerance;
        bool nearRight = Math.Abs(point.X - selection.Right) <= tolerance;
        bool nearTop = Math.Abs(point.Y - selection.Y) <= tolerance;
        bool nearBottom = Math.Abs(point.Y - selection.Bottom) <= tolerance;

        // 角优先于边：小选区时也能稳定抓到角
        if (nearLeft && nearTop)
        {
            return SelectionHandle.TopLeft;
        }

        if (nearRight && nearTop)
        {
            return SelectionHandle.TopRight;
        }

        if (nearLeft && nearBottom)
        {
            return SelectionHandle.BottomLeft;
        }

        if (nearRight && nearBottom)
        {
            return SelectionHandle.BottomRight;
        }

        if (nearLeft)
        {
            return SelectionHandle.Left;
        }

        if (nearRight)
        {
            return SelectionHandle.Right;
        }

        if (nearTop)
        {
            return SelectionHandle.Top;
        }

        if (nearBottom)
        {
            return SelectionHandle.Bottom;
        }

        return SelectionHandle.Inside;
    }

    private PhysicalRect ResizeTo(PhysicalPoint point)
    {
        int left = Current.X;
        int top = Current.Y;
        int right = Current.Right;
        int bottom = Current.Bottom;

        if (_resizeHandle is SelectionHandle.Left or SelectionHandle.TopLeft or SelectionHandle.BottomLeft)
        {
            left = point.X;
        }

        if (_resizeHandle is SelectionHandle.Right or SelectionHandle.TopRight or SelectionHandle.BottomRight)
        {
            right = point.X;
        }

        if (_resizeHandle is SelectionHandle.Top or SelectionHandle.TopLeft or SelectionHandle.TopRight)
        {
            top = point.Y;
        }

        if (_resizeHandle is SelectionHandle.Bottom or SelectionHandle.BottomLeft or SelectionHandle.BottomRight)
        {
            bottom = point.Y;
        }

        // 允许越过对边翻转，但保证至少 1px
        int x1 = Math.Min(left, right);
        int x2 = Math.Max(left, right);
        int y1 = Math.Min(top, bottom);
        int y2 = Math.Max(top, bottom);

        x1 = Math.Clamp(x1, _bounds.X, _bounds.Right - MinSize);
        y1 = Math.Clamp(y1, _bounds.Y, _bounds.Bottom - MinSize);
        x2 = Math.Clamp(x2, x1 + MinSize, _bounds.Right);
        y2 = Math.Clamp(y2, y1 + MinSize, _bounds.Bottom);

        return new PhysicalRect(x1, y1, x2 - x1, y2 - y1);
    }

    private PhysicalRect MoveTo(PhysicalPoint point)
    {
        int dx = point.X - _moveStart.X;
        int dy = point.Y - _moveStart.Y;

        int maxX = Math.Max(_bounds.Right - _moveOrigin.Width, _bounds.X);
        int maxY = Math.Max(_bounds.Bottom - _moveOrigin.Height, _bounds.Y);

        return new PhysicalRect(
            Math.Clamp(_moveOrigin.X + dx, _bounds.X, maxX),
            Math.Clamp(_moveOrigin.Y + dy, _bounds.Y, maxY),
            _moveOrigin.Width,
            _moveOrigin.Height);
    }

    private PhysicalPoint Clamp(PhysicalPoint point) => new(
        Math.Clamp(point.X, _bounds.X, _bounds.Right),
        Math.Clamp(point.Y, _bounds.Y, _bounds.Bottom));
}
