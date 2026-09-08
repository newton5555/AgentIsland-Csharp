# Changelog

All notable changes to AgentIsland for Windows will be documented in this file.

> 版本条目保留各版本发布时的历史快照。当前主分支的测试数量、能力矩阵和架构边界请以根目录 README 与 `docs/architecture.md` 为准；当前测试基线为 54 项（53 项常规测试 + 1 项压力/资源测试）。

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
- 发布时自动化测试套件为 **42 项测试**，涵盖单元测试、STA 线程 UI 渲染测试、MVVM ViewModel 交互测试、网络断路器与 1 年极限并发基准测试；后续主分支已扩展为 54 项，详见上方当前基线。
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
