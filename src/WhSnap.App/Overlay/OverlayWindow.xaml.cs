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

namespace WhSnap.Overlay;

/// <summary>
/// 单台显示器的遮罩窗口。
///
/// 设计要点：选区模型是**会话级共享的虚拟桌面绝对物理坐标**，本窗口只负责
/// "把共享选区在本显示器范围内的部分画出来"。因此：
///   * 跨屏拖拽天然成立——鼠标被按下时的那个窗口捕获鼠标，后续移动仍上报绝对坐标；
///   * 每台显示器只有一个窗口、一个 DPI 上下文，混合 DPI 下不会出现拉伸错位。
/// </summary>
internal partial class OverlayWindow : System.Windows.Window
{
    private const double HandleSizeDip = 7;
    private const int MagnifierOffsetPx = 24;

    private readonly OverlaySession _session;
    private readonly CaptureFrame _frame;
    private readonly double _scale;
    private readonly int _dimOpacity;
    private readonly Rectangle[] _dim = new Rectangle[4];
    private readonly Rectangle[] _handles = new Rectangle[8];
    private readonly Rectangle _border;
    private readonly WriteableBitmap _magnifierBitmap;
    private readonly BitmapSource _frozenSlice;

    public OverlayWindow(OverlaySession session, CaptureFrame frame, MonitorInfo monitor, AppSettings settings)
    {
        InitializeComponent();

        _session = session;
        _frame = frame;
        Monitor = monitor;
        _scale = monitor.ScaleX <= 0 ? 1.0 : monitor.ScaleX;
        _dimOpacity = Math.Clamp(settings.OverlayDimOpacity, 0, 90);

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

        var dimBrush = new SolidColorBrush(Color.FromArgb((byte)(_dimOpacity * 255 / 100), 0, 0, 0));
        dimBrush.Freeze();

        for (int i = 0; i < _dim.Length; i++)
        {
            _dim[i] = new Rectangle { Fill = dimBrush, IsHitTestVisible = false };
            Layer.Children.Add(_dim[i]);
        }

        _border = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x3B, 0x9C, 0xFF)),
            StrokeThickness = 1.5,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Layer.Children.Add(_border);

        var handleFill = new SolidColorBrush(Color.FromRgb(0x3B, 0x9C, 0xFF));
        var handleStroke = new SolidColorBrush(Colors.White);
        handleFill.Freeze();
        handleStroke.Freeze();

        for (int i = 0; i < _handles.Length; i++)
        {
            _handles[i] = new Rectangle
            {
                Width = HandleSizeDip,
                Height = HandleSizeDip,
                Fill = handleFill,
                Stroke = handleStroke,
                StrokeThickness = 1,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            Layer.Children.Add(_handles[i]);
        }

        // 放大镜：源 31x31 物理像素，8 倍最近邻放大
        int sample = Magnifier.DefaultSampleSize;
        int zoom = Magnifier.DefaultZoom;
        _magnifierBitmap = new WriteableBitmap(sample, sample, 96, 96, PixelFormats.Bgra32, null);
        MagnifierImage.Source = _magnifierBitmap;

        double panelDip = PhysicalToDip(sample * zoom);
        MagnifierPanel.Width = panelDip;
        MagnifierPanel.Height = panelDip;
        MagnifierRoot.Width = panelDip;
        MagnifierRoot.Height = panelDip;
        MagnifierImage.Width = panelDip;
        MagnifierImage.Height = panelDip;
        MagnifierGrid.Width = panelDip;
        MagnifierGrid.Height = panelDip;
        MagnifierCenter.Width = PhysicalToDip(zoom);
        MagnifierCenter.Height = PhysicalToDip(zoom);

        BuildMagnifierGrid(sample, zoom);

        // 先把窗口放到目标显示器，让 WPF 从一开始就用该屏的 DPI 上下文
        Left = monitor.Bounds.X / _scale;
        Top = monitor.Bounds.Y / _scale;
        Width = PhysicalToDip(monitor.Bounds.Width);
        Height = PhysicalToDip(monitor.Bounds.Height);

        SourceInitialized += (_, _) => ApplyPhysicalBounds();
        DpiChanged += (_, _) => ApplyPhysicalBounds();
    }

    public MonitorInfo Monitor { get; }

    internal int SampleSize => Magnifier.DefaultSampleSize;

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

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        var absolute = ToAbsolutePhysical(e.GetPosition(this));
        CaptureMouse();
        _session.OnPointerPressed(absolute, e.ClickCount);
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _session.OnPointerMoved(ToAbsolutePhysical(e.GetPosition(this)));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
        _session.OnPointerReleased();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        _session.Cancel();
    }

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

        if (!visible)
        {
            foreach (var handle in _handles)
            {
                handle.Visibility = Visibility.Collapsed;
            }

            return;
        }

        Canvas.SetLeft(_border, rect.Left);
        Canvas.SetTop(_border, rect.Top);
        _border.Width = rect.Width;
        _border.Height = rect.Height;

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

                bool inside = centerX >= -HandleSizeDip && centerX <= fullWidth + HandleSizeDip
                              && centerY >= -HandleSizeDip && centerY <= fullHeight + HandleSizeDip;

                handle.Visibility = inside ? Visibility.Visible : Visibility.Collapsed;

                if (inside)
                {
                    Canvas.SetLeft(handle, centerX - (HandleSizeDip / 2));
                    Canvas.SetTop(handle, centerY - (HandleSizeDip / 2));
                }
            }
        }
    }

    /// <summary>渲染元素检测高亮（绝对物理坐标；跨屏时只画落在本显示器内的部分）。</summary>
    internal void RenderElement(PhysicalRect? absolute)
    {
        if (absolute is not { } rect || rect.IsEmpty)
        {
            ElementBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var local = VirtualDesktop.ToMonitorLocal(rect, Monitor);

        // 与本屏求交：跨屏元素只显示本屏那一块
        var visible = local.Intersect(new PhysicalRect(0, 0, Monitor.Bounds.Width, Monitor.Bounds.Height));
        if (visible.IsEmpty)
        {
            ElementBorder.Visibility = Visibility.Collapsed;
            return;
        }

        double left = PhysicalToDip(visible.X);
        double top = PhysicalToDip(visible.Y);

        ElementBorder.Visibility = Visibility.Visible;
        Canvas.SetLeft(ElementBorder, left);
        Canvas.SetTop(ElementBorder, top);
        ElementBorder.Width = PhysicalToDip(visible.Width);
        ElementBorder.Height = PhysicalToDip(visible.Height);

        // 标签跟随元素左上角，超出屏幕时贴到内部
        ElementLabel.Text = $"{rect.Width} × {rect.Height}　元素";
        ElementLabelBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelSize = ElementLabelBorder.DesiredSize;
        double fullWidth = PhysicalToDip(Monitor.Bounds.Width);
        double fullHeight = PhysicalToDip(Monitor.Bounds.Height);

        Canvas.SetLeft(ElementLabelBorder, Math.Clamp(left, 0, Math.Max(fullWidth - labelSize.Width, 0)));
        Canvas.SetTop(ElementLabelBorder, Math.Clamp(top - labelSize.Height - 2, 0, Math.Max(fullHeight - labelSize.Height, 0)));
    }

    /// <summary>渲染颜色历史色块（左下角）。</summary>
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
            var swatch = new Rectangle
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(2, 0, 2, 0),
                Fill = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
                Stroke = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
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

    /// <summary>更新光标相关 HUD。只有光标确实位于本显示器时才绘制放大镜/色值/尺寸。</summary>
    internal void RenderCursor(PhysicalPoint absolute)
    {
        bool inside = absolute.X >= Monitor.Bounds.X && absolute.X < Monitor.Bounds.Right
                      && absolute.Y >= Monitor.Bounds.Y && absolute.Y < Monitor.Bounds.Bottom;

        if (!inside)
        {
            MagnifierPanel.Visibility = Visibility.Collapsed;
            HudBorder.Visibility = Visibility.Collapsed;
            ColorBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var layout = Magnifier.Compute(absolute, Monitor.Bounds, Magnifier.DefaultSampleSize, Magnifier.DefaultZoom);
        var pixels = ReadPixels(layout.Source);
        _magnifierBitmap.WritePixels(
            new Int32Rect(0, 0, layout.SampleSize, layout.SampleSize),
            pixels,
            layout.SampleSize * 4,
            0);

        var (centerX, centerY) = Magnifier.CenterCell(layout, absolute);
        Canvas.SetLeft(MagnifierCenter, PhysicalToDip(centerX * layout.Zoom));
        Canvas.SetTop(MagnifierCenter, PhysicalToDip(centerY * layout.Zoom));

        // 放大镜与色值跟随光标，超出窗口时自动翻到另一侧
        double cursorDipX = PhysicalToDip(absolute.X - Monitor.Bounds.X);
        double cursorDipY = PhysicalToDip(absolute.Y - Monitor.Bounds.Y);
        double fullWidth = PhysicalToDip(Monitor.Bounds.Width);
        double fullHeight = PhysicalToDip(Monitor.Bounds.Height);
        double offset = PhysicalToDip(MagnifierOffsetPx);

        double panelLeft = cursorDipX + offset;
        double panelTop = cursorDipY + offset;

        if (panelLeft + MagnifierPanel.Width > fullWidth)
        {
            panelLeft = cursorDipX - offset - MagnifierPanel.Width;
        }

        if (panelTop + MagnifierPanel.Height > fullHeight)
        {
            panelTop = cursorDipY - offset - MagnifierPanel.Height;
        }

        panelLeft = Math.Clamp(panelLeft, 0, Math.Max(fullWidth - MagnifierPanel.Width, 0));
        panelTop = Math.Clamp(panelTop, 0, Math.Max(fullHeight - MagnifierPanel.Height, 0));

        MagnifierPanel.Visibility = Visibility.Visible;
        Canvas.SetLeft(MagnifierPanel, panelLeft);
        Canvas.SetTop(MagnifierPanel, panelTop);

        // 色值：取放大镜中心像素（即光标所在像素）
        int centerIndex = ((centerY * layout.SampleSize) + centerX) * 4;
        if (centerIndex >= 0 && centerIndex + 3 < pixels.Length)
        {
            byte b = pixels[centerIndex];
            byte g = pixels[centerIndex + 1];
            byte r = pixels[centerIndex + 2];
            ColorText.Text = $"#{r:X2}{g:X2}{b:X2}　({r},{g},{b})";
        }

        ColorBorder.Visibility = Visibility.Visible;
        Canvas.SetLeft(ColorBorder, panelLeft);
        Canvas.SetTop(ColorBorder, panelTop + MagnifierPanel.Height + 4);

        // 尺寸提示：有选区时显示尺寸与左上角绝对坐标，否则显示光标坐标
        var selection = _session.CurrentSelection;
        HudText.Text = selection.IsEmpty
            ? $"{absolute.X}, {absolute.Y}"
            : $"{selection.Width} × {selection.Height}　@{selection.X},{selection.Y}";

        HudBorder.Visibility = Visibility.Visible;
        HudBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = HudBorder.DesiredSize;
        double hudLeft = Math.Clamp(cursorDipX + offset, 0, Math.Max(fullWidth - size.Width, 0));
        double hudTop = Math.Clamp(cursorDipY - offset - size.Height, 0, Math.Max(fullHeight - size.Height, 0));
        Canvas.SetLeft(HudBorder, hudLeft);
        Canvas.SetTop(HudBorder, hudTop);
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

    /// <summary>像素网格：每个源像素一格，帮助判断是否选到了想要的边界。</summary>
    private void BuildMagnifierGrid(int sample, int zoom)
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        brush.Freeze();
        double step = PhysicalToDip(zoom);

        for (int i = 1; i < sample; i++)
        {
            double offset = i * step;

            MagnifierGrid.Children.Add(new Rectangle
            {
                Width = 1,
                Height = PhysicalToDip(sample * zoom),
                Fill = brush,
            });

            var vertical = (Rectangle)MagnifierGrid.Children[^1];
            Canvas.SetLeft(vertical, offset);
            Canvas.SetTop(vertical, 0);

            MagnifierGrid.Children.Add(new Rectangle
            {
                Width = PhysicalToDip(sample * zoom),
                Height = 1,
                Fill = brush,
            });

            var horizontal = (Rectangle)MagnifierGrid.Children[^1];
            Canvas.SetLeft(horizontal, 0);
            Canvas.SetTop(horizontal, offset);
        }
    }

    private double PhysicalToDip(int physical) => physical / _scale;

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
