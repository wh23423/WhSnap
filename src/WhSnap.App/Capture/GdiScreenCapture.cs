using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WhSnap.Core;
using WhSnap.Interop;
using static WhSnap.Interop.NativeMethods;

namespace WhSnap.Capture;

/// <summary>一次冻结的屏幕内容：整张虚拟桌面位图 + 拓扑信息。</summary>
internal sealed record CaptureFrame(BitmapSource Image, PhysicalRect VirtualBounds, IReadOnlyList<MonitorInfo> Monitors);

/// <summary>
/// 预览版采集后端：GDI BitBlt（CAPTUREBLT）。
/// 优点：零依赖、兼容性最好、能立刻跑通。
/// 已知局限（规划中已列）：抓不到部分硬件叠加层内容、HDR 下颜色可能不准、帧率较低。
/// 后续由 IScreenCapture 抽象替换为 WGC / DXGI 后端（M1）。
/// </summary>
internal static class GdiScreenCapture
{
    public static CaptureFrame CaptureFrozen()
    {
        int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"虚拟桌面尺寸无效：{width}x{height}。");
        }

        var virtualBounds = new PhysicalRect(x, y, width, height);

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
            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                throw new InvalidOperationException("创建兼容位图失败。");
            }

            previousBitmap = SelectObject(memoryDc, bitmap);

            // CAPTUREBLT 让分层窗口（含部分叠加层）也进入抓取结果
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, SRCCOPY | (int)CAPTUREBLT))
            {
                throw new InvalidOperationException("BitBlt 抓屏失败。");
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            // Clone 得到独立像素副本，之后才能真正释放 HBITMAP
            var frozen = source.Clone();
            frozen.Freeze();
            return new CaptureFrame(frozen, virtualBounds, MonitorEnumerator.Enumerate());
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
