using System.Text;
using WhSnap.Core;
using WhSnap.Services;

namespace WhSnap.Diagnostics;

/// <summary>
/// 更新链路自检：WhSnap.exe --update-test
///
/// 全程在临时目录里用**本地传输**端到端跑：构造 update.json 与假安装包 →
/// 检查 → 下载 → SHA-256 校验 → 篡改后必须被拒 → 通道/版本/最低版本门槛。
/// 不需要联网，也不会真的启动安装器。
/// </summary>
internal static class UpdateTestCommand
{
    public static int Run()
    {
        var report = new StringBuilder();
        var failures = new List<string>();
        string workDir = Path.Combine(Path.GetTempPath(), "whsnap-update-test");

        report.AppendLine($"WhSnap 更新链路自检　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"当前程序版本（读自程序集）：{UpdateService.GetCurrentVersion()}");
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
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }

            Directory.CreateDirectory(workDir);

            string payloadPath = Path.Combine(workDir, "WhSnap-9.9.9-setup.exe");
            CreatePayload(payloadPath);
            string payloadHash = Sha256Util.ComputeFile(payloadPath);
            long payloadSize = new FileInfo(payloadPath).Length;

            report.AppendLine($"测试载荷：{payloadPath}（{payloadSize / 1024} KB，SHA-256 {payloadHash[..16]}…）");
            report.AppendLine(new string('-', 68));

            Check("清单解析与语义化版本比较", () =>
            {
                if (!SemanticVersion.TryParse("9.9.9", out var newer) || !SemanticVersion.TryParse("0.5.0", out var current))
                {
                    throw new InvalidOperationException("版本解析失败。");
                }

                if (!newer.IsNewerThan(current))
                {
                    throw new InvalidOperationException("9.9.9 应大于 0.5.0。");
                }

                if (!SemanticVersion.TryParse("1.0.0-preview.1", out var pre) || !SemanticVersion.TryParse("1.0.0", out var stable) || !stable.IsNewerThan(pre))
                {
                    throw new InvalidOperationException("正式版应大于同号预览版。");
                }

                string json = BuildManifest("9.9.9", "stable", payloadPath, payloadHash, payloadSize, string.Empty).ToJson();
                var parsed = UpdateManifest.FromJson(json);

                if (parsed is null || parsed.Version != "9.9.9" || parsed.Sha256 != payloadHash)
                {
                    throw new InvalidOperationException("清单往返解析不一致。");
                }
            });

            Check("本地清单：检查到可更新", () =>
            {
                string manifestPath = WriteManifest(workDir, "update-ok.json", "9.9.9", "stable", payloadPath, payloadHash, payloadSize, string.Empty);
                var service = new UpdateService(manifestPath, "stable", "0.5.0");
                var result = service.CheckAsync().GetAwaiter().GetResult();

                if (!result.CanUpdate || result.Manifest is null)
                {
                    throw new InvalidOperationException($"应判定可更新，实际：{result.Message}");
                }
            });

            Check("下载 + SHA-256 校验通过", () =>
            {
                var manifest = BuildManifest("9.9.9", "stable", payloadPath, payloadHash, payloadSize, string.Empty);
                var service = new UpdateService(Path.Combine(workDir, "unused.json"), "stable", "0.5.0");
                string downloaded = service.DownloadAsync(manifest).GetAwaiter().GetResult();

                if (!File.Exists(downloaded))
                {
                    throw new InvalidOperationException("下载后文件不存在。");
                }

                if (!Sha256Util.Matches(payloadHash, downloaded, out string actual))
                {
                    throw new InvalidOperationException($"下载文件哈希不符：{actual}");
                }

                if (new FileInfo(downloaded).Length != payloadSize)
                {
                    throw new InvalidOperationException("下载文件体积不符。");
                }

                File.Delete(downloaded);
            });

            Check("篡改安装包必须被拒绝（且不残留临时文件）", () =>
            {
                const string version = "9.9.8";
                string tamperedPath = Path.Combine(workDir, "WhSnap-9.9.8-setup.exe");
                CreatePayload(tamperedPath);

                // 先记录正确哈希，再篡改一个字节——清单里仍然是旧哈希
                string tamperedHash = Sha256Util.ComputeFile(tamperedPath);
                var bytes = File.ReadAllBytes(tamperedPath);
                bytes[^1] ^= 0xFF;
                File.WriteAllBytes(tamperedPath, bytes);

                var manifest = BuildManifest(version, "stable", tamperedPath, tamperedHash, bytes.Length, string.Empty);
                var service = new UpdateService(Path.Combine(workDir, "unused.json"), "stable", "0.5.0");

                bool rejected = false;
                try
                {
                    service.DownloadAsync(manifest).GetAwaiter().GetResult();
                }
                catch (InvalidDataException ex)
                {
                    rejected = ex.Message.Contains("SHA-256", StringComparison.Ordinal);
                }

                if (!rejected)
                {
                    throw new InvalidOperationException("被篡改的安装包没有被拒绝。");
                }

                string target = UpdateService.GetDownloadPath(version);
                if (File.Exists(target))
                {
                    throw new InvalidOperationException($"校验失败后仍残留了下载文件：{target}");
                }
            });

            Check("清单缺少 SHA-256 时拒绝下载", () =>
            {
                var manifest = BuildManifest("9.9.7", "stable", payloadPath, string.Empty, 0, string.Empty);
                var service = new UpdateService(Path.Combine(workDir, "unused.json"), "stable", "0.5.0");

                try
                {
                    service.DownloadAsync(manifest).GetAwaiter().GetResult();
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                throw new InvalidOperationException("缺少哈希的清单仍被下载。");
            });

            Check("通道规则：稳定通道拒绝预览版，测试通道接受", () =>
            {
                string betaManifest = WriteManifest(workDir, "update-beta.json", "9.9.9", "beta", payloadPath, payloadHash, payloadSize, string.Empty);

                var stable = new UpdateService(betaManifest, "stable", "0.5.0").CheckAsync().GetAwaiter().GetResult();
                if (stable.CanUpdate)
                {
                    throw new InvalidOperationException("稳定通道不应接受 beta 清单。");
                }

                var beta = new UpdateService(betaManifest, "beta", "0.5.0").CheckAsync().GetAwaiter().GetResult();
                if (!beta.CanUpdate)
                {
                    throw new InvalidOperationException($"测试通道应接受 beta 清单，实际：{beta.Message}");
                }
            });

            Check("旧版本 / 同版本不提示更新", () =>
            {
                string older = WriteManifest(workDir, "update-old.json", "0.0.1", "stable", payloadPath, payloadHash, payloadSize, string.Empty);
                var result = new UpdateService(older, "stable", "0.5.0").CheckAsync().GetAwaiter().GetResult();

                if (result.CanUpdate)
                {
                    throw new InvalidOperationException("0.0.1 不应被判定为可更新。");
                }
            });

            Check("最低可升级版本门槛生效", () =>
            {
                string gated = WriteManifest(workDir, "update-gated.json", "9.9.9", "stable", payloadPath, payloadHash, payloadSize, "9.0.0");
                var result = new UpdateService(gated, "stable", "0.5.0").CheckAsync().GetAwaiter().GetResult();

                if (result.CanUpdate)
                {
                    throw new InvalidOperationException("低于最低可升级版本时不应允许更新。");
                }
            });

            Check("清单缺失 / 格式错误不抛异常", () =>
            {
                var missing = new UpdateService(Path.Combine(workDir, "not-found.json"), "stable", "0.5.0").CheckAsync().GetAwaiter().GetResult();
                if (missing.CanUpdate || missing.Manifest is not null)
                {
                    throw new InvalidOperationException("缺失的清单不应判定为可更新。");
                }

                string broken = Path.Combine(workDir, "update-broken.json");
                File.WriteAllText(broken, "{ this is not json", new UTF8Encoding(false));
                var malformed = new UpdateService(broken, "stable", "0.5.0").CheckAsync().GetAwaiter().GetResult();

                if (malformed.CanUpdate)
                {
                    throw new InvalidOperationException("损坏的清单不应判定为可更新。");
                }
            });

            Check("静默安装命令行正确", () =>
            {
                string args = UpdateService.BuildInstallerArguments();

                foreach (string expected in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" })
                {
                    if (!args.Contains(expected, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"安装参数缺少 {expected}：{args}");
                    }
                }

                var startInfo = UpdateService.BuildInstallerStartInfo(@"C:\temp\setup.exe");
                if (!startInfo.UseShellExecute || !startInfo.FileName.EndsWith("setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("安装启动信息不正确。");
                }
            });
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
                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, recursive: true);
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

    private static void CreatePayload(string path)
    {
        var bytes = new byte[256 * 1024];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
    }

    private static UpdateManifest BuildManifest(
        string version,
        string channel,
        string url,
        string sha256,
        long size,
        string minimumVersion) => new()
        {
            Version = version,
            Channel = channel,
            Url = url,
            Sha256 = sha256,
            Size = size,
            Notes = "自检用清单",
            MinimumVersion = minimumVersion,
        };

    private static string WriteManifest(
        string directory,
        string fileName,
        string version,
        string channel,
        string url,
        string sha256,
        long size,
        string minimumVersion)
    {
        string path = Path.Combine(directory, fileName);
        var manifest = BuildManifest(version, channel, url, sha256, size, minimumVersion);
        File.WriteAllText(path, manifest.ToJson(), new UTF8Encoding(false));
        return path;
    }

    private static void Write(StringBuilder report)
    {
        string path = Path.Combine(Path.GetTempPath(), "whsnap-update-test.txt");

        try
        {
            File.WriteAllText(path, report.ToString(), new UTF8Encoding(true));
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
