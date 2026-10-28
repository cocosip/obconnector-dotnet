# P2-02 外部DDL确认错误后的缓存淘汰（2026-10-28）

在 `Commands/StatementCacheTests.cs` 增加双模式 × 同步/异步共4项实库验收，4/4通过。

- 独立非池化连接创建随机自有表，池化连接准备并完整读取初始两列数据。
- 第一命令Dispose后，另一命令取得同一个prepared对象，确认是真实cache hit。
- 独立连接删除谓词引用的value列；缓存命令执行返回确认的服务端错误、非unknown，原连接及物理会话仍健康。
- 释放失败命令后，独立连接恢复value列、增加extra列并写入不同数据。
- 新命令的prepared对象必须不同，参数查询返回全部新列和准确数据；普通SQL仍成功。

删列及恢复均发生在另一条物理连接，不能由被测连接的客户端DDL epoch代替失败淘汰。
prepared身份仅用于区分缓存命中/失败条目复用，结果内容和metadata仍作真实断言。
只操作随机自有表，沿用DatabaseContext的LIFO清理，不修改服务器权限或全局配置。

走查发现原先DROP表再重建在失败路径可能造成重复删除并掩盖原始断言，已改为删列/恢复，
表始终保留且沿用原清理契约。修改后缓存与协议相关组34/34通过，独立复查无待修发现，测试项目Release构建零警告/错误。
生产源码未改，完整单测8140/8140、DI14/14及五资产Release门禁沿用
[本轮源码记录](2026-10-28-multi-statement-client-policy.md)。
最终日志/TRX为ignored `TestResults/cache-external-ddl-related.*`；初版DROP表专项日志
`TestResults/external-ddl-cache-error.*` 不作为最终删列方案证据。

此证据只覆盖当前双模式直连的外部删列后missing-column确认错误及显式命令重建，
不声称取得特定1615 reprepare错误、服务器自动重备、透明DDL依赖追踪或未知写重放。
其他版本、代理、资源/负载和其他DDL形状仍未完整验收。
