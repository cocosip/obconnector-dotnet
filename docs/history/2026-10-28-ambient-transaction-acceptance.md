# P0-03 单资源环境事务直连验收（2026-10-28）

## 覆盖范围

补齐已有 PSPE 实现的真实数据库证据，不增加分布式提升或 durable coordinator。
新增 `Transactions/AmbientTransactionTests.cs`，双模式、同步/异步、提交/回滚共16项：

- TransactionScope 内 Open 自动入列；独立 observer 在 Suppress 范围确认未提交写不可见。
- Complete 或未 Complete 的 scope 结束后，observer 确认实际提交或回滚；原连接和后续借用者继续写入。
- 显式登记的环境事务提前 Close，确认回滚；同物理会话交给新借用者，并开启新本地事务。
  旧环境事务迟到提交/回滚后，新事务仍未提交；新事务写入并提交后两行均保留。

独立走查指出初版新借用者使用自动提交，无法充分识别旧回调误发 COMMIT/ROLLBACK。
已改用未提交本地事务，并在回调前后及新提交后核对 observer 行数。

## 验证证据

- 新增16项全部通过，无跳过。
- Transactions、XA、Faults 完整本轮直连组67/67通过，0跳过；包含本地事务、
  savepoint、XA recover、16项 COMMIT/ROLLBACK 回包丢失及慢查询取消/超时。
- 慢查询能力已写入 ignored 本地 profile：MySQL 使用 `SELECT SLEEP(5)`，
  Oracle 使用 `BEGIN DBMS_LOCK.SLEEP(5); END;`；取消/超时基础专项6/6通过。
- 本轮驱动源码完整单测8138/8138、DI14/14、五资产Release构建零警告/错误，
  详见[源关闭模块](2026-10-28-data-source-open-shutdown.md)。本模块仅新增实库测试，
  未更改这些验证之后的驱动源码。新增测试所在项目Release构建通过。
- 独立走查的一处断言强度问题已修正；无其余待修发现。

日志和 TRX：git-ignored `TestResults/ambient-transaction-final-live.*`。
环境为 Windows/.NET10.0.14、两套 OceanBase 直连、TLS Disabled。

## 保留边界

不证明真实 OBProxy/TLS、多资源提升、并发回调、事务管理器进程崩溃、
服务器故障或全部 unknown-outcome 发送/执行/取消边界。
基础慢查询用例仅验证中断异常；活动本地事务在中断后的重建仍须专项补验。
