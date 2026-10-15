using System.IO;
using WhSnap.Services;

namespace WhSnap.Tray;

/// <summary>托盘图标与菜单。所有事件都在 WPF UI 线程上触发。</summary>
internal sealed class TrayHost : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ToolStripMenuItem _captureItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _autoStartItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _pinItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _clipboardPinItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _panelItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _recentColorsItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _checkUpdatesItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _exportDiagnosticsItem;
    private bool _suppressAutoStartEvent;

    public TrayHost(string hotkeyText, bool autoStartEnabled)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip { ShowImageMargin = false };

        _captureItem = new System.Windows.Forms.ToolStripMenuItem($"截图\t{hotkeyText}");
        _captureItem.Click += (_, _) => CaptureRequested?.Invoke();

        _clipboardPinItem = new System.Windows.Forms.ToolStripMenuItem("从剪贴板贴图");
        _clipboardPinItem.Click += (_, _) => ClipboardPinRequested?.Invoke();

        _panelItem = new System.Windows.Forms.ToolStripMenuItem("贴图管理…");
        _panelItem.Click += (_, _) => ManagePinsRequested?.Invoke();

        _pinItem = new System.Windows.Forms.ToolStripMenuItem("显示全部贴图");
        _pinItem.Click += (_, _) => ShowAllPinsRequested?.Invoke();

        var hideAllPins = new System.Windows.Forms.ToolStripMenuItem("隐藏全部贴图");
        hideAllPins.Click += (_, _) => HideAllPinsRequested?.Invoke();

        var closeAllPins = new System.Windows.Forms.ToolStripMenuItem("关闭全部贴图");
        closeAllPins.Click += (_, _) => CloseAllPinsRequested?.Invoke();

        var openSaveFolder = new System.Windows.Forms.ToolStripMenuItem("打开保存目录");
        openSaveFolder.Click += (_, _) => OpenSaveFolderRequested?.Invoke();

        var openConfigFolder = new System.Windows.Forms.ToolStripMenuItem("打开配置目录");
        openConfigFolder.Click += (_, _) => OpenConfigFolderRequested?.Invoke();

        var openPinBackupFolder = new System.Windows.Forms.ToolStripMenuItem("打开贴图备份目录");
        openPinBackupFolder.Click += (_, _) => OpenPinBackupFolderRequested?.Invoke();

        var settings = new System.Windows.Forms.ToolStripMenuItem("设置…");
        settings.Click += (_, _) => SettingsRequested?.Invoke();

        _checkUpdatesItem = new System.Windows.Forms.ToolStripMenuItem("检查更新…");
        _checkUpdatesItem.Click += (_, _) => CheckUpdatesRequested?.Invoke();

        _exportDiagnosticsItem = new System.Windows.Forms.ToolStripMenuItem("导出诊断包…");
        _exportDiagnosticsItem.Click += (_, _) => ExportDiagnosticsRequested?.Invoke();

        var picker = new System.Windows.Forms.ToolStripMenuItem("取色器（取色并复制）");
        picker.Click += (_, _) => PickerRequested?.Invoke();

        _recentColorsItem = new System.Windows.Forms.ToolStripMenuItem("最近颜色（空）")
        {
            Enabled = false,
        };

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
        menu.Items.Add(_clipboardPinItem);
        menu.Items.Add(_panelItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(picker);
        menu.Items.Add(_recentColorsItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_pinItem);
        menu.Items.Add(hideAllPins);
        menu.Items.Add(closeAllPins);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(openSaveFolder);
        menu.Items.Add(openPinBackupFolder);
        menu.Items.Add(openConfigFolder);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(settings);
        menu.Items.Add(_checkUpdatesItem);
        menu.Items.Add(_exportDiagnosticsItem);
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

    public event Action? ClipboardPinRequested;

    public event Action? ManagePinsRequested;

    public event Action? ShowAllPinsRequested;

    public event Action? HideAllPinsRequested;

    public event Action? CloseAllPinsRequested;

    public event Action? ExitRequested;

    public event Action<bool>? AutoStartToggled;

    public event Action? OpenSaveFolderRequested;

    public event Action? OpenConfigFolderRequested;

    public event Action? OpenPinBackupFolderRequested;

    public event Action? AboutRequested;

    public event Action? SettingsRequested;

    /// <summary>托盘「检查更新…」：手动检查一次（含下载与安装确认）。</summary>
    public event Action? CheckUpdatesRequested;

    /// <summary>托盘「导出诊断包…」：一键打包日志/配置/拓扑/崩溃证据。</summary>
    public event Action? ExportDiagnosticsRequested;

    /// <summary>托盘「取色器」：开始一次只取色的截图会话。</summary>
    public event Action? PickerRequested;

    /// <summary>托盘「最近颜色」里选中了某个颜色（宿主负责按设置格式复制）。</summary>
    public event Action<WhSnap.Core.RgbColor>? RecentColorSelected;

    /// <summary>托盘「最近颜色」里的「清空历史」。</summary>
    public event Action? ClearColorHistoryRequested;

    public void UpdateHotkeyText(string hotkeyText) => _captureItem.Text = $"截图\t{hotkeyText}";

    /// <summary>自检用：「检查更新…」菜单项的当前文案（用户可达性的机器证据）。</summary>
    internal string CheckUpdatesItemText => _checkUpdatesItem.Text ?? string.Empty;

    /// <summary>自检用：模拟点击「检查更新…」，验证菜单项确实把事件抛给宿主。</summary>
    internal void SimulateCheckUpdatesClick() => _checkUpdatesItem.PerformClick();

    /// <summary>自检用：「导出诊断包…」菜单项的当前文案。</summary>
    internal string ExportDiagnosticsItemText => _exportDiagnosticsItem.Text ?? string.Empty;

    /// <summary>自检用：模拟点击「导出诊断包…」。</summary>
    internal void SimulateExportDiagnosticsClick() => _exportDiagnosticsItem.PerformClick();

    /// <summary>贴图数量与剪贴板监听状态直接写在菜单上，避免"不知道现在有几张贴图"。</summary>
    public void SyncPinState(int pinCount, bool clipboardWatcherRunning)
    {
        _panelItem.Text = $"贴图管理…（{pinCount} 张）";
        _pinItem.Text = pinCount == 0 ? "显示全部贴图" : $"显示全部贴图（{pinCount}）";
        _panelItem.Enabled = pinCount > 0;
        _clipboardPinItem.Text = clipboardWatcherRunning ? "从剪贴板贴图　（监听中）" : "从剪贴板贴图";
    }

    /// <summary>刷新「最近颜色」子菜单（取色后即时更新）。</summary>
    public void SyncRecentColors(IReadOnlyList<WhSnap.Core.RgbColor> colors)
    {
        var dropDown = _recentColorsItem.DropDownItems;
        dropDown.Clear();

        _recentColorsItem.Text = colors.Count == 0 ? "最近颜色（空）" : $"最近颜色（{colors.Count}）";
        _recentColorsItem.Enabled = colors.Count > 0;

        foreach (var color in colors.Take(12))
        {
            var item = new System.Windows.Forms.ToolStripMenuItem($"{color.ToHex().ToUpperInvariant()}　rgb({color.R}, {color.G}, {color.B})")
            {
                Tag = color,
                ForeColor = System.Drawing.Color.FromArgb(color.R, color.G, color.B),
            };

            item.Click += (_, _) => RecentColorSelected?.Invoke((WhSnap.Core.RgbColor)item.Tag);
            dropDown.Add(item);
        }

        if (colors.Count > 0)
        {
            dropDown.Add(new System.Windows.Forms.ToolStripSeparator());
            var clear = new System.Windows.Forms.ToolStripMenuItem("清空颜色历史");
            clear.Click += (_, _) => ClearColorHistoryRequested?.Invoke();
            dropDown.Add(clear);
        }
    }

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
