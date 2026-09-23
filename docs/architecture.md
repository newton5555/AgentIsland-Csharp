# Architecture

> 本文描述当前实现。后台职责重构见 [未来计划](future-plan.md)；P0 契约见 [p0-baseline.md](p0-baseline.md)。`AgentMonitoring.Host` 已提供仅 loopback 的本机只读 API；完整多 Agent 采集和订阅仍在迁移中。

## 依赖方向

```text
AgentMonitoring.Host (headless, loopback HTTP API)
└── AgentMonitoring ──> AgentMonitoring.Core, AgentIsland.Providers
    当前运行采集：Codex 本地消费

AgentIsland (WPF / Generic Host)
├── AgentMonitoring ──────────> AgentMonitoring.Core
├── AgentIsland.Core ─────────> AgentMonitoring.Core
├── AgentIsland.Providers ────> AgentMonitoring.Core
└── AgentIsland.Windows ──────> AgentIsland.Core, AgentMonitoring.Core
```

`AgentMonitoring.Core` 与 `AgentIsland.Core` 均不引用 WPF、WinForms、Registry 或 Win32。消费查询契约使用 `AgentKey`。Codex 消费由 `CodexConsumptionCollector` 写入 `ConsumptionStore`；本机 API 提供版本化只读 JSON 查询和采集健康状态。概览、消费汇总和报告走 `IMonitoringQuery`。额度/余额快照按 `AccountRef` 隔离；活动与提醒以 `AgentKey` 为身份。当前 Headless Host 尚未注册 WPF 中的 Provider、活动、额度或余额采集器，完整状态监控仍在迁移中。无窗口入口见 [p6-headless.md](p6-headless.md)。Windows 目录解析、进程/窗口控制、托盘、更新替换和屏幕定位属于 `AgentIsland.Windows`；布局、动画和交互属于 WPF 项目。`DisplayProvider` 只用于岛体槽位展示，经适配层换成 `AgentKey`。

WPF 项目内部也保持同样的二分：`AgentIsland.UI` 是窗口/控件/视觉层，`AgentIsland.Backend.*` 是桌面进程内的后台编排层。这里的 Backend 不是 Web 服务器。

## 分类

### 跨平台候选

- `AgentKey`、`AgentDescriptor`、`AgentCapabilities`
- `ActivityState`、`ScannedSession`、`AppUsage`、Cost 数据结构
- JSON/JSONL 解析、会话状态判定、格式化、费用聚合、缓存策略
- Claude/Codex/Grok/Cursor 费用解析、Antigravity/Grok/Usage payload 解析
- HTTP/文件读取等接口的契约（具体路径与凭据解析由平台层注入）

### Windows/WPF

- `IslandPaths` 以及 `%APPDATA%` / `%LOCALAPPDATA%` 约定
- WPF Window、Control、Storyboard、MediaPlayer、Dispatcher
- tray `NotifyIcon`、屏幕与窗口位置、前台窗口、Win32 P/Invoke
- Windows 原生 SQLite、启动项、更新安装与进程启动
- `CursorDatabaseReader` 对 `winsqlite3.dll` 的只读封装

## Agent 能力

Agent 不要求实现一套固定的“5 小时 + 7 天”数据模型。目录中的 `AgentCapabilities` 只声明该 Agent 实际支持的能力；具体 quota 形状、账单周期和会话边界由 Provider 自己解释。新增 Agent 的最小路径是：

1. 增加一个稳定 key 和 descriptor；
2. 增加对应 Provider 文件夹与可用的能力适配器；
3. 在组合根接入适配器；
4. 为解析/聚合规则补测试。

后台查询、活动快照和采集已按 `AgentKey` 索引，未知 key 可以进入 `GetOverviews()`、活动存储和 `AgentRuntime`。岛体两个槽位仍使用 `DisplayProvider`：要出现在槽位上仍需扩展该枚举、`Parse/All`、可见性及 UI 映射；桌面提醒弹窗还需 `TriggerTool`。

当前不引入动态 DLL 插件加载，也不把每个小功能拆成独立项目；新增 Agent 先以源码内置模块形式接入：在 WPF 组合根注册 `IAgentProvider`，并在 `BuiltInAgentCatalog` 注册匹配的描述符/模块，等真实需求出现再演进插件机制。
