namespace WhSnap.Core;

/// <summary>虚拟桌面（所有显示器外接矩形）的几何计算。</summary>
public static class VirtualDesktop
{
    /// <summary>所有显示器物理矩形的并集。多屏可能为负坐标（副屏在主屏左侧/上方）。</summary>
    public static PhysicalRect BoundsOf(IEnumerable<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        int left = int.MaxValue;
        int top = int.MaxValue;
        int right = int.MinValue;
        int bottom = int.MinValue;
        bool any = false;

        foreach (var monitor in monitors)
        {
            any = true;
            left = Math.Min(left, monitor.Bounds.X);
            top = Math.Min(top, monitor.Bounds.Y);
            right = Math.Max(right, monitor.Bounds.Right);
            bottom = Math.Max(bottom, monitor.Bounds.Bottom);
        }

        if (!any)
        {
            throw new ArgumentException("显示器列表为空。", nameof(monitors));
        }

        return new PhysicalRect(left, top, right - left, bottom - top);
    }

    /// <summary>把绝对物理矩形换算为某台显示器窗口内的局部坐标。</summary>
    public static PhysicalRect ToMonitorLocal(PhysicalRect absolute, MonitorInfo monitor) =>
        new(absolute.X - monitor.Bounds.X, absolute.Y - monitor.Bounds.Y, absolute.Width, absolute.Height);
}
