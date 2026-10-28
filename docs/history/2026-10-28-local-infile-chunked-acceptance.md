# P1-03 大输入与源读取失败直连验收（2026-10-28）

新增 `Protocol/LocalInfileTests.cs`，只对具备建表权限的 MySQL profile 显式启用上传。
同步/异步各覆盖成功及第二次源读取抛 IOException，共4项通过。

成功上传大于1MiB的UTF-8 CSV（256行），跨多个64KiB读取/上传分块；独立observer
逐行核对id、全部长文本和总行数。上传流由驱动释放，原物理会话仍可查询。

源失败时断言unknown outcome、IOException、Broken及流释放，Close/Open取得不同
物理会话，factory仍只被调用一次，后续SQL成功。不推断失败上传的实际部分导入行数，
也不以重开作为补偿或自动重放。

独立走查无待修发现；Protocol及StatementCache直连回归28/28通过、0跳过。
驱动源码未改；完整单测8140/8140、DI14/14及五资产Release零警告/错误见
[本轮源码门禁](2026-10-28-multi-statement-client-policy.md)。
日志/TRX：ignored `TestResults/local-infile-chunked-live.*`、`protocol-cache-final-live.*`。

环境为Windows/.NET10、MySQL直连、TLS Disabled。不证明大于最大逻辑包的输入、
真实上传中途取消/超时、OBProxy策略、压缩/OB20/TLS组合。
