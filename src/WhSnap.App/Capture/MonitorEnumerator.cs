using System.Runtime.InteropServices;
using WhSnap.Core;
using WhSnap.Interop;
using static WhSnap.Interop.NativeMethods;

namespace WhSnap.Capture;

/// <summary>枚举显示器并读取每台的物理矩形与有效 DPI。</summary>
internal static class MonitorEnumerator
{
    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var monitors = new List<MonitorInfo>();

        MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                uint dpiX = 96;
                uint dpiY = 96;
                try
                {
                    if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint x, out uint y) == 0)
                    {
                        dpiX = x == 0 ? 96 : x;
                        dpiY = y == 0 ? 96 : y;
                    }
                }
                catch (DllNotFoundException)
                {
                    // shcore.dll 不可用时退回 96 DPI
                }

                monitors.Add(new MonitorInfo(
                    DeviceName: string.Empty,
                    Bounds: new PhysicalRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Width, info.rcMonitor.Height),
                    WorkArea: new PhysicalRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Width, info.rcWork.Height),
                    DpiX: dpiX,
                    DpiY: dpiY,
                    IsPrimary: (info.dwFlags & MONITORINFOF_PRIMARY) != 0)
                {
                    Handle = hMonitor,
                });
            }

            return true;
        };

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        if (monitors.Count == 0)
        {
            // 极端兜底：至少给一个覆盖虚拟桌面的伪显示器，避免后续空引用
            int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            monitors.Add(new MonitorInfo(
                string.Empty,
                new PhysicalRect(0, 0, Math.Max(width, 1), Math.Max(height, 1)),
                new PhysicalRect(0, 0, Math.Max(width, 1), Math.Max(height, 1)),
                96,
                96,
                true));
        }

        return monitors;
    }

    /// <summary>取鼠标所在显示器（预览版只在该屏显示遮罩，跨屏选择留到 M1）。</summary>
    public static MonitorInfo GetMonitorAtCursor(IReadOnlyList<MonitorInfo> monitors)
    {
        if (GetCursorPos(out POINT point))
        {
            IntPtr handle = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (handle != IntPtr.Zero && GetMonitorInfo(handle, ref info))
            {
                var bounds = new PhysicalRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Width, info.rcMonitor.Height);
                var match = monitors.FirstOrDefault(m => m.Bounds == bounds);
                if (match is not null)
                {
                    return match;
                }
            }
        }

        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }
}
