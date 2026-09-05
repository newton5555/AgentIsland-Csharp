# AgentIsland for Windows

这是从 Agent Island 2.1.2 fork 出来的独立 Windows 产品线，使用 .NET 8 + WPF。当前版本保留 Agent Island 的核心设计与 Agent 监控能力，并针对 Windows 的窗口、托盘、路径和系统集成做了独立实现；它不再依赖 macOS 上游仓库的目录、构建或发布流程。

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

## 灵动岛透明渲染实验

主窗口默认继续使用逐像素透明。退出已运行的 Agent Island 后，可用下面的命令启用 WindowChrome / DWM 实验（设置窗口和报告窗口不受影响）：

```powershell
dotnet run --project .\src\AgentIsland\AgentIsland.csproj -c Release -- --island-renderer=chrome
```

对照原方案时，退出程序，重新运行同一命令并去掉 `--island-renderer=chrome`。参数仅作用于本次进程，语言切换重建窗口后仍保留；不修改用户配置。DWM 不可用时，创建窗口前自动保留原方案。

实验保持原有扫光约 15fps、阴影、窗口尺寸和鼠标检测间隔，以便先比较渲染路径。建议分别记录收起空闲、单/双 Agent 扫光、展开收起时的进程 CPU、GPU、内存和帧时间；两组使用相同数据、缩放、屏幕和采样时长。不能将构建通过或进程 CPU 降低直接视为视觉验收通过。

启用为默认前需人工验收：透明留白和圆角可以点击到其他应用；快速移入后能点击胶囊；悬停展开、浮动拖动、隐藏再显示、语言切换正常；100%/150%/200% DPI 和跨屏位置正确；阴影无黑底或裁切。实验使用原生样式钩子保留 `WS_EX_LAYERED`，绕过 WPF 在非逐像素透明模式下移除该样式的行为，后续升级 WPF 时需要复验。若出现黑底或交互异常，退出并去掉参数即可回到原方案。

## 新 Agent 的扩展规则

Agent 使用稳定的字符串 `AgentKey`，而不是继续扩展共享枚举。新增 Agent 先在 `AgentIsland.Providers/BuiltIn/BuiltInAgentCatalog.cs` 注册描述，再按能力增加自己的 Usage、Activity、Cost 或导航适配器。没有实现的能力不返回“伪造的 0”，由 UI 显示“不支持/暂无数据”。

当前运行时仍保留必要的 `TriggerTool` 兼容入口，以读取既有配置和维持现有 UI；新增 Agent 从稳定 key/catalog 开始，不需要修改 macOS 上游或新增独立服务器项目。
