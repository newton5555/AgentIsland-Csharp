# AgentIsland 版本更新与发布日志索引 (Release Documentation)

本目录记录 AgentIsland for Windows 各正式版本的详细更新日志、架构设计决策与技术演进说明。

| 版本号 | 发布日期 | 核心主题 | 详情文档 |
| :--- | :---: | :--- | :--- |
| **v1.2.0** | 2026-09-06 | 现代企业级 C# 纯 DI 重构、双 Agent 限制与 1 年压力测试、开箱默认体验 | [v1.2.0 Release Notes](v1.2.0.md) |
| **v1.0.1** | 2026-09-05 | Windows 独立工程化、内核级物理内存修剪（<100MB）、流切片优化 | [v1.0.1 Release Notes](v1.0.1.md) |

---

## 🛠️ CI 自动化发布说明
- 每次在 `release` 分支提交或推送 `v*.*.*` 标签时，GitHub Actions 会自动触发 `.github/workflows/release.yml`；
- 构建流程将自动运行全量测试套件（42 项测试），编译自包含可执行文件并打包生成 `dist/AgentIsland-{Version}-win-x64.zip`；
- CI 会自动读取 `docs/releases/v{Version}.md` 作为 GitHub Release 的官方发布说明并自动发布。
