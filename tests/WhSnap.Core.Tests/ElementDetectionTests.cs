using WhSnap.Core;
using Xunit;

namespace WhSnap.Core.Tests;

public class ColorHistoryTests
{
    [Fact]
    public void Add_puts_the_newest_first_and_deduplicates()
    {
        var history = new ColorHistory(4);
        history.Add(new RgbColor(1, 2, 3));
        history.Add(new RgbColor(4, 5, 6));
        history.Add(new RgbColor(1, 2, 3));

        Assert.Equal(2, history.Count);
        Assert.Equal(new RgbColor(1, 2, 3), history.Items[0]);
        Assert.Equal(new RgbColor(4, 5, 6), history.Items[1]);
    }

    [Fact]
    public void Add_drops_the_oldest_when_capacity_is_reached()
    {
        var history = new ColorHistory(3);

        for (int i = 1; i <= 5; i++)
        {
            history.Add(new RgbColor((byte)i, 0, 0));
        }

        Assert.Equal(3, history.Count);
        Assert.Equal(new RgbColor(5, 0, 0), history.Items[0]);
        Assert.Equal(new RgbColor(3, 0, 0), history.Items[2]);
        Assert.False(history.Contains(new RgbColor(2, 0, 0)));
    }

    [Fact]
    public void Serialize_and_Load_round_trip()
    {
        var history = new ColorHistory(8);
        history.Add(new RgbColor(0xFF, 0x3B, 0x30));
        history.Add(new RgbColor(0x00, 0x7A, 0xFF));

        var restored = ColorHistory.FromText(history.Serialize(), 8);

        Assert.Equal(history.Items, restored.Items);
    }

    [Fact]
    public void Load_skips_garbage_instead_of_throwing()
    {
        var history = ColorHistory.FromText("#FF3B30, not-a-color , rgb(0,122,255)", 8);

        Assert.Equal(2, history.Count);
        Assert.Equal(new RgbColor(0xFF, 0x3B, 0x30), history.Items[0]);
        Assert.Equal(new RgbColor(0x00, 0x7A, 0xFF), history.Items[1]);
    }

    [Fact]
    public void Load_respects_capacity_and_keeps_the_serialized_order()
    {
        // 序列化格式是"最近在前"，所以恢复时保留头部、丢弃尾部
        var history = ColorHistory.FromText("#010101,#020202,#030303", 2);

        Assert.Equal(2, history.Count);
        Assert.Equal(new RgbColor(0x01, 0x01, 0x01), history.Items[0]);
        Assert.Equal(new RgbColor(0x02, 0x02, 0x02), history.Items[1]);
        Assert.False(history.Contains(new RgbColor(0x03, 0x03, 0x03)));
    }

    [Fact]
    public void Clear_and_Remove_work()
    {
        var history = new ColorHistory(4);
        history.Add(new RgbColor(9, 9, 9));

        Assert.True(history.Remove(new RgbColor(9, 9, 9)));
        Assert.False(history.Remove(new RgbColor(9, 9, 9)));
        Assert.Equal(0, history.Count);

        history.Add(new RgbColor(1, 1, 1));
        history.Clear();
        Assert.Equal(0, history.Count);
        Assert.Equal(string.Empty, history.Serialize());
    }
}

public class ColorTextTests
{
    private static readonly RgbColor Sample = new(0xFF, 0x80, 0x40);

    [Fact]
    public void Format_supports_every_notation()
    {
        Assert.Equal("#FF8040", ColorText.Format(Sample, ColorCopyFormat.Hex));
        Assert.Equal("rgb(255, 128, 64)", ColorText.Format(Sample, ColorCopyFormat.Rgb));
        Assert.StartsWith("hsl(", ColorText.Format(Sample, ColorCopyFormat.Hsl));
        Assert.StartsWith("cmyk(", ColorText.Format(Sample, ColorCopyFormat.Cmyk));

        string all = ColorText.Format(Sample, ColorCopyFormat.All);
        Assert.Contains("#FF8040", all);
        Assert.Contains("rgb(255, 128, 64)", all);
        Assert.Contains(Environment.NewLine, all);
    }

    [Fact]
    public void ParseFormat_is_case_insensitive_and_defaults_to_hex()
    {
        Assert.Equal(ColorCopyFormat.Rgb, ColorText.ParseFormat("RGB"));
        Assert.Equal(ColorCopyFormat.Cmyk, ColorText.ParseFormat("cmyk"));
        Assert.Equal(ColorCopyFormat.All, ColorText.ParseFormat("all"));
        Assert.Equal(ColorCopyFormat.Hex, ColorText.ParseFormat("nonsense"));
        Assert.Equal(ColorCopyFormat.Hex, ColorText.ParseFormat(null));
    }

    [Fact]
    public void ToName_round_trips()
    {
        foreach (var format in Enum.GetValues<ColorCopyFormat>())
        {
            Assert.Equal(format, ColorText.ParseFormat(ColorText.ToName(format)));
        }
    }
}

public class ElementDetectionTests
{
    private static ElementCandidate Candidate(int x, int y, int w, int h, string name = "", string source = "uia", int depth = 0) =>
        new(new PhysicalRect(x, y, w, h), name, source, depth);

    [Fact]
    public void Choose_prefers_the_smallest_candidate_containing_the_point()
    {
        var candidates = new List<ElementCandidate>
        {
            Candidate(0, 0, 1920, 1080, "窗口"),
            Candidate(100, 100, 800, 600, "面板"),
            Candidate(200, 200, 120, 32, "按钮"),
        };

        var chosen = ElementDetection.Choose(candidates, new PhysicalPoint(250, 210));

        Assert.NotNull(chosen);
        Assert.Equal("按钮", chosen!.Name);
    }

    [Fact]
    public void Choose_falls_back_to_the_outer_element_when_the_point_is_outside_inner_ones()
    {
        var candidates = new List<ElementCandidate>
        {
            Candidate(0, 0, 1000, 800, "窗口"),
            Candidate(100, 100, 200, 100, "输入框"),
        };

        var chosen = ElementDetection.Choose(candidates, new PhysicalPoint(900, 700));

        Assert.Equal("窗口", chosen!.Name);
    }

    [Fact]
    public void Choose_returns_null_when_nothing_contains_the_point()
    {
        var candidates = new List<ElementCandidate> { Candidate(0, 0, 100, 100) };

        Assert.Null(ElementDetection.Choose(candidates, new PhysicalPoint(500, 500)));
    }

    [Fact]
    public void Cycle_goes_from_inner_controls_out_to_the_window()
    {
        var candidates = new List<ElementCandidate>
        {
            Candidate(0, 0, 1920, 1080, "窗口"),
            Candidate(100, 100, 800, 600, "面板"),
            Candidate(200, 200, 120, 32, "按钮"),
        };

        var point = new PhysicalPoint(250, 210);

        var first = ElementDetection.Cycle(candidates, point, null);
        var second = ElementDetection.Cycle(candidates, point, first);
        var third = ElementDetection.Cycle(candidates, point, second);
        var fourth = ElementDetection.Cycle(candidates, point, third);

        Assert.Equal("按钮", first!.Name);
        Assert.Equal("面板", second!.Name);
        Assert.Equal("窗口", third!.Name);
        Assert.Equal("按钮", fourth!.Name); // 循环回到最里层
    }

    [Fact]
    public void Cycle_ignores_candidates_that_do_not_contain_the_point()
    {
        var candidates = new List<ElementCandidate>
        {
            Candidate(0, 0, 1000, 1000, "窗口"),
            Candidate(2000, 2000, 100, 100, "另一个窗口的控件"),
        };

        var ordered = ElementDetection.OrderForCycle(candidates, new PhysicalPoint(10, 10));

        Assert.Single(ordered);
        Assert.Equal("窗口", ordered[0].Name);
    }

    [Fact]
    public void Cycle_returns_null_for_an_empty_candidate_list()
    {
        Assert.Null(ElementDetection.Cycle([], new PhysicalPoint(5, 5), null));
    }

    [Fact]
    public void Distinct_removes_duplicate_rectangles_and_empty_ones()
    {
        var candidates = new List<ElementCandidate>
        {
            Candidate(0, 0, 100, 100, "A", "uia"),
            Candidate(0, 0, 100, 100, "A", "window"),
            Candidate(0, 0, 0, 0, "空"),
            Candidate(50, 50, 20, 20, "B"),
        };

        var distinct = ElementDetection.Distinct(candidates);

        Assert.Equal(2, distinct.Count);
        Assert.Equal("A", distinct[0].Name);
        Assert.Equal("B", distinct[1].Name);
    }

    [Fact]
    public void Candidate_describes_itself_for_the_overlay_hint()
    {
        var text = Candidate(10, 20, 100, 30, "保存按钮", "uia", 2).Describe();

        Assert.Contains("10,20 100x30", text);
        Assert.Contains("保存按钮", text);
        Assert.Contains("uia", text);
        Assert.Contains("层级 2", text);
    }
}
