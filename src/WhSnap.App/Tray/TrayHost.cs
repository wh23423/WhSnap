using System.IO;
using WhSnap.Core;
using WhSnap.Services;

namespace WhSnap.Tray;

/// <summary>
/// 托盘图标与菜单。所有事件都在 WPF UI 线程上触发。
///
/// 视觉落地 design/focal/surfaces/tray.html：obsidian-800 菜单面 · 1px 发丝边 · 圆角 14 ·
/// 行高 28 · 内边距 8 · 14px 文本 · 悬停 = 琥珀底 + 墨字 · 快捷键/数量右对齐成等宽第二列 ·
/// 危险项（退出）走 SignalDanger · 细分隔线 · 「最近颜色」用 6 列色块网格。
/// 绘制全在 <see cref="FocalMenuRenderer"/> 里，颜色只从 Focal 令牌取。
/// </summary>
internal sealed class TrayHost : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly FocalContextMenu _menu;
    private readonly FocalMenuItem _captureItem;
    private readonly FocalMenuItem _clipboardPinItem;
    private readonly FocalMenuItem _panelItem;
    private readonly FocalMenuItem _pinItem;
    private readonly FocalMenuItem _checkUpdatesItem;
    private readonly FocalMenuItem _exportDiagnosticsItem;
    private readonly FocalMenuItem _recentColorsItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _autoStartItem;
    private bool _suppressAutoStartEvent;

    public TrayHost(string hotkeyText, bool autoStartEnabled)
    {
        _menu = new FocalContextMenu
        {
            Renderer = new FocalMenuRenderer(),
        };

        // ── 截图 ────────────────────────────────────────────────────────────
        _captureItem = new FocalMenuItem("截图", hotkeyText);
        _captureItem.Click += (_, _) => CaptureRequested?.Invoke();

        _clipboardPinItem = new FocalMenuItem("从剪贴板贴图");
        _clipboardPinItem.Click += (_, _) => ClipboardPinRequested?.Invoke();

        _panelItem = new FocalMenuItem("贴图管理…");
        _panelItem.Click += (_, _) => ManagePinsRequested?.Invoke();

        // ── 贴图 ────────────────────────────────────────────────────────────
        _pinItem = new FocalMenuItem("显示全部贴图");
        _pinItem.Click += (_, _) => ShowAllPinsRequested?.Invoke();

        var hideAllPins = new FocalMenuItem("隐藏全部贴图");
        hideAllPins.Click += (_, _) => HideAllPinsRequested?.Invoke();

        var closeAllPins = new FocalMenuItem("关闭全部贴图");
        closeAllPins.Click += (_, _) => CloseAllPinsRequested?.Invoke();

        // ── 目录 ────────────────────────────────────────────────────────────
        var openSaveFolder = new FocalMenuItem("打开保存目录");
        openSaveFolder.Click += (_, _) => OpenSaveFolderRequested?.Invoke();

        var openConfigFolder = new FocalMenuItem("打开配置目录");
        openConfigFolder.Click += (_, _) => OpenConfigFolderRequested?.Invoke();

        var openPinBackupFolder = new FocalMenuItem("打开贴图备份目录");
        openPinBackupFolder.Click += (_, _) => OpenPinBackupFolderRequested?.Invoke();

        // ── 工具 ────────────────────────────────────────────────────────────
        var settings = new FocalMenuItem("设置…");
        settings.Click += (_, _) => SettingsRequested?.Invoke();

        _checkUpdatesItem = new FocalMenuItem("检查更新…");
        _checkUpdatesItem.Click += (_, _) => CheckUpdatesRequested?.Invoke();

        _exportDiagnosticsItem = new FocalMenuItem("导出诊断包…");
        _exportDiagnosticsItem.Click += (_, _) => ExportDiagnosticsRequested?.Invoke();

        var picker = new FocalMenuItem("取色器（取色并复制）");
        picker.Click += (_, _) => PickerRequested?.Invoke();

        // 「最近颜色」：设计稿要求 6 列色块网格（颜色用眼睛找），内容由 SyncRecentColors 填
        _recentColorsItem = new FocalMenuItem("最近颜色");
        _recentColorsItem.DropDownItems.Add(new FocalMenuHeader("最近颜色"));
        _recentColorsItem.DropDownItems.Add(new FocalMenuItem("还没有取过颜色。"));

        // ── 开关与退出 ──────────────────────────────────────────────────────
        _autoStartItem = new FocalMenuItem("开机自启", showCheck: true)
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

        var about = new FocalMenuItem("关于 WhSnap");
        about.Click += (_, _) => AboutRequested?.Invoke();

        var exit = new FocalMenuItem("退出", danger: true);
        exit.Click += (_, _) => ExitRequested?.Invoke();

        // ── 菜单结构（分组顺序与托盘设计稿一致）────────────────────────────
        _menu.Items.Add(_captureItem);
        _menu.Items.Add(_clipboardPinItem);
        _menu.Items.Add(_panelItem);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        _menu.Items.Add(_pinItem);
        _menu.Items.Add(hideAllPins);
        _menu.Items.Add(closeAllPins);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        _menu.Items.Add(openSaveFolder);
        _menu.Items.Add(openConfigFolder);
        _menu.Items.Add(openPinBackupFolder);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        _menu.Items.Add(settings);
        _menu.Items.Add(_checkUpdatesItem);
        _menu.Items.Add(_exportDiagnosticsItem);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        _menu.Items.Add(picker);
        _menu.Items.Add(_recentColorsItem);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        _menu.Items.Add(about);
        _menu.Items.Add(exit);

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "WhSnap — 截图贴图工具",
            Visible = true,
            ContextMenuStrip = _menu,
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

    /// <summary>托盘「最近颜色」里的「清空颜色历史」。</summary>
    public event Action? ClearColorHistoryRequested;

    /// <summary>截图项的快捷键提示（右对齐的第二列）。</summary>
    public void UpdateHotkeyText(string hotkeyText) => _captureItem.Secondary = hotkeyText ?? string.Empty;

    /// <summary>自检用：「检查更新…」菜单项的当前文案（用户可达性的机器证据）。</summary>
    internal string CheckUpdatesItemText => _checkUpdatesItem.Text ?? string.Empty;

    /// <summary>自检用：模拟点击「检查更新…」，验证菜单项确实把事件抛给宿主。</summary>
    internal void SimulateCheckUpdatesClick() => _checkUpdatesItem.PerformClick();

    /// <summary>自检用：「导出诊断包…」菜单项的当前文案。</summary>
    internal string ExportDiagnosticsItemText => _exportDiagnosticsItem.Text ?? string.Empty;

    /// <summary>自检用：模拟点击「导出诊断包…」。</summary>
    internal void SimulateExportDiagnosticsClick() => _exportDiagnosticsItem.PerformClick();

    /// <summary>
    /// 贴图数量与剪贴板监听状态直接写在菜单上（右对齐的等宽第二列），
    /// 避免"不知道现在有几张贴图"。
    /// </summary>
    public void SyncPinState(int pinCount, bool clipboardWatcherRunning)
    {
        _panelItem.Secondary = $"{pinCount} 张";
        _panelItem.Enabled = pinCount > 0;
        _pinItem.Secondary = pinCount == 0 ? string.Empty : pinCount.ToString();
        _clipboardPinItem.Secondary = clipboardWatcherRunning ? "监听中" : string.Empty;
    }

    /// <summary>
    /// 刷新「最近颜色」：6 列色块网格（色块 20×20 圆角 2，悬停有反馈），
    /// 底部保留「清空颜色历史」。点击色块仍触发既有的 <see cref="RecentColorSelected"/>。
    /// </summary>
    public void SyncRecentColors(IReadOnlyList<WhSnap.Core.RgbColor> colors)
    {
        var dropDown = _recentColorsItem.DropDownItems;
        dropDown.Clear();

        _recentColorsItem.Secondary = colors.Count == 0 ? "空" : colors.Count.ToString();
        _recentColorsItem.Enabled = colors.Count > 0;

        var header = new FocalMenuHeader("最近颜色");
        dropDown.Add(header);

        if (colors.Count == 0)
        {
            dropDown.Add(new FocalMenuItem("还没有取过颜色。"));
            return;
        }

        var grid = new FocalColorPanel(colors, () => ClearColorHistoryRequested?.Invoke());
        grid.ColorSelected += color => RecentColorSelected?.Invoke(color);

        dropDown.Add(new System.Windows.Forms.ToolStripControlHost(grid)
        {
            AutoSize = true,
            Margin = System.Windows.Forms.Padding.Empty,
            Padding = System.Windows.Forms.Padding.Empty,
            BackColor = System.Drawing.Color.Transparent,
        });
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

        _menu.Dispose();
    }
}
