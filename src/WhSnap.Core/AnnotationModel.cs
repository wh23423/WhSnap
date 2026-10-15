namespace WhSnap.Core;

/// <summary>标注工具种类（规划 §2.1 E 的十种工具 + 序号/高亮）。</summary>
public enum AnnotationKind
{
    /// <summary>矩形框（可填充）。</summary>
    Rectangle,

    Ellipse,

    Line,

    Arrow,

    /// <summary>铅笔：细的不透明自由笔。</summary>
    Pencil,

    /// <summary>马克笔：粗的半透明自由笔。</summary>
    Marker,

    Text,

    /// <summary>马赛克（像素化）。</summary>
    Mosaic,

    /// <summary>高斯模糊（盒式近似）。</summary>
    Blur,

    /// <summary>橡皮擦：擦掉已画的标注像素。</summary>
    Eraser,

    /// <summary>序号：自动递增的圆形数字标签。</summary>
    Badge,

    /// <summary>高亮：半透明粗线，像荧光笔。</summary>
    Highlight,
}

/// <summary>图元样式。像素级效果（马赛克块大小、模糊半径）也在这里，便于"事后调整"。</summary>
public sealed record AnnotationStyle
{
    public static AnnotationStyle Default { get; } = new();

    public RgbColor Stroke { get; init; } = new(0xFF, 0x3B, 0x30);

    public RgbColor Fill { get; init; } = new(0xFF, 0x3B, 0x30);

    /// <summary>描边宽度（物理像素）。</summary>
    public int StrokeWidth { get; init; } = 3;

    /// <summary>填充不透明度（0 = 不填充，1-100 为百分比）。</summary>
    public int FillOpacity { get; init; }

    public string FontFamily { get; init; } = "Microsoft YaHei UI";

    public double FontSize { get; init; } = 18;

    /// <summary>马赛克块边长（物理像素）。</summary>
    public int MosaicBlock { get; init; } = PixelEffects.DefaultMosaicBlock;

    /// <summary>模糊半径（物理像素）。</summary>
    public int BlurRadius { get; init; } = PixelEffects.DefaultBlurRadius;

    /// <summary>橡皮擦半径（物理像素）。</summary>
    public int EraserRadius { get; init; } = 12;

    public AnnotationStyle WithWidth(int width) => this with { StrokeWidth = Math.Clamp(width, 1, 64) };

    public AnnotationStyle WithStroke(RgbColor color) => this with { Stroke = color, Fill = color };

    public AnnotationStyle WithFillOpacity(int opacity) => this with { FillOpacity = Math.Clamp(opacity, 0, 100) };
}

/// <summary>
/// 一个标注图元。**不可变**（record）：撤销栈、二次编辑、导出都靠持有快照实现，
/// 因此不需要深拷贝像素，也不会出现"撤销后图元被后续编辑带偏"的问题。
/// </summary>
public sealed record AnnotationItem
{
    public int Id { get; init; }

    public AnnotationKind Kind { get; init; }

    /// <summary>几何：两点图元是起点/终点，自由笔是折线，橡皮擦是笔迹路径；序号/文字只用第一个点。</summary>
    public IReadOnlyList<PhysicalPoint> Points { get; init; } = [];

    public string Text { get; init; } = string.Empty;

    /// <summary>序号图元的编号（添加时自动递增）。</summary>
    public int Badge { get; init; }

    public AnnotationStyle Style { get; init; } = AnnotationStyle.Default;

    /// <summary>是否为"两点拖拽"型图元（矩形/椭圆/直线/箭头/马赛克/模糊）。</summary>
    public static bool IsTwoPoint(AnnotationKind kind) => kind
        is AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Line
        or AnnotationKind.Arrow or AnnotationKind.Mosaic or AnnotationKind.Blur;

    /// <summary>是否为自由笔迹（铅笔/马克笔/橡皮擦/高亮）。</summary>
    public static bool IsStroke(AnnotationKind kind) => kind
        is AnnotationKind.Pencil or AnnotationKind.Marker or AnnotationKind.Eraser or AnnotationKind.Highlight;

    /// <summary>是否必须在"像素缓冲"上生效（而不是矢量绘制）。</summary>
    public static bool IsPixelEffect(AnnotationKind kind) => kind
        is AnnotationKind.Mosaic or AnnotationKind.Blur or AnnotationKind.Eraser;

    public PhysicalPoint Start => Points.Count > 0 ? Points[0] : default;

    public PhysicalPoint End => Points.Count > 1 ? Points[^1] : Start;

    /// <summary>
    /// 图元边界（物理像素）。文字/序号的边界用字号估算——
    /// 精确边界需要字体度量，那属于渲染层；命中测试容差足够吸收这点误差。
    /// </summary>
    public PhysicalRect Bounds
    {
        get
        {
            if (Points.Count == 0)
            {
                return default;
            }

            if (Kind == AnnotationKind.Badge)
            {
                double size = Style.FontSize * 1.6;
                var center = Points[0];
                return new PhysicalRect(
                    center.X - (int)(size / 2),
                    center.Y - (int)(size / 2),
                    (int)size,
                    (int)size);
            }

            if (Kind == AnnotationKind.Text)
            {
                double height = Style.FontSize * 1.4;
                double width = Math.Max(Text.Length, 1) * Style.FontSize * 0.62;
                return new PhysicalRect(Points[0].X, Points[0].Y, (int)Math.Ceiling(width), (int)Math.Ceiling(height));
            }

            if (IsTwoPoint(Kind))
            {
                return PhysicalRect.FromPoints(Start.X, Start.Y, End.X, End.Y);
            }

            int left = int.MaxValue;
            int top = int.MaxValue;
            int right = int.MinValue;
            int bottom = int.MinValue;

            foreach (var point in Points)
            {
                left = Math.Min(left, point.X);
                top = Math.Min(top, point.Y);
                right = Math.Max(right, point.X);
                bottom = Math.Max(bottom, point.Y);
            }

            int inflate = Math.Max(Style.StrokeWidth, Kind == AnnotationKind.Eraser ? Style.EraserRadius : Style.StrokeWidth) / 2 + 1;
            return new PhysicalRect(left - inflate, top - inflate, (right - left) + (inflate * 2), (bottom - top) + (inflate * 2));
        }
    }

    /// <summary>是否"太小"到不值得保留（误点、手抖）。</summary>
    public bool IsDegenerate
    {
        get
        {
            if (Points.Count == 0)
            {
                return true;
            }

            if (Kind == AnnotationKind.Text)
            {
                return string.IsNullOrWhiteSpace(Text);
            }

            if (Kind == AnnotationKind.Badge)
            {
                return false;
            }

            if (IsTwoPoint(Kind))
            {
                return Math.Abs(End.X - Start.X) < 2 && Math.Abs(End.Y - Start.Y) < 2;
            }

            return Points.Count < 2;
        }
    }

    public static AnnotationItem Create(
        int id,
        AnnotationKind kind,
        IEnumerable<PhysicalPoint> points,
        AnnotationStyle style,
        string text = "",
        int badge = 0) => new()
    {
        Id = id,
        Kind = kind,
        Points = points.ToList(),
        Style = style,
        Text = text,
        Badge = badge,
    };

    public static AnnotationItem FromDrag(int id, AnnotationKind kind, PhysicalPoint start, PhysicalPoint end, AnnotationStyle style) =>
        Create(id, kind, [start, end], style);

    public override string ToString() =>
        $"#{Id} {Kind} {Bounds}" + (Text.Length > 0 ? $" “{Text}”" : string.Empty) + (Badge > 0 ? $" [{Badge}]" : string.Empty);
}

/// <summary>
/// 标注文档 + 撤销/重做命令栈。
///
/// 规划 §5.6 要求"命令栈撤销（非位图快照，省内存且支持二次编辑）"：
/// 图元本身不可变，所以一次快照只是一个**指针数组**（100 个图元 = 100 个引用，几 KB 量级），
/// 既不复制像素又能保证撤销后可以继续二次编辑。
/// </summary>
public sealed class AnnotationDocument
{
    public const int MaxHistory = 200;

    private readonly List<AnnotationItem> _items = [];
    private readonly List<List<AnnotationItem>> _undo = [];
    private readonly List<List<AnnotationItem>> _redo = [];

    /// <summary>序号起始值（可配置）。</summary>
    public int BadgeStart { get; private set; } = 1;

    private int _nextBadge = 1;
    private int _nextId = 1;

    public IReadOnlyList<AnnotationItem> Items => _items;

    public int Count => _items.Count;

    public bool IsEmpty => _items.Count == 0;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int UndoDepth => _undo.Count;

    public int RedoDepth => _redo.Count;

    /// <summary>重置文档（换一张图继续标注时用）。</summary>
    public void Reset(int badgeStart = 1)
    {
        _items.Clear();
        _undo.Clear();
        _redo.Clear();
        _nextId = 1;
        BadgeStart = Math.Max(badgeStart, 1);
        _nextBadge = BadgeStart;
    }

    public AnnotationItem Add(AnnotationKind kind, IEnumerable<PhysicalPoint> points, AnnotationStyle style, string text = "")
    {
        var item = AnnotationItem.Create(_nextId++, kind, points, style, text, kind == AnnotationKind.Badge ? _nextBadge++ : 0);
        Push(_items);
        _items.Add(item);
        _redo.Clear();
        return item;
    }

    /// <summary>直接加入一个图元（撤销一支笔、粘贴图元等场景）。</summary>
    public AnnotationItem Add(AnnotationItem item)
    {
        var normalized = item.Id == 0 ? item with { Id = _nextId++ } : item;
        _nextId = Math.Max(_nextId, normalized.Id + 1);

        Push(_items);
        _items.Add(normalized);
        _redo.Clear();
        return normalized;
    }

    /// <summary>替换图元（二次编辑：移动/缩放/改样式/改文字）。</summary>
    public bool Update(AnnotationItem item)
    {
        int index = _items.FindIndex(existing => existing.Id == item.Id);
        if (index < 0)
        {
            return false;
        }

        Push(_items);
        _items[index] = item;
        _redo.Clear();
        return true;
    }

    public bool Remove(int id)
    {
        int index = _items.FindIndex(existing => existing.Id == id);
        if (index < 0)
        {
            return false;
        }

        Push(_items);
        _items.RemoveAt(index);
        _redo.Clear();
        return true;
    }

    /// <summary>清空全部图元（可撤销）。</summary>
    public bool Clear()
    {
        if (_items.Count == 0)
        {
            return false;
        }

        Push(_items);
        _items.Clear();
        _redo.Clear();
        return true;
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        var snapshot = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add([.. _items]);
        Replace(snapshot);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        var snapshot = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add([.. _items]);
        Replace(snapshot);
        return true;
    }

    public AnnotationItem? Find(int id) => _items.FirstOrDefault(item => item.Id == id);

    /// <summary>命中最上层的图元（后画的在上）。</summary>
    public AnnotationItem? Topmost(PhysicalPoint point, int tolerance) =>
        AnnotationEditing.HitTest(_items, point, tolerance)?.Item;

    private void Push(List<AnnotationItem> current)
    {
        _undo.Add([.. current]);

        if (_undo.Count > MaxHistory)
        {
            _undo.RemoveAt(0);
        }
    }

    private void Replace(List<AnnotationItem> snapshot)
    {
        _items.Clear();
        _items.AddRange(snapshot);
    }
}

/// <summary>图元上可抓取的部位。</summary>
public enum AnnotationHandle
{
    None,
    Start,
    End,
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft,
    Inside,
}

public sealed record AnnotationHit(AnnotationItem Item, AnnotationHandle Handle);

/// <summary>图元的命中测试与二次编辑（移动/缩放/改样式）。纯逻辑，可单测。</summary>
public static class AnnotationEditing
{
    public static AnnotationHit? HitTest(IReadOnlyList<AnnotationItem> items, PhysicalPoint point, int tolerance)
    {
        ArgumentNullException.ThrowIfNull(items);

        for (int i = items.Count - 1; i >= 0; i--)
        {
            var handle = HandleAt(items[i], point, tolerance);
            if (handle != AnnotationHandle.None)
            {
                return new AnnotationHit(items[i], handle);
            }
        }

        return null;
    }

    /// <summary>判断点在某个图元的哪个部位上（角 → 边/端点 → 内部）。</summary>
    public static AnnotationHandle HandleAt(AnnotationItem item, PhysicalPoint point, int tolerance)
    {
        ArgumentNullException.ThrowIfNull(item);

        tolerance = Math.Max(tolerance, 2);
        var bounds = item.Bounds;

        // 两点图元允许"退化"边界：水平直线高度为 0、垂直线宽度为 0，但都必须能命中
        if (bounds.IsEmpty && !AnnotationItem.IsTwoPoint(item.Kind) && item.Kind != AnnotationKind.Badge && item.Kind != AnnotationKind.Text)
        {
            return AnnotationHandle.None;
        }

        if (AnnotationItem.IsTwoPoint(item.Kind))
        {
            bool nearStartX = Math.Abs(point.X - bounds.X) <= tolerance;
            bool nearStartY = Math.Abs(point.Y - bounds.Y) <= tolerance;
            bool nearEndX = Math.Abs(point.X - bounds.Right) <= tolerance;
            bool nearEndY = Math.Abs(point.Y - bounds.Bottom) <= tolerance;
            bool withinX = point.X >= bounds.X - tolerance && point.X <= bounds.Right + tolerance;
            bool withinY = point.Y >= bounds.Y - tolerance && point.Y <= bounds.Bottom + tolerance;

            if (!withinX || !withinY)
            {
                return AnnotationHandle.None;
            }

            if (nearStartX && nearStartY)
            {
                return AnnotationHandle.TopLeft;
            }

            if (nearEndX && nearStartY)
            {
                return AnnotationHandle.TopRight;
            }

            if (nearStartX && nearEndY)
            {
                return AnnotationHandle.BottomLeft;
            }

            if (nearEndX && nearEndY)
            {
                return AnnotationHandle.BottomRight;
            }

            // 直线/箭头只认两个端点
            if (item.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
            {
                if (DistanceToSegment(point, item.Start, item.End) <= tolerance + (item.Style.StrokeWidth / 2))
                {
                    return AnnotationHandle.Inside;
                }

                return AnnotationHandle.None;
            }

            return AnnotationHandle.Inside;
        }

        if (AnnotationItem.IsStroke(item.Kind))
        {
            return DistanceToPolyline(point, item.Points) <= tolerance + (StrokeRadius(item)) ? AnnotationHandle.Inside : AnnotationHandle.None;
        }

        // 文字 / 序号：整体移动
        var expanded = new PhysicalRect(
            bounds.X - tolerance,
            bounds.Y - tolerance,
            bounds.Width + (tolerance * 2),
            bounds.Height + (tolerance * 2));

        return Contains(expanded, point) ? AnnotationHandle.Inside : AnnotationHandle.None;
    }

    /// <summary>整体平移。</summary>
    public static AnnotationItem Move(AnnotationItem item, int dx, int dy)
    {
        ArgumentNullException.ThrowIfNull(item);

        var points = item.Points.Select(point => new PhysicalPoint(point.X + dx, point.Y + dy)).ToList();
        return item with { Points = points };
    }

    /// <summary>拖动某个手柄改变几何。自由笔迹按比例缩放到新边界，两点图元改对应角。</summary>
    public static AnnotationItem Resize(AnnotationItem item, AnnotationHandle handle, PhysicalPoint point)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (handle is AnnotationHandle.None or AnnotationHandle.Inside)
        {
            return item;
        }

        if (AnnotationItem.IsTwoPoint(item.Kind))
        {
            var start = item.Start;
            var end = item.End;

            // 自由排序，保证 start 永远是左上、end 是右下（视觉上等价，导出时用 FromPoints 归一化）
            int left = Math.Min(start.X, end.X);
            int top = Math.Min(start.Y, end.Y);
            int right = Math.Max(start.X, end.X);
            int bottom = Math.Max(start.Y, end.Y);

            switch (handle)
            {
                case AnnotationHandle.TopLeft:
                    left = point.X;
                    top = point.Y;
                    break;
                case AnnotationHandle.TopRight:
                    right = point.X;
                    top = point.Y;
                    break;
                case AnnotationHandle.BottomLeft:
                    left = point.X;
                    bottom = point.Y;
                    break;
                case AnnotationHandle.BottomRight:
                    right = point.X;
                    bottom = point.Y;
                    break;
                case AnnotationHandle.Start:
                    (left, top) = (point.X, point.Y);
                    break;
                case AnnotationHandle.End:
                    (right, bottom) = (point.X, point.Y);
                    break;
            }

            int x1 = Math.Min(left, right);
            int x2 = Math.Max(left, right);
            int y1 = Math.Min(top, bottom);
            int y2 = Math.Max(top, bottom);

            return item with { Points = [new PhysicalPoint(x1, y1), new PhysicalPoint(x2, y2)] };
        }

        if (AnnotationItem.IsStroke(item.Kind) && item.Points.Count > 0)
        {
            var bounds = item.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return item;
            }

            int left = bounds.X;
            int top = bounds.Y;
            int right = bounds.Right;
            int bottom = bounds.Bottom;

            switch (handle)
            {
                case AnnotationHandle.TopLeft:
                    left = point.X;
                    top = point.Y;
                    break;
                case AnnotationHandle.TopRight:
                    right = point.X;
                    top = point.Y;
                    break;
                case AnnotationHandle.BottomLeft:
                    left = point.X;
                    bottom = point.Y;
                    break;
                case AnnotationHandle.BottomRight:
                    right = point.X;
                    bottom = point.Y;
                    break;
            }

            int newWidth = Math.Max(right - left, 1);
            int newHeight = Math.Max(bottom - top, 1);
            double scaleX = newWidth / (double)bounds.Width;
            double scaleY = newHeight / (double)bounds.Height;

            var points = item.Points
                .Select(p => new PhysicalPoint(
                    left + (int)Math.Round((p.X - bounds.X) * scaleX),
                    top + (int)Math.Round((p.Y - bounds.Y) * scaleY)))
                .ToList();

            return item with { Points = points };
        }

        return item;
    }

    /// <summary>改样式（颜色/线宽/字体/块大小）。</summary>
    public static AnnotationItem Restyle(AnnotationItem item, AnnotationStyle style)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item with { Style = style };
    }

    public static AnnotationItem SetText(AnnotationItem item, string text)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item with { Text = text };
    }

    private static int StrokeRadius(AnnotationItem item) => item.Kind switch
    {
        AnnotationKind.Eraser => item.Style.EraserRadius,
        AnnotationKind.Marker or AnnotationKind.Highlight => Math.Max(item.Style.StrokeWidth * 2, 6),
        _ => Math.Max(item.Style.StrokeWidth, 2),
    };

    private static bool Contains(PhysicalRect rect, PhysicalPoint point) =>
        point.X >= rect.X && point.X <= rect.Right && point.Y >= rect.Y && point.Y <= rect.Bottom;

    private static double DistanceToSegment(PhysicalPoint point, PhysicalPoint from, PhysicalPoint to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double lengthSquared = (dx * dx) + (dy * dy);

        if (lengthSquared < 1e-9)
        {
            return Math.Sqrt(((point.X - from.X) * (point.X - from.X)) + ((point.Y - from.Y) * (point.Y - from.Y)));
        }

        double t = Math.Clamp((((point.X - from.X) * dx) + ((point.Y - from.Y) * dy)) / lengthSquared, 0, 1);
        double nearestX = from.X + (t * dx);
        double nearestY = from.Y + (t * dy);
        return Math.Sqrt(((point.X - nearestX) * (point.X - nearestX)) + ((point.Y - nearestY) * (point.Y - nearestY)));
    }

    private static double DistanceToPolyline(PhysicalPoint point, IReadOnlyList<PhysicalPoint> points)
    {
        if (points.Count == 0)
        {
            return double.MaxValue;
        }

        if (points.Count == 1)
        {
            return DistanceToSegment(point, points[0], points[0]);
        }

        double best = double.MaxValue;

        for (int i = 0; i < points.Count - 1; i++)
        {
            best = Math.Min(best, DistanceToSegment(point, points[i], points[i + 1]));
        }

        return best;
    }
}
