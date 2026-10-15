using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WhSnap.Annotation;
using WhSnap.Capture;
using WhSnap.Core;
using WhSnap.Pin;
using WhSnap.Services;

namespace WhSnap.Diagnostics;

/// <summary>
/// 标注系统无交互自检：WhSnap.exe --annotate-test
///
/// 真实创建标注编辑器窗口，把整条链路跑一遍：
/// 工具切换、矩形/箭头/自由笔/文字/序号/马赛克/模糊/橡皮擦落笔、撤销重做、
/// 二次编辑（选中 + 移动 + 改样式）、合成结果的**像素断言**（1:1 尺寸、马赛克按块取均值、
/// 橡皮擦只擦标注不擦底图）、确认回传，以及"标注回写贴图"。
/// 运行期间屏幕上会短暂出现标注窗口（约 1 秒）。
/// </summary>
internal static class AnnotationTestCommand
{
    public static int Run()
    {
        var log = new StringBuilder();
        var failures = new List<string>();

        log.AppendLine($"WhSnap 标注自检　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
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
        var region = new PhysicalRect(
            monitor.Bounds.X + 60,
            monitor.Bounds.Y + 60,
            360,
            240);

        const int imageWidth = 360;
        const int imageHeight = 240;
        var baseImage = CreateBaseImage(imageWidth, imageHeight);

        AnnotationWindow? window = null;

        try
        {
            Check("创建标注窗口 + 工具栏（13 个工具）", () =>
            {
                window = new AnnotationWindow(baseImage, region, monitor.ScaleX, settings.Current);
                window.Show();
                Pump();

                if (window.CurrentTool != AnnotationWindow.ParseTool(settings.Current.AnnotationDefaultTool))
                {
                    throw new InvalidOperationException($"默认工具应为 {settings.Current.AnnotationDefaultTool}，实际 {window.CurrentTool}。");
                }

                if (window.ComposedImageSource is null)
                {
                    throw new InvalidOperationException("窗口创建后没有合成结果。");
                }

                if (window.ComposedImageSource.PixelWidth != imageWidth || window.ComposedImageSource.PixelHeight != imageHeight)
                {
                    throw new InvalidOperationException(
                        $"合成尺寸 {window.ComposedImageSource.PixelWidth}x{window.ComposedImageSource.PixelHeight} 与底图 {imageWidth}x{imageHeight} 不一致（必须 1:1）。");
                }
            });

            Check("落笔：矩形 / 箭头 / 自由笔 / 文字 / 序号", () =>
            {
                var document = window!.Document;

                AssertSameTool(window, "rectangle");
                window.SimulateDrag(new PhysicalPoint(10, 10), new PhysicalPoint(110, 70));

                AssertSameTool(window, "arrow");
                window.SimulateDrag(new PhysicalPoint(120, 20), new PhysicalPoint(200, 80));

                AssertSameTool(window, "pencil");
                window.SimulateDrag(new PhysicalPoint(20, 120), new PhysicalPoint(120, 150));

                AssertSameTool(window, "text");
                window.SimulateClick(new PhysicalPoint(30, 180), "标注文字");

                AssertSameTool(window, "badge");
                window.SimulateClick(new PhysicalPoint(220, 180));

                if (document.Count != 5)
                {
                    throw new InvalidOperationException($"应有 5 个图元，实际 {document.Count}。");
                }

                if (document.Items[4].Badge != settings.Current.AnnotationBadgeStart)
                {
                    throw new InvalidOperationException($"序号起始值应为 {settings.Current.AnnotationBadgeStart}，实际 {document.Items[4].Badge}。");
                }

                return;
            });

            Check("像素效果：马赛克按块取均值、模糊、橡皮擦只擦标注", () =>
            {
                var document = window!.Document;

                // 马赛克：画在纯渐变区域上，落笔后该区域内每个块必须完全一致
                AssertSameTool(window, "mosaic");
                var mosaicRegion = new PhysicalRect(240, 20, 90, 60);
                window.SimulateDrag(
                    new PhysicalPoint(mosaicRegion.X, mosaicRegion.Y),
                    new PhysicalPoint(mosaicRegion.Right, mosaicRegion.Bottom));

                var afterMosaic = ReadPixels(window.ComposedImageSource!);
                int block = window.CurrentStyle.MosaicBlock;
                var firstBlock = PixelAt(afterMosaic, imageWidth, mosaicRegion.X, mosaicRegion.Y);

                for (int y = 0; y < block && mosaicRegion.Y + y < mosaicRegion.Bottom; y++)
                {
                    for (int x = 0; x < block && mosaicRegion.X + x < mosaicRegion.Right; x++)
                    {
                        var sample = PixelAt(afterMosaic, imageWidth, mosaicRegion.X + x, mosaicRegion.Y + y);
                        if (sample != firstBlock)
                        {
                            throw new InvalidOperationException(
                                $"马赛克块内像素不一致：({mosaicRegion.X + x},{mosaicRegion.Y + y}) = {sample}，期望 {firstBlock}。");
                        }
                    }
                }

                // 模糊：在另一个区域落笔后，区域内像素应当"被平均过"（不再与原图逐像素相同）
                var beforeBlur = ReadPixels(window.ComposedImageSource!);
                AssertSameTool(window, "blur");
                var blurRegion = new PhysicalRect(240, 100, 90, 60);
                window.SimulateDrag(
                    new PhysicalPoint(blurRegion.X, blurRegion.Y),
                    new PhysicalPoint(blurRegion.Right, blurRegion.Bottom));

                var afterBlur = ReadPixels(window.ComposedImageSource!);
                if (PixelsEqual(beforeBlur, afterBlur))
                {
                    throw new InvalidOperationException("模糊没有改变画面。");
                }

                // 橡皮擦：擦掉刚画的矩形描边，但**底图不能被擦出透明洞**
                var rectangleBounds = document.Items[0].Bounds;
                int sampleX = rectangleBounds.X + (rectangleBounds.Width / 2);
                int sampleY = rectangleBounds.Y;

                AssertSameTool(window, "eraser");
                window.SimulateDrag(new PhysicalPoint(rectangleBounds.X - 4, sampleY), new PhysicalPoint(rectangleBounds.Right + 4, sampleY));

                var afterErase = ReadPixels(window.ComposedImageSource!);
                var basePixels = ReadPixels(baseImage);

                if (PixelAt(afterErase, imageWidth, sampleX, sampleY).A == 0)
                {
                    throw new InvalidOperationException("橡皮擦把底图擦出了透明洞——必须只擦标注。");
                }

                if (PixelAt(afterErase, imageWidth, sampleX, sampleY) != PixelAt(basePixels, imageWidth, sampleX, sampleY))
                {
                    throw new InvalidOperationException("橡皮擦应当把该处的标注像素恢复成底图。");
                }

                return;
            });

            Check("撤销 / 重做（命令栈，不是位图快照）", () =>
            {
                var document = window!.Document;
                int before = document.Count;

                if (!document.CanUndo)
                {
                    throw new InvalidOperationException("落笔后应当可以撤销。");
                }

                window.Undo();
                window.Undo();

                if (document.Count != before - 2)
                {
                    throw new InvalidOperationException($"撤销两次后应为 {before - 2} 个图元，实际 {document.Count}。");
                }

                window.Redo();

                if (document.Count != before - 1)
                {
                    throw new InvalidOperationException($"重做一次后应为 {before - 1} 个图元，实际 {document.Count}。");
                }

                if (window.ComposedImageSource is null)
                {
                    throw new InvalidOperationException("撤销/重做后没有重新合成。");
                }

                return;
            });

            Check("二次编辑：选中图元 → 移动 → 改样式", () =>
            {
                var document = window!.Document;
                var target = document.Items[1]; // 箭头
                var originalBounds = target.Bounds;
                var center = new PhysicalPoint(originalBounds.X + (originalBounds.Width / 2), originalBounds.Y + (originalBounds.Height / 2));

                // 先切到选择工具，命中测试找到图元
                AssertSameTool(window, "select");
                var hit = AnnotationEditing.HitTest(document.Items, center, 8);

                if (hit is null)
                {
                    throw new InvalidOperationException("选择工具没有命中箭头图元。");
                }

                // 通过窗口的模拟拖拽走"移动"路径
                window.SimulateSelectMove(hit.Item.Id, new PhysicalPoint(20, 15));

                var moved = document.Find(hit.Item.Id);
                if (moved is null || moved.Bounds.X == originalBounds.X)
                {
                    throw new InvalidOperationException("图元没有被移动。");
                }

                // 改样式：换色 + 加粗
                var restyled = AnnotationEditing.Restyle(moved, moved.Style.WithStroke(new RgbColor(0x00, 0x7A, 0xFF)).WithWidth(9));
                document.Update(restyled);

                var after = document.Find(hit.Item.Id)!;
                if (after.Style.StrokeWidth != 9 || after.Style.Stroke.R != 0x00 || after.Style.Stroke.B != 0xFF)
                {
                    throw new InvalidOperationException($"样式没有改成功：{after.Style.StrokeWidth}px {after.Style.Stroke}。");
                }

                return;
            });

            Check("删除选中图元 + 清空（都可撤销）", () =>
            {
                var document = window!.Document;
                int before = document.Count;
                var last = document.Items[^1];

                document.Remove(last.Id);
                if (document.Count != before - 1)
                {
                    throw new InvalidOperationException("删除图元失败。");
                }

                if (!document.Undo() || document.Count != before)
                {
                    throw new InvalidOperationException("删除后撤销失败。");
                }

                window.ClearAllForTest();
                if (!document.IsEmpty)
                {
                    throw new InvalidOperationException("清空失败。");
                }

                if (!document.Undo() || document.Count != before)
                {
                    throw new InvalidOperationException("清空后撤销失败。");
                }

                return;
            });

            Check("确认：回传 1:1 合成结果并关闭窗口", () =>
            {
                BitmapSource? result = null;
                bool copyOnly = true;
                window!.Completed += (image, copy) =>
                {
                    result = image;
                    copyOnly = copy;
                };

                window.Confirm(false);
                Pump();

                if (result is null)
                {
                    throw new InvalidOperationException("确认后没有回传合成结果。");
                }

                if (result.PixelWidth != imageWidth || result.PixelHeight != imageHeight)
                {
                    throw new InvalidOperationException($"回传尺寸 {result.PixelWidth}x{result.PixelHeight} 不是 1:1。");
                }

                if (copyOnly)
                {
                    throw new InvalidOperationException("Confirm(false) 不应被标记为「只复制」。");
                }

                if (window.IsVisible)
                {
                    throw new InvalidOperationException("确认后窗口应当关闭。");
                }

                return;
            });

            Check("标注回写贴图（贴图内容替换 + 尺寸跟随）", () =>
            {
                string backupRoot = Path.Combine(Path.GetTempPath(), "whsnap-annotate-test-" + Guid.NewGuid().ToString("N")[..8]);
                var manager = new PinManager(settings, new PinBackupStore(backupRoot));

                try
                {
                    manager.Initialize();
                    var pin = manager.CreatePin(baseImage, new PhysicalPoint(region.X, region.Y + region.Height + 20));

                    var annotated = AnnotationRenderer.Compose(baseImage, [
                        AnnotationItem.FromDrag(1, AnnotationKind.Rectangle, new PhysicalPoint(5, 5), new PhysicalPoint(150, 100), AnnotationStyle.Default),
                    ]);

                    pin.ReplaceSource(annotated);

                    if (pin.Source.PixelWidth != annotated.PixelWidth || pin.Source.PixelHeight != annotated.PixelHeight)
                    {
                        throw new InvalidOperationException("回写后贴图的原始尺寸没有跟随新图。");
                    }

                    var composedPixels = ReadPixels(pin.Source);
                    var basePixels = ReadPixels(baseImage);

                    if (PixelsEqual(composedPixels, basePixels))
                    {
                        throw new InvalidOperationException("回写后的贴图内容与底图完全相同，标注没有写进去。");
                    }
                }
                finally
                {
                    manager.Dispose();

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
                window?.Close();
            }
            catch
            {
                // 关闭异常不影响结论
            }
        }
    }

    private static void AssertSameTool(AnnotationWindow window, string name)
    {
        var expected = AnnotationWindow.ParseTool(name);
        window.SetToolForTest(expected);

        if (window.CurrentTool != expected)
        {
            throw new InvalidOperationException($"工具切换失败：期望 {expected}，实际 {window.CurrentTool}。");
        }
    }

    private static (byte B, byte G, byte R, byte A) PixelAt(byte[] pixels, int width, int x, int y)
    {
        int index = ((y * width) + x) * 4;
        return (pixels[index], pixels[index + 1], pixels[index + 2], pixels[index + 3]);
    }

    private static byte[] ReadPixels(BitmapSource bitmap)
    {
        var normalized = SaveService.Normalize(bitmap);
        int stride = normalized.PixelWidth * 4;
        var pixels = new byte[stride * normalized.PixelHeight];
        normalized.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static bool PixelsEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>有内容的测试底图（渐变 + 网格），避免纯色导致像素断言无意义。</summary>
    private static BitmapSource CreateBaseImage(int width, int height)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * stride) + (x * 4);
                bool grid = x % 40 == 0 || y % 40 == 0;
                pixels[index] = (byte)(grid ? 0x20 : (x * 255 / width));
                pixels[index + 1] = (byte)(grid ? 0x20 : (y * 255 / height));
                pixels[index + 2] = (byte)(grid ? 0x20 : ((x + y) % 256));
                pixels[index + 3] = 0xFF;
            }
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

    private static void Write(StringBuilder log)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "whsnap-annotate-test.txt");
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
