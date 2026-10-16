using System.Globalization;
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
using WhSnap.Theme;

namespace WhSnap.Overlay;

/// <summary>
/// 单台显示器的遮罩窗口（Focal 设计语言：「暗幕四块拼接 + 琥珀选区 + 11×11 放大镜 + 浮动工具条」）。
///
/// 设计要点：选区模型是**会话级共享的虚拟桌面绝对物理坐标**，本窗口只负责
/// "把共享选区在本显示器范围内的部分画出来"。因此：
///   * 跨屏拖拽天然成立——鼠标被按下时的那个窗口捕获鼠标，后续移动仍上报绝对坐标；
///   * 每台显示器只有一个窗口、一个 DPI 上下文，混合 DPI 下不会出现拉伸错位。
///
/// 颜色一律来自 <see cref="FocalPalette"/>（令牌唯一来源见 Theme/FocalTheme.xaml）。
/// </summary>
internal partial class OverlayWindow : System.Windows.Window
{
    private const int MagnifierOffsetPx = 24;
    private const int SizeLabelGapPx = 8;

    private readonly OverlaySession _session;
    private readonly CaptureFrame _frame;
    private readonly double _scale;
    private readonly int _dimOpacity;
    private readonly double _handleSize;
    private readonly Rectangle[] _dim = new Rectangle[4];
    private readonly Rectangle[] _handles = new Rectangle[8];
    private readonly Rectangle _border;
    private readonly Rectangle _borderRing;
    private readonly WriteableBitmap _magnifierBitmap;
    private readonly BitmapSource _frozenSlice;
    private readonly int _loupeSample;
    private readonly int _loupeZoom;
    private bool _pointerStartedOnChrome;

    public OverlayWindow(OverlaySession session, CaptureFrame frame, MonitorInfo monitor, AppSettings settings)
    {
        InitializeComponent();

        _session = session;
        _frame = frame;
        Monitor = monitor;
        _scale = monitor.ScaleX <= 0 ? 1.0 : monitor.ScaleX;
        _dimOpacity = Math.Clamp(settings.OverlayDimOpacity, 0, 90);
        _handleSize = FocalPalette.HandleSize;

        // 本显示器那一块冻结画面
        var localCrop = new Int32Rect(
            monitor.Bounds.X - frame.VirtualBounds.X,
            monitor.Bounds.Y - frame.VirtualBounds.Y,
            monitor.Bounds.Width,
            monitor.Bounds.Height);

        var crop = new CroppedBitmap(frame.Image, localCrop);
        crop.Freeze();
        _frozenSlice = crop;

        FrozenImage.Source = crop;
        FrozenImage.Width = PhysicalToDip(monitor.Bounds.Width);
        FrozenImage.Height = PhysicalToDip(monitor.Bounds.Height);

        // 暗幕：浓度来自设置，颜色来自令牌（Focal.ScrimBrush）
        var dimBrush = FocalPalette.Scrim(_dimOpacity);

        for (int i = 0; i < _dim.Length; i++)
        {
            _dim[i] = new Rectangle { Fill = dimBrush, IsHitTestVisible = false };
            Layer.Children.Add(_dim[i]);
        }

        // 选区：琥珀 1px 实线 + 外侧 1px 暗线（保证压在浅色内容上也读得出边界）
        _borderRing = new Rectangle
        {
            Stroke = FocalPalette.Brush("Focal.MeasureDarkRingBrush"),
            StrokeThickness = 1,
            Fill = null,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Layer.Children.Add(_borderRing);

        _border = new Rectangle
        {
            Stroke = FocalPalette.MeasureSelect,
            StrokeThickness = 1,
            Fill = null,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Layer.Children.Add(_border);

        // 8 向手柄：7px 琥珀方块 + 1px 暗描边（方的是"点的"）
        var handleFill = FocalPalette.MeasureSelect;
        var handleStroke = FocalPalette.Brush("Focal.HandleRingBrush");

        for (int i = 0; i < _handles.Length; i++)
        {
            _handles[i] = new Rectangle
            {
                Width = _handleSize,
                Height = _handleSize,
                RadiusX = 1,
                RadiusY = 1,
                Fill = handleFill,
                Stroke = handleStroke,
                StrokeThickness = 1,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            Layer.Children.Add(_handles[i]);
        }

        // 放大镜：11×11 物理像素 @12x = 132px（DESIGN §8），最近邻放大
        _loupeSample = (int)FocalPalette.Metric("Focal.LoupeSample");
        _loupeZoom = (int)FocalPalette.Metric("Focal.LoupeZoom");

        _magnifierBitmap = new WriteableBitmap(_loupeSample, _loupeSample, 96, 96, PixelFormats.Bgra32, null);
        MagnifierImage.Source = _magnifierBitmap;

        double loupeDip = FocalPalette.Metric("Focal.LoupeSize");
        MagnifierRoot.Width = loupeDip;
        MagnifierRoot.Height = loupeDip;
        MagnifierImage.Width = loupeDip;
        MagnifierImage.Height = loupeDip;
        MagnifierGrid.Width = loupeDip;
        MagnifierGrid.Height = loupeDip;

        // 中心像素环 = 一个源像素 = zoom 物理像素
        MagnifierCenter.Width = PhysicalToDip(_loupeZoom);
        MagnifierCenter.Height = PhysicalToDip(_loupeZoom);

        BuildMagnifierGrid(_loupeSample, _loupeZoom);
        ApplyBindings();

        // 先把窗口放到目标显示器，让 WPF 从一开始就用该屏的 DPI 上下文
        Left = monitor.Bounds.X / _scale;
        Top = monitor.Bounds.Y / _scale;
        Width = PhysicalToDip(monitor.Bounds.Width);
        Height = PhysicalToDip(monitor.Bounds.Height);

        // 提示条内容多了以后在窄屏上会超出画面：给一个"屏宽 - 两侧各 24"的上限，让它自己折行。
        HintBar.MaxWidth = Math.Max(PhysicalToDip(monitor.Bounds.Width) - 48, 240);

        SourceInitialized += (_, _) => ApplyPhysicalBounds();
        DpiChanged += (_, _) => ApplyPhysicalBounds();
        _session.ToolChanged += OnToolChanged;
        Closed += (_, _) => _session.ToolChanged -= OnToolChanged;
    }

    public MonitorInfo Monitor { get; }

    internal int SampleSize => _loupeSample;

    /// <summary>把当前键位绑定填进提示条与工具条键帽（键位可改，不能写死默认值）。</summary>
    private void ApplyBindings()
    {
        SetKbd(HintTabKey, _session.KeyText("overlay.cycleElement"));
        SetKbd(HintEnterKey, _session.KeyText("overlay.confirm"));
        SetKbd(HintPinKey, _session.KeyText("overlay.pin"));
        SetKbd(HintCancelKey, _session.KeyText("overlay.cancel"));
        SetKbd(HintAnnotateKey, _session.KeyText("overlay.annotate"));
        SetKbd(HintColorKey, _session.KeyText("overlay.pickColor"));
        SetKbd(ActionCopyKey, _session.KeyText("overlay.copy"));
        SetKbd(ActionSaveKey, _session.KeyText("overlay.save"));
        SetKbd(ActionPinKey, _session.KeyText("overlay.pin"));
        SetKbd(ToolColorKey, _session.KeyText("overlay.pickColor"));
        SetKbd(ToolAnnotateKey, _session.KeyText("overlay.annotate"));
        ApplyToolState();
    }

    /// <summary>键帽：未绑定的动作不显示空壳键帽。</summary>
    private static void SetKbd(TextBlock text, string keys)
    {
        text.Text = keys;
        if (text.Parent is Border cap)
        {
            cap.Visibility = string.IsNullOrWhiteSpace(keys) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void ApplyToolState()
    {
        ToolRegion.IsChecked = _session.Tool == OverlayTool.Region;
        ToolElement.IsChecked = _session.Tool == OverlayTool.Element;
        ToolColor.IsChecked = _session.Tool == OverlayTool.Color;
        ToolAnnotate.IsChecked = false;
    }

    private void OnToolChanged(OverlayTool tool) => ApplyToolState();

    /// <summary>用物理像素设置窗口位置与尺寸，绕开 WPF DIP 换算在多屏下的歧义。</summary>
    private void ApplyPhysicalBounds()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HWND_TOPMOST,
            Monitor.Bounds.X,
            Monitor.Bounds.Y,
            Monitor.Bounds.Width,
            Monitor.Bounds.Height,
            NativeMethods.SWP_SHOWWINDOW);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Focus();
        Keyboard.Focus(this);
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (_session.HandleKey(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// 自检用：按**真实的 WPF 路由事件**派发一次 PreviewKeyDown（与用户按键走同一条路径：
    /// 路由事件 → <c>OnPreviewKeyDown</c> → 会话处理）。返回事件是否被处理。
    /// </summary>
    internal bool RaisePreviewKey(Key key, ModifierKeys modifiers)
    {
        // WPF 的按键处理方法读的是真实键盘状态（Keyboard.Modifiers），伪造不了修饰键：
        // 状态不一致时如实返回 false，由调用方退化处理，而不是造出"看起来通过"的假证据。
        if (modifiers != Keyboard.Modifiers)
        {
            return false;
        }

        if (PresentationSource.FromVisual(this) is not { } source)
        {
            return false;
        }

        var args = new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };

        RaiseEvent(args);
        return args.Handled;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // 浮动工具条上的点击属于"操作面"，不能同时开始框选
        _pointerStartedOnChrome = IsOverChrome(e.OriginalSource as DependencyObject);
        if (_pointerStartedOnChrome)
        {
            return;
        }

        var absolute = ToAbsolutePhysical(e.GetPosition(this));
        CaptureMouse();
        _session.OnPointerPressed(absolute, e.ClickCount);
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_pointerStartedOnChrome)
        {
            return;
        }

        // Shift = 等比约束（正方形 / 保持原比例），按下时就生效，不需要松开重按
        _session.OnPointerMoved(ToAbsolutePhysical(e.GetPosition(this)), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_pointerStartedOnChrome)
        {
            _pointerStartedOnChrome = false;
            return;
        }

        ReleaseMouseCapture();
        _session.OnPointerReleased();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        _session.Cancel();
    }

    private bool IsOverChrome(DependencyObject? source)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ToolbarBar) || ReferenceEquals(source, HintBar))
            {
                return true;
            }

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    // ── 工具条 ────────────────────────────────────────────────────────────────

    private void ToolRegion_Click(object sender, RoutedEventArgs e) => _session.SetTool(OverlayTool.Region);

    private void ToolElement_Click(object sender, RoutedEventArgs e) => _session.SetTool(OverlayTool.Element);

    private void ToolColor_Click(object sender, RoutedEventArgs e) => _session.SetTool(OverlayTool.Color);

    private void ToolAnnotate_Click(object sender, RoutedEventArgs e)
    {
        ApplyToolState();
        _session.RequestAnnotate();
    }

    private void ActionCopy_Click(object sender, RoutedEventArgs e) => _session.Confirm(OverlayConfirmAction.CopyOnly);

    private void ActionSave_Click(object sender, RoutedEventArgs e) => _session.Confirm(OverlayConfirmAction.SaveOnly);

    private void ActionPin_Click(object sender, RoutedEventArgs e) => _session.Confirm(OverlayConfirmAction.PinOnly);

    private void ActionCancel_Click(object sender, RoutedEventArgs e) => _session.Cancel();

    private void ActionConfirm_Click(object sender, RoutedEventArgs e) => _session.Confirm();

    // ── 选区 / 暗幕 / 尺寸标签 ────────────────────────────────────────────────

    /// <summary>把共享的绝对选区渲染到本显示器范围内（暗化在选区以外的部分）。</summary>
    internal void RenderSelection(PhysicalRect absolute)
    {
        double fullWidth = PhysicalToDip(Monitor.Bounds.Width);
        double fullHeight = PhysicalToDip(Monitor.Bounds.Height);

        var local = VirtualDesktop.ToMonitorLocal(absolute, Monitor);
        var rect = new Rect(
            PhysicalToDip(local.X),
            PhysicalToDip(local.Y),
            PhysicalToDip(Math.Max(local.Width, 0)),
            PhysicalToDip(Math.Max(local.Height, 0)));

        // 选区可能大部分落在本显示器之外（跨屏框选），必须先与本屏求交再算暗化区域，
        // 否则会把负的 Left/Top 当成宽度传给 Rect 而抛异常。
        double selLeft = Math.Clamp(rect.Left, 0, fullWidth);
        double selTop = Math.Clamp(rect.Top, 0, fullHeight);
        double selRight = Math.Clamp(rect.Right, 0, fullWidth);
        double selBottom = Math.Clamp(rect.Bottom, 0, fullHeight);

        // 未框选时 rect 为 (0,0,0,0)，此时四块暗化区域正好铺满整屏
        SetRect(_dim[0], new Rect(0, 0, fullWidth, selTop));
        SetRect(_dim[1], new Rect(0, selBottom, fullWidth, Math.Max(fullHeight - selBottom, 0)));
        SetRect(_dim[2], new Rect(0, selTop, selLeft, Math.Max(selBottom - selTop, 0)));
        SetRect(_dim[3], new Rect(selRight, selTop, Math.Max(fullWidth - selRight, 0), Math.Max(selBottom - selTop, 0)));

        bool visible = !absolute.IsEmpty;
        _border.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _borderRing.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SizeLabel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (!visible)
        {
            foreach (var handle in _handles)
            {
                handle.Visibility = Visibility.Collapsed;
            }

            return;
        }

        SetRect(_border, rect);
        // 外侧暗线：比选区大 1px、起点外移 1px
        SetRect(_borderRing, new Rect(rect.Left - 1, rect.Top - 1, rect.Width + 2, rect.Height + 2));

        // 8 向手柄：只画落在本显示器可见范围内的那些（跨屏选区时另一部分由对方窗口画）
        double[] xs = [rect.Left, rect.Left + (rect.Width / 2), rect.Right];
        double[] ys = [rect.Top, rect.Top + (rect.Height / 2), rect.Bottom];
        int index = 0;

        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                if (row == 1 && column == 1)
                {
                    continue;
                }

                double centerX = xs[column];
                double centerY = ys[row];
                var handle = _handles[index++];

                bool inside = centerX >= -_handleSize && centerX <= fullWidth + _handleSize
                              && centerY >= -_handleSize && centerY <= fullHeight + _handleSize;

                handle.Visibility = inside ? Visibility.Visible : Visibility.Collapsed;

                if (inside)
                {
                    Canvas.SetLeft(handle, centerX - (_handleSize / 2));
                    Canvas.SetTop(handle, centerY - (_handleSize / 2));
                }
            }
        }

        UpdateSizeLabel(absolute, rect, fullWidth, fullHeight);
    }

    /// <summary>尺寸标签：内容永远是「宽 × 高 · x, y · 缩放」；上方放不下时翻到选区下方。</summary>
    private void UpdateSizeLabel(PhysicalRect absolute, Rect rect, double fullWidth, double fullHeight)
    {
        SizeLabelSize.Text = $"{absolute.Width} × {absolute.Height}";
        SizeLabelPosition.Text = $"{absolute.X}, {absolute.Y}";
        SizeLabelScale.Text = string.Create(CultureInfo.InvariantCulture, $"{_scale * 100:0}%");

        SizeLabel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = SizeLabel.DesiredSize;
        double gap = PhysicalToDip(SizeLabelGapPx);

        double labelTop = rect.Top - size.Height - gap;
        double labelLeft = rect.Left;

        // 上方会被屏幕边缘（或内容）挡住时翻到下方
        if (labelTop < 0)
        {
            labelTop = Math.Min(rect.Bottom + gap, Math.Max(fullHeight - size.Height, 0));
        }

        labelLeft = Math.Clamp(labelLeft, 0, Math.Max(fullWidth - size.Width, 0));
        labelTop = Math.Clamp(labelTop, 0, Math.Max(fullHeight - size.Height, 0));

        Canvas.SetLeft(SizeLabel, labelLeft);
        Canvas.SetTop(SizeLabel, labelTop);
    }

    /// <summary>渲染元素检测高亮（绝对物理坐标；跨屏时只画落在本显示器内的部分）。</summary>
    internal void RenderElement(PhysicalRect? absolute, string? label = null)
    {
        if (absolute is not { } rect || rect.IsEmpty)
        {
            ElementBorder.Visibility = Visibility.Collapsed;
            ElementLabelBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var local = VirtualDesktop.ToMonitorLocal(rect, Monitor);

        // 与本屏求交：跨屏元素只显示本屏那一块
        var visible = local.Intersect(new PhysicalRect(0, 0, Monitor.Bounds.Width, Monitor.Bounds.Height));
        if (visible.IsEmpty)
        {
            ElementBorder.Visibility = Visibility.Collapsed;
            ElementLabelBorder.Visibility = Visibility.Collapsed;
            return;
        }

        double left = PhysicalToDip(visible.X);
        double top = PhysicalToDip(visible.Y);

        ElementBorder.Visibility = Visibility.Visible;
        Canvas.SetLeft(ElementBorder, left);
        Canvas.SetTop(ElementBorder, top);
        ElementBorder.Width = PhysicalToDip(visible.Width);
        ElementBorder.Height = PhysicalToDip(visible.Height);

        // 标签：元素名 + 尺寸，贴在元素外侧；上方放不下就翻到下方
        ElementLabel.Text = string.IsNullOrWhiteSpace(label)
            ? $"{rect.Width} × {rect.Height}"
            : $"{label} · {rect.Width} × {rect.Height}";
        ElementLabelBorder.Visibility = Visibility.Visible;
        ElementLabelBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelSize = ElementLabelBorder.DesiredSize;
        double fullWidth = PhysicalToDip(Monitor.Bounds.Width);
        double fullHeight = PhysicalToDip(Monitor.Bounds.Height);

        double labelLeft = Math.Clamp(left - 2, 0, Math.Max(fullWidth - labelSize.Width, 0));
        double labelTop = top - labelSize.Height - 2;
        if (labelTop < 0)
        {
            labelTop = Math.Min(top + PhysicalToDip(visible.Height) + 2, Math.Max(fullHeight - labelSize.Height, 0));
        }

        Canvas.SetLeft(ElementLabelBorder, labelLeft);
        Canvas.SetTop(ElementLabelBorder, Math.Max(labelTop, 0));
    }

    /// <summary>渲染颜色历史色块（左下角）：颜色用眼睛找，不用一列文字。</summary>
    internal void RenderColorHistory(ColorHistory history, bool visible)
    {
        ColorHistoryStrip.Children.Clear();

        if (!visible || history.Count == 0)
        {
            ColorHistoryPanel.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var color in history.Items.Take(12))
        {
            var swatch = new Border
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(2, 0, 2, 0),
                CornerRadius = FocalPalette.RadiusHair,
                Background = FocalPalette.FromRgb(color),
                BorderBrush = FocalPalette.HairlineDarkStrong,
                BorderThickness = new Thickness(1),
            };

            swatch.ToolTip = ColorValue.Describe(color);
            ColorHistoryStrip.Children.Add(swatch);
        }

        ColorHistoryPanel.Visibility = Visibility.Visible;
        ColorHistoryPanel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = ColorHistoryPanel.DesiredSize;
        double fullHeight = PhysicalToDip(Monitor.Bounds.Height);
        Canvas.SetLeft(ColorHistoryPanel, PhysicalToDip(16));
        Canvas.SetTop(ColorHistoryPanel, Math.Max(fullHeight - size.Height - PhysicalToDip(16), 0));
    }

    /// <summary>更新光标相关 HUD：放大镜 + 四行读数。只有光标确实位于本显示器时才绘制。</summary>
    internal void RenderCursor(PhysicalPoint absolute)
    {
        bool inside = absolute.X >= Monitor.Bounds.X && absolute.X < Monitor.Bounds.Right
                      && absolute.Y >= Monitor.Bounds.Y && absolute.Y < Monitor.Bounds.Bottom;

        if (!inside)
        {
            MagnifierPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var layout = Magnifier.Compute(absolute, Monitor.Bounds, _loupeSample, _loupeZoom);
        var pixels = ReadPixels(layout.Source);
        _magnifierBitmap.WritePixels(
            new Int32Rect(0, 0, layout.SampleSize, layout.SampleSize),
            pixels,
            layout.SampleSize * 4,
            0);

        var (centerX, centerY) = Magnifier.CenterCell(layout, absolute);
        Canvas.SetLeft(MagnifierCenter, PhysicalToDip(centerX * layout.Zoom));
        Canvas.SetTop(MagnifierCenter, PhysicalToDip(centerY * layout.Zoom));

        // 读数：坐标 / 选区 / 色值 / 缩放
        var selection = _session.CurrentSelection;
        ReadoutPosition.Text = $"{absolute.X}, {absolute.Y}";
        ReadoutSelection.Text = selection.IsEmpty ? "—" : $"{selection.Width} × {selection.Height}";
        ReadoutZoom.Text = string.Create(CultureInfo.InvariantCulture, $"1 : {_scale:0.##}");

        // 色值：取放大镜中心像素（即光标所在像素）
        int centerIndex = ((centerY * layout.SampleSize) + centerX) * 4;
        if (centerIndex >= 0 && centerIndex + 3 < pixels.Length)
        {
            byte b = pixels[centerIndex];
            byte g = pixels[centerIndex + 1];
            byte r = pixels[centerIndex + 2];
            ReadoutColor.Text = $"#{r:X2}{g:X2}{b:X2}";
            ReadoutSwatch.Background = FocalPalette.BrushColor(r, g, b);
        }

        // 放大镜跟随光标，超出屏幕时自动翻到另一侧
        double cursorDipX = PhysicalToDip(absolute.X - Monitor.Bounds.X);
        double cursorDipY = PhysicalToDip(absolute.Y - Monitor.Bounds.Y);
        double fullWidth = PhysicalToDip(Monitor.Bounds.Width);
        double fullHeight = PhysicalToDip(Monitor.Bounds.Height);
        double offset = PhysicalToDip(MagnifierOffsetPx);

        MagnifierPanel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var panelSize = MagnifierPanel.DesiredSize;
        MagnifierPanel.Width = panelSize.Width;
        MagnifierPanel.Height = panelSize.Height;

        double panelLeft = cursorDipX + offset;
        double panelTop = cursorDipY + offset;

        if (panelLeft + panelSize.Width > fullWidth)
        {
            panelLeft = cursorDipX - offset - panelSize.Width;
        }

        if (panelTop + panelSize.Height > fullHeight)
        {
            panelTop = cursorDipY - offset - panelSize.Height;
        }

        panelLeft = Math.Clamp(panelLeft, 0, Math.Max(fullWidth - panelSize.Width, 0));
        panelTop = Math.Clamp(panelTop, 0, Math.Max(fullHeight - panelSize.Height, 0));

        MagnifierPanel.Visibility = Visibility.Visible;
        Canvas.SetLeft(MagnifierPanel, panelLeft);
        Canvas.SetTop(MagnifierPanel, panelTop);
    }

    /// <summary>从本显示器的冻结切片里取出源区域的 BGRA 像素。</summary>
    private byte[] ReadPixels(PhysicalRect absoluteSource)
    {
        var local = VirtualDesktop.ToMonitorLocal(absoluteSource, Monitor);
        var cropped = new CroppedBitmap(_frozenSlice, new Int32Rect(local.X, local.Y, local.Width, local.Height));
        int stride = local.Width * 4;
        var buffer = new byte[stride * local.Height];
        cropped.CopyPixels(buffer, stride, 0);
        return buffer;
    }

    /// <summary>像素网格：每个源像素一格（1px 实线），让"这是一个像素"成立。</summary>
    private void BuildMagnifierGrid(int sample, int zoom)
    {
        var brush = FocalPalette.Brush("Focal.PixelGridBrush");
        double step = PhysicalToDip(zoom);
        double full = PhysicalToDip(sample * zoom);

        for (int i = 1; i < sample; i++)
        {
            double offset = i * step;

            var vertical = new Rectangle { Width = 1, Height = full, Fill = brush };
            MagnifierGrid.Children.Add(vertical);
            Canvas.SetLeft(vertical, offset);
            Canvas.SetTop(vertical, 0);

            var horizontal = new Rectangle { Width = full, Height = 1, Fill = brush };
            MagnifierGrid.Children.Add(horizontal);
            Canvas.SetLeft(horizontal, 0);
            Canvas.SetTop(horizontal, offset);
        }
    }

    private double PhysicalToDip(int physical) => physical / _scale;

    private double PhysicalToDip(double physical) => physical / _scale;

    private PhysicalPoint ToAbsolutePhysical(System.Windows.Point dip) => new(
        Monitor.Bounds.X + (int)Math.Round(dip.X * _scale, MidpointRounding.AwayFromZero),
        Monitor.Bounds.Y + (int)Math.Round(dip.Y * _scale, MidpointRounding.AwayFromZero));

    private static void SetRect(Rectangle rectangle, Rect rect)
    {
        Canvas.SetLeft(rectangle, rect.Left);
        Canvas.SetTop(rectangle, rect.Top);
        rectangle.Width = Math.Max(rect.Width, 0);
        rectangle.Height = Math.Max(rect.Height, 0);
    }
}
