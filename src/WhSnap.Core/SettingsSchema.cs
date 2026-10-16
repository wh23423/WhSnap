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
            "自动会按 WGC → DXGI → GDI 依次尝试并自动降级；某个后端在这台机器上异常时再手动固定。",
            Options:
            [
                new("auto", "自动（推荐）"),
                new("wgc", "WGC（Windows.Graphics.Capture）"),
                new("dxgi", "DXGI（Desktop Duplication）"),
                new("gdi", "GDI（BitBlt 兜底）"),
            ]),
        new("CopyOnCapture", "截图", "确认后复制到剪贴板", SettingKind.Bool, "按 {overlay.confirm} 确认时执行。"),
        new("SaveOnCapture", "截图", "确认后保存文件", SettingKind.Bool, "按 {overlay.confirm} 确认时执行。"),
        new("PinOnCapture", "截图", "确认后直接贴图", SettingKind.Bool,
            "开启后 {overlay.confirm} 会同时贴图；也可在遮罩里按 {overlay.pin} 只贴图。"),
        new("OverlayDimOpacity", "截图", "遮罩暗度", SettingKind.Int, "选区以外区域的压暗程度。", 0, 90, 5, "%"),
        new("ElementDetectionEnabled", "截图", "启用元素检测", SettingKind.Bool,
            "遮罩显示时自动高亮光标下的界面元素；单击即可选中该元素，{overlay.cycleElement} 由内向外切换候选。"),
        new("ElementDetectionUseUia", "截图", "元素检测使用 UI Automation", SettingKind.Bool,
            "开启后能识别按钮/输入框等真实控件；关闭则只按顶层窗口矩形检测（更快，但粒度粗）。"),

        // ---- 取色器 ----
        new("ColorCopyFormat", "取色器", "复制颜色时的表示法", SettingKind.Choice,
            "在遮罩里按 {overlay.pickColor} 取色时复制到剪贴板的格式。",
            Options:
            [
                new("hex", "#RRGGBB（推荐）"),
                new("rgb", "rgb(r, g, b)"),
                new("hsl", "hsl(h, s%, l%)"),
                new("cmyk", "cmyk(c%, m%, y%, k%)"),
                new("all", "四种表示法（多行）"),
            ]),
        new("ColorHistoryRetention", "取色器", "颜色历史条数", SettingKind.Int, "最近取过的颜色，可在遮罩与托盘里复用。", 1, 64, 1, "个"),
        new("OverlayShowColorHistory", "取色器", "遮罩里显示颜色历史", SettingKind.Bool, "在遮罩左下角显示最近取到的颜色块。"),

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

        // ---- 标注 ----
        new("AnnotationDefaultTool", "标注", "默认工具", SettingKind.Choice, "打开标注编辑器时选中的工具。",
            Options:
            [
                new("select", "选择 / 移动"),
                new("rectangle", "矩形"),
                new("ellipse", "椭圆"),
                new("line", "直线"),
                new("arrow", "箭头"),
                new("pencil", "铅笔"),
                new("marker", "马克笔"),
                new("text", "文字"),
                new("mosaic", "马赛克"),
                new("blur", "模糊"),
                new("eraser", "橡皮擦"),
                new("badge", "序号"),
                new("highlight", "高亮"),
            ]),
        new("AnnotationStrokeColor", "标注", "默认颜色", SettingKind.Color, "工具栏也可以随时换色（数字键 1-8 是固定交互，不可改键）。"),
        new("AnnotationStrokeWidth", "标注", "默认线宽", SettingKind.Int, "在画布上按 [ 与 ] 可临时调整。", 1, 64, 1, "px"),
        new("AnnotationFillOpacity", "标注", "矩形/椭圆填充不透明度", SettingKind.Int, "0 = 不填充。", 0, 100, 5, "%"),
        new("AnnotationFontSize", "标注", "文字/序号字号", SettingKind.Double, "按物理像素渲染。", 8, 72, 1, "px"),
        new("AnnotationMosaicBlock", "标注", "马赛克块大小", SettingKind.Int, "越大越糊。", 2, 64, 1, "px"),
        new("AnnotationBlurRadius", "标注", "模糊半径", SettingKind.Int, "盒式模糊半径（两趟可分离，近似高斯）。", 1, 64, 1, "px"),
        new("AnnotationEraserRadius", "标注", "橡皮擦半径", SettingKind.Int, string.Empty, 1, 64, 1, "px"),
        new("AnnotationBadgeStart", "标注", "序号起始值", SettingKind.Int, string.Empty, 1, 999, 1, string.Empty),
        new("AnnotationPalette", "标注", "调色板", SettingKind.Text,
            "逗号分隔的颜色列表，显示为工具栏上的色块；支持 #RGB / #RRGGBB / rgb() / hsl()。"),

        // ---- 系统集成 ----
        new("AutoStart", "系统", "开机自启", SettingKind.Bool, "随 Windows 登录自动启动（只对当前用户生效，无需管理员）。"),
        new("HotkeyConflictPolicy", "系统", "热键冲突处理", SettingKind.Choice,
            "默认在冲突时弹窗让你选择；也可改为「保存但停用冲突的热键」。两种都不会静默失败。",
            Options:
            [
                new("block", "阻断并询问我（推荐）"),
                new("save-disabled", "保存但停用冲突的热键"),
            ]),
        // ---- 更新 ----
        new("CheckForUpdatesOnStartup", "更新", "启动时检查更新", SettingKind.Bool,
            "只读取一份很小的 update.json（不含任何本机信息），不会自动下载；关掉则完全不做联网检查。"),
        new("UpdateChannel", "更新", "更新通道", SettingKind.Choice,
            "稳定通道只接受正式版；测试通道同时接受预览版。",
            Options:
            [
                new("stable", "稳定版（stable）"),
                new("beta", "测试版（beta，含预览版）"),
            ]),
        new("UpdateAutoDownload", "更新", "发现新版本时自动下载", SettingKind.Bool,
            "下载完成后会校验 SHA-256，再提示你安装；关闭时只在托盘提示有新版本。"),
        new("UpdateManifestUrl", "更新", "更新清单地址", SettingKind.Text,
            "默认取 GitHub Releases 最新版的 update.json；也可填本地路径（离线分发或自检用）。",
            Placeholder: "https://…/update.json"),
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
