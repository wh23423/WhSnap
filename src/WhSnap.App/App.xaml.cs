using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using WhSnap.Capture;
using WhSnap.Core;
using WhSnap.Diagnostics;
using WhSnap.Overlay;
using WhSnap.Pin;
using WhSnap.Services;
using WhSnap.Tray;

namespace WhSnap;
public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private TrayHost? _tray;
    private HotkeyManager? _hotkeys;
    private CaptureController? _capture;
    private SettingsService? _settings;
    private PinManager? _pins;
    private PinPanelWindow? _pinPanel;
    private Settings.SettingsWindow? _settingsWindow;
    private TopologyWatcher? _topology;
    private ClipboardWatcher? _clipboard;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(SelfTestRunner.Run());
            return;
        }

        if (e.Args.Any(a => a.Equals("--overlay-test", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(Diagnostics.OverlayTestCommand.Run());
            return;
        }

        if (e.Args.Any(a => a.Equals("--pin-test", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(Diagnostics.PinTestCommand.Run());
            return;
        }

        if (e.Args.Any(a => a.Equals("--settings-test", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(Diagnostics.SettingsTestCommand.Run());
            return;
        }

        if (e.Args.Any(a => a.Equals("--annotate-test", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(Diagnostics.AnnotationTestCommand.Run());
            return;
        }

        if (TryGetArgument(e.Args, "--capture-to", out string? capturePath) && !string.IsNullOrWhiteSpace(capturePath))
        {
            TryGetArgument(e.Args, "--backend", out string? backendName);
            Shutdown(Diagnostics.CaptureCommand.Run(capturePath, backendName));
            return;
        }

        // 名称必须与安装包 WhSnap.iss 的 AppMutex 一致，安装器才能检测到正在运行的实例
        _instanceMutex = new Mutex(true, "WhSnap_SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            Log.Info("检测到已有 WhSnap 实例，本次启动直接退出。");
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("未处理的界面异常", args.Exception);
            _tray?.ShowBalloon("WhSnap 遇到问题", args.Exception.Message, warning: true);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("未处理的域异常", args.ExceptionObject as Exception);

        _settings = new SettingsService();
        _settings.Load();

        _hotkeys = new HotkeyManager();
        _hotkeys.ActionPressed += OnHotkeyAction;

        _tray = new TrayHost(TrayHotkeyText(), AutoStartService.IsEnabled());
        _tray.CaptureRequested += OnCaptureRequested;
        _tray.ExitRequested += OnExitRequested;
        _tray.AutoStartToggled += OnAutoStartToggled;
        _tray.ClipboardPinRequested += OnClipboardPinRequested;
        _tray.ManagePinsRequested += ShowPinPanel;
        _tray.ShowAllPinsRequested += () => _pins?.ShowAll();
        _tray.HideAllPinsRequested += () => _pins?.HideAll();
        _tray.CloseAllPinsRequested += () => _pins?.CloseAll();
        _tray.SettingsRequested += ShowSettings;
        _tray.OpenSaveFolderRequested += () => OpenFolder(SaveService.ResolveDirectory(_settings.Current), "保存目录");
        _tray.OpenConfigFolderRequested += () => OpenFolder(SettingsService.ConfigDirectory, "配置目录");
        _tray.OpenPinBackupFolderRequested += () => OpenFolder(_pins?.BackupStore.Root ?? SettingsService.ConfigDirectory, "贴图备份目录");
        _tray.AboutRequested += ShowAbout;

        // 贴图（M2）：先建管家并恢复上次的贴图，再挂监听
        _pins = new PinManager(_settings)
        {
            KeyBindings = _settings.ResolveHotkeys(),
        };
        _pins.StatusReported += OnStatusReported;
        _pins.PinsChanged += UpdateTrayPinState;
        _pins.AnnotationRequested += OnPinAnnotationRequested;
        _pins.Initialize();

        _capture = new CaptureController(_settings)
        {
            KeyBindings = _settings.ResolveHotkeys(),
        };
        _capture.StatusReported += OnStatusReported;
        _capture.AnnotateRequested += (image, rect) => OpenAnnotation(image, rect, (annotated, copyOnly) =>
            _capture?.CompleteConfirmed(annotated, rect, copyOnly ? OverlayConfirmAction.CopyOnly : OverlayConfirmAction.Default));
        _capture.PinRequested = image =>
        {
            try
            {
                _pins?.CreatePin(image);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("截图确认后创建贴图失败", ex);
                return false;
            }
        };

        _topology = new TopologyWatcher();
        _topology.TopologyChanged += () =>
        {
            try
            {
                _pins?.UpdateTopology();
            }
            catch (Exception ex)
            {
                Log.Error("拓扑变化后重算贴图失败", ex);
            }
        };
        _topology.Start();

        _clipboard = new ClipboardWatcher();
        _clipboard.ClipboardChanged += OnClipboardChanged;
        ApplyClipboardWatcher();

        UpdateTrayPinState();

        var hotkeyResults = ApplyHotkeys();
        EnsureAutoStartState();

        if (!string.IsNullOrEmpty(_settings.LastError))
        {
            _tray.ShowBalloon("配置已重置为默认值", _settings.LastError, warning: true);
        }
        else
        {
            string capture = _settings.ResolveHotkeys().First(binding => binding.ActionId == "capture.region").Keys ?? "(未绑定)";
            _tray.ShowBalloon("WhSnap 已启动", $"按 {capture} 开始截图（Enter 确认，F3 直接贴图），或双击托盘图标。");
        }

        // 注册失败的热键必须显式告知，绝不静默失败（§5.7）
        foreach (var failed in hotkeyResults.Where(result => result.Status == HotkeyStatus.ConflictWithSystem))
        {
            _tray.ShowBalloon("热键已被占用", $"{failed.Message} 可在托盘「设置…」里改键或检测占用。", warning: true);
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _topology?.Dispose();
        _clipboard?.Dispose();
        _pins?.Dispose();
        _pinPanel?.Close();
        _settingsWindow?.Close();
        _tray?.Dispose();
        _hotkeys?.Dispose();

        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch
        {
            // 未持有互斥体时释放会抛异常，退出路径忽略
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private string TrayHotkeyText()
    {
        var capture = _settings?.ResolveHotkeys().FirstOrDefault(binding => binding.ActionId == "capture.region");
        return capture?.Keys ?? "未绑定";
    }

    /// <summary>按当前配置重新注册全部全局热键，并把最新绑定推给遮罩/贴图窗口。</summary>
    private IReadOnlyList<HotkeyRegistrationResult> ApplyHotkeys()
    {
        if (_settings is null || _hotkeys is null)
        {
            return [];
        }

        var bindings = _settings.ResolveHotkeys();
        var results = _hotkeys.ApplyAll(bindings);

        if (_pins is not null)
        {
            _pins.KeyBindings = bindings;
        }

        if (_capture is not null)
        {
            _capture.KeyBindings = bindings;
        }

        _tray?.UpdateHotkeyText(TrayHotkeyText());
        return results;
    }

    private void OnHotkeyAction(string actionId)
    {
        try
        {
            switch (actionId)
            {
                case "capture.region":
                    OnCaptureRequested();
                    break;

                case "capture.pinClipboard":
                    OnClipboardPinRequested();
                    break;

                case "pin.toggleAll":
                    if (_pins is null)
                    {
                        break;
                    }

                    if (_pins.VisibleCount > 0)
                    {
                        _pins.HideAll();
                    }
                    else
                    {
                        _pins.ShowAll();
                    }

                    break;

                case "pin.closeAll":
                    _pins?.CloseAll();
                    break;

                case "pin.showPanel":
                    ShowPinPanel();
                    break;

                case "settings.open":
                    ShowSettings();
                    break;

                default:
                    Log.Warn($"未处理的热键动作：{actionId}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"处理热键动作 {actionId} 失败", ex);
            _tray?.ShowBalloon("WhSnap 出错了", ex.Message, warning: true);
        }
    }

    private void ShowSettings()
    {
        if (_settings is null)
        {
            return;
        }

        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var window = new Settings.SettingsWindow(_settings)
        {
            ApplyHotkeys = _ => ApplyHotkeys(),
            ProbeHotkeys = bindings => _hotkeys?.ProbeAll(bindings) ?? [],
            DiagnosticsProvider = BuildDiagnostics,
            ClearPinBackups = () =>
            {
                _pins?.BackupStore.Clear();
                Log.Info("用户清理了贴图备份。");
            },
        };

        window.SettingsChanged += OnSettingsChanged;
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        window.Show();
    }

    /// <summary>设置里改了会即时生效的项（自启、剪贴板监听、采集后端…）后同步运行时状态。</summary>
    private void OnSettingsChanged()
    {
        try
        {
            if (_settings is null)
            {
                return;
            }

            AutoStartService.SetEnabled(_settings.Current.AutoStart);
            _tray?.SyncAutoStart(AutoStartService.IsEnabled());
            ApplyClipboardWatcher();
            ApplyHotkeys();
            UpdateTrayPinState();
        }
        catch (Exception ex)
        {
            Log.Error("应用设置变更失败", ex);
        }
    }

    private string BuildDiagnostics()
    {
        var builder = new System.Text.StringBuilder();
        var monitors = _capture is null ? [] : PinMonitorList();

        builder.AppendLine($"版本　　　　WhSnap 0.4.0-preview.1（{Environment.OSVersion.VersionString}）");
        builder.AppendLine($"进程　　　　{(Environment.Is64BitProcess ? "x64" : "x86")}　PID {Environment.ProcessId}");
        builder.AppendLine($"虚拟桌面　　{_pins?.Desktop}　显示器 {monitors.Count} 台");
        builder.AppendLine($"最近后端　　{_settings?.Current.CaptureBackend}");
        builder.AppendLine($"贴图　　　　{_pins?.Count ?? 0} 张（显示中 {_pins?.VisibleCount ?? 0}）");
        builder.AppendLine($"热键　　　　已注册 {_hotkeys?.RegisteredCount ?? 0} 个全局热键");
        builder.AppendLine($"剪贴板监听　{(_clipboard?.IsRunning == true ? "开启" : "关闭")}");
        builder.AppendLine($"配置　　　　{SettingsService.ConfigFilePath}");
        builder.AppendLine($"贴图备份　　{_pins?.BackupStore.Root}");
        builder.AppendLine($"日志　　　　{Log.CurrentLogFile}");
        return builder.ToString();
    }

    private List<MonitorInfo> PinMonitorList()
    {
        try
        {
            return Capture.MonitorEnumerator.Enumerate().ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举显示器失败：{ex.Message}");
            return [];
        }
    }

    private void UpdateTrayPinState() =>
        _tray?.SyncPinState(_pins?.Count ?? 0, _clipboard?.IsRunning == true);

    private void OnClipboardPinRequested()
    {
        try
        {
            _pins?.CreatePinsFromClipboard();
        }
        catch (Exception ex)
        {
            Log.Error("从剪贴板贴图失败", ex);
            _pins?.ReportStatus($"从剪贴板贴图失败：{ex.Message}", warning: true);
        }
    }

    private void OnClipboardChanged()
    {
        if (_settings?.Current.ClipboardWatchEnabled != true || _pins is null)
        {
            return;
        }

        // 自己写进剪贴板的内容（截图复制）不该再被贴回来
        if (DateTime.UtcNow - ClipboardService.LastWriteUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        try
        {
            _pins.CreatePinsFromClipboard();
        }
        catch (Exception ex)
        {
            Log.Error("复制即贴图失败", ex);
        }
    }

    private void ApplyClipboardWatcher()
    {
        if (_clipboard is null)
        {
            return;
        }

        if (_settings?.Current.ClipboardWatchEnabled == true)
        {
            _clipboard.Start();
        }
        else
        {
            _clipboard.Stop();
        }

        UpdateTrayPinState();
    }

    private void ShowPinPanel()
    {
        if (_pins is null)
        {
            return;
        }

        if (_pinPanel is { IsLoaded: true })
        {
            _pinPanel.Activate();
            return;
        }

        _pinPanel = new PinPanelWindow(_pins);
        _pinPanel.Closed += (_, _) => _pinPanel = null;
        _pinPanel.Show();
    }

    private void OnPinAnnotationRequested(PinWindow pin)
    {
        try
        {
            OpenAnnotation(pin.Source, pin.Bounds, (annotated, _) =>
            {
                pin.ReplaceSource(annotated);
                _pins?.ReportStatus("贴图内容已更新（标注已写入）");
            });
        }
        catch (Exception ex)
        {
            Log.Error("打开贴图标注失败", ex);
            _tray?.ShowBalloon("WhSnap 出错了", ex.Message, warning: true);
        }
    }

    /// <summary>
    /// 打开标注编辑器。区域就是屏幕上的物理矩形，底图按 1:1 显示；
    /// 工具栏放不下时翻到区域上方（贴图靠近屏幕底边时会用到）。
    /// </summary>
    private void OpenAnnotation(BitmapSource image, PhysicalRect region, Action<BitmapSource, bool> onCompleted)
    {
        if (_settings is null || image is null || region.IsEmpty)
        {
            return;
        }

        var monitors = MonitorEnumerator.Enumerate();
        var monitor = MonitorEnumerator.GetMonitorAtPoint(monitors, region.X + (region.Width / 2), region.Y + (region.Height / 2))
                      ?? monitors.FirstOrDefault();

        double scale = monitor?.ScaleX is > 0 ? monitor.ScaleX : 1.0;
        bool toolbarAbove = monitor is not null && region.Bottom + 48 > monitor.Bounds.Bottom;

        var window = new Annotation.AnnotationWindow(image, region, scale, _settings.Current, toolbarAbove);

        window.Completed += (annotated, copyOnly) =>
        {
            try
            {
                onCompleted(annotated, copyOnly);
            }
            catch (Exception ex)
            {
                Log.Error("标注完成后的后续动作失败", ex);
                _tray?.ShowBalloon("WhSnap 出错了", ex.Message, warning: true);
            }
        };

        window.Cancelled += () => Log.Info("标注已取消。");
        window.Show();
    }

    private void EnsureAutoStartState()
    {
        string? command = AutoStartService.GetCurrentCommand();
        bool registered = !string.IsNullOrEmpty(command);

        if (registered && !AutoStartService.IsConsistent())
        {
            _tray?.SyncAutoStart(true);
            _tray?.ShowBalloon("开机自启需要修正", "注册表里的自启项指向已失效的路径，可在托盘菜单中关闭后重新开启。", warning: true);
            return;
        }

        // 默认开机自启：仅在"已安装"的位置自动写入，开发目录（bin/）不污染注册表
        if (!registered && _settings?.Current.AutoStart == true && AutoStartService.IsInstalledLocation)
        {
            try
            {
                AutoStartService.SetEnabled(true);
                registered = true;
                Log.Info("已按默认配置写入开机自启项。");
            }
            catch (Exception ex)
            {
                Log.Warn($"写入开机自启失败：{ex.Message}");
            }
        }

        _tray?.SyncAutoStart(registered);
    }

    private void OnCaptureRequested()
    {
        if (_capture?.IsBusy == true)
        {
            return;
        }

        _capture?.Start();
    }

    private void OnStatusReported(string message, bool isWarning)
    {
        _tray?.ShowBalloon(isWarning ? "WhSnap 出错了" : "截图完成", message, isWarning);
    }

    private void OnAutoStartToggled(bool enabled)
    {
        if (_settings is null)
        {
            return;
        }

        try
        {
            AutoStartService.SetEnabled(enabled);
            _settings.Current.AutoStart = enabled;
            _settings.Save();
            _tray?.SyncAutoStart(AutoStartService.IsEnabled());
            _tray?.ShowBalloon("开机自启", enabled ? "已启用，下次登录自动运行。" : "已关闭。");
        }
        catch (Exception ex)
        {
            Log.Error("切换开机自启失败", ex);
            _tray?.ShowBalloon("操作失败", ex.Message, warning: true);
            _tray?.SyncAutoStart(AutoStartService.IsEnabled());
        }
    }

    private void ShowAbout()
    {
        _tray?.ShowBalloon(
            "关于 WhSnap",
            "WhSnap 0.2.0-preview　对标 Snipaste 的 Windows 截图贴图工具。"
            + "当前能力：全局热键截图、多屏遮罩、像素级微调、放大镜、贴图（缩放/旋转/翻转/透明度/穿透/缩略图/备份恢复）、"
            + "从剪贴板贴图（图像/文件/HTML/颜色/文本）、贴图管理面板。");
    }

    private static void OpenFolder(string path, string title)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开{title}失败：{ex.Message}");
        }
    }

    private void OnExitRequested()
    {
        Log.Info("用户从托盘菜单退出。");
        Shutdown(0);
    }

    /// <summary>读取 "--name value" 形式的命令行参数。</summary>
    private static bool TryGetArgument(string[] args, string name, out string? value)
    {
        value = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = args[i + 1];
                return true;
            }
        }

        return false;
    }
}
