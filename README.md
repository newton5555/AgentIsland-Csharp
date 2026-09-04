# AgentIsland for Windows

这是从 Agent Island 2.1.2 fork 出来的独立 Windows 产品线，使用 .NET 8 + WPF；仓库同时开始建设 Avalonia 跨平台前端。当前 Windows 版本保留 Agent Island 的核心设计与 Agent 监控能力，并针对 Windows 的窗口、托盘、路径和系统集成做了独立实现；它不再依赖 macOS 上游仓库的目录、构建或发布流程。

Agent Island 是一个本地优先的 AI Agent 桌面监控工具，用悬浮岛展示 Claude、Codex、DeepSeek Harness、Antigravity、Grok 和 Cursor 的活动状态、Token 用量、账号额度及周/月统计报告。

## 相对 Agent Island 2.1.2 的主要修改

本项目以 Agent Island 2.1.2 的产品交互、数据契约和视觉方向为基线，重新实现为 Windows 产品线；它不是把 macOS 工程直接换平台编译。1.0.0 主要包含：

- **Windows 独立运行**：改为 .NET 8 + WPF，集中处理 Windows 路径、托盘、注册表开机启动以及 Win32 窗口/进程跳转，不再依赖 macOS 宿主能力。
- **架构重组**：拆成 `Core`、`Providers`、`Windows` 和 WPF 组合根四个项目；用稳定的字符串 `AgentKey`、显式 Agent Catalog 和能力声明替代继续扩展共享枚举。
- **Agent 适配扩展**：保留 Claude/Codex 的核心用量与会话监控，并加入 Antigravity、Grok、Cursor；新增 DeepSeek Harness 的 `.dsh` 压缩事件流 Token/活动统计和官方余额查询。没有实现的能力显示“不支持/暂无数据”，不伪造百分比额度。
- **本地统计与报告**：费用、Token、活动状态、周/月卡和年度热力图均从本机已有记录聚合；支持全部 Token 与输入/输出 Token 两种口径、Provider 专属色彩、对决图和 DeepSeek 余额卡。
- **Windows UI 与可靠性**：设置页改为 XAML 控件体系，补齐 Provider 槽位绑定、动画、托盘状态、原子化偏好写入、单实例保护和崩溃日志。

具体的迁移边界、保留项和暂不拆分的模块见 [`docs/migration.md`](docs/migration.md)。

## 当前验证范围

以下 Agent 已完成实际运行验证，统计与状态展示链路可用：

- Antigravity（`agy`）
- DeepSeek Harness（`dsh`）
- Codex

## 项目结构

```text
src/
├─ AgentIsland.Core       # 跨平台：模型、解析规则、聚合与 Agent 能力契约
├─ AgentIsland.Providers  # Agent 适配器、会话/费用/Usage 解析与显式注册
├─ AgentIsland.Runtime    # 跨平台：快照契约、刷新并发、扫描缓存与成本源骨架
├─ AgentIsland.Windows    # Windows：路径、进程、系统集成
└─ AgentIsland             # WPF 程序：组合根
   ├─ UI                   # 窗口、页面、控件、动画、主题
   └─ Backend              # WPF 宿主后台：Monitoring/Usage/Cost/Alarms/Updates/Settings
src/AgentIsland.Avalonia/  # Avalonia P0 窗口与 Provider 槽位视觉状态（迁移进行中）
assets/                    # 产品 Logo、Agent 图标、引导图、报告图和更新说明图
tests/
├─ AgentIsland.Tests       # WPF/Windows 回归
└─ AgentIsland.Runtime.Tests # Runtime 跨平台契约回归
```

根目录 `assets/` 保留一份与 WPF 运行时资源同名的素材目录，便于设计和产品维护；应用当前继续从 `src/AgentIsland/Assets/` 打包，避免改变既有 pack URI。

`AgentIsland.Application` 没有单独建项目：这是一个小工具，组合根就在 WPF 可执行项目中；后台功能仍然按 Service/Store/Provider 归类，不为了 IoC 这个词额外增加一层空壳。

目录与项目的边界已经落地：`Core` 和 `Providers` 不引用 WPF/WinForms/Win32，`Windows` 集中 Windows 原生能力，`AgentIsland` 负责 UI 和把这些模块组合起来。

## 开发

Visual Studio 可打开 `AgentIsland.sln` 或 `AgentIsland.slnx`；两者包含相同的项目、素材和文档项。

```powershell
dotnet build .\AgentIsland.sln
dotnet run --project .\tests\AgentIsland.Tests\AgentIsland.Tests.csproj
dotnet run --project .\tests\AgentIsland.Runtime.Tests\AgentIsland.Runtime.Tests.csproj
dotnet run --project .\src\AgentIsland.Avalonia\AgentIsland.Avalonia.csproj
dotnet run --project .\src\AgentIsland.Avalonia\AgentIsland.Avalonia.csproj -- --verify
# Avalonia 本地真实数据（默认离线）
dotnet run --project .\src\AgentIsland.Avalonia\AgentIsland.Avalonia.csproj -- --live
# Avalonia 本地数据 + 官方 DeepSeek 余额（显式联网）
dotnet run --project .\src\AgentIsland.Avalonia\AgentIsland.Avalonia.csproj -- --live-online
```

Avalonia 迁移的阶段计划和当前落地状态见 [`docs/avalonia-plan.md`](docs/avalonia-plan.md)。现阶段 Avalonia 仍是独立的验证前端，Runtime 已完成无 UI 的快照/刷新骨架，真实 Codex/dsh 数据闭环和 Linux 真机验证尚未替代 WPF 默认前端。

发布：

```powershell
.\build.ps1 -Runtime win-x64
```

## 新 Agent 的扩展规则

Agent 使用稳定的字符串 `AgentKey`，而不是继续扩展共享枚举。新增 Agent 先在 `AgentIsland.Providers/BuiltIn/BuiltInAgentCatalog.cs` 注册描述，再按能力增加自己的 Usage、Activity、Cost 或导航适配器。没有实现的能力不返回“伪造的 0”，由 UI 显示“不支持/暂无数据”。

当前运行时仍保留必要的 `TriggerTool` 兼容入口，以读取既有配置和维持现有 UI；新增 Agent 从稳定 key/catalog 开始，不需要修改 macOS 上游或新增独立服务器项目。
