using WhSnap.Core;

namespace WhSnap.Services;

/// <summary>
/// 更新链路的运行时入口：把已经实现并被 <c>--update-test</c> 覆盖过的 <see cref="UpdateService"/>
/// 接到**启动 / 托盘 / 设置维护页**这三条用户可达路径上。
///
/// 三条硬约束（对应进度快照 §7 的坑）：
///   * **绝不在 UI 线程上做网络或磁盘 I/O**：检查与下载都丢到线程池，调用方 <c>await</c> 即可，
///     界面线程只负责把结果写进气泡/文字；
///   * **检查与下载都带超时**：失败一律变成"不可更新 + 可读的原因"，不把异常抛到界面；
///   * **配置单一来源**：清单地址 / 通道 / 启动检查 / 自动下载全部读自 <see cref="AppSettings"/>，
///     在设置里改完即时生效（不需要重启）。
/// </summary>
internal sealed class UpdateCoordinator
{
    /// <summary>单次检查或下载的超时（秒）。启动检查不能拖着界面，宁可提示"稍后再试"。</summary>
    public const int TimeoutSeconds = 30;

    private readonly SettingsService _settings;

    public UpdateCoordinator(SettingsService settings) => _settings = settings;

    /// <summary>当前程序版本（设置维护页与「关于」显示的都是它）。</summary>
    public string CurrentVersion => UpdateService.GetCurrentVersion();

    public string ManifestSource => _settings.Current.UpdateManifestUrl;

    public string Channel => UpdateChannels.Parse(_settings.Current.UpdateChannel);

    public bool StartupCheckEnabled => _settings.Current.CheckForUpdatesOnStartup;

    public bool AutoDownloadEnabled => _settings.Current.UpdateAutoDownload;

    /// <summary>按当前配置构造更新服务——四个更新配置项真正被消费的地方。</summary>
    public UpdateService CreateService() => UpdateService.FromSettings(_settings.Current);

    /// <summary>
    /// 检查更新。整体跑在线程池上并带超时；返回值永远非 null：
    /// 网络/解析/超时失败都表现为 <see cref="UpdateCheckResult.CanUpdate"/> 为 false + 可读原因。
    /// </summary>
    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var service = CreateService();

        return Task.Run(
            async () =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
                return await service.CheckAsync(timeout.Token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>
    /// 下载安装包到临时目录并校验 SHA-256。校验不通过会抛异常——
    /// 由调用方决定怎么提示用户，但**绝不**把哈希对不上的可执行文件交给用户运行。
    /// </summary>
    public Task<string> DownloadAsync(
        UpdateManifest manifest,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var service = CreateService();
        return Task.Run(() => service.DownloadAsync(manifest, progress, cancellationToken), cancellationToken);
    }

    /// <summary>启动安装器（自动更新用静默参数）；调用方随后应退出自身，让安装器替换程序文件。</summary>
    public static void LaunchInstaller(string installerPath) => UpdateService.LaunchInstaller(installerPath);
}
