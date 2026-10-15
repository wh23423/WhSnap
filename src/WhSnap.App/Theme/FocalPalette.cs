using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WhSnap.Theme;

/// <summary>
/// Focal 设计令牌的**代码侧唯一访问点**。
///
/// 规则（DESIGN §9 / README「六条不会破例的规则」）：界面代码里不允许出现字面量色值 ——
/// XAML 用 <c>{StaticResource Focal.*}</c>，C# 用这里。令牌只定义在
/// <c>Theme/FocalTheme.xaml</c>，它再合并进 <c>App.xaml</c>。
///
/// 取不到令牌时**抛异常**而不是静默返回透明色：令牌缺失是编程错误，
/// 由 <c>--focal-test</c> 自检在 CI 里提前抓住，而不是让界面在用户机器上悄悄画错。
/// </summary>
internal static class FocalPalette
{
    // ── 表面：Obsidian（暗色机壳） ────────────────────────────────────────────
    public static Brush Void => Brush("Focal.VoidBrush");
    public static Brush Obsidian900 => Brush("Focal.Obsidian900Brush");
    public static Brush Obsidian800 => Brush("Focal.Obsidian800Brush");
    public static Brush Obsidian700 => Brush("Focal.Obsidian700Brush");
    public static Brush Obsidian600 => Brush("Focal.Obsidian600Brush");
    public static Brush Canvas => Brush("Focal.CanvasBrush");

    // ── 表面：Paper（纸色阅读面） ────────────────────────────────────────────
    public static Brush Paper => Brush("Focal.PaperBrush");
    public static Brush Linen => Brush("Focal.LinenBrush");
    public static Brush LinenDeep => Brush("Focal.LinenDeepBrush");

    // ── 文字 ─────────────────────────────────────────────────────────────────
    public static Brush Ink => Brush("Focal.InkBrush");
    public static Brush InkMuted => Brush("Focal.InkMutedBrush");
    public static Brush InkFaint => Brush("Focal.InkFaintBrush");
    public static Brush OnDark => Brush("Focal.OnDarkBrush");
    public static Brush OnDarkMuted => Brush("Focal.OnDarkMutedBrush");
    public static Brush OnDarkFaint => Brush("Focal.OnDarkFaintBrush");

    // ── 强调：Signal Amber（一族四档 + 半透明档） ────────────────────────────
    public static Brush Accent => Brush("Focal.AccentBrush");
    public static Brush AccentHover => Brush("Focal.AccentHoverBrush");
    public static Brush AccentPress => Brush("Focal.AccentPressBrush");
    public static Brush AccentDeep => Brush("Focal.AccentDeepBrush");
    public static Brush AccentInk => Brush("Focal.AccentInkBrush");
    public static Brush AccentTint => Brush("Focal.AccentTintBrush");
    public static Brush AccentTint2 => Brush("Focal.AccentTint2Brush");
    public static Brush AccentRing => Brush("Focal.AccentRingBrush");
    public static Brush AccentBorder => Brush("Focal.AccentBorderBrush");

    // ── 发丝线 ───────────────────────────────────────────────────────────────
    public static Brush HairlineDark => Brush("Focal.HairlineDarkBrush");
    public static Brush HairlineDarkStrong => Brush("Focal.HairlineDarkStrongBrush");
    public static Brush HairlineLight => Brush("Focal.HairlineLightBrush");

    // ── 语义（只出现在状态面） ───────────────────────────────────────────────
    public static Brush SignalOk => Brush("Focal.SignalOkBrush");
    public static Brush SignalWarn => Brush("Focal.SignalWarnBrush");
    public static Brush SignalDanger => Brush("Focal.SignalDangerBrush");

    // ── 量测标记（遮罩 / 标注专用，与主题无关） ──────────────────────────────
    public static Brush MeasureSelect => Brush("Focal.MeasureSelectBrush");
    public static Brush MeasureElement => Brush("Focal.MeasureElementBrush");
    public static Brush MeasureCenter => Brush("Focal.MeasureCenterBrush");

    // ── 字体 ─────────────────────────────────────────────────────────────────
    public static FontFamily DisplayFont => Font("Focal.DisplayFont");
    public static FontFamily TextFont => Font("Focal.TextFont");
    public static FontFamily DataFont => Font("Focal.DataFont");

    // ── 度量 ─────────────────────────────────────────────────────────────────
    public static double ControlHeight => Metric("Focal.ControlHeight");
    public static double ControlHeightLarge => Metric("Focal.ControlHeightLarge");
    public static double RowHeight => Metric("Focal.RowHeight");
    public static double RowHeightLarge => Metric("Focal.RowHeightLarge");
    public static double ToolbarHeight => Metric("Focal.ToolbarHeight");
    public static double FloatbarHeight => Metric("Focal.FloatbarHeight");
    public static double TitleBarHeight => Metric("Focal.TitleBarHeight");
    public static double HandleSize => Metric("Focal.HandleSize");
    public static double IconSm => Metric("Focal.IconSm");
    public static double IconMd => Metric("Focal.IconMd");
    public static double IconLg => Metric("Focal.IconLg");
    public static double IconXl => Metric("Focal.IconXl");

    // ── 字号 ─────────────────────────────────────────────────────────────────
    public static double BodySize => Metric("Focal.BodySize");
    public static double CaptionSize => Metric("Focal.CaptionSize");
    public static double MicroSize => Metric("Focal.MicroSize");
    public static double DataSize => Metric("Focal.DataSize");
    public static double DataLargeSize => Metric("Focal.DataLargeSize");

    // ── 圆角 ─────────────────────────────────────────────────────────────────
    public static CornerRadius RadiusHair => Corner("Focal.RadiusHair");
    public static CornerRadius RadiusControl => Corner("Focal.RadiusControl");
    public static CornerRadius RadiusCard => Corner("Focal.RadiusCard");
    public static CornerRadius RadiusPanel => Corner("Focal.RadiusPanel");

    /// <summary>取任意令牌（画刷 / 颜色 / 字号 / 度量 / 样式）。取不到即抛。</summary>
    public static T Get<T>(string key)
    {
        object? value = System.Windows.Application.Current?.TryFindResource(key);
        if (value is T typed)
        {
            return typed;
        }

        throw new InvalidOperationException(
            value is null
                ? $"Focal 令牌缺失：{key}。请把它加进 Theme/FocalTheme.xaml（令牌唯一来源）。"
                : $"Focal 令牌 {key} 的类型是 {value.GetType().Name}，不是 {typeof(T).Name}。");
    }

    public static bool TryGet<T>(string key, out T value)
    {
        if (System.Windows.Application.Current?.TryFindResource(key) is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    public static Brush Brush(string key) => Get<Brush>(key);

    public static Color Color(string key) => Get<Color>(key);

    public static double Metric(string key) => Get<double>(key);

    public static CornerRadius Corner(string key) => Get<CornerRadius>(key);

    public static FontFamily Font(string key) => Get<FontFamily>(key);

    /// <summary>取样式（界面代码不写内联模板，统一走这里）。</summary>
    public static Style Style(string key) => Get<Style>(key);

    // ── 派生 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 暗幕：浓度按设置（0–90）缩放 <c>Focal.ScrimBrush</c> 的 alpha。
    /// 遮罩的暗幕浓度是"物理像素计算"，不随主题变化 —— 但颜色仍取自令牌，而不是字面量。
    /// </summary>
    public static SolidColorBrush Scrim(double opacityPercent)
    {
        var baseColor = (Brush("Focal.ScrimBrush") as SolidColorBrush)?.Color ?? Colors.Black;
        double clamped = Math.Clamp(opacityPercent, 0, 100) / 100.0;
        byte alpha = (byte)Math.Round(baseColor.A * clamped, MidpointRounding.AwayFromZero);
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>把令牌画刷按比例调透明（不用改颜色定义）。</summary>
    public static SolidColorBrush Fade(Brush brush, double factor)
    {
        var color = (brush as SolidColorBrush)?.Color ?? Colors.Transparent;
        byte alpha = (byte)Math.Clamp(Math.Round(color.A * factor, MidpointRounding.AwayFromZero), 0, 255);
        var faded = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, color.R, color.G, color.B));
        faded.Freeze();
        return faded;
    }

    /// <summary>纯色画刷（用于用户数据：取色历史、标注颜色 —— 这些颜色来自内容，不是 UI 主题）。</summary>
    public static SolidColorBrush FromRgb(Core.RgbColor color)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush FromColor(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>由分量构造画刷（用于把用户数据的像素颜色画出来，例如取色器读数）。</summary>
    public static SolidColorBrush BrushColor(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>把令牌画刷转成 System.Drawing.Color（托盘菜单 / 图标绘制用）。</summary>
    public static System.Drawing.Color DrawingColor(Brush brush)
    {
        var color = (brush as SolidColorBrush)?.Color ?? Colors.Transparent;
        return System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    /// <summary>
    /// WinForms 侧按**画刷令牌键**取色：界面代码引用的是 `<c>Focal.XxxBrush</c>`（与 XAML 一致），
    /// 有些令牌（发丝线 / accent-tint 等）只以画刷形式存在。内部走
    /// <see cref="DrawingColor(Brush)"/>，取不到令牌仍然抛异常。
    /// </summary>
    public static System.Drawing.Color DrawingColor(string brushKey)
    {
        return DrawingColor(Brush(brushKey));
    }

    /// <summary>把"用户的颜色"（内容，不是主题色）转成 WinForms 颜色：托盘的最近颜色色块用。</summary>
    public static System.Drawing.Color DrawingColor(Core.RgbColor color) =>
        System.Drawing.Color.FromArgb(255, color.R, color.G, color.B);

    /// <summary>令牌颜色的 ARGB 十六进制（诊断输出 / 对比度检查用）。</summary>
    public static string Hex(string key)
    {
        var color = Color(key);
        return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    // ── WinForms 桥（托盘菜单自绘） ───────────────────────────────────────────
    // WinForms 里没有画刷/几何/字体令牌的概念，所以桥必须收在这里：
    // 托盘自绘代码只允许调用下面三个方法，不许自己 new Font("#..") 或写颜色。

    /// <summary>按令牌字体族构造 GDI+ 字体（字号按像素，与 WPF 的 DIP 视觉一致）。</summary>
    public static System.Drawing.Font DrawingFont(string fontTokenKey, double sizeDips, System.Drawing.FontStyle style = System.Drawing.FontStyle.Regular)
    {
        string family = Get<FontFamily>(fontTokenKey).Source;
        try
        {
            return new System.Drawing.Font(family, (float)sizeDips, style, System.Drawing.GraphicsUnit.Pixel);
        }
        catch
        {
            return new System.Drawing.Font(System.Drawing.FontFamily.GenericSansSerif, (float)sizeDips, style, System.Drawing.GraphicsUnit.Pixel);
        }
    }

    /// <summary>
    /// 把 Focal 图标渲染成 GDI+ 位图（托盘菜单 / 通知区图标用）。
    /// 走 WPF 几何渲染再拷像素：几何本身仍是同一份 <c>Focal.Icon.*</c> 令牌。
    /// </summary>
    public static System.Drawing.Bitmap IconBitmap(string icon, int sizePixels, System.Drawing.Color color)
    {
        int size = Math.Max(sizePixels, 1);
        var geometry = FocalIcon.Resolve(icon);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (geometry is not null && !geometry.IsEmpty())
            {
                var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));
                var pen = new System.Windows.Media.Pen(brush, 1.6)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                };

                dc.PushTransform(new ScaleTransform(size / 24.0, size / 24.0));
                dc.DrawGeometry(null, pen, geometry);
                dc.Pop();
            }
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;

        // GDI+ 的 Bitmap(Stream) 要求流在位图存活期间一直开着：这里拷一份像素再返回
        using var decoded = new System.Drawing.Bitmap(stream);
        var result = new System.Drawing.Bitmap(decoded.Width, decoded.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(result))
        {
            graphics.DrawImage(decoded, 0, 0, decoded.Width, decoded.Height);
        }

        return result;
    }
}
