# P0-03 本地事务管理器超时直连验收（2026-10-28）

新增 `Transactions/AmbientTimeoutTests.cs`，双模式 × 同步/异步共4项。
使用真实System.Transactions的三秒CommittableTransaction超时，不以手工Rollback代替超时。
通过TransactionCompleted事件等待Aborted，不使用固定sleep猜测超时是否发生。

- 准备对象、连接等工作在计时事务之外；登记后INSERT，确认事务仍Active、observer不可见。
- 等到管理器报告Aborted后，observer仍为零；关闭前原owner自身也必须查到零且无需附加事务，
  同时验证本地事务已解除及服务端回滚已完成。
- 关闭owner并由新逻辑连接复用相同物理会话；新本地事务写入期间对过期事务Commit必须拒绝，
  随后迟到Dispose不能提交或回滚新事务。
- observer在新事务提交前为零，提交后精确两行且sum(id)=5，原事务的id=1不能出现。

独立走查指出仅observer=0会被后续Close补回滚掩盖，已增加关闭前owner自身读取断言。
修改前新增4/4通过；最终强化断言后的事务/XA/故障相关组135/135通过、零跳过，测试项目
Release构建零警告/错误。独立走查的假阳性已消除，关闭前实际查询断言已在两模式同步异步执行。
本模块没有新增生产改动；源码门禁为LOCAL INFILE修复`5f7043a`的完整单测8144/8144、
DI14/14及五目标资产Release零警告/错误，见[记录](2026-10-28-local-infile-validation-ownership.md)。
生产源码未为此验收修改；相关日志/TRX为ignored `TestResults/ambient-manager-timeout.*`、
`TestResults/ambient-timeout-w2-final.*`。

证据限当前Windows/.NET10、单资源本地事务管理器、空闲连接上的定时超时。
不证明活动命令期间超时、分布式提升、in-doubt、管理器崩溃或真实代理/服务器故障。
