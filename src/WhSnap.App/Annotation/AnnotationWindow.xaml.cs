using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WhSnap.Capture;
using WhSnap.Core;
using WhSnap.Interop;
using WhSnap.Services;
using WhSnap.Theme;

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
/// 标注编辑器（规划 §2.1 E / §5.6；界面按 design/focal/surfaces/annotate.html 落地）。
///
/// 结构（annotate.html）：标题栏 → [ 左工具 rail | 画布 stage ] → 样式条 → 动作条。
/// 设计要点：
///   * 画布里的底图与标注**永远按物理像素 1:1** 呈现（窗口用 DIP 布局，物理换算在
///     <see cref="ApplyPhysicalBounds"/> 里做）；画布周围是设计稿的 stage（棋盘格留白），
///     窗口比区域宽时留白补在两侧，底图不会被拉伸；
///   * 实时预览走 <see cref="DrawingVisual"/>（矢量、零位图开销），每次"落笔完成"才重新合成一次位图；
///   * 图元与撤销栈都在 <see cref="AnnotationDocument"/> 里（图元不可变 → 撤销是命令栈而非位图快照）；
///   * 界面层的颜色（选中框 / 手柄 / 中心像素标记 / 输入框）全部走 <see cref="FocalPalette"/> 的
///     量测色与表面令牌，不出现字面量色值；
///   * 确认后把合成结果交回上一步（截图流程 → 复制/保存/贴图；贴图流程 → 回写贴图内容）。
/// </summary>
internal partial class AnnotationWindow : Window
{
    private const int HitTolerance = 6;

    /// <summary>13 个工具的唯一来源：rail 的顺序、图标、中文名、快捷键字母与分组位置。</summary>
    private static readonly (AnnotationTool Tool, string Icon, string Name, string Key, bool NewGroup)[] ToolCatalog =
    [
        (AnnotationTool.Select, "select", "选择", "V", false),
        (AnnotationTool.Rectangle, "rect", "矩形", "R", true),
        (AnnotationTool.Ellipse, "ellipse", "椭圆", "O", false),
        (AnnotationTool.Line, "line", "直线", "L", false),
        (AnnotationTool.Arrow, "arrow", "箭头", "A", false),
        (AnnotationTool.Pencil, "pencil", "铅笔", "D", true),
        (AnnotationTool.Marker, "marker", "马克笔", "M", false),
        (AnnotationTool.Highlight, "highlight", "高亮", "Y", false),
        (AnnotationTool.Mosaic, "mosaic", "马赛克", "G", true),
        (AnnotationTool.Blur, "blur", "模糊", "B", false),
        (AnnotationTool.Eraser, "eraser", "橡皮擦", "X", false),
        (AnnotationTool.Text, "text", "文字", "T", true),
        (AnnotationTool.Badge, "badge", "序号", "N", false),
    ];

    private readonly BitmapSource _baseImage;
    private readonly AppSettings _settings;
    private readonly PhysicalRect _region;
    private readonly double _scale;
    private readonly AnnotationDocument _document = new();
    private readonly VisualHost _previewHost = new();
    private readonly List<(ToggleButton Button, AnnotationTool Tool, FocalIcon Icon, TextBlock Key)> _toolRailItems = [];
    private readonly List<ToggleButton> _swatches = [];

    private AnnotationTool _tool = AnnotationTool.Rectangle;
    private AnnotationStyle _style = AnnotationStyle.Default;
    private AnnotationItem? _draft;
    private AnnotationItem? _selected;
    private AnnotationHandle _activeHandle = AnnotationHandle.None;
    private PhysicalPoint _dragStart;
    private bool _dragging;
    private bool _resolved;
    private bool _ready;
    private bool _suppressSync;
    private int _restyleCoalesceId;
    private TextBox? _inlineText;
    private BitmapSource? _composed;
    private RgbColor _strokeColor;

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

        double imageWidthDip = PhysicalToDip(region.Width);
        double imageHeightDip = PhysicalToDip(region.Height);

        CanvasArea.Width = imageWidthDip;
        CanvasArea.Height = imageHeightDip;

        ComposedImage.Width = imageWidthDip;
        ComposedImage.Height = imageHeightDip;

        _previewHost.Attach();
        _previewHost.Width = imageWidthDip;
        _previewHost.Height = imageHeightDip;
        _previewHost.IsHitTestVisible = false;
        Canvas.SetLeft(_previewHost, 0);
        Canvas.SetTop(_previewHost, 0);
        CanvasArea.Children.Add(_previewHost);

        // 贴近屏幕底边时把动作条/样式条翻到画布上方（行序跟着翻，画布始终占剩下的整行）
        if (toolbarAbove)
        {
            Grid.SetRow(BodyHost, 3);
            Grid.SetRow(StyleBar, 1);
            Grid.SetRow(ActionBar, 2);
        }

        BuildToolRail();
        BuildSwatches();

        double railDip = (double)FindResource("An.RailWidth");
        double styleDip = (double)FindResource("An.StyleBarHeight");
        double titleDip = FocalPalette.TitleBarHeight;
        double actionDip = FocalPalette.ToolbarHeight;

        // 行高与窗口高度取自同一组令牌，保证"画布正好是底图 1:1 的那一块"
        TitleRow.Height = new GridLength(titleDip);
        StyleRow.Height = new GridLength(styleDip);
        ToolbarRow.Height = new GridLength(actionDip);

        // 动作条是唯一不能滚动、也不能被裁掉的一行（它手里是"完成"）：窗口宽度以它的自然宽度为下限；
        // 画布不够宽/不够高时补出来的是 stage 留白（13 个工具要 470px 高，短区域不该让 rail 变成滚动条），
        // 底图仍然是 1:1。
        ActionBar.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        ToolRailItems.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double bodyWidthDip = Math.Max(imageWidthDip, ActionBar.DesiredSize.Width);
        double bodyHeightDip = Math.Max(imageHeightDip, ToolRailItems.DesiredSize.Height);

        int railPhysical = (int)Math.Round(railDip * _scale, MidpointRounding.AwayFromZero);
        int bodyWidthPhysical = (int)Math.Round(bodyWidthDip * _scale, MidpointRounding.AwayFromZero);
        int bodyHeightPhysical = (int)Math.Round(bodyHeightDip * _scale, MidpointRounding.AwayFromZero);
        int chromePhysical = (int)Math.Round((titleDip + styleDip + actionDip) * _scale, MidpointRounding.AwayFromZero);
        int windowWidthPhysical = railPhysical + bodyWidthPhysical;
        int windowHeightPhysical = bodyHeightPhysical + chromePhysical;

        // 画布在 stage 里居中，所以窗口位置按"留白的一半"回退，底图仍然落在区域原来的位置
        int slackX = Math.Max(bodyWidthPhysical - region.Width, 0);
        int slackY = Math.Max(bodyHeightPhysical - region.Height, 0);
        int windowX = region.X - railPhysical - (slackX / 2);
        int windowY = toolbarAbove
            ? region.Y - chromePhysical - (slackY / 2)
            : region.Y - (int)Math.Round(titleDip * _scale, MidpointRounding.AwayFromZero) - (slackY / 2);

        (windowX, windowY) = ClampToMonitor(windowX, windowY, windowWidthPhysical, windowHeightPhysical);

        Left = windowX / _scale;
        Top = windowY / _scale;
        Width = windowWidthPhysical / _scale;
        Height = windowHeightPhysical / _scale;

        SizeReadout.Text = $"{region.Width} × {region.Height} · 1:1";
        ActionBar.ToolTip = ToolbarHint;

        SyncUi();
        Recompose();

        _ready = true;

        SourceInitialized += (_, _) => ApplyPhysicalBounds(windowWidthPhysical, windowHeightPhysical, windowX, windowY);
        DpiChanged += (_, _) => ApplyPhysicalBounds(windowWidthPhysical, windowHeightPhysical, windowX, windowY);
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            Keyboard.Focus(this);
        };
    }

    /// <summary>标注完成（合成结果已经是物理像素 1:1）。<c>copyOnly</c> 来自动作条的「复制」按钮。</summary>
    public event Action<BitmapSource, bool>? Completed;

    public event Action? Cancelled;

    /// <summary>工具与快捷键提示（挂到动作条的 ToolTip 上，不占地方）。</summary>
    internal static string ToolbarHint =>
        "工具键位与截图/贴图窗口一致，全部可在设置里改：V 选择 · R 矩形 · O 椭圆 · L 直线 · A 箭头 · D 铅笔 · "
        + "M 马克笔 · Y 高亮 · G 马赛克 · B 模糊 · X 橡皮擦 · T 文字 · N 序号 · Ctrl+Z/Y 撤销重做 · [ ] 线宽 · 1-8 换色";

    // ---------------------------------------------------------------- 生命周期

    /// <summary>把窗口摆到区域旁边：画布与区域对齐，摆不下时收进显示器范围内。</summary>
    private (int X, int Y) ClampToMonitor(int x, int y, int width, int height)
    {
        try
        {
            var monitors = MonitorEnumerator.Enumerate();
            var monitor = MonitorEnumerator.GetMonitorAtPoint(monitors, _region.X + (_region.Width / 2), _region.Y + (_region.Height / 2))
                          ?? monitors.FirstOrDefault();

            if (monitor is not null)
            {
                var bounds = monitor.Bounds;
                x = (int)Math.Clamp(x, bounds.X, Math.Max(bounds.X, bounds.Right - width));
                y = (int)Math.Clamp(y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - height));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"标注窗口定位失败，保留默认位置：{ex.Message}");
        }

        return (x, y);
    }

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

    // ---------------------------------------------------------------- 机壳（标题栏 / 工具 rail / 样式条 / 动作条）

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

    /// <summary>左侧 13 个工具：图标 + 中文名 + 快捷键字母（顺序与分组照设计稿）。</summary>
    private void BuildToolRail()
    {
        var itemStyle = (Style)FindResource("An.RailItem");
        var labelStyle = (Style)FindResource("An.RailLabel");
        var iconStyle = (Style)FindResource("An.RailIcon");
        var keyStyle = (Style)FindResource("An.RailKey");

        foreach (var entry in ToolCatalog)
        {
            if (entry.NewGroup && ToolRailItems.Children.Count > 0)
            {
                ToolRailItems.Children.Add(new Rectangle
                {
                    Height = 1,
                    Fill = FocalPalette.HairlineDark,
                    Margin = new Thickness(8, 6, 8, 6),
                    SnapsToDevicePixels = true,
                });
            }

            var icon = new FocalIcon { Icon = entry.Icon, Style = iconStyle };
            var label = new TextBlock { Text = entry.Name, Style = labelStyle };
            var key = new TextBlock { Text = entry.Key, Style = keyStyle };

            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(icon, 0);
            Grid.SetColumn(label, 1);
            Grid.SetColumn(key, 2);
            content.Children.Add(icon);
            content.Children.Add(label);
            content.Children.Add(key);

            var button = new ToggleButton
            {
                Style = itemStyle,
                Content = content,
                ToolTip = $"{entry.Name}（{entry.Key}）",
            };

            AutomationProperties.SetName(button, $"{entry.Name}工具，快捷键 {entry.Key}");
            button.Click += (_, _) => SetTool(entry.Tool);

            _toolRailItems.Add((button, entry.Tool, icon, key));
            ToolRailItems.Children.Add(button);
        }
    }

    /// <summary>描边色板：色块 = 用户颜色（内容层的例外），选中用琥珀外环。</summary>
    private void BuildSwatches()
    {
        foreach (var color in ParsePalette(_settings.AnnotationPalette))
        {
            var swatch = new ToggleButton
            {
                Style = FocalPalette.Style("Focal.Swatch"),
                Background = FocalPalette.FromRgb(color),
                Tag = color,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = color.ToHex(),
            };

            AutomationProperties.SetName(swatch, $"描边颜色 {color.ToHex()}");
            swatch.Click += (_, _) => SetStrokeColor(color);

            _swatches.Add(swatch);
            SwatchHost.Children.Add(swatch);
        }
    }

    /// <summary>把窗口状态推回机壳：选中工具、选中色、参数格可见性、滑杆读数、撤销/重做可用性。</summary>
    private void SyncUi()
    {
        foreach (var (button, tool, icon, key) in _toolRailItems)
        {
            bool active = tool == _tool;
            button.IsChecked = active;

            // 选中 = accent-tint 底 + 琥珀图标（Focal.RailItem 给底，图标/快捷键字母在这里给色）
            icon.SetValue(TextElement.ForegroundProperty, active ? FocalPalette.Accent : FocalPalette.OnDarkFaint);
            key.Foreground = active ? FocalPalette.Accent : FocalPalette.OnDarkFaint;
        }

        foreach (var swatch in _swatches)
        {
            swatch.IsChecked = swatch.Tag is RgbColor color && SameColor(color, _strokeColor);
        }

        UpdateStyleGroups();
        UpdateStyleControls();
        UpdateHistoryButtons();

        Cursor = _tool == AnnotationTool.Select ? Cursors.SizeAll : Cursors.Cross;
    }

    /// <summary>只显示与"当前工具"（或当前选中的图元）有关的参数格；末格不留右侧发丝线。</summary>
    private void UpdateStyleGroups()
    {
        var selected = _selected?.Kind;
        bool shapeTool = _tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse
                         || selected is AnnotationKind.Rectangle or AnnotationKind.Ellipse;
        bool mosaicTool = _tool == AnnotationTool.Mosaic || selected == AnnotationKind.Mosaic;
        bool strokeTool = _tool is not (AnnotationTool.Mosaic or AnnotationTool.Blur or AnnotationTool.Eraser);
        bool opacityTool = shapeTool
                           || _tool is AnnotationTool.Marker or AnnotationTool.Highlight
                           || selected is AnnotationKind.Marker or AnnotationKind.Highlight;

        GroupStroke.Visibility = strokeTool ? Visibility.Visible : Visibility.Collapsed;
        GroupWidth.Visibility = strokeTool ? Visibility.Visible : Visibility.Collapsed;
        GroupFill.Visibility = shapeTool ? Visibility.Visible : Visibility.Collapsed;
        GroupOpacity.Visibility = opacityTool ? Visibility.Visible : Visibility.Collapsed;
        GroupMosaic.Visibility = mosaicTool ? Visibility.Visible : Visibility.Collapsed;

        Border[] groups = [GroupStroke, GroupFill, GroupWidth, GroupOpacity, GroupMosaic];
        Border? last = groups.LastOrDefault(group => group.Visibility == Visibility.Visible);

        foreach (var group in groups)
        {
            group.BorderThickness = group == last ? new Thickness(0) : new Thickness(0, 0, 1, 0);
        }
    }

    private void UpdateStyleControls()
    {
        _suppressSync = true;

        try
        {
            WidthSlider.Value = Math.Clamp(_style.StrokeWidth, (int)WidthSlider.Minimum, (int)WidthSlider.Maximum);
            WidthReadout.Text = $"{_style.StrokeWidth} px";

            int opacity = CurrentOpacity;
            OpacitySlider.Value = Math.Clamp(opacity, (int)OpacitySlider.Minimum, (int)OpacitySlider.Maximum);
            OpacityReadout.Text = $"{opacity}%";

            MosaicSlider.Value = Math.Clamp(_style.MosaicBlock, (int)MosaicSlider.Minimum, (int)MosaicSlider.Maximum);
            MosaicReadout.Text = $"{_style.MosaicBlock} px";

            FillNone.IsChecked = _style.FillOpacity <= 0;
            FillHalf.IsChecked = _style.FillOpacity is > 0 and < 95;
            FillSolid.IsChecked = _style.FillOpacity >= 95;
        }
        finally
        {
            _suppressSync = false;
        }
    }

    private void UpdateHistoryButtons()
    {
        UndoButton.IsEnabled = _document.CanUndo;
        RedoButton.IsEnabled = _document.CanRedo;
        ClearButton.IsEnabled = !_document.IsEmpty;
    }

    /// <summary>马克笔/高亮的内置笔迹透明度（样式里的 FillOpacity 为 0 时用）。</summary>
    private static int DefaultStrokeOpacity(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Marker => 45,
        AnnotationTool.Highlight => 35,
        _ => 100,
    };

    /// <summary>样式条上显示的不透明度：形状工具就是填充不透明度，笔迹工具是笔迹透明度。</summary>
    private int CurrentOpacity => _tool is AnnotationTool.Marker or AnnotationTool.Highlight
        ? (_style.FillOpacity > 0 ? _style.FillOpacity : DefaultStrokeOpacity(_tool))
        : _style.FillOpacity;

    private static bool SameColor(RgbColor left, RgbColor right) =>
        left.R == right.R && left.G == right.G && left.B == right.B;

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

    // ---------------------------------------------------------------- 样式条交互

    private void SetTool(AnnotationTool tool)
    {
        CommitInlineText();

        _tool = tool;
        _selected = null;
        EndStyleCoalesce();

        SyncUi();
        RenderPreview();
    }

    private void SetStrokeColor(RgbColor color)
    {
        _strokeColor = color;
        _style = _style.WithStroke(color);
        ApplyStyleChange();
    }

    private void SetStrokeWidth(int width, bool coalesce = false)
    {
        _style = _style.WithWidth(width);
        ApplyStyleChange(coalesce);
    }

    private void SetFillOpacity(int opacity)
    {
        _style = _style.WithFillOpacity(opacity);
        ApplyStyleChange();
    }

    private void SetMosaicBlock(int block)
    {
        _style = _style with { MosaicBlock = Math.Clamp(block, 4, 40) };
        ApplyStyleChange();
    }

    private void AdjustWidth(int delta) => SetStrokeWidth(Math.Clamp(_style.StrokeWidth + delta, 1, 20));

    /// <summary>
    /// 改样式：选中的图元顺手跟着改（二次编辑的一部分）。
    /// <paramref name="coalesce"/> = 滑杆拖动：同一图元的连续修改合成**一条**撤销记录，
    /// 否则一次拖动会在撤销栈里留下几十条几乎相同的快照。
    /// </summary>
    private void ApplyStyleChange(bool coalesce = false)
    {
        if (_selected is not null)
        {
            var item = _selected;
            bool merge = coalesce && _restyleCoalesceId == item.Id && _document.CanUndo;

            if (merge)
            {
                _document.Undo();
            }

            _document.Update(AnnotationEditing.Restyle(item, _style));
            _selected = _document.Find(item.Id);
            _restyleCoalesceId = coalesce ? item.Id : 0;
            Recompose();
        }
        else
        {
            RenderPreview();
        }

        SyncUi();
    }

    /// <summary>结束一次"连续改样式"（换工具 / 换选中 / 撤销重做 / 落笔时调用）。</summary>
    private void EndStyleCoalesce() => _restyleCoalesceId = 0;

    private void OnWidthSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _suppressSync)
        {
            return;
        }

        SetStrokeWidth((int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero), coalesce: true);
    }

    private void OnOpacitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _suppressSync)
        {
            return;
        }

        SetFillOpacity((int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero));
    }

    private void OnMosaicSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _suppressSync)
        {
            return;
        }

        SetMosaicBlock((int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero));
    }

    private void OnFillNoneClick(object sender, RoutedEventArgs e) => SetFillOpacity(0);

    private void OnFillHalfClick(object sender, RoutedEventArgs e) => SetFillOpacity(45);

    private void OnFillSolidClick(object sender, RoutedEventArgs e) => SetFillOpacity(100);

    private void OnUndoClick(object sender, RoutedEventArgs e) => Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => Redo();

    private void OnClearClick(object sender, RoutedEventArgs e) => ClearAll();

    private void OnCopyClick(object sender, RoutedEventArgs e) => Confirm(true);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Cancel();

    private void OnConfirmClick(object sender, RoutedEventArgs e) => Confirm(false);

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已经松开：忽略这一次拖动
        }
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

        UpdateHistoryButtons();
        RenderPreview();
    }

    /// <summary>实时预览层：草稿图元 + 选中框与手柄（矢量绘制，拖动时零位图开销）。</summary>
    private void RenderPreview()
    {
        using DrawingContext dc = _previewHost.Visual.RenderOpen();
        dc.PushTransform(new ScaleTransform(1 / _scale, 1 / _scale));

        // 像素效果的草稿先只画量测色虚线框（真实效果在落笔后合成）
        if (_draft is not null)
        {
            if (AnnotationItem.IsPixelEffect(_draft.Kind))
            {
                var pen = new Pen(FocalPalette.MeasureSelect, Chrome(1.5)) { DashStyle = new DashStyle([4, 3], 0) };
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
            DrawSelection(dc, _selected.Bounds);
        }

        dc.Pop();
    }

    /// <summary>
    /// 选中框 / 手柄 / 中心像素标记：这一层是**界面**，不是用户内容，
    /// 所以颜色一律取 Focal 量测令牌（选区琥珀 + 中心像素标记 + 手柄暗描边），
    /// 尺寸取设计值（1px 虚线框、7px 手柄）—— 预览层用的是"图像物理像素"坐标系，
    /// 所以界面尺寸要乘回 DPI 比例才等于设计稿上的 1px / 7px。
    /// </summary>
    private void DrawSelection(DrawingContext dc, PhysicalRect bounds)
    {
        double width = Math.Max(bounds.Width, 1);
        double height = Math.Max(bounds.Height, 1);

        var selectPen = new Pen(FocalPalette.MeasureSelect, Chrome(1)) { DashStyle = new DashStyle([3, 3], 0) };
        selectPen.Freeze();
        dc.DrawRectangle(null, selectPen, new Rect(bounds.X - 2, bounds.Y - 2, width + 4, height + 4));

        double handle = Chrome(FocalPalette.HandleSize);
        var ringPen = new Pen(FocalPalette.Brush("Focal.HandleRingBrush"), Chrome(1));
        ringPen.Freeze();

        foreach (var point in HandlePoints(bounds))
        {
            dc.DrawRectangle(FocalPalette.MeasureSelect, ringPen, new Rect(point.X - (handle / 2), point.Y - (handle / 2), handle, handle));
        }

        // 中心像素标记：1px 暗底 + 量测中心色，压在任意内容上都读得出"这是中心"
        double centerX = bounds.X + (width / 2);
        double centerY = bounds.Y + (height / 2);
        double marker = Chrome(2);
        dc.DrawRectangle(FocalPalette.Brush("Focal.MeasureDarkRingBrush"), null, new Rect(centerX - marker, centerY - marker, marker * 2, marker * 2));
        dc.DrawRectangle(FocalPalette.MeasureCenter, null, new Rect(centerX - (marker / 2), centerY - (marker / 2), marker, marker));
    }

    /// <summary>界面尺寸（DIP）→ 预览层的图像物理像素坐标。</summary>
    private double Chrome(double dip) => dip * _scale;

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

        // 点在机壳（rail / 样式条 / 动作条 / stage 留白）上不落笔
        var position = e.GetPosition(CanvasArea);
        if (position.X < 0 || position.Y < 0 || position.X > CanvasArea.Width || position.Y > CanvasArea.Height)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            StartTextEdit(ToPhysical(position), editSelected: true);
            return;
        }

        var point = ToPhysical(position);
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
        EndStyleCoalesce();

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

        // 选中即把该图元的样式读进样式条：看到的读数就是接下来改的那个值
        _style = hit.Item.Style;
        _strokeColor = _style.Stroke;
        SyncUi();

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
        EndStyleCoalesce();
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
        double fontSizeDip = Math.Max(_style.FontSize / _scale, 10);

        _inlineText = new TextBox
        {
            Style = FocalPalette.Style("Focal.Input"),          // 凹陷表面 + 琥珀聚焦边（组件层）
            MinWidth = 80,
            Height = Math.Max(FocalPalette.ControlHeight, fontSizeDip * 1.6),
            Padding = new Thickness(0),                          // 文字落点与最终绘制的 origin 对齐
            FontSize = fontSizeDip,
            FontFamily = new FontFamily(_style.FontFamily),      // 用户内容字体（不是界面字体）
            Foreground = FocalPalette.FromRgb(_strokeColor),      // 用户颜色：内容层的例外
            Text = target?.Text ?? string.Empty,
            AcceptsReturn = false,
        };

        AutomationProperties.SetName(_inlineText, "标注文字");

        // 输入框的 1px 边框与最终绘制位置对齐
        Canvas.SetLeft(_inlineText, PhysicalToDip(origin.X) - 1);
        Canvas.SetTop(_inlineText, PhysicalToDip(origin.Y) - 1);
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

        _inlineText.LostFocus += (_, _) => CommitInlineText();

        _inlineText.Focus();
        Keyboard.Focus(_inlineText);
    }

    private void CommitInlineText()
    {
        if (_inlineText is null || _resolved)
        {
            return;
        }

        string text = _inlineText.Text.Trim();
        var origin = ToPhysical(new System.Windows.Point(Canvas.GetLeft(_inlineText) + 1, Canvas.GetTop(_inlineText) + 1));

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

        var box = _inlineText;
        _inlineText = null;

        CanvasArea.Children.Remove(box);
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
            EndStyleCoalesce();
            Recompose();
        }
    }

    public void Redo()
    {
        if (_document.Redo())
        {
            _selected = null;
            EndStyleCoalesce();
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
        EndStyleCoalesce();
        Recompose();
    }

    private void ClearAll()
    {
        if (_document.Clear())
        {
            _selected = null;
            EndStyleCoalesce();
            Recompose();
        }
    }

    // ---------------------------------------------------------------- 完成

    /// <summary>确认：合成结果交回调用方（<paramref name="copyOnly"/> 表示动作条「复制」按钮）。</summary>
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
