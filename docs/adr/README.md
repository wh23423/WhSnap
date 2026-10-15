# 架构决策记录（ADR）

单人项目的关键选择要留痕（规划 §8.6）：**为什么这么选、代价是什么、什么时候该重新评估**。
每条 ADR 都很短——它是给三个月后的自己看的。

| 编号 | 决策 | 状态 |
|---|---|---|
| [0001](0001-capture-backend.md) | 采集后端三路可切换，`auto` 自动降级 | 已采纳（M1） |
| [0002](0002-physical-pixel-coordinates.md) | 领域层坐标一律物理像素 | 已采纳（M0） |
| [0003](0003-layered-pin-window.md) | 贴图用原生分层窗口 + `UpdateLayeredWindow` | 已采纳（M2） |
| [0004](0004-html-pin-plain-text-only.md) | HTML 贴图只提取纯文本，不引入渲染引擎 | 已采纳（M2） |
| [0005](0005-schema-driven-settings.md) | 配置以 schema 为单一数据源，界面自动生成 | 已采纳（M3） |
| [0006](0006-hotkey-model-and-conflict-layers.md) | 热键统一模型 + 三层冲突检测，不做反查占用者 | 已采纳（M3） |
| [0007](0007-self-signed-certificate.md) | 自签名证书分发（不采购 OV/EV） | 已采纳（M0） |
| [0008](0008-per-user-installer.md) | Inno Setup per-user 安装，免 UAC | 已采纳（M0） |
