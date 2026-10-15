using System.IO;
using System.Text;
using WhSnap.Interop;
using WhSnap.Services;

namespace WhSnap.Diagnostics;

/// <summary>
/// 崩溃证据自检：<c>WhSnap.exe --crash-test [--with-dump]</c>
///
/// 默认**不写 dump**（避免自检产生大文件），只验证：
///   ① dumps 目录可创建、可写、可删；
///   ② <c>dbghelp!MiniDumpWriteDump</c> 可解析（这台机器上 dump 能力是否存在）；
///   ③ 文本崩溃报告能写出，且含版本/异常类型/堆栈/隐私说明，**用户目录已脱敏**；
///   ④ 「上次崩溃」标记读一次即消费（保证只提示一次）；
///   ⑤ 保留策略：超过上限的旧证据会被清理。
/// 加 <c>--with-dump</c> 会真的写一份自身进程的 minidump，校验 MDMP 魔数与体积后立即删除。
///
/// 全部在临时目录里跑，不碰 <c>%APPDATA%\WhSnap\dumps</c>，也不会让下次启动弹"上次崩溃"气泡。
/// </summary>
internal static class CrashTestCommand
{
    public static int Run(bool withDump = false)
    {
        var report = new StringBuilder();
        var failures = new List<string>();
        string root = Path.Combine(Path.GetTempPath(), "whsnap-crash-test");

        report.AppendLine($"WhSnap 崩溃证据自检　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"程序版本：{UpdateService.GetCurrentVersion()}　写 dump：{(withDump ? "是（写完即删）" : "否")}");
        report.AppendLine(new string('-', 68));

        void Check(string name, Action body)
        {
            try
            {
                body();
                report.AppendLine($"[PASS] {name}");
            }
            catch (Exception ex)
            {
                failures.Add(name);
                report.AppendLine($"[FAIL] {name}：{ex.GetType().Name}: {ex.Message}");
            }
        }

        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            Directory.CreateDirectory(root);

            Check("dumps 目录可创建、可写、可删", () =>
            {
                string probe = Path.Combine(root, "write-probe.tmp");
                File.WriteAllText(probe, "probe", new UTF8Encoding(false));

                if (!File.Exists(probe) || new FileInfo(probe).Length == 0)
                {
                    throw new InvalidOperationException("探针文件写入后读不到。");
                }

                File.Delete(probe);
            });

            Check("dbghelp!MiniDumpWriteDump 可解析（不写 dump）", () =>
            {
                if (!NativeMethods.IsDumpApiAvailable())
                {
                    throw new InvalidOperationException("这台机器上 dbghelp.dll 或 MiniDumpWriteDump 不可用。");
                }
            });

            Check("文本崩溃报告：内容齐全且已脱敏", () =>
            {
                var crash = CrashHandler.Write(
                    "自检",
                    new InvalidOperationException($"自检构造的异常，含用户目录：{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}"),
                    writeDump: false,
                    directory: root);

                if (crash is null || !File.Exists(crash.TextPath))
                {
                    throw new InvalidOperationException("崩溃报告没有落盘。");
                }

                string text = File.ReadAllText(crash.TextPath);
                string version = UpdateService.GetCurrentVersion();

                foreach (string expected in new[] { version, "InvalidOperationException", "自检", "隐私说明" })
                {
                    if (!text.Contains(expected, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"报告里缺少「{expected}」。");
                    }
                }

                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                if (!string.IsNullOrWhiteSpace(profile) && text.Contains(profile, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"报告里仍然出现明文用户目录：{profile}");
                }

                if (!text.Contains("%USERPROFILE%", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("报告里没有出现脱敏标记 %USERPROFILE%。");
                }

                if (crash.DumpPath is not null)
                {
                    throw new InvalidOperationException("writeDump:false 时不该产生 dump。");
                }

                report.AppendLine($"       报告：{crash.TextPath}（{new FileInfo(crash.TextPath).Length} 字节）");
            });

            Check("「上次崩溃」标记读一次即消费", () =>
            {
                string? first = CrashHandler.ReadPendingNoticeOnce(root);

                if (string.IsNullOrWhiteSpace(first) || !first.Contains("自检", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"首次读取没有拿到崩溃摘要：{first ?? "null"}");
                }

                string? second = CrashHandler.ReadPendingNoticeOnce(root);

                if (second is not null)
                {
                    throw new InvalidOperationException("标记被重复消费，下次启动会反复弹气泡。");
                }
            });

            Check("保留策略：旧证据会被清理", () =>
            {
                for (int i = 0; i < CrashHandler.RetentionCount + 2; i++)
                {
                    CrashHandler.Write("保留策略自检", new InvalidOperationException($"第 {i} 份"), writeDump: false, directory: root);
                }

                var reports = CrashHandler.ListReports(root);

                if (reports.Count != CrashHandler.RetentionCount)
                {
                    throw new InvalidOperationException($"保留 {reports.Count} 份，期望 {CrashHandler.RetentionCount} 份。");
                }
            });

            if (withDump)
            {
                Check("真写一份自身进程的 minidump 并校验", () =>
                {
                    string dumpPath = Path.Combine(root, "probe.dmp");

                    if (!CrashHandler.TryWriteMiniDump(dumpPath, out string? error))
                    {
                        throw new InvalidOperationException($"写 dump 失败：{error ?? "未知原因"}");
                    }

                    var info = new FileInfo(dumpPath);

                    if (!info.Exists || info.Length < 4096)
                    {
                        throw new InvalidOperationException($"dump 体积异常：{(info.Exists ? info.Length : -1)} 字节。");
                    }

                    using (var stream = File.OpenRead(dumpPath))
                    {
                        var header = new byte[4];

                        if (stream.Read(header, 0, 4) != 4 || Encoding.ASCII.GetString(header) != "MDMP")
                        {
                            throw new InvalidOperationException("dump 文件头不是 MDMP。");
                        }
                    }

                    long size = info.Length;
                    File.Delete(dumpPath);
                    report.AppendLine($"       真实 dump：{size / 1024} KB（MDMP 头正确，已删除）");
                });
            }
        }
        catch (Exception ex)
        {
            failures.Add("自检框架");
            report.AppendLine($"[FAIL] 自检框架：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // 临时目录清理失败不影响结论
            }
        }

        report.AppendLine(new string('-', 68));
        report.AppendLine(failures.Count == 0 ? "结论：全部通过。" : $"结论：{failures.Count} 项失败 → {string.Join("、", failures)}");

        Write(report);
        return failures.Count == 0 ? 0 : 1;
    }

    private static void Write(StringBuilder report)
    {
        string path = Path.Combine(Path.GetTempPath(), "whsnap-crash-test.txt");

        try
        {
            File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 报告落盘失败不影响退出码
        }

        try
        {
            Console.WriteLine(report.ToString());
        }
        catch
        {
            // 无控制台时忽略
        }
    }
}
