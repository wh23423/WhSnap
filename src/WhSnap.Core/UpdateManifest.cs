using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhSnap.Core;

/// <summary>
/// 语义化版本（支持 <c>1.2.3-preview.4</c> 这种预览标记）。
/// 规则按 semver 的常见约定：主.次.补丁 逐级比较；**有预览标记的小于同号正式版**
/// （1.0.0-preview.1 &lt; 1.0.0），预览标记之间逐段比较（数字按数值、其余按序数）。
/// </summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string PreRelease = "")
    : IComparable<SemanticVersion>
{
    public static SemanticVersion Zero => new(0, 0, 0);

    public bool IsPreRelease => !string.IsNullOrEmpty(PreRelease);

    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = Zero;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text.Trim().TrimStart('v', 'V');
        string preRelease = string.Empty;
        int dash = value.IndexOf('-');

        if (dash >= 0)
        {
            preRelease = value[(dash + 1)..].Trim();
            value = value[..dash];
        }

        // 允许 "1.2" 这种省略补丁号的写法
        var parts = value.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        var numbers = new int[3];

        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], preRelease);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        int major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        int minor = Minor.CompareTo(other.Minor);
        if (minor != 0)
        {
            return minor;
        }

        int patch = Patch.CompareTo(other.Patch);
        if (patch != 0)
        {
            return patch;
        }

        // 正式版 > 预览版
        if (!IsPreRelease && !other.IsPreRelease)
        {
            return 0;
        }

        if (!IsPreRelease)
        {
            return 1;
        }

        if (!other.IsPreRelease)
        {
            return -1;
        }

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static int ComparePreRelease(string left, string right)
    {
        var leftParts = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int count = Math.Max(leftParts.Length, rightParts.Length);

        for (int i = 0; i < count; i++)
        {
            // 段数少的一方更小（1.0.0-preview < 1.0.0-preview.1）
            if (i >= leftParts.Length)
            {
                return -1;
            }

            if (i >= rightParts.Length)
            {
                return 1;
            }

            bool leftNumeric = int.TryParse(leftParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int leftNumber);
            bool rightNumeric = int.TryParse(rightParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int rightNumber);

            int result = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1, // 数字标识符优先级低于字母
                (false, true) => 1,
                _ => string.CompareOrdinal(leftParts[i], rightParts[i]),
            };

            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }

    public bool IsNewerThan(SemanticVersion other) => CompareTo(other) > 0;

    public override string ToString() =>
        IsPreRelease ? $"{Major}.{Minor}.{Patch}-{PreRelease}" : $"{Major}.{Minor}.{Patch}";
}

/// <summary>
/// 更新清单（发布时由构建脚本生成，随 Release 一起上传）。
/// 只放"够用"的字段：版本、通道、下载地址、SHA-256、体积、更新说明、最低可升级版本。
/// </summary>
public sealed record UpdateManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Version { get; init; } = "0.0.0";

    /// <summary>发布通道：stable / beta。</summary>
    public string Channel { get; init; } = UpdateChannels.Stable;

    /// <summary>安装包下载地址（http/https，或本地/共享目录路径）。</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>安装包 SHA-256（小写十六进制）。</summary>
    public string Sha256 { get; init; } = string.Empty;

    public long Size { get; init; }

    public DateTime PublishedAtUtc { get; init; } = DateTime.UtcNow;

    public string Notes { get; init; } = string.Empty;

    /// <summary>
    /// 最低"可以从这个版本直接升级"的版本号；低于它必须先手动安装一次（例如数据布局变化）。
    /// 留空表示不限制。
    /// </summary>
    public string MinimumVersion { get; init; } = string.Empty;

    [JsonIgnore]
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static UpdateManifest? FromJson(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, SerializerOptions);

            if (manifest is null || manifest.SchemaVersion > CurrentSchemaVersion)
            {
                return null;
            }

            return manifest;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public static class UpdateChannels
{
    public const string Stable = "stable";
    public const string Beta = "beta";

    public static string Parse(string? name) =>
        string.Equals(name?.Trim(), Beta, StringComparison.OrdinalIgnoreCase) ? Beta : Stable;

    /// <summary>稳定通道只接受稳定版；测试通道同时接受测试版与稳定版（不会因此降级，因为还要求版本更新）。</summary>
    public static bool Accepts(string requestedChannel, string manifestChannel)
    {
        string requested = Parse(requestedChannel);
        string offered = Parse(manifestChannel);

        return requested == Beta || offered == Stable;
    }
}

/// <summary>升级可行性判定（纯逻辑，可单测）。</summary>
public static class UpdateRules
{
    public sealed record Decision(bool CanUpdate, string Reason)
    {
        public bool IsBlocked => !CanUpdate;
    }

    public static Decision Evaluate(string currentVersion, UpdateManifest manifest, string requestedChannel)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!SemanticVersion.TryParse(manifest.Version, out var offered))
        {
            return new Decision(false, $"更新清单里的版本号无法解析：{manifest.Version}");
        }

        if (!SemanticVersion.TryParse(currentVersion, out var current))
        {
            // 当前版本号异常时按"可以升级"处理：能修好总比卡住好
            current = SemanticVersion.Zero;
        }

        if (!offered.IsNewerThan(current))
        {
            return new Decision(false, $"当前已是最新版本（{current} ≥ {offered}）。");
        }

        if (!UpdateChannels.Accepts(requestedChannel, manifest.Channel))
        {
            return new Decision(false, $"该版本属于 {manifest.Channel} 通道，当前通道 {requestedChannel} 不接收。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Url))
        {
            return new Decision(false, "更新清单里没有下载地址。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            return new Decision(false, "更新清单里没有 SHA-256，出于安全考虑拒绝自动更新。");
        }

        if (!string.IsNullOrWhiteSpace(manifest.MinimumVersion))
        {
            if (!SemanticVersion.TryParse(manifest.MinimumVersion, out var minimum))
            {
                return new Decision(false, $"更新清单里的最低版本号无法解析：{manifest.MinimumVersion}");
            }

            if (current.CompareTo(minimum) < 0)
            {
                return new Decision(false, $"当前版本 {current} 低于最低可升级版本 {minimum}，请手动安装一次。");
            }
        }

        return new Decision(true, $"发现新版本 {offered}（当前 {current}）：{manifest.Notes}");
    }
}

/// <summary>SHA-256 计算与校验（更新包完整性、诊断包清单都用它）。</summary>
public static class Sha256Util
{
    public static string Compute(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    public static string ComputeFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static bool Matches(string expectedHex, string path, out string actualHex)
    {
        actualHex = ComputeFile(path);
        return string.Equals(expectedHex?.Trim(), actualHex, StringComparison.OrdinalIgnoreCase);
    }
}
