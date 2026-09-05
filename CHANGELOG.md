# Changelog

All notable changes to AgentIsland for Windows will be documented in this file.

## [1.0.1] - 2026-09-05

### 🚀 性能与内存深度优化 (Performance & Memory)
- **Provider 禁用后物理内存深度回收**：
  - 新增 `MemoryReclaimer` 异步防抖内存回收器，支持 Gen 2 + LOH（大对象堆）碎片整理与压缩。
  - 调用 Windows 内核 API `SetProcessWorkingSetSize`，在 Provider 禁用后将未引用的物理工作集（Working Set）迅速退还给操作系统，空闲常驻内存从 ~200MB 回落至 100MB 以内。
  - 修复 `SessionScanner` 在禁用 Codex 时漏清理静态元数据缓存 `CodexMetaCache` 的问题。
- **会话与日志扫描分配优化**：
  - `SessionScanner` 引入 `ArrayPool` 及逆向流切片，减少读取超长 JSONL 时的临时缓冲区分配与尾部扫描开销。
  - 引入 `CodexMetaCache` 静态元数据指纹缓存，避免重复解析未变动会话。
- **费用解析图谱常驻缓存**：
  - `LogParseCache` 对未改动文件复用已解析的 `TokenEvent` 对象图谱，避免多周期轮询重复解析 JSON 和重复创建事件对象。
- **托盘渲染与图标资源优化**：
  - `TrayIconRenderer` 按视觉状态缓存 `Icon`，减少轮询定时器在状态不变时的重复 GDI+ 渲染。
  - 预渲染 32×32 产品 Logo 底图，缩短托盘图标渲染路径。
- **灵动岛悬浮检测加速**：
  - `IslandWindow` 增加 Silhouette 屏幕空间 AABB 包围盒快速粗筛，过滤无效的复杂几何命中测试，减少鼠标移动时不必要的命中计算。

### 🧪 测试与质量保证 (Tests & QA)
- 新增 `PerformanceOptimizationTests`，包含元数据流解析、包围盒命中、托盘状态键及内存回收与缓存淘汰等全面测试用例。
- 完整 27 套测试套件全部通过（ALL GREEN）。
