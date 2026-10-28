# P1-03 LOCAL INFILE流校验失败释放（2026-10-28）

源码走查发现：工厂返回非空流后，`OpenLocalInfileStream`检查`CanRead`，
若为false或getter抛异常，退出发生在调用方using取得所有权之前，导致流泄漏。
已有工厂源流由驱动释放的契约没有排除这个失败窗口。

最小修复在共有入口校验失败时Dispose并重新抛出；同步及异步上传仍使用原using。
没有新增API/选项/依赖，成功上传的所有权和未知结果策略不变。
工厂本身抛错或返回null仍按原路径拒绝；不读取不可用流、不打开服务端文件名。

单测同步/异步 × CanRead=false/getter抛IOException共4项，先观察全部因
DisposeCalls期望1实际0失败，再修复后相关19/19通过。
真实MySQL直连另4项验证流释放一次、零Read、工厂调用一次、unknown及原始异常类型、
Broken；显式Close/Open得到新物理会话，后续SELECT成功且工厂没有再次调用。
协议/缓存相关组38/38通过；五目标资产Release零警告/错误，DI14/14通过。
独立走查无待修发现。完整单测8144/8144、零失败/跳过，耗时8分51秒。

日志/TRX保存在ignored `TestResults/local-infile-validation-red.log`、
`TestResults/local-infile-validation-green.log`、`TestResults/local-infile-validation-live.*`、
`TestResults/infile-validation-five-tfm-build.log`、`TestResults/infile-validation-di.log`、
`TestResults/infile-validation-full-unit.*`。

仍不把失败上传当成已确认未执行，不自动重放；自定义Dispose自身抛异常沿用标准using语义。
直连证据不替代上传超限、同步源阻塞、代理策略及完整协议/中断组合验收。
