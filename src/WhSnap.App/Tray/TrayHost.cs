using System.IO;
using WhSnap.Services;

namespace WhSnap.Tray;

/// <summary>托盘图标与菜单。所有事件都在 WPF UI 线程上触发。</summary>
internal sealed class TrayHost : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ToolStripMenuItem _captureItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _autoStartItem;
    private bool _suppressAutoStartEvent;

    public TrayHost(string hotkeyText, bool autoStartEnabled)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip { ShowImageMargin = false };

        _captureItem = new System.Windows.Forms.ToolStripMenuItem($"截图\t{hotkeyText}");
        _captureItem.Click += (_, _) => CaptureRequested?.Invoke();

        var openSaveFolder = new System.Windows.Forms.ToolStripMenuItem("打开保存目录");
        openSaveFolder.Click += (_, _) => OpenSaveFolderRequested?.Invoke();

        var openConfigFolder = new System.Windows.Forms.ToolStripMenuItem("打开配置目录");
        openConfigFolder.Click += (_, _) => OpenConfigFolderRequested?.Invoke();

        _autoStartItem = new System.Windows.Forms.ToolStripMenuItem("开机自启")
        {
            CheckOnClick = true,
            Checked = autoStartEnabled,
        };
        _autoStartItem.CheckedChanged += (_, _) =>
        {
            if (!_suppressAutoStartEvent)
            {
                AutoStartToggled?.Invoke(_autoStartItem.Checked);
            }
        };

        var about = new System.Windows.Forms.ToolStripMenuItem("关于 WhSnap");
        about.Click += (_, _) => AboutRequested?.Invoke();

        var exit = new System.Windows.Forms.ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(_captureItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(openSaveFolder);
        menu.Items.Add(openConfigFolder);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(about);
        menu.Items.Add(exit);

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "WhSnap — 截图贴图工具",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => CaptureRequested?.Invoke();
    }

    public event Action? CaptureRequested;

    public event Action? ExitRequested;

    public event Action<bool>? AutoStartToggled;

    public event Action? OpenSaveFolderRequested;

    public event Action? OpenConfigFolderRequested;

    public event Action? AboutRequested;

    public void UpdateHotkeyText(string hotkeyText) => _captureItem.Text = $"截图\t{hotkeyText}";

    public void SyncAutoStart(bool enabled)
    {
        _suppressAutoStartEvent = true;
        _autoStartItem.Checked = enabled;
        _suppressAutoStartEvent = false;
    }

    public void ShowBalloon(string title, string text, bool warning = false)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(
                3000,
                title,
                text,
                warning ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Warn($"气泡通知失败：{ex.Message}");
        }
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "whsnap.ico");
            if (File.Exists(path))
            {
                return new System.Drawing.Icon(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"托盘图标加载失败：{ex.Message}");
        }

        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        catch
        {
            // 退出路径上的异常一律吞掉
        }
    }
}
