# ADR 0003：贴图用原生分层窗口 + `UpdateLayeredWindow`

- 状态：已采纳（M2）
- 日期：2026-10-15

## 背景

贴图需要：逐像素 alpha（截图/文本贴图里有透明像素）、置顶无边框、支持缩放旋转翻转、
鼠标可以穿过透明区域、内存占用低（30 张贴图 ≤ 250 MB）。

WPF 的 `Window.AllowsTransparency=True` 也能做透明窗口，但它是整窗合成、性能与保真都差，
而且透明区域仍然吃掉鼠标消息。

## 决策

用原生窗口 + `UpdateLayeredWindow`：

- 窗口样式 `WS_POPUP | WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW`（不进 Alt+Tab）。
- 像素内容用 WPF 合成（`TransformedBitmap` 做 90° 旋转、`DrawingVisual` 做缩放/翻转/阴影/描边）
  后拷进 32bpp 顶朝下的 DIB，一次性提交给 `UpdateLayeredWindow`。
- 不透明度用 `BLENDFUNCTION.SourceConstantAlpha` 实现，**不需要重新合成像素**。
- 拖动只走 `SetWindowPos`（不上传像素）；缩放/旋转/翻转才重新合成。
- 透明像素天然鼠标穿透；**阴影留白区显式返回 `HTTRANSPARENT`**，避免贴图周围出现"隐形挡板"。

## 代价与后果

- 消息循环、命中测试、右键菜单（`TrackPopupMenu`）、拖放（`WM_DROPFILES`）都要自己处理。
- 好处：30 张贴图实测 53 MB（门槛 250 MB）；像素放大用最近邻保持"像素级内容不糊"。

## 重新评估的时机

- 4K 下帧时间不达标时，把合成从 WPF `RenderTargetBitmap` 下沉到 Direct2D（规划 §4 的 V1/V2 分档）。
