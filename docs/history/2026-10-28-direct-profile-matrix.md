# 双模式直连缺口复核（2026-10-28）

## 本轮验证及失败分类

以本轮已提交源码和本机显式启用的两套直连profile执行完整IntegrationTests。
第一轮584项：560通过、18失败、6跳过。新增上传、缓存和批处理测试均单独通过；
此前600项：576通过、18失败、6跳过，失败集合与首轮一致。日志/TRX为ignored
`TestResults/direct-profile-final.*`。完整驱动单测8140/8140、DI14/14及五资产Release
零警告/错误见[多语句模块](2026-10-28-multi-statement-client-policy.md)。
后续新增写入边界16项、外置LOB读取回包中断8项及两项原生转发器fixture、外部删列缓存4项。
截至 `f51310d` 的最新完整矩阵630项：606通过、18失败、6跳过，耗时1分50秒。
逐项比较600项快照和本次TRX的失败名称，18项完全一致、差异为零。
最新日志/TRX为ignored `TestResults/direct-profile-current.*`；默认不启用实库时
20通过、140跳过、0失败，日志为 `TestResults/current-no-opt-in.log`。
新模块分别见[写入边界](2026-10-28-write-outcome-boundaries.md)、
[外置LOB](2026-10-28-external-lob-read-interruption.md)、
[外部删列缓存](2026-10-28-statement-cache-external-ddl.md)，均已有独立走查及本地提交。

18项失败保留为失败，没有删除用例、改成skip或放宽契约：

| 项目 | 失败数 | 当前复核 |
| --- | --- | --- |
| Oracle complex national客户端编码 | 12 | utf8mb3、sjis、big5各4项止于Open；不代表数组绑定已执行失败 |
| MySQL JSON/utf8mb3 | 2 | 同步/异步均止于Open的严格会话字符集校验，不代表JSON存储已完成验收 |
| Oracle nested-table表列 | 1 | 自有collection类型创建后，使用该类型建表返回600/0A000 |
| Oracle SELECT CURSOR结果列 | 1 | 准备后执行阶段返回902/HY000，未取得结果列 |
| Oracle目录对象权限 | 2 | CREATE VIEW与CREATE SYNONYM均返回1031/HY000，无法完成相应目录断言 |

另用相同驱动公开API单独核对会话状态及服务端错误，源代码和日志保留在ignored
`TestResults/charset-gap-probe/`、`TestResults/direct-gap-probe.log`。探针只创建随机自有
对象，执行后清理；不修改SQL_MODE、权限或全局配置，不记录连接信息。

实际字符集结果：

- MySQL和Oracle在utf8mb3赋值后，都报告client/connection/results为utf8mb4、
  collation为utf8mb4_general_ci。当前严格utf8mb3契约不能以这个结果关闭。
- MySQL的sjis/big5状态符合请求，对应Open已通过。
- Oracle先切client/connection到sjis或big5后，后续固定ASCII
  `SET character_set_results` 返回900/42000。

六项跳过为两个alternateDatabase、专用不支持认证插件端点、禁用自动提交端点、
真实OB20和trace profile。两套直连凭据已保存在本机user-secrets，无需重复配置；
这不自动提供其他端点/证书/能力。

## 下一步边界

已经完成的模块分别有本地提交、单测/实库证据和独立走查。当前工作包继续保持开放：

- W1：外置LOB真实读取回包中断已后续补验8/8，见[记录](2026-10-28-external-lob-read-interruption.md)；其他中断点、完整资源交错与持续资源上限、代理reset仍缺。
- W2：更多发送/执行边界、节点/代理和事务管理器故障。
- W3：上传超限、其他协议组合、真实压缩帧和OB20/trace环境。
- W4：实际TLS/mTLS、证书轮换、认证插件/KDC及多节点发现/HA。
- W5/W6：上述失败的服务端/权限前提及其他类型/目录形状。
- W7：外部删列确认错误后缓存淘汰已后续补验4/4，见[记录](2026-10-28-statement-cache-external-ddl.md)；其他DDL/特定reprepare错误、警告/限额、负载与诊断开销仍缺。
- W8：其他平台、承诺运行时、真实包消费和发布审查。

不为消除红项而把utf8mb3静默改为utf8mb4、不增加服务器不支持形状的客户端
workaround、不自动授予权限，也不扩展没有协议依据的输入游标、OB20压缩/reroute、bulk。
全部实库证据限Windows/.NET10、当前双模式直连、TLS Disabled；不能宣称所有缺口完成。
