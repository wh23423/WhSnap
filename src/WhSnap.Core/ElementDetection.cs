namespace WhSnap.Core;

/// <summary>
/// 颜色历史（取色器用）。最近使用的在前、自动去重、容量固定，
/// 可以序列化成一行文本存进配置，无需额外文件。纯逻辑，可单测。
/// </summary>
public sealed class ColorHistory
{
    public const int DefaultCapacity = 24;

    private readonly List<RgbColor> _items = [];

    public ColorHistory(int capacity = DefaultCapacity)
    {
        Capacity = Math.Clamp(capacity, 1, 200);
    }

    public int Capacity { get; }

    public IReadOnlyList<RgbColor> Items => _items;

    public int Count => _items.Count;

    /// <summary>加入一个颜色：已存在则提到最前，超出容量则丢弃最旧的。</summary>
    public void Add(RgbColor color)
    {
        _items.RemoveAll(existing => existing == color);
        _items.Insert(0, color);

        while (_items.Count > Capacity)
        {
            _items.RemoveAt(_items.Count - 1);
        }
    }

    public bool Remove(RgbColor color) => _items.Remove(color);

    public void Clear() => _items.Clear();

    public bool Contains(RgbColor color) => _items.Contains(color);

    /// <summary>序列化为逗号分隔的十六进制（存进 settings.json）。</summary>
    public string Serialize() => string.Join(',', _items.Select(color => color.ToHex()));

    /// <summary>
    /// 从配置文本恢复（顺序保持"最近在前"），无法解析的片段直接跳过——配置损坏不该影响启动。
    /// 注意：不能用逗号无脑切分，因为 rgb(0,122,255) 内部也有逗号。
    /// </summary>
    public void Load(string? text)
    {
        _items.Clear();

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (string token in text.Split([';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // 带括号的表示法（rgb/hsl）整段解析；否则按逗号切成多个 hex
            var segments = token.Contains('(')
                ? [token]
                : token.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (string segment in segments)
            {
                if (ColorValue.TryParse(segment, out var color) && !_items.Contains(color))
                {
                    _items.Add(color);
                }
            }
        }

        while (_items.Count > Capacity)
        {
            _items.RemoveAt(_items.Count - 1);
        }
    }

    public static ColorHistory FromText(string? text, int capacity = DefaultCapacity)
    {
        var history = new ColorHistory(capacity);
        history.Load(text);
        return history;
    }
}

/// <summary>取色器复制到剪贴板时的表示法。</summary>
public enum ColorCopyFormat
{
    Hex,
    Rgb,
    Hsl,
    Cmyk,

    /// <summary>四种表示法一起复制（多行）。</summary>
    All,
}

public static class ColorText
{
    public static ColorCopyFormat ParseFormat(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "rgb" => ColorCopyFormat.Rgb,
        "hsl" => ColorCopyFormat.Hsl,
        "cmyk" => ColorCopyFormat.Cmyk,
        "all" => ColorCopyFormat.All,
        _ => ColorCopyFormat.Hex,
    };

    public static string ToName(ColorCopyFormat format) => format switch
    {
        ColorCopyFormat.Rgb => "rgb",
        ColorCopyFormat.Hsl => "hsl",
        ColorCopyFormat.Cmyk => "cmyk",
        ColorCopyFormat.All => "all",
        _ => "hex",
    };

    public static string Format(RgbColor color, ColorCopyFormat format) => format switch
    {
        ColorCopyFormat.Rgb => ColorValue.ToRgbText(color),
        ColorCopyFormat.Hsl => ColorValue.ToHslText(color),
        ColorCopyFormat.Cmyk => ColorValue.ToCmykText(color),
        ColorCopyFormat.All => string.Join(Environment.NewLine, [
            color.ToHex().ToUpperInvariant(),
            ColorValue.ToRgbText(color),
            ColorValue.ToHslText(color),
            ColorValue.ToCmykText(color),
        ]),
        _ => color.ToHex().ToUpperInvariant(),
    };
}

/// <summary>界面元素候选（来自 UI Automation 或窗口矩形回退）。</summary>
public sealed record ElementCandidate(PhysicalRect Bounds, string Name, string Source, int Depth = 0)
{
    public string Describe() =>
        $"{Bounds}　{(string.IsNullOrWhiteSpace(Name) ? "(未命名)" : Name)}　[{Source}{(Depth > 0 ? $" 层级 {Depth}" : string.Empty)}]";
}

/// <summary>
/// 元素检测的纯逻辑部分：从候选里挑"光标下最合适的那一个"，以及 Tab 循环的顺序。
///
/// 真实候选由 UI Automation + 窗口矩形在 App 层采集（见 ElementLocator），
/// 但"选哪个"这一步必须可单测——否则元素检测就成了"看起来能用但说不清规则"的功能。
/// </summary>
public static class ElementDetection
{
    /// <summary>
    /// 选出默认候选：包含光标且**面积最小**的那个（UIA 的子元素天然比父窗口小，
    /// 所以"最小的那个"通常就是用户想选的按钮/输入框）。
    /// </summary>
    public static ElementCandidate? Choose(IReadOnlyList<ElementCandidate> candidates, PhysicalPoint point)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        ElementCandidate? best = null;
        long bestArea = long.MaxValue;

        foreach (var candidate in candidates)
        {
            if (!Contains(candidate.Bounds, point))
            {
                continue;
            }

            long area = (long)candidate.Bounds.Width * candidate.Bounds.Height;
            if (area < bestArea)
            {
                bestArea = area;
                best = candidate;
            }
        }

        // 光标不在任何候选里（例如点在桌面空白处）：退化为"包含光标的显示器"级别的候选
        return best ?? candidates.FirstOrDefault(candidate => Contains(candidate.Bounds, point));
    }

    /// <summary>
    /// Tab 循环顺序：由小到大（先选中最里层的控件，再逐级放大到父元素/窗口），
    /// 这样连按 Tab 就是"从小到大往外退"，符合"选错了就再按一下"的直觉。
    /// </summary>
    public static IReadOnlyList<ElementCandidate> OrderForCycle(IReadOnlyList<ElementCandidate> candidates, PhysicalPoint point)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Where(candidate => Contains(candidate.Bounds, point))
            .OrderBy(candidate => (long)candidate.Bounds.Width * candidate.Bounds.Height)
            .ThenBy(candidate => candidate.Depth)
            .ToList();
    }

    /// <summary>取循环里的下一个候选；<paramref name="current"/> 为 null 时返回第一个。</summary>
    public static ElementCandidate? Cycle(
        IReadOnlyList<ElementCandidate> candidates,
        PhysicalPoint point,
        ElementCandidate? current)
    {
        var ordered = OrderForCycle(candidates, point);
        if (ordered.Count == 0)
        {
            return null;
        }

        if (current is null)
        {
            return ordered[0];
        }

        int index = ordered.ToList().FindIndex(candidate => candidate.Bounds == current.Bounds);
        return index < 0 ? ordered[0] : ordered[(index + 1) % ordered.Count];
    }

    /// <summary>去重（UIA 与窗口矩形经常给出同一个矩形）。</summary>
    public static IReadOnlyList<ElementCandidate> Distinct(IEnumerable<ElementCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var seen = new HashSet<PhysicalRect>();
        var result = new List<ElementCandidate>();

        foreach (var candidate in candidates)
        {
            if (candidate.Bounds.IsEmpty || !seen.Add(candidate.Bounds))
            {
                continue;
            }

            result.Add(candidate);
        }

        return result;
    }

    private static bool Contains(PhysicalRect bounds, PhysicalPoint point) =>
        point.X >= bounds.X && point.X < bounds.Right && point.Y >= bounds.Y && point.Y < bounds.Bottom;
}
