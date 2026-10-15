namespace WhSnap.Core;

/// <summary>
/// 预乘 alpha 的 BGRA 像素合成。
///
/// 为什么需要它：标注里的马赛克/模糊/橡皮擦必须"作用在已经画好的内容上"，
/// 所以渲染管线是"矢量层合成进像素缓冲 → 像素效果直接改缓冲 → 继续画矢量"，
/// 中间那一步就得自己做 source-over。
/// </summary>
public static class PixelBlend
{
    /// <summary>
    /// source-over：把预乘 alpha 的 source 叠加到 destination 上（两者都是 BGRA 排列）。
    /// 公式：dst = src + dst * (1 - srcA)，其中 src 已预乘。
    /// </summary>
    public static void SourceOver(byte[] destination, byte[] source, int length)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);

        int count = Math.Min(Math.Min(length, destination.Length), source.Length);
        count -= count % 4;

        SourceOver(destination, 0, source, 0, count);
    }

    /// <summary>
    /// 带偏移的 source-over（就地合成）：把一行/一块标注像素叠到目标缓冲的对应位置。
    /// 渲染管线里"把标注层贴到合成结果上"就是靠它，避免整块临时数组的来回拷贝。
    /// </summary>
    public static void SourceOver(byte[] destination, int destinationOffset, byte[] source, int sourceOffset, int length)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);

        int count = Math.Min(length, Math.Min(destination.Length - destinationOffset, source.Length - sourceOffset));
        count -= count % 4;

        for (int i = 0; i < count; i += 4)
        {
            int sourceIndex = sourceOffset + i;
            int destinationIndex = destinationOffset + i;
            int alpha = source[sourceIndex + 3];

            if (alpha == 0)
            {
                continue;
            }

            if (alpha == 255)
            {
                destination[destinationIndex] = source[sourceIndex];
                destination[destinationIndex + 1] = source[sourceIndex + 1];
                destination[destinationIndex + 2] = source[sourceIndex + 2];
                destination[destinationIndex + 3] = 255;
                continue;
            }

            int inverse = 255 - alpha;
            destination[destinationIndex] = (byte)Math.Min(source[sourceIndex] + ((destination[destinationIndex] * inverse) / 255), 255);
            destination[destinationIndex + 1] = (byte)Math.Min(source[sourceIndex + 1] + ((destination[destinationIndex + 1] * inverse) / 255), 255);
            destination[destinationIndex + 2] = (byte)Math.Min(source[sourceIndex + 2] + ((destination[destinationIndex + 2] * inverse) / 255), 255);
            destination[destinationIndex + 3] = (byte)Math.Min(alpha + ((destination[destinationIndex + 3] * inverse) / 255), 255);
        }
    }
}

/// <summary>
/// 标注里的像素级效果：马赛克、高斯（盒式近似）模糊、橡皮擦。
///
/// 三者都是纯函数式地修改一块 BGRA 缓冲，不依赖 UI，因此可以单测——
/// 「马赛克真的按块取了均值」「模糊在纯色区域不改变画面」「橡皮擦只清掉笔迹覆盖的像素」
/// 这些都能用像素断言证明。
/// </summary>
public static class PixelEffects
{
    public const int DefaultMosaicBlock = 9;
    public const int DefaultBlurRadius = 8;

    /// <summary>把区域裁剪到图像范围内（区域可能因为拖拽超出边界）。</summary>
    public static PhysicalRect ClampRegion(PhysicalRect region, int width, int height)
    {
        int left = Math.Clamp(Math.Min(region.X, region.Right), 0, width);
        int right = Math.Clamp(Math.Max(region.X, region.Right), 0, width);
        int top = Math.Clamp(Math.Min(region.Y, region.Bottom), 0, height);
        int bottom = Math.Clamp(Math.Max(region.Y, region.Bottom), 0, height);
        return new PhysicalRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// 马赛克：把区域按 blockSize 分块，每块用块内 BGR 均值填充（块从区域左上角开始对齐）。
    /// 不破坏 alpha。
    /// </summary>
    public static void Mosaic(byte[] pixels, int width, int height, int stride, PhysicalRect region, int blockSize)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        blockSize = Math.Max(blockSize, 2);
        var area = ClampRegion(region, width, height);
        if (area.IsEmpty)
        {
            return;
        }

        for (int blockTop = area.Y; blockTop < area.Bottom; blockTop += blockSize)
        {
            for (int blockLeft = area.X; blockLeft < area.Right; blockLeft += blockSize)
            {
                int blockRight = Math.Min(blockLeft + blockSize, area.Right);
                int blockBottom = Math.Min(blockTop + blockSize, area.Bottom);
                long blue = 0;
                long green = 0;
                long red = 0;
                int count = 0;

                for (int y = blockTop; y < blockBottom; y++)
                {
                    int row = y * stride;
                    for (int x = blockLeft; x < blockRight; x++)
                    {
                        int index = row + (x * 4);
                        blue += pixels[index];
                        green += pixels[index + 1];
                        red += pixels[index + 2];
                        count++;
                    }
                }

                if (count == 0)
                {
                    continue;
                }

                byte averageBlue = (byte)(blue / count);
                byte averageGreen = (byte)(green / count);
                byte averageRed = (byte)(red / count);

                for (int y = blockTop; y < blockBottom; y++)
                {
                    int row = y * stride;
                    for (int x = blockLeft; x < blockRight; x++)
                    {
                        int index = row + (x * 4);
                        pixels[index] = averageBlue;
                        pixels[index + 1] = averageGreen;
                        pixels[index + 2] = averageRed;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 盒式模糊（可分离两趟，等价于一次近似高斯）。边界按钳制取样，不引入暗边。
    /// radius ≤ 0 时不做任何事。
    /// </summary>
    public static void BoxBlur(byte[] pixels, int width, int height, int stride, PhysicalRect region, int radius)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        radius = Math.Clamp(radius, 0, 64);
        if (radius == 0)
        {
            return;
        }

        var area = ClampRegion(region, width, height);
        if (area.IsEmpty)
        {
            return;
        }

        // 模糊会从区域外取样，因此实际处理范围要外扩 radius（并裁到图像内）
        var working = ClampRegion(new PhysicalRect(area.X - radius, area.Y - radius, area.Width + (radius * 2), area.Height + (radius * 2)), width, height);
        int workingWidth = working.Width;
        int workingHeight = working.Height;
        int window = (radius * 2) + 1;

        var buffer = new byte[workingWidth * workingHeight * 4];
        for (int y = 0; y < workingHeight; y++)
        {
            Buffer.BlockCopy(pixels, ((working.Y + y) * stride) + (working.X * 4), buffer, y * workingWidth * 4, workingWidth * 4);
        }

        var horizontal = new byte[buffer.Length];
        var vertical = new byte[buffer.Length];

        // 横向
        for (int y = 0; y < workingHeight; y++)
        {
            int row = y * workingWidth * 4;

            for (int channel = 0; channel < 4; channel++)
            {
                int sum = 0;

                for (int x = -radius; x <= radius; x++)
                {
                    int sampleX = Math.Clamp(x, 0, workingWidth - 1);
                    sum += buffer[row + (sampleX * 4) + channel];
                }

                for (int x = 0; x < workingWidth; x++)
                {
                    horizontal[row + (x * 4) + channel] = (byte)(sum / window);

                    int outX = Math.Clamp(x - radius, 0, workingWidth - 1);
                    int inX = Math.Clamp(x + radius + 1, 0, workingWidth - 1);
                    sum += buffer[row + (inX * 4) + channel] - buffer[row + (outX * 4) + channel];
                }
            }
        }

        // 纵向
        for (int x = 0; x < workingWidth; x++)
        {
            for (int channel = 0; channel < 4; channel++)
            {
                int sum = 0;

                for (int y = -radius; y <= radius; y++)
                {
                    int sampleY = Math.Clamp(y, 0, workingHeight - 1);
                    sum += horizontal[((sampleY * workingWidth) + x) * 4 + channel];
                }

                for (int y = 0; y < workingHeight; y++)
                {
                    vertical[((y * workingWidth) + x) * 4 + channel] = (byte)(sum / window);

                    int outY = Math.Clamp(y - radius, 0, workingHeight - 1);
                    int inY = Math.Clamp(y + radius + 1, 0, workingHeight - 1);
                    sum += horizontal[((inY * workingWidth) + x) * 4 + channel] - horizontal[((outY * workingWidth) + x) * 4 + channel];
                }
            }
        }

        for (int y = 0; y < workingHeight; y++)
        {
            Buffer.BlockCopy(vertical, y * workingWidth * 4, pixels, ((working.Y + y) * stride) + (working.X * 4), workingWidth * 4);
        }
    }

    /// <summary>
    /// 橡皮擦：沿折线把半径内的像素**清成完全透明**（alpha=0）。
    /// 语义是"擦掉之前画上去的标注像素"，而不是在底图上涂白——底图必须保持可复原。
    /// </summary>
    public static void EraseStroke(
        byte[] pixels,
        int width,
        int height,
        int stride,
        IReadOnlyList<PhysicalPoint> points,
        int radius)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count == 0)
        {
            return;
        }

        radius = Math.Max(radius, 1);

        if (points.Count == 1)
        {
            ClearCircle(pixels, width, height, stride, points[0], radius);
            return;
        }

        for (int i = 0; i < points.Count - 1; i++)
        {
            ClearSegment(pixels, width, height, stride, points[i], points[i + 1], radius);
        }
    }

    private static void ClearSegment(
        byte[] pixels,
        int width,
        int height,
        int stride,
        PhysicalPoint from,
        PhysicalPoint to,
        int radius)
    {
        int left = Math.Max(Math.Min(from.X, to.X) - radius, 0);
        int right = Math.Min(Math.Max(from.X, to.X) + radius, width - 1);
        int top = Math.Max(Math.Min(from.Y, to.Y) - radius, 0);
        int bottom = Math.Min(Math.Max(from.Y, to.Y) + radius, height - 1);

        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        double radiusSquared = radius * radius;

        for (int y = top; y <= bottom; y++)
        {
            int row = y * stride;

            for (int x = left; x <= right; x++)
            {
                double distanceSquared;

                if (lengthSquared < 1e-9)
                {
                    double px = x - from.X;
                    double py = y - from.Y;
                    distanceSquared = (px * px) + (py * py);
                }
                else
                {
                    double t = (((x - from.X) * dx) + ((y - from.Y) * dy)) / lengthSquared;
                    t = Math.Clamp(t, 0, 1);
                    double nearestX = from.X + (t * dx);
                    double nearestY = from.Y + (t * dy);
                    double px = x - nearestX;
                    double py = y - nearestY;
                    distanceSquared = (px * px) + (py * py);
                }

                if (distanceSquared <= radiusSquared)
                {
                    int index = row + (x * 4);
                    pixels[index] = 0;
                    pixels[index + 1] = 0;
                    pixels[index + 2] = 0;
                    pixels[index + 3] = 0;
                }
            }
        }
    }

    private static void ClearCircle(byte[] pixels, int width, int height, int stride, PhysicalPoint center, int radius)
    {
        int left = Math.Max(center.X - radius, 0);
        int right = Math.Min(center.X + radius, width - 1);
        int top = Math.Max(center.Y - radius, 0);
        int bottom = Math.Min(center.Y + radius, height - 1);
        double radiusSquared = radius * radius;

        for (int y = top; y <= bottom; y++)
        {
            int row = y * stride;

            for (int x = left; x <= right; x++)
            {
                double px = x - center.X;
                double py = y - center.Y;

                if ((px * px) + (py * py) <= radiusSquared)
                {
                    int index = row + (x * 4);
                    pixels[index] = 0;
                    pixels[index + 1] = 0;
                    pixels[index + 2] = 0;
                    pixels[index + 3] = 0;
                }
            }
        }
    }
}
