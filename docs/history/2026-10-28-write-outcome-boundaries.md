# P0-04 写入请求/响应边界直连验收（2026-10-28）

新增 `Faults/WriteOutcomeBoundaryTests.cs`，复用已有精确COM_QUERY转发器，
双模式 × 同步/异步 × 四个截断点16/16通过：

- 驱动已将完整INSERT发送给转发器，转发器不转发上游请求或只转发包头和命令字节；
  独立observer确认实际零行。驱动不能据此证明未执行，仍保守报告unknown。
- 转发器实际读取服务端OK，再丢弃完整或部分回包；observer确认实际一行已提交，
  驱动报告unknown并置Broken。

每项都确认目标请求仅一次、无自动重放，Close/Open取得新物理会话。
新事务写入期间再释放旧失败Command，observer仍只见核对过的原零/一行；
新事务提交后count及sum(id)准确，识别旧资源误提交/回滚及原写重放。
两个转发连接精确计数，清池/关闭后等待转发器结束。

独立走查无待修发现。完整事务/XA/故障直连组91/91通过、0跳过；测试项目
Release构建通过。生产源码未改，完整单测8140/8140、DI14/14和五资产Release
零警告/错误沿用[本轮源码门禁](2026-10-28-multi-statement-client-policy.md)。
日志/TRX：ignored `TestResults/write-outcome-boundary-final-live.*`。

此证据限Windows/.NET10、双模式单上游直连、TLS/压缩/OB20关闭。
转发器的上游截断不能称为驱动本地发送前失败，也不证明真实服务器、代理或协调器崩溃。
