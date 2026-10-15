using System.IO;
using System.Windows.Media.Imaging;
using WhSnap.Core;
using WhSnap.Services;

namespace WhSnap.Pin;

/// <summary>
/// 贴图自动备份 / 崩溃与重启恢复。
///
/// 目录结构：<c>%APPDATA%\WhSnap\pins\&lt;yyyyMMdd-HHmmss&gt;\manifest.json + pin-000.png …</c>
/// 会话目录名就是时间戳，因此**按名字倒序 = 按时间倒序**，恢复时直接取最新一个即可，不需要索引文件。
/// 保存顺序是先写图片、最后写清单：清单存在即代表这一批图片完整（崩溃时不会读到半套）。
/// </summary>
internal sealed class PinBackupStore
{
    private const string ManifestFileName = "manifest.json";

    public PinBackupStore(string? rootDirectory = null)
    {
        Root = rootDirectory ?? Path.Combine(SettingsService.ConfigDirectory, "pins");
    }

    public string Root { get; }

    /// <summary>该贴图是否已经在本会话目录里落过盘（命中则跳过 PNG 重编码，备份开销降到只写清单）。</summary>
    private string? _currentSession;

    public string? CurrentSession => _currentSession;

    /// <summary>把当前所有贴图写成一个新会话目录（或复用已有会话目录）。返回落盘目录，失败返回 null。</summary>
    public string? Save(IReadOnlyList<PinWindow> pins, PhysicalRect desktop, int retention)
    {
        ArgumentNullException.ThrowIfNull(pins);

        try
        {
            string session = _currentSession ?? DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string directory = Path.Combine(Root, session);
            Directory.CreateDirectory(directory);

            var entries = new List<PinBackupEntry>();
            int index = 0;

            // 按 Z 序保存，恢复后层级一致
            foreach (var pin in pins.OrderBy(p => p.ZIndex).ToList())
            {
                string fileName = $"pin-{index:000}.png";
                string path = Path.Combine(directory, fileName);

                if (pin.BackupFileName != fileName || !File.Exists(path))
                {
                    SaveService.SaveImage(pin.Source, path);
                    pin.BackupFileName = fileName;
                }

                entries.Add(PinBackupEntry.FromState(pin.State, fileName, index));
                index++;
            }

            var manifest = new PinBackupManifest
            {
                Desktop = RectCodec.Format(desktop),
                SavedAtUtc = DateTime.UtcNow,
                Pins = entries,
            };

            // 清单最后写，并且先写临时文件再原子替换，避免崩溃留下半截 JSON
            string manifestPath = Path.Combine(directory, ManifestFileName);
            string tempPath = manifestPath + ".tmp";
            File.WriteAllText(tempPath, manifest.ToJson());
            File.Move(tempPath, manifestPath, overwrite: true);

            _currentSession = session;
            Prune(retention);
            return directory;
        }
        catch (Exception ex)
        {
            Log.Warn($"贴图备份失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>读取最近一次备份。返回 null 表示没有可用备份或清单损坏。</summary>
    public (PinBackupManifest Manifest, string Directory)? LoadLatest()
    {
        foreach (string session in ListSessions())
        {
            string directory = Path.Combine(Root, session);
            string manifestPath = Path.Combine(directory, ManifestFileName);

            try
            {
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                var manifest = PinBackupManifest.FromJson(File.ReadAllText(manifestPath));
                if (manifest is null)
                {
                    Log.Warn($"贴图备份清单无法解析，跳过：{manifestPath}");
                    continue;
                }

                _currentSession = session;
                return (manifest, directory);
            }
            catch (Exception ex)
            {
                Log.Warn($"读取贴图备份失败（{session}）：{ex.Message}");
            }
        }

        return null;
    }

    /// <summary>会话目录名（最新在前）。</summary>
    public IReadOnlyList<string> ListSessions()
    {
        try
        {
            if (!Directory.Exists(Root))
            {
                return [];
            }

            return Directory.GetDirectories(Root)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderByDescending(name => name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举贴图备份目录失败：{ex.Message}");
            return [];
        }
    }

    public BitmapSource? LoadImage(string sessionDirectory, string fileName)
    {
        try
        {
            string path = Path.Combine(sessionDirectory, fileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var frame = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            // 重新解码出来的图可能带调色板/预乘，统一到 Bgra32 供贴图使用
            var normalized = SaveService.Normalize(frame);
            normalized.Freeze();
            return normalized;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取贴图备份图片失败（{fileName}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>只保留最近 keep 个会话（keep 至少为 1）。</summary>
    public void Prune(int keep)
    {
        foreach (string victim in PinBackupRetention.Victims(ListSessions(), keep))
        {
            try
            {
                string path = Path.Combine(Root, victim);
                Directory.Delete(path, recursive: true);
                Log.Info($"已清理过期贴图备份：{victim}");
            }
            catch (Exception ex)
            {
                Log.Warn($"清理贴图备份失败（{victim}）：{ex.Message}");
            }
        }
    }

    /// <summary>清空全部备份（设置界面「清理贴图备份」用）。</summary>
    public void Clear()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            _currentSession = null;
        }
        catch (Exception ex)
        {
            Log.Warn($"清空贴图备份失败：{ex.Message}");
        }
    }

    /// <summary>本次会话的备份目录（尚未落盘时为 null）。</summary>
    public string? CurrentDirectory => _currentSession is null ? null : Path.Combine(Root, _currentSession);
}
