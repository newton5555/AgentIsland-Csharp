# AgentIsland.Avalonia (P0 最小工程骨架)

本项目是 AgentIsland 跨平台（Windows / Linux）Avalonia 迁移计划的 **P0 验证工程骨架**。遵照 [Avalonia 开发计划](../../docs/avalonia-plan.md) 第一阶段目标建立，用于验证 Avalonia 11 在目标环境上的透明窗口、圆角、拖拽与交互能力。

---

## 技术选型与包版本

- **目标框架**：`net8.0`（纯跨平台目标，不包含 `-windows` 专有绑定）
- **Avalonia 版本**：`11.3.0`
- **依赖项**：
  - `Avalonia` (11.3.0)
  - `Avalonia.Desktop` (11.3.0)
  - `Avalonia.Themes.Fluent` (11.3.0)
  - `Avalonia.Fonts.Inter` (11.3.0)
  - `Avalonia.Diagnostics` (11.3.0, Debug 条件引用)
  - `AgentIsland.Core` (net8.0 项目引用)
  - `AgentIsland.Runtime` (net8.0 项目引用)

---

## 运行与构建方式

在仓库根目录下通过命令行直接构建与运行：

```powershell
# 构建项目
dotnet build src/AgentIsland.Avalonia/AgentIsland.Avalonia.csproj

# 运行悬浮岛窗口（默认 Mock 演示模式）
dotnet run --project src/AgentIsland.Avalonia/AgentIsland.Avalonia.csproj

# 运行真实本地数据闭环诊断模式（--live：扫描本地 Codex 与 DeepSeek Harness 日志并启动 15 秒轮询）
dotnet run --project src/AgentIsland.Avalonia/AgentIsland.Avalonia.csproj -- --live

# 运行带官方 DeepSeek 余额的诊断模式（显式联网；需要 DEEPSEEK_API_KEY 或 ~/.dsh/.credentials.yaml）
dotnet run --project src/AgentIsland.Avalonia/AgentIsland.Avalonia.csproj -- --live-online

# 运行 RuntimeIslandBinder 单向绑定与 Live 构造契约验证
dotnet run --project src/AgentIsland.Avalonia/AgentIsland.Avalonia.csproj -c Release -- --verify

# 运行 Runtime 跨平台契约回归
dotnet run --project tests/AgentIsland.Runtime.Tests/AgentIsland.Runtime.Tests.csproj
```

---

## P0 / P1 / P2 / P3 前半段 已实现能力

1. **悬浮岛视觉基线**：
   - 无边框透明窗口（`SystemDecorations="None"`, `TransparencyLevelHint="Transparent"`）
   - 深色岛体剪影（`#020203` 胶囊圆角，16px CornerRadius，柔和外阴影）
   - 沿用 macOS/WPF 视觉语义色彩（Codex 紫 `#7B61FF`、LiveTeal 绿色 `#3DD68C`、灰阶文本体系）
2. **纯视觉状态模型与绑定（P3 前半段）**：
   - `IslandViewModel` / `ProviderSlotViewModel`：纯视觉状态数据模型，支持 0、1、2 个 Provider 槽位。
   - `ProviderSlotStatus` 支持 `Idle`、`Working`、`NeedsYou`、`Error` 四种状态。
   - `MainWindow.axaml` 采用 `ItemsControl` + `DataTemplate` 动态渲染槽位，支持展示 Provider 标识、名称、状态指示灯、状态文案、额度标签（`.slot-quota`）、官方余额标签（`.slot-balance`）及独立成本标签（`.slot-cost`）。
   - 提供 AXAML 视觉状态样式（不同边框高亮与指示灯色彩）及平滑过渡。
   - 提供清晰的 Mock 数据工厂方法（`CreateMock()`、`CreateTwoSlotMock()`、`CreateEmptyMock()`），默认启动不访问真实日志、凭据或后台。
   - 单一绑定入口：`MainWindow.ViewModel` 可随时全量替换或局部更新。
3. **基础窗口交互**：
   - **窗口拖动**：鼠标左键按住岛体任意空白区域即可平滑拖动（基于 `BeginMoveDrag`）。
   - **关闭退出**：提供右上角关闭按钮（✕），支持键盘 `Esc` 键直接关闭。
   - **右键菜单**：右键岛体支持“置顶显示切换 (Toggle Topmost)”、“居中窗口 (Center Window)”以及“退出 (Exit)”。
4. **Runtime 单向绑定适配层（P1/P2 边界）与 --live 模式**：
   - `RuntimeIslandBinder`（及别名 `RuntimeIslandController`）：连接 `AgentRuntime` 与 `IslandViewModel` 的单向绑定适配器。**此为展示适配与线程封送层，并非真实数据源**；不持有或扫描凭据与日志，不在构造或 `Attach()` 时自动启动 `RefreshAsync` 或 `RunAsync`（刷新调度由上层生命周期拥有）。
   - 具有显式 `Attach()` / `Detach()` / `Dispose()` 单订阅生命周期，订阅 `AgentRuntime.SnapshotsChanged`，将快照安全封送至 Avalonia UI 线程调度执行，避免跨线程集合修改与订阅泄漏；`Detach()` / `Dispose()` 后事件立即切断更新。
   - **--live 诊断模式**：通过 `dotnet run --project src/AgentIsland.Avalonia/AgentIsland.Avalonia.csproj -- --live` 显式开启；自动创建跨平台 `LocalTokenSources`（Codex 成本、DeepSeek Harness 成本与 DeepSeek Harness 本地活动），经 `AgentRuntime`（同一 Agent 自动通过 `CompositeAgentSnapshotSource` 组合 Cost 与 Activity facets）驱动 `RuntimeIslandBinder`，并启动可取消的 15 秒轮询循环。当本地人类会话在 18 秒内有 `Active` 事件时，dsh 槽位显示 `Working` 并保留已统计的 `CostText`（如 `1.5k tok`）；无近期事件时回到 `Idle`。自动排除 subagent 会话；默认完全离线，不访问凭据或网络余额。窗口关闭或应用退出时，优雅取消并释放全部资源，无未捕获异常。
   - **--live-online 余额模式**：在 `--live` 的本地统计基础上，显式添加官方 `https://api.deepseek.com/user/balance` 源；只读取 `DEEPSEEK_API_KEY` 或 `~/.dsh/.credentials.yaml`，余额以独立金额标签显示，不转成 quota 百分比。请求失败不会清空本地 Token 统计，也不会把失败伪装成零余额。
   - **真实成本与配额严格分离**：绝对禁止将 Cost/Token 数据冒充为 Quota/Balance 百分比。`ProviderSlotViewModel` 提供独立的 `CostText`（及 AXAML `.slot-cost` 独立标签），展示已扫描账本的金额或 Token 数（如 `$12.50` 或 `1.5k tok`）；`QuotaText`（`.slot-quota`）仅在存在真实的 `Usage` 窗口用量时展示百分比。
   - 状态保真：严格遵守 `IslandViewModel` 0~2 槽位约束；支持稳定的 AgentKey 优先级排序；精确区分 `Working`、`NeedsYou`、`Error`、`Stale`、`No Data` 与 `Not Configured`（`Error`/`Stale` 绝不静默成 `Ready`）。

---

## 已验证平台与已知限制

### 已验证平台
- **Windows 10 / 11 (x64)**：.NET 8.0 SDK / Avalonia 11.3.0 编译生成通过，0 警告 0 错误（Debug / Release）。`--verify` 与 `--live` 均可正常执行。

### 已知限制与后续规划
- **默认非凭据/无网络设计**：普通 `--live` 只读本地磁盘日志文件（Codex 与 DeepSeek Harness），不访问凭据、不发起网络调用；需要官方余额时使用显式 `--live-online`，云端额度窗口仍按后续阶段推进。
- **无托盘图标 (TrayIcon)**：保持窗口在任务栏显示，避免在无托盘 Linux 环境中失去窗口入口；托盘适配将在 P5 阶段实施。
- **无全局点击穿透**：透明边缘的操作系统级点击穿透待平台层独立验证。
- **高阶动效**：当前使用轻量 AXAML 状态过渡；蓝鲸游动与彗星环等高频逐帧动效留待后续演进。
