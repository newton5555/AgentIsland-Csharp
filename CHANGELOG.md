# Changelog

All notable changes to AgentIsland for Windows will be documented in this file.

> 版本条目保留各版本发布时的历史快照。当前主分支的测试数量、能力矩阵和架构边界请以根目录 README 与 `docs/architecture.md` 为准；当前测试基线为 67 项（66 项常规测试 + 1 项压力/资源测试）。

## [2.2.1] - 2026-09-09

### 🎛️ 现代化系统托盘菜单 (ModernTrayMenu)
- 彻底替换旧版 Windows Forms 原生 `ContextMenuStrip`，告别 Win98 白底直角边框与左侧灰色图标槽（Gutter）。
- 重构为 Fluent 深色微卡片（`#191A1F` 黑曜石底色、1px 微透光玻璃边框、8px 柔和圆角）。
- 视觉风格克制收敛：去除多余头部状态块与易渲染为黑块的 Emoji 图标，采用统一 12px 边距左对齐文本与平滑微胶囊 Hover 高亮。
- 投影收敛至 10px 精致微阴影（`BlurRadius = 10, ShadowDepth = 2, Opacity = 0.24`），配合 WPF 原生 `PlacementMode.MousePoint` 智能贴合鼠标光标并自适应屏幕边缘防溢出。

### 👻 灵动岛透明穿透模式 (Transparent Mode)
- 托盘菜单原“显示 / 隐藏灵动岛”功能升级为“透明穿透模式”与“常规交互模式”一键切换。
- **透明模式**：整岛进入 **70% 半透明状态**（`Opacity = 0.7`），并启用底层 **鼠标完全穿透**（`WS_EX_TRANSPARENT`）；用户可在监控模型进度的同时直接点击操作灵动岛后方的代码编辑器、IDE 标签或游戏窗口，不再发生遮挡与误触。
- **常规模式**：恢复 **100% 完整不透明度** 与悬停展开交互。
- 托盘右键自适应显示当前状态标签（`透明穿透模式 (70%透明)` / `✓ 恢复正常模式 (当前: 70%透明穿透)`）；左键单击托盘图标弹出时，自动安全重置回正常交互模式。

### 📐 150% 缩放初始居中修复
- 修复了在设置中配置 150% 界面缩放后，应用冷启动时初始岛向右偏离屏幕中心 225px 的时序 Bug。
- 优化 `IslandWindow` 初始化加载顺序：在计算水平屏幕居中前优先执行 `ApplyInterfaceScale()` 计算缩放后实际宽度（1350px），确保启动时 100% 像素级对齐屏幕正中。

## [2.2.0] - 2026-09-09

### 📅 日报卡与本地明细
- 新增按本地日查看的日报卡，可从灵动岛页脚、设置页和系统托盘打开，并支持日期翻页与日报 PNG 导出。
- 日报展示 24 小时 Token 脉冲、前一日环比、活跃 Agent/模型数量，以及 Agent → Model 两级明细和全局占比。
- 缓存读取/写入量进入日报聚合；有价格表的模型展示缓存节省金额，未知或不支持计价的 Provider 保持 `—`，不伪造费用。

### 🤖 Antigravity 账本识别
- 扩充 Google Antigravity 动态 `MODEL_PLACEHOLDER_Mxxx` 代号归一化，覆盖 Gemini 3.8/3.7 Flash、3.5 Flash、3.1 Flash Lite 等实际记录形态。
- 补齐对应 Gemini 价格表与缓存读取节省计算，并升级解析缓存版本以避免旧模型映射残留。

### 🎨 Provider 视觉
- Antigravity 工作态沿用 Google 蓝、红、黄、绿四种品牌色的分段色带；Codex 使用更明亮的紫色，DeepSeek 保持鲸鱼蓝，三者在状态动画中可区分。

### 🧪 验证
- 本地 `dotnet test tests/AgentIsland.Tests/AgentIsland.Tests.csproj --no-restore`：**67/67 通过**（66 项常规测试 + 1 项压力/资源测试）。

## [1.2.1] - 2026-09-08

### 🏝️ 灵动岛交互体验与紧凑态形态优化 (Island Adaptive Interaction)
- **15 秒无操作自动收缩 (15s Idle Collapse)**：用户无鼠标交互持续 15 秒后，灵动岛自动优雅折叠为紧凑胶囊态，释放屏幕视野；鼠标移入即刻唤醒恢复原窥视态（Peek）。
- **移出区域即时防抖折叠 (Mouse-Leave Auto Collapse)**：悬停/展开状态下，鼠标移出灵动岛区域 120ms 防抖后自动收缩复位；支持按下 `Esc` 键快捷折叠展开面板。
- **双 Agent 紧凑态死区收拢**：解决双 Agent 激活时紧凑态中间无用黑色死区的问题，收窄死区为 16px，单双 Agent 紧凑态外框宽度严格统一（92px / 300px）。
- **常驻用量数据显示保持**：开启“常驻显示用量”时，紧凑态下依旧保留显示左右 Agent 头部用量药丸（300px），避免收缩后用量数据丢失；默认与悬停保持 484px 完整宽度。

### 📊 报告窗口全量本地化与健壮性提升 (ReportWindow & Localization)
- **全量多语言支持 (Full L10n)**：补齐 `ReportWindow` 所有中英文对照字典（窗口标题、聚合周期、指标标签、图谱提示、文件保存与复制状态）。
- **文件保存反馈准确性**：导出文件后精准提示“已保存到文件（Saved to file: {path}）”，不再误显示“已复制到剪贴板”。
- **剪贴板抗争锁重试**：剪贴板复制逻辑增加 3 次指数重试机制，避免 Windows 系统剪贴板偶发争锁引发异常。
- **单例窗口激活管理**：唤起报告窗口时检测 `.IsVisible`，已存在时自动解除最小化并激活置顶，避免重复实例。

### 🧪 并发测试稳定性与沙箱隔离 (Test Harness & Concurrency)
- **调度队列冲刷与去重竞态消除**：优化 `StoreDispatcherConcurrencyTests` 调度队列冲刷与并发排队，消除高并发去重扫描下的竞态，54 项测试全部稳定通过。
- **xUnit 串行执行集合隔离**：采用 `[Collection("IslandSettingsTests")]` 避免跨测试并发修改配置与静态上下文，全量配置沙箱化，避免测试污染生产配置。

## [1.2.0] - 2026-09-06

### 🏗️ 现代企业级 C# 架构重构 (Enterprise Modernization)
- **以 DI 为主的运行时组合**：移除业务 Store 的全局 `.Shared` 单例模式，并迁移至 `Microsoft.Extensions.DependencyInjection` 容器进行服务注册与解析；少量平台工具和 UI 兼容访问入口仍保留静态实现。
- **Generic Host 应用宿主**：基于 `Microsoft.Extensions.Hosting` 统一编排应用生命周期，将轮询任务统一改造为基于 `PeriodicTimer` 的托管后台工作者（`ActivityMonitoringWorker`, `UsagePollingWorker`, `CostAggregationWorker`, `UpdateCheckWorker`）。
- **弹性网络管线与离线快速失败**：接入 `IHttpClientFactory` 与 Polly 弹性策略（指数退避重试、断路器机制），配合 `INetworkConnectivityService` 实现离线状态下的毫秒级快速失败，避免后台网络挂起。
- **强类型配置模式**：引入 `IOptions<T>` 与 `IOptionsMonitor<T>` 响应式配置监听，配合 `AtomicJsonSettingsStorage` 原子化持久化。
- **提供商能力架构**：使用 `IAgentProvider` 与 `IAgentCatalog` 的显式源码注册替代硬编码枚举，Agent 能力显式声明；当前不启用动态 DLL 加载。
- **MVVM 规范化与窗口服务抽象**：规范 View 与 ViewModel（`IslandViewModel`, `SettingsViewModel`, `UsagePageViewModel`, `CostPageViewModel`），抽离 `IWindowService` 与 `IDialogService` 接口，解耦 UI 弹窗与核心逻辑。

### ⚡ 极限负载保障与压力测试 (Stress Testing & Worst-Case I/O)
- **双 Agent 并发上限契约**：针对实际高负荷工作流，引入 `SemaphoreSlim(2, 2)` 严格限制活跃 Agent 跟踪并发度，避免文件扫描抢占磁盘与系统 I/O。
- **1 年（365 天）历史会话最坏场景仿真**：覆盖 730+ 个超大历史会话文件，全盘冷扫描耗时 < 30ms，热轮询耗时 < 1ms，CPU 增量峰值 < 5%。
- **一键压测体验工具**：新增 `Launch-StressUI.bat` 与 `scripts/Launch-StressTestUI.ps1`，自动挂载独立沙箱数据目录并自动呼出灵动岛与仪表盘。

### 🎨 开箱即用体验优化 (Fresh-Install Defaults)
- 额度显示默认开启**剩余额度（Remaining Quota）**，优先展示剩余额度；
- 费用统计显示默认**关闭（Off）**，优先保障隐私与离线纯净体验；
- 视觉外观默认模式**跟随模型（Follow Model）**，自动匹配 Agent 品牌主色调；
- 灵动岛胶囊条默认开启**常驻显示用量（Always Show Usage）**。

### 🧪 自动化测试体系全面跃升 (Test Automation)
- 发布时自动化测试套件为 **42 项测试**，涵盖单元测试、STA 线程 UI 渲染测试、MVVM ViewModel 交互测试、网络断路器与 1 年极限并发基准测试；后续主分支已扩展为 56 项，详见上方当前基线。
- 引入沙箱数据隔离机制，杜绝测试过程与运行中实例的数据互扰。

## [1.0.1] - 2026-09-05

### 🚀 性能与内存深度优化 (Performance & Memory)
- **Provider 禁用后物理内存深度回收**：
  - 新增 `MemoryReclaimer` 异步防抖内存回收器，支持 Gen 2 + LOH（大对象堆）碎片整理与压缩。
  - 调用 Windows 内核 API `SetProcessWorkingSetSize`，在 Provider 禁用后将未引用的物理工作集（Working Set）迅速退还给操作系统，空闲常驻内存从 ~200MB 回落至 100MB 以内。
  - 修复 `SessionScanner` 在禁用 Codex 时漏清理静态元数据缓存 `CodexMetaCache` 的问题。
- **会话与日志扫描分配优化**：
  - `SessionScanner` 引入 `ArrayPool` 及逆向流切片，减少读取超长 JSONL 时的临时缓冲区分配与尾部扫描开销。
  - 引入 `CodexMetaCache` 静态元数据指纹缓存，避免重复解析未变动会话。
- **费用解析图谱常驻缓存**：
  - `LogParseCache` 对未改动文件复用已解析的 `TokenEvent` 对象图谱，避免多周期轮询重复解析 JSON 和重复创建事件对象。
- **托盘渲染与图标资源优化**：
  - `TrayIconRenderer` 按视觉状态缓存 `Icon`，减少轮询定时器在状态不变时的重复 GDI+ 渲染。
  - 预渲染 32×32 产品 Logo 底图，缩短托盘图标渲染路径。
- **灵动岛悬浮检测加速**：
  - `IslandWindow` 增加 Silhouette 屏幕空间 AABB 包围盒快速粗筛，过滤无效的复杂几何命中测试，减少鼠标移动时不必要的命中计算。

### 🧪 测试与质量保证 (Tests & QA)
- 新增 `PerformanceOptimizationTests`，包含元数据流解析、包围盒命中、托盘状态键及内存回收与缓存淘汰等全面测试用例。
- 完整 27 套测试套件全部通过（ALL GREEN）。
