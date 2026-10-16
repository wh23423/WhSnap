using WhSnap.Core;
using Xunit;

namespace WhSnap.Core.Tests;

public class HotkeyCatalogTests
{
    [Fact]
    public void Catalog_passes_its_own_integrity_check()
    {
        Assert.Empty(HotkeyCatalog.Validate());
    }

    [Fact]
    public void Every_action_id_is_unique_and_named()
    {
        var ids = HotkeyCatalog.All.Select(action => action.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(HotkeyCatalog.All, action => Assert.False(string.IsNullOrWhiteSpace(action.DisplayName)));
        Assert.All(HotkeyCatalog.All, action => Assert.False(string.IsNullOrWhiteSpace(action.Category)));
    }

    [Fact]
    public void Every_default_binding_parses()
    {
        foreach (var action in HotkeyCatalog.All.Where(action => action.DefaultKeys is not null))
        {
            Assert.True(
                HotkeySpec.TryParse(action.DefaultKeys, out _, out string? error),
                $"{action.Id} 的默认键 {action.DefaultKeys} 无法解析：{error}");
        }
    }

    [Fact]
    public void Default_bindings_do_not_repeat_within_a_scope()
    {
        foreach (var scope in Enum.GetValues<HotkeyScope>())
        {
            var keys = HotkeyCatalog.InScope(scope)
                .Where(action => action.DefaultKeys is not null)
                .Select(action => action.DefaultKeys!)
                .ToList();

            Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void Find_is_case_insensitive_and_Require_throws_for_unknown_ids()
    {
        Assert.NotNull(HotkeyCatalog.Find("CAPTURE.REGION"));
        Assert.Null(HotkeyCatalog.Find("no.such.action"));
        Assert.Null(HotkeyCatalog.Find(null));
        Assert.Throws<KeyNotFoundException>(() => HotkeyCatalog.Require("no.such.action"));
    }

    [Fact]
    public void Scopes_cover_all_three_dispatch_paths()
    {
        Assert.Contains(HotkeyCatalog.All, action => action.Scope == HotkeyScope.Global);
        Assert.Contains(HotkeyCatalog.All, action => action.Scope == HotkeyScope.Overlay);
        Assert.Contains(HotkeyCatalog.All, action => action.Scope == HotkeyScope.Pin);
    }
}

public class HotkeyBindingTests
{
    [Fact]
    public void Resolve_uses_catalog_defaults()
    {
        var bindings = HotkeyBindings.Resolve();

        Assert.Equal(HotkeyCatalog.All.Count, bindings.Count);
        Assert.All(bindings, binding => Assert.True(binding.Enabled));

        var capture = bindings.Single(binding => binding.ActionId == "capture.region");
        // 断言"解析结果等于动作表默认值"，而不是把某个具体键写死在这里——
        // 默认键会随产品决策调整（F1 曾占用了系统帮助键），测试不该因此变红。
        Assert.Equal(HotkeyCatalog.CaptureRegionKeys, capture.Keys);
        Assert.True(capture.IsActive);
    }

    [Fact]
    public void Resolve_applies_overrides_and_disabled_list()
    {
        var bindings = HotkeyBindings.Resolve(
            new Dictionary<string, string> { ["capture.region"] = "Ctrl+Alt+Q" },
            ["pin.toggleAll"]);

        Assert.Equal("Ctrl+Alt+Q", bindings.Single(b => b.ActionId == "capture.region").Keys);

        var toggled = bindings.Single(b => b.ActionId == "pin.toggleAll");
        Assert.False(toggled.Enabled);
        Assert.False(toggled.IsActive);
    }

    [Fact]
    public void Resolve_can_filter_by_scope()
    {
        var overlay = HotkeyBindings.Resolve(scope: HotkeyScope.Overlay);

        Assert.NotEmpty(overlay);
        Assert.All(overlay, binding => Assert.Equal(HotkeyScope.Overlay, binding.Definition.Scope));
    }

    [Fact]
    public void Binding_with_unparsable_keys_is_not_active()
    {
        var binding = new HotkeyBinding("capture.region", "Ctrl+NoSuchKey", true);

        Assert.True(binding.IsBound);
        Assert.False(binding.IsActive);
    }

    [Fact]
    public void Default_keys_do_not_squat_on_system_wide_single_function_keys()
    {
        // F1 是 Windows 事实上的帮助键、F3 是浏览器/编辑器的"查找下一个"。全局注册会独占它们，
        // 让前台程序再也收不到——所以默认值必须是带修饰键的组合。
        // 这条断言是"别再改回 F1/F3"的护栏。
        foreach (string actionId in new[] { "capture.region", "capture.pinClipboard" })
        {
            var definition = HotkeyCatalog.Require(actionId);
            Assert.False(string.IsNullOrWhiteSpace(definition.DefaultKeys));

            Assert.True(
                HotkeySpec.TryParse(definition.DefaultKeys, out var spec, out _),
                $"{actionId} 的默认键「{definition.DefaultKeys}」无法解析。");

            Assert.NotEqual(0u, spec.Modifiers);
        }
    }

    [Theory]
    [InlineData("capture.region", "F1", HotkeyCatalog.CaptureRegionKeys)]
    [InlineData("capture.pinClipboard", "F3", HotkeyCatalog.PinClipboardKeys)]
    public void Legacy_default_key_is_migrated(string actionId, string legacyKeys, string expected)
    {
        var overrides = new Dictionary<string, string> { [actionId] = legacyKeys };

        var migrated = HotkeyDefaultMigration.ApplyLegacyDefaults(overrides);

        Assert.Equal(expected, overrides[actionId]);
        Assert.Contains(actionId, migrated);
    }

    [Fact]
    public void Legacy_migration_does_not_touch_user_customized_keys()
    {
        var overrides = new Dictionary<string, string>
        {
            ["capture.region"] = "Ctrl+Alt+Q",      // 用户自己改的
            ["capture.pinClipboard"] = "Ctrl+Alt+P", // 用户自己改的
        };

        var migrated = HotkeyDefaultMigration.ApplyLegacyDefaults(overrides);

        Assert.Empty(migrated);
        Assert.Equal("Ctrl+Alt+Q", overrides["capture.region"]);
        Assert.Equal("Ctrl+Alt+P", overrides["capture.pinClipboard"]);
    }

    [Fact]
    public void Legacy_migration_is_idempotent()
    {
        // 用"显式写了旧默认值"的配置（老用户存档的真实形态）。
        var overrides = new Dictionary<string, string>
        {
            ["capture.region"] = HotkeyCatalog.LegacyCaptureRegionKeys,
        };

        Assert.NotEmpty(HotkeyDefaultMigration.ApplyLegacyDefaults(overrides));
        // 第二次不得再迁移（此时值已是新默认键，不等于旧键）。
        Assert.Empty(HotkeyDefaultMigration.ApplyLegacyDefaults(overrides));
    }

    [Fact]
    public void Legacy_migration_does_nothing_when_no_override_exists()
    {
        // 没有显式配置的动作本来就拿动作表的默认值（已是新键），不该被迁移，
        // 也不该在配置里凭空造出一条 overrides。
        var overrides = new Dictionary<string, string>();

        Assert.Empty(HotkeyDefaultMigration.ApplyLegacyDefaults(overrides));
        Assert.Empty(overrides);
    }

    [Fact]
    public void ToOverrides_and_ToDisabled_round_trip()
    {
        var bindings = new List<HotkeyBinding>
        {
            new("capture.region", "F2", true),
            new("pin.closeAll", null, true),
            new("pin.toggleAll", "Shift+F3", false),
        };

        var overrides = HotkeyBindings.ToOverrides(bindings);
        Assert.Equal("F2", overrides["capture.region"]);
        Assert.False(overrides.ContainsKey("pin.closeAll"));

        Assert.Equal(["pin.toggleAll"], HotkeyBindings.ToDisabled(bindings));
    }

    [Fact]
    public void Match_dispatches_scoped_keys_and_ignores_other_scopes()
    {
        var bindings = HotkeyBindings.Resolve();

        // 遮罩里的 Enter 命中 overlay.confirm
        Assert.True(HotkeySpec.TryParse("Enter", out var enter, out _));
        Assert.Equal("overlay.confirm", HotkeyBindings.Match(bindings, enter, HotkeyScope.Overlay)?.Id);

        // 同一个 Enter 在贴图作用域里不该命中任何动作
        Assert.Null(HotkeyBindings.Match(bindings, enter, HotkeyScope.Pin));

        // 贴图里的 Ctrl+C / 空格 / 数字旋转都能命中
        Assert.True(HotkeySpec.TryParse("Ctrl+C", out var ctrlC, out _));
        Assert.Equal("pin.copy", HotkeyBindings.Match(bindings, ctrlC, HotkeyScope.Pin)?.Id);

        Assert.True(HotkeySpec.TryParse("Space", out var space, out _));
        Assert.Equal("pin.annotate", HotkeyBindings.Match(bindings, space, HotkeyScope.Pin)?.Id);

        Assert.True(HotkeySpec.TryParse("1", out var one, out _));
        Assert.Equal("pin.rotateCw", HotkeyBindings.Match(bindings, one, HotkeyScope.Pin)?.Id);
    }

    [Fact]
    public void Disabled_bindings_do_not_match()
    {
        var bindings = HotkeyBindings.Resolve(disabled: ["overlay.confirm"]);
        HotkeySpec.TryParse("Enter", out var enter, out _);

        Assert.Null(HotkeyBindings.Match(bindings, enter, HotkeyScope.Overlay));
    }
}

public class HotkeyConflictTests
{
    [Fact]
    public void Internal_duplicates_are_detected_and_block_saving()
    {
        var bindings = new List<HotkeyBinding>
        {
            new("capture.region", "Ctrl+Shift+A", true),
            new("capture.pinClipboard", "Shift+Ctrl+A", true), // 修饰键顺序不同但等价
        };

        var reports = HotkeyConflictDetector.DetectInternalDuplicates(bindings);

        Assert.Equal(2, reports.Count); // 两个动作各报一条
        Assert.All(reports, report => Assert.True(report.Blocks));
        Assert.Contains(reports, report => report.OtherActionId == "capture.pinClipboard");
        Assert.Equal("Ctrl+Shift+A", reports[0].Message.Split(' ')[0]);
    }

    [Fact]
    public void Same_key_in_different_scopes_is_not_a_conflict()
    {
        // Enter 既是遮罩的确认键，也可以出现在别的作用域里而不冲突
        var bindings = new List<HotkeyBinding>
        {
            new("overlay.confirm", "Enter", true),
            new("overlay.cancel", "Enter", true),
        };

        var conflict = HotkeyConflictDetector.DetectInternalDuplicates(bindings);
        Assert.Equal(2, conflict.Count);

        // 把第二个换到别的作用域后就不该再报（用两个不同作用域的绑定验证）
        var crossScope = new List<HotkeyBinding>
        {
            new("overlay.confirm", "Enter", true),
            new("pin.close", "Delete", true),
        };

        Assert.Empty(HotkeyConflictDetector.DetectInternalDuplicates(crossScope));
    }

    [Fact]
    public void Disabled_or_unbound_bindings_never_conflict()
    {
        var bindings = new List<HotkeyBinding>
        {
            new("capture.region", "Ctrl+Alt+Q", true),
            new("capture.pinClipboard", "Ctrl+Alt+Q", false),
            new("pin.closeAll", null, true),
        };

        Assert.Empty(HotkeyConflictDetector.DetectInternalDuplicates(bindings));
    }

    [Theory]
    [InlineData("Win+Shift+S", "系统截图")]
    [InlineData("Alt+A", "微信")]
    [InlineData("Ctrl+Space", "输入法")]
    [InlineData("F1", "Snipaste")]
    public void Known_conflict_sources_are_explained(string keys, string expectedOwnerFragment)
    {
        Assert.True(HotkeySpec.TryParse(keys, out var spec, out _));
        Assert.True(KnownConflictSources.TryLookup(spec, out string owner, out string advice));

        Assert.Contains(expectedOwnerFragment, owner);
        Assert.False(string.IsNullOrWhiteSpace(advice));
    }

    [Fact]
    public void Unknown_keys_have_no_known_source()
    {
        Assert.True(HotkeySpec.TryParse("Ctrl+Alt+F12", out var spec, out _));
        Assert.False(KnownConflictSources.TryLookup(spec, out _, out _));
    }

    [Fact]
    public void ExplainKnownSources_only_reports_global_scope()
    {
        var bindings = new List<HotkeyBinding>
        {
            new("capture.region", "F1", true),          // 全局，命中 Snipaste
            new("pin.rotateCw", "1", true),             // 贴图作用域，不该参与 L3
        };

        var reports = HotkeyConflictDetector.ExplainKnownSources(bindings);

        Assert.Single(reports);
        Assert.Equal("capture.region", reports[0].ActionId);
        Assert.Equal(HotkeyConflictLayer.KnownSource, reports[0].Layer);
        Assert.False(reports[0].Blocks);
    }

    [Theory]
    [InlineData("Alt+Tab")]
    [InlineData("Win+L")]
    [InlineData("Ctrl+Alt+Delete")]
    public void Reserved_keys_are_flagged(string keys)
    {
        Assert.True(HotkeySpec.TryParse(keys, out var spec, out _));
        Assert.True(ReservedHotkeys.IsReserved(spec, out string reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void DetectAll_combines_all_layers_and_summarize_counts_blocking_ones()
    {
        var bindings = new List<HotkeyBinding>
        {
            new("capture.region", "Alt+Tab", true),        // 保留键
            new("capture.pinClipboard", "F1", true),       // 与下面的 F1 内部重复 + 已知源
            new("pin.toggleAll", "F1", true),
        };

        var reports = HotkeyConflictDetector.DetectAll(bindings);

        Assert.Contains(reports, report => report.Layer == HotkeyConflictLayer.Reserved);
        Assert.Contains(reports, report => report.Layer == HotkeyConflictLayer.Internal);
        Assert.Contains(reports, report => report.Layer == HotkeyConflictLayer.KnownSource);
        Assert.Contains("必须修正", HotkeyConflictDetector.Summarize(reports));
    }

    [Fact]
    public void Summarize_says_clean_when_there_is_nothing_to_report()
    {
        Assert.Equal("未发现冲突。", HotkeyConflictDetector.Summarize([]));
    }
}
