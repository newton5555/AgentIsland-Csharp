# 性能验证

优化约束：保留现有窗口透明模式选择、阴影、扫光、波浪、呼吸、旋转动画及其帧率和时长。减少重复工作与临时对象，不用强制 GC、工作集裁剪或降低特效作为生产优化。

## 隔离与口径

- 构建使用相同的 Release 配置、运行时、显示缩放和透明模式。
- `AGENTISLAND_DATA_DIR` 和 `AGENTISLAND_CACHE_DIR` 仅覆盖 AgentIsland 自身的配置和缓存目录，须在进程启动前设置；它们不自动隔离 Agent 的日志或凭据。测试扫描/请求应使用固定输入和替身。
- 标准测试入口是 `dotnet test`；测试 fixture 应在初始化依赖前创建独立的临时目录。压力/资源测试通过过滤器单独执行，便于区分常规回归和长耗时场景。
- 工作集（Working Set）、私有提交（Private Bytes）、托管存活堆、每轮分配量分开记录。减少临时分配不代表任务管理器内存必然等比例下降；Windows/WPF 渲染资源也不等同于托管堆。

```powershell
dotnet build .\AgentIsland.sln -c Release
dotnet test .\AgentIsland.sln -c Release --filter "FullyQualifiedName!~AgentStressAndResourceTests"
dotnet test .\AgentIsland.sln -c Release --filter "FullyQualifiedName~AgentStressAndResourceTests"
```

## 当前代码基线（2026-09-08）

- `dotnet build .\AgentIsland.sln -c Release --no-restore`：0 个错误，10 个测试项目警告（现存的 `CS8602` / `CS0067`，不阻断构建）。
- 常规回归测试：55 项通过；压力/资源测试：1 项通过；合计 56 项通过。
- 以上是当前主分支的工程验证基线，不代表 WPF 工作集、GPU 显存或 UI 帧时间已经完成实机性能验收。

## 自动回归重点

1. 零 Agent 不启动费用/活动扫描和额度请求；所有刷新入口服从同一启用集合。
2. 关闭某 Agent 取消其后台任务并释放内存缓存，不清除磁盘历史或凭据；延迟返回的旧任务不能重新写入。另一 Agent 的有效结果不受影响。
3. 费用面板和报表共享同一 Provider 的读取；关闭报表只取消该消费者，不能取消其他消费者需要的数据。快速切页只展示最新请求。
4. 冷解析、磁盘恢复、内存命中、清理并发的 Token/费用/时区/去重结果一致。
5. 文件新增、修改、删除、重命名、归档及 watcher 溢出后能恢复；即使文件没有变化，也继续推进 Working/NeedsYou/Idle 的时间状态。
6. 合并属性刷新不丢失一次性告警；关闭窗口后不再持有计时器或待执行 UI 工作。既有图形与动画测试继续通过。

## 进程资源采样

先运行待测程序并取得 PID，再执行只读采样。脚本不会启动/关闭程序、修改 Agent 开关、调用 GC 或裁剪工作集。

```powershell
Get-Process AgentIsland | Select-Object Id, ProcessName
.\scripts\Measure-ProcessResources.ps1 -TargetProcessId 12345 -DurationSeconds 60 -OutputPath .\.tmp\idle.json
```

`CpuPercentMachine` 与逻辑 CPU 数量归一化；`CpuPercentOneCore` 的 100% 表示占满一个逻辑核。报告平均值与峰值，保留逐秒数据。脚本不测 GPU 显存或帧时间，不能用 CPU 数字替代动画流畅度验收。

| 场景 | 固定条件 | 关注项 |
| --- | --- | --- |
| 0 Agent，收起空闲 | 冷启动后稳定，再采样 60 秒 | 请求/扫描次数为 0，内存与句柄无持续增长 |
| 单 Agent | 同一份日志，冷/热缓存分开 | 首次扫描与重复扫描时间、分配量 |
| Codex + Antigravity | 同一活动数据与启用顺序 | 独立刷新、进程 CPU/私有内存 |
| 关闭其中一个/全部 | 记录关闭前后及下一次原定刷新 | 旧任务失效、缓存释放、另一 Agent 不受影响 |
| 报表快速切换/关闭 | 费用刷新并发进行 | 最新页正确，无重复扫描或旧页回写 |
| 扫光、展开收起与高 DPI | 前后版本同参数 | 阴影、透明留白、动效和点击穿透不退化 |

## 本轮对照基线（2026-09-05）

改造前工作树快照 Release 构建和原有完整测试均通过。独立 Core 探针使用 120 个固定文件、共 84,000 条事件；每轮执行缓存读取、面板汇总与 7 天报表汇总。3 个独立进程，每进程 12 次内存命中，取中位数。

| 阶段 | 基线耗时 | 基线总分配 |
| --- | ---: | ---: |
| 冷解析并保存 | 466.06 ms | 72.39 MiB |
| 从磁盘缓存恢复 | 237.31 ms | 72.19 MiB |
| 内存命中后统计 | 56.22 ms | 16.75 MiB |

这些是固定输入的缓存/聚合微基准，不是整程序内存或真实 Agent 日志解析时间。探针中的强制 GC 仅用于观察缓存释放，不用于应用运行逻辑。优化后数据须使用相同输入验证并补录，不能从对象数量推算整程序节省比例。

## 本轮优化后对照

同一探针、同一输入、3 个独立进程取中位数：

| 阶段 | 优化后耗时 | 优化后总分配 | 与基线相比 |
| --- | ---: | ---: | ---: |
| 冷解析并保存 | 461.41 ms | 40.73 MiB | 分配量 -43.7% |
| 从磁盘缓存恢复 | 191.58 ms | 29.51 MiB | 耗时约 -19.3%，分配量 -59.1% |
| 内存命中后统计 | 40.54 ms | 9.77 MiB | 耗时约 -27.9% |

缓存中的托管对象存活量从 25.81 MiB 降到 9.43 MiB；调用 `ClearMemory` 后从 16.71 MiB 降到 0.77 MiB。84,000 条事件的数量、token 总数、费用汇总和 7 天切片校验值完全一致。这个结果说明本轮主要降低的是成本扫描缓存和重复聚合的托管内存，不等于任务管理器中的 WPF 工作集会按同样比例下降。

## 后续 CPU / 分配优化计划（待执行）

以下项目先记录，不在本轮直接修改。目标是减少扫描分配、无效 CPU/GDI+ 工作和 GC 抖动，同时保持现有运行时特效和交互响应。

1. **P0：定向重构 `SessionScanner.CodexMeta()`**
   - 去掉 `List<byte>`、分块 `ToArray()` 和最终 `Take().ToArray()` 的中间拷贝。
   - 保持首行最大长度限制、共享文件读取、UTF-8 行为和 `ParseCodexMeta` 的现有结果不变。
   - 用同一批 Codex 文件验证调用次数、总分配、LOH、Gen 2 GC、CPU 和首行边界案例；`ArrayPool<byte>` 只有在实测收益明确且归还路径安全时才采用。
   - 注意：`CodexMeta` 只解析每个文件的第一行，不能按“过滤大部分日志行”估算收益。

2. **P1：托盘图标状态缓存**
   - 在 `TrayIcon` / `TrayIconRenderer` 路径缓存上一次实际渲染状态。
   - 状态未变化时跳过 `Bitmap`、`Graphics`、HICON 和图标替换。
   - 保持状态阈值、托盘徽标和资源释放语义不变，重点验证不会复用已释放的 `Icon`。

3. **P1：缓存灵动岛命中测试的屏幕矩形**
   - 缓存 `IsPointInsideSilhouette()` 使用的屏幕坐标和尺寸，只在窗口位置、尺寸、布局或 DPI 变化时重新计算。
   - 保持鼠标检测 watchdog 的 **16ms** 频率，不降为 50ms，避免透明穿透和点击响应变慢。
   - 重点覆盖展开/收起、浮动拖动、多 DPI 和透明留白穿透。

4. **范围边界与验收门槛**
   - `ReadToEnd().Split()`、`TailLines()` 等其他大块读取路径先通过分配调用树确认，不和 P0 混改。
   - 暂不引入 `RecyclableMemoryStream`，不做全局 `StringBuilder` / `ArrayPool<char>` 改造；LOH 总分配不等同于 LOH 碎片，必须有堆快照证据后再决定。
   - 不把 `ArrayPool`、降频或画质调整当作默认优化；没有实测收益或造成行为退化就不保留。
   - 先用同一 Release 场景记录前后 CPU、Working Set、Private Bytes、句柄和线程数据，再决定是否保留改动。
   - 分配优化另外记录总分配、Gen 0/1/2、LOH 分配、暂停时间和 UI 帧时间；总分配下降不代表常驻 Private Bytes 等比例下降。
   - 自动化测试和人工桌面交互均不能退化。

## 本轮执行记录（2026-09-05）

- `SessionScanner.CodexMeta()` 已改为复用 `ArrayPool<byte>` 缓冲区，去掉分块 `ToArray()` 和最终二次复制；首行上限、共享读取、UTF-8 和解析结果保持不变。
- 托盘图标已按实际徽标视觉状态缓存；同一徽标状态不再重复创建 Bitmap、Graphics、HICON 或替换托盘图标，并保留旧图标的释放路径。
- `IsPointInsideSilhouette()` 已缓存屏幕矩形；窗口位置/尺寸、Silhouette 尺寸、形状状态、缩放及 `WM_DPICHANGED` 会使缓存失效，鼠标 watchdog 仍保持 16ms。
- 新增首行读取、命中区域几何和托盘视觉状态边界测试。
- 性能优化测试与构建在当时的固定输入下通过；该记录是 2026-09-05 的历史快照，当前编译器警告和测试基线以本文“当前代码基线”一节为准。
- 本轮没有 profiler 原始工件，因此不报告 CPU、LOH、Gen 2 或 UI 帧时间的百分比收益；这些仍需在相同 Release 场景和真实桌面上采样确认。
