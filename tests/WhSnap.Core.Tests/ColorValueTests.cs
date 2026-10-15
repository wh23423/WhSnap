using WhSnap.Core;
using Xunit;

namespace WhSnap.Core.Tests;

public class ColorValueTests
{
    [Theory]
    [InlineData("#FF8040", 255, 128, 64)]
    [InlineData("#f80", 255, 136, 0)]
    [InlineData("rgb(255, 128, 64)", 255, 128, 64)]
    [InlineData("RGB(255 128 64)", 255, 128, 64)]
    [InlineData("rgba(255, 128, 64, 0.5)", 255, 128, 64)]
    [InlineData("255,128,64", 255, 128, 64)]
    [InlineData("255 128 64", 255, 128, 64)]
    [InlineData("hsl(0, 100%, 50%)", 255, 0, 0)]
    public void TryParse_accepts_common_notations(string text, int r, int g, int b)
    {
        Assert.True(ColorValue.TryParse(text, out var color));
        Assert.Equal(new RgbColor((byte)r, (byte)g, (byte)b), color);
    }

    [Fact]
    public void TryParse_requires_the_hash_prefix_for_hex()
    {
        // 裸 "FF8040" 在剪贴板里更可能是普通文本，不做猜测（否则会把内容误当颜色贴成色块）
        Assert.False(ColorValue.TryParse("FF8040", out _));
    }

    [Fact]
    public void TryParse_reads_eight_digit_hex_as_aarrggbb()
    {
        Assert.True(ColorValue.TryParse("#80FF8040", out var color));
        Assert.Equal(new RgbColor(255, 128, 64), color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#12345")]
    [InlineData("rgb(1,2)")]
    [InlineData("hsl(nope)")]
    [InlineData("hello world")]
    public void TryParse_rejects_invalid_input(string? text)
    {
        Assert.False(ColorValue.TryParse(text, out _));
    }

    [Fact]
    public void ToHex_is_round_trippable_and_uppercase()
    {
        var color = new RgbColor(0x0A, 0xB0, 0xC0);

        Assert.Equal("#0AB0C0", color.ToHex());
        Assert.True(ColorValue.TryParse(color.ToHex(), out var parsed));
        Assert.Equal(color, parsed);
    }

    [Theory]
    [InlineData(255, 0, 0, 0.0, 100.0, 50.0)]
    [InlineData(0, 255, 0, 120.0, 100.0, 50.0)]
    [InlineData(0, 0, 255, 240.0, 100.0, 50.0)]
    [InlineData(255, 255, 255, 0.0, 0.0, 100.0)]
    [InlineData(0, 0, 0, 0.0, 0.0, 0.0)]
    public void ToHsl_matches_reference_values(int r, int g, int b, double h, double s, double l)
    {
        var (actualH, actualS, actualL) = ColorValue.ToHsl(new RgbColor((byte)r, (byte)g, (byte)b));

        Assert.Equal(h, actualH, 1);
        Assert.Equal(s, actualS, 1);
        Assert.Equal(l, actualL, 1);
    }

    [Fact]
    public void Hsl_round_trip_stays_within_one_unit_per_channel()
    {
        for (int r = 0; r <= 255; r += 17)
        {
            for (int g = 0; g <= 255; g += 17)
            {
                for (int b = 0; b <= 255; b += 17)
                {
                    var original = new RgbColor((byte)r, (byte)g, (byte)b);
                    var (h, s, l) = ColorValue.ToHsl(original);
                    var back = ColorValue.FromHsl(h, s, l);

                    Assert.True(Math.Abs(back.R - r) <= 1, $"{original} → {back}");
                    Assert.True(Math.Abs(back.G - g) <= 1, $"{original} → {back}");
                    Assert.True(Math.Abs(back.B - b) <= 1, $"{original} → {back}");
                }
            }
        }
    }

    [Fact]
    public void ToCmyk_handles_black_white_and_primaries()
    {
        Assert.Equal((0.0, 0.0, 0.0, 100.0), ColorValue.ToCmyk(new RgbColor(0, 0, 0)));
        Assert.Equal((0.0, 0.0, 0.0, 0.0), ColorValue.ToCmyk(new RgbColor(255, 255, 255)));
        Assert.Equal((0.0, 100.0, 100.0, 0.0), ColorValue.ToCmyk(new RgbColor(255, 0, 0)));
    }

    [Fact]
    public void Text_helpers_are_human_readable()
    {
        var color = new RgbColor(255, 128, 64);

        Assert.Equal("rgb(255, 128, 64)", ColorValue.ToRgbText(color));
        Assert.StartsWith("hsl(", ColorValue.ToHslText(color));
        Assert.StartsWith("cmyk(", ColorValue.ToCmykText(color));
        Assert.Contains("#FF8040", ColorValue.Describe(color));
    }
}
