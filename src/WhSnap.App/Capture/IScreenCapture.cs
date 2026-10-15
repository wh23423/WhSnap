using System.Diagnostics;
using System.Windows.Media.Imaging;
using WhSnap.Core;

namespace WhSnap.Capture;

/// <summary>用户可选的采集后端。Auto 按 WGC → DXGI → GDI 依次尝试。</summary>
internal enum CaptureBackend
{
    Auto,
    Wgc,
    Dxgi,
    Gdi,
}

/// <summary>
/// 一次冻结的屏幕内容：整张虚拟桌面位图 + 拓扑信息。
/// Backend/Notes 用于诊断与设置界面的"当前实际后端"展示。
/// </summary>
internal sealed record CaptureFrame(
    BitmapSource Image,
    PhysicalRect VirtualBounds,
    IReadOnlyList<MonitorInfo> Monitors,
    string Backend,
    TimeSpan Duration,
    bool ProtectedContentMasked = false,
    string? Notes = null);

/// <summary>
/// 该后端在当前环境（系统版本、显示器、像素格式）下根本不适用，而非"坏掉了"。
/// 自检把它记为 SKIP，其他异常记为 FAIL——避免把"这台机器不支持"误报成缺陷。
/// </summary>
internal sealed class CaptureNotSupportedException(string message) : Exception(message);

/// <summary>
/// 屏幕采集后端。一次调用必须返回"整张虚拟桌面"的冻结位图：
/// 后续选区、标注、导出全部基于这张不可变底图，是所有坐标换算的唯一数据源。
/// </summary>
internal interface IScreenCapture
{
    string Name { get; }

    CaptureFrame Capture();
}

internal static class CaptureBackendNames
{
    public const string Wgc = "wgc";
    public const string Dxgi = "dxgi";
    public const string Gdi = "gdi";
    public const string Auto = "auto";

    public static CaptureBackend Parse(string? value) => (value ?? Auto).Trim().ToLowerInvariant() switch
    {
        Wgc => CaptureBackend.Wgc,
        Dxgi => CaptureBackend.Dxgi,
        Gdi => CaptureBackend.Gdi,
        _ => CaptureBackend.Auto,
    };

    public static string ToName(CaptureBackend backend) => backend switch
    {
        CaptureBackend.Wgc => Wgc,
        CaptureBackend.Dxgi => Dxgi,
        CaptureBackend.Gdi => Gdi,
        _ => Auto,
    };
}
