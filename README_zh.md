# AgentIsland for Windows (C# .NET 8 + WPF)

[English](README.md) | [简体中文](README_zh.md)

[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat&logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20x64-0078D6?style=flat&logo=windows)](https://www.microsoft.com/windows)
[![Tests](https://img.shields.io/badge/Tests-54%20Passing-brightgreen?style=flat&logo=githubactions)](tests/AgentIsland.Tests)
[![Memory Footprint](https://img.shields.io/badge/Working%20Set-~100MB%20(down%20from%20200MB+)-success?style=flat)](docs/performance.md)
[![License](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**AgentIsland for Windows** 是专为 Windows 平台打造的高性能、低开销 AI 编程 Agent 桌面灵动岛监控工具。常驻屏幕顶端，以流畅的 60fps 弹簧动效实时感知 Claude Code、OpenAI Codex、DeepSeek Harness、Google Antigravity、xAI Grok 及 Cursor 的会话状态、Token 消耗与剩余额度。

---

## 🌟 项目来源与独立重构背景

* **基线来源（Upstream Baseline v2.1.2）**：  
  本项目最初源自上游开源项目 [Agent Island](https://github.com/agent-island/agent-island) 的 **v2.1.2** 版本。原上游项目以 macOS（Swift）为核心，其内部附带的 Windows 端（`windows/`）仅为一个早期的验证性原型。
* **为什么独立立项与现代 C# 完全重写？**  
  在实际 Windows 长期使用中，原版 Windows 原型存在明显的工程瓶颈：大量全局静态单例、强耦合的硬编码枚举、缺乏自动化测试保障、超大日志读取易引发 LOH 内存碎片，且长时间后台挂机后**物理常驻内存经常攀升至 200MB 以上**。  
  本仓库将 Windows 客户端**彻底剥离为独立的顶级 .NET 8 C# 解决方案**，由内而外完成现代化重构，专为 Windows 平台打造极致轻量、高可靠性且以能力契约驱动的 Provider 扩展体系。

---

## 🧩 支持的 Agent 矩阵与 Windows 集成

本工具严格遵循**本地优先（Local-First）**原则：所有会话状态嗅探与 Token 解析均直接在本地执行，您的源代码、Prompt 和会话记录**绝不会上传至任何第三方服务器**；额度查询仅使用本地已有的官方 Key 直接与官方 API 通信。

| Agent Provider | 活动状态监控 | Token 消耗统计 | 额度 / 余额查询 | 历史费用分析 | Windows 本地探测机制 |
| :--- | :---: | :---: | :---: | :---: | :--- |
| **Claude Code** | ✅ 实时探测 | ✅ 完整统计 | ✅ 官方 API 对接 | ✅ 账本分析 | `%USERPROFILE%\.claude\projects` |
| **OpenAI Codex** | ✅ 状态监听 | ✅ 完整统计 | ✅ 会话元数据 | ✅ 账本分析 | `%USERPROFILE%\.codex\sessions` |
| **DeepSeek Harness** | ✅ 网关监听 | ✅ 事件统计 | ✅ 官方余额查询 | ✅ 费用折算 | `%USERPROFILE%\.dsh\sessions` (zstd 压缩流) |
| **Google Antigravity**| ✅ 会话捕获 | ✅ 令牌统计 | ✅ 专用用量抓取 | ✅ 费用账本分析 | `%USERPROFILE%\.gemini` (IDE 与 CLI 会话) |
| **xAI Grok** | ✅ 状态感知 | ✅ 令牌统计 | ✅ 用量查询 | ✅ 费用账本分析 | `%USERPROFILE%\.grok` |
| **Cursor IDE** | ✅ 活动感知 | ✅ 聚合统计 | ✅ 额度状态 | ✅ 费用账本分析 | `%APPDATA%\Cursor\...\state.vscdb` (SQLite WAL) |

---

## 🔌 架构重构红利：能力驱动的 Provider 扩展与新 Agent 接入

原版原型采用硬编码枚举（`enum TriggerTool`）以及遍布各子模块的巨型 `switch-case`，并且假定所有 Agent 必须具备相同的“5 小时额度 + 7 天周期”模型，导致新增或维护 Agent 极其困难。

经过本次架构重构，全面转为**能力契约驱动（Capability-Driven）**的 Provider 体系。当前 Agent 以源码内置模块方式注册，暂不引入动态 DLL 加载：

### 1. 细粒度能力位（`AgentCapabilities`）
不同 Agent 具备不同的能力集，不再强求一致：
- `AgentCapabilities.Activity`：会话状态探测与智能回合提醒（Working / Your Turn / Idle）
- `AgentCapabilities.Usage`：官方周期额度与账户余额抓取
- `AgentCapabilities.Cost`：本地 Token 历史账本与费用统计
- `AgentCapabilities.SessionNavigation`：点击一键跳转对应终端进程或工作目录

### 2. 三大标准化适配器契约
- **`ISessionSensor`**：负责扫描本地会话文件、识别当前 Turn 状态；
- **`IUsageFetcher`**：负责按需抓取官方 Quota / Balance；
- **`ICostLedgerReader`**：负责读取与增量解析本地费用明细。

### 3. 新增 Agent 四步接入法
社区开发者接入一款全新的 AI 编程 Agent（如 Windsurf、Cline、Roo Code、豆包 MarsCode 等）仅需四步；核心 UI 渲染管线、60fps 弹簧动效与后台调度工作者无需修改：

```csharp
// 步骤 1：实现 Provider，并按需实现能力接口
public sealed class DoubaoProvider : IAgentProvider, ISessionSensor, ICostLedgerReader
{
    public AgentDescriptor Descriptor { get; } = new(
        new AgentKey("doubao"), "Doubao MarsCode",
        AgentCapabilities.Activity | AgentCapabilities.Cost,
        "marscode");

    public ValueTask<IReadOnlyList<ScannedSession>> ScanSessionsAsync(...) { ... }
    public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(...) { ... }
}

// 步骤 2：在 WPF 组合根注册 Provider
services.AddSingleton<IAgentProvider, DoubaoProvider>();

// 步骤 3：在 BuiltInAgentCatalog 注册对应描述符/模块
catalog.Register(new BuiltInAgentModule(
    new AgentDescriptor(new AgentKey("doubao"), "Doubao MarsCode",
        AgentCapabilities.Activity | AgentCapabilities.Cost, "marscode")));

// 步骤 4：为日志、会话状态和费用规则补充单元测试
```

---

## ⚡ 针对 Windows 长期挂机的极致性能调优

作为一款需要 24/7 常驻后台的桌面监控工具，“低资源占用”是生命线。重构版针对 Windows 平台特性进行了底层调优：

### 1. Win32 内核物理内存修剪（从 200MB+ 回落至 100MB 左右）
* **痛点**：原版 Windows 原型在开启 Codex 等大日志 Agent 或长期运行后，常驻物理工作集（Working Set）容易**飙升至 200MB 以上**。
* **解决方案**：引入 `MemoryReclaimer` 异步防抖内存回收器。当用户关闭高负荷 Agent、清理历史缓存或应用进入空闲状态时，触发 Gen 2 + LOH 内存整理与压缩，并调用 Win32 内核 API `SetProcessWorkingSetSize` 将未引用的物理页面即刻退还给操作系统。
* **效果**：空闲物理常驻内存从原版的 **200MB+** 显著回落并稳定在 **100MB 左右**。

### 2. 逆向流切片与对象池复用（LOH 零分配）
* **`ArrayPool<byte>` 内存池**：使用逆向流切片仅读取超长 JSONL 日志的尾部有效行，避免产生 >85KB 的大对象进入大对象堆（LOH）引起内存碎片。
* **双层指纹缓存**：引入 `CodexMetaCache` 与 `LogParseCache`。在轮询周期中，文件未发生变动时直接命中指纹，实现**未变动日志 0 重复解析**，热轮询耗时 **< 1ms**。

### 3. 双 Agent 并发门控与 1 年最坏情况压力测试
* **并发限制契约**：在日常开发中，开发者通常最多同时运行 2 个活跃 Agent（如 Claude + Codex 并行）。系统通过 `SemaphoreSlim(2, 2)` 严格限制并发解析深度，杜绝多 Agent 扫描抢占开发者磁盘 I/O。
* **1 年（365 天）历史数据极端测试**：
  - 构造覆盖 365 天、包含 730+ 个密集会话的大型测试数据集；
  - **冷扫描（Cold Scan）**：全盘深扫 1 年历史数据首屏耗时 **< 30ms**；
  - **热扫描（Warm Scan）**：缓存命中后定时轮询耗时 **< 1ms**；
  - 扫描全过程 CPU 增量峰值 **< 5%**，UI 保持 60fps 丝滑无卡顿。
* **一键压测工具**：项目提供 [`Launch-StressUI.bat`](Launch-StressUI.bat) 与 [`scripts/Launch-StressTestUI.ps1`](scripts/Launch-StressTestUI.ps1)，自动挂载沙箱数据并展开灵动岛与监控仪表盘，直观体验极限负载下的流畅表现。

---

## 🏗️ 现代企业级 C# 工程标准

本解决方案严格遵照现代 .NET 企业级架构规范：

* **以 DI 为主的组合**：业务 Store 已移除历史 `.Shared` 单例模式；WPF 宿主通过 `Microsoft.Extensions.DependencyInjection` 注册运行时依赖图，同时保留少量有意存在的平台静态工具和 UI 兼容访问入口。
* **Generic Host 驱动**：使用 `Microsoft.Extensions.Hosting` 统一编排生命周期，后台轮询全量转为基于 `PeriodicTimer` 的托管后台工作者（`ActivityMonitoringWorker`、`UsagePollingWorker`、`CostAggregationWorker`、`UpdateCheckWorker`）。
* **弹性网络管线与离线快速失败**：基于 `IHttpClientFactory` 与 **Polly** 弹性策略（指数退避、熔断断路器），结合 `INetworkConnectivityService` 实现网络断开时的毫秒级快速失败，避免后台接口阻塞。
* **强类型配置模式（Options Pattern）**：接入 `IOptions<T>` 与 `IOptionsMonitor<T>` 实现配置响应式热更新，配合 `AtomicJsonSettingsStorage` 实现原子化安全持久化。
* **服务抽象解耦**：抽离 `IWindowService` 与 `IDialogService`，View 与 ViewModel 严格解耦，大幅提升 UI 可测试性。

---

## 🧪 自动化测试与质量保障

项目拥有完善的自动化测试套件，全面覆盖核心逻辑、并发安全与 UI 线程交互：

* **54 项测试用例全部通过（ALL GREEN）**：其中 53 项常规测试，另有 1 项压力/资源测试；
* **测试覆盖范围**：涵盖领域计算、逆向流解析、断路器熔断机制、MVVM 交互、STA UI 线程渲染以及 1 年历史数据极限压测；
* **数据沙箱隔离**：测试执行时自动隔离至独立临时目录，杜绝与本地实际运行数据互扰。

运行测试命令：
```powershell
dotnet test AgentIsland.sln
```

---

## 🛠️ 快速上手与运行指南

### 1. 普通用户（免安装绿色运行）
1. 前往本仓库的 **[Releases](../../releases)** 页面；
2. 下载最新的发布包 `AgentIsland-x.x.x-win-x64.zip`；
3. 解压至任意目录，双击运行 `AgentIsland.exe` 即可（自包含独立执行文件，无需预装 .NET 运行时）。

### 2. 开发者从源码构建

#### 环境要求
* Windows 10 / 11 x64
* [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

#### 常用命令

```powershell
# 1. 还原依赖并编译解决方案
dotnet build AgentIsland.sln

# 2. 运行完整自动化测试套件（54 项：53 项常规 + 1 项压力/资源）
dotnet test AgentIsland.sln

# 3. 运行 1 年极限历史数据压测 UI
.\Launch-StressUI.bat

# 4. 本地发布自包含独立绿色可执行文件
.\build.ps1 -Runtime win-x64 -Version 1.2.1
```

编译输出的便携绿色包将位于 `dist/AgentIsland-1.2.1-win-x64.zip`。

---

## 📂 解决方案分层架构

```text
AgentIsland-Csharp/
├─ src/
│  ├─ AgentIsland.Core/         # 跨平台领域模型、AgentCapabilities、通用计算契约
│  ├─ AgentIsland.Providers/    # Provider 账本/用量解析器、内置 Agent 注册目录
│  ├─ AgentIsland.Windows/      # Windows 路径解析、winsqlite3.dll 驱动、内核内存修剪 API
│  └─ AgentIsland/              # WPF 宿主、灵动岛悬浮窗、MVVM ViewModel、系统托盘与仪表盘
├─ tests/
│  └─ AgentIsland.Tests/        # 54 项自动化测试（53 项常规 + 1 项压力/资源测试）
├─ scripts/
│  ├─ Launch-StressTestUI.ps1   # 1 年最坏场景沙箱压测启动脚本
│  └─ Measure-ProcessResources.ps1 # 进程 CPU 与物理工作集实时采样脚本
├─ Launch-StressUI.bat          # 一键拉起 1 年压测 UI 批处理
├─ build.ps1                    # 自包含二进制打包打包脚本
└─ AgentIsland.sln              # 解决方案入口
```

---

## 📄 许可证与开源致谢

* 本项目基于与上游一致的开源许可协议发布，详情参阅 [LICENSE](LICENSE) 文件。
* 衷心致谢上游项目 [Agent Island](https://github.com/agent-island/agent-island) 及其创作者在 macOS 原生灵动岛交互与 AI Agent 桌面体验上的杰出构想！
