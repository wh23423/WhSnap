namespace WhSnap.Core;

/// <summary>
/// 一个动作的当前绑定。<paramref name="Keys"/> 为 null/空 表示未绑定；
/// <paramref name="Enabled"/> 为 false 表示用户在冲突时选择了"仍然保存但不生效"。
/// </summary>
public sealed record HotkeyBinding(string ActionId, string? Keys, bool Enabled = true)
{
    public HotkeyActionDefinition Definition => HotkeyCatalog.Require(ActionId);

    public bool IsBound => !string.IsNullOrWhiteSpace(Keys);

    /// <summary>是否真的会注册/派发：启用了、绑定了、而且键位能解析。</summary>
    public bool IsActive => Enabled && IsBound && TryGetSpec(out _);

    public bool TryGetSpec(out HotkeySpec spec)
    {
        spec = default;
        return IsBound && HotkeySpec.TryParse(Keys, out spec, out _);
    }

    public override string ToString() => $"{ActionId} = {(IsBound ? Keys : "(未绑定)")}{(Enabled ? string.Empty : " [已停用]")}";
}

/// <summary>
/// 绑定解析与按键匹配。
/// 解析规则：内置默认键 &lt; 用户覆盖；被列入停用名单的动作不生效。
/// </summary>
public static class HotkeyBindings
{
    /// <summary>按动作表生成当前生效的绑定列表（可按作用域过滤）。</summary>
    public static IReadOnlyList<HotkeyBinding> Resolve(
        IReadOnlyDictionary<string, string>? overrides = null,
        IReadOnlyCollection<string>? disabled = null,
        HotkeyScope? scope = null)
    {
        var result = new List<HotkeyBinding>();

        foreach (var action in HotkeyCatalog.All)
        {
            if (scope is { } wanted && action.Scope != wanted)
            {
                continue;
            }

            string? keys = action.DefaultKeys;

            if (overrides is not null && overrides.TryGetValue(action.Id, out string? custom))
            {
                keys = custom;
            }

            bool enabled = disabled is null || !disabled.Contains(action.Id, StringComparer.OrdinalIgnoreCase);
            result.Add(new HotkeyBinding(action.Id, keys, enabled));
        }

        return result;
    }

    /// <summary>
    /// 在一个作用域内查找与按下的键匹配的动作。
    /// 用于遮罩 / 贴图窗口的按键派发：它们不需要注册全局热键，但需要同一套键位定义。
    /// </summary>
    public static HotkeyActionDefinition? Match(
        IReadOnlyList<HotkeyBinding> bindings,
        HotkeySpec pressed,
        HotkeyScope scope)
    {
        foreach (var binding in bindings)
        {
            if (!binding.IsActive || binding.Definition.Scope != scope)
            {
                continue;
            }

            if (binding.TryGetSpec(out var spec) && spec.Equals(pressed))
            {
                return binding.Definition;
            }
        }

        return null;
    }

    /// <summary>把绑定表整理成 "动作 id → 键位" 的字典（设置界面保存用）。</summary>
    public static IReadOnlyDictionary<string, string> ToOverrides(IEnumerable<HotkeyBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in bindings)
        {
            if (binding.IsBound)
            {
                map[binding.ActionId] = binding.Keys!.Trim();
            }
        }

        return map;
    }

    /// <summary>停用名单（用于保存设置）。</summary>
    public static IReadOnlyList<string> ToDisabled(IEnumerable<HotkeyBinding> bindings) =>
        bindings.Where(binding => !binding.Enabled).Select(binding => binding.ActionId).Distinct().ToList();
}
