# AgentIsland for Windows

这是独立维护的 Windows 产品线，使用 .NET 8 + WPF。它不再依赖 macOS 上游仓库的目录、构建或发布流程。

## 项目结构

```text
src/
├─ AgentIsland.Core       # 跨平台：模型、解析规则、聚合与 Agent 能力契约
├─ AgentIsland.Providers  # Agent 适配器、会话/费用/Usage 解析与显式注册
├─ AgentIsland.Windows    # Windows：路径、进程、系统集成
└─ AgentIsland             # WPF 程序：组合根
   ├─ UI                   # 窗口、页面、控件、动画、主题
   └─ Backend              # WPF 宿主后台：Monitoring/Usage/Cost/Alarms/Updates/Settings
assets/                    # 产品 Logo、Agent 图标、引导图、报告图和更新说明图
tests/
└─ AgentIsland.Tests      # 当前保留一个测试入口，按目录区分测试类型
```

根目录 `assets/` 保留一份与 WPF 运行时资源同名的素材目录，便于设计和产品维护；应用当前继续从 `src/AgentIsland/Assets/` 打包，避免改变既有 pack URI。

`AgentIsland.Application` 没有单独建项目：这是一个小工具，组合根就在 WPF 可执行项目中；后台功能仍然按 Service/Store/Provider 归类，不为了 IoC 这个词额外增加一层空壳。

目录与项目的边界已经落地：`Core` 和 `Providers` 不引用 WPF/WinForms/Win32，`Windows` 集中 Windows 原生能力，`AgentIsland` 负责 UI 和把这些模块组合起来。

## 开发

Visual Studio 可打开 `AgentIsland.sln` 或 `AgentIsland.slnx`；两者包含相同的项目、素材和文档项。

```powershell
dotnet build .\AgentIsland.sln
dotnet run --project .\tests\AgentIsland.Tests\AgentIsland.Tests.csproj
```

发布：

```powershell
.\build.ps1 -Runtime win-x64
```

## 新 Agent 的扩展规则

Agent 使用稳定的字符串 `AgentKey`，而不是继续扩展共享枚举。新增 Agent 先在 `AgentIsland.Providers/BuiltIn/BuiltInAgentCatalog.cs` 注册描述，再按能力增加自己的 Usage、Activity、Cost 或导航适配器。没有实现的能力不返回“伪造的 0”，由 UI 显示“不支持/暂无数据”。

当前运行时仍保留必要的 `TriggerTool` 兼容入口，以读取既有配置和维持现有 UI；新增 Agent 从稳定 key/catalog 开始，不需要修改 macOS 上游或新增独立服务器项目。
