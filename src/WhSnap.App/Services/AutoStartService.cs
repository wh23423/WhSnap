using Microsoft.Win32;

namespace WhSnap.Services;

/// <summary>
/// 开机自启（HKCU\...\Run，per-user，无需提权）。
/// 只负责读写与一致性校验，不静默覆盖用户的删除操作——被优化软件移除后由界面提示恢复。
/// </summary>
internal static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WhSnap";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WhSnap.exe");

    public static string ExpectedCommand => $"\"{ExecutablePath}\" --tray";

    /// <summary>
    /// 是否运行在"已安装"的位置（而非 bin/Debug 这类开发目录）。
    /// 只有已安装的实例才会按默认配置自动写入自启项，避免开发机被调试版自启污染。
    /// </summary>
    public static bool IsInstalledLocation
    {
        get
        {
            string path = ExecutablePath;
            return !path.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)
                && !path.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)
                && !path.Contains(@"\artifacts\", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static string? GetCurrentCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    public static bool IsEnabled() => !string.IsNullOrEmpty(GetCurrentCommand());

    /// <summary>注册表里的自启项是否与实际 exe 路径一致（换目录安装后可能不一致）。</summary>
    public static bool IsConsistent()
    {
        string? current = GetCurrentCommand();
        if (string.IsNullOrEmpty(current))
        {
            return false;
        }

        return current.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            throw new InvalidOperationException("无法打开 HKCU Run 注册表项。");
        }

        if (enabled)
        {
            key.SetValue(ValueName, ExpectedCommand, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
