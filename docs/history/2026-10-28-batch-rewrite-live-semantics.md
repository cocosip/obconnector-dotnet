# P2-03 INSERT rewrite 默认值、触发器及失败边界（2026-10-28）

新增 `Batches/InsertRewriteTests.cs`，显式 `RewriteBatchedInserts=true`，
MySQL直连同步/异步提交、回滚及重复键错误共6/6通过。

三条参数INSERT省略带Unicode默认值的列，并触发AFTER INSERT写入自有审计表。
observer确认提交前两表均不可见，提交后均三行或回滚后均零行；事务内直接读取
默认字符串并与.NET常量比较。独立走查指出仅用同一SQL字面量做WHERE计数会有
编码损坏的假阳性，已补直接读取断言，提交/回滚两个分支都覆盖。

失败测试先提交单条prefix，再执行两条合并的重复键group，最后留一条suffix。
断言1062/已确认错误、精确BatchCommandGroup及受影响行数[1,-1,-1,-1]；独立observer
确认主表及触发器审计表都只有id10，原连接仍可执行SQL。实际失败group提供合并
证据；未执行suffix、没有重放未知操作。随机trigger先于自有表清理。

独立走查的一处断言问题已修正，无其余待修发现。相关池/事务/协议/Batch/缓存及
Oracle数组绑定直连组245通过、4配置跳过、0失败；未显式启用实库时18本地测试通过、
136跳过。生产源码未改，完整单测8140/8140、DI14/14及五资产Release零警告/错误
沿用[本轮源码门禁](2026-10-28-multi-statement-client-policy.md)。

日志/TRX：ignored `TestResults/batch-rewrite-live.*`、`pool-transaction-protocol-batch-final.*`。
Windows/.NET10、MySQL直连、TLS Disabled；不证明其他触发器/默认表达式、警告、限额、
吞吐、真实故障、代理/TLS或全部部分失败组合。
