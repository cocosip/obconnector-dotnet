# 缺口完成情况复核（2026-10-29）

> 本报告保留 `7fc4d85` 加修正前工作区的检查点。后续 national 输入修正、最终门禁和提交状态见
> [模块交付记录](2026-10-29-multi-statement-national-input.md)；下文的失败记录不是修正后当前状态。
> 续接复核：national 多语句已由 `f011212` 修复；显式 national 标量编码/结果的后续扩展及剩余边界见
> [编码交付记录](2026-10-29-national-character-encodings.md)。以下原始表格与失败现场保留，不冒充最新测试。

## 结论与复核范围

**没有全部完成。** 当前源码已有大量受支持能力，但仍有受限实现、一个尚未完成的 national 多语句输入草稿，以及未闭合的真实环境验收。
原 23 个编号还包含已明确排除或移除的功能，不能把它们全部算成待开发功能，也不能用局部测试通过宣称全部关闭。

本次基线为 `master/7fc4d85` 加现有工作区改动。复核了当前[完整性清单](../obconnector-dotnet-completeness.md)、
[功能矩阵](../obconnector-dotnet-progress.md)、[范围审计](../obconnector-dotnet-scope-audit.md)、实现入口、拒绝分支和相关回归。
这是完成情况复核，不是全部并发路径的正确性审计。本次没有修改功能代码、提交或推送。
历史实库记录仅作为已有证据引用，不作为本次重新运行结果。

## 原 23 项逐项核对

“已有实现，欠验收”表示已找到对应实现，不表示已证明所有场景正确；“受限”也不意味着必须扩展当前产品承诺。

| ID | 源码核对入口 | 当前判断 | 尚未关闭的事项或范围边界 |
| --- | --- | --- | --- |
| P0-01 连接池 | [PhysicalSessionPool](../../src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs)、[OceanBaseDataSource](../../src/OceanBaseConnector/OceanBaseDataSource.cs) | 已有实现，欠验收 | 容量、等待、reset、预热及关闭路径已有；长期资源上限、完整并发交错和部署矩阵未闭合。 |
| P0-02 会话隔离 | [PhysicalSession.ResetCore](../../src/OceanBaseConnector/Internal/Session/PhysicalSession.cs)、[CommandExchange.BeginSend](../../src/OceanBaseConnector/Internal/Session/CommandExchange.cs) | 受限，欠验收 | reset/代际和可识别状态 SQL 退休已有；任意隐式副作用、完整 LOB/cursor/reader 中断交错及代理 reset 不在现有通过证据之内。 |
| P0-03 Ambient | [AmbientEnlistment](../../src/OceanBaseConnector/Internal/Session/AmbientEnlistment.cs) | 单资源已实现，欠故障验收 | 使用 PSPE，`Promote()` 明确抛错；分布式提升不是已承诺能力。管理器崩溃、in-doubt、代理故障矩阵未闭合。 |
| P0-04 Unknown outcome | [CommandExchange.Fail](../../src/OceanBaseConnector/Internal/Session/CommandExchange.cs)、[OceanBaseException](../../src/OceanBaseConnector/OceanBaseException.cs) | 已有实现，欠验收 | 按执行帧是否开始写入区分本地 pre-send 与 unknown，失败租约销毁；完整发送/执行/取消及服务器/代理故障边界未闭合。 |
| P0-05 活动连接恢复 | [PhysicalSession.Break](../../src/OceanBaseConnector/Internal/Session/PhysicalSession.cs)、[OceanBaseConnection](../../src/OceanBaseConnector/OceanBaseConnection.cs) | 显式重建已有，欠验收 | 已有 Broken 和 Close/Open；透明迁移、自动重连及未知写 replay 没有当前承诺，真实节点/代理恢复仍欠验收。 |
| P0-06 池路由隔离 | [PhysicalSessionPool](../../src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs)、[DiscoverySnapshot](../../src/OceanBaseConnector/Internal/DiscoverySnapshot.cs) | 已有实现，欠验收 | resolver/路由快照及闲置会话退休已有；独立多节点/代理 HA、压力和 DNS/IP 身份边界仍未闭合。 |
| P1-01 多语句 | [ClientPreparedSql](../../src/OceanBaseConnector/Internal/Sql/ClientPreparedSql.cs)、[OceanBaseCommand](../../src/OceanBaseConnector/OceanBaseCommand.cs) | 部分实现；national 草稿未完成 | 普通标量、时间、区间、时区时间及 MySQL JSON 路径已存在；Prepare/cursor/输出、stream/复杂输入仍拒绝。现有 national 草稿另有本次复现的同步/异步失败，见下节。 |
| P1-02 压缩/OB20 | [Ob20Packet](../../src/OceanBaseConnector/Internal/Protocol/Ob20Packet.cs)、[Ob20Stream](../../src/OceanBaseConnector/Internal/Protocol/Ob20Stream.cs)、[MySqlCompressionStream](../../src/OceanBaseConnector/Internal/Protocol/MySqlCompressionStream.cs) | 普通路径已有；真实验收未完成 | 压缩 OB20 和 proxy reroute 有显式拒绝，不是隐藏的已支持能力；真实压缩帧、OB20/trace、OBProxy/TLS 组合未闭合。 |
| P1-03 LOCAL INFILE | [CommandExchange](../../src/OceanBaseConnector/Internal/Session/CommandExchange.cs)、[源流 I/O](../../src/OceanBaseConnector/Internal/Session/CommandExchange.Io.cs) | 已有实现，欠验收 | 应用流与授权边界已有；同步源阻塞、其他中断组合及代理策略仍欠验收。同步 `Stream.Read` 不是可强制中断任意用户流的保证。 |
| P1-04 TLS/mTLS | [TlsHandshakeFlow](../../src/OceanBaseConnector/Internal/Transport/TlsHandshakeFlow.cs)、[TlsUpgrade](../../src/OceanBaseConnector/Internal/Transport/TlsUpgrade.cs) | 已有实现，欠真实安全验收 | TLS 升级、证书、回调和撤销选项已有；企业 CA、负向握手、证书轮换、平台撤销未完整验收。 |
| P1-05 Command/Batch | [OceanBaseCommand](../../src/OceanBaseConnector/OceanBaseCommand.cs)、[OceanBaseBatch](../../src/OceanBaseConnector/OceanBaseBatch.cs)、[MySqlCommandPlan](../../src/OceanBaseConnector/Internal/Sql/MySqlCommandPlan.cs) | 受限，欠验收 | SchemaOnly/KeyInfo 限单语句及特定参数方向；MySQL 函数只支持单 ReturnValue+Input，完整行为/结果形状/权限矩阵未闭合。 |
| P1-06 Oracle 输出/游标 | [OracleCommandPlan](../../src/OceanBaseConnector/Internal/Sql/OracleCommandPlan.cs)、[ResultColumn](../../src/OceanBaseConnector/Internal/Protocol/ResultColumn.cs) | 部分实现 | OUT/RETURNING 和四字节游标 ID 已有；输出 offset 拒绝；Input/InputOutput REF CURSOR 明确不支持。`SELECT CURSOR(...)` 的既有失败不能算完成。 |
| P1-07 复杂类型 | [ComplexValueCodec](../../src/OceanBaseConnector/Internal/Values/ComplexValueCodec.cs)、[ResultColumn](../../src/OceanBaseConnector/Internal/Protocol/ResultColumn.cs)、[ComplexTypes](../../src/OceanBaseConnector/OceanBaseConnection.ComplexTypes.cs) | 已有受支持子集，仍有明确拒绝 | 文本复杂结果要求 binary 而拒绝，命名 LOB 成员拒绝 locator，复杂 cursor 输入无编码；未解析输出身份、真实 SQL/DDL/资源及部署矩阵未闭合。 |
| P1-08 类型/字符集 | [NationalCharacterSet](../../src/OceanBaseConnector/Internal/Protocol/NationalCharacterSet.cs)、[ComplexTypes](../../src/OceanBaseConnector/OceanBaseConnection.ComplexTypes.cs)、[ResultColumn](../../src/OceanBaseConnector/Internal/Protocol/ResultColumn.cs) | 部分实现 | national 结果只接受受支持的可信编码；stored complex national 限 AL16UTF16；扩展元数据仍拒绝。Oracle SJIS/Big5 和二进制非空 TZ 名称另有服务端限制记录。 |
| P1-09 HA/发现 | [EndpointAttemptPlan](../../src/OceanBaseConnector/Internal/EndpointAttemptPlan.cs)、[TNS resolver](../../src/OceanBaseConnector/OceanBaseTnsAddressResolver.cs)、[OCP resolver](../../src/OceanBaseConnector/OceanBaseOcpAddressResolver.cs) | 已有实现，欠验收 | Open 阶段 TCP 候选/重试、发现和黑名单已有；TCP 健康不证明数据库健康，真实发现服务/节点/代理故障未完整验收。 |
| P1-10 Schema | [RoutineParameterDerivation](../../src/OceanBaseConnector/Internal/Sql/RoutineParameterDerivation.cs)、[Schema](../../src/OceanBaseConnector/OceanBaseConnection.Schema.cs) | 已有实现，类型及验收受限 | `TYPE_SUBNAME` 非空的包内 PL/SQL 参数类型明确拒绝；View/Synonym 的既有权限失败仍须在满足权限后复验。 |
| P1-11 凭据轮换 | [CredentialAcquisition](../../src/OceanBaseConnector/Internal/CredentialAcquisition.cs)、[池凭据版本](../../src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.Credentials.cs) | 已有实现，欠验收 | Open 获取凭据、版本检查和旧会话退休已有；真实 provider/plugin/KDC 和刷新/轮换服务仍欠验收。 |
| P2-01 DataAdapter/CommandBuilder | [OceanBaseProviderFactory](../../src/OceanBaseConnector/OceanBaseProviderFactory.cs)、[范围审计](../obconnector-dotnet-scope-audit.md) | 已移出范围 | 当前 factory 不提供对应入口；应记作范围移除，不能记为功能开发完成。 |
| P2-02 Statement cache | [PhysicalSession.PreparedStatements](../../src/OceanBaseConnector/Internal/Session/PhysicalSession.PreparedStatements.cs) | 已有实现，欠验收 | idle LRU、容量及代际/上下文失效已有；其他外部 DDL/reprepare、资源和负载矩阵仍欠验收。 |
| P2-03 批处理优化 | [Batch INSERT rewrite](../../src/OceanBaseConnector/OceanBaseBatch.InsertRewrite.cs)、[OracleArrayBindingPlan](../../src/OceanBaseConnector/Internal/Sql/OracleArrayBindingPlan.cs) | 已有受支持路径，欠验收 | rewrite/array binding 已有；multi-send/pipelining/独立 bulk 已明确排除，其他部分失败/触发器/警告/吞吐仍欠验收。 |
| P2-04 诊断 | [DriverDiagnostics](../../src/OceanBaseConnector/Internal/DriverDiagnostics.cs)、[Session I/O](../../src/OceanBaseConnector/Internal/DriverDiagnostics.SessionIo.cs) | 已有实现，欠负载验收 | 日志/Activity/Meter 及既定指标已有；真实负载、资源曲线和监听开销未闭合，不新增未定义等待点作为开发目标。 |
| P2-05 TFM/生态 | [项目目标框架](../../src/OceanBaseConnector/OceanBaseConnector.csproj)、[包消费证据](2026-10-28-package-consumption.md) | 五目标可构建；完整发布矩阵未完成 | 本次五目标构建成功；既有 Windows 包消费证据不能外推 Linux/macOS、完整 ORM 或所有运行时功能。 |
| P2-06 真实兼容性 | [Acceptance cases](../../tests/OceanBaseConnector.Acceptance/DatabaseCases.cs)、[SecurityTests 边界](../../tests/OceanBaseConnector.SecurityTests/README.md) | 未完成 | TLS/auth 仍有 DeferredReason；完整 OBProxy/真实 OB20/压缩/TLS/auth/HA/版本/平台/资源及发布审查证据未闭合。 |

## 本次可复现的未完成点：national 多语句输入

工作区增加了 NChar/NVarchar2 白名单与 Unicode 字面量生成，但还没有完成执行路径：

- [同步 OceanBaseCommand](../../src/OceanBaseConnector/OceanBaseCommand.cs#L120) 和
  [异步路径](../../src/OceanBaseConnector/OceanBaseCommand.cs#L202)仍对 national 参数调用 `PreparedParameters.Encode`，
  即使实际请求最后采用 SQL 文本，二进制参数缓冲区也不会发送。
- [NationalCharacterSet.ParameterEncoding](../../src/OceanBaseConnector/Internal/Protocol/NationalCharacterSet.cs#L17)
  对 unsupported national 状态抛 `NotSupportedException`，因此已经生成的自包含 Unicode 字面量仍被阻断。
- [回归用例](../../tests/OceanBaseConnector.UnitTests/OracleMultiStatementParameterTests.cs#L45)
  `OracleNationalMultiStatementsDoNotUseBinaryEncodingForUnicodeLiterals` 同步/异步两项均在本次新构建后失败，
  错误为 `The session national character encoding is not supported.`。

不能据字面量 helper 或较早产物的通过结果关闭该功能。[草稿证据](2026-10-29-multi-statement-national-input.md)
仍标记最终门禁执行中，其中“文本字面量不执行未使用的二进制 national 参数编码”尚不符合当前源码。
此外，national 后续结果没有新的可信编码事实时仍会拒绝解码，这是既有安全边界，不应靠复用旧状态或猜测 collation 绕过。

## 本次实际执行

环境：当前 Windows 主机，Release，UnitTests 的 net10.0；未启用真实数据库或 SecurityTests。

| 验证 | 本次结果 |
| --- | --- |
| UnitTests 项目构建，`--no-restore -m:1 -nr:false -p:UseSharedCompilation=false` | 成功，0 警告、0 错误 |
| 全解决方案同参数构建，覆盖 netstandard2.0/2.1、net8.0/9.0/10.0 | 成功，0 警告、0 错误 |
| ClientPreparedSql helper 首轮过滤 | 35/35；部分测试文件实际声明为 `TextCommandTests`，本轮不能作为执行路径覆盖，随后纠正过滤器 |
| `ClientPreparedSqlTests`、名称含 `MultiStatements`、national fresh-facts 用例 | **93 通过、2 失败、0 跳过，共 95**；失败即上述 national 同步/异步用例 |
| OB20 packet/MySQL compression、文本复杂结果拒绝、national 存储拒绝、ambient 第二资源拒绝、national fresh-facts | 87/87，验证支持/拒绝边界；不代表真实协议部署通过 |
| REF CURSOR 输入拒绝及部分 national 输出边界 | 8/8；与上一组存在用例重叠，不相加当作独立覆盖数 |

本次 TRX 保存在 ignored `TestResults/gap-review-2026-10-29/`：
`gap-review-client-parameters.trx`、`gap-review-multi-statement-execution.trx`、
`gap-review-support-boundaries.trx`、`gap-review-oracle-rejections.trx`。

## 历史证据与下一步判断

- 普通多语句时间、INTERVAL、TZ/LTZ 和 MySQL JSON 的实现可在当前源码找到；对应提交为
  `25603a9`、`23a1dab`、`e1b187a`、`7fc4d85`，不能再沿用早期“均未支持”的描述。
- [直连失败归因](2026-10-28-direct-failure-triage.md)保留 Oracle SJIS/Big5、nested-table、
  `SELECT CURSOR(...)`、View/Synonym 权限限制，并有官方 Java 对照；这些不能一律归类为 .NET 漏实现。
- [OB20 验收记录](2026-10-27-ob20-trace-acceptance.md)显示直连实际为普通 MySQL 协议；
  fallback 成功不能关闭真实 OB20/trace 门禁。
- 最近的功能交付并没有完成全部八个工作包。应先完成 national 草稿的执行修正及最终门禁，
  再分别推进明确受支持形状和实际环境验收；保持已排除功能的拒绝契约。

本次未重跑完整默认单测、完整消费者、真实数据库、SecurityTests、OBProxy 或跨平台矩阵。
因此可确认“没有全部完成”，但不据本次局部复核新增生产兼容或全量回归通过声明。
