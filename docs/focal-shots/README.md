# Focal 实现截图（本仓库渲染产物）

这些 PNG 不是设计稿，而是**从应用真实的 WPF 令牌与组件层渲染出来的**实现结果，
用来和 `design/focal/shots/*.png`（设计稿渲染）逐个界面对照评审。

生成方式（离屏渲染：窗口在屏幕外显示，不抢焦点）：

```powershell
& .\build\tools\focal-build.ps1 -Quiet
.\src\WhSnap.App\bin\Release\net8.0-windows10.0.22000.0\WhSnap.exe --focal-shots --out artifacts\focal-shots
```

| 文件 | 内容 | 对应设计稿 |
|---|---|---|
| `focal-tokens.png` | 令牌表：颜色 / 排版 | `design/focal/index.html` |
| `focal-components.png` | 组件层：按钮 / 工具键 / 开关 / 输入 / 横幅 / 列表行 | `design/focal/index.html` |
| `focal-icons.png` | 图标集：64 个自绘图标 | `design/focal/icons.js` |
| `surface-settings.png` | 设置窗口整窗 | `surfaces/settings.html` |
| `surface-annotation.png` | 标注编辑器整窗 | `surfaces/annotate.html` |
| `surface-pinPanel.png` | 贴图管理面板整窗（空状态） | `surfaces/pin-panel.html` |
| `surface-onboarding.png` | 首次运行向导整窗 | `surfaces/onboarding.html` |

> 说明：遮罩（`surfaces/overlay.html`）与托盘菜单（`surfaces/tray.html`）没有进这个清单 ——
> 遮罩是全屏交互面、托盘菜单是 WinForms 原生菜单，两者的"截图"都必须在真实桌面上出现
> （`--overlay-test` / `--pin-test` 会短暂显示它们）。遮罩的量测色、尺寸标签、11×11 放大镜与
> 浮动工具条由 `--focal-test` 的令牌/度量断言与 `--overlay-test` 的行为断言共同守住。
