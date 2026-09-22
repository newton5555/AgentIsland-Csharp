# P6 无窗口宿主

日期：2026-09-22。`AgentMonitoring.Host` 是不加载 WPF 的后台入口：采集 Codex 本地消费、写入存储、用 `IMonitoringQuery` 读快照。

## 运行

```text
dotnet run --project src/AgentMonitoring.Host -- --once
dotnet run --project src/AgentMonitoring.Host
```

`--once` 采集一轮并打印 tokens/dollars 后退出。省略参数则按 5 分钟间隔常驻。

环境：

- `CODEX_HOME`：一个或多个 Codex 根目录，逗号分隔。缺省为用户目录下的 `.codex`。
- 消费存储默认 `%LOCALAPPDATA%\AgentIsland\cache\codex-consumption.v1.json`，与桌面产品共用，关闭窗口再打开查询仍读这份事实。

## 验收

- 宿主与 `AgentMonitoring` 程序集不引用 PresentationFramework / AgentIsland WPF。
- 测试入口：`tests/AgentMonitoring.Tests`（net8.0，无 WPF）。
- 同一存储上新建 `MonitoringQueryService` 得到相同 Token 合计（模拟前端重建）。
- `AgentRuntime` 合并进行中的采集，完成后从 in-flight 表移除。

## 资源采样

不把历史数字当作本阶段证据。对无窗口进程：

```text
dotnet run --project src/AgentMonitoring.Host
# 另开终端
.\scripts\Measure-ProcessResources.ps1 -TargetProcessId <pid> -DurationSeconds 60 -OutputPath .\.tmp\headless-idle.json
```

记录 CPU、Private Working Set、Working Set、Private Bytes。方法见 [performance.md](performance.md)。
