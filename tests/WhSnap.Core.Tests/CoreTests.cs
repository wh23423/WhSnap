using WhSnap.Core;
using Xunit;

namespace WhSnap.Core.Tests;

public class PhysicalRectTests
{
    [Theory]
    [InlineData(10, 20, 110, 220, 10, 20, 100, 200)]
    [InlineData(110, 220, 10, 20, 10, 20, 100, 200)] // 反向拖拽必须归一化
    [InlineData(50, 50, 50, 50, 50, 50, 0, 0)]       // 原地点击 → 空矩形
    public void FromPoints_normalizes_direction(int x1, int y1, int x2, int y2, int ex, int ey, int ew, int eh)
    {
        var rect = PhysicalRect.FromPoints(x1, y1, x2, y2);

        Assert.Equal(ex, rect.X);
        Assert.Equal(ey, rect.Y);
        Assert.Equal(ew, rect.Width);
        Assert.Equal(eh, rect.Height);
    }

    [Fact]
    public void IsEmpty_true_only_for_zero_or_negative_size()
    {
        Assert.True(new PhysicalRect(0, 0, 0, 5).IsEmpty);
        Assert.True(new PhysicalRect(0, 0, 5, 0).IsEmpty);
        Assert.False(new PhysicalRect(0, 0, 1, 1).IsEmpty);
    }

    [Fact]
    public void ClampTo_clips_rect_to_bounds()
    {
        var bounds = new PhysicalRect(0, 0, 1920, 1080);
        var rect = PhysicalRect.FromPoints(-100, -50, 2000, 2000);

        var clamped = rect.ClampTo(bounds);

        Assert.Equal(new PhysicalRect(0, 0, 1920, 1080), clamped);
    }

    [Fact]
    public void ClampTo_handles_negative_origin_monitor()
    {
        // 副屏在主屏左侧时，物理坐标可能是负数
        var bounds = new PhysicalRect(-1920, 0, 1920, 1080);
        var rect = PhysicalRect.FromPoints(-2000, -100, -100, 500);

        var clamped = rect.ClampTo(bounds);

        Assert.Equal(-1920, clamped.X);
        Assert.Equal(0, clamped.Y);
        Assert.Equal(1820, clamped.Width);
        Assert.Equal(500, clamped.Height);
    }

    [Fact]
    public void Intersect_returns_empty_when_disjoint()
    {
        var a = new PhysicalRect(0, 0, 100, 100);
        var b = new PhysicalRect(200, 200, 100, 100);

        Assert.True(a.Intersect(b).IsEmpty);
    }

    [Fact]
    public void Offset_shifts_origin_but_keeps_size()
    {
        var rect = new PhysicalRect(10, 10, 100, 50);

        var shifted = rect.Offset(-1920, 30);

        Assert.Equal(-1910, shifted.X);
        Assert.Equal(40, shifted.Y);
        Assert.Equal(100, shifted.Width);
        Assert.Equal(50, shifted.Height);
    }
}

public class SelectionModelTests
{
    [Fact]
    public void New_model_has_no_selection()
    {
        var model = new SelectionModel(new PhysicalRect(0, 0, 1920, 1080));

        Assert.False(model.HasSelection);
        Assert.False(model.IsDragging);
        Assert.Null(model.Anchor);
    }

    [Fact]
    public void Drag_produces_normalized_selection_in_monitor_local_pixels()
    {
        var model = new SelectionModel(new PhysicalRect(0, 0, 1920, 1080));

        model.Begin(new PhysicalPoint(500, 400));
        var rect = model.Update(new PhysicalPoint(300, 200));
        model.End();

        Assert.Equal(new PhysicalRect(300, 200, 200, 200), rect);
        Assert.True(model.HasSelection);
        Assert.False(model.IsDragging);
    }

    [Fact]
    public void Drag_beyond_bounds_is_clamped_instead_of_overflowing()
    {
        var model = new SelectionModel(new PhysicalRect(0, 0, 1920, 1080));

        model.Begin(new PhysicalPoint(100, 100));
        var rect = model.Update(new PhysicalPoint(5000, 5000));

        Assert.Equal(1920, rect.Right);
        Assert.Equal(1080, rect.Bottom);
        Assert.Equal(1820, rect.Width);
        Assert.Equal(980, rect.Height);
    }

    [Fact]
    public void Drag_respects_negative_origin_monitor()
    {
        var model = new SelectionModel(new PhysicalRect(-1920, 0, 1920, 1080));

        model.Begin(new PhysicalPoint(-1800, 100));
        var rect = model.Update(new PhysicalPoint(-2500, -200));

        // 起点(-1800,100)，终点被夹到(-1920,0)
        Assert.Equal(-1920, rect.X);
        Assert.Equal(0, rect.Y);
        Assert.Equal(120, rect.Width);
        Assert.Equal(100, rect.Height);
    }

    [Fact]
    public void Cancel_clears_state()
    {
        var model = new SelectionModel(new PhysicalRect(0, 0, 800, 600));
        model.Begin(new PhysicalPoint(10, 10));
        model.Update(new PhysicalPoint(200, 200));

        model.Cancel();

        Assert.False(model.HasSelection);
        Assert.Null(model.Anchor);
        Assert.Null(model.Cursor);
    }

    [Fact]
    public void Update_without_begin_does_not_throw()
    {
        var model = new SelectionModel(new PhysicalRect(0, 0, 800, 600));

        var rect = model.Update(new PhysicalPoint(100, 100));

        Assert.False(model.HasSelection);
        Assert.True(rect.IsEmpty);
    }
}

public class DpiMathTests
{
    [Theory]
    [InlineData(150, 1.5, 100)]
    [InlineData(3840, 2.0, 1920)]
    [InlineData(1920, 1.0, 1920)]
    public void PxToDip_divides_by_scale(int px, double scale, double expected)
    {
        Assert.Equal(expected, DpiMath.PxToDip(px, scale), 3);
    }

    [Theory]
    [InlineData(100.0, 1.5, 150)]
    [InlineData(1920.0, 2.0, 3840)]
    [InlineData(96.4, 2.0, 193)]
    public void DipToPx_multiplies_by_scale_with_rounding(double dip, double scale, int expected)
    {
        Assert.Equal(expected, DpiMath.DipToPx(dip, scale));
    }

    [Fact]
    public void DipToPx_round_trips_within_one_pixel()
    {
        const double scale = 1.75;

        for (int px = 0; px < 4000; px += 7)
        {
            double dip = DpiMath.PxToDip(px, scale);
            Assert.InRange(DpiMath.DipToPx(dip, scale), px - 1, px + 1);
        }
    }

    [Fact]
    public void Zero_scale_falls_back_to_identity()
    {
        Assert.Equal(500, DpiMath.PxToDip(500, 0));
    }

    [Fact]
    public void ToVirtual_offsets_monitor_local_rect()
    {
        var monitor = new MonitorInfo("", new PhysicalRect(-1920, 120, 1920, 1080), new PhysicalRect(-1920, 120, 1920, 1040), 144, 144, false);
        var local = new PhysicalRect(100, 200, 300, 400);

        var absolute = DpiMath.ToVirtual(local, monitor);

        Assert.Equal(-1820, absolute.X);
        Assert.Equal(320, absolute.Y);
        Assert.Equal(300, absolute.Width);
        Assert.Equal(400, absolute.Height);
    }
}

public class HotkeySpecTests
{
    [Fact]
    public void Parses_plain_function_key()
    {
        Assert.True(HotkeySpec.TryParse("F1", out var spec, out _));
        Assert.Equal(0u, spec.Modifiers);
        Assert.Equal(0x70u, spec.VirtualKey);
        Assert.Equal("F1", spec.ToString());
    }

    [Fact]
    public void Parses_modifier_combination_case_insensitively()
    {
        Assert.True(HotkeySpec.TryParse("ctrl+SHIFT+a", out var spec, out _));

        Assert.Equal(HotkeySpec.ModControl | HotkeySpec.ModShift, spec.Modifiers);
        Assert.Equal((uint)'A', spec.VirtualKey);
        Assert.Equal("Ctrl+Shift+A", spec.ToString());
    }

    [Fact]
    public void Parses_named_keys()
    {
        Assert.True(HotkeySpec.TryParse("PrtScn", out var printScreen, out _));
        Assert.Equal(0x2Cu, printScreen.VirtualKey);

        Assert.True(HotkeySpec.TryParse("Alt+PgDn", out var pageDown, out _));
        Assert.Equal(0x22u, pageDown.VirtualKey);
        Assert.Equal(HotkeySpec.ModAlt, pageDown.Modifiers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NoSuchKey")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Shift")]
    [InlineData("F25")]
    public void Rejects_invalid_input(string text)
    {
        Assert.False(HotkeySpec.TryParse(text, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Register_modifiers_add_no_repeat_flag()
    {
        var spec = new HotkeySpec(HotkeySpec.ModControl, 0x70);

        Assert.Equal(HotkeySpec.ModControl | HotkeySpec.ModNoRepeat, spec.RegisterModifiers);
    }

    [Fact]
    public void ToString_round_trips_for_every_modifier_combination()
    {
        uint[] combos =
        [
            0,
            HotkeySpec.ModControl,
            HotkeySpec.ModAlt,
            HotkeySpec.ModShift,
            HotkeySpec.ModWin,
            HotkeySpec.ModControl | HotkeySpec.ModShift,
            HotkeySpec.ModControl | HotkeySpec.ModAlt | HotkeySpec.ModShift,
            HotkeySpec.ModControl | HotkeySpec.ModAlt | HotkeySpec.ModShift | HotkeySpec.ModWin,
        ];

        foreach (uint modifiers in combos)
        {
            var original = new HotkeySpec(modifiers, 0x70);
            Assert.True(HotkeySpec.TryParse(original.ToString(), out var parsed, out _), $"无法回解：{original}");
            Assert.Equal(original, parsed);
        }
    }
}
