using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using WhSnap.Core;

namespace WhSnap.Services;

/// <summary>清单与安装包的传输方式。本地路径用于离线分发（共享目录）与自检。</summary>
internal enum UpdateSourceKind
{
    Http,
    LocalFile,
}

internal sealed record UpdateCheckResult(
    bool CanUpdate,
    string Message,
    UpdateManifest? Manifest,
    string Source)
{
    public static UpdateCheckResult NotAvailable(string message, string source) => new(false, message, null, source);
}

/// <summary>
/// 更新服务。职责边界刻意划清：
///   * **判定**（版本比较、通道规则、最低可升级版本）全在 Core 的 <see cref="UpdateRules"/>，纯函数可单测；
///   * **传输**（https / 本地路径）与**落盘校验**（SHA-256）在这里；
///   * **安装**只负责构造命令并启动安装包，替换文件由安装器自己完成（per-user 安装免提权）。
///
/// 不做任何后台上报：只读取一份很小的 update.json，且可被"启动时检查更新"开关完全关闭。
/// </summary>
internal sealed class UpdateService
{
    /// <summary>默认清单地址：GitHub Releases 最新版的 update.json 资产。</summary>
    public const string DefaultManifestUrl =
        "https://github.com/wh23423/WhSnap/releases/latest/download/update.json";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public UpdateService(string? manifestSource, string channel, string? currentVersion = null)
    {
        Source = string.IsNullOrWhiteSpace(manifestSource) ? DefaultManifestUrl : manifestSource.Trim();
        Channel = UpdateChannels.Parse(channel);
        CurrentVersion = string.IsNullOrWhiteSpace(currentVersion) ? GetCurrentVersion() : currentVersion.Trim();
    }

    public string Source { get; }

    public string Channel { get; }

    public string CurrentVersion { get; }

    /// <summary>
    /// 按用户配置构造（清单地址 / 通道）。更新相关的四个配置项必须从这里进入代码路径，
    /// 否则配置面上有开关、实际没人读——这正是 M6 第 4 项要补的缺口。
    /// </summary>
    public static UpdateService FromSettings(AppSettings settings) =>
        new(settings.UpdateManifestUrl, settings.UpdateChannel);

    /// <summary>当前程序版本（取自程序集信息版本，去掉 +build 元数据）。</summary>
    public static string GetCurrentVersion()
    {
        var assembly = typeof(UpdateService).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public static UpdateSourceKind ResolveSourceKind(string source) =>
        source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? UpdateSourceKind.Http
            : UpdateSourceKind.LocalFile;

    /// <summary>读取并判定更新清单。任何网络/解析失败都返回"不可更新 + 原因"，不抛异常。</summary>
    /// <remarks>
    /// 所有 await 都带 ConfigureAwait(false)：调用方可能是 WPF UI 线程上用
    /// <c>GetAwaiter().GetResult()</c> 同步等待（自检命令、启动时检查），
    /// 若续体需要回到被阻塞的 UI 线程就会直接死锁。
    /// </remarks>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string? json = await ReadTextAsync(Source, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return UpdateCheckResult.NotAvailable($"更新清单不可读：{Source}", Source);
            }

            var manifest = UpdateManifest.FromJson(json);
            if (manifest is null)
            {
                return UpdateCheckResult.NotAvailable("更新清单格式无法解析。", Source);
            }

            var decision = UpdateRules.Evaluate(CurrentVersion, manifest, Channel);
            return new UpdateCheckResult(decision.CanUpdate, decision.Reason, manifest, Source);
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.NotAvailable($"检查更新失败：{ex.GetType().Name}: {ex.Message}", Source);
        }
    }

    /// <summary>安装包下载到临时目录时的目标路径（暴露出来便于自检断言"失败后不残留"）。</summary>
    public static string GetDownloadPath(string version) =>
        Path.Combine(Path.GetTempPath(), $"WhSnap-{version}-setup.exe");

    /// <summary>
    /// 下载安装包到临时目录并校验 SHA-256。校验不通过会删除文件并抛异常——
    /// **绝不允许把一个哈希对不上的可执行文件交给用户运行**。
    /// </summary>
    public async Task<string> DownloadAsync(
        UpdateManifest manifest,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifest.Url))
        {
            throw new InvalidOperationException("更新清单没有提供下载地址。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            throw new InvalidOperationException("更新清单没有提供 SHA-256，拒绝下载。");
        }

        string target = GetDownloadPath(manifest.Version);

        if (File.Exists(target))
        {
            File.Delete(target);
        }

        try
        {
            await CopyPayloadAsync(manifest.Url, target, progress, cancellationToken).ConfigureAwait(false);

            if (manifest.Size > 0)
            {
                long actualSize = new FileInfo(target).Length;
                if (actualSize != manifest.Size)
                {
                    throw new InvalidDataException($"下载体积不符：清单 {manifest.Size} 字节，实际 {actualSize} 字节。");
                }
            }

            if (!Sha256Util.Matches(manifest.Sha256, target, out string actualHash))
            {
                throw new InvalidDataException($"SHA-256 校验失败：清单 {manifest.Sha256}，实际 {actualHash}。");
            }

            progress?.Report(1.0);
            Log.Info($"更新包已下载并校验通过：{target}（{manifest.Version}）");
            return target;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>静默安装参数：不弹向导、不弹消息框、不自动重启。</summary>
    public static string BuildInstallerArguments() => "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

    public static ProcessStartInfo BuildInstallerStartInfo(string installerPath) =>
        new(installerPath, BuildInstallerArguments())
        {
            UseShellExecute = true,
        };

    /// <summary>启动安装包。调用方随后应自行退出，让安装器替换正在运行的程序文件。</summary>
    public static void LaunchInstaller(string installerPath)
    {
        Log.Info($"启动更新安装：{installerPath}");
        Process.Start(BuildInstallerStartInfo(installerPath));
    }

    private async Task CopyPayloadAsync(
        string url,
        string target,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (ResolveSourceKind(url) == UpdateSourceKind.LocalFile)
        {
            string source = ToLocalPath(url);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException($"安装包不存在：{source}");
            }

            var buffer = new byte[81920];
            long total = new FileInfo(source).Length;
            long copied = 0;

            using var input = File.OpenRead(source);
            using var output = File.Create(target);

            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                if (total > 0)
                {
                    progress?.Report((double)copied / total);
                }
            }

            return;
        }

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? contentLength = response.Content.Headers.ContentLength;
        long received = 0;

        await using var payload = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(target);

        var chunk = new byte[81920];
        int count;
        while ((count = await payload.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            received += count;
            if (contentLength is > 0)
            {
                progress?.Report((double)received / contentLength.Value);
            }
        }
    }

    private static async Task<string?> ReadTextAsync(string source, CancellationToken cancellationToken)
    {
        if (ResolveSourceKind(source) == UpdateSourceKind.LocalFile)
        {
            string path = ToLocalPath(source);
            return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null;
        }

        using var response = await Http.GetAsync(source, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把 file:///C:/x 或普通路径统一成本地文件系统路径。</summary>
    public static string ToLocalPath(string source)
    {
        if (source.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return uri.LocalPath;
        }

        return source;
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
        catch (Exception ex)
        {
            Log.Warn($"清理下载文件失败：{ex.Message}");
        }
    }
}
