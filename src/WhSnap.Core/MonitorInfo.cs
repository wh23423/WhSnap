namespace WhSnap.Core;

/// <summary>一台显示器的物理信息。DpiX/DpiY 为 96 基准的有效 DPI。</summary>
public sealed record MonitorInfo(
    string DeviceName,
    PhysicalRect Bounds,
    PhysicalRect WorkArea,
    uint DpiX,
    uint DpiY,
    bool IsPrimary)
{
    /// <summary>HMONITOR 句柄。Windows.Graphics.Capture 按显示器创建采集项时必需。</summary>
    public nint Handle { get; init; }

    public double ScaleX => DpiX / 96.0;

    public double ScaleY => DpiY / 96.0;

    public bool IsMixedDpi => Math.Abs(ScaleX - ScaleY) > 0.001;

    public override string ToString() =>
        $"{(IsPrimary ? "主屏" : "副屏")} {Bounds} dpi={DpiX} scale={ScaleX:0.##}";
}
