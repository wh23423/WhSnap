using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhSnap.Services;

/// <summary>
/// 用户配置。目标（见规划 §6）：能开放给用户调的项都放在这里，
/// 并且 UI 与文件双向可达，不做"只有改 JSON 才能调"的隐藏项。
/// </summary>
internal sealed class AppSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>触发截图的热键，格式如 "F1" / "Ctrl+Shift+A"。</summary>
    public string Hotkey { get; set; } = "F1";

    /// <summary>留空则用「图片\WhSnap」。</summary>
    public string SaveDirectory { get; set; } = string.Empty;

    /// <summary>支持 {date} {time} {w} {h} 占位符。</summary>
    public string FileNameTemplate { get; set; } = "WhSnap_{date}_{time}_{w}x{h}";

    public bool CopyOnCapture { get; set; } = true;

    public bool SaveOnCapture { get; set; } = true;

    public bool AutoStart { get; set; } = true;

    /// <summary>遮罩未选中区域的暗度，0-90。</summary>
    public int OverlayDimOpacity { get; set; } = 45;

    /// <summary>
    /// 采集后端：auto（WGC → DXGI → GDI 自动降级）/ wgc / dxgi / gdi。
    /// 默认 auto；某个后端在特定机器上表现异常时可手动固定。
    /// </summary>
    public string CaptureBackend { get; set; } = "auto";

    /// <summary>另存为/快捷保存 JPEG 时的质量（10-100）。</summary>
    public int JpegQuality { get; set; } = 90;

    // ---- 贴图（M2）----

    /// <summary>截图确认后直接贴图（Enter 一次完成复制+保存+贴图，见 §2.1「确认后三选一」）。</summary>
    public bool PinOnCapture { get; set; }

    /// <summary>新贴图的默认不透明度（0-100）。</summary>
    public int PinDefaultOpacity { get; set; } = 100;

    /// <summary>新贴图默认是否鼠标穿透（默认关闭，否则新贴图点不动）。</summary>
    public bool PinClickThroughDefault { get; set; }

    /// <summary>滚轮一格的缩放步进。</summary>
    public double PinZoomStep { get; set; } = 1.1;

    /// <summary>拖动结束时是否吸附到显示器边缘。</summary>
    public bool PinSnapToEdges { get; set; } = true;

    /// <summary>边缘吸附容差（物理像素）。</summary>
    public int PinSnapTolerance { get; set; } = 12;

    /// <summary>新贴图是否置顶于其它贴图。</summary>
    public bool PinTopmostNew { get; set; } = true;

    /// <summary>自动备份贴图（崩溃/重启后可恢复）。</summary>
    public bool PinBackupEnabled { get; set; } = true;

    /// <summary>备份去抖间隔（秒）：贴图变动后至少等这么久才落盘一次。</summary>
    public int PinBackupIntervalSeconds { get; set; } = 5;

    /// <summary>保留多少次备份会话。</summary>
    public int PinBackupRetention { get; set; } = 10;

    /// <summary>启动时恢复上次的贴图。</summary>
    public bool RestorePinsOnStartup { get; set; } = true;

    /// <summary>监听剪贴板，「复制即贴图」。</summary>
    public bool ClipboardWatchEnabled { get; set; }

    // ---- 文本/颜色贴图外观 ----

    public string TextPinFontFamily { get; set; } = "Microsoft YaHei UI";

    public double TextPinFontSize { get; set; } = 14;

    /// <summary>文本贴图自动换行的最大宽度（物理像素）。</summary>
    public int TextPinMaxWidth { get; set; } = 480;

    /// <summary>文本贴图最大高度，超出按省略号截断。</summary>
    public int TextPinMaxHeight { get; set; } = 1200;

    public double TextPinPadding { get; set; } = 12;

    public string TextPinForeground { get; set; } = "#F2F2F2";

    public string TextPinBackground { get; set; } = "#1B1B1B";

    [JsonIgnore]
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

internal sealed class SettingsService
{
    public static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WhSnap");

    public static string ConfigFilePath => Path.Combine(ConfigDirectory, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public string? LastError { get; private set; }

    public void Load()
    {
        LastError = null;

        try
        {
            if (!File.Exists(ConfigFilePath))
            {
                Current = new AppSettings();
                Save();
                return;
            }

            string json = File.ReadAllText(ConfigFilePath);
            Current = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.SerializerOptions) ?? new AppSettings();

            // schema 迁移：新增项全部带默认值，因此老配置只需把版本号抬到当前值即可。
            // 真正需要"改数据"的迁移在 M3 起按版本号逐级处理。
            if (Current.SchemaVersion < AppSettings.CurrentSchemaVersion)
            {
                Log.Info($"配置 schema {Current.SchemaVersion} → {AppSettings.CurrentSchemaVersion}：新增项使用默认值。");
                Current.SchemaVersion = AppSettings.CurrentSchemaVersion;
                Save();
            }
        }
        catch (Exception ex)
        {
            // 配置损坏不能让程序起不来：退回默认值并保留证据
            LastError = $"配置读取失败，已使用默认配置：{ex.Message}";
            Log.Warn(LastError);
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            string json = JsonSerializer.Serialize(Current, AppSettings.SerializerOptions);
            File.WriteAllText(ConfigFilePath, json);
        }
        catch (Exception ex)
        {
            LastError = $"配置写入失败：{ex.Message}";
            Log.Warn(LastError);
        }
    }
}
