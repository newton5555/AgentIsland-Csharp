# AgentIsland for Windows

[English](README.md) | [简体中文](README_zh.md)

AgentIsland for Windows 是面向 Windows 平台的 AI Agent 桌面悬浮监控工具，使用 C#（.NET 8 + WPF）开发。它常驻桌面，用于查看 Claude、Codex、DeepSeek Harness、Antigravity、Grok 和 Cursor 的实时状态、Token 消耗、配额与使用报告。

---

## 与原仓库的关系

* **来源与背景**：本项目源自 [Agent Island](https://github.com/agent-island/agent-island)（参考基线 2.1.2）。原仓库为一个包含 macOS 主版本与 Windows 适配子目录（`windows/`）的多平台项目。
* **独立工程化**：本仓库将原仓库中的 Windows 实现提取为一个独立的顶级 C# 仓库，剥离了与 macOS 构建相关的配置和残留文件，不再依赖上游的多平台代码树。
* **独立维护与演进**：脱离原工程后，本仓库独立组织解决方案与构建发布流程，针对 Windows 环境的稳定性、扩展性与资源占用持续进行迭代。

---

## 本仓库所做的工作与更新

在独立提取的基础上，本仓库主要完成了以下重构与适配工作：

1. **架构分层重构**：
   - 重新划分为 `Core`（模型与通用规则）、`Providers`（Agent 适配与解析）、`Windows`（系统路径、底层 API 与内存管理）和 `AgentIsland`（WPF 界面与宿主），解耦业务逻辑与 UI。
   - 建立显式的 `AgentCatalog` 能力注册机制，替代原有的硬编码枚举，各 Agent 按照实际支持情况声明用量、费用、活动状态与导航能力，无数据时明确显示，不伪造数值。
2. **Agent 适配与扩展**：
   - 全面对齐原版支持的 Agent（Claude、Codex、Antigravity、Grok、Cursor）在 Windows 下的会话/日志路径探测与解析（包含 Cursor 全局存储 SQLite 数据库直读）；
   - 新增支持 DeepSeek Harness（解析本地 `.dsh` 压缩事件流，并对接官方余额查询接口）。
3. **本地数据离线聚合**：
   - 费用统计、周期用量报告与对决矩阵完全从开发者本机现有的日志离线读取，不向外上传代码或数据。

---

## 1.0.1 性能与内存优化

在 1.0.1 版本中，主要针对后台长时间运行时的内存占用和轮询开销进行了针对性优化：

* **空闲物理内存回收（常驻控制在 100MB 内）**：
  - 针对历史会话较多的 Provider（如 Codex）禁用后内存无法下降的问题，新增 `MemoryReclaimer` 模块；
  - 采用 600ms 异步防抖，在禁用 Provider 或清理大缓存后执行 Gen 2 与 LOH 堆碎片压缩；
  - 调用 Windows 内核 API `SetProcessWorkingSetSize`，将未引用的物理内存页及时归还给操作系统，空闲常驻内存从峰值 200MB+ 平滑回落至 100MB 以内；
  - 修复了 `SessionScanner` 在禁用 Provider 时漏清理静态元数据缓存的问题。
* **大文件扫描减少大对象堆（LOH）分配**：
  - `SessionScanner` 采用 `ArrayPool<byte>` 缓冲区并进行逆向流切片，读取超长会话 JSONL 首行与尾行，避免大字符串进入大对象堆（LOH）；
  - 引入 `CodexMetaCache` 静态指纹比对（基于文件修改时间与大小），对未改动的会话文件跳过重复解析。
* **费用解析对象图谱内存驻留**：
  - `LogParseCache` 在内存中常驻已解析的 `TokenEvent` 实例，在日常轮询且日志文件未改动时，实现 0 额外托管堆分配。
* **托盘图标句柄复用**：
  - `TrayIconRenderer` 预渲染 32×32 图标底图，并建立 6 种视觉状态的单例 `Icon` 句柄缓存，避免轮询定时器频繁创建和销毁 GDI+ 资源。
* **悬浮窗鼠标检测粗筛**：
  - `IslandWindow` 在高频鼠标移动检测中加入屏幕空间 AABB 快速粗筛，提前过滤无效区域，降低非凸几何体命中测试的计算开销。

---

## 项目结构

```text
src/
├─ AgentIsland.Core       # 通用核心：数据模型、统计规则、缓存策略与接口契约
├─ AgentIsland.Providers  # Agent 适配层：各 Agent 的日志解析与注册中心
├─ AgentIsland.Windows    # Windows 原生层：路径探测、SQLite、底层内存管理
└─ AgentIsland             # WPF 宿主程序：界面渲染、托盘交互、设置面板与监控后台
assets/                    # 界面素材、图标与演示图
tests/
└─ AgentIsland.Tests      # 单元测试与集成测试（包含 25 套测试套件）
```

---

## 构建与运行

### 环境要求
- Windows 10 / 11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 常用命令

```powershell
# 编译整个解决方案
dotnet build .\AgentIsland.sln

# 运行全量测试套件
dotnet run --project .\tests\AgentIsland.Tests\AgentIsland.Tests.csproj

# 本地打包自包含发布包
.\build.ps1 -Runtime win-x64 -Version 1.0.1
```

发布产物位于 `dist/AgentIsland-1.0.1-win-x64.zip`。
