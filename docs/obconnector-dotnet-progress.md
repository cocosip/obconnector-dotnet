# SDK 功能、能力与支持边界矩阵

更新：2026-11-03，源码基线`bc3737d`，本轮补齐 D-07f CP932指定子集；D-07e和D-08aa已本地提交。本文是当前功能范围的唯一入口；
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
| C10 物理连接池/容量/等待/预热/清池 | 可选 | opt-in实现 | opt-in实现 | PhysicalSessionPool、OceanBaseConnection.Pooling | J11；MySQL/Oracle 4.4.2.2 直连均通过 16 并发、10000 次借还的容量/复用专项；60 分钟交错、清池及句柄/线程/内存稳定性仍未验收 |
| C11 reset/租约/资源隔离 | 核心复用正确性 | 有实现，无法证明状态则退休 | 同左，含Oracle资源 | PhysicalSession、PhysicalSessionPool | Oracle reader/LOB/cursor/XA 旧租约隔离 **52/52** 通过；不保证任意隐式副作用；60 分钟交错与长期资源稳定性仍待验收 |
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
| Q02 参数绑定/重复占位/NULL/方向 | 核心 | 位置绑定、过程IN/OUT/INOUT | 命名绑定（含 BMP Unicode 字母标签）、标量IN/OUT/INOUT；中文派生及 DML/脚本直连 **6/6**（A-03g） | ParameterBindingPlan、PreparedParameters、OceanBaseParameter | 参数方向与值类型分别验证；不等于全部组合支持 |
| Q03 Prepare/PrepareAsync/重执行 | 核心 | 服务端prepare | 服务端prepare；可选PrepareExecute | PreparedStatement、OraclePrepareExecutePacket | J1/J4；版本/能力门槛不能用客户端期望替代 |
| Q04 普通客户端prepare回退 | 可选 | 明确1295拒绝后限定标量/JSON/BIT/YEAR/时间 | 普通标量、DATE/TIMESTAMP/时区、INTERVAL、NCHAR/NVARCHAR2、显式 Urowid 等已有字面量子集；`oracle-direct` 实库 Prepare/输出/时间脚本复核 **38/38** 通过，原生1295仍未复现；命名 OBJECT/collection 构造器已复用于普通/批量受控回退输入（D-01b） | ClientPreparedSql、CommandExchange.ClientPreparedSql | 显式Prepare/输出/cursor/stream等不回退；需原生1295 profile 才能完成回退实库验收 |
| Q05 文本多语句/参数/多结果 | 可选opt-in | 普通标量、JSON/BIT/YEAR/时间/二进制流 | 普通标量/national/时间/interval/二进制流；命名 OBJECT、嵌套 OBJECT 和集合输入已有指定直连子集 | ClientPreparedSql、SqlStatementBoundary | J5；Prepare/cursor/输出拒绝；复杂输入仅 D-02d1～d3 子集，集合子树 NULL OBJECT、locator 和其他未验证形状仍拒绝；展开 SQL 有界；Oracle temporal 存储见 D-02a2；引用类型名的递归 OBJECT text 多结果/半读切换见 D-04e2，其他复杂结果限制保留 |
| Q06 存储过程/函数 | 核心模式差异 | 过程IN/OUT/INOUT；函数单ReturnValue+Input | PL块/过程/函数/RETURNING输出子集 | MySqlCommandPlan、OracleCommandPlan | J4；MySQL函数不混OUT，元数据/权限有条件 |
| Q07 参数元数据派生 | 可选ADO.NET适配 | 可见唯一签名 | 可见唯一签名；未引用 BMP Unicode 字母过程名的执行与派生共用校验，中文独立过程/package 直连 sync/async **4/4**（A-03f）；中文 IN/OUT/INOUT 参数原名派生并执行及普通 DML/脚本独立读回，新增直连 sync/async **6/6**（A-03g）；带空格的引用 IN/OUT/INOUT 形参已可原名派生并按位置调用，普通过程/package/函数直连 sync/async 新增 **6/6**（A-03h）；仅大小写不同形参的客户端精确身份有本地证据，当前实库创建/调用失败不计为支持；普通 package-local `PL/SQL RECORD` 目录可见但公开绑定仍无协议契约；含 `SYS_REFCURSOR` 成员的 PL-only RECORD 在当前目标使 package INVALID，Java/.NET 同为服务端限制 | OceanBaseConnection.Parameters、RoutineParameterDerivation | 双模式函数的合成返回名已避让真实形参，exact/后缀冲突直连 sync/async 新增 **8/8**（A-03i）；不执行目标；歧义/包内PL类型等拒绝；普通 RECORD 需字段描述和成功 wire 后再实现；cursor 成员需新服务端形状后重开 |
| Q08 Reader/NextResult/typed getters | 核心 | 有实现 | 有实现 | OceanBaseDataReader、QueryResult、ResultColumn | metadata、NULL、精度与类型身份须一致 |
| Q09 SequentialAccess/流式读取 | 核心资源 | 有实现 | 有实现，含LOB资源 | SequentialRow、LobReadStream、LobTextReader | 半读/取消/关闭/池租约子集验收；已支持 multi-send SequentialAccess（D-08w），复杂字段沿用有界物化；不等于全部资源交错 |
| Q10 cursor fetch/page/offset | 可选 | prepared cursor分页 | prepared cursor分页，offset有条件 | QueryResult.Cursor、OracleCursorOwner | 普通cursor与PL输出不同；输出offset拒绝 |
| Q11 REF CURSOR结果/OUT | 可选Oracle | — | 四字节cursor/OUT实现 | OceanBaseRefCursor、QueryResult.OraclePrepareExecute | J4；当前目标版本 `SELECT CURSOR` 列形状为服务端限制且 Java 同样失败，Integration profile 通过 `capabilities.serverLimitations` 在发现期跳过该已确认形状；不能整体称支持；PL OUT/REF CURSOR 仍按独立契约验收 |
| Q12 REF CURSOR IN/INOUT | 排除当前范围 | — | 发送前拒绝 | OracleCommandPlan/参数校验 | 无参考writer依据；不影响普通标量INOUT |
| Q13 CommandBehavior | ADO.NET契约 | SchemaOnly/KeyInfo/SingleRow/SingleResult/CloseConnection子集 | 同左 | OceanBaseCommand.KeyInfo、KeyInfoQuery、OceanBaseBatchReader | SchemaOnly不执行、不上传；KeyInfo仅证明的单表形状扩隐藏键；整批 SingleRow/SingleResult 已保留 opt-in multi-send，可组合 SequentialAccess/CloseConnection/data source（D-08x） |
| Q14 DbBatch顺序执行/结果 | 可选ADO.NET适配 | 有实现 | 有实现 | OceanBaseBatch、OceanBaseBatchReader | 仅net8/9/10；失败前缀/未执行后缀清晰，不承诺原子批次 |
| Q15 INSERT rewrite | 可选性能 | opt-in受限INSERT | — | OceanBaseBatch.InsertRewrite、MySqlInsertRewrite | J5；触发器/默认值/事务已有直连子集，完整吞吐待测 |
| Q16 DML数组绑定 | 可选性能 | — | opt-in已有实现/状态 | OceanBaseCommand.ArrayBinding、OracleArrayBindingPlan | J4；标量参数数组不等于命名collection值；offset仍拒绝 |
| Q17 prepared statement cache | 可选性能 | opt-in idle LRU | 同左 | PhysicalSession.PreparedStatements | J5；DDL/reset/上下文失效；外部DDL特定错误/负载待验收 |
| Q18 LOCAL INFILE | 可选MySQL | 显式授权+应用流 | — | CommandExchange.Payload、LocalInfile接口 | J13；服务端文件名不得直接打开本地文件；源归属/限额/中断 |
| Q19 参数大值分片/INOUT二进制流 | 可选 | prepared上传已有实现 | prepared/PrepareExecute上传已有实现 | CommandExchange.LongData/PieceData、PreparedParameters | 多语句流先有界展开；普通回退不等价 |
| Q20 multi-send/pipelining | 可选opt-in子集 | Java已有；.NET 已实现默认关闭的 prepared batch multi-send 子集，MySQL Stream 已接入 long-data 发送，MySQL BLOB/TEXT binary result 已可在 batch reader 中 materialize，server prepare 返回 1295 时可回退到 client-prepared text；Oracle Stream 在该选项下回退到逐命令 piece-data；named OBJECT/collection 普通输入及 named complex binary 结果已有实现；根 locator 输入、命名 LOB 内容输入和保留调用方连接的根/命名复杂 LOB binary 结果已支持，Oracle DML RETURNING 标量 OUT/INOUT、命名复杂值及保留调用方连接的 LOB 输出已接入每条响应发布，XMLTYPE String 结果及 bound Prepare 的目录身份解析已支持（D-08q）；命名 OBJECT 标量/嵌套 text 结果已接入受控 1295 fallback（D-08r）；命名 OBJECT/collection 普通输入构造器也已接入受控 fallback（D-01b）；匿名集合 binary 结果已复用普通 reader decoder（D-08s，仅 loopback）；reader 已支持 CloseConnection/data source 的 locator 生命周期，全部响应排空后、关闭前可读，根 BLOB/CLOB 直连 **8/8**（D-08u）；普通DML匿名输入/声明输出及 cursor 成员仍拒绝 | input-only DML/scalar/reader 已有指定直连子集；Stream 回退到逐命令 piece-data，自动提交远程 DML LOB 输出读取受当前服务端事务边界限制（D-08z）；PL 同组流水化输出、cursor/array 未纳入流水化 | `UseBatchMultiSend`、`OceanBaseBatch.MultiSend` | 可缓存 input-only DML/SELECT、基本 scalar 排空、MySQL BLOB/TEXT loopback sync/async **4/4**、64 MiB 分组边界、上下文/副作用边界和 MySQL Stream 的 loopback wire 顺序已有证据；4.4.2.2 MySQL/Oracle 直连临时 profile 的 DML/scalar × sync/async **8/8 通过**，真实 MySQL Stream 存储/`GetStream` 回读 **2/2 通过**，Oracle Stream piece-data fallback 存储/LOB 读回 **2/2 通过**，已知响应错误的 prefix/error/suffix 边界 **4/4 通过**，Oracle PrepareExecute + locator Stream 安全回退 **2/2 通过**，1295 text fallback 的 sync/async loopback **2/2 通过**，第一响应成功后第二响应挂起的 caller token/batch cancel/timeout **3/3 通过**，并修复了 pipelined 响应 packet sequence 边界；Connector/J 2.4.14 的 Oracle piece-data 路径也是每片等待确认，未提供 piece-data 流水化证据，因此 .NET 不猜测增加该协议；Oracle 自动提交远程 DML LOB 读取受目标版本事务边界限制（D-08z）；complex text LOB、PL 同组流水化输出、cursor/array、代理/OB20、收益和更大错误排序矩阵仍未验收；named OBJECT/collection 普通输入已通过精确 wire/错误边界单测及 Oracle 直连 sync/async 独立存储 **4/4**（D-08i）；普通/嵌套 OBJECT binary 结果与顶层 national/LTZ 混合读取、scalar/nonquery 排空已通过 Oracle 直连 **4/4**（D-08j），collection 结果仅有 loopback 证据；根 locator 输入/结果已分别通过 Oracle 直连 **4/4**（D-08k/l），命名 LOB 内容输入 **8/8**（D-08m），命名复杂 LOB binary 结果的平面/嵌套 OBJECT **8/8**（D-08n），collection 结果仅 loopback；Oracle DML RETURNING 标量 OUT/INOUT 已通过直连显式/隐式 Prepare × sync/async **8/8**（D-08o）；命名复杂值及根/OBJECT LOB RETURNING 输出通过 Oracle 直连 sync 隐式/async 显式 Prepare **10/10**，collection 输出仅 loopback（D-08p）；普通DML匿名输入/声明输出、cursor 成员、复杂 text LOB 仍未纳入流水化；自动提交远程 DML LOB 读取受目标版本事务边界限制（D-08z）；关闭连接 reader 的读取窗口已补齐，根远程 BLOB/CLOB 直连 **8/8**（D-08u）；data source NonQuery 已支持普通 LOB 行无句柄排空，根远程 BLOB/CLOB 直连 **4/4**、匿名 binary LOB 仅 loopback（D-08v）；SequentialAccess reader 已复用流式字段与失败清理，双模式普通字段直连 **8/8**、Oracle LOB 窗口 **16/16**（含 Default 对照8）、XML **4/4**（含 Default 对照2），命名/匿名复杂顺序路径仅 loopback（D-08w）；整批 SingleRow/SingleResult 已保留 opt-in multi-send 与隐藏结果计数/RETURNING，双模式限读及 Oracle 隐藏输出直连 **10/10**，LOB 限读窗口/隐藏句柄排空仅 loopback（D-08x）；不等同 MariaDB bulk；混合 StoredProcedure/Oracle PL 状态屏障已支持，安全段仍流水化，后段预检延迟到完整routine响应结束；双模式存储/OUT及Oracle匿名集合OUT/时区变更直连 **10/10**（D-08y），不代表PL同组流水化；自有连接 Scalar 的根远程 LOB、只读 PL INOUT 和 inline RETURNING OUT 已支持，由 batch 保留原连接，释放全部句柄后方可重执行；直连16成功路径及8事务边界负向通过（D-08z），匿名复杂 Scalar 仅 loopback；mixed batch routine 屏障已支持显式根 REF CURSOR OUT/ReturnValue，调用方游标独立于batch寿命，自有连接保留至资源释放，全部响应排空前禁止FETCH；Oracle直连sync/async×调用方/自有连接 **4/4**，现代DLL消费 **6/6**（D-08aa）；不代表PL同组或SELECT/DML RETURNING cursor流水化 |
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
| T12 ROWID/UROWID | — | 协议209结果解码与 UROWID 参数 | ROWID结果保持String；新增 Urowid 参数类型、NULL/type209 编码、目录映射 | 直连 Oracle 4.4.2.2 的输入/输出/RETURNING 已验收；多语句及受控 1295 client fallback 已接入 CHARTOROWID，空字符串执行前拒绝（D-05b）；DeriveParameters 目录边界见唯一路线图 |
| T13 BLOB/CLOB locator | 普通BLOB byte[]/流，不用Oracle locator | locator读写/修改/回绑的限定子集 | OceanBaseLob、LobSession、LobMutation | 需PrepareExecute/locator能力；失效/旧租约/外置大值 |
| T14 命名OBJECT/collection | — | 加载描述、预装类型、嵌套值子集 | OceanBaseComplexType/Value、ComplexTypeContext/Codec | 类型DDL失效；named与anonymous、输入与结果分别验收；当前 OBJECT/嵌套 OBJECT NULL 输出身份为服务端限制，collection NULL 形状另有证据；named BLOB 成员 DML 在同 wire 的 Java/.NET 诊断回放均返回 932，当前目标服务端限制；空格/括号/Unicode 引用类型名的标量及递归 OBJECT text 结果已补齐普通读取、多结果及 batch 受控回退子集（D-04e2）；引号/NULL 前缀/名字自身前后空白仅有本地 parser 证据 |
| T15 复杂LOB/cursor成员 | — | 结果读取子集；命名LOB内容/匿名locator输入子集；named OBJECT 标量/递归 text 结果子集 | ComplexValueCodec、SequentialRow.Complex | Oracle named BLOB OBJECT 成员 DML 在 .NET 与 Java 同 wire 诊断回放均返回 `932/22000`，当前目标服务端限制；当前 RAW/DATE/TIMESTAMP text 成员在 .NET 与 Connector/J 收到相同重复 RAW payload，Java `getObject` 为 null，也按服务端限制跳过；命名 collection 构造 text 结果 metadata 为 `VARCHAR2`，两 SDK 都按 String 返回；CLOB/BLOB named OBJECT text 结果在 Java `getObject` 为 null、文本 payload 无成员身份，.NET 预加载描述后 sync/async **2/2** 安全拒绝，按当前服务端 wire 限制跳过；命名locator回绑/cursor输入/未解析RETURNING身份仍拒绝 |
| T16 LONG/LONG RAW/BFILE/NCLOB | — | 排除专用支持 | 无专用API/模拟实现 | O4明确不支持；Java别名/枚举不能扩大范围 |
| T17 XMLType/SDO_GEOMETRY/ANYDATA | — | XMLType 已有 4.4.2.2 直连的字符串 wire 子集：type 160 ownerless 结果按 String 返回，`SYS.XMLTYPE` metadata 有安全的 String 消费边界，`DbType.Xml` 参数按 type 253 字符串写入；NULL、metadata、同步/异步 DML 与 PL 函数结果已验收；PL XMLTYPE OUT/INOUT 在 Java/.NET 同一租户分别返回 `600/0A000`、`600/42000`，按服务端限制跳过；仍无带 owner/复杂 XML 专用 codec；SDO 已验收显式 Prepare 整列读取与 generic OBJECT/ARRAY 输入子集，仍无专用 codec；ANYDATA 在当前目标租户找不到 `SYS.ANYDATA`，Java/.NET 同 SQL 失败并按服务端限制跳过 | XMLTYPE 当前 text+binary String 子集和 `XMLSERIALIZE→CLOB` 已验证；PL 输出负向边界 sync/async **4/4**；SDO 通用 OBJECT 路径已覆盖整列读取和受限输入 | O8；XMLTYPE 带 owner 复杂 payload、坏值/最大值和跨版本部署仍待证据；SDO 专用 WKB/WKT/SRID 契约与非法几何行为仍待证据，ANYDATA 需新版本/租户提供包后重开 |
| T18 向量/位图/SQL数组 | 4.4.2.2 直连 VECTOR/ARRAY 通用 String、RoaringBitmap 通用 Binary/SQL 函数子集已验收 | 不把 MySQL 扩展套用此模式 | ResultColumn、PreparedParameters；复用现有 String/Binary，不新增专用 codec | O2；D-12a/b/c 指定子集 P4；SPARSEVECTOR、嵌套 ARRAY、专用 codec 与跨版本仍待证据；Oracle collection/数组绑定另见 T14/Q16 |
| T19 客户端SQL编码 | utf8mb4/mb3、ASCII、latin1、GB2312、GBK、GB18030/2022、DEC8、TIS620、EUCKR、SJIS、Big5、HKSCS/31、CP850、HP8、MacRoman、CP932 | 相同解析白名单，MySQL直连 SJIS/Big5 专项4/4通过 | 未列名字发送前拒绝；Oracle SJIS/Big5/CP932仍失败 | CP850/HP8/MACROMAN双模式客户端直连12/12（D-07e）；SWE7只解码结果，客户端因ASCII语法映射发送前拒绝；CP932严格映射/MySQL客户端直连2/2（D-07f），普通Oracle初始化900/42000已由Java匹配；UJIS/EUCJPMS仍待严格映射与存储证据 |
| T20 结果/national编码 | 映射UTF8、UTF16/LE及已实现单/多字节collation（含GB2312 24/86、EUCKR 19/85、CP850 4/80、HP8 6/72、MacRoman 39/53、SWE7 10/82及CP932 95/96） | 普通结果与national独立规则 | ResultCharacterSet、NationalCharacterSet | 未知collation/坏字节拒绝；GB2312/EUCKR指定直连结果/national子集见D-07；D-07e新增MySQL结果4/4、Oracle national6/6；D-07f新增CP932 MySQL canonical/duplicate结果2/2、Oracle national2/2；当前目标 Oracle 普通 EUCKR 初始化由服务端 900 限制且 Java 同交互失败；其他版本/部署及 UJIS/EUCJPMS 等仍未覆盖 |

类型依据O2/O4、J2/J3/J6；实际约束在[ResultColumn](../src/OceanBaseConnector/Internal/Protocol/ResultColumn.cs)、
[PreparedParameters](../src/OceanBaseConnector/Internal/Protocol/PreparedParameters.cs)和各codec中。
服务端新增类型属于候选覆盖差距，不能混成当前核心必须项或错误宣称已经支持。

## 事务、协议、元数据与运维

| ID / 能力 | 范围 | 模式/实现 | 实现入口 | 验收边界 |
| --- | --- | --- | --- | --- |
| X01 本地事务/隔离级别/savepoint | 核心 | 双模式，按模式验证SQL和级别 | OceanBaseTransaction、Connection.Transactions/Savepoints | DDL隐式提交与原生数据库差异不能隐藏 |
| X02 单资源TransactionScope | 可选ADO.NET适配 | PSPE登记/回调/超时/回滚 | AmbientEnlistment | 不是分布式ambient；提升/durable prepare明确拒绝 |
| X03 显式XA | 可选OceanBase | MySQL XA语句；Oracle DBMS_XA | OceanBaseXaResource、Connection.Xa/XaOracle | MySQL/Oracle 4.4.2.2 直连生命周期、recover 和池丢弃恢复 **28/28** 通过；O6/J8 的协调器、崩溃和完整 unknown-outcome 矩阵仍待验收，不承诺通用协调器 |
| X04 unknown outcome/显式重建 | 核心安全 | 请求可能已发后故障分类/Broken/新租约 | PhysicalSession、CommandExchange.Io、OceanBaseException | Oracle 直连响应丢失/relay 切断 **16/16** 通过；不自动重放、pre-send不误报、独立查账和旧资源晚释放仍需扩大到协调器/节点崩溃 |
| P01 标准wire/逻辑包限制 | 核心 | 双模式、序号/截断/长度校验 | PacketChannel、ByteCursor | 畸形包/超时/取消不污染下一借用者 |
| P02 标准压缩 | 可选协议 | 仅能力协商后使用；未声明按Java普通协议回退 | MySqlCompressionStream、ZlibBlock | 当前直连只证明回退，未证明真实压缩帧 |
| P03 OB20 | 可选协议 | 能力协商后的普通OB20 | Ob20Stream/Packet/Checksum | 显式profile实际Layer必须OB20；当前租户拒绝协商 |
| P04 Full Link Trace | 可选协议 | legacy/modern extra-info子集 | Ob20TraceContext、ProtocolDiagnostics | 服务器sampling/control、实际帧和collector分开验收 |
| P05 压缩OB20/proxy reroute | 排除当前范围 | 明确拒绝/无执行支持 | 协商/帧保护保留 | J7；取得独立协议依据再讨论，常量不是实现 |
| M01 GetSchema/列元数据 | 核心+ADO.NET适配 | 双模式实际目录查询/DataTypes | Connection.Schema.*、ResultDbColumn | Oracle 直连基础目录、过程签名和未约束 NUMBER 回归 18/18 通过；package NUMBER/DATE overload 按 `OVERLOAD` 派生并执行 sync/async 4/4 通过，引用 package 标识符派生 sync/async 2/2 通过，Oracle DATE 未知 precision metadata 已归一化；普通 package-local `PL/SQL RECORD` 目录值可读，独立探针去掉 `DATA_LEVEL=0` 后仍只有顶层行且无 `ALL_PLSQL_*` 字段布局，公开绑定发送前拒绝 sync/async 2/2，.NET/Connector-J 仍无可移植 wire/API；含 cursor 成员的 PL-only RECORD 当前 package INVALID 的回归 2/2，Java/.NET 同为服务端限制；权限/最小权限及更多标识符边界、视图/同义词1031仍保留；Oracle `ALL_ARGUMENTS.CHAR_LENGTH` 在当前目标版本缺失且 Java 同样返回904，字符长度保持服务端限制 |
| M02 日志/Activity/Meter/慢操作 | 可选运维适配 | 双模式已有实现 | DriverDiagnostics、ConnectionLog | 不记录SQL/参数/凭据；慢操作包含客户端消费，不是纯服务端耗时 |
| M03 协商/帧/I/O/源流/池指标 | 可选运维适配 | 已有固定维度诊断 | ProtocolDiagnostics、DriverDiagnostics.* | 不定义猜测性其他等待点，监听开销/卸载/负载待验收 |
| R01 包/TFM/平台 | 发布门槛 | 两包五资产，现代 API 仅 net8/9/10；当前 Windows 包/运行时子集见 A-04 | csproj、eng/Verify-Packages.ps1 | net8/9/10 独立 runtime、netstandard2.0/2.1 的 net10 host 及 netstandard2.0 的 net48 host 共 48 项双模式包实库场景通过；netstandard 不是独立 runtime，完整 API/ORM 和非 Windows 实库仍欠验收 |
| R02 DataAdapter/CommandBuilder | 范围排除 | 已移除；显式命令CRUD | factory保留平台默认不支持 | 是产品选择，不是服务端不支持或Java缺同名API |
| R03 原生数据库/运维产品扩展 | 范围排除 | 不实现Oracle OCI/TNS wire、Aurora/Galera、binlog复制/备份管理器 | 无支持承诺 | 可发送合法OceanBase SQL，不承诺原生驱动替换 |

## 如何读取完成状态

源码有实现仅关闭“缺实现”，不能关闭对应的真实TLS/ODP/版本/平台门槛。
同一能力必须按输入、输出、文本、服务端prepare、回退、多语句、流/locator、同步/异步分别确认。
精确失败、跳过、尚未实现与排除项只在[缺失矩阵](obconnector-dotnet-completeness.md)维护，
旧23项和八工作包不再用于计算百分比。历史摘要见[历史证据索引](history/README.md)。
