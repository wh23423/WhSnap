using System.Windows.Media.Imaging;
using WhSnap.Core;
using WhSnap.Overlay;
using WhSnap.Services;

namespace WhSnap.Capture;

/// <summary>
/// 截图会话编排：冻结屏幕 → 选定活动显示器 → 弹遮罩 → 确认后按动作复制/保存/贴图。
/// 同一时刻只允许一个会话（重复按热键不会叠加遮罩）。
/// </summary>
internal sealed class CaptureController(SettingsService settings)
{
    private readonly SettingsService _settings = settings;
    private OverlaySession? _overlay;

    public event Action<string, bool>? StatusReported;

    /// <summary>用户按了「进入标注」：宿主打开标注编辑器，完成后回调 <see cref="CompleteConfirmed"/>。</summary>
    public event Action<BitmapSource, PhysicalRect>? AnnotateRequested;

    /// <summary>贴图回调（由 App 注入 <c>PinManager.CreatePin</c>）。返回 false 表示贴图失败。</summary>
    public Func<BitmapSource, bool>? PinRequested { get; set; }

    /// <summary>遮罩内的按键绑定（来自设置，改键后宿主会更新）。</summary>
    public IReadOnlyList<HotkeyBinding> KeyBindings { get; set; } = HotkeyBindings.Resolve(scope: HotkeyScope.Overlay);

    public bool IsBusy => _overlay is not null;

    public void Start()
    {
        if (_overlay is not null)
        {
            Log.Info("已有截图会话在进行，忽略本次触发。");
            return;
        }

        try
        {
            var backend = CaptureBackendNames.Parse(_settings.Current.CaptureBackend);
            var frame = ScreenCaptureFactory.Capture(backend, out var diagnostics);
            var monitor = MonitorEnumerator.GetMonitorAtCursor(frame.Monitors);

            Log.Info($"冻结屏幕：后端 {frame.Backend}，虚拟桌面 {frame.VirtualBounds}，活动显示器 {monitor}，共 {frame.Monitors.Count} 台，耗时 {frame.Duration.TotalMilliseconds:0} ms");

            foreach (string line in diagnostics)
            {
                Log.Info($"采集尝试：{line}");
            }

            if (!string.IsNullOrWhiteSpace(frame.Notes))
            {
                Log.Info($"采集说明：{frame.Notes}");
            }

            if (frame.ProtectedContentMasked)
            {
                Log.Warn("检测到受保护内容被系统屏蔽，截图可能包含黑块。");
            }

            var session = new OverlaySession(frame, monitor, _settings.Current, KeyBindings);
            _overlay = session;

            session.Confirmed += (image, rect, action) =>
            {
                _overlay = null;
                HandleConfirmed(image, rect, action);
            };

            session.Cancelled += () =>
            {
                _overlay = null;
                Log.Info("截图已取消。");
            };

            session.AnnotateRequested += (image, rect) =>
            {
                _overlay = null;
                AnnotateRequested?.Invoke(image, rect);
            };

            session.Show();
        }
        catch (Exception ex)
        {
            _overlay = null;
            Log.Error("截图失败", ex);
            StatusReported?.Invoke($"截图失败：{ex.Message}", true);
        }
    }

    /// <summary>
    /// 走完"确认后动作"（复制 / 保存 / 贴图）。标注编辑器确认后由宿主调用它，
    /// 这样"标注后确认"和"直接确认"永远是同一套行为。
    /// </summary>
    public void CompleteConfirmed(BitmapSource image, PhysicalRect rect, OverlayConfirmAction action = OverlayConfirmAction.Default) =>
        HandleConfirmed(image, rect, action);

    private void HandleConfirmed(BitmapSource image, PhysicalRect rect, OverlayConfirmAction action)
    {
        bool copy = action switch
        {
            OverlayConfirmAction.CopyOnly => true,
            OverlayConfirmAction.SaveOnly or OverlayConfirmAction.PinOnly => false,
            _ => _settings.Current.CopyOnCapture,
        };

        bool save = action switch
        {
            OverlayConfirmAction.SaveOnly => true,
            OverlayConfirmAction.CopyOnly or OverlayConfirmAction.PinOnly => false,
            _ => _settings.Current.SaveOnCapture,
        };

        bool pin = action switch
        {
            OverlayConfirmAction.PinOnly => true,
            OverlayConfirmAction.CopyOnly or OverlayConfirmAction.SaveOnly => false,
            _ => _settings.Current.PinOnCapture,
        };

        var parts = new List<string>();

        if (pin)
        {
            if (PinRequested is { } handler && handler(image))
            {
                parts.Add("已贴到屏幕");
            }
            else
            {
                parts.Add("贴图失败");
                Log.Warn("截图确认后贴图失败（未注入贴图回调或贴图窗口创建失败）。");
            }
        }

        if (copy)
        {
            if (ClipboardService.TryCopy(image, out string copyError))
            {
                parts.Add("已复制到剪贴板");
            }
            else
            {
                parts.Add($"复制失败（{copyError}）");
                Log.Warn($"复制到剪贴板失败：{copyError}");
            }
        }

        if (save)
        {
            try
            {
                string path = SaveService.SavePng(image, _settings.Current, out long bytes);
                parts.Add($"已保存 {Path.GetFileName(path)}（{bytes / 1024.0:0.#} KB）");
                Log.Info($"截图已保存：{path}（{rect.Width}x{rect.Height}）");
            }
            catch (Exception ex)
            {
                parts.Add($"保存失败（{ex.Message}）");
                Log.Error("保存截图失败", ex);
            }
        }

        if (parts.Count == 0)
        {
            parts.Add("未执行任何动作（复制 / 保存 / 贴图都已关闭）");
        }

        StatusReported?.Invoke(string.Join("；", parts), false);
    }
}
