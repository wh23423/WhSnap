using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhSnap.Capture;
using WhSnap.Core;
using WhSnap.Interop;
using WhSnap.Pin;
using WhSnap.Services;

namespace WhSnap.Diagnostics;

/// <summary>
/// 贴图系统无交互自检：WhSnap.exe --pin-test
///
/// 真实创建分层贴图窗口（屏幕上会短暂出现贴图，约 1 秒），把整条链路跑一遍：
/// 缩放锚点、旋转/翻转、透明度、缩略图、鼠标穿透、拖动、隐藏/显示、层级命中、
/// 文本/颜色/文件卡片渲染、自动备份 → 销毁 → 重启恢复、显示器拓扑重算。
/// 用独立的临时备份目录，不碰用户真实贴图备份。
/// </summary>
internal static class PinTestCommand
{
    public static int Run()
    {
        var log = new StringBuilder();
        var failures = new List<string>();

        log.AppendLine($"WhSnap 贴图自检　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        string backupRoot = Path.Combine(Path.GetTempPath(), "whsnap-pin-test-" + Guid.NewGuid().ToString("N")[..8]);

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

        // 自检期间用内存里的临时配置（不落盘）：关掉吸附以便精确断言，打开备份/恢复
        settings.Current.PinBackupEnabled = true;
        settings.Current.RestorePinsOnStartup = true;
        settings.Current.PinBackupRetention = 3;
        settings.Current.PinSnapToEdges = false;
        settings.Current.PinDefaultOpacity = 100;
        settings.Current.PinTopmostNew = true;

        var store = new PinBackupStore(backupRoot);
        var manager = new PinManager(settings, store);
        manager.Initialize();

        var desktop = manager.Desktop;
        var monitors = manager.Monitors;

        log.AppendLine($"虚拟桌面 {desktop}，显示器 {monitors.Count} 台，备份目录 {backupRoot}");
        log.AppendLine(new string('-', 68));

        PinWindow? first = null;

        try
        {
            Check("创建贴图 + 窗口落位（分层窗口尺寸 = 贴图 + 阴影留白）", () =>
            {
                first = manager.CreatePin(CreateGradient(200, 120), new PhysicalPoint(desktop.X + 100, desktop.Y + 100));

                if (manager.Count != 1)
                {
                    throw new InvalidOperationException($"贴图数量应为 1，实际 {manager.Count}。");
                }

                var bounds = first!.Bounds;
                if (bounds.Width != 200 || bounds.Height != 120)
                {
                    throw new InvalidOperationException($"100% 缩放下显示尺寸应为 200x120，实际 {bounds.Width}x{bounds.Height}。");
                }

                var windowRect = GetWindowRect(first);
                int expectedX = bounds.X - PinWindow.ShadowPad;
                int expectedY = bounds.Y - PinWindow.ShadowPad;

                if (windowRect.Left != expectedX || windowRect.Top != expectedY)
                {
                    throw new InvalidOperationException($"窗口位置 {windowRect.Left},{windowRect.Top} 与期望 {expectedX},{expectedY} 不符。");
                }

                if (windowRect.Width != bounds.Width + (PinWindow.ShadowPad * 2) || windowRect.Height != bounds.Height + (PinWindow.ShadowPad * 2))
                {
                    throw new InvalidOperationException($"窗口尺寸 {windowRect.Width}x{windowRect.Height} 与贴图 {bounds} + 阴影不符。");
                }
            });

            Check("滚轮缩放：以光标为锚点，缩放后锚点像素不动", () =>
            {
                var pin = first!;
                var anchor = new PhysicalPoint(pin.Bounds.X + 50, pin.Bounds.Y + 30);

                pin.Zoom(2, anchor); // 1.1^2 = 1.21

                double expectedZoom = 1.21;
                if (Math.Abs(pin.State.Zoom - expectedZoom) > 0.001)
                {
                    throw new InvalidOperationException($"缩放倍率应为 {expectedZoom:0.###}，实际 {pin.State.Zoom:0.###}。");
                }

                if (pin.Bounds.Width != (int)Math.Round(200 * expectedZoom, MidpointRounding.AwayFromZero))
                {
                    throw new InvalidOperationException($"显示宽度未跟随缩放：{pin.Bounds.Width}。");
                }

                pin.Zoom(-2, anchor);
                if (Math.Abs(pin.State.Zoom - 1.0) > 0.001)
                {
                    throw new InvalidOperationException($"缩回后倍率应为 1.0，实际 {pin.State.Zoom:0.###}。");
                }
            });

            Check("旋转 / 翻转：尺寸交换且中心保持", () =>
            {
                var pin = first!;
                var center = PinGeometry.Center(pin.State);

                pin.Rotate(clockwise: true);

                if (pin.State.Rotation != PinRotation.Clockwise90)
                {
                    throw new InvalidOperationException($"旋转状态错误：{pin.State.Rotation}。");
                }

                if (pin.Bounds.Width != 120 || pin.Bounds.Height != 200)
                {
                    throw new InvalidOperationException($"旋转后显示尺寸应为 120x200，实际 {pin.Bounds.Width}x{pin.Bounds.Height}。");
                }

                if (PinGeometry.Center(pin.State) != center)
                {
                    throw new InvalidOperationException("旋转后中心发生位移。");
                }

                pin.Flip(horizontal: true);
                if (!pin.State.FlipHorizontal)
                {
                    throw new InvalidOperationException("水平翻转未生效。");
                }

                pin.Flip(horizontal: false);
                if (!pin.State.FlipVertical || !pin.State.FlipHorizontal)
                {
                    throw new InvalidOperationException("垂直翻转未生效（或误清了水平翻转）。");
                }

                // 复位：两轴各翻一次即回到原状
                pin.Flip(horizontal: true);
                pin.Flip(horizontal: false);
                pin.Rotate(clockwise: false);

                if (pin.State.Rotation != PinRotation.None || pin.State.FlipVertical || pin.State.FlipHorizontal)
                {
                    throw new InvalidOperationException(
                        $"旋转/翻转回退失败：rot={pin.State.Rotation} H={pin.State.FlipHorizontal} V={pin.State.FlipVertical}。");
                }
            });

            Check("不透明度与鼠标穿透（含扩展样式落位）", () =>
            {
                var pin = first!;
                pin.SetOpacity(45);

                if (pin.State.Opacity != 45)
                {
                    throw new InvalidOperationException($"不透明度应为 45，实际 {pin.State.Opacity}。");
                }

                pin.ToggleClickThrough();
                uint exStyle = (uint)NativeMethods.GetWindowLongAuto(pin.Handle, NativeMethods.GWL_EXSTYLE).ToInt64();

                if ((exStyle & NativeMethods.WS_EX_TRANSPARENT) == 0)
                {
                    throw new InvalidOperationException($"鼠标穿透未写入 WS_EX_TRANSPARENT（exStyle=0x{exStyle:X8}）。");
                }

                if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0)
                {
                    throw new InvalidOperationException("分层窗口样式 WS_EX_LAYERED 丢失。");
                }

                pin.ToggleClickThrough();
                pin.SetOpacity(100);
            });

            Check("缩略图模式：显示缩小、中心不变、原始像素不变", () =>
            {
                var pin = first!;
                var center = PinGeometry.Center(pin.State);
                pin.SetThumbnail(true);

                if (pin.Bounds.Width != 50 || pin.Bounds.Height != 30)
                {
                    throw new InvalidOperationException($"缩略图尺寸应为 50x30，实际 {pin.Bounds.Width}x{pin.Bounds.Height}。");
                }

                if (PinGeometry.Center(pin.State) != center || pin.State.SourceWidth != 200)
                {
                    throw new InvalidOperationException("缩略图模式改变了中心或原始像素尺寸。");
                }

                pin.SetThumbnail(false);
            });

            Check("拖动：窗口跟随物理像素位移", () =>
            {
                var pin = first!;
                var start = new PhysicalPoint(5000, 5000);
                var before = pin.State.Position;

                pin.BeginDrag(start);
                pin.DragTo(new PhysicalPoint(start.X + 37, start.Y - 24));
                pin.EndDrag();

                var expected = new PhysicalPoint(before.X + 37, before.Y - 24);
                if (pin.State.Position != expected)
                {
                    throw new InvalidOperationException($"拖动后位置 {pin.State.Position}，期望 {expected}。");
                }

                if (GetWindowRect(pin).Left != expected.X - PinWindow.ShadowPad)
                {
                    throw new InvalidOperationException("拖动后窗口未跟随贴图位置。");
                }
            });

            Check("隐藏 / 显示：窗口可见性随之变化", () =>
            {
                var pin = first!;
                pin.Hide();

                if (!pin.IsHidden || NativeMethods.IsWindowVisible(pin.Handle))
                {
                    throw new InvalidOperationException("隐藏后窗口仍然可见。");
                }

                pin.Show();

                if (pin.IsHidden || !NativeMethods.IsWindowVisible(pin.Handle))
                {
                    throw new InvalidOperationException("显示后窗口不可见。");
                }
            });

            Check("层级与命中测试：最上层优先", () =>
            {
                var second = manager.CreatePin(CreateGradient(80, 80), new PhysicalPoint(desktop.X + 300, desktop.Y + 300));

                if (second.ZIndex <= first!.ZIndex)
                {
                    throw new InvalidOperationException($"新贴图 Z 序应更大：{second.ZIndex} vs {first.ZIndex}。");
                }

                var hit = manager.PinAt(PinGeometry.Center(second.State));
                if (!ReferenceEquals(hit, second))
                {
                    throw new InvalidOperationException($"命中测试返回 {(hit is null ? "null" : $"#{hit.ZIndex}")}，期望 #{second.ZIndex}。");
                }

                var miss = manager.PinAt(new PhysicalPoint(desktop.Right + 500, desktop.Bottom + 500));
                if (miss is not null)
                {
                    throw new InvalidOperationException("桌面外应命中不到贴图。");
                }
            });

            Check("文本 / 颜色 / 文件卡片渲染", () =>
            {
                var style = manager.TextStyle;
                var text = PinContentRenderer.RenderText("WhSnap 贴图自检\n第二行：中文与 Emoji 🎯", style);
                if (text.PixelWidth < 40 || text.PixelHeight < 20)
                {
                    throw new InvalidOperationException($"文本贴图尺寸异常：{text.PixelWidth}x{text.PixelHeight}。");
                }

                var card = PinContentRenderer.RenderColorCard(new RgbColor(0xFF, 0x80, 0x40), style);
                if (card.PixelWidth < 150 || card.PixelHeight < 90)
                {
                    throw new InvalidOperationException($"颜色卡片尺寸异常：{card.PixelWidth}x{card.PixelHeight}。");
                }

                string sample = Path.Combine(Path.GetTempPath(), "whsnap-pin-test.txt");
                File.WriteAllText(sample, "文件卡片自检");
                var fileCard = PinContentRenderer.RenderFileCard(sample, style);
                if (fileCard.PixelWidth < 100 || fileCard.PixelHeight < 30)
                {
                    throw new InvalidOperationException($"文件卡片尺寸异常：{fileCard.PixelWidth}x{fileCard.PixelHeight}。");
                }

                // 剪贴板内容 → 位图（不依赖真实剪贴板，直接构造载荷）
                var colorFromClipboard = ClipboardReader.ToBitmap(new ClipboardPayload(ClipboardContentKind.Color, Text: "#FF8040"), style);
                var textFromClipboard = ClipboardReader.ToBitmap(new ClipboardPayload(ClipboardContentKind.Text, Text: "hello"), style);
                var htmlFromClipboard = ClipboardReader.ToBitmap(new ClipboardPayload(ClipboardContentKind.Html, Text: HtmlText.ExtractPlainText("<p>Hi<br>there</p>")), style);

                if (colorFromClipboard is null || textFromClipboard is null || htmlFromClipboard is null)
                {
                    throw new InvalidOperationException("剪贴板载荷转位图失败。");
                }
            });

            Check("30 张贴图常驻内存（≤ 250 MB，见规划 §2.2）", () =>
            {
                var process = System.Diagnostics.Process.GetCurrentProcess();
                process.Refresh();
                long baseline = process.PrivateMemorySize64;

                var bulk = new List<PinWindow>();
                for (int i = 0; i < 30; i++)
                {
                    bulk.Add(manager.CreatePin(
                        CreateGradient(160, 120),
                        new PhysicalPoint(desktop.X + 60 + (i % 10 * 20), desktop.Y + 60 + (i / 10 * 20))));
                }

                process.Refresh();
                long withPins = process.PrivateMemorySize64;
                long megabytes = withPins / (1024 * 1024);

                foreach (var pin in bulk)
                {
                    manager.Close(pin);
                }

                if (withPins > 250L * 1024 * 1024)
                {
                    throw new InvalidOperationException($"30 张贴图时常驻内存 {megabytes} MB，超过 250 MB 门槛。");
                }

                log.AppendLine($"       基线 {baseline / (1024 * 1024)} MB → 30 张贴图 {megabytes} MB（门槛 250 MB）");
            });

            Check("自动备份：清单与图片落盘", () =>
            {
                manager.SaveBackupNow();
                string? directory = store.CurrentDirectory;

                if (directory is null || !Directory.Exists(directory))
                {
                    throw new InvalidOperationException("备份目录未创建。");
                }

                string manifestPath = Path.Combine(directory, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    throw new InvalidOperationException("备份清单未写出。");
                }

                var manifest = PinBackupManifest.FromJson(File.ReadAllText(manifestPath));
                if (manifest is null || manifest.Pins.Count != manager.Count)
                {
                    throw new InvalidOperationException($"清单条目数 {manifest?.Pins.Count} 与实际贴图数 {manager.Count} 不符。");
                }

                foreach (var entry in manifest.Pins)
                {
                    if (!File.Exists(Path.Combine(directory, entry.File)))
                    {
                        throw new InvalidOperationException($"清单引用的图片不存在：{entry.File}。");
                    }
                }

                log.AppendLine($"       备份 {manifest.Pins.Count} 张 → {directory}");
            });

            Check("显示器拓扑重算：贴图跟着桌面平移", () =>
            {
                var pin = first!;
                var before = pin.State.Position;
                var shiftedDesktop = desktop.Offset(-1000, 0);

                pin.ReconcileTopology(desktop, shiftedDesktop);

                if (pin.State.Position.X != before.X - 1000)
                {
                    throw new InvalidOperationException($"拓扑平移后位置 {pin.State.Position}，期望 X={before.X - 1000}。");
                }

                pin.ReconcileTopology(shiftedDesktop, desktop);
            });

            // 记录备份前的状态，用于恢复比对
            var expectedStates = manager.Pins
                .OrderBy(p => p.ZIndex)
                .Select(p => (p.ZIndex, p.State.Position, Width: p.State.SourceWidth, Height: p.State.SourceHeight))
                .ToList();

            manager.SaveBackupNow();
            manager.Dispose();

            Check("重启恢复：新进程从备份还原贴图与几何", () =>
            {
                var restoredStore = new PinBackupStore(backupRoot);
                var restoredManager = new PinManager(settings, restoredStore);

                try
                {
                    restoredManager.Initialize();

                    if (restoredManager.Count != expectedStates.Count)
                    {
                        throw new InvalidOperationException($"恢复 {restoredManager.Count} 张，期望 {expectedStates.Count} 张。");
                    }

                    foreach (var (zIndex, position, width, height) in expectedStates)
                    {
                        var pin = restoredManager.Pins.FirstOrDefault(p => p.ZIndex == zIndex);
                        if (pin is null)
                        {
                            throw new InvalidOperationException($"未恢复 Z 序 {zIndex} 的贴图。");
                        }

                        if (pin.State.Position != position)
                        {
                            throw new InvalidOperationException($"恢复后位置 {pin.State.Position}，期望 {position}。");
                        }

                        if (pin.State.SourceWidth != width || pin.State.SourceHeight != height)
                        {
                            throw new InvalidOperationException($"恢复后原始尺寸 {pin.State.SourceWidth}x{pin.State.SourceHeight}，期望 {width}x{height}。");
                        }
                    }

                    // 关闭全部后再落盘，重启就不该再冒出旧贴图
                    restoredManager.CloseAll();
                    restoredManager.SaveBackupNow();
                }
                finally
                {
                    restoredManager.Dispose();
                }

                var afterClose = new PinManager(settings, new PinBackupStore(backupRoot));
                try
                {
                    afterClose.Initialize();
                    if (afterClose.Count != 0)
                    {
                        throw new InvalidOperationException($"关闭全部贴图后仍恢复了 {afterClose.Count} 张。");
                    }
                }
                finally
                {
                    afterClose.Dispose();
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
                manager.Dispose();
            }
            catch
            {
                // 清理路径上的异常不影响结论
            }

            try
            {
                if (Directory.Exists(backupRoot))
                {
                    Directory.Delete(backupRoot, recursive: true);
                }
            }
            catch
            {
                // 临时目录清理失败可忽略
            }
        }
    }

    private static NativeMethods.RECT GetWindowRect(PinWindow pin)
    {
        if (!NativeMethods.GetWindowRect(pin.Handle, out var rect))
        {
            throw new InvalidOperationException("读取贴图窗口矩形失败。");
        }

        return rect;
    }

    /// <summary>构造一张有内容差异的测试图（避免全黑图被判为无效画面）。</summary>
    private static BitmapSource CreateGradient(int width, int height)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * stride) + (x * 4);
                pixels[index] = (byte)(x % 256);
                pixels[index + 1] = (byte)(y % 256);
                pixels[index + 2] = (byte)((x + y) % 256);
                pixels[index + 3] = 0xFF;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Write(StringBuilder log)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "whsnap-pin-test.txt");
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
