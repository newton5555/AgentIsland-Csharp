# P0 行为基线与契约

日期：2026-09-22。状态：已落地并关闭。对应 [future-plan.md](future-plan.md) P0。

本文件区分**当前行为**（保留或对照）与**拟调整行为**（P2 起落地）。未写进「拟调整」的统计语义保持不变。可回归入口：`tests/AgentIsland.Tests/P0CodexBaselineTests.cs`、`P0ConsumptionContractTests.cs`，样本在 `tests/AgentIsland.Tests/Fixtures/P0/Codex/`。

## 1. Agent 能力、来源、账户、刷新

刷新间隔：活动监控 6 秒；额度/余额与消费聚合默认 300 秒（设置项，下限 5/10 秒）。消费与额度独立 Worker。`CostQueryService.ScanAsync` 仍是查询入口，会启动或加入扫描——拟调整见第 5 节。

| Agent | 目录声明 | 实际已实现 | 本地消费来源 | 官方额度/余额 | 账户身份 | 刷新 |
| --- | --- | --- | --- | --- | --- | --- |
| Claude | Activity, Usage, Cost, SessionNavigation, Reauthentication | 与目录一致 | `%USERPROFILE%\.claude\projects` 与 `.config\claude\projects`（`CLAUDE_CONFIG_DIR` 可覆盖，逗号分隔）；`*.jsonl`，含子代理 | `api.anthropic.com/api/oauth/usage`；凭据 `%USERPROFILE%\.claude\.credentials.json` 或环境变量 | 消费事件无账户字段；额度用当前 OAuth | 额度 Worker；消费 Worker |
| Codex | 同上 | 与目录一致；另有停车账户切换 | `%USERPROFILE%\.codex\sessions` 与 `archived_sessions`（`CODEX_HOME` 单路径）；`*.jsonl` | `chatgpt.com/backend-api/wham/usage`；`auth.json` | 消费事件无账户字段；额度用当前 `auth.json`；停车账户在 `agentisland-accounts` | 额度 Worker；消费 Worker；可手动/耗尽自动切账户 |
| Antigravity | Activity, Usage, SessionNavigation | Provider 另实现了 `ICostLedgerReader` | `%USERPROFILE%\.gemini\antigravity{,-cli,-ide,-backup}` 与 `.config\antigravity` 的 conversations SQLite（`ANTIGRAVITY_DATA_DIR`） | 本机 language server `RetrieveUserQuotaSummary`，非云端 | 额度可带 email；消费事件无账户 | 额度需 agy/IDE 在跑；消费随 Worker |
| Grok | Activity, Usage, Cost, SessionNavigation | 与目录一致 | `%USERPROFILE%\.grok\sessions\...\updates.jsonl` | `cli-chat-proxy.grok.com/v1/billing`；`%USERPROFILE%\.grok\auth.json` | 消费事件无账户；额度用当前 CLI token | 额度 Worker；消费每次全量解析（不走 `LogParseCache`，因缓存 DTO 无金额列） |
| Cursor | Activity, Usage, Cost, SessionNavigation | 与目录一致 | `%APPDATA%\Cursor\User\globalStorage\state.vscdb` bubble | `api2.cursor.sh` DashboardService 当前账期 | 消费事件无账户 | 额度 Worker；消费读库 |
| DeepSeek | Activity, Cost | 另有官方余额，无目录位 | `%USERPROFILE%\.dsh\sessions\session*.jsonl.zstd`；同目录多版本取最高 | 无配额窗口；余额 `api.deepseek.com/user/balance` | 本地账本含全部 route（含 `my-gateway`）；余额用 Harness API key，不写进消费事件 | 消费 Worker；余额随 Usage 刷新 |

P1 已落地的能力声明（不改当前统计口径）：

- Antigravity 目录已声明 Cost。
- 已增加 `AgentCapabilities.Balance`；DeepSeek 声明之。余额仍独立于配额。
- `AccountRef.AccountId` 为 null 表示未知账户，不填当前登录账户。

## 2. 消费事件模型

目标类型已在 Core 编译：`ConsumptionFact`、`TokenBuckets`、`ModelRef`、`SourceRef`、`PricingContext`、`PricingQuote`、`ScanCursor`。报告路径仍经 `TokenEvent`，`FactMapping` 会带上推理口径、服务档位、长上下文和来源元数据。

| 字段 | 用途 |
| --- | --- |
| `Source.RecordId` | 去重与追溯；同一身份只保留一条 |
| `Source.SessionId` / `ProjectId` | 会话与项目分析 |
| `Source.AccountId` | 可识别则填写；否则 null |
| `Source.SourcePath` / `ByteOffset` | 扫描游标与重建 |
| `Model.Raw` / `Canonical` / `IsFallback` | 原始名、计价名、是否解析器填的默认模型 |
| `Tokens.*` + `ReasoningAccounting` | Token 口径 |
| `Pricing.ServiceTier` / `LongContext` / `EventTime` / `OfficialCostUsd` | 重新计价所需元数据 |

推理口径：

| Agent | 当前 | 目标 |
| --- | --- | --- |
| Codex | 不读 `reasoning_output_tokens`，只记 `output_tokens` | `Reasoning` 保存该字段；`ReasoningAccounting = IncludedInOutput`；Wire/Billable 不再加一次 |
| Claude | 只有 input/output/cache | `Absent` |
| Grok | input/output/cachedRead + 自报金额 | `Absent`；金额走 Official |
| Cursor | input/output | `Absent` |
| DeepSeek | 注释写明 reasoning 不加入；DSH 投影为 input+cache+output | `IncludedInOutput` 若源另有 reasoning 字段 |
| Antigravity | 把 Visible+Reasoning 折进 Output | 折并后 `IncludedInOutput`；若同时保存 Reasoning 计数，也不得再加 |

`TokenEvent` 继续服务现有图表，直到 P2 切换存储。不要把增量库建在 `TokenEvent` 上。

RecordId 规则（P2 实现）：

- 有消息/请求 ID：`agent + sessionId + 源 ID`（Claude 的 `messageId:requestId`，Grok 的 `promptId|model`，Antigravity 的 response/message id）。
- Codex：`sessionId + timestamp + total_input + total_output + last_input + last_output + sourcePath`。复制文件只要 session 与事件身份相同即碰撞。
- 无 ID：`timestamp + canonical model + 四类 Token`，仅作弱键。

## 3. Codex 去重规则

当前 `CodexLogParser`：同一文件内，相邻 `total_token_usage (input, output)` 相等则跳过；累计缩小视为压缩重置并计入；无 totals 的旧行计入。`CodexLogReader.Scan` 枚举 `sessions` 与 `archived_sessions` 全部 `*.jsonl`，无相对路径去重，无父子继承。`CODEX_HOME` 仅单目录。缺 `turn_context` 时模型回退 `gpt-5.4`。

目标规则（样本已标 keep / 调整）：

1. **同文件重放**：相邻累计输入/输出相等 → 跳过。保留。
2. **压缩重置**：累计缩小 → 计入该行 `last_token_usage`。保留。
3. **跨文件复制**：同一 `session_id` 与事件身份 → 只留一条。P2 调整。
4. **多目录**：同一 `CODEX_HOME` 下相对路径同时存在于 `sessions/` 与 `archived_sessions/` → 活动副本胜出。P2 调整。`codex archive` 若是移动，不会触发。
5. **父子继承**：子会话 `forked_from_id`（或等价 parent）重放父累计前缀时，以继承快照的最后 `total_token_usage` 为基线，只计此后前进量。P2 调整。
6. **归档**：仅归档、活动侧已不存在 → 计归档，不因文件离开 `sessions/` 删除已入库事实。删除文件是否保留历史按现有「仍计入已解析缓存直至窗口外淘汰」在 P2 存量表述一次。
7. **推理**：`reasoning_output_tokens` 已包含在 `output_tokens` 中。

样本与期望：`tests/AgentIsland.Tests/Fixtures/P0/Codex/expected.json`。旧解析器仍锁定 `current`；P2 管线锁定 `target`（复制、归档、分叉已收拢）。

## 4. Pricing 契约

当前：`Pricing` 静态表（快照 2026-06-10），未知模型 `Cost` 返回 0，`IsKnown` 供 UI 提示；Grok 用 `SelfReportedCostUSD` 覆盖表。价格与事实绑在 `TokenEvent.Dollars`。

目标模块职责：

- 消费事实不含估算金额；官方自报金额放在 `PricingContext.OfficialCostUsd`。
- `IPricer.Price(fact)` 返回 `PricingQuote`：Official / Estimated / Unpriced。
- Unpriced 的 `AmountUsd` 必须为 null；Estimated 可以为 0。
- Quote 带 `PriceSource`（名称、版本、快照日、有效时间）。
- 在线更新失败使用已验证嵌入快照；覆盖配置按 **Raw** 模型名。
- 价格表变更对已存事实重新计价，不重读原始日志。

P2 先为 Codex 接第一版：嵌入快照 + Unpriced 区分 + 官方自报优先。在线拉取与覆盖配置可同阶段最小实现，失败回退快照。

## 5. 接口、依赖、存储迁移

目标接口（P1 放入 AgentMonitoring.Core，P2 实现；本阶段不改项目图）：

```text
IConsumptionSource.CollectAsync(cursor, ct) → 新事实 + 新游标
IPricer.Price(fact) → PricingQuote
IConsumptionStore.Append/Replace 与游标同一事务
IConsumptionQuery.Read(filter) → 已存事实快照，不启动采集
```

依赖仍按 future-plan 第 4 节：Providers → Core；后台不引用 AgentIsland。

存储：

- 现在：`LogParseCache` JSON，键为路径，指纹 `(mtime, size)`，值为精简 Token DTO；Grok 无此缓存。
- 目标：消费事实 + `ScanCursor`（含 `ParserVersion`）。解析版本变化则该源重建。
- P0 不选定 SQLite / 其他引擎。P2 先评估能否演进现有缓存文件；契约稳定后再换引擎。
- 凭据不入库。旧 `*-parse-cache.v2.json` 在切换后只作重建输入，不作唯一真相。

查询：P2 起 `GetConsumptionSummary` / `GetReport` 只读存储。切日期、开报表、重建窗口不调用 Collect。现有 CostQueryService 的任务合并、消费者取消、代次 `Version` 迁到 Runtime，继续用于采集，不用于查询。

## 6. 与 ccusage `4e67d789` 的口径

对照性质：静态实现比较，未跑分。ccusage 用作边界案例，不是唯一标准答案。

| 点 | ccusage | 本仓库当前 | 本仓库目标 |
| --- | --- | --- | --- |
| 产品 | 命令式多 Agent 报告 | 常驻监控 + 报告 | 保持常驻；查询不采集 |
| `CODEX_HOME` | 逗号多根 | 单路径 | P2 可对齐多根，需样本 |
| 归档 | 同相对路径时 sessions 胜出 | 两根全计 | 对齐 sessions 胜出 |
| 分叉继承 | ReplayPlan，子会话只计前进量 | 无 | 对齐，用 05 样本 |
| 缺模型 | 早期日志回退 `gpt-5`，标 isFallback | 回退 `gpt-5.4`，无标记 | 保留 gpt-5.4 回退（与现网 Codex 一致），补 IsFallback；与 ccusage 总数不可直接比 |
| 推理 | 展示但不另计费 | 不读该字段 | 保存并标 IncludedInOutput |
| 服务档位 | thread_settings_applied + `--speed` | 无 | 有则写入 ServiceTier；无则 Unspecified，不发明倍率 |
| 计价 | LiteLLM/models.dev 快照 + 覆盖 + 离线 | 静态表，未知为 0 | 三分金额 + 快照回退 |
| 缓存 | 报告链发现→读→聚合 | 文件级缓存 | 字节游标；比 ccusage 更严的采集/查询分离 |

与 ccusage 对同一台机器的 Codex 目录时，允许因回退模型名、Fast 档位、多根 `CODEX_HOME`、分叉规则落地前后而不同。差异写入回归记录，不把对方合计当断言。

## 7. 旧行为 / 拟调整清单

保留：

- 同文件 Codex 重放与压缩重置。
- Claude 文件内 `messageId:requestId` 去重，Scan 后再按时间+Token 形状跨文件去重。
- Grok 自报金额覆盖价表；无 ticks 则无估算。
- DeepSeek chunk/message 按 (route, turn, step) 替换；reasoning 不加进合计。
- 日报计入未置顶但有数据的 Agent。
- 额度失败保留上次成功值。
- Worker 合并、取消、代次。

拟调整（P2+）：

- 消费模型改为 `ConsumptionFact`。
- Codex 跨文件复制、归档相对路径、父子继承。
- Pricing 独立；未知模型 Unpriced 而非 0。
- 查询不扫描；字节游标替代整文件缓存。
- Antigravity 能力声明补 Cost；DeepSeek 声明 Balance。

## 8. P0 完成条件对照

- [x] 每 Agent 能力/来源/账户/刷新已列表。
- [x] 消费事件模型可编译，推理口径有测试。
- [x] Codex 去重规则 + 脱敏样本；当前与目标合计可区分。
- [x] Pricing 三分金额与快照字段已定。
- [x] 接口与存储迁移原则已定，未提前选库。
- [x] 与 ccusage 的差异已逐项记录。
- [ ] 评审确认后关闭 P0，再开 P1 项目边界。
