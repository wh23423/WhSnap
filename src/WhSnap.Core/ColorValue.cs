using System.Globalization;

namespace WhSnap.Core;

/// <summary>24 位 sRGB 颜色。</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public override string ToString() => ToHex();
}

/// <summary>
/// 颜色值的解析与多表示法换算。
/// 用途：① 取色器显示 HEX/RGB/HSL/CMYK 并记录历史；② 剪贴板里的纯文本如果是一个颜色值，
/// 可以直接贴成色块；③ 颜色历史去重比较。纯逻辑，可单测。
/// </summary>
public static class ColorValue
{
    /// <summary>
    /// 解析常见颜色写法：#RGB / #RRGGBB / #AARRGGBB / #RRGGBBAA / rgb(r,g,b) / rgba(r,g,b,a) /
    /// hsl(h,s%,l%) / "r,g,b" / "r g b"。无法识别时返回 false。
    /// </summary>
    public static bool TryParse(string? text, out RgbColor color)
    {
        color = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text.Trim();

        if (value.StartsWith('#'))
        {
            return TryParseHex(value, out color);
        }

        if (value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseFunction(value, "rgb", out color);
        }

        if (value.StartsWith("hsl", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseHslFunction(value, out color);
        }

        // "255,0,0" / "255 0 0"
        var parts = value.Split([',', ' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 3
            && TryByte(parts[0], out byte r)
            && TryByte(parts[1], out byte g)
            && TryByte(parts[2], out byte b))
        {
            color = new RgbColor(r, g, b);
            return true;
        }

        return false;
    }

    private static bool TryParseHex(string value, out RgbColor color)
    {
        color = default;
        string hex = value[1..];

        if (!hex.All(Uri.IsHexDigit))
        {
            return false;
        }

        switch (hex.Length)
        {
            case 3:
                color = new RgbColor(
                    (byte)(Convert.ToInt32(new string(hex[0], 2), 16)),
                    (byte)(Convert.ToInt32(new string(hex[1], 2), 16)),
                    (byte)(Convert.ToInt32(new string(hex[2], 2), 16)));
                return true;

            case 6:
                color = new RgbColor(
                    (byte)Convert.ToInt32(hex[..2], 16),
                    (byte)Convert.ToInt32(hex.Substring(2, 2), 16),
                    (byte)Convert.ToInt32(hex.Substring(4, 2), 16));
                return true;

            // 8 位时按 AARRGGBB（Windows 习惯），忽略 alpha
            case 8:
                color = new RgbColor(
                    (byte)Convert.ToInt32(hex.Substring(2, 2), 16),
                    (byte)Convert.ToInt32(hex.Substring(4, 2), 16),
                    (byte)Convert.ToInt32(hex.Substring(6, 2), 16));
                return true;

            default:
                return false;
        }
    }

    private static bool TryParseFunction(string value, string name, out RgbColor color)
    {
        color = default;

        int open = value.IndexOf('(');
        int close = value.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return false;
        }

        string head = value[..open].Trim();
        if (!head.Equals(name, StringComparison.OrdinalIgnoreCase) && !head.Equals(name + "a", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = value[(open + 1)..close]
            .Split([',', '/', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 3)
        {
            return false;
        }

        if (!TryByte(parts[0], out byte r) || !TryByte(parts[1], out byte g) || !TryByte(parts[2], out byte b))
        {
            return false;
        }

        color = new RgbColor(r, g, b);
        return true;
    }

    private static bool TryParseHslFunction(string value, out RgbColor color)
    {
        color = default;

        int open = value.IndexOf('(');
        int close = value.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return false;
        }

        string head = value[..open].Trim();
        if (!head.Equals("hsl", StringComparison.OrdinalIgnoreCase) && !head.Equals("hsla", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = value[(open + 1)..close]
            .Split([',', '/', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 3
            || !TryDouble(parts[0], out double h)
            || !TryDouble(parts[1].TrimEnd('%'), out double s)
            || !TryDouble(parts[2].TrimEnd('%'), out double l))
        {
            return false;
        }

        color = FromHsl(h, s, l);
        return true;
    }

    private static bool TryByte(string token, out byte value)
    {
        value = 0;
        if (!TryDouble(token.TrimEnd('%'), out double parsed))
        {
            return false;
        }

        if (parsed < 0)
        {
            parsed = 0;
        }
        else if (parsed > 255)
        {
            parsed = 255;
        }

        value = (byte)Math.Round(parsed, MidpointRounding.AwayFromZero);
        return true;
    }

    private static bool TryDouble(string token, out double value) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>RGB → HSL。H 为 0–360，S/L 为 0–100。</summary>
    public static (double H, double S, double L) ToHsl(RgbColor color)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double l = (max + min) / 2.0;
        double h = 0;
        double s = 0;

        if (delta > 1e-9)
        {
            s = l > 0.5
                ? delta / (2.0 - max - min)
                : delta / (max + min);

            if (Math.Abs(max - r) < 1e-9)
            {
                h = ((g - b) / delta) % 6;
            }
            else if (Math.Abs(max - g) < 1e-9)
            {
                h = ((b - r) / delta) + 2;
            }
            else
            {
                h = ((r - g) / delta) + 4;
            }

            h *= 60;
            if (h < 0)
            {
                h += 360;
            }
        }

        return (Math.Round(h, 1), Math.Round(s * 100, 1), Math.Round(l * 100, 1));
    }

    /// <summary>HSL → RGB（H 0–360，S/L 0–100）。</summary>
    public static RgbColor FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 100) / 100.0;
        l = Math.Clamp(l, 0, 100) / 100.0;

        double c = (1 - Math.Abs((2 * l) - 1)) * s;
        double x = c * (1 - Math.Abs(((h / 60) % 2) - 1));
        double m = l - (c / 2);

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return new RgbColor(
            ToByte(r + m),
            ToByte(g + m),
            ToByte(b + m));

        static byte ToByte(double value) =>
            (byte)Math.Clamp(Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
    }

    /// <summary>RGB → CMYK（均为 0–100）。</summary>
    public static (double C, double M, double Y, double K) ToCmyk(RgbColor color)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        double k = 1 - Math.Max(r, Math.Max(g, b));
        if (k >= 1 - 1e-9)
        {
            return (0, 0, 0, 100);
        }

        double c = (1 - r - k) / (1 - k);
        double m = (1 - g - k) / (1 - k);
        double y = (1 - b - k) / (1 - k);

        return (Math.Round(c * 100, 1), Math.Round(m * 100, 1), Math.Round(y * 100, 1), Math.Round(k * 100, 1));
    }

    public static string ToRgbText(RgbColor color) => $"rgb({color.R}, {color.G}, {color.B})";

    public static string ToHslText(RgbColor color)
    {
        var (h, s, l) = ToHsl(color);
        return $"hsl({h:0.#}, {s:0.#}%, {l:0.#}%)";
    }

    public static string ToCmykText(RgbColor color)
    {
        var (c, m, y, k) = ToCmyk(color);
        return $"cmyk({c:0.#}%, {m:0.#}%, {y:0.#}%, {k:0.#}%)";
    }

    /// <summary>取色器 HUD 用的单行文本。</summary>
    public static string Describe(RgbColor color) =>
        $"{color.ToHex()}　{ToRgbText(color)}　{ToHslText(color)}";
}
