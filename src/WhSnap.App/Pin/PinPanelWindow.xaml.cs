using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WhSnap.Core;
using WhSnap.Services;
using WhSnap.Theme;

namespace WhSnap.Pin;

/// <summary>贴图管理面板中的一行（design/focal/surfaces/pin-panel.html 的 .f-item）。</summary>
internal sealed class PinRow(PinWindow pin)
{
    public PinWindow Pin { get; } = pin;

    public bool IsHidden => Pin.IsHidden;

    public string ToggleText => Pin.IsHidden ? "显示" : "隐藏";

    public string StateText => Pin.IsHidden ? "已隐藏" : "显示中";

    /// <summary>状态列的补充说明（只在悬停提示里出现，不占行宽）。</summary>
    public string StateHint => Pin.IsHidden
        ? "这张贴图已收起，点「显示」恢复。"
        : "这张贴图正钉在桌面上。";

    /// <summary>单张操作的第一颗按钮：把这张贴图提到最上层（配合层级编号读）。</summary>
    public string PinActionText => "置顶";

    /// <summary>标题：有备份文件名（= 原始来源名）就用它，否则退到层级编号。</summary>
    public string Title
    {
        get
        {
            string name = Path.GetFileNameWithoutExtension(Pin.BackupFileName ?? string.Empty);
            return string.IsNullOrWhiteSpace(name) ? $"贴图 #{Pin.ZIndex + 1}" : name;
        }
    }

    /// <summary>元信息（等宽 12）：宽×高 · 缩放 · 位置 · 状态标记。</summary>
    public string Detail
    {
        get
        {
            var bounds = Pin.Bounds;
            string flags = string.Empty;

            if (Pin.State.ClickThrough)
            {
                flags += " · 穿透";
            }

            if (Pin.State.Thumbnail)
            {
                flags += " · 缩略图";
            }

            if (Pin.State.Opacity < 100)
            {
                flags += $" · {Pin.State.Opacity}%";
            }

            if (Pin.State.Rotation != PinRotation.None)
            {
                flags += $" · {(int)Pin.State.Rotation * 90}°";
            }

            if (Pin.State.FlipHorizontal)
            {
                flags += " · 水平翻转";
            }

            if (Pin.State.FlipVertical)
            {
                flags += " · 垂直翻转";
            }

            return $"{Pin.State.SourceWidth} × {Pin.State.SourceHeight} · "
                   + $"{PinGeometry.EffectiveZoom(Pin.State) * 100:0}% · "
                   + $"{bounds.X}, {bounds.Y}{flags}";
        }
    }

    /// <summary>
    /// 缩略块内容：不是"画一张示意图"，而是把这张贴图本身缩到 44×34（44 / 34 圆角块内的 UniformToFill）。
    /// 源图比缩略块还小时直接用原图，避免无意义的重采样。
    /// </summary>
    public BitmapSource? Thumbnail
    {
        get
        {
            var source = Pin.Source;

            try
            {
                double scale = Math.Min(88.0 / Math.Max(source.PixelWidth, 1), 68.0 / Math.Max(source.PixelHeight, 1));

                if (scale >= 1 || !source.CanFreeze)
                {
                    return source;
                }

                var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                scaled.Freeze();
                return scaled;
            }
            catch (Exception ex)
            {
                Log.Warn($"生成贴图缩略图失败：{ex.Message}");
                return source;
            }
        }
    }

    /// <summary>这张贴图占的像素内存（宽 × 高 × 4 字节），概要行与状态栏的"内存"用它。</summary>
    public long Bytes => (long)Pin.State.SourceWidth * Pin.State.SourceHeight * 4;
}

/// <summary>
/// 贴图管理面板（§2.1 B「贴图数量与层级管理面板」）。
/// 列出全部贴图的尺寸/位置/状态，可逐张置顶、隐藏、关闭，也可批量操作。
/// 视觉落地 design/focal/surfaces/pin-panel.html：obsidian-900 机壳 / 标题栏 32 /
/// 行高 40（双行）/ 缩略块 44×34 / 悬停才出操作 / 底部批量条 / 状态栏。
/// 刷新做了去抖：拖动贴图时 Changed 事件很密集，没必要每帧重建列表。
/// </summary>
internal partial class PinPanelWindow : Window
{
    private readonly PinManager _manager;
    private readonly DispatcherTimer _refreshTimer;
    private bool _closing;

    public PinPanelWindow(PinManager manager)
    {
        InitializeComponent();

        _manager = manager;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };

        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            Refresh();
        };

        _manager.PinsChanged += OnPinsChanged;
        Closed += OnClosed;

        Refresh();
    }

    private void OnPinsChanged()
    {
        if (_closing)
        {
            return;
        }

        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        _refreshTimer.Stop();
        _manager.PinsChanged -= OnPinsChanged;
    }

    /// <summary>动作当前的键位文本（读 PinManager 的绑定）；未绑定/停用时返回回退值。</summary>
    private string ActionKeyText(string actionId, string fallback)
    {
        var binding = _manager.KeyBindings
            .FirstOrDefault(b => string.Equals(b.ActionId, actionId, StringComparison.OrdinalIgnoreCase));

        return binding is { IsActive: true } ? binding.Keys!.Trim() : fallback;
    }

    /// <summary>
    /// 空状态提示（自检可直接调用）。两处要求：
    /// ① 键位取当前绑定而不是写死默认值；② 动作名与 <see cref="HotkeyCatalog"/> 完全一致
    /// （同一个动作在托盘、设置、空状态里必须是同一个名字）。
    /// </summary>
    internal static string ComposeEmptyHint(Func<string, string, string> keyText)
    {
        string clipboardAction = HotkeyCatalog.Require("capture.pinClipboard").DisplayName;
        string pinAction = HotkeyCatalog.Require("overlay.pin").DisplayName;
        string pinKeys = keyText("overlay.pin", string.Empty);

        return string.IsNullOrWhiteSpace(pinKeys)
            ? $"把图放进剪贴板后从托盘菜单选「{clipboardAction}」；遮罩里的「{pinAction}」当前未绑定，可在设置里给它设一个键。"
            : $"把图放进剪贴板后从托盘菜单选「{clipboardAction}」，或在截图遮罩里按 {pinKeys} {pinAction}。";
    }

    private void Refresh()
    {        var rows = _manager.Pins
            .OrderByDescending(pin => pin.ZIndex)
            .Select(pin => new PinRow(pin))
            .ToList();

        PinList.ItemsSource = rows;
        // 空状态：列表空时给"人话 + 怎么产生贴图"，而不是一片黑
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (rows.Count == 0)
        {
            EmptyHintText.Text = ComposeEmptyHint(ActionKeyText);
        }

        int total = _manager.Count;
        int visible = _manager.VisibleCount;
        string memory = FormatBytes(rows.Sum(row => row.Bytes));

        SummaryCount.Text = total.ToString();
        SummaryVisible.Text = visible.ToString();
        SummaryMemory.Text = memory;

        StatusVisible.Text = visible.ToString();
        StatusTotal.Text = total.ToString();
        StatusMemory.Text = memory;
    }

    /// <summary>内存读数走等宽：人类可读单位 + 一位小数（KB / MB）。</summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 KB";
        }

        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        }

        return $"{bytes / 1024.0:0.0} KB";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        // 无边框窗口：标题栏拖动 = 移动窗口（最大化时先还原，避免"拖不动"）
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已经抬起时 DragMove 会抛 —— 直接忽略，不影响其它交互
        }
    }

    private static PinWindow? Resolve(object sender) =>
        (sender as FrameworkElement)?.Tag as PinRow is { } row ? row.Pin : null;

    private void PinToFront_Click(object sender, RoutedEventArgs e)
    {
        if (Resolve(sender) is { } pin)
        {
            pin.BringToFront();
            Refresh();
        }
    }

    private void PinToggleHidden_Click(object sender, RoutedEventArgs e)
    {
        if (Resolve(sender) is not { } pin)
        {
            return;
        }

        if (pin.IsHidden)
        {
            pin.Show();
        }
        else
        {
            pin.Hide();
        }

        Refresh();
    }

    private void PinClose_Click(object sender, RoutedEventArgs e)
    {
        if (Resolve(sender) is { } pin)
        {
            _manager.Close(pin);
            Refresh();
        }
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        _manager.ShowAll();
        Refresh();
    }

    private void HideAll_Click(object sender, RoutedEventArgs e)
    {
        _manager.HideAll();
        Refresh();
    }

    private void BringAll_Click(object sender, RoutedEventArgs e)
    {
        _manager.BringAllToFront();
        Refresh();
    }

    private void CloseAll_Click(object sender, RoutedEventArgs e)
    {
        _manager.CloseAll();
        Refresh();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();
}
