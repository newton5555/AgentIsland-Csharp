# Migration Notes

## 与 Agent Island 2.1.2 的关系

本仓库是基于 Agent Island 2.1.2 的独立 Windows fork。2.1.2 提供产品交互、状态语义、报告结构和视觉方向的参考；本仓库没有继续携带 macOS 的 Xcode、SwiftUI、Sparkle 或 Keychain 运行时，而是在同一产品目标下重做 Windows 宿主。

相对 2.1.2，当前 2.2.0 的重要变化是：

1. **平台替换**：SwiftUI/macOS 宿主替换为 .NET 8/WPF；路径、托盘、开机启动、Win32 窗口跳转和 SQLite 访问均由 Windows 层负责。
2. **模块化**：跨平台模型、聚合和规则进入 `AgentIsland.Core`，Provider 解析进入 `AgentIsland.Providers`，Windows 能力进入 `AgentIsland.Windows`，WPF 只负责界面和组合。
3. **Agent 扩展**：除 Claude/Codex 外接入 Antigravity、Grok、Cursor，并加入 DeepSeek Harness 的本地 `.dsh` Token/活动统计与官方余额接口；每个 Agent 按实际能力声明用量、成本、活动和导航能力。
4. **统计与视觉**：把本地 Token/费用聚合、周/月报告、对决素材、Provider 色彩和工作态动画接入 Windows UI；不支持的官方数据保持为空或明确标注，不用 0 代替。
5. **日报与明细**：新增按本地日聚合的日报卡，提供 24 小时 Token 脉冲、日环比、缓存命中/节省、Agent→Model 两级明细及日报 PNG 导出；数据仍只来自本机账本。
6. **Antigravity 模型识别**：补齐动态 `MODEL_PLACEHOLDER_Mxxx` 代号的模型归一化与价格表，并让缓存读取量参与日报成本节省计算。

## 已完成

1. 从旧仓库的 `windows/` 提取到独立顶层目录，清理 macOS 工程和资源残留。
2. 新建 Core、Providers、Windows、WPF 四个正式项目和一个测试项目。
3. 将无 WPF 依赖的模型、费用聚合、缓存策略和 JSON/JSONL 规则迁入 Core。
4. 将 Claude/Codex/Grok/Cursor 费用 payload 解析器迁入 Providers。
5. 将 Antigravity/Grok 的纯 quota/billing 解析迁入 Providers。
6. 将 Windows 路径、文件系统容错、进程、启动项、Cursor 对话数据库和 Cursor token 数据库访问集中到 Windows 项目。
7. 将 WPF 项目整理为 `UI/` 与 `Backend/` 两块，并把后台监控命名空间明确为 `AgentIsland.Backend.Monitoring`。
8. 用显式 `BuiltInAgentCatalog` 建立可扩展的 Agent 注册点，并由 WPF 组合根绑定当前活动能力。
9. 测试工程已迁移为标准 xUnit 入口；当前基线为 67 项测试（66 项常规测试 + 1 项压力/资源测试），通过 `dotnet test` 执行。
10. 已完成 Antigravity（`agy`）、DeepSeek Harness（`dsh`）和 Codex 的实际运行验证，确认统计与状态展示链路可用。

## 当前边界

- `AgentIsland.Core`：跨平台模型、规则、聚合和契约；不放 Windows 路径或 WPF 类型。
- `AgentIsland.Providers`：Agent 自己的 payload/transcript 解释；不读取机器固定目录。
- `AgentIsland.Windows`：Windows 目录、SQLite、进程、注册表和文件系统边界。
- `AgentIsland`：WPF UI、宿主后台编排和组合根。

UsageStore、CostStore、ActivityMonitor 仍位于 WPF 宿主的 Backend 命名空间，因为它们直接接触 Dispatcher、设置存储或现有 UI 兼容面；它们已经通过构造函数和接口接入 DI，并由 Generic Host 的后台 Worker 编排。这不是未完成的跨平台拆分，而是小工具保持简单的刻意边界。

## 后续只属于功能演进

1. 新 Agent 按 `AgentKey`、能力描述、Provider 解析器和测试接入。
2. 如果未来需要完全无 UI 宿主，再进一步拆分 WPF Dispatcher、计时器和 UI 兼容面；当前 Store 的构造注入和接口已经存在，不为 IoC 预先增加 `Application` 项目。
3. 设置页等高变页面可在有实际维护收益时逐步转为 XAML + ViewModel；IslandWindow 和高频绘制控件继续保留代码控制。
