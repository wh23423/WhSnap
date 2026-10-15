using System.Windows.Interop;
using System.Windows.Threading;
using WhSnap.Interop;

namespace WhSnap.Services;

/// <summary>
/// 剪贴板变化监听（`AddClipboardFormatListener` + message-only 窗口）。
/// 用系统通知而不是轮询：轮询要么漏、要么白烧 CPU。
///
/// 一次复制常常触发多次通知（不同格式分步写入），因此做 350 ms 去抖，
/// 只在"内容稳定下来"之后读一次剪贴板。
/// </summary>
internal sealed class ClipboardWatcher : IDisposable
{
    private const int DebounceMilliseconds = 350;

    private HwndSource? _source;
    private DispatcherTimer? _debounce;
    private bool _disposed;

    /// <summary>剪贴板内容已稳定（在 UI 线程上触发）。</summary>
    public event Action? ClipboardChanged;

    public bool IsRunning => _source is not null;

    public void Start()
    {
        if (_disposed || _source is not null)
        {
            return;
        }

        var parameters = new HwndSourceParameters("WhSnapClipboardSink")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            Width = 0,
            Height = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(MessageHook);

        if (!NativeMethods.AddClipboardFormatListener(_source.Handle))
        {
            Log.Warn($"注册剪贴板监听失败（Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）。");
        }
        else
        {
            Log.Info("剪贴板监听已启动（复制即贴）。");
        }
    }

    public void Stop()
    {
        if (_source is null)
        {
            return;
        }

        _debounce?.Stop();
        NativeMethods.RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(MessageHook);
        _source.Dispose();
        _source = null;
        Log.Info("剪贴板监听已停止。");
    }

    private IntPtr MessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_CLIPBOARDUPDATE)
        {
            return IntPtr.Zero;
        }

        handled = true;

        _debounce ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds),
        };

        _debounce.Stop();
        _debounce.Tick -= OnDebounceTick;
        _debounce.Tick += OnDebounceTick;
        _debounce.Start();

        return IntPtr.Zero;
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce?.Stop();
        ClipboardChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}

/// <summary>
/// 显示器拓扑变化监听（`WM_DISPLAYCHANGE` / `WM_SETTINGCHANGE` / `WM_DPICHANGED`）。
/// 贴图在显示器拔插、分辨率变化、缩放比变化后必须重新落位，否则会"消失"在屏幕外。
/// </summary>
internal sealed class TopologyWatcher : IDisposable
{
    private const int WmDpiChanged = 0x02E0;
    private const int DebounceMilliseconds = 500;

    private HwndSource? _source;
    private DispatcherTimer? _debounce;
    private bool _disposed;

    public event Action? TopologyChanged;

    public bool IsRunning => _source is not null;

    public void Start()
    {
        if (_disposed || _source is not null)
        {
            return;
        }

        var parameters = new HwndSourceParameters("WhSnapTopologySink")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            Width = 0,
            Height = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(MessageHook);
    }

    public void Stop()
    {
        if (_source is null)
        {
            return;
        }

        _debounce?.Stop();
        _source.RemoveHook(MessageHook);
        _source.Dispose();
        _source = null;
    }

    private IntPtr MessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is not (NativeMethods.WM_DISPLAYCHANGE or NativeMethods.WM_SETTINGCHANGE or WmDpiChanged))
        {
            return IntPtr.Zero;
        }

        handled = true;

        _debounce ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds),
        };

        _debounce.Stop();
        _debounce.Tick -= OnDebounceTick;
        _debounce.Tick += OnDebounceTick;
        _debounce.Start();

        return IntPtr.Zero;
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce?.Stop();
        Log.Info("检测到显示器拓扑变化，正在重算贴图位置。");
        TopologyChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
