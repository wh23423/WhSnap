namespace WhSnap.Core;

/// <summary>
/// 快捷键作用域。这是冲突检测能给出正确结论的前提：
/// **全局热键**（`RegisterHotKey`，与整个系统抢）、**遮罩内按键**、**贴图窗口内按键**
/// 三者的命名空间互不干扰——遮罩里的 `Enter` 不会和别的软件的 `Enter` 冲突。
/// </summary>
public enum HotkeyScope
{
    Global,
    Overlay,
    Pin,
    Annotation,
}

/// <summary>一个可配置动作的元数据（默认键位、作用域、分组、说明）。</summary>
public sealed record HotkeyActionDefinition(
    string Id,
    string DisplayName,
    HotkeyScope Scope,
    string Category,
    string? DefaultKeys,
    string Description);

/// <summary>
/// 全部可绑定动作的**唯一数据源**：设置界面、冲突检测、注册服务都从这里取，
/// 每加一个动作只需要在这里加一行，不需要同时改三处。
/// 只收录**已经实现**的动作——不预留"以后会有"的空壳，避免设置里出现调了没反应的开关。
/// </summary>
public static class HotkeyCatalog
{
    public static IReadOnlyList<HotkeyActionDefinition> All { get; } =
    [
        // ---- 全局热键 ----
        new("capture.region", "截图（区域）", HotkeyScope.Global, "截图", "F1",
            "唤起遮罩框选截图。这是最常用的键，建议保留在容易按到的位置。"),
        new("capture.pinClipboard", "贴图剪贴板内容", HotkeyScope.Global, "贴图", "F3",
            "把剪贴板里的图像/文件/文本/颜色值直接贴到屏幕上。"),
        new("pin.toggleAll", "显示 / 隐藏全部贴图", HotkeyScope.Global, "贴图", "Shift+F3",
            "一键把所有贴图收起来或放回来（不关闭，随时恢复）。"),
        new("pin.closeAll", "关闭全部贴图", HotkeyScope.Global, "贴图", null,
            "关闭所有贴图并清空本次备份（默认不绑定，避免误触）。"),
        new("pin.showPanel", "打开贴图管理面板", HotkeyScope.Global, "贴图", null,
            "查看贴图数量、层级与状态，可逐张或批量操作。"),
        new("settings.open", "打开设置", HotkeyScope.Global, "系统", "Ctrl+Shift+,",
            "打开设置窗口。"),

        // ---- 遮罩内按键（只在截图遮罩显示时生效）----
        new("overlay.confirm", "确认截图", HotkeyScope.Overlay, "遮罩", "Enter",
            "按配置执行复制 / 保存 / 贴图（见「截图」分组）。"),
        new("overlay.cancel", "取消截图", HotkeyScope.Overlay, "遮罩", "Esc",
            "放弃本次截图，不产生任何结果。"),
        new("overlay.copy", "复制并确认", HotkeyScope.Overlay, "遮罩", "Ctrl+C",
            "只复制到剪贴板，忽略其它开关。"),
        new("overlay.save", "保存并确认", HotkeyScope.Overlay, "遮罩", "Ctrl+S",
            "只保存文件，忽略其它开关。"),
        new("overlay.pin", "直接贴图", HotkeyScope.Overlay, "遮罩", "F3",
            "把选区直接贴到屏幕上，不进剪贴板、不落盘。"),
        new("overlay.annotate", "进入标注", HotkeyScope.Overlay, "遮罩", "T",
            "打开标注编辑器，在选区内画矩形/箭头/文字/马赛克等（工具键位见「标注」作用域）。"),

        // ---- 标注编辑器（弹窗内生效）----
        new("annotation.confirm", "确认标注", HotkeyScope.Annotation, "标注", "Enter",
            "把标注合成到底图并交给上一步（复制/保存/贴图）。"),
        new("annotation.cancel", "取消标注", HotkeyScope.Annotation, "标注", "Esc",
            "放弃全部标注，回到截图/贴图流程。"),
        new("annotation.undo", "撤销", HotkeyScope.Annotation, "标注", "Ctrl+Z",
            "撤销上一步（图元级命令栈，不是位图快照）。"),
        new("annotation.redo", "重做", HotkeyScope.Annotation, "标注", "Ctrl+Y", string.Empty),
        new("annotation.delete", "删除选中图元", HotkeyScope.Annotation, "标注", "Delete",
            "先用「选择」工具点中一个图元，再按删除。"),

        new("annotation.tool.select", "工具：选择 / 移动", HotkeyScope.Annotation, "标注工具", "V",
            "点中图元可整体拖动、拖角可缩放，双击文字可改内容。"),
        new("annotation.tool.rectangle", "工具：矩形", HotkeyScope.Annotation, "标注工具", "R", "按住拖动即可，可填充。"),
        new("annotation.tool.ellipse", "工具：椭圆", HotkeyScope.Annotation, "标注工具", "O", string.Empty),
        new("annotation.tool.line", "工具：直线", HotkeyScope.Annotation, "标注工具", "L", string.Empty),
        new("annotation.tool.arrow", "工具：箭头", HotkeyScope.Annotation, "标注工具", "A", string.Empty),
        new("annotation.tool.pencil", "工具：铅笔", HotkeyScope.Annotation, "标注工具", "D", "细的不透明自由笔。"),
        new("annotation.tool.marker", "工具：马克笔", HotkeyScope.Annotation, "标注工具", "M", "粗的半透明自由笔。"),
        new("annotation.tool.text", "工具：文字", HotkeyScope.Annotation, "标注工具", "T", "点一下就地输入，Enter 确认。"),
        new("annotation.tool.mosaic", "工具：马赛克", HotkeyScope.Annotation, "标注工具", "G", "框选区域打码，块大小可调。"),
        new("annotation.tool.blur", "工具：模糊", HotkeyScope.Annotation, "标注工具", "B", "框选区域做盒式模糊（近似高斯）。"),
        new("annotation.tool.eraser", "工具：橡皮擦", HotkeyScope.Annotation, "标注工具", "X", "只擦掉自己画的标注，底图不会被擦出洞。"),
        new("annotation.tool.badge", "工具：序号", HotkeyScope.Annotation, "标注工具", "N", "点一下生成递增编号。"),
        new("annotation.tool.highlight", "工具：高亮", HotkeyScope.Annotation, "标注工具", "Y", "半透明粗线，像荧光笔。"),

        // ---- 贴图窗口内按键（鼠标在贴图上/贴图获得焦点时生效）----
        new("pin.copy", "复制到剪贴板", HotkeyScope.Pin, "贴图窗口", "Ctrl+C", "复制原始像素（未缩放、未旋转）到剪贴板。"),
        new("pin.saveAs", "另存为…", HotkeyScope.Pin, "贴图窗口", "Ctrl+S", "按 PNG / JPG / BMP 落盘。"),
        new("pin.annotate", "贴图内标注", HotkeyScope.Pin, "贴图窗口", "Space", "在贴图上继续标注（M4 提供标注引擎）。"),
        new("pin.rotateCw", "顺时针旋转 90°", HotkeyScope.Pin, "贴图窗口", "1", "绕中心旋转，不会跳位。"),
        new("pin.rotateCcw", "逆时针旋转 90°", HotkeyScope.Pin, "贴图窗口", "2", "绕中心旋转，不会跳位。"),
        new("pin.flipH", "水平翻转", HotkeyScope.Pin, "贴图窗口", "3", string.Empty),
        new("pin.flipV", "垂直翻转", HotkeyScope.Pin, "贴图窗口", "4", string.Empty),
        new("pin.resetZoom", "恢复 100%", HotkeyScope.Pin, "贴图窗口", "0", "清除缩放与缩略图状态。"),
        new("pin.thumbnail", "切换缩略图模式", HotkeyScope.Pin, "贴图窗口", "T", "按 1/4 显示，原始像素不变。"),
        new("pin.clickThrough", "切换鼠标穿透", HotkeyScope.Pin, "贴图窗口", "P", "开启后点击直接落到下层窗口。"),
        new("pin.hide", "隐藏这张贴图", HotkeyScope.Pin, "贴图窗口", "H", "也可用 Esc 隐藏。"),
        new("pin.close", "关闭这张贴图", HotkeyScope.Pin, "贴图窗口", "Delete", "关闭后不再恢复（备份同步更新）。"),
    ];

    private static readonly Dictionary<string, HotkeyActionDefinition> Index =
        All.ToDictionary(action => action.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Categories { get; } =
        All.Select(action => action.Category).Distinct().ToList();

    public static HotkeyActionDefinition? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : Index.GetValueOrDefault(id);

    public static HotkeyActionDefinition Require(string id) =>
        Find(id) ?? throw new KeyNotFoundException($"未知的动作 id：{id}");

    public static IEnumerable<HotkeyActionDefinition> InScope(HotkeyScope scope) =>
        All.Where(action => action.Scope == scope);

    /// <summary>表本身的完整性检查（唯一 id、默认键可解析、默认键在作用域内不重复）。</summary>
    public static IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        foreach (var group in All.GroupBy(action => action.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            problems.Add($"动作 id 重复：{group.Key}");
        }

        foreach (var action in All)
        {
            if (string.IsNullOrWhiteSpace(action.DisplayName))
            {
                problems.Add($"动作 {action.Id} 缺少名称。");
            }

            if (action.DefaultKeys is null)
            {
                continue;
            }

            if (!HotkeySpec.TryParse(action.DefaultKeys, out _, out string? error))
            {
                problems.Add($"动作 {action.Id} 的默认键「{action.DefaultKeys}」无法解析：{error}");
            }
        }

        foreach (var scope in Enum.GetValues<HotkeyScope>())
        {
            var duplicates = All
                .Where(action => action.Scope == scope && action.DefaultKeys is not null)
                .GroupBy(action => action.DefaultKeys!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1);

            foreach (var duplicate in duplicates)
            {
                problems.Add($"{scope} 作用域内默认键重复：{duplicate.Key}（{string.Join("、", duplicate.Select(a => a.Id))}）");
            }
        }

        return problems;
    }
}
