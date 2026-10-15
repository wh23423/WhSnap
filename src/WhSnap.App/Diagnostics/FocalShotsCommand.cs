using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WhSnap.Interop;
using WhSnap.Theme;

namespace WhSnap.Diagnostics;

/// <summary>
/// Focal 视觉稿回归：WhSnap.exe --focal-shots [输出目录]
///
/// 把组件层（按钮 / 工具键 / 开关 / 输入 / 行 / 横幅 / 卡片 / 图标网格 …）在一张固定画布上
/// 真正**渲染**成 PNG。它同时是"模板能不能跑"的回归：任何 StaticResource 解析失败或
/// ControlTemplate 写错都会在这里抛异常（--focal-test 只能验证键存在，验证不了模板）。
///
/// 产物可直接和 design/focal/shots/*.png 对照评审。
/// </summary>
internal static class FocalShotsCommand
{
    public static int Run(string? outputDirectory)
    {
        TryAttachConsole();

        string directory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.Combine(Path.GetTempPath(), "whsnap-focal-shots")
            : Path.GetFullPath(outputDirectory);

        var log = new StringBuilder();
        var failures = new List<string>();

        log.AppendLine($"WhSnap Focal 视觉稿导出　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine($"输出目录：{directory}");
        log.AppendLine(new string('-', 72));

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            log.AppendLine($"[FAIL] 输出目录不可用：{ex.Message}");
            Console.WriteLine(log.ToString());
            return 1;
        }

        void Shot(string name, Func<FrameworkElement> build, int width, int height)
        {
            try
            {
                var root = build();
                string path = Path.Combine(directory, name + ".png");
                Render(root, path, width, height);
                var info = new FileInfo(path);
                log.AppendLine($"[PASS] {name}.png　{width}×{height}　{info.Length / 1024} KB");
            }
            catch (Exception ex)
            {
                failures.Add(name);
                log.AppendLine($"[FAIL] {name}：{ex.GetType().Name}: {ex.Message}");
            }
        }

        void WindowShot(string name, Func<Window> build, int width, int height)
        {
            Window? window = null;
            try
            {
                window = build();
                string path = Path.Combine(directory, name + ".png");
                RenderWindow(window, path, width, height);
                var info = new FileInfo(path);
                log.AppendLine($"[PASS] {name}.png　{width}×{height}　{info.Length / 1024} KB");
            }
            catch (Exception ex)
            {
                failures.Add(name);
                log.AppendLine($"[FAIL] {name}：{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try
                {
                    window?.Close();
                }
                catch
                {
                    // 关窗失败不影响导出
                }
            }
        }

        Shot("focal-tokens", () => BuildTokenSheet(), 1180, 700);
        Shot("focal-components", () => BuildComponentSheet(), 1180, 820);
        Shot("focal-icons", () => BuildIconSheet(), 1180, 560);

        // 真实窗口：整窗渲染（设计稿是固定画布，这里按目标尺寸取景）
        WindowShot("surface-settings", BuildSettingsWindow, 980, 680);
        WindowShot("surface-annotation", BuildAnnotationWindow, 1280, 800);
        WindowShot("surface-pinPanel", BuildPinPanelWindow, 760, 560);
        WindowShot("surface-onboarding", BuildOnboardingWindow, 880, 620);

        log.AppendLine(new string('-', 72));
        log.AppendLine(failures.Count == 0 ? "Focal 视觉稿：全部渲染成功。" : $"Focal 视觉稿：{failures.Count} 张失败 —— {string.Join("、", failures)}");

        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "whsnap-focal-shots.txt"), log.ToString(), Encoding.UTF8);
            Console.WriteLine(log.ToString());
        }
        catch
        {
            // 无控制台时忽略
        }

        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>离屏渲染：不建窗口，直接 Measure/Arrange 后渲染到 RenderTargetBitmap。</summary>
    private static void Render(FrameworkElement root, string path, int width, int height)
    {
        var host = new Border
        {
            Background = FocalPalette.Obsidian900,
            Width = width,
            Height = height,
            Child = root,
        };

        host.Measure(new System.Windows.Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// 整窗渲染：真实窗口先 Show 再取内容渲染（Loaded 里生成的内容才会出现），
    /// 然后把窗口丢到屏幕外，避免导出时在用户面前闪一下。
    /// 取景尺寸用窗口**自己排布出来的**尺寸（强行套设计稿尺寸会让内容被裁切，看到假缺陷）。
    /// </summary>
    private static void RenderWindow(Window window, string path, int width, int height)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowInTaskbar = false;
        window.Topmost = false;
        window.Left = -32000;
        window.Top = -32000;
        window.Show();
        Pump();

        var content = window.Content as FrameworkElement
                      ?? throw new InvalidOperationException("窗口没有可渲染的内容。");

        content.UpdateLayout();
        Pump();

        // 渲染窗口自身（而不是 Grid 内容）：无边框窗口的 18px 投影余量是布局偏移，
        // 渲染子元素会把这段偏移一起带进来，看起来像"内容被裁掉"。
        int renderWidth = (int)Math.Ceiling(window.ActualWidth);
        int renderHeight = (int)Math.Ceiling(window.ActualHeight);
        if (renderWidth <= 0 || renderHeight <= 0)
        {
            renderWidth = width;
            renderHeight = height;
        }

        var bitmap = new RenderTargetBitmap(renderWidth, renderHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        _ = content;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    // ── 真实界面 ──────────────────────────────────────────────────────────────

    private static Window BuildSettingsWindow()
    {
        var settings = new Services.SettingsService();
        settings.Load();

        return new Settings.SettingsWindow(settings)
        {
            ApplyHotkeys = _ => { },
            ProbeHotkeys = _ => [],
            DiagnosticsProvider = () => "视觉稿导出：诊断信息占位。",
        };
    }

    private static Window BuildPinPanelWindow()
    {
        var settings = new Services.SettingsService();
        settings.Load();

        string backupRoot = Path.Combine(Path.GetTempPath(), "whsnap-focal-shots-pins");
        var manager = new Pin.PinManager(settings, new Pin.PinBackupStore(backupRoot));
        manager.Initialize();

        return new Pin.PinPanelWindow(manager);
    }

    private static Window BuildAnnotationWindow()
    {
        var settings = new Services.SettingsService();
        settings.Load();

        var monitors = Capture.MonitorEnumerator.Enumerate();
        var monitor = monitors[0];
        var image = CreateBaseImage(1024, 560);
        var region = new Core.PhysicalRect(monitor.Bounds.X + 80, monitor.Bounds.Y + 80, image.PixelWidth, image.PixelHeight);

        return new Annotation.AnnotationWindow(image, region, monitor.ScaleX, settings.Current);
    }

    private static Window BuildOnboardingWindow()
    {
        var window = new Onboarding.OnboardingWindow();
        window.PreloadBindings(Core.HotkeyBindings.Resolve(scope: Core.HotkeyScope.Global));
        return window;
    }

    /// <summary>标注编辑器的示例底图：浅色卡片 + 琥珀色块，方便看清工具与样式条。</summary>
    private static BitmapSource CreateBaseImage(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(FocalPalette.Linen, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(FocalPalette.Paper, null, new Rect(24, 24, width - 48, 120));
            dc.DrawRectangle(FocalPalette.LinenDeep, null, new Rect(24, 168, (width - 72) / 2.0, height - 200));
            dc.DrawRectangle(FocalPalette.LinenDeep, null, new Rect(48 + ((width - 72) / 2.0), 168, (width - 72) / 2.0, height - 200));
            dc.DrawRectangle(FocalPalette.Accent, null, new Rect(48, 48, 220, 72));
            dc.DrawRectangle(FocalPalette.Obsidian800, null, new Rect(width - 320, height - 120, 260, 72));

            var text = new FormattedText(
                "Focal · 标注编辑器底图示例",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(FocalPalette.TextFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                20,
                FocalPalette.Ink,
                96);

            dc.DrawText(text, new System.Windows.Point(48, 66));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // ── 画布构件 ──────────────────────────────────────────────────────────────

    private static StackPanel Page(string title)
    {
        var page = new StackPanel { Margin = new Thickness(32, 28, 32, 28) };
        page.Children.Add(new TextBlock
        {
            Text = title,
            Style = FocalPalette.Style("Focal.TitleText"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        page.Children.Add(new TextBlock
        {
            Text = "WhSnap · Focal 设计语言 v1.0 · 由 --focal-shots 从真实的 WPF 令牌与组件层渲染",
            Style = FocalPalette.Style("Focal.CaptionText"),
            Margin = new Thickness(0, 0, 0, 20),
        });
        return page;
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        Style = FocalPalette.Style("Focal.MicroText"),
        Margin = new Thickness(0, 0, 0, 10),
    };

    private static Border Section(UIElement content) => new()
    {
        Background = FocalPalette.Obsidian800,
        BorderBrush = FocalPalette.HairlineDark,
        BorderThickness = new Thickness(1),
        CornerRadius = FocalPalette.RadiusCard,
        Padding = new Thickness(16),
        Margin = new Thickness(0, 0, 0, 16),
        Child = content,
    };

    private static WrapPanel Row(params UIElement[] children)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children)
        {
            if (child is FrameworkElement element)
            {
                element.Margin = new Thickness(0, 0, 12, 12);
            }

            panel.Children.Add(child);
        }

        return panel;
    }

    // ── 1. 令牌表（颜色 + 排版） ───────────────────────────────────────────────

    private static FrameworkElement BuildTokenSheet()
    {
        var page = Page("Focal 令牌");

        page.Children.Add(SectionLabel("颜色 · Obsidian / Paper / 强调 / 语义 / 量测"));
        var swatches = new WrapPanel();
        (string Key, string Name)[] colors =
        [
            ("Focal.VoidBrush", "void"), ("Focal.Obsidian900Brush", "obsidian-900"),
            ("Focal.Obsidian800Brush", "obsidian-800"), ("Focal.Obsidian700Brush", "obsidian-700"),
            ("Focal.Obsidian600Brush", "obsidian-600"), ("Focal.CanvasBrush", "canvas"),
            ("Focal.PaperBrush", "paper"), ("Focal.LinenBrush", "linen"),
            ("Focal.LinenDeepBrush", "linen-deep"), ("Focal.InkBrush", "ink"),
            ("Focal.InkMutedBrush", "ink-muted"), ("Focal.InkFaintBrush", "ink-faint"),
            ("Focal.OnDarkBrush", "on-dark"), ("Focal.OnDarkMutedBrush", "on-dark-muted"),
            ("Focal.OnDarkFaintBrush", "on-dark-faint"),
            ("Focal.AccentBrush", "accent"), ("Focal.AccentHoverBrush", "accent-hover"),
            ("Focal.AccentPressBrush", "accent-press"), ("Focal.AccentDeepBrush", "accent-deep"),
            ("Focal.AccentInkBrush", "accent-ink"),
            ("Focal.SignalOkBrush", "signal-ok"), ("Focal.SignalWarnBrush", "signal-warn"),
            ("Focal.SignalDangerBrush", "signal-danger"),
            ("Focal.MeasureSelectBrush", "measure-select"), ("Focal.MeasureElementBrush", "measure-element"),
            ("Focal.MeasureCenterBrush", "measure-center"),
        ];

        foreach (var (key, name) in colors)
        {
            var chip = new StackPanel { Margin = new Thickness(0, 0, 14, 14), Width = 150 };
            chip.Children.Add(new Border
            {
                Height = 46,
                CornerRadius = FocalPalette.RadiusCard,
                Background = FocalPalette.Brush(key),
                BorderBrush = FocalPalette.HairlineDarkStrong,
                BorderThickness = new Thickness(1),
            });
            chip.Children.Add(new TextBlock
            {
                Text = name,
                Style = FocalPalette.Style("Focal.CaptionText"),
                Margin = new Thickness(0, 6, 0, 0),
            });
            chip.Children.Add(new TextBlock
            {
                Text = FocalPalette.Hex(key.Replace("Brush", "Color", StringComparison.Ordinal)),
                Style = FocalPalette.Style("Focal.DataCaptionText"),
                Foreground = FocalPalette.OnDarkFaint,
            });
            swatches.Children.Add(chip);
        }

        page.Children.Add(Section(swatches));

        page.Children.Add(SectionLabel("排版 · 14px 基准 / 显示字号负字距 / 数据等宽"));
        var type = new StackPanel();
        (string Text, string Style, string Meta)[] lines =
        [
            ("显示 28 / 600", "Focal.DisplayText", "display · -0.4px"),
            ("标题 20 / 600", "Focal.TitleText", "title · -0.3px"),
            ("副标题 15 / 600", "Focal.SubtitleText", "subtitle · -0.2px"),
            ("正文 14 / 400 —— 界面基准密度", "Focal.BodyText", "body · 0"),
            ("正文强调 14 / 600", "Focal.BodyStrongText", "body-strong · -0.1px"),
            ("说明 12 / 400", "Focal.CaptionText", "caption"),
            ("小号大写标签 11 / 600", "Focal.MicroText", "micro · +0.4px"),
            ("数据 13 / 500 · 572 × 280 · 372, 330 · 100% · Ctrl+S", "Focal.DataText", "data · tabular-nums"),
        ];

        foreach (var (text, style, meta) in lines)
        {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sample = new TextBlock { Text = text, Style = FocalPalette.Style(style), VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = meta, Style = FocalPalette.Style("Focal.DataCaptionText"), Foreground = FocalPalette.OnDarkFaint };
            Grid.SetColumn(sample, 0);
            Grid.SetColumn(label, 1);
            row.Children.Add(sample);
            row.Children.Add(label);
            type.Children.Add(row);
        }

        page.Children.Add(Section(type));
        return page;
    }

    // ── 2. 组件画廊 ───────────────────────────────────────────────────────────

    private static FrameworkElement BuildComponentSheet()
    {
        var page = Page("Focal 组件");

        page.Children.Add(SectionLabel("按钮 · 两级语法（primary 是唯一的决定性动作，CTA 不用胶囊）"));
        var primary = new Button { Content = "完成", Style = FocalPalette.Style("Focal.Button.Primary") };
        var primaryLarge = new Button { Content = "开始使用", Style = FocalPalette.Style("Focal.Button.PrimaryLarge") };
        var secondary = new Button { Content = "保存", Style = FocalPalette.Style("Focal.Button.Secondary") };
        var quiet = new Button { Content = "取消", Style = FocalPalette.Style("Focal.Button.Quiet") };
        var danger = new Button { Content = "关闭全部", Style = FocalPalette.Style("Focal.Button.Danger") };
        var small = new Button { Content = "置顶", Style = FocalPalette.Style("Focal.Button.Small") };
        var disabled = new Button { Content = "不可用", Style = FocalPalette.Style("Focal.Button.Secondary"), IsEnabled = false };
        var iconButton = new Button
        {
            Style = FocalPalette.Style("Focal.Button.Quiet"),
            Content = FocalIcon.Create("gear", FocalPalette.IconMd),
        };
        page.Children.Add(Section(Row(primary, primaryLarge, secondary, quiet, danger, small, disabled, iconButton)));

        page.Children.Add(SectionLabel("工具键 / 分段 / 开关 / 复选框 / 键帽"));
        var toolOn = new ToggleButton
        {
            Style = FocalPalette.Style("Focal.ToolWide"),
            IsChecked = true,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { FocalIcon.Create("crop"), new TextBlock { Text = "区域", Margin = new Thickness(6, 0, 0, 0) } },
            },
        };
        var toolOff = new ToggleButton
        {
            Style = FocalPalette.Style("Focal.ToolWide"),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { FocalIcon.Create("pointer"), new TextBlock { Text = "元素", Margin = new Thickness(6, 0, 0, 0) } },
            },
        };
        var switchOn = new ToggleButton { Style = FocalPalette.Style("Focal.Switch"), IsChecked = true };
        var switchOff = new ToggleButton { Style = FocalPalette.Style("Focal.Switch") };
        var checkOn = new CheckBox { Content = "开机自启", IsChecked = true, Style = FocalPalette.Style("Focal.Check") };
        var checkOff = new CheckBox { Content = "显示颜色历史", Style = FocalPalette.Style("Focal.Check") };
        var keycap = new Border
        {
            Style = FocalPalette.Style("Focal.Kbd"),
            Child = new TextBlock { Text = "Ctrl+Shift+A", Style = FocalPalette.Style("Focal.KbdText") },
        };
        var recording = new ToggleButton
        {
            Content = "按下新键位…",
            Style = FocalPalette.Style("Focal.KeycapButton"),
            IsChecked = true,
        };
        page.Children.Add(Section(Row(toolOn, toolOff, switchOn, switchOff, checkOn, checkOff, keycap, recording)));

        page.Children.Add(SectionLabel("输入 / 下拉 / 滑杆 / 色板"));
        var input = new TextBox { Text = "WhSnap_{yyyyMMdd_HHmmss}", Width = 230, Style = FocalPalette.Style("Focal.InputData") };
        var select = new ComboBox { Width = 160, Style = FocalPalette.Style("Focal.Select") };
        select.Items.Add("自动（WGC → DXGI → GDI）");
        select.SelectedIndex = 0;
        var slider = new Slider { Width = 180, Minimum = 0, Maximum = 90, Value = 45, Style = FocalPalette.Style("Focal.Slider") };
        var swatchOn = new ToggleButton { Style = FocalPalette.Style("Focal.Swatch"), Background = FocalPalette.SignalDanger, IsChecked = true };
        var swatch = new ToggleButton { Style = FocalPalette.Style("Focal.Swatch"), Background = FocalPalette.MeasureElement };
        page.Children.Add(Section(Row(input, select, slider, swatchOn, swatch)));

        page.Children.Add(SectionLabel("横幅（语义色只出现在状态面）/ 卡片 / 胶囊"));
        var banners = new StackPanel();
        banners.Children.Add(Banner("Focal.BannerWarn", "Focal.SignalWarnBrush", "warning", "检测到热键冲突", "Ctrl+Alt+A 已被其他程序占用，点这里改绑或先停用。"));
        banners.Children.Add(Banner("Focal.BannerDanger", "Focal.SignalDangerBrush", "x", "配置写入失败", "目标目录不可写；已回滚到上一次的配置。"));
        banners.Children.Add(Banner("Focal.BannerOk", "Focal.SignalOkBrush", "check", "更新已安装", "WhSnap 1.0.0-preview.2 已就绪，重启后生效。"));
        page.Children.Add(Section(banners));

        page.Children.Add(SectionLabel("列表行（悬停才出操作）/ 导航项 / 菜单项"));
        var list = new StackPanel();
        list.Children.Add(ListRow("pin", "贴图 #3 · 384 × 216", "2026-03-28 09:14 · 屏幕右上角", true));
        list.Children.Add(ListRow("pin", "贴图 #2 · 1024 × 640", "2026-03-28 09:02 · 副屏居中", false));
        page.Children.Add(Section(list));

        return page;
    }

    private static Border Banner(string styleKey, string iconBrushKey, string icon, string title, string detail)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var glyph = FocalIcon.Create(icon, FocalPalette.IconMd);
        glyph.Foreground = FocalPalette.Brush(iconBrushKey);
        glyph.Margin = new Thickness(0, 1, 10, 0);
        glyph.VerticalAlignment = VerticalAlignment.Top;

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, Style = FocalPalette.Style("Focal.BodyStrongText") });
        text.Children.Add(new TextBlock { Text = detail, Style = FocalPalette.Style("Focal.CaptionText") });

        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(glyph);
        grid.Children.Add(text);

        return new Border { Style = FocalPalette.Style(styleKey), Child = grid };
    }

    private static Border ListRow(string icon, string title, string meta, bool selected)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var thumb = new Border
        {
            Width = 44,
            Height = 34,
            CornerRadius = FocalPalette.RadiusControl,
            Background = FocalPalette.Obsidian700,
            BorderBrush = FocalPalette.HairlineDark,
            BorderThickness = new Thickness(1),
            Child = FocalIcon.Create(icon, FocalPalette.IconMd),
        };

        ((FocalIcon)thumb.Child).Foreground = FocalPalette.OnDarkMuted;
        ((FocalIcon)thumb.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((FocalIcon)thumb.Child).VerticalAlignment = VerticalAlignment.Center;

        var main = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        main.Children.Add(new TextBlock { Text = title, Style = FocalPalette.Style("Focal.BodyText") });
        main.Children.Add(new TextBlock { Text = meta, Style = FocalPalette.Style("Focal.DataCaptionText"), Foreground = FocalPalette.OnDarkMuted });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(new Button { Content = "置顶", Style = FocalPalette.Style("Focal.Button.Small"), Margin = new Thickness(0, 0, 4, 0) });
        actions.Children.Add(new Button { Content = "隐藏", Style = FocalPalette.Style("Focal.Button.Small"), Margin = new Thickness(0, 0, 4, 0) });
        actions.Children.Add(new Button { Content = "关闭", Style = FocalPalette.Style("Focal.Button.Small") });

        Grid.SetColumn(thumb, 0);
        Grid.SetColumn(main, 1);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(thumb);
        grid.Children.Add(main);
        grid.Children.Add(actions);

        return new Border
        {
            Background = selected ? FocalPalette.AccentTint : Brushes.Transparent,
            BorderBrush = selected ? FocalPalette.AccentBorder : FocalPalette.HairlineDark,
            BorderThickness = new Thickness(1),
            CornerRadius = FocalPalette.RadiusCard,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid,
        };
    }

    // ── 3. 图标网格 ───────────────────────────────────────────────────────────

    private static FrameworkElement BuildIconSheet()
    {
        var page = Page("Focal 图标集");
        page.Children.Add(SectionLabel("64 个自绘图标 · 24 网格 · 1.6 描边 · 只用 currentColor"));

        var grid = new WrapPanel();
        foreach (string name in FocalIconsCommandNames())
        {
            var cell = new StackPanel { Width = 96, Margin = new Thickness(0, 0, 8, 16) };
            var icon = FocalIcon.Create(name, FocalPalette.IconLg);
            icon.Foreground = FocalPalette.OnDark;
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            cell.Children.Add(icon);
            cell.Children.Add(new TextBlock
            {
                Text = name,
                Style = FocalPalette.Style("Focal.DataCaptionText"),
                Foreground = FocalPalette.OnDarkFaint,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            grid.Children.Add(cell);
        }

        page.Children.Add(Section(grid));
        return page;
    }

    /// <summary>与 icons.js 一一对应的 64 个键名（与 --focal-test 的检查口径一致）。</summary>
    private static string[] FocalIconsCommandNames() =>
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
            // 无控制台时忽略
        }
    }
}
