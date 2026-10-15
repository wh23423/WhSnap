# WhSnap

对标 Snipaste 的 Windows 截图 / 贴图工具。当前处于 **预览版（0.2.0-preview）**，按里程碑逐步推进。

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
| 热键冲突检测（第一层：系统占用） | ✅ 可用，能识别 Win32 1409 |
| **贴图：置顶分层窗口、逐像素 alpha** | ✅ 可用（M2） |
| **贴图：滚轮缩放 / `Ctrl`+滚轮透明度 / 旋转 / 翻转 / 缩略图** | ✅ 可用（M2） |
| **贴图：拖动、边缘吸附、方向键微调、鼠标穿透、双击隐藏** | ✅ 可用（M2） |
| **贴图：从剪贴板贴图（图像 / 文件 / HTML / 颜色值 / 文本）** | ✅ 可用（M2） |
| **贴图：拖放文件即贴、右键菜单全操作** | ✅ 可用（M2） |
| **贴图：自动备份 + 崩溃/重启恢复、显示器拓扑变化重算** | ✅ 可用（M2，`--pin-test` 实测） |
| **贴图管理面板（层级/显示/关闭/批量）** | ✅ 可用（M2） |
| 标注（矩形/箭头/文字/马赛克…） | ⏳ M4 |
| 贴图内标注（空格键入口已就位） | ⏳ M4 |
| 快捷键设置界面（三层冲突检测） | ⏳ M3 |

### 遮罩操作

| 操作 | 说明 |
|---|---|
| 拖动 | 框选区域；从已选区域内部拖动 = 整体平移 |
| 拖动 8 个手柄 | 缩放选区（拖过对边会自动翻转，最小 1px） |
| `↑` `↓` `←` `→` | 平移选区 1 像素 |
| `Shift` + 方向键 | 平移 10 像素 |
| `Ctrl` + 方向键 | 调整右下边界：`←`/`↑` 收窄，`→`/`↓` 加宽 |
| `Enter` / 双击 | 确认（复制并保存） |
| `Esc` / 右键 | 取消 |

跨屏框选：鼠标在哪台显示器按下，就由那个遮罩窗口接管拖拽，选区可以横跨多台显示器（含混合缩放比）。

确认动作可直达：`Enter` 按配置执行，`Ctrl+C` 只复制、`Ctrl+S` 只保存、`F3` 只贴图。

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
- **贴图内标注尚未提供**（空格键入口已就绪，M4 落地标注引擎）。
- 贴图缩放上限 32×，并且单张贴图缓冲超过 160 MB 时会拒绝继续放大（避免极端尺寸把内存打满）。
- 尚无设置界面；热键与全部贴图参数目前只能在 `settings.json` 中改（M3 提供设置界面与三层冲突检测）。
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
  "SchemaVersion": 2,
  "Hotkey": "F1",
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

`FileNameTemplate` 支持 `{date}` `{time}` `{w}` `{h}` 占位符。修改后需重启程序生效（M3 起改为即时生效）。
`SchemaVersion` 用于配置迁移：旧配置读入后自动补齐新增项并抬版本号，不会丢已有设置。
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

---

## 分发（自己 + 小圈子）

发布流程：打标签 → GitHub Actions 自动构建并创建 Release。

```powershell
git tag v0.2.0
git push origin v0.2.0
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
  WhSnap.Core/          纯逻辑：物理像素几何、选区状态机、贴图几何、颜色换算、备份清单、DPI 换算、热键解析（可单测，无 UI 依赖）
  WhSnap.App/           WPF 宿主：托盘、遮罩、贴图、采集、服务、自检
    Capture/            抓屏后端（WGC/DXGI/GDI）、显示器枚举、截图编排
    Overlay/            全屏遮罩窗口
    Pin/                分层贴图窗口、贴图管家、备份存储、贴图内容渲染、管理面板
    Interop/            全部 Win32 P/Invoke 集中于此
    Services/           热键、剪贴板监听与读入、保存、自启、配置、日志、显示器拓扑监听
    Tray/               托盘图标与菜单
    Diagnostics/        无界面自检（selftest / overlay-test / pin-test / capture-to）
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

---

## 里程碑

M0 预研与骨架 ✅ → M1 截图核心 ✅（多屏遮罩、WGC/DXGI/GDI）→ **M2 贴图 ✅**（`v0.2.0-preview`）→ M3 快捷键设置与三层冲突检测 → M4 全量标注 → M5 对标收尾 → M6 发布工程化。

详见 [项目规划方案](docs/whsnap-项目规划方案.md)。
