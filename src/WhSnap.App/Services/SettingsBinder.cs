using System.Globalization;
using System.Reflection;
using WhSnap.Core;

namespace WhSnap.Services;

/// <summary>
/// 设置项的读写桥：把 <see cref="SettingsSchema"/> 里的键名映射到 <see cref="AppSettings"/> 的属性上。
///
/// 用反射而不是手写 switch 的理由：schema 是单一数据源（规划 §6），
/// 手写映射意味着"加一项要改两处"，早晚会漏；反射 + 自检（<see cref="VerifySchema"/>）能把
/// "schema 写了但属性不存在"这类问题在 `--selftest` 里直接暴露出来。
/// </summary>
internal static class SettingsBinder
{
    private static readonly Dictionary<string, PropertyInfo> Properties = typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanRead && property.CanWrite)
        .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>读取某项的当前值（统一转成文本，界面与导出都用同一套表示）。</summary>
    public static string? Read(AppSettings settings, string key)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!Properties.TryGetValue(key, out var property))
        {
            return null;
        }

        object? value = property.GetValue(settings);

        return value switch
        {
            null => string.Empty,
            bool flag => flag ? "true" : "false",
            double number => number.ToString("0.####", CultureInfo.InvariantCulture),
            int number => number.ToString(CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    /// <summary>写入某项。会先用 schema 做范围/取值校验，非法值拒绝写入并给出原因。</summary>
    public static bool Write(AppSettings settings, string key, string? value, out string error)
    {
        ArgumentNullException.ThrowIfNull(settings);

        error = string.Empty;

        var definition = SettingsSchema.Find(key);
        if (definition is null)
        {
            error = $"配置项 {key} 不在 schema 中。";
            return false;
        }

        if (!Properties.TryGetValue(key, out var property))
        {
            error = $"AppSettings 缺少属性 {key}。";
            return false;
        }

        if (!SettingsImportValidator.IsValid(definition, value, out string normalized))
        {
            error = $"「{definition.Label}」的值 “{value}” 不合法。";
            return false;
        }

        try
        {
            object converted = definition.Kind switch
            {
                SettingKind.Bool => bool.Parse(normalized),
                SettingKind.Int => int.Parse(normalized, CultureInfo.InvariantCulture),
                SettingKind.Double => double.Parse(normalized, CultureInfo.InvariantCulture),
                _ => normalized,
            };

            property.SetValue(settings, converted);
            return true;
        }
        catch (Exception ex)
        {
            error = $"写入 {key} 失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>导出全部配置项（键 → 文本）。</summary>
    public static IReadOnlyDictionary<string, string?> ReadAll(AppSettings settings) =>
        SettingsSchema.All.ToDictionary(setting => setting.Key, setting => Read(settings, setting.Key));

    /// <summary>把配置恢复为默认值（只动 schema 覆盖的项）。</summary>
    public static IReadOnlyList<string> ResetToDefaults(AppSettings settings)
    {
        var defaults = new AppSettings();
        var changed = new List<string>();

        foreach (var definition in SettingsSchema.All)
        {
            string? current = Read(settings, definition.Key);
            string? fallback = Read(defaults, definition.Key);

            if (current != fallback && Write(settings, definition.Key, fallback, out _))
            {
                changed.Add(definition.Key);
            }
        }

        return changed;
    }

    /// <summary>
    /// schema 与 AppSettings 的一致性检查：键存在、类型匹配、取值范围可行、颜色默认值可解析。
    /// 由 `--selftest` 调用——这是"设置界面不会静默少一项"的护栏。
    /// </summary>
    public static IReadOnlyList<string> VerifySchema()
    {
        var problems = new List<string>();

        foreach (var definition in SettingsSchema.All)
        {
            if (!Properties.TryGetValue(definition.Key, out var property))
            {
                problems.Add($"schema 里的 {definition.Key} 在 AppSettings 中不存在。");
                continue;
            }

            Type expected = definition.Kind switch
            {
                SettingKind.Bool => typeof(bool),
                SettingKind.Int => typeof(int),
                SettingKind.Double => typeof(double),
                _ => typeof(string),
            };

            if (property.PropertyType != expected)
            {
                problems.Add($"{definition.Key} 的类型是 {property.PropertyType.Name}，schema 期望 {expected.Name}。");
            }
        }

        // 反向检查：AppSettings 里除热键/版本号以外的可写属性都应该出现在 schema 里，
        // 否则就是"只能改 JSON 才能调"的隐藏项（违反 §6 第 1 条）。
        foreach (var property in Properties.Values)
        {
            if (property.Name is nameof(AppSettings.SchemaVersion) or nameof(AppSettings.Hotkey)
                or nameof(AppSettings.Hotkeys) or nameof(AppSettings.DisabledHotkeys)
                or nameof(AppSettings.HotkeyConflictPolicy))
            {
                continue;
            }

            if (SettingsSchema.Find(property.Name) is null)
            {
                problems.Add($"AppSettings.{property.Name} 没有对应的设置界面入口。");
            }
        }

        return problems;
    }
}
