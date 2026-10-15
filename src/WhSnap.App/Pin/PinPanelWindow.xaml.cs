using System.Windows;
using System.Windows.Threading;
using WhSnap.Core;

namespace WhSnap.Pin;

/// <summary>贴图管理面板中的一行。</summary>
internal sealed class PinRow(PinWindow pin)
{
    public PinWindow Pin { get; } = pin;

    public string ToggleText => Pin.IsHidden ? "显示" : "隐藏";

    public string Title
    {
        get
        {
            var bounds = Pin.Bounds;
            string state = Pin.IsHidden ? "隐藏" : "显示";
            string flags = (Pin.State.ClickThrough ? "　穿透" : string.Empty)
                           + (Pin.State.Thumbnail ? "　缩略图" : string.Empty)
                           + (Pin.State.Opacity < 100 ? $"　{Pin.State.Opacity}%" : string.Empty)
                           + (Pin.State.Rotation != PinRotation.None ? $"　{(int)Pin.State.Rotation * 90}°" : string.Empty);

            return $"#{Pin.ZIndex + 1}　{Pin.State.SourceWidth}×{Pin.State.SourceHeight}　{bounds.X},{bounds.Y}　{state}{flags}";
        }
    }

    public string Detail =>
        $"显示尺寸 {Pin.Bounds.Width}×{Pin.Bounds.Height}　缩放 {PinGeometry.EffectiveZoom(Pin.State) * 100:0}%"
        + (Pin.State.FlipHorizontal ? "　水平翻转" : string.Empty)
        + (Pin.State.FlipVertical ? "　垂直翻转" : string.Empty);
}

/// <summary>
/// 贴图管理面板（§2.1 B「贴图数量与层级管理面板」）。
/// 列出全部贴图的尺寸/位置/状态，可逐张置顶、隐藏、关闭，也可批量操作。
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

    private void Refresh()
    {
        var rows = _manager.Pins
            .OrderByDescending(pin => pin.ZIndex)
            .Select(pin => new PinRow(pin))
            .ToList();

        PinList.ItemsSource = rows;

        SummaryText.Text = _manager.Count == 0
            ? "当前没有贴图。用托盘菜单「从剪贴板贴图」，或在截图遮罩里按 F3 贴图。"
            : $"共 {_manager.Count} 张贴图，其中 {_manager.VisibleCount} 张显示中（列表按层级从上到下）。";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

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
