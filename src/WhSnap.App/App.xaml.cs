using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private UpdateCoordinator? _updater;
    private bool _updateCheckRunning;
    private bool _diagnosticsBusy;
    private ColorHistory _colorHistory = new();
    private DispatcherTimer? _settingsSaveTimer;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        // 性能埋点：用进程真实启动时间做基线（比"进入 OnStartup"更接近冷启动）
        PerfTrace.StartSession();

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

        if (e.Args.Any(a => a.Equals("--element-test", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(Diagnostics.ElementTestCommand.Run());
            return;
        }

        if (e.Args.Any(a => a.Equals("--update-test", StringComparison.OrdinalIgnoreCase)))
        {
            // 这个自检内部会同步等待异步 I/O，放到线程池上跑：没有 WPF 上下文就不会因
            // "续体等待被阻塞的 UI 线程"而死锁（已实测过一次，表现为进程静默挂死）。
            // 可选 --manifest <update.json>：额外校验构建脚本产出的真实发布清单。
            TryGetArgument(e.Args, "--manifest", out string? manifestPath);
            Shutdown(Task.Run(() => Diagnostics.UpdateTestCommand.Run(manifestPath)).GetAwaiter().GetResult());
            return;
        }

        if (e.Args.Any(a => a.Equals("--crash-test", StringComparison.OrdinalIgnoreCase)))
        {
            // 默认只写文本报告；加 --with-dump 才真写一份 minidump（写完校验并删除）
            bool withDump = e.Args.Any(a => a.Equals("--with-dump", StringComparison.OrdinalIgnoreCase));
            Shutdown(Diagnostics.CrashTestCommand.Run(withDump));
            return;
        }

        if (e.Args.Any(a => a.Equals("--power-test", StringComparison.OrdinalIgnoreCase)))
        {
            // 风险 R4：唤醒后热键静默失效。这个自检把电源恢复消息投递给热键消息窗口，
            // 断言重注册后热键仍由本进程持有、按键仍能派发到动作。
            Shutdown(Diagnostics.PowerTestCommand.Run());
            return;
        }

        if (TryGetArgument(e.Args, "--capture-to", out string? capturePath) && !string.IsNullOrWhiteSpace(capturePath))
        {
            TryGetArgument(e.Args, "--backend", out string? backendName);
            Shutdown(Diagnostics.CaptureCommand.Run(capturePath, backendName));
            return;
        }

        if (e.Args.Any(a => a.Equals("--diagnostics-pack", StringComparison.OrdinalIgnoreCase)))
        {
            // 程序起不来（或要让用户"跑一句命令把 zip 发我"）时也能拿到证据
            TryGetArgument(e.Args, "--out", out string? packDirectory);
            Shutdown(Diagnostics.DiagnosticsPackCommand.Run(packDirectory));
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
            CrashHandler.Write("界面异常", args.Exception);
            _tray?.ShowBalloon(
                "WhSnap 遇到问题",
                $"{args.Exception.Message}\n\n已在「托盘 → 导出诊断包…」里附带崩溃证据。",
                warning: true);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error("未处理的域异常", args.ExceptionObject as Exception);

            // 这个处理器之后进程就要结束了：dump 必须在这里同步写完
            CrashHandler.Write("域异常", args.ExceptionObject as Exception);
        };

        _settings = new SettingsService();
        _settings.Load();
        _updater = new UpdateCoordinator(_settings);
        _colorHistory = ColorHistory.FromText(_settings.Current.ColorHistoryText, _settings.Current.ColorHistoryRetention);

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
        _tray.CheckUpdatesRequested += OnCheckUpdatesRequested;
        _tray.ExportDiagnosticsRequested += OnExportDiagnosticsRequested;
        _tray.PickerRequested += () => _capture?.Start(colorPickMode: true);
        _tray.RecentColorSelected += CopyColorFromHistory;
        _tray.ClearColorHistoryRequested += () =>
        {
            _colorHistory.Clear();
            PersistColorHistory();
            _tray.ShowBalloon("取色器", "颜色历史已清空。");
        };
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
            ColorHistory = _colorHistory,
        };
        _capture.StatusReported += OnStatusReported;
        _capture.ColorPicked += _ =>
        {
            PersistColorHistory();
            _tray?.SyncRecentColors(_colorHistory.Items);
        };
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
        _tray.SyncRecentColors(_colorHistory.Items);

        // 性能埋点（规划 §2.2 第 4 项）：冷启动 → 托盘就绪（图标 + 菜单 + 状态已就绪）
        PerfTrace.RecordSinceSessionStart(PerfMetric.ColdStartToTray);
        PerfTrace.Persist();

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

        // 更新检查放在最后：先让托盘/热键/贴图就绪，再异步拉清单（不阻塞界面）
        StartStartupUpdateCheck();

        // 上次崩溃只提示一次（读完即删标记），并指向诊断包入口
        NotifyPreviousCrashIfAny();

        if (e.Args.Any(a => a.Equals("--crash-now", StringComparison.OrdinalIgnoreCase)))
        {
            // 验收专用：在一个真实界面事件处理器里抛未处理异常，验证
            // "崩溃 → 文本报告 + dump 落盘 → 下次启动提示" 整条链路真的工作。正常使用不会走到这里。
            Log.Warn("--crash-now：即将抛出验收用的自检异常。");
            var probe = new System.Windows.Window
            {
                Width = 320,
                Height = 140,
                Title = "WhSnap 崩溃自检",
                Content = "这个窗口的 Loaded 处理器会抛异常，用来验证崩溃证据链。",
            };

            probe.Loaded += (_, _) => throw new InvalidOperationException("--crash-now：验收用的自检崩溃");
            probe.Show();
        }
    }

    /// <summary>上次运行崩过就弹一次气泡：给出摘要 + 证据路径 + 怎么交给我。</summary>
    private void NotifyPreviousCrashIfAny()
    {
        string? notice = CrashHandler.ReadPendingNoticeOnce();

        if (string.IsNullOrWhiteSpace(notice))
        {
            return;
        }

        Log.Warn("检测到上次运行崩溃，已提示用户导出诊断包。");
        _tray?.ShowBalloon("上次运行崩溃过", notice + "\n建议：托盘「导出诊断包…」把证据打包给我。", warning: true);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        // 把本次会话的性能样本落盘（下次运行/自检/诊断包里能看到真实数字）
        PerfTrace.Persist();

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
                    // 性能埋点：热键派发即开表，遮罩显示完成时由 OverlaySession 结束它
                    PerfTrace.BeginMetric(PerfMetric.HotkeyToOverlay);
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
            Updater = _updater,
            InstallUpdate = InstallUpdateFromSettingsAsync,
            ExportDiagnostics = ExportDiagnosticsAsync,
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

    // ---------------------------------------------------------------- 更新链路（M6 第 4 项）

    /// <summary>
    /// 启动检查更新：完全异步、带超时，失败只写日志（不弹任何东西、也绝不让程序起不来）。
    /// 这里刻意不 <c>await</c>，让 OnStartup 立刻返回把界面交还给用户。
    /// </summary>
    private void StartStartupUpdateCheck()
    {
        if (_updater is null || !_updater.StartupCheckEnabled)
        {
            Log.Info("启动检查更新已关闭（设置「更新」分组里的「启动时检查更新」）。");
            return;
        }

        _ = RunStartupUpdateCheckAsync();
    }

    private async Task RunStartupUpdateCheckAsync()
    {
        try
        {
            // 先等托盘、热键、贴图恢复完成，别在最忙的时候抢网络
            await Task.Delay(TimeSpan.FromSeconds(4));

            var result = await _updater!.CheckAsync();

            if (result is not { CanUpdate: true, Manifest: not null })
            {
                Log.Info($"启动检查更新：{result.Message}");
                return;
            }

            Log.Info($"启动检查更新：发现新版本 {result.Manifest.Version}（当前 {_updater.CurrentVersion}）。");
            _tray?.ShowBalloon(
                "发现新版本",
                $"WhSnap {result.Manifest.Version} 可用（当前 {_updater.CurrentVersion}）。"
                + "可在托盘「检查更新…」或设置「维护」页安装。");

            if (_updater.AutoDownloadEnabled)
            {
                await DownloadAndOfferInstallAsync(result.Manifest, silentOnFailure: true);
            }
        }
        catch (Exception ex)
        {
            // 启动路径上的任何更新问题都只进日志
            Log.Warn($"启动检查更新失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>托盘「检查更新…」：手动检查，结果一定以气泡回报（包括"已是最新"和失败原因）。</summary>
    private async void OnCheckUpdatesRequested()
    {
        if (_updater is null)
        {
            return;
        }

        if (_updateCheckRunning)
        {
            _tray?.ShowBalloon("检查更新", "正在检查中，请稍候…");
            return;
        }

        _updateCheckRunning = true;
        _tray?.ShowBalloon("检查更新", $"正在检查 {_updater.Channel} 通道…");

        try
        {
            var result = await _updater.CheckAsync();

            if (result is not { CanUpdate: true, Manifest: not null })
            {
                _tray?.ShowBalloon("检查更新", result.Message);
                return;
            }

            _tray?.ShowBalloon("发现新版本", result.Message);
            await DownloadAndOfferInstallAsync(result.Manifest, silentOnFailure: false);
        }
        catch (Exception ex)
        {
            Log.Error("手动检查更新失败", ex);
            _tray?.ShowBalloon("检查更新失败", $"{ex.GetType().Name}: {ex.Message}", warning: true);
        }
        finally
        {
            _updateCheckRunning = false;
        }
    }

    /// <summary>下载 → SHA-256 校验 → 询问 → 安装。启动路径上静默失败，手动路径上明确报错。</summary>
    private async Task DownloadAndOfferInstallAsync(UpdateManifest manifest, bool silentOnFailure)
    {
        if (_updater is null)
        {
            return;
        }

        try
        {
            _tray?.ShowBalloon("正在下载更新", $"WhSnap {manifest.Version} 下载中，完成后会校验 SHA-256。");
            string installer = await _updater.DownloadAsync(manifest);

            var choice = System.Windows.MessageBox.Show(
                $"WhSnap {manifest.Version} 已下载并通过 SHA-256 校验。\n\n{installer}\n\n"
                + "现在安装吗？（会先退出 WhSnap；安装完成后可重新启动）",
                "WhSnap 更新",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (choice != System.Windows.MessageBoxResult.Yes)
            {
                _tray?.ShowBalloon("更新包已就绪", $"可稍后手动运行：{installer}");
                return;
            }

            InstallUpdateAndExit(installer);
        }
        catch (Exception ex)
        {
            Log.Error("下载或安装更新失败", ex);

            if (!silentOnFailure)
            {
                _tray?.ShowBalloon("更新失败", $"{ex.GetType().Name}: {ex.Message}", warning: true);
            }
        }
    }

    /// <summary>设置「维护」页的「下载并安装」：与托盘路径共用同一套下载/校验/退出逻辑。</summary>
    private async Task<string> InstallUpdateFromSettingsAsync(UpdateManifest manifest)
    {
        if (_updater is null)
        {
            return "更新服务不可用。";
        }

        _tray?.ShowBalloon("正在下载更新", $"WhSnap {manifest.Version} 下载中，完成后会校验 SHA-256。");
        string installer = await _updater.DownloadAsync(manifest);

        var choice = System.Windows.MessageBox.Show(
            $"WhSnap {manifest.Version} 已下载并通过 SHA-256 校验。\n\n{installer}\n\n"
            + "现在安装吗？（会先退出 WhSnap；安装完成后可重新启动）",
            "WhSnap 更新",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (choice != System.Windows.MessageBoxResult.Yes)
        {
            return $"已下载并校验通过：{installer}（未安装，可稍后手动运行）。";
        }

        InstallUpdateAndExit(installer);
        return "正在启动安装程序，WhSnap 即将退出…";
    }

    /// <summary>启动静默安装并退出自身——安装器要替换的正是当前正在运行的程序文件。</summary>
    private void InstallUpdateAndExit(string installerPath)
    {
        UpdateService.LaunchInstaller(installerPath);
        Log.Info("更新安装已启动，WhSnap 即将退出以便替换程序文件。");
        Shutdown(0);
    }

    // ---------------------------------------------------------------- 诊断包（M6 第 5 项）

    /// <summary>托盘「导出诊断包…」：打包在工作线程完成，界面线程只等结果。</summary>
    private async void OnExportDiagnosticsRequested()
    {
        if (_diagnosticsBusy)
        {
            _tray?.ShowBalloon("导出诊断包", "正在打包，请稍候…");
            return;
        }

        _diagnosticsBusy = true;

        try
        {
            string description = await ExportDiagnosticsAsync();
            _tray?.ShowBalloon(
                "诊断包已导出",
                $"{description}\n\n可直接把 zip 发给开发者；包里不含剪贴板内容与任何凭据。");
        }
        catch (Exception ex)
        {
            Log.Error("导出诊断包失败", ex);
            _tray?.ShowBalloon("导出诊断包失败", $"{ex.GetType().Name}: {ex.Message}", warning: true);
        }
        finally
        {
            _diagnosticsBusy = false;
        }
    }

    /// <summary>
    /// 真正打包（托盘与设置维护页共用同一条路径）。IO 整体丢到线程池，界面线程只显示返回值。
    /// </summary>
    private Task<string> ExportDiagnosticsAsync()
    {
        var context = BuildDiagnosticsPackContext();
        return Task.Run(() => DiagnosticsPack.Create(context).Describe());
    }

    private DiagnosticsPackContext BuildDiagnosticsPackContext() =>
        DiagnosticsPack.Collect(
            version: UpdateService.GetCurrentVersion(),
            report: BuildDiagnostics(),
            monitors: BuildMonitorText(),
            hotkeys: BuildHotkeyText(),
            settingsJson: _settings?.ExportJson() ?? "{}",
            outputDirectory: DiagnosticsPack.DefaultDirectory,
            performanceReport: PerfTrace.Report());

    private string BuildMonitorText()
    {
        var monitors = PinMonitorList();
        return monitors.Count == 0
            ? "（未枚举到显示器）"
            : string.Join(Environment.NewLine, monitors.Select(m => m.ToString()));
    }

    private string BuildHotkeyText()
    {
        var builder = new System.Text.StringBuilder();

        foreach (var binding in _settings?.ResolveHotkeys() ?? [])
        {
            string status = !binding.Enabled
                ? "停用"
                : !binding.IsBound
                    ? "未绑定"
                    : binding.IsActive ? "生效" : "键位非法";

            builder.AppendLine(
                $"{binding.Definition.Scope,-7} {binding.Definition.Id,-24} {binding.Keys ?? "(未绑定)",-16} {binding.Definition.DisplayName}　[{status}]");
        }

        return builder.ToString();
    }

    private string BuildDiagnostics()
    {
        var builder = new System.Text.StringBuilder();
        var monitors = _capture is null ? [] : PinMonitorList();

        builder.AppendLine($"版本　　　　WhSnap {UpdateService.GetCurrentVersion()}（{Environment.OSVersion.VersionString}）");
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

    /// <summary>把颜色历史写回配置（去抖，避免连续取色时频繁落盘）。</summary>
    private void PersistColorHistory()
    {
        if (_settings is null)
        {
            return;
        }

        _settings.Current.ColorHistoryText = _colorHistory.Serialize();
        _settingsSaveTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Tick -= OnSettingsSaveTick;
        _settingsSaveTimer.Tick += OnSettingsSaveTick;
        _settingsSaveTimer.Start();
    }

    private void OnSettingsSaveTick(object? sender, EventArgs e)
    {
        _settingsSaveTimer?.Stop();
        _settings?.Save();
    }

    /// <summary>托盘「最近颜色」：按设置里的表示法复制到剪贴板。</summary>
    private void CopyColorFromHistory(RgbColor color)
    {
        var format = ColorText.ParseFormat(_settings?.Current.ColorCopyFormat);
        string text = ColorText.Format(color, format);

        _colorHistory.Add(color);
        PersistColorHistory();
        _tray?.SyncRecentColors(_colorHistory.Items);

        if (ClipboardService.TrySetText(text, out string error))
        {
            _tray?.ShowBalloon("颜色已复制", text.Replace(Environment.NewLine, "　", StringComparison.Ordinal));
        }
        else
        {
            _tray?.ShowBalloon("复制颜色失败", error, warning: true);
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
            $"WhSnap {UpdateService.GetCurrentVersion()}　对标 Snipaste 的 Windows 截图贴图工具。"
            + "当前能力：全局热键截图、多屏遮罩、像素级微调、放大镜、贴图（缩放/旋转/翻转/透明度/穿透/缩略图/备份恢复）、"
            + "从剪贴板贴图（图像/文件/HTML/颜色/文本）、贴图管理面板、标注、元素检测、取色器；"
            + "更新可在托盘「检查更新…」或设置「维护」页手动检查。");
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
