using System.IO;
using System.IO.Compression;
using System.Text;

namespace WhSnap.Services;

/// <summary>打包诊断包所需的全部输入。宿主从运行状态收集；自检用临时目录构造同样的形状。</summary>
internal sealed record DiagnosticsPackContext
{
    /// <summary>zip 落到哪个目录（不存在会自动创建）。</summary>
    public required string OutputDirectory { get; init; }

    public required string Version { get; init; }

    /// <summary>report.txt：版本 / 进程 / 配置与日志路径等运行状态。</summary>
    public required string Report { get; init; }

    /// <summary>monitors.txt：显示器拓扑明细（物理矩形 / DPI / 缩放 / 主屏）。</summary>
    public required string Monitors { get; init; }

    /// <summary>hotkeys.txt：当前生效的热键表。</summary>
    public required string Hotkeys { get; init; }

    /// <summary>settings.json：当前配置原文（打包时脱敏）。</summary>
    public required string SettingsJson { get; init; }

    /// <summary>日志目录（取其中最新的一份日志，截断后带进包）。</summary>
    public string? LogDirectory { get; init; }

    /// <summary>性能埋点报告（M6 第 7 项落地前为 null，包里写占位说明）。</summary>
    public string? PerformanceReport { get; init; }

    /// <summary>现有崩溃文本报告（新的在前）：完整带进包。</summary>
    public IReadOnlyList<string> CrashReports { get; init; } = [];

    /// <summary>现有崩溃 dump（新的在前）：只在 crashes.txt 里列清单（体积大，不塞进 zip）。</summary>
    public IReadOnlyList<string> CrashDumps { get; init; } = [];
}

/// <summary>诊断包导出结果。</summary>
internal sealed record DiagnosticsPackResult(string Path, int EntryCount, long SizeBytes)
{
    public string Describe() => $"{Path}（{EntryCount} 个条目，{SizeBytes / 1024} KB）";
}

/// <summary>
/// 诊断包导出（M6 第 5 项，规划 §10.4）。用户遇到问题时一键打一个 zip 交给我。
///
/// 包内结构：
/// <code>
///   README.txt              包说明 + 隐私声明
///   report.txt              版本 / 进程 / 路径等运行状态
///   monitors.txt            显示器拓扑
///   hotkeys.txt             生效热键表
///   settings.json           当前配置（已脱敏）
///   performance.txt         性能埋点报告（T4 前是占位说明）
///   crashes.txt             崩溃清单 + 最近一份崩溃报告正文
///   crash-*.txt             每份崩溃报告的原文（已脱敏）
///   logs/whsnap-*.log       最新日志（截断到最近若干行，已脱敏）
/// </code>
///
/// **隐私边界（写死在 README 与报告里）**：不含剪贴板内容、不含任何凭据（本程序不存凭据）；
/// 所有文本里的用户目录一律替换成 <c>%USERPROFILE%</c>。
/// </summary>
internal static class DiagnosticsPack
{
    /// <summary>日志最多带这么多行（避免包体积失控）。</summary>
    public const int MaxLogLines = 2000;

    /// <summary>日志最多带这么多字节。</summary>
    public const int MaxLogBytes = 1024 * 1024;

    public static string DefaultDirectory => Path.Combine(SettingsService.ConfigDirectory, "diagnostics");

    /// <summary>
    /// 按"真实运行状态"收集上下文：崩溃证据、日志目录、性能报告都取默认位置。
    /// 托盘/设置维护页与命令行导出共用它，保证两条入口打出来的包结构完全一致。
    /// </summary>
    public static DiagnosticsPackContext Collect(
        string version,
        string report,
        string monitors,
        string hotkeys,
        string settingsJson,
        string outputDirectory,
        string? logDirectory = null,
        string? crashDirectory = null,
        string? performanceReport = null) => new()
        {
            OutputDirectory = outputDirectory,
            Version = version,
            Report = report,
            Monitors = monitors,
            Hotkeys = hotkeys,
            SettingsJson = settingsJson,
            LogDirectory = logDirectory ?? Log.LogDirectory,
            PerformanceReport = performanceReport,
            CrashReports = CrashHandler.ListReports(crashDirectory),
            CrashDumps = CrashHandler.ListDumps(crashDirectory),
        };

    public static DiagnosticsPackResult Create(DiagnosticsPackContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Directory.CreateDirectory(context.OutputDirectory);

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string fileName = $"WhSnap-diagnostics-{Sanitize(context.Version)}-{stamp}.zip";
        string path = Path.Combine(context.OutputDirectory, fileName);

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            AddText(zip, "README.txt", BuildReadme(context));
            AddText(zip, "report.txt", context.Report);
            AddText(zip, "monitors.txt", context.Monitors);
            AddText(zip, "hotkeys.txt", context.Hotkeys);
            AddText(zip, "settings.json", context.SettingsJson);
            AddText(zip, "performance.txt", BuildPerformance(context));
            AddText(zip, "crashes.txt", BuildCrashIndex(context));

            // 每份崩溃报告的原文（脱敏后）都带进包，便于直接阅读
            for (int i = 0; i < context.CrashReports.Count; i++)
            {
                AddFile(zip, $"crashes/{Path.GetFileName(context.CrashReports[i])}", context.CrashReports[i]);
            }

            AddLatestLog(zip, context.LogDirectory);
        }

        int entries = CountEntries(path);
        long size = new FileInfo(path).Length;
        Log.Info($"诊断包已导出：{path}（{entries} 个条目，{size / 1024} KB）");
        return new DiagnosticsPackResult(path, entries, size);
    }

    /// <summary>zip 里的条目名清单（自检据此断言"该有的都在"）。</summary>
    public static IReadOnlyList<string> ReadEntryNames(string zipPath)
    {
        using var stream = File.OpenRead(zipPath);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return zip.Entries.Select(entry => entry.FullName).ToList();
    }

    /// <summary>读回某个条目的文本（自检验证"重新打开能读出内容"）。</summary>
    public static string? ReadEntryText(string zipPath, string entryName)
    {
        using var stream = File.OpenRead(zipPath);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry(entryName);

        if (entry is null)
        {
            return null;
        }

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string BuildReadme(DiagnosticsPackContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine("WhSnap 诊断包");
        builder.AppendLine("==============");
        builder.AppendLine($"生成时间　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"程序版本　WhSnap {context.Version}");
        builder.AppendLine();
        builder.AppendLine("这是什么");
        builder.AppendLine("--------");
        builder.AppendLine("用户遇到问题时一键导出的证据包，用于定位问题。可以直接把这个 zip 发给开发者。");
        builder.AppendLine();
        builder.AppendLine("包里有什么");
        builder.AppendLine("----------");
        builder.AppendLine("  README.txt        本说明");
        builder.AppendLine("  report.txt        版本 / 进程 / 路径等运行状态");
        builder.AppendLine("  monitors.txt      显示器拓扑（物理矩形、DPI、缩放、主屏）");
        builder.AppendLine("  hotkeys.txt       当前生效的热键表");
        builder.AppendLine("  settings.json     当前配置");
        builder.AppendLine("  performance.txt   性能埋点报告（尚未实现时是占位说明）");
        builder.AppendLine("  crashes.txt       崩溃清单 + 最近一次崩溃的摘要");
        builder.AppendLine("  crashes/          每份崩溃报告的原文");
        builder.AppendLine("  logs/             最新一份日志（已截断）");
        builder.AppendLine();
        builder.AppendLine("包里没有什么（隐私边界）");
        builder.AppendLine("------------------------");
        builder.AppendLine("  * 不含剪贴板内容（截图、文本、图片都不会进包）；");
        builder.AppendLine("  * 不含任何凭据、账号、令牌（本程序不保存凭据）；");
        builder.AppendLine("  * 所有文本里的用户目录都已替换为 %USERPROFILE%。");
        builder.AppendLine();
        builder.AppendLine("怎么用");
        builder.AppendLine("------");
        builder.AppendLine("  * 想看运行状态：report.txt");
        builder.AppendLine("  * 想看当时发生了什么：logs/ 里的日志");
        builder.AppendLine("  * 想看崩溃：crashes.txt 与 crashes/ 下的报告");
        builder.AppendLine("  * 崩溃的 .dmp 体积较大，默认不打进包；crashes.txt 里有它在本机的路径。");
        return builder.ToString();
    }

    private static string BuildPerformance(DiagnosticsPackContext context) =>
        context.PerformanceReport ?? "（性能埋点尚未实现：M6 第 7 项完成后，本文件会自动带上热键→遮罩、鼠标跟随、确认→剪贴板、冷启动四项实测值。）";

    private static string BuildCrashIndex(DiagnosticsPackContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"崩溃证据清单　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine(new string('-', 68));

        if (context.CrashReports.Count == 0 && context.CrashDumps.Count == 0)
        {
            builder.AppendLine("没有找到崩溃记录（这是一件好事）。");
        }
        else
        {
            builder.AppendLine($"文本报告 {context.CrashReports.Count} 份（已随包带上，见 crashes/）：");

            foreach (string report in context.CrashReports)
            {
                builder.AppendLine($"  {Path.GetFileName(report)}　{File.GetLastWriteTime(report):yyyy-MM-dd HH:mm:ss}");
            }

            builder.AppendLine();
            builder.AppendLine($"内存转储 {context.CrashDumps.Count} 个（体积较大，未打进包；需要时请单独发送）：");

            foreach (string dump in context.CrashDumps)
            {
                builder.AppendLine($"  {Path.GetFileName(dump)}　{new FileInfo(dump).Length / 1024} KB　{dump}");
            }
        }

        builder.AppendLine(new string('-', 68));
        builder.AppendLine("隐私说明：崩溃报告与日志在打包时都已把用户目录替换为 %USERPROFILE%。");
        return builder.ToString();
    }

    private static void AddLatestLog(ZipArchive zip, string? logDirectory)
    {
        if (string.IsNullOrWhiteSpace(logDirectory) || !Directory.Exists(logDirectory))
        {
            AddText(zip, "logs/(没有日志文件).txt", "没有找到日志目录。");
            return;
        }

        string? newest = Directory.GetFiles(logDirectory, "whsnap-*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (newest is null)
        {
            AddText(zip, "logs/(没有日志文件).txt", "日志目录里没有 whsnap-*.log。");
            return;
        }

        try
        {
            var lines = File.ReadLines(newest).TakeLast(MaxLogLines).ToList();
            string text = string.Join(Environment.NewLine, lines);

            if (text.Length > MaxLogBytes)
            {
                text = text[^MaxLogBytes..];
            }

            var builder = new StringBuilder();
            builder.AppendLine($"# 来源：{Path.GetFileName(newest)}（最多保留最近 {MaxLogLines} 行 / {MaxLogBytes / 1024} KB）");
            builder.AppendLine(text);
            AddText(zip, $"logs/{Path.GetFileName(newest)}", builder.ToString());
        }
        catch (Exception ex)
        {
            AddText(zip, "logs/(读取失败).txt", $"读取日志失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void AddText(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);

        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        // 统一在入口处脱敏：settings.json 里的保存目录、日志里的路径都含用户名
        writer.Write(CrashHandler.Redact(content));
    }

    private static void AddFile(ZipArchive zip, string entryName, string sourcePath)
    {
        try
        {
            AddText(zip, entryName, File.ReadAllText(sourcePath));
        }
        catch (Exception ex)
        {
            AddText(zip, entryName + ".error.txt", $"读取 {sourcePath} 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int CountEntries(string zipPath)
    {
        try
        {
            return ReadEntryNames(zipPath).Count;
        }
        catch
        {
            return 0;
        }
    }

    private static string Sanitize(string version)
    {
        var builder = new StringBuilder(version.Length);

        foreach (char c in version)
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        }

        return builder.Length == 0 ? "0.0.0" : builder.ToString();
    }
}
