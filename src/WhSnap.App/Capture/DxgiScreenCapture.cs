using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WhSnap.Core;

namespace WhSnap.Capture;

/// <summary>
/// DXGI Desktop Duplication 采集后端。
///
/// 为什么放在 GDI 之前：Duplication 是同步的，能一次性拿到每个输出（显示器）的当前帧，
/// 非常适合"冻结屏幕"这类单帧需求，也不受 GDI 抓不到硬件叠加层的影响。
///
/// 已知限制（会在 Notes 里如实报告，必要时抛 CaptureNotSupportedException 触发降级）：
///   * 受 DRM 保护的内容被系统屏蔽（OutduplFrameInfo.ProtectedContentMaskedOut）
///   * HDR/宽色域输出返回 10-bit 或浮点格式，本后端只处理 B8G8R8A8_UNORM
///   * 独占全屏 / 切换分辨率 / 锁屏期间 DuplicateOutput 可能失效
/// </summary>
internal sealed class DxgiScreenCapture : IScreenCapture
{
    private const int AcquireTimeoutMs = 300;
    private const int AcquireAttempts = 5;

    public string Name => CaptureBackendNames.Dxgi;

    public CaptureFrame Capture()
    {
        var stopwatch = Stopwatch.StartNew();
        var monitors = MonitorEnumerator.Enumerate();
        var bounds = VirtualDesktop.BoundsOf(monitors);

        var canvas = new byte[bounds.Width * 4 * bounds.Height];
        var notes = new List<string>();
        var state = new CaptureState();

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var adapterResult = factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter);
            if (adapterResult.Failure)
            {
                break;
            }

            using (adapter)
            {
                if (!HasAttachedOutput(adapter))
                {
                    continue;
                }

                var deviceResult = D3D11.D3D11CreateDevice(
                    adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    null!, // null 表示让 D3D 自行选择特性级别（显式列出会因不支持 11.1 而失败）
                    out ID3D11Device device,
                    out ID3D11DeviceContext context);

                if (deviceResult.Failure || device is null || context is null)
                {
                    notes.Add($"适配器 {adapterIndex} 创建 D3D11 设备失败（0x{deviceResult.Code:X8}）");
                    continue;
                }

                using (device)
                using (context)
                {
                    CaptureAdapter(adapter, device, context, bounds, canvas, notes, state);
                }
            }
        }

        if (state.CapturedOutputs == 0)
        {
            string detail = notes.Count == 0 ? string.Empty : " 原因：" + string.Join("；", notes);

            // 所有可见输出都只因"像素格式不支持"而失败，说明是环境问题（HDR），不是缺陷
            if (state.EligibleOutputs > 0 && state.UnsupportedFormatOutputs == state.EligibleOutputs)
            {
                throw new CaptureNotSupportedException("DXGI 后端不支持当前显示输出的像素格式（疑似 HDR/宽色域）。" + detail);
            }

            if (state.EligibleOutputs == 0)
            {
                throw new CaptureNotSupportedException("没有连接到桌面的 DXGI 输出。" + detail);
            }

            throw new InvalidOperationException("DXGI 未捕获到任何显示器画面。" + detail);
        }

        var image = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96, PixelFormats.Bgra32, null, canvas, bounds.Width * 4);
        image.Freeze();

        // 抓到纯黑/纯白图说明这个环境其实没能提供桌面画面，按"环境不支持"上报以便自动降级
        if (!FrameSanity.LooksLikeRealContent(image, out string sanity))
        {
            throw new CaptureNotSupportedException($"DXGI 抓到的画面完全均匀（{sanity}），当前显示适配器可能未正确支持 Desktop Duplication。");
        }

        return new CaptureFrame(
            image,
            bounds,
            monitors,
            Name,
            stopwatch.Elapsed,
            state.ProtectedContentMasked,
            notes.Count == 0 ? null : string.Join("；", notes));
    }

    private sealed class CaptureState
    {
        public int CapturedOutputs;
        public int EligibleOutputs;
        public int UnsupportedFormatOutputs;
        public bool ProtectedContentMasked;
    }

    private static bool HasAttachedOutput(IDXGIAdapter1 adapter)
    {
        for (uint index = 0; ; index++)
        {
            var result = adapter.EnumOutputs(index, out IDXGIOutput output);
            if (result.Failure)
            {
                return false;
            }

            using (output)
            {
                if (output.Description.AttachedToDesktop)
                {
                    return true;
                }
            }
        }
    }

    private static void CaptureAdapter(
        IDXGIAdapter1 adapter,
        ID3D11Device device,
        ID3D11DeviceContext context,
        PhysicalRect bounds,
        byte[] canvas,
        List<string> notes,
        CaptureState state)
    {
        for (uint outputIndex = 0; ; outputIndex++)
        {
            var outputResult = adapter.EnumOutputs(outputIndex, out IDXGIOutput output);
            if (outputResult.Failure)
            {
                break;
            }

            using (output)
            {
                var description = output.Description;
                if (!description.AttachedToDesktop)
                {
                    continue;
                }

                state.EligibleOutputs++;

                var desktop = description.DesktopCoordinates;
                var desktopRect = new PhysicalRect(desktop.Left, desktop.Top, desktop.Right - desktop.Left, desktop.Bottom - desktop.Top);
                var rotation = (DisplayRotation)(uint)description.Rotation;

                try
                {
                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                    using var duplication = output1.DuplicateOutput(device);

                    if (!TryAcquireFrame(duplication, out IDXGIResource? resource, out OutduplFrameInfo frameInfo, out string acquireError))
                    {
                        notes.Add($"输出 {outputIndex} 取帧失败：{acquireError}");
                        continue;
                    }

                    try
                    {
                        if (frameInfo.ProtectedContentMaskedOut)
                        {
                            state.ProtectedContentMasked = true;
                        }

                        using var texture = resource!.QueryInterface<ID3D11Texture2D>();
                        var textureDescription = texture.Description;
                        uint format = (uint)textureDescription.Format;

                        if (!D3D11TextureReader.IsBgra8(format))
                        {
                            state.UnsupportedFormatOutputs++;
                            notes.Add($"输出 {outputIndex} 像素格式为 DXGI_FORMAT {format}（HDR/宽色域），DXGI 后端未支持");
                            continue;
                        }

                        byte[] frame = D3D11TextureReader.ReadBgra(device, context, texture, textureDescription);

                        var effectiveRotation = ResolveRotation(
                            (int)textureDescription.Width,
                            (int)textureDescription.Height,
                            desktopRect,
                            rotation,
                            notes,
                            outputIndex);

                        string? composeNote = D3D11TextureReader.Compose(
                            frame,
                            (int)textureDescription.Width,
                            (int)textureDescription.Height,
                            desktopRect,
                            effectiveRotation,
                            bounds,
                            canvas);

                        if (!string.IsNullOrWhiteSpace(composeNote))
                        {
                            notes.Add($"输出 {outputIndex}：{composeNote}");
                        }

                        state.CapturedOutputs++;
                    }
                    finally
                    {
                        duplication.ReleaseFrame();
                    }
                }
                catch (Exception ex)
                {
                    notes.Add($"输出 {outputIndex} 失败：{ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// 判定实际需要应用的旋转。
    /// 不能盲信 OutputDescription.Rotation：实测某些虚拟显示器会报告 Rotate90 而纹理本身
    /// 已经是桌面方向。这里以"纹理尺寸 vs 桌面矩形尺寸"为准：
    ///   * 尺寸已一致 → 不旋转
    ///   * 尺寸互为转置 → 需要旋转（以报告的旋转角为准，Identity 时按顺时针 90 处理）
    ///   * 两者都不是 → 按原样合成并记录诊断
    /// </summary>
    private static DisplayRotation ResolveRotation(
        int textureWidth,
        int textureHeight,
        PhysicalRect desktop,
        DisplayRotation reported,
        List<string> notes,
        uint outputIndex)
    {
        bool sizeMatches = textureWidth == desktop.Width && textureHeight == desktop.Height;
        if (sizeMatches)
        {
            if (reported != DisplayRotation.Identity)
            {
                notes.Add($"输出 {outputIndex} 报告旋转 {reported}，但纹理 {textureWidth}x{textureHeight} 与桌面矩形一致，按不旋转处理");
            }

            return DisplayRotation.Identity;
        }

        bool transposed = textureHeight == desktop.Width && textureWidth == desktop.Height;
        if (transposed)
        {
            var effective = reported == DisplayRotation.Identity ? DisplayRotation.Rotate90 : reported;
            notes.Add($"输出 {outputIndex} 纹理 {textureWidth}x{textureHeight} 与桌面矩形 {desktop.Width}x{desktop.Height} 为转置关系，按 {effective} 旋转");
            return effective;
        }

        notes.Add($"输出 {outputIndex} 纹理 {textureWidth}x{textureHeight} 与桌面矩形 {desktop.Width}x{desktop.Height} 不匹配且非转置，按原样合成");
        return DisplayRotation.Identity;
    }

    /// <summary>
    /// 取一帧。关键细节：首次 AcquireNextFrame 常常返回 AccumulatedFrames = 0 的"空桌面"帧
    /// （内容为纯黑），必须释放后重取，否则会稳定拿到黑图。
    /// </summary>
    private static bool TryAcquireFrame(
        IDXGIOutputDuplication duplication,
        out IDXGIResource? resource,
        out OutduplFrameInfo frameInfo,
        out string error)
    {
        resource = null;
        frameInfo = default;
        error = string.Empty;

        for (int attempt = 1; attempt <= AcquireAttempts; attempt++)
        {
            // 屏幕静止时不产生新帧，超时属正常，重试即可
            var result = duplication.AcquireNextFrame(AcquireTimeoutMs, out frameInfo, out resource);
            if (result.Failure || resource is null)
            {
                error = $"AcquireNextFrame 0x{result.Code:X8}（第 {attempt}/{AcquireAttempts} 次）";
                continue;
            }

            if (frameInfo.AccumulatedFrames == 0)
            {
                // 空帧：必须释放并重新取，累计帧数 > 0 才代表画面已经合成进来
                duplication.ReleaseFrame();
                resource.Dispose();
                resource = null;
                error = $"第 {attempt}/{AcquireAttempts} 次取到 AccumulatedFrames=0 的空帧";
                continue;
            }

            return true;
        }

        return false;
    }
}
