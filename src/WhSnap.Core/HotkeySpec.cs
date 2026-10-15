using System.Globalization;

namespace WhSnap.Core;

/// <summary>
/// 快捷键的规范化表示与文本解析（"Ctrl+Shift+A" / "F1" / "Alt+PrtScn"）。
/// 解析结果是注册与冲突检测的唯一依据，纯逻辑可单测。
/// </summary>
public readonly record struct HotkeySpec(uint Modifiers, uint VirtualKey)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>RegisterHotKey 的 MOD_NOREPEAT，避免长按触发洪水。</summary>
    public const uint ModNoRepeat = 0x4000;

    public uint RegisterModifiers => Modifiers | ModNoRepeat;

    public bool IsValid => VirtualKey != 0;

    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PRINTSCREEN"] = 0x2C,
        ["PRTSCN"] = 0x2C,
        ["PRTSCR"] = 0x2C,
        ["SNAPSHOT"] = 0x2C,
        ["INSERT"] = 0x2D,
        ["INS"] = 0x2D,
        ["DELETE"] = 0x2E,
        ["DEL"] = 0x2E,
        ["HOME"] = 0x24,
        ["END"] = 0x23,
        ["PAGEUP"] = 0x21,
        ["PGUP"] = 0x21,
        ["PAGEDOWN"] = 0x22,
        ["PGDN"] = 0x22,
        ["SPACE"] = 0x20,
        ["TAB"] = 0x09,
        ["ENTER"] = 0x0D,
        ["RETURN"] = 0x0D,
        ["ESC"] = 0x1B,
        ["ESCAPE"] = 0x1B,
        ["BACKSPACE"] = 0x08,
        ["UP"] = 0x26,
        ["DOWN"] = 0x28,
        ["LEFT"] = 0x25,
        ["RIGHT"] = 0x27,
        ["`"] = 0xC0,
        ["-"] = 0xBD,
        ["="] = 0xBB,
        ["["] = 0xDB,
        ["]"] = 0xDD,
        ["\\"] = 0xDC,
        [";"] = 0xBA,
        ["'"] = 0xDE,
        [","] = 0xBC,
        ["."] = 0xBE,
        ["/"] = 0xBF,
    };

    public static bool TryParse(string? text, out HotkeySpec spec, out string? error)
    {
        spec = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "快捷键为空。";
            return false;
        }

        uint modifiers = 0;
        uint key = 0;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            error = "快捷键为空。";
            return false;
        }

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            bool isLast = i == parts.Length - 1;

            if (!isLast)
            {
                switch (part.ToUpperInvariant())
                {
                    case "CTRL":
                    case "CONTROL":
                        modifiers |= ModControl;
                        break;
                    case "ALT":
                        modifiers |= ModAlt;
                        break;
                    case "SHIFT":
                        modifiers |= ModShift;
                        break;
                    case "WIN":
                    case "WINDOWS":
                    case "META":
                        modifiers |= ModWin;
                        break;
                    default:
                        error = $"无法识别的修饰键：“{part}”。";
                        return false;
                }

                continue;
            }

            if (!TryParseKey(part, out key))
            {
                error = $"无法识别的按键：“{part}”。";
                return false;
            }
        }

        if (key == 0)
        {
            error = "快捷键必须以一个非修饰键结尾。";
            return false;
        }

        spec = new HotkeySpec(modifiers, key);
        return true;
    }

    private static bool TryParseKey(string token, out uint key)
    {
        key = 0;

        // F1..F24
        if (token.Length is >= 2 and <= 3 && (token[0] is 'F' or 'f')
            && int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int fn)
            && fn is >= 1 and <= 24)
        {
            key = (uint)(0x70 + fn - 1);
            return true;
        }

        // A-Z / 0-9
        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                key = c;
                return true;
            }
        }

        return NamedKeys.TryGetValue(token, out key);
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if ((Modifiers & ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((Modifiers & ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((Modifiers & ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & ModWin) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }

    public static string KeyName(uint vk) => vk switch
    {
        >= 0x70 and <= 0x87 => $"F{vk - 0x70 + 1}",
        >= 'A' and <= 'Z' => ((char)vk).ToString(),
        >= '0' and <= '9' => ((char)vk).ToString(),
        0x2C => "PrtScn",
        0x2D => "Insert",
        0x2E => "Delete",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x20 => "Space",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Esc",
        0x08 => "Backspace",
        0x26 => "Up",
        0x28 => "Down",
        0x25 => "Left",
        0x27 => "Right",
        _ => $"VK(0x{vk:X2})",
    };
}
