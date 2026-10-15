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
/// 全屏遮罩：显示冻结画面、允许拖拽选区、确认后输出裁剪结果。
///
/// 预览版范围：遮罩只覆盖"鼠标所在的那一台显示器"。这样单窗口单 DPI，
/// 坐标换算最不容易出错；跨屏选择（每屏一个遮罩 + 跨屏拖拽）排在 M1。
/// </summary>
internal partial class OverlayWindow : System.Windows.Window
{
    private readonly CaptureFrame _frame;
    private readonly MonitorInfo _monitor;
    private readonly SelectionModel _selection;
    private readonly double _scale;
    private readonly Rectangle[] _dim = new Rectangle[4];
    private readonly Rectangle _border;
    private readonly int _dimOpacity;
    private bool _resolved;

    public event Action<BitmapSource, PhysicalRect>? SelectionConfirmed;

    public event Action? Cancelled;

    public OverlayWindow(CaptureFrame frame, MonitorInfo monitor, AppSettings settings)
    {
        InitializeComponent();

        _frame = frame;
        _monitor = monitor;
        _scale = monitor.ScaleX <= 0 ? 1.0 : monitor.ScaleX;
        _selection = new SelectionModel(new PhysicalRect(0, 0, monitor.Bounds.Width, monitor.Bounds.Height));
        _dimOpacity = Math.Clamp(settings.OverlayDimOpacity, 0, 90);

        // 只取本显示器那一块冻结图，并按 1:1 物理像素落位
        var localCrop = new Int32Rect(
            monitor.Bounds.X - frame.VirtualBounds.X,
            monitor.Bounds.Y - frame.VirtualBounds.Y,
            monitor.Bounds.Width,
            monitor.Bounds.Height);

        var crop = new CroppedBitmap(frame.Image, localCrop);
        crop.Freeze();
        FrozenImage.Source = crop;
        FrozenImage.Width = monitor.Bounds.Width / _scale;
        FrozenImage.Height = monitor.Bounds.Height / _scale;

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

        // 先把窗口放到目标显示器，让 WPF 从一开始就用该屏的 DPI 上下文
        Left = monitor.Bounds.X / _scale;
        Top = monitor.Bounds.Y / _scale;
        Width = monitor.Bounds.Width / _scale;
        Height = monitor.Bounds.Height / _scale;

        SourceInitialized += (_, _) => ApplyPhysicalBounds();
        DpiChanged += (_, _) => ApplyPhysicalBounds();
        RenderSelection();
    }

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
            _monitor.Bounds.X,
            _monitor.Bounds.Y,
            _monitor.Bounds.Width,
            _monitor.Bounds.Height,
            NativeMethods.SWP_SHOWWINDOW);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Activate();
        Focus();
        Keyboard.Focus(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Cancel();
                e.Handled = true;
                return;
            case Key.Enter:
                Confirm();
                e.Handled = true;
                return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (e.ClickCount >= 2)
        {
            Confirm();
            return;
        }

        _selection.Begin(ToPhysical(e.GetPosition(this)));
        CaptureMouse();
        RenderSelection();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var position = e.GetPosition(this);
        if (_selection.IsDragging)
        {
            _selection.Update(ToPhysical(position));
        }

        RenderSelection(position);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _selection.End();
        ReleaseMouseCapture();
        RenderSelection(e.GetPosition(this));
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Cancel();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_resolved)
        {
            _resolved = true;
            Cancelled?.Invoke();
        }
    }

    private void Cancel()
    {
        if (_resolved)
        {
            return;
        }

        _resolved = true;
        _selection.Cancel();
        Cancelled?.Invoke();
        Close();
    }

    private void Confirm()
    {
        if (_resolved)
        {
            return;
        }

        if (!_selection.HasSelection)
        {
            // 还没框选就回车：不当作确认，避免误操作
            return;
        }

        var localRect = _selection.Current;
        var virtualRect = DpiMath.ToVirtual(localRect, _monitor);

        var cropRect = new Int32Rect(
            virtualRect.X - _frame.VirtualBounds.X,
            virtualRect.Y - _frame.VirtualBounds.Y,
            virtualRect.Width,
            virtualRect.Height);

        var result = new CroppedBitmap(_frame.Image, cropRect);
        result.Freeze();

        _resolved = true;
        SelectionConfirmed?.Invoke(result, virtualRect);
        Close();
    }

    private PhysicalPoint ToPhysical(System.Windows.Point dip) =>
        DpiMath.DipPointToPx(dip.X, dip.Y, _scale, _scale);

    private static void SetRect(Rectangle rectangle, Rect rect)
    {
        Canvas.SetLeft(rectangle, rect.Left);
        Canvas.SetTop(rectangle, rect.Top);
        rectangle.Width = Math.Max(rect.Width, 0);
        rectangle.Height = Math.Max(rect.Height, 0);
    }

    private void RenderSelection(System.Windows.Point? cursorDip = null)
    {
        double fullWidth = _monitor.Bounds.Width / _scale;
        double fullHeight = _monitor.Bounds.Height / _scale;

        var selection = _selection.Current;
        var rect = new Rect(
            selection.X / _scale,
            selection.Y / _scale,
            Math.Max(selection.Width / _scale, 0),
            Math.Max(selection.Height / _scale, 0));

        // 未框选时 rect 为 (0,0,0,0)，四块暗化区域正好铺满全屏
        SetRect(_dim[0], new Rect(0, 0, fullWidth, rect.Top));
        SetRect(_dim[1], new Rect(0, rect.Bottom, fullWidth, Math.Max(fullHeight - rect.Bottom, 0)));
        SetRect(_dim[2], new Rect(0, rect.Top, rect.Left, rect.Height));
        SetRect(_dim[3], new Rect(rect.Right, rect.Top, Math.Max(fullWidth - rect.Right, 0), rect.Height));

        if (_selection.HasSelection)
        {
            Canvas.SetLeft(_border, rect.Left);
            Canvas.SetTop(_border, rect.Top);
            _border.Width = rect.Width;
            _border.Height = rect.Height;
            _border.Visibility = Visibility.Visible;
        }
        else
        {
            _border.Visibility = Visibility.Collapsed;
        }

        UpdateHud(cursorDip ?? new System.Windows.Point(double.NaN, double.NaN));
    }

    private void UpdateHud(System.Windows.Point cursorDip)
    {
        string text;
        double x;
        double y;

        if (_selection.HasSelection)
        {
            var selection = _selection.Current;
            text = $"{selection.Width} × {selection.Height}";
            var anchorX = _selection.Cursor?.X ?? selection.X;
            var anchorY = _selection.Cursor?.Y ?? selection.Y;
            x = anchorX / _scale + 16;
            y = anchorY / _scale + 22;
        }
        else if (!double.IsNaN(cursorDip.X))
        {
            var physical = ToPhysical(cursorDip);
            text = $"{physical.X}, {physical.Y}";
            x = cursorDip.X + 16;
            y = cursorDip.Y + 22;
        }
        else
        {
            HudBorder.Visibility = Visibility.Collapsed;
            return;
        }

        HudText.Text = text;
        HudBorder.Visibility = Visibility.Visible;
        HudBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = HudBorder.DesiredSize;
        double maxX = _monitor.Bounds.Width / _scale - size.Width - 4;
        double maxY = _monitor.Bounds.Height / _scale - size.Height - 4;
        Canvas.SetLeft(HudBorder, Math.Clamp(x, 4, Math.Max(maxX, 4)));
        Canvas.SetTop(HudBorder, Math.Clamp(y, 4, Math.Max(maxY, 4)));
    }
}
