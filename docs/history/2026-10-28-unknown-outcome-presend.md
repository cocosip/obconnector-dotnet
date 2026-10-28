# Unknown outcome 本地 pre-send 边界（2026-10-28）

`CommandExchange` 在开始命令前会准备状态和响应租约。此前它在真正写入第一个协议帧前
就把 SQL 标记为已发送，因此本地帧参数校验失败会被错误包装成 execution outcome unknown，
并销毁仍可复用的物理会话。

本次修改让 `PacketChannel` 记录当前命令是否开始写帧。只有实际进入帧写入路径后，写入/回包
故障才继续按 unknown 处理；帧写入前的本地校验失败保持“命令初始化失败”，完成当前租约并
保留会话。部分写入仍先标记为已开始，避免把网络边界误判为已确认失败。

回归 `LocalPacketValidationBeforeFrameWriteDoesNotBecomeUnknownOrBreakSession` 覆盖同步和异步
两项：传入越界帧长度时错误不带 unknown 标记，物理连接仍为 Open，随后命令成功执行。
已有的部分写入、取消和响应丢失测试继续覆盖实际写入后的 unknown 边界。

这只关闭驱动本地 pre-send 校验边界；真实服务器、OBProxy、协调器和完整发送/执行/取消故障
矩阵仍需专用环境验收。
