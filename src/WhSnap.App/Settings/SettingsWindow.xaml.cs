using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WhSnap.Core;
using WhSnap.Services;

namespace WhSnap.Settings;

/// <summary>
/// 设置窗口。界面**由 <see cref="SettingsSchema"/> 自动生成**（规划 §6 的实现要点），
/// 因此加一个配置项只需要改 schema，不会出现"功能加了但设置界面漏了"。
///
/// 三条行为约定（对应 §6 四条原则）：
///   * 能配的都在这：schema 覆盖全部配置项，另有独立的热键页；
///   * 改完即时生效：写入后立刻落盘并广播（重启后才生效的项不存在）；
///   * 冲突绝不静默：三层检测结果显示在每行徽标 + 顶部横幅上，阻断策略可显式选择。
/// </summary>
internal partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly List<HotkeyRow> _hotkeyRows = [];
    private readonly Dictionary<string, TextBlock> _errorLabels = [];
    private readonly DispatcherTimer _saveTimer;

    private string? _recordingActionId;
    private IReadOnlyList<HotkeyRegistrationResult> _probeResults = [];
    private bool _building;

    private TextBlock? _versionText;
    private TextBlock? _updateStatusText;
    private Button? _checkUpdatesButton;
    private Button? _installUpdateButton;
    private UpdateCheckResult? _lastUpdateResult;

    public SettingsWindow(SettingsService settings)
    {
        InitializeComponent();

        _settings = settings;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _settings.Save();
        };

        PreviewKeyDown += OnPreviewKeyDown;
        BuildUi();
        RefreshHotkeyStatuses(probe: false);
    }

    /// <summary>热键绑定变化（用户改键位/停用）后由宿主重新注册。</summary>
    public Action<IReadOnlyList<HotkeyBinding>>? ApplyHotkeys { get; set; }

    /// <summary>L1 试注册探针（由宿主实现：放开自己的键 → 逐个试注册 → 恢复）。</summary>
    public Func<IReadOnlyList<HotkeyBinding>, IReadOnlyList<HotkeyRegistrationResult>>? ProbeHotkeys { get; set; }

    /// <summary>诊断信息（虚拟桌面、采集后端、贴图数量…），维护页展示。</summary>
    public Func<string>? DiagnosticsProvider { get; set; }

    /// <summary>清理贴图备份（维护页按钮）。</summary>
    public Action? ClearPinBackups { get; set; }

    /// <summary>更新链路（宿主注入）。为空时维护页会如实说"更新服务不可用"，而不是假装能检查。</summary>
    public UpdateCoordinator? Updater { get; set; }

    /// <summary>
    /// 维护页「下载并安装」的实现（宿主负责：下载 → 校验 → 询问 → 安装并退出自身）。
    /// 返回值是直接显示给用户的状态文字。
    /// </summary>
    public Func<UpdateManifest, Task<string>>? InstallUpdate { get; set; }

    /// <summary>配置发生变化（宿主可据此刷新托盘文案等）。</summary>
    public event Action? SettingsChanged;

    private AppSettings Settings => _settings.Current;

    // ---------------------------------------------------------------- 自检支持

    /// <summary>已生成控件的配置项（自检用它证明"schema 里每一项都有界面入口"）。</summary>
    internal IReadOnlyCollection<string> RenderedSettingKeys => _renderedKeys;

    private readonly HashSet<string> _renderedKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>界面上实际出现的热键行数（应等于动作表条目数）。</summary>
    internal int HotkeyRowCount => _hotkeyRows.Count;

    internal int TabCount => Tabs.Items.Count;

    /// <summary>
    /// 把一次按键转换成键位文本（录制控件用）。
    /// 自检直接调它来验证"某些键必须被拒绝、常见组合必须能被接受"。
    /// </summary>
    internal static string? TryComposeHotkey(System.Windows.Input.Key key, ModifierKeys modifiers) =>
        IsModifierKey(key) ? null : ComposeKeys(key, modifiers);

    /// <summary>模拟用户录制一次按键（自检用，绕开真实键盘事件）。</summary>
    internal bool SimulateRecording(System.Windows.Input.Key key, ModifierKeys modifiers)
    {
        string? keys = TryComposeHotkey(key, modifiers);
        if (keys is null)
        {
            return false;
        }

        SetHotkey("capture.region", keys);
        return true;
    }

    /// <summary>当前生效的热键绑定（自检读取用）。</summary>
    internal IReadOnlyList<HotkeyBinding> CurrentBindings => _settings.ResolveHotkeys();

    /// <summary>取某行的状态徽标文字（自检用它验证冲突判定确实显示出来了）。</summary>
    internal string StatusBadgeFor(string actionId) =>
        _hotkeyRows.FirstOrDefault(row => row.Definition.Id == actionId)?.StatusText.Text ?? string.Empty;

    /// <summary>维护页显示的版本串（自检断言"维护页真的显示了版本号"）。</summary>
    internal string MaintenanceVersionText => _versionText?.Text ?? string.Empty;

    /// <summary>维护页的更新结果文字（自检断言检查结论真的写进了界面）。</summary>
    internal string UpdateStatusText => _updateStatusText?.Text ?? string.Empty;

    /// <summary>维护页是否有「检查更新」与「下载并安装」入口。</summary>
    internal bool HasUpdateEntry => _checkUpdatesButton is not null && _installUpdateButton is not null;

    /// <summary>「下载并安装」当前是否可用（检查到新版本后才放开）。</summary>
    internal bool CanInstallUpdate => _installUpdateButton?.IsEnabled == true;

    // ---------------------------------------------------------------- 界面构建

    private void BuildUi()
    {
        _building = true;

        try
        {
            Tabs.Items.Clear();

            foreach (string group in SettingsSchema.Groups)
            {
                Tabs.Items.Add(BuildSettingTab(group));
            }

            Tabs.Items.Add(BuildHotkeyTab());
            Tabs.Items.Add(BuildMaintenanceTab());
        }
        finally
        {
            _building = false;
        }
    }

    private TabItem BuildSettingTab(string group)
    {
        var panel = new StackPanel { Margin = new Thickness(4) };

        foreach (var definition in SettingsSchema.InGroup(group))
        {
            panel.Children.Add(BuildSettingRow(definition));
        }

        return CreateTab(group, panel);
    }

    private static TabItem CreateTab(string header, UIElement content) => new()
    {
        Header = header,
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(12),
            Content = content,
        },
        Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B)),
        Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)),
    };

    private FrameworkElement BuildSettingRow(SettingDefinition definition)
    {
        var grid = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labels = new StackPanel();
        labels.Children.Add(new TextBlock { Text = definition.Label, FontWeight = FontWeights.SemiBold });

        if (!string.IsNullOrWhiteSpace(definition.Description))
        {
            labels.Children.Add(new TextBlock
            {
                Text = definition.Description,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                FontSize = 11,
                Margin = new Thickness(0, 2, 12, 0),
            });
        }

        var error = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A)),
            FontSize = 11,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 2, 0, 0),
        };

        _errorLabels[definition.Key] = error;
        labels.Children.Add(error);

        Grid.SetColumn(labels, 0);
        grid.Children.Add(labels);

        var control = BuildControl(definition);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        _renderedKeys.Add(definition.Key);
        return grid;
    }

    private FrameworkElement BuildControl(SettingDefinition definition)
    {
        string current = SettingsBinder.Read(Settings, definition.Key) ?? string.Empty;

        switch (definition.Kind)
        {
            case SettingKind.Bool:
            {
                var checkBox = new CheckBox
                {
                    IsChecked = current.Equals("true", StringComparison.OrdinalIgnoreCase),
                    VerticalAlignment = VerticalAlignment.Center,
                };

                checkBox.Checked += (_, _) => Apply(definition.Key, "true", checkBox);
                checkBox.Unchecked += (_, _) => Apply(definition.Key, "false", checkBox);
                return checkBox;
            }

            case SettingKind.Choice:
            {
                var combo = new ComboBox { Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
                foreach (var option in definition.Options ?? [])
                {
                    combo.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
                }

                foreach (ComboBoxItem item in combo.Items)
                {
                    if (string.Equals((string)item.Tag, current, StringComparison.OrdinalIgnoreCase))
                    {
                        combo.SelectedItem = item;
                        break;
                    }
                }

                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem { Tag: string value })
                    {
                        Apply(definition.Key, value, combo);
                    }
                };

                return combo;
            }

            case SettingKind.Int:
            case SettingKind.Double:
            {
                bool isDouble = definition.Kind == SettingKind.Double;
                double value = double.TryParse(current, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)
                    ? parsed
                    : definition.Min;

                var panel = new StackPanel { Orientation = Orientation.Horizontal };

                var textBox = new TextBox
                {
                    Text = current,
                    Width = 70,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var slider = new Slider
                {
                    Minimum = definition.Min,
                    Maximum = definition.Max,
                    Value = Math.Clamp(value, definition.Min, definition.Max),
                    Width = 190,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TickFrequency = definition.Step > 0 ? definition.Step : 1,
                    IsSnapToTickEnabled = definition.Step > 0,
                };

                slider.ValueChanged += (_, args) =>
                {
                    if (_building)
                    {
                        return;
                    }

                    string text = isDouble
                        ? args.NewValue.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                        : ((int)Math.Round(args.NewValue)).ToString(System.Globalization.CultureInfo.InvariantCulture);

                    textBox.Text = text;
                    Apply(definition.Key, text, slider);
                };

                textBox.LostFocus += (_, _) => Apply(definition.Key, textBox.Text, textBox);
                textBox.KeyDown += (_, args) =>
                {
                    if (args.Key == Key.Enter)
                    {
                        Apply(definition.Key, textBox.Text, textBox);
                    }
                };

                panel.Children.Add(slider);
                panel.Children.Add(textBox);

                if (!string.IsNullOrEmpty(definition.Unit))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = definition.Unit,
                        Margin = new Thickness(4, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }

                return panel;
            }

            case SettingKind.Color:
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal };
                var textBox = new TextBox { Text = current, Width = 110 };
                var swatch = new Border
                {
                    Width = 22,
                    Height = 22,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                    BorderThickness = new Thickness(1),
                    Background = BuildColorBrush(current),
                };

                textBox.LostFocus += (_, _) => Apply(definition.Key, textBox.Text, swatch);
                textBox.KeyDown += (_, args) =>
                {
                    if (args.Key == Key.Enter)
                    {
                        Apply(definition.Key, textBox.Text, swatch);
                    }
                };

                panel.Children.Add(textBox);
                panel.Children.Add(swatch);
                return panel;
            }

            case SettingKind.DirectoryPath:
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal };
                var textBox = new TextBox { Text = current, Width = 280 };

                var browse = new Button { Content = "浏览…" };
                browse.Click += (_, _) =>
                {
                    var dialog = new OpenFolderDialog { Title = $"选择{definition.Label}" };
                    if (Directory.Exists(textBox.Text))
                    {
                        dialog.InitialDirectory = textBox.Text;
                    }

                    if (dialog.ShowDialog() == true)
                    {
                        textBox.Text = dialog.FolderName;
                        Apply(definition.Key, textBox.Text, textBox);
                    }
                };

                textBox.LostFocus += (_, _) => Apply(definition.Key, textBox.Text, textBox);
                panel.Children.Add(textBox);
                panel.Children.Add(browse);
                return panel;
            }

            default:
            {
                var textBox = new TextBox { Text = current, Width = 280 };
                textBox.LostFocus += (_, _) => Apply(definition.Key, textBox.Text, textBox);
                textBox.KeyDown += (_, args) =>
                {
                    if (args.Key == Key.Enter)
                    {
                        Apply(definition.Key, textBox.Text, textBox);
                    }
                };

                return textBox;
            }
        }
    }

    private static Brush BuildColorBrush(string text) =>
        ColorValue.TryParse(text, out var color)
            ? new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B))
            : Brushes.Transparent;

    private void Apply(string key, string? value, FrameworkElement source)
    {
        if (_building)
        {
            return;
        }

        if (!SettingsBinder.Write(Settings, key, value, out string error))
        {
            if (_errorLabels.TryGetValue(key, out var label))
            {
                label.Text = error;
                label.Visibility = Visibility.Visible;
            }

            return;
        }

        if (_errorLabels.TryGetValue(key, out var existing))
        {
            existing.Visibility = Visibility.Collapsed;
        }

        ScheduleSave();

        // 系统集成与剪贴板监听需要宿主立刻反应
        SettingsChanged?.Invoke();
        UpdateBanner();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>立刻落盘（关闭设置窗口、改热键、导入后调用）。</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        _settings.Save();
    }

    // ---------------------------------------------------------------- 热键页

    private sealed class HotkeyRow
    {
        public required HotkeyActionDefinition Definition { get; init; }

        public required TextBlock KeysText { get; init; }

        public required TextBlock StatusText { get; init; }

        public required TextBlock DetailText { get; init; }
    }

    private TabItem BuildHotkeyTab()
    {
        var panel = new StackPanel { Margin = new Thickness(4) };

        panel.Children.Add(new TextBlock
        {
            Text = "全局热键会与整个系统抢键（L1 试注册判定）；遮罩/贴图窗口内的按键只在本程序的窗口里生效，不会与别的软件冲突。",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            Margin = new Thickness(0, 0, 0, 10),
        });

        foreach (HotkeyScope scope in Enum.GetValues<HotkeyScope>())
        {
            var actions = HotkeyCatalog.InScope(scope).ToList();
            if (actions.Count == 0)
            {
                continue;
            }

            panel.Children.Add(new TextBlock
            {
                Text = scope switch
                {
                    HotkeyScope.Global => "全局热键",
                    HotkeyScope.Overlay => "遮罩内按键",
                    _ => "贴图窗口内按键",
                },
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Margin = new Thickness(0, 12, 0, 6),
            });

            foreach (var action in actions)
            {
                panel.Children.Add(BuildHotkeyRow(action, scope));
            }
        }

        return CreateTab("快捷键", panel);
    }

    private FrameworkElement BuildHotkeyRow(HotkeyActionDefinition action, HotkeyScope scope)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labels = new StackPanel();
        labels.Children.Add(new TextBlock { Text = action.DisplayName, FontWeight = FontWeights.SemiBold });

        if (!string.IsNullOrWhiteSpace(action.Description))
        {
            labels.Children.Add(new TextBlock
            {
                Text = action.Description,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                FontSize = 11,
                Margin = new Thickness(0, 2, 12, 0),
            });
        }

        Grid.SetColumn(labels, 0);
        grid.Children.Add(labels);

        var keysText = new TextBlock
        {
            Text = "(未绑定)",
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
        };

        Grid.SetColumn(keysText, 1);
        grid.Children.Add(keysText);

        var statusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = string.Empty };
        var detailText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var record = new Button { Content = "录制" };
        record.Click += (_, _) => StartRecording(action.Id);

        var clear = new Button { Content = "清除" };
        clear.Click += (_, _) => SetHotkey(action.Id, null);

        var reset = new Button { Content = "默认" };
        reset.Click += (_, _) => SetHotkey(action.Id, action.DefaultKeys, forceDefault: true);

        buttons.Children.Add(record);
        buttons.Children.Add(clear);
        buttons.Children.Add(reset);
        buttons.Children.Add(statusText);

        var right = new StackPanel();
        right.Children.Add(buttons);
        right.Children.Add(detailText);

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        _hotkeyRows.Add(new HotkeyRow
        {
            Definition = action,
            KeysText = keysText,
            StatusText = statusText,
            DetailText = detailText,
        });

        return grid;
    }

    private void StartRecording(string actionId)
    {
        _recordingActionId = actionId;
        RefreshHotkeyStatuses(probe: false);

        var row = _hotkeyRows.FirstOrDefault(r => r.Definition.Id == actionId);
        if (row is not null)
        {
            row.KeysText.Text = "请按组合键…（Esc 取消）";
            row.DetailText.Text = "修饰键可组合（Ctrl/Shift/Alt/Win）；单独按修饰键不算，需要再按一个普通键。";
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingActionId is null)
        {
            return;
        }

        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            _recordingActionId = null;
            RefreshHotkeyStatuses(probe: false);
            return;
        }

        if (IsModifierKey(key))
        {
            return;
        }

        string? keys = ComposeKeys(key, Keyboard.Modifiers);
        string actionId = _recordingActionId;
        _recordingActionId = null;

        if (keys is null)
        {
            var row = _hotkeyRows.FirstOrDefault(r => r.Definition.Id == actionId);
            if (row is not null)
            {
                row.KeysText.Text = "(未绑定)";
            }

            MessageBox.Show(this, "这个按键暂时无法作为热键（例如 Win 键组合由系统独占、或该键不在支持列表里），请换一个组合。", "WhSnap 设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshHotkeyStatuses(probe: false);
            return;
        }

        SetHotkey(actionId, keys);
    }

    private static bool IsModifierKey(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt
        or Key.LWin or Key.RWin
        or Key.System;

    /// <summary>把 WPF 按键 + 修饰键转成登记用的键位文本；无法正确往返的键返回 null。</summary>
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

        // 必须能往返解析（有些键名不在解析表里，如 VK(0xXX)），否则界面显示了但注册不了
        return HotkeySpec.TryParse(text, out var roundTrip, out _) && roundTrip.Equals(candidate) ? text : null;
    }

    private void SetHotkey(string actionId, string? keys, bool forceDefault = false)
    {
        var definition = HotkeyCatalog.Require(actionId);
        string? previous = Settings.Hotkeys.TryGetValue(actionId, out string? existing) ? existing : null;

        if (forceDefault)
        {
            Settings.Hotkeys.Remove(actionId);
        }
        else if (string.IsNullOrWhiteSpace(keys))
        {
            Settings.Hotkeys.Remove(actionId);
        }
        else
        {
            Settings.Hotkeys[actionId] = keys.Trim();
        }

        Settings.DisabledHotkeys.RemoveAll(id => id.Equals(actionId, StringComparison.OrdinalIgnoreCase));

        // L2 预检：同作用域撞键必须显式选择，绝不静默失败
        var bindings = _settings.ResolveHotkeys();
        var conflicts = HotkeyConflictDetector.DetectInternalDuplicates(bindings)
            .Where(report => report.ActionId.Equals(actionId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (conflicts.Count > 0)
        {
            if (Settings.HotkeyConflictPolicy.Equals("save-disabled", StringComparison.OrdinalIgnoreCase))
            {
                // 用户在设置里选了"保存但停用"：仍然明确告知，只是不弹窗打断
                if (!Settings.DisabledHotkeys.Contains(actionId, StringComparer.OrdinalIgnoreCase))
                {
                    Settings.DisabledHotkeys.Add(actionId);
                }

                BannerText.Text = $"「{definition.DisplayName}」与其它动作冲突，已按设置保存但停用。";
            }
            else
            {
                string message = $"「{definition.DisplayName}」的 {keys} 与其它动作冲突：\n\n{conflicts[0].Message}\n\n"
                                 + "是 = 仍保存，但把这个动作停用（推荐）\n否 = 撤销本次修改\n取消 = 仍保存且不禁用（可能注册失败）";

                var choice = MessageBox.Show(this, message, "热键冲突", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

                if (choice == MessageBoxResult.No)
                {
                    if (previous is null)
                    {
                        Settings.Hotkeys.Remove(actionId);
                    }
                    else
                    {
                        Settings.Hotkeys[actionId] = previous;
                    }
                }
                else if (choice == MessageBoxResult.Yes)
                {
                    if (!Settings.DisabledHotkeys.Contains(actionId, StringComparer.OrdinalIgnoreCase))
                    {
                        Settings.DisabledHotkeys.Add(actionId);
                    }
                }
            }
        }

        ScheduleSave();
        ApplyHotkeyBindings();
        RefreshHotkeyStatuses(probe: true);
        UpdateBanner();
    }

    private void ApplyHotkeyBindings()
    {
        var bindings = _settings.ResolveHotkeys();
        ApplyHotkeys?.Invoke(bindings);
    }

    private void RefreshHotkeyStatuses(bool probe)
    {
        var bindings = _settings.ResolveHotkeys();
        var conflicts = HotkeyConflictDetector.DetectAll(bindings);

        if (probe && ProbeHotkeys is not null)
        {
            try
            {
                _probeResults = ProbeHotkeys(bindings);
            }
            catch (Exception ex)
            {
                Log.Warn($"热键占用探测失败：{ex.Message}");
                _probeResults = [];
            }
        }

        foreach (var row in _hotkeyRows)
        {
            var binding = bindings.First(b => b.ActionId == row.Definition.Id);
            row.KeysText.Text = binding.IsBound ? binding.Keys! : "(未绑定)";

            var rowConflicts = conflicts.Where(report => report.ActionId == row.Definition.Id).ToList();
            var probeResult = _probeResults.FirstOrDefault(result => result.ActionId == row.Definition.Id);

            string status;
            Brush color;

            if (!binding.Enabled)
            {
                status = "已停用";
                color = Brushes.Gray;
            }
            else if (!binding.IsBound)
            {
                status = "未绑定";
                color = Brushes.Gray;
            }
            else if (!binding.IsActive)
            {
                status = "键位非法";
                color = Brushes.OrangeRed;
            }
            else if (rowConflicts.Any(report => report.Blocks) || probeResult?.Status == HotkeyStatus.ConflictWithSystem)
            {
                status = "冲突";
                color = Brushes.OrangeRed;
            }
            else if (probeResult?.Status == HotkeyStatus.Registered)
            {
                status = "空闲";
                color = Brushes.LightGreen;
            }
            else
            {
                status = "已生效";
                color = Brushes.LightGreen;
            }

            row.StatusText.Text = $"　[{status}]";
            row.StatusText.Foreground = color;

            var details = new List<string>();

            if (binding.Definition.Scope == HotkeyScope.Global && probeResult is not null)
            {
                details.Add(probeResult.Message);
            }

            details.AddRange(rowConflicts.Select(report => report.Message + (report.Advice is null ? string.Empty : $" → {report.Advice}")));

            row.DetailText.Text = string.Join("\n", details);
        }

        UpdateBanner(conflicts);
    }

    private void UpdateBanner(IReadOnlyList<HotkeyConflictReport>? conflicts = null)
    {
        var bindings = _settings.ResolveHotkeys();
        conflicts ??= HotkeyConflictDetector.DetectAll(bindings);

        int blocking = conflicts.Count(report => report.Blocks);
        int l1 = _probeResults.Count(result => result.Status == HotkeyStatus.ConflictWithSystem);
        int active = bindings.Count(binding => binding.IsActive && binding.Definition.Scope == HotkeyScope.Global);

        var parts = new List<string>
        {
            $"全局热键 {active} 个",
            $"冲突检测：{HotkeyConflictDetector.Summarize(conflicts)}",
        };

        if (_probeResults.Count > 0)
        {
            parts.Add(l1 == 0 ? "L1 试注册：全部可用" : $"L1 试注册：{l1} 个已被占用");
        }

        parts.Add($"配置：{SettingsService.ConfigFilePath}");

        BannerText.Text = string.Join("　|　", parts);
        BannerBorder.BorderBrush = blocking > 0 || l1 > 0
            ? new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x5A))
            : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    }

    // ---------------------------------------------------------------- 维护页

    private TabItem BuildMaintenanceTab()
    {
        var panel = new StackPanel { Margin = new Thickness(4) };

        var diagnostics = new TextBlock
        {
            Text = DiagnosticsProvider?.Invoke() ?? "(诊断信息不可用)",
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            Margin = new Thickness(0, 0, 0, 12),
        };

        panel.Children.Add(new TextBlock { Text = "运行状态", FontWeight = FontWeights.Bold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(diagnostics);

        panel.Children.Add(new TextBlock { Text = "更新", FontWeight = FontWeights.Bold, FontSize = 13, Margin = new Thickness(0, 12, 0, 6) });

        _versionText = new TextBlock
        {
            Text = VersionSummary(),
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
        };

        panel.Children.Add(_versionText);

        var updateButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };

        _checkUpdatesButton = new Button { Content = "检查更新", Margin = new Thickness(0, 0, 6, 6) };
        _checkUpdatesButton.Click += async (_, _) => await RunUpdateCheckAsync();

        _installUpdateButton = new Button { Content = "下载并安装", Margin = new Thickness(0, 0, 6, 6), IsEnabled = false };
        _installUpdateButton.Click += async (_, _) => await RunUpdateInstallAsync();

        updateButtons.Children.Add(_checkUpdatesButton);
        updateButtons.Children.Add(_installUpdateButton);
        panel.Children.Add(updateButtons);

        _updateStatusText = new TextBlock
        {
            Text = "尚未检查更新。",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            Margin = new Thickness(0, 4, 0, 0),
        };

        panel.Children.Add(_updateStatusText);

        panel.Children.Add(new TextBlock
        {
            Text = "检查更新只读取一份很小的 update.json（版本 / 下载地址 / SHA-256），不上报任何本机信息；"
                   + "下载后先校验 SHA-256，对不上就删除并拒绝安装。安装会先退出 WhSnap，再静默运行安装包"
                   + "（per-user 安装，不需要管理员权限）。",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 0),
        });

        panel.Children.Add(new TextBlock { Text = "维护", FontWeight = FontWeights.Bold, FontSize = 13, Margin = new Thickness(0, 12, 0, 6) });

        var buttons = new WrapPanel();
        buttons.Children.Add(MakeButton("打开配置目录", () => OpenPath(SettingsService.ConfigDirectory)));
        buttons.Children.Add(MakeButton("打开保存目录", () => OpenPath(SaveService.ResolveDirectory(Settings))));
        buttons.Children.Add(MakeButton("复制当前配置到剪贴板", () =>
        {
            try
            {
                System.Windows.Clipboard.SetText(_settings.ExportJson());
                BannerText.Text = "当前配置已复制到剪贴板。";
            }
            catch (Exception ex)
            {
                BannerText.Text = $"复制配置失败：{ex.Message}";
            }
        }));
        buttons.Children.Add(MakeButton("清理贴图备份", () =>
        {
            ClearPinBackups?.Invoke();
            BannerText.Text = "已清理贴图备份目录。";
        }));
        buttons.Children.Add(MakeButton("打开日志目录", () => OpenPath(Log.LogDirectory)));

        panel.Children.Add(buttons);

        panel.Children.Add(new TextBlock
        {
            Text = "配置文件是纯 JSON，可以直接编辑后重启程序；导入时未知项与非法值都会被拒绝并报告，不会静默丢弃。",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            Margin = new Thickness(0, 12, 0, 0),
        });

        return CreateTab("维护", panel);
    }

    private static Button MakeButton(string content, Action action)
    {
        var button = new Button { Content = content, Margin = new Thickness(0, 0, 6, 6) };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>维护页顶部的版本串：版本号 + 通道 + 清单来源（本地路径 / https 一眼可辨）。</summary>
    private string VersionSummary()
    {
        string version = Updater?.CurrentVersion ?? UpdateService.GetCurrentVersion();
        string channel = Updater?.Channel ?? UpdateChannels.Parse(Settings.UpdateChannel);
        string source = Updater?.ManifestSource ?? Settings.UpdateManifestUrl;
        string kind = UpdateService.ResolveSourceKind(source) == UpdateSourceKind.LocalFile ? "本地路径" : "https";

        return $"当前版本　WhSnap {version}（{channel} 通道）\n更新清单　{kind}：{source}";
    }

    /// <summary>
    /// 检查更新并把结论写进维护页。自检直接调用它（与按钮同一条路径），
    /// 因此"按钮 → 检查 → 状态文字"这条链路是被机器验证过的。
    /// </summary>
    internal async Task<string> RunUpdateCheckAsync()
    {
        if (Updater is null)
        {
            SetInstallEnabled(false);
            return SetUpdateStatus("更新服务不可用。");
        }

        SetUpdateStatus("正在检查更新…");
        SetInstallEnabled(false);

        if (_checkUpdatesButton is not null)
        {
            _checkUpdatesButton.IsEnabled = false;
        }

        try
        {
            var result = await Updater.CheckAsync();
            _lastUpdateResult = result;

            if (result is { CanUpdate: true, Manifest: not null })
            {
                // UpdateRules 给出的原因本身就含"发现新版本 x.y.z（当前 …）：更新说明"，直接呈现
                SetInstallEnabled(InstallUpdate is not null);
                return SetUpdateStatus(result.Message);
            }

            return SetUpdateStatus(result.Message);
        }
        catch (Exception ex)
        {
            // 正常情况下 UpdateCoordinator 不会抛（失败已转成原因文字），这里是最后一道兜底
            Log.Error("检查更新失败", ex);
            return SetUpdateStatus($"检查更新失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (_checkUpdatesButton is not null)
            {
                _checkUpdatesButton.IsEnabled = true;
            }
        }
    }

    /// <summary>维护页「下载并安装」：下载/校验/询问/退出自身都由宿主实现，这里只负责显示状态。</summary>
    internal async Task<string> RunUpdateInstallAsync()
    {
        var manifest = _lastUpdateResult is { CanUpdate: true, Manifest: not null } result ? result.Manifest : null;

        if (manifest is null)
        {
            return SetUpdateStatus("没有可安装的更新，请先点「检查更新」。");
        }

        if (InstallUpdate is null)
        {
            return SetUpdateStatus("更新服务不可用。");
        }

        SetInstallEnabled(false);
        SetUpdateStatus($"正在下载 WhSnap {manifest.Version} 并校验 SHA-256…");

        try
        {
            return SetUpdateStatus(await InstallUpdate(manifest));
        }
        catch (Exception ex)
        {
            Log.Error("下载或安装更新失败", ex);
            SetInstallEnabled(true);
            return SetUpdateStatus($"更新失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private string SetUpdateStatus(string text)
    {
        if (_updateStatusText is not null)
        {
            _updateStatusText.Text = text;
        }

        return text;
    }

    private void SetInstallEnabled(bool enabled)
    {
        if (_installUpdateButton is not null)
        {
            _installUpdateButton.IsEnabled = enabled;
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开目录失败（{path}）：{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 底部按钮

    private void ProbeHotkeys_Click(object sender, RoutedEventArgs e)
    {
        RefreshHotkeyStatuses(probe: true);
        UpdateBanner();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出 WhSnap 配置",
            FileName = $"whsnap-settings-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Filter = "JSON 配置|*.json",
            DefaultExt = ".json",
            InitialDirectory = SettingsService.ConfigDirectory,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, _settings.ExportJson(), Encoding.UTF8);
            BannerText.Text = $"配置已导出：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "WhSnap 设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 WhSnap 配置",
            Filter = "JSON 配置|*.json|所有文件|*.*",
            InitialDirectory = SettingsService.ConfigDirectory,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string json;

        try
        {
            json = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"读取失败：{ex.Message}", "WhSnap 设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = _settings.ImportJson(json);
        SaveNow();

        _building = true;
        try
        {
            BuildUi();
        }
        finally
        {
            _building = false;
        }

        ApplyHotkeyBindings();
        RefreshHotkeyStatuses(probe: true);
        SettingsChanged?.Invoke();

        MessageBox.Show(
            this,
            $"导入完成：{result.Describe()}"
            + (result.Unknown.Count > 0 ? $"\n\n未识别（已忽略）：{string.Join("、", result.Unknown)}" : string.Empty)
            + (result.Invalid.Count > 0 ? $"\n\n取值非法（已忽略）：{string.Join("、", result.Invalid)}" : string.Empty),
            "WhSnap 设置",
            MessageBoxButton.OK,
            result.HasProblems ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "把全部设置恢复为默认值？（热键也会一起恢复）", "WhSnap 设置", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        SettingsBinder.ResetToDefaults(Settings);
        Settings.Hotkeys.Clear();
        Settings.DisabledHotkeys.Clear();
        Settings.HotkeyConflictPolicy = "block";
        SaveNow();

        _building = true;
        try
        {
            BuildUi();
        }
        finally
        {
            _building = false;
        }

        ApplyHotkeyBindings();
        RefreshHotkeyStatuses(probe: true);
        SettingsChanged?.Invoke();
        BannerText.Text = "已恢复默认设置。";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _saveTimer.Stop();
        _settings.Save();
        base.OnClosed(e);
    }
}
