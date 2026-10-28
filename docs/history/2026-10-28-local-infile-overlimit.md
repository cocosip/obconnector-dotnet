# LOCAL INFILE 超限边界（2026-10-28）

LOCAL INFILE 上传按 `PacketChannel.DefaultMaxPacketSize` 限制逻辑包总字节数，当前上限为64MiB。超过上限时，驱动在发送超限数据包前抛出 `ProtocolException`，命令结果标记为未知并使物理会话进入Broken；工厂流仍由命令交换释放。

本地协议回归 `LocalInfileTests.LocalInfileRejectsPayloadBeyondLogicalPacketLimit` 使用可生成的64MiB+1流，不分配整块上传缓冲。验证结果：同步/异步2/2通过，源流释放一次，连接Broken，未发送LOCAL INFILE终止帧。完整 `LocalInfileTests` 为19/19通过。

该证据覆盖驱动本地包上限和未知结果处置，不替代代理、同步源阻塞、压缩帧或其他中断组合验收。
