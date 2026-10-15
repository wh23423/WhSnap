using System.IO;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using WhSnap.Core;
using WhSnap.Services;
using WhSnap.Settings;

namespace WhSnap.Diagnostics;

/// <summary>
/// 设置界面无交互自检：WhSnap.exe --settings-test
///
/// 设置界面是完全由 <see cref="SettingsSchema"/> 自动生成的，所以"界面是否真的覆盖了每一项"
/// 必须被机器验证，否则加配置项时很容易只加数据、忘了界面。
/// 本自检真实创建设置窗口、渲染全部标签页与控件，然后验证：
///   ① 每个 schema 项都有控件、每个动作都有热键行；
///   ② 导出 → 导入 无损（未知项/非法值为 0）；
///   ③ 键位录制对合法组合成立、对非法键（纯修饰键等）拒绝；
///   ④ 恢复默认与 schema 双向一致性（SettingsBinder.VerifySchema）。
/// 运行期间会短暂闪现设置窗口（约 1 秒）。
/// </summary>
internal static class SettingsTestCommand
{
    public static int Run()
    {
        var log = new StringBuilder();
        var failures = new List<string>();

        log.AppendLine($"WhSnap 设置界面自检　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine(new string('-', 68));

        void Check(string name, Action body)
        {
            try
            {
                body();
                log.AppendLine($"[PASS] {name}");
            }
            catch (Exception ex)
            {
                failures.Add(name);
                log.AppendLine($"[FAIL] {name}：{ex.GetType().Name}: {ex.Message}");
            }
        }

        var settings = new SettingsService();
        settings.Load();

        SettingsWindow? window = null;

        try
        {
            Check("构建设置窗口（全部标签页与控件由 schema 生成）", () =>
            {
                window = new SettingsWindow(settings)
                {
                    ApplyHotkeys = _ => { },
                    ProbeHotkeys = _ => [],
                    DiagnosticsProvider = () => "自检模式：诊断信息占位。",
                };

                window.Show();
                Pump();

                int expectedTabs = SettingsSchema.Groups.Count + 2; // 配置分组 + 快捷键 + 维护
                if (window.TabCount != expectedTabs)
                {
                    throw new InvalidOperationException($"标签页 {window.TabCount} 个，期望 {expectedTabs} 个。");
                }

                if (window.StatusBadgeFor("capture.region").Length == 0)
                {
                    throw new InvalidOperationException("热键行没有渲染出状态徽标。");
                }
            });

            Check("界面覆盖度：schema 每一项都有控件、动作表每一条都有热键行", () =>
            {
                var missing = SettingsSchema.All
                    .Where(definition => !window!.RenderedSettingKeys.Contains(definition.Key))
                    .Select(definition => definition.Key)
                    .ToList();

                if (missing.Count > 0)
                {
                    throw new InvalidOperationException($"这些配置项没有界面入口：{string.Join("、", missing)}");
                }

                if (window!.HotkeyRowCount != HotkeyCatalog.All.Count)
                {
                    throw new InvalidOperationException($"热键行 {window.HotkeyRowCount} 条，动作表 {HotkeyCatalog.All.Count} 条。");
                }

                return;
            });

            Check("键位录制：合法组合可录入、非法按键被拒绝", () =>
            {
                // 纯修饰键不能单独作为热键
                foreach (var modifierOnly in new[] { Key.LeftCtrl, Key.RightShift, Key.LeftAlt, Key.LWin })
                {
                    if (SettingsWindow.TryComposeHotkey(modifierOnly, ModifierKeys.None) is not null)
                    {
                        throw new InvalidOperationException($"{modifierOnly} 是纯修饰键，不该被接受。");
                    }
                }

                string? ctrlAltQ = SettingsWindow.TryComposeHotkey(Key.Q, ModifierKeys.Control | ModifierKeys.Alt);
                if (ctrlAltQ != "Ctrl+Alt+Q")
                {
                    throw new InvalidOperationException($"Ctrl+Alt+Q 录制结果异常：{ctrlAltQ ?? "null"}");
                }

                string? f9 = SettingsWindow.TryComposeHotkey(Key.F9, ModifierKeys.None);
                if (f9 != "F9")
                {
                    throw new InvalidOperationException($"F9 录制结果异常：{f9 ?? "null"}");
                }

                // 录入后必须真的写进配置并能被解析
                if (!window!.SimulateRecording(Key.Q, ModifierKeys.Control | ModifierKeys.Alt))
                {
                    throw new InvalidOperationException("模拟录制失败。");
                }

                var binding = window.CurrentBindings.First(item => item.ActionId == "capture.region");
                if (binding.Keys != "Ctrl+Alt+Q" || !binding.IsActive)
                {
                    throw new InvalidOperationException($"录制后绑定未生效：{binding}");
                }

                // 恢复默认，避免自检改动用户配置
                settings.Current.Hotkeys.Remove("capture.region");
                settings.Current.DisabledHotkeys.Clear();
                settings.Save();
                window.SimulateRecording(Key.F1, ModifierKeys.None);
                settings.Current.Hotkeys.Remove("capture.region");
                settings.Save();
            });

            Check("导出 → 导入 无损（未知项与非法值都为 0）", () =>
            {
                string exported = settings.ExportJson();
                var result = settings.ImportJson(exported);

                if (result.Unknown.Count > 0 || result.Invalid.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"导出再导入出现损耗：未知 {string.Join("、", result.Unknown)}；非法 {string.Join("、", result.Invalid)}。");
                }

                if (result.Applied.Count < SettingsSchema.All.Count)
                {
                    throw new InvalidOperationException($"只应用了 {result.Applied.Count} 项，schema 共 {SettingsSchema.All.Count} 项。");
                }

                log.AppendLine($"       {result.Describe()}");
            });

            Check("导入非法配置会被逐项拒绝并报告", () =>
            {
                const string bad = """
                { "CaptureBackend": "vulkan", "OverlayDimOpacity": 999, "NoSuchKey": 1, "PinBackupEnabled": true }
                """;

                var result = settings.ImportJson(bad);

                if (result.Applied.Count != 1 || result.Unknown.Count != 1 || result.Invalid.Count != 2)
                {
                    throw new InvalidOperationException(
                        $"校验结果不符：应用 {result.Applied.Count}、未知 {result.Unknown.Count}、非法 {result.Invalid.Count}。");
                }
            });

            Check("恢复默认与 schema 一致性", () =>
            {
                var problems = SettingsBinder.VerifySchema();
                if (problems.Count > 0)
                {
                    throw new InvalidOperationException(string.Join("；", problems.Take(5)));
                }

                // 在临时副本上验证"恢复默认"能真的把被改过的项改回去
                var copy = new AppSettings();
                SettingsBinder.Write(copy, "OverlayDimOpacity", "80", out _);
                int changed = SettingsBinder.ResetToDefaults(copy).Count;

                if (SettingsBinder.Read(copy, "OverlayDimOpacity") != "45")
                {
                    throw new InvalidOperationException("恢复默认未把遮罩暗度改回 45。");
                }

                return;
            });

            log.AppendLine(new string('-', 68));
            log.AppendLine(failures.Count == 0
                ? "结论：通过。"
                : $"结论：{failures.Count} 项失败 → {string.Join("、", failures)}");

            Write(log);
            return failures.Count == 0 ? 0 : 1;
        }
        finally
        {
            try
            {
                window?.Close();
            }
            catch
            {
                // 关闭异常不影响结论
            }
        }
    }

    /// <summary>把 Dispatcher 队列里优先级不低于 Background 的项目都处理掉（含布局与渲染）。</summary>
    private static void Pump()
    {
        for (int i = 0; i < 6; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
    }

    private static void Write(StringBuilder log)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "whsnap-settings-test.txt");
            File.WriteAllText(path, log.ToString(), Encoding.UTF8);
            Console.WriteLine($"报告文件：{path}");
        }
        catch
        {
            // 报告落盘失败不影响退出码
        }

        try
        {
            Console.WriteLine(log.ToString());
        }
        catch
        {
            // 无控制台时忽略
        }
    }
}
