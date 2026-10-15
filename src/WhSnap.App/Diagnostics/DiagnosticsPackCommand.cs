using System.IO;
using System.Text;
using WhSnap.Capture;
using WhSnap.Core;
using WhSnap.Services;

namespace WhSnap.Diagnostics;

/// <summary>
/// 命令行导出诊断包：<c>WhSnap.exe --diagnostics-pack [--out &lt;目录&gt;]</c>
///
/// 用途：程序根本起不来时（或让用户"跑一句命令把 zip 发我"时），不依赖界面也能拿到证据。
/// 与托盘/设置维护页共用 <see cref="DiagnosticsPack.Collect"/>，包结构完全一致。
/// 退出码 0 = 已生成。
/// </summary>
internal static class DiagnosticsPackCommand
{
    public static int Run(string? outputDirectory)
    {
        var log = new StringBuilder();

        try
        {
            var settings = new SettingsService();
            settings.Load();

            var monitors = MonitorEnumerator.Enumerate().ToList();
            var bindings = settings.ResolveHotkeys();

            string report = BuildReport(monitors.Count, bindings.Count, outputDirectory);
            var context = DiagnosticsPack.Collect(
                version: UpdateService.GetCurrentVersion(),
                report: report,
                monitors: monitors.Count == 0
                    ? "（未枚举到显示器）"
                    : string.Join(Environment.NewLine, monitors.Select(m => m.ToString())),
                hotkeys: BuildHotkeys(bindings),
                settingsJson: settings.ExportJson(),
                outputDirectory: string.IsNullOrWhiteSpace(outputDirectory) ? DiagnosticsPack.DefaultDirectory : outputDirectory);

            var result = DiagnosticsPack.Create(context);

            log.AppendLine($"WhSnap 诊断包导出　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            log.AppendLine(new string('-', 68));
            log.AppendLine($"诊断包：{result.Path}");
            log.AppendLine($"条目　　{result.EntryCount} 个，体积 {result.SizeBytes / 1024} KB");
            log.AppendLine($"内容　　{string.Join("、", DiagnosticsPack.ReadEntryNames(result.Path))}");
            log.AppendLine("隐私　　日志/配置/崩溃报告都已把用户目录替换为 %USERPROFILE%；不含剪贴板内容与任何凭据。");

            Write(log);
            return 0;
        }
        catch (Exception ex)
        {
            log.AppendLine($"WhSnap 诊断包导出　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            log.AppendLine($"[FAIL] {ex.GetType().Name}: {ex.Message}");
            Write(log);
            return 1;
        }
    }

    private static string BuildReport(int monitorCount, int hotkeyCount, string? outputDirectory)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"版本　　　　WhSnap {UpdateService.GetCurrentVersion()}（命令行导出）");
        builder.AppendLine($"系统　　　　{Environment.OSVersion.VersionString}（{(Environment.Is64BitProcess ? "x64" : "x86")}）");
        builder.AppendLine($"进程　　　　PID {Environment.ProcessId}");
        builder.AppendLine($"显示器　　　{monitorCount} 台");
        builder.AppendLine($"热键　　　　{hotkeyCount} 个动作有绑定记录");
        builder.AppendLine($"配置　　　　{SettingsService.ConfigFilePath}");
        builder.AppendLine($"日志　　　　{Log.CurrentLogFile}");
        builder.AppendLine($"诊断包目录　{(string.IsNullOrWhiteSpace(outputDirectory) ? DiagnosticsPack.DefaultDirectory : outputDirectory)}");
        builder.AppendLine($"崩溃证据　　{CrashHandler.DumpDirectory}（报告 {CrashHandler.ListReports().Count} 份，dump {CrashHandler.ListDumps().Count} 个）");
        builder.AppendLine("说明　　　　本次是命令行导出：不含运行中的贴图数量、剪贴板监听状态等界面状态。");
        return builder.ToString();
    }

    private static string BuildHotkeys(IReadOnlyList<HotkeyBinding> bindings)
    {
        var builder = new StringBuilder();

        foreach (var binding in bindings)
        {
            string status = !binding.Enabled
                ? "停用"
                : !binding.IsBound
                    ? "未绑定"
                    : binding.IsActive ? "生效" : "键位非法";

            builder.AppendLine(
                $"{binding.Definition.Scope,-7} {binding.Definition.Id,-24} {binding.Keys ?? "(未绑定)",-16} {binding.Definition.DisplayName}　[{status}]");
        }

        return builder.ToString();
    }

    private static void Write(StringBuilder log)
    {
        string path = Path.Combine(Path.GetTempPath(), "whsnap-diagnostics-pack.txt");

        try
        {
            File.WriteAllText(path, log.ToString(), new UTF8Encoding(false));
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
