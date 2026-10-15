using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhSnap.Capture;
using WhSnap.Core;
using WhSnap.Interop;
using WhSnap.Services;

namespace WhSnap.Diagnostics;

/// <summary>
/// 无界面自检（WhSnap.exe --selftest）。
/// 用来在没有人工点击的情况下验证：抓屏、显示器拓扑、热键注册与冲突判定、
/// 剪贴板、配置读写、开机自启读取。退出码 0 = 全部通过。
/// </summary>
internal static class SelfTestRunner
{
    public static int Run()
    {
        TryAttachConsole();

        var report = new StringBuilder();
        var failures = new List<string>();

        report.AppendLine($"WhSnap 自检　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"版本 {UpdateService.GetCurrentVersion()}　进程 {(Environment.Is64BitProcess ? "x64" : "x86")}");
        report.AppendLine(new string('-', 68));

        void Check(string name, Func<string> body)
        {
            try
            {
                string detail = body();
                report.AppendLine($"[PASS] {name}：{detail}");
            }
            catch (Exception ex)
            {
                failures.Add(name);
                report.AppendLine($"[FAIL] {name}：{ex.GetType().Name}: {ex.Message}");
            }
        }

        Check("屏幕采集 + 自动降级链（auto）", () =>
        {
            var frame = ScreenCaptureFactory.Capture(CaptureBackend.Auto, out var attempts);

            if (frame.Image.PixelWidth != frame.VirtualBounds.Width || frame.Image.PixelHeight != frame.VirtualBounds.Height)
            {
                throw new InvalidOperationException($"位图尺寸 {frame.Image.PixelWidth}x{frame.Image.PixelHeight} 与虚拟桌面 {frame.VirtualBounds} 不一致。");
            }

            string path = Path.Combine(Path.GetTempPath(), "whsnap-selftest-capture.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame.Image));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            long bytes = new FileInfo(path).Length;
            if (bytes < 1000)
            {
                throw new InvalidOperationException($"抓图落盘仅 {bytes} 字节，疑似黑屏/失败。");
            }

            if (!FrameSanity.LooksLikeRealContent(frame.Image, out string sanity))
            {
                throw new InvalidOperationException($"画面疑似黑帧（{sanity}）。");
            }

            return $"实际后端 {frame.Backend}，{frame.VirtualBounds}，{frame.Duration.TotalMilliseconds:0} ms，{sanity}，PNG {bytes / 1024} KB；尝试链 {string.Join(" → ", attempts)}";
        });

        Check("显示器拓扑与 DPI", () =>
        {
            var monitors = MonitorEnumerator.Enumerate();
            if (monitors.Count == 0)
            {
                throw new InvalidOperationException("未枚举到任何显示器。");
            }

            return string.Join("　|　", monitors.Select(m => m.ToString()));
        });

        Check("全局热键注册与 L1 冲突判定（Win32 1409）", () =>
        {
            using var manager = new HotkeyManager();

            var bindings = new List<HotkeyBinding>
            {
                new("capture.region", "Ctrl+Alt+F9", true),
                new("capture.pinClipboard", "Ctrl+Alt+F9", true), // 故意撞键：第二个必须被判为已占用
                new("pin.toggleAll", null, true),                 // 未绑定
                new("pin.closeAll", "Ctrl+Alt+F10", false),       // 已停用
            };

            var results = manager.ApplyAll(bindings);

            var registered = results.First(result => result.ActionId == "capture.region");
            if (!registered.IsActive)
            {
                throw new InvalidOperationException($"首次注册失败：{registered.Message}");
            }

            var conflict = results.First(result => result.ActionId == "capture.pinClipboard");
            if (conflict.Status != HotkeyStatus.ConflictWithSystem)
            {
                throw new InvalidOperationException($"重复键未被判为冲突，实际状态：{conflict.Status}（{conflict.Message}）");
            }

            var unbound = results.First(result => result.ActionId == "pin.toggleAll");
            var disabled = results.First(result => result.ActionId == "pin.closeAll");
            if (unbound.Status != HotkeyStatus.Unbound || disabled.Status != HotkeyStatus.Disabled)
            {
                throw new InvalidOperationException($"未绑定/停用状态判定错误：{unbound.Status} / {disabled.Status}");
            }

            // L1 探针：另一个实例先占住 Ctrl+Alt+F11，再探测同一个键
            HotkeySpec.TryParse("Ctrl+Alt+F11", out var occupied, out _);
            using (var other = new HotkeyManager())
            {
                other.ApplyAll([new HotkeyBinding("capture.region", "Ctrl+Alt+F11", true)]);

                var probe = manager.ProbeOne("capture.region", "截图", occupied);
                if (probe.Status != HotkeyStatus.ConflictWithSystem)
                {
                    throw new InvalidOperationException($"探针未能识别被占用的热键：{probe.Status}（{probe.Message}）");
                }
            }

            // 探针释放自己的键后重新探测，空闲的键必须被判为可用
            var freeProbe = manager.ProbeAll(bindings).First(result => result.ActionId == "capture.region");
            if (freeProbe.Status != HotkeyStatus.Registered)
            {
                throw new InvalidOperationException($"空闲热键被误判为不可用：{freeProbe.Message}");
            }

            manager.UnregisterAll();
            return $"注册 1 个 + 冲突 1 个（Win32 {conflict.Win32Error}）+ 未绑定/停用各 1 个；L1 探针正确识别被占用与空闲键";
        });

        Check("快捷键动作表与配置面完整性（动作表 ↔ 设置 schema ↔ AppSettings）", () =>
        {
            var problems = new List<string>();
            problems.AddRange(HotkeyCatalog.Validate());
            problems.AddRange(SettingsSchema.Validate());
            problems.AddRange(SettingsBinder.VerifySchema());

            if (problems.Count > 0)
            {
                throw new InvalidOperationException(string.Join("；", problems.Take(5)));
            }

            int global = HotkeyCatalog.InScope(HotkeyScope.Global).Count();
            int overlay = HotkeyCatalog.InScope(HotkeyScope.Overlay).Count();
            int pin = HotkeyCatalog.InScope(HotkeyScope.Pin).Count();

            return $"动作 {HotkeyCatalog.All.Count} 个（全局 {global} / 遮罩 {overlay} / 贴图 {pin}）；"
                   + $"配置项 {SettingsSchema.All.Count} 项、{SettingsSchema.Groups.Count} 组；schema 与 AppSettings 双向一致";
        });

        Check("三层冲突检测（L2 内部重复 / L3 冲突源 / 保留键）", () =>
        {
            var bindings = new List<HotkeyBinding>
            {
                new("capture.region", "Alt+A", true),        // L3：微信截图键
                new("capture.pinClipboard", "Ctrl+Shift+Q", true),
                new("pin.toggleAll", "Shift+Ctrl+Q", true),  // L2：修饰键顺序不同但等价
                new("pin.closeAll", "Alt+Tab", true),        // 保留键
            };

            var reports = HotkeyConflictDetector.DetectAll(bindings);

            if (!reports.Any(report => report.Layer == HotkeyConflictLayer.Internal))
            {
                throw new InvalidOperationException("L2 内部重复未被检出。");
            }

            if (!reports.Any(report => report.Layer == HotkeyConflictLayer.KnownSource))
            {
                throw new InvalidOperationException("L3 常见冲突源未被检出。");
            }

            if (!reports.Any(report => report.Layer == HotkeyConflictLayer.Reserved))
            {
                throw new InvalidOperationException("保留键未被检出。");
            }

            if (!reports.Any(report => report.Blocks))
            {
                throw new InvalidOperationException("必须阻断保存的冲突没有被标记为阻断。");
            }

            return $"检出 L2/L3/保留键 共 {reports.Count} 条；{HotkeyConflictDetector.Summarize(reports)}";
        });

        Check("配置导入校验（未知项与非法值都会被报告而非静默丢弃）", () =>
        {
            var result = SettingsImportValidator.Validate(new Dictionary<string, string?>
            {
                ["CaptureBackend"] = "wgc",
                ["PinZoomStep"] = "99",          // 超出范围
                ["NotASettingAtAll"] = "1",      // 未知项
            });

            if (result.Applied.Count != 1 || result.Unknown.Count != 1 || result.Invalid.Count != 1)
            {
                throw new InvalidOperationException($"校验结果不符：应用 {result.Applied.Count}、未知 {result.Unknown.Count}、非法 {result.Invalid.Count}。");
            }

            return result.Describe();
        });

        Check("热键文本解析", () =>
        {
            if (!HotkeySpec.TryParse("Ctrl+Shift+A", out var spec, out string? error))
            {
                throw new InvalidOperationException($"解析失败：{error}");
            }

            if (!HotkeySpec.TryParse("F1", out var f1, out _) || f1.VirtualKey != 0x70)
            {
                throw new InvalidOperationException("F1 解析结果不正确。");
            }

            if (HotkeySpec.TryParse("NoSuchKey", out _, out _))
            {
                throw new InvalidOperationException("非法键名未被拒绝。");
            }

            if (!HotkeySpec.TryParse("PrtScn", out var prtScn, out _) || prtScn.VirtualKey != 0x2C)
            {
                throw new InvalidOperationException("PrtScn 解析结果不正确。");
            }

            return $"Ctrl+Shift+A → {spec}；F1 → {f1}；PrtScn → {prtScn}";
        });

        Check("剪贴板写入与读回", () =>
        {
            const int size = 8;
            var pixels = new byte[size * size * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 0x40;      // B
                pixels[i + 1] = 0xA0;  // G
                pixels[i + 2] = 0xFF;  // R
                pixels[i + 3] = 0xFF;  // A
            }

            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
            bitmap.Freeze();

            if (!ClipboardService.TryCopy(bitmap, out string error))
            {
                throw new InvalidOperationException($"写入剪贴板失败：{error}");
            }

            var readBack = System.Windows.Clipboard.GetImage();
            if (readBack is null)
            {
                throw new InvalidOperationException("剪贴板读回为空。");
            }

            return $"写入 {size}x{size} 成功，读回 {readBack.PixelWidth}x{readBack.PixelHeight}";
        });

        Check("配置读写", () =>
        {
            var service = new SettingsService();
            service.Load();
            var original = service.Current.Hotkey;
            service.Current.Hotkey = original;
            service.Save();
            if (!File.Exists(SettingsService.ConfigFilePath))
            {
                throw new InvalidOperationException("配置文件未生成。");
            }

            var settings = service.Current;
            if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
            {
                throw new InvalidOperationException($"schema 版本应为 {AppSettings.CurrentSchemaVersion}，实际 {settings.SchemaVersion}（迁移未执行）。");
            }

            return $"{SettingsService.ConfigFilePath}（热键 {original}，schema {settings.SchemaVersion}）";
        });

        Check("贴图设置项与备份存储", () =>
        {
            var service = new SettingsService();
            service.Load();
            var settings = service.Current;

            if (settings.PinZoomStep <= 1.0 || settings.PinBackupRetention < 1 || settings.PinBackupIntervalSeconds < 1)
            {
                throw new InvalidOperationException(
                    $"贴图配置项取值异常：zoomStep={settings.PinZoomStep} retention={settings.PinBackupRetention} interval={settings.PinBackupIntervalSeconds}。");
            }

            if (!ColorValue.TryParse(settings.TextPinBackground, out _) || !ColorValue.TryParse(settings.TextPinForeground, out _))
            {
                throw new InvalidOperationException("文本贴图的前景/背景色无法解析。");
            }

            // 真跑一遍备份存储的落盘与读回（用临时目录，不碰用户数据）
            string root = Path.Combine(Path.GetTempPath(), "whsnap-selftest-pins-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var store = new Pin.PinBackupStore(root);
                store.Save([], new PhysicalRect(0, 0, 1920, 1080), retention: 2);

                var loaded = store.LoadLatest();
                if (loaded is null)
                {
                    throw new InvalidOperationException("备份清单写入后读不回来。");
                }

                if (loaded.Value.Manifest.Pins.Count != 0)
                {
                    throw new InvalidOperationException($"空贴图列表应写出 0 条清单，实际 {loaded.Value.Manifest.Pins.Count} 条。");
                }

                return $"schema {settings.SchemaVersion}；缩放步进 {settings.PinZoomStep:0.##}、备份保留 {settings.PinBackupRetention} 次；备份存储读写正常";
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch
                {
                    // 临时目录清理失败可忽略
                }
            }
        });

        Check("开机自启状态读取", () =>
        {
            bool enabled = AutoStartService.IsEnabled();
            bool consistent = AutoStartService.IsConsistent();
            return $"已启用={enabled}，注册表路径一致={consistent}（自启项由安装包默认写入，程序仅在缺失时补写）";
        });

        Check("更新链路已进用户可达路径（版本可读 + 4 个配置项被消费）", () =>
        {
            var settings = new SettingsService();
            settings.Load();
            var updater = new UpdateCoordinator(settings);

            string version = updater.CurrentVersion;
            if (string.IsNullOrWhiteSpace(version) || version == "0.0.0")
            {
                throw new InvalidOperationException("读不到程序版本号（设置维护页与「关于」都靠它）。");
            }

            if (updater.ManifestSource != settings.Current.UpdateManifestUrl
                || updater.Channel != UpdateChannels.Parse(settings.Current.UpdateChannel))
            {
                throw new InvalidOperationException(
                    $"更新配置项没有被运行时消费：清单 {updater.ManifestSource}、通道 {updater.Channel}");
            }

            string kind = UpdateService.ResolveSourceKind(updater.ManifestSource) == UpdateSourceKind.LocalFile ? "本地路径" : "https";

            return $"当前版本 {version}；通道 {updater.Channel}；启动检查 {updater.StartupCheckEnabled}；"
                   + $"自动下载 {updater.AutoDownloadEnabled}；清单 {kind}（入口：启动检查 / 托盘「检查更新…」/ 设置维护页）";
        });

        Check("托盘入口可达（「检查更新…」与「导出诊断包…」点击真的会通知宿主）", () =>
        {
            using var tray = new WhSnap.Tray.TrayHost("F1", autoStartEnabled: false);
            int updates = 0;
            int diagnostics = 0;
            tray.CheckUpdatesRequested += () => updates++;
            tray.ExportDiagnosticsRequested += () => diagnostics++;

            string updateText = tray.CheckUpdatesItemText;
            string diagnosticsText = tray.ExportDiagnosticsItemText;

            if (string.IsNullOrWhiteSpace(updateText) || !updateText.Contains("更新", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"托盘菜单里找不到「检查更新…」入口，实际文案：{updateText}");
            }

            if (string.IsNullOrWhiteSpace(diagnosticsText) || !diagnosticsText.Contains("诊断", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"托盘菜单里找不到「导出诊断包…」入口，实际文案：{diagnosticsText}");
            }

            tray.SimulateCheckUpdatesClick();
            tray.SimulateExportDiagnosticsClick();

            if (updates != 1 || diagnostics != 1)
            {
                throw new InvalidOperationException($"点击后宿主收到 更新={updates} / 诊断={diagnostics}，各期望 1 次。");
            }

            return $"「{updateText}」「{diagnosticsText}」→ 宿主各收到 1 次请求";
        });

        Check("崩溃证据：dumps 目录可写 + MiniDumpWriteDump 可解析（不真写 dump）", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "whsnap-selftest-dumps-" + Guid.NewGuid().ToString("N")[..8]);

            try
            {
                Directory.CreateDirectory(root);
                var crash = CrashHandler.Write("自检", new InvalidOperationException("自检构造的异常"), writeDump: false, directory: root);

                if (crash is null || !File.Exists(crash.TextPath))
                {
                    throw new InvalidOperationException("崩溃文本报告没有落盘。");
                }

                string text = File.ReadAllText(crash.TextPath);
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                if (!string.IsNullOrWhiteSpace(profile) && text.Contains(profile, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("崩溃报告里的用户目录没有脱敏。");
                }

                if (!NativeMethods.IsDumpApiAvailable())
                {
                    throw new InvalidOperationException("dbghelp!MiniDumpWriteDump 在这台机器上不可用。");
                }

                return $"{Path.GetFileName(crash.TextPath)}（{new FileInfo(crash.TextPath).Length} 字节，已脱敏）；dump API 可解析（未真写 dump）";
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch
                {
                    // 临时目录清理失败可忽略
                }
            }
        });

        Check("诊断包导出（zip 端到端：条目齐全 + 回读 + 已脱敏）", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "whsnap-selftest-diagnostics-" + Guid.NewGuid().ToString("N")[..8]);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            try
            {
                string logs = Path.Combine(root, "logs");
                string dumps = Path.Combine(root, "dumps");
                Directory.CreateDirectory(logs);
                Directory.CreateDirectory(dumps);

                File.WriteAllText(
                    Path.Combine(logs, "whsnap-20261015.log"),
                    $"00:00:01 [INFO] 保存到 {profile}\\Pictures{Environment.NewLine}",
                    new UTF8Encoding(false));

                File.WriteAllText(
                    Path.Combine(dumps, "crash-20261015-000000-000.txt"),
                    $"崩溃报告：用户目录 {profile}",
                    new UTF8Encoding(false));

                var context = new DiagnosticsPackContext
                {
                    OutputDirectory = Path.Combine(root, "out"),
                    Version = UpdateService.GetCurrentVersion(),
                    Report = $"版本 WhSnap {UpdateService.GetCurrentVersion()}；用户目录 {profile}",
                    Monitors = "1 台显示器 1920x1080 @100%（主屏）",
                    Hotkeys = "Global  capture.region  F1  截图  [生效]",
                    SettingsJson = "{\"SaveDirectory\": \"" + profile.Replace("\\", "\\\\") + "\\\\Pictures\"}",
                    LogDirectory = logs,
                    CrashReports = CrashHandler.ListReports(dumps),
                    CrashDumps = CrashHandler.ListDumps(dumps),
                };

                var result = DiagnosticsPack.Create(context);
                var names = DiagnosticsPack.ReadEntryNames(result.Path);

                foreach (string expected in new[] { "README.txt", "report.txt", "monitors.txt", "hotkeys.txt", "settings.json", "performance.txt", "crashes.txt" })
                {
                    if (!names.Contains(expected, StringComparer.Ordinal))
                    {
                        throw new InvalidOperationException($"诊断包缺少 {expected}；实际条目：{string.Join("、", names)}");
                    }
                }

                if (!names.Any(name => name.StartsWith("logs/", StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException($"诊断包没有带日志：{string.Join("、", names)}");
                }

                if (!names.Any(name => name.StartsWith("crashes/", StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException($"诊断包没有带崩溃报告：{string.Join("、", names)}");
                }

                // 回读 + 逐个条目确认脱敏（zip 里不该出现明文用户目录）
                foreach (string name in names)
                {
                    string content = DiagnosticsPack.ReadEntryText(result.Path, name) ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(profile) && content.Contains(profile, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"条目 {name} 里仍有明文用户目录。");
                    }
                }

                string settingsEntry = DiagnosticsPack.ReadEntryText(result.Path, "settings.json") ?? string.Empty;

                if (!settingsEntry.Contains("%USERPROFILE%", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("settings.json 没有脱敏标记。");
                }

                string readme = DiagnosticsPack.ReadEntryText(result.Path, "README.txt") ?? string.Empty;

                if (!readme.Contains("不含剪贴板内容", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("README 里没有写明隐私边界。");
                }

                return $"{result.Describe()}；条目 {string.Join("、", names)}；用户目录已脱敏";
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch
                {
                    // 临时目录清理失败可忽略
                }
            }
        });

        Check("选区编辑（绝对坐标 / 微调 / 手柄 / 放大镜）", () =>
        {
            var monitors = MonitorEnumerator.Enumerate();
            var bounds = VirtualDesktop.BoundsOf(monitors);
            var model = new SelectionModel(bounds);

            // 从虚拟桌面左上角拖到右下角，等价于一次跨屏框选
            model.Begin(new PhysicalPoint(bounds.X + 8, bounds.Y + 8));
            model.Update(new PhysicalPoint(bounds.Right - 8, bounds.Bottom - 8));
            model.End();

            if (model.Current.Width != bounds.Width - 16 || model.Current.Height != bounds.Height - 16)
            {
                throw new InvalidOperationException($"跨屏框选结果异常：{model.Current}（虚拟桌面 {bounds}）");
            }

            // 换成有余量的选区，才能验证微调与手柄（铺满屏幕时平移会被正确地限位）
            model.SetSelection(new PhysicalRect(bounds.X + 200, bounds.Y + 200, 400, 300));

            int beforeX = model.Current.X;
            model.Nudge(NudgeDirection.Right, 10, resize: false);
            if (model.Current.X != beforeX + 10)
            {
                throw new InvalidOperationException($"方向键平移未生效：{beforeX} → {model.Current.X}");
            }

            var handle = SelectionModel.HitTest(model.Current, new PhysicalPoint(model.Current.X, model.Current.Y), 6);
            if (handle != SelectionHandle.TopLeft)
            {
                throw new InvalidOperationException($"手柄命中错误：{handle}");
            }

            model.BeginResize(handle, new PhysicalPoint(model.Current.X, model.Current.Y));
            int widthBefore = model.Current.Width;
            model.Update(new PhysicalPoint(model.Current.X + 30, model.Current.Y + 20));
            model.End();

            if (model.Current.Width != widthBefore - 30)
            {
                throw new InvalidOperationException($"手柄缩放未生效：{widthBefore} → {model.Current.Width}");
            }

            var magnifier = Magnifier.Compute(new PhysicalPoint(monitors[0].Bounds.X + 100, monitors[0].Bounds.Y + 100), monitors[0].Bounds);
            if (magnifier.Source.Width != Magnifier.DefaultSampleSize || magnifier.Zoom < 1)
            {
                throw new InvalidOperationException("放大镜取样几何异常。");
            }

            return $"虚拟桌面 {bounds}，{monitors.Count} 台显示器；框选 {model.Current}；平移/手柄/放大镜均正常";
        });

        // 逐后端单独验证：某个后端"这台机器不支持"记为 SKIP，真正出错才算 FAIL
        foreach (var backend in new[] { CaptureBackend.Dxgi, CaptureBackend.Wgc, CaptureBackend.Gdi })
        {
            string backendName = CaptureBackendNames.ToName(backend);

            try
            {
                var frame = ScreenCaptureFactory.Create(backend).Capture();

                if (frame.Image.PixelWidth != frame.VirtualBounds.Width || frame.Image.PixelHeight != frame.VirtualBounds.Height)
                {
                    throw new InvalidOperationException($"位图尺寸 {frame.Image.PixelWidth}x{frame.Image.PixelHeight} 与虚拟桌面 {frame.VirtualBounds} 不一致。");
                }

                if (!FrameSanity.LooksLikeRealContent(frame.Image, out string sanity))
                {
                    throw new InvalidOperationException($"画面疑似黑帧（{sanity}）。");
                }

                report.AppendLine($"[PASS] 采集后端 {backendName}：{frame.VirtualBounds}，{frame.Duration.TotalMilliseconds:0} ms，{sanity}");

                if (!string.IsNullOrWhiteSpace(frame.Notes))
                {
                    report.AppendLine($"       说明：{frame.Notes}");
                }
            }
            catch (CaptureNotSupportedException ex)
            {
                report.AppendLine($"[SKIP] 采集后端 {backendName}：{ex.Message}");
            }
            catch (Exception ex)
            {
                failures.Add($"采集后端 {backendName}");
                report.AppendLine($"[FAIL] 采集后端 {backendName}：{ex.GetType().Name}: {ex.Message}");
            }
        }

        report.AppendLine(new string('-', 68));
        report.AppendLine(failures.Count == 0
            ? "结论：全部通过。"
            : $"结论：{failures.Count} 项失败 → {string.Join("、", failures)}");

        string reportPath = Path.Combine(Path.GetTempPath(), "whsnap-selftest.txt");
        try
        {
            File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 报告落盘失败不应改变退出码语义
        }

        try
        {
            Console.WriteLine(report.ToString());
            Console.WriteLine($"报告文件：{reportPath}");
        }
        catch
        {
            // 无控制台时忽略
        }

        return failures.Count == 0 ? 0 : 1;
    }

    private static void TryAttachConsole()
    {
        try
        {
            if (NativeMethods.GetConsoleWindow() == IntPtr.Zero)
            {
                NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
            }
        }
        catch
        {
            // 无控制台可用时依赖报告文件
        }
    }

    /// <summary>页面内容统计已抽到 <see cref="FrameSanity"/>，自检与采集后端共用同一判据。</summary>
}
