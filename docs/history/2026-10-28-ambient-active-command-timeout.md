# P0-03 活动命令期间管理器超时直连验收（2026-10-28）

新增显式opt-in `Transactions/AmbientActiveCommandTimeoutTests.cs`，MySQL/Oracle直连
各同步、异步，共4项。无生产代码改动，验证现有忙会话拒绝并丢弃的安全行为。

单资源CommittableTransaction写入后启动配置的慢查询，等待真实TransactionCompleted=Aborted。
管理器1秒超时必须先于20秒命令超时/30秒总截止时间中止执行；明确排除命令超时或调用方取消
造成的假通过。断言OceanBaseException/unknown outcome、Broken、旧事务Commit拒绝及原写未提交。

在同一容量1私有池中显式Close/Open，必须获得新物理会话。新事务写id2后，旧事务和旧Command
晚Dispose不能改变其未提交可见性；继续写id3并提交后，独立observer精确两行且SUM(id)=5。
失败清理关闭连接并等待执行任务结束，测试只创建当前profile下的随机对象。

独立走查发现原3秒管理器预算不满足slowQuery仅保证大于2秒的契约，已改为1秒，并增加排除命令
自身超时/取消的断言；最终版本双模式4/4通过，零跳过，耗时11秒。项目Release零警告/错误。
默认未启用实库时，整个IntegrationTests为22项配置测试通过、145项实库用例跳过，未访问真实库。
证据为ignored `TestResults/ambient-active-command/ambient-active-final.trx`、
`live-final.log`、`build-final.log`及`no-opt-in-final.trx`。

范围限当前直连profile、Windows/.NET10和单资源本地管理器；不证明in-doubt、管理器进程崩溃、
分布式提升、真实代理/服务器故障或独立运行时兼容性。该验收不加入普通单测或CI。
