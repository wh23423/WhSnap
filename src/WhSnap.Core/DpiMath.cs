namespace WhSnap.Core;

/// <summary>DIP（WPF 逻辑像素）与物理像素之间的换算。缩放比来自所在显示器的有效 DPI。</summary>
public static class DpiMath
{
    public static double PxToDip(int px, double scale) => scale <= 0 ? px : px / scale;

    public static int DipToPx(double dip, double scale) => (int)Math.Round(dip * scale, MidpointRounding.AwayFromZero);

    public static PhysicalPoint DipPointToPx(double dipX, double dipY, double scaleX, double scaleY) =>
        new(DipToPx(dipX, scaleX), DipToPx(dipY, scaleY));

    /// <summary>把显示器局部物理坐标换算为虚拟桌面绝对物理坐标。</summary>
    public static PhysicalRect ToVirtual(PhysicalRect monitorLocal, MonitorInfo monitor) =>
        monitorLocal.Offset(monitor.Bounds.X, monitor.Bounds.Y);
}
