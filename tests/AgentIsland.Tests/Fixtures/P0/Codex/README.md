# P0 Codex 脱敏样本

这些 JSONL 不含真实路径、账号或提示词。规则与期望见 `docs/p0-baseline.md` 第 3 节。

| 样本 | 覆盖 | 当前解析 vs 目标 |
| --- | --- | --- |
| `01-same-file-replay.jsonl` | 同文件相邻累计值相等 | 一致：跳过重放 |
| `02-compaction-reset.jsonl` | 累计值缩小 | 一致：计入重置后的新增量 |
| `03-copy-a.jsonl` + `03-copy-b.jsonl` | 跨文件复制 | 当前加倍；目标按会话身份去重 |
| `04-sessions/` + `04-archived_sessions/` | 同相对路径的活动/归档副本 | 当前两份都计；目标活动副本胜出 |
| `05-parent.jsonl` + `05-child-fork.jsonl` | 子会话继承父累计前缀 | 当前把前缀当子会话消费；目标只计前进量 |
| `06-reasoning-in-output.jsonl` | 推理 Token | 当前忽略该字段（未再加）；目标显式标记已包含在输出中 |
