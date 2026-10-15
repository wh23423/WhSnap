using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using WhSnap.Capture;
using WhSnap.Services;

namespace WhSnap.Diagnostics;

/// <summary>
/// 命令行抓屏诊断：WhSnap.exe --capture-to &lt;文件&gt; [--backend wgc|dxgi|gdi|auto]
/// 用于在没有界面的情况下验证某个采集后端，并把诊断信息写到同名 .txt 便于反馈。
/// </summary>
internal static class CaptureCommand
{
    public static int Run(string outputPath, string? backendName)
    {
        var log = new StringBuilder();

        try
        {
            var backend = CaptureBackendNames.Parse(backendName);
            log.AppendLine($"请求后端：{CaptureBackendNames.ToName(backend)}（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）");

            var frame = ScreenCaptureFactory.Capture(backend, out var diagnostics);
            log.AppendLine($"实际后端：{frame.Backend}，耗时 {frame.Duration.TotalMilliseconds:0} ms");
            log.AppendLine($"虚拟桌面：{frame.VirtualBounds}");
            log.AppendLine($"显示器数量：{frame.Monitors.Count}");
            log.AppendLine($"受保护内容被屏蔽：{frame.ProtectedContentMasked}");

            foreach (string line in diagnostics)
            {
                log.AppendLine($"  尝试：{line}");
            }

            if (!string.IsNullOrWhiteSpace(frame.Notes))
            {
                log.AppendLine($"  说明：{frame.Notes}");
            }

            string fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame.Image));
            using (var stream = File.Create(fullPath))
            {
                encoder.Save(stream);
            }

            log.AppendLine($"已保存：{fullPath}（{new FileInfo(fullPath).Length / 1024} KB）");
            WriteSidecar(fullPath, log);
            Log.Info($"命令行抓屏成功：{fullPath}");
            return 0;
        }
        catch (Exception ex)
        {
            log.AppendLine($"失败：{ex.GetType().Name}: {ex.Message}");
            WriteSidecar(Path.GetFullPath(outputPath), log);
            Log.Error("命令行抓屏失败", ex);
            return 1;
        }
    }

    private static void WriteSidecar(string outputPath, StringBuilder log)
    {
        try
        {
            File.WriteAllText(outputPath + ".txt", log.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 诊断文件写不出去不影响退出码
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
