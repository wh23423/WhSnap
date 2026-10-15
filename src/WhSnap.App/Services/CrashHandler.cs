using System.IO;
using System.Text;
using WhSnap.Interop;

namespace WhSnap.Services;

/// <summary>一次崩溃落盘的结果（路径 + 是否写成功 dump + 可读摘要）。</summary>
internal sealed record CrashReport(string TextPath, string? DumpPath, string Summary, string Source, DateTime TimestampUtc);

/// <summary>
/// 崩溃处理（M6 第 6 项）。目标只有一个：**用户遇到崩溃时，手里有能交给我的东西**。
///
/// 每次崩溃写两份证据到 <c>%APPDATA%\WhSnap\dumps\</c>：
///   * <c>crash-&lt;ts&gt;.txt</c>：脱敏文本报告（版本、来源、异常类型/消息/堆栈、最近的日志尾巴）；
///   * <c>crash-&lt;ts&gt;.dmp</c>：自身进程的 minidump（<c>MiniDumpNormal | MiniDumpWithThreadInfo</c>，几 MB 量级）。
///
/// 同时写一份 <c>last-crash.txt</c> 标记，下次启动弹一次气泡告知（读完即删，不反复打扰）。
/// dump 走自己调 <c>MiniDumpWriteDump</c>，**不注册 WER LocalDumps**（那需要管理员权限）。
/// 报告与包一样做脱敏：用户目录一律替换成 <c>%USERPROFILE%</c>。
/// </summary>
internal static class CrashHandler
{
    /// <summary>最多保留多少份崩溃证据（每份含文本 + dump，避免无限长大）。</summary>
    public const int RetentionCount = 5;

    public static string DumpDirectory => Path.Combine(SettingsService.ConfigDirectory, "dumps");

    /// <summary>「上次崩溃」标记文件名。</summary>
    public const string PendingNoticeFileName = "last-crash.txt";

    /// <summary>崩溃证据目录；传目录参数时用它（自检用临时目录，不碰用户数据）。</summary>
    public static string ResolveDirectory(string? directory) =>
        string.IsNullOrWhiteSpace(directory) ? DumpDirectory : directory;

    /// <summary>
    /// 写一次崩溃证据。**任何一步失败都不抛异常**（崩溃路径上再抛异常只会掩盖原始问题）。
    /// </summary>
    public static CrashReport? Write(string source, Exception? exception, bool writeDump = true, string? directory = null)
    {
        DateTime now = DateTime.Now;
        string root = ResolveDirectory(directory);

        try
        {
            Directory.CreateDirectory(root);

            string stamp = now.ToString("yyyyMMdd-HHmmss-fff");
            string textPath = UniquePath(root, $"crash-{stamp}", ".txt");
            string? dumpPath = null;

            if (writeDump)
            {
                string candidate = UniquePath(root, $"crash-{stamp}", ".dmp");
                dumpPath = TryWriteMiniDump(candidate, out string? dumpError) ? candidate : null;

                if (dumpPath is null && !string.IsNullOrWhiteSpace(dumpError))
                {
                    exception ??= new InvalidOperationException(dumpError);
                }
            }

            string text = BuildReport(source, now, exception, dumpPath);
            File.WriteAllText(textPath, text, new UTF8Encoding(false));

            var report = new CrashReport(textPath, dumpPath, Summarize(exception), source, now.ToUniversalTime());
            WritePendingNotice(report, root);
            Trim(root);
            Log.Error($"已记录崩溃证据：{textPath}" + (dumpPath is null ? "（未生成 dump）" : $" + {dumpPath}"));

            return report;
        }
        catch (Exception ex)
        {
            // 崩溃处理器自身的失败只能进日志
            Log.Error("写崩溃报告失败", ex);
            return null;
        }
    }

    /// <summary>读一次「上次崩溃」标记；读到即消费（删除），保证只提示一次。</summary>
    public static string? ReadPendingNoticeOnce(string? directory = null)
    {
        try
        {
            string marker = Path.Combine(ResolveDirectory(directory), PendingNoticeFileName);

            if (!File.Exists(marker))
            {
                return null;
            }

            string text = File.ReadAllText(marker);
            File.Delete(marker);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取上次崩溃标记失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>把自身进程写成 minidump。返回是否成功；失败原因通过 <paramref name="error"/> 带回。</summary>
    public static bool TryWriteMiniDump(string path, out string? error)
    {
        bool ok;
        string? failure = null;

        try
        {
            // 先关文件再删：Windows 上打开着的文件删不掉
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                ok = NativeMethods.MiniDumpWriteDump(
                    NativeMethods.GetCurrentProcess(),
                    NativeMethods.GetCurrentProcessId(),
                    stream.SafeFileHandle,
                    NativeMethods.MiniDumpNormal | NativeMethods.MiniDumpWithThreadInfo,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (!ok)
                {
                    failure = $"MiniDumpWriteDump 失败（Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）";
                }
            }
        }
        catch (Exception ex)
        {
            ok = false;
            failure = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (!ok)
        {
            TryDelete(path);
            error = failure;
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// 脱敏：用户目录 → <c>%USERPROFILE%</c>（临时目录、AppData、日志路径都在它下面，一并覆盖）。
    ///
    /// 要处理三种写法，否则会在 JSON 或 URL 里漏掉：
    ///   * 原样：<c>C:\Users\x\Pictures</c>；
    ///   * JSON 转义：<c>C:\\Users\\x\\Pictures</c>（`settings.json` 里就是这个形态）；
    ///   * 正斜杠：<c>C:/Users/x/Pictures</c>（file:// URL、部分日志）。
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string result = text;
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrWhiteSpace(profile))
        {
            result = result
                .Replace(profile.Replace("\\", "\\\\", StringComparison.Ordinal), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                .Replace(profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                .Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        string userName = Environment.UserName;

        if (!string.IsNullOrWhiteSpace(userName))
        {
            result = result
                .Replace($@"C:\\Users\\{userName}", "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                .Replace($@"C:\Users\{userName}", "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                .Replace($"C:/Users/{userName}", "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>现有崩溃文本报告（新的在前），供诊断包收集。</summary>
    public static IReadOnlyList<string> ListReports(string? directory = null)
    {
        try
        {
            string root = ResolveDirectory(directory);

            if (!Directory.Exists(root))
            {
                return [];
            }

            return Directory.GetFiles(root, "crash-*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举崩溃报告失败：{ex.Message}");
            return [];
        }
    }

    /// <summary>现有 dump 文件（新的在前）。</summary>
    public static IReadOnlyList<string> ListDumps(string? directory = null)
    {
        try
        {
            string root = ResolveDirectory(directory);

            if (!Directory.Exists(root))
            {
                return [];
            }

            return Directory.GetFiles(root, "crash-*.dmp")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举崩溃 dump 失败：{ex.Message}");
            return [];
        }
    }

    private static string BuildReport(string source, DateTime now, Exception? exception, string? dumpPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"WhSnap 崩溃报告　{now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine(new string('-', 68));
        builder.AppendLine($"来源　　　{source}");
        builder.AppendLine($"版本　　　WhSnap {UpdateService.GetCurrentVersion()}");
        builder.AppendLine($"系统　　　{Environment.OSVersion.VersionString}（{(Environment.Is64BitProcess ? "x64" : "x86")}）");
        builder.AppendLine($"进程　　　PID {Environment.ProcessId}");
        builder.AppendLine($"工作目录　{AppContext.BaseDirectory}");
        builder.AppendLine($"dump　　　{dumpPath ?? "（未生成）"}");

        if (exception is not null)
        {
            builder.AppendLine(new string('-', 68));
            builder.AppendLine($"异常类型　{exception.GetType().FullName}");
            builder.AppendLine($"异常消息　{exception.Message}");
            builder.AppendLine("堆栈：");
            builder.AppendLine(exception.StackTrace ?? "（无堆栈）");

            if (exception.InnerException is not null)
            {
                builder.AppendLine("内部异常：");
                builder.AppendLine($"{exception.InnerException.GetType().FullName}: {exception.InnerException.Message}");
            }
        }

        builder.AppendLine(new string('-', 68));
        builder.AppendLine("最近日志（最多 40 行）：");

        foreach (string line in ReadLogTail(40))
        {
            builder.AppendLine(line);
        }

        builder.AppendLine(new string('-', 68));
        builder.AppendLine("隐私说明：本报告已把用户目录替换为 %USERPROFILE%；不含剪贴板内容、不含任何凭据（本程序不存凭据）。");

        return Redact(builder.ToString());
    }

    private static IEnumerable<string> ReadLogTail(int lines)
    {
        try
        {
            string log = Log.CurrentLogFile;

            if (!File.Exists(log))
            {
                return ["（没有日志文件）"];
            }

            return File.ReadLines(log).TakeLast(lines);
        }
        catch (Exception ex)
        {
            return [$"（读取日志失败：{ex.Message}）"];
        }
    }

    private static string Summarize(Exception? exception) =>
        exception is null ? "未知异常" : $"{exception.GetType().Name}: {exception.Message}";

    private static void WritePendingNotice(CrashReport report, string root)
    {
        try
        {
            string text = $"上次运行发生崩溃：{report.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}（{report.Source}）\n"
                          + $"摘要：{report.Summary}\n"
                          + $"证据：{report.TextPath}\n"
                          + (report.DumpPath is null ? "dump：未生成\n" : $"dump：{report.DumpPath}\n");

            File.WriteAllText(Path.Combine(root, PendingNoticeFileName), Redact(text), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Warn($"写上次崩溃标记失败：{ex.Message}");
        }
    }

    /// <summary>只保留最近 <see cref="RetentionCount"/> 份文本报告与 dump。</summary>
    private static void Trim(string root)
    {
        try
        {
            foreach (var group in new[] { ListReports(root), ListDumps(root) })
            {
                foreach (string path in group.Skip(RetentionCount))
                {
                    TryDelete(path);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"清理旧崩溃证据失败：{ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }

    /// <summary>同一秒内多次崩溃也不能互相覆盖（自检会连写多份验证保留策略）。</summary>
    private static string UniquePath(string root, string baseName, string extension)
    {
        string path = Path.Combine(root, baseName + extension);
        int index = 2;

        while (File.Exists(path))
        {
            path = Path.Combine(root, $"{baseName}-{index}{extension}");
            index++;
        }

        return path;
    }
}
