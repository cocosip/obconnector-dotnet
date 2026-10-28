# P0-01/P0-02 池维护期间关闭（2026-10-28）

## 缺口与契约

`Rent/RentAsync` 从 idle 栈取出连接后先探活，再登记借用；`Return/ReturnAsync`
先结束借用，再执行 reset。这两个阶段的连接仍归池所有，但已不在 idle 栈。
旧 `Dispose` 只释放 idle 和未完成 Open 的连接，因此无命令超时的探活/reset
可能在池销毁后一直等待服务器响应。

本次在池锁内取出所有未借出的 session，移除 session/退休登记，在锁外关闭传输。
既有 `Open/Busy` 借出连接继续可用，归还时释放；不改变 Clear 和凭据轮换的退休契约。
没有新增公共 API 或协议选项。

## 测试与走查

- 新增 `PoolMaintenanceShutdownTests.DisposeClosesSessionDuringPoolMaintenance`，
  覆盖探活/reset × 同步/异步四项；服务器故意不回复，命令超时设为无限。
  修复前四项均触发两秒 watchdog；修复后确认及时结束、socket 关闭、session Closed、池清空。
- 新回归与 `PhysicalSessionPoolTests`：23/23 通过。
- DI：14/14 通过。
- SDK 10.0.403；五资产 `netstandard2.0/netstandard2.1/net8.0/net9.0/net10.0`
  Release 构建零警告、零错误。
- 独立最终走查无待修发现：核对 ownership 锁、迟到的探活/reset、借用保留、
  session/lease 指标不重复扣减和 Clear/凭据退休交错；走查者另跑关闭专项 8/8。

完整单测：8126/8126 通过，零失败、零跳过，耗时 8 分 48 秒。原始日志/TRX 位于 git-ignored `TestResults/pool-maintenance-*`。

## 保留边界

本次使用 loopback 协议服务器验证传输和池所有权，不新增实库/TLS/OBProxy 验收。
凭据/发现阶段尚无 session 的 Rent 取消、长期压力及其他资源交错另行推进。
