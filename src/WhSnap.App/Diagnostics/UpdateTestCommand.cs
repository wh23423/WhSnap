using System.Text;
using WhSnap.Core;
using WhSnap.Services;

namespace WhSnap.Diagnostics;

/// <summary>
/// 更新链路自检：WhSnap.exe --update-test [--manifest &lt;update.json&gt;]
///
/// 全程在临时目录里用**本地传输**端到端跑：构造 update.json 与假安装包 →
/// 检查 → 下载 → SHA-256 校验 → 篡改后必须被拒 → 通道/版本/最低版本门槛。
/// 不需要联网，也不会真的启动安装器。
///
/// 带 <c>--manifest</c> 时额外验证"构建脚本刚产出的那份发布清单"：
/// 字段能解析、哈希是 64 位十六进制、本地安装包体积与 SHA-256 对得上，
/// 并且能被 <see cref="UpdateService"/> 真的下载校验一遍——这是 T6 的机器验收。
/// </summary>
internal static class UpdateTestCommand
{
    public static int Run(string? manifestPath = null)
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

            // 这一段专门守住"配置面上有开关、代码里没人读"的缺口（M6 第 4 项之前的实际状态）
            Check("更新配置项被运行时消费（清单地址 / 通道 / 启动开关 / 自动下载）", () =>
            {
                string manifestPath = WriteManifest(workDir, "update-settings.json", "9.9.9", "beta", payloadPath, payloadHash, payloadSize, string.Empty);

                var settings = new SettingsService();
                settings.Load();

                string originalUrl = settings.Current.UpdateManifestUrl;
                string originalChannel = settings.Current.UpdateChannel;
                bool originalStartup = settings.Current.CheckForUpdatesOnStartup;
                bool originalAutoDownload = settings.Current.UpdateAutoDownload;

                try
                {
                    settings.Current.UpdateManifestUrl = manifestPath;
                    settings.Current.UpdateChannel = "beta";
                    settings.Current.CheckForUpdatesOnStartup = false;
                    settings.Current.UpdateAutoDownload = true;

                    var coordinator = new UpdateCoordinator(settings);

                    if (coordinator.ManifestSource != manifestPath || coordinator.Channel != UpdateChannels.Beta)
                    {
                        throw new InvalidOperationException(
                            $"清单地址/通道没有被消费：{coordinator.ManifestSource} / {coordinator.Channel}");
                    }

                    if (coordinator.StartupCheckEnabled || !coordinator.AutoDownloadEnabled)
                    {
                        throw new InvalidOperationException(
                            $"启动检查({coordinator.StartupCheckEnabled}) / 自动下载({coordinator.AutoDownloadEnabled}) 开关没有被消费。");
                    }

                    if (string.IsNullOrWhiteSpace(coordinator.CurrentVersion) || coordinator.CurrentVersion != UpdateService.GetCurrentVersion())
                    {
                        throw new InvalidOperationException($"版本号不可读：{coordinator.CurrentVersion}");
                    }

                    // 本自检整体跑在线程池上，这里同步等待是安全的（不会死锁）；
                    // 顺带验证"按配置构造的服务"真的能跑完一次检查。
                    var result = coordinator.CheckAsync().GetAwaiter().GetResult();
                    if (!result.CanUpdate)
                    {
                        throw new InvalidOperationException($"按当前配置检查未发现可更新版本：{result.Message}");
                    }

                    report.AppendLine($"       版本 {coordinator.CurrentVersion}，通道 {coordinator.Channel}，清单 {coordinator.ManifestSource}");
                }
                finally
                {
                    settings.Current.UpdateManifestUrl = originalUrl;
                    settings.Current.UpdateChannel = originalChannel;
                    settings.Current.CheckForUpdatesOnStartup = originalStartup;
                    settings.Current.UpdateAutoDownload = originalAutoDownload;
                    settings.Save();
                }
            });

            if (!string.IsNullOrWhiteSpace(manifestPath))
            {
                CheckRealManifest(manifestPath, report, failures);
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

    /// <summary>
    /// 校验构建脚本产出的真实发布清单（T6 验收）：字段完整 → 本地安装包体积与 SHA-256 对得上 →
    /// 能被程序真的下载校验一遍 → 篡改哈希会被拒。
    /// </summary>
    private static void CheckRealManifest(string manifestPath, StringBuilder report, List<string> failures)
    {
        try
        {
            report.AppendLine(new string('-', 68));
            report.AppendLine($"发布清单验证：{manifestPath}");

            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException($"清单不存在：{manifestPath}");
            }

            var manifest = UpdateManifest.FromJson(File.ReadAllText(manifestPath));
            if (manifest is null)
            {
                throw new InvalidOperationException("清单无法解析（字段名/类型要与 UpdateManifest 一致）。");
            }

            if (string.IsNullOrWhiteSpace(manifest.Version) || !SemanticVersion.TryParse(manifest.Version, out _))
            {
                throw new InvalidOperationException($"版本号不可解析：{manifest.Version}");
            }

            if (manifest.Channel != UpdateChannels.Stable && manifest.Channel != UpdateChannels.Beta)
            {
                throw new InvalidOperationException($"通道非法：{manifest.Channel}（应为 stable 或 beta）。");
            }

            if (manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidOperationException($"SHA-256 不是 64 位十六进制：{manifest.Sha256}");
            }

            if (manifest.Size <= 0)
            {
                throw new InvalidOperationException($"体积非法：{manifest.Size}");
            }

            report.AppendLine($"[PASS] 字段完整：版本 {manifest.Version}，通道 {manifest.Channel}，体积 {manifest.Size / 1024 / 1024.0:0.#} MB，SHA-256 {manifest.Sha256[..16]}…");

            if (UpdateService.ResolveSourceKind(manifest.Url) != UpdateSourceKind.LocalFile)
            {
                report.AppendLine($"[SKIP] 下载校验：清单指向 https（{manifest.Url}），本地自检不访问网络。");
                return;
            }

            string localPayload = UpdateService.ToLocalPath(manifest.Url);
            if (!File.Exists(localPayload))
            {
                throw new FileNotFoundException($"清单里的本地安装包不存在：{localPayload}");
            }

            long actualSize = new FileInfo(localPayload).Length;
            if (actualSize != manifest.Size)
            {
                throw new InvalidOperationException($"体积不符：清单 {manifest.Size}，实际 {actualSize}。");
            }

            if (!Sha256Util.Matches(manifest.Sha256, localPayload, out string actualHash))
            {
                throw new InvalidOperationException($"SHA-256 不符：清单 {manifest.Sha256}，实际 {actualHash}。");
            }

            report.AppendLine($"[PASS] 本地安装包与清单一致：{Path.GetFileName(localPayload)}（{actualSize / 1024 / 1024.0:0.#} MB）");

            // 经程序自己的下载路径再验一遍（含体积与哈希校验；失败会删除临时文件）
            var service = new UpdateService(manifestPath, manifest.Channel);
            string downloaded = service.DownloadAsync(manifest).GetAwaiter().GetResult();
            try
            {
                if (!Sha256Util.Matches(manifest.Sha256, downloaded, out string downloadedHash))
                {
                    throw new InvalidOperationException($"下载后哈希不符：{downloadedHash}");
                }

                report.AppendLine($"[PASS] 经更新服务下载 + 校验通过：{downloaded}");
            }
            finally
            {
                try
                {
                    if (File.Exists(downloaded))
                    {
                        File.Delete(downloaded);
                    }
                }
                catch
                {
                    // 清理失败不影响结论
                }
            }

            // 清单本身不可更新（版本不高于当前程序）时，理由必须说清楚，而不是静默失败
            var current = new UpdateService(manifestPath, manifest.Channel).CheckAsync().GetAwaiter().GetResult();
            if (!current.CanUpdate && string.IsNullOrWhiteSpace(current.Message))
            {
                throw new InvalidOperationException("不可更新时没有给出原因。");
            }

            report.AppendLine($"[PASS] 检查结论可读：{current.Message}");
        }
        catch (Exception ex)
        {
            failures.Add("发布清单验证");
            report.AppendLine($"[FAIL] 发布清单验证：{ex.GetType().Name}: {ex.Message}");
        }
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
