using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WhSnap.Services;

/// <summary>规划 §2.2 的四个性能指标。</summary>
internal enum PerfMetric
{
    /// <summary>热键 → 遮罩可交互（≤ 200 ms）。</summary>
    HotkeyToOverlay,

    /// <summary>遮罩内鼠标跟随延迟（≤ 16 ms，帧时间统计）。</summary>
    MouseFollow,

    /// <summary>确认 → 剪贴板可粘贴（4K 单屏，≤ 400 ms，PNG 编码计入）。</summary>
    ConfirmToClipboard,

    /// <summary>冷启动 → 托盘就绪（≤ 1.5 s）。</summary>
    ColdStartToTray,
}

/// <summary>一个指标的元信息：中文名、规划门槛、防回归红线。</summary>
internal sealed record PerfMetricInfo(PerfMetric Metric, string Display, double TargetMs, double RegressionCeilingMs);

/// <summary>
/// 性能埋点（M6 第 7 项）。对应规划 §2.2 里之前挂着"待埋点"的四项。
///
/// 设计取舍：
///   * **只在内存里攒样本，不写日志**：鼠标跟随每次移动都记一条，写日志会把盘写爆；
///   * 需要跨进程看的只有冷启动一项，所以提供 <see cref="Persist"/> / <see cref="Report"/>——
///     报告同时展示"上次运行持久化的值"，这样 `--selftest` 与诊断包里也能看到真实冷启动数字；
///   * **超标不失败**：规划门槛只用于标注"达标/超标"，另设明显宽松的防回归红线，
///     只在自检里守"不许比目标慢好几倍"（防止后续改动把性能拖垮还不知道）。
/// </summary>
internal static class PerfTrace
{
    /// <summary>每个指标最多保留多少样本（鼠标跟随很密集，取中位数足够）。</summary>
    public const int MaxSamplesPerMetric = 200;

    /// <summary>未完成的起点超过这个时间就丢弃（避免跨会话的陈旧起点算出离谱值）。</summary>
    private static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(30);

    private static readonly object Gate = new();
    private static readonly Dictionary<PerfMetric, List<double>> Samples = [];
    private static readonly Dictionary<PerfMetric, string?> LastNotes = [];
    private static readonly Dictionary<PerfMetric, long> PendingStarts = [];

    /// <summary>指标顺序、中文名、规划门槛、防回归红线。</summary>
    public static readonly IReadOnlyList<PerfMetricInfo> Metrics =
    [
        new(PerfMetric.HotkeyToOverlay, "热键 → 遮罩可交互", 200, 1000),
        new(PerfMetric.MouseFollow, "遮罩内鼠标跟随延迟", 16, 100),
        new(PerfMetric.ConfirmToClipboard, "确认 → 剪贴板可粘贴（4K）", 400, 2000),
        new(PerfMetric.ColdStartToTray, "冷启动 → 托盘就绪", 1500, 5000),
    ];

    public static string PersistedFilePath => Path.Combine(SettingsService.ConfigDirectory, "performance.json");

    /// <summary>本进程的起点（用 Process.StartTime，比"OnStartup 第一行"更接近真实冷启动）。</summary>
    public static DateTime SessionStartUtc { get; private set; } = DateTime.UtcNow;

    public static void StartSession()
    {
        try
        {
            SessionStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch (Exception ex)
        {
            Log.Warn($"读进程启动时间失败，改用当前时间：{ex.Message}");
            SessionStartUtc = DateTime.UtcNow;
        }
    }

    public static PerfMetricInfo Info(PerfMetric metric) => Metrics.First(info => info.Metric == metric);

    /// <summary>记一条样本。</summary>
    public static void Record(PerfMetric metric, TimeSpan elapsed, string? note = null) =>
        Record(metric, elapsed.TotalMilliseconds, note);

    public static void Record(PerfMetric metric, double milliseconds, string? note = null)
    {
        if (double.IsNaN(milliseconds) || milliseconds < 0)
        {
            return;
        }

        lock (Gate)
        {
            if (!Samples.TryGetValue(metric, out var list))
            {
                list = [];
                Samples[metric] = list;
            }

            list.Add(milliseconds);

            if (list.Count > MaxSamplesPerMetric)
            {
                list.RemoveRange(0, list.Count - MaxSamplesPerMetric);
            }

            LastNotes[metric] = note;
        }
    }

    /// <summary>从"进程启动"起算记一条（冷启动用）。</summary>
    public static void RecordSinceSessionStart(PerfMetric metric, string? note = null) =>
        Record(metric, DateTime.UtcNow - SessionStartUtc, note);

    /// <summary>开一个起点（真实路径用：热键派发、确认动作开始）。同一指标只保留最后一个未完成起点。</summary>
    public static void BeginMetric(PerfMetric metric)
    {
        lock (Gate)
        {
            PendingStarts[metric] = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>结束起点并记样本；没有起点（或起点过期）时什么都不做。</summary>
    public static void EndMetric(PerfMetric metric, string? note = null)
    {
        long start;
        double elapsedMs;

        lock (Gate)
        {
            if (!PendingStarts.Remove(metric, out start))
            {
                return;
            }

            elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (elapsedMs > PendingTimeout.TotalMilliseconds)
            {
                return;
            }
        }

        Record(metric, elapsedMs, note);
    }

    /// <summary>丢弃未完成的起点（不记样本）。例如"确认"之后用户选的是保存/贴图，没有剪贴板动作。</summary>
    public static void CancelMetric(PerfMetric metric)
    {
        lock (Gate)
        {
            PendingStarts.Remove(metric);
        }
    }

    /// <summary>
    /// 有未完成起点就用它算（真实路径：热键 → 创建 → 显示全程）；
    /// 没有起点就用 <paramref name="fallback"/> 记一条（自检直接构造会话，没有热键往返）。
    /// </summary>
    public static void EndMetricOrRecord(PerfMetric metric, TimeSpan fallback, string fallbackNote)
    {
        lock (Gate)
        {
            if (PendingStarts.ContainsKey(metric))
            {
                EndMetric(metric);
                return;
            }
        }

        Record(metric, fallback, fallbackNote);
    }

    public static bool HasSamples(PerfMetric metric)
    {
        lock (Gate)
        {
            return Samples.TryGetValue(metric, out var list) && list.Count > 0;
        }
    }

    public static int SampleCount(PerfMetric metric)
    {
        lock (Gate)
        {
            return Samples.TryGetValue(metric, out var list) ? list.Count : 0;
        }
    }

    /// <summary>指标统计：样本数 / 最近 / 中位 / 最大。</summary>
    public static (int Count, double Last, double Median, double Max) Stats(PerfMetric metric)
    {
        lock (Gate)
        {
            if (!Samples.TryGetValue(metric, out var list) || list.Count == 0)
            {
                return (0, 0, 0, 0);
            }

            var sorted = list.ToList();
            sorted.Sort();
            double median = sorted.Count % 2 == 1
                ? sorted[sorted.Count / 2]
                : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;

            return (list.Count, list[^1], median, sorted[^1]);
        }
    }

    public static double? Median(PerfMetric metric)
    {
        var (count, _, median, _) = Stats(metric);
        return count == 0 ? null : median;
    }

    public static string? LastNote(PerfMetric metric)
    {
        lock (Gate)
        {
            return LastNotes.TryGetValue(metric, out string? note) ? note : null;
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            Samples.Clear();
            LastNotes.Clear();
            PendingStarts.Clear();
        }
    }

    /// <summary>
    /// 把当前样本落盘，供下次运行（自检/诊断包）读到真实冷启动等数字。
    /// **按指标合并**：本进程没采到的指标保留文件里已有的值——否则跑一次自检就会把
    /// 上次真实运行的冷启动数字冲掉。
    /// </summary>
    public static void Persist()
    {
        try
        {
            var (_, existingSamples, existingNotes) = ReadPersisted();
            var payload = new Dictionary<string, double[]>();
            var notes = new Dictionary<string, string?>();

            foreach (PerfMetric metric in Enum.GetValues<PerfMetric>())
            {
                lock (Gate)
                {
                    if (Samples.TryGetValue(metric, out var list) && list.Count > 0)
                    {
                        payload[metric.ToString()] = list.ToArray();
                        notes[metric.ToString()] = LastNotes.TryGetValue(metric, out string? note) ? note : null;
                    }
                    else if (existingSamples.TryGetValue(metric, out var old) && old.Length > 0)
                    {
                        payload[metric.ToString()] = old;
                        notes[metric.ToString()] = existingNotes.TryGetValue(metric, out string? oldNote) ? oldNote : null;
                    }
                }
            }

            var document = new Dictionary<string, object?>
            {
                ["savedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["version"] = UpdateService.GetCurrentVersion(),
                ["samples"] = payload,
                ["notes"] = notes,
            };

            Directory.CreateDirectory(SettingsService.ConfigDirectory);
            File.WriteAllText(PersistedFilePath, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn($"性能样本落盘失败：{ex.Message}");
        }
    }

    /// <summary>读上次运行持久化的样本（没有就返回空）。</summary>
    public static (DateTime SavedAtUtc, Dictionary<PerfMetric, double[]> Samples, Dictionary<PerfMetric, string?> Notes) ReadPersisted()
    {
        var samples = new Dictionary<PerfMetric, double[]>();
        var notes = new Dictionary<PerfMetric, string?>();

        try
        {
            if (!File.Exists(PersistedFilePath))
            {
                return (DateTime.MinValue, samples, notes);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(PersistedFilePath));
            var root = document.RootElement;

            DateTime savedAt = root.TryGetProperty("savedAtUtc", out var saved)
                && DateTime.TryParse(saved.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed.ToUniversalTime()
                    : DateTime.MinValue;

            if (root.TryGetProperty("samples", out var sampleElement))
            {
                foreach (var property in sampleElement.EnumerateObject())
                {
                    if (!Enum.TryParse(property.Name, out PerfMetric metric) || property.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    samples[metric] = property.Value.EnumerateArray().Select(item => item.GetDouble()).ToArray();
                }
            }

            if (root.TryGetProperty("notes", out var noteElement))
            {
                foreach (var property in noteElement.EnumerateObject())
                {
                    if (Enum.TryParse(property.Name, out PerfMetric metric))
                    {
                        notes[metric] = property.Value.GetString();
                    }
                }
            }

            return (savedAt, samples, notes);
        }
        catch (Exception ex)
        {
            Log.Warn($"读性能样本失败：{ex.Message}");
            return (DateTime.MinValue, samples, notes);
        }
    }

    /// <summary>给自检/气泡用的一行摘要。</summary>
    public static IReadOnlyList<string> Summarize()
    {
        var lines = new List<string>();

        foreach (var info in Metrics)
        {
            var (count, _, median, _) = Stats(info.Metric);
            lines.Add(count == 0
                ? $"{info.Display}=未采样（规划门槛 ≤ {FormatTarget(info.TargetMs)}）"
                : $"{info.Display}={median:0.#} ms（{count} 样本，门槛 ≤ {FormatTarget(info.TargetMs)}，{(median <= info.TargetMs ? "达标" : "超标")}）");
        }

        return lines;
    }

    /// <summary>完整文本报告（进诊断包、自检打印）。用竖线分隔，避免中文宽度把表格挤歪。</summary>
    public static string Report()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"WhSnap 性能埋点报告　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"版本 {UpdateService.GetCurrentVersion()}　进程 PID {Environment.ProcessId}　本进程起点 {SessionStartUtc.ToLocalTime():HH:mm:ss.fff}");
        builder.AppendLine(new string('-', 88));
        builder.AppendLine("指标 | 样本 | 最近 | 中位 | 最大 | 规划门槛 | 超标");
        builder.AppendLine(new string('-', 88));

        var persisted = ReadPersisted();

        foreach (var info in Metrics)
        {
            var (count, last, median, max) = Stats(info.Metric);
            string target = "≤ " + FormatTarget(info.TargetMs);

            if (count == 0)
            {
                string fallback = persisted.Samples.TryGetValue(info.Metric, out var old) && old.Length > 0
                    ? $"　（已持久化：中位 {Median(old):0.#} ms）"
                    : string.Empty;

                builder.AppendLine($"{info.Display} | 未采样 | - | - | - | {target} | -{fallback}");
                continue;
            }

            string verdict = median <= info.TargetMs ? "否" : "是";

            if (max > info.TargetMs)
            {
                verdict += $"（最大 {max:0.#} ms 超门槛，可能是单帧抖动）";
            }

            builder.AppendLine(
                $"{info.Display} | {count} | {last:0.#} ms | {median:0.#} ms | {max:0.#} ms | {target} | {verdict}");

            string? note = LastNote(info.Metric);

            if (!string.IsNullOrWhiteSpace(note))
            {
                builder.AppendLine($"　说明：{note}");
            }
        }

        builder.AppendLine(new string('-', 88));

        var persistedLines = Metrics
            .Where(info => !HasSamples(info.Metric)
                           && persisted.Samples.TryGetValue(info.Metric, out var old)
                           && old.Length > 0)
            .Select(info => $"  {info.Display}：{Median(persisted.Samples[info.Metric]):0.#} ms"
                            + $"（{persisted.Samples[info.Metric].Length} 样本，{(Median(persisted.Samples[info.Metric]) <= info.TargetMs ? "达标" : "超标")}）")
            .ToList();

        if (persisted.SavedAtUtc != DateTime.MinValue && persistedLines.Count > 0)
        {
            builder.AppendLine($"持久化样本（上次落盘 {persisted.SavedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}；每个指标保留最近一次采到样本的那次运行）：");
            persistedLines.ForEach(line => builder.AppendLine(line));
            builder.AppendLine(new string('-', 88));
        }

        builder.AppendLine("说明：样本只来自本机；鼠标跟随为事件处理同步耗时（UIA 元素高亮的异步到达时间由 --element-test 单独统计）；");
        builder.AppendLine("      「确认 → 剪贴板」含裁剪 + PNG 编码 + 剪贴板写入，自检里用 4K 合成图测量；");
        builder.AppendLine("      规划门槛只用于标注达标/超标，不判失败；防回归红线见 PerfTrace.Metrics（宽松，仅防性能被拖垮）。");
        return builder.ToString();
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(value => value).ToArray();
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static string FormatTarget(double targetMs) =>
        targetMs >= 1000 ? $"{targetMs / 1000:0.##} s" : $"{targetMs:0.#} ms";
}
