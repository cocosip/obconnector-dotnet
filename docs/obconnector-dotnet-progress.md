# SDK 功能、能力与支持边界矩阵

更新：2026-10-29，源码基线`1705c1b`。本文件是当前功能范围的唯一入口；
[依据与清理决策](obconnector-dotnet-scope-audit.md)固定官方文档/Java版本并解释O/J编号，
[缺失矩阵](obconnector-dotnet-completeness.md)列开发与验收差距，
[验收标准](acceptance-criteria.md)定义完成条件，[兼容性](compatibility.md)保留环境阻塞。

**当前是实验性驱动，不承诺生产级全功能。** 下表“有实现/部分”是源码状态，
“直连子集”引用历史4.4.2.2记录，不是本次重跑或全功能验收。任何部署支持均须绑定
`服务端版本+租户模式+直连/ODP+协议+TLS/认证+客户端OS/runtime+选项`，不能只有“支持Oracle/MySQL”。

范围：**核心**为必须正确的ADO.NET能力；**可选**为显式选项/特定模式扩展；
**候选**为有服务端或Java依据但未纳入当前承诺的扩展；**排除**不构成待开发项。
“—”表示该模式不适用该特定接口，不表示该服务端没有类似SQL能力。

## 连接、会话、安全与部署

| ID / 能力 | 范围 | MySQL模式 | Oracle模式 | .NET实现入口 | 证据/验收状态 |
| --- | --- | --- | --- | --- | --- |
| C01 身份/模式/产品版本 | 核心 | 认证后识别MySql | 认证后识别Oracle | Internal/Session/HandshakeFlow、HandshakeFlow.Identity | O1/O5/J1；双模式直连子集 |
| C02 同步/异步Open/Close、factory | 核心 | 有实现 | 有实现 | OceanBaseConnection、OceanBaseProviderFactory | J2；connection组/消费者 |
| C03 现代DataSource/DI | 可选ADO.NET适配 | 有实现 | 有实现 | OceanBaseDataSource/Builder、DependencyInjection | 仅net8/9/10资产；不是服务端新能力 |
| C04 ChangeDatabase | 核心差异 | 有实现，需目标库权限 | 明确拒绝，不模拟切schema | OceanBaseConnection.Database | 直连alternateDatabase未配置跳过保留 |
| C05 TLS验证/主机名/撤销 | 核心安全，部署可选 | VerifyFull默认 | VerifyFull默认 | Internal/Transport/TlsUpgrade、TlsHandshakeFlow | O7/J12；当前真实直连 profile 为 TLS Disabled，服务端前提缺失，暂不开发 |
| C06 客户端证书/mTLS | 可选 | 有实现 | 有实现 | OceanBaseConnection.ClientCertificate、TlsUpgrade | 当前无 TLS/mTLS 端点；暂不开发，保留真实验收 Deferred |
| C07 native password | 核心 | 有实现 | 有实现，共享认证协议 | AuthenticationConversation、NativePassword | J9；直连仅已配置账号，不是全部认证矩阵 |
| C08 SHA256/caching-SHA2/old/clear/Ed25519/PAM/GSSAPI | 条件互操作分支 | 有实现，不承诺服务端均支持 | 同左 | AuthenticationConversation、GssapiAuthentication | 当前仅 native 账号直连，无插件/KDC端点；暂不开发非native分支 |
| C09 凭据provider与版本退休 | 可选ADO.NET适配 | 有实现 | 有实现 | IOceanBaseCredentialProvider、CredentialAcquisition、PhysicalSessionPool.Credentials | J9；每次Open取值，不是活动会话实时改密 |
| C10 物理连接池/容量/等待/预热/清池 | 可选 | opt-in实现 | opt-in实现 | PhysicalSessionPool、OceanBaseConnection.Pooling | J11；直连子集，长期压力未完成 |
| C11 reset/租约/资源隔离 | 核心复用正确性 | 有实现，无法证明状态则退休 | 同左，含Oracle资源 | PhysicalSession、PhysicalSessionPool | 不保证任意隐式副作用；见池隔离历史 |
| C12 endpoint/resolver/路由身份 | 可选 | 有实现 | 有实现 | EndpointSelector、DiscoverySnapshot、IOceanBaseAddressResolver | 当前无真实 proxy/多节点 profile；受控单上游路由不升级为 HA，暂不开发 |
| C13 TNS/OCP/组/权重/黑名单 | 可选 | 依部署地址发现 | 同左，不使用Oracle Net | OceanBaseTnsAddressResolver、OceanBaseOcpAddressResolver | 当前无真实 OCP/TNS/ODP 服务；暂不开发，保留 parser 和 Deferred |
| C14 TCP重试/健康缓存 | 可选 | 建连TCP失败候选切换 | 同左 | TcpTransport、EndpointHealthCache | 不重试TLS/auth/已发SQL；TCP健康不是数据库健康 |
| C15 自动重放/活动会话迁移 | 排除 | 不支持 | 不支持 | Broken/Close/Open安全路径 | Java有限重放不要求.NET复制；应用显式恢复 |
| C16 配置解析/脱敏/信任材料 | 核心 | 严格键/范围/重复同义键与脱敏 | 同左 | OceanBaseConnectionStringBuilder、OceanBaseConnectionOptions | 凭据和策略隔离；RSA公钥/允许获取是认证分支选项，不替代TLS或服务端plugin支持 |

源码入口均相对[产品目录](../src/OceanBaseConnector)。完整配置键见[README](../README.md)，
字符集和凭据契约分别见[编码](client-character-sets.md)、[凭据](credentials.md)。

## 执行、参数、结果与批处理

| ID / 能力 | 范围 | MySQL模式 | Oracle模式 | .NET入口 | 证据/验收边界 |
| --- | --- | --- | --- | --- | --- |
| Q01 单语句text/CRUD/DDL/错误 | 核心 | 有实现 | 有实现，仅Oracle兼容SQL | OceanBaseCommand、CommandExchange、ErrPacket | O2/O3/J2；服务端SQL错误不由驱动模拟；TableDirect拒绝 |
| Q02 参数绑定/重复占位/NULL/方向 | 核心 | 位置绑定、过程IN/OUT/INOUT | 命名绑定、标量IN/OUT/INOUT | ParameterBindingPlan、PreparedParameters、OceanBaseParameter | 参数方向与值类型分别验证；不等于全部组合支持 |
| Q03 Prepare/PrepareAsync/重执行 | 核心 | 服务端prepare | 服务端prepare；可选PrepareExecute | PreparedStatement、OraclePrepareExecutePacket | J1/J4；版本/能力门槛不能用客户端期望替代 |
| Q04 普通客户端prepare回退 | 可选 | 明确1295拒绝后限定标量/JSON/BIT/YEAR/时间 | 普通标量、DATE/TIMESTAMP/时区、INTERVAL、NCHAR/NVARCHAR2等已有字面量子集 | ClientPreparedSql、CommandExchange.ClientPreparedSql | 显式Prepare/输出/cursor/stream等不回退；原生1295未复现 |
| Q05 文本多语句/参数/多结果 | 可选opt-in | 普通标量、JSON/BIT/YEAR/时间/二进制流 | 普通标量/national/时间/interval/二进制流 | ClientPreparedSql、SqlStatementBoundary | J5；Prepare/cursor/输出/复杂输入拒绝；展开SQL有界 |
| Q06 存储过程/函数 | 核心模式差异 | 过程IN/OUT/INOUT；函数单ReturnValue+Input | PL块/过程/函数/RETURNING输出子集 | MySqlCommandPlan、OracleCommandPlan | J4；MySQL函数不混OUT，元数据/权限有条件 |
| Q07 参数元数据派生 | 可选ADO.NET适配 | 可见唯一签名 | 可见唯一签名 | OceanBaseConnection.Parameters、RoutineParameterDerivation | 不执行目标；歧义/包内PL类型等拒绝 |
| Q08 Reader/NextResult/typed getters | 核心 | 有实现 | 有实现 | OceanBaseDataReader、QueryResult、ResultColumn | metadata、NULL、精度与类型身份须一致 |
| Q09 SequentialAccess/流式读取 | 核心资源 | 有实现 | 有实现，含LOB资源 | SequentialRow、LobReadStream、LobTextReader | 半读/取消/关闭/池租约子集验收，不等于全部资源交错 |
| Q10 cursor fetch/page/offset | 可选 | prepared cursor分页 | prepared cursor分页，offset有条件 | QueryResult.Cursor、OracleCursorOwner | 普通cursor与PL输出不同；输出offset拒绝 |
| Q11 REF CURSOR结果/OUT | 可选Oracle | — | 四字节cursor/OUT实现 | OceanBaseRefCursor、QueryResult.OraclePrepareExecute | J4；SELECT CURSOR列当前实库形状失败，不能整体称支持 |
| Q12 REF CURSOR IN/INOUT | 排除当前范围 | — | 发送前拒绝 | OracleCommandPlan/参数校验 | 无参考writer依据；不影响普通标量INOUT |
| Q13 CommandBehavior | ADO.NET契约 | SchemaOnly/KeyInfo/SingleRow/SingleResult/CloseConnection子集 | 同左 | OceanBaseCommand.KeyInfo、KeyInfoQuery | SchemaOnly不执行、不上传；KeyInfo仅证明的单表形状扩隐藏键 |
| Q14 DbBatch顺序执行/结果 | 可选ADO.NET适配 | 有实现 | 有实现 | OceanBaseBatch、OceanBaseBatchReader | 仅net8/9/10；失败前缀/未执行后缀清晰，不承诺原子批次 |
| Q15 INSERT rewrite | 可选性能 | opt-in受限INSERT | — | OceanBaseBatch.InsertRewrite、MySqlInsertRewrite | J5；触发器/默认值/事务已有直连子集，完整吞吐待测 |
| Q16 DML数组绑定 | 可选性能 | — | opt-in已有实现/状态 | OceanBaseCommand.ArrayBinding、OracleArrayBindingPlan | J4；标量参数数组不等于命名collection值；offset仍拒绝 |
| Q17 prepared statement cache | 可选性能 | opt-in idle LRU | 同左 | PhysicalSession.PreparedStatements | J5；DDL/reset/上下文失效；外部DDL特定错误/负载待验收 |
| Q18 LOCAL INFILE | 可选MySQL | 显式授权+应用流 | — | CommandExchange.Payload、LocalInfile接口 | J13；服务端文件名不得直接打开本地文件；源归属/限额/中断 |
| Q19 参数大值分片/INOUT二进制流 | 可选 | prepared上传已有实现 | prepared/PrepareExecute上传已有实现 | CommandExchange.LongData/PieceData、PreparedParameters | 多语句流先有界展开；普通回退不等价 |
| Q20 multi-send/pipelining | 候选 | Java已有，.NET未实现 | Java路径需分模式核验 | 无产品入口 | 当前无目标OB服务端 profile 和收益证据，暂不开发；不等同MariaDB bulk |
| Q21 MariaDB bulk协议 | 排除 | 无支持承诺 | — | 无产品入口 | Java说明针对MariaDB，不能冒充OceanBase批量协议 |
| Q22 Cancel/CommandTimeout/调用者取消 | 核心 | 同步/异步命令预算与取消 | 同左 | OceanBaseCommand.Cancel、CommandExchange、PhysicalSession | 取消不保证服务器未执行；发送后状态按X04处理，完整故障顺序待验收 |
| Q23 单会话活动操作/所有权 | 核心 | 活动操作冲突拒绝 | 同左 | PhysicalSession.Reserve/Acquire及reader/lease所有权 | 不承诺同一连接MARS或多线程共享command/parameter；跨连接并发走池 |

## 类型、编码与复杂值

| ID / 类型能力 | MySQL模式 | Oracle模式 | 本地映射 / 范围 | 验收边界 |
| --- | --- | --- | --- | --- |
| T01 整数/无符号/布尔 | 有符号/无符号至64位，TINYINT映射可选 | 按实际NUMBER等元数据，不假定MySQL列类型 | DbType、OceanBaseDbType；ResultColumn/PreparedParameters | 极值、NULL、溢出与元数据一致，不能只SELECT常量 |
| T02 DECIMAL/NUMBER/FLOAT | DECIMAL至65精度/30 scale约束 | NUMBER至38精度、负scale；FLOAT二进制精度 | decimal或OceanBaseDecimal；DecimalCodec | CLR decimal装不下不能静默截断；Oracle未知精度保持值 |
| T03 浮点 | FLOAT/DOUBLE | BINARY_FLOAT/BINARY_DOUBLE | float/double | 特殊值/极值和协商分别验证，不用NUMBER路径代替 |
| T04 普通字符/二进制 | CHAR/VARCHAR/TEXT/BINARY/VARBINARY/BLOB | CHAR/VARCHAR2/RAW及已支持文本/二进制 | string/byte[]、流；严格编码 | Oracle空串/NUL/RAW目标限制；长NUL脚本超时保留 |
| T05 ENUM/SET/GEOMETRY | ENUM/SET字符串；GEOMETRY原始byte[]输入/结果 | — | 复用Binary参数、type255结果；空间过程参数目录映射；无GIS领域API | SRID=0直连往返见D-06；目标SRS目录仅0，非零SRID与其他部署未验收 |
| T06 JSON | 显式Json输入、UTF8/binary结果字符串 | 无专用Oracle JSON codec承诺，可执行服务端SQL转换 | OceanBaseDbType.Json、字符串 | JSON null不同SQL NULL；服务端校验，非DOM解析库 |
| T07 BIT/YEAR | BIT1..64、四位YEAR | — | ulong/ushort及typed输入 | 常量数值metadata不同真实BIT/YEAR列 |
| T08 DATE/DATETIME/TIME | 小数至6位；TIME有符号 | Oracle DATE含时间、无小数 | DateTime/TimeSpan、OceanBaseDate/DateTime | MySQL零日期需provider值访问及相容SQL_MODE；TIME极值文本/binary差异保留 |
| T09 TIMESTAMP | 至6位与会话时区 | 至9位；TZ/LTZ | OceanBaseTimestamp、TimestampWithTimeZone、TimestampLocalTimeZone | 命名地区/二进制非空TZ名称1882阻塞，不能用文本通过覆盖 |
| T10 INTERVAL | — | YEAR TO MONTH、DAY TO SECOND | 精确provider值、IntervalCodec | 负值/精度/NULL；普通回退仅覆盖已有字面量子集 |
| T11 NCHAR/NVARCHAR2 | 不套用Oracle national协议 | 标量和有依据复杂成员子集 | string+显式类型、NationalCharacterSet | 输入/结果/存储编码不同；需可信身份/编码快照 |
| T12 ROWID/UROWID | — | 协议209结果解码与 UROWID 参数 | ROWID结果保持String；新增 Urowid 参数类型、NULL/type209 编码、目录映射 | 直连 Oracle 4.4.2.2 的输入/输出/RETURNING 已验收；DeriveParameters 受服务端目录形状限制，见唯一路线图 |
| T13 BLOB/CLOB locator | 普通BLOB byte[]/流，不用Oracle locator | locator读写/修改/回绑的限定子集 | OceanBaseLob、LobSession、LobMutation | 需PrepareExecute/locator能力；失效/旧租约/外置大值 |
| T14 命名OBJECT/collection | — | 加载描述、预装类型、嵌套值子集 | OceanBaseComplexType/Value、ComplexTypeContext/Codec | 类型DDL失效；named与anonymous、输入与结果分别验收 |
| T15 复杂LOB/cursor成员 | — | 结果读取子集；命名LOB内容/匿名locator输入子集 | ComplexValueCodec、SequentialRow.Complex | 命名locator回绑/cursor输入/未解析RETURNING身份/文本复杂结果拒绝 |
| T16 LONG/LONG RAW/BFILE/NCLOB | — | 排除专用支持 | 无专用API/模拟实现 | O4明确不支持；Java别名/枚举不能扩大范围 |
| T17 XMLType/SDO_GEOMETRY/ANYDATA | — | 服务端有条件能力，.NET专用codec未承诺 | 可使用服务端转换为已支持标量 | O8；不是现有复杂codec自动支持；列候选差距 |
| T18 向量/位图/SQL数组 | 服务端版本相关；无专用codec承诺 | 不把MySQL扩展套用此模式 | 当前Oracle数组绑定不是这些类型 | O2；需新增wire/type证据与需求才纳入 |
| T19 客户端SQL编码 | utf8mb4/mb3、ASCII、latin1、GB2312、GBK、GB18030/2022、DEC8、TIS620、EUCKR、SJIS、Big5、HKSCS/31 | 相同解析白名单，MySQL直连 SJIS/Big5 专项4/4通过 | 未列名字发送前拒绝；Oracle SJIS/Big5仍失败 | UJIS/EUCJPMS/CP932/CP850/HP8/MACROMAN/SWE7 无当前服务端证据，暂不扩展 |
| T20 结果/national编码 | 映射UTF8、UTF16/LE及已实现单/多字节collation（含GB2312 24/86、EUCKR 19/85） | 普通结果与national独立规则 | ResultCharacterSet、NationalCharacterSet | 未知collation/坏字节拒绝；GB2312/EUCKR指定直连结果/national子集见D-07，Oracle普通EUCKR客户端900仍开放；UJIS/EUCJPMS等仍未覆盖 |

类型依据O2/O4、J2/J3/J6；实际约束在[ResultColumn](../src/OceanBaseConnector/Internal/Protocol/ResultColumn.cs)、
[PreparedParameters](../src/OceanBaseConnector/Internal/Protocol/PreparedParameters.cs)和各codec中。
服务端新增类型属于候选覆盖差距，不能混成当前核心必须项或错误宣称已经支持。

## 事务、协议、元数据与运维

| ID / 能力 | 范围 | 模式/实现 | 实现入口 | 验收边界 |
| --- | --- | --- | --- | --- |
| X01 本地事务/隔离级别/savepoint | 核心 | 双模式，按模式验证SQL和级别 | OceanBaseTransaction、Connection.Transactions/Savepoints | DDL隐式提交与原生数据库差异不能隐藏 |
| X02 单资源TransactionScope | 可选ADO.NET适配 | PSPE登记/回调/超时/回滚 | AmbientEnlistment | 不是分布式ambient；提升/durable prepare明确拒绝 |
| X03 显式XA | 可选OceanBase | MySQL XA语句；Oracle DBMS_XA | OceanBaseXaResource、Connection.Xa/XaOracle | O6/J8；生命周期/recover已有实现，不承诺通用协调器 |
| X04 unknown outcome/显式重建 | 核心安全 | 请求可能已发后故障分类/Broken/新租约 | PhysicalSession、CommandExchange.Io、OceanBaseException | 不自动重放；pre-send不误报；独立查账、旧资源晚释放 |
| P01 标准wire/逻辑包限制 | 核心 | 双模式、序号/截断/长度校验 | PacketChannel、ByteCursor | 畸形包/超时/取消不污染下一借用者 |
| P02 标准压缩 | 可选协议 | 仅能力协商后使用；未声明按Java普通协议回退 | MySqlCompressionStream、ZlibBlock | 当前直连只证明回退，未证明真实压缩帧 |
| P03 OB20 | 可选协议 | 能力协商后的普通OB20 | Ob20Stream/Packet/Checksum | 显式profile实际Layer必须OB20；当前租户拒绝协商 |
| P04 Full Link Trace | 可选协议 | legacy/modern extra-info子集 | Ob20TraceContext、ProtocolDiagnostics | 服务器sampling/control、实际帧和collector分开验收 |
| P05 压缩OB20/proxy reroute | 排除当前范围 | 明确拒绝/无执行支持 | 协商/帧保护保留 | J7；取得独立协议依据再讨论，常量不是实现 |
| M01 GetSchema/列元数据 | 核心+ADO.NET适配 | 双模式实际目录查询/DataTypes | Connection.Schema.*、ResultDbColumn | 权限/大小写/重载有边界，视图/同义词1031保留 |
| M02 日志/Activity/Meter/慢操作 | 可选运维适配 | 双模式已有实现 | DriverDiagnostics、ConnectionLog | 不记录SQL/参数/凭据；慢操作包含客户端消费，不是纯服务端耗时 |
| M03 协商/帧/I/O/源流/池指标 | 可选运维适配 | 已有固定维度诊断 | ProtocolDiagnostics、DriverDiagnostics.* | 不定义猜测性其他等待点，监听开销/卸载/负载待验收 |
| R01 包/TFM/平台 | 发布门槛 | 两包五资产，现代API仅net8/9/10 | csproj、eng/Verify-Packages.ps1 | netstandard由host加载不等于独立runtime；非Windows实库欠验收 |
| R02 DataAdapter/CommandBuilder | 范围排除 | 已移除；显式命令CRUD | factory保留平台默认不支持 | 是产品选择，不是服务端不支持或Java缺同名API |
| R03 原生数据库/运维产品扩展 | 范围排除 | 不实现Oracle OCI/TNS wire、Aurora/Galera、binlog复制/备份管理器 | 无支持承诺 | 可发送合法OceanBase SQL，不承诺原生驱动替换 |

## 如何读取完成状态

源码有实现仅关闭“缺实现”，不能关闭对应的真实TLS/ODP/版本/平台门槛。
同一能力必须按输入、输出、文本、服务端prepare、回退、多语句、流/locator、同步/异步分别确认。
精确失败、跳过、尚未实现与排除项只在[缺失矩阵](obconnector-dotnet-completeness.md)维护，
旧23项和八工作包不再用于计算百分比。历史摘要见[历史证据索引](history/README.md)。
