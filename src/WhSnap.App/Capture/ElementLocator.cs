using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using WhSnap.Core;
using WhSnap.Interop;
using WhSnap.Services;

namespace WhSnap.Capture;

/// <summary>
/// 元素检测（规划 §2.1 A「自动检测窗口/界面元素区域」）：
/// 主动路径是 UI Automation（能拿到按钮/输入框这类真实控件），
/// 回退路径是顶层窗口矩形（DWM 扩展边框，比 GetWindowRect 更准，不含投影透明边）。
///
/// 性能与可靠性上的两个保护：
///   * UIA 调用放在线程池线程上并设超时——目标程序卡住时不能把我们的界面一起卡死；
///   * 候选做去重（UIA 与窗口矩形经常给出同一个矩形）。
/// </summary>
internal static class ElementLocator
{
    /// <summary>UIA 向上遍历的最大层级（再往上就是窗口/桌面，没有价值）。</summary>
    private const int MaxUiaDepth = 8;

    /// <summary>候选数量上限，避免重窗口环境下候选列表爆炸。</summary>
    private const int MaxCandidates = 16;

    /// <summary>
    /// 异步查询光标下的元素候选。UIA 部分带超时，超时就用窗口矩形候选兜底。
    /// </summary>
    public static Task<IReadOnlyList<ElementCandidate>> FindAsync(PhysicalPoint point, bool useUia, int timeoutMs = 400)
    {
        return Task.Run<IReadOnlyList<ElementCandidate>>(() =>
        {
            var candidates = new List<ElementCandidate>();

            if (useUia)
            {
                try
                {
                    var task = Task.Run(() => FromUia(point).ToList());

                    if (task.Wait(timeoutMs))
                    {
                        candidates.AddRange(task.Result);
                    }
                    else
                    {
                        Log.Warn("UI Automation 查询超时，本次仅使用窗口矩形候选。");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"UI Automation 查询失败，退回窗口矩形：{ex.GetType().Name}: {ex.Message}");
                }
            }

            try
            {
                candidates.AddRange(FromWindows(point));
            }
            catch (Exception ex)
            {
                Log.Warn($"枚举窗口失败：{ex.Message}");
            }

            return ElementDetection.Distinct(candidates).Take(MaxCandidates).ToList();
        });
    }

    /// <summary>同步查询（自检与快捷键路径用；内部同样带超时）。</summary>
    public static IReadOnlyList<ElementCandidate> Find(PhysicalPoint point, bool useUia, int timeoutMs = 300) =>
        FindAsync(point, useUia, timeoutMs).GetAwaiter().GetResult();

    /// <summary>UI Automation：从光标处元素逐级向上取父级，天然就是"由小到大"的候选序列。</summary>
    private static IEnumerable<ElementCandidate> FromUia(PhysicalPoint point)
    {
        AutomationElement? element = null;

        try
        {
            element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
        }
        catch (Exception ex)
        {
            Log.Warn($"AutomationElement.FromPoint 失败：{ex.GetType().Name}: {ex.Message}");
            yield break;
        }

        int depth = 0;

        while (element is not null && depth < MaxUiaDepth)
        {
            ElementCandidate? candidate = null;

            try
            {
                var bounds = element.Current.BoundingRectangle;
                string name = element.Current.Name ?? string.Empty;
                string kind = element.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", string.Empty, StringComparison.Ordinal) ?? "Element";

                if (!bounds.IsEmpty && bounds.Width >= 1 && bounds.Height >= 1)
                {
                    candidate = new ElementCandidate(
                        new PhysicalRect(
                            (int)Math.Round(bounds.X),
                            (int)Math.Round(bounds.Y),
                            Math.Max((int)Math.Round(bounds.Width), 1),
                            Math.Max((int)Math.Round(bounds.Height), 1)),
                        string.IsNullOrWhiteSpace(name) ? kind : $"{kind}：{name}",
                        "UIA",
                        depth);
                }
            }
            catch (ElementNotAvailableException)
            {
                // 元素在遍历过程中消失了（例如菜单关闭）：结束本次遍历
                yield break;
            }
            catch (Exception ex)
            {
                Log.Warn($"读取 UIA 元素属性失败：{ex.GetType().Name}: {ex.Message}");
                yield break;
            }

            if (candidate is not null)
            {
                yield return candidate;
            }

            AutomationElement? parent;

            try
            {
                parent = TreeWalker.ControlViewWalker.GetParent(element);
            }
            catch (Exception)
            {
                yield break;
            }

            element = parent;
            depth++;
        }
    }

    /// <summary>窗口矩形回退：可见、未最小化、非 cloaked 的顶层窗口，且包含该点或与光标所在窗口同源。</summary>
    private static IEnumerable<ElementCandidate> FromWindows(PhysicalPoint point)
    {
        var results = new List<ElementCandidate>();
        uint ownProcess = (uint)Environment.ProcessId;
        IntPtr foreground = NativeMethods.GetForegroundWindow();

        NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
                {
                    return true;
                }

                // 跳过我们自己的窗口（遮罩/贴图/设置），否则会一直选到自己
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
                if (processId == ownProcess)
                {
                    return true;
                }

                // 跳过 cloaked（UWP 挂起窗口、虚拟桌面之外的窗口）
                if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                {
                    return true;
                }

                // 优先用 DWM 扩展边框（不含不可见阴影边距）
                NativeMethods.RECT rect;

                if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<NativeMethods.RECT>()) != 0)
                {
                    if (!NativeMethods.GetWindowRect(hwnd, out rect))
                    {
                        return true;
                    }
                }

                var bounds = new PhysicalRect(rect.Left, rect.Top, rect.Width, rect.Height);
                if (bounds.IsEmpty)
                {
                    return true;
                }

                bool contains = point.X >= bounds.X && point.X < bounds.Right && point.Y >= bounds.Y && point.Y < bounds.Bottom;
                if (!contains)
                {
                    // 不包含光标的窗口也不是完全没用：前台窗口常被拖到光标附近，但会让候选变杂，所以只收包含光标的
                    return true;
                }

                results.Add(new ElementCandidate(bounds, DescribeWindow(hwnd, foreground), "窗口", 100));

                // 有父窗口的（对话框等）由父窗口那条候选代表
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"处理窗口 {hwnd} 时出错：{ex.Message}");
                return true;
            }
        };

        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        return results;
    }

    private static string DescribeWindow(IntPtr hwnd, IntPtr foreground)
    {
        var builder = new StringBuilder(Math.Max(NativeMethods.GetWindowTextLength(hwnd), 1) + 1);
        NativeMethods.GetWindowText(hwnd, builder, builder.Capacity);
        string title = builder.ToString();

        if (string.IsNullOrWhiteSpace(title))
        {
            title = "(无标题窗口)";
        }

        return hwnd == foreground ? $"窗口：{title}（前台）" : $"窗口：{title}";
    }
}
