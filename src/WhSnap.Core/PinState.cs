namespace WhSnap.Core;

/// <summary>贴图旋转角（顺时针四分之一圈为单位）。</summary>
public enum PinRotation
{
    None = 0,
    Clockwise90 = 1,
    Clockwise180 = 2,
    Clockwise270 = 3,
}

/// <summary>
/// 一张贴图的完整状态。所有几何量都是**虚拟桌面绝对物理像素**，
/// 与截图、遮罩共用同一坐标口径，因此多屏 + 混合 DPI 下贴图不会错位。
///
/// 这是不可变值对象：所有编辑都返回新实例（record with），
/// 使得"撤销/备份/拓扑重算"都可以安全地持有快照。
/// </summary>
public sealed record PinState
{
    /// <summary>贴图左上角在虚拟桌面中的绝对物理坐标。</summary>
    public PhysicalPoint Position { get; init; }

    /// <summary>原始图像宽度（未旋转、未缩放）。</summary>
    public int SourceWidth { get; init; }

    /// <summary>原始图像高度（未旋转、未缩放）。</summary>
    public int SourceHeight { get; init; }

    /// <summary>用户缩放倍率（1.0 = 原始像素 1:1）。</summary>
    public double Zoom { get; init; } = 1.0;

    public PinRotation Rotation { get; init; }

    public bool FlipHorizontal { get; init; }

    public bool FlipVertical { get; init; }

    /// <summary>不透明度百分比：0（全透明）～100（不透明）。</summary>
    public int Opacity { get; init; } = 100;

    /// <summary>鼠标穿透：开启后点击直接落到下层窗口，只能通过托盘/热键关闭。</summary>
    public bool ClickThrough { get; init; }

    /// <summary>隐藏（双击贴图隐藏，可从托盘或贴图面板恢复）。</summary>
    public bool Hidden { get; init; }

    /// <summary>缩略图模式（Shift+双击切换）：按固定比例缩小显示，保留原始像素数据。</summary>
    public bool Thumbnail { get; init; }

    /// <summary>从原始尺寸创建一张位于指定位置的贴图。</summary>
    public static PinState ForImage(int sourceWidth, int sourceHeight, PhysicalPoint position) => new()
    {
        Position = position,
        SourceWidth = Math.Max(sourceWidth, 1),
        SourceHeight = Math.Max(sourceHeight, 1),
    };

    public bool IsValid => SourceWidth > 0 && SourceHeight > 0;

    public override string ToString() =>
        $"{Bounds} zoom={Zoom:0.##} rot={Rotation}{(FlipHorizontal ? " H" : string.Empty)}{(FlipVertical ? " V" : string.Empty)} alpha={Opacity}%"
        + (Thumbnail ? " 缩略图" : string.Empty)
        + (ClickThrough ? " 穿透" : string.Empty)
        + (Hidden ? " 隐藏" : string.Empty);

    /// <summary>当前几何边界（= PinGeometry.Bounds 的便捷入口）。</summary>
    public PhysicalRect Bounds => PinGeometry.Bounds(this);
}

/// <summary>
/// 贴图几何与编辑运算。全部为纯函数，不依赖 UI，可直接单测——
/// 缩放锚点、旋转后的中心保持、边缘吸附、显示器拓扑变化重算都在这里保证正确性。
/// </summary>
public static class PinGeometry
{
    public const double MinZoom = 0.05;
    public const double MaxZoom = 32.0;

    /// <summary>缩略图模式相对当前缩放再缩小的比例。</summary>
    public const double ThumbnailFactor = 0.25;

    /// <summary>滚轮一格的默认缩放步进。</summary>
    public const double DefaultZoomStep = 1.1;

    /// <summary>旋转后的原始尺寸（90/270 交换宽高）。</summary>
    public static (int Width, int Height) RotatedSourceSize(PinState state) =>
        state.Rotation is PinRotation.Clockwise90 or PinRotation.Clockwise270
            ? (state.SourceHeight, state.SourceWidth)
            : (state.SourceWidth, state.SourceHeight);

    /// <summary>实际显示倍率（缩略图模式下为 Zoom × ThumbnailFactor）。</summary>
    public static double EffectiveZoom(PinState state)
    {
        double zoom = ClampZoom(state.Zoom);
        return state.Thumbnail ? ClampZoom(zoom * ThumbnailFactor) : zoom;
    }

    public static int RotationDegrees(PinState state) => (int)state.Rotation * 90;

    /// <summary>贴图在屏幕上占据的物理矩形（宽高至少 1 像素）。</summary>
    public static PhysicalRect Bounds(PinState state)
    {
        var (width, height) = RotatedSourceSize(state);
        double zoom = EffectiveZoom(state);
        int displayWidth = Math.Max((int)Math.Round(width * zoom, MidpointRounding.AwayFromZero), 1);
        int displayHeight = Math.Max((int)Math.Round(height * zoom, MidpointRounding.AwayFromZero), 1);
        return new PhysicalRect(state.Position.X, state.Position.Y, displayWidth, displayHeight);
    }

    /// <summary>显示尺寸（等价于 Bounds 的宽高）。</summary>
    public static (int Width, int Height) DisplaySize(PinState state)
    {
        var bounds = Bounds(state);
        return (bounds.Width, bounds.Height);
    }

    public static PhysicalPoint Center(PinState state)
    {
        var bounds = Bounds(state);
        return new PhysicalPoint(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
    }

    public static bool Contains(PinState state, PhysicalPoint point)
    {
        var bounds = Bounds(state);
        return point.X >= bounds.X && point.X < bounds.Right
               && point.Y >= bounds.Y && point.Y < bounds.Bottom;
    }

    public static double ClampZoom(double zoom) =>
        double.IsNaN(zoom) ? 1.0 : Math.Clamp(zoom, MinZoom, MaxZoom);

    /// <summary>按倍率缩放，并保持 anchor（通常是光标位置）下的那个像素不动。</summary>
    public static PinState ZoomBy(PinState state, double factor, PhysicalPoint anchor, PhysicalRect? desktop = null)
    {
        if (factor <= 0 || double.IsNaN(factor))
        {
            return state;
        }

        return ZoomTo(state, state.Zoom * factor, anchor, desktop);
    }

    /// <summary>滚轮缩放：steps 为正放大、为负缩小。</summary>
    public static PinState ZoomSteps(PinState state, int steps, PhysicalPoint anchor, double step = DefaultZoomStep, PhysicalRect? desktop = null)
    {
        if (steps == 0)
        {
            return state;
        }

        step = step <= 0 ? DefaultZoomStep : step;
        return ZoomBy(state, Math.Pow(step, steps), anchor, desktop);
    }

    /// <summary>缩放到指定倍率，保持 anchor 下的像素不动。</summary>
    public static PinState ZoomTo(PinState state, double zoom, PhysicalPoint anchor, PhysicalRect? desktop = null)
    {
        double target = ClampZoom(zoom);

        if (Math.Abs(target - state.Zoom) < 1e-9)
        {
            return state;
        }

        double ratio = target / state.Zoom;
        double dx = anchor.X - state.Position.X;
        double dy = anchor.Y - state.Position.Y;

        var next = state with
        {
            Zoom = target,
            Position = new PhysicalPoint(
                anchor.X - (int)Math.Round(dx * ratio, MidpointRounding.AwayFromZero),
                anchor.Y - (int)Math.Round(dy * ratio, MidpointRounding.AwayFromZero)),
        };

        return desktop is { } bounds ? EnsureVisible(next, bounds) : next;
    }

    /// <summary>把显示尺寸设置为指定的物理像素尺寸（保持宽高比，左上角不动）。</summary>
    public static PinState ResizeTo(PinState state, int width, int height)
    {
        var (sourceWidth, _) = RotatedSourceSize(state);
        if (sourceWidth <= 0)
        {
            return state;
        }

        // 以较大的那个方向为准，避免极端比例下抖动
        double byWidth = Math.Max(width, 1) / (double)sourceWidth;
        var (rotatedWidth, rotatedHeight) = RotatedSourceSize(state);
        double byHeight = Math.Max(height, 1) / (double)rotatedHeight;

        double zoom = Math.Abs(byWidth - byHeight) <= 0.02 * Math.Max(byWidth, byHeight)
            ? (byWidth + byHeight) / 2
            : byWidth;

        return state with { Zoom = ClampZoom(zoom), Thumbnail = false };
    }

    /// <summary>顺时针旋转 90°，围绕中心旋转（贴图不会因尺寸交换而跳位）。</summary>
    public static PinState RotateClockwise(PinState state)
    {
        var center = Center(state);
        var next = state with
        {
            Rotation = (PinRotation)(((int)state.Rotation + 1) % 4),
        };

        return CenterOn(next, center);
    }

    /// <summary>旋转回上一档（逆时针 90°）。</summary>
    public static PinState RotateCounterClockwise(PinState state)
    {
        var center = Center(state);
        var next = state with
        {
            Rotation = (PinRotation)(((int)state.Rotation + 3) % 4),
        };

        return CenterOn(next, center);
    }

    public static PinState FlipHorizontally(PinState state) => state with { FlipHorizontal = !state.FlipHorizontal };

    public static PinState FlipVertically(PinState state) => state with { FlipVertical = !state.FlipVertical };

    /// <summary>把贴图中心对齐到指定点（旋转后保持视觉中心不变）。</summary>
    public static PinState CenterOn(PinState state, PhysicalPoint center)
    {
        var bounds = Bounds(state);
        return state with
        {
            Position = new PhysicalPoint(
                center.X - (bounds.Width / 2),
                center.Y - (bounds.Height / 2)),
        };
    }

    public static PinState MoveTo(PinState state, PhysicalPoint position) => state with { Position = position };

    public static PinState MoveBy(PinState state, int dx, int dy) =>
        state with { Position = new PhysicalPoint(state.Position.X + dx, state.Position.Y + dy) };

    public static PinState SetOpacity(PinState state, int opacity) =>
        state with { Opacity = Math.Clamp(opacity, 0, 100) };

    /// <summary>调整不透明度（Ctrl+滚轮），step 为百分比增量。</summary>
    public static PinState AdjustOpacity(PinState state, int delta) => SetOpacity(state, state.Opacity + delta);

    public static PinState SetClickThrough(PinState state, bool enabled) => state with { ClickThrough = enabled };

    public static PinState SetHidden(PinState state, bool hidden) => state with { Hidden = hidden };

    /// <summary>切换缩略图模式，同时保持中心不变。</summary>
    public static PinState SetThumbnail(PinState state, bool thumbnail)
    {
        if (state.Thumbnail == thumbnail)
        {
            return state;
        }

        var center = Center(state);
        return CenterOn(state with { Thumbnail = thumbnail }, center);
    }

    public static PinState ToggleThumbnail(PinState state) => SetThumbnail(state, !state.Thumbnail);

    /// <summary>恢复 100% 显示（保持中心）。</summary>
    public static PinState ResetZoom(PinState state)
    {
        var center = Center(state);
        return CenterOn(state with { Zoom = 1.0, Thumbnail = false }, center);
    }

    /// <summary>
    /// 保证贴图至少有一部分落在桌面范围内；完全跑出去就移回桌面左上角附近。
    /// 用于显示器拔插、分辨率变化之后。
    /// </summary>
    public static PinState EnsureVisible(PinState state, PhysicalRect desktop, int margin = 24)
    {
        if (desktop.IsEmpty)
        {
            return state;
        }

        var bounds = Bounds(state);

        if (bounds.Intersect(desktop).IsEmpty)
        {
            return state with
            {
                Position = new PhysicalPoint(
                    Math.Clamp(desktop.X + margin, desktop.X, Math.Max(desktop.Right - bounds.Width, desktop.X)),
                    Math.Clamp(desktop.Y + margin, desktop.Y, Math.Max(desktop.Bottom - bounds.Height, desktop.Y))),
            };
        }

        int maxX = Math.Max(desktop.Right - bounds.Width, desktop.X);
        int maxY = Math.Max(desktop.Bottom - bounds.Height, desktop.Y);

        int x = bounds.Width >= desktop.Width
            ? desktop.X
            : Math.Clamp(bounds.X, desktop.X - bounds.Width + margin, maxX);

        int y = bounds.Height >= desktop.Height
            ? desktop.Y
            : Math.Clamp(bounds.Y, desktop.Y - bounds.Height + margin, maxY);

        return x == state.Position.X && y == state.Position.Y
            ? state
            : state with { Position = new PhysicalPoint(x, y) };
    }

    /// <summary>
    /// 边缘吸附：把贴图最近的一条边吸附到任意显示器的对应边（阈值内）。
    /// 只需给出目标位置，由调用方决定是否应用（拖动结束时）。
    /// </summary>
    public static PinState SnapToEdges(
        PinState state,
        IReadOnlyList<MonitorInfo> monitors,
        int tolerance,
        out bool snappedX,
        out bool snappedY)
    {
        snappedX = false;
        snappedY = false;

        if (monitors.Count == 0 || tolerance < 0)
        {
            return state;
        }

        var bounds = Bounds(state);
        int bestDx = 0;
        int bestX = int.MaxValue;
        int bestDy = 0;
        int bestY = int.MaxValue;

        foreach (var monitor in monitors)
        {
            foreach (int edgeX in new[] { monitor.Bounds.X, monitor.Bounds.Right })
            {
                Consider(edgeX - bounds.X, ref bestDx, ref bestX);
                Consider(edgeX - bounds.Right, ref bestDx, ref bestX);
            }

            foreach (int edgeY in new[] { monitor.Bounds.Y, monitor.Bounds.Bottom })
            {
                Consider(edgeY - bounds.Y, ref bestDy, ref bestY);
                Consider(edgeY - bounds.Bottom, ref bestDy, ref bestY);
            }
        }

        int x = state.Position.X;
        int y = state.Position.Y;

        if (bestX != int.MaxValue && Math.Abs(bestDx) <= tolerance)
        {
            x += bestDx;
            snappedX = true;
        }

        if (bestY != int.MaxValue && Math.Abs(bestDy) <= tolerance)
        {
            y += bestDy;
            snappedY = true;
        }

        return snappedX || snappedY ? state with { Position = new PhysicalPoint(x, y) } : state;

        static void Consider(int delta, ref int bestDelta, ref int bestDistance)
        {
            int distance = Math.Abs(delta);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestDelta = delta;
            }
        }
    }

    /// <summary>
    /// 显示器拓扑变化后的位置重算。
    ///
    /// 规则（按"贴图尽量待在原地"的直觉）：
    ///   * 桌面整体平移（副屏从左侧拔掉/插上使原点变化）→ 位置随之平移；
    ///   * 桌面变大（插了显示器、分辨率提高）→ **不做等比放大**，贴图保持原绝对坐标；
    ///   * 桌面变小（分辨率降低、显示器被拔掉）→ 等比缩小，避免贴图跑到桌面外；
    ///   * 最后统一保证仍可见。
    /// </summary>
    public static PinState ReconcileTopology(PinState state, PhysicalRect oldDesktop, PhysicalRect newDesktop)
    {
        if (newDesktop.IsEmpty)
        {
            return state;
        }

        if (oldDesktop.IsEmpty || oldDesktop == newDesktop)
        {
            return EnsureVisible(state, newDesktop);
        }

        double scaleX = newDesktop.Width < oldDesktop.Width ? newDesktop.Width / (double)oldDesktop.Width : 1.0;
        double scaleY = newDesktop.Height < oldDesktop.Height ? newDesktop.Height / (double)oldDesktop.Height : 1.0;

        var mapped = state with
        {
            Position = new PhysicalPoint(
                newDesktop.X + (int)Math.Round((state.Position.X - oldDesktop.X) * scaleX, MidpointRounding.AwayFromZero),
                newDesktop.Y + (int)Math.Round((state.Position.Y - oldDesktop.Y) * scaleY, MidpointRounding.AwayFromZero)),
        };

        return EnsureVisible(mapped, newDesktop);
    }
}
