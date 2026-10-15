using System.IO;
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

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));

        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        bytes = new FileInfo(path).Length;
        return path;
    }
}
