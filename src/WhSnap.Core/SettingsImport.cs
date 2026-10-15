using System.Globalization;

namespace WhSnap.Core;

/// <summary>导入配置的校验结果。</summary>
public sealed record SettingsImportResult(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Unknown,
    IReadOnlyList<string> Invalid)
{
    public bool HasProblems => Unknown.Count > 0 || Invalid.Count > 0;

    public string Describe() =>
        $"可应用 {Applied.Count} 项"
        + (Unknown.Count > 0 ? $"；忽略未知项 {Unknown.Count} 个（{string.Join("、", Unknown.Take(5))}）" : string.Empty)
        + (Invalid.Count > 0 ? $"；非法值 {Invalid.Count} 个（{string.Join("、", Invalid.Take(5))}）" : string.Empty);
}

/// <summary>
/// 配置导入校验（规划 §6 第 4 条：导入时做校验并报告无效项）。
/// 只做"这一项能不能用"的判断，实际写回由 App 层按属性类型完成——
/// 这样校验逻辑纯函数可测，也不会把 JSON 反射那套东西塞进领域层。
/// </summary>
public static class SettingsImportValidator
{
    public static SettingsImportResult Validate(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var applied = new List<string>();
        var unknown = new List<string>();
        var invalid = new List<string>();

        foreach (var (key, value) in values)
        {
            var definition = SettingsSchema.Find(key);
            if (definition is null)
            {
                unknown.Add(key);
                continue;
            }

            if (IsValid(definition, value, out _))
            {
                applied.Add(key);
            }
            else
            {
                invalid.Add(key);
            }
        }

        return new SettingsImportResult(applied, unknown, invalid);
    }

    /// <summary>单值校验，同时给出规范化后的文本（供写回使用）。</summary>
    public static bool IsValid(SettingDefinition definition, string? value, out string normalized)
    {
        ArgumentNullException.ThrowIfNull(definition);

        normalized = value?.Trim() ?? string.Empty;

        switch (definition.Kind)
        {
            case SettingKind.Bool:
                if (bool.TryParse(normalized, out bool flag))
                {
                    normalized = flag ? "true" : "false";
                    return true;
                }

                return false;

            case SettingKind.Int:
                if (!int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer))
                {
                    return false;
                }

                if (definition.HasRange && (integer < definition.Min || integer > definition.Max))
                {
                    return false;
                }

                normalized = integer.ToString(CultureInfo.InvariantCulture);
                return true;

            case SettingKind.Double:
                if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                {
                    return false;
                }

                if (definition.HasRange && (number < definition.Min || number > definition.Max))
                {
                    return false;
                }

                normalized = number.ToString("0.####", CultureInfo.InvariantCulture);
                return true;

            case SettingKind.Choice:
                if (definition.Options is null)
                {
                    return false;
                }

                foreach (var option in definition.Options)
                {
                    if (string.Equals(option.Value, normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        normalized = option.Value;
                        return true;
                    }
                }

                return false;

            case SettingKind.Color:
                return ColorValue.TryParse(normalized, out _);

            case SettingKind.DirectoryPath:
                // 空字符串是合法值（表示"用默认目录"）
                return normalized.Length == 0 || !normalized.Contains('\0');

            default:
                return true;
        }
    }
}
