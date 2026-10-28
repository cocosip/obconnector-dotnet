# P2-02 Statement cache 的DDL/reset直连验收（2026-10-28）

新增 `Commands/StatementCacheTests.cs`，双模式 × 同步/异步4/4通过。
显式缓存容量2、最大池1，验证已释放Command的准备对象被下一Command实际复用；
对象身份仅用于区分缓存命中与每次重新准备，不作为性能结论。

保留一个准备中的Command并缓存另一SQL，执行同会话可观察ALTER TABLE。
两条SQL的旧准备对象都失效；保留的SELECT *重新执行返回三列、新列默认值7及
原始数据。Close/Open复用同物理会话后，保留Command再次重新准备，参数及结果正确。

独立走查无待修发现；Protocol/StatementCache直连回归28/28通过、0跳过。
本模块只增加实库测试，驱动源码完整单测8140/8140、DI14/14及五资产Release零警告/
错误见[本轮门禁](2026-10-28-multi-statement-client-policy.md)。测试项目Release构建通过。
日志/TRX：ignored `TestResults/statement-cache-lifecycle-live.*`、`protocol-cache-final-live.*`。

环境为Windows/.NET10、双模式直连、TLS Disabled。不证明外部不可观察DDL、服务端
reprepare错误、并发缓存操作、长期资源曲线、延迟/吞吐或其他部署profile。
