using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhSnap.Services;

/// <summary>
/// 用户配置。目标（见规划 §6）：能开放给用户调的项都放在这里，
/// 并且 UI 与文件双向可达，不做"只有改 JSON 才能调"的隐藏项。
/// </summary>
internal sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

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

    /// <summary>预览版固定为 gdi；M1 起支持 wgc / dxgi / auto。</summary>
    public string CaptureBackend { get; set; } = "gdi";

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
