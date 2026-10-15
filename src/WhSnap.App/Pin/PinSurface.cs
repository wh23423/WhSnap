using WhSnap.Core;
using WhSnap.Interop;

namespace WhSnap.Pin;

/// <summary>
/// 分层窗口的像素承载面：一块 32bpp 顶朝下 DIB + 一个内存 DC，用 <c>UpdateLayeredWindow</c> 一次性提交。
///
/// 为什么不用 WPF 的 Image/RenderTargetBitmap 直接显示：
/// 贴图必须"逐像素 alpha"（截图里有透明像素时能看见底下的窗口），
/// 而 WPF 的 AllowsTransparency 是整窗合成且性能差；分层窗口还能让**完全透明的像素自动穿透鼠标**。
/// </summary>
internal sealed class PinSurface : IDisposable
{
    private IntPtr _memoryDc;
    private IntPtr _bitmap;
    private IntPtr _previousBitmap;
    private IntPtr _bits;
    private bool _disposed;

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>按需重建缓冲（尺寸不变时复用，避免拖动/调透明度时反复分配 GDI 对象）。</summary>
    public void EnsureSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"无效的贴图缓冲尺寸 {width}x{height}。");
        }

        if (_bitmap != IntPtr.Zero && width == Width && height == Height)
        {
            return;
        }

        Release();

        _memoryDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        if (_memoryDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("创建内存 DC 失败。");
        }

        var info = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                // 负数高度 = 顶朝下，与 RenderTargetBitmap 的扫描行顺序一致
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
            },
        };

        _bitmap = NativeMethods.CreateDIBSection(_memoryDc, ref info, NativeMethods.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero)
        {
            Release();
            throw new InvalidOperationException("创建 32bpp DIB 失败。");
        }

        _previousBitmap = NativeMethods.SelectObject(_memoryDc, _bitmap);
        Width = width;
        Height = height;
    }

    /// <summary>写入预乘 alpha 的 BGRA 像素（长度必须 ≥ width × height × 4）。</summary>
    public void WritePixels(byte[] premultipliedBgra)
    {
        ArgumentNullException.ThrowIfNull(premultipliedBgra);

        int required = Width * Height * 4;
        if (premultipliedBgra.Length < required)
        {
            throw new ArgumentOutOfRangeException(nameof(premultipliedBgra), $"像素缓冲只有 {premultipliedBgra.Length} 字节，需要 {required}。");
        }

        System.Runtime.InteropServices.Marshal.Copy(premultipliedBgra, 0, _bits, required);
    }

    /// <summary>把当前缓冲提交到窗口（同时设置窗口位置、尺寸与整体不透明度）。</summary>
    public void Commit(IntPtr hwnd, PhysicalPoint screenPosition, int opacityPercent)
    {
        if (_memoryDc == IntPtr.Zero || hwnd == IntPtr.Zero)
        {
            return;
        }

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            var destination = new NativeMethods.POINT { X = screenPosition.X, Y = screenPosition.Y };
            var size = new NativeMethods.SIZE(Width, Height);
            var source = new NativeMethods.POINT { X = 0, Y = 0 };
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = (byte)Math.Clamp(opacityPercent * 255 / 100, 0, 255),
                AlphaFormat = NativeMethods.AC_SRC_ALPHA,
            };

            if (!NativeMethods.UpdateLayeredWindow(
                    hwnd,
                    screenDc,
                    ref destination,
                    ref size,
                    _memoryDc,
                    ref source,
                    0,
                    ref blend,
                    NativeMethods.ULW_ALPHA))
            {
                throw new InvalidOperationException(
                    $"UpdateLayeredWindow 失败（Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）。");
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void Release()
    {
        if (_memoryDc != IntPtr.Zero && _previousBitmap != IntPtr.Zero)
        {
            NativeMethods.SelectObject(_memoryDc, _previousBitmap);
            _previousBitmap = IntPtr.Zero;
        }

        if (_bitmap != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_bitmap);
            _bitmap = IntPtr.Zero;
        }

        if (_memoryDc != IntPtr.Zero)
        {
            NativeMethods.DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }

        _bits = IntPtr.Zero;
        Width = 0;
        Height = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release();
    }
}
