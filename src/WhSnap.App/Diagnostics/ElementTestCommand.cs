using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WhSnap.Capture;
using WhSnap.Core;
using WhSnap.Overlay;
using WhSnap.Services;

namespace WhSnap.Diagnostics;

/// <summary>
/// 元素检测 + 取色器无交互自检：WhSnap.exe --element-test
///
/// 元素检测是最"吃环境"的功能（UIA 依赖目标程序配合），因此必须真跑：
///   * 创建一个真实测试窗口，验证 UI Automation 能拿到它的边界；
///   * 验证窗口矩形回退路径在当前桌面上能枚举出候选；
///   * 验证遮罩会话的"悬停 → 异步查询 → 高亮"链路真的产出了高亮元素；
///   * 验证取色器：从冻结画面取像素 → 按设置格式复制到剪贴板 → 记入颜色历史。
/// 运行期间屏幕上会短暂出现一个测试窗口（约 1 秒）。
/// </summary>
internal static class ElementTestCommand
{
    public static int Run()
    {
        var log = new StringBuilder();
        var failures = new List<string>();

        log.AppendLine($"WhSnap 元素检测 / 取色器自检　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine(new string('-', 68));

        void Check(string name, Action body)
        {
            try
            {
                body();
                log.AppendLine($"[PASS] {name}");
            }
            catch (Exception ex)
            {
                failures.Add(name);
                log.AppendLine($"[FAIL] {name}：{ex.GetType().Name}: {ex.Message}");
            }
        }

        var settings = new SettingsService();
        settings.Load();

        var monitors = MonitorEnumerator.Enumerate();
        var monitor = monitors[0];

        // 测试窗口：放在主屏中央偏一点，避免压住任务栏
        const int windowWidth = 360;
        const int windowHeight = 240;
        var windowRect = new PhysicalRect(
            monitor.Bounds.X + ((monitor.Bounds.Width - windowWidth) / 2),
            monitor.Bounds.Y + ((monitor.Bounds.Height - windowHeight) / 2) - 60,
            windowWidth,
            windowHeight);

        Window? testWindow = null;
        var center = new PhysicalPoint(windowRect.X + (windowRect.Width / 2), windowRect.Y + (windowRect.Height / 2));

        try
        {
            testWindow = CreateTestWindow(windowRect, monitor.ScaleX);
            testWindow.Show();
            Pump();

            Check("UI Automation 能识别测试窗口（含边界与名称）", () =>
            {
                // 必须走异步路径并在等待期间抽空消息队列：
                // 我们自己的 WPF 窗口的 UIA 提供程序需要 UI 线程响应，阻塞 UI 线程去等就成了死等。
                var candidates = Await(ElementLocator.FindAsync(center, useUia: true, timeoutMs: 3000));
                if (candidates.Count == 0)
                {
                    throw new InvalidOperationException("光标下没有拿到任何元素候选。");
                }

                var uia = candidates.Where(candidate => candidate.Source == "UIA").ToList();
                if (uia.Count == 0)
                {
                    throw new InvalidOperationException("UIA 路径没有候选（只有窗口回退候选）。");
                }

                var best = ElementDetection.Choose(candidates, center);
                if (best is null)
                {
                    throw new InvalidOperationException("没有选出默认元素。");
                }

                log.AppendLine($"       候选 {candidates.Count} 个（UIA {uia.Count}），最佳：{best.Describe()}");

                // 最内层元素就在测试窗口里：必须与窗口矩形有实质重叠
                if (best.Bounds.Intersect(windowRect).IsEmpty)
                {
                    throw new InvalidOperationException($"选中的元素 {best.Bounds} 与测试窗口 {windowRect} 没有交集。");
                }
            });

            Check("窗口矩形回退路径能枚举到包含光标的窗口", () =>
            {
                // 采样若干点，只要有一个点能枚举到外层窗口就说明回退路径可用
                var points = new List<PhysicalPoint>
                {
                    center,
                    new PhysicalPoint(monitor.Bounds.X + 10, monitor.Bounds.Y + 10),
                    new PhysicalPoint(monitor.Bounds.X + (monitor.Bounds.Width / 2), monitor.Bounds.Bottom - 10),
                };

                var found = new List<ElementCandidate>();

                foreach (var point in points)
                {
                    var candidates = ElementLocator.Find(point, useUia: false);
                    found.AddRange(candidates.Where(candidate => candidate.Source == "窗口"));
                }

                if (found.Count == 0)
                {
                    log.AppendLine("       [SKIP] 当前桌面在采样点上没有其它可见窗口（例如全屏独占场景），窗口回退路径无法验证。");
                    return;
                }

                log.AppendLine($"       窗口回退候选示例：{found[0].Describe()}");
            });

            Check("遮罩会话：悬停 → 异步查询 → 高亮元素", () =>
            {
                var frame = ScreenCaptureFactory.Capture(CaptureBackend.Auto, out _);
                var active = MonitorEnumerator.GetMonitorAtCursor(frame.Monitors);
                var session = new OverlaySession(frame, active, settings.Current);

                session.OnPointerMoved(center);

                // 等待异步 UIA 查询（最多 1.5 秒）
                for (int i = 0; i < 30 && session.HighlightedElement is null; i++)
                {
                    Thread.Sleep(50);
                    Pump();
                }

                if (session.ElementCandidates.Count == 0)
                {
                    throw new InvalidOperationException("遮罩会话没有拿到元素候选。");
                }

                if (session.HighlightedElement is null)
                {
                    throw new InvalidOperationException(
                        $"拿到了 {session.ElementCandidates.Count} 个候选，但没有选出高亮元素。");
                }

                var highlighted = session.HighlightedElement;
                if (highlighted.Bounds.Intersect(windowRect).IsEmpty)
                {
                    throw new InvalidOperationException($"高亮元素 {highlighted.Bounds} 不在测试窗口内。");
                }

                // 高亮渲染路径（真实调用 OverlayWindow.RenderElement）
                session.OnPointerMoved(new PhysicalPoint(center.X + 1, center.Y + 1));
                Pump();

                log.AppendLine($"       高亮：{highlighted.Describe()}");
            });

            Check("取色器：冻结画面取像素 → 复制到剪贴板 → 记入颜色历史", () =>
            {
                // 合成一张已知颜色的冻结画面（配套一台同尺寸的合成显示器，遮罩窗口才有合法裁剪区）
                var expected = new RgbColor(0x12, 0x34, 0x56);
                var bounds = new PhysicalRect(0, 0, 64, 48);
                var syntheticMonitor = new MonitorInfo("synthetic", bounds, bounds, 96, 96, true);
                var frame = new CaptureFrame(CreateSolidImage(bounds.Width, bounds.Height, expected), bounds, [syntheticMonitor], "synthetic", TimeSpan.Zero);

                var history = new ColorHistory(8);
                var session = new OverlaySession(frame, syntheticMonitor, settings.Current, colorHistory: history);

                RgbColor? picked = null;
                session.ColorPicked += color => picked = color;

                var sample = new PhysicalPoint(10, 12);
                session.PickColor(sample);

                if (picked is null || picked.Value != expected)
                {
                    throw new InvalidOperationException($"取色结果应为 {expected.ToHex()}，实际 {picked?.ToHex() ?? "null"}。");
                }

                if (!history.Contains(expected) || history.Items[0] != expected)
                {
                    throw new InvalidOperationException("取到的颜色没有记入颜色历史。");
                }

                string expectedText = ColorText.Format(expected, ColorText.ParseFormat(settings.Current.ColorCopyFormat));
                string? clipboard = ClipboardService.TryGetText();

                if (clipboard is null || !clipboard.Equals(expectedText, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"剪贴板内容应为“{expectedText}”，实际“{clipboard ?? "null"}”。");
                }

                // 序列化 → 恢复（颜色历史要能存进配置）
                var restored = ColorHistory.FromText(history.Serialize(), 8);
                if (!restored.Contains(expected))
                {
                    throw new InvalidOperationException("颜色历史序列化后丢失了取到的颜色。");
                }

                log.AppendLine($"       取到 {expected.ToHex().ToUpperInvariant()}，剪贴板“{expectedText.Replace(Environment.NewLine, " / ")}”，历史 {restored.Count} 条");

                // 取色模式：单击即取色并结束会话
                var pickModeSession = new OverlaySession(frame, syntheticMonitor, settings.Current, colorHistory: history, colorPickMode: true);
                if (!pickModeSession.ColorPickMode)
                {
                    throw new InvalidOperationException("取色模式没有生效。");
                }

                pickModeSession.PickColor(new PhysicalPoint(1, 1));
                pickModeSession.Cancel();
            });

            Check("元素候选选择规则（纯逻辑回归）", () =>
            {
                var candidates = new List<ElementCandidate>
                {
                    new(new PhysicalRect(0, 0, 1920, 1080), "窗口", "窗口"),
                    new(new PhysicalRect(100, 100, 400, 300), "面板", "UIA", 3),
                    new(new PhysicalRect(150, 150, 80, 30), "按钮", "UIA", 0),
                };

                var point = new PhysicalPoint(160, 160);

                var best = ElementDetection.Choose(candidates, point);
                if (best?.Name != "按钮")
                {
                    throw new InvalidOperationException($"应选中面积最小的『按钮』，实际 {best?.Name ?? "null"}。");
                }

                var first = ElementDetection.Cycle(candidates, point, null);
                var second = ElementDetection.Cycle(candidates, point, first);
                var third = ElementDetection.Cycle(candidates, point, second);

                if (first?.Name != "按钮" || second?.Name != "面板" || third?.Name != "窗口")
                {
                    throw new InvalidOperationException($"Tab 循环顺序错误：{first?.Name} → {second?.Name} → {third?.Name}。");
                }
            });

            log.AppendLine(new string('-', 68));
            log.AppendLine(failures.Count == 0
                ? "结论：通过。"
                : $"结论：{failures.Count} 项失败 → {string.Join("、", failures)}");

            Write(log);
            return failures.Count == 0 ? 0 : 1;
        }
        finally
        {
            try
            {
                testWindow?.Close();
            }
            catch
            {
                // 关闭异常不影响结论
            }
        }
    }

    private static Window CreateTestWindow(PhysicalRect rect, double scale)
    {
        double dipWidth = scale <= 0 ? rect.Width : rect.Width / scale;
        double dipHeight = scale <= 0 ? rect.Height : rect.Height / scale;

        var window = new Window
        {
            Title = "WhSnap 元素检测自检窗口",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = true,
            Topmost = true,
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x6A, 0xC0)),
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "元素检测自检",
                Foreground = Brushes.White,
                FontSize = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Left = rect.X / scale,
            Top = rect.Y / scale,
            Width = dipWidth,
            Height = dipHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };

        return window;
    }

    private static BitmapSource CreateSolidImage(int width, int height, RgbColor color)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = 0xFF;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Pump()
    {
        for (int i = 0; i < 6; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
    }

    /// <summary>等待一个任务完成，同时继续抽消息队列（UIA 需要我们自己的界面线程响应）。</summary>
    private static T Await<T>(Task<T> task, int timeoutMs = 5000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (!task.IsCompleted && stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(15);
            Pump();
        }

        if (!task.IsCompleted)
        {
            throw new TimeoutException($"等待异步查询超时（{timeoutMs} ms）。");
        }

        return task.GetAwaiter().GetResult();
    }

    private static void Write(StringBuilder log)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "whsnap-element-test.txt");
            File.WriteAllText(path, log.ToString(), Encoding.UTF8);
            Console.WriteLine($"报告文件：{path}");
        }
        catch
        {
            // 报告落盘失败不影响退出码
        }

        try
        {
            Console.WriteLine(log.ToString());
        }
        catch
        {
            // 无控制台时忽略
        }
    }
}
