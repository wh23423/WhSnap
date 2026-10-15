# ADR 0008：Inno Setup per-user 安装，免 UAC

- 状态：已采纳（M0）
- 日期：2026-10-15

## 背景

决策 D5 是"仅安装包，不做便携版"；D7 是"安装后默认开机自启"。
若用 per-machine 安装（`Program Files`），每次安装/升级/卸载都要 UAC 提权，
而自启项是 per-user 的（`HKCU\...\Run`）——两者权限模型不一致会带来麻烦。

## 决策

Inno Setup **per-user 安装**到 `%LOCALAPPDATA%\Programs\WhSnap`：

- 免 UAC（安装、覆盖升级、卸载都不提权）；
- 自启项写 `HKCU`，与安装范围一致；
- 卸载清理自启项、快捷方式、缓存，配置与日志保留（询问）；
- 提供静默参数 `/VERYSILENT /SUPPRESSMSGBOXES`；
- 安装包用 `AppMutex=WhSnap_SingleInstance` 与主程序互斥体对齐，避免"安装时程序还在跑"。

## 代价与后果

- 每个用户各装一份（小圈子场景下无所谓）。
- 安装目录在用户配置区，安全软件/优化工具更容易误删自启项，因此启动时做一次
  **自启状态自检**并提示恢复（不静默重写，见风险 R6）。

## 重新评估的时机

如果需要"一台机器所有用户共享安装"，才需要回到 per-machine（并引入 UAC 流程）。
