using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhSnap.Core;

namespace WhSnap.Pin;

/// <summary>贴图内容渲染的外观参数（来自设置，见规划 §6「全配置化」）。</summary>
internal sealed record PinTextStyle(
    string FontFamily,
    double FontSize,
    int MaxWidth,
    int MaxHeight,
    Color Foreground,
    Color Background,
    double Padding)
{
    public static PinTextStyle FromSettings(Services.AppSettings settings) => new(
        settings.TextPinFontFamily,
        settings.TextPinFontSize,
        settings.TextPinMaxWidth,
        settings.TextPinMaxHeight,
        ParseColor(settings.TextPinForeground, Color.FromRgb(0xF2, 0xF2, 0xF2)),
        ParseColor(settings.TextPinBackground, Color.FromRgb(0x1B, 0x1B, 0x1B)),
        settings.TextPinPadding);

    private static Color ParseColor(string? text, Color fallback) =>
        ColorValue.TryParse(text, out var rgb) ? Color.FromRgb(rgb.R, rgb.G, rgb.B) : fallback;
}

/// <summary>
/// 把"不是位图"的剪贴板内容（纯文本 / HTML / 颜色值 / 文件）渲染成贴图用的位图。
///
/// 全部按 **1 物理像素 = 1 DIP**（pixelsPerDip = 1.0）渲染，
/// 保证文字在高 DPI 屏上不模糊，也与"贴图导出 1:1"的约定一致。
/// </summary>
internal static class PinContentRenderer
{
    private const double PixelsPerDip = 1.0;

    public static BitmapSource RenderText(string text, PinTextStyle style)
    {
        if (string.IsNullOrEmpty(text))
        {
            text = "（空文本）";
        }

        var typeface = new Typeface(new FontFamily(style.FontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var brush = new SolidColorBrush(style.Foreground);
        brush.Freeze();

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            style.FontSize,
            brush,
            PixelsPerDip)
        {
            MaxTextWidth = Math.Max(style.MaxWidth, 40),
            Trimming = TextTrimming.None,
        };

        double contentWidth = Math.Ceiling(formatted.Width);
        double contentHeight = Math.Ceiling(formatted.Height);

        // 超长文本按最大高度截断（带省略号），避免一张贴图吃掉几百 MB
        if (contentHeight > style.MaxHeight)
        {
            formatted.MaxTextHeight = style.MaxHeight;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
            formatted.MaxLineCount = Math.Max((int)(style.MaxHeight / (style.FontSize * 1.35)), 1);
            contentHeight = Math.Ceiling(formatted.Height);
        }

        return RenderCanvas(
            style,
            contentWidth + (style.Padding * 2),
            contentHeight + (style.Padding * 2),
            (dc, width, height) => dc.DrawText(formatted, new Point(style.Padding, style.Padding)));
    }

    /// <summary>颜色值 → 色块 + 四种表示法（取色器与"复制颜色即贴图"共用）。</summary>
    public static BitmapSource RenderColorCard(RgbColor color, PinTextStyle style)
    {
        const double swatch = 96;
        var typeface = new Typeface(new FontFamily("Consolas, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var textBrush = new SolidColorBrush(style.Foreground);
        textBrush.Freeze();

        string[] lines =
        [
            color.ToHex().ToUpperInvariant(),
            ColorValue.ToRgbText(color),
            ColorValue.ToHslText(color),
            ColorValue.ToCmykText(color),
        ];

        var formatted = lines.Select(line => new FormattedText(
            line,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            13,
            textBrush,
            PixelsPerDip));

        double textWidth = formatted.Max(f => Math.Ceiling(f.Width));
        double textHeight = formatted.Sum(f => Math.Ceiling(f.Height) + 4);
        double contentWidth = swatch + 12 + textWidth;
        double contentHeight = Math.Max(swatch, textHeight);

        return RenderCanvas(
            style,
            contentWidth + (style.Padding * 2),
            contentHeight + (style.Padding * 2),
            (dc, _, _) =>
            {
                var swatchRect = new Rect(style.Padding, style.Padding, swatch, swatch);
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)), new Pen(textBrush, 1), swatchRect);

                double y = style.Padding;
                foreach (var line in formatted)
                {
                    dc.DrawText(line, new Point(style.Padding + swatch + 12, y));
                    y += Math.Ceiling(line.Height) + 4;
                }
            });
    }

    /// <summary>文件（非图片或图片以外的拖放）→ 一张带图标、文件名、大小与路径的卡片。</summary>
    public static BitmapSource RenderFileCard(string path, PinTextStyle style)
    {
        var typeface = new Typeface(new FontFamily(style.FontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var textBrush = new SolidColorBrush(style.Foreground);
        var mutedBrush = new SolidColorBrush(Color.FromArgb(0xB0, style.Foreground.R, style.Foreground.G, style.Foreground.B));
        textBrush.Freeze();
        mutedBrush.Freeze();

        string name = Path.GetFileName(path);
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string size;

        try
        {
            size = FormatSize(new FileInfo(path).Length);
        }
        catch
        {
            size = "未知大小";
        }

        string kind = Path.GetExtension(path).TrimStart('.');
        kind = kind.Length == 0 ? "文件" : kind.ToUpperInvariant() + " 文件";

        var nameText = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, style.FontSize + 2, textBrush, PixelsPerDip)
        {
            MaxTextWidth = Math.Max(style.MaxWidth, 80),
            Trimming = TextTrimming.CharacterEllipsis,
        };

        var metaText = new FormattedText(
            $"{size}　{kind}",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            style.FontSize - 2,
            mutedBrush,
            PixelsPerDip);

        var pathText = new FormattedText(directory, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, style.FontSize - 3, mutedBrush, PixelsPerDip)
        {
            MaxTextWidth = Math.Max(style.MaxWidth, 80),
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = 2,
        };

        const double iconSize = 32;
        double textWidth = Math.Max(Math.Ceiling(nameText.Width), Math.Max(Math.Ceiling(metaText.Width), Math.Ceiling(pathText.Width)));
        double contentHeight = Math.Max(iconSize, Math.Ceiling(nameText.Height) + Math.Ceiling(metaText.Height) + Math.Ceiling(pathText.Height) + 6);
        double contentWidth = iconSize + 12 + textWidth;

        var icon = TryLoadFileIcon(path);

        return RenderCanvas(
            style,
            contentWidth + (style.Padding * 2),
            contentHeight + (style.Padding * 2),
            (dc, _, _) =>
            {
                double y = style.Padding;

                if (icon is not null)
                {
                    dc.DrawImage(icon, new Rect(style.Padding, style.Padding, iconSize, iconSize));
                }
                else
                {
                    var fallback = new FormattedText("📄", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 24, textBrush, PixelsPerDip);
                    dc.DrawText(fallback, new Point(style.Padding, style.Padding));
                }

                double x = style.Padding + iconSize + 12;
                dc.DrawText(nameText, new Point(x, y));
                y += Math.Ceiling(nameText.Height) + 2;
                dc.DrawText(metaText, new Point(x, y));
                y += Math.Ceiling(metaText.Height) + 2;
                dc.DrawText(pathText, new Point(x, y));
            });
    }

    /// <summary>用系统文件关联图标画卡片头部；取不到就退化为 emoji。</summary>
    private static BitmapSource? TryLoadFileIcon(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    private static BitmapSource RenderCanvas(PinTextStyle style, double width, double height, Action<DrawingContext, double, double> draw)
    {
        int pixelWidth = Math.Max((int)Math.Ceiling(width), 16);
        int pixelHeight = Math.Max((int)Math.Ceiling(height), 16);

        var background = new SolidColorBrush(style.Background);
        background.Freeze();

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, new Rect(0, 0, pixelWidth, pixelHeight));
            draw(dc, pixelWidth, pixelHeight);
        }

        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
