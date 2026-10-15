using WhSnap.Core;
using Xunit;

namespace WhSnap.Core.Tests;

/// <summary>采集与合成相关的纯逻辑测试：像素旋转、贴图偏移、虚拟桌面几何。</summary>
public class BgraPixelsTests
{
    private const byte A = 0x11;
    private const byte B = 0x22;
    private const byte C = 0x33;
    private const byte D = 0x44;

    /// <summary>构造一行 n 个像素的 BGRA 缓冲，灰度值用给定字节填充。</summary>
    private static byte[] Row(params byte[] values)
    {
        var buffer = new byte[values.Length * 4];

        for (int i = 0; i < values.Length; i++)
        {
            buffer[(i * 4)] = values[i];
            buffer[(i * 4) + 1] = values[i];
            buffer[(i * 4) + 2] = values[i];
            buffer[(i * 4) + 3] = 0xFF;
        }

        return buffer;
    }

    private static byte Value(byte[] buffer, int x, int y, int width) => buffer[((y * width) + x) * 4];

    [Theory]
    [InlineData(4, 3, DisplayRotation.Identity, 4, 3)]
    [InlineData(4, 3, DisplayRotation.Rotate90, 3, 4)]
    [InlineData(4, 3, DisplayRotation.Rotate180, 4, 3)]
    [InlineData(4, 3, DisplayRotation.Rotate270, 3, 4)]
    public void RotatedSize_swaps_dimensions_for_quarter_turns(int width, int height, DisplayRotation rotation, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), BgraPixels.RotatedSize(width, height, rotation));
    }

    [Fact]
    public void Rotate90_maps_source_row_to_target_column()
    {
        // 一行两个像素 [A B]，顺时针 90 度后应变成一列 [A; B]
        var source = Row(A, B);

        var rotated = BgraPixels.Rotate(source, 2, 1, 8, DisplayRotation.Rotate90);

        Assert.Equal(8, rotated.Length);
        Assert.Equal(A, Value(rotated, 0, 0, 1));
        Assert.Equal(B, Value(rotated, 0, 1, 1));
    }

    [Fact]
    public void Rotate270_reverses_the_column_order()
    {
        var source = Row(A, B);

        var rotated = BgraPixels.Rotate(source, 2, 1, 8, DisplayRotation.Rotate270);

        Assert.Equal(B, Value(rotated, 0, 0, 1));
        Assert.Equal(A, Value(rotated, 0, 1, 1));
    }

    [Fact]
    public void Rotate180_flips_both_axes()
    {
        var source = Row(A, B);

        var rotated = BgraPixels.Rotate(source, 2, 1, 8, DisplayRotation.Rotate180);

        Assert.Equal(B, Value(rotated, 0, 0, 2));
        Assert.Equal(A, Value(rotated, 1, 0, 2));
    }

    [Fact]
    public void Rotate_identity_keeps_pixels_but_drops_row_padding()
    {
        // stride 12 表示每行末尾有 4 字节填充
        var source = new byte[12];
        source[0] = A; source[1] = A; source[2] = A; source[3] = 0xFF;
        source[4] = B; source[5] = B; source[6] = B; source[7] = 0xFF;

        var result = BgraPixels.Rotate(source, 2, 1, 12, DisplayRotation.Identity);

        Assert.Equal(8, result.Length);
        Assert.Equal(A, result[0]);
        Assert.Equal(B, result[4]);
    }

    [Fact]
    public void Rotate_four_quarter_turns_returns_to_original()
    {
        // 2x3 图像，像素值 = 索引，方便比对
        const int width = 2;
        const int height = 3;
        var source = new byte[width * height * 4];

        for (int i = 0; i < width * height; i++)
        {
            source[(i * 4)] = (byte)(i + 1);
            source[(i * 4) + 3] = 0xFF;
        }

        var current = source;

        for (int turn = 0; turn < 4; turn++)
        {
            current = BgraPixels.Rotate(current, width, height, width * 4, DisplayRotation.Rotate90);
        }

        // 转四次回到原尺寸；值集合必须一致
        Assert.Equal(source.Length, current.Length);
        Assert.Equal(source.OrderBy(v => v), current.OrderBy(v => v));
    }

    [Fact]
    public void Rotate_rejects_stride_smaller_than_row()
    {
        var source = new byte[8];

        Assert.Throws<ArgumentOutOfRangeException>(() => BgraPixels.Rotate(source, 4, 1, 8, DisplayRotation.Rotate90));
    }

    [Fact]
    public void Blit_places_source_at_offset()
    {
        var source = Row(A, B, C, D);            // 4x1
        var target = new byte[6 * 2 * 4];        // 6x2 画布

        BgraPixels.Blit(source, 4, 1, 16, target, 6, 2, 24, 1, 1);

        Assert.Equal(0, Value(target, 0, 1, 6));
        Assert.Equal(A, Value(target, 1, 1, 6));
        Assert.Equal(D, Value(target, 4, 1, 6));
        Assert.Equal(0, Value(target, 5, 1, 6));
        Assert.Equal(0, Value(target, 1, 0, 6));
    }

    [Fact]
    public void Blit_rejects_out_of_range_offset()
    {
        var source = Row(A, B, C, D);
        var target = new byte[6 * 2 * 4];

        Assert.Throws<ArgumentOutOfRangeException>(() => BgraPixels.Blit(source, 4, 1, 16, target, 6, 2, 24, 3, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BgraPixels.Blit(source, 4, 1, 16, target, 6, 2, 24, 0, 2));
    }
}

public class VirtualDesktopTests
{
    private static MonitorInfo Monitor(int x, int y, int width, int height, bool primary = false) =>
        new(string.Empty, new PhysicalRect(x, y, width, height), new PhysicalRect(x, y, width, height), 96, 96, primary);

    [Fact]
    public void BoundsOf_single_monitor_is_identity()
    {
        var bounds = VirtualDesktop.BoundsOf([Monitor(0, 0, 1920, 1080, true)]);

        Assert.Equal(new PhysicalRect(0, 0, 1920, 1080), bounds);
    }

    [Fact]
    public void BoundsOf_handles_monitor_to_the_left_with_negative_origin()
    {
        var bounds = VirtualDesktop.BoundsOf([
            Monitor(0, 0, 1920, 1080, true),
            Monitor(-1680, 0, 1680, 1050),
        ]);

        Assert.Equal(-1680, bounds.X);
        Assert.Equal(0, bounds.Y);
        Assert.Equal(3600, bounds.Width);
        Assert.Equal(1080, bounds.Height);
    }

    [Fact]
    public void BoundsOf_handles_mixed_heights_and_vertical_arrangement()
    {
        var bounds = VirtualDesktop.BoundsOf([
            Monitor(0, 0, 2560, 1440, true),
            Monitor(2560, -200, 1920, 1200),
            Monitor(0, 1440, 2560, 1080),
        ]);

        Assert.Equal(new PhysicalRect(0, -200, 4480, 2720), bounds);
    }

    [Fact]
    public void BoundsOf_throws_on_empty_input()
    {
        Assert.Throws<ArgumentException>(() => VirtualDesktop.BoundsOf([]));
    }

    [Fact]
    public void ToMonitorLocal_subtracts_monitor_origin()
    {
        var monitor = Monitor(-1920, 120, 1920, 1080);
        var absolute = new PhysicalRect(-1800, 300, 400, 200);

        var local = VirtualDesktop.ToMonitorLocal(absolute, monitor);

        Assert.Equal(120, local.X);
        Assert.Equal(180, local.Y);
        Assert.Equal(400, local.Width);
        Assert.Equal(200, local.Height);
    }
}
