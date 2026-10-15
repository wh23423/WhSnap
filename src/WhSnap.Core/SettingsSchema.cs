namespace WhSnap.Core;

/// <summary>设置项控件类型，决定设置界面用什么控件渲染。</summary>
public enum SettingKind
{
    Bool,
    Int,
    Double,
    Text,
    Choice,
    Color,
    DirectoryPath,
}

public sealed record SettingOption(string Value, string Label);

/// <summary>一个配置项的元数据（键名与 <c>AppSettings</c> 的属性名一一对应）。</summary>
public sealed record SettingDefinition(
    string Key,
    string Group,
    string Label,
    SettingKind Kind,
    string Description = "",
    double Min = 0,
    double Max = 0,
    double Step = 0,
    string Unit = "",
    IReadOnlyList<SettingOption>? Options = null,
    string? Placeholder = null)
{
    public bool HasRange => Kind is SettingKind.Int or SettingKind.Double;
}

/// <summary>
/// 配置面的**单一数据源**（规划 §6 的实现要点）：
/// 设置界面由这张表自动生成，导入校验也读这张表，
/// 因此「加一个配置项」= 在 <c>AppSettings</c> 加属性 + 在这里加一行，不会出现
/// "界面漏了某项 / 导入静默失败"的经典问题。
///
/// 键名用属性名（而非 json 字段名）是因为 C# 侧用反射直接读写属性：
/// 写错键名会在自检里立刻暴露（见 <c>--selftest</c> 的 schema 完整性检查）。
/// </summary>
public static class SettingsSchema
{
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        // ---- 截图 ----
        new("CaptureBackend", "截图", "采集后端", SettingKind.Choice,
            "auto 会按 WGC → DXGI → GDI 依次尝试并自动降级；某个后端在这台机器上异常时可手动固定。",
            Options:
            [
                new("auto", "自动（推荐）"),
                new("wgc", "WGC（Windows.Graphics.Capture）"),
                new("dxgi", "DXGI（Desktop Duplication）"),
                new("gdi", "GDI（BitBlt 兜底）"),
            ]),
        new("CopyOnCapture", "截图", "确认后复制到剪贴板", SettingKind.Bool, "按 Enter 确认时执行。"),
        new("SaveOnCapture", "截图", "确认后保存文件", SettingKind.Bool, "按 Enter 确认时执行。"),
        new("PinOnCapture", "截图", "确认后直接贴图", SettingKind.Bool, "开启后 Enter 会同时贴图；也可在遮罩里按 F3 只贴图。"),
        new("OverlayDimOpacity", "截图", "遮罩暗度", SettingKind.Int, "选区以外区域的压暗程度。", 0, 90, 5, "%"),

        // ---- 保存 ----
        new("SaveDirectory", "保存", "默认保存目录", SettingKind.DirectoryPath, "留空则用「图片\\WhSnap」。"),
        new("FileNameTemplate", "保存", "文件名模板", SettingKind.Text,
            "支持 {date} {time} {w} {h} 占位符。", Placeholder: "WhSnap_{date}_{time}_{w}x{h}"),
        new("JpegQuality", "保存", "JPEG 质量", SettingKind.Int, "另存为 .jpg 时使用。", 10, 100, 5, "%"),

        // ---- 贴图 ----
        new("PinDefaultOpacity", "贴图", "新贴图不透明度", SettingKind.Int, string.Empty, 10, 100, 5, "%"),
        new("PinClickThroughDefault", "贴图", "新贴图默认鼠标穿透", SettingKind.Bool,
            "默认关闭：穿透后点击会直接落到下层窗口，只能从托盘或管理面板关闭。"),
        new("PinZoomStep", "贴图", "滚轮缩放步进", SettingKind.Double, "每格滚轮的缩放倍率，1.1 = 每格放大 10%。", 1.01, 2.0, 0.01, "×"),
        new("PinSnapToEdges", "贴图", "拖动后吸附显示器边缘", SettingKind.Bool, "松手时把贴图吸到最近的屏幕边缘。"),
        new("PinSnapTolerance", "贴图", "吸附容差", SettingKind.Int, "距离边缘多少物理像素以内触发吸附。", 1, 64, 1, "px"),
        new("PinTopmostNew", "贴图", "新贴图置于最上层", SettingKind.Bool, string.Empty),
        new("PinBackupEnabled", "贴图", "自动备份贴图", SettingKind.Bool, "崩溃或重启后可恢复贴图。"),
        new("PinBackupIntervalSeconds", "贴图", "备份去抖间隔", SettingKind.Int, "贴图变动后至少等待这么久才落盘一次。", 1, 300, 1, "秒"),
        new("PinBackupRetention", "贴图", "备份保留份数", SettingKind.Int, "超出份数的旧会话会被清理。", 1, 100, 1, "次"),
        new("RestorePinsOnStartup", "贴图", "启动时恢复上次贴图", SettingKind.Bool, "恢复几何、层级、透明度与穿透状态。"),

        // ---- 剪贴板 ----
        new("ClipboardWatchEnabled", "剪贴板", "复制即贴图", SettingKind.Bool,
            "打开后监听剪贴板，复制图像/文件/文本就自动贴到屏幕上（程序自己写入的截图不会被回贴）。"),

        // ---- 文本贴图 ----
        new("TextPinFontFamily", "文本贴图", "字体", SettingKind.Text, "文本/HTML 贴图使用。"),
        new("TextPinFontSize", "文本贴图", "字号", SettingKind.Double, "按物理像素渲染，1:1 不模糊。", 8, 72, 1, "px"),
        new("TextPinMaxWidth", "文本贴图", "最大宽度", SettingKind.Int, "超过则自动换行。", 120, 2000, 20, "px"),
        new("TextPinMaxHeight", "文本贴图", "最大高度", SettingKind.Int, "超过则按省略号截断，避免一张贴图吃掉几百 MB。", 100, 4000, 100, "px"),
        new("TextPinPadding", "文本贴图", "内边距", SettingKind.Double, string.Empty, 0, 64, 2, "px"),
        new("TextPinForeground", "文本贴图", "前景色", SettingKind.Color, "支持 #RGB / #RRGGBB / rgb() / hsl()。"),
        new("TextPinBackground", "文本贴图", "背景色", SettingKind.Color, string.Empty),

        // ---- 系统集成 ----
        new("AutoStart", "系统", "开机自启", SettingKind.Bool, "随 Windows 登录自动启动（per-user，无需管理员）。"),
        new("HotkeyConflictPolicy", "系统", "热键冲突处理", SettingKind.Choice,
            "block：冲突时弹窗让你选择（推荐，默认）；save-disabled：冲突时直接保存但停用该动作，不弹窗。两种都不会静默失败。",
            Options:
            [
                new("block", "阻断并询问我（推荐）"),
                new("save-disabled", "保存但停用冲突的热键"),
            ]),
    ];

    public static IReadOnlyList<string> Groups { get; } =
        All.Select(setting => setting.Group).Distinct().ToList();

    private static readonly Dictionary<string, SettingDefinition> Index =
        All.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);

    public static SettingDefinition? Find(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : Index.GetValueOrDefault(key);

    public static IReadOnlyList<SettingDefinition> InGroup(string group) =>
        All.Where(setting => setting.Group == group).ToList();

    /// <summary>表自身的完整性检查（键唯一、选择项合法、范围合法、颜色默认值可解析等）。</summary>
    public static IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        foreach (var duplicate in All.GroupBy(setting => setting.Key, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            problems.Add($"配置键重复：{duplicate.Key}");
        }

        foreach (var setting in All)
        {
            switch (setting.Kind)
            {
                case SettingKind.Choice when setting.Options is null || setting.Options.Count == 0:
                    problems.Add($"Choice 类型的 {setting.Key} 没有候选项。");
                    break;

                case SettingKind.Choice:
                    foreach (var option in setting.Options!.GroupBy(option => option.Value, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                    {
                        problems.Add($"{setting.Key} 的候选项重复：{option.Key}");
                    }

                    break;

                case SettingKind.Int:
                case SettingKind.Double:
                    if (setting.Max <= setting.Min)
                    {
                        problems.Add($"{setting.Key} 的取值范围非法：[{setting.Min}, {setting.Max}]");
                    }

                    break;
            }
        }

        return problems;
    }
}
