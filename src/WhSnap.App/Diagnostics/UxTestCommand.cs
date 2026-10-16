using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WhSnap.Capture;
using WhSnap.Core;
using WhSnap.Interop;
using WhSnap.Overlay;
using WhSnap.Services;

namespace WhSnap.Diagnostics;

/// <summary>
/// 体验护栏自检：<c>WhSnap.exe --ux-test</c>
///
/// 为什么要有它：现有 12 个自检里，<c>--focal-test</c> 能给**设计一致性**下 6 条断言，却没有任何一条
/// 关心"按键是否真的通到动作"，于是评审报告里的 P0-2（全局键吃掉遮罩内同名键）与 P0-4（一键三义）
/// 能活到用户手里。本命令把这些体验问题变成不会复发的机器断言：
///
///   ① **全局默认键位真能注册**（真 <c>RegisterHotKey</c>；被别的软件占用的如实记为备注，不假装通过）；
///   ② **全局按键真能派发到动作**（投递 <c>WM_HOTKEY</c>，断言 id → 对应动作 id）；
///   ③ **遮罩内默认键位经真实 WPF 路由事件派发**（合成帧 + 真遮罩窗口 + <c>RaiseEvent(PreviewKeyDown)</c>，
///      断言每个动作产生它该有的结果）；带修饰键的组合无法伪造 <c>Keyboard.Modifiers</c>，
///      那部分退化为直调 <see cref="OverlaySession.HandleKey"/> 并在报告里如实标注；
///   ④ **无写死键名**：把四个动作改绑成占位键后，遍历启动气泡 / 引导 Hero / 自启动说明 / 设置说明 /
///      贴图右键菜单 / 贴图面板空状态 / 标注速查条的全部文案，断言跟随新键且不含旧键名；
///   ⑤ **无跨作用域抢占**：默认键位里不得出现"全局键 = 某个局部键"；
///   ⑥ **无一键多义**：同一个键默认绑定的作用域不得超过两个（P0-4 的 `T` 就是三义）；
///   ⑦ **数值一致**：颜色历史面板写的容量必须等于实际配置容量；
///   ⑧ **引导一致**：Hero 与自启动说明必须含当前截图键；
///   ⑨ **破坏性动作默认不绑定**（关闭全部贴图 / 打开面板）；
///   ⑩ **冲突提示作用域文案正确**：标注作用域内部撞键不得被说成"贴图窗口内按键"。
///
/// 运行期间遮罩窗口会短暂闪现（合成 640×480 帧，只为拿到真实的 WPF 路由与渲染路径）。
/// </summary>
internal static class UxTestCommand
{
    /// <summary>改绑用的占位键（必须带修饰键，且不含 F1/F3 子串，避免断言自我误伤）。</summary>
    private const string MarkerCapture = "Ctrl+Alt+Shift+F7";
    private const string MarkerOverlayPin = "Ctrl+Alt+Shift+F9";
    private const string MarkerUndo = "Ctrl+Alt+Shift+F6";
    private const string MarkerPickColor = "Ctrl+Alt+Shift+F5";
    private const string MarkerPinCopy = "Ctrl+Alt+Shift+F4";

    public static int Run()
    {
        var report = new StringBuilder();
        var failures = new List<string>();
        var notes = new List<string>();

        report.AppendLine($"WhSnap 体验护栏自检　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
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

        var settings = new SettingsService();
        settings.Load();

        // ── ① / ② 全局热键：真注册 + 真派发 ─────────────────────────────────────

        Check("全局默认键位：至少一个能在本机真实注册，且注册成功的都能把 WM_HOTKEY 派发到正确动作", () =>
        {
            var globalBindings = HotkeyBindings.Resolve(settings.Current.Hotkeys, settings.Current.DisabledHotkeys)
                .Where(binding => binding.Definition.Scope == HotkeyScope.Global
                                  && binding.Enabled
                                  && binding.IsBound
                                  && binding.TryGetSpec(out _))
                .ToList();

            if (globalBindings.Count == 0)
            {
                throw new InvalidOperationException("全局作用域里没有任何默认绑定的动作，这条断言没有任何意义。");
            }

            using var manager = new HotkeyManager();
            var pressed = new List<string>();
            manager.ActionPressed += action => pressed.Add(action);

            var registered = new List<string>();

            foreach (var binding in globalBindings)
            {
                binding.TryGetSpec(out var spec);
                var probe = manager.ProbeOne(binding.ActionId, binding.Definition.DisplayName, spec);

                if (probe.Status == HotkeyStatus.Registered)
                {
                    registered.Add(binding.ActionId);
                }
                else
                {
                    // 被别的软件占用是用户机器的正常状态（不是 WhSnap 的缺陷），如实记录为备注。
                    notes.Add($"{binding.Definition.DisplayName}（{spec}）本机不可用：{probe.Message} → 本项未取证");
                }
            }

            if (registered.Count == 0)
            {
                throw new InvalidOperationException(
                    $"默认全局键位在本机全部不可用（{string.Join("、", globalBindings.Select(b => b.Keys))}），这条断言无法取证。");
            }

            var testable = globalBindings.Where(binding => registered.Contains(binding.ActionId)).ToList();
            var results = manager.ApplyAll(testable);

            var notRegistered = results.Where(result => !result.IsActive).ToList();
            if (notRegistered.Count > 0)
            {
                throw new InvalidOperationException(
                    "这些默认键刚才探测是空闲的，现在却注册不上：" + string.Join("；", notRegistered.Select(r => r.Message)));
            }

            foreach (var entry in manager.RegisteredIds)
            {
                int id = entry.Key;
                string expected = entry.Value;
                pressed.Clear();
                NativeMethods.SendMessage(manager.MessageWindowHandle, NativeMethods.WM_HOTKEY, id, IntPtr.Zero);

                if (pressed.Count != 1 || !string.Equals(pressed[0], expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"id 0x{id:X} 应派发「{expected}」，实际 {(pressed.Count == 0 ? "没有任何动作被触发" : string.Join("、", pressed))}。");
                }
            }

            notes.Add($"全局按键派发：{testable.Count} 个默认动作逐个投递 WM_HOTKEY，全部命中；"
                      + $"{globalBindings.Count - testable.Count} 个因被占用未取证");
        });

        // ── ③ 遮罩内按键：真实 WPF 路由事件 → 动作 ──────────────────────────────

        Check("遮罩内默认键位经真实 WPF 路由事件派发到动作，且产生该有的结果", () =>
        {
            var overlayBindings = HotkeyBindings.Resolve(scope: HotkeyScope.Overlay)
                .Where(binding => binding.IsActive && binding.TryGetSpec(out _))
                .ToList();

            if (overlayBindings.Count == 0)
            {
                throw new InvalidOperationException("遮罩作用域里没有任何默认绑定的动作。");
            }

            int routedCount = 0;
            int directCount = 0;
            var problems = new List<string>();

            foreach (var binding in overlayBindings)
            {
                string keys = binding.Keys!.Trim();

                if (!KeySpecFactory.TryToWpf(keys, out var key, out var modifiers))
                {
                    problems.Add($"{binding.Definition.DisplayName}（{keys}）无法解析成 WPF 按键");
                    continue;
                }

                var session = CreateSyntheticSession(settings.Current, out var effects);

                try
                {
                    session.SetSelectionForTest(new PhysicalRect(10, 10, 240, 160));
                    session.Show();
                    Pump();

                    bool delivered = session.RaisePreviewKey(key, modifiers);
                    bool routed = delivered;

                    if (!routed)
                    {
                        // 带修饰键的组合：WPF 读的是真实键盘状态，伪造不了 Ctrl/Shift，只能直调会话处理。
                        delivered = session.HandleKey(key, modifiers);
                    }

                    if (routed)
                    {
                        routedCount++;
                    }
                    else
                    {
                        directCount++;
                    }

                    if (!delivered)
                    {
                        problems.Add($"{binding.Definition.DisplayName}（{keys}）没有被遮罩会话识别");
                    }
                    else if (!effects.Matches(binding.ActionId, out string detail))
                    {
                        problems.Add($"{binding.Definition.DisplayName}（{keys}）{detail}");
                    }
                }
                finally
                {
                    session.Cancel();
                    Pump();
                }
            }

            if (problems.Count > 0)
            {
                throw new InvalidOperationException(string.Join("；", problems));
            }

            if (routedCount == 0)
            {
                throw new InvalidOperationException("没有任何一个动作是经真实路由事件派发的，这条断言失去了意义。");
            }

            notes.Add($"遮罩按键：{routedCount} 个经真实 PreviewKeyDown 路由派发，"
                      + $"{directCount} 个因带修饰键（Keyboard.Modifiers 无法伪造）退化为直调 HandleKey");
        });

        // ── ④ 无写死键名：改绑后全部用户可见文案必须跟随 ─────────────────────────

        Check("改绑后全部用户可见文案跟随当前绑定，且不含旧键名（P0-3 的 8 处一次性锁死）", () =>
        {
            var original = new Dictionary<string, string>(settings.Current.Hotkeys);
            var originalDisabled = new List<string>(settings.Current.DisabledHotkeys);

            try
            {
                settings.Current.Hotkeys["capture.region"] = MarkerCapture;
                settings.Current.Hotkeys["overlay.pin"] = MarkerOverlayPin;
                settings.Current.Hotkeys["overlay.pickColor"] = MarkerPickColor;
                settings.Current.Hotkeys["annotation.undo"] = MarkerUndo;
                settings.Current.Hotkeys["pin.copy"] = MarkerPinCopy;
                settings.Current.DisabledHotkeys.Clear();

                var current = settings.Current;
                Func<string, string, string> keyText = (id, fallback) => KeyTextOf(current, id, fallback);

                // 引导页与标注速查条会把 + 展开成空格（数据字体下更易读），期望值要跟着展开
                string spacedCapture = MarkerCapture.Replace("+", " + ", StringComparison.Ordinal);
                string spacedUndo = MarkerUndo.Replace("+", " ", StringComparison.Ordinal);

                var surfaces = new List<(string Where, string Text, string[] Must, string[] MustNot)>
                {
                    ("启动气泡", App.StartupHintText(keyText), [MarkerCapture, MarkerOverlayPin], ["F1", "F3"]),
                    ("引导 Hero", Onboarding.OnboardingWindow.ComposeHeroText(current.Hotkeys["capture.region"]), [spacedCapture], ["F1"]),
                    ("引导自启动说明", Onboarding.OnboardingWindow.ComposeAutoStartHint(current.Hotkeys["capture.region"]), [spacedCapture], ["F1"]),
                    ("贴图面板空状态", Pin.PinPanelWindow.ComposeEmptyHint(keyText), [MarkerOverlayPin], ["F3"]),
                    ("设置说明（确认后直接贴图）", Settings.SettingsWindow.HintTextForTest("PinOnCapture", id => keyText(id, "未绑定")), [MarkerOverlayPin], ["F3"]),
                    ("设置说明（取色表示法）", Settings.SettingsWindow.HintTextForTest("ColorCopyFormat", id => keyText(id, "未绑定")), [MarkerPickColor], []),
                    ("标注速查条", Annotation.AnnotationWindow.ComposeToolbarHint(current), [spacedUndo], ["Ctrl+Z"]),
                };

                var menuLabels = Pin.PinWindow.ComposeMenuLabels(keyText);
                surfaces.Add(("贴图右键菜单（复制）", menuLabels.First(label => label.StartsWith("复制到剪贴板", StringComparison.Ordinal)), [MarkerPinCopy], ["Ctrl+C"]));

                // 菜单必须把 12 个可改键动作全列出来，且每一项都显示当前绑定
                // （评审 P1-5：原先只列了 Ctrl+C / Ctrl+S，其余 10 个不可发现）
                var keyedActions = Pin.PinWindow.KeyedMenuActionIds;

                if (keyedActions.Count != 12)
                {
                    throw new InvalidOperationException($"贴图右键菜单里带键位的动作有 {keyedActions.Count} 个，期望 12 个。");
                }

                var missingRows = keyedActions
                    .Where(actionId => !menuLabels.Any(label => label.EndsWith($"\t{keyText(actionId, string.Empty)}", StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (missingRows.Count > 0)
                {
                    throw new InvalidOperationException("菜单里找不到这些动作的键位列：" + string.Join("、", missingRows));
                }

                var problems = new List<string>();

                foreach (var (where, text, must, mustNot) in surfaces)
                {
                    foreach (string token in must)
                    {
                        if (!text.Contains(token, StringComparison.OrdinalIgnoreCase))
                        {
                            problems.Add($"{where} 未跟随新键 {token}：{text}");
                        }
                    }

                    foreach (string token in mustNot)
                    {
                        if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                        {
                            problems.Add($"{where} 仍残留旧键名 {token}：{text}");
                        }
                    }
                }

                if (problems.Count > 0)
                {
                    throw new InvalidOperationException(string.Join("；", problems));
                }

                notes.Add($"文案扫描：{surfaces.Count} 处用户可见文案在改绑后全部跟随，贴图右键菜单 12 项全带键位");
            }
            finally
            {
                settings.Current.Hotkeys.Clear();

                foreach (var pair in original)
                {
                    settings.Current.Hotkeys[pair.Key] = pair.Value;
                }

                settings.Current.DisabledHotkeys.Clear();
                settings.Current.DisabledHotkeys.AddRange(originalDisabled);
            }
        });

        // ── ⑤ 无跨作用域抢占 ────────────────────────────────────────────────────

        Check("默认键位里不存在「全局键 = 局部键」（P0-2 的默认配置护栏）", () =>
        {
            var defaults = DefaultSpecsByScope();
            var globalKeys = defaults[HotkeyScope.Global];
            var clashes = new List<string>();

            foreach (var scope in new[] { HotkeyScope.Overlay, HotkeyScope.Pin, HotkeyScope.Annotation })
            {
                foreach (string key in defaults[scope].Keys)
                {
                    if (globalKeys.TryGetValue(key, out var owner))
                    {
                        clashes.Add($"{key}（全局「{owner}」 vs {scope}「{defaults[scope][key]}」）");
                    }
                }
            }

            if (clashes.Count > 0)
            {
                throw new InvalidOperationException(
                    "默认配置就让全局热键吃掉了局部按键：" + string.Join("、", clashes)
                    + "。让位机制（HotkeyManager.SuspendScopeConflicts）只应作为兜底，默认值不该撞键。");
            }

            notes.Add("跨作用域抢占：默认键位无「全局 ↔ 局部」同键（让位机制本身由 --selftest 的实例抢占断言覆盖）");
        });

        // ── ⑥ 无一键多义 ────────────────────────────────────────────────────────

        Check("同一个键默认绑定的作用域不超过两个（P0-4 的一键三义护栏）", () =>
        {
            var defaults = DefaultSpecsByScope();
            var scopesByKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var (scope, keys) in defaults)
            {
                foreach (string key in keys.Keys)
                {
                    if (!scopesByKey.TryGetValue(key, out var list))
                    {
                        list = [];
                        scopesByKey[key] = list;
                    }

                    list.Add($"{scope}「{keys[key]}」");
                }
            }

            var triples = scopesByKey.Where(pair => pair.Value.Count > 2).ToList();

            if (triples.Count > 0)
            {
                throw new InvalidOperationException(
                    "这些键一键多义：" + string.Join("；", triples.Select(pair => $"{pair.Key} → {string.Join(" / ", pair.Value)}")));
            }

            string pinThumbnail = HotkeyCatalog.Require("pin.thumbnail").DefaultKeys ?? string.Empty;
            string overlayAnnotate = HotkeyCatalog.Require("overlay.annotate").DefaultKeys ?? string.Empty;
            string textTool = HotkeyCatalog.Require("annotation.tool.text").DefaultKeys ?? string.Empty;

            if (pinThumbnail.Equals(overlayAnnotate, StringComparison.OrdinalIgnoreCase)
                || pinThumbnail.Equals(textTool, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"「切换缩略图模式」的默认键 {pinThumbnail} 仍与标注/遮罩里的 T 系列动作撞在一起。");
            }

            notes.Add($"一键多义：同一键最多出现在 {scopesByKey.Values.Max(list => list.Count)} 个作用域（上限 2）");
        });

        // ── ⑦ 数值一致：颜色历史容量 ────────────────────────────────────────────

        Check("颜色历史面板写的容量等于实际配置容量（不再写死 12）", () =>
        {
            int retention = settings.Current.ColorHistoryRetention;
            var history = new ColorHistory(retention);

            if (history.Capacity != retention)
            {
                throw new InvalidOperationException($"颜色历史实际容量 {history.Capacity} 与设置值 {retention} 不一致。");
            }

            string text = Tray.FocalColorPanel.ColorPanelCapacityText(5, history.Capacity);

            if (!text.Contains(history.Capacity.ToString(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"面板文案没有写出实际容量：{text}");
            }

            if (retention != 12 && text.Contains("12", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"面板文案仍残留写死的 12：{text}");
            }

            notes.Add($"颜色历史：设置 {retention} → 面板「{text}」");
        });

        // ── ⑧ 引导一致 ──────────────────────────────────────────────────────────

        Check("引导 Hero 与自启动说明含当前截图键", () =>
        {
            string capture = settings.Current.Hotkeys.TryGetValue("capture.region", out string? stored) && !string.IsNullOrWhiteSpace(stored)
                ? stored.Trim()
                : HotkeyCatalog.Require("capture.region").DefaultKeys!;

            string hero = Onboarding.OnboardingWindow.ComposeHeroText(capture);
            string autoStart = Onboarding.OnboardingWindow.ComposeAutoStartHint(capture);
            string pretty = capture.Replace("+", " + ", StringComparison.Ordinal);

            if (!hero.Contains(pretty, StringComparison.Ordinal) || !autoStart.Contains(pretty, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"引导文案没有跟上当前截图键 {capture}：Hero「{hero}」/ 自启动「{autoStart}」");
            }

            notes.Add($"引导一致性：Hero 与自启动说明都含「{pretty}」");
        });

        // ── ⑨ 破坏性动作默认不绑定 ──────────────────────────────────────────────

        Check("破坏性动作默认不绑定（关闭全部贴图 / 打开管理面板）", () =>
        {
            foreach (string actionId in new[] { "pin.closeAll", "pin.showPanel" })
            {
                if (HotkeyCatalog.Require(actionId).DefaultKeys is not null)
                {
                    throw new InvalidOperationException(
                        $"「{HotkeyCatalog.Require(actionId).DisplayName}」默认绑定了 {HotkeyCatalog.Require(actionId).DefaultKeys}，破坏性动作不该占默认键。");
                }
            }

            var problems = HotkeyCatalog.Validate();

            if (problems.Count > 0)
            {
                throw new InvalidOperationException("动作表自检失败：" + string.Join("；", problems));
            }
        });

        // ── ⑩ 冲突提示作用域文案 ────────────────────────────────────────────────

        Check("冲突提示的作用域文案正确（标注内部撞键不得说成「贴图窗口内按键」）", () =>
        {
            var problems = new List<string>();

            foreach (var scope in new[] { HotkeyScope.Overlay, HotkeyScope.Pin, HotkeyScope.Annotation })
            {
                var members = HotkeyCatalog.InScope(scope).Where(action => action.DefaultKeys is not null).Take(2).ToList();

                if (members.Count < 2)
                {
                    continue;
                }

                var bindings = new List<HotkeyBinding>
                {
                    new(members[0].Id, "Ctrl+Alt+Shift+F5", true),
                    new(members[1].Id, "Ctrl+Alt+Shift+F5", true),
                };

                var reports = HotkeyConflictDetector.DetectInternalDuplicates(bindings);

                // 同一个键上的每个动作各报一条，两个动作 = 两条
                if (reports.Count != 2)
                {
                    problems.Add($"{scope}：同键撞车应报 2 条，实际 {reports.Count} 条");
                    continue;
                }

                string message = reports[0].Message;
                string expected = scope switch
                {
                    HotkeyScope.Overlay => "遮罩内按键",
                    HotkeyScope.Pin => "贴图窗口内按键",
                    _ => "标注窗口内按键",
                };

                if (!message.Contains(expected, StringComparison.Ordinal))
                {
                    problems.Add($"{scope}：提示里没有正确的作用域「{expected}」→ {message}");
                }

                if (scope == HotkeyScope.Annotation && message.Contains("贴图窗口", StringComparison.Ordinal))
                {
                    problems.Add($"标注内部撞键被误报成贴图窗口：{message}");
                }
            }

            if (problems.Count > 0)
            {
                throw new InvalidOperationException(string.Join("；", problems));
            }
        });

        report.AppendLine(new string('-', 68));

        foreach (string note in notes)
        {
            report.AppendLine($"       {note}");
        }

        report.AppendLine(failures.Count == 0 ? "结论：全部通过。" : $"结论：{failures.Count} 项失败 → {string.Join("、", failures)}");
        report.AppendLine("边界：① 全局键的「占用」由真注册判定，「是谁占的」仍只能给启发式（KnownConflictSources）；");
        report.AppendLine("      ② 带修饰键的组合无法伪造 Keyboard.Modifiers，那部分走 HandleKey 直调并在上面如实计数；");
        report.AppendLine("      ③ 运行期间遮罩窗口会短暂闪现（只为走真实的 WPF 路由与渲染路径）。");

        Write(report);
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>各作用域里"默认就绑了键"的动作：键（规范化文本）→ 动作名。</summary>
    private static Dictionary<HotkeyScope, Dictionary<string, string>> DefaultSpecsByScope()
    {
        var result = new Dictionary<HotkeyScope, Dictionary<string, string>>();

        foreach (var scope in Enum.GetValues<HotkeyScope>())
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var action in HotkeyCatalog.InScope(scope))
            {
                if (action.DefaultKeys is null || !HotkeySpec.TryParse(action.DefaultKeys, out var spec, out _))
                {
                    continue;
                }

                map[spec.ToString()] = action.DisplayName;
            }

            result[scope] = map;
        }

        return result;
    }

    private static string KeyTextOf(AppSettings settings, string actionId, string fallback)
    {
        var binding = HotkeyBindings.Resolve(settings.Hotkeys, settings.DisabledHotkeys)
            .FirstOrDefault(b => string.Equals(b.ActionId, actionId, StringComparison.OrdinalIgnoreCase));

        return binding is { IsActive: true } ? binding.Keys!.Trim() : fallback;
    }

    /// <summary>一次遮罩会话产生的可观测结果（断言用）。</summary>
    private sealed class SessionEffects
    {
        public bool Confirmed;
        public bool Cancelled;
        public bool Annotated;
        public int StatusReports;
        public OverlayConfirmAction Action = OverlayConfirmAction.Default;

        public bool Matches(string actionId, out string detail)
        {
            detail = string.Empty;
            bool ok;

            switch (actionId)
            {
                case "overlay.confirm":
                    ok = Confirmed && Action == OverlayConfirmAction.Default;
                    detail = $"期望「确认截图」，实际 confirmed={Confirmed} action={Action}";
                    break;
                case "overlay.cancel":
                    ok = Cancelled;
                    detail = $"期望「取消截图」，实际 cancelled={Cancelled}";
                    break;
                case "overlay.copy":
                    ok = Confirmed && Action == OverlayConfirmAction.CopyOnly;
                    detail = $"期望「复制」，实际 confirmed={Confirmed} action={Action}";
                    break;
                case "overlay.save":
                    ok = Confirmed && Action == OverlayConfirmAction.SaveOnly;
                    detail = $"期望「保存」，实际 confirmed={Confirmed} action={Action}";
                    break;
                case "overlay.pin":
                    ok = Confirmed && Action == OverlayConfirmAction.PinOnly;
                    detail = $"期望「直接贴图」，实际 confirmed={Confirmed} action={Action}";
                    break;
                case "overlay.annotate":
                    ok = Annotated;
                    detail = $"期望进入标注，实际 annotated={Annotated}";
                    break;
                default:
                    // 切换元素候选 / 取色：不结束会话，只要求被识别（由调用方断言 delivered）
                    ok = true;
                    break;
            }

            return ok;
        }
    }

    /// <summary>
    /// 合成一帧 640×480 的假屏幕并建一个真实遮罩会话（窗口是真的、XAML 是真的、渲染路径是真的，
    /// 只有底图像素是生成的）。这样自检不需要真抓屏，也不会因为屏幕内容变化而漂移。
    /// </summary>
    private static OverlaySession CreateSyntheticSession(AppSettings settings, out SessionEffects effects)
    {
        var monitor = new MonitorInfo(
            "ux-synthetic",
            new PhysicalRect(0, 0, 640, 480),
            new PhysicalRect(0, 0, 640, 480),
            96,
            96,
            IsPrimary: true);

        var image = CreateSyntheticBitmap(640, 480);
        var bounds = VirtualDesktop.BoundsOf([monitor]);
        var frame = new CaptureFrame(image, bounds, [monitor], "synthetic", TimeSpan.Zero);
        var session = new OverlaySession(frame, monitor, settings);

        var captured = new SessionEffects();
        effects = captured;

        session.Confirmed += (_, _, action) =>
        {
            captured.Confirmed = true;
            captured.Action = action;
        };
        session.Cancelled += () => captured.Cancelled = true;
        session.AnnotateRequested += (_, _) => captured.Annotated = true;
        session.StatusReported += (_, _) => captured.StatusReports++;

        return session;
    }

    private static BitmapSource CreateSyntheticBitmap(int width, int height)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;

            for (int x = 0; x < width; x++)
            {
                int index = row + (x * 4);
                pixels[index] = (byte)(x % 256);
                pixels[index + 1] = (byte)(y % 256);
                pixels[index + 2] = (byte)((x + y) % 256);
                pixels[index + 3] = 0xFF;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>把 Dispatcher 队列里优先级不低于 Background 的项目都处理掉（含布局与渲染）。</summary>
    private static void Pump()
    {
        for (int i = 0; i < 6; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
    }

    private static void Write(StringBuilder report)
    {
        string path = Path.Combine(Path.GetTempPath(), "whsnap-ux-test.txt");

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
