# Migration Notes

## 已完成

1. 从旧仓库的 `windows/` 提取到独立顶层目录。
2. 新建 Core、Providers、Windows、WPF 四个正式项目和一个测试项目。
3. 将无 WPF 依赖的模型、解析规则、费用聚合、Usage 缓存策略迁入 Core。
4. 将路径解析迁入 Windows；WPF 端通过兼容入口继续运行，降低一次性迁移风险。
5. 用显式 `BuiltInAgentCatalog` 建立可扩展的 Agent 注册点。

## 后续顺序

1. 把 `SessionScanner` 拆为 Core 的扫描契约、Providers 的 transcript 解析、Windows 的路径/SQLite/进程实现。
2. 把 `UsageFetcher`/各 Agent credentials 移入 Providers，把网络与凭据读取改成可注入依赖。
3. 把 `UsageStore`、`CostStore`、`ActivityMonitor` 的全局单例逐步收进 WPF 组合根管理的后台服务。
4. 最后按改动频率把 Settings、Report、WhatsNew 等页面改为 XAML + ViewModel；IslandWindow 和高频绘制控件继续保留代码控制。

每一步都以“行为不变、测试先绿、再移动下一组文件”为准，不做一次性大爆炸重写。
