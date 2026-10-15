using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhSnap.Core;
using WhSnap.Services;

namespace WhSnap.Annotation;

/// <summary>
/// 标注合成管线：把"不可变底图 + 图元列表"按顺序合成为一张**物理像素 1:1** 的位图。
///
/// 关键设计（对应规划 §5.6）：
///   * 图元按顺序处理；矢量图元批量画进一个图层再合成，**像素效果（马赛克/模糊/橡皮擦）必须在中间打断**，
///     因为它要作用于"当前已经画出来的内容"；
///   * 橡皮擦的语义是**只擦掉标注像素**（自己画的线），底图绝不能被擦出洞：
///     橡皮擦清的是"标注层"，然后底图重新合成；
///   * 马赛克/模糊作用在"当前可见画面"上（含已画的标注），符合"给这块打码"的直觉。
/// </summary>
internal static class AnnotationRenderer
{
    /// <summary>合成结果（1:1 物理像素，Bgra32 直通 alpha）。</summary>
    public static BitmapSource Compose(BitmapSource baseImage, IReadOnlyList<AnnotationItem> items)
    {
        ArgumentNullException.ThrowIfNull(baseImage);
        ArgumentNullException.ThrowIfNull(items);

        int width = baseImage.PixelWidth;
        int height = baseImage.PixelHeight;
        int stride = width * 4;

        // 底图（直通 alpha 的 Bgra32），以及"标注层"（底图透明）
        var basePixels = new byte[stride * height];
        SaveService.Normalize(baseImage).CopyPixels(basePixels, stride, 0);

        var annotations = new byte[basePixels.Length];
        var composited = new byte[basePixels.Length];
        Buffer.BlockCopy(basePixels, 0, composited, 0, basePixels.Length);

        var pending = new List<AnnotationItem>();

        foreach (var item in items)
        {
            if (!AnnotationItem.IsPixelEffect(item.Kind))
            {
                pending.Add(item);
                continue;
            }

            FlushVectorLayer(pending, annotations, width, height, stride);
            pending.Clear();

            if (item.Kind == AnnotationKind.Eraser)
            {
                // 只擦标注层：底图完好，擦完重新合成
                PixelEffects.EraseStroke(annotations, width, height, stride, item.Points, item.Style.EraserRadius);
                Buffer.BlockCopy(basePixels, 0, composited, 0, basePixels.Length);
                PixelBlend.SourceOver(composited, annotations, composited.Length);
            }
            else
            {
                var region = item.Bounds;
                if (item.Kind == AnnotationKind.Mosaic)
                {
                    PixelEffects.Mosaic(composited, width, height, stride, region, item.Style.MosaicBlock);
                }
                else
                {
                    PixelEffects.BoxBlur(composited, width, height, stride, region, item.Style.BlurRadius);
                }
            }
        }

        FlushVectorLayer(pending, annotations, width, height, stride);

        // 最后一轮：把标注层的剩余内容贴到合成结果上
        var result = new byte[basePixels.Length];
        Buffer.BlockCopy(composited, 0, result, 0, composited.Length);
        PixelBlend.SourceOver(result, annotations, result.Length);

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, result, stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>把一批连续的矢量图元画进标注层（只渲染它们的外接矩形，避免整图重画）。</summary>
    private static void FlushVectorLayer(List<AnnotationItem> items, byte[] annotations, int width, int height, int stride)
    {
        if (items.Count == 0)
        {
            return;
        }

        var bounds = UnionBounds(items, width, height);
        if (bounds.IsEmpty)
        {
            return;
        }

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            AnnotationDrawing.Draw(dc, items);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(bounds.Width, bounds.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        int layerStride = bounds.Width * 4;
        var layer = new byte[layerStride * bounds.Height];
        bitmap.CopyPixels(layer, layerStride, 0);

        // 只把这一块就地叠加到标注层的对应位置
        for (int y = 0; y < bounds.Height; y++)
        {
            int sourceIndex = y * layerStride;
            int targetIndex = ((bounds.Y + y) * stride) + (bounds.X * 4);
            PixelBlend.SourceOver(annotations, targetIndex, layer, sourceIndex, bounds.Width * 4);
        }
    }

    /// <summary>把所有图元的外接矩形并起来（像素效果图元不参与），并裁到图像范围内。</summary>
    private static PhysicalRect UnionBounds(List<AnnotationItem> items, int width, int height)
    {
        int left = int.MaxValue;
        int top = int.MaxValue;
        int right = int.MinValue;
        int bottom = int.MinValue;

        foreach (var item in items)
        {
            var bounds = item.Bounds;
            left = Math.Min(left, bounds.X);
            top = Math.Min(top, bounds.Y);
            right = Math.Max(right, bounds.Right);
            bottom = Math.Max(bottom, bounds.Bottom);
        }

        if (left == int.MaxValue)
        {
            return default;
        }

        // 描边/阴影可能溢出图元边界，留一点余量
        const int margin = 8;
        left = Math.Max(left - margin, 0);
        top = Math.Max(top - margin, 0);
        right = Math.Min(right + margin, width);
        bottom = Math.Min(bottom + margin, height);

        return right <= left || bottom <= top ? default : new PhysicalRect(left, top, right - left, bottom - top);
    }
}

/// <summary>
/// 图元的矢量绘制（标注窗口的实时预览与最终合成都走这一份代码，
/// 避免"预览看到的样子"和"导出的样子"不一致）。
/// 所有坐标都是**图像局部物理像素**。
/// </summary>
internal static class AnnotationDrawing
{
    public static void Draw(DrawingContext dc, IEnumerable<AnnotationItem> items)
    {
        foreach (var item in items)
        {
            DrawOne(dc, item);
        }
    }

    public static void DrawOne(DrawingContext dc, AnnotationItem item)
    {
        ArgumentNullException.ThrowIfNull(dc);
        ArgumentNullException.ThrowIfNull(item);

        var stroke = new SolidColorBrush(ToColor(item.Style.Stroke));
        stroke.Freeze();

        double width = Math.Max(item.Style.StrokeWidth, 1);
        var pen = new Pen(stroke, width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        // 给描边加一层极淡的外描边，浅色底图上也能看清（不影响像素 1:1）
        pen.Freeze();

        switch (item.Kind)
        {
            case AnnotationKind.Rectangle:
            {
                var rect = ToRect(item.Bounds);
                dc.DrawRectangle(GetFill(item), pen, rect);
                break;
            }

            case AnnotationKind.Ellipse:
            {
                var rect = ToRect(item.Bounds);
                dc.DrawEllipse(GetFill(item), pen, new Point(rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2)), rect.Width / 2, rect.Height / 2);
                break;
            }

            case AnnotationKind.Line:
                dc.DrawLine(pen, ToPoint(item.Start), ToPoint(item.End));
                break;

            case AnnotationKind.Arrow:
                DrawArrow(dc, pen, stroke, item);
                break;

            case AnnotationKind.Pencil:
            {
                var pencilPen = new Pen(stroke, Math.Max(width, 1))
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                };
                pencilPen.Freeze();
                DrawPolyline(dc, pencilPen, item.Points);
                break;
            }

            case AnnotationKind.Marker:
            {
                var markerBrush = new SolidColorBrush(ToColor(item.Style.Stroke)) { Opacity = 0.45 };
                markerBrush.Freeze();
                var markerPen = new Pen(markerBrush, Math.Max(width * 3, 10))
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                };
                markerPen.Freeze();
                DrawPolyline(dc, markerPen, item.Points);
                break;
            }

            case AnnotationKind.Highlight:
            {
                var highlightBrush = new SolidColorBrush(ToColor(item.Style.Stroke)) { Opacity = 0.35 };
                highlightBrush.Freeze();
                var highlightPen = new Pen(highlightBrush, Math.Max(width * 5, 16))
                {
                    StartLineCap = PenLineCap.Flat,
                    EndLineCap = PenLineCap.Flat,
                };
                highlightPen.Freeze();
                DrawPolyline(dc, highlightPen, item.Points.Count >= 2 ? item.Points : [item.Start, item.End]);
                break;
            }

            case AnnotationKind.Text:
                DrawText(dc, stroke, item);
                break;

            case AnnotationKind.Badge:
                DrawBadge(dc, item);
                break;

            case AnnotationKind.Mosaic:
            case AnnotationKind.Blur:
            case AnnotationKind.Eraser:
                // 像素效果不在矢量层绘制（由渲染管线处理）
                break;
        }
    }

    private static Brush? GetFill(AnnotationItem item)
    {
        if (item.Style.FillOpacity <= 0)
        {
            return null;
        }

        var brush = new SolidColorBrush(ToColor(item.Style.Fill))
        {
            Opacity = Math.Clamp(item.Style.FillOpacity / 100.0, 0, 1),
        };
        brush.Freeze();
        return brush;
    }

    private static void DrawArrow(DrawingContext dc, Pen pen, Brush brush, AnnotationItem item)
    {
        var start = ToPoint(item.Start);
        var end = ToPoint(item.End);
        dc.DrawLine(pen, start, end);

        double headLength = Math.Max(pen.Thickness * 4, 12);
        double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        const double spread = Math.PI / 7;

        var left = new Point(
            end.X - (headLength * Math.Cos(angle - spread)),
            end.Y - (headLength * Math.Sin(angle - spread)));

        var right = new Point(
            end.X - (headLength * Math.Cos(angle + spread)),
            end.Y - (headLength * Math.Sin(angle + spread)));

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(end, isFilled: true, isClosed: true);
            context.LineTo(left, isStroked: true, isSmoothJoin: false);
            context.LineTo(right, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }

    private static void DrawText(DrawingContext dc, Brush brush, AnnotationItem item)
    {
        var typeface = new Typeface(new FontFamily(item.Style.FontFamily), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var formatted = new FormattedText(
            item.Text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            item.Style.FontSize,
            brush,
            1.0);

        // 文字压在复杂背景上时需要描边才看得清
        var halo = new FormattedText(
            item.Text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            item.Style.FontSize,
            Brushes.Black,
            1.0);

        var origin = ToPoint(item.Start);

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                dc.DrawText(halo, new Point(origin.X + dx, origin.Y + dy));
            }
        }

        dc.DrawText(formatted, origin);
    }

    private static void DrawBadge(DrawingContext dc, AnnotationItem item)
    {
        var center = ToPoint(item.Start);
        double radius = Math.Max(item.Style.FontSize * 0.8, 8);

        var fill = new SolidColorBrush(ToColor(item.Style.Stroke));
        fill.Freeze();
        var outline = new Pen(Brushes.White, Math.Max(item.Style.FontSize / 8, 1));
        outline.Freeze();

        dc.DrawEllipse(fill, outline, center, radius, radius);

        var text = new FormattedText(
            item.Badge.ToString(),
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            item.Style.FontSize,
            Brushes.White,
            1.0);

        dc.DrawText(text, new Point(center.X - (text.Width / 2), center.Y - (text.Height / 2)));
    }

    private static void DrawPolyline(DrawingContext dc, Pen pen, IReadOnlyList<PhysicalPoint> points)
    {
        if (points.Count == 0)
        {
            return;
        }

        if (points.Count == 1)
        {
            var point = ToPoint(points[0]);
            dc.DrawEllipse(pen.Brush, null, point, pen.Thickness / 2, pen.Thickness / 2);
            return;
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(ToPoint(points[0]), isFilled: false, isClosed: false);

            for (int i = 1; i < points.Count; i++)
            {
                context.LineTo(ToPoint(points[i]), isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static Color ToColor(RgbColor color) => Color.FromRgb(color.R, color.G, color.B);

    private static Point ToPoint(PhysicalPoint point) => new(point.X, point.Y);

    private static Rect ToRect(PhysicalRect rect) => new(rect.X, rect.Y, Math.Max(rect.Width, 1), Math.Max(rect.Height, 1));
}

/// <summary>
/// 把 <see cref="DrawingVisual"/> 挂进 WPF 可视树的最小宿主。
/// 用它做标注的实时预览：改内容只需要重新 <c>RenderOpen</c>，不产生任何位图，拖动时零成本。
/// </summary>
internal sealed class VisualHost : FrameworkElement
{
    private readonly VisualCollection _children;

    public VisualHost()
    {
        _children = new VisualCollection(this);
    }

    public DrawingVisual Visual { get; } = new();

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    public void Attach()
    {
        if (_children.Count == 0)
        {
            _children.Add(Visual);
        }
    }

    public void Detach()
    {
        _children.Clear();
    }
}
