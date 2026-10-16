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

/// <summary>
/// 默认键位的历史迁移：把"仍在用旧默认值"的用户配置搬到新默认值。
///
/// 为什么需要它：用户配置里一旦出现某个动作的键位，它就覆盖动作表的默认值——
/// 所以**单纯改动作表的默认键不会影响任何老用户**。改默认键时必须配套一次迁移，
/// 否则老用户永远停在旧键上（这也是"改了默认值却发现没生效"的常见原因）。
///
/// 只在"缺失"或"恰好等于旧默认值"时迁移：**用户显式改过的一律不动**。
/// </summary>
public static class HotkeyDefaultMigration
{
    private static readonly (string ActionId, string LegacyKeys)[] LegacyKeysByAction =
    [
        ("capture.region", HotkeyCatalog.LegacyCaptureRegionKeys),
        ("capture.pinClipboard", HotkeyCatalog.LegacyPinClipboardKeys),
        ("pin.thumbnail", HotkeyCatalog.LegacyPinThumbnailKeys),
    ];

    /// <summary>
    /// 就地迁移 <paramref name="overrides"/>；返回实际迁移的动作 id（已迁移的动作会显式写入新键，
    /// 语义更清晰，也不会被未来的默认值变更再次牵动）。
    /// </summary>
    public static IReadOnlyList<string> ApplyLegacyDefaults(IDictionary<string, string> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        var migrated = new List<string>();

        foreach (var (actionId, legacyKeys) in LegacyKeysByAction)
        {
            var definition = HotkeyCatalog.Find(actionId);

            if (definition is null)
            {
                continue;
            }

            if (overrides.TryGetValue(actionId, out string? stored))
            {
                // 有显式配置：只有"恰好还在用旧默认键"才迁移，用户改过的不动。
                if (!string.Equals(stored?.Trim(), legacyKeys, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            else if (!string.Equals(definition.DefaultKeys, legacyKeys, StringComparison.OrdinalIgnoreCase))
            {
                // 没有显式配置，但动作表默认值已经不是旧键 → 用户本来就拿的是新默认值。
                continue;
            }

            overrides[actionId] = definition.DefaultKeys!;
            migrated.Add(actionId);
        }

        return migrated;
    }
}
