# ADR 0001：采集后端三路可切换，`auto` 自动降级

- 状态：已采纳（M1）
- 日期：2026-10-15

## 背景

Windows 上没有"永远正确"的抓屏方式：`Windows.Graphics.Capture`（WGC）覆盖面最好但首次抓帧慢、
需要 WinRT 互操作；`DXGI Desktop Duplication` 帧稳但抓不到受保护内容，且旋转屏要自己转；
GDI `BitBlt` 最稳最旧但拿不到部分硬件叠加层，宽色域/旋转屏由系统合成。

## 决策

抽象出 `IScreenCapture`，提供 **WGC / DXGI / GDI 三个实现**；默认 `auto` 按 WGC → DXGI → GDI 依次尝试，
**任一后端抛错或抓到"完全均匀画面"（典型纯黑帧）就自动降级**，并把整条尝试链写进日志。
设置里可固定某一个后端（`CaptureBackend`）。

## 代价与后果

- 每次截图多一次"失败尝试"的开销（仅在首选后端不可用时发生）。
- 需要 `FrameSanity` 判断黑帧，否则会把黑图交给用户（比"后端失败"更糟）。
- 三套实现都要维护；自检 `--selftest` 逐个后端验证，把"这台机器不支持"记为 SKIP 而不是 FAIL。

## 重新评估的时机

- Win11 后续版本如果让 WGC 在所有场景都可靠，可以考虑只留 WGC + GDI 两路。
- 需要 HDR/宽色域正确输出时，WGC/DXGI 的 BGRA8 路径要先做色调映射（见 backlog）。
