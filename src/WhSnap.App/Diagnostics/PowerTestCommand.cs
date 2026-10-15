using System.IO;
using System.Text;
using WhSnap.Core;
using WhSnap.Interop;
using WhSnap.Services;

namespace WhSnap.Diagnostics;

/// <summary>
/// 休眠 / 唤醒后热键重注册自检：<c>WhSnap.exe --power-test</c>
///
/// 背景（风险 R4）：系统从睡眠/休眠恢复后**不会**替应用恢复 <c>RegisterHotKey</c> 注册，
/// 热键会静默失效——不报错、不崩溃，用户只感觉"按了没反应"。<c>HotkeyManager</c> 因此监听
/// <c>WM_POWERBROADCAST</c> 自动重注册；本自检证明这条路径真的通：
///   ① 自检键位在开始前是空闲的（否则后面的"谁持有"没有意义）；
///   ② 注册后热键确实被本进程持有（L1 探针拿到 Win32 1409）；
///   ③ 投递 <c>PBT_APMRESUMEAUTOMATIC</c> 会触发重注册（事件 + 日志证据）；
///   ④ **重注册后热键仍被本进程持有**——这是 R4 真正要防的"重注册没生效"；
///   ⑤ 重注册后 <c>WM_HOTKEY</c> 仍能派发到正确动作（id → 动作映射被重建）；
///   ⑥ <c>PBT_APMRESUMESUSPEND</c>（用户按键唤醒）同样触发，无关电源事件不触发；
///   ⑦ 释放后键位归还系统（重注册不会泄漏占用）。
///
/// 诚实边界：自检把电源消息**投递给程序自己的热键消息窗口**（与系统广播走同一个窗口过程），
/// 而不是真的把机器睡下去；"系统会在恢复时广播这条消息"属于 Windows 既定行为，
/// 真机睡眠/唤醒各做一次人工确认仍然建议做（见 <c>docs/进度快照.md</c> T9）。
/// </summary>
internal static class PowerTestCommand
{
    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;

    private const string ResumeLogText = "检测到系统从休眠/睡眠恢复，重新注册全局热键。";

    /// <summary>候选键位：挑当前空闲的用，避免和用户真实占用的热键打架（前面几个故意挑冷门组合）。</summary>
    private static readonly string[] CandidateKeys =
    [
        "Ctrl+Alt+Shift+F9",
        "Ctrl+Alt+Shift+F10",
        "Ctrl+Alt+Shift+F11",
        "Ctrl+Alt+Shift+F12",
        "Ctrl+Shift+F9",
        "Ctrl+Shift+F10",
    ];

    public static int Run()
    {
        var report = new StringBuilder();
        var failures = new List<string>();
        var notes = new List<string>();

        report.AppendLine($"WhSnap 休眠唤醒热键自检　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"程序版本：{UpdateService.GetCurrentVersion()}　进程 {(Environment.Is64BitProcess ? "x64" : "x86")}");
        report.AppendLine(new string('-', 68));

        void Check(string name, Action body)
        {
            try
            {
                body();
                report.AppendLine($"[PASS] {name}");
            }
            catch (Exception ex)
            {
                failures.Add(name);
                report.AppendLine($"[FAIL] {name}：{ex.GetType().Name}: {ex.Message}");
            }
        }

        string? keyA = null;
        string? keyB = null;
        long logOffset = 0;

        try
        {
            using var manager = new HotkeyManager();

            int reregisterEvents = 0;
            string? lastReregisterMessage = null;
            var pressed = new List<string>();

            manager.Reregistered += message =>
            {
                reregisterEvents++;
                lastReregisterMessage = message;
            };

            manager.ActionPressed += action => pressed.Add(action);

            // --- 前置：挑两个当前空闲的键位（探针不改动当前注册状态） ------------------
            Check("自检键位在开始前空闲（否则「谁持有」的结论不成立）", () =>
            {
                var free = new List<string>();

                foreach (string candidate in CandidateKeys)
                {
                    if (!HotkeySpec.TryParse(candidate, out var spec, out _))
                    {
                        continue;
                    }

                    if (manager.ProbeOne("capture.region", "截图（区域）", spec).Status == HotkeyStatus.Registered)
                    {
                        free.Add(candidate);
                    }

                    if (free.Count == 2)
                    {
                        break;
                    }
                }

                if (free.Count < 2)
                {
                    throw new InvalidOperationException(
                        $"候选键位里空闲的不足 2 个（试过 {CandidateKeys.Length} 个：{string.Join(" / ", CandidateKeys)}）。");
                }

                keyA = free[0];
                keyB = free[1];
                notes.Add($"自检键位：{keyA} / {keyB}");
            });

            var bindings = new List<HotkeyBinding>
            {
                new("capture.region", keyA ?? CandidateKeys[0], true),
                new("settings.open", keyB ?? CandidateKeys[1], true),
            };

            // --- ② 注册后确实被本进程持有 -------------------------------------------
            Check("注册后热键被本进程持有（L1 探针必须拿到 Win32 1409）", () =>
            {
                if (keyA is null)
                {
                    throw new InvalidOperationException("自检键位未就绪。");
                }

                var results = manager.ApplyAll(bindings);

                if (manager.RegisteredCount != bindings.Count)
                {
                    throw new InvalidOperationException(
                        $"注册成功 {manager.RegisteredCount} 个，期望 {bindings.Count} 个：{string.Join("；", results.Select(r => r.Message))}");
                }

                if (manager.MessageWindowHandle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("热键消息窗口没有建立，电源消息无处可投。");
                }

                HotkeySpec.TryParse(keyA, out var spec, out _);
                var probe = manager.ProbeOne("capture.region", "截图（区域）", spec);

                if (probe.Status != HotkeyStatus.ConflictWithSystem)
                {
                    throw new InvalidOperationException(
                        $"{keyA} 注册成功后探针却报告「{probe.Message}」——说明注册没有真的生效。");
                }
            });

            // --- ③ 恢复通知触发重注册（事件 + 日志证据） ------------------------------
            Check("PBT_APMRESUMEAUTOMATIC 触发重注册（事件 + 日志证据）", () =>
            {
                RequireKeys(keyA);

                int before = reregisterEvents;
                logOffset = LogLength();

                NativeMethods.SendMessage(
                    manager.MessageWindowHandle, NativeMethods.WM_POWERBROADCAST, PbtApmResumeAutomatic, IntPtr.Zero);

                if (reregisterEvents != before + 1)
                {
                    throw new InvalidOperationException($"重注册事件触发 {reregisterEvents - before} 次，期望 1 次。");
                }

                if (manager.RegisteredCount != bindings.Count)
                {
                    throw new InvalidOperationException(
                        $"重注册后生效热键 {manager.RegisteredCount} 个，期望 {bindings.Count} 个。");
                }

                string appended = ReadLogFrom(logOffset);

                if (!appended.Contains(ResumeLogText, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"日志里没有出现「{ResumeLogText}」，用户排查时看不到证据。");
                }

                notes.Add($"重注册回报：{lastReregisterMessage ?? "(无文字)"}；日志已落盘");
            });

            // --- ④ 核心断言：重注册后仍然持有 ---------------------------------------
            Check("重注册后热键仍被本进程持有（R4 的核心断言）", () =>
            {
                RequireKeys(keyA);

                HotkeySpec.TryParse(keyA, out var spec, out _);
                var probe = manager.ProbeOne("capture.region", "截图（区域）", spec);

                if (probe.Status != HotkeyStatus.ConflictWithSystem)
                {
                    throw new InvalidOperationException(
                        $"重注册后 {keyA} 变成「{probe.Message}」——热键在唤醒后其实已经失效。");
                }

                if (manager.RegisteredIds.Count != bindings.Count)
                {
                    throw new InvalidOperationException(
                        $"重注册后 id → 动作映射只有 {manager.RegisteredIds.Count} 条，期望 {bindings.Count} 条。");
                }
            });

            // --- ⑤ 重注册后 WM_HOTKEY 仍能派发到正确动作 ----------------------------
            Check("重注册后 WM_HOTKEY 仍能派发到正确动作（id → 动作映射已重建）", () =>
            {
                pressed.Clear();
                var entry = manager.RegisteredIds.FirstOrDefault();

                if (entry.Value is null)
                {
                    throw new InvalidOperationException("重注册后没有任何「id → 动作」映射，按键会静默失效。");
                }

                NativeMethods.SendMessage(
                    manager.MessageWindowHandle, NativeMethods.WM_HOTKEY, entry.Key, IntPtr.Zero);

                if (pressed.Count != 1 || !string.Equals(pressed[0], entry.Value, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"期望触发「{entry.Value}」，实际 {(pressed.Count == 0 ? "没有任何动作被触发" : string.Join("、", pressed))}。");
                }

                notes.Add($"按键派发：id 0x{entry.Key:X} → {entry.Value}");
            });

            // --- ⑥ 另一种恢复码 + 无关电源事件 --------------------------------------
            Check("PBT_APMRESUMESUSPEND（按键唤醒）同样触发重注册", () =>
            {
                int before = reregisterEvents;

                NativeMethods.SendMessage(
                    manager.MessageWindowHandle, NativeMethods.WM_POWERBROADCAST, PbtApmResumeSuspend, IntPtr.Zero);

                if (reregisterEvents != before + 1)
                {
                    throw new InvalidOperationException($"重注册事件触发 {reregisterEvents - before} 次，期望 1 次。");
                }
            });

            Check("无关电源事件（PBT_APMSUSPEND）不触发重注册", () =>
            {
                int before = reregisterEvents;

                NativeMethods.SendMessage(
                    manager.MessageWindowHandle, NativeMethods.WM_POWERBROADCAST, PbtApmSuspend, IntPtr.Zero);

                if (reregisterEvents != before)
                {
                    throw new InvalidOperationException(
                        $"睡眠通知不该触发重注册，实际触发了 {reregisterEvents - before} 次。");
                }
            });

            // --- ⑦ 释放后归还系统 ---------------------------------------------------
            Check("释放后键位归还系统（重注册不泄漏占用）", () =>
            {
                RequireKeys(keyA);

                manager.Dispose();

                using var fresh = new HotkeyManager();
                HotkeySpec.TryParse(keyA, out var spec, out _);
                var probe = fresh.ProbeOne("capture.region", "截图（区域）", spec);

                if (probe.Status != HotkeyStatus.Registered)
                {
                    throw new InvalidOperationException($"释放后 {keyA} 仍被占用：{probe.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            failures.Add("自检框架");
            report.AppendLine($"[FAIL] 自检框架：{ex.GetType().Name}: {ex.Message}");
        }

        report.AppendLine(new string('-', 68));

        foreach (string note in notes)
        {
            report.AppendLine($"       {note}");
        }

        report.AppendLine(failures.Count == 0 ? "结论：全部通过。" : $"结论：{failures.Count} 项失败 → {string.Join("、", failures)}");
        report.AppendLine("边界：电源消息由自检直接投递给本程序的热键消息窗口（与系统广播同一窗口过程）；");
        report.AppendLine("      真实睡眠/唤醒各一次的人工确认仍建议做（docs/进度快照.md T9）。");

        Write(report);
        return failures.Count == 0 ? 0 : 1;
    }

    private static void RequireKeys(string? keyA)
    {
        if (keyA is null)
        {
            throw new InvalidOperationException("自检键位未就绪（前一项已失败）。");
        }
    }

    private static long LogLength()
    {
        try
        {
            var info = new FileInfo(Log.CurrentLogFile);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string ReadLogFrom(long offset)
    {
        try
        {
            using var stream = new FileStream(Log.CurrentLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(Math.Min(offset, stream.Length), SeekOrigin.Begin);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void Write(StringBuilder report)
    {
        string path = Path.Combine(Path.GetTempPath(), "whsnap-power-test.txt");

        try
        {
            File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 报告落盘失败不影响退出码
        }

        try
        {
            Console.WriteLine(report.ToString());
        }
        catch
        {
            // 无控制台时忽略
        }
    }
}
