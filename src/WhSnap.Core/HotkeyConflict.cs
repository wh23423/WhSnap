namespace WhSnap.Core;

/// <summary>冲突层级，对应规划 §5.7 的三层检测。</summary>
public enum HotkeyConflictLayer
{
    /// <summary>L2：与已配置的其它动作撞键（规范化修饰键后比对）。</summary>
    Internal,

    /// <summary>L3：命中常见冲突源库，给出"最可能被谁占用"的启发式提示。</summary>
    KnownSource,

    /// <summary>保留键：系统独占或极危险，注册必然失败或用户会后悔。</summary>
    Reserved,
}

/// <summary>一条冲突/风险报告。<see cref="Blocks"/> 表示"必须阻断保存"（L2 内部撞键）。</summary>
public sealed record HotkeyConflictReport(
    string ActionId,
    HotkeyConflictLayer Layer,
    string Message,
    string? OtherActionId = null,
    string? Advice = null)
{
    /// <summary>L1（被系统占用）由注册探针判定，不在这里；L2 内部重复必须阻断。</summary>
    public bool Blocks => Layer == HotkeyConflictLayer.Internal;

    public override string ToString() =>
        $"[{Layer}] {ActionId}：{Message}" + (Advice is null ? string.Empty : $" → {Advice}");
}

/// <summary>常见冲突源库（随 Windows 版本维护，见规划 §5.7 的 L3 清单）。</summary>
public static class KnownConflictSources
{
    private sealed record Entry(string Keys, string Owner, string Advice);

    private static readonly Entry[] Entries =
    [
        new("Win+Shift+S", "Windows 系统截图", "系统级注册，第三方无法抢占；请改用别的组合。"),
        new("PrtScn", "系统截图工具（Win11 可在「设置 → 辅助功能 → 键盘」里开启）", "关掉该开关，或换用带修饰键的组合（如 Ctrl+Shift+A）。"),
        new("Win+PrtScn", "系统截图并自动存盘", "系统独占，建议避开。"),
        new("Win+G", "Xbox Game Bar", "可在「设置 → 游戏 → Xbox Game Bar」关闭；否则请避开。"),
        new("Win+Alt+PrtScn", "Xbox Game Bar 录屏", "同上。"),
        new("Ctrl+Space", "输入法中英文切换", "输入法占用，第三方程序通常抢不到；请换键。"),
        new("Shift+Space", "输入法全/半角切换", "输入法占用，建议避开。"),
        new("Alt+A", "微信截图", "微信默认截图键；若同时装了微信会被抢占。"),
        new("Ctrl+Alt+A", "QQ 截图", "QQ 默认截图键。"),
        new("Ctrl+Shift+A", "QQ / 钉钉 / 飞书等截图键", "多个国内 IM 都会注册这一组合，风险较高。"),
        new("Alt+Z", "NVIDIA GeForce Experience 叠加层", "可在 NVIDIA 面板里改键或关闭叠加层。"),
        new("Alt+F9", "NVIDIA 录制 / 部分录屏软件", "可在对应软件里改键。"),
        new("Ctrl+Shift+P", "PowerToys / 编辑器命令面板", "PowerToys 会全局注册部分快捷键，建议避开。"),
        new("Win+Shift+T", "PowerToys「始终置顶」", "PowerToys 全局注册。"),
        new("F1", "Snipaste / 各类帮助键", "Snipaste 默认截图键；同时装着两个工具时必然冲突。"),
        new("F3", "Snipaste 贴图键", "同上。"),
        new("Win+V", "Windows 剪贴板历史", "系统独占，无法注册。"),
        new("Win+.", "Windows 表情符号面板", "系统独占，无法注册。"),
        new("PrtScn+Alt", "部分笔记本的截图/录屏功能键", "取决于厂商驱动，建议实测。"),
    ];

    public static IReadOnlyList<(string Keys, string Owner, string Advice)> All =>
        Entries.Select(entry => (entry.Keys, entry.Owner, entry.Advice)).ToList();

    /// <summary>查表：命中则给出占用者与排查指引。</summary>
    public static bool TryLookup(HotkeySpec spec, out string owner, out string advice)
    {
        string text = spec.ToString();

        foreach (var entry in Entries)
        {
            if (!HotkeySpec.TryParse(entry.Keys, out var known, out _))
            {
                continue;
            }

            if (known.Equals(spec))
            {
                owner = entry.Owner;
                advice = entry.Advice;
                return true;
            }
        }

        owner = string.Empty;
        advice = string.Empty;
        return false;
    }
}

/// <summary>系统保留键：注册必然失败，或按下去会做危险操作。</summary>
public static class ReservedHotkeys
{
    private static readonly (string Keys, string Reason)[] Entries =
    [
        ("Ctrl+Alt+Delete", "系统安全注意序列，任何程序都无法注册。"),
        ("Win+L", "锁定计算机，系统独占。"),
        ("Alt+Tab", "切换窗口，系统独占。"),
        ("Alt+Shift+Tab", "反向切换窗口，系统独占。"),
        ("Alt+Esc", "切换窗口（不走任务栏），系统独占。"),
        ("Alt+F4", "关闭当前窗口，系统独占。"),
        ("Ctrl+Shift+Esc", "任务管理器，系统独占。"),
        ("Win+Tab", "任务视图，系统独占。"),
        ("Win+D", "显示桌面，系统独占。"),
        ("Win+E", "文件资源管理器，系统独占。"),
        ("Win+R", "运行对话框，系统独占。"),
        ("Win+X", "快速链接菜单，系统独占。"),
    ];

    public static IReadOnlyList<string> All => Entries.Select(entry => entry.Keys).ToList();

    public static bool IsReserved(HotkeySpec spec, out string reason)
    {
        foreach (var (keys, why) in Entries)
        {
            if (HotkeySpec.TryParse(keys, out var reserved, out _) && reserved.Equals(spec))
            {
                reason = why;
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }
}

/// <summary>
/// 三层冲突检测的纯逻辑部分：
///   * L2 内部重复——修饰键规范化后比对，跨作用域不算冲突；
///   * L3 常见冲突源——启发式提示（Windows 没有公开反查占用者的 API，见风险 R3）；
///   * 保留键——直接劝退。
/// L1（是否被系统/其它软件占用）必须真去试注册，在 App 层的注册探针里做。
/// </summary>
public static class HotkeyConflictDetector
{
    public static IReadOnlyList<HotkeyConflictReport> DetectInternalDuplicates(IReadOnlyList<HotkeyBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        var reports = new List<HotkeyConflictReport>();

        foreach (var group in bindings
                     .Where(binding => binding.IsActive && binding.TryGetSpec(out _))
                     .GroupBy(binding => (binding.Definition.Scope, Spec: binding.TryGetSpec(out var spec) ? spec : default))
                     .Where(group => group.Count() > 1))
        {
            var members = group.ToList();

            foreach (var binding in members)
            {
                var others = members.Where(other => !ReferenceEquals(other, binding)).Select(other => other.Definition.DisplayName);
                reports.Add(new HotkeyConflictReport(
                    binding.ActionId,
                    HotkeyConflictLayer.Internal,
                    $"{group.Key.Spec} 与「{string.Join("」「", others)}」重复（作用域：{Describe(group.Key.Scope)}）。",
                    members.First(other => !ReferenceEquals(other, binding)).ActionId,
                    "同一个键在同一个作用域里只能绑一个动作，请改掉其中一个。"));
            }
        }

        return reports;
    }

    public static IReadOnlyList<HotkeyConflictReport> ExplainKnownSources(IReadOnlyList<HotkeyBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        var reports = new List<HotkeyConflictReport>();

        foreach (var binding in bindings.Where(binding => binding.IsActive && binding.Definition.Scope == HotkeyScope.Global))
        {
            if (binding.TryGetSpec(out var spec) && KnownConflictSources.TryLookup(spec, out string owner, out string advice))
            {
                reports.Add(new HotkeyConflictReport(
                    binding.ActionId,
                    HotkeyConflictLayer.KnownSource,
                    $"{spec} 最可能被「{owner}」占用。",
                    Advice: advice));
            }
        }

        return reports;
    }

    public static IReadOnlyList<HotkeyConflictReport> CheckReserved(IReadOnlyList<HotkeyBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        var reports = new List<HotkeyConflictReport>();

        foreach (var binding in bindings.Where(binding => binding.IsActive && binding.TryGetSpec(out _)))
        {
            binding.TryGetSpec(out var spec);

            if (ReservedHotkeys.IsReserved(spec, out string reason))
            {
                reports.Add(new HotkeyConflictReport(
                    binding.ActionId,
                    HotkeyConflictLayer.Reserved,
                    $"{spec} 是系统保留键：{reason}",
                    Advice: "请换一个组合键。"));
            }
        }

        return reports;
    }

    /// <summary>不依赖试注册的全量预检（保存前调用）。</summary>
    public static IReadOnlyList<HotkeyConflictReport> DetectAll(IReadOnlyList<HotkeyBinding> bindings)
    {
        var reports = new List<HotkeyConflictReport>();
        reports.AddRange(DetectInternalDuplicates(bindings));
        reports.AddRange(CheckReserved(bindings));
        reports.AddRange(ExplainKnownSources(bindings));
        return reports;
    }

    /// <summary>把所有绑定合成一份"保存前检查"结论（供设置界面顶部横幅显示）。</summary>
    public static string Summarize(IReadOnlyList<HotkeyConflictReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        if (reports.Count == 0)
        {
            return "未发现冲突。";
        }

        int blocking = reports.Count(report => report.Blocks);
        return blocking > 0
            ? $"发现 {blocking} 处必须修正的冲突，以及 {reports.Count - blocking} 条风险提示。"
            : $"发现 {reports.Count} 条风险提示（不阻断保存）。";
    }

    private static string Describe(HotkeyScope scope) => scope switch
    {
        HotkeyScope.Global => "全局热键",
        HotkeyScope.Overlay => "遮罩内按键",
        _ => "贴图窗口内按键",
    };
}
