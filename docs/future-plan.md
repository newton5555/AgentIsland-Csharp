# 未来计划：后台职责重构与前后台解耦

更新日期：2026-09-23。状态：P0–P6 规划项已落地，评审缺陷已按优先级修复。无窗口宿主见 [p6-headless.md](p6-headless.md)。

来源：任务「项目重构计划」（`01a064f9-9a29-7400-b3aa-848e9da31c15`）2026-09-22 的连续讨论，以及同日对 [ccusage](https://github.com/ccusage/ccusage) 主分支提交 [`4e67d789`](https://github.com/ccusage/ccusage/commit/4e67d789) 的静态实现对照（未做性能跑分）。本文件作为后续版本规划的统一入口。阶段编号表示依赖顺序，不代表已承诺的版本号或交付日期。

## 1. 目标与范围

将当前以实现功能为主的后台整理为可独立运行的 Agent 监控业务系统：消费采集、额度查询、余额查询、活动监控、账户接入各司其职，再通过查询层组合成产品需要的数据。

- `AgentIsland` 专指桌面 UI 产品；后台统一使用 `AgentMonitoring`，目录与命名空间按实际业务职责组织。
- 后台不创建 WPF 窗口、不依赖 UI 线程；关闭和重建前端不影响采集、刷新或已有状态。
- 先在同一进程内完成解耦，以无窗口宿主验证后台；独立服务进程、IPC、远程服务留待实际需要时再规划。
- 本轮规划暂不考虑 Avalonia 迁移；现有方案保留为历史参考，不作为本计划的前置条件或交付项。
- 保留现有统计语义、Provider 能力和 WPF 交互，在每条业务链完成替换后逐步清理旧实现。
- 保留现有 Worker 的任务合并、取消令牌和运行代次控制，并与 UI 解耦；无 UI 化建立在这套调度之上，而不是重做调度。

## 2. 当前基础与仍留在桌面侧的边界

后台已拆到 `AgentMonitoring.Core`、`AgentMonitoring`、`AgentMonitoring.Host`；解析器仍在 `AgentIsland.Providers`，Windows 路径与凭据仍在 `AgentIsland.Windows`。WPF 产品是 `AgentIsland`。不存在独立的 `AgentMonitoring.Providers` / `AgentMonitoring.Windows` 项目。

已经落到后台的部分：`ConsumptionFact` 与字节游标采集、Codex 跨文件/分叉去重、独立 `SnapshotPricer`、额度/余额按 `AccountRef` 存储与请求、`IMonitoringQuery` 只读快照、`AgentRuntime` 内部取消、`ReminderBroker` 观察基线。当前架构见 [architecture.md](architecture.md)，P0 契约见 [p0-baseline.md](p0-baseline.md)，无窗口入口见 [p6-headless.md](p6-headless.md)。

仍留在 WPF 宿主、不作为本轮缺口的部分：

- `UsageStore`、`CostStore`、`ActivityMonitor` 仍编排桌面刷新，部分路径使用 Dispatcher。
- 岛体两个槽位仍用 `DisplayProvider`；后台查询与采集使用 `AgentKey`。
- 其它 Agent 的本地消费仍可走账本扫描；Codex 已走 `ConsumptionStore`。
- 声音、托盘、弹窗和窗口激活仍由桌面侧执行。

ccusage 对照结论见第 12 节。

## 3. 业务术语与能力

| 能力 | 职责 | 典型输出 |
| --- | --- | --- |
| Consumption | 读取本地消费事实，增量解析和校正 | 时间、会话、项目、模型、输入/输出/缓存 Token、来源 |
| Pricing | 为已保存的消费事实计价 | 官方金额、估算金额、无法计价，以及来源/版本/有效时间 |
| Quotas | 查询官方账户额度 | 额度窗口、已用/剩余、周期、重置时间、套餐 |
| Balances | 查询官方余额 | 金额、币种、查询时间 |
| Activity | 识别会话活动并推进时间状态 | Working、NeedsYou、Idle、对应会话 |
| Accounts | 账户识别、凭据接入、受支持的认证和切换 | 账户标识、认证状态、操作结果 |
| Navigation | 提供会话定位信息 | 会话 ID、项目目录、可用启动目标 |

消费 Token 不能直接推算官方额度百分比；余额、额度和估算费用分别建模。消费事实与价格分离：事实先入库，计价由 Pricing 根据当时有效的价格来源计算。UI 可以继续使用“用量”等用户熟悉的名称。

每个 Agent 仅声明并实现实际支持的能力。不支持余额的 Agent 无需实现空的余额功能；远端查询失败不阻断本地统计。具体协议、账户操作和平台支持在迁移时逐项核实，能力名称不代表所有 Agent 已支持。

## 4. 项目、命名空间与依赖

当前仓库的实际项目（尚未拆出独立的 `AgentMonitoring.Providers` / `AgentMonitoring.Windows`）：

| 项目 / 根命名空间 | 职责 |
| --- | --- |
| AgentIsland | WPF、ViewModel、视觉、交互、桌面入口和应用组装 |
| AgentMonitoring.Core | Agent/账户标识、能力与平台契约、纯数据模型、统计规则 |
| AgentIsland.Providers | 各 Agent 的日志格式、协议、能力适配器 |
| AgentMonitoring | 运行时调度、采集协调、状态存储、账户协调、组合查询、提醒规则 |
| AgentIsland.Windows | Windows 路径、凭据、进程发现、只读数据库适配 |
| AgentMonitoring.Host | 无窗口采集与查询宿主 |

目标依赖：Providers → Core；AgentMonitoring → Core/Providers；Windows → Core 中的平台契约；宿主组装 AgentMonitoring。后台项目不得反向引用 AgentIsland WPF。后续若再拆 `AgentMonitoring.Providers` / `AgentMonitoring.Windows`，职责与此表对应，不另起一套边界。

后台内部按功能组织，不把每个目录都拆成独立项目：

```text
AgentMonitoring/
  Consumption/       消费增量采集与校正
  Pricing/           价格来源、离线快照、覆盖配置与计价
  Quotas/            额度刷新
  Balances/          余额刷新
  Activity/          活动信号与时间状态
  Accounts/          账户及认证协调
  Storage/           消费记录、扫描游标、快照
  Queries/           概览、消费汇总、报告组合查询
  Notifications/     提醒规则与事件
  Runtime/           启停、调度、取消、并发控制
```

模型与调度已迁入 `AgentMonitoring.Core` / `AgentMonitoring`；解析器仍在 `AgentIsland.Providers`，Windows 平台适配仍在 `AgentIsland.Windows`。托盘、窗口定位、点击穿透、屏幕和窗口激活留在 AgentIsland 桌面侧。应用更新安装属于产品宿主职责，不归入 Agent 额度或消费服务。

## 5. 采集与存储设计

### 5.1 独立能力链

拟采用 `IConsumptionSource`、`IQuotaSource`、`IBalanceSource`、`IActivitySource` 等小接口。Provider 解释自身格式和协议；公共后台负责调度、持久化、去重协调、费用计算及查询。接口签名在第一阶段结合现有 Provider 固定。

消费源可以拆分文件发现、增量读取与解析组件。消费与活动允许共享底层文件读取结果，但不互相驱动业务刷新，也不共享混杂的状态模型。

Pricing 是独立业务模块，不嵌在解析器或 UI 里。解析器产出消费事实；Pricing 读取事实和当时有效的价格来源，产出带出处的计价结果。

### 5.2 消费事件模型先于增量存储

未来消费事实至少包含：

- 来源记录 ID（用于去重、追溯和扫描游标）
- 会话 / 项目
- 可识别的账户；无法识别时明确标为未知，不归入当前登录账户
- 原始模型与归一化模型
- 输入、输出、缓存创建、缓存读取 Token
- Token 口径，尤其要明确推理 Token 是否已包含在输出中，避免再加一次
- 计价所需元数据：服务档位、长上下文分类、事件时间、官方自报金额（若有）

先在 P0 固定该模型与口径，再在 P2 设计增量存储和扫描游标。模型不足时不要先落库。当前 `TokenEvent` 用于现有图表够用，不能直接作为持久化事实。

### 5.3 去重与扫描游标

保存去重键和来源标识，覆盖：

- 同一文件内的重复播报
- 跨文件复制与多目录重复
- 父子任务继承与分叉会话中的重放前缀
- 归档副本与活动副本并存
- 累计值重置、文件截断、替换、删除和重新扫描

删除文件是否保留历史须按现有行为固定，不自动等同于删除消费事实。当前 Codex 解析链只覆盖相邻累计值相等的同文件重放；P2 必须补上复制日志、多目录重复、父子任务继承、归档与累计值重置的样本，先明确规则，再设计事件身份和字节游标。

当前 `LogParseCache` 按 `(mtime, size)` 复用整文件解析结果。目标是按字节游标增量采集：未变化的文件跳过，变化的文件从上次位置继续读，解析版本变化时再重建。

消费写入与游标提交需要事务或等价的一致性保证；中断重启后不漏计、不重复计数。

### 5.4 定价模块

Pricing 的职责：

- 消费事实独立保存，价格变化不要求重读原始日志，可对已存事实重新计价。
- 计价结果携带来源、版本和有效时间。
- 官方记录金额、估算金额、无法计价分别表达；未知费用不能显示为零。
- 在线价格更新失败时使用已验证快照；覆盖配置可按原始模型名生效。
- 未知模型不得在底层静默返回零后依赖其他判断提示未计价。

P0 固定上述契约。P2 在 Codex 消费闭环中落地第一版实现；其余 Agent 在后续阶段接入同一模块。

### 5.5 数据生命周期

| 数据 | 保存内容 | 更新方式 |
| --- | --- | --- |
| 消费事实 | 标准化记录、来源身份、校正信息 | 新增、追加、替换或重建时增量更新 |
| 扫描游标 | 来源身份、位置、解析版本、必要校验信息 | 与对应消费写入保持一致 |
| 计价结果 | 金额类别、来源、版本、有效时间 | 价格更新或事实校正后重算，不重读原始日志 |
| 额度/余额 | 最新成功快照、最近尝试结果、有效性 | 定时、手动、到期刷新 |
| 活动 | 当前会话状态、必要的事件时间 | 文件事件及时间推进 |
| 账户 | 稳定标识、来源、认证状态、凭据引用 | 账户识别、认证或切换 |

- 远端快照按 Agent + Account 隔离。
- 错误不覆盖最新成功数值。分别记录成功时间、尝试时间、是否过期和错误原因。
- 区分正常零值、不支持、未安装、未登录、未获取、无记录、失败、过期等状态。
- 关闭 Agent 取消采集和请求、释放运行缓存，保留历史和凭据。历史查询不受是否置顶影响。
- 存储实现先评估现有缓存复用与持久化需求，再选择方案；逻辑契约先于数据库选型，凭据不进入报表或普通消费记录。

## 6. 调度、并发与生命周期

`AgentRuntime` 管理 Agent、账户和能力启停；每项能力拥有独立刷新策略。

- 手动刷新额度仅刷新额度；切换报告日期仅查询已有数据；UI 读取不能隐式触发扫描或网络请求。
- 采集更新消费存储 → 查询读取一致快照 → UI 展示。切日期、开报表、重建窗口都不得重新触发采集。
- 同一 Agent/账户/能力的重复请求合并，不同 Agent 可独立推进；限制总并发和积压。
- 使用取消令牌与运行代次/版本校验，保证关闭、切账户或重新启用后旧结果不能回写。现有 `CostQueryService` 的共享扫描、消费者取消与代次控制迁入 Runtime，查询侧不再启动扫描。
- 后台采用明确的串行状态更新或同步机制，对外发布不可变快照及版本；正确性不依靠 UI Dispatcher。
- watcher 负责提示变化，溢出后可补扫；活动时间状态即使无文件变化也能从工作推进到空闲。
- 订阅者取消只释放自身订阅或查询，不能取消其他消费者共享的采集任务。
- 退出宿主时统一取消、等待收尾和释放资源；重复启动、关闭和恢复不能产生多套循环。
- Agent 采集启用、前端置顶槽位、历史筛选分别建模，迁移时保留当前日报纳入非置顶消费的行为。

## 7. 查询组合与前端边界

| 查询 | 返回内容 |
| --- | --- |
| GetAgentOverview | 当前账户、今日消费、额度、余额、活动及各自来源状态 |
| GetConsumptionSummary | 按时间、Agent、账户、模型、项目等支持维度聚合 |
| GetReport | 日/周/月报告、热力图、日明细和对决数据 |

查询读取已存储事实和快照；多个页面共享同一套统计规则。返回 DTO 包含数值、单位、来源时间、有效性、错误及快照版本，不含 WPF 类型、颜色画刷、动画或翻译文本。金额字段区分官方、估算和无法计价。

跨消费与额度来源允许不同更新时间，组合结果明确保留这种差异。单份消费报告使用一致快照、时区、日期范围与筛选口径，避免各页面合计不一致。

WPF ViewModel 订阅变化、获取快照并自行切回 UI 线程；启停、刷新、账户操作通过明确命令提交。重新创建窗口时先读取最新快照，再订阅后续变化，避免遗漏更新。账户切换策略独立于额度查询；查询不得暗中切换账户。

提醒规则输出结构化事件，桌面侧负责声音、托盘或弹窗。一次性提醒需要稳定事件标识和消费规则，防止重建窗口重复提醒。导航由 Provider 给出语义目标，桌面侧执行并反馈真实结果。

## 8. Codex 首条迁移链示例

```text
AgentIsland.Providers/Cost/Codex/
  CodexTranscriptParser
  CodexReplayPlan
  CodexRolloutDiscovery
AgentMonitoring/Consumption/
  CodexConsumptionCollector
  ConsumptionStore
```

这些是当前落地的职责名称。后续若迁入独立 Providers 项目，保持同一边界。`CodexReplayPlan` 负责父子会话关系、分叉时间和继承记录，与单文件相邻累计值去重分开。Codex 模块理解本地记录、额度协议、账户来源和会话定位；公共后台统一管理扫描位置、消费存储、价格估算、查询与提醒。

验收覆盖当前/归档会话、同文件重放、复制日志、多目录重复、父子任务继承、累计值重置、环境变量路径优先级、无法识别账户的历史、断网与凭据失效。推理 Token 不得再次计入输出。今日 Token 和官方额度分别采集、独立刷新，并在概览中组合展示。

## 9. 分阶段实施与交付条件

| 阶段 | 任务与交付 | 完成条件 |
| --- | --- | --- |
| P0 行为基线与契约 | 已写入并关闭 [p0-baseline.md](p0-baseline.md)：Agent 能力/来源/账户/刷新；`ConsumptionFact` 与 Token 口径；Codex 脱敏样本与当前/目标合计；Pricing 三分金额；接口与存储原则；ccusage 口径差异 | 有可回归的数据及行为清单，旧行为与拟调整行为可区分；事件模型足以支持会话分析、账户归属、追溯和重新计价 |
| P1 命名与业务边界 | 已建立 `AgentMonitoring.Core` / `AgentMonitoring`；Agent/账户/能力/快照契约迁入 Core；`ICostQueryService` 使用 `AgentKey`；`DisplayProvider` 留在 WPF 适配层；Antigravity 声明 Cost，DeepSeek 声明 Balance | 后台公开契约不引用 UI 类型，现有 WPF 通过适配层仍可运行 |
| P2 Codex 消费闭环 | 已落地 `CodexTranscriptParser` / `CodexReplayPlan`、`ConsumptionStore`、`SnapshotPricer`、`CodexConsumptionCollector`。CostStore/Worker 先采集；报告 `ScanAsync` 只读存储。复制、归档相对路径、父子继承按 P0 目标合计 | 冷/热扫描结果一致，中断恢复不重计；切报告日期、开报表、重建窗口不扫描；复制/归档/继承样本不重计；价格更新可对已存事实重新计价 |
| P3 额度、余额和账户 | 已落地 `QuotaStore`/`QuotaRefresher`、`BalanceStore`/`BalanceRefresher`、`IAccountDirectory`。UsageStore 按 AccountRef 读写额度快照；DeepSeek 声明并实现 Balance。失败保留上次成功值 | 断网不影响本地统计，切账户不混快照，失败保留成功值 |
| P4 查询与 UI 适配 | 已落地 `IMonitoringQuery` / `LedgerSnapshotStore`。采集写入快照；`GetOverview` / `GetConsumptionSummary` / `GetReport` 只读。报告页优先走查询；CostPage 自己切回 UI 线程 | 同一范围原始总数一致，UI 不直接操作扫描器或其他页面 Store |
| P5 其余 Agent、活动、提醒 | 已落地 `ActivityReducer`/`AgentRuntime`/`ReminderBroker`/`NavigationTarget`。活动与提醒用 AgentKey；采集按能力注册的 ledger reader；WPF 只投递提醒。未知 key 无需 DisplayProvider 即可进入活动快照与查询 | 各 Agent 按实际能力回归，注册扩展不再依赖后台 UI 枚举 |
| P6 无窗口验收与收尾 | 已落地 `AgentMonitoring.Host` 与 `AgentMonitoring.Tests`。`--once` / 常驻采集不加载 WPF；持久化消费存储上重建查询合计一致；Runtime 不残留 in-flight。采样方法见 [p6-headless.md](p6-headless.md) | 后台不加载 WPF，前端重建恢复最新状态，持续运行无任务或订阅累积 |
| 评审修复 | 重建先解析再一次性替换事实与游标；分叉重放读取已存储父基线；`AgentRuntime` 内部 CTS；`FetchAsync(AccountRef)` 拒绝用当前凭据写停车账户；空扫描也 `Observe`；额度按窗口合并；`TokenEvent` 保留推理/档位/来源；`GetOverviews` 并入额度/余额/活动 Agent | 解析失败不丢旧事实；父文件未变时子重建不重计；取消一个等待者不取消共享采集；停车账户刷新不污染；第一次真实 NeedsYou 可投递；单窗口失败保留另一窗口 |

按 P0 → P1 → P2 → P3 → P4 → P5 → P6 推进，随后处理评审缺陷。每阶段保留可运行程序和明确回归记录，完成新链路对照后才删除相应旧流程。

消费模型、跨文件去重和独立定价在 P0 固定规则与样本，在 P2 随 Codex 闭环落地。不要把增量存储做在精简 `TokenEvent` 之上，也不要把 Pricing 留到全部 Agent 迁完再拆。

## 10. 回归与验收清单

- [x] 后台项目和测试入口不引用 WPF、WinForms、AgentIsland、DisplayProvider 或 UI Dispatcher。
- [x] 无窗口宿主可执行消费采集和查询（额度/活动刷新在无窗口图中以存储形式存在，网络拉取仍由桌面 Provider 适配）。
- [x] 重建查询读同一持久化消费存储，合计一致；Runtime 采集结束后不留 in-flight。
- [ ] 同一份日志的逐日、模型、Agent 和总 Token 与迁移前一致；日期边界、时区、归档和重复记录有覆盖。
- [x] 消费事实含来源记录 ID、会话/项目、账户（或明确未知）、原始/归一化模型、Token 口径；推理 Token 是否已计入输出有测试锁定。`FactMapping` 把这些字段带进报告用的 `TokenEvent`。
- [x] Codex 去重覆盖同文件重放、跨文件复制、多目录重复、父子任务继承、归档副本和累计值重置；子文件重建使用已存储父基线。
- [x] 计价带来源/版本/有效时间；官方、估算、无法计价可区分；价格更新可对已存事实重新计价。在线价格源尚未接入。
- [x] 查询、切日期、开报表、重建窗口不启动采集。
- [ ] DeepSeek 的本地消费/活动与官方余额规则分别保持；Antigravity 的发现、鉴权和模型映射分别验证。
- [ ] 0 个采集启用 Agent 不扫描、不查询额度；历史仍可查看，置顶槽位不决定历史是否计入报告。
- [ ] 关闭 Agent、切账户、并发刷新、取消及延迟返回不会污染新状态或其他 Agent。
- [ ] 截断、替换、追加到半行、删除、watcher 溢出、缓存损坏和进程中断后可恢复。
- [ ] 未知账户、不支持、零值、失败和过期可区分，官方额度不由本地 Token 推算。
- [ ] 新增 Agent 主要涉及自身 Provider、能力注册和必要 UI 资源，不改公共调度/聚合分支。
- [ ] 与 ccusage 对照时逐项解释口径差异，不以其总数为唯一标准答案。
- [ ] 固定 Release 场景记录 CPU、Private Working Set、Working Set、Private Bytes、托管分配、句柄和线程；性能结论有实测证据。

性能方法沿用 [performance.md](performance.md)。历史测试数量和旧采样不作为本次已通过证据；实施各阶段重新记录对应提交和验证结果。解耦本身不承诺降低到某个内存数值。本次 ccusage 对照是静态实现比较，不代替上述资源采样。

## 11. 迁移兼容与维护

- 使用适配层逐条替换 Store，不同时维护两套独立计数来源；对照期间仅新链路验证，切换后明确唯一写入者。
- 项目和命名空间调整同步更新解决方案、引用、构建脚本、测试及资源定位；产品名与用户可见配置目录不因后台重命名自动改变。
- 持久化格式变更提供版本标识、旧数据读取/重建方案和回退说明；先备份，避免覆盖原始历史与凭据。
- 当前已有 UI 和素材未提交修改，实施阶段应单独核对并保留。
- 每阶段在本文件更新状态、对应提交、验证结果和剩余问题；现状变化同步到 architecture.md，历史实施记录写入 migration.md。
- 后续考虑独立后台进程或其他前端时，以本次无 UI 契约为基础另开方案。

## 12. 与 ccusage 的对照结论

对照对象：ccusage 主分支提交 `4e67d789`，以及本仓库当时的本地实现。性质：静态实现比较，未做性能跑分。

| 方面 | ccusage 当前实现 | 本仓库当前 |
| --- | --- | --- |
| 产品重心 | 本地消费分析，输出多 Agent 日、周、月、会话报告 | 桌面常驻监控，组合消费、官方额度、余额和活动 |
| Agent 接入 | Agent 划分 adapter，拆 loader、parser、aggregate/report 等 | `AgentIsland.Providers` 解析；调度与存储在 `AgentMonitoring` |
| 数据模型 | Codex 事件保留会话、模型、缓存、推理 Token、服务档位等信息 | 持久化 `ConsumptionFact`；报告 `TokenEvent` 带推理口径、档位、来源 |
| 查询组织 | 报告加载数据后聚合，多报告可复用基础结果 | 采集写入存储；`IMonitoringQuery` 只读快照 |
| 常驻调度 | 所检查链路主要围绕命令执行和报告加载 | Worker + `AgentRuntime` 合并、内部取消、代次 |
| 展示边界 | 有终端展示层，但 Core 仍包含部分报告输出逻辑 | DTO，WPF 自己处理展示 |
| 缓存 | 报告链仍是发现文件 → 读取 → 聚合 | Codex 字节游标；其它 Agent 仍可走文件级缓存 |
| 定价 | 在线价格、内置离线快照、覆盖配置，以及长上下文和服务档位 | 独立 `SnapshotPricer`；未知模型为 Unpriced。在线价格源未接入 |

最值得吸收、并已写入上文阶段的四点：

1. **消费事件模型要先补齐，再谈增量存储。** ccusage 的 Codex 事件包含 session、模型是否为回退值、缓存创建、推理输出、服务档位；聚合时还保留请求级长上下文分类和时间信息。我们需要来源记录 ID、会话/项目、账户、原始与归一化模型、Token 口径和计价元数据，并明确推理 Token 是否已包含在输出中。
2. **Codex 去重需要覆盖跨文件和分叉继承。** ccusage 除事件去重外还有独立 replay 计划，读取父子会话关系与分叉时间，处理继承记录。P2 样本必须覆盖复制日志、多目录重复、父子任务继承、归档与累计值重置。
3. **定价值得独立成一个业务模块。** 消费事实独立保存；计价结果携带来源、版本和有效时间；官方记录金额、估算金额、无法计价分别表达；在线更新失败时使用已验证快照。
4. **采集与查询必须分离。** ccusage 的 Codex 报告链是发现文件 → 读取 → 聚合，不能当作常驻增量后台的模板。我们的目标保持：采集更新消费存储 → 查询读取一致快照 → UI 展示。

ccusage 可作为解析边界案例和统计结果的对照工具。双方口径不同的地方需要逐项解释，不能直接把它的总数当作唯一标准答案。不把 ccusage 的报告加载链当作后台调度设计来源；我们已有的取消和并发机制应保留，再完成无 UI 化。
