using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WhSnap.Capture;

/// <summary>DXGI 与 Windows.Graphics.Capture 两个后端共用的 GPU 纹理读取逻辑。</summary>
internal static class D3D11TextureReader
{
    /// <summary>DXGI_FORMAT_B8G8R8A8_UNORM / _SRGB 的数值（用数值比较，避免绑定某个 Vortice 版本的枚举名）。</summary>
    public const uint FormatBgra8Unorm = 87;
    public const uint FormatBgra8UnormSrgb = 91;

    public static bool IsBgra8(uint format) => format is FormatBgra8Unorm or FormatBgra8UnormSrgb;

    /// <summary>把 GPU 纹理拷贝到 staging 纹理并映射到 CPU，返回紧密排列的 BGRA 缓冲。</summary>
    public static byte[] ReadBgra(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D texture, Texture2DDescription description)
    {
        int width = (int)description.Width;
        int height = (int)description.Height;

        var stagingDescription = new Texture2DDescription
        {
            Width = description.Width,
            Height = description.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = description.Format,
            SampleDescription = new SampleDescription { Count = 1, Quality = 0 },
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };

        using var staging = device.CreateTexture2D(stagingDescription);
        context.CopyResource(staging, texture);

        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var frame = new byte[width * height * 4];
            int rowBytes = width * 4;

            for (int y = 0; y < height; y++)
            {
                IntPtr rowPointer = IntPtr.Add(mapped.DataPointer, y * (int)mapped.RowPitch);
                Marshal.Copy(rowPointer, frame, y * rowBytes, rowBytes);
            }

            return frame;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    /// <summary>
    /// 把已读取的 BGRA 缓冲按显示器桌面坐标贴进虚拟桌面画布。
    /// 返回需要记入诊断的说明（尺寸被裁剪等），无异常情况返回 null。
    /// </summary>
    public static string? Compose(
        byte[] frame,
        int frameWidth,
        int frameHeight,
        WhSnap.Core.PhysicalRect desktopCoordinates,
        WhSnap.Core.DisplayRotation rotation,
        WhSnap.Core.PhysicalRect virtualBounds,
        byte[] canvas)
    {
        byte[] source = frame;
        int width = frameWidth;
        int height = frameHeight;

        if (rotation != WhSnap.Core.DisplayRotation.Identity)
        {
            source = WhSnap.Core.BgraPixels.Rotate(frame, frameWidth, frameHeight, frameWidth * 4, rotation);
            (int rotatedWidth, int rotatedHeight) = WhSnap.Core.BgraPixels.RotatedSize(frameWidth, frameHeight, rotation);
            width = rotatedWidth;
            height = rotatedHeight;
        }

        // 尺寸容错：某些虚拟显示器 / 远程会话给出的纹理尺寸与显示器矩形并不完全一致，
        // 这里裁剪到画布内而不是直接失败——能截到正确区域比追求尺寸严格一致更重要。
        string? note = null;
        int drawWidth = width;
        int drawHeight = height;

        if (drawWidth > virtualBounds.Width || drawHeight > virtualBounds.Height)
        {
            drawWidth = Math.Min(drawWidth, virtualBounds.Width);
            drawHeight = Math.Min(drawHeight, virtualBounds.Height);
            note = $"采集尺寸 {width}x{height} 超出虚拟桌面 {virtualBounds.Width}x{virtualBounds.Height}，已裁剪到 {drawWidth}x{drawHeight}";
        }

        int offsetX = Math.Clamp(desktopCoordinates.X - virtualBounds.X, 0, Math.Max(virtualBounds.Width - drawWidth, 0));
        int offsetY = Math.Clamp(desktopCoordinates.Y - virtualBounds.Y, 0, Math.Max(virtualBounds.Height - drawHeight, 0));

        WhSnap.Core.BgraPixels.Blit(
            source,
            drawWidth,
            drawHeight,
            width * 4,
            canvas,
            virtualBounds.Width,
            virtualBounds.Height,
            virtualBounds.Width * 4,
            offsetX,
            offsetY);

        return note;
    }
}
