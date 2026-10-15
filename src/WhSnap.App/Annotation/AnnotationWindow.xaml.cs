using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WhSnap.Capture;
using WhSnap.Core;
using WhSnap.Interop;
using WhSnap.Services;

namespace WhSnap.Annotation;

/// <summary>标注工具（选择工具不是图元类型，单独列）。</summary>
internal enum AnnotationTool
{
    Select,
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Pencil,
    Marker,
    Text,
    Mosaic,
    Blur,
    Eraser,
    Badge,
    Highlight,
}

/// <summary>
/// 标注编辑器（规划 §2.1 E / §5.6）。
///
/// 设计要点：
///   * 窗口**恰好覆盖目标区域**、底图按物理像素 1:1 呈现（工具栏放在区域下方/上方的额外一条），
///     所以画到哪里就是哪里，不存在预览与实际导出不一致的问题；
///   * 实时预览走 <see cref="DrawingVisual"/>（矢量、零位图开销），每次"落笔完成"才重新合成一次位图，
///     这样拖动时的帧率与区域大小无关；
///   * 图元与撤销栈都在 <see cref="AnnotationDocument"/> 里（图元不可变 → 撤销是命令栈而非位图快照）；
///   * 确认后把合成结果交回上一步（截图流程 → 复制/保存/贴图；贴图流程 → 回写贴图内容）。
/// </summary>
internal partial class AnnotationWindow : Window
{
    private const double ToolbarHeightPx = 40;
    private const int HitTolerance = 6;

    private readonly BitmapSource _baseImage;
    private readonly AppSettings _settings;
    private readonly PhysicalRect _region;
    private readonly double _scale;
    private readonly AnnotationDocument _document = new();
    private readonly VisualHost _previewHost = new();
    private readonly Dictionary<Button, AnnotationTool> _toolButtons = [];
    private readonly List<Border> _swatches = [];

    private AnnotationTool _tool = AnnotationTool.Rectangle;
    private AnnotationStyle _style = AnnotationStyle.Default;
    private AnnotationItem? _draft;
    private AnnotationItem? _selected;
    private AnnotationHandle _activeHandle = AnnotationHandle.None;
    private PhysicalPoint _dragStart;
    private bool _dragging;
    private bool _resolved;
    private TextBox? _inlineText;
    private BitmapSource? _composed;
    private RgbColor _strokeColor = new(0xFF, 0x3B, 0x30);

    public AnnotationWindow(BitmapSource baseImage, PhysicalRect region, double scale, AppSettings settings, bool toolbarAbove = false)
    {
        InitializeComponent();

        _baseImage = baseImage;
        _region = region;
        _scale = scale <= 0 ? 1.0 : scale;
        _settings = settings;

        _document.Reset(settings.AnnotationBadgeStart);
        _style = BuildDefaultStyle(settings);
        _strokeColor = _style.Stroke;
        _tool = ParseTool(settings.AnnotationDefaultTool);

        // 窗口 = 区域 + 工具栏条；工具栏默认在下方，贴近屏幕底边时翻到上方
        double imageWidthDip = PhysicalToDip(region.Width);
        double imageHeightDip = PhysicalToDip(region.Height);
        double toolbarDip = PhysicalToDip((int)ToolbarHeightPx);

        double windowWidthPhysical = Math.Max(region.Width, 560);
        double windowHeightPhysical = region.Height + ToolbarHeightPx;
        double top = toolbarAbove ? region.Y - ToolbarHeightPx : region.Y;

        ImageRow.Height = new GridLength(imageHeightDip);
        ToolbarRow.Height = new GridLength(toolbarDip);

        CanvasArea.Width = imageWidthDip;
        CanvasArea.Height = imageHeightDip;

        _previewHost.Attach();
        _previewHost.Width = imageWidthDip;
        _previewHost.Height = imageHeightDip;
        _previewHost.IsHitTestVisible = false;
        Canvas.SetLeft(_previewHost, 0);
        Canvas.SetTop(_previewHost, 0);
        CanvasArea.Children.Add(_previewHost);

        Left = PhysicalToDip(region.X);
        Top = PhysicalToDip((int)top);
        Width = PhysicalToDip((int)windowWidthPhysical);
        Height = PhysicalToDip((int)windowHeightPhysical);

        ComposedImage.Width = imageWidthDip;
        ComposedImage.Height = imageHeightDip;

        ToolbarBorder.ToolTip = ToolbarHint;
        BuildToolbar();
        UpdateToolButtons();
        Recompose();

        SourceInitialized += (_, _) => ApplyPhysicalBounds((int)windowWidthPhysical, (int)windowHeightPhysical, region.X, (int)top);
        DpiChanged += (_, _) => ApplyPhysicalBounds((int)windowWidthPhysical, (int)windowHeightPhysical, region.X, (int)top);
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            Keyboard.Focus(this);
        };
    }

    /// <summary>标注完成（合成结果已经是物理像素 1:1）。<c>copyOnly</c> 来自工具栏的「复制」按钮。</summary>
    public event Action<BitmapSource, bool>? Completed;

    public event Action? Cancelled;

    /// <summary>工具与快捷键提示（挂到工具栏的 ToolTip 上，不占地方）。</summary>
    internal static string ToolbarHint =>
        "工具键位与截图/贴图窗口一致，全部可在设置里改：R 矩形 · O 椭圆 · L 直线 · A 箭头 · D 铅笔 · M 马克笔 · "
        + "T 文字 · G 马赛克 · B 模糊 · X 橡皮擦 · N 序号 · Y 高亮 · V 选择 · Ctrl+Z/Y 撤销重做 · [ ] 线宽 · 1-8 换色";

    // ---------------------------------------------------------------- 生命周期

    private void ApplyPhysicalBounds(int width, int height, int x, int y)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(handle, NativeMethods.HWND_TOPMOST, x, y, width, height, NativeMethods.SWP_SHOWWINDOW);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_resolved)
        {
            _resolved = true;
            Cancelled?.Invoke();
        }

        base.OnClosed(e);
    }

    // ---------------------------------------------------------------- 工具栏

    private static AnnotationStyle BuildDefaultStyle(AppSettings settings)
    {
        var style = AnnotationStyle.Default;

        if (ColorValue.TryParse(settings.AnnotationStrokeColor, out var stroke))
        {
            style = style.WithStroke(stroke);
        }

        return style with
        {
            StrokeWidth = Math.Clamp(settings.AnnotationStrokeWidth, 1, 64),
            FillOpacity = Math.Clamp(settings.AnnotationFillOpacity, 0, 100),
            FontSize = settings.AnnotationFontSize,
            MosaicBlock = settings.AnnotationMosaicBlock,
            BlurRadius = settings.AnnotationBlurRadius,
            EraserRadius = settings.AnnotationEraserRadius,
            FontFamily = settings.TextPinFontFamily,
        };
    }

    private void BuildToolbar()
    {
        AddTool(Toolbar, AnnotationTool.Select, "选择", "V");
        AddTool(Toolbar, AnnotationTool.Rectangle, "矩形", "R");
        AddTool(Toolbar, AnnotationTool.Ellipse, "椭圆", "O");
        AddTool(Toolbar, AnnotationTool.Line, "直线", "L");
        AddTool(Toolbar, AnnotationTool.Arrow, "箭头", "A");
        AddTool(Toolbar, AnnotationTool.Pencil, "铅笔", "D");
        AddTool(Toolbar, AnnotationTool.Marker, "马克笔", "M");
        AddTool(Toolbar, AnnotationTool.Text, "文字", "T");
        AddTool(Toolbar, AnnotationTool.Mosaic, "马赛克", "G");
        AddTool(Toolbar, AnnotationTool.Blur, "模糊", "B");
        AddTool(Toolbar, AnnotationTool.Eraser, "橡皮擦", "X");
        AddTool(Toolbar, AnnotationTool.Badge, "序号", "N");
        AddTool(Toolbar, AnnotationTool.Highlight, "高亮", "Y");

        AddSeparator(Toolbar);

        foreach (var color in ParsePalette(_settings.AnnotationPalette))
        {
            var swatch = new Border
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(2, 0, 2, 0),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
                Cursor = Cursors.Hand,
                ToolTip = color.ToHex(),
            };

            swatch.MouseLeftButtonDown += (_, _) => SetStrokeColor(color);
            _swatches.Add(swatch);
            Toolbar.Children.Add(swatch);
        }

        AddSeparator(Toolbar);

        var thinner = MakeButton("−线宽", () => AdjustWidth(-1));
        thinner.ToolTip = "变细（[）";
        var thicker = MakeButton("＋线宽", () => AdjustWidth(1));
        thicker.ToolTip = "变粗（]）";
        Toolbar.Children.Add(thinner);
        Toolbar.Children.Add(thicker);

        AddSeparator(Toolbar);

        AddButton("撤销", () => Undo(), "Ctrl+Z");
        AddButton("重做", () => Redo(), "Ctrl+Y");
        AddButton("清空", () => ClearAll(), "清空全部标注");
        AddButton("复制", () => Confirm(true), "确认并只复制");

        AddSeparator(Toolbar);

        var confirm = new Button
        {
            Content = "确认 (Enter)",
            Padding = new Thickness(12, 3, 12, 3),
            Margin = new Thickness(4, 0, 4, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x9C, 0xFF)),
        };

        confirm.Click += (_, _) => Confirm(false);
        Toolbar.Children.Add(confirm);

        var cancel = MakeCancelButton();
        Toolbar.Children.Add(cancel);
    }

    private Button MakeCancelButton()
    {
        var cancel = new Button
        {
            Content = "取消 (Esc)",
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0),
        };

        cancel.Click += (_, _) => Cancel();
        return cancel;
    }

    private void AddTool(Panel host, AnnotationTool tool, string label, string key)
    {
        var button = new Button
        {
            Content = $"{label} {key}",
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 0, 3, 0),
            ToolTip = $"{label}（快捷键 {key}）",
        };

        button.Click += (_, _) => SetTool(tool);
        _toolButtons[button] = tool;
        host.Children.Add(button);
    }

    private static void AddSeparator(Panel host)
    {
        host.Children.Add(new Rectangle
        {
            Width = 1,
            Margin = new Thickness(5, 2, 5, 2),
            Fill = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
        });
    }

    private void AddButton(string label, Action action, string tooltip)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(3, 0, 0, 0),
            ToolTip = tooltip,
        };

        button.Click += (_, _) => action();
        Toolbar.Children.Add(button);
    }

    private static Button MakeButton(string label, Action action)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(3, 0, 0, 0),
        };

        button.Click += (_, _) => action();
        return button;
    }

    private void UpdateToolButtons()
    {
        foreach (var (button, tool) in _toolButtons)
        {
            bool active = tool == _tool;
            button.Background = active
                ? new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB))
                : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            button.Foreground = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        }

        foreach (var swatch in _swatches)
        {
            if (swatch.Background is SolidColorBrush brush)
            {
                var color = brush.Color;
                bool active = color.R == _strokeColor.R && color.G == _strokeColor.G && color.B == _strokeColor.B;
                swatch.BorderBrush = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
                swatch.BorderThickness = new Thickness(active ? 2 : 1);
            }
        }

        Cursor = _tool == AnnotationTool.Select ? Cursors.SizeAll : Cursors.Cross;
    }

    private void SetTool(AnnotationTool tool)
    {
        _tool = tool;
        _selected = null;
        UpdateToolButtons();
        RenderPreview();
    }

    private void SetStrokeColor(RgbColor color)
    {
        _strokeColor = color;
        _style = _style.WithStroke(color);

        // 已经选中的图元顺手改色（二次编辑的一部分）
        if (_selected is not null)
        {
            _document.Update(AnnotationEditing.Restyle(_selected, _style));
            _selected = _document.Find(_selected.Id);
            Recompose();
        }

        UpdateToolButtons();
    }

    private void AdjustWidth(int delta)
    {
        _style = _style.WithWidth(_style.StrokeWidth + delta);

        if (_selected is not null)
        {
            _document.Update(AnnotationEditing.Restyle(_selected, _style));
            _selected = _document.Find(_selected.Id);
            Recompose();
        }

        RenderPreview();
    }

    private static IReadOnlyList<RgbColor> ParsePalette(string? text)
    {
        var colors = new List<RgbColor>();

        foreach (string token in (text ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ColorValue.TryParse(token, out var color) && !colors.Contains(color))
            {
                colors.Add(color);
            }
        }

        if (colors.Count == 0)
        {
            colors.AddRange([
                new RgbColor(0xFF, 0x3B, 0x30),
                new RgbColor(0xFF, 0xCC, 0x00),
                new RgbColor(0x34, 0xC7, 0x59),
                new RgbColor(0x00, 0x7A, 0xFF),
            ]);
        }

        return colors;
    }

    // ---------------------------------------------------------------- 合成与预览

    /// <summary>重新合成（落笔完成、撤销重做、改样式时调用）。</summary>
    private void Recompose()
    {
        try
        {
            _composed = AnnotationRenderer.Compose(_baseImage, _document.Items);
            ComposedImage.Source = _composed;
        }
        catch (Exception ex)
        {
            Log.Error("标注合成失败", ex);
        }

        RenderPreview();
    }

    /// <summary>实时预览层：草稿图元 + 选中框与手柄（矢量绘制，拖动时零位图开销）。</summary>
    private void RenderPreview()
    {
        using DrawingContext dc = _previewHost.Visual.RenderOpen();
        dc.PushTransform(new ScaleTransform(1 / _scale, 1 / _scale));

        // 像素效果的草稿先只画虚线框（真实效果在落笔后合成）
        if (_draft is not null)
        {
            if (AnnotationItem.IsPixelEffect(_draft.Kind))
            {
                var pen = new Pen(Brushes.DeepSkyBlue, 1.5) { DashStyle = new DashStyle([4, 3], 0) };
                pen.Freeze();
                var bounds = _draft.Bounds;
                dc.DrawRectangle(null, pen, new Rect(bounds.X, bounds.Y, Math.Max(bounds.Width, 1), Math.Max(bounds.Height, 1)));
            }
            else
            {
                AnnotationDrawing.DrawOne(dc, _draft);
            }
        }

        if (_selected is not null)
        {
            var pen = new Pen(Brushes.DeepSkyBlue, 1) { DashStyle = new DashStyle([3, 3], 0) };
            pen.Freeze();
            var bounds = _selected.Bounds;
            dc.DrawRectangle(null, pen, new Rect(bounds.X - 2, bounds.Y - 2, bounds.Width + 4, bounds.Height + 4));

            foreach (var point in HandlePoints(bounds))
            {
                dc.DrawRectangle(Brushes.White, pen, new Rect(point.X - 3, point.Y - 3, 6, 6));
            }
        }

        dc.Pop();
    }

    private static IEnumerable<PhysicalPoint> HandlePoints(PhysicalRect bounds)
    {
        yield return new PhysicalPoint(bounds.X, bounds.Y);
        yield return new PhysicalPoint(bounds.Right, bounds.Y);
        yield return new PhysicalPoint(bounds.X, bounds.Bottom);
        yield return new PhysicalPoint(bounds.Right, bounds.Bottom);
    }

    // ---------------------------------------------------------------- 鼠标交互

    private PhysicalPoint ToPhysical(System.Windows.Point dip) => new(
        (int)Math.Round(dip.X * _scale, MidpointRounding.AwayFromZero),
        (int)Math.Round(dip.Y * _scale, MidpointRounding.AwayFromZero));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (e.ClickCount >= 2)
        {
            StartTextEdit(ToPhysical(e.GetPosition(CanvasArea)), editSelected: true);
            return;
        }

        var point = ToPhysical(e.GetPosition(CanvasArea));
        _dragStart = point;
        _dragging = true;
        CaptureMouse();

        if (_tool == AnnotationTool.Select)
        {
            BeginSelectDrag(point);
            return;
        }

        switch (_tool)
        {
            case AnnotationTool.Badge:
                _document.Add(AnnotationKind.Badge, [point], _style);
                EndStrokeAndRecompose();
                return;

            case AnnotationTool.Text:
                StartTextEdit(point, editSelected: false);
                _dragging = false;
                ReleaseMouseCapture();
                return;
        }

        var kind = ToKind(_tool);
        _draft = AnnotationItem.IsStroke(kind)
            ? AnnotationItem.Create(0, kind, [point], _style)
            : AnnotationItem.FromDrag(0, kind, point, point, _style);

        RenderPreview();
    }

    private void BeginSelectDrag(PhysicalPoint point)
    {
        var hit = AnnotationEditing.HitTest(_document.Items, point, HitTolerance);

        if (hit is null)
        {
            _selected = null;
            _dragging = false;
            ReleaseMouseCapture();
            RenderPreview();
            return;
        }

        _selected = hit.Item;
        _activeHandle = hit.Handle;

        // 线条/箭头/自由笔的"端点"手柄也用角来表示
        if (_activeHandle == AnnotationHandle.Inside && _tool != AnnotationTool.Select)
        {
            _activeHandle = AnnotationHandle.Inside;
        }

        RenderPreview();
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_dragging || _draft is null && _selected is null)
        {
            return;
        }

        var point = ToPhysical(e.GetPosition(CanvasArea));

        if (_draft is not null)
        {
            if (AnnotationItem.IsStroke(_draft.Kind))
            {
                // 折线抽稀：移动超过 2 物理像素才记一个点
                if (_draft.Points.Count == 0 || Distance(_draft.Points[^1], point) >= 2)
                {
                    _draft = _draft with { Points = [.. _draft.Points, point] };
                }
            }
            else
            {
                _draft = _draft with { Points = [_dragStart, point] };
            }

            RenderPreview();
            return;
        }

        if (_selected is not null)
        {
            int dx = point.X - _dragStart.X;
            int dy = point.Y - _dragStart.Y;

            _draft = _activeHandle == AnnotationHandle.Inside
                ? AnnotationEditing.Move(_selected, dx, dy)
                : AnnotationEditing.Resize(_selected, _activeHandle, point);

            RenderPreview();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();

        if (_draft is null)
        {
            return;
        }

        if (_selected is not null && _activeHandle != AnnotationHandle.None)
        {
            _document.Update(_draft);
            _selected = _document.Find(_draft.Id);
        }
        else if (_draft.IsDegenerate)
        {
            // 误点/手抖：不保留图元，也不污染撤销栈
            _draft = null;
            RenderPreview();
            return;
        }
        else
        {
            var committed = _document.Add(_draft.Kind, _draft.Points, _draft.Style, _draft.Text);
            _selected = _tool == AnnotationTool.Select ? null : committed;
        }

        _draft = null;
        _activeHandle = AnnotationHandle.None;
        Recompose();
    }

    private void EndStrokeAndRecompose()
    {
        _dragging = false;
        ReleaseMouseCapture();
        _draft = null;
        Recompose();
    }

    private static double Distance(PhysicalPoint a, PhysicalPoint b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    // ---------------------------------------------------------------- 文字输入

    private void StartTextEdit(PhysicalPoint point, bool editSelected)
    {
        if (editSelected && (_selected is null || _selected.Kind != AnnotationKind.Text))
        {
            return;
        }

        CommitInlineText();

        var target = editSelected ? _selected! : null;
        var origin = target?.Start ?? point;

        _inlineText = new TextBox
        {
            MinWidth = 60,
            FontSize = Math.Max(_style.FontSize / _scale, 10),
            FontFamily = new FontFamily(_style.FontFamily),
            Foreground = new SolidColorBrush(Color.FromRgb(_strokeColor.R, _strokeColor.G, _strokeColor.B)),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x10, 0x10)),
            CaretBrush = Brushes.White,
            BorderBrush = Brushes.DeepSkyBlue,
            BorderThickness = new Thickness(1),
            Text = target?.Text ?? string.Empty,
            AcceptsReturn = false,
        };

        Canvas.SetLeft(_inlineText, PhysicalToDip(origin.X));
        Canvas.SetTop(_inlineText, PhysicalToDip(origin.Y));
        CanvasArea.Children.Add(_inlineText);

        _inlineText.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                args.Handled = true;
                CommitInlineText();
            }
            else if (args.Key == Key.Escape)
            {
                args.Handled = true;
                CancelInlineText();
            }
        };

        _inlineText.Focus();
        Keyboard.Focus(_inlineText);
    }

    private void CommitInlineText()
    {
        if (_inlineText is null)
        {
            return;
        }

        string text = _inlineText.Text.Trim();
        var origin = ToPhysical(new System.Windows.Point(Canvas.GetLeft(_inlineText), Canvas.GetTop(_inlineText)));

        RemoveInlineText();

        if (text.Length == 0)
        {
            RenderPreview();
            return;
        }

        if (_selected is { Kind: AnnotationKind.Text } existing)
        {
            _document.Update(AnnotationEditing.SetText(existing, text));
        }
        else
        {
            _document.Add(AnnotationKind.Text, [origin], _style, text);
        }

        _selected = null;
        Recompose();
    }

    private void CancelInlineText()
    {
        RemoveInlineText();
        RenderPreview();
    }

    private void RemoveInlineText()
    {
        if (_inlineText is null)
        {
            return;
        }

        CanvasArea.Children.Remove(_inlineText);
        _inlineText = null;
        Focus();
    }

    // ---------------------------------------------------------------- 键盘

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // 就地编辑文字时，键盘归输入框
        if (_inlineText is not null && _inlineText.IsKeyboardFocusWithin)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        var bindings = HotkeyBindings.Resolve(_settings.Hotkeys, _settings.DisabledHotkeys, HotkeyScope.Annotation);
        var spec = KeySpecFactory.FromWpf(e.Key, Keyboard.Modifiers);
        var action = HotkeyBindings.Match(bindings, spec, HotkeyScope.Annotation);

        if (action is not null && HandleAction(action.Id))
        {
            e.Handled = true;
            return;
        }

        // 固定交互：线宽与调色板
        switch (e.Key)
        {
            case Key.OemOpenBrackets:
                AdjustWidth(-1);
                e.Handled = true;
                return;

            case Key.OemCloseBrackets:
                AdjustWidth(1);
                e.Handled = true;
                return;
        }

        if (e.Key is >= Key.D1 and <= Key.D8)
        {
            int index = e.Key - Key.D1;
            var palette = ParsePalette(_settings.AnnotationPalette);

            if (index < palette.Count)
            {
                SetStrokeColor(palette[index]);
                e.Handled = true;
                return;
            }
        }

        base.OnPreviewKeyDown(e);
    }

    private bool HandleAction(string actionId)
    {
        switch (actionId)
        {
            case "annotation.confirm":
                Confirm(false);
                return true;

            case "annotation.cancel":
                Cancel();
                return true;

            case "annotation.undo":
                Undo();
                return true;

            case "annotation.redo":
                Redo();
                return true;

            case "annotation.delete":
                DeleteSelected();
                return true;
        }

        if (actionId.StartsWith("annotation.tool.", StringComparison.Ordinal))
        {
            SetTool(ParseTool(actionId["annotation.tool.".Length..]));
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- 编辑命令

    public void Undo()
    {
        if (_document.Undo())
        {
            _selected = null;
            Recompose();
        }
    }

    public void Redo()
    {
        if (_document.Redo())
        {
            _selected = null;
            Recompose();
        }
    }

    private void DeleteSelected()
    {
        if (_selected is null)
        {
            return;
        }

        _document.Remove(_selected.Id);
        _selected = null;
        Recompose();
    }

    private void ClearAll()
    {
        if (_document.Clear())
        {
            _selected = null;
            Recompose();
        }
    }

    // ---------------------------------------------------------------- 完成

    /// <summary>确认：合成结果交回调用方（<paramref name="copyOnly"/> 表示工具栏「复制」按钮）。</summary>
    public void Confirm(bool copyOnly)
    {
        if (_resolved)
        {
            return;
        }

        CommitInlineText();

        if (_composed is null)
        {
            Recompose();
        }

        if (_composed is null)
        {
            return;
        }

        _resolved = true;
        Completed?.Invoke(_composed, copyOnly);
        Close();
    }

    public void Cancel()
    {
        if (_resolved)
        {
            return;
        }

        _resolved = true;
        Cancelled?.Invoke();
        Close();
    }

    // ---------------------------------------------------------------- 自检支持

    /// <summary>当前文档（自检读图元数量与撤销栈深度）。</summary>
    internal AnnotationDocument Document => _document;

    internal AnnotationTool CurrentTool => _tool;

    /// <summary>按工具名切换（自检与快捷键共用）。</summary>
    internal static AnnotationTool ParseTool(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "select" => AnnotationTool.Select,
        "ellipse" => AnnotationTool.Ellipse,
        "line" => AnnotationTool.Line,
        "arrow" => AnnotationTool.Arrow,
        "pencil" => AnnotationTool.Pencil,
        "marker" => AnnotationTool.Marker,
        "text" => AnnotationTool.Text,
        "mosaic" => AnnotationTool.Mosaic,
        "blur" => AnnotationTool.Blur,
        "eraser" => AnnotationTool.Eraser,
        "badge" => AnnotationTool.Badge,
        "highlight" => AnnotationTool.Highlight,
        _ => AnnotationTool.Rectangle,
    };

    internal static AnnotationKind ToKind(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Ellipse => AnnotationKind.Ellipse,
        AnnotationTool.Line => AnnotationKind.Line,
        AnnotationTool.Arrow => AnnotationKind.Arrow,
        AnnotationTool.Pencil => AnnotationKind.Pencil,
        AnnotationTool.Marker => AnnotationKind.Marker,
        AnnotationTool.Text => AnnotationKind.Text,
        AnnotationTool.Mosaic => AnnotationKind.Mosaic,
        AnnotationTool.Blur => AnnotationKind.Blur,
        AnnotationTool.Eraser => AnnotationKind.Eraser,
        AnnotationTool.Badge => AnnotationKind.Badge,
        AnnotationTool.Highlight => AnnotationKind.Highlight,
        _ => AnnotationKind.Rectangle,
    };

    /// <summary>模拟一次拖拽（自检用：绕开真实鼠标事件，走同一套落笔逻辑）。</summary>
    internal void SimulateDrag(PhysicalPoint from, PhysicalPoint to)
    {
        _dragStart = from;
        _dragging = true;

        var kind = ToKind(_tool);

        if (kind == AnnotationKind.Badge)
        {
            _document.Add(AnnotationKind.Badge, [from], _style);
            EndStrokeAndRecompose();
            return;
        }

        _draft = AnnotationItem.IsStroke(kind)
            ? AnnotationItem.Create(0, kind, [from, to], _style)
            : AnnotationItem.FromDrag(0, kind, from, to, _style);

        _dragging = false;

        if (_draft.IsDegenerate)
        {
            _draft = null;
            return;
        }

        _document.Add(_draft.Kind, _draft.Points, _draft.Style, _draft.Text);
        _draft = null;
        Recompose();
    }

    /// <summary>模拟一次点击（自检用：序号/文字这类单点图元）。</summary>
    internal void SimulateClick(PhysicalPoint point, string text = "")
    {
        var kind = ToKind(_tool);

        if (kind == AnnotationKind.Text)
        {
            _document.Add(AnnotationKind.Text, [point], _style, text.Length > 0 ? text : "标注");
        }
        else
        {
            _document.Add(kind, [point], _style);
        }

        Recompose();
    }

    /// <summary>模拟"选中某个图元并整体拖动"（自检用）。</summary>
    internal void SimulateSelectMove(int itemId, PhysicalPoint delta)
    {
        var item = _document.Find(itemId);
        if (item is null)
        {
            return;
        }

        _selected = item;
        _document.Update(AnnotationEditing.Move(item, delta.X, delta.Y));
        _selected = _document.Find(itemId);
        Recompose();
    }

    internal void SetToolForTest(AnnotationTool tool) => SetTool(tool);

    internal void ClearAllForTest() => ClearAll();

    /// <summary>当前合成结果（自检比对像素用）。</summary>
    internal BitmapSource? ComposedImageSource => _composed;

    internal AnnotationStyle CurrentStyle => _style;

    /// <summary>取当前生效的设置快照（自检读默认值）。</summary>
    internal AppSettings SettingsSnapshot => _settings;

    private double PhysicalToDip(int physical) => physical / _scale;
}
