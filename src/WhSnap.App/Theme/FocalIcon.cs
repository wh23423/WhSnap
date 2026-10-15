using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace WhSnap.Theme;

/// <summary>
/// Focal 自绘图标（对应 design/focal/icons.js）：24×24 网格、1.6 描边、圆头圆角、只跟随 Foreground。
///
/// 用法：<c>&lt;theme:FocalIcon Icon="crop" Size="16"/&gt;</c>（XAML）
///      <c>FocalIcon.Create("crop", 16)</c>（C#）
///
/// 颜色不在这里定义：几何走 <c>Focal.Icon.*</c> 令牌，颜色继承 <see cref="TextElement.ForegroundProperty"/>
/// ——它与 <c>Control.Foreground</c> 是同一个依赖属性，所以按钮的 hover / 选中态换前景色时图标会跟着换。
/// </summary>
internal sealed class FocalIcon : FrameworkElement
{
    /// <summary>图标名（icons.js 的键名，例如 crop / check-circle / chevronDown）。</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(FocalIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>边长（DIP）。不设时用令牌 <c>Focal.IconMd</c>。</summary>
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(FocalIcon),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>描边粗细（24 网格下的用户单位，随 Size 缩放）。设计值 1.6。</summary>
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(FocalIcon),
        new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Dictionary<string, Geometry?> Cache = [];

    public string? Icon
    {
        get => (string?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>
    /// 图标颜色。它就是继承属性 <see cref="TextElement.ForegroundProperty"/>（与 Control.Foreground 同一个依赖属性），
    /// 所以在 XAML 里不设也会跟随按钮的前景色；C# 侧要单独指定时用这个属性。
    /// </summary>
    public Brush? Foreground
    {
        get => (Brush?)GetValue(TextElement.ForegroundProperty);
        set => SetValue(TextElement.ForegroundProperty, value);
    }

    /// <summary>便利构造（C# 侧建工具栏用）。</summary>
    public static FocalIcon Create(string icon, double size = double.NaN) => new() { Icon = icon, Size = size };

    /// <summary>取图标几何（带缓存；缺令牌时返回 null —— 由 --focal-test 负责报错，界面不因一个图标崩掉）。</summary>
    public static Geometry? Resolve(string? icon)
    {
        if (string.IsNullOrEmpty(icon))
        {
            return null;
        }

        if (Cache.TryGetValue(icon, out var cached))
        {
            return cached;
        }

        Geometry? geometry = null;
        if (FocalPalette.TryGet<Geometry>("Focal.Icon." + icon, out var found) && found is not null)
        {
            if (found.CanFreeze)
            {
                found.Freeze();
            }

            geometry = found;
        }

        Cache[icon] = geometry;
        return geometry;
    }

    /// <summary>可用图标名（--focal-test 与设计对齐检查用）。</summary>
    public static IReadOnlyCollection<string> KnownIcons => Cache.Keys;

    private double EffectiveSize
    {
        get
        {
            double size = Size;
            if (!double.IsNaN(size) && size > 0)
            {
                return size;
            }

            return FocalPalette.TryGet<double>("Focal.IconMd", out double token) ? token : 16;
        }
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
    {
        double size = EffectiveSize;
        return new System.Windows.Size(size, size);
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize) => finalSize;

    protected override void OnRender(DrawingContext drawingContext)
    {
        var geometry = Resolve(Icon);
        if (geometry is null || geometry.IsEmpty())
        {
            return;
        }

        double size = EffectiveSize;
        if (size <= 0)
        {
            return;
        }

        var brush = (Brush)GetValue(TextElement.ForegroundProperty);
        if (brush is null)
        {
            return;
        }

        // 24 网格 → 目标边长：描边粗细一起缩放，缩放后正好是设计稿的 1.6 用户单位
        double scale = size / 24.0;
        var pen = new Pen(brush, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(null, pen, geometry);
        drawingContext.Pop();
    }
}
