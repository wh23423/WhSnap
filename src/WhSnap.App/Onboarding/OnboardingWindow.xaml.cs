using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WhSnap.Core;
using WhSnap.Services;
using WhSnap.Theme;

namespace WhSnap.Onboarding;

/// <summary>
/// 首次运行向导（M4 onboarding，对应 design/focal/surfaces/onboarding.html 与
/// docs/focal-UI实现约定.md §5 的 <c>Onboarding/OnboardingWindow.xaml(.cs)</c>）。
///
/// 职责边界：**只做界面与自身交互**（三段内容 / 3 段进度 / 热键录制 / 自启动开关）。
/// 不读配置、不写配置、不注册热键、不改注册表 —— 持久化由宿主（App）在
/// <see cref="HotkeyRecorded"/> 与 <see cref="Finished"/> 回调里完成。
///
/// 视觉纪律：颜色 / 度量 / 圆角 / 字号全部来自 <c>Focal.*</c> 令牌，
/// C# 侧只走 <see cref="FocalPalette"/>，本文件不出现字面量色值。
/// </summary>
internal sealed partial class OnboardingWindow : Window
{
    /// <summary>按设计稿固定：880×620，居中，不可缩放，不出现在任务栏。</summary>
    private const double OnboardingWidth = 880;

    private const double OnboardingHeight = 620;

    /// <summary>录制态脉冲：1.2s 一个来回（0.6s 单程），与 Focal.KeycapButton 的节奏一致。</summary>
    private static readonly Duration PulseHalfPeriod = new(TimeSpan.FromSeconds(0.6));

    /// <summary>
    /// 三段内容各自对应的动作。键位默认值一律取 <see cref="HotkeyCatalog"/>，不在界面里写死。
    /// <para>
    /// 注意：向导编辑的是**全局热键**，而冲突是"同作用域内撞键"，所以绑定表里必须留着
    /// 全部 Global 动作（例如 pin.toggleAll = Shift+F3），否则把截图键录成 Shift+F3 会悄悄生效。
    /// 遮罩 / 贴图 / 标注作用域的按键与全局热键不在同一个命名空间，可以安全丢弃。
    /// </para>
    /// </summary>
    private static readonly string[] RowActionIds =
    [
        "capture.region",
        "capture.pinClipboard",
        "settings.open",
    ];

    /// <summary>当前编辑中的绑定表（宿主装载现值或目录默认值；录制只改这里的内存副本）。</summary>
    private readonly List<HotkeyBinding> _bindings = [];

    /// <summary>这一次向导里录制出来的改动，键 = 动作 id。</summary>
    private readonly Dictionary<string, HotkeyBinding> _recorded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>进入录制前的键位备份：Esc / 关闭取消时原样回退。</summary>
    private readonly Dictionary<string, string?> _keysBeforeRecording = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前正在录制的行（null = 没有在录制）。</summary>
    private Button? _recordingRow;

    /// <summary>是否经由 <see cref="ShowModal"/> 显示（只有模态下才允许写 DialogResult）。</summary>
    private bool _shownAsDialog;

    /// <summary>正在动画中的行 → 它的 Storyboard（退出录制时移除，避免动画残留）。</summary>
    private readonly Dictionary<Button, Storyboard> _pulses = [];

    internal OnboardingWindow()
    {
        InitializeComponent();

        Width = OnboardingWidth;
        Height = OnboardingHeight;

        // 键位默认值取 Core 的权威目录（capture.region / capture.pinClipboard / settings.open）
        ResetBindings(HotkeyBindings.Resolve());
        AutoStartToggle.IsChecked = true;

        // 标题栏版本号读程序集，不写死：写死会在下次发版后显示旧版本
        VersionText.Text = "v" + UpdateService.GetCurrentVersion();

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => RowCapture.Focus();
    }

    // ────────────────────────────────────────────────────────────────────────
    //  公开 API（宿主接线面）—— 窗口可独立 Show()，不依赖 App.xaml.cs 的额外初始化
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 用户刚录制完一个键位（已在窗口内做过保留键 / 内部撞键校验）。
    /// 窗口**不会**自己写配置：宿主在回调里交给 SettingsService 并做注册探针（L1 占用检测）。
    /// <c>HasConflict = true</c> 表示该键位与其它动作撞键 → 建议按 <c>HotkeyConflictPolicy</c> 处理（默认必须修正）。
    /// </summary>
    public event EventHandler<HotkeyRecordedEventArgs>? HotkeyRecorded;

    /// <summary>用户点了「稍后再看」（此时 <see cref="SkipRequested"/> 为 true）。</summary>
    public event EventHandler? Skip;

    /// <summary>用户点了「开始使用」。若宿主需要区分「开始使用 / 稍后再看」，用它或 <see cref="SkipRequested"/>。</summary>
    public event EventHandler? Finished;

    /// <summary>用户直接关掉向导（标题栏 × 或 Esc）——既没点「开始使用」也没点「稍后再看」。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// 「打开用户手册」被点击。宿主接线到手册 / 帮助页；不接线时按钮不做事。
    /// </summary>
    public event EventHandler? ManualRequested;

    /// <summary>true = 用户点了「稍后再看」。与 <see cref="Finished"/> 互斥。</summary>
    public bool SkipRequested { get; private set; }

    /// <summary>true = 用户点了「开始使用」。</summary>
    public bool FinishedByUser { get; private set; }

    /// <summary>「开机自启动」开关的当前状态（<see cref="AppSettings.AutoStart"/> 的默认值是 true，与设计稿一致）。</summary>
    public bool StartWithWindows => AutoStartToggle.IsChecked == true;

    /// <summary>这一次向导里录制出来的键位改动（未改动的动作不在里面）。</summary>
    public IReadOnlyList<HotkeyBinding> RecordedHotkeys => _recorded.Values.ToList();

    /// <summary>当前生效的绑定表（未录制时就是目录默认值 / 宿主装载的现值）。</summary>
    public IReadOnlyList<HotkeyBinding> CurrentBindings => _bindings.ToList();

    /// <summary>
    /// 宿主装载"当前生效的绑定"，让向导显示真实键位而不是永远显示默认值。
    /// 典型调用：<c>window.PreloadBindings(settings.ResolveHotkeys());</c>
    /// 传 null / 空 时回退到 <see cref="HotkeyCatalog"/> 的默认键位。
    /// </summary>
    public void PreloadBindings(IReadOnlyList<HotkeyBinding>? bindings)
    {
        _bindings.Clear();
        _bindings.AddRange(GlobalBindings(bindings is { Count: > 0 } ? bindings : null));
        _recorded.Clear();
        _keysBeforeRecording.Clear();
        RefreshRows();
    }

    /// <summary>宿主接管冲突提示文案（例如在 L1 注册探针里发现"已被别的软件占用"）。传 null 清除。</summary>
    public void ShowHostStatus(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            HideStatus();
            return;
        }

        StatusText.Text = message;
        StatusBanner.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 以模态方式显示（宿主用）。非模态请直接用 <c>Show()</c>：两者都不需要额外初始化。
    /// </summary>
    public bool? ShowModal(Window? owner = null)
    {
        if (owner is not null && !ReferenceEquals(owner, this))
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        _shownAsDialog = true;
        return ShowDialog();
    }

    /// <summary>
    /// 无输入的自测入口：等价于用户在向导里按下 <paramref name="key"/> + <paramref name="modifiers"/>。
    /// 目标行不在录制态时返回 false。供 <c>--onboarding-preview</c> 之类的诊断开关使用。
    /// </summary>
    internal bool SimulateRecording(string actionId, Key key, ModifierKeys modifiers)
    {
        Button? row = RowFor(actionId);
        if (row is null || !ReferenceEquals(row, _recordingRow))
        {
            return false;
        }

        return CompleteRecording(row, key, modifiers);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  绑定表 / 行
    // ────────────────────────────────────────────────────────────────────────

    private void ResetBindings(IReadOnlyList<HotkeyBinding>? source)
    {
        _bindings.Clear();
        _recorded.Clear();
        _bindings.AddRange(GlobalBindings(source));
        RefreshRows();
    }

    /// <summary>
    /// 取"全部 Global 动作"的绑定：优先用 <paramref name="source"/>（宿主现值），
    /// 缺失的动作补目录默认值。这是冲突检测的完整参照系。
    /// </summary>
    private static List<HotkeyBinding> GlobalBindings(IReadOnlyList<HotkeyBinding>? source)
    {
        var result = new List<HotkeyBinding>();

        if (source is not null)
        {
            foreach (HotkeyBinding binding in source)
            {
                HotkeyActionDefinition? definition = HotkeyCatalog.Find(binding.ActionId);
                if (definition is not null && definition.Scope == HotkeyScope.Global)
                {
                    result.Add(binding);
                }
            }
        }

        foreach (HotkeyActionDefinition action in HotkeyCatalog.All)
        {
            if (action.Scope != HotkeyScope.Global)
            {
                continue;
            }

            if (result.All(binding => !string.Equals(binding.ActionId, action.Id, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new HotkeyBinding(action.Id, action.DefaultKeys));
            }
        }

        return result;
    }

    private string? BindingKeys(string actionId) =>
        _bindings.FirstOrDefault(binding => string.Equals(binding.ActionId, actionId, StringComparison.OrdinalIgnoreCase))?.Keys;

    private void SetBinding(string actionId, string? keys)
    {
        for (int i = 0; i < _bindings.Count; i++)
        {
            if (string.Equals(_bindings[i].ActionId, actionId, StringComparison.OrdinalIgnoreCase))
            {
                _bindings[i] = new HotkeyBinding(_bindings[i].ActionId, keys);
                return;
            }
        }

        _bindings.Add(new HotkeyBinding(actionId, keys));
    }

    private Button? RowFor(string actionId) => actionId switch
    {
        "capture.region" => RowCapture,
        "capture.pinClipboard" => RowPinClipboard,
        "settings.open" => RowSettings,
        _ => null,
    };

    private static string? RowActionId(Button row) => row.Tag as string;

    private void RefreshRows()
    {
        // 行标题一律取动作表：同一个动作在向导 / 托盘 / 设置里必须是同一个名字
        // （评审 P1-7：向导叫"截图"、设置叫"截图（区域）"会让人对不上号）。
        RowCaptureTitle.Text = HotkeyCatalog.Require("capture.region").DisplayName;
        RowPinClipboardTitle.Text = HotkeyCatalog.Require("capture.pinClipboard").DisplayName;
        RowSettingsTitle.Text = HotkeyCatalog.Require("settings.open").DisplayName;

        ApplyRow(RowCapture, "capture.region");
        ApplyRow(RowPinClipboard, "capture.pinClipboard");
        ApplyRow(RowSettings, "settings.open");

        // Hero 与自启动说明里的键位同样要跟着绑定走：写死 F1 会在用户改键后
        // 与同一页的键帽自相矛盾（"按一下 F1" + 一枚显示 Ctrl+Shift+A 的键帽）。
        string? captureKeys = BindingKeys("capture.region");

        HeroHeadline.Text = ComposeHeroText(captureKeys);
        AutoStartHint.Text = ComposeAutoStartHint(captureKeys);
    }

    /// <summary>自检用：Hero 文案（键位取当前绑定，未绑定时给可执行的指引而不是写死一个键）。</summary>
    internal static string ComposeHeroText(string? captureKeys)
    {
        string keys = string.IsNullOrWhiteSpace(captureKeys) ? "快捷键" : FormatKeys(captureKeys);
        return $"按一下 {keys}，就能截下屏幕上的任何一块";
    }

    /// <summary>自检用：自启动说明文案。</summary>
    internal static string ComposeAutoStartHint(string? captureKeys)
    {
        string keys = string.IsNullOrWhiteSpace(captureKeys) ? "快捷键" : FormatKeys(captureKeys);
        return $"登录 Windows 后自动驻留托盘，{keys} 随时可用。之后可以在托盘菜单里改。";
    }

    private void ApplyRow(Button row, string actionId)
    {
        string? keys = BindingKeys(actionId);
        GetKeycapText(actionId).Text = FormatKeys(keys);
        GetKeycapBorder(actionId).ToolTip = string.IsNullOrWhiteSpace(keys)
            ? "未绑定 —— 点这一行后按下新的组合键"
            : $"当前 {FormatKeys(keys)} —— 点这一行改键";
    }

    private TextBlock GetKeycapText(string actionId) => actionId switch
    {
        "capture.region" => RowCaptureKeys,
        "capture.pinClipboard" => RowPinClipboardKeys,
        "settings.open" => RowSettingsKeys,
        _ => RowCaptureKeys,
    };

    private Border GetKeycapBorder(string actionId) => actionId switch
    {
        "capture.region" => RowCaptureKeycap,
        "capture.pinClipboard" => RowPinClipboardKeycap,
        "settings.open" => RowSettingsKeycap,
        _ => RowCaptureKeycap,
    };

    /// <summary>键位展示：数据字体下的空格可读性（"Ctrl+Shift+," 挤在一起读不出是三个键）。</summary>
    private static string FormatKeys(string? keys) =>
        string.IsNullOrWhiteSpace(keys) ? "未绑定" : keys.Replace("+", " + ", StringComparison.Ordinal);

    // ────────────────────────────────────────────────────────────────────────
    //  录制态
    // ────────────────────────────────────────────────────────────────────────

    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button row)
        {
            return;
        }

        Button? current = _recordingRow;

        // 点同一行 = 取消录制；点另一行 = 切过去
        if (!ReferenceEquals(current, row))
        {
            if (current is not null)
            {
                CancelRecording(current);
            }

            StartRecording(row);
            return;
        }

        CancelRecording(row);
    }

    private void StartRecording(Button row)
    {
        string? actionId = RowActionId(row);
        if (actionId is null)
        {
            return;
        }

        _keysBeforeRecording[actionId] = BindingKeys(actionId);
        _recordingRow = row;

        HideStatus();
        SetRowVisuals(row, recording: true, conflict: false);
        StartPulse(row);

        // 让整窗拿到键盘焦点：Esc / 组合键都从窗口的 PreviewKeyDown 统一派发
        Focus();
    }

    /// <summary>Esc / 关闭 / 点同一行：恢复进入录制前的键位，不动其余行。</summary>
    private void CancelRecording(Button row)
    {
        string? actionId = RowActionId(row);
        if (actionId is not null && _keysBeforeRecording.TryGetValue(actionId, out string? previous))
        {
            SetBinding(actionId, previous);
        }

        EndRecording(row);
    }

    private void EndRecording(Button row)
    {
        _recordingRow = null;
        StopPulse(row);
        SetRowVisuals(row, recording: false, conflict: false);
        RefreshRows();
    }

    /// <summary>把一行的三种状态（常态 / 录制中 / 冲突）同步到模板状态与行内元素上。</summary>
    private void SetRowVisuals(Button row, bool recording, bool conflict)
    {
        string? actionId = RowActionId(row);
        KeycapButton.SetIsRecording(row, recording);
        KeycapButton.SetIsConflict(row, conflict && !recording);

        if (actionId is null)
        {
            return;
        }

        GetHint(actionId).Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        GetState(actionId).Visibility = recording ? Visibility.Visible : Visibility.Collapsed;

        Brush iconBrush = FocalPalette.OnDarkMuted;
        if (conflict)
        {
            iconBrush = FocalPalette.SignalDanger;
        }
        else if (recording)
        {
            iconBrush = FocalPalette.Accent;
        }

        GetGlyph(actionId).SetValue(TextElement.ForegroundProperty, iconBrush);
        GetIconBorder(actionId).BorderBrush = conflict
            ? FocalPalette.SignalDanger
            : recording
                ? FocalPalette.AccentBorder
                : FocalPalette.HairlineDark;

        // 键帽：录制 / 冲突时描线与字色跟着行状态走
        Border keycap = GetKeycapBorder(actionId);
        keycap.BorderBrush = conflict
            ? FocalPalette.SignalDanger
            : recording
                ? FocalPalette.Accent
                : FocalPalette.HairlineDarkStrong;
        GetKeycapText(actionId).Foreground = conflict
            ? FocalPalette.SignalDanger
            : recording
                ? FocalPalette.Accent
                : FocalPalette.OnDarkMuted;
    }

    /// <summary>录制态 1.2s 透明度脉冲（0.6s 单程 + AutoReverse = 1.2s 一个来回）。</summary>
    private void StartPulse(Button row)
    {
        StopPulse(row);

        var animation = new DoubleAnimation(1.0, 0.55, PulseHalfPeriod)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        Storyboard.SetTarget(animation, row);
        Storyboard.SetTargetProperty(animation, new PropertyPath(OpacityProperty));

        _pulses[row] = storyboard;
        storyboard.Begin(row, true);
    }

    private void StopPulse(Button row)
    {
        if (_pulses.Remove(row, out Storyboard? storyboard))
        {
            storyboard.Stop(row);
        }

        row.BeginAnimation(OpacityProperty, null);
        row.Opacity = 1;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingRow is null)
        {
            return;
        }

        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Esc 取消：恢复原键位
        if (key == Key.Escape)
        {
            CancelRecording(_recordingRow);
            return;
        }

        if (IsModifierKey(key))
        {
            return; // 只按修饰键不算
        }

        CompleteRecording(_recordingRow, key, Keyboard.Modifiers);
    }

    /// <summary>收尾一次录制：校验 → 通过则落到内存绑定表，不通过则原样回退。</summary>
    private bool CompleteRecording(Button row, Key key, ModifierKeys modifiers)
    {
        string? actionId = RowActionId(row);
        if (actionId is null)
        {
            return false;
        }

        string? keys = ComposeKeys(key, modifiers);
        if (keys is null)
        {
            RejectKeys(row, actionId, "这个按键没法登记成全局热键（例如 Win 组合由系统独占，或该键不在支持列表里）。请换一个组合。");
            return false;
        }

        if (HotkeySpec.TryParse(keys, out HotkeySpec spec, out _))
        {
            if (ReservedHotkeys.IsReserved(spec, out string reason))
            {
                RejectKeys(row, actionId, $"{spec} 是系统保留键：{reason}请换一个组合。");
                return false;
            }

            IReadOnlyList<HotkeyConflictReport> conflicts = ConflictsOf(actionId, spec);
            if (conflicts.Count > 0)
            {
                // 冲突键位不落到界面上（否则用户会以为已经生效），原样回退到进入录制前的键位
                SetBinding(actionId, _keysBeforeRecording.GetValueOrDefault(actionId));
                ShowStatus($"「{HotkeyCatalog.Find(actionId)?.DisplayName ?? actionId}」的 {spec} 与其它动作冲突："
                           + string.Join(" ", conflicts.Select(report => report.Message))
                           + " 已还原为原来的键位。");
                MarkConflict(row, conflict: true);
                EndRecording(row);
                return false;
            }
        }

        SetBinding(actionId, keys);
        _recorded[actionId] = new HotkeyBinding(actionId, keys);
        EndRecording(row);
        HotkeyRecorded?.Invoke(this, new HotkeyRecordedEventArgs(actionId, keys, false));
        return true;
    }

    /// <summary>保留键 / 键名不可解析：冲突提示 + 键位原样回退。</summary>
    private void RejectKeys(Button row, string actionId, string message)
    {
        SetBinding(actionId, _keysBeforeRecording.GetValueOrDefault(actionId));
        ShowStatus(message);
        MarkConflict(row, conflict: true);
        EndRecording(row);
    }

    private void MarkConflict(Button row, bool conflict) => SetRowVisuals(row, recording: false, conflict: conflict);

    /// <summary>
    /// 窗口内能自行判定的冲突（与 HotkeyConflictDetector 同一套结论，L2 内部撞键）。
    /// L3 常见冲突源 / L1 系统占用属于宿主（注册探针）的职责，这里不假装知道。
    /// </summary>
    private IReadOnlyList<HotkeyConflictReport> ConflictsOf(string actionId, HotkeySpec spec)
    {
        var trial = new List<HotkeyBinding>(_bindings.Count);
        foreach (HotkeyBinding binding in _bindings)
        {
            trial.Add(string.Equals(binding.ActionId, actionId, StringComparison.OrdinalIgnoreCase)
                ? new HotkeyBinding(binding.ActionId, spec.ToString(), binding.Enabled)
                : binding);
        }

        return HotkeyConflictDetector
            .DetectInternalDuplicates(trial)
            .Where(report => string.Equals(report.ActionId, actionId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static bool IsModifierKey(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt
        or Key.LWin or Key.RWin
        or Key.System;

    /// <summary>
    /// 把 WPF 按键 + 修饰键转成登记用的键位文本；无法正确往返的键返回 null。
    /// 与 <c>SettingsWindow.ComposeKeys</c> 同一套规则（键位文本必须能往返解析，否则界面显示了却注册不了）。
    /// </summary>
    private static string? ComposeKeys(Key key, ModifierKeys modifiers)
    {
        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            return null;
        }

        uint bits = 0;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            bits |= HotkeySpec.ModControl;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            bits |= HotkeySpec.ModShift;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            bits |= HotkeySpec.ModAlt;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            bits |= HotkeySpec.ModWin;
        }

        var candidate = new HotkeySpec(bits, (uint)virtualKey);
        string text = candidate.ToString();
        return HotkeySpec.TryParse(text, out HotkeySpec roundTrip, out _) && roundTrip.Equals(candidate) ? text : null;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  状态面（语义色只出现在这里）
    // ────────────────────────────────────────────────────────────────────────

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusBanner.Visibility = Visibility.Visible;
    }

    private void HideStatus() => StatusBanner.Visibility = Visibility.Collapsed;

    // ────────────────────────────────────────────────────────────────────────
    //  底部动作
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 无边框窗口（WindowStyle=None）默认没有可拖拽的标题栏；这里补上，并让标题栏上的按钮
    /// （关闭）保留自己的点击行为 —— 光标落在 Button 上时不拖窗。
    /// </summary>
    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsOverButton(sender, e))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标在 DragMove 期间已经被松开：忽略，窗口保持原位置
        }
    }

    private static bool IsOverButton(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Visual reference || e.OriginalSource is not DependencyObject source)
        {
            return false;
        }

        DependencyObject? node = source;
        while (node is not null)
        {
            if (node is ButtonBase)
            {
                return true;
            }

            if (ReferenceEquals(node, reference))
            {
                break;
            }

            node = node is Visual ? VisualTreeHelper.GetParent(node) : null;
        }

        return false;
    }

    private void OnStartClick(object sender, RoutedEventArgs e) => CloseWith(finished: true, skipped: false);

    private void OnSkipClick(object sender, RoutedEventArgs e) => CloseWith(finished: false, skipped: true);

    private void OnManualClick(object sender, RoutedEventArgs e) => ManualRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseWith(finished: false, skipped: false);

    private void CloseWith(bool finished, bool skipped)
    {
        if (_recordingRow is not null)
        {
            CancelRecording(_recordingRow);
        }

        SkipRequested = skipped;
        FinishedByUser = finished;

        if (skipped)
        {
            Skip?.Invoke(this, EventArgs.Empty);
        }
        else if (finished)
        {
            Finished?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        // 模态（ShowDialog）下用它关窗；非模态（Show）下 DialogResult 会抛，退化为 Close()
        if (_shownAsDialog)
        {
            DialogResult = finished || skipped;
        }
        else
        {
            Close();
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  行内元素寻址（三段内容各自命名的元素）
    // ────────────────────────────────────────────────────────────────────────

    private TextBlock GetHint(string actionId) => actionId switch
    {
        "capture.region" => RowCaptureHint,
        "capture.pinClipboard" => RowPinClipboardHint,
        _ => RowSettingsHint,
    };

    private StackPanel GetState(string actionId) => actionId switch
    {
        "capture.region" => RowCaptureState,
        "capture.pinClipboard" => RowPinClipboardState,
        _ => RowSettingsState,
    };

    private FrameworkElement GetGlyph(string actionId) => actionId switch
    {
        "capture.region" => RowCaptureGlyph,
        "capture.pinClipboard" => RowPinClipboardGlyph,
        _ => RowSettingsGlyph,
    };

    private Border GetIconBorder(string actionId) => actionId switch
    {
        "capture.region" => RowCaptureIcon,
        "capture.pinClipboard" => RowPinClipboardIcon,
        _ => RowSettingsIcon,
    };
}

/// <summary>一个动作刚录制完成的键位。<see cref="HasConflict"/> 由窗口内部检测给出（L2 内部撞键）。</summary>
public sealed class HotkeyRecordedEventArgs : EventArgs
{
    public HotkeyRecordedEventArgs(string actionId, string keys, bool hasConflict)
    {
        ActionId = actionId;
        Keys = keys;
        HasConflict = hasConflict;
    }

    /// <summary>动作 id（<see cref="HotkeyCatalog"/> 的键，例如 capture.region）。</summary>
    public string ActionId { get; }

    /// <summary>规范化键位文本（例如 "Ctrl+Shift+A"），可直接写进 AppSettings.Hotkeys。</summary>
    public string Keys { get; }

    public bool HasConflict { get; }
}

/// <summary>
/// 热键行的两个状态附加属性（挂在 <see cref="Button"/> 上，由窗口 code-behind 驱动）。
///
/// 为什么不用 <c>ToggleButton.IsChecked</c>：行本身是"点击即录制"的 Button，
/// 借用 ToggleButton 的选中态来表达"录制中"会把按钮的勾选语义和录制状态绑在一起，
/// 且 <c>Tag="conflict"</c> 那种哨兵值会让行的动作 id（Tag）被状态污染。
/// 附加属性只负责把状态转成模板里的 VisualState，Tag 始终是动作 id。
/// </summary>
public static class KeycapButton
{
    /// <summary>true = 这一行正在录制（琥珀边界 + 琥珀字 + 1.2s 透明度脉冲）。</summary>
    public static readonly DependencyProperty IsRecordingProperty = DependencyProperty.RegisterAttached(
        "IsRecording", typeof(bool), typeof(KeycapButton),
        new PropertyMetadata(false, OnStateChanged));

    /// <summary>true = 这一行刚录到一个冲突/保留键（danger 描边，只出现在状态面）。</summary>
    public static readonly DependencyProperty IsConflictProperty = DependencyProperty.RegisterAttached(
        "IsConflict", typeof(bool), typeof(KeycapButton),
        new PropertyMetadata(false, OnStateChanged));

    public static void SetIsRecording(DependencyObject element, bool value) =>
        element.SetValue(IsRecordingProperty, value);

    public static bool GetIsRecording(DependencyObject element) =>
        (bool)element.GetValue(IsRecordingProperty);

    public static void SetIsConflict(DependencyObject element, bool value) =>
        element.SetValue(IsConflictProperty, value);

    public static bool GetIsConflict(DependencyObject element) =>
        (bool)element.GetValue(IsConflictProperty);

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control control)
        {
            return;
        }

        string state = GetIsConflict(control)
            ? "Conflict"
            : GetIsRecording(control)
                ? "RecordingOn"
                : "RecordingOff";

        VisualStateManager.GoToState(control, state, false);
    }
}
