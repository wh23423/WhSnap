using System.Runtime.InteropServices;
using System.Windows.Interop;
using WhSnap.Core;
using WhSnap.Interop;
using static WhSnap.Interop.NativeMethods;

namespace WhSnap.Services;

internal enum HotkeyStatus
{
    /// <summary>已成功注册并生效。</summary>
    Registered,

    /// <summary>L1：被系统或其它程序占用（Win32 1409）。</summary>
    ConflictWithSystem,

    /// <summary>注册失败但不是"被占用"。</summary>
    Failed,

    /// <summary>用户明确停用了这个动作。</summary>
    Disabled,

    /// <summary>没有绑定键位。</summary>
    Unbound,

    /// <summary>键位文本无法解析。</summary>
    Invalid,
}

internal sealed record HotkeyRegistrationResult(
    HotkeyStatus Status,
    string ActionId,
    string Keys,
    HotkeySpec Spec,
    int Win32Error,
    string Message)
{
    public bool IsActive => Status == HotkeyStatus.Registered;

    /// <summary>设置界面徽标文字。</summary>
    public string Badge => Status switch
    {
        HotkeyStatus.Registered => "已生效",
        HotkeyStatus.ConflictWithSystem => "已被占用",
        HotkeyStatus.Failed => "注册失败",
        HotkeyStatus.Disabled => "已停用",
        HotkeyStatus.Unbound => "未绑定",
        _ => "键位非法",
    };
}

/// <summary>
/// 全局热键注册服务：挂在 message-only 窗口上收 WM_HOTKEY，**每个动作一个 id**。
///
/// 冲突检测的第一层（L1）就落在这里：`RegisterHotKey` 失败且错误码为 1409 即"已被占用"。
/// Windows 没有公开的反查占用者 API，所以占用者身份只能靠 L3 冲突源库启发式提示（风险 R3）。
///
/// 另外处理风险 R4：休眠/唤醒后热键会静默失效，因此监听 `WM_POWERBROADCAST` 自动重注册。
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int FirstHotkeyId = 0xB100;
    private const int ProbeHotkeyId = 0xB0FF;
    private const int WmPowerBroadcast = NativeMethods.WM_POWERBROADCAST;
    private const int PbtApmResumeAutomatic = 0x0012;
    private const int PbtApmResumeSuspend = 0x0007;

    private readonly Dictionary<int, string> _idToAction = [];
    private readonly Dictionary<string, HotkeyRegistrationResult> _results = new(StringComparer.OrdinalIgnoreCase);

    private HwndSource? _source;
    private IReadOnlyList<HotkeyBinding> _bindings = [];
    private bool _disposed;

    /// <summary>某个全局动作被按下（在 UI 线程上触发）。</summary>
    public event Action<string>? ActionPressed;

    /// <summary>热键因休眠唤醒/显示变化被重新注册（用于写日志与诊断）。</summary>
    public event Action<string>? Reregistered;

    public IReadOnlyDictionary<string, HotkeyRegistrationResult> Results => _results;

    public int RegisteredCount => _results.Values.Count(result => result.IsActive);

    /// <summary>按绑定表重新注册全部全局热键。返回每个动作的结果。</summary>
    public IReadOnlyList<HotkeyRegistrationResult> ApplyAll(IReadOnlyList<HotkeyBinding> bindings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnsureMessageWindow();
        UnregisterAll();

        _bindings = bindings;
        var results = new List<HotkeyRegistrationResult>();
        int nextId = FirstHotkeyId;

        foreach (var binding in bindings.Where(binding => binding.Definition.Scope == HotkeyScope.Global))
        {
            var definition = binding.Definition;

            if (!binding.Enabled)
            {
                results.Add(Record(new HotkeyRegistrationResult(
                    HotkeyStatus.Disabled, definition.Id, binding.Keys ?? string.Empty, default, 0,
                    $"「{definition.DisplayName}」已停用。")));
                continue;
            }

            if (!binding.IsBound)
            {
                results.Add(Record(new HotkeyRegistrationResult(
                    HotkeyStatus.Unbound, definition.Id, string.Empty, default, 0,
                    $"「{definition.DisplayName}」未绑定热键。")));
                continue;
            }

            if (!binding.TryGetSpec(out var spec))
            {
                results.Add(Record(new HotkeyRegistrationResult(
                    HotkeyStatus.Invalid, definition.Id, binding.Keys!, default, 0,
                    $"「{definition.DisplayName}」的键位「{binding.Keys}」无法解析。")));
                continue;
            }

            if (NativeMethods.RegisterHotKey(_source!.Handle, nextId, spec.RegisterModifiers, spec.VirtualKey))
            {
                _idToAction[nextId] = definition.Id;
                nextId++;
                results.Add(Record(new HotkeyRegistrationResult(
                    HotkeyStatus.Registered, definition.Id, spec.ToString(), spec, 0,
                    $"「{definition.DisplayName}」→ {spec} 已生效。")));
                continue;
            }

            int error = Marshal.GetLastWin32Error();
            results.Add(Record(error == ERROR_HOTKEY_ALREADY_REGISTERED
                ? new HotkeyRegistrationResult(
                    HotkeyStatus.ConflictWithSystem, definition.Id, spec.ToString(), spec, error,
                    $"「{definition.DisplayName}」的 {spec} 已被系统或其它程序占用。")
                : new HotkeyRegistrationResult(
                    HotkeyStatus.Failed, definition.Id, spec.ToString(), spec, error,
                    $"「{definition.DisplayName}」的 {spec} 注册失败（Win32 错误 {error}）。")));
        }

        foreach (var result in results)
        {
            if (result.IsActive)
            {
                Log.Info(result.Message);
            }
            else
            {
                Log.Warn(result.Message);
            }
        }

        return results;
    }

    /// <summary>
    /// L1 预检：临时放开自己占用的热键，逐个试注册，再恢复。
    /// 这是唯一能证明"这个键现在能不能用"的办法（试注册成功=可用）。
    /// </summary>
    public IReadOnlyList<HotkeyRegistrationResult> ProbeAll(IReadOnlyList<HotkeyBinding> bindings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var probeResults = new List<HotkeyRegistrationResult>();

        // 先释放自己的键，否则探测到的永远是自己占用的结果
        UnregisterAll();

        foreach (var binding in bindings.Where(binding => binding.Definition.Scope == HotkeyScope.Global))
        {
            var definition = binding.Definition;

            if (!binding.Enabled || !binding.IsBound || !binding.TryGetSpec(out var spec))
            {
                continue;
            }

            probeResults.Add(ProbeOne(definition.Id, definition.DisplayName, spec));
        }

        ApplyAll(bindings);
        return probeResults;
    }

    /// <summary>单独探测一个键位（不改变当前注册状态）。</summary>
    public HotkeyRegistrationResult ProbeOne(string actionId, string displayName, HotkeySpec spec)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureMessageWindow();

        if (NativeMethods.RegisterHotKey(_source!.Handle, ProbeHotkeyId, spec.RegisterModifiers, spec.VirtualKey))
        {
            NativeMethods.UnregisterHotKey(_source.Handle, ProbeHotkeyId);
            return new HotkeyRegistrationResult(HotkeyStatus.Registered, actionId, spec.ToString(), spec, 0, "可用。");
        }

        int error = Marshal.GetLastWin32Error();
        return new HotkeyRegistrationResult(
            error == ERROR_HOTKEY_ALREADY_REGISTERED ? HotkeyStatus.ConflictWithSystem : HotkeyStatus.Failed,
            actionId,
            spec.ToString(),
            spec,
            error,
            error == ERROR_HOTKEY_ALREADY_REGISTERED ? "已被占用。" : $"注册失败（Win32 {error}）。");
    }

    public void UnregisterAll()
    {
        if (_source is null)
        {
            return;
        }

        foreach (int id in _idToAction.Keys.ToList())
        {
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        }

        _idToAction.Clear();
    }

    private HotkeyRegistrationResult Record(HotkeyRegistrationResult result)
    {
        _results[result.ActionId] = result;
        return result;
    }

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
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();

            if (_idToAction.TryGetValue(id, out string? actionId))
            {
                handled = true;
                ActionPressed?.Invoke(actionId);
            }

            return IntPtr.Zero;
        }

        if (msg == WmPowerBroadcast)
        {
            int reason = wParam.ToInt32();

            // R4：休眠唤醒后热键会静默失效，必须重新注册
            if (reason is PbtApmResumeAutomatic or PbtApmResumeSuspend)
            {
                handled = true;
                Log.Info("检测到系统从休眠/睡眠恢复，重新注册全局热键。");

                try
                {
                    ApplyAll(_bindings);
                    Reregistered?.Invoke($"已重新注册 {RegisteredCount} 个热键。");
                }
                catch (Exception ex)
                {
                    Log.Error("唤醒后重新注册热键失败", ex);
                }
            }

            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterAll();

        if (_source is not null)
        {
            _source.RemoveHook(MessageHook);
            _source.Dispose();
            _source = null;
        }
    }
}
