# Migration Notes

## 已完成

1. 从旧仓库的 `windows/` 提取到独立顶层目录，清理 macOS 工程和资源残留。
2. 新建 Core、Providers、Windows、WPF 四个正式项目和一个测试项目。
3. 将无 WPF 依赖的模型、费用聚合、缓存策略和 JSON/JSONL 规则迁入 Core。
4. 将 Claude/Codex/Grok/Cursor 费用 payload 解析器迁入 Providers。
5. 将 Antigravity/Grok 的纯 quota/billing 解析迁入 Providers。
6. 将 Windows 路径、文件系统容错、进程、启动项、Cursor 对话数据库和 Cursor token 数据库访问集中到 Windows 项目。
7. 将 WPF 项目整理为 `UI/` 与 `Backend/` 两块，并把后台监控命名空间明确为 `AgentIsland.Backend.Monitoring`。
8. 用显式 `BuiltInAgentCatalog` 建立可扩展的 Agent 注册点，并由 WPF 组合根绑定当前活动能力。
9. 保留一个兼容测试入口，Debug/Release 构建和现有测试均通过。

## 当前边界

- `AgentIsland.Core`：跨平台模型、规则、聚合和契约；不放 Windows 路径或 WPF 类型。
- `AgentIsland.Providers`：Agent 自己的 payload/transcript 解释；不读取机器固定目录。
- `AgentIsland.Windows`：Windows 目录、SQLite、进程、注册表和文件系统边界。
- `AgentIsland`：WPF UI、宿主后台编排和组合根。

UsageStore、CostStore、ActivityMonitor 仍由 WPF 宿主管理，因为它们直接绑定 Dispatcher、设置存储或现有 UI 兼容面；这不是未完成的项目拆分，而是小工具保持简单的刻意边界。

## 后续只属于功能演进

1. 新 Agent 按 `AgentKey`、能力描述、Provider 解析器和测试接入。
2. 如果未来需要无 UI 宿主，再为后台 Store 引入构造注入和接口；当前不为 IoC 预先增加 `Application` 项目。
3. 设置页等高变页面可在有实际维护收益时逐步转为 XAML + ViewModel；IslandWindow 和高频绘制控件继续保留代码控制。
