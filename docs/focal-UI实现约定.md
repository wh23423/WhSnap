# Focal UI 实现约定（WPF 侧）

> 设计来源：`design/focal/`（UI 工程师交付包 v1.0）。
> 本文件是实现方（WPF）与设计包之间的落地约定，实现 UI 时先读它，再读 `DESIGN.md`。

## 1. 令牌只有一个来源

| 文件 | 作用 |
|---|---|
| `src/WhSnap.App/Theme/FocalTheme.xaml` | **令牌唯一来源**：颜色 / 画刷 / 字体 / 字号 / 半径 / 度量 / 间距 / 层级 / 文本样式 |
| `src/WhSnap.App/Theme/FocalControls.xaml` | 组件层：按钮 / 工具键 / 分段 / 开关 / 复选框 / 输入 / 下拉 / 滑杆 / 滚动条 / 列表行 / 导航项 / 横幅 / 卡片 / 键帽 / 色板 / 标题栏 / 菜单项 |
| `src/WhSnap.App/Theme/FocalIcons.xaml` | 图标几何，键名 `Focal.Icon.<icons.js 的名字>`；只跟随 `Foreground`，本身无颜色 |
| `src/WhSnap.App/Theme/FocalPalette.cs` | **C# 侧唯一取色入口** |

三份字典由 `App.xaml` 合并进 `Application.Resources`。

## 2. 硬规则（`--focal-test` 会检查）

1. **界面代码里不允许出现字面量色值**：XAML 用 `{StaticResource Focal.*}`，C# 用 `FocalPalette.*`。
   唯一的例外是"渲染用户内容"的文件（`Annotation/AnnotationRenderer.cs`、`Pin/PinContentRenderer.cs`、
   `Pin/PinWindow.cs`、`Services/SettingsService.cs`、`Diagnostics/*`）——那里画的是用户的图/色，不是界面主题。
   确有必要的单行可以标 `focal:allow-literal`（写在该行或它上一行），扫描器会放过。
2. `FocalPalette` 取不到令牌**抛异常**，不静默兜底。
3. 度量必须是设计值：控件高 30、主操作 34、列表行 32（双行 40）、工具条 40、浮动条 44、标题栏 32、
   圆角 6 / 10 / 14、界面基准字号 14。
4. `#F5A524`（`Focal.AccentBrush`）**永不承担小字正文**：暗底可用；浅底只能做填充（配 `AccentInk`）或换
   `AccentDeepBrush`。
5. 阴影只给"浮在不可控内容之上"的面（`Focal.ChromeSurface` / `Focal.ShadowChrome`）。
   按钮、卡片、列表行、设置行、文字一律平铺。
6. 全圆只给状态与连续量（状态点、胶囊、开关轨道与握把、滑杆握把、滚动条拇指）；动作与容器一律 6/10/14，CTA 不用胶囊。
7. 数字（坐标 / 尺寸 / 色值 / 快捷键 / 路径）走 `Focal.DataText` 或 `Focal.DataFont` + tabular-nums。
8. 字重只用 400 / Medium（数据）/ SemiBold，不出现 Bold(700)。

### 2.1 两个"构建期查不出来"的坑（都有过真实事故）

- **隐式文本样式不许写死前景色。** `FocalControls.xaml` 里的隐式 `TextBlock` 样式只给字体与行高，
  前景色靠继承最近控件的 `Foreground`（无控件祖先时回落 `OnDark`）。
  否则"琥珀填充 + 墨字"的选中态会被一个白色文字毁掉（对比度 1.5:1）。
- **`RowDefinition.Height` / `ColumnDefinition.Width` 不能吃 `sys:Double` 令牌。**
  `GridLength` 依赖属性**不做类型转换**，`Height="{StaticResource Focal.TitleBarHeight}"` 会在打开窗口时抛
  `XamlParseException: "32"不是属性"Height"的有效值`（构建期完全看不出来）。
  用 `Focal.TitleBarHeightLength` / `Focal.ToolbarHeightLength` / `Focal.FloatbarHeightLength` /
  `Focal.HintbarHeightLength` / `Focal.RowHeightLength` / `Focal.StatusBarHeightLength` 这些 `GridLength` 令牌。

## 3. 常用键

```
画刷   Focal.{Void,Obsidian900,Obsidian800,Obsidian700,Obsidian600,Canvas}Brush
       Focal.{Paper,Linen,LinenDeep}Brush
       Focal.{Ink,InkMuted,InkFaint,OnDark,OnDarkMuted,OnDarkFaint}Brush
       Focal.{Accent,AccentHover,AccentPress,AccentDeep,AccentInk,AccentTint,AccentRing,AccentBorder}Brush
       Focal.{HairlineDark,HairlineDarkStrong,HairlineLight}Brush
       Focal.{SignalOk,SignalWarn,SignalDanger}Brush
       Focal.{MeasureSelect,MeasureElement,MeasureCenter}Brush
文本   Focal.{Display,Title,Subtitle,Body,BodyStrong,Caption,Micro,Data,DataLarge,Muted,Faint}Text
样式   Focal.Button.{Primary,PrimaryLarge,Secondary,Small,Quiet,Danger,Icon}
       Focal.{Tool,ToolWide,Segment,Switch,Check,Input,InputData,Select,Slider,Swatch}
       Focal.{Card,Chip,ChipAccent,Banner,BannerWarn,BannerDanger,BannerOk}
       Focal.{Rail,RailItem,ListItem,Row,RowLabel,RowHint,Kbd,KbdText,ChromeSurface}
       Focal.{WindowShell,TitleBar,TitleBarText,WindowButton,WindowButtonClose,StatusBar,StatusDot}
       Focal.{KeycapButton,MenuItem,DividerV,DividerH,FocusVisual}
度量   Focal.{ControlHeight,ControlHeightLarge,ControlHeightSmall,RowHeight,RowHeightLarge,
       ToolbarHeight,FloatbarHeight,TitleBarHeight,StatusBarHeight,RailWidth,HandleSize,
       IconSm,IconMd,IconLg,IconXl,BodySize,CaptionSize,MicroSize,DataSize,DataLargeSize}
       Focal.Radius{Hair,Control,Card,Panel}
```

C# 侧对应属性见 `FocalPalette`（`FocalPalette.Accent`、`FocalPalette.Metric("Focal.RowHeight")`、
`FocalPalette.Scrim(percent)` 等）。

## 4. 构建与验证

```powershell
# 并发写代码时所有构建必须走这个加锁包装，避免 MSBuild 争用 obj/bin
& .\build\tools\focal-build.ps1 -Quiet

# 设计一致性自检（令牌齐全 / 度量 / 图标几何 / 对比度红线 / 字面量色值扫描）
.\src\WhSnap.App\bin\Release\net8.0-windows10.0.22000.0\WhSnap.exe --focal-test

# 视觉回归：令牌表 / 组件层 / 图标集 + 四个真实窗口整窗渲染成 PNG（离屏，不抢焦点）
.\src\WhSnap.App\bin\Release\net8.0-windows10.0.22000.0\WhSnap.exe --focal-shots --out artifacts\focal-shots

# 首次运行向导目视验收（不读配置、不写配置）
.\src\WhSnap.App\bin\Release\net8.0-windows10.0.22000.0\WhSnap.exe --onboarding-preview

# 图标集单独校验（临时工程 + XamlReader.Load）
& .\build\tools\focal-icons-check.ps1

# 全量回归（构建 + 单测 + 11 个自检命令 + 手册覆盖率）
powershell -File artifacts\probe\run-full-regression.ps1
```

> ⚠️ 渲染出来的 PNG 才是"真相"：`read_image` 的预览会被缩放，细看文字颜色/裁切请用
> `System.Drawing` 采样像素或放大局部，别凭预览下结论（本轮就靠它抓到了"白字压琥珀"）。

## 5. 六个界面 → 落地位置

| 视觉稿 | 落地 |
|---|---|
| `surfaces/overlay.html` | `Overlay/OverlayWindow.xaml(.cs)` + `Overlay/OverlaySession.cs` |
| `surfaces/annotate.html` | `Annotation/AnnotationWindow.xaml(.cs)` + `Annotation/AnnotationRenderer.cs` |
| `surfaces/settings.html` | `Settings/SettingsWindow.xaml(.cs)` |
| `surfaces/pin-panel.html` | `Pin/PinPanelWindow.xaml(.cs)` |
| `surfaces/tray.html` | `Tray/TrayHost.cs`（WinForms 自绘 `ToolStripRenderer`） |
| `surfaces/onboarding.html` | `Onboarding/OnboardingWindow.xaml(.cs)` |

## 6. 画布尺寸不是响应式

视觉稿是**固定画布**（overlay 1440×900、annotate 1280×800、settings 980×680、pin-panel 760×560、
tray 700×760、onboarding 880×620）。WPF 侧窗口尺寸可不同，但**密度、度量、圆角、层级、颜色关系**必须一致。
