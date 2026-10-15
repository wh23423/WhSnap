# ADR 0005：配置以 schema 为单一数据源，设置界面自动生成

- 状态：已采纳（M3）
- 日期：2026-10-15

## 背景

规划 §6 要求"能配的都开放给用户"，并明确点名实现要点：
`SettingsSchema` 单一数据源 → 自动生成设置 UI，避免"每加一项就手写一遍界面"。
M2 结束时已有 28 项配置，如果靠手写 XAML，每次加功能都要同步改界面、导入校验、默认值重置三处。

## 决策

- `WhSnap.Core.SettingsSchema` 是配置项的**唯一数据源**：键名（= `AppSettings` 属性名）、分组、标题、
  说明、控件类型（Bool/Int/Double/Text/Choice/Color/DirectoryPath）、范围、单位、候选项。
- `WhSnap.App.Settings.SettingsWindow` 遍历 schema 生成全部标签页与控件（代码生成，不写 XAML per 项）。
- `SettingsBinder` 用反射把 schema 键映射到 `AppSettings` 属性，并做范围/取值校验。
- `SettingsImportValidator`（Core，纯函数）复用它做导入校验：未知项与非法值都会被**报告**。
- 两道护栏（都在自检里，不靠人自觉）：
  - `SettingsBinder.VerifySchema()`：schema ↔ `AppSettings` **双向**一致（缺属性、缺界面入口都报错）；
  - `--settings-test`：真实构建设置窗口，验证 schema 每一项都渲染出了控件。

## 代价与后果

- 反射读写属性，性能不如直接赋值——但设置项一年也就改几次，完全不敏感。
- 控件外观受限于"按类型统一生成"，个别项需要特例（目录选择、颜色）在 `BuildControl` 里分支处理。
- 收益：M4/M5/M6 新增配置项的成本从"改三处"降到"加一行"。

## 重新评估的时机

如果出现大量"联动/依赖"型配置（A 开启才显示 B），需要在 schema 上加 `DependsOn` 字段，
而不是退回手写界面。
