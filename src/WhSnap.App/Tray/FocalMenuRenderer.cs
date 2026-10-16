using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using WhSnap.Core;
using WhSnap.Services;
using WhSnap.Theme;

namespace WhSnap.Tray;

/// <summary>
/// 菜单项：第二列（快捷键 / 数量 / 状态）与危险标记。
/// 文本本身由 <see cref="FocalMenuRenderer"/> 排版成两列 —— ToolStripItem.Text 用 '\t' 分隔，
/// 这样自检读数与可访问性仍然能读到完整文案（例如「检查更新…」）。
/// </summary>
internal sealed class FocalMenuItem : ToolStripMenuItem
{
    private string _secondary = string.Empty;

    public FocalMenuItem(string label, string secondary = "", bool danger = false, bool showCheck = false)
        : base(label)
    {
        Label = label;
        IsDanger = danger;
        ShowCheck = showCheck;
        Secondary = secondary;

        // 行高 28（设计值）：文本自身只有 22 高，上下各补 3 得到 28；
        // 左右各补 2 只是给自绘的第二列多留一点量，文本位置由渲染器按面板内边距对齐。
        Padding = new Padding(2, 3, 2, 3);

        if (danger)
        {
            Tag = FocalMenuRenderer.DangerTag;
        }
    }

    /// <summary>左侧标签。</summary>
    public string Label { get; }

    /// <summary>右对齐的第二列文本（等宽 12px）：快捷键 / 数量 / 状态。</summary>
    public string Secondary
    {
        get => _secondary;
        set
        {
            _secondary = value ?? string.Empty;
            Text = _secondary.Length == 0 ? Label : Label + "\t" + _secondary;
        }
    }

    /// <summary>危险项（退出）：常态 SignalDanger，悬停 = 危险色底 + 浅字。</summary>
    public bool IsDanger { get; }

    /// <summary>勾选项（开机自启）：文本前留出固定勾选列，勾由渲染器画。</summary>
    public bool ShowCheck { get; }
}

/// <summary>分组标题：小号大写 + 正字距（.f-menu__title）。不可点，只做分组说明。</summary>
internal sealed class FocalMenuHeader : ToolStripMenuItem
{
    public FocalMenuHeader(string text)
        : base(text)
    {
        Enabled = false;
    }
}

/// <summary>
/// 托盘菜单的自绘渲染器 —— 把 design/focal/focal.css 的 .f-menu / .f-menuitem /
/// .f-menu__keys / .f-menusep / .f-menu__title 落到 WinForms。
///
/// 度量（设计值，UI 单位）：面板圆角 14 · 1px 发丝边 · 内边距 8 · 行高 28 ·
/// 文本 14px Segoe UI Variable Text · 快捷键列右对齐 12px Cascadia Code · 分组标题 11px 正字距。
/// 所有颜色只从令牌取（<see cref="FocalPalette.BrushColor(string)"/>），这里不出现字面量色值。
/// </summary>
internal sealed class FocalMenuRenderer : ToolStripProfessionalRenderer
{
    /// <summary>行内主文本（设计值 14px）。</summary>
    private const float ItemFontSize = 14f;

    /// <summary>第二列（快捷键 / 数量 / 状态）：等宽 12px。</summary>
    private const float DataFontSize = 12f;

    /// <summary>分组标题：11px SemiBold + 正字距。</summary>
    private const float LabelFontSize = 11f;

    /// <summary>行高 28 · 左内边距 8 · 两列间距 12 · 菜单内边距 8 · 面板圆角 14 · 内层圆角 6。</summary>
    private const int ItemHeightDip = 28;
    private const int ItemPaddingDip = 8;
    private const int ColumnGapDip = 12;
    private const int MenuPaddingDip = 8;
    private const int PanelRadiusDip = 14;
    private const int ItemRadiusDip = 6;

    /// <summary>勾选列宽度（= Focal.IconMd）：没有勾的项也留出这一列，标签才对得齐。</summary>
    private const int CheckColumnDip = 16;

    private readonly Brush _surface = new SolidBrush(FocalPalette.DrawingColor("Focal.Obsidian800Brush"));
    private readonly Brush _hairline = new SolidBrush(FocalPalette.DrawingColor("Focal.HairlineDarkBrush"));
    private readonly Brush _hairlineStrong = new SolidBrush(FocalPalette.DrawingColor("Focal.HairlineDarkStrongBrush"));
    private readonly Brush _hoverFill = new SolidBrush(FocalPalette.DrawingColor("Focal.AccentBrush"));
    private readonly Brush _dangerHoverFill = new SolidBrush(FocalPalette.DrawingColor("Focal.SignalDangerBrush"));

    private readonly System.Drawing.Color _text;
    private readonly System.Drawing.Color _textFaint;
    private readonly System.Drawing.Color _accent;
    private readonly System.Drawing.Color _accentInk;
    private readonly System.Drawing.Color _danger;
    private readonly System.Drawing.Color _onDark;

    private readonly Font _itemFont;
    private readonly Font _keysFont;
    private readonly Font _labelFont;

    private readonly GraphicsPath _checkGlyph = new();
    private readonly float _scale;

    public FocalMenuRenderer()
        : this(ReadDpiScale())
    {
    }

    /// <param name="scale">当前 DPI 相对 96 的倍数。</param>
    public FocalMenuRenderer(float scale)
    {
        // 设计值是 UI 单位；WinForms 用设备像素绘制，按 DPI 换算一次，之后所有度量都在设备像素上做
        _scale = Math.Max(1f, scale);
        _itemFont = ResolveFont("Focal.TextFont", ItemFontSize, FontStyle.Regular);
        _keysFont = ResolveFont("Focal.DataFont", DataFontSize, FontStyle.Regular);
        _labelFont = ResolveFont("Focal.TextFont", LabelFontSize, FontStyle.Bold);

        _text = FocalPalette.DrawingColor("Focal.OnDarkBrush");
        _textFaint = FocalPalette.DrawingColor("Focal.OnDarkFaintBrush");
        _accent = FocalPalette.DrawingColor("Focal.AccentBrush");
        _accentInk = FocalPalette.DrawingColor("Focal.AccentInkBrush");
        _danger = FocalPalette.DrawingColor("Focal.SignalDangerBrush");
        _onDark = FocalPalette.DrawingColor("Focal.OnDarkBrush");

        BuildCheckGlyph();
    }

    /// <summary>菜单行高（UI 单位）：每行正好 28。</summary>
    public const int DefaultItemHeight = ItemHeightDip;

    /// <summary>菜单内边距（UI 单位）：与渲染器的 8 一致。</summary>
    public const int DefaultPadding = MenuPaddingDip;

    /// <summary>危险项标记（退出）—— 用 Tag 而不是颜色，颜色只从令牌取。</summary>
    public static object DangerTag { get; } = new();

    /// <summary>当前 DPI 相对 96 的倍数（菜单项与色板用同一套换算，密度才一致）。</summary>
    public static float DpiScale => ReadDpiScale();

    private static float ReadDpiScale()
    {
        try
        {
            using var graphics = Graphics.FromHwnd(IntPtr.Zero);
            return Math.Max(1f, graphics.DpiX / 96f);
        }
        catch (Exception ex)
        {
            Log.Warn($"托盘菜单读取 DPI 失败，按 100% 处理：{ex.Message}");
            return 1f;
        }
    }

    private int Device(double dip) => (int)Math.Round(dip * _scale);

    internal static Font ResolveFont(string tokenKey, float sizeDip, FontStyle style)
    {
        // 字体栈是完整的 CSS 式列表（"Cascadia Code, Cascadia Mono, Consolas, ..."）：
        // 逐个试，取第一个这台机器上真的存在的 GDI+ 家族
        foreach (string name in FontCandidates(tokenKey))
        {
            try
            {
                var family = new FontFamily(name);
                return new Font(family, sizeDip, style, GraphicsUnit.Pixel);
            }
            catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
            {
                // 这个家族不存在，试下一个
            }
        }

        // 最后兜底：设计栈里一个都没有时，用系统界面字体（字重阶梯不塌）
        return new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, style);
    }

    /// <summary>把令牌里的字体栈拆成候选家族名。</summary>
    private static List<string> FontCandidates(string tokenKey)
    {
        var names = new List<string>();

        try
        {
            var family = FocalPalette.Font(tokenKey);
            if (family is not null && !string.IsNullOrWhiteSpace(family.Source))
            {
                names.AddRange(family.Source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"托盘菜单取字体令牌 {tokenKey} 失败，退到系统界面字体：{ex.Message}");
        }

        // 家族名可能带引号（CSS 字体栈里为含空格的家族加引号）
        for (int index = 0; index < names.Count; index++)
        {
            names[index] = names[index].Trim().Trim('"', '\'');
        }

        return names;
    }

    /// <summary>勾选几何：Focal.Icon.check（24 网格）转成 GDI+ 路径，描边粗细与缩放交给绘制方。</summary>
    private void BuildCheckGlyph()
    {
        if (_checkGlyph.PointCount > 0 || FocalIcon.Resolve("check") is not { } geometry)
        {
            return;
        }

        using var converted = ToGraphicsPath(geometry);
        _checkGlyph.AddPath(converted, false);
    }

    /// <summary>
    /// WPF Geometry（<c>Focal.Icon.*</c> 令牌）→ GDI+ GraphicsPath。
    /// 图标是共享的几何令牌，不能在托盘侧重画一份 —— 所以做一次展平转换。
    /// </summary>
    private static GraphicsPath ToGraphicsPath(System.Windows.Media.Geometry geometry)
    {
        var path = new GraphicsPath();
        var flattened = geometry.GetFlattenedPathGeometry(0.2, System.Windows.Media.ToleranceType.Absolute);

        foreach (System.Windows.Media.PathFigure figure in flattened.Figures)
        {
            var points = new List<PointF>();

            // 展平后的路径只有折线（曲线已经被 Flatten 成折线段）
            foreach (System.Windows.Media.PathSegment segment in figure.Segments)
            {
                if (segment is System.Windows.Media.PolyLineSegment poly)
                {
                    points.AddRange(poly.Points.Select(p => new PointF((float)p.X, (float)p.Y)));
                }
                else if (segment is System.Windows.Media.LineSegment line)
                {
                    points.Add(new PointF((float)line.Point.X, (float)line.Point.Y));
                }
            }

            if (points.Count == 0)
            {
                continue;
            }

            points.Insert(0, new PointF((float)figure.StartPoint.X, (float)figure.StartPoint.Y));
            path.AddLines(points.ToArray());

            if (figure.IsClosed)
            {
                path.CloseFigure();
            }
        }

        return path;
    }

    // ---------------------------------------------------------------- 面板

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        // 基底交给基类（清掉上一次的绘制），面板自己画成圆角
        base.OnRenderToolStripBackground(e);

        if (e.ToolStrip is not ToolStripDropDown)
        {
            return;
        }

        var bounds = e.ToolStrip.ClientRectangle;
        float width = Math.Max(1f, _scale);
        using var region = RoundedPath(
            new RectangleF(bounds.X, bounds.Y, bounds.Width - width, bounds.Height - width),
            Device(PanelRadiusDip));

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(_surface, region);

        using var pen = new System.Drawing.Pen(_hairlineStrong, width);
        e.Graphics.DrawPath(pen, region);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // 1px 发丝边在 OnRenderToolStripBackground 里跟着圆角一起画，基类的方角边框不要
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // 设计稿没有图标列：文本左对齐到内边距
    }

    // ---------------------------------------------------------------- 行

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        var bounds = new Rectangle(Point.Empty, item.Size);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        if (item.Selected && item.Enabled)
        {
            using var path = RoundedPath(bounds, Device(ItemRadiusDip));
            e.Graphics.FillPath(IsDanger(item) ? _dangerHoverFill : _hoverFill, path);
            return;
        }

        // 非悬停态：把行恢复成菜单面，避免上一次的琥珀底残留
        e.Graphics.FillRectangle(_surface, bounds);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var item = e.Item;
        var bounds = e.TextRectangle;

        if (bounds.Width <= 4)
        {
            bounds = new Rectangle(0, 0, item.Width, item.Height);
        }

        // 文本区的左边界已经落在菜单内边距之内（ToolStripDropDownMenu 给子项加了 DefaultPadding.Left），
        // 所以这里只再补「设计内边距 - 文本区已让出的量」，两边不会各加一次
        int pad = Math.Max(0, Device(ItemPaddingDip) - bounds.Left);
        int gap = Device(ColumnGapDip);
        string label = item.Text ?? string.Empty;
        string keys = string.Empty;
        int tab = label.IndexOf('\t');
        if (tab >= 0)
        {
            keys = label[(tab + 1)..];
            label = label[..tab];
        }

        bool showColumn = item is FocalMenuItem { ShowCheck: true };
        int columnWidth = showColumn ? Device(CheckColumnDip) : 0;
        int left = bounds.Left + pad + (showColumn ? columnWidth + gap : 0);
        int rightLimit = bounds.Right - pad;

        if (keys.Length > 0)
        {
            rightLimit -= TextRenderer.MeasureText(keys, _keysFont, Size.Empty, TextFormatFlags.NoPadding).Width + gap;
        }

        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        if (keys.Length > 0)
        {
            var keysColor = item.Selected && item.Enabled ? _accentInk : _textFaint;
            var keysBounds = new Rectangle(rightLimit + gap, bounds.Top, bounds.Right - pad - rightLimit - gap, bounds.Height);

            TextRenderer.DrawText(
                e.Graphics,
                keys,
                _keysFont,
                keysBounds,
                keysColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        if (showColumn && item is ToolStripMenuItem { Checked: true })
        {
            var columnBounds = new Rectangle(bounds.Left + pad, bounds.Top, columnWidth, bounds.Height);
            var glyph = _checkGlyph.GetBounds();

            // 勾选几何按 24 网格等比缩到勾选列内并居中（不依赖展平后的具体边界值）
            float span = Math.Max(Math.Max(glyph.Width, glyph.Height), 1f);
            float box = Math.Max(4, Device(CheckColumnDip) - Device(2));
            float ratio = box / span;
            float dx = columnBounds.Left + ((columnWidth - (glyph.Width * ratio)) / 2f) - (glyph.Left * ratio);
            float dy = columnBounds.Top + ((columnBounds.Height - (glyph.Height * ratio)) / 2f) - (glyph.Top * ratio);

            var state = e.Graphics.Save();
            e.Graphics.TranslateTransform(dx, dy);
            e.Graphics.ScaleTransform(ratio, ratio);

            using var pen = new System.Drawing.Pen(item.Selected ? _accentInk : _accent, Math.Max(1.4f, _scale * 1.6f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };

            e.Graphics.DrawPath(pen, _checkGlyph);
            e.Graphics.Restore(state);
        }

        int labelWidth = Math.Max(0, rightLimit - left);
        if (labelWidth <= 0)
        {
            return;
        }

        if (item is FocalMenuHeader)
        {
            DrawHeader(e.Graphics, label.ToUpperInvariant(), bounds, left);
            return;
        }

        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                    | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

        TextRenderer.DrawText(e.Graphics, label, _itemFont, new Rectangle(left, bounds.Top, labelWidth, bounds.Height), ResolveLabelColor(item), flags);
    }

    /// <summary>分组标题：小号大写 + 正字距（CSS 的 text-transform / letter-spacing 在 WinForms 里手动做）。</summary>
    private void DrawHeader(Graphics graphics, string text, Rectangle bounds, int left)
    {
        int x = left;

        foreach (char glyph in text)
        {
            string piece = glyph.ToString();
            TextRenderer.DrawText(
                graphics,
                piece,
                _labelFont,
                new Rectangle(x, bounds.Top, Device(24), bounds.Height),
                _textFaint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            x += TextRenderer.MeasureText(graphics, piece, _labelFont, Size.Empty, TextFormatFlags.NoPadding).Width + Math.Max(1, Device(0.4));
        }
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var bounds = e.Item.Bounds;
        int inset = Device(MenuPaddingDip);
        int y = bounds.Top + (bounds.Height / 2);

        e.Graphics.FillRectangle(
            _hairline,
            bounds.Left + inset,
            y,
            Math.Max(1, bounds.Width - (inset * 2)),
            Math.Max(1, Device(1)));
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // 勾选在 OnRenderItemText 里与标签一起排版（勾选列宽固定，勾与文字同一条竖线）
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        // 不做箭头：数量 / 状态写在第二列，比三角箭头信息量大
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        // 菜单项不带图标（设计稿没有图标列）
    }

    private System.Drawing.Color ResolveLabelColor(ToolStripItem item)
    {
        if (item is FocalMenuHeader)
        {
            return _textFaint;
        }

        if (!item.Enabled)
        {
            return _textFaint;
        }

        if (IsDanger(item))
        {
            return item.Selected ? _onDark : _danger;
        }

        return item.Selected ? _accentInk : _text;
    }

    private static bool IsDanger(ToolStripItem item) =>
        item is FocalMenuItem { IsDanger: true };

    private static GraphicsPath RoundedPath(Rectangle bounds, int radius) =>
        RoundedPath(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), radius);

    internal static GraphicsPath RoundedPath(RectangleF bounds, int radius)
    {
        float d = Math.Max(1f, radius) * 2f;
        var path = new GraphicsPath();

        if (bounds.Width <= d || bounds.Height <= d)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, d, d, 180f, 90f);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270f, 90f);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0f, 90f);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90f, 90f);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// 托盘菜单本体：圆角（14）+ 原生投影（L1 机壳）。WinForms 的 ToolStripDropDown 是方角窗口，
/// 这里给窗口句柄设一个圆角 Region，渲染器再在 Region 内画 1px 发丝边。
/// </summary>
internal sealed class FocalContextMenu : ContextMenuStrip
{
    public FocalContextMenu()
    {
        DropShadowEnabled = true;
        ShowImageMargin = false;
        GripStyle = ToolStripGripStyle.Hidden;
        AutoSize = true;

        // 行高由 AutoSize 从字号推出来：GDI+ 的 14px em 高 + 菜单内边距正好落在设计值 28
        ImageScalingSize = Size.Empty;
        Padding = new Padding(1);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        try
        {
            float scale = FocalMenuRenderer.DpiScale;
            int radius = (int)Math.Round(14 * scale);

            using var path = FocalMenuRenderer.RoundedPath(
                new RectangleF(0, 0, ClientRectangle.Width - 1, ClientRectangle.Height - 1),
                radius);

            Region = new Region(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"托盘菜单圆角设置失败（退到方角）：{ex.Message}");
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Region?.Dispose();
        Region = null;
        base.OnHandleDestroyed(e);
    }
}

/// <summary>
/// 「最近颜色」：6 列色块网格（色块 20×20 圆角 2，悬停有反馈 + 手型光标）。
/// 设计稿明确"颜色用眼睛找"—— 所以是网格而不是一列文字。点击色块触发宿主既有的
/// <c>RecentColorSelected</c>；底部保留「清空颜色历史」。
/// </summary>
internal sealed class FocalColorPanel : Panel
{
    private const int Columns = 6;
    private const int SwatchSizeDip = 20;
    private const int GapDip = 8;
    private const float HexFontSize = 11f;

    /// <summary>
    /// 「最近颜色」右上角的计数。容量必须来自实际设置（<c>ColorHistoryRetention</c>），
    /// 不能写死——写死会与设置页的上限/默认值对不上（评审 P2-9 的"数值矛盾"）。
    /// </summary>
    internal static string ColorPanelCapacityText(int count, int capacity)
    {
        int shown = Math.Max(capacity, 0);
        return count == 0 ? $"空 · 最多保留 {shown} 个" : $"{count} 个 · 最多保留 {shown} 个";
    }

    private readonly Font _hexFont;
    private readonly System.Drawing.Color _textFaint;

    public FocalColorPanel(IReadOnlyList<RgbColor> colors, int capacity, Action clearHistory)
    {
        float scale = FocalMenuRenderer.DpiScale;
        int scaledSwatch = Device(SwatchSizeDip, scale);
        int gap = Device(GapDip, scale);
        int headerHeight = Device(20, scale);
        int footerHeight = Device(26, scale);

        _hexFont = FocalMenuRenderer.ResolveFont("Focal.DataFont", HexFontSize, FontStyle.Regular);
        _textFaint = FocalPalette.DrawingColor("Focal.OnDarkFaintBrush");
        var textMuted = FocalPalette.DrawingColor("Focal.OnDarkMutedBrush");

        // 面板底透明，颜色由菜单面提供（正好落在 obsidian-800 上）
        BackColor = FocalPalette.DrawingColor("Focal.Obsidian800Brush");
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        AutoSize = false;

        // 量字宽用的样本取自令牌（#FFFFFF = Focal.PaperColor），不写字面量色值。
        // Label 内部会补 3px 左右内边距，所以这里不带 NoPadding 量，否则 hex 会缺最后一位
        string widestHex = "#" + FocalPalette.Hex("Focal.PaperColor").Substring(3);
        int cellWidth = Math.Max(scaledSwatch, TextRenderer.MeasureText(widestHex, _hexFont).Width);
        int rows = Math.Max(1, (colors.Count + Columns - 1) / Columns);
        int gridHeight = (rows * (scaledSwatch + Device(14, scale))) + ((rows - 1) * gap);

        // ── 头部：分组标题 + 右侧计数 ──────────────────────────────────────
        var header = new Label
        {
            Text = "最近颜色",
            Font = FocalMenuRenderer.ResolveFont("Focal.TextFont", 11f, FontStyle.Bold),
            ForeColor = _textFaint,
            AutoSize = false,
            Height = headerHeight,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(0, 0),
            Width = Math.Max(80, (cellWidth + gap) * Columns),
        };

        var counter = new Label
        {
            Text = ColorPanelCapacityText(colors.Count, capacity),
            Font = _hexFont,
            ForeColor = textMuted,
            AutoSize = false,
            Height = headerHeight,
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(0, 0),
            Width = header.Width,
        };

        Controls.Add(counter);
        Controls.Add(header);

        // ── 色块网格：6 列，每格 = 色块 20×20 + 下方等宽 hex ───────────────
        for (int index = 0; index < colors.Count; index++)
        {
            var color = colors[index];
            int column = index % Columns;
            int row = index / Columns;

            var cell = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, gap, gap),
                Padding = Padding.Empty,
                BackColor = Color.Transparent,
                Location = new Point(column * (cellWidth + gap), headerHeight + (row * (scaledSwatch + Device(14, scale) + gap))),
            };

            var swatch = new FocalSwatch(color)
            {
                Size = new Size(scaledSwatch, scaledSwatch),
                Margin = new Padding(0),
            };
            swatch.Click += (_, _) => ColorSelected?.Invoke(color);

            var hex = new Label
            {
                Text = color.ToHex().ToUpperInvariant(),
                Font = _hexFont,
                ForeColor = FocalPalette.DrawingColor("Focal.OnDarkBrush"),
                AutoSize = false,
                Size = new Size(cellWidth, Device(12, scale)),
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(0),
            };

            cell.Controls.Add(swatch);
            cell.Controls.Add(hex);
            Controls.Add(cell);
        }

        // ── 底部：一句提示 + 清空颜色历史（细分隔线以上）────────────────────
        int footerTop = headerHeight + gridHeight + Device(8, scale);

        var separator = new Panel
        {
            BackColor = FocalPalette.DrawingColor("Focal.HairlineDarkBrush"),
            Location = new Point(0, footerTop),
            Size = new Size(header.Width, Math.Max(1, Device(1, scale))),
        };

        var hint = new Label
        {
            Text = colors.Count == 0 ? "取色后会自动记在这里。" : "左键复制色值",
            Font = _hexFont,
            ForeColor = _textFaint,
            AutoSize = false,
            Location = new Point(0, footerTop + Device(6, scale)),
            Size = new Size(header.Width / 2, Device(18, scale)),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var clear = new Button
        {
            Text = "清空颜色历史",
            FlatStyle = FlatStyle.Flat,
            Font = FocalMenuRenderer.ResolveFont("Focal.TextFont", 12f, FontStyle.Regular),
            ForeColor = FocalPalette.DrawingColor("Focal.OnDarkBrush"),
            BackColor = FocalPalette.DrawingColor("Focal.Obsidian700Brush"),
            Enabled = colors.Count > 0,
            Location = new Point(header.Width - Device(104, scale), footerTop + Device(6, scale)),
            Size = new Size(Device(104, scale), Device(22, scale)),
        };

        clear.FlatAppearance.BorderColor = FocalPalette.DrawingColor("Focal.HairlineDarkBrush");
        clear.FlatAppearance.MouseOverBackColor = FocalPalette.DrawingColor("Focal.Obsidian600Brush");
        clear.Click += (_, _) => clearHistory();

        Controls.Add(separator);
        Controls.Add(hint);
        Controls.Add(clear);

        Size = new Size(header.Width, footerTop + footerHeight);
    }

    /// <summary>用户点了某个色块。</summary>
    public event Action<RgbColor>? ColorSelected;

    private static int Device(double dip, float scale) => (int)Math.Round(dip * scale);
}

/// <summary>单个色块：20×20 · 圆角 2 · 1px 发丝边 · 悬停提亮边框 + 手型。</summary>
internal sealed class FocalSwatch : Control
{
    private readonly System.Drawing.Color _fill;
    private readonly System.Drawing.Color _border;
    private readonly System.Drawing.Color _borderHover;
    private bool _hover;

    public FocalSwatch(RgbColor color)
    {
        // 画的是"用户的颜色"（内容，不是界面主题色）：走 FocalPalette 的 WinForms 桥
        _fill = FocalPalette.DrawingColor(color);
        _border = FocalPalette.DrawingColor("Focal.HairlineDarkStrongBrush");
        _borderHover = FocalPalette.DrawingColor("Focal.AccentBrush");

        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor,
            true);

        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        TabStop = false;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = FocalMenuRenderer.RoundedPath(bounds, Math.Max(1, (int)Math.Round(2 * FocalMenuRenderer.DpiScale)));

        using var fill = new SolidBrush(_fill);
        e.Graphics.FillPath(fill, path);

        using var pen = new System.Drawing.Pen(_hover ? _borderHover : _border, 1f);
        e.Graphics.DrawPath(pen, path);
    }
}
