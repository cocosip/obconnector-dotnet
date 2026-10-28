# Windows 连接池验收夹具取消异常（2026-10-28）

用户提供的 Windows CI 日志中，
`AcceptanceRequiresResetReuseRatherThanSuccessfulReconnection(false, true, false)`
在清理阶段等待 `serving` 时失败。异常来自尚未完成的 `AcceptTcpClientAsync`，
为 `SocketException / OperationAborted`。此前 `994a262` 已将顺序调整为取消、
等待服务任务、停止监听器；该顺序保留，但不能保证 Windows 总将取消包装为
`OperationCanceledException`。

本次仅补充夹具的预期退出处理：取消令牌已触发且 socket 错误恰为
`OperationAborted` 时正常结束。其他 socket 错误、未取消时的同类错误及断言失败
仍由服务任务传播。生产驱动、CI 策略以及连接数、Prepare/reset/ping 次数和
同一物理 channel 的验收断言均未修改；不需要实库或证书。

验证日志位于 ignored `TestResults/ci-pool-shutdown/`：

- 修改前本机专项 8/8 通过，未在本机重现用户的偶发 CI 失败。
- 修改后 Release 构建零警告/错误；专项连续 20 轮，共 160/160 通过。
- Windows/.NET 10 完整默认单测 8,003/8,003 通过，零失败/跳过，耗时 8 分 15 秒；
  使用普通用户上下文执行，保留 TNS 文件替换测试。
- 独立只读走查无待修发现，确认非预期异常仍传播、监听器始终由 using 释放。

远程 Windows CI 尚未重跑，本机重复通过不等于远程已验证。
