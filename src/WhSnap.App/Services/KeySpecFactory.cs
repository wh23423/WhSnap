using System.Windows.Input;
using WhSnap.Core;
using WhSnap.Interop;

namespace WhSnap.Services;

/// <summary>
/// 把"按下的键"归一成 <see cref="HotkeySpec"/>，让遮罩/贴图窗口也能走同一套键位定义。
///
/// 两条路径：
///   * WPF 窗口（遮罩）：<see cref="FromWpf"/> 直接用 WPF 的 Key 与 ModifierKeys；
///   * 原生窗口（贴图）：<see cref="FromVirtualKey"/> 用 <c>GetKeyState</c> 读修饰键当前状态。
/// </summary>
internal static class KeySpecFactory
{
    public static HotkeySpec FromWpf(Key key, ModifierKeys modifiers)
    {
        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        return new HotkeySpec(ModifierBits(modifiers), (uint)Math.Max(virtualKey, 0));
    }

    public static uint ModifierBits(ModifierKeys modifiers)
    {
        uint bits = 0;

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            bits |= HotkeySpec.ModControl;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            bits |= HotkeySpec.ModShift;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            bits |= HotkeySpec.ModAlt;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            bits |= HotkeySpec.ModWin;
        }

        return bits;
    }

    /// <summary>原生窗口里按下的键：修饰键状态从系统实时读取（VK_LWIN/RWIN 与左右修饰键都算）。</summary>
    public static HotkeySpec FromVirtualKey(int virtualKey)
    {
        uint bits = 0;

        if (IsDown(0x11) || IsDown(0xA2) || IsDown(0xA3)) // Ctrl / LCtrl / RCtrl
        {
            bits |= HotkeySpec.ModControl;
        }

        if (IsDown(0x10) || IsDown(0xA0) || IsDown(0xA1)) // Shift / LShift / RShift
        {
            bits |= HotkeySpec.ModShift;
        }

        if (IsDown(0x12) || IsDown(0xA4) || IsDown(0xA5)) // Alt / LAlt / RAlt
        {
            bits |= HotkeySpec.ModAlt;
        }

        if (IsDown(0x5B) || IsDown(0x5C)) // LWin / RWin
        {
            bits |= HotkeySpec.ModWin;
        }

        return new HotkeySpec(bits, (uint)virtualKey);
    }

    private static Dictionary<uint, Key>? _virtualKeyToWpf;

    /// <summary>
    /// 反向转换：把配置里的键位文本（如 <c>"Ctrl+C"</c>）还原成 WPF 的按键与修饰键。
    /// 自检需要"按配置里的键"而不是写死某个默认键——写死会让改键后自检失真。
    /// </summary>
    public static bool TryToWpf(string? keys, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;

        if (string.IsNullOrWhiteSpace(keys) || !HotkeySpec.TryParse(keys, out var spec, out _))
        {
            return false;
        }

        _virtualKeyToWpf ??= BuildVirtualKeyMap();

        if (!_virtualKeyToWpf.TryGetValue(spec.VirtualKey, out var wpfKey))
        {
            return false;
        }

        if ((spec.Modifiers & HotkeySpec.ModControl) != 0)
        {
            modifiers |= ModifierKeys.Control;
        }

        if ((spec.Modifiers & HotkeySpec.ModShift) != 0)
        {
            modifiers |= ModifierKeys.Shift;
        }

        if ((spec.Modifiers & HotkeySpec.ModAlt) != 0)
        {
            modifiers |= ModifierKeys.Alt;
        }

        if ((spec.Modifiers & HotkeySpec.ModWin) != 0)
        {
            modifiers |= ModifierKeys.Windows;
        }

        key = wpfKey;
        return true;
    }

    /// <summary>枚举 <see cref="Key"/> 建立 VK → Key 的反查表（取第一个映射，保证稳定）。</summary>
    private static Dictionary<uint, Key> BuildVirtualKeyMap()
    {
        var map = new Dictionary<uint, Key>();

        foreach (Key candidate in Enum.GetValues<Key>())
        {
            if (candidate == Key.None)
            {
                continue;
            }

            int vk = KeyInterop.VirtualKeyFromKey(candidate);

            if (vk > 0)
            {
                map.TryAdd((uint)vk, candidate);
            }
        }

        return map;
    }

    private static bool IsDown(int virtualKey) => (NativeMethods.GetKeyState(virtualKey) & 0x8000) != 0;
}
