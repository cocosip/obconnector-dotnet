# P1-03 上传源等待期间取消/超时（2026-10-28）

在 `Protocol/LocalInfileTests.cs` 增加两个MySQL真实上传用例。源第一次读取返回64KiB，
完成网络上传后才进入第二次ReadAsync并发出等待信号；测试在该信号之后取消调用方，
或等待一秒CommandTimeout。源读取遵循驱动传入的取消令牌。

断言unknown outcome、具体异常及调用方原token，驱动释放源、连接Broken，
显式Close/Open得到新物理会话，factory始终仅调用一次且后续SQL成功。
这只覆盖异步源等待期间中断，不推断部分导入的行数，也不证明服务器处理进度。

独立走查发现清理复用了可能已取消的30秒deadline，可能没有等待执行任务收尾。
已改为独立五秒watchdog并断言任务结束。最终协议直连组26/26通过；包含新增两项及
[前一上传模块](2026-10-28-local-infile-chunked-acceptance.md)四项。
最终默认未启用实库：18本地测试通过、137跳过，没有隐式连接数据库。

生产源码未更改；完整单测8140/8140、DI14/14、五资产Release零警告/错误沿用
[当前源码门禁](2026-10-28-multi-statement-client-policy.md)。测试项目Release构建通过。
日志/TRX：ignored `TestResults/local-infile-interruption-final.*`、`integration-no-opt-in-final2.*`。

实库证据限Windows/.NET10、MySQL直连、TLS Disabled。同步源阻塞、超限输入、
真实网络/服务器故障、压缩/OB20/TLS/代理组合仍开放。
