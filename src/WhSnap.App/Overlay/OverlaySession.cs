using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using WhSnap.Capture;
using WhSnap.Core;
using WhSnap.Services;
namespace WhSnap.Overlay;

/// <summary>
/// 遮罩确认后要执行的动作。对应 §2.1「确认后三选一热键直达」：
/// Enter 走配置里的默认组合，Ctrl+C / Ctrl+S / F3 可直达单一动作。
/// </summary>
internal enum OverlayConfirmAction
{
    /// <summary>按设置执行（复制 / 保存 / 贴图各自的开开关）。</summary>
    Default,

    CopyOnly,

    SaveOnly,

    PinOnly,
}

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
    private readonly IReadOnlyList<HotkeyBinding> _bindings;
    private bool _resolved;

    public OverlaySession(CaptureFrame frame, MonitorInfo activeMonitor, AppSettings settings, IReadOnlyList<HotkeyBinding>? bindings = null)
    {
        _frame = frame;
        _selection = new SelectionModel(frame.VirtualBounds);
        _bindings = bindings ?? HotkeyBindings.Resolve(scope: HotkeyScope.Overlay);

        foreach (var monitor in frame.Monitors)
        {
            var window = new OverlayWindow(this, frame, monitor, settings);
            window.Closed += OnWindowClosed;
            _windows.Add(window);
        }

        _primary = _windows.FirstOrDefault(w => w.Monitor.Bounds == activeMonitor.Bounds) ?? _windows[0];
    }

    public event Action<BitmapSource, PhysicalRect, OverlayConfirmAction>? Confirmed;

    /// <summary>用户要求先把选区送去标注（`T`）。会话会就地关闭，由宿主打开标注编辑器。</summary>
    public event Action<BitmapSource, PhysicalRect>? AnnotateRequested;

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

    /// <summary>
    /// 集中处理键盘操作：所有遮罩窗口都会把按键转到这里。
    /// 动作键位来自配置（<see cref="HotkeyScope.Overlay"/>），方向键微调是固定交互。
    /// </summary>
    internal bool HandleKey(Key key, ModifierKeys modifiers)
    {
        // 方向键微调不是可配置动作，但它必须永远可用（键盘全流程的前提）
        if (key is Key.Left or Key.Right or Key.Up or Key.Down)
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

        var spec = Services.KeySpecFactory.FromWpf(key, modifiers);
        var action = HotkeyBindings.Match(_bindings, spec, HotkeyScope.Overlay);
        if (action is null)
        {
            return false;
        }

        switch (action.Id)
        {
            case "overlay.confirm":
                Confirm();
                return true;

            case "overlay.cancel":
                Cancel();
                return true;

            case "overlay.copy":
                Confirm(OverlayConfirmAction.CopyOnly);
                return true;

            case "overlay.save":
                Confirm(OverlayConfirmAction.SaveOnly);
                return true;

            case "overlay.pin":
                Confirm(OverlayConfirmAction.PinOnly);
                return true;

            case "overlay.annotate":
                RequestAnnotate();
                return true;

            default:
                return false;
        }
    }

    /// <summary>把当前选区交给标注编辑器：裁剪出图元后关闭遮罩，但**不算确认、也不算取消**。</summary>
    private void RequestAnnotate()
    {
        if (_resolved || !_selection.HasSelection)
        {
            return;
        }

        var rect = _selection.Current;
        var result = new CroppedBitmap(_frame.Image, new Int32Rect(
            rect.X - _frame.VirtualBounds.X,
            rect.Y - _frame.VirtualBounds.Y,
            rect.Width,
            rect.Height));

        result.Freeze();
        _resolved = true;

        AnnotateRequested?.Invoke(result, rect);
        CloseAll();
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

    public void Confirm() => Confirm(OverlayConfirmAction.Default);

    public void Confirm(OverlayConfirmAction action)
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
        Confirmed?.Invoke(result, rect, action);
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
