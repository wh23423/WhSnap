using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WhSnap.Services;

internal static class SaveService
{
    public static string GetDefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "WhSnap");

    public static string ResolveDirectory(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.SaveDirectory) ? GetDefaultDirectory() : settings.SaveDirectory;

    public static string BuildFileName(string template, int width, int height, DateTime timestamp)
    {
        string name = string.IsNullOrWhiteSpace(template) ? "WhSnap_{date}_{time}_{w}x{h}" : template;
        name = name
            .Replace("{date}", timestamp.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", timestamp.ToString("HHmmss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{w}", width.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{h}", height.ToString(), StringComparison.OrdinalIgnoreCase);

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name + ".png";
    }

    /// <summary>保存为 PNG（无损、1:1 物理像素）。返回落盘路径。</summary>
    public static string SavePng(BitmapSource image, AppSettings settings, out long bytes)
    {
        string directory = ResolveDirectory(settings);
        Directory.CreateDirectory(directory);

        string fileName = BuildFileName(settings.FileNameTemplate, image.PixelWidth, image.PixelHeight, DateTime.Now);
        string path = Path.Combine(directory, fileName);

        bytes = SaveImage(image, path, settings.JpegQuality);
        return path;
    }

    /// <summary>
    /// 把任意格式/预乘 alpha 的位图统一成**直通 alpha 的 Bgra32**。
    ///
    /// 这一步是必须的：WPF 渲染出来的位图是 Pbgra32（预乘），直接交给 PNG 编码器或剪贴板 DIB
    /// 会被当成"已经是直通值"写出去，半透明像素会明显发暗。所有贴图源都先经过这里。
    /// </summary>
    public static BitmapSource Normalize(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (image.Format == PixelFormats.Bgra32)
        {
            if (image.CanFreeze && !image.IsFrozen)
            {
                image.Freeze();
            }

            return image;
        }

        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>按扩展名选择编码器落盘，返回字节数（PNG 无损 / JPG 可调质量 / BMP）。</summary>
    public static long SaveImage(BitmapSource image, string path, int jpegQuality = 90)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        BitmapEncoder encoder = extension switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 10, 100) },
            ".bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };

        encoder.Frames.Add(BitmapFrame.Create(Normalize(image)));

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        return new FileInfo(path).Length;
    }
}
