using System.Text;

namespace WhSnap.Services;

/// <summary>
/// 用户可见文案里的键位占位符：写成 <c>{动作 id}</c>（例如 <c>"等价于在遮罩里按 {overlay.pin}"</c>），
/// 显示前换成**当前**绑定的键位文本。
///
/// 为什么必须这样做：键位是可配置的，任何把默认键（F1 / F3 / Ctrl+C …）直接写进文案的地方，
/// 都会在用户改键之后变成错的说明——"照着提示按却没反应"是最伤信任的一类缺陷。
/// 新增涉及键位的文案请一律使用占位符，不要写字面键名。
/// </summary>
internal static class HotkeyPlaceholder
{
    /// <summary>把 <paramref name="text"/> 里的 <c>{动作 id}</c> 换成 <paramref name="keyText"/> 的结果。</summary>
    public static string Replace(string text, Func<string, string> keyText)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);
        int index = 0;

        while (index < text.Length)
        {
            int open = text.IndexOf('{', index);

            if (open < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            int close = text.IndexOf('}', open + 1);

            if (close < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            string actionId = text[(open + 1)..close];

            // 只替换"看起来像动作 id"的占位符（小写字母、点、连字符），避免误伤正常文案里的花括号。
            if (!IsActionId(actionId))
            {
                builder.Append(text, index, close - index + 1);
                index = close + 1;
                continue;
            }

            builder.Append(text, index, open - index);
            builder.Append(keyText(actionId));
            index = close + 1;
        }

        return builder.ToString();
    }

    private static bool IsActionId(string candidate)
    {
        if (candidate.Length == 0 || candidate.IndexOf('.') < 0)
        {
            return false;
        }

        // 动作 id 是 camelCase（overlay.pickColor / pin.showPanel / annotation.tool.text），
        // 所以大小写字母都要放行——只放小写会让这些占位符原样显示成 "{overlay.pickColor}"。
        // 仍然要求"带点"，这样文件名模板里的 {date} {w} {h} 之类不会被误替换。
        foreach (char ch in candidate)
        {
            if (!char.IsAsciiLetter(ch) && ch != '.' && ch != '-')
            {
                return false;
            }
        }

        return true;
    }
}
