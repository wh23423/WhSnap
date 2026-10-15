using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WhSnap.Core;
using WhSnap.Interop;
using static WhSnap.Interop.NativeMethods;

namespace WhSnap.Capture;

/// <summary>
/// GDI BitBlt + CAPTUREBLT 采集后端。
/// 定位：最后的兜底。优点零依赖、一次性拿到整张虚拟桌面、旋转屏/宽色域都由系统合成为 SDR 位图；
/// 缺点抓不到部分硬件叠加层、帧率低。
/// </summary>
internal sealed class GdiScreenCapture : IScreenCapture
{
    public string Name => CaptureBackendNames.Gdi;

    public CaptureFrame Capture()
    {
        var stopwatch = Stopwatch.StartNew();
        var monitors = MonitorEnumerator.Enumerate();
        var bounds = VirtualDesktop.BoundsOf(monitors);
        var notes = new List<string>();

        // 与 GetSystemMetrics 交叉校验：两者不一致说明拓扑正在变化
        int systemX = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int systemY = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int systemWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int systemHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (systemWidth != bounds.Width || systemHeight != bounds.Height || systemX != bounds.X || systemY != bounds.Y)
        {
            notes.Add($"显示器枚举结果 {bounds} 与系统虚拟桌面 {systemX},{systemY} {systemWidth}x{systemHeight} 不一致（拓扑可能刚变化）");
        }

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previousBitmap = IntPtr.Zero;

        try
        {
            screenDc = CreateDC("DISPLAY", null, null, IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateDC(\"DISPLAY\") 失败。");
            }

            memoryDc = CreateCompatibleDC(screenDc);
            bitmap = CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                throw new InvalidOperationException("创建兼容位图失败。");
            }

            previousBitmap = SelectObject(memoryDc, bitmap);

            if (!BitBlt(memoryDc, 0, 0, bounds.Width, bounds.Height, screenDc, bounds.X, bounds.Y, SRCCOPY | (int)CAPTUREBLT))
            {
                throw new InvalidOperationException($"BitBlt 抓屏失败（Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）。");
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            // Clone 得到独立像素副本，之后才能真正释放 HBITMAP
            var frozen = source.Clone();
            frozen.Freeze();

            return new CaptureFrame(
                frozen,
                bounds,
                monitors,
                Name,
                stopwatch.Elapsed,
                ProtectedContentMasked: false,
                Notes: notes.Count == 0 ? null : string.Join("；", notes));
        }
        finally
        {
            if (previousBitmap != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                SelectObject(memoryDc, previousBitmap);
            }

            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                DeleteDC(memoryDc);
            }

            if (screenDc != IntPtr.Zero)
            {
                DeleteDC(screenDc);
            }
        }
    }
}
