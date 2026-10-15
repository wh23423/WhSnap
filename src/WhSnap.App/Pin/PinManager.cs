using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WhSnap.Capture;
using WhSnap.Core;
using WhSnap.Services;

namespace WhSnap.Pin;

/// <summary>
/// 贴图管家：创建、层级、批量操作、显示器拓扑重算、自动备份与重启恢复。
///
/// 生命周期与 App 一致。所有方法都在 UI 线程调用（贴图窗口是原生窗口，消息也由 UI 线程派发）。
/// </summary>
internal sealed class PinManager : IDisposable
{
    private readonly SettingsService _settings;
    private readonly PinBackupStore _backup;
    private readonly DispatcherTimer _backupTimer;
    private readonly List<PinWindow> _pins = [];

    private IReadOnlyList<MonitorInfo> _monitors = [];
    private PhysicalRect _desktop;
    private string _lastSignature = string.Empty;
    private bool _backupDirty;
    private int _nextZIndex;
    private int _cascade;
    private bool _disposed;
    private bool _restoring;

    public PinManager(SettingsService settings, PinBackupStore? backupStore = null)
    {
        _settings = settings;
        _backup = backupStore ?? new PinBackupStore();

        _backupTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(settings.Current.PinBackupIntervalSeconds, 1, 300)),
        };

        _backupTimer.Tick += (_, _) =>
        {
            _backupTimer.Stop();

            if (_backupDirty)
            {
                SaveBackupNow();
            }
        };
    }

    public AppSettings Settings => _settings.Current;

    public IReadOnlyList<PinWindow> Pins => _pins;

    public IReadOnlyList<MonitorInfo> Monitors => _monitors;

    public PhysicalRect Desktop => _desktop;

    public int Count => _pins.Count;

    public int VisibleCount => _pins.Count(pin => !pin.IsHidden);

    public PinBackupStore BackupStore => _backup;

    public PinTextStyle TextStyle => PinTextStyle.FromSettings(_settings.Current);

    /// <summary>贴图窗口内的按键绑定（来自设置，改键后宿主会更新）。</summary>
    public IReadOnlyList<HotkeyBinding> KeyBindings { get; set; } = HotkeyBindings.Resolve(scope: HotkeyScope.Pin);

    public event Action<string, bool>? StatusReported;

    public event Action? PinsChanged;

    /// <summary>用户在某张贴图上按了空格（M4 的标注入口）。</summary>
    public event Action<PinWindow>? AnnotationRequested;

    // ---------------------------------------------------------------- 初始化

    /// <summary>枚举显示器、建立桌面坐标，并按设置恢复上次的贴图。</summary>
    public void Initialize()
    {
        UpdateTopology();

        if (_settings.Current.RestorePinsOnStartup)
        {
            RestoreFromBackup();
        }
    }

    /// <summary>显示器拓扑变化后重新枚举，并把所有贴图重算到新桌面里。</summary>
    public void UpdateTopology()
    {
        var monitors = MonitorEnumerator.Enumerate();
        if (monitors.Count == 0)
        {
            Log.Warn("贴图管理器未枚举到显示器，跳过拓扑更新。");
            return;
        }

        var desktop = VirtualDesktop.BoundsOf(monitors);
        var previous = _desktop;

        _monitors = monitors;
        _desktop = desktop;

        Log.Info($"贴图桌面拓扑更新：虚拟桌面 {desktop}，显示器 {monitors.Count} 台。");

        if (previous.IsEmpty || previous == desktop)
        {
            return;
        }

        foreach (var pin in _pins)
        {
            pin.ReconcileTopology(previous, desktop);
        }

        ScheduleBackup();
    }

    // ---------------------------------------------------------------- 创建

    /// <summary>用一张位图创建贴图。<paramref name="seed"/> 用于备份恢复（保留原始几何）。</summary>
    public PinWindow CreatePin(BitmapSource image, PhysicalPoint? position = null, PinState? seed = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        var source = SaveService.Normalize(image);
        var state = seed ?? CreateDefaultState(source, position);

        var pin = new PinWindow(this, source, state)
        {
            ZIndex = _nextZIndex++,
        };

        Attach(pin);
        _pins.Add(pin);

        if (seed is null && Settings.PinTopmostNew)
        {
            pin.BringToFront();
        }

        Log.Info($"新建贴图：{source.PixelWidth}x{source.PixelHeight} @ {pin.Bounds}（当前 {_pins.Count} 张）");

        ScheduleBackup();
        PinsChanged?.Invoke();
        return pin;
    }

    private PinState CreateDefaultState(BitmapSource source, PhysicalPoint? position)
    {
        var state = PinState.ForImage(source.PixelWidth, source.PixelHeight, position ?? NextCascadePosition());
        state = PinGeometry.SetOpacity(state, Settings.PinDefaultOpacity);
        state = PinGeometry.SetClickThrough(state, Settings.PinClickThroughDefault);
        return PinGeometry.EnsureVisible(state, _desktop, 16);
    }

    /// <summary>没有指定落点时按层叠偏移摆放，避免多张贴图完全叠在一起。</summary>
    private PhysicalPoint NextCascadePosition()
    {
        var cursor = PinWindow.CursorPosition();
        int offset = (_cascade++ % 8) * 24;
        return new PhysicalPoint(cursor.X + 24 + offset, cursor.Y + 24 + offset);
    }

    /// <summary>从剪贴板贴图（图像 / 文件 / HTML / 颜色 / 文本）。返回创建数量。</summary>
    public int CreatePinsFromClipboard()
    {
        var payload = ClipboardReader.Read();

        if (payload.IsEmpty)
        {
            ReportStatus("剪贴板里没有可贴的内容（支持图像、图片文件、HTML 片段、颜色值、文本）。", warning: true);
            return 0;
        }

        if (payload.Kind == ClipboardContentKind.Files)
        {
            var files = payload.Files ?? [];
            int count = CreatePinsFromFiles(files, PinWindow.CursorPosition());
            return count;
        }

        var bitmap = ClipboardReader.ToBitmap(payload, TextStyle);
        if (bitmap is null)
        {
            ReportStatus("剪贴板内容无法转成贴图。", warning: true);
            return 0;
        }

        CreatePin(bitmap);
        ReportStatus($"已贴图：{payload.Describe()}");
        return 1;
    }

    /// <summary>拖放/复制的文件列表 → 图片按图贴，其他渲染成文件卡片。</summary>
    public int CreatePinsFromFiles(IEnumerable<string> paths, PhysicalPoint anchor)
    {
        ArgumentNullException.ThrowIfNull(paths);

        int created = 0;
        int offset = 0;

        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            BitmapSource? content = null;

            if (File.Exists(path) && ClipboardReader.LooksLikeImageFile(path))
            {
                content = ClipboardReader.LoadImageFile(path);
            }

            content ??= PinContentRenderer.RenderFileCard(path, TextStyle);

            var position = new PhysicalPoint(anchor.X + 24 + (offset * 24), anchor.Y + 24 + (offset * 24));
            CreatePin(content, position);
            created++;
            offset++;
        }

        if (created > 0)
        {
            ReportStatus($"已贴 {created} 个文件");
        }
        else
        {
            ReportStatus("没有可贴的文件。", warning: true);
        }

        return created;
    }

    private void Attach(PinWindow pin)
    {
        pin.Changed += OnPinChanged;
        pin.CloseRequested += Close;
        pin.AnnotationRequested += OnAnnotationRequested;
    }

    private void OnPinChanged(PinWindow pin) => PinsChanged?.Invoke();

    private void OnAnnotationRequested(PinWindow pin) => AnnotationRequested?.Invoke(pin);

    // ---------------------------------------------------------------- 批量操作

    public void HideAll()
    {
        foreach (var pin in _pins.Where(p => !p.IsHidden).ToList())
        {
            pin.Hide();
        }

        ReportStatus("已隐藏全部贴图（托盘菜单可恢复）");
    }

    public void ShowAll()
    {
        foreach (var pin in _pins.Where(p => p.IsHidden).ToList())
        {
            pin.Show();
        }

        ReportStatus(_pins.Count == 0 ? "当前没有贴图。" : $"已显示 {_pins.Count} 张贴图");
    }

    public void CloseAll()
    {
        foreach (var pin in _pins.ToList())
        {
            Close(pin);
        }

        ReportStatus("已关闭全部贴图");
    }

    public void BringAllToFront()
    {
        foreach (var pin in _pins.OrderBy(p => p.ZIndex).ToList())
        {
            pin.BringToFront();
        }
    }

    public void Close(PinWindow pin)
    {
        ArgumentNullException.ThrowIfNull(pin);

        if (!_pins.Remove(pin))
        {
            return;
        }

        pin.Changed -= OnPinChanged;
        pin.CloseRequested -= Close;
        pin.AnnotationRequested -= OnAnnotationRequested;

        int zIndex = pin.ZIndex;
        pin.Dispose();

        // 关闭后压缩 Z 序，避免恢复备份时层级出现空洞
        foreach (var other in _pins.Where(p => p.ZIndex > zIndex))
        {
            other.ZIndex--;
        }

        ScheduleBackup();
        PinsChanged?.Invoke();
    }

    /// <summary>命中测试（按最上层优先）。用于托盘/热键里的"当前贴图"操作。</summary>
    public PinWindow? PinAt(PhysicalPoint point)
    {
        foreach (var pin in _pins.OrderByDescending(p => p.ZIndex))
        {
            if (!pin.IsHidden && PinGeometry.Contains(pin.State, point))
            {
                return pin;
            }
        }

        return null;
    }

    public PinWindow? PinAtCursor() => PinAt(PinWindow.CursorPosition());

    // ---------------------------------------------------------------- 备份与恢复

    public void ScheduleBackup()
    {
        if (_disposed || !Settings.PinBackupEnabled || _restoring)
        {
            return;
        }

        _backupDirty = true;
        _backupTimer.Stop();
        _backupTimer.Start();
    }

    /// <summary>立刻落盘。内容与上次完全一致时跳过（避免频繁创建备份目录）。</summary>
    public void SaveBackupNow()
    {
        if (!Settings.PinBackupEnabled)
        {
            _backupDirty = false;
            return;
        }

        _backupTimer.Stop();
        _backupDirty = false;

        string signature = BuildSignature();
        if (signature == _lastSignature)
        {
            return;
        }

        string? directory = _backup.Save(_pins, _desktop, Settings.PinBackupRetention);
        if (directory is null)
        {
            _backupDirty = true;
            return;
        }

        _lastSignature = signature;
        Log.Info($"贴图已备份 {_pins.Count} 张 → {directory}");
    }

    private string BuildSignature()
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(RectCodec.Format(_desktop)).Append('|');

        foreach (var pin in _pins.OrderBy(p => p.ZIndex))
        {
            builder.Append(pin.ZIndex).Append(':')
                .Append(pin.State.SourceWidth).Append('x').Append(pin.State.SourceHeight).Append(':')
                .Append(pin.State.ToString()).Append(';');
        }

        return builder.ToString();
    }

    /// <summary>从最近一次备份恢复。返回恢复的贴图数量。</summary>
    public int RestoreFromBackup()
    {
        var loaded = _backup.LoadLatest();
        if (loaded is null)
        {
            return 0;
        }

        var (manifest, directory) = loaded.Value;
        var oldDesktop = RectCodec.TryParse(manifest.Desktop, out var parsed) ? parsed : _desktop;
        int restored = 0;

        _restoring = true;

        try
        {
            foreach (var entry in manifest.Pins)
            {
                var image = _backup.LoadImage(directory, entry.File);
                if (image is null)
                {
                    continue;
                }

                var state = entry.ToState(image.PixelWidth, image.PixelHeight);
                state = PinGeometry.ReconcileTopology(state, oldDesktop, _desktop);

                var pin = CreatePin(image, seed: state);
                pin.BackupFileName = entry.File;
                pin.ZIndex = entry.ZIndex;
                restored++;
            }
        }
        catch (Exception ex)
        {
            Log.Error("恢复贴图备份失败", ex);
        }
        finally
        {
            _restoring = false;
        }

        _nextZIndex = _pins.Count == 0 ? 0 : _pins.Max(p => p.ZIndex) + 1;
        _lastSignature = BuildSignature();

        if (restored > 0)
        {
            Log.Info($"已从备份恢复 {restored} 张贴图（{directory}）。");
            ReportStatus($"已恢复上次的 {restored} 张贴图");
            PinsChanged?.Invoke();
        }

        return restored;
    }

    public void ReportStatus(string message, bool warning = false) => StatusReported?.Invoke(message, warning);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _backupTimer.Stop();

        try
        {
            SaveBackupNow();
        }
        catch (Exception ex)
        {
            Log.Warn($"退出前保存贴图备份失败：{ex.Message}");
        }

        foreach (var pin in _pins.ToList())
        {
            pin.Changed -= OnPinChanged;
            pin.CloseRequested -= Close;
            pin.AnnotationRequested -= OnAnnotationRequested;
            pin.Dispose();
        }

        _pins.Clear();
    }
}
