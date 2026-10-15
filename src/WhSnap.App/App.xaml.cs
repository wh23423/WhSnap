using System.Diagnostics;
using System.IO;
using System.Threading;
using WhSnap.Capture;
using WhSnap.Core;
using WhSnap.Diagnostics;
using WhSnap.Pin;
using WhSnap.Services;
using WhSnap.Tray;

namespace WhSnap;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private TrayHost? _tray;
    private HotkeyService? _hotkeys;
    private CaptureController? _capture;
    private SettingsService? _settings;
    private PinManager? _pins;
    private PinPanelWindow? _pinPanel;
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

        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += OnCaptureRequested;

        _tray = new TrayHost(_settings.Current.Hotkey, AutoStartService.IsEnabled());
        _tray.CaptureRequested += OnCaptureRequested;
        _tray.ExitRequested += OnExitRequested;
        _tray.AutoStartToggled += OnAutoStartToggled;
        _tray.ClipboardPinRequested += OnClipboardPinRequested;
        _tray.ManagePinsRequested += ShowPinPanel;
        _tray.ShowAllPinsRequested += () => _pins?.ShowAll();
        _tray.HideAllPinsRequested += () => _pins?.HideAll();
        _tray.CloseAllPinsRequested += () => _pins?.CloseAll();
        _tray.OpenSaveFolderRequested += () => OpenFolder(SaveService.ResolveDirectory(_settings.Current), "保存目录");
        _tray.OpenConfigFolderRequested += () => OpenFolder(SettingsService.ConfigDirectory, "配置目录");
        _tray.OpenPinBackupFolderRequested += () => OpenFolder(_pins?.BackupStore.Root ?? SettingsService.ConfigDirectory, "贴图备份目录");
        _tray.AboutRequested += ShowAbout;

        // 贴图（M2）：先建管家并恢复上次的贴图，再挂监听
        _pins = new PinManager(_settings);
        _pins.StatusReported += OnStatusReported;
        _pins.PinsChanged += UpdateTrayPinState;
        _pins.AnnotationRequested += OnPinAnnotationRequested;
        _pins.Initialize();

        _capture = new CaptureController(_settings);
        _capture.StatusReported += OnStatusReported;
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

        ApplyHotkey(_settings.Current.Hotkey);
        EnsureAutoStartState();

        if (!string.IsNullOrEmpty(_settings.LastError))
        {
            _tray.ShowBalloon("配置已重置为默认值", _settings.LastError, warning: true);
        }
        else
        {
            string hotkey = _hotkeys.Current?.ToString() ?? _settings.Current.Hotkey;
            _tray.ShowBalloon("WhSnap 已启动", $"按 {hotkey} 开始截图（Enter 确认，F3 直接贴图），或双击托盘图标。");
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _topology?.Dispose();
        _clipboard?.Dispose();
        _pins?.Dispose();
        _pinPanel?.Close();
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
        // M4 之前：贴图内标注还没有引擎，明确告知而不是静默失败（见规划 §7 里程碑）
        _tray?.ShowBalloon(
            "贴图内标注",
            $"已选中贴图 #{pin.ZIndex + 1}。贴图内标注引擎在 M4 提供；当前可先「复制到剪贴板」，或关闭贴图后用截图遮罩继续。");
    }

    private void ApplyHotkey(string configured)
    {
        if (!HotkeySpec.TryParse(configured, out var spec, out string? error))
        {
            Log.Warn($"配置中的热键无法解析（{configured}）：{error}");
            _tray?.ShowBalloon("热键无效", $"{error} 已临时回退为 F1，可在配置目录的 settings.json 中修改。", warning: true);
            HotkeySpec.TryParse("F1", out spec, out _);
        }

        var result = _hotkeys!.Register(spec);
        if (!result.IsRegistered)
        {
            Log.Warn(result.Message);
            _tray?.ShowBalloon("热键注册失败", $"{result.Message} 仍可用托盘菜单截图。", warning: true);
            return;
        }

        _tray?.UpdateHotkeyText(spec.ToString());
        Log.Info(result.Message);
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
