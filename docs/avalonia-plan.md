# Avalonia 版本开发计划（待评审）

基线：AgentIsland for Windows v1.0.0，提交 `a5458ce`。计划日期：2026-09-04。

本文是开发方案，尚未实施 Avalonia 代码。当前 WPF 已实际测试的 Agent 为 Antigravity（agy）、DeepSeek Harness（dsh）和 Codex；这不代表其 Linux 适配已经验证。

## 1. 目标与建议范围

在同一仓库增加原生 Avalonia 桌面前端，复用已有解析和统计逻辑，逐步把后台服务从 WPF 宿主中抽出。

- 第一阶段目标平台建议为 Windows x64 与 Linux x64；Windows 用来对照现有 WPF 行为，Linux 用来验证跨平台能力。
- Linux 首轮建议选 Ubuntu 24.04 桌面作为固定测试环境，分别记录 X11、Wayland 会话中的实际表现。发行版和桌面环境可按实际测试机器调整。
- 首批 Agent：Codex → dsh → agy。Claude、Grok、Cursor 保留已有解析器，后续分别做真实账号和平台适配验收。
- 产品目标包含悬浮岛、额度/余额、实时状态、Token 热力图、周/月卡、对决画面、工作动画和设置。
- WPF 1.0.0 在过渡期保留，作为行为与视觉基线；Avalonia 达到验收条件后再决定是否替代 Windows 默认前端。
- macOS、ARM64、手机/Web、原生 Wayland 特殊桌面协议适配暂列后续，不作为首版完成条件。

推荐使用原生 Avalonia AXAML 和控件体系。具体 Avalonia/.NET 版本在技术验证阶段锁定稳定版本与兼容组合；不要直接把当前 `net8.0-windows` 项目目标框架改名后继续引用 WPF。

## 2. 已检查的代码与复用边界

| 当前代码 | 判断 | 计划处理 |
| --- | --- | --- |
| `src/AgentIsland.Core` | `net8.0`；模型、Token/费用聚合、缓存策略、能力描述可以复用 | 原则上保持业务规则；新增报告纯数据模型放在这里 |
| `src/AgentIsland.Providers` | `net8.0`，含 dsh 解析和 `ZstdSharp.Port` | 复用解析器，使用脱敏文件验证 Windows/Linux 相同结果 |
| `Backend/Cost/*LogReader.cs` | 部分代码只做路径发现与缓存，但依赖 Windows 命名空间 | 通用文件读取迁入 Runtime；路径由平台实现提供 |
| `Backend/Cost/CostStore.cs`、`Backend/Monitoring/ActivityMonitor.cs`、`Backend/Usage/*Store.cs` | 多处使用 WPF Dispatcher/DispatcherTimer | 提取后台任务、刷新策略和状态快照；UI 线程切换留在前端适配层 |
| `UI/Report/ReportData.cs` | 数据聚合同时引用 WPF Color、UI Provider 类型和静态 Store | 分离报告计算与展示；模型保留 AgentKey/颜色标识，前端转换画刷 |
| `AgentIsland.Windows/Paths/IslandPaths.cs` | 路径、应用设置与 Provider 数据来源混在 Windows 项目 | 分离应用目录与 Agent 数据目录，Linux 使用 HOME/XDG 路径规则 |
| `AgentIsland.Windows/Storage/Cursor*.cs` | 直接 P/Invoke `winsqlite3.dll` | Windows 保留；Linux 通过只读数据库适配器实现等价查询 |
| `Backend/Usage/AntigravityLanguageServer.cs` | 本地协议请求可复用，端口发现使用 `iphlpapi.dll` | 分离协议调用与进程/端口发现；Linux 单独实现发现逻辑 |
| `Backend/Alarms/*` | 混有 Win32、声音播放、WPF 窗口和提醒规则 | 保留提醒规则，窗口、声音、导航交给宿主/平台 |
| `UI/*.xaml`、`ProviderLogo.cs`、`Theme/*`、`ReportCards.cs` | WPF 控件、绘图、动画、事件实现 | 迁移布局意图和素材，重新实现 Avalonia 控件与动画 |
| `tests/AgentIsland.Tests` | 唯一入口目标为 `net8.0-windows`，启用 WPF/WinForms | 保留 WPF 测试，抽出可在 Linux 执行的业务测试入口 |

仓库已有分层有用，但后台并未全部跨平台。迁移成本主要集中在后台调度、系统能力、悬浮窗口和自绘控件。

## 3. 建议的目标结构

```text
src/
  AgentIsland.Core          纯模型、规则、统计、报告计算
  AgentIsland.Providers     Agent 协议和日志解析
  AgentIsland.Runtime       新增：扫描、缓存、刷新、状态编排与平台契约
  AgentIsland.Windows       Windows 路径、进程、启动项、原生窗口能力
  AgentIsland.Linux         新增：Linux 路径、进程发现、桌面集成
  AgentIsland               保留现有 WPF 前端及兼容适配
  AgentIsland.Avalonia      新增：AXAML、ViewModel、控件、桌面入口
tests/
  AgentIsland.Tests         保留 WPF/Windows 回归
  AgentIsland.Runtime.Tests 新增：跨平台解析、聚合与后台行为
  AgentIsland.Avalonia.Tests 按实际 UI 测试需要增加
```

依赖约束：

1. Providers → Core；Runtime → Core/Providers。
2. Windows/Linux → Runtime/Core，提供 Runtime 声明的平台实现。
3. 两个前端负责组合 Runtime 和相应平台实现。Runtime 不引用任何前端、WPF、Avalonia 或 WinForms。
4. Avalonia 的 Linux 构建不能引用当前 `net8.0-windows` 的 Windows 项目。采用明确的目标框架/平台条件引用，Windows 构建引用 Windows 实现，Linux 构建引用 Linux 实现。
5. 第一个阶段就从 Linux 构建入口验证项目依赖，不以“Windows 上编译成功”代替跨平台验证。

`Runtime` 在这里承载实际共享服务：原先只有一个 WPF 宿主时没有必要，现在两个前端需要共享同一套扫描与状态规则。

初期只按已出现的依赖引入少量接口：

- 数据路径与设置存储：应用配置/缓存目录、Provider 日志与凭据位置。
- Agent 进程/端口发现：重点支持 agy 本地服务和会话关联。
- 系统导航：打开链接、目录、终端、目标应用；聚焦具体窗口是独立可选能力。
- UI 通知适配：后台发布快照，前端在自身 Dispatcher 更新界面。

托盘、通知、声音、自启动和窗口穿透在相应阶段实现。避免预建一套庞大的插件/服务框架。

## 4. 必须保持的业务契约

### 4.1 dsh：三个口径分别验收

1. **Token**：扫描 `.dsh/sessions/**/session.jsonl.zstd`；官方和其他网关记录均计入，沿用现有事件去重与替换规则。
2. **余额**：只调用官方 DeepSeek 余额接口；沿用环境变量/官方凭据文件来源。余额显示金额与币种，零余额、欠费、读取失败分开处理。
3. **活动**：`DeepSeekActivityReader` 接受所有 dsh 网关、排除子 Agent，依据事件及时间窗驱动蓝鲸。官方路由限制只用于余额接口，不用于本地活动状态。

本地日志有数据时，即使余额请求失败，Token 统计仍必须正常显示。不得把请求失败转换成金额为零或额度百分比。

### 4.2 Codex 与 agy

- Codex 保留 `CODEX_HOME`、当前/归档会话、账号凭据、额度窗口、重置时间和日志重放去重规则。
- agy 分开验证本地服务发现、鉴权/CSRF、额度解析与活动监控；不能因为界面显示四彩标识，就认为其 Linux 数据链路已经接通。
- 既有账号切换、重认证、终端跳转单独列出验收项。首个演示版本可复用已登录凭据，完整版本再验收这些交互。
- 任何 Provider 都区分未安装、未登录、暂无记录、请求失败、缓存过期与正常零值。

### 4.3 报告一致性

- 同一份扫描快照、Provider 筛选、时区、日期范围和 Token 模式驱动热力图、日详情、周卡、月卡。
- 保留既有按扫描数据确定报告日期的语义，迁移时不悄悄改成另一套自然周/月定义。
- `568M` 与 `5.68亿` 是显示单位差异；验收比较格式化前整数和逐日明细。
- 热力图混色沿用原有阈值与分色规则，先从现有实现提取，禁止直接固定成所有双 Provider 都对半显示。
- 保留 Codex 亮紫、DeepSeek 图标蓝、agy 四彩视觉，以及已有对决素材选择和缺图降级。

## 5. 开发阶段与交付门槛

工作量为单名熟悉项目开发者的有效人日估算，不是交付承诺；整体约 25–40 人日。真实 Linux 环境、账号可用性和窗口特性验证结果会影响排期，P0 后重新估算。

| 阶段 | 预计人日 | 主要交付 | 通过条件 |
| --- | --- | --- | --- |
| P0 技术验证与基线 | 2–3 | 最小透明窗口、基础托盘、拖动、双系统构建；WPF 参考截图和脱敏数据 | Windows/Linux 均能启动；给出窗口定位、穿透、托盘的实际能力表 |
| P1 共享 Runtime | 4–6 | 扫描、缓存、刷新、活动与报告计算解耦；跨平台业务测试 | 同一数据集两端输出一致；WPF 现有测试继续通过 |
| P2 首个真实数据闭环 | 3–5 | Codex/dsh 路径、凭据读取、Token、额度/余额，最小双槽岛 | Windows/Linux 都展示真实数据；断网不影响本地统计 |
| P3 岛体和工作动画 | 3–5 | 展开/收起、单/双槽、透明边缘、Logo、蓝鲸动画、低功耗 | 状态驱动画面；空闲停止工作动画；DPI 和多屏切换可用 |
| P4 统计与周/月卡 | 4–6 | 热力图、日详情、报告、对决、PNG 导出 | 所有视图口径一致；日期/色彩/导出视觉验收通过 |
| P5 设置和系统集成 | 3–5 | 设置页、托盘菜单、提醒、导航、自启动、配置迁移 | 可完整配置和退出；重启恢复；无托盘环境仍能操作 |
| P6 agy 与兼容验收 | 3–5 | Linux agy 发现和鉴权；三 Agent 真实场景回归 | Codex/dsh/agy 分别记录实测结果，窗口能力按桌面环境标记 |
| P7 收尾 | 1–2 | 安装运行说明、问题清单、最终性能记录 | 试用反馈中的阻塞问题关闭；决定 Windows 默认前端是否切换 |

先后关系：P0 → P1 → P2；P3/P4 都依赖 P2 的数据契约；P5 依赖 P1/P3；P6 的 agy 风险探测在 P0 开始，最终集成在 P5 后。打包和发布安排在功能验收后处理。

第一轮可体验目标为 P0–P2：真实 Codex+dsh 数据进入 Avalonia 岛体。此时不标记为完整替代 WPF。

## 6. 可以直接拆任务的实现清单

### P0：先验证悬浮岛最难的系统行为

- 记录当前 WPF：折叠/展开、一个/两个 Agent、余额、额度、热力图、周/月报告、设置窗口。
- 建立独立实验配置目录，避免 WPF 和 Avalonia 同时写正式配置。
- 最小 Avalonia 窗口验证透明背景、圆角、拖动、置顶请求、屏幕位置、缩放与关闭行为。
- 真正用另一个进程窗口测试透明区域点击穿透；控件级 `IsHitTestVisible` 不作为系统穿透验收依据。
- 验证 Linux 托盘可用/不可用场景；确认无托盘时仍能打开设置、恢复窗口和退出。
- 小范围探测 Linux agy 的运行进程、命令行/端口/鉴权来源，仅检查当前用户自己的服务。
- 锁定 Avalonia 与 SDK 版本，记录 Windows/Linux 依赖和执行方式。

### P1：后台与报告规则解耦

- 将文件遍历、解析缓存、读取周期和取消逻辑移入 Runtime。
- 用可取消的异步循环取代后台依赖 DispatcherTimer；保持原刷新周期、刷新合并及过期缓存规则。
- 防止同时启动多个扫描/刷新循环；退出时取消，恢复运行后能重新扫描。
- 把 `CostStore/UsageStore/ActivityMonitor` 的数据计算与 UI 通知分开。按功能逐个切换 WPF 适配器，避免同时重写所有 Store。
- 将 ReportData/ReportPeriods 中的纯聚合抽出，去除 WPF Color、UI DisplayProvider 与静态全局 Store 依赖。
- 把颜色保存为语义标识或普通颜色值，WPF/Avalonia 各自转换为画刷。
- 将解析、缓存、聚合测试迁到可在 Linux 执行的入口；保留依赖 WPF 的布局测试。

### P2：Codex/dsh 真实数据

- 注入 Agent 路径解析；保留现有环境变量优先级，不写死开发者目录。
- 复用 dsh 解压/事件解析，验证大文件、追加写入、未写完整和重新扫描的行为。
- Codex 包含归档目录，保留重放事件去重；共享文件不能因新旧前端而重复计数。
- 迁移余额和额度请求、取消/超时、缓存与错误状态，前端分别绑定金额模型与 quota 模型。
- 最小岛体展示 Provider、状态、额度/余额；详情显示数据时间与读取状态。
- 同一脱敏样本跑 Windows/Linux，对照逐日/模型/Provider 原始数值。

### P3/P4：界面与素材任务表

| 现有文件/区域 | Avalonia 任务 | 验收重点 |
| --- | --- | --- |
| `IslandWindow.xaml(.cs)`、`IslandModel.cs` | 新 IslandWindow/ViewModel；岛体状态、展开布局与平台窗口适配分离 | 单槽居中、双槽位置、边缘点击、焦点、多屏和缩放 |
| `ProviderLogo.cs`、`ProviderMarks.cs`、`Theme/IslandAnimations.cs` | Logo 控件、状态样式、动画生命周期 | agy 四彩、Codex 紫色、dsh 蓝鲸游动/喷水；不因反复绑定叠加动画 |
| `UsagePage.cs`、`DeepSeekBalanceText.cs`、`ResetCardChip.cs` | 用量/余额模板及额度进度控件 | 双窗口、重置时间、余额为零/欠费/不可用 |
| `CostPage.cs`、`Charts/ChartViews.cs` | 热力图、圆环、柱图，复用计算结果 | 混色色块规则、悬停日期、Provider 筛选、长数字 |
| `Report/ReportWindow.xaml`、`ReportCards.cs` | 周/月卡 AXAML 和必要的自绘区域 | 卡片 border、字号、比例、对决位置、品牌颜色 |
| `ReportCalendarPopup.cs`、`PagedContent.cs` | 日期选择和翻页控件 | 与热力图日详情对应，无时间区间偏移 |
| `SettingsWindow`、各 SettingsPage | 窗口、导航、服务/显示/提醒/通用/状态/关于等有效页面 | 当前有入口的功能完整，隐藏旧入口不因迁移重新出现 |
| `ProviderRowControl`、`SortableProviderStack` | 服务行、开关、排序拖动 | 最多两个 Agent、顺序保存、禁用状态一致 |
| `Segmented`、`CobaltToggle`、`PillButtonControl`、`CaptionButtons` | 共用样式和基础控件 | 鼠标/键盘操作、焦点、禁用态、缩放下的尺寸 |
| `TrayIcon`、`TurnAlarmWindow`、`ReauthFlow` | Avalonia 托盘/提醒/对话框，调用平台导航 | 无托盘退出、提醒去重、认证取消、窗口激活 |

AXAML 迁移需逐项转换样式、属性、事件和模板；Avalonia 官方说明这些机制与 WPF 有明确差异，不能按扩展名批量替换。[WPF 迁移指南](https://docs.avaloniaui.net/docs/migration/wpf)

素材首先通过资源链接复用现有 `src/AgentIsland/Assets`，Avalonia 使用自己的资源 URI；保留 WPF pack URI。维护一份共享素材映射表，明确两端资源来源。完成迁移后再决定是否把根 `assets/` 设为唯一来源。

动画实现使用 Avalonia 原生动画/绘图机制；高频自绘仅用于确实需要的区域。不把每一帧状态回写到共享业务 Store。

### P5/P6：平台差异逐项闭环

| 能力 | Windows | Linux 计划 | 缺失时的行为 |
| --- | --- | --- | --- |
| 应用设置/缓存 | 保留兼容读取 | 应用设置按 XDG config，缓存按 XDG cache；Provider 路径各自解析 | 明确提示目录不可读，不覆盖已有设置 |
| agy 端口发现 | 复用现有原生实现 | 根据实际进程/端口来源实现与缓存，避免每个动画帧启动命令 | 显示服务未发现或保留过期额度 |
| SQLite | 现有 Windows 原生读取 | 选定受维护的跨平台 SQLite 方案，验证只读/WAL/锁竞争 | 单个 Provider 错误不影响其他统计 |
| 托盘 | 对照原有菜单行为 | 根据桌面环境实测 | 保留可访问的窗口入口和退出操作 |
| 窗口定位/置顶/穿透 | Windows 原生适配与 Avalonia 窗口句柄配合 | X11 与 Wayland/XWayland 分别测试 | 不支持绝对定位时允许普通可拖动窗口；不支持局部穿透时缩小透明边界 |
| 前台判断/会话跳转 | 复用窗口查找和终端逻辑 | 分开实现打开应用、打开目录/URI、终端启动；精确聚焦依桌面能力判断 | 使用“打开应用/目录”等真实可执行操作，不标成成功聚焦会话 |
| 提醒/声音 | 保持提醒规则 | 桌面通知与声音适配 | 通知不可用时在应用中保留提示 |
| 开机启动 | 注册表实现 | 经用户开启后写当前用户桌面自启动项 | 不支持时说明原因 |

官方 Linux 文档当前说明：默认使用 X11，在 Wayland 桌面通常通过 XWayland；原生 Wayland 后端需要显式启用，仍标为实验性。因此首版建议先验收默认后端，再评估原生 Wayland。这里的窗口降级策略是项目方案，实际支持范围由 P0 真机结果决定。[Avalonia Linux 文档](https://docs.avaloniaui.net/docs/platform-specific-guides/linux)

Windows/Linux 的账号保存方式也要分别验证。迁移初期优先复用各 Agent 已登录凭据；认证 UI、令牌刷新和账号切换逐个接入。密钥不进入日志、报告、测试样本和版本库。

## 7. 验收与测试矩阵

### 数据验收

- 至少准备 Codex、dsh、agy 的脱敏样本；模拟数据与真实运行结果分别标注。
- dsh 样本包含官方/其他网关/混合路由/子 Agent/重复 usage；分别验证 Token 和活动筛选规则。
- 用同一快照对照热力图、周/月报告和详情的原始 Token，总数一致才验收。
- 覆盖跨日、周/月边界、本地时区、缓存重建、旧日志、文件追加、断网和凭据缺失。
- agy 退出和重启、端口变化后能恢复；恢复失败时明确显示过期数据。

### 界面与真实环境验收

- Windows 100%/125%/150%/200% 缩放；单屏、双屏不同 DPI、拔插屏幕后窗口可恢复。
- Linux 记录发行版、桌面环境、X11/Wayland、实际后端和托盘条件；不能只写“Linux 通过”。
- WSLg 可用于早期启动检查；发布级桌面行为要在真实 Linux 桌面/虚拟机桌面验证。
- 单 Agent、双 Agent、无数据、额度过期、欠费状态；中英文、长模型名、超大 Token。
- 周/月卡导出的 PNG 与界面内容一致；剪贴板和保存路径分别测试。
- dsh 运行时蓝鲸动作可见，结束后按既有时间窗恢复；agy 四彩效果、Codex 工作动画分别人工验收。
- 使用可控时间验证动画状态与停止行为，不恢复此前因无头 CI 渲染导致不稳定的逐帧像素变化断言。

### 性能与持续运行

- P0 记录 WPF 基线，统一机器/日志量/采样方法；比较冷启动、首次扫描、增量扫描、空闲 CPU 和内存。
- 首次扫描允许后台进行，窗口必须可交互；日志增长不能导致重复全量解压影响 UI。
- 静置、连续工作、反复切 Provider、关闭重开设置/报告后，不积累刷新任务或动画。
- 建议完成一次 8 小时真实使用验收，记录资源走势和异常；具体性能阈值在基线测量后确定。

## 8. 配置兼容、开发组织与停止点

- 建议从当前 main 建立 `codex/avalonia` 开发分支；本文评审后再创建和开始实现。
- 实验期独立配置/缓存。正式迁移提供一次性读取旧 WPF 设置的流程，保留旧文件，验证 Provider 顺序、颜色、语言、缩放等有效设置。
- 每个阶段产出可运行结果与验收记录；共享逻辑一旦迁入 Runtime，两端都使用同一实现，避免复制后分别修 Bug。
- 任务可按 Runtime、系统适配、AXAML/视觉三个方向划分，先稳定数据契约再并行。若后续交给 agy，优先安排边界明确的 AXAML 页面任务，由主维护者验收业务数据和真机行为。
- P0 若确认目标 Linux 桌面无法提供需要的定位/穿透行为，先评审降级体验，再投入完整动画和页面迁移。
- 本轮只提交计划供评审；不启动账号请求、真实 Agent 任务、框架迁移或版本发布。

## 9. 评审时建议确认的三项

1. 首版平台采用 Windows x64 + Linux x64，macOS 后续；Linux 首台验收机器的发行版/桌面环境是什么？
2. 首批 Agent 采用 Codex、dsh、agy；Claude/Grok/Cursor 在真实测试完成前标为未验证。
3. Linux 遇到窗口系统限制时，是否接受普通可拖动悬浮窗及减少透明边缘的降级体验？

以上已有推荐方案，可据此继续拆任务；第 1 项的实际机器信息在 P0 前补充即可。
