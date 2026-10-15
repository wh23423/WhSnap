using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WhSnap.Capture;

/// <summary>
/// 画面合理性校验：判断抓到的到底是不是一屏真实内容。
/// 采集后端"成功返回一张纯黑图"比直接报错更危险——用户会以为截图工具坏了却看不出原因，
/// 所以后端在返回前必须过这一关。
/// </summary>
internal static class FrameSanity
{
    /// <summary>亮度极差小于该值即视为"完全均匀"（黑帧/白帧的典型特征）。</summary>
    private const double UniformThreshold = 2.0;

    public static bool LooksLikeRealContent(BitmapSource image, out string description)
    {
        // 先缩到 1/4 再统计：足够判断是否均匀，又不必为 8K 画布分配大缓冲
        var scaled = new TransformedBitmap(image, new ScaleTransform(0.25, 0.25));
        var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

        int width = converted.PixelWidth;
        int height = converted.PixelHeight;

        if (width <= 0 || height <= 0)
        {
            description = "位图尺寸为 0";
            return false;
        }

        int stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        int step = Math.Max(1, Math.Min(width, height) / 120);
        double sum = 0;
        double min = 255;
        double max = 0;
        long count = 0;
        long black = 0;

        for (int y = 0; y < height; y += step)
        {
            for (int x = 0; x < width; x += step)
            {
                int index = (y * stride) + (x * 4);
                double b = pixels[index];
                double g = pixels[index + 1];
                double r = pixels[index + 2];
                double luminance = (0.114 * b) + (0.587 * g) + (0.299 * r);

                sum += luminance;
                count++;
                min = Math.Min(min, luminance);
                max = Math.Max(max, luminance);

                if (r < 8 && g < 8 && b < 8)
                {
                    black++;
                }
            }
        }

        if (count == 0)
        {
            description = "没有采样到像素";
            return false;
        }

        description = $"平均亮度 {sum / count:0.0}，范围 {min:0}-{max:0}，近黑 {(double)black / count * 100:0.0}%";
        return (max - min) >= UniformThreshold;
    }
}
