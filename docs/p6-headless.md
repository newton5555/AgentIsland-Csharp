# P6 无窗口宿主与本机只读 API

日期：2026-09-23。`AgentMonitoring.Host` 不加载 WPF，默认只监听 IPv4/IPv6 loopback。API 以版本化 JSON 暴露后台查询和采集健康状态，可由本机的 WPF、桌面壳或其他前端读取。

当前采集能力仍有限：常驻 Worker 只运行 Codex 本地消费采集；额度、余额和活动尚未接入此宿主。因此，未采样的活动状态会返回 `unknown`，而不是伪装成 `idle`。本机 API 是跨前端边界的第一步，不代表所有 Agent 采集已经迁出 WPF。

## 运行

```powershell
dotnet run --project src/AgentMonitoring.Host -- --once
dotnet run --project src/AgentMonitoring.Host
```

`--once` 采集一轮并打印 tokens/dollars 后退出。常驻模式每 5 分钟采集一次，并在 `http://127.0.0.1:43127` 提供只读 API。单轮采集失败会记录健康状态并在下一周期重试，不会直接结束后台循环。

可配置环境变量：

- `CODEX_HOME`：一个或多个 Codex 根目录，逗号分隔；缺省为用户目录下的 `.codex`。
- `AGENT_MONITORING_PORT`：本机 API 端口，缺省为 `43127`。服务固定绑定 loopback，不接受通过命令行或 `ASPNETCORE_URLS` 改成外部监听。
- `AGENT_MONITORING_COLLECT_INTERVAL_SECONDS`：采集间隔，范围 1–86400 秒，缺省 `300`。
- `AGENT_MONITORING_ALLOWED_ORIGINS`：可选的精确 loopback 浏览器来源，以逗号分隔，例如 `http://localhost:5173`。未配置时不启用 CORS；不接受通配来源或非本机来源。
- Headless 消费存储默认 `%LOCALAPPDATA%\AgentIsland\cache\codex-consumption.headless.v1.json`，与 WPF 当前使用的文件隔离。两者尚未切到单一服务写入前，不共用同一个 JSON 文件，避免跨进程覆盖。
- Host 对消费存储启用严格读写：损坏文件或持久化失败会显式报错并进入健康状态，不会静默按空数据继续；损坏的原文件不会被启动流程覆盖。

API 为只读；所有响应都设置 `Cache-Control: no-store`。健康检查和路由：

| 路由 | 用途 |
| --- | --- |
| `GET /health/live` | 进程存活 |
| `GET /health/ready` | 后台采集 Worker 已启动 |
| `GET /api/v1/health` | 最近采集尝试、成功时间、连续失败次数与错误摘要 |
| `GET /api/v1/agents` | 当前有后台快照的 Agent 概览 |
| `GET /api/v1/agents/{agentKey}/overview` | 单个 Agent 概览 |
| `GET /api/v1/consumption?from={ISO-8601}&to={ISO-8601}&agent=codex` | 消费汇总；`agent` 可重复传入，范围最多 10 年 |
| `GET /api/v1/reports?from={ISO-8601}&to={ISO-8601}&agent=codex` | 报告切片；`agent` 可重复传入，范围最多 10 年 |

前端以轮询方式读取快照；日期必须带时区。概览 DTO 使用稳定字符串 Agent Key，并为活动数据提供 `available` 和 `updatedAt`，避免将未采样与真实空闲混淆。

## 当前验收边界

- Host、API 和 `AgentMonitoring` 不引用 PresentationFramework / AgentIsland WPF。
- API 集成测试验证 loopback 监听、恶意 Host 头拒绝、JSON 路由、查询范围限制和未采样活动状态。
- 采集失败测试验证服务保持运行、记录错误并在后续周期重试。
- 既有无窗口消费持久化测试仍验证重建查询后合计一致。
- 多 Agent 订阅配置、活动/额度/余额采集和状态快照重启恢复仍未完成；这几项是本机服务后续交付，不应视作 P6 已覆盖。

## 资源采样

不把历史数字当作本阶段证据。对无窗口进程：

```powershell
dotnet run --project src/AgentMonitoring.Host
# 另开终端
.\scripts\Measure-ProcessResources.ps1 -TargetProcessId <pid> -DurationSeconds 60 -OutputPath .\.tmp\headless-idle.json
```

记录 CPU、Private Working Set、Working Set、Private Bytes。方法见 [performance.md](performance.md)。
