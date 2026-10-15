using WhSnap.Core;
using Xunit;

namespace WhSnap.Core.Tests;

public class PinGeometryTests
{
    private static PinState Make(int x = 100, int y = 200, int width = 400, int height = 300) =>
        PinState.ForImage(width, height, new PhysicalPoint(x, y));

    [Fact]
    public void ForImage_defaults_to_100_percent_at_requested_position()
    {
        var pin = Make();

        Assert.Equal(1.0, pin.Zoom);
        Assert.Equal(100, pin.Opacity);
        Assert.False(pin.Hidden);
        Assert.False(pin.ClickThrough);
        Assert.Equal(new PhysicalRect(100, 200, 400, 300), pin.Bounds);
    }

    [Theory]
    [InlineData(PinRotation.None, 400, 300)]
    [InlineData(PinRotation.Clockwise90, 300, 400)]
    [InlineData(PinRotation.Clockwise180, 400, 300)]
    [InlineData(PinRotation.Clockwise270, 300, 400)]
    public void Bounds_swaps_size_on_quarter_turns(PinRotation rotation, int expectedWidth, int expectedHeight)
    {
        var pin = Make() with { Rotation = rotation };

        Assert.Equal((expectedWidth, expectedHeight), PinGeometry.DisplaySize(pin));
    }

    [Fact]
    public void Bounds_scales_with_zoom_and_never_drops_below_one_pixel()
    {
        var pin = Make() with { Zoom = 0.5 };
        Assert.Equal(new PhysicalRect(100, 200, 200, 150), PinGeometry.Bounds(pin));

        var tiny = Make() with { Zoom = PinGeometry.MinZoom };
        Assert.True(PinGeometry.Bounds(tiny).Width >= 1);
        Assert.True(PinGeometry.Bounds(tiny).Height >= 1);
    }

    [Fact]
    public void ZoomBy_keeps_the_anchor_pixel_under_the_cursor()
    {
        var pin = Make();
        var anchor = new PhysicalPoint(200, 300); // 贴图内 (100,100) 处

        var zoomed = PinGeometry.ZoomBy(pin, 2.0, anchor);

        // 锚点在贴图内的相对偏移必须等比放大：贴图内 (100,100) 处放大到 (200,200)
        Assert.Equal(2.0, zoomed.Zoom);
        Assert.Equal(anchor.X - (100 * 2), zoomed.Position.X);
        Assert.Equal(anchor.Y - (100 * 2), zoomed.Position.Y);
        Assert.Equal(800, zoomed.Bounds.Width);
    }

    [Fact]
    public void ZoomBy_clamps_to_supported_range()
    {
        var pin = Make();

        var huge = PinGeometry.ZoomBy(pin, 1000, new PhysicalPoint(100, 200));
        Assert.Equal(PinGeometry.MaxZoom, huge.Zoom);

        var tiny = PinGeometry.ZoomBy(pin, 0.0001, new PhysicalPoint(100, 200));
        Assert.Equal(PinGeometry.MinZoom, tiny.Zoom);
    }

    [Fact]
    public void ZoomSteps_applies_step_per_notch_and_ignores_zero()
    {
        var pin = Make();
        var anchor = new PhysicalPoint(100, 200);

        var up = PinGeometry.ZoomSteps(pin, 2, anchor);
        Assert.Equal(1.21, up.Zoom, 3);

        var down = PinGeometry.ZoomSteps(up, -2, anchor);
        Assert.Equal(1.0, down.Zoom, 3);

        Assert.Same(pin, PinGeometry.ZoomSteps(pin, 0, anchor));
    }

    [Fact]
    public void ZoomTo_clamps_and_returns_same_state_when_unchanged()
    {
        var pin = Make();

        Assert.Same(pin, PinGeometry.ZoomTo(pin, 1.0, new PhysicalPoint(100, 200)));
        Assert.Equal(PinGeometry.MaxZoom, PinGeometry.ZoomTo(pin, 999, new PhysicalPoint(100, 200)).Zoom);
    }

    [Fact]
    public void ResizeTo_keeps_aspect_ratio_and_clears_thumbnail_mode()
    {
        var pin = Make() with { Thumbnail = true };

        var resized = PinGeometry.ResizeTo(pin, 800, 600);

        Assert.Equal(2.0, resized.Zoom, 3);
        Assert.False(resized.Thumbnail);
        Assert.Equal(new PhysicalRect(100, 200, 800, 600), resized.Bounds);
    }

    [Fact]
    public void RotateClockwise_keeps_visual_center_and_swaps_display_size()
    {
        var pin = Make();
        var center = PinGeometry.Center(pin);

        var rotated = PinGeometry.RotateClockwise(pin);

        Assert.Equal(PinRotation.Clockwise90, rotated.Rotation);
        Assert.Equal((300, 400), PinGeometry.DisplaySize(rotated));
        Assert.Equal(center, PinGeometry.Center(rotated));
    }

    [Fact]
    public void Rotate_four_times_returns_to_the_original_state()
    {
        var pin = Make();
        var rotated = pin;

        for (int i = 0; i < 4; i++)
        {
            rotated = PinGeometry.RotateClockwise(rotated);
        }

        Assert.Equal(pin.Rotation, rotated.Rotation);
        Assert.Equal(pin.Position, rotated.Position);
        Assert.Equal(pin.Bounds, rotated.Bounds);
    }

    [Fact]
    public void RotateCounterClockwise_undoes_RotateClockwise()
    {
        var pin = Make();

        var back = PinGeometry.RotateCounterClockwise(PinGeometry.RotateClockwise(pin));

        Assert.Equal(pin.Rotation, back.Rotation);
        Assert.Equal(pin.Position, back.Position);
    }

    [Fact]
    public void Flip_toggles_flags_without_moving_the_pin()
    {
        var pin = Make();

        var flipped = PinGeometry.FlipVertically(PinGeometry.FlipHorizontally(pin));

        Assert.True(flipped.FlipHorizontal);
        Assert.True(flipped.FlipVertical);
        Assert.Equal(pin.Bounds, flipped.Bounds);
    }

    [Fact]
    public void SetThumbnail_shrinks_display_size_but_keeps_center()
    {
        var pin = Make();
        var center = PinGeometry.Center(pin);

        var thumb = PinGeometry.SetThumbnail(pin, true);

        Assert.True(thumb.Thumbnail);
        Assert.Equal(100, thumb.Bounds.Width);
        Assert.Equal(75, thumb.Bounds.Height);
        Assert.Equal(center, PinGeometry.Center(thumb));

        // 缩略图只是显示模式：原始像素尺寸不变
        Assert.Equal(400, thumb.SourceWidth);
    }

    [Fact]
    public void ToggleThumbnail_is_an_involution()
    {
        var pin = Make();

        var back = PinGeometry.ToggleThumbnail(PinGeometry.ToggleThumbnail(pin));

        Assert.False(back.Thumbnail);
        Assert.Equal(pin.Position, back.Position);
    }

    [Fact]
    public void ResetZoom_restores_100_percent_around_center()
    {
        var pin = PinGeometry.ZoomBy(Make(), 4.0, new PhysicalPoint(100, 200)) with { Thumbnail = true };
        var center = PinGeometry.Center(pin);

        var reset = PinGeometry.ResetZoom(pin);

        Assert.Equal(1.0, reset.Zoom);
        Assert.False(reset.Thumbnail);
        Assert.Equal(center, PinGeometry.Center(reset));
    }

    [Fact]
    public void AdjustOpacity_clamps_to_0_100()
    {
        var pin = Make();

        Assert.Equal(60, PinGeometry.AdjustOpacity(pin, -40).Opacity);
        Assert.Equal(0, PinGeometry.AdjustOpacity(pin, -500).Opacity);
        Assert.Equal(100, PinGeometry.AdjustOpacity(pin, 500).Opacity);
    }

    [Fact]
    public void Contains_and_Center_use_display_bounds()
    {
        var pin = Make();

        Assert.True(PinGeometry.Contains(pin, new PhysicalPoint(100, 200)));
        Assert.True(PinGeometry.Contains(pin, new PhysicalPoint(499, 499)));
        Assert.False(PinGeometry.Contains(pin, new PhysicalPoint(500, 500)));
        Assert.Equal(new PhysicalPoint(300, 350), PinGeometry.Center(pin));
    }

    [Fact]
    public void EnsureVisible_pulls_a_pin_back_from_offscreen()
    {
        var desktop = new PhysicalRect(0, 0, 1920, 1080);
        var offscreen = Make(x: 5000, y: 5000);

        var fixedPin = PinGeometry.EnsureVisible(offscreen, desktop);

        Assert.False(fixedPin.Bounds.Intersect(desktop).IsEmpty);
    }

    [Fact]
    public void EnsureVisible_keeps_a_pin_inside_the_desktop_untouched()
    {
        var desktop = new PhysicalRect(0, 0, 1920, 1080);
        var pin = Make(x: 100, y: 100, width: 200, height: 200);

        Assert.Same(pin, PinGeometry.EnsureVisible(pin, desktop));
    }

    [Fact]
    public void EnsureVisible_allows_a_small_overhang_but_clamps_the_origin()
    {
        var desktop = new PhysicalRect(0, 0, 1920, 1080);
        var pin = Make(x: 1900, y: 1060, width: 200, height: 200);

        var fixedPin = PinGeometry.EnsureVisible(pin, desktop);

        Assert.Equal(1920 - 200, fixedPin.Position.X);
        Assert.Equal(1080 - 200, fixedPin.Position.Y);
    }

    [Fact]
    public void SnapToEdges_snaps_to_the_nearest_monitor_edge()
    {
        var monitor = new MonitorInfo("m", new PhysicalRect(0, 0, 1920, 1080), new PhysicalRect(0, 0, 1920, 1040), 96, 96, true);
        var pin = Make(x: 4, y: 300, width: 200, height: 200);

        var snapped = PinGeometry.SnapToEdges(pin, [monitor], 8, out bool snappedX, out bool snappedY);

        Assert.True(snappedX);
        Assert.False(snappedY);
        Assert.Equal(0, snapped.Position.X);
        Assert.Equal(300, snapped.Position.Y);
    }

    [Fact]
    public void SnapToEdges_leaves_position_alone_outside_tolerance()
    {
        var monitor = new MonitorInfo("m", new PhysicalRect(0, 0, 1920, 1080), new PhysicalRect(0, 0, 1920, 1040), 96, 96, true);
        var pin = Make(x: 40, y: 300, width: 200, height: 200);

        var snapped = PinGeometry.SnapToEdges(pin, [monitor], 8, out bool snappedX, out bool snappedY);

        Assert.False(snappedX);
        Assert.False(snappedY);
        Assert.Same(pin, snapped);
    }

    [Fact]
    public void ReconcileTopology_keeps_relative_position_when_the_desktop_moves()
    {
        var oldDesktop = new PhysicalRect(0, 0, 1920, 1080);
        var newDesktop = new PhysicalRect(-1280, 0, 3200, 1080);
        var pin = Make(x: 1000, y: 540, width: 200, height: 200);

        var moved = PinGeometry.ReconcileTopology(pin, oldDesktop, newDesktop);

        Assert.Equal(-1280 + 1000, moved.Position.X);
        Assert.Equal(540, moved.Position.Y);
    }

    [Fact]
    public void ReconcileTopology_shrinks_positions_when_the_desktop_gets_smaller()
    {
        var oldDesktop = new PhysicalRect(0, 0, 3840, 2160);
        var newDesktop = new PhysicalRect(0, 0, 1920, 1080);
        var pin = Make(x: 1920, y: 1080, width: 100, height: 100);

        var moved = PinGeometry.ReconcileTopology(pin, oldDesktop, newDesktop);

        Assert.Equal(960, moved.Position.X);
        Assert.Equal(540, moved.Position.Y);
    }

    [Fact]
    public void ReconcileTopology_does_not_spread_pins_when_the_desktop_grows()
    {
        var oldDesktop = new PhysicalRect(0, 0, 1920, 1080);
        var newDesktop = new PhysicalRect(0, 0, 3840, 2160);
        var pin = Make(x: 960, y: 540, width: 100, height: 100);

        var moved = PinGeometry.ReconcileTopology(pin, oldDesktop, newDesktop);

        Assert.Equal(960, moved.Position.X);
        Assert.Equal(540, moved.Position.Y);
    }

    [Fact]
    public void ReconcileTopology_returns_visible_pin_when_desktop_is_unchanged()
    {
        var desktop = new PhysicalRect(0, 0, 1920, 1080);
        var pin = Make(x: 100, y: 100, width: 200, height: 200);

        Assert.Same(pin, PinGeometry.ReconcileTopology(pin, desktop, desktop));
    }
}
