using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
/// 遮罩浮动工具条上的三种互斥模式（DESIGN §8 ov-toolbar：模式组 → 动作组 → 结束组）。
/// </summary>
internal enum OverlayTool
{
    /// <summary>区域框选（拖动决定边界）。</summary>
    Region,

    /// <summary>元素检测：单击选中光标下的界面元素，Tab 切换候选。</summary>
    Element,

    /// <summary>取色：单击取走光标下的像素颜色并复制，不结束会话。</summary>
    Color,
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
    private readonly AppSettings _settings;
    private readonly ColorHistory _colorHistory;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private IReadOnlyList<ElementCandidate> _candidates = [];
    private ElementCandidate? _highlighted;
    private PhysicalRect? _pendingElement;
    private PhysicalPoint _pressPoint;
    private int _elementGeneration;
    private bool _resolved;
    private readonly long _createdTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

    public OverlaySession(
        CaptureFrame frame,
        MonitorInfo activeMonitor,
        AppSettings settings,
        IReadOnlyList<HotkeyBinding>? bindings = null,
        ColorHistory? colorHistory = null,
        bool colorPickMode = false)
    {
        _frame = frame;
        _selection = new SelectionModel(frame.VirtualBounds);
        _bindings = bindings ?? HotkeyBindings.Resolve(scope: HotkeyScope.Overlay);
        _settings = settings;
        _colorHistory = colorHistory ?? new ColorHistory(settings.ColorHistoryRetention);
        ColorPickMode = colorPickMode;

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

    /// <summary>取到颜色（取色器）。宿主负责持久化颜色历史。</summary>
    public event Action<RgbColor>? ColorPicked;

    /// <summary>状态提示（取色结果等）。</summary>
    public event Action<string, bool>? StatusReported;

    public event Action? Cancelled;

    /// <summary>浮动工具条切换模式（每个遮罩窗口都要跟着更新选中态）。</summary>
    internal event Action<OverlayTool>? ToolChanged;

    /// <summary>当前工具模式。默认区域框选，保持"打开遮罩即可框选"的老手感。</summary>
    internal OverlayTool Tool { get; private set; } = OverlayTool.Region;

    /// <summary>切换工具模式（工具条调用）。</summary>
    internal void SetTool(OverlayTool tool)
    {
        if (Tool == tool)
        {
            return;
        }

        Tool = tool;
        ToolChanged?.Invoke(tool);

        if (tool == OverlayTool.Element)
        {
            StatusReported?.Invoke("元素检测：单击选中元素，Tab 在候选之间切换。", false);
        }
        else if (tool == OverlayTool.Color)
        {
            StatusReported?.Invoke("取色：单击取走该像素颜色并复制（不会结束截图）。", false);
        }
    }

    /// <summary>取某个动作当前的键位文本（提示条与键帽显示用：键位可改，不能写死默认值）。</summary>
    internal string KeyText(string actionId)
    {
        var binding = _bindings.FirstOrDefault(b => string.Equals(b.ActionId, actionId, StringComparison.OrdinalIgnoreCase));
        return binding is { IsActive: true } ? binding.Keys!.Trim() : string.Empty;
    }

    /// <summary>取色模式：单击即取色并结束会话，不产生选区（托盘「取色器」）。</summary>
    public bool ColorPickMode { get; }

    public PhysicalRect CurrentSelection => _selection.Current;

    public IReadOnlyList<MonitorInfo> Monitors => _frame.Monitors;

    /// <summary>已创建的遮罩窗口数量（正常情况下等于显示器数量）。</summary>
    internal int WindowCount => _windows.Count;

    /// <summary>当前元素检测候选（自检读取）。</summary>
    internal IReadOnlyList<ElementCandidate> ElementCandidates => _candidates;

    /// <summary>当前高亮的元素（自检读取）。</summary>
    internal ElementCandidate? HighlightedElement => _highlighted;

    /// <summary>颜色历史（自检读取）。</summary>
    internal ColorHistory History => _colorHistory;

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
        BroadcastColorHistory();

        if (ColorPickMode)
        {
            StatusReported?.Invoke("取色器：点一下即可取色并复制，Esc 退出。", false);
        }

        // 性能埋点（规划 §2.2 第 1 项）：真实路径下 App 在热键派发时就开了起点，
        // 这里结束它（含 热键 → 冻结屏幕 → 建窗 → 显示 全程）；自检直接构造会话时没有起点，
        // 退化成"创建 → 显示可交互"。
        PerfTrace.EndMetricOrRecord(
            PerfMetric.HotkeyToOverlay,
            System.Diagnostics.Stopwatch.GetElapsedTime(_createdTimestamp),
            "自检：创建会话 → 遮罩可交互（不含热键往返）");
    }

    internal void OnPointerPressed(PhysicalPoint absolute, int clickCount)
    {
        if (ColorPickMode)
        {
            PickColor(absolute);
            Cancel();
            return;
        }

        if (clickCount >= 2)
        {
            Confirm();
            return;
        }

        // 工具条上的模式决定这次按下意味着什么
        if (Tool == OverlayTool.Color)
        {
            PickColor(absolute);
            return;
        }

        if (Tool == OverlayTool.Element)
        {
            SelectElementAt(absolute);
            return;
        }

        _pressPoint = absolute;

        // 元素检测：按下时若光标下有高亮元素，先记住它——只有"没拖动"才把它当选中
        _pendingElement = _selection.HasSelection ? null : _highlighted?.Bounds;

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
        // 性能埋点（规划 §2.2 第 2 项）：从收到移动事件到"选区 + 光标 HUD 重绘完成"的同步耗时。
        // UIA 元素高亮是异步到达的，不在这一项里（由 --element-test 的悬停→高亮单独统计）。
        long start = System.Diagnostics.Stopwatch.GetTimestamp();

        if (_selection.IsDragging)
        {
            _selection.Update(absolute);
            Broadcast();

            // 拖动超过 4 像素说明用户想手动框选，放弃元素吸附
            if (_pendingElement is not null && Distance(_pressPoint, absolute) > 4)
            {
                _pendingElement = null;
                RenderElementHighlight(null);
            }
        }
        else
        {
            RequestElements(absolute);
        }

        UpdateCursor(absolute);

        PerfTrace.Record(PerfMetric.MouseFollow, System.Diagnostics.Stopwatch.GetElapsedTime(start));
    }

    internal void OnPointerReleased()
    {
        // 单击（没有明显拖动）+ 光标下有元素 → 直接选中该元素
        if (_pendingElement is { } element && Distance(_pressPoint, Pin.PinWindow.CursorPosition()) <= 4)
        {
            _selection.SetSelection(element);
            StatusReported?.Invoke($"已选中元素：{element}", false);
        }

        _pendingElement = null;
        _selection.End();
        Broadcast();
    }

    private static double Distance(PhysicalPoint a, PhysicalPoint b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>异步查询光标下的元素候选（UIA 可能慢，绝不阻塞界面）。过期结果直接丢弃。</summary>
    private void RequestElements(PhysicalPoint point)
    {
        if (!_settings.ElementDetectionEnabled)
        {
            if (_highlighted is not null)
            {
                _highlighted = null;
                _candidates = [];
                RenderElementHighlight(null);
            }

            return;
        }

        int generation = ++_elementGeneration;

        _ = ElementLocator.FindAsync(point, _settings.ElementDetectionUseUia).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully || generation != _elementGeneration)
            {
                return;
            }

            var candidates = task.Result;

            _dispatcher.BeginInvoke(() =>
            {
                if (generation != _elementGeneration || _resolved)
                {
                    return;
                }

                _candidates = candidates;
                _highlighted = ElementDetection.Choose(candidates, point);
                RenderElementHighlight(_highlighted?.Bounds, _highlighted?.Describe());
            });
        }, TaskScheduler.Default);
    }

    /// <summary>Tab：在光标下的元素候选之间由内向外切换，并直接选中。</summary>
    private void CycleElement()
    {
        var cursor = Pin.PinWindow.CursorPosition();

        if (_candidates.Count == 0)
        {
            _candidates = ElementLocator.Find(cursor, _settings.ElementDetectionUseUia);
        }

        _highlighted = ElementDetection.Cycle(_candidates, cursor, _highlighted);

        if (_highlighted is null)
        {
            StatusReported?.Invoke("光标下没有检测到界面元素（可关闭元素检测或改用手动框选）。", true);
            return;
        }

        _selection.SetSelection(_highlighted.Bounds);
        RenderElementHighlight(_highlighted.Bounds, _highlighted.Describe());
        Broadcast();
        StatusReported?.Invoke($"元素：{_highlighted.Describe()}", false);
    }

    private void RenderElementHighlight(PhysicalRect? absolute, string? label = null)
    {
        foreach (var window in _windows)
        {
            window.RenderElement(absolute, label);
        }
    }

    /// <summary>元素工具：单击选中光标下的界面元素（同步查询，工具条交互不能等异步结果）。</summary>
    private void SelectElementAt(PhysicalPoint point)
    {
        if (!_settings.ElementDetectionEnabled)
        {
            StatusReported?.Invoke("元素检测已在设置里关闭；用「区域」模式手动框选。", true);
            return;
        }

        var candidates = ElementLocator.Find(point, _settings.ElementDetectionUseUia);
        var chosen = ElementDetection.Choose(candidates, point) ?? _highlighted;

        if (chosen is null)
        {
            StatusReported?.Invoke("光标下没有检测到界面元素；用「区域」模式手动框选。", true);
            return;
        }

        _candidates = candidates;
        _highlighted = chosen;
        _selection.SetSelection(chosen.Bounds);
        RenderElementHighlight(chosen.Bounds, chosen.Describe());
        Broadcast();
        StatusReported?.Invoke($"已选中元素：{chosen.Describe()}", false);
    }

    private void BroadcastColorHistory()
    {
        foreach (var window in _windows)
        {
            window.RenderColorHistory(_colorHistory, _settings.OverlayShowColorHistory);
        }
    }

    /// <summary>取色：读冻结底图上光标所在像素，按设置格式复制到剪贴板并计入历史。</summary>
    internal void PickColorAtCursor() => PickColor(Pin.PinWindow.CursorPosition());

    internal void PickColor(PhysicalPoint absolute)
    {
        if (!TryReadPixel(absolute, out var color))
        {
            StatusReported?.Invoke("取色失败：光标超出冻结画面范围。", true);
            return;
        }

        _colorHistory.Add(color);
        var format = ColorText.ParseFormat(_settings.ColorCopyFormat);
        string clipboardText = ColorText.Format(color, format);
        string oneLine = clipboardText.Replace(Environment.NewLine, "　", StringComparison.Ordinal);

        if (ClipboardService.TrySetText(clipboardText, out string error))
        {
            StatusReported?.Invoke($"已复制颜色 {oneLine}", false);
        }
        else
        {
            StatusReported?.Invoke($"取到颜色 {oneLine}，但写入剪贴板失败：{error}", true);
        }

        ColorPicked?.Invoke(color);
        BroadcastColorHistory();
    }

    /// <summary>从冻结画面读一个像素。</summary>
    internal bool TryReadPixel(PhysicalPoint absolute, out RgbColor color)
    {
        color = default;

        int x = absolute.X - _frame.VirtualBounds.X;
        int y = absolute.Y - _frame.VirtualBounds.Y;

        if (x < 0 || y < 0 || x >= _frame.Image.PixelWidth || y >= _frame.Image.PixelHeight)
        {
            return false;
        }

        var pixel = new CroppedBitmap(_frame.Image, new Int32Rect(x, y, 1, 1));
        var buffer = new byte[4];
        pixel.CopyPixels(buffer, 4, 0);
        color = new RgbColor(buffer[2], buffer[1], buffer[0]);
        return true;
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

            case "overlay.cycleElement":
                CycleElement();
                return true;

            case "overlay.pickColor":
                PickColorAtCursor();
                return true;

            default:
                return false;
        }
    }

    /// <summary>把当前选区交给标注编辑器：裁剪出图元后关闭遮罩，但**不算确认、也不算取消**。</summary>
    internal void RequestAnnotate()
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

        // 性能埋点（规划 §2.2 第 3 项）：确认 → 裁剪 → 宿主动作。
        // 起点开在这里，**由宿主在真正写入剪贴板后结束**（只有走到剪贴板那一步才算"可粘贴"；
        // 选保存/贴图或没有宿主动作时会话会取消这条起点）。
        PerfTrace.BeginMetric(PerfMetric.ConfirmToClipboard);

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
