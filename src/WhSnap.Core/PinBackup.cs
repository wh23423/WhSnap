using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhSnap.Core;

/// <summary>物理矩形的文本编解码（"x,y,w,h"），用于备份清单里的桌面拓扑快照。</summary>
public static class RectCodec
{
    public static string Format(PhysicalRect rect) => $"{rect.X},{rect.Y},{rect.Width},{rect.Height}";

    public static bool TryParse(string? text, out PhysicalRect rect)
    {
        rect = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4
            || !int.TryParse(parts[0], out int x)
            || !int.TryParse(parts[1], out int y)
            || !int.TryParse(parts[2], out int width)
            || !int.TryParse(parts[3], out int height))
        {
            return false;
        }

        rect = new PhysicalRect(x, y, width, height);
        return true;
    }
}

/// <summary>一张贴图在备份清单中的记录。图像本体以 PNG 落在同目录，这里只存几何与显示状态。</summary>
public sealed record PinBackupEntry
{
    /// <summary>相对备份目录的 PNG 文件名。</summary>
    public string File { get; init; } = string.Empty;

    public int X { get; init; }

    public int Y { get; init; }

    public double Zoom { get; init; } = 1.0;

    public PinRotation Rotation { get; init; }

    public bool FlipHorizontal { get; init; }

    public bool FlipVertical { get; init; }

    public int Opacity { get; init; } = 100;

    public bool Thumbnail { get; init; }

    public bool ClickThrough { get; init; }

    public int ZIndex { get; init; }

    public static PinBackupEntry FromState(PinState state, string file, int zIndex) => new()
    {
        File = file,
        X = state.Position.X,
        Y = state.Position.Y,
        Zoom = state.Zoom,
        Rotation = state.Rotation,
        FlipHorizontal = state.FlipHorizontal,
        FlipVertical = state.FlipVertical,
        Opacity = state.Opacity,
        Thumbnail = state.Thumbnail,
        ClickThrough = state.ClickThrough,
        ZIndex = zIndex,
    };

    /// <summary>还原成贴图状态（图像尺寸由实际解码结果补入）。</summary>
    public PinState ToState(int sourceWidth, int sourceHeight) => new()
    {
        Position = new PhysicalPoint(X, Y),
        SourceWidth = Math.Max(sourceWidth, 1),
        SourceHeight = Math.Max(sourceHeight, 1),
        Zoom = Zoom,
        Rotation = Rotation,
        FlipHorizontal = FlipHorizontal,
        FlipVertical = FlipVertical,
        Opacity = Opacity,
        Thumbnail = Thumbnail,
        ClickThrough = ClickThrough,
    };
}

/// <summary>备份清单（一次会话一个目录：images/*.png + manifest.json）。</summary>
public sealed record PinBackupManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>保存时的虚拟桌面矩形，"x,y,w,h"。</summary>
    public string Desktop { get; init; } = string.Empty;

    public DateTime SavedAtUtc { get; init; } = DateTime.UtcNow;

    public List<PinBackupEntry> Pins { get; init; } = [];

    [JsonIgnore]
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static PinBackupManifest? FromJson(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<PinBackupManifest>(json, SerializerOptions);

            // schemaVersion 高于当前版本时不猜测语义，直接放弃恢复
            if (manifest is null || manifest.SchemaVersion > CurrentSchemaVersion)
            {
                return null;
            }

            manifest.Pins.Sort((a, b) => a.ZIndex.CompareTo(b.ZIndex));
            return manifest;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>备份目录的保留策略：只保留最近 N 次会话，其余返回待删除（纯逻辑，可单测）。</summary>
public static class PinBackupRetention
{
    /// <summary>
    /// sessions 必须按“最新在前”排序；返回需要删除的会话目录（超出保留数量的部分）。
    /// keep 小于 1 时视为 1（至少保留一次会话，避免把用户的贴图全删光）。
    /// </summary>
    public static IReadOnlyList<string> Victims(IReadOnlyList<string> sessionsNewestFirst, int keep)
    {
        ArgumentNullException.ThrowIfNull(sessionsNewestFirst);

        keep = Math.Max(keep, 1);
        return sessionsNewestFirst.Count <= keep
            ? []
            : sessionsNewestFirst.Skip(keep).ToList();
    }
}
