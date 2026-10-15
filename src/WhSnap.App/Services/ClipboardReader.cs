using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using WhSnap.Core;
using WhSnap.Pin;

namespace WhSnap.Services;

/// <summary>剪贴板里能拿来贴图的内容类型（对标 Snipaste 的"复制即贴"）。</summary>
internal enum ClipboardContentKind
{
    None,
    Image,
    Files,
    Html,
    Color,
    Text,
}

internal sealed record ClipboardPayload(
    ClipboardContentKind Kind,
    BitmapSource? Image = null,
    string? Text = null,
    IReadOnlyList<string>? Files = null)
{
    public bool IsEmpty => Kind == ClipboardContentKind.None;

    public string Describe() => Kind switch
    {
        ClipboardContentKind.Image => $"图像 {Image?.PixelWidth}×{Image?.PixelHeight}",
        ClipboardContentKind.Files => $"文件 {Files?.Count ?? 0} 个",
        ClipboardContentKind.Html => "HTML 片段（已提取纯文本）",
        ClipboardContentKind.Color => $"颜色值 {Text}",
        ClipboardContentKind.Text => $"文本 {Text?.Length ?? 0} 字",
        _ => "无可贴内容",
    };
}

/// <summary>
/// 从剪贴板读出可贴内容。顺序即优先级：
/// 图像 → 文件 → HTML → 颜色值 → 文本。
/// 之所以把图像放最前：浏览器/Office 复制图片时往往同时带 HTML 与文本，
/// 用户想要的显然是图，不是那段文字。
/// </summary>
internal static class ClipboardReader
{
    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp"];

    public static bool LooksLikeImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>剪贴板属于共享资源，被别的程序锁住时重试；全部失败返回 None 而不是抛异常。</summary>
    public static ClipboardPayload Read(int attempts = 4)
    {
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return ReadOnce();
            }
            catch (COMException ex)
            {
                Log.Warn($"读取剪贴板失败（第 {attempt} 次）：{ex.Message}");
                Thread.Sleep(60 * attempt);
            }
            catch (Exception ex)
            {
                Log.Warn($"读取剪贴板内容异常：{ex.Message}");
                return new ClipboardPayload(ClipboardContentKind.None);
            }
        }

        return new ClipboardPayload(ClipboardContentKind.None);
    }

    private static ClipboardPayload ReadOnce()
    {
        var data = System.Windows.Clipboard.GetDataObject();
        if (data is null)
        {
            return new ClipboardPayload(ClipboardContentKind.None);
        }

        // 1) 图像：优先自定义 PNG 流（保真度最好），退回系统位图
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is Stream pngStream)
        {
            var decoded = DecodePng(pngStream);
            if (decoded is not null)
            {
                return new ClipboardPayload(ClipboardContentKind.Image, Image: decoded);
            }
        }

        if (data.GetDataPresent(DataFormats.Bitmap))
        {
            var image = System.Windows.Clipboard.GetImage();
            if (image is not null)
            {
                return new ClipboardPayload(ClipboardContentKind.Image, Image: SaveService.Normalize(image));
            }
        }

        // 2) 拖放/复制的文件列表
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            return new ClipboardPayload(ClipboardContentKind.Files, Files: files);
        }

        // 3) HTML 片段 → 纯文本
        if (data.GetDataPresent(DataFormats.Html))
        {
            string? html = System.Windows.Clipboard.GetText(TextDataFormat.Html);
            string plain = HtmlText.ExtractPlainText(html);
            if (!string.IsNullOrWhiteSpace(plain))
            {
                return new ClipboardPayload(ClipboardContentKind.Html, Text: plain);
            }
        }

        // 4) 纯文本：先看是不是颜色值（复制 #RRGGBB 直接贴成色块）
        if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
        {
            string text = System.Windows.Clipboard.GetText() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return ColorValue.TryParse(text.Trim(), out _)
                    ? new ClipboardPayload(ClipboardContentKind.Color, Text: text.Trim())
                    : new ClipboardPayload(ClipboardContentKind.Text, Text: text);
            }
        }

        return new ClipboardPayload(ClipboardContentKind.None);
    }

    private static BitmapSource? DecodePng(Stream stream)
    {
        try
        {
            stream.Position = 0;
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            var frame = SaveService.Normalize(decoder.Frames[0]);
            frame.Freeze();
            return frame;
        }
        catch (Exception ex)
        {
            Log.Warn($"PNG 剪贴板解码失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>从文件加载图像（PNG/JPG/BMP/GIF/ICO/WebP 由 WIC 解码，GIF 取首帧）。</summary>
    public static BitmapSource? LoadImageFile(string path)
    {
        try
        {
            var frame = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var normalized = SaveService.Normalize(frame);
            normalized.Freeze();
            return normalized;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取图片文件失败（{path}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>把剪贴板内容渲染成贴图源位图；无可贴内容返回 null。</summary>
    public static BitmapSource? ToBitmap(ClipboardPayload payload, PinTextStyle style)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return payload.Kind switch
        {
            ClipboardContentKind.Image => payload.Image,
            ClipboardContentKind.Text or ClipboardContentKind.Html => PinContentRenderer.RenderText(payload.Text ?? string.Empty, style),
            ClipboardContentKind.Color => ColorValue.TryParse(payload.Text, out var color)
                ? PinContentRenderer.RenderColorCard(color, style)
                : null,
            _ => null,
        };
    }
}
