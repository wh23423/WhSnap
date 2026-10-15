using System.Windows.Media.Imaging;
using WhSnap.Core;
using WhSnap.Overlay;
using WhSnap.Services;

namespace WhSnap.Capture;

/// <summary>
/// 截图会话编排：冻结屏幕 → 选定活动显示器 → 弹遮罩 → 确认后复制/保存。
/// 同一时刻只允许一个会话（重复按热键不会叠加遮罩）。
/// </summary>
internal sealed class CaptureController(SettingsService settings)
{
    private readonly SettingsService _settings = settings;
    private OverlayWindow? _overlay;

    public event Action<string, bool>? StatusReported;

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

            var overlay = new OverlayWindow(frame, monitor, _settings.Current);
            _overlay = overlay;

            overlay.SelectionConfirmed += (image, rect) =>
            {
                _overlay = null;
                HandleConfirmed(image, rect);
            };

            overlay.Cancelled += () =>
            {
                _overlay = null;
                Log.Info("截图已取消。");
            };

            overlay.Closed += (_, _) => _overlay = null;

            overlay.Show();
            overlay.Activate();
        }
        catch (Exception ex)
        {
            _overlay = null;
            Log.Error("截图失败", ex);
            StatusReported?.Invoke($"截图失败：{ex.Message}", true);
        }
    }

    private void HandleConfirmed(BitmapSource image, PhysicalRect rect)
    {
        var parts = new List<string>();

        if (_settings.Current.CopyOnCapture)
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

        if (_settings.Current.SaveOnCapture)
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
            parts.Add("未执行任何动作（复制与保存都已关闭）");
        }

        StatusReported?.Invoke(string.Join("；", parts), false);
    }
}
