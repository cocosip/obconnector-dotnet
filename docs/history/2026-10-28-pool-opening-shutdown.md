# P0-01/P0-02 池关闭时的未完成 Open（2026-10-28）

## 依据与范围

Java 参考 `obconnector-j` 的 `internal/util/pool/Pool.java` 在 `close()` 中进入
closing 状态，并关闭/中止池内连接。当前 .NET `PhysicalSessionPool.Dispose()` 只清理
idle session；借出但仍处于物理连接建立阶段的 session 可能继续等待网络/连接超时。

本次只补这一条生命周期边界：池销毁时原子关闭借出的 `Closed` 或 `Connecting`
session，并中止 `Min Pool Size` 预热中尚未注册的 session；已经完成认证并处于
`Open`/`Busy` 的连接继续可用，保持既有 `OceanBaseDataSource` 活跃连接语义。
没有引入自动重连、事务迁移或 replay。

## 实现

- `PhysicalSession.TryCloseOpening()` 在 session 锁内检查状态、释放传输并触发关闭取消。
- `PhysicalSessionPool.Dispose()` 只处理借出且尚未完成 Open 的 session，正确移除租约、
  session 和 discard 指标。
- 预热期间的 session 在 `_warming` 中登记；池销毁会取消并释放这些连接，迟到的注册会被拒绝。
- 新建 session 在注册时完成借用登记；候选复用路径拒绝池销毁后的迟到登记，避免资源/指标泄漏。

## 验证

| 门禁 | 结果 |
| --- | --- |
| 回归（先 RED 后 GREEN） | `DisposedPoolCancelsBorrowedSessionBeforeOpen`、`DisposedPoolCancelsSessionDuringMinPoolWarmup`：均通过；预热回归修复前在 5 秒连接超时后才结束 |
| 受影响单测 | `PhysicalSessionPoolTests`：19/19 通过 |
| 构建运行时 | .NET 11.0.100-rc.3 SDK 构建 `net10.0`，由 .NET 10 runtime 运行测试 |
| 真实数据库 | 未新增实库验收；这是池/传输生命周期边界，使用 loopback 证明取消和 socket 关闭 |

完整资源交错、代理 reset、长期压力和其他部署矩阵仍开放。
