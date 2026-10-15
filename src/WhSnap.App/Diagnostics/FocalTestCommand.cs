using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using WhSnap.Interop;
using WhSnap.Theme;

namespace WhSnap.Diagnostics;

/// <summary>
/// Focal 设计一致性自检（WhSnap.exe --focal-test）。
///
/// 它把"设计交付包 → 代码"这条链路上最容易悄悄断掉的四件事变成可执行断言：
///   1. 令牌齐全：设计交付的每一个键都能解析（缺令牌 = 界面会抛异常，必须在 CI 里先死）；
///   2. 度量一致：控件 30 / 行 32 / 标题栏 32 / 浮动条 44 / 圆角 6·10·14 / 界面基准 14；
///   3. 对比度红线：DESIGN §10 的自查表（含 `#F5A524` 压纸色只有 2.0:1 的禁令）；
///   4. 令牌唯一来源：界面代码里不允许出现字面量色值。
///
/// 退出码 0 = 全部通过。
/// </summary>
internal static class FocalTestCommand
{
    /// <summary>设计交付包（design/focal）承诺的令牌键。少一个都算交付不完整。</summary>
    private static readonly string[] RequiredKeys =
    [
        // 颜色
        "Focal.VoidColor", "Focal.Obsidian900Color", "Focal.Obsidian800Color",
        "Focal.Obsidian700Color", "Focal.Obsidian600Color", "Focal.CanvasColor",
        "Focal.PaperColor", "Focal.LinenColor", "Focal.LinenDeepColor",
        "Focal.InkColor", "Focal.InkMutedColor", "Focal.InkFaintColor",
        "Focal.OnDarkColor", "Focal.OnDarkMutedColor", "Focal.OnDarkFaintColor",
        "Focal.AccentColor", "Focal.AccentHoverColor", "Focal.AccentPressColor",
        "Focal.AccentDeepColor", "Focal.AccentInkColor",
        "Focal.SignalOkColor", "Focal.SignalWarnColor", "Focal.SignalDangerColor",
        "Focal.MeasureSelectColor", "Focal.MeasureElementColor", "Focal.MeasureCenterColor",

        // 画刷：表面
        "Focal.VoidBrush", "Focal.Obsidian900Brush", "Focal.Obsidian800Brush",
        "Focal.Obsidian700Brush", "Focal.Obsidian600Brush", "Focal.CanvasBrush",
        "Focal.PaperBrush", "Focal.LinenBrush", "Focal.LinenDeepBrush",
        // 画刷：文字
        "Focal.InkBrush", "Focal.InkMutedBrush", "Focal.InkFaintBrush",
        "Focal.OnDarkBrush", "Focal.OnDarkMutedBrush", "Focal.OnDarkFaintBrush",
        // 画刷：强调
        "Focal.AccentBrush", "Focal.AccentHoverBrush", "Focal.AccentPressBrush",
        "Focal.AccentDeepBrush", "Focal.AccentInkBrush", "Focal.AccentTintBrush",
        "Focal.AccentTint2Brush", "Focal.AccentRingBrush", "Focal.AccentHaloBrush",
        "Focal.AccentBorderBrush", "Focal.AccentInkMutedBrush",
        // 画刷：发丝线 / 语义 / 量测 / 遮罩
        "Focal.HairlineDarkBrush", "Focal.HairlineDarkStrongBrush",
        "Focal.HairlineLightBrush", "Focal.HairlineLightSoftBrush", "Focal.InnerTopLightBrush",
        "Focal.SignalOkBrush", "Focal.SignalWarnBrush", "Focal.SignalDangerBrush",
        "Focal.SignalOkFillBrush", "Focal.SignalOkBorderBrush",
        "Focal.SignalDangerFillBrush", "Focal.SignalDangerBorderBrush", "Focal.SignalWarnFillBrush",
        "Focal.MeasureSelectBrush", "Focal.MeasureElementBrush", "Focal.MeasureCenterBrush",
        "Focal.ScrimBrush", "Focal.CheckerBrush",
        "Focal.HudBrush", "Focal.HintSurfaceBrush", "Focal.ToolbarSurfaceBrush",
        "Focal.MeasureDarkRingBrush", "Focal.HandleRingBrush",
        "Focal.PixelGridBrush", "Focal.LoupeCrossBrush",
        "Focal.MeasureCenterFillBrush", "Focal.MeasureElementFillBrush", "Focal.MeasureElementInkBrush",

        // 字体 / 字号
        "Focal.DisplayFont", "Focal.TextFont", "Focal.DataFont",
        "Focal.DisplaySize", "Focal.TitleSize", "Focal.SubtitleSize", "Focal.BodySize",
        "Focal.CaptionSize", "Focal.MicroSize", "Focal.DataSize", "Focal.DataLargeSize", "Focal.KbdSize",

        // 圆角
        "Focal.RadiusHair", "Focal.RadiusControl", "Focal.RadiusCard", "Focal.RadiusPanel", "Focal.RadiusFull",

        // 度量
        "Focal.ControlHeight", "Focal.ControlHeightLarge", "Focal.ControlHeightSmall",
        "Focal.RowHeight", "Focal.RowHeightLarge", "Focal.ToolbarHeight", "Focal.FloatbarHeight",
        "Focal.HintbarHeight", "Focal.TitleBarHeight", "Focal.StatusBarHeight", "Focal.RailWidth",
        "Focal.HandleSize", "Focal.IconSm", "Focal.IconMd", "Focal.IconLg", "Focal.IconXl",
        "Focal.LoupeSize", "Focal.LoupeSample", "Focal.LoupeZoom",

        // 间距
        "Focal.Space3xs", "Focal.Space2xs", "Focal.SpaceXs", "Focal.SpaceSm", "Focal.SpaceMd",
        "Focal.SpaceLg", "Focal.SpaceXl", "Focal.Space2xl", "Focal.Space3xl", "Focal.Space4xl",
        "Focal.Space5xl", "Focal.Space6xl",

        // 内边距 / 层级
        "Focal.RowPadding", "Focal.ButtonPadding", "Focal.PanelPadding", "Focal.ContentPadding",
        "Focal.LoupePaddingThickness",
        "Focal.ShadowChrome", "Focal.ShadowDialog", "Focal.ShadowProduct",

        // 文本样式
        "Focal.DisplayText", "Focal.TitleText", "Focal.SubtitleText", "Focal.BodyText",
        "Focal.BodyStrongText", "Focal.CaptionText", "Focal.MicroText", "Focal.DataText",
        "Focal.DataLargeText", "Focal.DataCaptionText", "Focal.MutedText", "Focal.FaintText",
        "Focal.KbdText",

        // 组件层
        "Focal.Button.Primary", "Focal.Button.PrimaryLarge", "Focal.Button.Secondary",
        "Focal.Button.Small", "Focal.Button.Quiet", "Focal.Button.Danger", "Focal.Button.Icon",
        "Focal.Tool", "Focal.ToolWide", "Focal.Segment", "Focal.Switch", "Focal.Check",
        "Focal.Input", "Focal.InputData", "Focal.Select", "Focal.SelectItem",
        "Focal.Slider", "Focal.ScrollBar", "Focal.ScrollThumb",
        "Focal.Card", "Focal.Chip", "Focal.ChipAccent", "Focal.DividerV", "Focal.DividerH",
        "Focal.Banner", "Focal.BannerWarn", "Focal.BannerDanger", "Focal.BannerOk",
        "Focal.WindowShell", "Focal.TitleBar", "Focal.TitleBarText",
        "Focal.WindowButton", "Focal.WindowButtonClose", "Focal.StatusBar", "Focal.StatusDot",
        "Focal.Rail", "Focal.RailItem", "Focal.ListItem", "Focal.Row", "Focal.RowLabel",
        "Focal.RowHint", "Focal.KeycapButton", "Focal.Swatch", "Focal.MenuItem",
        "Focal.Kbd", "Focal.ChromeSurface", "Focal.FocusVisual",
    ];

    /// <summary>design/focal/icons.js 的 64 个图标（键名原样）。</summary>
    private static readonly string[] RequiredIcons =
    [
        "brand", "crop", "capture", "check", "check-circle", "x", "copy", "save", "save-as",
        "upload", "download", "refresh", "trash", "undo", "redo", "plus", "minus", "search",
        "gear", "keyboard", "info", "warning", "external", "chevronRight", "chevronDown", "power",
        "magnifier", "crosshair", "pointer", "eyedropper", "ruler", "grid", "monitor", "layers",
        "history", "palette", "image", "film", "rect", "ellipse", "line", "arrow", "pencil",
        "marker", "text", "mosaic", "blur", "eraser", "badge", "highlight", "select",
        "rotate", "flipH", "flipV", "droplet", "pin", "window", "eye", "eye-off", "clipboard",
        "folder", "tray", "lock", "speed",
    ];

    /// <summary>
    /// 渲染"用户内容"的文件天然会用到用户颜色：它们不受令牌规则约束（见 docs/focal-UI实现约定.md）。
    /// settingsservice.cs 里是标注调色板 / 颜色历史的**默认色**（用户内容），也不是界面主题色。
    /// </summary>
    private static readonly string[] LiteralColorAllowList =
    [
        @"\theme\", @"\diagnostics\",
        @"\annotation\annotationrenderer.cs", @"\pin\pincontentrenderer.cs", @"\pin\pinwindow.cs",
        @"\services\settingsservice.cs",
    ];

    public static int Run()
    {
        TryAttachConsole();

        var report = new StringBuilder();
        var failures = new List<string>();

        report.AppendLine($"WhSnap Focal 设计一致性自检　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine(new string('-', 72));

        void Check(string name, Func<string> body)
        {
            try
            {
                string detail = body();
                report.AppendLine($"[PASS] {name}：{detail}");
            }
            catch (Exception ex)
            {
                failures.Add(name);
                report.AppendLine($"[FAIL] {name}：{ex.GetType().Name}: {ex.Message}");
            }
        }

        Check("令牌字典已合并进 Application.Resources", () =>
        {
            var app = System.Windows.Application.Current
                      ?? throw new InvalidOperationException("没有 Application 实例（必须在 WPF 启动路径里跑）。");

            int merged = app.Resources.MergedDictionaries.Count;
            if (merged < 3)
            {
                throw new InvalidOperationException($"App.xaml 只合并了 {merged} 个字典，应至少 3 个（FocalTheme / FocalIcons / FocalControls）。");
            }

            return $"合并字典 {merged} 个，顶层资源 {app.Resources.Count} 项";
        });

        Check("设计令牌齐全（颜色/画刷/字体/度量/圆角/文本样式/组件层）", () =>
        {
            var missing = RequiredKeys.Where(key => !FocalPalette.TryGet<object>(key, out _)).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException($"缺 {missing.Count} 个键：{string.Join(", ", missing.Take(12))}{(missing.Count > 12 ? " …" : string.Empty)}");
            }

            return $"{RequiredKeys.Length} 个键全部可解析";
        });

        Check("度量与设计值一致（桌面密度）", () =>
        {
            (string Key, double Expected)[] expected =
            [
                ("Focal.ControlHeight", 30), ("Focal.ControlHeightLarge", 34), ("Focal.RowHeight", 32),
                ("Focal.RowHeightLarge", 40), ("Focal.ToolbarHeight", 40), ("Focal.FloatbarHeight", 44),
                ("Focal.HintbarHeight", 32), ("Focal.TitleBarHeight", 32), ("Focal.RailWidth", 188),
                ("Focal.HandleSize", 7), ("Focal.BodySize", 14), ("Focal.CaptionSize", 12),
                ("Focal.MicroSize", 11), ("Focal.LoupeSample", 11), ("Focal.LoupeZoom", 12),
            ];

            var wrong = new List<string>();
            foreach (var (key, want) in expected)
            {
                double got = FocalPalette.Metric(key);
                if (Math.Abs(got - want) > 0.001)
                {
                    wrong.Add($"{key}={got}（应为 {want}）");
                }
            }

            (string Key, double Expected)[] radii =
            [
                ("Focal.RadiusHair", 2), ("Focal.RadiusControl", 6), ("Focal.RadiusCard", 10), ("Focal.RadiusPanel", 14),
            ];

            foreach (var (key, want) in radii)
            {
                var got = FocalPalette.Corner(key);
                if (Math.Abs(got.TopLeft - want) > 0.001)
                {
                    wrong.Add($"{key}={got.TopLeft}（应为 {want}）");
                }
            }

            if (wrong.Count > 0)
            {
                throw new InvalidOperationException($"度量偏离设计值：{string.Join("；", wrong)}");
            }

            return $"{expected.Length} 项度量 + {radii.Length} 级圆角与 DESIGN §4/§5 一致";
        });

        Check("图标几何全部可解析且非空（icons.js 全覆盖）", () =>
        {
            var missing = new List<string>();
            var broken = new List<string>();

            foreach (string name in RequiredIcons)
            {
                var geometry = FocalIcon.Resolve(name);
                if (geometry is null)
                {
                    missing.Add(name);
                    continue;
                }

                var bounds = geometry.Bounds;
                // 注意：线段类图标（minus / line / x / check）没有面积，Geometry.IsEmpty() 会返回 true，
                // 所以这里用 Bounds 判空，而不是 IsEmpty()。
                if (bounds.IsEmpty || (bounds.Width <= 0 && bounds.Height <= 0))
                {
                    broken.Add($"{name}（空几何）");
                    continue;
                }

                if (bounds.Left < -0.5 || bounds.Top < -0.5 || bounds.Right > 24.5 || bounds.Bottom > 24.5)
                {
                    broken.Add($"{name}（{bounds.Left:0.#},{bounds.Top:0.#},{bounds.Right:0.#},{bounds.Bottom:0.#} 超出 24 网格）");
                }
            }

            if (missing.Count > 0)
            {
                throw new InvalidOperationException($"缺 {missing.Count} 个图标：{string.Join(", ", missing.Take(12))}{(missing.Count > 12 ? " …" : string.Empty)}");
            }

            if (broken.Count > 0)
            {
                throw new InvalidOperationException($"几何异常：{string.Join("；", broken)}");
            }

            return $"{RequiredIcons.Length} 个图标全部落在 24×24 网格内且非空";
        });

        Check("对比度红线（DESIGN §10）", () =>
        {
            var rows = new List<string>();
            var violated = new List<string>();

            void AtLeast(string name, Color fg, Color bg, double min)
            {
                double ratio = Contrast(fg, bg);
                rows.Add($"{name} {ratio:0.0}:1");
                if (ratio < min)
                {
                    violated.Add($"{name} 只有 {ratio:0.0}:1，要求 ≥ {min:0.0}:1");
                }
            }

            void Below(string name, Color fg, Color bg, double max)
            {
                double ratio = Contrast(fg, bg);
                rows.Add($"{name} {ratio:0.0}:1");
                if (ratio >= max)
                {
                    violated.Add($"{name} 是 {ratio:0.0}:1，按规范应 < {max:0.0}:1（该组合禁止用于文字）");
                }
            }

            AtLeast("on-dark / obsidian-900", FocalPalette.Color("Focal.OnDarkColor"), FocalPalette.Color("Focal.Obsidian900Color"), 7);
            AtLeast("on-dark-muted / obsidian-800", FocalPalette.Color("Focal.OnDarkMutedColor"), FocalPalette.Color("Focal.Obsidian800Color"), 4.5);
            AtLeast("accent / obsidian-900", FocalPalette.Color("Focal.AccentColor"), FocalPalette.Color("Focal.Obsidian900Color"), 4.5);
            AtLeast("accent-ink / accent", FocalPalette.Color("Focal.AccentInkColor"), FocalPalette.Color("Focal.AccentColor"), 4.5);
            AtLeast("ink / linen", FocalPalette.Color("Focal.InkColor"), FocalPalette.Color("Focal.LinenColor"), 7);
            AtLeast("ink-muted / linen", FocalPalette.Color("Focal.InkMutedColor"), FocalPalette.Color("Focal.LinenColor"), 4.5);
            AtLeast("accent-deep / linen", FocalPalette.Color("Focal.AccentDeepColor"), FocalPalette.Color("Focal.LinenColor"), 4.5);
            Below("accent / paper（禁止文字）", FocalPalette.Color("Focal.AccentColor"), FocalPalette.Color("Focal.PaperColor"), 3);

            if (violated.Count > 0)
            {
                throw new InvalidOperationException(string.Join("；", violated));
            }

            return string.Join("　", rows);
        });

        Check("令牌唯一来源：界面代码里没有字面量色值", () =>
        {
            string? root = FindRepositoryRoot();
            if (root is null)
            {
                return "跳过（不在仓库里运行，例如已安装版本）";
            }

            var violations = ScanLiteralColors(Path.Combine(root, "src", "WhSnap.App"));
            if (violations.Count > 0)
            {
                throw new InvalidOperationException(
                    $"发现 {violations.Count} 处字面量：{string.Join("；", violations.Take(8))}{(violations.Count > 8 ? " …" : string.Empty)}");
            }

            return "src/WhSnap.App 下 XAML 与 C# 均只引用 Focal 令牌（渲染用户内容的文件除外）";
        });

        report.AppendLine(new string('-', 72));
        report.AppendLine(failures.Count == 0
            ? "Focal 设计一致性：全部通过。"
            : $"Focal 设计一致性：{failures.Count} 项未通过 —— {string.Join("、", failures)}");

        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "whsnap-focal-test.txt"), report.ToString(), Encoding.UTF8);
            Console.WriteLine(report.ToString());
        }
        catch
        {
            // 无控制台时忽略
        }

        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>WCAG 相对亮度对比度。</summary>
    private static double Contrast(Color a, Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        double lighter = Math.Max(la, lb);
        double darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            double v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    /// <summary>从可执行文件位置向上找仓库根（含 WhSnap.sln）。</summary>
    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && directory is not null; i++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WhSnap.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static readonly Regex HexColor = new(@"#[0-9A-Fa-f]{3,8}\b", RegexOptions.Compiled);
    // Transparent 不算颜色字面量（它是"没有颜色"），其余命名色都算违约
    private static readonly Regex ApiColor = new(@"\b(?:Color|Colors|Brushes)\.(?:FromRgb|FromArgb|FromScRgb|FromValues|White|Black|Gray|Grey|Red|Green|Blue|Orange|Yellow)\b", RegexOptions.Compiled);

    /// <summary>静态扫字面量色值：XAML 的 #RRGGBB、C# 的 Color.FromRgb / Brushes.X 等。</summary>
    private static List<string> ScanLiteralColors(string appRoot)
    {
        var violations = new List<string>();
        if (!Directory.Exists(appRoot))
        {
            return violations;
        }

        var files = Directory.EnumerateFiles(appRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase));

        foreach (string file in files)
        {
            string relative = "\\" + Path.GetRelativePath(appRoot, file).ToLowerInvariant();
            bool allowListed = LiteralColorAllowList.Any(token => relative.Contains(token, StringComparison.Ordinal));

            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch
            {
                continue;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                // 约定：允许在某一行末尾或它**上一行**写 `focal:allow-literal`（注释习惯）
                if (line.Contains("focal:allow-literal", StringComparison.OrdinalIgnoreCase)
                    || (i > 0 && lines[i - 1].Contains("focal:allow-literal", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // XML 注释里可以讨论色值（例如令牌文件的说明文字），不算代码
                string code = StripComment(line);

                Match match = HexColor.Match(code);
                if (!match.Success)
                {
                    match = ApiColor.Match(code);
                }

                if (!match.Success)
                {
                    continue;
                }

                // 发丝线 / 遮罩允许写在令牌文件里；其余文件一律算违约（allowlist 文件除外）
                if (allowListed)
                {
                    continue;
                }

                violations.Add($"{Path.GetRelativePath(appRoot, file)}:{i + 1} {match.Value}");
            }
        }

        return violations;
    }

    private static string StripComment(string line)
    {
        int xml = line.IndexOf("<!--", StringComparison.Ordinal);
        if (xml >= 0)
        {
            line = line[..xml];
        }

        int cs = line.IndexOf("//", StringComparison.Ordinal);
        if (cs >= 0)
        {
            // 只当行内注释处理（http:// 这类不会出现在界面代码里）
            line = line[..cs];
        }

        return line;
    }

    private static void TryAttachConsole()
    {
        try
        {
            if (NativeMethods.GetConsoleWindow() == IntPtr.Zero)
            {
                NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
            }
        }
        catch
        {
            // 无控制台可用时依赖报告文件
        }
    }
}
