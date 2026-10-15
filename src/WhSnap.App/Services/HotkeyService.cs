using System.Runtime.InteropServices;
using System.Windows.Interop;
using WhSnap.Core;
using WhSnap.Interop;
using static WhSnap.Interop.NativeMethods;

namespace WhSnap.Services;

internal enum HotkeyStatus
{
    Registered,
    ConflictWithSystem,
    Failed,
}

internal sealed record HotkeyRegistrationResult(HotkeyStatus Status, HotkeySpec Spec, int Win32Error, string Message)
{
    public bool IsRegistered => Status == HotkeyStatus.Registered;
}

/// <summary>
/// 全局热键注册。挂在 message-only 窗口上接收 WM_HOTKEY。
/// 冲突检测第 1 层：RegisterHotKey 返回失败即"已被占用"（Windows 未提供反查占用者的公开 API）。
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0xB001;

    private HwndSource? _source;
    private bool _registered;
    private bool _disposed;

    public event Action? Pressed;

    public bool IsRegistered => _registered;

    public HotkeySpec? Current { get; private set; }

    private void EnsureMessageWindow()
    {
        if (_source is not null)
        {
            return;
        }

        var parameters = new HwndSourceParameters("WhSnapHotkeySink")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(MessageHook);
    }

    private IntPtr MessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }

        return IntPtr.Zero;
    }

    public HotkeyRegistrationResult Register(HotkeySpec spec)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(HotkeyService));
        }

        EnsureMessageWindow();
        Unregister();

        if (!NativeMethods.RegisterHotKey(_source!.Handle, HotkeyId, spec.RegisterModifiers, spec.VirtualKey))
        {
            int error = Marshal.GetLastWin32Error();
            return error == ERROR_HOTKEY_ALREADY_REGISTERED
                ? new HotkeyRegistrationResult(
                    HotkeyStatus.ConflictWithSystem,
                    spec,
                    error,
                    $"热键 {spec} 已被系统或其他程序占用。")
                : new HotkeyRegistrationResult(
                    HotkeyStatus.Failed,
                    spec,
                    error,
                    $"热键 {spec} 注册失败（Win32 错误 {error}）。");
        }

        _registered = true;
        Current = spec;
        return new HotkeyRegistrationResult(HotkeyStatus.Registered, spec, 0, $"热键 {spec} 已生效。");
    }

    public void Unregister()
    {
        if (_registered && _source is not null)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();

        if (_source is not null)
        {
            _source.RemoveHook(MessageHook);
            _source.Dispose();
            _source = null;
        }
    }
}
