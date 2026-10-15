using System.Text.Json;
using System.Text.Json.Serialization;
using WhSnap.Core;

namespace WhSnap.Services;

/// <summary>
/// 用户配置。目标（见规划 §6）：能开放给用户调的项都放在这里，
/// 并且 UI 与文件双向可达，不做"只有改 JSON 才能调"的隐藏项。
/// </summary>
internal sealed class AppSettings
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// 【已迁移】旧版单热键字段。schema 3 起改用 <see cref="Hotkeys"/>（动作 id → 键位），
    /// 这里仅保留用于读取老配置并迁移，程序不再写它。
    /// </summary>
    public string Hotkey { get; set; } = "F1";

    /// <summary>各动作的键位覆盖（动作 id → 键位文本）。未列出的动作用内置默认值。</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>用户明确停用的动作（冲突时选择"仍保存但不生效"）。</summary>
    public List<string> DisabledHotkeys { get; set; } = [];

    /// <summary>
    /// 冲突策略：block = 阻断保存（默认）；save-disabled = 仍保存但停用该热键。
    /// 无论哪种都**绝不静默失败**——冲突一定会显示出来（§5.7）。
    /// </summary>
    public string HotkeyConflictPolicy { get; set; } = "block";

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

    /// <summary>当前生效的热键绑定（默认值 + 用户覆盖 + 停用名单）。</summary>
    public IReadOnlyList<Core.HotkeyBinding> ResolveHotkeys() =>
        Core.HotkeyBindings.Resolve(Current.Hotkeys, Current.DisabledHotkeys);

    /// <summary>全局作用域的热键绑定。M3 起全部配置项都在 <see cref="Current"/> 上。</summary>
    public IReadOnlyList<Core.HotkeyBinding> ResolveGlobalHotkeys() =>
        Core.HotkeyBindings.Resolve(Current.Hotkeys, Current.DisabledHotkeys, Core.HotkeyScope.Global);

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
            Current.Hotkeys ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Current.DisabledHotkeys ??= [];

            // schema 迁移：新增项全部带默认值，老配置只需补"语义发生变化"的部分。
            if (Current.SchemaVersion < AppSettings.CurrentSchemaVersion)
            {
                Migrate(Current, Current.SchemaVersion);
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

    /// <summary>按版本逐级迁移（每级只做自己那一步，便于以后继续加）。</summary>
    private static void Migrate(AppSettings settings, int fromVersion)
    {
        // v2 → v3：单个 Hotkey 字段改为按动作的键位表
        if (fromVersion < 3
            && !string.IsNullOrWhiteSpace(settings.Hotkey)
            && !settings.Hotkey.Equals("F1", StringComparison.OrdinalIgnoreCase)
            && !settings.Hotkeys.ContainsKey("capture.region"))
        {
            settings.Hotkeys["capture.region"] = settings.Hotkey.Trim();
            Log.Info($"配置迁移：旧热键 {settings.Hotkey} → 动作 capture.region。");
        }

        Log.Info($"配置 schema {fromVersion} → {AppSettings.CurrentSchemaVersion}：新增项使用默认值。");
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

    /// <summary>导出当前配置（备份、求助、导入导出用）。</summary>
    public string ExportJson() => JsonSerializer.Serialize(Current, AppSettings.SerializerOptions);

    /// <summary>
    /// 导入配置：逐项校验后写回，未知项与非法值都会被**报告**而不是静默丢弃（§6 第 4 条）。
    /// 热键表与停用名单单独处理（它们是字典/列表，不在 schema 里）。
    /// </summary>
    public SettingsImportResult ImportJson(string json)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var hotkeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var disabled = new List<string>();

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new SettingsImportResult([], [], ["根节点不是 JSON 对象"]);
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    // 程序自己的簿记字段：导出文件里会带上，导入时静默跳过（不是"未知项"）
                    case nameof(AppSettings.SchemaVersion):
                    case nameof(AppSettings.Hotkey):
                        break;

                    case nameof(AppSettings.Hotkeys) when property.Value.ValueKind == JsonValueKind.Object:
                        foreach (var hotkey in property.Value.EnumerateObject())
                        {
                            hotkeys[hotkey.Name] = hotkey.Value.GetString() ?? string.Empty;
                        }

                        break;

                    case nameof(AppSettings.DisabledHotkeys) when property.Value.ValueKind == JsonValueKind.Array:
                        disabled.AddRange(property.Value.EnumerateArray()
                            .Select(item => item.GetString())
                            .Where(item => !string.IsNullOrWhiteSpace(item))
                            .Select(item => item!));
                        break;

                    default:
                        values[property.Name] = property.Value.ValueKind switch
                        {
                            JsonValueKind.String => property.Value.GetString(),
                            JsonValueKind.Number => property.Value.GetRawText(),
                            JsonValueKind.True => "true",
                            JsonValueKind.False => "false",
                            JsonValueKind.Null => null,
                            _ => null,
                        };
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            return new SettingsImportResult([], [], [$"JSON 解析失败：{ex.Message}"]);
        }

        var result = SettingsImportValidator.Validate(values);
        var applied = new List<string>();

        foreach (string key in result.Applied)
        {
            if (SettingsBinder.Write(Current, key, values[key], out _))
            {
                applied.Add(key);
            }
        }

        // 热键：只接受动作表里存在的 id
        int hotkeysApplied = 0;
        foreach (var (actionId, keys) in hotkeys)
        {
            if (HotkeyCatalog.Find(actionId) is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(keys))
            {
                Current.Hotkeys.Remove(actionId);
            }
            else if (HotkeySpec.TryParse(keys, out _, out _))
            {
                Current.Hotkeys[actionId] = keys.Trim();
                hotkeysApplied++;
            }
        }

        Current.DisabledHotkeys = disabled.Where(id => HotkeyCatalog.Find(id) is not null).ToList();
        Save();

        if (hotkeysApplied > 0)
        {
            applied.Add($"Hotkeys（{hotkeysApplied} 项）");
        }

        Log.Info($"配置导入：{string.Join("、", applied)}；忽略 {result.Unknown.Count} 项，非法 {result.Invalid.Count} 项。");
        return new SettingsImportResult(applied, result.Unknown, result.Invalid);
    }
}
