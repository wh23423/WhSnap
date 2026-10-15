using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhSnap.Capture;
using WhSnap.Core;
using WhSnap.Interop;
using WhSnap.Services;

namespace WhSnap.Diagnostics;

/// <summary>
/// 无界面自检（WhSnap.exe --selftest）。
/// 用来在没有人工点击的情况下验证：抓屏、显示器拓扑、热键注册与冲突判定、
/// 剪贴板、配置读写、开机自启读取。退出码 0 = 全部通过。
/// </summary>
internal static class SelfTestRunner
{
    public static int Run()
    {
        TryAttachConsole();

        var report = new StringBuilder();
        var failures = new List<string>();

        report.AppendLine($"WhSnap 自检　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"版本 0.1.0-preview.1　进程 {(Environment.Is64BitProcess ? "x64" : "x86")}");
        report.AppendLine(new string('-', 68));

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

        Check("屏幕采集 + 自动降级链（auto）", () =>
        {
            var frame = ScreenCaptureFactory.Capture(CaptureBackend.Auto, out var attempts);

            if (frame.Image.PixelWidth != frame.VirtualBounds.Width || frame.Image.PixelHeight != frame.VirtualBounds.Height)
            {
                throw new InvalidOperationException($"位图尺寸 {frame.Image.PixelWidth}x{frame.Image.PixelHeight} 与虚拟桌面 {frame.VirtualBounds} 不一致。");
            }

            string path = Path.Combine(Path.GetTempPath(), "whsnap-selftest-capture.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame.Image));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            long bytes = new FileInfo(path).Length;
            if (bytes < 1000)
            {
                throw new InvalidOperationException($"抓图落盘仅 {bytes} 字节，疑似黑屏/失败。");
            }

            if (!FrameSanity.LooksLikeRealContent(frame.Image, out string sanity))
            {
                throw new InvalidOperationException($"画面疑似黑帧（{sanity}）。");
            }

            return $"实际后端 {frame.Backend}，{frame.VirtualBounds}，{frame.Duration.TotalMilliseconds:0} ms，{sanity}，PNG {bytes / 1024} KB；尝试链 {string.Join(" → ", attempts)}";
        });

        Check("显示器拓扑与 DPI", () =>
        {
            var monitors = MonitorEnumerator.Enumerate();
            if (monitors.Count == 0)
            {
                throw new InvalidOperationException("未枚举到任何显示器。");
            }

            return string.Join("　|　", monitors.Select(m => m.ToString()));
        });

        Check("全局热键注册与冲突检测", () =>
        {
            var spec = new HotkeySpec(HotkeySpec.ModControl | HotkeySpec.ModAlt, 0x78); // Ctrl+Alt+F9
            using var service = new HotkeyService();

            var first = service.Register(spec);
            if (!first.IsRegistered)
            {
                throw new InvalidOperationException($"首次注册失败：{first.Message}");
            }

            // 第二个服务实例抢同一个热键，必须被判为"已占用"
            using var second = new HotkeyService();
            var conflict = second.Register(spec);
            if (conflict.Status != HotkeyStatus.ConflictWithSystem)
            {
                throw new InvalidOperationException($"重复注册未被判为冲突，实际状态：{conflict.Status}（{conflict.Message}）");
            }

            service.Unregister();
            return $"注册成功，重复注册被判为冲突（Win32 {conflict.Win32Error}）";
        });

        Check("热键文本解析", () =>
        {
            if (!HotkeySpec.TryParse("Ctrl+Shift+A", out var spec, out string? error))
            {
                throw new InvalidOperationException($"解析失败：{error}");
            }

            if (!HotkeySpec.TryParse("F1", out var f1, out _) || f1.VirtualKey != 0x70)
            {
                throw new InvalidOperationException("F1 解析结果不正确。");
            }

            if (HotkeySpec.TryParse("NoSuchKey", out _, out _))
            {
                throw new InvalidOperationException("非法键名未被拒绝。");
            }

            if (!HotkeySpec.TryParse("PrtScn", out var prtScn, out _) || prtScn.VirtualKey != 0x2C)
            {
                throw new InvalidOperationException("PrtScn 解析结果不正确。");
            }

            return $"Ctrl+Shift+A → {spec}；F1 → {f1}；PrtScn → {prtScn}";
        });

        Check("剪贴板写入与读回", () =>
        {
            const int size = 8;
            var pixels = new byte[size * size * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 0x40;      // B
                pixels[i + 1] = 0xA0;  // G
                pixels[i + 2] = 0xFF;  // R
                pixels[i + 3] = 0xFF;  // A
            }

            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
            bitmap.Freeze();

            if (!ClipboardService.TryCopy(bitmap, out string error))
            {
                throw new InvalidOperationException($"写入剪贴板失败：{error}");
            }

            var readBack = System.Windows.Clipboard.GetImage();
            if (readBack is null)
            {
                throw new InvalidOperationException("剪贴板读回为空。");
            }

            return $"写入 {size}x{size} 成功，读回 {readBack.PixelWidth}x{readBack.PixelHeight}";
        });

        Check("配置读写", () =>
        {
            var service = new SettingsService();
            service.Load();
            var original = service.Current.Hotkey;
            service.Current.Hotkey = original;
            service.Save();
            if (!File.Exists(SettingsService.ConfigFilePath))
            {
                throw new InvalidOperationException("配置文件未生成。");
            }

            return $"{SettingsService.ConfigFilePath}（热键 {original}）";
        });

        Check("开机自启状态读取", () =>
        {
            bool enabled = AutoStartService.IsEnabled();
            bool consistent = AutoStartService.IsConsistent();
            return $"已启用={enabled}，注册表路径一致={consistent}（自启项由安装包默认写入，程序仅在缺失时补写）";
        });

        // 逐后端单独验证：某个后端"这台机器不支持"记为 SKIP，真正出错才算 FAIL
        foreach (var backend in new[] { CaptureBackend.Dxgi, CaptureBackend.Wgc, CaptureBackend.Gdi })
        {
            string backendName = CaptureBackendNames.ToName(backend);

            try
            {
                var frame = ScreenCaptureFactory.Create(backend).Capture();

                if (frame.Image.PixelWidth != frame.VirtualBounds.Width || frame.Image.PixelHeight != frame.VirtualBounds.Height)
                {
                    throw new InvalidOperationException($"位图尺寸 {frame.Image.PixelWidth}x{frame.Image.PixelHeight} 与虚拟桌面 {frame.VirtualBounds} 不一致。");
                }

                if (!FrameSanity.LooksLikeRealContent(frame.Image, out string sanity))
                {
                    throw new InvalidOperationException($"画面疑似黑帧（{sanity}）。");
                }

                report.AppendLine($"[PASS] 采集后端 {backendName}：{frame.VirtualBounds}，{frame.Duration.TotalMilliseconds:0} ms，{sanity}");

                if (!string.IsNullOrWhiteSpace(frame.Notes))
                {
                    report.AppendLine($"       说明：{frame.Notes}");
                }
            }
            catch (CaptureNotSupportedException ex)
            {
                report.AppendLine($"[SKIP] 采集后端 {backendName}：{ex.Message}");
            }
            catch (Exception ex)
            {
                failures.Add($"采集后端 {backendName}");
                report.AppendLine($"[FAIL] 采集后端 {backendName}：{ex.GetType().Name}: {ex.Message}");
            }
        }

        report.AppendLine(new string('-', 68));
        report.AppendLine(failures.Count == 0
            ? "结论：全部通过。"
            : $"结论：{failures.Count} 项失败 → {string.Join("、", failures)}");

        string reportPath = Path.Combine(Path.GetTempPath(), "whsnap-selftest.txt");
        try
        {
            File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 报告落盘失败不应改变退出码语义
        }

        try
        {
            Console.WriteLine(report.ToString());
            Console.WriteLine($"报告文件：{reportPath}");
        }
        catch
        {
            // 无控制台时忽略
        }

        return failures.Count == 0 ? 0 : 1;
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

    /// <summary>页面内容统计已抽到 <see cref="FrameSanity"/>，自检与采集后端共用同一判据。</summary>
}
