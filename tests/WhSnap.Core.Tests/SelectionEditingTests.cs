using WhSnap.Core;
using Xunit;

namespace WhSnap.Core.Tests;

/// <summary>选区编辑（手柄缩放、平移、方向键微调）与放大镜几何的测试。</summary>
public class SelectionEditingTests
{
    private static SelectionModel Model(int width = 1920, int height = 1080) =>
        new(new PhysicalRect(0, 0, width, height));

    private static SelectionModel WithSelection(PhysicalRect rect, int width = 1920, int height = 1080)
    {
        var model = Model(width, height);
        model.SetSelection(rect);
        return model;
    }

    [Theory]
    [InlineData(100, 100, SelectionHandle.TopLeft)]
    [InlineData(300, 100, SelectionHandle.TopRight)]
    [InlineData(100, 200, SelectionHandle.BottomLeft)]
    [InlineData(300, 200, SelectionHandle.BottomRight)]
    [InlineData(200, 100, SelectionHandle.Top)]
    [InlineData(200, 200, SelectionHandle.Bottom)]
    [InlineData(100, 150, SelectionHandle.Left)]
    [InlineData(300, 150, SelectionHandle.Right)]
    [InlineData(200, 150, SelectionHandle.Inside)]
    [InlineData(500, 500, SelectionHandle.None)]
    public void HitTest_resolves_handles_inside_and_outside(int x, int y, SelectionHandle expected)
    {
        var selection = new PhysicalRect(100, 100, 200, 100);

        Assert.Equal(expected, SelectionModel.HitTest(selection, new PhysicalPoint(x, y), 6));
    }

    [Fact]
    public void HitTest_returns_none_for_empty_selection()
    {
        Assert.Equal(SelectionHandle.None, SelectionModel.HitTest(new PhysicalRect(10, 10, 0, 0), new PhysicalPoint(10, 10), 6));
    }

    [Fact]
    public void HitTest_respects_tolerance()
    {
        var selection = new PhysicalRect(100, 100, 200, 100);

        // 距左边 5px：容差 6 → 命中 Left；容差 2 → 已在选区外
        Assert.Equal(SelectionHandle.Left, SelectionModel.HitTest(selection, new PhysicalPoint(95, 150), 6));
        Assert.Equal(SelectionHandle.None, SelectionModel.HitTest(selection, new PhysicalPoint(95, 150), 2));

        // 选区内侧 2px：容差 2 仍算落在边线上（用于抓取已选区的边）
        Assert.Equal(SelectionHandle.Left, SelectionModel.HitTest(selection, new PhysicalPoint(102, 150), 2));
    }

    [Fact]
    public void Resize_bottom_right_grows_selection()
    {
        var model = WithSelection(new PhysicalRect(100, 100, 200, 100));

        model.BeginResize(SelectionHandle.BottomRight, new PhysicalPoint(300, 200));
        model.Update(new PhysicalPoint(400, 300));
        model.End();

        Assert.Equal(new PhysicalRect(100, 100, 300, 200), model.Current);
    }

    [Fact]
    public void Resize_top_left_moves_origin_and_keeps_opposite_corner()
    {
        var model = WithSelection(new PhysicalRect(100, 100, 200, 100));

        model.BeginResize(SelectionHandle.TopLeft, new PhysicalPoint(100, 100));
        model.Update(new PhysicalPoint(150, 130));

        Assert.Equal(new PhysicalRect(150, 130, 150, 70), model.Current);
    }

    [Fact]
    public void Resize_left_edge_only_changes_x_and_width()
    {
        var model = WithSelection(new PhysicalRect(100, 100, 200, 100));

        model.BeginResize(SelectionHandle.Left, new PhysicalPoint(100, 150));
        model.Update(new PhysicalPoint(160, 150));

        Assert.Equal(new PhysicalRect(160, 100, 140, 100), model.Current);
    }

    [Fact]
    public void Resize_flipping_over_the_opposite_edge_is_allowed()
    {
        var model = WithSelection(new PhysicalRect(100, 100, 200, 100));

        model.BeginResize(SelectionHandle.Right, new PhysicalPoint(300, 150));
        model.Update(new PhysicalPoint(50, 150));

        // 拖过左边后仍应得到一个归一化的正矩形
        Assert.Equal(50, model.Current.X);
        Assert.Equal(50, model.Current.Width);
        Assert.Equal(100, model.Current.Y);
        Assert.Equal(100, model.Current.Height);
    }

    [Fact]
    public void Resize_never_shrinks_below_one_pixel()
    {
        var model = WithSelection(new PhysicalRect(100, 100, 10, 10));

        model.BeginResize(SelectionHandle.Right, new PhysicalPoint(110, 105));
        model.Update(new PhysicalPoint(100, 105));

        Assert.Equal(SelectionModel.MinSize, model.Current.Width);
    }

    [Fact]
    public void Resize_is_clamped_to_bounds()
    {
        var model = WithSelection(new PhysicalRect(100, 100, 200, 100));

        model.BeginResize(SelectionHandle.BottomRight, new PhysicalPoint(300, 200));
        model.Update(new PhysicalPoint(9999, 9999));

        Assert.Equal(1920, model.Current.Right);
        Assert.Equal(1080, model.Current.Bottom);
    }

    [Fact]
    public void Move_keeps_size_and_follows_pointer_delta()
    {
        var model = WithSelection(new PhysicalRect(100, 100, 200, 100));

        model.BeginMove(new PhysicalPoint(200, 150));
        model.Update(new PhysicalPoint(300, 250));

        Assert.Equal(new PhysicalRect(200, 200, 200, 100), model.Current);
    }

    [Fact]
    public void Move_is_clamped_at_bounds_without_resizing()
    {
        var model = WithSelection(new PhysicalRect(1700, 900, 200, 100));

        model.BeginMove(new PhysicalPoint(1800, 950));
        model.Update(new PhysicalPoint(5000, 5000));

        Assert.Equal(1720, model.Current.X);
        Assert.Equal(980, model.Current.Y);
        Assert.Equal(200, model.Current.Width);
        Assert.Equal(100, model.Current.Height);
    }

    [Fact]
    public void Nudge_move_shifts_selection_by_step()
    {
        var model = WithSelection(new PhysicalRect(500, 500, 200, 100));

        Assert.True(model.Nudge(NudgeDirection.Right, 10, resize: false));
        Assert.True(model.Nudge(NudgeDirection.Down, 1, resize: false));

        Assert.Equal(new PhysicalRect(510, 501, 200, 100), model.Current);
    }

    [Fact]
    public void Nudge_move_is_clamped_at_bounds()
    {
        var model = WithSelection(new PhysicalRect(0, 0, 200, 100));

        model.Nudge(NudgeDirection.Left, 10, resize: false);
        model.Nudge(NudgeDirection.Up, 10, resize: false);

        Assert.Equal(0, model.Current.X);
        Assert.Equal(0, model.Current.Y);
    }

    [Fact]
    public void Nudge_resize_grows_and_shrinks_right_bottom_edges()
    {
        var model = WithSelection(new PhysicalRect(100, 100, 200, 100));

        model.Nudge(NudgeDirection.Right, 10, resize: true);
        Assert.Equal(210, model.Current.Width);

        model.Nudge(NudgeDirection.Left, 10, resize: true);
        Assert.Equal(200, model.Current.Width);

        model.Nudge(NudgeDirection.Down, 5, resize: true);
        Assert.Equal(105, model.Current.Height);

        model.Nudge(NudgeDirection.Up, 5, resize: true);
        Assert.Equal(100, model.Current.Height);
    }

    [Fact]
    public void Nudge_resize_keeps_at_least_one_pixel_and_respects_bounds()
    {
        var model = WithSelection(new PhysicalRect(100, 100, 5, 5));

        for (int i = 0; i < 20; i++)
        {
            model.Nudge(NudgeDirection.Left, 2, resize: true);
            model.Nudge(NudgeDirection.Up, 2, resize: true);
        }

        Assert.Equal(SelectionModel.MinSize, model.Current.Width);
        Assert.Equal(SelectionModel.MinSize, model.Current.Height);

        for (int i = 0; i < 200; i++)
        {
            model.Nudge(NudgeDirection.Right, 20, resize: true);
            model.Nudge(NudgeDirection.Down, 20, resize: true);
        }

        Assert.Equal(1920, model.Current.Right);
        Assert.Equal(1080, model.Current.Bottom);
    }

    [Fact]
    public void Nudge_does_nothing_without_selection()
    {
        var model = Model();

        Assert.False(model.Nudge(NudgeDirection.Right, 1, resize: false));
        Assert.False(model.Nudge(NudgeDirection.Right, 1, resize: true));
    }

    [Fact]
    public void Cross_monitor_drag_uses_absolute_virtual_coordinates()
    {
        // 副屏在主屏左侧：虚拟桌面从 -1920 开始
        var bounds = new PhysicalRect(-1920, 0, 3840, 1080);
        var model = new SelectionModel(bounds);

        model.Begin(new PhysicalPoint(-1700, 200));
        model.Update(new PhysicalPoint(400, 800));
        model.End();

        Assert.Equal(new PhysicalRect(-1700, 200, 2100, 600), model.Current);
        Assert.True(model.Current.Width > 1920, "跨屏选区宽度应超过单屏宽度");
    }
}

public class MagnifierTests
{
    private static readonly PhysicalRect Monitor = new(0, 0, 1920, 1080);

    [Fact]
    public void Compute_centers_source_on_cursor_with_odd_size()
    {
        var layout = Magnifier.Compute(new PhysicalPoint(500, 500), Monitor, sampleSize: 31, zoom: 8);

        Assert.Equal(31, layout.SampleSize);
        Assert.Equal(8, layout.Zoom);
        Assert.Equal(248, layout.DisplaySize);
        Assert.Equal(485, layout.Source.X);
        Assert.Equal(485, layout.Source.Y);
    }

    [Fact]
    public void Compute_rounds_even_sample_size_up_to_odd()
    {
        var layout = Magnifier.Compute(new PhysicalPoint(500, 500), Monitor, sampleSize: 30, zoom: 4);

        Assert.Equal(31, layout.SampleSize);
    }

    [Fact]
    public void Compute_clamps_source_at_monitor_edges()
    {
        var nearOrigin = Magnifier.Compute(new PhysicalPoint(1, 1), Monitor, 31, 8);
        Assert.Equal(0, nearOrigin.Source.X);
        Assert.Equal(0, nearOrigin.Source.Y);
        Assert.Equal(31, nearOrigin.Source.Width);

        var nearFar = Magnifier.Compute(new PhysicalPoint(1919, 1079), Monitor, 31, 8);
        Assert.Equal(1920, nearFar.Source.Right);
        Assert.Equal(1080, nearFar.Source.Bottom);
    }

    [Fact]
    public void Compute_clamps_for_monitor_with_negative_origin()
    {
        var monitor = new PhysicalRect(-1920, 0, 1920, 1080);

        var layout = Magnifier.Compute(new PhysicalPoint(-1920, 500), monitor, 31, 8);

        Assert.Equal(-1920, layout.Source.X);
        Assert.Equal(-1905, layout.Source.X + 15);
    }

    [Fact]
    public void CenterCell_locates_cursor_pixel_inside_source()
    {
        var layout = Magnifier.Compute(new PhysicalPoint(500, 500), Monitor, 31, 8);

        var (x, y) = Magnifier.CenterCell(layout, new PhysicalPoint(500, 500));

        Assert.Equal(15, x);
        Assert.Equal(15, y);
    }

    [Fact]
    public void CenterCell_is_inside_source_even_when_clamped()
    {
        var layout = Magnifier.Compute(new PhysicalPoint(2, 2), Monitor, 31, 8);

        var (x, y) = Magnifier.CenterCell(layout, new PhysicalPoint(2, 2));

        Assert.InRange(x, 0, layout.SampleSize - 1);
        Assert.InRange(y, 0, layout.SampleSize - 1);
    }
}
