---
version: 1.0
name: Focal
description: A precision-instrument interface for a pixel-exact screen tool. Dark-first because the capture chrome floats on top of content it does not control; paper-light for surfaces you read at length. Exactly one interactive colour (Signal Amber), and it never carries small body text — on light surfaces it becomes a fill with ink glyphs, or the deep shade. Depth comes from surface steps and 1px hairlines; shadows exist only for chrome floating over uncontrolled content.

colors:
  # ---- Obsidian: 暗色机壳（承载操作） ----
  void: "#08090a"
  obsidian-900: "#101114"
  obsidian-800: "#16181c"
  obsidian-700: "#1e2126"
  obsidian-600: "#2a2e35"
  hairline-dark: "rgba(255,255,255,0.08)"
  hairline-dark-strong: "rgba(255,255,255,0.14)"
  inner-top-light: "rgba(255,255,255,0.06)"
  # ---- Paper: 纸色阅读面 ----
  paper: "#ffffff"
  linen: "#f4f4f2"
  linen-deep: "#e9e9e5"
  hairline-light: "rgba(20,22,26,0.10)"
  # ---- 文字 ----
  ink: "#14161a"
  ink-muted: "#5b6169"
  ink-faint: "#8a9098"
  on-dark: "#f2f3f5"
  on-dark-muted: "#a9aeb6"
  on-dark-faint: "#767c85"
  # ---- 强调（一族四档） ----
  accent: "#f5a524"
  accent-hover: "#ffbb4a"
  accent-press: "#d98e15"
  accent-deep: "#8a5a0a"
  accent-ink: "#1a1206"
  # ---- 语义（只出现在状态面） ----
  signal-ok: "#2fb27c"
  signal-warn: "#f5a524"
  signal-danger: "#e5484d"
  # ---- 量测标记（遮罩/标注专用） ----
  measure-select: "#f5a524"
  measure-element: "#57c7ff"
  measure-center: "#ffd34d"

typography:
  font-display: "Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI, system-ui, sans-serif"
  font-text: "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, system-ui, sans-serif"
  font-data: "Cascadia Code, Cascadia Mono, Consolas, Microsoft YaHei UI, ui-monospace, monospace"
  display:   { fontSize: 28px, fontWeight: 600, lineHeight: 1.15, letterSpacing: -0.4px }
  title:     { fontSize: 20px, fontWeight: 600, lineHeight: 1.25, letterSpacing: -0.3px }
  subtitle:  { fontSize: 15px, fontWeight: 600, lineHeight: 1.35, letterSpacing: -0.2px }
  body:      { fontSize: 14px, fontWeight: 400, lineHeight: 1.50, letterSpacing: 0 }
  body-strong: { fontSize: 14px, fontWeight: 600, lineHeight: 1.40, letterSpacing: -0.1px }
  caption:   { fontSize: 12px, fontWeight: 400, lineHeight: 1.45, letterSpacing: 0 }
  micro:     { fontSize: 11px, fontWeight: 600, lineHeight: 1.30, letterSpacing: 0.4px, textTransform: uppercase }
  data:      { fontFamily: "{typography.font-data}", fontSize: 13px, fontWeight: 500, lineHeight: 1.2, fontVariantNumeric: tabular-nums }
  data-lg:   { fontFamily: "{typography.font-data}", fontSize: 20px, fontWeight: 600, lineHeight: 1.10, letterSpacing: -0.4px }
  kbd:       { fontFamily: "{typography.font-data}", fontSize: 11px, fontWeight: 600, letterSpacing: 0.3px }

rounded:
  hair: 2px
  control: 6px
  card: 10px
  panel: 14px
  full: 9999px

spacing:
  "3xs": 2px
  "2xs": 4px
  xs: 6px
  sm: 8px
  md: 12px
  lg: 16px
  xl: 20px
  "2xl": 24px
  "3xl": 32px
  "4xl": 40px
  "5xl": 56px
  "6xl": 72px

metrics:
  control-height: 30px
  control-height-lg: 34px
  row-height: 32px
  row-height-lg: 40px
  bar-height: 40px
  titlebar-height: 32px

elevation:
  l0-flat: "no shadow, 1px hairline"
  l1-chrome: "inset 0 1px 0 rgba(255,255,255,0.06), 0 1px 2px rgba(0,0,0,0.40), 0 10px 30px rgba(0,0,0,0.55)"
  l2-dialog: "0 2px 6px rgba(16,18,22,0.08), 0 18px 48px rgba(16,18,22,0.18)"

motion:
  ease: "cubic-bezier(0.2, 0.8, 0.3, 1)"
  instant: 90ms
  fast: 140ms
  base: 220ms

components:
  button-primary:      { backgroundColor: "{colors.accent}", textColor: "{colors.accent-ink}", rounded: "{rounded.control}", height: 30px, padding: "0 12px", fontWeight: 600 }
  button-secondary:    { backgroundColor: "{colors.obsidian-700}", textColor: "{colors.on-dark}", rounded: "{rounded.control}", border: "1px {colors.hairline-dark}" }
  button-quiet:        { backgroundColor: transparent, textColor: "{colors.on-dark-muted}", rounded: "{rounded.control}" }
  button-danger:       { backgroundColor: transparent, textColor: "{colors.signal-danger}", border: "1px {colors.hairline-dark-strong}" }
  button-tool:         { backgroundColor: transparent, textColor: "{colors.on-dark-muted}", rounded: "{rounded.control}", size: 30px }
  button-tool-selected:{ backgroundColor: "{colors.accent}", textColor: "{colors.accent-ink}" }
  segmented-item:      { backgroundColor: transparent, rounded: 4px, height: 24px }
  segmented-selected:  { backgroundColor: "{colors.accent}", textColor: "{colors.accent-ink}" }
  titlebar:            { backgroundColor: "{colors.obsidian-900}", height: 32px, border: "bottom 1px {colors.hairline-dark}" }
  rail:                { backgroundColor: "{colors.obsidian-900}", width: 200px, itemHeight: 30px, selected: "{colors.accent-tint}" }
  row-setting:         { backgroundColor: "{colors.obsidian-800}", padding: "10px 18px", border: "bottom 1px {colors.hairline-dark}" }
  switch:              { trackSize: "34x18", thumbSize: 12px, offTrack: "{colors.obsidian-600}", onTrack: "{colors.accent}" }
  slider:              { trackHeight: 3px, thumbSize: 14px, thumbColor: "{colors.accent}" }
  input:               { backgroundColor: "{colors.obsidian-900}", border: "1px {colors.hairline-dark}", height: 30px, rounded: "{rounded.control}" }
  kbd:                 { backgroundColor: "{colors.obsidian-700}", border: "1px {colors.hairline-dark-strong}", rounded: 4px, height: 20px }
  chip:                { backgroundColor: "{colors.obsidian-700}", rounded: "{rounded.full}", height: 20px }
  banner-warn:         { backgroundColor: "rgba(245,165,36,0.14)", border: "1px rgba(245,165,36,0.40)" }
  menu:                { backgroundColor: "{colors.obsidian-800}", rounded: "{rounded.panel}", shadow: "{elevation.l1-chrome}", itemHeight: 28px }
  menu-item-hover:     { backgroundColor: "{colors.accent}", textColor: "{colors.accent-ink}" }
  overlay-scrim:       { backgroundColor: "rgba(0,0,0,0.45)" }
  overlay-selection:   { border: "1px {colors.measure-select}", outerRing: "1px rgba(0,0,0,0.55)", handleSize: 7px }
  overlay-toolbar:     { backgroundColor: "rgba(16,17,20,0.94)", rounded: "{rounded.panel}", height: 44px, backdropBlur: 14px }
  magnifier:           { rounded: "{rounded.card}", pixelGrid: "1px rgba(20,22,26,0.09)", centerRing: "1px {colors.measure-center} + 1px rgba(0,0,0,0.6)" }
  element-highlight:   { border: "2px {colors.measure-element}", fill: "rgba(87,199,255,0.14)" }
---

# Focal — WhSnap 界面设计语言 v1.0

## 1. 为什么是这一套

WhSnap 是一个**像素级工具**：遮罩浮在任意桌面内容之上，放大镜要能读出单个物理像素，贴图要能钉在屏幕任何角落。工具类界面和内容类界面的根本差别在于 —— **它永远不拥有背景**。

这条约束推导出了整套语言：

| 约束 | 推导出的规则 |
|---|---|
| 遮罩盖在别人的画面上 | 操作面必须是暗的（Obsidian），亮度由"暗幕浓度"控制，而不是主题 |
| 设置/文档要久看 | 阅读面是纸色的（Paper/Linen），同一套令牌换表面 |
| 用户正在看别的内容 | 全局只有一个交互色，且它必须能压在任意内容上被看见 → Signal Amber |
| 读数在实时跳动 | 所有数字走等宽 + tabular-nums |
| 界面不能抢内容的注意力 | 阴影只给"浮在不可控内容之上"的机壳；按钮/卡片/列表永远平铺 |
| 工具窗口尺寸小、信息密 | 14px 基准（桌面密度），行高 32，控件高 30 |

**与"画廊式"设计语言的区别**：画廊要让产品说话，所以它把 UI 变成白色、把留白拉到极致；工具要让**操作**说话，所以 Focal 把 UI 变成暗色、把密度压到 14px，只在"读"的场景才切到纸色。同一个审美纪律（少色、少线、少圆角），两个相反的方向。

## 2. 颜色

### 2.1 强调色是一族四档，不是一个值

| 档位 | 值 | 用途 |
|---|---|---|
| `accent` | `#F5A524` | 唯一交互色。**填充**（主按钮、选中态）、**描边**（选区、焦点环）、量测标记 |
| `accent-hover` | `#FFBB4A` | 暗底 hover |
| `accent-press` | `#D98E15` | 按下 |
| `accent-deep` | `#8A5A0A` | **浅底上唯一允许承载小字正文的琥珀**（对 Linen 5.4:1） |
| `accent-ink` | `#1A1206` | 琥珀填充上的文字/图标（10.4:1） |

**铁律：`#F5A524` 永不承担小字正文。** 在暗底上它是 9.3:1，够用；在纸色面上它只有 2.0:1。所以浅底需要"琥珀色的文字"时有两条合法路径：

1. 变成填充：琥珀底 + 墨字（`button-primary`、`chip--accent` 在浅底上的形态）；
2. 换深档：`accent-deep #8A5A0A`。

这等于把"暗底用亮档"的同一逻辑也给了浅底 —— 强调色是一族，和中性色一样有明度阶梯。

### 2.2 表面

- **Obsidian 900/800/700/600**（`#101114 / #16181C / #1E2126 / #2A2E35`）：窗口底 → 面板与工具条 → 抬升块与输入框 → hover 与轨道。四阶足够；第五阶一定是设计出了问题。
- **Void**（`#08090A`）：真空，只给播放器与照片压暗。
- **Paper / Linen / Linen Deep**（`#FFFFFF / #F4F4F2 / #E9E9E5`）：纸色阅读面。Linen 比纯白暖 2 个色阶 —— 这是把"仪器"和"办公软件"区分开的最小代价。
- **发丝线**：暗底用白色 8%（浮层 14%），浅底用墨色 10%。**永远不要用灰色实色画暗底上的线**，它在不同内容上会发脏。

### 2.3 语义色只活在状态面

`ok #2FB27C`、`warn #F5A524`（与强调同源，不新增色）、`danger #E5484D`。它们只出现在横幅、状态条、冲突提示中，**不进导航、标题、装饰、图标填充**。

### 2.4 量测标记

遮罩与标注是一套"测量仪器"，需要三个固定语义色，与主题无关：选区琥珀、元素高亮 `#57C7FF`、放大镜中心像素 `#FFD34D`。它们是**内容层的标记**，不是 UI 的强调色。

## 3. 排版

- **显示/正文**：`Segoe UI Variable Display / Text`，Win10 落到 `Segoe UI`，再落到系统字体。零下载、零 FOIT。
- **数据**：`Cascadia Code`（Win11 自带）→ `Cascadia Mono` → `Consolas`。坐标、尺寸、色值、百分比、快捷键、文件路径全部走等宽。
- **界面基准 14px，不是 16px。** 这是桌面工具的密度。16px 只出现在文档页的正文。
- **字距方向相反的两条规则**：
  - 显示字号（≥15px）用**负**字距（-0.2 → -0.4px），标题更紧；
  - 小号大写标签用**正**字距（+0.4px），呼吸更开。
  两条规则方向相反，目的相同：让"读"和"看"分开。
- **字重阶梯**：400 / 500（等宽数据）/ 600。**不出现 700，也不出现 500 的正文** —— 中量强调一律 600。
- 中文与数字混排时，等宽字体栈里保留 `Microsoft YaHei UI` 兜底，避免中文落到衬线。

## 4. 布局与度量

- 4px 基数。结构性布局只走 4 的倍数。
- **控件高 30 / 主操作 34 / 列表行 32（双行 40）/ 工具条 40 / 浮动条 44 / 标题栏 32。**
- 窗口内边距：面板 16，内容区 20，行内边距 `10px 18px`。
- 设置行结构固定为 **「标题 + 一句人话 + 控件」**：标题 14/400（不加粗，避免整屏粗体），说明 12/400 且必须是一句**具体的**话（"自动按 WGC → DXGI → GDI 降级"），不是"是否启用此功能"。
- 一屏原则：一个分组应当在一屏内看完，不允许为了两行说明制造滚动。

## 5. 圆角

只有四级 + 全圆：`hair 2` / `control 6` / `card 10` / `panel 14` / `full`。

- 控件（按钮、输入框、工具键、键帽所在的容器）：6。
- 卡片、列表块、浮层内的分组：10。
- 面板、浮动工具条、菜单：14。
- **全圆只表达"状态与连续量"**：状态点、状态胶囊（chip）、开关轨道与握把、滑杆握把、滚动条拇指。
- **一切"动作"与"容器"都是方的**：按钮、工具键、CTA、卡片、面板、菜单、浮动条。
- 这条规则的实际收益：**圆的东西是"读的"，方的东西是"点的"** —— 用户在看清文字之前，就能靠轮廓判断哪个能点。
- **CTA 绝不用胶囊。** 胶囊是营销按钮的语汇；工具的按钮是"按键"。

## 6. 层级

| 级别 | 处理 | 用途 |
|---|---|---|
| L0 平铺 | 无阴影 + 1px 发丝线 | 按钮、卡片、列表行、设置行、文档 |
| L1 机壳 | `inset 顶面高光 + 0 1px 2px rgba(0,0,0,.40) + 0 10px 30px rgba(0,0,0,.55)` | 浮在**不可控内容**之上：遮罩浮动工具条、放大镜、尺寸标签、托盘菜单、菜单 |
| L2 对话框 | `0 2px 6px rgba(16,18,22,.08) + 0 18px 48px rgba(16,18,22,.18)` | 纸色面上的模态对话框 |

判定标准只有一句：**这个面是不是浮在"我不知道下面是什么"的内容上？** 是 → 允许 L1；不是 → 平铺。所以设置窗口里的卡片没有阴影，而遮罩上的工具条必须有。

## 7. 动效

- 时长：90（按下）/ 140（状态切换）/ 220（浮层出现）。
- 曲线：`cubic-bezier(0.2, 0.8, 0.3, 1)`，无回弹。
- 按下态 = `translateY(1px)` + 变暗（不是缩放 —— 缩放会让"按键"变成"气泡"）。
- 录制态（等待按键）用 1.2s 透明度脉冲，这是唯一被允许的循环动画。

## 8. 组件（要点）

- **按钮两级语法**：`button-primary` 琥珀填充（唯一"决定性动作"，一个界面里通常只有一个）；其余全部是暗块或安静按钮。
- **工具键**：30×30，选中 = 琥珀填充 + 墨色图标；带标签时用 `inline-flex` + `nowrap`（不要用 grid 的 auto 列，会在收缩时折行）。
- **键帽**：20 高、底部 2px 边框模拟"可按下的实体"。主按钮里的键帽翻成"墨字 + 浅底"，不叠第二块暗色。
- **开关**：34×18 胶囊槽 + 12 圆点；关闭态是**凹槽**（`obsidian-600`），不是灰色按钮。
- **遮罩**：暗幕四块拼接把选区"挖"出来（选区保持原始亮度）；选区 1px 琥珀 + 外侧 1px 暗线（保证在浅色内容上也读得出边界）；8 向手柄 7px。
- **尺寸标签**：贴在选区外侧；上方会被内容遮挡时翻到下方。内容永远是 `宽 × 高 · x, y · 缩放`。
- **放大镜**：11×11 物理像素 + 1px 像素网格 + 中心像素环（琥珀描边 + 1px 暗描边，保证压在任意内容上都看得见）+ 四行读数（坐标/选区/色值/缩放）。
- **菜单**：快捷键右对齐成第二列；"最近颜色"用色块网格，不用一列文字 —— 颜色用眼睛找。
- **列表行**：悬停才出现单张操作（降低常态噪声）；选中态用 `accent-tint`，不用边框加粗。

## 9. Do / Don't

**Do**
- 用 `accent` 承载交互；浅底强调文字改用 `accent-deep` 或"琥珀底 + 墨字"。
- 先换表面（暗 ↔ 纸）再考虑加边框。
- 数字等宽、标签大写正字距、标题负字距。
- 说明文字写"人话"：说清**什么时候该改这个开关**。
- 遮罩/标注里的量测标记用固定语义色，别跟主题走。

**Don't**
- 不要让 `#F5A524` 承担浅底小字（2.0:1）。
- 不要给按钮、卡片、列表行、文字加阴影。
- 不要用装饰性渐变（唯一例外：标注的橡皮/模糊是内容处理，不是 UI）。
- 不要引入第二个强调色；语义色不许离开状态面。
- 不要混用圆角语法（6/10/14 之外只能出现 2 和 full）。
- 不要用胶囊做 CTA，不要用 700 字重，不要用 16px 界面正文。
- 不要在界面代码里写字面量色值 —— 只允许 `{token.refs}`，否则这套语言的成本会立刻失控。

## 10. 无障碍与对比度（自查表）

| 组合 | 对比度 | 结论 |
|---|---|---|
| `on-dark #F2F3F5` on `obsidian-900` | 17.0:1 | AAA |
| `on-dark-muted #A9AEB6` on `obsidian-800` | 8.0:1 | AAA |
| `accent #F5A524` on `obsidian-900` | 9.3:1 | AAA（图标/描边/大字） |
| `accent-ink #1A1206` on `accent` | 9.1:1 | AAA |
| `ink #14161A` on `linen #F4F4F2` | 16.4:1 | AAA |
| `ink-muted #5B6169` on `linen` | 5.8:1 | AA |
| `accent-deep #8A5A0A` on `linen` | 5.4:1 | AA（浅底强调文字的唯一合法档） |
| `accent #F5A524` on `paper` | **2.0:1** | ✗ 禁止用于文字 |
| `on-dark-faint #767C85` on `obsidian-900` | 4.5:1 | 只用于非必要信息（占位、禁用） |

键盘可达性：所有可聚焦元素有 2px 琥珀焦点环（`outline-offset: 1px`）；遮罩的全部动作都有键位，且键位可在设置里改。

## 11. 交接

| 文件 | 作用 |
|---|---|
| `tokens.css` | 唯一色值/尺寸来源（CSS 变量） |
| `focal.css` | 组件层：`f-` 通用、`ov-` 遮罩、`an-` 标注、`ob-` 向导、`spec-` 文档页 |
| `icons.js` | 自绘图标集：24 网格、1.6 描边、只用 `currentColor` |
| `theme/Theme.xaml` | WPF 资源字典：键名与 CSS 变量一一对应（连字符→驼峰） |
| `surfaces/*.html` | 六个界面的高保真实现（可直接打开的交互版） |
| `shot.ps1` | 无头 Chrome 渲染 → `shots/*.png`，用于视觉回归 |

**移植规则**：WPF 侧只改 `Theme.xaml`，不动界面代码；界面代码里不允许出现字面量色值。字体在 Win11 上是 `Segoe UI Variable` + `Cascadia Code`，Win10 自动落 `Segoe UI` + `Consolas`，字重阶梯不塌。

## 12. 已知缺口

- 深浅两套主题目前只覆盖了"纸色阅读面"和"暗色操作面"两种**场景**，没有做同一场景的双主题切换（桌面工具不需要跟随系统明暗：遮罩本身就是暗的）。
- 表单校验/错误态已在设置页给出（`banner--danger`），但没有做字段级红框与错误文案规范。
- 托盘菜单是 WinForms 原生菜单，`focal.css` 给的是设计规格；真正落地需要在 `TrayHost.cs` 里自绘 `ToolStripRenderer`，这部分尚未实现。
- 图标集覆盖了当前界面用到的 64 个图标，未做 16px 光学尺寸的单独微调（当前是同一份 24 网格缩放）。
