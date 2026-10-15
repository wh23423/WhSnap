using WhSnap.Core;
using Xunit;

namespace WhSnap.Core.Tests;

public class PixelEffectsTests
{
    private static byte[] CreateImage(int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> paint)
    {
        var pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = ((y * width) + x) * 4;
                var (b, g, r, a) = paint(x, y);
                pixels[index] = b;
                pixels[index + 1] = g;
                pixels[index + 2] = r;
                pixels[index + 3] = a;
            }
        }

        return pixels;
    }

    private static (byte B, byte G, byte R, byte A) At(byte[] pixels, int width, int x, int y)
    {
        int index = ((y * width) + x) * 4;
        return (pixels[index], pixels[index + 1], pixels[index + 2], pixels[index + 3]);
    }

    [Fact]
    public void Mosaic_makes_every_block_uniform_and_keeps_the_average()
    {
        const int size = 12;
        var pixels = CreateImage(size, size, (x, y) => ((byte)(x * 20 % 256), (byte)(y * 10 % 256), 100, 255));

        PixelEffects.Mosaic(pixels, size, size, size * 4, new PhysicalRect(0, 0, size, size), blockSize: 3);

        // 3x3 块内必须完全一致
        var first = At(pixels, size, 0, 0);
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                Assert.Equal(first, At(pixels, size, x, y));
            }
        }

        // 取值必须是原块均值（第 0 块：x=0..2, y=0..2）
        byte expectedBlue = (byte)((0 + 20 + 40) / 3);
        byte expectedGreen = (byte)((0 + 10 + 20) / 3);
        Assert.Equal(expectedBlue, first.B);
        Assert.Equal(expectedGreen, first.G);
        Assert.Equal(100, first.R);
        Assert.Equal(255, first.A);
    }

    [Fact]
    public void Mosaic_only_touches_the_requested_region()
    {
        const int size = 8;
        var pixels = CreateImage(size, size, (x, y) => ((byte)x, (byte)y, 0, 255));
        var before = (byte[])pixels.Clone();

        PixelEffects.Mosaic(pixels, size, size, size * 4, new PhysicalRect(2, 2, 4, 4), blockSize: 2);

        // 区域外逐字节不变
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool inside = x >= 2 && x < 6 && y >= 2 && y < 6;
                if (!inside)
                {
                    int index = ((y * size) + x) * 4;
                    Assert.Equal(before[index], pixels[index]);
                    Assert.Equal(before[index + 1], pixels[index + 1]);
                }
            }
        }
    }

    [Fact]
    public void Mosaic_ignores_out_of_range_regions()
    {
        const int size = 4;
        var pixels = CreateImage(size, size, (_, _) => (10, 20, 30, 255));
        var before = (byte[])pixels.Clone();

        PixelEffects.Mosaic(pixels, size, size, size * 4, new PhysicalRect(100, 100, 50, 50), blockSize: 4);

        Assert.Equal(before, pixels);
    }

    [Fact]
    public void BoxBlur_leaves_a_uniform_image_unchanged()
    {
        const int size = 16;
        var pixels = CreateImage(size, size, (_, _) => (80, 160, 200, 255));
        var before = (byte[])pixels.Clone();

        PixelEffects.BoxBlur(pixels, size, size, size * 4, new PhysicalRect(0, 0, size, size), radius: 4);

        Assert.Equal(before, pixels);
    }

    [Fact]
    public void BoxBlur_spreads_an_impulse_over_the_kernel_and_stops_at_its_edge()
    {
        const int size = 21;
        var pixels = CreateImage(size, size, (_, _) => (0, 0, 0, 255));
        int center = size / 2;
        int centerIndex = ((center * size) + center) * 4;
        pixels[centerIndex] = 255;

        PixelEffects.BoxBlur(pixels, size, size, size * 4, new PhysicalRect(0, 0, size, size), radius: 3);

        int blurredCenter = At(pixels, size, center, center).B;
        int insideKernel = At(pixels, size, center + 3, center).B;
        int outsideKernel = At(pixels, size, center + 5, center).B;

        Assert.True(blurredCenter > 0, "中心像素应当仍有能量");
        Assert.True(insideKernel > 0, "能量应当扩散到核内邻域");
        // 盒式模糊在核内是均匀的（这正是"盒式"的含义）
        Assert.Equal(blurredCenter, insideKernel);
        Assert.Equal(0, outsideKernel);
    }

    [Fact]
    public void BoxBlur_with_zero_radius_does_nothing()
    {
        const int size = 8;
        var pixels = CreateImage(size, size, (x, y) => ((byte)x, (byte)y, 1, 255));
        var before = (byte[])pixels.Clone();

        PixelEffects.BoxBlur(pixels, size, size, size * 4, new PhysicalRect(0, 0, size, size), radius: 0);

        Assert.Equal(before, pixels);
    }

    [Fact]
    public void EraseStroke_clears_only_pixels_near_the_path()
    {
        const int size = 20;
        var pixels = CreateImage(size, size, (_, _) => (200, 100, 50, 255));

        PixelEffects.EraseStroke(
            pixels,
            size,
            size,
            size * 4,
            [new PhysicalPoint(2, 10), new PhysicalPoint(17, 10)],
            radius: 2);

        // 笔迹上：透明
        Assert.Equal(0, At(pixels, size, 10, 10).A);
        Assert.Equal(0, At(pixels, size, 10, 12).A);

        // 笔迹外：原样
        Assert.Equal(255, At(pixels, size, 10, 15).A);
        Assert.Equal(255, At(pixels, size, 10, 5).A);
        Assert.Equal((byte)200, At(pixels, size, 10, 15).B);
    }

    [Fact]
    public void EraseStroke_with_a_single_point_behaves_like_a_dot()
    {
        const int size = 12;
        var pixels = CreateImage(size, size, (_, _) => (10, 10, 10, 255));

        PixelEffects.EraseStroke(pixels, size, size, size * 4, [new PhysicalPoint(6, 6)], radius: 2);

        Assert.Equal(0, At(pixels, size, 6, 6).A);
        Assert.Equal(0, At(pixels, size, 8, 6).A);
        Assert.Equal(255, At(pixels, size, 6, 9).A);
    }

    [Fact]
    public void ClampRegion_trims_to_image_bounds_and_normalizes()
    {
        Assert.Equal(new PhysicalRect(0, 0, 10, 10), PixelEffects.ClampRegion(new PhysicalRect(-5, -5, 15, 15), 10, 10));
        Assert.Equal(new PhysicalRect(6, 6, 4, 4), PixelEffects.ClampRegion(new PhysicalRect(6, 6, 4, 4), 10, 10));
        Assert.True(PixelEffects.ClampRegion(new PhysicalRect(50, 50, 5, 5), 10, 10).IsEmpty);
    }
}

public class PixelBlendTests
{
    private static byte[] Pixel(byte b, byte g, byte r, byte a) => [b, g, r, a];

    [Fact]
    public void Opaque_source_replaces_the_destination()
    {
        var destination = Pixel(10, 20, 30, 255);
        PixelBlend.SourceOver(destination, Pixel(200, 100, 50, 255), 4);

        Assert.Equal([200, 100, 50, 255], destination);
    }

    [Fact]
    public void Transparent_source_leaves_the_destination_alone()
    {
        var destination = Pixel(10, 20, 30, 255);
        PixelBlend.SourceOver(destination, Pixel(200, 100, 50, 0), 4);

        Assert.Equal([10, 20, 30, 255], destination);
    }

    [Fact]
    public void Half_transparent_source_blends_proportionally()
    {
        // 预乘源：50% 红 (B=0,G=0,R=128,A=128) 叠加到不透明白色
        // 结果 = 0.5 * 红 + 0.5 * 白 → R 仍为 255，G/B 变成 127
        var destination = Pixel(255, 255, 255, 255);
        PixelBlend.SourceOver(destination, Pixel(0, 0, 128, 128), 4);

        Assert.Equal(255, destination[2]);
        Assert.Equal(127, destination[1]);
        Assert.Equal(127, destination[0]);
        Assert.Equal(255, destination[3]);
    }

    [Fact]
    public void Blending_ignores_a_trailing_partial_pixel()
    {
        // 只有完整像素才参与合成；7 字节 = 1 个完整像素 + 3 字节残渣
        var destination = Pixel(10, 10, 10, 255);
        PixelBlend.SourceOver(destination, Pixel(200, 200, 200, 255), 7);
        Assert.Equal([200, 200, 200, 255], destination);

        // 3 字节连一个完整像素都不够，必须原样不动
        var untouched = Pixel(10, 10, 10, 255);
        PixelBlend.SourceOver(untouched, Pixel(200, 200, 200, 255), 3);
        Assert.Equal([10, 10, 10, 255], untouched);
    }
}
