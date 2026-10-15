using WhSnap.Services;

namespace WhSnap.Capture;

/// <summary>
/// 采集后端工厂：按优先级依次尝试，把每次尝试的结果记录成诊断信息。
/// 任一后端抛异常都不会让截图功能整体失效——这是"能截到图"比"用最好的后端"更重要。
/// </summary>
internal static class ScreenCaptureFactory
{
    public static CaptureFrame Capture(CaptureBackend preferred, out IReadOnlyList<string> diagnostics)
    {
        var attempts = new List<string>();

        foreach (var backend in Order(preferred))
        {
            try
            {
                var frame = Create(backend).Capture();
                attempts.Add($"{CaptureBackendNames.ToName(backend)}: 成功 {frame.Duration.TotalMilliseconds:0} ms");

                // out 参数必须在所有 return 路径上先赋值
                diagnostics = attempts;

                return frame with
                {
                    Notes = CombineNotes(frame.Notes, frame.Backend, attempts),
                };
            }
            catch (Exception ex)
            {
                attempts.Add($"{CaptureBackendNames.ToName(backend)}: 失败（{ex.GetType().Name}: {ex.Message}）");
                Log.Warn($"采集后端 {CaptureBackendNames.ToName(backend)} 失败：{ex.Message}");
            }
        }

        diagnostics = attempts;
        throw new InvalidOperationException("所有采集后端均失败 → " + string.Join("；", attempts));
    }

    public static IScreenCapture Create(CaptureBackend backend) => backend switch
    {
        CaptureBackend.Wgc => new WgcScreenCapture(),
        CaptureBackend.Dxgi => new DxgiScreenCapture(),
        _ => new GdiScreenCapture(),
    };

    /// <summary>
    /// 尝试顺序。Auto 下 WGC 优先：它能拿到 DXGI 拿不到的部分叠加层内容；
    /// DXGI 次之（同步、单帧可靠）；GDI 最后兜底（旋转屏与宽色域最稳）。
    /// </summary>
    public static IReadOnlyList<CaptureBackend> Order(CaptureBackend preferred) => preferred switch
    {
        CaptureBackend.Wgc => [CaptureBackend.Wgc, CaptureBackend.Dxgi, CaptureBackend.Gdi],
        CaptureBackend.Dxgi => [CaptureBackend.Dxgi, CaptureBackend.Wgc, CaptureBackend.Gdi],
        CaptureBackend.Gdi => [CaptureBackend.Gdi],
        _ => [CaptureBackend.Wgc, CaptureBackend.Dxgi, CaptureBackend.Gdi],
    };

    private static string? CombineNotes(string? frameNotes, string backend, List<string> attempts)
    {
        var parts = new List<string>();

        if (attempts.Count > 1)
        {
            parts.Add("降级过程：" + string.Join("；", attempts));
        }

        if (!string.IsNullOrWhiteSpace(frameNotes))
        {
            parts.Add(frameNotes);
        }

        parts.Insert(0, $"实际后端 {backend}");
        return string.Join(" | ", parts);
    }
}
