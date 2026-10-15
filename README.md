# WhSnap

对标 Snipaste 的 Windows 截图 / 贴图工具。当前处于 **预览版（0.5.0-preview）**，按里程碑逐步推进。

- 项目规划方案：[docs/whsnap-项目规划方案.md](docs/whsnap-项目规划方案.md)
- 目标系统：Windows 11（主）／Windows 10 21H2+（尽力兼容）
- 分发：仅安装包（Inno Setup，per-user），通过 GitHub Releases 发布

---

## 当前状态

| 能力 | 状态 |
|---|---|
| 全局热键唤起截图（默认 `F1`） | ✅ 可用 |
| 采集后端：WGC / DXGI / GDI，自动降级 | ✅ 三后端实测通过 |
| 冻结屏幕 + 全屏遮罩拖拽选区 | ✅ 可用 |
| **多屏遮罩：每显示器一个窗口、跨屏拖拽** | ✅ 可用（合成混合 DPI 双屏已验证） |
| **放大镜：像素网格 + 坐标 + 色值** | ✅ 可用 |
| **像素级微调：方向键 1px / Shift 10px / Ctrl 改边 / 8 向手柄** | ✅ 可用 |
| 复制到剪贴板（DIB + PNG 双格式） | ✅ 可用 |
| 保存 PNG（文件名模板可配） | ✅ 可用 |
| 托盘图标与菜单、开机自启 | ✅ 可用 |
| 配置读写（`%APPDATA%\WhSnap\settings.json`，schema 迁移） | ✅ 可用 |
| **三层冲突检测（L1 试注册 / L2 内部重复 / L3 冲突源库）** | ✅ 可用（M3） |
| **全部动作热键可自定义（全局 6 / 遮罩 5 / 贴图窗口 12）** | ✅ 可用（M3） |
| **设置界面由 schema 自动生成（28 项配置，6 组 + 快捷键 + 维护）** | ✅ 可用（M3） |
| **设置导入导出、单项/全部恢复默认、复制配置到剪贴板** | ✅ 可用（M3） |
| **热键冲突策略（阻断询问 / 保存但停用）与保留键提示** | ✅ 可用（M3） |
| **标注：矩形 / 椭圆 / 直线 / 箭头 / 铅笔 / 马克笔 / 文字 / 高亮** | ✅ 可用（M4） |
| **标注：马赛克 / 高斯模糊 / 橡皮擦（只擦标注）** | ✅ 可用（M4） |
| **标注：序号、撤销重做、图元二次编辑（选中/移动/缩放/改样式）** | ✅ 可用（M4） |
| **标注：导出严格物理像素 1:1** | ✅ 可用（M4，像素断言自检） |
| **贴图内标注（贴图上按空格，标注回写贴图）** | ✅ 可用（M4） |
| **元素检测：悬停高亮光标下的控件/窗口，单击选中，`Tab` 由内向外切换** | ✅ 可用（M5，UIA + 窗口矩形回退） |
| **取色器：`C` 取色复制（HEX/RGB/HSL/CMYK 可选）+ 颜色历史** | ✅ 可用（M5） |
| **托盘细化：取色器入口、最近颜色子菜单、菜单内实时状态** | ✅ 可用（M5） |
| **贴图：置顶分层窗口、逐像素 alpha** | ✅ 可用（M2） |
| **贴图：滚轮缩放 / `Ctrl`+滚轮透明度 / 旋转 / 翻转 / 缩略图** | ✅ 可用（M2） |
| **贴图：拖动、边缘吸附、方向键微调、鼠标穿透、双击隐藏** | ✅ 可用（M2） |
| **贴图：从剪贴板贴图（图像 / 文件 / HTML / 颜色值 / 文本）** | ✅ 可用（M2） |
| **贴图：拖放文件即贴、右键菜单全操作** | ✅ 可用（M2） |
| **贴图：自动备份 + 崩溃/重启恢复、显示器拓扑变化重算** | ✅ 可用（M2，`--pin-test` 实测） |
| **贴图管理面板（层级/显示/关闭/批量）** | ✅ 可用（M2） |
| 标注（矩形/箭头/文字/马赛克…） | ✅ 可用（M4） |
| 贴图内标注（空格键） | ✅ 可用（M4） |

### 遮罩操作

| 操作 | 说明 |
|---|---|
| 拖动 | 框选区域；从已选区域内部拖动 = 整体平移 |
| 拖动 8 个手柄 | 缩放选区（拖过对边会自动翻转，最小 1px） |
| `↑` `↓` `←` `→` | 平移选区 1 像素 |
| `Shift` + 方向键 | 平移 10 像素 |
| `Ctrl` + 方向键 | 调整右下边界：`←`/`↑` 收窄，`→`/`↓` 加宽 |
| `Enter` / 双击 | 确认（按配置执行复制/保存/贴图） |
| `Esc` / 右键 | 取消 |
| `Ctrl+C` / `Ctrl+S` / `F3` | 只复制 / 只保存 / 只贴图 |

上表中的动作键位都可以在设置里改（作用域「遮罩」）。方向键微调是固定交互，始终可用。

跨屏框选：鼠标在哪台显示器按下，就由那个遮罩窗口接管拖拽，选区可以横跨多台显示器（含混合缩放比）。

### 贴图操作（M2）

贴图是 `WS_EX_LAYERED | WS_EX_TOPMOST` 的原生分层窗口 + 逐像素 alpha 合成，
所以**完全透明的像素天然鼠标穿透**，贴图周围不会出现"隐形挡板"（阴影区也显式返回 `HTTRANSPARENT`）。

| 操作 | 说明 |
|---|---|
| 拖动 | 移动贴图；松手时按设置吸附到显示器边缘 |
| 滚轮 | 以光标为锚点缩放（0.05×–32×，放大 ≥3× 用最近邻不糊像素） |
| `Ctrl`+滚轮 | 调整不透明度（`Shift` 加大步进） |
| `1` / `2` | 顺时针 / 逆时针旋转 90°（绕中心旋转，不跳位） |
| `3` / `4` | 水平 / 垂直翻转 |
| 双击 | 隐藏贴图（托盘菜单「显示全部贴图」或管理面板恢复） |
| `Shift`+双击 | 切换缩略图模式（按 1/4 显示，原始像素不变） |
| 右键 | 上下文菜单：复制 / 另存为 / 缩放 / 旋转 / 翻转 / 透明度 / 穿透 / 隐藏 / 关闭 / 批量操作 |
| 空格 | 贴图内标注入口（M4 提供标注引擎，当前给出明确提示） |
| `Ctrl+C` / `Ctrl+S` | 复制到剪贴板 / 另存为（PNG、JPG、BMP） |
| `P` / `H` / `Delete` / `Esc` | 切换鼠标穿透 / 隐藏 / 关闭 / 隐藏 |
| 方向键 | 移动 1px；`Shift`+方向键 10px |
| `0` / `+` / `-` | 恢复 100% / 放大 / 缩小 |

贴图窗口内的按键同样可以在设置里改（作用域「贴图窗口」）；方向键微调、`+`/`-` 缩放、`Esc` 隐藏是固定交互。

从剪贴板贴图（托盘菜单「从剪贴板贴图」，或开启"复制即贴图"后自动触发）：

| 剪贴板内容 | 贴出来的东西 |
|---|---|
| 图像（含 `PNG` / `CF_DIB`） | 位图本身，1:1 物理像素 |
| 图片文件（PNG/JPG/BMP/GIF/ICO/WebP） | 图片（GIF 取首帧） |
| 其它文件 | 文件卡片：系统关联图标 + 文件名 + 大小 + 路径 |
| HTML 片段 | 提取纯文本后渲染（不做完整 HTML 排版，见"已知限制"） |
| 颜色值（`#RGB` / `#RRGGBB` / `rgb()` / `hsl()` / `R,G,B`） | 色块 + HEX/RGB/HSL/CMYK 四种表示法 |
| 纯文本 | 自动换行的文本贴图（字号/颜色/最大宽高可配） |

拖放文件到任意贴图上也会贴出这些文件（图片按图贴，其它按文件卡片）。

自动备份与恢复：贴图变动后延迟若干秒写入 `%APPDATA%\WhSnap\pins\<时间戳>\`（`manifest.json` + `pin-000.png`…），
只保留最近 N 次会话；启动时自动恢复上次的贴图（几何、层级、透明度、穿透状态全部保留）。
托盘菜单「打开贴图备份目录」可直达。

### 从剪贴板贴图 / 贴图管理

- 托盘菜单：「从剪贴板贴图」「贴图管理…（N 张）」「显示/隐藏/关闭全部贴图」。
- 管理面板列出全部贴图的尺寸、位置、缩放、旋转、透明度与穿透状态，支持逐张置顶/隐藏/关闭与批量操作。

### 设置与快捷键（M3）

打开方式：托盘菜单「设置…」，或默认热键 `Ctrl+Shift+,`。

设置界面**完全由 `SettingsSchema` 自动生成**（28 项配置、6 个分组 + 「快捷键」+「维护」），
所以"加一个配置项"只需要改 schema 一处，不会出现"功能加了但界面漏了"——自检 `--settings-test`
会逐项验证界面对 schema 的覆盖率，`--selftest` 会验证 schema 与 `AppSettings` 双向一致。

| 分组 | 内容 |
|---|---|
| 截图 | 采集后端、确认后复制/保存/贴图、遮罩暗度 |
| 保存 | 默认目录、文件名模板、JPEG 质量 |
| 贴图 | 默认不透明度、默认穿透、缩放步进、边缘吸附与容差、置顶、备份开关/间隔/保留、启动恢复 |
| 剪贴板 | 复制即贴图 |
| 文本贴图 | 字体、字号、最大宽高、内边距、前景/背景色 |
| 系统 | 开机自启、热键冲突处理策略 |
| 快捷键 | 23 个动作的键位录制（按作用域分组） |
| 维护 | 运行状态诊断、打开各目录、复制配置、清理贴图备份 |

**三层冲突检测（§5.7 交付重点）**

| 层 | 判定方式 | 界面表现 |
|---|---|---|
| L1 系统占用 | **临时释放自己的热键后试注册**（唯一可靠办法；Windows 无公开反查 API） | 徽标「已被占用」+ 顶部横幅统计 |
| L2 内部重复 | 修饰键规范化后比对，**只在同一作用域内算冲突** | 徽标「冲突」，默认**阻断并弹窗**让你选择 |
| L3 常见冲突源 | 内置 19 条冲突源库（系统截图、Xbox Game Bar、输入法、微信/QQ、NVIDIA、PowerToys、Snipaste…） | 行内提示「最可能被 X 占用」+ 排查建议 |

另有保留键提示（`Ctrl+Alt+Del`、`Win+L`、`Alt+Tab`、`Alt+F4` 等 12 条）。

冲突策略可配（设置 → 系统 → 热键冲突处理）：

- `block`（默认）：弹窗让你选「仍保存但停用该热键 / 撤销修改 / 仍保存」——**绝不静默失败**；
- `save-disabled`：直接保存但停用冲突项，不弹窗。

录制控件：点「录制」后按组合键即可，`Esc` 取消；纯修饰键、以及无法往返解析的键会被明确拒绝并给出原因。
每个动作都有「清除」「默认」两个按钮；「检测热键占用」按钮会重新跑一遍 L1 全量探针。

导入导出：设置窗口底部「导出配置…／导入配置…」。导入会**逐项校验**，
未知项与非法值都会被列出来（不会静默丢弃），可应用的项立即生效。

### 标注（M4）

两个入口：截图遮罩里按 `T`（选区先进标注编辑器，确认后继续走复制/保存/贴图）；贴图上按空格（标注后**回写贴图**）。

标注窗口严格覆盖选区、底图按物理像素 1:1 呈现，工具栏放在区域下方（贴近屏幕底边时自动翻到上方）。

| 工具 | 快捷键 | 说明 |
|---|---|---|
| 选择 / 移动 | `V` | 点中图元整体拖动，拖角缩放；双击文字改内容 |
| 矩形 / 椭圆 | `R` / `O` | 可填充（填充不透明度可配） |
| 直线 / 箭头 | `L` / `A` | |
| 铅笔 / 马克笔 | `D` / `M` | 细不透明 / 粗半透明自由笔 |
| 文字 | `T` | 点一下就地输入，`Enter` 确认 |
| 马赛克 / 模糊 | `G` / `B` | 框选区域打码 / 盒式模糊（近似高斯），块大小与半径可配 |
| 橡皮擦 | `X` | **只擦掉自己画的标注，底图不会被擦出洞** |
| 序号 / 高亮 | `N` / `Y` | 递增编号 / 荧光笔式半透明粗线 |

其他操作：`Ctrl+Z` / `Ctrl+Y` 撤销重做、`Delete` 删除选中图元、`[` `]` 调线宽、`1`-`8` 换调色板颜色、
`Enter` 确认、`Esc` 取消。工具栏还有「清空」「复制」（确认并只复制）。工具键位同样可以在设置里改（作用域「标注」）。

三个实现要点（都在 `--annotate-test` 里用像素断言验证）：

1. **撤销是命令栈，不是位图快照**：图元不可变，一次快照只是一个指针数组，既不复制像素，又能撤销后继续二次编辑；
2. **矢量与像素效果按序交织**：马赛克/模糊作用于"当前已画出的画面"，所以渲染管线会在它们前面先把矢量图层合成下去；
3. **橡皮擦只作用于标注层**：擦完用底图重新合成，绝不在底图上打洞（截图被擦出透明洞是不可接受的）。

### 元素检测与取色器（M5）

**元素检测**（默认开启，设置 → 截图）：遮罩显示时自动查询光标下的界面元素并高亮它。

| 操作 | 说明 |
|---|---|
| 悬停 | 高亮光标下最小的那个元素（通常是按钮/输入框），标签显示尺寸 |
| 单击 | 直接选中高亮元素（拖拽超过 4px 仍按手动框选处理，互不干扰） |
| `Tab` | 在候选之间**由内向外**切换（控件 → 面板 → 窗口）并选中 |

两条路径：**UI Automation** 优先（能识别真实控件、拿到名称与层级），失败/超时/关闭该选项时退回
**顶层窗口矩形**（用 `DWMWA_EXTENDED_FRAME_BOUNDS`，比 `GetWindowRect` 更贴合可见边框）。
UIA 查询走**线程池 + 超时 + 过期结果丢弃**，绝不阻塞界面——目标程序卡死时最坏只是退化成窗口级候选。

**取色器**：遮罩里按 `C`，或托盘菜单「取色器（取色并复制）」（单击即取色并退出）。

- 复制格式可配：`hex` / `rgb` / `hsl` / `cmyk` / 四种一起（设置 → 取色器）；
- 取到的颜色进入**颜色历史**（默认保留 24 条），遮罩左下角显示最近 12 个色块，
  托盘「最近颜色」子菜单里可以随时再复制；
- 颜色历史存在配置里（`ColorHistoryText`），重启后仍在。

### 采集后端（`CaptureBackend` 配置项：`auto` / `wgc` / `dxgi` / `gdi`）

默认 `auto`，按 **WGC → DXGI → GDI** 依次尝试，任一后端抛错即自动降级，并把降级过程写进日志。

| 后端 | 实测耗时（2560×1440） | 说明 |
|---|---|---|
| `wgc`（Windows.Graphics.Capture） | 66 ms | Win11 首选，能覆盖部分硬件叠加层内容 |
| `dxgi`（Desktop Duplication） | 42 ms | 同步单帧，最稳；抓不到 DRM 保护内容 |
| `gdi`（BitBlt） | 27 ms | 兜底；旋转屏与宽色域下由系统合成为 SDR，最不容易出错 |

三个后端都会做**画面合理性校验**：抓到"完全均匀"的图（典型是纯黑帧）会被判为该环境不支持并自动降级，而不是把黑图交给用户。

### 预览版已知限制

- **多屏行为目前只在合成拓扑上验证**（左屏 1280×1024@100% + 右屏 1920×1024@150% 的自动化测试）。
  开发机只有一台显示器，因此"真实双屏下的鼠标捕获移交"这一步还需要你在双屏机器上实际试一次。
- HDR / 宽色域输出：WGC 与 DXGI 只处理 BGRA8，遇到 10-bit 或浮点格式会自动降级到 GDI。
- **HTML 贴图只提取纯文本**：不引入 IE/WebView 渲染引擎（一个 HTML 排版引擎的重量与风险都远超收益），
  因此从网页复制的内容会以"纯文本 + 换行"贴出，不保留表格/样式。
- **跨屏大区域（横跨混合 DPI 两台显示器）的标注画布可能有缩放**：编辑器只在单台显示器的 DPI 上下文里显示，
  另一台上看起来可能略有模糊；**合成与导出不受影响**（纯像素运算，1:1 精确）。建议跨屏时先裁成单屏再标注。
- 贴图缩放上限 32×，并且单张贴图缓冲超过 160 MB 时会拒绝继续放大（避免极端尺寸把内存打满）。
- **低级键盘钩子降级路径（§5.7）尚未提供**：若某个键确实被系统占用，目前只能改键，不会退回 `WH_KEYBOARD_LL`
  （该方案有风险且需明确提示，排在 v1.1，见 `docs/backlog.md`）。
- 唤醒后热键自动重注册已按 `WM_POWERBROADCAST` 监听实现，仍待实机验证。

---

## 使用

1. 安装后首次运行会在托盘出现图标（并写入默认配置）。
2. 按 `F1`（或双击托盘图标）开始截图。
3. 拖动选择区域 → `Enter` 按配置执行；`Ctrl+C`/`Ctrl+S`/`F3` 直达复制/保存/贴图；`Esc` 或右键取消。
4. 结果：复制到剪贴板 + 保存到 `图片\WhSnap\` +（可选）贴到屏幕，三者在配置中各自开关。
5. 贴图不想要了：双击隐藏，或右键「关闭这张贴图」；退出程序前贴图会自动备份，下次启动自动恢复。

### 配置文件

位置：`%APPDATA%\WhSnap\settings.json`（托盘菜单「打开配置目录」可直达）

```json
{
  "SchemaVersion": 3,
  "Hotkeys": {},
  "DisabledHotkeys": [],
  "HotkeyConflictPolicy": "block",
  "SaveDirectory": "",
  "FileNameTemplate": "WhSnap_{date}_{time}_{w}x{h}",
  "CopyOnCapture": true,
  "SaveOnCapture": true,
  "AutoStart": true,
  "OverlayDimOpacity": 45,
  "CaptureBackend": "auto",
  "JpegQuality": 90,

  "PinOnCapture": false,
  "PinDefaultOpacity": 100,
  "PinClickThroughDefault": false,
  "PinZoomStep": 1.1,
  "PinSnapToEdges": true,
  "PinSnapTolerance": 12,
  "PinTopmostNew": true,
  "PinBackupEnabled": true,
  "PinBackupIntervalSeconds": 5,
  "PinBackupRetention": 10,
  "RestorePinsOnStartup": true,
  "ClipboardWatchEnabled": false,

  "TextPinFontFamily": "Microsoft YaHei UI",
  "TextPinFontSize": 14,
  "TextPinMaxWidth": 480,
  "TextPinMaxHeight": 1200,
  "TextPinPadding": 12,
  "TextPinForeground": "#F2F2F2",
  "TextPinBackground": "#1B1B1B"
}
```

`FileNameTemplate` 支持 `{date}` `{time}` `{w}` `{h}` 占位符。
`Hotkeys` 只存放"与默认值不同"的动作键位（动作 id → 键位文本，如 `"capture.region": "Ctrl+Alt+A"`），
未列出的动作使用内置默认值；完整动作表见设置窗口「快捷键」页或 `HotkeyCatalog`。
`SchemaVersion` 用于配置迁移：v2→v3 会把旧的单个 `Hotkey` 字段迁到 `Hotkeys["capture.region"]`，
新增项一律用默认值补齐，不会丢已有设置。
设置界面里改任何一项都**即时生效**（不需要重启）。
开发目录（`bin\`）下运行不会写入注册表自启项，只有已安装实例才按默认值自动注册。
`ClipboardWatchEnabled` 打开后即"复制即贴图"（程序自己写入剪贴板的截图不会回贴）。

### 日志

`%APPDATA%\WhSnap\logs\whsnap-yyyyMMdd.log`

---

## 构建

前置：.NET 8 SDK（`winget install Microsoft.DotNet.SDK.8`）、Inno Setup 6（`winget install JRSoftware.InnoSetup`）。

```powershell
# 构建 + 单元测试
dotnet build WhSnap.sln -c Release
dotnet test tests/WhSnap.Core.Tests/WhSnap.Core.Tests.csproj -c Release

# 生成图标（仅在需要改图标时执行）
powershell -File build/tools/make-icon.ps1

# 打安装包（自动 publish + 编译 .iss）
powershell -File build/tools/build-installer.ps1 -Version 0.1.0
```

### 无界面自检

不依赖人工点击即可验证关键链路（抓屏 / 显示器 DPI / 热键注册与冲突判定 / 剪贴板 / 配置 / 自启状态）：

```powershell
& src\WhSnap.App\bin\Release\net8.0-windows\WhSnap.exe --selftest
Get-Content "$env:TEMP\whsnap-selftest.txt" -Encoding UTF8
```

退出码 `0` 表示全部通过；报告同时写入 `%TEMP%\whsnap-selftest.txt`。自检会逐个验证三个采集后端，并根据画面统计判断是不是黑帧。

### 单后端抓屏诊断

在不确定某个采集后端是否可用时，可以直接抓一张图并把诊断写到同名 `.txt`：

```powershell
& src\WhSnap.App\bin\Release\net8.0-windows10.0.22000.0\WhSnap.exe --capture-to D:\temp\shot.png --backend dxgi
Get-Content D:\temp\shot.png.txt
```

### 遮罩层无交互自检

真实创建遮罩窗口、模拟跨屏框选 / 方向键微调 / 手柄缩放 / 放大镜取像素 / 确认裁剪，然后自动关闭。
还会额外跑一遍**合成混合 DPI 双屏拓扑**，用于在没有双屏的机器上验证多屏路径：

```powershell
& src\WhSnap.App\bin\Release\net8.0-windows10.0.22000.0\WhSnap.exe --overlay-test
```

运行期间屏幕上会短暂出现遮罩（约 1 秒），退出码 `0` 表示通过。

### 贴图系统无交互自检

真实创建分层贴图窗口，把 M2 的整条链路跑一遍：窗口落位与阴影留白、缩放锚点、旋转/翻转中心保持、
不透明度与鼠标穿透的扩展样式、缩略图、拖动跟随、隐藏/显示、层级命中测试、文本/颜色/文件卡片渲染、
**30 张贴图常驻内存**、自动备份落盘 → 销毁 → 重启恢复、显示器拓扑重算：

```powershell
& src\WhSnap.App\bin\Release\net8.0-windows10.0.22000.0\WhSnap.exe --pin-test
Get-Content "$env:TEMP\whsnap-pin-test.txt" -Encoding UTF8
```

运行期间屏幕上会短暂出现贴图（约 1 秒），使用独立的临时备份目录，不会碰你真实的贴图备份。
退出码 `0` 表示通过。

### 设置界面无交互自检

设置界面完全由 schema 生成，所以"界面对配置项的覆盖率"也必须被机器验证
（否则加配置项时容易只加数据忘了界面）：

```powershell
& src\WhSnap.App\bin\Release\net8.0-windows10.0.22000.0\WhSnap.exe --settings-test
Get-Content "$env:TEMP\whsnap-settings-test.txt" -Encoding UTF8
```

验证内容：标签页与控件生成、每个 schema 项都有界面入口、每个动作都有热键行、
键位录制对合法组合成立且拒绝纯修饰键、**导出 → 导入无损**（未知项/非法值为 0）、
导入非法配置逐项拒绝并报告、恢复默认与 schema 一致性。运行期间会短暂闪现设置窗口。

### 标注系统无交互自检

真实创建标注编辑器，跑完整条链路：13 个工具、落笔、**马赛克按块取均值**、
模糊生效、**橡皮擦只擦标注不擦底图**、撤销/重做、二次编辑（选中→移动→改样式）、
删除与清空、确认回传 1:1 结果、标注回写贴图：

```powershell
& src\WhSnap.App\bin\Release\net8.0-windows10.0.22000.0\WhSnap.exe --annotate-test
Get-Content "$env:TEMP\whsnap-annotate-test.txt" -Encoding UTF8
```

运行期间屏幕上会短暂出现标注窗口（约 1 秒）。退出码 `0` 表示通过。

### 元素检测与取色器无交互自检

创建一个真实测试窗口，验证 **UI Automation 能识别它（含名称与边界）**、窗口矩形回退路径能枚举候选、
遮罩会话的"悬停 → 异步查询 → 高亮"链路真的产出高亮元素，以及**取色器全链路**
（冻结画面取像素 → 按设置格式写剪贴板 → 记入颜色历史 → 序列化恢复）：

```powershell
& src\WhSnap.App\bin\Release\net8.0-windows10.0.22000.0\WhSnap.exe --element-test
Get-Content "$env:TEMP\whsnap-element-test.txt" -Encoding UTF8
```

### 更新链路自检

在临时目录里用**本地传输**端到端验证更新链路：清单解析与语义化版本比较 → 检查到可更新 →
下载 + SHA-256 校验 → **篡改安装包必须被拒且不残留临时文件** → 缺哈希拒绝下载 → 通道规则
（稳定通道拒绝 beta）→ 旧版本不提示 → 最低可升级版本门槛 → 缺失/损坏清单不抛异常 → 静默安装参数。
不需要联网，也不会真的启动安装器：

```powershell
& src\WhSnap.App\bin\Release\net8.0-windows10.0.22000.0\WhSnap.exe --update-test
Get-Content "$env:TEMP\whsnap-update-test.txt" -Encoding UTF8
```

更新相关配置项（设置界面「更新」分组）：`CheckForUpdatesOnStartup`（默认开启，只读取一份很小的
`update.json`，不含任何本机信息）、`UpdateChannel`（stable / beta）、`UpdateAutoDownload`
（默认关闭）、`UpdateManifestUrl`（默认取 GitHub Releases 最新版的 `update.json`，也可填本地路径做离线分发）。

---

## 分发（自己 + 小圈子）

发布流程：打标签 → GitHub Actions 自动构建并创建 Release。

```powershell
git tag v0.5.0
git push origin v0.5.0
```

### 代码签名（自签名，零成本）

诚实说明：**自签名证书不会消除 SmartScreen 警告，也基本不会降低杀软误报**。它的实际收益是让文件属性页显示发布者名称，并在对方导入信任后不再提示「未知发布者」。对「自己 + 小圈子」的分发方式，这是够用且免费的选择。

```powershell
# 1. 一次性生成证书（.pfx 绝不要提交到仓库）
powershell -File build/tools/make-cert.ps1

# 2. 本地签名
powershell -File build/tools/sign.ps1 -Path artifacts\installer\WhSnap-0.1.0-setup-x64.exe -PfxFile artifacts\signing\WhSnap.pfx

# 3. 让 CI 自动签名：把 .pfx 转成 base64 存入仓库 Secrets
[Convert]::ToBase64String([IO.File]::ReadAllBytes("artifacts\signing\WhSnap.pfx"))
#    Secret 名：SIGNING_PFX_BASE64 / SIGNING_PFX_PASSWORD
```

接收方（每台机器一次，需管理员）：

```powershell
powershell -ExecutionPolicy Bypass -File install-trust.ps1 -CerPath .\WhSnap.cer
```

签名需要 Windows SDK 的 `signtool`；GitHub Actions 的 `windows-latest` 已自带。

---

## 仓库结构

```
WhSnap.sln
src/
  WhSnap.Core/          纯逻辑：物理像素几何、选区状态机、贴图几何、颜色换算、像素效果（马赛克/模糊/橡皮擦/alpha 合成）、
                        标注图元与命令栈、备份清单、DPI 换算、热键模型、设置 schema 与导入校验（可单测，无 UI 依赖）
  WhSnap.App/           WPF 宿主：托盘、遮罩、贴图、标注、设置、采集、服务、自检
    Capture/            抓屏后端（WGC/DXGI/GDI）、显示器枚举、截图编排、元素检测（UIA + 窗口矩形）
    Overlay/            全屏遮罩窗口
    Pin/                分层贴图窗口、贴图管家、备份存储、贴图内容渲染、管理面板
    Annotation/         标注编辑器、合成管线、矢量绘制
    Settings/           设置窗口（由 schema 自动生成 UI）+ 热键录制与冲突展示
    Interop/            全部 Win32 P/Invoke 集中于此
    Services/           热键注册与 L1 探针、剪贴板监听与读入、设置绑定/导入导出、保存、自启、配置、日志、拓扑监听
    Tray/               托盘图标与菜单
    Diagnostics/        无界面自检（selftest / overlay-test / pin-test / settings-test / annotate-test / capture-to）
tests/
  WhSnap.Core.Tests/    xUnit
build/
  installer/            Inno Setup 脚本、信任导入脚本
  tools/                图标生成、安装包构建、证书与签名
.github/workflows/      release
```

### 架构约定（不可违反）

1. **坐标一律物理像素**贯穿领域层；DIP 只在 WPF 布局时临时换算 —— 这是混合 DPI 下选区不错位的根本保证。
2. **View 不直接 P/Invoke**，平台调用全部集中在 `Interop/NativeMethods*.cs`。
3. **一次截图 = 不可变底图 + 图元列表**；标注是叠加数据，导出时才 1:1 合成。
4. **贴图窗口是原生分层窗口**（`UpdateLayeredWindow` 逐像素 alpha），不走 WPF `AllowsTransparency`：
   保真、省内存，并且天然获得"透明处鼠标穿透"。
5. **贴图状态一律不可变**（`PinState` record）：备份、拓扑重算、后续撤销都能安全持有快照。
6. **单一数据源**：可绑定动作只在 `HotkeyCatalog` 声明，可配置项只在 `SettingsSchema` 声明；
   界面、冲突检测、导入校验、注册服务都从这两张表派生，并用自检守住"表 ↔ 实现"的一致性。

---

## 里程碑

M0 预研与骨架 ✅ → M1 截图核心 ✅（多屏遮罩、WGC/DXGI/GDI）→ M2 贴图 ✅（`v0.2.0-preview`）→
M3 快捷键设置与全配置化 ✅（`v0.3.0-preview`）→ M4 全量标注 ✅（`v0.4.0-preview`）→
**M5 对标收尾 ✅**（`v0.5.0-preview`：元素检测、取色器与颜色历史、托盘细化）→ M6 发布工程化。

详见 [项目规划方案](docs/whsnap-项目规划方案.md)。
