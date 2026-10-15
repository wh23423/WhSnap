using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using WhSnap.Capture;
using WhSnap.Core;
using WhSnap.Services;

namespace WhSnap.Overlay;

/// <summary>
/// 一次截图会话：为每台显示器创建一个遮罩窗口，共享同一个**虚拟桌面绝对物理坐标**的选区模型。
///
/// 跨屏拖拽的实现方式：按下鼠标的那个窗口捕获鼠标，之后无论光标移动到哪台显示器，
/// 事件都由它上报绝对坐标；所有窗口再按"共享选区 ∩ 自己的显示器"重绘。
/// 这样既不需要一个大窗口跨屏（会因混合 DPI 被拉伸），也不需要在窗口之间移交捕获。
/// </summary>
internal sealed class OverlaySession
{
    /// <summary>手柄命中容差（物理像素）。</summary>
    private const int HandleTolerancePx = 6;

    private readonly CaptureFrame _frame;
    private readonly SelectionModel _selection;
    private readonly List<OverlayWindow> _windows = [];
    private readonly OverlayWindow _primary;
    private bool _resolved;

    public OverlaySession(CaptureFrame frame, MonitorInfo activeMonitor, AppSettings settings)
    {
        _frame = frame;
        _selection = new SelectionModel(frame.VirtualBounds);

        foreach (var monitor in frame.Monitors)
        {
            var window = new OverlayWindow(this, frame, monitor, settings);
            window.Closed += OnWindowClosed;
            _windows.Add(window);
        }

        _primary = _windows.FirstOrDefault(w => w.Monitor.Bounds == activeMonitor.Bounds) ?? _windows[0];
    }

    public event Action<BitmapSource, PhysicalRect>? Confirmed;

    public event Action? Cancelled;

    public PhysicalRect CurrentSelection => _selection.Current;

    public IReadOnlyList<MonitorInfo> Monitors => _frame.Monitors;

    /// <summary>已创建的遮罩窗口数量（正常情况下等于显示器数量）。</summary>
    internal int WindowCount => _windows.Count;

    public void Show()
    {
        foreach (var window in _windows)
        {
            window.Show();
        }

        // 键盘事件需要有落点：激活鼠标所在显示器对应的窗口
        _primary.Activate();
        _primary.Focus();
        Broadcast();
    }

    internal void OnPointerPressed(PhysicalPoint absolute, int clickCount)
    {
        if (clickCount >= 2)
        {
            Confirm();
            return;
        }

        var handle = SelectionModel.HitTest(_selection.Current, absolute, HandleTolerancePx);

        switch (handle)
        {
            case SelectionHandle.Inside:
                _selection.BeginMove(absolute);
                break;
            case SelectionHandle.None:
                _selection.Begin(absolute);
                break;
            default:
                _selection.BeginResize(handle, absolute);
                break;
        }

        Broadcast();
    }

    internal void OnPointerMoved(PhysicalPoint absolute)
    {
        if (_selection.IsDragging)
        {
            _selection.Update(absolute);
            Broadcast();
        }

        UpdateCursor(absolute);
    }

    internal void OnPointerReleased()
    {
        _selection.End();
        Broadcast();
    }

    /// <summary>集中处理键盘操作：所有遮罩窗口都会把按键转到这里。</summary>
    internal bool HandleKey(Key key, ModifierKeys modifiers)
    {
        switch (key)
        {
            case Key.Escape:
                Cancel();
                return true;

            case Key.Enter:
                Confirm();
                return true;

            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
            {
                int step = modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
                bool resize = modifiers.HasFlag(ModifierKeys.Control);
                var direction = key switch
                {
                    Key.Left => NudgeDirection.Left,
                    Key.Right => NudgeDirection.Right,
                    Key.Up => NudgeDirection.Up,
                    _ => NudgeDirection.Down,
                };

                if (_selection.Nudge(direction, step, resize))
                {
                    Broadcast();
                }

                return true;
            }

            default:
                return false;
        }
    }

    public void Cancel()
    {
        if (_resolved)
        {
            return;
        }

        _resolved = true;
        Cancelled?.Invoke();
        CloseAll();
    }

    public void Confirm()
    {
        if (_resolved || !_selection.HasSelection)
        {
            // 还没框选就回车不当作确认，避免误操作
            return;
        }

        var rect = _selection.Current;
        var cropRect = new Int32Rect(
            rect.X - _frame.VirtualBounds.X,
            rect.Y - _frame.VirtualBounds.Y,
            rect.Width,
            rect.Height);

        var result = new CroppedBitmap(_frame.Image, cropRect);
        result.Freeze();

        _resolved = true;
        Confirmed?.Invoke(result, rect);
        CloseAll();
    }

    private void Broadcast()
    {
        foreach (var window in _windows)
        {
            window.RenderSelection(_selection.Current);
        }
    }

    private void UpdateCursor(PhysicalPoint absolute)
    {
        foreach (var window in _windows)
        {
            window.RenderCursor(absolute);
        }
    }

    private void CloseAll()
    {
        foreach (var window in _windows)
        {
            window.Closed -= OnWindowClosed;
            window.Close();
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // 任一遮罩窗口被外部关闭（例如 Alt+F4）即视为取消整个会话
        if (!_resolved)
        {
            _resolved = true;
            Cancelled?.Invoke();
        }

        foreach (var window in _windows.Where(w => !ReferenceEquals(w, sender)))
        {
            window.Closed -= OnWindowClosed;
            window.Close();
        }
    }
}
