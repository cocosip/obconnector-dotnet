# Oracle 普通文本客户端字面量（2026-10-29）

## OceanBase / 官方客户端依据

基线 `5618f92`。Connector/J 2.4.14、源码
`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1` 的 `OBVarcharParameter.writeTo`
使用 `writeBytesEscapedQuote` 写普通引号文本，只加倍单引号；NUL 按字节保留，没有
4000 字节 guard。实际探针确认使用 `ClientSidePreparedStatement`。

本轮 Java 对照确认普通文本表达式及两个 INSERT 的独立 CLOB 存储可超过 4000 字节，
ASCII 通过 1MiB。直接引用的表达式类型是 CHAR，不能直接替换 .NET 已有 VARCHAR2 语义。
固定 `CAST(... AS VARCHAR2(4000))` 在长值上会截断；扩大 CAST 声明到 32767 仍限制
实际字节，多字节值即使写 CHAR 也截断，更长声明返回 `910/42000`。
`CONCAT(value,NULL)` 保留 VARCHAR2，但长值返回 `1489/HY000`。

`REPLACE(value,NULL,NULL)` 保留 VARCHAR2 和完整内容：ASCII 至 1MiB、引号/中文至
65536 字符的表达式及独立 CLOB 存储通过，空值仍是 SQL NULL；NUL 模式 `a\0b` 的
表达式至 196608 字符通过，12003 字符 CLOB 存储通过。更大的 NUL CLOB 存储在 Java
仍出现 `600/HY000` 或 `0/08000`，不能把长 NUL 表达式可用外推为所有 CLOB 存储可用。
随后用完全相同的两个 SELECT 脚本独立复核：12003 字符 NUL 值单条/多语句均通过；
98301 和 196608 字符单条表达式分别约 23/41ms 通过，多语句均在约 10 秒超时，
Java 返回 `0/08000`。限制涉及大 NUL 多语句处理，不能仅归因为 CLOB。

最后对完整 SQL 形状复核发现，官方 `ComQuery.addParenthesesArroundValue` 会在值前写空格。
Java prepared 成功表达式实际含此空格；不能把它等同于没有空格的 SQL。直接 Statement
对照确认 196608 字符 NUL 的 `REPLACE('…',NULL,NULL)` 单条超时，而
`REPLACE( '…',NULL,NULL)` 单条通过（带/不带外层括号均已对照）；大 NUL 多语句仍超时。
.NET 原生成的无空格单条也有 4/4 超时，随后按官方值分隔形式补一个空格并补回归，
修正后单条 4/4 通过；不能用 Java prepared 成功宣称原 .NET 无空格形状已通过。

首个 plain/CAST 探针长 NUL 存储断连后，原连接不能清理临时表。随后独立查询确认仅有
一个近期、自有命名模式的表，并核对 ID NUMBER / V CLOB 两列后清理；后续探针 finally
使用独立连接清理，均已结束。没有删除其他对象。

## 实现与契约

- 共享 `ClientPreparedSql` 普通 Oracle 字符串改用 `REPLACE( 'escaped',NULL,NULL)`，
  删除 NUL 拒绝和转义后 4000 字节限制，避免固定 CAST 截断。所有普通文本使用相同形状，
  不用客户端字节数选择可能截断数据库字节的 CAST 长度。
  值前空格沿用官方客户端形式，避免当前租户大 NUL 单条的无空格解析失败。
- 单引号仍加倍，反斜线/NUL/分号/绑定标记仍是值内容。保留严格编码、参数声明、快照、
  重复名称及展开后 64MiB SQL 预算；空字符串仍为 Oracle SQL NULL。
- 保留 VARCHAR2 身份及 trailing-space 比较语义，不能换成有 blank-padding 语义的 CHAR。
  表达式长度元数据不再固定为 4000；实际目标列、函数及服务器版本仍决定存储限制。
- 同时扩展 opt-in 文本多语句及普通 opt-in fallback；fallback 仍要求完整 `1295/HY000`
  自动准备失败，显式 Prepare/cursor/输出等拒绝不变。本轮真正实库触发 1295 未验证。
  Oracle national、byte[]/Stream 和 MySQL 字面量规则不扩展。

## 测试与走查

- 正式 RED：24 失败/1 通过；14 项命中原 4000 字节 guard，6 项命中 NUL guard，
  4 项为旧 CAST 与新 VARCHAR2 表达式断言差异。
- 首轮相关单测 213 通过/2 失败，均为遗漏更新的旧 Oracle 多语句 wire 断言；修正后复验。
- 新单测覆盖空值、trailing spaces、引号/反斜线/NUL/分号/Unicode、超过原限长及 1MiB，
  重复名称累计预算；同步/异步普通 fallback 与多语句，准备期间更改公有参数声明和值后
  仍使用原快照，更新计数与后续会话复用。
- 新实际 DLL 消费者覆盖三类代表资产、同步/异步、TCP/合成 OB20 的长文本/NUL 多语句
  及 fallback；复用已支持的多帧 fixture 和实际 next sequence。
- 新实库矩阵覆盖 VARCHAR2 结果身份及非 CHAR padding、独立 CLOB 存储、NULL/空值、
  buffered/sequential、1MiB、长 NUL 表达式、五种客户端编码及独立 UTF8 读取、
  非法编码/展开超限无任何目标副作用，以及目标列失败的事务 prefix/suffix/rollback。
- 首轮实库 325 通过/4 失败/329 总数、零跳过；4 项均为 196608 字符 NUL 的两个 SELECT
  脚本等待首个响应超时。官方 Java 对完全相同脚本独立复现；保留原失败 TRX，
  不把该服务端形状列为正向验收。最终测试改为实际共享 helper 生成的单条表达式执行两次，
  另补 12003 字符 NUL 多语句存储/结果验证，没有增加客户端 NUL 长度禁令或重放。
- 后续实库 329 通过/4 失败/333 总数、零跳过；4 项为无值前空格的共享 helper 单条。
  精确 Java 直接文本对照定位上述分隔差异后，新增独立 separator 单测先 RED 1/1，
  再修正共享 helper 和 Oracle wire 期望。完整单测还发现遗漏的旧字符集 CAST/限长断言，
  改为 Latin1/UTF8 完整字面量相等及 ASCII 严格拒绝；专项113/113通过。
- 一次独立最终走查未发现产品/测试代码 Critical/Important；文档证据表述已修正。
  最后 separator 修正由失败实库和新增单测覆盖，按 RED→GREEN 和完整回归验收。
  修正后的双模式多语句/流直连 **333/333**（本模块新增 **50/50**）、Consumer **162/162**、
  DI **14/14**、扩大相关单测 **5692/5692** 及五目标 Release 零警告/错误通过，均零跳过。
  相关单测包括全部 TextCommandTests、字面量及客户端字符集组；完整默认单测
  **8425/8425**、零失败/跳过，耗时 8分16秒，使用沙箱外执行避开已知 Windows
  File.Replace 沙箱限制。没有启用实库或 SecurityTests 的默认单测依赖。
  全部门禁完成，最后 separator 修正通过独立 RED 单测及完整回归，不再重复派审。

证据在 ignored `TestResults/oracle-text-literals/`。Java 探针在 ignored
`TestResults/java-gap-probe/`，包括 `OracleTextBoundaryProbe`、`OracleTextCastProbe`、
`OracleTextFunctionProbe`、`OracleNulMultiProbe`、`OracleNulShapeProbe`、`OracleNulDirectProbe`、
`OracleProbeCleanup` 及对应 wrapper/log。凭据仅经既有
user-secrets/stdin 使用；未输出或保存连接信息及原始敏感异常。

## 未关闭范围

仅当前 Windows/.NET10、OceanBase 4.4.2.2 Oracle 直连、TLS Disabled 的记录范围；
回归组另含 MySQL。合成 OB20 不证明真实 OB20/OBProxy/TLS 或其他版本/平台。
大 NUL 多语句的官方 Java 超时及 CLOB 存储失败不列为 .NET 已支持形状，
也不做隐藏截断、重放或 CLOB workaround。
统一 REPLACE 的长度元数据与执行计划/性能可能改变；本轮验证值和类型，不证明性能等价。
普通 opt-in fallback 沿用共享 helper 和原 1295 门禁，其他版本仍可能拒绝；真实服务器
1295 触发未验证，不外推其他版本/平台、真实 OB20/OBProxy/TLS 的支持。
不关闭整个 P1-01 或整体缺口目标；未推送。
