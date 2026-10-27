# 2026-10-28 Oracle 资源与 REF CURSOR 直连验收

本次继续核查 P0-02 会话/池资源隔离和 P1-06 Oracle REF CURSOR 输出能力。连接信息只通过当前进程环境变量注入，未写入仓库、报告或文档。

## 协议和 Java 依据

- Java Connector/J 2.4.14 的 `AbstractQueryProtocol.fetchRowViaCursorForOracle` 对 Oracle
  server cursor 使用 offset fetch；REF CURSOR 当前不支持 `OCI_FETCH_ABSOLUTE`，不能据此推导输入
  或双向绑定协议。
- Java 参考测试没有 `SELECT CURSOR(...)` 结果列的真实 SQL 集成用例。
- 因此本次不实现 REF CURSOR Input/InputOutput，也不把输出 cursor ID 直接当作输入编码。

## 运行边界

- SDK：`.NET 11.0.100-rc.3`；目标框架：`net10.0`；运行时：`.NET 10.0.14`。
- profile：Oracle 直连，TLS Disabled；资源矩阵使用单端点、`MaxPoolSize=1` 的池化配置。
- 测试使用用户提供的 Oracle 租户；连接信息和凭据未进入版本库或报告文本。

## 结果

| 范围 | 结果 | 说明 |
| --- | --- | --- |
| Oracle 池资源生命周期 | 32/32 通过 | 持久 BLOB/CLOB、句柄/流、open/partial/complete/disposed、同步/异步、同端点复用，以及 Oracle 输出 cursor 退休隔离 |
| Oracle REF CURSOR 输出矩阵 | 15/16 通过 | 输出、分页、SingleRow/SingleResult、命令释放、连接复用和同步/异步路径通过 |
| `PreparedCursorColumnSurvivesParentCommandDisposal` | 1 失败 | `PrepareAsync` 成功；执行 `SELECT CURSOR(SELECT 7 AS value FROM DUAL) AS c FROM DUAL` 时服务端返回 `902/HY000`，未返回父行或子 cursor |

清洁报告为：

- [`oracle-pool-resource-live.trx`](../../tests/OceanBaseConnector.IntegrationTests/TestResults/oracle-pool-resource-live.trx)：32/32 通过；
- [`oracle-refcursor-live-clean.trx`](../../tests/OceanBaseConnector.IntegrationTests/TestResults/oracle-refcursor-live-clean.trx)：15/16 通过。

## 当前结论

资源所有权和失败会话退休在该 Oracle 直连 profile 上已有真实证据，但不能推及 OBProxy、其他版本、TLS 或跨平台部署。
REF CURSOR 输出路径的分页、释放和连接复用已有 15 项真实证据；`SELECT CURSOR(...)` 结果列是当前服务端形状限制，
不能用客户端 fallback 掩盖，也不能把这 1 项写成驱动已支持。

REF CURSOR Input/InputOutput、复杂 cursor 输入和其他没有 Java/OceanBase 输入协议依据的形状继续明确拒绝。
