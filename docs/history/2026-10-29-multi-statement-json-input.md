# MySQL 多语句 JSON 输入（2026-10-29）

## OceanBase / 官方客户端依据

以 OceanBase Connector/J 2.4.14、源码 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1` 对照：
`ColumnType.JSON` 的 JDBC 映射为 VARCHAR 和 `java.lang.String`，普通 `setString` 使用客户端字符串编码，
不要求原生 MySQL 的专用 JSON 字面量或客户端 JSON 解析器。

同一 OceanBase 4.4.2.2 MySQL 直连租户上，Java 多语句插入 JSON 文档、JSON null、SQL NULL 和转义
Unicode，通过 UTF-8、GB18030、windows-1252 各两项往返及存储检查，共 6/6。
`useServerPrepStmts=false/true` 两个选项均实际返回 `ClientSidePreparedStatement`，这是文本路径证据，
不宣称取得了二进制协议对照。

## 实现及保留限制

- 只扩展 `ClientPreparedSql` 的多语句 provider 类型白名单，接受 MySQL `OceanBaseDbType.Json` Input。
- 内容沿用普通字符串的可信会话字符集上下文、严格编码、十六进制/REPLACE 字面量、参数快照和 SQL 包预算。
  值、collation/coercibility 及 SQL_MODE 切换语义沿用既有实现。
- JSON null 仍是字符串 `null`；SQL NULL 是空值参数。非法 JSON 留给服务端校验，保持与现有二进制输入契约一致。
- Oracle JSON、非零 Precision/Scale、输出、单语句 client fallback、Prepare/cursor/stream/复杂输入限制保持。
  字符串绑定的脚本仍不得改变解析字符集或执行不受支持的动态 SQL/过程。
- 国民字符输入使用不同的 Java 字面量路径，本次不以普通字符串代替该类型。

## 测试与走查

- 新增单测 3 项在原实现白名单处 RED，另 1 项已有模式/声明/fallback 限制检查通过。
- 新增公开 DLL 消费者 12 项全部 RED；扩展既有三个代表资产、同步/异步、TCP/合成 OB20 样例，
  检查实际发送文本、typed SQL NULL、更新计数和连接复用。复用的 fixture 清理等待服务端任务结束。
- 新增实库 52 项：50 项因原实现拒绝 JSON 输入 RED，2 项本地拒绝边界已通过。
  48 项覆盖六种客户端字符集（utf8mb4/ascii/latin1/utf8mb3/gbk/gb18030）、同步/异步、
  两种初始 SQL_MODE 与中途反转、普通/顺序结果读取；实际读取 JSON 表列、类型和多结果，
  区分 JSON null/SQL NULL，并验证 Unicode、引号、反斜杠、NUL 和参数标记文本。
  另 4 项验证非法 JSON 的已确认前缀/未执行后缀与后续连接复用、本地编码/声明/字符集拒绝无目标副作用。
- GREEN：相关单测 259/259；公开 DLL 消费者相关组 72/72；双模式多语句、类型及 JSON 字符集实库
  202/202（其中多语句组 164/164）；DI 14/14；五目标解决方案 Release 构建零警告、零错误。
- 独立最终走查未发现 Critical/Important/Minor 问题；确认复用的快照、包预算及 literal
  collation/coercibility 回归覆盖共享路径。完整默认单测 8,120/8,120，零失败、零跳过，耗时 8 分 19 秒。
  在沙箱外执行，以避开此前已确认的 `File.Replace` 沙箱拒绝；不启用实库或 SecurityTests。

本地证据为 ignored `TestResults/multi-json/*.trx`、`json-*.log`；Java 源码、secret-safe 包装脚本和日志
为 ignored `TestResults/java-gap-probe/MultiJsonProbe.java`、`RunMultiJsonMySql.ps1`、`multi-json-mysql.log`。
凭据只从现有 user-secrets 读取，经 stdin 传入 Java；不记录连接串、凭据或原始异常 SQL。

## 未关闭范围

实库证据仅覆盖 Windows/.NET 10、OceanBase 4.4.2.2 双模式直连、TLS Disabled；新增 JSON 能力仅 MySQL。
真实 OB20、OBProxy、TLS/KDC、其他版本/平台、完整故障和资源矩阵未验收；P1-01 及全部缺口矩阵保持开放。
