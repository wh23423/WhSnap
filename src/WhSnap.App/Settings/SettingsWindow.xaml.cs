using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WhSnap.Core;
using WhSnap.Services;
using WhSnap.Theme;

namespace WhSnap.Settings;

/// <summary>
/// 设置窗口。界面**由 <see cref="SettingsSchema"/> 自动生成**（规划 §6 的实现要点），
/// 因此加一个配置项只需要改 schema，不会出现"功能加了但设置界面漏了"。
///
/// 结构照 design/focal/surfaces/settings.html 落地：
///   标题栏 32（obsidian-900 窗口底 + 琥珀图标 + 标题 + 窗口按钮，WindowStyle=None 时拖标题栏移动窗口）
///   → 左导航 188（11 个分组一次摊开，点一下跳到对应分组）
///   → 右侧可滚动内容区（状态横幅 + 9 个配置分组 + 快捷键 + 维护）
///   → 内容区底部状态条 28（结论 + 配置文件路径，数字/路径走等宽）
///   → 底部操作条 56（检测热键占用 / 导入 / 导出 / 恢复默认 / 完成）
///
/// 三条行为约定（对应 §6 四条原则）：
///   * 能配的都在这：schema 覆盖全部配置项，另有独立的热键分组；
///   * 改完即时生效：写入后立刻落盘并广播（重启后才生效的项不存在）；
///   * 冲突绝不静默：三层检测结果显示在键位按钮的红色态 + 顶部横幅上，阻断策略可显式选择。
///
/// 硬规则：界面代码里不允许出现字面量色值 —— XAML 用 {StaticResource Focal.*}，
/// C# 一律走 <see cref="FocalPalette"/>。
/// </summary>
internal partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly List<HotkeyRow> _hotkeyRows = [];
    private readonly List<SectionLink> _sections = [];
    private readonly Dictionary<string, TextBlock> _errorLabels = [];
    private readonly DispatcherTimer _saveTimer;

    private string? _recordingActionId;
    private IReadOnlyList<HotkeyRegistrationResult> _probeResults = [];
    private string? _conflictFocusActionId;
    private string? _errorSourceKey;
    private bool _hotkeyBannerDismissed;
    private bool _pendingSave;
    private bool _building;

    private TextBlock? _versionText;
    private TextBlock? _updateStatusText;
    private Button? _checkUpdatesButton;
    private Button? _installUpdateButton;
    private Button? _exportDiagnosticsButton;
    private TextBlock? _diagnosticsStatusText;
    private UpdateCheckResult? _lastUpdateResult;

    public SettingsWindow(SettingsService settings)
    {
        InitializeComponent();

        _settings = settings;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _pendingSave = false;
            _settings.Save();
            UpdateBanner();
        };

        PreviewKeyDown += OnPreviewKeyDown;
        ApplyIconFallbacks();
        BuildUi();
    }

    /// <summary>热键绑定变化（用户改键位/停用）后由宿主重新注册。</summary>
    public Action<IReadOnlyList<HotkeyBinding>>? ApplyHotkeys { get; set; }

    /// <summary>L1 试注册探针（由宿主实现：放开自己的键 → 逐个试注册 → 恢复）。</summary>
    public Func<IReadOnlyList<HotkeyBinding>, IReadOnlyList<HotkeyRegistrationResult>>? ProbeHotkeys { get; set; }

    /// <summary>诊断信息（虚拟桌面、采集后端、贴图数量…），维护分组展示。</summary>
    public Func<string>? DiagnosticsProvider { get; set; }

    /// <summary>清理贴图备份（维护分组按钮）。</summary>
    public Action? ClearPinBackups { get; set; }

    /// <summary>更新链路（宿主注入）。为空时维护分组会如实说"更新服务不可用"，而不是假装能检查。</summary>
    public UpdateCoordinator? Updater { get; set; }

    /// <summary>
    /// 维护分组「下载并安装」的实现（宿主负责：下载 → 校验 → 询问 → 安装并退出自身）。
    /// 返回值是直接显示给用户的状态文字。
    /// </summary>
    public Func<UpdateManifest, Task<string>>? InstallUpdate { get; set; }

    /// <summary>维护分组「导出诊断包…」的实现（宿主真正打包），返回值是显示给用户的状态文字。</summary>
    public Func<Task<string>>? ExportDiagnostics { get; set; }

    /// <summary>配置发生变化（宿主可据此刷新托盘文案等）。</summary>
    public event Action? SettingsChanged;

    private AppSettings Settings => _settings.Current;

    // ---------------------------------------------------------------- 自检支持

    /// <summary>已生成控件的配置项（自检用它证明"schema 里每一项都有界面入口"）。</summary>
    internal IReadOnlyCollection<string> RenderedSettingKeys => _renderedKeys;

    private readonly HashSet<string> _renderedKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>界面上实际出现的热键行数（应等于动作表条目数）。</summary>
    internal int HotkeyRowCount => _hotkeyRows.Count;

    /// <summary>
    /// 导航分区数：9 个配置分组 + 快捷键 + 维护（自检断言"分组一个都没漏"）。
    /// 左导航里另有一条分隔线，它不是一个分组，所以不在这里计数。
    /// </summary>
    internal int TabCount => _sections.Count;

    /// <summary>
    /// 把一次按键转换成键位文本（录制控件用）。
    /// 自检直接调它来验证"某些键必须被拒绝、常见组合必须能被接受"。
    /// </summary>
    internal static string? TryComposeHotkey(System.Windows.Input.Key key, ModifierKeys modifiers) =>
        IsModifierKey(key) ? null : ComposeKeys(key, modifiers);

    /// <summary>模拟用户录制一次按键（自检用，绕开真实键盘事件）。</summary>
    internal bool SimulateRecording(System.Windows.Input.Key key, ModifierKeys modifiers)
    {
        string? keys = TryComposeHotkey(key, modifiers);
        if (keys is null)
        {
            return false;
        }

        SetHotkey("capture.region", keys);
        return true;
    }

    /// <summary>当前生效的热键绑定（自检读取用）。</summary>
    internal IReadOnlyList<HotkeyBinding> CurrentBindings => _settings.ResolveHotkeys();

    /// <summary>取某行的状态徽标文字（自检用它验证冲突判定确实显示出来了）。</summary>
    internal string StatusBadgeFor(string actionId) =>
        _hotkeyRows.FirstOrDefault(row => row.Definition.Id == actionId)?.StatusText.Text ?? string.Empty;

    /// <summary>维护分组显示的版本串（自检断言"维护分组真的显示了版本号"）。</summary>
    internal string MaintenanceVersionText => _versionText?.Text ?? string.Empty;

    /// <summary>维护分组的更新结果文字（自检断言检查结论真的写进了界面）。</summary>
    internal string UpdateStatusText => _updateStatusText?.Text ?? string.Empty;

    /// <summary>维护分组是否有「检查更新」与「下载并安装」入口。</summary>
    internal bool HasUpdateEntry => _checkUpdatesButton is not null && _installUpdateButton is not null;

    /// <summary>「下载并安装」当前是否可用（检查到新版本后才放开）。</summary>
    internal bool CanInstallUpdate => _installUpdateButton?.IsEnabled == true;

    /// <summary>维护分组是否有「导出诊断包…」入口。</summary>
    internal bool HasDiagnosticsEntry => _exportDiagnosticsButton is not null;

    /// <summary>维护分组的诊断包状态文字（自检断言结论真的写进了界面）。</summary>
    internal string DiagnosticsStatusText => _diagnosticsStatusText?.Text ?? string.Empty;

    // ---------------------------------------------------------------- 令牌 / 组件便捷方法

    // 颜色、度量、样式一律从 FocalPalette 取（令牌唯一来源是 Theme/FocalTheme.xaml）。
    private static Style StyleOf(string key) => FocalPalette.Style(key);

    private static Brush BrushOf(string key) => FocalPalette.Brush(key);

    private static double MetricOf(string key) => FocalPalette.Metric(key);

    /// <summary>
    /// icons.js 的图标键名 → WPF 图标几何。设计包里的 64 个图标尚未全部落地到
    /// Theme/FocalIcons.xaml 时，先用语义最近的已交付图标顶上；图标补齐后这里自动失效
    /// （每次都重新解析，不缓存"缺失"结论）。
    /// </summary>
    private static readonly Dictionary<string, string> IconFallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["clipboard"] = "copy",
        ["text"] = "pencil",
        ["keyboard"] = "grid",
        ["speed"] = "chevronRight",
        ["warning"] = "info",
        ["upload"] = "save",
        ["download"] = "save",
        ["folder"] = "copy",
    };

    private static string IconName(string icon) =>
        FocalIcon.Resolve(icon) is null && IconFallbacks.TryGetValue(icon, out string? fallback) ? fallback : icon;

    private static FocalIcon Glyph(string icon, double size, Brush? foreground = null)
    {
        var glyph = new FocalIcon
        {
            Icon = IconName(icon),
            Size = size,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 明确给了颜色才覆盖：留 null 时图标跟随祖先控件的前景色（hover / 选中态才会跟着变）
        if (foreground is not null)
        {
            glyph.Foreground = foreground;
        }

        return glyph;
    }

    /// <summary>
    /// 跟随祖先控件前景色的文字。不写死颜色，所以按钮的 hover / 选中 / 禁用态能带着它一起变；
    /// 也避免隐式 TextBlock 样式把按钮里的字压成 on-dark（主按钮是墨字）。
    /// </summary>
    private static TextBlock InheritText(string text, double fontSize, double leftMargin = 6) =>
        InheritText(text, fontSize, leftMargin, FocalPalette.TextFont, FontWeights.Normal);

    /// <summary>等宽版本：键位、数值、路径这类"数据"文字（DESIGN §3：数据走 Cascadia + tabular-nums）。</summary>
    private static TextBlock InheritDataText(string text, double fontSize, double leftMargin = 0) =>
        InheritText(text, fontSize, leftMargin, FocalPalette.DataFont, FontWeights.Medium);

    private static TextBlock InheritText(string text, double fontSize, double leftMargin, FontFamily font, FontWeight weight)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = font,
            FontSize = fontSize,
            FontWeight = weight,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(leftMargin, 0, 0, 0),
        };

        block.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1),
        });

        return block;
    }

    /// <summary>
    /// 下拉项的显示模板：文字跟随 ComboBoxItem 的前景色（悬停时翻成墨字，压得住琥珀底）。
    /// 选中后同一段模板会渲染进 ComboBox 的显示区，那里找不到 ComboBoxItem 祖先，
    /// 于是回落到继承色（= 控件的 on-dark）——正好是设计要的两种状态。
    /// </summary>
    private static DataTemplate OptionTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding("."));
        text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ComboBoxItem), 1),
        });

        return new DataTemplate { VisualTree = text };
    }

    /// <summary>XAML 里声明的图标也走一次回退判定（图标名写的是设计稿的名字）。</summary>
    private void ApplyIconFallbacks()
    {
        TitleBarIcon.Icon = IconName("gear");
        CloseButtonIcon.Icon = IconName("x");
        StatusBannerIcon.Icon = IconName("check");
        ErrorBannerIcon.Icon = IconName("warning");
        HotkeyBannerIcon.Icon = IconName("warning");
        ProbeIcon.Icon = IconName("keyboard");
        ExportIcon.Icon = IconName("upload");
        ImportIcon.Icon = IconName("download");
    }

    // ---------------------------------------------------------------- 文案（标题之外的"一句人话"）

    /// <summary>
    /// 设置行的说明必须是一句**具体的**话（DESIGN §4）：说清什么时候该改这个开关。
    /// schema 的描述是兜底，这里给每项一句更贴操作的说明（schema 只读，不能改）。
    /// </summary>
    private static readonly Dictionary<string, string> Hints = new(StringComparer.OrdinalIgnoreCase)
    {
        // 截图
        ["CaptureBackend"] = "自动按 WGC → DXGI → GDI 降级；抓不到画面时再手动指定。",
        ["CopyOnCapture"] = "确认时同时写 DIB 与 PNG 两种格式，粘到聊天窗口不会糊。",
        ["SaveOnCapture"] = "确认时按文件名模板落盘到默认目录，不弹「另存为」对话框。",
        ["PinOnCapture"] = "确认后立刻钉在屏幕上，等价于在遮罩里按 {overlay.pin}。",
        ["OverlayDimOpacity"] = "只压暗未选中区域。屏幕本身很暗时降到 30% 以下更舒服。",
        ["ElementDetectionEnabled"] = "光标下的控件会高亮，单击即按控件边界选区。",
        ["ElementDetectionUseUia"] = "关掉后只用窗口矩形回退，只能识别整窗。",
        // 取色器
        ["ColorCopyFormat"] = "按 {overlay.pickColor} 取色时写进剪贴板的表示法；「四种」会写成多行。",
        ["ColorHistoryRetention"] = "最近取到的颜色保留多少个，可在遮罩与托盘里复用。",
        ["OverlayShowColorHistory"] = "在遮罩左下角画出最近取到的色块，按对应数字键直接复用。",
        // 保存
        ["SaveDirectory"] = @"留空则存到「图片\WhSnap」；目录不存在时保存前会自动创建。",
        ["FileNameTemplate"] = "支持 {date} {time} {w} {h} 占位符，可自定义文件名。",
        ["JpegQuality"] = "另存为 .jpg 时的压缩质量，95 以上肉眼无损。",
        // 贴图
        ["PinDefaultOpacity"] = "新贴图出现时的不透明度；之后每张贴图仍可单独调整。",
        ["PinClickThroughDefault"] = "默认关闭：穿透后点击落到下层窗口，只能从托盘关掉。",
        ["PinZoomStep"] = "每格滚轮的缩放倍率，1.1 表示每滚一格放大 10%。",
        ["PinSnapToEdges"] = "松手时把贴图吸到最近的屏幕边缘，不用再像素级对齐。",
        ["PinSnapTolerance"] = "离边缘多少物理像素以内就触发吸附；调小可以贴得更随意。",
        ["PinTopmostNew"] = "新贴图默认压在其它窗口之上，单张仍可取消置顶。",
        ["PinBackupEnabled"] = "把贴图的几何与层级定期写进备份，崩溃或重启后可恢复。",
        ["PinBackupIntervalSeconds"] = "贴图变动后至少等这么久才落盘一次，避免拖动时疯狂写盘。",
        ["PinBackupRetention"] = "保留最近多少份备份会话，超出的旧会话会被清理。",
        ["RestorePinsOnStartup"] = "启动时恢复上次贴图：几何、层级、透明度与穿透状态。",
        // 剪贴板
        ["ClipboardWatchEnabled"] = "复制图像 / 文件 / 文本就自动贴到屏幕上，不回贴自己的截图。",
        // 文本贴图
        ["TextPinFontFamily"] = "文本与 HTML 贴图用的字体名，例：Microsoft YaHei UI。",
        ["TextPinFontSize"] = "按物理像素渲染，1:1 不模糊；改大后内容会重新排版。",
        ["TextPinMaxWidth"] = "超过这个宽度就自动换行，避免一张贴图横穿整个桌面。",
        ["TextPinMaxHeight"] = "超过这个高度按省略号截断，防止一张贴图吃掉几百 MB 内存。",
        ["TextPinPadding"] = "文字四周留出的空白，0 表示紧贴边缘。",
        ["TextPinForeground"] = "文字颜色，支持十六进制写法，也支持 rgb() 与 hsl()。",
        ["TextPinBackground"] = "贴图底色，写 transparent 可以得到透明背景。",
        // 标注
        ["AnnotationDefaultTool"] = "打开标注编辑器时默认选中的工具，工具栏里随时可以换。",
        ["AnnotationStrokeColor"] = "新画的线、箭头、矩形的颜色；工具栏数字键 1-8 也能换。",
        ["AnnotationStrokeWidth"] = "新图形的线宽，画的过程中按 [ 和 ] 可以临时加减。",
        ["AnnotationFillOpacity"] = "矩形与椭圆的填充浓度，0 表示只画轮廓不填充。",
        ["AnnotationFontSize"] = "文字与序号按物理像素渲染，导出后与屏幕所见一致。",
        ["AnnotationMosaicBlock"] = "马赛克格子边长，越大越糊；对付小字建议 8 以上。",
        ["AnnotationBlurRadius"] = "盒式模糊半径（两趟可分离，近似高斯），越大越糊。",
        ["AnnotationEraserRadius"] = "橡皮擦的笔尖半径，只擦掉笔画，不改动原图。",
        ["AnnotationBadgeStart"] = "序号标注的起始数字，多张图接着编号时可以调大。",
        ["AnnotationPalette"] = "逗号分隔的颜色列表，显示成工具栏色块（支持十六进制）。",
        // 系统
        ["AutoStart"] = "随 Windows 登录自动启动（当前用户，不需要管理员权限）。",
        ["HotkeyConflictPolicy"] = "block 冲突时弹窗询问；save-disabled 保存但停用。",
        // 更新
        ["CheckForUpdatesOnStartup"] = "只读一份很小的 update.json，不会自动下载。",
        ["UpdateChannel"] = "稳定通道只接受正式版，测试通道同时接受预览版。",
        ["UpdateAutoDownload"] = "下载后先校验 SHA-256 再提示安装。",
        ["UpdateManifestUrl"] = "默认取 GitHub Releases 的 update.json。",
    };

    /// <summary>
    /// 说明文案里的 <c>{动作 id}</c> 占位符换成**当前**键位文本。
    /// 理由：键位可改，写死默认键会让改键后的说明变成错的（例如"等价于按 F3"）。
    /// </summary>
    private string HintFor(SettingDefinition definition)
    {
        string hint = Hints.TryGetValue(definition.Key, out string? text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : string.IsNullOrWhiteSpace(definition.Description)
                ? $"{definition.Label}：改完立即生效。"
                : definition.Description;

        return HotkeyPlaceholder.Replace(hint, KeyText);
    }

    /// <summary>自检用：取某个配置项当前显示的说明文案（用于断言"文案跟随当前绑定"）。</summary>
    internal string HintForTest(string settingKey)
    {
        var definition = SettingsSchema.All.FirstOrDefault(item => item.Key == settingKey);
        return definition is null ? string.Empty : HintFor(definition);
    }

    /// <summary>动作当前的键位文本；未绑定/已停用时返回"未绑定"。</summary>
    private string KeyText(string actionId)
    {
        var binding = _settings.ResolveHotkeys()
            .FirstOrDefault(b => string.Equals(b.ActionId, actionId, StringComparison.OrdinalIgnoreCase));

        return binding is { IsActive: true } ? binding.Keys!.Trim() : "未绑定";
    }

    /// <summary>分组标题右侧的一句话（"7 项 · 采集后端、确认动作与元素检测"）。</summary>
    private static readonly Dictionary<string, string> GroupSummaries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["截图"] = "采集后端、确认动作与元素检测",
        ["取色器"] = "复制格式、历史条数与遮罩里的色块",
        ["保存"] = "目录、文件名模板与 JPEG 质量",
        ["贴图"] = "透明度、吸附、备份与启动恢复",
        ["剪贴板"] = "复制即贴图的监听开关",
        ["文本贴图"] = "字体、尺寸、留白与配色",
        ["标注"] = "默认工具、线宽、马赛克与调色板",
        ["系统"] = "开机自启与热键冲突策略",
        ["更新"] = "检查时机、更新通道与下载行为",
    };

    private static readonly Dictionary<string, string> GroupIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["截图"] = "crop",
        ["取色器"] = "eyedropper",
        ["保存"] = "save",
        ["贴图"] = "pin",
        ["剪贴板"] = "clipboard",
        ["文本贴图"] = "text",
        ["标注"] = "pencil",
        ["系统"] = "monitor",
        ["更新"] = "download",
    };

    private static string GroupSummary(string group) =>
        GroupSummaries.TryGetValue(group, out string? summary) ? summary : "该分组的全部设置项";

    private static string GroupIcon(string group) =>
        GroupIcons.TryGetValue(group, out string? icon) ? icon : "gear";

    // ---------------------------------------------------------------- 界面构建

    private sealed class SectionLink
    {
        public required string Title { get; init; }

        public required FrameworkElement Target { get; init; }

        public required ToggleButton Item { get; init; }

        public required FocalIcon Icon { get; init; }
    }

    private void BuildUi()
    {
        _building = true;

        try
        {
            _hotkeyRows.Clear();
            _sections.Clear();
            _errorLabels.Clear();
            _renderedKeys.Clear();
            _recordingActionId = null;
            _conflictFocusActionId = null;
            _hotkeyBannerDismissed = false;
            RailItems.Items.Clear();
            ContentHost.Children.Clear();

            foreach (string group in SettingsSchema.Groups)
            {
                AddSettingSection(group);
            }

            // 分隔线只在导航里出现，不算一个分组（TabCount 因此是 9 + 2 = 11）
            RailItems.Items.Add(BuildRailSeparator());

            AddHotkeySection();
            AddMaintenanceSection();
            SelectFirstSection();
        }
        finally
        {
            _building = false;
        }

        UpdateConfigBanner();
        RefreshHotkeyStatuses(probe: false);
    }

    private void AddSettingSection(string group)
    {
        var definitions = SettingsSchema.InGroup(group);

        string hint = $"{definitions.Count.ToString(CultureInfo.InvariantCulture)} 项 · {GroupSummary(group)}";
        var header = BuildSectionHeader(group, hint);
        var card = GroupCard(definitions.Select(BuildSettingRow));

        ContentHost.Children.Add(header);
        ContentHost.Children.Add(card);
        AddRailItem(group, GroupIcon(group), definitions.Count.ToString(CultureInfo.InvariantCulture), header);
    }

    /// <summary>分组标题：15/600 + 一句"这一组管什么"（12/400，muted）。</summary>
    private static FrameworkElement BuildSectionHeader(string title, string hint)
    {
        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, MetricOf("Focal.SpaceMd"), 0, MetricOf("Focal.SpaceMd")),
        };

        line.Children.Add(new TextBlock { Text = title, Style = StyleOf("Focal.SubtitleText") });
        line.Children.Add(new TextBlock
        {
            Text = hint,
            Style = StyleOf("Focal.CaptionText"),
            Margin = new Thickness(MetricOf("Focal.SpaceMd"), 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
        });

        return line;
    }

    /// <summary>分组卡：Focal.Card（10 圆角 + 发丝线 + 平铺，零阴影），行之间靠行自身的下边线分隔。</summary>
    private static Border GroupCard(IEnumerable<FrameworkElement> rows)
    {
        var list = rows.ToList();
        var stack = new StackPanel();

        for (int i = 0; i < list.Count; i++)
        {
            // 最后一行不带下边线，避免和卡片的 1px 边框叠成两条
            if (i == list.Count - 1 && list[i] is Border last)
            {
                last.BorderThickness = new Thickness(0);
            }

            stack.Children.Add(list[i]);
        }

        return new Border
        {
            Style = StyleOf("Focal.Card"),
            Padding = new Thickness(0),
            Child = stack,
        };
    }

    /// <summary>
    /// 设置行的固定三段式：标题 14/400（不加粗）+ 一句人话 12/400 + 右侧控件。
    /// 控件顶端对齐到标题第一行（对应 focal.css 的 .f-row__ctl padding-top:2px）。
    /// </summary>
    private Border BuildRow(string label, string hint, FrameworkElement? control, out TextBlock errorText, out StackPanel main)
    {
        main = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        main.Children.Add(new TextBlock { Text = label, Style = StyleOf("Focal.RowLabel") });

        if (!string.IsNullOrWhiteSpace(hint))
        {
            main.Children.Add(new TextBlock { Text = hint, Style = StyleOf("Focal.RowHint") });
        }

        errorText = new TextBlock
        {
            Style = StyleOf("Focal.CaptionText"),
            Foreground = BrushOf("Focal.SignalDangerBrush"),
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, MetricOf("Focal.SpaceXs"), 0, 0),
        };

        main.Children.Add(errorText);

        var row = new Border { Style = StyleOf("Focal.Row") };

        if (control is null)
        {
            row.Child = main;
            return row;
        }

        control.VerticalAlignment = VerticalAlignment.Top;
        control.Margin = new Thickness(MetricOf("Focal.SpaceLg"), 2, 0, 0);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(main, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(main);
        grid.Children.Add(control);

        row.Child = grid;
        return row;
    }

    private FrameworkElement BuildSettingRow(SettingDefinition definition)
    {
        FrameworkElement control = BuildControl(definition);
        string hint = HintFor(definition);

        AutomationProperties.SetName(control, definition.Label);
        AutomationProperties.SetHelpText(control, hint);

        var row = BuildRow(definition.Label, hint, control, out TextBlock error, out _);
        _errorLabels[definition.Key] = error;
        _renderedKeys.Add(definition.Key);
        return row;
    }

    private FrameworkElement BuildControl(SettingDefinition definition)
    {
        string current = SettingsBinder.Read(Settings, definition.Key) ?? string.Empty;

        switch (definition.Kind)
        {
            case SettingKind.Bool:
            {
                // 开关（34×18 胶囊），不是勾选框：这是"开/关状态"，圆的东西是"读的"
                var toggle = new ToggleButton
                {
                    Style = StyleOf("Focal.Switch"),
                    IsChecked = current.Equals("true", StringComparison.OrdinalIgnoreCase),
                    ToolTip = HintFor(definition),
                };

                toggle.Checked += (_, _) => Apply(definition.Key, "true", toggle);
                toggle.Unchecked += (_, _) => Apply(definition.Key, "false", toggle);
                return toggle;
            }

            case SettingKind.Choice:
            {
                var combo = new ComboBox
                {
                    Width = 260,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    ToolTip = HintFor(definition),
                };

                foreach (var option in definition.Options ?? [])
                {
                    combo.Items.Add(new ComboBoxItem
                    {
                        Content = option.Label,
                        Tag = option.Value,
                        ContentTemplate = OptionTemplate(),
                    });
                }

                foreach (ComboBoxItem item in combo.Items)
                {
                    if (string.Equals((string)item.Tag, current, StringComparison.OrdinalIgnoreCase))
                    {
                        combo.SelectedItem = item;
                        break;
                    }
                }

                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem { Tag: string value })
                    {
                        Apply(definition.Key, value, combo);
                    }
                };

                return combo;
            }

            case SettingKind.Int:
            case SettingKind.Double:
            {
                bool isDouble = definition.Kind == SettingKind.Double;
                double value = double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                    ? parsed
                    : definition.Min;

                var panel = new StackPanel { Orientation = Orientation.Horizontal };

                // 「遮罩暗度」把"45% 到底多黑"直接画出来（design/focal/surfaces/settings.html 的预览）
                Rectangle? scrim = null;
                if (definition.Key.Equals("OverlayDimOpacity", StringComparison.OrdinalIgnoreCase))
                {
                    panel.Children.Add(BuildDimPreview(Math.Clamp(value, definition.Min, definition.Max), out scrim));
                }

                var textBox = new TextBox
                {
                    Style = StyleOf("Focal.InputData"),
                    Width = 64,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "可以直接输入精确值（回车或离开焦点生效）",
                };

                var slider = new Slider
                {
                    Style = StyleOf("Focal.Slider"),
                    Minimum = definition.Min,
                    Maximum = definition.Max,
                    Value = Math.Clamp(value, definition.Min, definition.Max),
                    Width = 148,
                    TickFrequency = definition.Step > 0 ? definition.Step : 1,
                    IsSnapToTickEnabled = definition.Step > 0,
                    SmallChange = definition.Step > 0 ? definition.Step : 1,
                    LargeChange = definition.Step > 0 ? definition.Step * 2 : 2,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = HintFor(definition),
                };

                string Format(double number) => isDouble
                    ? number.ToString("0.####", CultureInfo.InvariantCulture)
                    : ((int)Math.Round(number)).ToString(CultureInfo.InvariantCulture);

                void Display(double number)
                {
                    textBox.Text = Format(number); // 数值走等宽（Focal.InputData = DataFont + 表格数字）

                    if (scrim is not null)
                    {
                        scrim.Fill = FocalPalette.Scrim(number);
                    }
                }

                Display(Math.Clamp(value, definition.Min, definition.Max));

                void CommitNumber()
                {
                    if (!double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double typed))
                    {
                        Display(slider.Value); // 打错的输入退回当前生效值
                        return;
                    }

                    double normalized = Math.Round(Math.Clamp(typed, definition.Min, definition.Max), 4);

                    if (Math.Abs(slider.Value - normalized) > 0.0001)
                    {
                        slider.Value = normalized; // 触发 ValueChanged → Apply
                        return;
                    }

                    Display(normalized);
                    Apply(definition.Key, Format(normalized), textBox);
                }

                slider.ValueChanged += (_, args) =>
                {
                    if (_building)
                    {
                        return;
                    }

                    double normalized = Math.Round(args.NewValue, 4);
                    Display(normalized);
                    Apply(definition.Key, Format(normalized), slider);
                };

                textBox.LostFocus += (_, _) => CommitNumber();
                textBox.KeyDown += (_, args) =>
                {
                    if (args.Key == Key.Enter)
                    {
                        CommitNumber();
                        args.Handled = true;
                    }
                };

                panel.Children.Add(slider);
                panel.Children.Add(textBox);

                if (!string.IsNullOrEmpty(definition.Unit))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = definition.Unit,
                        Style = StyleOf("Focal.CaptionText"),
                        Foreground = BrushOf("Focal.OnDarkFaintBrush"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(MetricOf("Focal.SpaceXs"), 0, 0, 0),
                    });
                }

                return panel;
            }

            case SettingKind.Color:
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal };

                var swatch = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = FocalPalette.RadiusControl,
                    BorderBrush = BrushOf("Focal.HairlineDarkStrongBrush"),
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(MetricOf("Focal.SpaceSm"), 0, 0, 0),
                    Background = UserColorBrush(current),
                };

                var textBox = new TextBox
                {
                    Style = StyleOf("Focal.InputData"),
                    Width = 128,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = HintFor(definition),
                };

                textBox.Text = current;

                void CommitColor()
                {
                    Apply(definition.Key, textBox.Text, textBox);

                    // 写入失败时读回来的是旧值，色块不会跟着错误输入走
                    swatch.Background = UserColorBrush(SettingsBinder.Read(Settings, definition.Key));
                }

                textBox.LostFocus += (_, _) => CommitColor();
                textBox.KeyDown += (_, args) =>
                {
                    if (args.Key == Key.Enter)
                    {
                        CommitColor();
                        args.Handled = true;
                    }
                };

                AutomationProperties.SetName(swatch, definition.Label + " 当前色");
                panel.Children.Add(textBox);
                panel.Children.Add(swatch);
                return panel;
            }

            case SettingKind.DirectoryPath:
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal };

                var textBox = new TextBox
                {
                    Style = StyleOf("Focal.InputData"),
                    Width = 300,
                    Text = current,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = HintFor(definition),
                };

                var browse = new Button
                {
                    Style = StyleOf("Focal.Button.Small"),
                    Content = InheritText("浏览…", FocalPalette.CaptionSize),
                    Margin = new Thickness(MetricOf("Focal.SpaceSm"), 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "选一个目录",
                };

                AutomationProperties.SetName(browse, "浏览" + definition.Label);

                browse.Click += (_, _) =>
                {
                    var dialog = new OpenFolderDialog { Title = $"选择{definition.Label}" };
                    if (Directory.Exists(textBox.Text))
                    {
                        dialog.InitialDirectory = textBox.Text;
                    }

                    if (dialog.ShowDialog(this) == true)
                    {
                        textBox.Text = dialog.FolderName;
                        Apply(definition.Key, textBox.Text, textBox);
                    }
                };

                textBox.LostFocus += (_, _) => Apply(definition.Key, textBox.Text, textBox);
                textBox.KeyDown += (_, args) =>
                {
                    if (args.Key == Key.Enter)
                    {
                        Apply(definition.Key, textBox.Text, textBox);
                        args.Handled = true;
                    }
                };

                panel.Children.Add(textBox);
                panel.Children.Add(browse);
                return panel;
            }

            default:
            {
                var textBox = new TextBox
                {
                    Style = StyleOf("Focal.Input"),
                    Width = 300,
                    Text = current,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = HintFor(definition),
                };

                textBox.LostFocus += (_, _) => Apply(definition.Key, textBox.Text, textBox);
                textBox.KeyDown += (_, args) =>
                {
                    if (args.Key == Key.Enter)
                    {
                        Apply(definition.Key, textBox.Text, textBox);
                        args.Handled = true;
                    }
                };

                return string.IsNullOrWhiteSpace(definition.Placeholder)
                    ? textBox
                    : WithWatermark(textBox, definition.Placeholder!);
            }
        }
    }

    /// <summary>把用户配置里的颜色画成色块（颜色来自用户内容，不是界面主题；取色入口仍是 FocalPalette）。</summary>
    private static Brush UserColorBrush(string? text) =>
        ColorValue.TryParse(text ?? string.Empty, out var color)
            ? FocalPalette.FromRgb(color)
            : BrushOf("Focal.Obsidian600Brush");

    /// <summary>输入框的占位文字（WPF 没有原生 placeholder，用一层不可点中的说明文字顶替）。</summary>
    private static FrameworkElement WithWatermark(TextBox box, string placeholder)
    {
        double width = box.Width;

        var watermark = new TextBlock
        {
            Text = placeholder,
            Style = StyleOf("Focal.CaptionText"),
            Foreground = BrushOf("Focal.OnDarkFaintBrush"),
            Margin = new Thickness(13, 0, 13, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed,
        };

        box.TextChanged += (_, _) =>
            watermark.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;

        box.Width = double.NaN;
        box.HorizontalAlignment = HorizontalAlignment.Stretch;

        var host = new Grid { Width = width };
        host.Children.Add(box);
        host.Children.Add(watermark);
        return host;
    }

    /// <summary>遮罩暗度预览：纸色底 + 两个内容色块 + 一层按当前浓度缩放的暗幕。</summary>
    private static FrameworkElement BuildDimPreview(double opacity, out Rectangle scrim)
    {
        var canvas = new Grid { Width = 58, Height = 28 };

        canvas.Children.Add(new Rectangle { Fill = BrushOf("Focal.LinenBrush") });
        canvas.Children.Add(new Rectangle
        {
            Width = 16,
            Height = 16,
            RadiusX = 2,
            RadiusY = 2,
            Fill = BrushOf("Focal.AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(9, 6, 0, 0),
        });

        canvas.Children.Add(new Rectangle
        {
            Width = 16,
            Height = 16,
            RadiusX = 2,
            RadiusY = 2,
            Fill = BrushOf("Focal.MeasureElementBrush"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(31, 6, 0, 0),
        });

        scrim = new Rectangle { Fill = FocalPalette.Scrim(opacity) };
        canvas.Children.Add(scrim);

        return new Border
        {
            Width = 58,
            Height = 28,
            BorderBrush = BrushOf("Focal.HairlineDarkBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = FocalPalette.RadiusControl,
            ClipToBounds = true,
            SnapsToDevicePixels = true,
            Child = canvas,
            Margin = new Thickness(0, 0, MetricOf("Focal.SpaceSm"), 0),
            ToolTip = "预览：这个浓度盖在屏幕内容上是什么样",
        };
    }

    // ---------------------------------------------------------------- 左导航

    private FrameworkElement BuildRailSeparator() => new Border
    {
        Height = 1,
        Background = BrushOf("Focal.HairlineDarkBrush"),
        Margin = new Thickness(
            MetricOf("Focal.SpaceXs"),
            MetricOf("Focal.SpaceSm"),
            MetricOf("Focal.SpaceXs"),
            MetricOf("Focal.SpaceSm")),
    };

    private ToggleButton AddRailItem(string title, string icon, string? count, FrameworkElement target)
    {
        // RailItem 的模板把内容左对齐（内容宽度 = 内容自身），所以宽度要自己算：
        // 侧栏宽 − 左右外边距（8×2）− 左右内边距（8×2）
        double contentWidth = MetricOf("Focal.RailWidth")
                              - (2 * MetricOf("Focal.SpaceSm"))
                              - (2 * MetricOf("Focal.SpaceSm"));

        var grid = new Grid { Width = contentWidth };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = Glyph(icon, FocalPalette.IconMd, FocalPalette.OnDarkFaint);
        var label = InheritText(title, FocalPalette.BodySize, MetricOf("Focal.SpaceSm"));

        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(label, 1);
        grid.Children.Add(glyph);
        grid.Children.Add(label);

        if (!string.IsNullOrEmpty(count))
        {
            var badge = new TextBlock
            {
                Text = count,
                Style = StyleOf("Focal.DataCaptionText"),
                Foreground = BrushOf("Focal.OnDarkFaintBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(MetricOf("Focal.SpaceSm"), 0, 0, 0),
            };

            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);
        }

        var item = new ToggleButton
        {
            Style = StyleOf("Focal.RailItem"),
            Content = grid,
            ToolTip = $"跳到「{title}」",
        };

        AutomationProperties.SetName(item, title);
        item.Click += (_, _) => SelectSection(target, item);

        RailItems.Items.Add(item);
        _sections.Add(new SectionLink { Title = title, Target = target, Item = item, Icon = glyph });
        return item;
    }

    private void SelectFirstSection()
    {
        if (_sections.Count == 0)
        {
            return;
        }

        _sections[0].Item.IsChecked = true;
        _sections[0].Icon.Foreground = FocalPalette.Accent;
    }

    private void SelectSection(FrameworkElement target, ToggleButton item)
    {
        foreach (var section in _sections)
        {
            bool selected = ReferenceEquals(section.Item, item);
            section.Item.IsChecked = selected;

            // 选中项图标走琥珀（design/focal/focal.css .f-rail__item[aria-current=true] .f-i）
            section.Icon.Foreground = selected ? FocalPalette.Accent : FocalPalette.OnDarkFaint;
        }

        target.BringIntoView();
    }

    private SectionLink? FindSection(string title) =>
        _sections.FirstOrDefault(section => section.Title.Equals(title, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- 快捷键分组

    private sealed class HotkeyRow
    {
        public required HotkeyActionDefinition Definition { get; init; }

        public required HotkeyScope Scope { get; init; }

        public required ToggleButton Keycap { get; init; }

        /// <summary>键帽上的键位文字（等宽；录制中换成提示语）。</summary>
        public required TextBlock KeyText { get; init; }

        public required TextBlock StatusText { get; init; }

        public required TextBlock DetailText { get; init; }
    }

    private void AddHotkeySection()
    {
        var rows = new List<FrameworkElement>();

        foreach (HotkeyScope scope in Enum.GetValues<HotkeyScope>())
        {
            var actions = HotkeyCatalog.InScope(scope).ToList();
            if (actions.Count == 0)
            {
                continue;
            }

            rows.Add(BuildScopeHeader(scope, actions.Count));

            foreach (var action in actions)
            {
                rows.Add(BuildHotkeyRow(action, scope));
            }
        }

        var header = BuildSectionHeader("快捷键", $"{HotkeyCatalog.All.Count} 条 · 点键位即可录制，Esc 取消");
        ContentHost.Children.Add(header);
        ContentHost.Children.Add(NoteBanner(
            "全局热键会与整个系统抢键（L1 试注册判定）；遮罩 / 贴图窗口内的按键只在本程序的窗口里生效，不会与别的软件冲突。",
            "info",
            BrushOf("Focal.OnDarkMutedBrush")));
        ContentHost.Children.Add(GroupCard(rows));

        AddRailItem("快捷键", "keyboard", HotkeyCatalog.All.Count.ToString(CultureInfo.InvariantCulture), header);
    }

    private static FrameworkElement BuildScopeHeader(HotkeyScope scope, int count)
    {
        (string title, string hint) = scope switch
        {
            HotkeyScope.Global => ("全局热键", "注册到系统，与整个系统抢键；L1 试注册能判定是否已被占用"),
            HotkeyScope.Overlay => ("遮罩内按键", "只在截图遮罩显示时生效，不会与别的软件冲突"),
            HotkeyScope.Pin => ("贴图窗口内按键", "焦点在贴图窗口时生效"),
            _ => ("标注编辑器内按键", "焦点在标注编辑器时生效"),
        };

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock
        {
            Text = title,
            Style = StyleOf("Focal.MicroText"),
            Foreground = BrushOf("Focal.OnDarkMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        line.Children.Add(new TextBlock
        {
            Text = $"　{hint} · {count} 条",
            Style = StyleOf("Focal.CaptionText"),
            Foreground = BrushOf("Focal.OnDarkFaintBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Border
        {
            Padding = new Thickness(18, 10, 18, 8),
            BorderBrush = BrushOf("Focal.HairlineDarkBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = line,
        };
    }

    private FrameworkElement BuildHotkeyRow(HotkeyActionDefinition action, HotkeyScope scope)
    {
        // 键位文本走等宽（Focal.KeycapButton 的模板字体就是 Focal.DataFont，这里再显式一次，
        // 免得隐式 TextBlock 样式把它拉回正文字体）
        var keyText = InheritDataText("(未绑定)", FocalPalette.DataSize);

        var keycap = new ToggleButton
        {
            Style = StyleOf("Focal.KeycapButton"),
            Content = keyText,
            ToolTip = "点击后按组合键录制；Esc 取消。再点一次可取消录制。",
        };

        keycap.Click += (_, _) => ToggleRecording(action.Id);
        AutomationProperties.SetName(keycap, action.DisplayName + " 键位");

        var status = new TextBlock
        {
            Style = StyleOf("Focal.DataCaptionText"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(MetricOf("Focal.SpaceSm"), 0, MetricOf("Focal.SpaceSm"), 0),
        };

        var clear = SideButton("清除", "清掉这个动作的绑定（不影响其它动作）", () => SetHotkey(action.Id, null));
        var reset = SideButton("默认", $"恢复默认键位（{action.DefaultKeys ?? "默认不绑定"}）", () => SetHotkey(action.Id, action.DefaultKeys, forceDefault: true));

        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(keycap);
        controls.Children.Add(status);
        controls.Children.Add(clear);
        controls.Children.Add(reset);

        var detail = new TextBlock
        {
            Style = StyleOf("Focal.CaptionText"),
            Foreground = BrushOf("Focal.OnDarkFaintBrush"),
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, MetricOf("Focal.SpaceXs"), 0, 0),
        };

        var row = BuildRow(action.DisplayName, action.Description, controls, out _, out StackPanel main);
        main.Children.Add(detail);

        _hotkeyRows.Add(new HotkeyRow
        {
            Definition = action,
            Scope = scope,
            Keycap = keycap,
            KeyText = keyText,
            StatusText = status,
            DetailText = detail,
        });

        return row;
    }

    private Button SideButton(string text, string tip, Action onClick)
    {
        var button = new Button
        {
            Style = StyleOf("Focal.Button.Small"),
            Content = InheritText(text, FocalPalette.CaptionSize, 0),
            ToolTip = tip,
            Margin = new Thickness(MetricOf("Focal.SpaceSm"), 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        button.Click += (_, _) => onClick();
        AutomationProperties.SetName(button, text);
        return button;
    }

    private void ToggleRecording(string actionId)
    {
        if (string.Equals(_recordingActionId, actionId, StringComparison.Ordinal))
        {
            _recordingActionId = null;
            RefreshHotkeyStatuses(probe: false);
            return;
        }

        StartRecording(actionId);
    }

    private void StartRecording(string actionId)
    {
        _recordingActionId = actionId;
        RefreshHotkeyStatuses(probe: false);

        var row = _hotkeyRows.FirstOrDefault(r => r.Definition.Id == actionId);
        if (row is not null)
        {
            row.DetailText.Text = "修饰键可组合（Ctrl / Shift / Alt / Win）；单独按修饰键不算，需要再按一个普通键；Esc 取消。";
            row.DetailText.Foreground = BrushOf("Focal.AccentBrush");
            row.DetailText.Visibility = Visibility.Visible;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingActionId is null)
        {
            return;
        }

        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            _recordingActionId = null;
            RefreshHotkeyStatuses(probe: false);
            return;
        }

        if (IsModifierKey(key))
        {
            return;
        }

        string? keys = ComposeKeys(key, Keyboard.Modifiers);
        string actionId = _recordingActionId;
        _recordingActionId = null;

        if (keys is null)
        {
            MessageBox.Show(this, "这个按键暂时无法作为热键（例如 Win 键组合由系统独占、或该键不在支持列表里），请换一个组合。", "WhSnap 设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshHotkeyStatuses(probe: false);
            return;
        }

        SetHotkey(actionId, keys);
    }

    private static bool IsModifierKey(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt
        or Key.LWin or Key.RWin
        or Key.System;

    /// <summary>把 WPF 按键 + 修饰键转成登记用的键位文本；无法正确往返的键返回 null。</summary>
    private static string? ComposeKeys(Key key, ModifierKeys modifiers)
    {
        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            return null;
        }

        uint bits = 0;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            bits |= HotkeySpec.ModControl;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            bits |= HotkeySpec.ModShift;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            bits |= HotkeySpec.ModAlt;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            bits |= HotkeySpec.ModWin;
        }

        var candidate = new HotkeySpec(bits, (uint)virtualKey);
        string text = candidate.ToString();

        // 必须能往返解析（有些键名不在解析表里，如 VK(0xXX)），否则界面显示了但注册不了
        return HotkeySpec.TryParse(text, out var roundTrip, out _) && roundTrip.Equals(candidate) ? text : null;
    }

    private void SetHotkey(string actionId, string? keys, bool forceDefault = false)
    {
        var definition = HotkeyCatalog.Require(actionId);
        string? previous = Settings.Hotkeys.TryGetValue(actionId, out string? existing) ? existing : null;

        _recordingActionId = null;
        _hotkeyBannerDismissed = false;

        if (forceDefault)
        {
            Settings.Hotkeys.Remove(actionId);
        }
        else if (string.IsNullOrWhiteSpace(keys))
        {
            Settings.Hotkeys.Remove(actionId);
        }
        else
        {
            Settings.Hotkeys[actionId] = keys.Trim();
        }

        Settings.DisabledHotkeys.RemoveAll(id => id.Equals(actionId, StringComparison.OrdinalIgnoreCase));

        // L2 预检：同作用域撞键必须显式选择，绝不静默失败
        var bindings = _settings.ResolveHotkeys();
        var conflicts = HotkeyConflictDetector.DetectInternalDuplicates(bindings)
            .Where(report => report.ActionId.Equals(actionId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (conflicts.Count > 0)
        {
            if (Settings.HotkeyConflictPolicy.Equals("save-disabled", StringComparison.OrdinalIgnoreCase))
            {
                // 用户在设置里选了"保存但停用"：仍然明确告知，只是不弹窗打断
                if (!Settings.DisabledHotkeys.Contains(actionId, StringComparer.OrdinalIgnoreCase))
                {
                    Settings.DisabledHotkeys.Add(actionId);
                }

                ShowStatus("热键冲突，已按设置保存但停用", $"「{definition.DisplayName}」的 {keys} 与其它动作冲突：{conflicts[0].Message}");
            }
            else
            {
                string message = $"「{definition.DisplayName}」的 {keys} 与其它动作冲突：\n\n{conflicts[0].Message}\n\n"
                                 + "是 = 仍保存，但把这个动作停用（推荐）\n否 = 撤销本次修改\n取消 = 仍保存且不禁用（可能注册失败）";

                var choice = MessageBox.Show(this, message, "热键冲突", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

                if (choice == MessageBoxResult.No)
                {
                    if (previous is null)
                    {
                        Settings.Hotkeys.Remove(actionId);
                    }
                    else
                    {
                        Settings.Hotkeys[actionId] = previous;
                    }
                }
                else if (choice == MessageBoxResult.Yes)
                {
                    if (!Settings.DisabledHotkeys.Contains(actionId, StringComparer.OrdinalIgnoreCase))
                    {
                        Settings.DisabledHotkeys.Add(actionId);
                    }
                }
            }
        }

        ScheduleSave();
        ApplyHotkeyBindings();
        RefreshHotkeyStatuses(probe: true);
    }

    private void ApplyHotkeyBindings()
    {
        var bindings = _settings.ResolveHotkeys();
        ApplyHotkeys?.Invoke(bindings);
    }

    private void RefreshHotkeyStatuses(bool probe)
    {
        var bindings = _settings.ResolveHotkeys();
        var conflicts = HotkeyConflictDetector.DetectAll(bindings);

        if (probe && ProbeHotkeys is not null)
        {
            try
            {
                _probeResults = ProbeHotkeys(bindings);
            }
            catch (Exception ex)
            {
                Log.Warn($"热键占用探测失败：{ex.Message}");
                _probeResults = [];
            }
        }

        foreach (var row in _hotkeyRows)
        {
            var binding = bindings.First(b => b.ActionId == row.Definition.Id);
            bool recording = string.Equals(_recordingActionId, row.Definition.Id, StringComparison.Ordinal);

            var rowConflicts = conflicts.Where(report => report.ActionId == row.Definition.Id).ToList();
            var probeResult = _probeResults.FirstOrDefault(result => result.ActionId == row.Definition.Id);

            string status;
            Brush tone;

            if (!binding.Enabled)
            {
                status = "已停用";
                tone = FocalPalette.OnDarkFaint;
            }
            else if (!binding.IsBound)
            {
                status = "未绑定";
                tone = FocalPalette.OnDarkFaint;
            }
            else if (!binding.IsActive)
            {
                status = "键位非法";
                tone = FocalPalette.SignalDanger;
            }
            else if (rowConflicts.Any(report => report.Blocks) || probeResult?.Status == HotkeyStatus.ConflictWithSystem)
            {
                status = "冲突";
                tone = FocalPalette.SignalDanger;
            }
            else if (probeResult?.Status == HotkeyStatus.Registered)
            {
                status = "空闲";
                tone = FocalPalette.SignalOk;
            }
            else
            {
                status = "已生效";
                tone = FocalPalette.SignalOk;
            }

            // 录制中 = IsChecked 的琥珀脉冲态；冲突 / 非法 = Tag="conflict" 的红色态
            row.Keycap.IsChecked = recording;
            row.KeyText.Text = recording
                ? "按组合键…"
                : binding.IsBound ? binding.Keys! : "(未绑定)";
            row.Keycap.Tag = status is "冲突" or "键位非法" ? "conflict" : null;

            row.StatusText.Text = $"[{status}]";
            row.StatusText.Foreground = recording ? FocalPalette.Accent : tone;

            if (!recording)
            {
                var details = new List<string>();

                if (binding.Definition.Scope == HotkeyScope.Global && probeResult is not null)
                {
                    details.Add(probeResult.Message);
                }

                details.AddRange(rowConflicts.Select(report => report.Message + (report.Advice is null ? string.Empty : $" → {report.Advice}")));

                row.DetailText.Text = string.Join("\n", details);
                row.DetailText.Foreground = status is "冲突" or "键位非法"
                    ? FocalPalette.SignalDanger
                    : FocalPalette.OnDarkFaint;
                row.DetailText.Visibility = details.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        UpdateBanner(conflicts);
    }

    // ---------------------------------------------------------------- 状态面（横幅 / 状态条）

    private Border NoteBanner(string text, string icon, Brush iconBrush)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var glyph = Glyph(icon, FocalPalette.IconMd, iconBrush);
        glyph.VerticalAlignment = VerticalAlignment.Top;
        glyph.Margin = new Thickness(0, 1, MetricOf("Focal.SpaceSm"), 0);

        var body = new TextBlock
        {
            Text = text,
            Style = StyleOf("Focal.CaptionText"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(body, 1);
        grid.Children.Add(glyph);
        grid.Children.Add(body);

        return new Border
        {
            Style = StyleOf("Focal.Banner"),
            Margin = new Thickness(0, 0, 0, MetricOf("Focal.SpaceMd")),
            Child = grid,
        };
    }

    /// <summary>状态横幅：配置载入状态与操作结果（语义色只出现在状态面）。</summary>
    private void ShowStatus(string title, string message, bool danger = false)
    {
        StatusBanner.Style = StyleOf(danger ? "Focal.BannerDanger" : "Focal.BannerOk");
        StatusBannerIcon.Icon = IconName(danger ? "warning" : "check");
        StatusBannerIcon.Foreground = danger ? FocalPalette.SignalDanger : FocalPalette.SignalOk;
        StatusBannerTitle.Text = title;
        BannerText.Text = message;
        StatusBanner.Visibility = Visibility.Visible;
    }

    private void UpdateConfigBanner()
    {
        if (!string.IsNullOrWhiteSpace(_settings.LastError))
        {
            ShowStatus("配置载入失败，已改用默认值", $"{_settings.LastError}　（{SettingsService.ConfigFilePath}）", danger: true);
            return;
        }

        ShowStatus(
            "配置已载入",
            $"{SettingsSchema.All.Count} 项设置 · schema v{AppSettings.CurrentSchemaVersion} · 热键 {HotkeyCatalog.All.Count} 条 · 所有改动即时生效并写入");
    }

    private void ShowError(string message, string? sourceKey = null)
    {
        _errorSourceKey = sourceKey;
        ErrorBannerText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void ClearError(string? sourceKey)
    {
        if (sourceKey is not null && !string.Equals(_errorSourceKey, sourceKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _errorSourceKey = null;
        ErrorBanner.Visibility = Visibility.Collapsed;
    }

    private void DismissError_Click(object sender, RoutedEventArgs e) => ClearError(null);

    private void DismissHotkeyBanner_Click(object sender, RoutedEventArgs e)
    {
        _hotkeyBannerDismissed = true;
        HotkeyBanner.Visibility = Visibility.Collapsed;
    }

    private void FixConflict_Click(object sender, RoutedEventArgs e)
    {
        if (FindSection("快捷键") is { } section)
        {
            SelectSection(section.Target, section.Item);
        }

        if (_conflictFocusActionId is not null)
        {
            StartRecording(_conflictFocusActionId);
        }
    }

    private void UpdateBanner(IReadOnlyList<HotkeyConflictReport>? conflicts = null)
    {
        var bindings = _settings.ResolveHotkeys();
        conflicts ??= HotkeyConflictDetector.DetectAll(bindings);

        var blocking = conflicts.Where(report => report.Blocks).ToList();
        var systemConflicts = _probeResults.Where(result => result.Status == HotkeyStatus.ConflictWithSystem).ToList();
        int active = bindings.Count(binding => binding.IsActive && binding.Definition.Scope == HotkeyScope.Global);
        bool trouble = blocking.Count > 0 || systemConflicts.Count > 0;

        // 热键冲突横幅：只说结论与来源，动作（换键位 / 忽略）就在横幅上
        bool show = trouble && !_hotkeyBannerDismissed;
        HotkeyBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (show)
        {
            _conflictFocusActionId = blocking.Count > 0
                ? blocking[0].ActionId
                : systemConflicts[0].ActionId;

            string actionName = _hotkeyRows
                .FirstOrDefault(row => row.Definition.Id == _conflictFocusActionId)?.Definition.DisplayName
                ?? _conflictFocusActionId
                ?? "这个动作";

            HotkeyBannerTitle.Text = blocking.Count > 0
                ? $"「{actionName}」与其它动作冲突" + (Settings.DisabledHotkeys.Count > 0 ? "，冲突的动作已停用" : string.Empty)
                : $"「{actionName}」的热键已被系统里的其它程序占用";

            HotkeyBannerText.Text = blocking.Count > 0
                ? blocking[0].Message + (blocking[0].Advice is null ? string.Empty : $" → {blocking[0].Advice}")
                : systemConflicts[0].Message + "　（L1 试注册判定；换成别的组合即可，已绑定的键位不会被静默丢弃）";
        }

        // 状态条：结论 + 配置路径（数字 / 路径走等宽）。冲突细节在键位行与横幅上，这里只说个数，
        // 免得把右侧的配置文件路径挤掉。
        var parts = new List<string>
        {
            _pendingSave ? "正在写入配置…" : "配置已保存",
            $"{SettingsSchema.All.Count} 项 · schema v{AppSettings.CurrentSchemaVersion}",
            $"全局热键 {active} 个",
        };

        if (trouble)
        {
            parts.Add(blocking.Count > 0 ? $"动作冲突 {blocking.Count} 个" : $"系统占用 {systemConflicts.Count} 个");
        }
        else if (_probeResults.Count > 0)
        {
            parts.Add("L1 试注册：全部可用");
        }

        StatusText.Text = string.Join("　|　", parts);
        StatusPath.Text = SettingsService.ConfigFilePath;
        StatusDot.Fill = trouble ? FocalPalette.SignalWarn : FocalPalette.SignalOk;
    }

    // ---------------------------------------------------------------- 写入 / 落盘

    private void Apply(string key, string? value, FrameworkElement source)
    {
        if (_building)
        {
            return;
        }

        var definition = SettingsSchema.Find(key);

        if (!SettingsBinder.Write(Settings, key, value, out string error))
        {
            if (_errorLabels.TryGetValue(key, out var label))
            {
                label.Text = error;
                label.Visibility = Visibility.Visible;
            }

            ShowError(error, key);
            return;
        }

        if (_errorLabels.TryGetValue(key, out var existing))
        {
            existing.Text = string.Empty;
            existing.Visibility = Visibility.Collapsed;
        }

        ClearError(key);
        ScheduleSave();

        // 系统集成与剪贴板监听需要宿主立刻反应
        SettingsChanged?.Invoke();
    }

    private void ScheduleSave()
    {
        _pendingSave = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        UpdateBanner();
    }

    /// <summary>立刻落盘（关闭设置窗口、改热键、导入后调用）。</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        _pendingSave = false;
        _settings.Save();
    }

    // ---------------------------------------------------------------- 维护分组

    private void AddMaintenanceSection()
    {
        var rows = new List<FrameworkElement>();

        // 版本：当前版本 + 通道 + 清单来源（本地路径 / https 一眼可辨）
        _versionText = new TextBlock
        {
            Style = StyleOf("Focal.DataText"),
            Foreground = BrushOf("Focal.OnDarkMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Text = VersionSummary(),
            Margin = new Thickness(0, MetricOf("Focal.SpaceXs"), 0, 0),
        };

        var versionRow = BuildRow("当前版本", "程序集版本、更新通道与清单来源（本地路径 / https）", null, out _, out StackPanel versionMain);
        versionMain.Children.Add(_versionText);
        rows.Add(versionRow);

        // 更新：检查 / 下载安装 + 结论
        _checkUpdatesButton = SideButton("检查更新", "读取 update.json 并给出结论；不会自动下载", () => _ = RunUpdateCheckAsync());
        _installUpdateButton = SideButton("下载并安装", "下载 → 校验 SHA-256 → 提示安装（per-user，不需要管理员）", () => _ = RunUpdateInstallAsync());
        _installUpdateButton.IsEnabled = false;

        var updateButtons = new StackPanel { Orientation = Orientation.Horizontal };
        updateButtons.Children.Add(_checkUpdatesButton);
        updateButtons.Children.Add(_installUpdateButton);

        var updateRow = BuildRow(
            "检查更新",
            "只读一份很小的 update.json（版本 / 下载地址 / SHA-256），不上报任何本机信息；下载后先校验再问你要不要装。",
            updateButtons,
            out _,
            out StackPanel updateMain);

        _updateStatusText = new TextBlock
        {
            Text = "尚未检查更新。",
            Style = StyleOf("Focal.CaptionText"),
            Foreground = BrushOf("Focal.OnDarkFaintBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, MetricOf("Focal.SpaceXs"), 0, 0),
        };

        updateMain.Children.Add(_updateStatusText);
        rows.Add(updateRow);

        // 诊断：运行状态 + 诊断包
        var diagnostics = new TextBlock
        {
            Text = DiagnosticsProvider?.Invoke() ?? "(诊断信息不可用)",
            Style = StyleOf("Focal.DataText"),
            Foreground = BrushOf("Focal.OnDarkMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, MetricOf("Focal.SpaceXs"), 0, 0),
        };

        var stateRow = BuildRow("运行状态", "当前采集后端、虚拟桌面、贴图数量等（只读，随打开时刷新）", null, out _, out StackPanel stateMain);
        stateMain.Children.Add(diagnostics);
        rows.Add(stateRow);

        _exportDiagnosticsButton = SideButton("导出诊断包…", "日志 + 配置 + 显示器拓扑 + 热键表 + 崩溃证据", () => _ = RunExportDiagnosticsAsync());

        var diagnosticsRow = BuildRow(
            "诊断包",
            "打包日志与配置用于排查问题：用户目录已脱敏，不含剪贴板内容与任何凭据。",
            _exportDiagnosticsButton,
            out _,
            out StackPanel diagnosticsMain);

        _diagnosticsStatusText = new TextBlock
        {
            Text = "导出后可以直接发给维护者；报告里不会出现你的截图内容。",
            Style = StyleOf("Focal.CaptionText"),
            Foreground = BrushOf("Focal.OnDarkFaintBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, MetricOf("Focal.SpaceXs"), 0, 0),
        };

        diagnosticsMain.Children.Add(_diagnosticsStatusText);
        rows.Add(diagnosticsRow);

        // 目录与本地操作
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(SideButton("打开配置目录", "配置文件所在的目录", () => OpenPath(SettingsService.ConfigDirectory)));
        actions.Children.Add(SideButton("打开保存目录", "截图默认落盘的位置", () => OpenPath(SaveService.ResolveDirectory(Settings))));
        actions.Children.Add(SideButton("打开日志目录", "运行日志与崩溃证据", () => OpenPath(Log.LogDirectory)));
        actions.Children.Add(SideButton("复制当前配置到剪贴板", "把 settings.json 的内容放进剪贴板", () => CopyConfigToClipboard()));
        actions.Children.Add(SideButton("清理贴图备份", "贴图备份目录里的历史会话", () => ClearPinBackupsNow()));

        var actionsRow = BuildRow(
            "配置与目录",
            "配置文件是纯 JSON，可以直接编辑后重启；导入时未知项与非法值都会被拒绝并报告，不会静默丢弃。",
            actions,
            out _,
            out _);

        rows.Add(actionsRow);

        var header = BuildSectionHeader("维护", "版本 · 更新 · 诊断包 · 配置目录与日志");
        ContentHost.Children.Add(header);
        ContentHost.Children.Add(GroupCard(rows));
        AddRailItem("维护", "speed", null, header);
    }

    private void CopyConfigToClipboard()
    {
        try
        {
            System.Windows.Clipboard.SetText(_settings.ExportJson());
            ShowStatus("配置已复制", "当前配置已复制到剪贴板，可以直接粘进工单或聊天窗口。");
        }
        catch (Exception ex)
        {
            ShowError($"复制配置失败：{ex.Message}");
        }
    }

    private void ClearPinBackupsNow()
    {
        ClearPinBackups?.Invoke();
        ShowStatus("贴图备份已清理", "贴图备份目录里的历史会话已删除。");
    }

    /// <summary>维护分组顶部的版本串：版本号 + 通道 + 清单来源（本地路径 / https 一眼可辨）。</summary>
    private string VersionSummary()
    {
        string version = Updater?.CurrentVersion ?? UpdateService.GetCurrentVersion();
        string channel = Updater?.Channel ?? UpdateChannels.Parse(Settings.UpdateChannel);
        string source = Updater?.ManifestSource ?? Settings.UpdateManifestUrl;
        string kind = UpdateService.ResolveSourceKind(source) == UpdateSourceKind.LocalFile ? "本地路径" : "https";

        return $"当前版本　WhSnap {version}（{channel} 通道）\n更新清单　{kind}：{source}";
    }

    /// <summary>
    /// 检查更新并把结论写进维护分组。自检直接调用它（与按钮同一条路径），
    /// 因此"按钮 → 检查 → 状态文字"这条链路是被机器验证过的。
    /// </summary>
    internal async Task<string> RunUpdateCheckAsync()
    {
        if (Updater is null)
        {
            SetInstallEnabled(false);
            return SetUpdateStatus("更新服务不可用。");
        }

        SetUpdateStatus("正在检查更新…");
        SetInstallEnabled(false);

        if (_checkUpdatesButton is not null)
        {
            _checkUpdatesButton.IsEnabled = false;
        }

        try
        {
            var result = await Updater.CheckAsync();
            _lastUpdateResult = result;

            if (result is { CanUpdate: true, Manifest: not null })
            {
                // UpdateRules 给出的原因本身就含"发现新版本 x.y.z（当前 …）：更新说明"，直接呈现
                SetInstallEnabled(InstallUpdate is not null);
                return SetUpdateStatus(result.Message);
            }

            return SetUpdateStatus(result.Message);
        }
        catch (Exception ex)
        {
            // 正常情况下 UpdateCoordinator 不会抛（失败已转成原因文字），这里是最后一道兜底
            Log.Error("检查更新失败", ex);
            return SetUpdateStatus($"检查更新失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (_checkUpdatesButton is not null)
            {
                _checkUpdatesButton.IsEnabled = true;
            }
        }
    }

    /// <summary>维护分组「下载并安装」：下载/校验/询问/退出自身都由宿主实现，这里只负责显示状态。</summary>
    internal async Task<string> RunUpdateInstallAsync()
    {
        var manifest = _lastUpdateResult is { CanUpdate: true, Manifest: not null } result ? result.Manifest : null;

        if (manifest is null)
        {
            return SetUpdateStatus("没有可安装的更新，请先点「检查更新」。");
        }

        if (InstallUpdate is null)
        {
            return SetUpdateStatus("更新服务不可用。");
        }

        SetInstallEnabled(false);
        SetUpdateStatus($"正在下载 WhSnap {manifest.Version} 并校验 SHA-256…");

        try
        {
            return SetUpdateStatus(await InstallUpdate(manifest));
        }
        catch (Exception ex)
        {
            Log.Error("下载或安装更新失败", ex);
            SetInstallEnabled(true);
            return SetUpdateStatus($"更新失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private string SetUpdateStatus(string text)
    {
        if (_updateStatusText is not null)
        {
            _updateStatusText.Text = text;
        }

        return text;
    }

    /// <summary>导出诊断包并把结果写进维护分组（自检与按钮走同一条路径）。</summary>
    internal async Task<string> RunExportDiagnosticsAsync()
    {
        if (ExportDiagnostics is null)
        {
            return SetDiagnosticsStatus("诊断包导出不可用。");
        }

        SetDiagnosticsStatus("正在打包诊断包…");

        if (_exportDiagnosticsButton is not null)
        {
            _exportDiagnosticsButton.IsEnabled = false;
        }

        try
        {
            return SetDiagnosticsStatus(await ExportDiagnostics());
        }
        catch (Exception ex)
        {
            Log.Error("导出诊断包失败", ex);
            return SetDiagnosticsStatus($"导出诊断包失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (_exportDiagnosticsButton is not null)
            {
                _exportDiagnosticsButton.IsEnabled = true;
            }
        }
    }

    private string SetDiagnosticsStatus(string text)
    {
        if (_diagnosticsStatusText is not null)
        {
            _diagnosticsStatusText.Text = text;
        }

        return text;
    }

    private void SetInstallEnabled(bool enabled)
    {
        if (_installUpdateButton is not null)
        {
            _installUpdateButton.IsEnabled = enabled;
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开目录失败（{path}）：{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 窗口机壳与底部操作条

    /// <summary>WindowStyle=None：拖标题栏空白处移动窗口（按钮自己吃掉了鼠标左键，不会误拖）。</summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ProbeHotkeys_Click(object sender, RoutedEventArgs e)
    {
        RefreshHotkeyStatuses(probe: true);
        UpdateBanner();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出 WhSnap 配置",
            FileName = $"whsnap-settings-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Filter = "JSON 配置|*.json",
            DefaultExt = ".json",
            InitialDirectory = SettingsService.ConfigDirectory,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, _settings.ExportJson(), Encoding.UTF8);
            ShowStatus("配置已导出", dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowError($"导出失败：{ex.Message}");
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 WhSnap 配置",
            Filter = "JSON 配置|*.json|所有文件|*.*",
            InitialDirectory = SettingsService.ConfigDirectory,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string json;

        try
        {
            json = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowError($"读取失败：{ex.Message}");
            return;
        }

        var result = _settings.ImportJson(json);
        SaveNow();

        _building = true;
        try
        {
            BuildUi();
        }
        finally
        {
            _building = false;
        }

        ApplyHotkeyBindings();
        RefreshHotkeyStatuses(probe: true);
        SettingsChanged?.Invoke();
        ShowStatus("配置已导入", result.Describe(), danger: result.HasProblems);

        MessageBox.Show(
            this,
            $"导入完成：{result.Describe()}"
            + (result.Unknown.Count > 0 ? $"\n\n未识别（已忽略）：{string.Join("、", result.Unknown)}" : string.Empty)
            + (result.Invalid.Count > 0 ? $"\n\n取值非法（已忽略）：{string.Join("、", result.Invalid)}" : string.Empty),
            "WhSnap 设置",
            MessageBoxButton.OK,
            result.HasProblems ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "把全部设置恢复为默认值？（热键也会一起恢复）", "WhSnap 设置", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        SettingsBinder.ResetToDefaults(Settings);
        Settings.Hotkeys.Clear();
        Settings.DisabledHotkeys.Clear();
        Settings.HotkeyConflictPolicy = "block";
        SaveNow();

        _building = true;
        try
        {
            BuildUi();
        }
        finally
        {
            _building = false;
        }

        ApplyHotkeyBindings();
        RefreshHotkeyStatuses(probe: true);
        SettingsChanged?.Invoke();
        ShowStatus("已恢复默认设置", "全部设置与热键都回到了出厂默认值。");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _saveTimer.Stop();
        _settings.Save();
        base.OnClosed(e);
    }
}
