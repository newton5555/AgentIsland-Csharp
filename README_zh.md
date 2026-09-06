# AgentIsland for Windows (C# .NET 8 + WPF)

[English](README.md) | [简体中文](README_zh.md)

AgentIsland for Windows 是专为 Windows 平台打造的 AI 编程 Agent 桌面轻量级灵动岛监控工具。常驻屏幕顶端，以极低功耗与内存占用实时展示 Claude Code、OpenAI Codex、DeepSeek Harness、Google Antigravity、xAI Grok 及 Cursor 的会话状态、Token 消耗、剩余额度与用量统计。

---

## 🌟 项目来源与演进背景

* **基线来源（Baseline v2.1.2）**：
  本项目最初源自上游 [Agent Island](https://github.com/agent-island/agent-island) 的 **v2.1.2** 版本。原仓库为一个以 macOS（Swift）为主、包含早期 Windows 子目录（`windows/`）的原型项目。
* **现代 C# 完全重构**：
  本仓库将原有的 Windows 原型彻底剥离为**独立的顶级 .NET 8 C# 解决方案**。针对原版存在的平台耦合、硬编码枚举、静态单例泛滥、缺乏测试及内存泄漏隐患，进行了**由内而外、覆盖全层级**的企业级现代 C# 重构，打造具备极致性能与高可靠性的 Windows 原生桌面体验。

---

## 🚀 核心演进历程与架构重塑

根据项目实际演进与技术迭代情况，本仓库完成了以下关键阶段的重构与技术突破：

### 1. 架构分层解耦与模块化体系
彻底重组原工程单一混乱的代码结构，清晰划分为 4 大职责层级：
* **`AgentIsland.Core`**：跨平台通用的业务领域模型、Token 计算规则、能力契约与枚举定义。
* **`AgentIsland.Providers`**：各 Agent 适配插件，负责本地会话发现、增量日志解析与用量/费用抓取。
* **`AgentIsland.Windows`**：Windows 本地化支持层，包含跨进程路径解析、Cursor 全局 SQLite 数据库读取及 Windows 内核内存管理 API。
* **`AgentIsland`**：WPF 宿主应用，包含灵动岛悬浮窗口（`IslandWindow`）、设置与用量仪表盘、托盘图标交互及后台工作者。

### 2. 现代企业级 C# 10 阶段深度现代化
为了实现完全可测试、可扩展的现代企业级架构，完成了 10 个阶段的重构：
1. **会话传感器契约化**：解耦各 Agent 的状态嗅探，建立标准 `ISessionSensor`、`IUsageFetcher`、`ICostLedgerReader` 抽象契约。
2. **Generic Host 驱动**：引入 `Microsoft.Extensions.Hosting`，使用宿主（`IHost`）统一编排生命周期，引入原子化 JSON 存储（`AtomicJsonSettingsStorage`）。
3. **MVVM 模式规范**：引入基于 CommunityToolkit 规范的 ViewModel 分离，同时保留灵动岛高频 60fps 弹簧动画的原生渲染管线。
4. **现代化测试套件与 STA 运行器**：建立独立的 xUnit 测试工程与 `StaFact` 调度机制，支持全自动化测试验证。
5. **纯依赖注入（100% Pure DI）**：彻底铲除全局静态单例（`_instance` / `Shared`），全面接入 `Microsoft.Extensions.DependencyInjection` 容器。
6. **提供商插件架构（Plugin Architecture）**：以 `IAgentProvider` 插件体系与 `IAgentCatalog` 动态注册中心替代硬编码枚举，新增 Agent 零侵入。
7. **后台托管服务机制**：将后台轮询统一重构为基于 `PeriodicTimer` 的 `BackgroundService`（活动状态监听、用量抓取、费用汇总与版本更新检测）。
8. **窗口与对话框抽象（Service Abstraction）**：抽取 `IWindowService` 与 `IDialogService`，解耦 UI 弹窗与核心逻辑，大幅提高可测试性。
9. **强类型配置模式（Options Pattern）**：接入 `IOptions<T>` 与 `IOptionsMonitor<T>`，实现设置变动的响应式热更新。
10. **弹性网络通信管线**：集成 `IHttpClientFactory` 与 **Polly** 弹性策略（指数退避重试、断路器），配合系统网络连通性服务（`INetworkConnectivityService`）实现断网快速失败（Offline Fast-Fail），避免后台网络阻塞。

### 3. v1.0.1 极致性能与内核级物理内存回收
针对长时间后台挂机可能存在的内存滞留问题进行了底层调优：
* **Windows 内核级物理内存修剪**：新增 `MemoryReclaimer`，在关闭高负荷 Agent（如 Codex）或清理历史缓存后，经过 600ms 异步防抖触发 Gen 2 + LOH 碎片压缩，并调用 Windows 内核 API `SetProcessWorkingSetSize` 将未引用的物理页面立即归还操作系统，使空闲常驻内存平滑稳定在 **100MB 以内**。
* **逆向流切片与对象池复用**：`SessionScanner` 采用 `ArrayPool<byte>` 与逆向流切片读取超长 JSONL 日志，避免 LOH 大对象堆分配；引入 `CodexMetaCache` 与 `LogParseCache` 静态指纹比对，未变动日志零重复解析。
* **AABB 屏幕空间粗筛**：在灵动岛复杂几何命中判定前增加快速包围盒判断，降低鼠标滑过时的 CPU 消耗。

### 4. 真实上限：最大双 Agent 并发与 1 年最坏场景压力测试
针对实际工作流中的极限负载建立了专属的基准与压力测试体系：
* **双 Agent 跟踪上限契约**：在日常高强度编程中，开发者通常同时运行最多 2 个协同 Agent（如 Claude + Codex 并行）。系统通过 `SemaphoreSlim(2, 2)` 严格限制并发会话解析，避免文件扫描抢占系统 I/O。
* **1 年历史数据最坏场景（Worst-Case I/O）**：
  - 生成覆盖 365 天、包含 730+ 个超大历史会话与转录日志的测试数据集；
  - **冷扫描（Cold Scan）**：全盘扫描 1 年数据首屏耗时 **< 30ms**；
  - **热扫描（Warm Scan）**：缓存命中后定时轮询耗时 **< 1ms**；
  - 扫描期间 CPU 增量峰值 **< 5%**，内存波动保持在安全阈值内。
* **一键压测 UI 启动器**：提供 [`Launch-StressUI.bat`](Launch-StressUI.bat) 与 [`scripts/Launch-StressTestUI.ps1`](scripts/Launch-StressTestUI.ps1)，自动注入独立沙箱数据目录，自动展开灵动岛与屏幕正中央的用量监控仪表盘，直观体验极限负载下的流畅表现。

### 5. 开箱即用配置优化（Fresh-Install Defaults）
全新安装后预置符合高频开发者习惯的默认体验：
* **额度显示**：默认开启“剩余额度（Remaining）”，优先展示剩余可用额度；
* **费用统计**：默认关闭（离线隐私优先）；
* **视觉模式**：默认跟随模型（Follow Model，自动切换对应 Agent 品牌主题）；
* **常驻用量**：默认开启（Always Show Usage，胶囊条常驻显示百分比）。

---

## 🧩 支持的 Agent 矩阵与能力

| Agent Provider | 活动状态监控 | Token 消耗统计 | 额度/余额查询 | 历史费用统计 | 本地日志路径机制 |
| :--- | :---: | :---: | :---: | :---: | :--- |
| **Claude Code** | ✅ 实时探测 | ✅ 完整统计 | ✅ 官方 API 对接 | ✅ 账本分析 | `%USERPROFILE%\.claude\projects` |
| **OpenAI Codex** | ✅ 状态监听 | ✅ 完整统计 | ✅ 会话元数据 | ✅ 账本分析 | `%USERPROFILE%\.codex\sessions` |
| **DeepSeek Harness** | ✅ 网关监听 | ✅ 事件统计 | ✅ 官方余额查询 | ✅ 费用折算 | `%USERPROFILE%\.dsh\sessions` (zstd 压缩流) |
| **Google Antigravity**| ✅ 会话捕获 | ✅ 令牌统计 | ➖ (跟随平台) | ➖ | `%USERPROFILE%\.antigravity` |
| **xAI Grok** | ✅ 状态感知 | ✅ 令牌统计 | ✅ 用量查询 | ➖ | `%USERPROFILE%\.grok` |
| **Cursor IDE** | ✅ 活动感知 | ✅ 聚合统计 | ✅ 额度状态 | ➖ | `%APPDATA%\Cursor\...\state.vscdb` (SQLite) |

> **数据安全承诺**：AgentIsland 严格采用“本地优先（Local-First）”策略。代码、本地转录与对话内容绝不会上传至任何第三方服务器；额度查询仅使用本地已有 Token/Key 走官方通道交互。

---

## 📂 项目结构概览

```text
AgentIsland-Csharp/
├─ src/
│  ├─ AgentIsland.Core/         # 领域模型、AgentCatalog 契约、通用计算逻辑
│  ├─ AgentIsland.Providers/    # 6 大 Agent 适配器、日志解析器、插件注册
│  ├─ AgentIsland.Windows/      # Windows 路径探测、SQLite 驱动、内核内存管理
│  └─ AgentIsland/              # WPF 宿主、灵动岛悬浮窗、MVVM ViewModel、设置面板
├─ tests/
│  └─ AgentIsland.Tests/        # 42 项全自动化单元、STA UI 与 1 年压力测试套件
├─ scripts/
│  ├─ Launch-StressTestUI.ps1   # 1 年历史数据最坏场景 UI 启动脚本
│  └─ Measure-ProcessResources.ps1 # CPU 与物理内存实时分析脚本
├─ Launch-StressUI.bat          # 一键拉起 1 年压力测试 UI 批处理
├─ build.ps1                    # 自包含二进制打包打包脚本
└─ AgentIsland.sln              # Visual Studio 解决方案
```

---

## 🛠️ 构建、运行与测试

### 环境要求
* Windows 10 / 11 x64
* [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 常用命令

```powershell
# 1. 编译解决方案
dotnet build AgentIsland.sln

# 2. 运行完整测试套件（42 项测试用例全部通过）
dotnet test AgentIsland.sln

# 3. 运行 1 年压力测试 UI（屏幕上方展开灵动岛，中央弹出仪表盘）
.\Launch-StressUI.bat

# 4. 本地打包发布独立执行文件
.\build.ps1 -Runtime win-x64 -Version 1.0.1
```

发布产物将输出在 `dist/AgentIsland-1.0.1-win-x64.zip`。

---

## 📄 开源许可证

本项目遵循与上游项目一致的开源许可协议。详情请参阅 [LICENSE](LICENSE) 文件。
