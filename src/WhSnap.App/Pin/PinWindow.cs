using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhSnap.Core;
using WhSnap.Interop;
using WhSnap.Services;

namespace WhSnap.Pin;

/// <summary>
/// 一张贴图的窗口：原生 `WS_EX_LAYERED | WS_EX_TOPMOST` 弹出窗口 + 逐像素 alpha 位图。
///
/// 交互设计（对标 Snipaste，键位写进 README）：
///   * 拖动移动；滚轮缩放（以光标为锚点）；`Ctrl`+滚轮调不透明度
///   * `1`/`2` 顺时针/逆时针旋转 90°；`3`/`4` 水平/垂直翻转；`0` 恢复 100%
///   * 双击隐藏；`Shift`+双击切缩略图；`Esc` 隐藏；`Delete` 关闭
///   * 右键菜单覆盖全部操作；方向键 1px 微调（`Shift` 10px）
///   * 透明像素天然鼠标穿透；`P` 或菜单可整窗穿透
///
/// 所有几何都走 <see cref="PinGeometry"/>（物理像素），窗口只负责合成与消息。
/// </summary>
internal sealed class PinWindow : IDisposable
{
    /// <summary>阴影留白：窗口比贴图大一圈，用于画柔和阴影与 1px 描边。</summary>
    internal const int ShadowPad = 12;

    /// <summary>单张贴图的像素上限（约 160 MB 缓冲），防止极端缩放把内存打满。</summary>
    private const long MaxSurfaceBytes = 160L * 1024 * 1024;

    private const string ClassName = "WhSnapPinWindow";

    private static readonly object ClassGate = new();
    private static bool _classRegistered;

    // 委托必须活到进程结束：窗口类持有函数指针，被 GC 回收会直接崩
    private static readonly NativeMethods.WndProcDelegate SharedWndProc = WindowProcedure;

    private readonly PinManager _manager;
    private readonly PinSurface _surface = new();
    private BitmapSource _source;

    private IntPtr _hwnd;
    private GCHandle _selfHandle;
    private bool _disposed;
    private bool _hiddenByUser;
    private bool _dragging;
    private PhysicalPoint _dragCursorOrigin;
    private PhysicalPoint _dragPositionOrigin;
    private int _committedOpacity = -1;
    private uint _appliedExStyle;

    internal PinWindow(PinManager manager, BitmapSource source, PinState state)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(source);

        _manager = manager;
        _source = source;
        _source.Freeze();
        State = state;

        CreateHandle();
        Recompose();
        SyncWindow(forceShow: true);
    }

    public PinState State { get; private set; }

    /// <summary>原始像素（备份与"另存为"用它，缩放/旋转都不影响落盘内容）。</summary>
    public BitmapSource Source => _source;

    /// <summary>备份文件名（由 <see cref="PinBackupStore"/> 在保存时写入）。</summary>
    public string? BackupFileName { get; set; }

    public int ZIndex { get; set; }

    public PhysicalRect Bounds => PinGeometry.Bounds(State);

    internal IntPtr Handle => _hwnd;

    internal bool IsHidden => _hiddenByUser || State.Hidden;

    internal bool IsComposing => _surface.Width > 0;

    /// <summary>贴图内容或状态发生变化（用于备份与面板刷新）。</summary>
    public event Action<PinWindow>? Changed;

    /// <summary>用户要求在这张贴图上标注（M4 接入；未接入时由管理器给出提示）。</summary>
    public event Action<PinWindow>? AnnotationRequested;

    /// <summary>用户要求关闭这张贴图。</summary>
    public event Action<PinWindow>? CloseRequested;

    // ---------------------------------------------------------------- 窗口生命周期

    private void CreateHandle()
    {
        EnsureClassRegistered();

        uint exStyle = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
        _appliedExStyle = exStyle;

        _hwnd = NativeMethods.CreateWindowEx(
            exStyle,
            ClassName,
            "WhSnap 贴图",
            NativeMethods.WS_POPUP,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"创建贴图窗口失败（Win32 {Marshal.GetLastWin32Error()}）。");
        }

        _selfHandle = GCHandle.Alloc(this);
        NativeMethods.SetWindowLongAuto(_hwnd, NativeMethods.GWLP_USERDATA, GCHandle.ToIntPtr(_selfHandle));
        NativeMethods.DragAcceptFiles(_hwnd, true);
    }

    private static void EnsureClassRegistered()
    {
        lock (ClassGate)
        {
            if (_classRegistered)
            {
                return;
            }

            var windowClass = new NativeMethods.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                style = NativeMethods.CS_DBLCLKS,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(SharedWndProc),
                hInstance = NativeMethods.GetModuleHandle(null),
                hCursor = NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_ARROW),
                lpszClassName = ClassName,
            };

            ushort atom = NativeMethods.RegisterClassEx(ref windowClass);
            if (atom == 0 && Marshal.GetLastWin32Error() != 1410) // 1410 = 类已存在
            {
                throw new InvalidOperationException($"注册贴图窗口类失败（Win32 {Marshal.GetLastWin32Error()}）。");
            }

            _classRegistered = true;
        }
    }

    private static IntPtr WindowProcedure(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            IntPtr data = NativeMethods.GetWindowLongAuto(hwnd, NativeMethods.GWLP_USERDATA);
            if (data != IntPtr.Zero && GCHandle.FromIntPtr(data).Target is PinWindow pin)
            {
                return pin.HandleMessage(msg, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            Log.Error("贴图窗口消息处理异常", ex);
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private IntPtr HandleMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_NCHITTEST:
                return (IntPtr)HitTest(lParam);

            case NativeMethods.WM_SETCURSOR:
                NativeMethods.SetCursor(NativeMethods.IDC_SIZEALL);
                return (IntPtr)1;

            case NativeMethods.WM_ERASEBKGND:
                return (IntPtr)1;

            case NativeMethods.WM_LBUTTONDOWN:
                NativeMethods.SetFocus(_hwnd);
                BeginDrag(CursorPosition());
                return IntPtr.Zero;

            case NativeMethods.WM_LBUTTONDBLCLK:
                HandleDoubleClick();
                return IntPtr.Zero;

            case NativeMethods.WM_MOUSEMOVE:
                if (_dragging)
                {
                    DragTo(CursorPosition());
                }

                return IntPtr.Zero;

            case NativeMethods.WM_LBUTTONUP:
                EndDrag();
                return IntPtr.Zero;

            case NativeMethods.WM_CAPTURECHANGED:
                _dragging = false;
                return IntPtr.Zero;

            case NativeMethods.WM_RBUTTONUP:
                ShowContextMenu();
                return IntPtr.Zero;

            case NativeMethods.WM_MOUSEWHEEL:
                HandleWheel(NativeMethods.HighWord(wParam), new PhysicalPoint(NativeMethods.LowWord(lParam), NativeMethods.HighWord(lParam)));
                return IntPtr.Zero;

            case NativeMethods.WM_KEYDOWN:
            case NativeMethods.WM_SYSKEYDOWN:
                return HandleKey((int)wParam) ? IntPtr.Zero : NativeMethods.DefWindowProc(_hwnd, msg, wParam, lParam);

            case NativeMethods.WM_DROPFILES:
                HandleDropFiles(wParam);
                return IntPtr.Zero;

            case NativeMethods.WM_CLOSE:
                Close();
                return IntPtr.Zero;

            case NativeMethods.WM_DESTROY:
                ReleaseHandleResources();
                return IntPtr.Zero;

            default:
                return NativeMethods.DefWindowProc(_hwnd, msg, wParam, lParam);
        }
    }

    private int HitTest(IntPtr lParam)
    {
        var point = new NativeMethods.POINT
        {
            X = NativeMethods.LowWord(lParam),
            Y = NativeMethods.HighWord(lParam),
        };

        if (!NativeMethods.ScreenToClient(_hwnd, ref point))
        {
            return NativeMethods.HTCLIENT;
        }

        // 阴影区（含 2px 误差）不接收点击：分层窗口的 alpha 命中测试会把半透明阴影也算作可点区域，
        // 不显式排除的话贴图周围会有一圈"隐形挡板"。
        const int tolerance = 2;
        bool insideImage = point.X >= ShadowPad - tolerance
                           && point.Y >= ShadowPad - tolerance
                           && point.X < _surface.Width - ShadowPad + tolerance
                           && point.Y < _surface.Height - ShadowPad + tolerance;

        return insideImage ? NativeMethods.HTCLIENT : NativeMethods.HTTRANSPARENT;
    }

    private void ReleaseHandleResources()
    {
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

        _hwnd = IntPtr.Zero;
    }

    // ---------------------------------------------------------------- 状态与渲染

    /// <summary>应用新的贴图状态：按需重新合成像素、移动窗口、更新透明度与穿透。</summary>
    internal void ApplyState(PinState next, bool recompose = true)
    {
        var previous = State;
        State = next;

        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (recompose && RequiresRecompose(previous, next))
        {
            Recompose();
        }
        else
        {
            if (previous.Opacity != next.Opacity)
            {
                CommitSurface();
            }

            SyncWindow(forceShow: false);
        }

        ApplyClickThrough();
    }

    private static bool RequiresRecompose(PinState previous, PinState next) =>
        previous.Rotation != next.Rotation
        || previous.FlipHorizontal != next.FlipHorizontal
        || previous.FlipVertical != next.FlipVertical
        || Math.Abs(previous.Zoom - next.Zoom) > 1e-9
        || previous.Thumbnail != next.Thumbnail
        || PinGeometry.Bounds(previous).Width != PinGeometry.Bounds(next).Width
        || PinGeometry.Bounds(previous).Height != PinGeometry.Bounds(next).Height;

    /// <summary>旋转 / 缩放 / 翻转后按物理像素 1:1 重新合成贴图画面。</summary>
    private void Recompose()
    {
        var bounds = PinGeometry.Bounds(State);

        if (State.Rotation == PinRotation.None)
        {
            // 直接按显示尺寸缩放原图（WPF 负责高质量重采样）
            DrawAndUpload(_source, bounds.Width, bounds.Height, PinGeometry.EffectiveZoom(State));
            return;
        }

        var rotated = new TransformedBitmap(_source, new RotateTransform(PinGeometry.RotationDegrees(State)));
        rotated.Freeze();
        DrawAndUpload(rotated, bounds.Width, bounds.Height, PinGeometry.EffectiveZoom(State));
    }

    private void DrawAndUpload(BitmapSource content, int imageWidth, int imageHeight, double zoom)
    {
        imageWidth = Math.Max(imageWidth, 1);
        imageHeight = Math.Max(imageHeight, 1);

        int totalWidth = imageWidth + (ShadowPad * 2);
        int totalHeight = imageHeight + (ShadowPad * 2);

        var visual = new DrawingVisual();
        // 放大到 3 倍以上用最近邻：像素级内容（截图/取色）放大后不该被插值糊掉
        RenderOptions.SetBitmapScalingMode(visual, zoom >= 3 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

        using (DrawingContext dc = visual.RenderOpen())
        {
            DrawShadow(dc, imageWidth, imageHeight);

            dc.PushTransform(new TranslateTransform(ShadowPad, ShadowPad));

            bool flipped = State.FlipHorizontal || State.FlipVertical;
            if (flipped)
            {
                dc.PushTransform(new ScaleTransform(
                    State.FlipHorizontal ? -1 : 1,
                    State.FlipVertical ? -1 : 1,
                    imageWidth / 2.0,
                    imageHeight / 2.0));
            }

            dc.DrawImage(content, new Rect(0, 0, imageWidth, imageHeight));

            if (flipped)
            {
                dc.Pop();
            }

            // 1px 描边：浅色图上也能看清贴图边界
            var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00)), 1);
            borderPen.Freeze();
            dc.DrawRectangle(null, borderPen, new Rect(0.5, 0.5, imageWidth - 1, imageHeight - 1));

            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(totalWidth, totalHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        int stride = totalWidth * 4;
        var pixels = new byte[(long)stride * totalHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        _surface.EnsureSize(totalWidth, totalHeight);
        _surface.WritePixels(pixels);
        CommitSurface();
    }

    /// <summary>用三层递减的半透明圆角矩形模拟柔和阴影（WPF 无高斯模糊效果，且此处不值得引入位图特效）。</summary>
    private static void DrawShadow(DrawingContext dc, int imageWidth, int imageHeight)
    {
        for (int i = 3; i >= 1; i--)
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)(8 * (4 - i)), 0, 0, 0));
            brush.Freeze();
            double inflate = i + 1;
            dc.DrawRoundedRectangle(
                brush,
                null,
                new Rect(
                    ShadowPad - inflate + 1,
                    ShadowPad - inflate + 2,
                    imageWidth + (inflate * 2),
                    imageHeight + (inflate * 2)),
                inflate + 1,
                inflate + 1);
        }
    }

    private void CommitSurface()
    {
        var bounds = PinGeometry.Bounds(State);
        _surface.Commit(_hwnd, new PhysicalPoint(bounds.X - ShadowPad, bounds.Y - ShadowPad), State.Opacity);
        _committedOpacity = State.Opacity;
    }

    /// <summary>只移动/显示窗口，不重新上传像素（拖动时必须走这条路径，否则每帧上传几 MB）。</summary>
    private void SyncWindow(bool forceShow)
    {
        var bounds = PinGeometry.Bounds(State);
        int width = bounds.Width + (ShadowPad * 2);
        int height = bounds.Height + (ShadowPad * 2);

        uint flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER;

        if (!IsHidden && (forceShow || !NativeMethods.IsWindowVisible(_hwnd)))
        {
            flags |= NativeMethods.SWP_SHOWWINDOW;
        }

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            bounds.X - ShadowPad,
            bounds.Y - ShadowPad,
            width,
            height,
            flags);

        if (IsHidden && NativeMethods.IsWindowVisible(_hwnd))
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        }
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        uint exStyle = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
        if (State.ClickThrough)
        {
            exStyle |= NativeMethods.WS_EX_TRANSPARENT;
        }

        if (exStyle == _appliedExStyle)
        {
            return;
        }

        NativeMethods.SetWindowLongAuto(_hwnd, NativeMethods.GWL_EXSTYLE, (IntPtr)exStyle);
        _appliedExStyle = exStyle;

        // 官方建议：改动扩展样式后重画一次，让分层窗口的命中测试区域立即更新
        CommitSurface();
    }

    // ---------------------------------------------------------------- 用户操作

    internal void BeginDrag(PhysicalPoint cursor)
    {
        _dragging = true;
        _dragCursorOrigin = cursor;
        _dragPositionOrigin = State.Position;
        NativeMethods.SetCapture(_hwnd);
    }

    internal void DragTo(PhysicalPoint cursor)
    {
        if (!_dragging)
        {
            return;
        }

        // 拖动过程不做吸附与限位（跟手优先），松手时再吸附
        State = State with
        {
            Position = new PhysicalPoint(
                _dragPositionOrigin.X + (cursor.X - _dragCursorOrigin.X),
                _dragPositionOrigin.Y + (cursor.Y - _dragCursorOrigin.Y)),
        };

        SyncWindow(forceShow: false);
    }

    internal void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        NativeMethods.ReleaseCapture();

        var next = State;

        if (Settings.PinSnapToEdges)
        {
            next = PinGeometry.SnapToEdges(next, _manager.Monitors, Settings.PinSnapTolerance, out _, out _);
        }

        next = PinGeometry.EnsureVisible(next, _manager.Desktop, 8);
        ApplyState(next, recompose: false);
        RaiseChanged(backup: true);
    }

    private void HandleDoubleClick()
    {
        bool shift = IsKeyDown(NativeMethods.VK_SHIFT);

        if (shift)
        {
            SetThumbnail(!State.Thumbnail);
        }
        else
        {
            Hide();
        }
    }

    private void HandleWheel(int delta, PhysicalPoint cursor)
    {
        int steps = delta / NativeMethods.WHEEL_DELTA;
        if (steps == 0)
        {
            return;
        }

        if (IsKeyDown(NativeMethods.VK_CONTROL))
        {
            int percentPerStep = IsKeyDown(NativeMethods.VK_SHIFT) ? 20 : 5;
            SetOpacity(State.Opacity + (steps * percentPerStep));
            _manager.ReportStatus($"贴图不透明度 {State.Opacity}%");
            return;
        }

        Zoom(steps, cursor);
    }

    /// <summary>
    /// 贴图窗口内按键同 <see cref="HotkeyScope.Pin"/> 的配置绑定。
    /// 方向键微调与 +/- 缩放是固定交互（不属于可配置动作），其余全部走键位表。
    /// </summary>
    internal bool HandleKey(int virtualKey)
    {
        switch (virtualKey)
        {
            case 0x25: // Left
            case 0x26: // Up
            case 0x27: // Right
            case 0x28: // Down
                Nudge(virtualKey);
                return true;

            case 0xBB: // '+'
            case 0x6B: // 小键盘 +
                Zoom(1, PinGeometry.Center(State));
                return true;

            case 0xBD: // '-'
            case 0x6D: // 小键盘 -
                Zoom(-1, PinGeometry.Center(State));
                return true;

            case 0x1B: // Esc：隐藏是"通用逃生键"，固定保留（可配置的是 H）
                Hide();
                return true;
        }

        var spec = Services.KeySpecFactory.FromVirtualKey(virtualKey);
        var action = HotkeyBindings.Match(_manager.KeyBindings, spec, HotkeyScope.Pin);
        if (action is null)
        {
            return false;
        }

        switch (action.Id)
        {
            case "pin.copy":
                CopyToClipboard();
                return true;

            case "pin.saveAs":
                SaveAs();
                return true;

            case "pin.annotate":
                AnnotationRequested?.Invoke(this);
                return true;

            case "pin.rotateCw":
                Rotate(clockwise: true);
                return true;

            case "pin.rotateCcw":
                Rotate(clockwise: false);
                return true;

            case "pin.flipH":
                Flip(horizontal: true);
                return true;

            case "pin.flipV":
                Flip(horizontal: false);
                return true;

            case "pin.resetZoom":
                ApplyState(PinGeometry.ResetZoom(State));
                RaiseChanged(backup: true);
                return true;

            case "pin.thumbnail":
                SetThumbnail(!State.Thumbnail);
                return true;

            case "pin.clickThrough":
                ToggleClickThrough();
                return true;

            case "pin.hide":
                Hide();
                return true;

            case "pin.close":
                Close();
                return true;

            default:
                return false;
        }
    }

    private void Nudge(int virtualKey)
    {
        int step = IsKeyDown(NativeMethods.VK_SHIFT) ? 10 : 1;
        var (dx, dy) = virtualKey switch
        {
            0x25 => (-step, 0),
            0x27 => (step, 0),
            0x26 => (0, -step),
            _ => (0, step),
        };

        var next = PinGeometry.EnsureVisible(PinGeometry.MoveBy(State, dx, dy), _manager.Desktop, 8);
        ApplyState(next, recompose: false);
        RaiseChanged(backup: true);
    }

    internal void Zoom(int steps, PhysicalPoint anchor)
    {
        var next = PinGeometry.ZoomSteps(State, steps, anchor, Settings.PinZoomStep, _manager.Desktop);

        if (Math.Abs(next.Zoom - State.Zoom) < 1e-9)
        {
            _manager.ReportStatus(steps > 0 ? "已到最大缩放" : "已到最小缩放");
            return;
        }

        if (!CanCompose(next))
        {
            _manager.ReportStatus("贴图过大，无法再放大（避免占满内存）。", warning: true);
            return;
        }

        ApplyState(next);
        _manager.ReportStatus($"贴图缩放 {PinGeometry.EffectiveZoom(next) * 100:0}%");
        RaiseChanged(backup: true);
    }

    internal void Rotate(bool clockwise)
    {
        ApplyState(clockwise ? PinGeometry.RotateClockwise(State) : PinGeometry.RotateCounterClockwise(State));
        _manager.ReportStatus($"贴图旋转 {(int)State.Rotation * 90}°");
        RaiseChanged(backup: true);
    }

    internal void Flip(bool horizontal)
    {
        ApplyState(horizontal ? PinGeometry.FlipHorizontally(State) : PinGeometry.FlipVertically(State));
        RaiseChanged(backup: true);
    }

    internal void SetOpacity(int opacity)
    {
        ApplyState(PinGeometry.SetOpacity(State, opacity));
        RaiseChanged(backup: true);
    }

    internal void SetThumbnail(bool thumbnail)
    {
        if (!CanCompose(PinGeometry.SetThumbnail(State, thumbnail)))
        {
            return;
        }

        ApplyState(PinGeometry.SetThumbnail(State, thumbnail));
        _manager.ReportStatus(State.Thumbnail ? "已切到缩略图" : "已恢复原尺寸");
        RaiseChanged(backup: true);
    }

    internal void ToggleClickThrough()
    {
        ApplyState(PinGeometry.SetClickThrough(State, !State.ClickThrough));
        _manager.ReportStatus(State.ClickThrough
            ? "贴图已开启鼠标穿透（用托盘菜单或管理器关闭）"
            : "贴图已关闭鼠标穿透");
        RaiseChanged(backup: true);
    }

    internal void Hide()
    {
        _hiddenByUser = true;
        ApplyState(State with { Hidden = true });
        _manager.ReportStatus("贴图已隐藏（托盘菜单可恢复）");
        RaiseChanged(backup: true);
    }

    internal void Show()
    {
        _hiddenByUser = false;
        ApplyState(State with { Hidden = false });
        SyncWindow(forceShow: true);
        RaiseChanged(backup: true);
    }

    internal void Close()
    {
        CloseRequested?.Invoke(this);
    }

    internal void BringToFront()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>显示器拓扑变化后重算位置与尺寸。</summary>
    internal void ReconcileTopology(PhysicalRect oldDesktop, PhysicalRect newDesktop)
    {
        var next = PinGeometry.ReconcileTopology(State, oldDesktop, newDesktop);

        if (next.Position == State.Position && next.Zoom == State.Zoom)
        {
            return;
        }

        ApplyState(next);
        RaiseChanged(backup: true);
    }

    internal void CopyToClipboard()
    {
        if (ClipboardService.TryCopy(_source, out string error))
        {
            _manager.ReportStatus("贴图已复制到剪贴板");
        }
        else
        {
            _manager.ReportStatus($"复制贴图失败：{error}", warning: true);
        }
    }

    /// <summary>
    /// 替换贴图内容（标注回写用）。原始尺寸随之更新，显示缩放保持不变，
    /// 并且会重新落备份——这样"标注后崩溃"也不会丢标注。
    /// </summary>
    internal void ReplaceSource(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var normalized = SaveService.Normalize(image);
        normalized.Freeze();

        _source = normalized;
        State = State with
        {
            SourceWidth = normalized.PixelWidth,
            SourceHeight = normalized.PixelHeight,
        };

        BackupFileName = null; // 内容变了，备份必须重新编码
        Recompose();
        RaiseChanged(backup: true);
    }

    internal void SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存贴图",
            FileName = SaveService.BuildFileName(Settings.FileNameTemplate, _source.PixelWidth, _source.PixelHeight, DateTime.Now),
            InitialDirectory = SaveService.ResolveDirectory(Settings),
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg|BMP 图片|*.bmp",
            DefaultExt = ".png",
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            long bytes = SaveService.SaveImage(_source, dialog.FileName, Settings.JpegQuality);
            _manager.ReportStatus($"贴图已保存 {Path.GetFileName(dialog.FileName)}（{bytes / 1024.0:0.#} KB）");
        }
        catch (Exception ex)
        {
            Log.Error("保存贴图失败", ex);
            _manager.ReportStatus($"保存贴图失败：{ex.Message}", warning: true);
        }
    }

    private void HandleDropFiles(IntPtr hDrop)
    {
        var files = new List<string>();
        uint count = NativeMethods.DragQueryFile(hDrop, uint.MaxValue, null, 0);

        for (uint i = 0; i < count; i++)
        {
            uint length = NativeMethods.DragQueryFile(hDrop, i, null, 0);
            var buffer = new System.Text.StringBuilder((int)length + 1);
            if (NativeMethods.DragQueryFile(hDrop, i, buffer, length + 1) > 0)
            {
                files.Add(buffer.ToString());
            }
        }

        NativeMethods.DragFinish(hDrop);

        if (files.Count > 0)
        {
            _manager.CreatePinsFromFiles(files, CursorPosition());
        }
    }

    private static bool IsKeyDown(int virtualKey) => (NativeMethods.GetKeyState(virtualKey) & 0x8000) != 0;

    internal static PhysicalPoint CursorPosition()
    {
        NativeMethods.GetCursorPos(out var point);
        return new PhysicalPoint(point.X, point.Y);
    }

    private AppSettings Settings => _manager.Settings;

    /// <summary>合成前的内存上限检查。</summary>
    private static bool CanCompose(PinState state)
    {
        var bounds = PinGeometry.Bounds(state);
        long bytes = (long)(bounds.Width + (ShadowPad * 2)) * (bounds.Height + (ShadowPad * 2)) * 4;
        return bytes <= MaxSurfaceBytes;
    }

    private void RaiseChanged(bool backup)
    {
        Changed?.Invoke(this);

        if (backup)
        {
            _manager.ScheduleBackup();
        }
    }

    // ---------------------------------------------------------------- 右键菜单

    private enum MenuCommand
    {
        Copy = 100,
        SaveAs,
        Annotate,
        ZoomIn,
        ZoomOut,
        ZoomReset,
        RotateClockwise,
        RotateCounterClockwise,
        FlipHorizontal,
        FlipVertical,
        Thumbnail,
        OpacityFull,
        Opacity80,
        Opacity60,
        Opacity40,
        Opacity20,
        ClickThrough,
        Hide,
        Close,
        HideAll,
        ShowAll,
        CloseAll,
    }

    /// <summary>
    /// 动作当前的键位文本（读 <see cref="PinManager.KeyBindings"/>）。
    /// 右键菜单必须用它而不是写死 Ctrl+C/Delete——贴图窗口的按键可配置，
    /// 写死会让改键后的菜单显示错的快捷键。
    /// </summary>
    private string KeyText(string actionId, string fallback)
    {
        var binding = _manager.KeyBindings
            .FirstOrDefault(b => string.Equals(b.ActionId, actionId, StringComparison.OrdinalIgnoreCase));

        return binding is { IsActive: true } ? binding.Keys!.Trim() : fallback;
    }

    private void ShowContextMenu()
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            Append(menu, MenuCommand.Copy, $"复制到剪贴板\t{KeyText("pin.copy", "Ctrl+C")}");
            Append(menu, MenuCommand.SaveAs, $"另存为…\t{KeyText("pin.saveAs", "Ctrl+S")}");
            Append(menu, MenuCommand.Annotate, $"贴图内标注\t{KeyText("pin.annotate", "空格")}");
            Append(menu, 0, null);

            Append(menu, MenuCommand.ZoomIn, "放大\t滚轮上");
            Append(menu, MenuCommand.ZoomOut, "缩小\t滚轮下");
            Append(menu, MenuCommand.ZoomReset, $"恢复 100%\t{KeyText("pin.resetZoom", "0")}");
            Append(menu, 0, null);

            Append(menu, MenuCommand.RotateClockwise, $"顺时针旋转 90°\t{KeyText("pin.rotateCw", "1")}");
            Append(menu, MenuCommand.RotateCounterClockwise, $"逆时针旋转 90°\t{KeyText("pin.rotateCcw", "2")}");
            Append(menu, MenuCommand.FlipHorizontal, $"水平翻转\t{KeyText("pin.flipH", "3")}", State.FlipHorizontal);
            Append(menu, MenuCommand.FlipVertical, $"垂直翻转\t{KeyText("pin.flipV", "4")}", State.FlipVertical);
            Append(menu, 0, null);

            Append(menu, MenuCommand.Thumbnail, $"缩略图模式\t{KeyText("pin.thumbnail", "T")}", State.Thumbnail);
            Append(menu, MenuCommand.OpacityFull, "不透明度 100%", State.Opacity == 100);
            Append(menu, MenuCommand.Opacity80, "不透明度 80%", State.Opacity == 80);
            Append(menu, MenuCommand.Opacity60, "不透明度 60%", State.Opacity == 60);
            Append(menu, MenuCommand.Opacity40, "不透明度 40%", State.Opacity == 40);
            Append(menu, MenuCommand.Opacity20, "不透明度 20%", State.Opacity == 20);
            Append(menu, MenuCommand.ClickThrough, $"鼠标穿透\t{KeyText("pin.clickThrough", "P")}", State.ClickThrough);
            Append(menu, 0, null);

            Append(menu, MenuCommand.Hide, $"隐藏\t{KeyText("pin.hide", "H")}");
            Append(menu, MenuCommand.Close, $"关闭这张贴图\t{KeyText("pin.close", "Delete")}");
            Append(menu, 0, null);
            Append(menu, MenuCommand.ShowAll, "显示全部贴图");
            Append(menu, MenuCommand.HideAll, "隐藏全部贴图");
            Append(menu, MenuCommand.CloseAll, "关闭全部贴图");

            var cursor = CursorPosition();
            NativeMethods.SetForegroundWindow(_hwnd);

            int command = NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY,
                cursor.X,
                cursor.Y,
                0,
                _hwnd,
                IntPtr.Zero);

            if (command > 0)
            {
                Execute((MenuCommand)command);
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private static void Append(IntPtr menu, MenuCommand command, string? text, bool check = false)
    {
        if (command == 0)
        {
            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);
            return;
        }

        uint flags = NativeMethods.MF_STRING;
        if (check)
        {
            flags |= NativeMethods.MF_CHECKED;
        }

        NativeMethods.AppendMenu(menu, flags, (IntPtr)(int)command, text);
    }

    private void Execute(MenuCommand command)
    {
        switch (command)
        {
            case MenuCommand.Copy:
                CopyToClipboard();
                break;
            case MenuCommand.SaveAs:
                SaveAs();
                break;
            case MenuCommand.Annotate:
                AnnotationRequested?.Invoke(this);
                break;
            case MenuCommand.ZoomIn:
                Zoom(1, PinGeometry.Center(State));
                break;
            case MenuCommand.ZoomOut:
                Zoom(-1, PinGeometry.Center(State));
                break;
            case MenuCommand.ZoomReset:
                ApplyState(PinGeometry.ResetZoom(State));
                RaiseChanged(backup: true);
                break;
            case MenuCommand.RotateClockwise:
                Rotate(clockwise: true);
                break;
            case MenuCommand.RotateCounterClockwise:
                Rotate(clockwise: false);
                break;
            case MenuCommand.FlipHorizontal:
                Flip(horizontal: true);
                break;
            case MenuCommand.FlipVertical:
                Flip(horizontal: false);
                break;
            case MenuCommand.Thumbnail:
                SetThumbnail(!State.Thumbnail);
                break;
            case MenuCommand.OpacityFull:
                SetOpacity(100);
                break;
            case MenuCommand.Opacity80:
                SetOpacity(80);
                break;
            case MenuCommand.Opacity60:
                SetOpacity(60);
                break;
            case MenuCommand.Opacity40:
                SetOpacity(40);
                break;
            case MenuCommand.Opacity20:
                SetOpacity(20);
                break;
            case MenuCommand.ClickThrough:
                ToggleClickThrough();
                break;
            case MenuCommand.Hide:
                Hide();
                break;
            case MenuCommand.Close:
                Close();
                break;
            case MenuCommand.ShowAll:
                _manager.ShowAll();
                break;
            case MenuCommand.HideAll:
                _manager.HideAll();
                break;
            case MenuCommand.CloseAll:
                _manager.CloseAll();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            IntPtr handle = _hwnd;
            _hwnd = IntPtr.Zero;
            NativeMethods.DestroyWindow(handle);
        }

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

        _surface.Dispose();
    }
}
