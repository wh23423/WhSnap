using System.Text;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WhSnap.Capture;
using WhSnap.Core;
using WhSnap.Overlay;
using WhSnap.Services;

namespace WhSnap.Diagnostics;

/// <summary>
/// 遮罩层无交互自检：WhSnap.exe --overlay-test
///
/// 真实创建遮罩窗口（每显示器一个）、模拟一次跨屏框选、方向键微调、手柄缩放、
/// 放大镜取像素、确认裁剪，然后关闭窗口。用来在没有人工点击的情况下验证
/// XAML、每屏 DPI 落位、放大镜像素通路与裁剪坐标是否正确。
/// 运行期间屏幕上会短暂出现遮罩（约 1 秒），这是预期行为。
/// </summary>
internal static class OverlayTestCommand
{
    public static int Run()
    {
        var log = new StringBuilder();
        log.AppendLine($"WhSnap 遮罩自检　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        try
        {
            var frame = ScreenCaptureFactory.Capture(CaptureBackend.Auto, out var attempts);
            log.AppendLine($"冻结屏幕：后端 {frame.Backend}，虚拟桌面 {frame.VirtualBounds}，显示器 {frame.Monitors.Count} 台");
            log.AppendLine($"采集尝试：{string.Join("；", attempts)}");

            var active = MonitorEnumerator.GetMonitorAtCursor(frame.Monitors);
            var settings = new SettingsService();
            settings.Load();

            var session = new OverlaySession(frame, active, settings.Current);

            BitmapSource? cropped = null;
            PhysicalRect confirmedRect = default;
            OverlayConfirmAction confirmedAction = OverlayConfirmAction.Default;
            bool cancelled = false;

            session.Confirmed += (image, rect, action) =>
            {
                cropped = image;
                confirmedRect = rect;
                confirmedAction = action;
            };
            session.Cancelled += () => cancelled = true;

            session.Show();
            Pump();

            if (session.WindowCount != frame.Monitors.Count)
            {
                throw new InvalidOperationException($"遮罩窗口数 {session.WindowCount} 与显示器数 {frame.Monitors.Count} 不一致。");
            }

            log.AppendLine($"遮罩窗口：{session.WindowCount} 个（每显示器一个）");

            // 1) 模拟框选 200x120
            var start = new PhysicalPoint(frame.VirtualBounds.X + 40, frame.VirtualBounds.Y + 40);
            var end = new PhysicalPoint(start.X + 200, start.Y + 120);

            session.OnPointerPressed(start, 1);
            session.OnPointerMoved(new PhysicalPoint(start.X + 100, start.Y + 60));
            session.OnPointerMoved(end);
            session.OnPointerReleased();
            Pump();

            var selection = session.CurrentSelection;
            if (selection.Width != 200 || selection.Height != 120)
            {
                throw new InvalidOperationException($"框选结果不符：期望 200x120，实际 {selection.Width}x{selection.Height}。");
            }

            log.AppendLine($"框选：{selection}");

            // 2) 方向键微调（Shift 加速）
            session.HandleKey(Key.Right, ModifierKeys.None);
            session.HandleKey(Key.Down, ModifierKeys.Shift);
            Pump();

            selection = session.CurrentSelection;
            if (selection.X != end.X - 200 + 1 || selection.Y != start.Y + 10)
            {
                throw new InvalidOperationException($"微调结果不符：{selection}（期望 X={end.X - 199}, Y={start.Y + 10}）。");
            }

            log.AppendLine($"方向键微调（1px + Shift 10px）：{selection}");

            // 3) 手柄缩放：抓右下角，向右下各扩 30 / 20
            var bottomRight = new PhysicalPoint(selection.Right, selection.Bottom);
            var handle = SelectionModel.HitTest(selection, bottomRight, 6);
            if (handle != SelectionHandle.BottomRight)
            {
                throw new InvalidOperationException($"手柄命中错误：{handle}");
            }

            session.OnPointerPressed(bottomRight, 1);
            session.OnPointerMoved(new PhysicalPoint(bottomRight.X + 30, bottomRight.Y + 20));
            session.OnPointerReleased();
            Pump();

            selection = session.CurrentSelection;
            if (selection.Width != 230 || selection.Height != 140)
            {
                throw new InvalidOperationException($"手柄缩放结果不符：{selection.Width}x{selection.Height}（期望 230x140）。");
            }

            log.AppendLine($"手柄缩放：{selection}");

            // 4) 放大镜：移动光标以触发真实取像素 + 写入 WriteableBitmap
            session.OnPointerMoved(new PhysicalPoint(selection.X + 50, selection.Y + 50));
            Pump();
            log.AppendLine("放大镜取像素与网格渲染：已执行（无异常）");

            // 5) 确认裁剪
            session.Confirm();
            Pump();

            if (cancelled)
            {
                throw new InvalidOperationException("确认过程中被判定为取消。");
            }

            if (cropped is null)
            {
                throw new InvalidOperationException("确认后没有产生裁剪结果。");
            }

            if (cropped.PixelWidth != confirmedRect.Width || cropped.PixelHeight != confirmedRect.Height)
            {
                throw new InvalidOperationException($"裁剪尺寸 {cropped.PixelWidth}x{cropped.PixelHeight} 与选区 {confirmedRect} 不一致。");
            }

            log.AppendLine($"确认裁剪：{cropped.PixelWidth}x{cropped.PixelHeight}（与选区一致）");

            // 单显示器机器上无法验证多屏路径，这里用合成拓扑补上：
            // 左屏 1280x1024 @100%、右屏 1920x1024 @150%，虚拟桌面原点为负（副屏在左侧）
            RunSyntheticMultiMonitorPhase(settings.Current, log);

            log.AppendLine(new string('-', 60));
            log.AppendLine("结论：通过。");
            Write(log);
            return 0;
        }
        catch (Exception ex)
        {
            log.AppendLine(new string('-', 60));
            log.AppendLine($"结论：失败 → {ex.GetType().Name}: {ex.Message}");
            log.AppendLine(ex.ToString());
            Write(log);
            return 1;
        }
    }

    /// <summary>
    /// 合成混合 DPI 双屏拓扑，验证每显示器一个窗口、跨屏选区、每屏局部渲染与放大镜换算。
    /// 窗口不 Show（避免在单屏机器上出现莫名窗口），但 XAML 元素与渲染逻辑都是真实的。
    /// </summary>
    private static void RunSyntheticMultiMonitorPhase(AppSettings settings, StringBuilder log)
    {
        var left = new MonitorInfo(
            "synthetic-left",
            new PhysicalRect(-1280, 0, 1280, 1024),
            new PhysicalRect(-1280, 0, 1280, 1024),
            96,
            96,
            IsPrimary: false);

        var right = new MonitorInfo(
            "synthetic-right",
            new PhysicalRect(0, 0, 1920, 1024),
            new PhysicalRect(0, 0, 1920, 1024),
            144,
            144,
            IsPrimary: true);

        var bounds = VirtualDesktop.BoundsOf([left, right]);
        var image = CreateSyntheticBitmap(bounds.Width, bounds.Height);
        var frame = new CaptureFrame(image, bounds, [left, right], "synthetic", TimeSpan.Zero);

        var session = new OverlaySession(frame, right, settings);

        if (session.WindowCount != 2)
        {
            throw new InvalidOperationException($"合成双屏下遮罩窗口数应为 2，实际 {session.WindowCount}。");
        }

        // 跨屏框选：从左屏左侧拖到右屏右下
        var from = new PhysicalPoint(-1200, 100);
        var to = new PhysicalPoint(1800, 900);

        session.OnPointerPressed(from, 1);
        session.OnPointerMoved(to);
        session.OnPointerReleased();

        // 触发每屏渲染 + 放大镜（右屏 scale=1.5，左屏 scale=1.0）
        session.OnPointerMoved(to);
        session.OnPointerMoved(new PhysicalPoint(-600, 500));

        var selection = session.CurrentSelection;
        if (selection.X != -1200 || selection.Width != 3000 || selection.Height != 800)
        {
            throw new InvalidOperationException($"跨屏选区不符：{selection}（期望 -1200,100 3000x800）。");
        }

        log.AppendLine($"合成双屏（1280x1024@100% + 1920x1024@150%）：窗口 2 个，跨屏选区 {selection}，双屏渲染与放大镜换算均无异常");

        // 顺带验证"确认动作直达"的键位映射：按**配置里当前的**「直接贴图」键按下应只贴图。
        // 这里必须读绑定而不是写死 F3 —— 写死会让改键之后自检失真。
        var pinKeys = HotkeyBindings
            .Resolve(settings.Hotkeys, settings.DisabledHotkeys, HotkeyScope.Overlay)
            .First(b => b.ActionId == "overlay.pin")
            .Keys;
        if (!KeySpecFactory.TryToWpf(pinKeys, out var pinKey, out var pinModifiers))
        {
            throw new InvalidOperationException($"无法解析「直接贴图」的绑定键「{pinKeys}」。");
        }

        OverlayConfirmAction action = OverlayConfirmAction.Default;
        bool confirmed = false;
        session.Confirmed += (image, rect, kind) =>
        {
            action = kind;
            confirmed = true;
            if (image.PixelWidth != rect.Width || image.PixelHeight != rect.Height)
            {
                throw new InvalidOperationException($"合成会话裁剪尺寸 {image.PixelWidth}x{image.PixelHeight} 与选区 {rect} 不一致。");
            }
        };

        if (!session.HandleKey(pinKey, pinModifiers))
        {
            throw new InvalidOperationException($"「直接贴图」的键 {pinKeys} 未被遮罩会话识别。");
        }

        if (!confirmed || action != OverlayConfirmAction.PinOnly)
        {
            throw new InvalidOperationException($"{pinKeys} 未映射到「只贴图」：confirmed={confirmed}, action={action}。");
        }

        if (!session.HandleKey(Key.C, ModifierKeys.Control))
        {
            throw new InvalidOperationException("Ctrl+C 未被遮罩会话识别。");
        }

        log.AppendLine($"确认动作直达键位：{pinKeys} → 只贴图（已验证），Ctrl+C → 只复制（已识别，会话已结束不再重复确认）");

        // T → 进入标注：应当把选区交给宿主并关闭遮罩，但**既不算确认、也不算取消**
        var annotateSession = new OverlaySession(frame, right, settings);
        BitmapSource? handedImage = null;
        PhysicalRect handedRect = default;
        bool annotateConfirmed = false;
        bool annotateCancelled = false;

        annotateSession.Confirmed += (_, _, _) => annotateConfirmed = true;
        annotateSession.Cancelled += () => annotateCancelled = true;
        annotateSession.AnnotateRequested += (image, rect) =>
        {
            handedImage = image;
            handedRect = rect;
        };

        annotateSession.OnPointerPressed(from, 1);
        annotateSession.OnPointerMoved(to);
        annotateSession.OnPointerReleased();

        if (!annotateSession.HandleKey(Key.T, ModifierKeys.None))
        {
            throw new InvalidOperationException("T 未被遮罩会话识别为「进入标注」。");
        }

        if (annotateConfirmed || annotateCancelled)
        {
            throw new InvalidOperationException("进入标注不应被当成确认或取消。");
        }

        if (handedImage is null || handedRect.Width != 3000 || handedImage.PixelWidth != handedRect.Width)
        {
            throw new InvalidOperationException($"进入标注时交出的选区不正确：rect={handedRect}，image={handedImage?.PixelWidth}x{handedImage?.PixelHeight}。");
        }

        log.AppendLine($"进入标注（T）：交出 {handedRect} / {handedImage.PixelWidth}x{handedImage.PixelHeight}，会话未产生确认或取消");

        try
        {
            session.Cancel();
        }
        catch (Exception ex)
        {
            log.AppendLine($"合成会话关闭提示：{ex.GetType().Name}（窗口未显示，可忽略）");
        }
    }

    private static BitmapSource CreateSyntheticBitmap(int width, int height)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int index = row + (x * 4);
                pixels[index] = (byte)(x % 256);
                pixels[index + 1] = (byte)(y % 256);
                pixels[index + 2] = (byte)((x + y) % 256);
                pixels[index + 3] = 0xFF;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>把 Dispatcher 队列里优先级不低于 Background 的项目都处理掉（含布局与渲染）。</summary>
    private static void Pump()
    {
        for (int i = 0; i < 6; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
    }

    private static void Write(StringBuilder log)
    {
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
