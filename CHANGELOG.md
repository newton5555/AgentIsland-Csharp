# Changelog

All notable changes to AgentIsland for Windows will be documented in this file.

## [1.0.1] - 2026-09-05

### 🚀 性能与内存深度优化 (Performance & Memory)
- **Provider 禁用后物理内存深度回收**：
  - 新增 `MemoryReclaimer` 异步防抖内存回收器，支持 Gen 2 + LOH（大对象堆）碎片整理与压缩。
  - 调用 Windows 内核 API `SetProcessWorkingSetSize`，在 Provider 禁用后将未引用的物理工作集（Working Set）迅速退还给操作系统，空闲常驻内存从 ~200MB 回落至 100MB 以内。
  - 修复 `SessionScanner` 在禁用 Codex 时漏清理静态元数据缓存 `CodexMetaCache` 的问题。
- **会话与日志扫描零分配优化**：
  - `SessionScanner` 引入 `ArrayPool` 逆向流切片读取首行与末行，消除超长行解析时对大对象堆（LOH）的内存分配压力。
  - 引入 `CodexMetaCache` 静态元数据指纹缓存，避免重复解析未变动会话。
- **费用解析图谱常驻缓存**：
  - `LogParseCache` 实现内存级 `TokenEvent` 对象图谱缓存，在多周期轮询中命中缓存时实现 0 额外对象分配。
- **托盘渲染与图标资源优化**：
  - `TrayIconRenderer` 实现 6 种视觉状态的单例 `Icon` 缓存，消除轮询定时器造成的频繁 GDI+ 句柄分配与析构。
  - 预渲染 32×32 产品 Logo 底图，加速托盘图标渲染管线。
- **灵动岛悬浮检测加速**：
  - `IslandWindow` 增加 Silhouette 屏幕空间 AABB 包围盒快速粗筛，过滤无效的复杂几何命中测试，降低鼠标移动时的 CPU 开销。

### 🧪 测试与质量保证 (Tests & QA)
- 新增 `PerformanceOptimizationTests`，包含元数据流解析、包围盒命中、托盘状态键及内存回收与缓存淘汰等全面测试用例。
- 完整 25 套测试套件全部通过（ALL GREEN）。
