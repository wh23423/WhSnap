namespace WhSnap.Core;

/// <summary>显示器旋转角。对应 DXGI 的 ModeRotation，避免 Core 依赖 DXGI 类型。</summary>
public enum DisplayRotation
{
    Identity = 0,
    Rotate90 = 1,
    Rotate180 = 2,
    Rotate270 = 3,
}

/// <summary>
/// BGRA 像素缓冲的重排与读取工具。
/// DXGI Desktop Duplication 返回的是"未旋转"的桌面图像，竖屏显示器需要自行旋转；
/// 这里保持纯函数，方便单测覆盖（贴图与遮罩渲染都依赖它）。
/// </summary>
public static class BgraPixels
{
    /// <summary>旋转后图像的宽高交换关系：90/270 度会交换宽高。</summary>
    public static (int Width, int Height) RotatedSize(int width, int height, DisplayRotation rotation) =>
        rotation is DisplayRotation.Rotate90 or DisplayRotation.Rotate270
            ? (height, width)
            : (width, height);

    /// <summary>把 BGRA 缓冲旋转到屏幕方向，返回新的紧密排列缓冲（stride = width * 4）。</summary>
    public static byte[] Rotate(byte[] source, int width, int height, int sourceStride, DisplayRotation rotation)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (sourceStride < width * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceStride), "行跨度小于 width * 4。");
        }

        if (rotation == DisplayRotation.Identity)
        {
            var copy = new byte[width * height * 4];
            Blit(source, width, height, sourceStride, copy, width, height, width * 4, 0, 0);
            return copy;
        }

        var (targetWidth, targetHeight) = RotatedSize(width, height, rotation);
        var target = new byte[targetWidth * targetHeight * 4];
        int targetStride = targetWidth * 4;

        for (int y = 0; y < height; y++)
        {
            int sourceRow = y * sourceStride;
            for (int x = 0; x < width; x++)
            {
                int sourceIndex = sourceRow + (x * 4);

                (int tx, int ty) = rotation switch
                {
                    // 顺时针 90：源 (x, y) → 目标 (height - 1 - y, x)
                    DisplayRotation.Rotate90 => (height - 1 - y, x),
                    // 180：对角翻转
                    DisplayRotation.Rotate180 => (width - 1 - x, height - 1 - y),
                    // 顺时针 270（逆时针 90）：源 (x, y) → 目标 (y, width - 1 - x)
                    DisplayRotation.Rotate270 => (y, width - 1 - x),
                    _ => (x, y),
                };

                int targetIndex = (ty * targetStride) + (tx * 4);
                target[targetIndex] = source[sourceIndex];
                target[targetIndex + 1] = source[sourceIndex + 1];
                target[targetIndex + 2] = source[sourceIndex + 2];
                target[targetIndex + 3] = source[sourceIndex + 3];
            }
        }

        return target;
    }

    /// <summary>
    /// 把一幅 BGRA 图像按偏移贴到更大的画布上（多显示器合成虚拟桌面时使用）。
    /// 只做整行拷贝，不缩放、不插值，保证 1:1 物理像素对齐。
    /// </summary>
    public static void Blit(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        byte[] target,
        int targetWidth,
        int targetHeight,
        int targetStride,
        int offsetX,
        int offsetY)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return;
        }

        if (offsetX < 0 || offsetY < 0 || offsetX + sourceWidth > targetWidth || offsetY + sourceHeight > targetHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(offsetX), $"贴图越界：源 {sourceWidth}x{sourceHeight} @ ({offsetX},{offsetY})，画布 {targetWidth}x{targetHeight}。");
        }

        if (sourceStride < sourceWidth * 4 || targetStride < targetWidth * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceStride), "行跨度小于 width * 4。");
        }

        int rowBytes = sourceWidth * 4;

        for (int y = 0; y < sourceHeight; y++)
        {
            int sourceIndex = y * sourceStride;
            int targetIndex = ((offsetY + y) * targetStride) + (offsetX * 4);

            if (sourceIndex + rowBytes > source.Length || targetIndex + rowBytes > target.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceHeight), "拷贝越界：源或目标缓冲过小。");
            }

            Buffer.BlockCopy(source, sourceIndex, target, targetIndex, rowBytes);
        }
    }
}
