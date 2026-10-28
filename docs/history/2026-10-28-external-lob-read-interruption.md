# P0-02 外置LOB读取回包中断（2026-10-28）

新增 `Connections/PoolResourceIsolationTests.LobReadInterruption.cs`，当前Oracle直连
profile下，BLOB/CLOB × 同步/异步 × 整包/半包截断共8/8通过。
每项先以自有随机表存入100000字节BLOB或含补充平面字符的220000字节UTF-8 CLOB，
确认返回的是真实外置locator，再武装转发器截断原生 `DBMS_LOB.READ` 的首个真实回包。

读取报错不标记原SELECT结果unknown，连接置Broken；目标读取请求仅一次。
显式Close/Open取得新物理会话，旧句柄及流/文本reader不能再读。
新LOB流/文本reader已占用新会话后才晚释放旧资源，完整内容仍逐字节/逐字符相等，
随后普通SQL成功；清池/关闭后等待两个转发连接结束。

复用原精确命令转发器，新增0xa1 PrepareExecute SQL识别，使用生产ByteCursor作有界读取。
先观察原生fixture两项失败、文本两项通过，再实现识别；全部本地fixture8/8通过。
首次实库BLOB4项通过，CLOB4项因测试误用仅限BLOB的OpenRead失败，改用已有OpenTextReader
契约后最终8/8通过；没有为迁就测试修改产品API或生产读取逻辑。

连接/事务/XA/故障相关组244通过、4配置跳过、0失败；测试项目Release构建零警告/错误。
独立走查无待修发现。生产源码未改，完整单测8140/8140、DI14/14及五资产Release
门禁沿用[本轮源码记录](2026-10-28-multi-statement-client-policy.md)。
日志/TRX保存在ignored `TestResults/external-lob-response-loss.*`、
`TestResults/lob-interruption-related.*`，本地fixture日志为 `TestResults/lob-read-relay-*.log`。

证据限当前Oracle直连、Windows/.NET10、单上游明文标准协议。
首帧marker=0是原生metadata，只证明真实读取回包已到达转发器，不证明内容块已接收或
服务端READ已完成；不能替代真实OBProxy故障、其他读取中断点或持续资源压力验收。
