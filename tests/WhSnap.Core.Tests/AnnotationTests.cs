using WhSnap.Core;
using Xunit;

namespace WhSnap.Core.Tests;

public class AnnotationDocumentTests
{
    private static AnnotationStyle Style => AnnotationStyle.Default;

    private static AnnotationDocument NewDocument()
    {
        var document = new AnnotationDocument();
        document.Reset();
        return document;
    }

    [Fact]
    public void New_document_is_empty_and_has_nothing_to_undo()
    {
        var document = NewDocument();

        Assert.True(document.IsEmpty);
        Assert.False(document.CanUndo);
        Assert.False(document.CanRedo);
        Assert.Equal(0, document.Count);
    }

    [Fact]
    public void Add_assigns_increasing_ids_and_returns_the_item()
    {
        var document = NewDocument();

        var first = document.Add(AnnotationKind.Rectangle, [new PhysicalPoint(0, 0), new PhysicalPoint(10, 10)], Style);
        var second = document.Add(AnnotationKind.Ellipse, [new PhysicalPoint(5, 5), new PhysicalPoint(20, 20)], Style);

        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);
        Assert.Equal(2, document.Count);
        Assert.True(document.CanUndo);
        Assert.False(document.CanRedo);
    }

    [Fact]
    public void Badge_numbers_advance_and_respect_the_configured_start()
    {
        var document = NewDocument();
        document.Reset(badgeStart: 5);

        var first = document.Add(AnnotationKind.Badge, [new PhysicalPoint(10, 10)], Style);
        var second = document.Add(AnnotationKind.Badge, [new PhysicalPoint(40, 10)], Style);

        Assert.Equal(5, first.Badge);
        Assert.Equal(6, second.Badge);
    }

    [Fact]
    public void Undo_and_redo_restore_previous_item_lists()
    {
        var document = NewDocument();
        document.Add(AnnotationKind.Rectangle, [new PhysicalPoint(0, 0), new PhysicalPoint(10, 10)], Style);
        var second = document.Add(AnnotationKind.Arrow, [new PhysicalPoint(0, 0), new PhysicalPoint(50, 50)], Style);

        Assert.True(document.Undo());
        Assert.Equal(1, document.Count);
        Assert.True(document.CanRedo);

        Assert.True(document.Redo());
        Assert.Equal(2, document.Count);
        Assert.Equal(second.Id, document.Items[1].Id);
    }

    [Fact]
    public void Undo_after_an_edit_brings_back_the_original_geometry()
    {
        var document = NewDocument();
        var item = document.Add(AnnotationKind.Rectangle, [new PhysicalPoint(0, 0), new PhysicalPoint(10, 10)], Style);

        document.Update(AnnotationEditing.Move(item, 100, 100));
        Assert.Equal(100, document.Find(item.Id)!.Start.X);

        Assert.True(document.Undo());
        Assert.Equal(0, document.Find(item.Id)!.Start.X);
    }

    [Fact]
    public void New_actions_clear_the_redo_stack()
    {
        var document = NewDocument();
        document.Add(AnnotationKind.Rectangle, [new PhysicalPoint(0, 0), new PhysicalPoint(5, 5)], Style);
        document.Add(AnnotationKind.Line, [new PhysicalPoint(0, 0), new PhysicalPoint(5, 5)], Style);
        document.Undo();

        Assert.True(document.CanRedo);
        document.Add(AnnotationKind.Text, [new PhysicalPoint(1, 1)], Style, "hi");
        Assert.False(document.CanRedo);
    }

    [Fact]
    public void Remove_and_clear_are_undoable()
    {
        var document = NewDocument();
        var item = document.Add(AnnotationKind.Rectangle, [new PhysicalPoint(0, 0), new PhysicalPoint(5, 5)], Style);
        document.Add(AnnotationKind.Line, [new PhysicalPoint(0, 0), new PhysicalPoint(9, 9)], Style);

        Assert.True(document.Remove(item.Id));
        Assert.Equal(1, document.Count);

        Assert.True(document.Clear());
        Assert.True(document.IsEmpty);

        Assert.True(document.Undo());
        Assert.Equal(1, document.Count);

        Assert.True(document.Undo());
        Assert.Equal(2, document.Count);
    }

    [Fact]
    public void Update_and_Remove_report_unknown_ids_instead_of_throwing()
    {
        var document = NewDocument();

        Assert.False(document.Remove(42));
        Assert.False(document.Update(AnnotationItem.Create(42, AnnotationKind.Line, [new PhysicalPoint(0, 0), new PhysicalPoint(1, 1)], Style)));
        Assert.Null(document.Find(42));
    }

    [Fact]
    public void History_is_capped_so_a_long_session_cannot_grow_without_bound()
    {
        var document = NewDocument();

        for (int i = 0; i < AnnotationDocument.MaxHistory + 50; i++)
        {
            document.Add(AnnotationKind.Pencil, [new PhysicalPoint(i, 0), new PhysicalPoint(i + 1, 1)], Style);
        }

        Assert.Equal(AnnotationDocument.MaxHistory, document.UndoDepth);
    }

    [Fact]
    public void Topmost_prefers_the_last_drawn_item()
    {
        var document = NewDocument();
        document.Add(AnnotationKind.Rectangle, [new PhysicalPoint(0, 0), new PhysicalPoint(100, 100)], Style);
        var top = document.Add(AnnotationKind.Ellipse, [new PhysicalPoint(20, 20), new PhysicalPoint(40, 40)], Style);

        Assert.Equal(top.Id, document.Topmost(new PhysicalPoint(30, 30), 3)?.Id);
        Assert.Null(document.Topmost(new PhysicalPoint(300, 300), 3));
    }

    [Fact]
    public void Reset_clears_items_and_history_but_keeps_the_new_badge_start()
    {
        var document = NewDocument();
        document.Add(AnnotationKind.Badge, [new PhysicalPoint(0, 0)], Style);
        document.Reset(badgeStart: 10);

        Assert.True(document.IsEmpty);
        Assert.False(document.CanUndo);
        Assert.Equal(10, document.Add(AnnotationKind.Badge, [new PhysicalPoint(1, 1)], Style).Badge);
    }
}

public class AnnotationItemTests
{
    private static AnnotationStyle Style => AnnotationStyle.Default;

    [Theory]
    [InlineData(AnnotationKind.Rectangle, true)]
    [InlineData(AnnotationKind.Mosaic, true)]
    [InlineData(AnnotationKind.Blur, true)]
    [InlineData(AnnotationKind.Pencil, false)]
    [InlineData(AnnotationKind.Badge, false)]
    public void IsTwoPoint_classifies_kinds(AnnotationKind kind, bool expected)
    {
        Assert.Equal(expected, AnnotationItem.IsTwoPoint(kind));
    }

    [Theory]
    [InlineData(AnnotationKind.Mosaic, true)]
    [InlineData(AnnotationKind.Blur, true)]
    [InlineData(AnnotationKind.Eraser, true)]
    [InlineData(AnnotationKind.Rectangle, false)]
    [InlineData(AnnotationKind.Marker, false)]
    public void IsPixelEffect_classifies_kinds(AnnotationKind kind, bool expected)
    {
        Assert.Equal(expected, AnnotationItem.IsPixelEffect(kind));
    }

    [Fact]
    public void Bounds_of_two_point_items_are_normalized()
    {
        var item = AnnotationItem.FromDrag(1, AnnotationKind.Rectangle, new PhysicalPoint(90, 80), new PhysicalPoint(10, 20), Style);

        Assert.Equal(new PhysicalRect(10, 20, 80, 60), item.Bounds);
    }

    [Fact]
    public void Bounds_of_stroke_items_include_the_stroke_width()
    {
        var item = AnnotationItem.Create(
            1,
            AnnotationKind.Pencil,
            [new PhysicalPoint(10, 10), new PhysicalPoint(30, 20)],
            Style.WithWidth(6));

        var bounds = item.Bounds;

        Assert.True(bounds.X < 10);
        Assert.True(bounds.Right > 30);
        Assert.True(bounds.Width >= 20);
    }

    [Fact]
    public void Badge_bounds_are_a_square_around_the_anchor()
    {
        var item = AnnotationItem.Create(1, AnnotationKind.Badge, [new PhysicalPoint(50, 50)], Style, badge: 1);

        Assert.Equal(50, item.Bounds.X + (item.Bounds.Width / 2));
        Assert.True(item.Bounds.Width > 0);
    }

    [Fact]
    public void Degenerate_detection_rejects_tiny_drags_and_empty_text()
    {
        Assert.True(AnnotationItem.FromDrag(1, AnnotationKind.Rectangle, new PhysicalPoint(10, 10), new PhysicalPoint(11, 11), Style).IsDegenerate);
        Assert.False(AnnotationItem.FromDrag(2, AnnotationKind.Rectangle, new PhysicalPoint(10, 10), new PhysicalPoint(40, 40), Style).IsDegenerate);
        Assert.True(AnnotationItem.Create(3, AnnotationKind.Text, [new PhysicalPoint(0, 0)], Style, "   ").IsDegenerate);
        Assert.False(AnnotationItem.Create(4, AnnotationKind.Text, [new PhysicalPoint(0, 0)], Style, "ok").IsDegenerate);
        Assert.False(AnnotationItem.Create(5, AnnotationKind.Badge, [new PhysicalPoint(0, 0)], Style, badge: 1).IsDegenerate);
    }
}

public class AnnotationEditingTests
{
    private static AnnotationStyle Style => AnnotationStyle.Default;

    [Fact]
    public void Two_point_hit_test_resolves_corners_and_inside()
    {
        var item = AnnotationItem.FromDrag(1, AnnotationKind.Rectangle, new PhysicalPoint(10, 10), new PhysicalPoint(110, 60), Style);

        Assert.Equal(AnnotationHandle.TopLeft, AnnotationEditing.HandleAt(item, new PhysicalPoint(10, 10), 4));
        Assert.Equal(AnnotationHandle.BottomRight, AnnotationEditing.HandleAt(item, new PhysicalPoint(110, 60), 4));
        Assert.Equal(AnnotationHandle.Inside, AnnotationEditing.HandleAt(item, new PhysicalPoint(60, 30), 4));
        Assert.Equal(AnnotationHandle.None, AnnotationEditing.HandleAt(item, new PhysicalPoint(500, 500), 4));
    }

    [Fact]
    public void Line_hit_test_requires_being_near_the_segment()
    {
        var line = AnnotationItem.FromDrag(1, AnnotationKind.Line, new PhysicalPoint(0, 0), new PhysicalPoint(100, 0), Style);

        Assert.Equal(AnnotationHandle.Inside, AnnotationEditing.HandleAt(line, new PhysicalPoint(50, 2), 3));
        Assert.Equal(AnnotationHandle.None, AnnotationEditing.HandleAt(line, new PhysicalPoint(50, 40), 3));
        Assert.Equal(AnnotationHandle.TopLeft, AnnotationEditing.HandleAt(line, new PhysicalPoint(0, 0), 3));
    }

    [Fact]
    public void Stroke_hit_test_follows_the_polyline()
    {
        var stroke = AnnotationItem.Create(
            1,
            AnnotationKind.Pencil,
            [new PhysicalPoint(0, 0), new PhysicalPoint(50, 0), new PhysicalPoint(50, 50)],
            Style.WithWidth(3));

        Assert.Equal(AnnotationHandle.Inside, AnnotationEditing.HandleAt(stroke, new PhysicalPoint(25, 1), 3));
        Assert.Equal(AnnotationHandle.Inside, AnnotationEditing.HandleAt(stroke, new PhysicalPoint(49, 25), 3));
        Assert.Equal(AnnotationHandle.None, AnnotationEditing.HandleAt(stroke, new PhysicalPoint(10, 40), 3));
    }

    [Fact]
    public void HitTest_returns_the_topmost_item()
    {
        var bottom = AnnotationItem.FromDrag(1, AnnotationKind.Rectangle, new PhysicalPoint(0, 0), new PhysicalPoint(100, 100), Style);
        var top = AnnotationItem.FromDrag(2, AnnotationKind.Rectangle, new PhysicalPoint(20, 20), new PhysicalPoint(60, 60), Style);

        var hit = AnnotationEditing.HitTest([bottom, top], new PhysicalPoint(40, 40), 3);

        Assert.NotNull(hit);
        Assert.Equal(2, hit!.Item.Id);
    }

    [Fact]
    public void Move_shifts_every_point_by_the_same_delta()
    {
        var stroke = AnnotationItem.Create(1, AnnotationKind.Pencil, [new PhysicalPoint(5, 5), new PhysicalPoint(15, 25)], Style);

        var moved = AnnotationEditing.Move(stroke, 10, -5);

        Assert.Equal(new PhysicalPoint(15, 0), moved.Points[0]);
        Assert.Equal(new PhysicalPoint(25, 20), moved.Points[1]);
    }

    [Fact]
    public void Resize_of_a_two_point_item_moves_the_dragged_corner()
    {
        var item = AnnotationItem.FromDrag(1, AnnotationKind.Rectangle, new PhysicalPoint(10, 10), new PhysicalPoint(60, 60), Style);

        var resized = AnnotationEditing.Resize(item, AnnotationHandle.BottomRight, new PhysicalPoint(100, 30));

        Assert.Equal(new PhysicalRect(10, 10, 90, 20), resized.Bounds);
    }

    [Fact]
    public void Resize_of_a_stroke_scales_all_points_proportionally()
    {
        var stroke = AnnotationItem.Create(1, AnnotationKind.Pencil, [new PhysicalPoint(0, 0), new PhysicalPoint(10, 10)], Style.WithWidth(2));
        var bounds = stroke.Bounds;

        // 抓住右下角，把边界拉成两倍宽
        var resized = AnnotationEditing.Resize(stroke, AnnotationHandle.BottomRight, new PhysicalPoint(bounds.X + (bounds.Width * 2), bounds.Y + bounds.Height));

        Assert.True(resized.Bounds.Width > bounds.Width);
        Assert.True(resized.Points[1].X > stroke.Points[1].X);
        Assert.Equal(stroke.Points.Count, resized.Points.Count);
    }

    [Fact]
    public void Resize_with_no_handle_is_a_no_op()
    {
        var item = AnnotationItem.FromDrag(1, AnnotationKind.Rectangle, new PhysicalPoint(0, 0), new PhysicalPoint(10, 10), Style);

        Assert.Same(item, AnnotationEditing.Resize(item, AnnotationHandle.Inside, new PhysicalPoint(50, 50)));
        Assert.Same(item, AnnotationEditing.Resize(item, AnnotationHandle.None, new PhysicalPoint(50, 50)));
    }

    [Fact]
    public void Restyle_and_SetText_keep_identity_and_geometry()
    {
        var item = AnnotationItem.FromDrag(7, AnnotationKind.Rectangle, new PhysicalPoint(0, 0), new PhysicalPoint(10, 10), Style);

        var restyled = AnnotationEditing.Restyle(item, Style.WithStroke(new RgbColor(0x00, 0xFF, 0x00)).WithWidth(8));
        var texted = AnnotationEditing.SetText(item, "hello");

        Assert.Equal(7, restyled.Id);
        Assert.Equal(item.Bounds, restyled.Bounds);
        Assert.Equal(new RgbColor(0x00, 0xFF, 0x00), restyled.Style.Stroke);
        Assert.Equal(8, restyled.Style.StrokeWidth);
        Assert.Equal("hello", texted.Text);
        Assert.Equal(AnnotationKind.Rectangle, texted.Kind);
    }

    [Fact]
    public void Style_helpers_clamp_ranges()
    {
        Assert.Equal(1, Style.WithWidth(0).StrokeWidth);
        Assert.Equal(64, Style.WithWidth(999).StrokeWidth);
        Assert.Equal(0, Style.WithFillOpacity(-5).FillOpacity);
        Assert.Equal(100, Style.WithFillOpacity(500).FillOpacity);
    }
}

public class HtmlTextTests
{
    [Fact]
    public void ExtractPlainText_reads_the_clipboard_fragment()
    {
        const string html = """
        <html><body><!--StartFragment--><p>Hello <b>World</b></p><p>第二行</p><!--EndFragment--></body></html>
        """;

        string text = HtmlText.ExtractPlainText(html);

        Assert.Equal("Hello World\n第二行", text);
    }

    [Fact]
    public void ExtractPlainText_decodes_entities_including_numeric_ones()
    {
        string text = HtmlText.ExtractPlainText("<div>a&amp;b &lt;tag&gt; &#65; &#x1F600;</div>");

        Assert.Contains("a&b <tag> A 😀", text);
    }

    [Fact]
    public void ExtractPlainText_drops_scripts_styles_and_collapses_blank_line_runs()
    {
        string text = HtmlText.ExtractPlainText("<style>p{color:red}</style><script>alert(1)</script><p>x</p><p></p><p></p><p>y</p>");

        // 空段落被折叠成"一个空行"（保留段落间隔），而不是堆成一片空白
        Assert.Equal("x\n\ny", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractPlainText_returns_empty_for_blank_input(string? html)
    {
        Assert.Equal(string.Empty, HtmlText.ExtractPlainText(html));
    }

    [Fact]
    public void ExtractPlainText_keeps_plain_text_html_untouched()
    {
        Assert.Equal("just text", HtmlText.ExtractPlainText("just text"));
    }
}
