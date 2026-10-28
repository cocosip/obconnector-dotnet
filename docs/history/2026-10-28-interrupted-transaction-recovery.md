# P0-04/P0-05 活动事务取消、超时与重建（2026-10-28）

新增 `Faults/InterruptedTransactionTests.cs`，补现有实现的真实直连证据。
双模式 × 同步/异步 × 取消/超时共8项：事务先写入一行，再运行本地 profile
声明的五秒慢查询。同步取消调用 `Cancel()`，异步取消传入原调用方令牌；超时为一秒。

断言覆盖 unknown outcome、具体异常原因及异步原 token、连接 Broken、旧事务失效、
Close/Open 使用新物理会话。新事务写入后再释放旧事务，observer 仍看不到未提交写；
新事务提交后 count=2、sum(id)=5，确认旧写未泄漏，旧释放未误提交或回滚新事务。

验证：新增8/8通过；完整 Transactions/XA/Faults 组75/75通过、0跳过；
测试项目Release构建通过。驱动源码未变，完整单测8138/8138、DI14/14及五资产
Release零警告/错误沿用[本轮源码门禁](2026-10-28-data-source-open-shutdown.md)。
独立走查无待修发现。日志/TRX 为 ignored `TestResults/interrupted-transaction-final-live.*`。

仅覆盖 Windows/.NET10、双模式直连、TLS Disabled。没有自动重放未知操作；
测试不证明断线瞬间服务端语句的执行进度，也不覆盖 OBProxy/TLS、服务器或协调器崩溃。
