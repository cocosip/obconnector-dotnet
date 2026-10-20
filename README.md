# OceanBaseConnector for .NET

面向 OceanBase 的实验性 ADO.NET 驱动，使用一个 provider、一个协议栈和每连接认证后的模式状态处理 MySQL / Oracle 兼容模式。可选 DI 包在五目标提供连接注册，在现代目标另提供普通和 keyed 数据源注册。

**当前为 `0.1.0-alpha` 开发版本，尚未通过真实数据库互通验收，也未发布候选版。** 本地单元、协议环回和包消费测试不代表生产兼容证明。

## 包与目标框架

| 包 | 目标资产 | Microsoft 通用库 |
| --- | --- | --- |
| `OceanBaseConnector` | netstandard2.0、netstandard2.1、net8.0、net9.0、net10.0 | 两个 Standard / net8 使用 8.x；net9 使用 9.x；net10 使用 10.x |
| `OceanBaseConnector.DependencyInjection` | netstandard2.0、netstandard2.1、net8.0、net9.0、net10.0 | 两个 Standard / net8 使用 8.x；net9 使用 9.x；net10 使用 10.x，精确依赖同版本核心包 |

两个包统一版本、统一打包。核心仅依赖日志抽象，两个 Standard 资产另依赖 DiagnosticSource 8.0.1；现代资产使用运行时诊断 API。核心不要求 DI 容器或日志后端。Standard 目标保留基础 ADO.NET 和 DI 连接注册；`DbDataSource`、`DbBatch` 仅在三个现代目标暴露。Standard2.1 对平台已提供的 prepare、事务和资源清理异步入口另有覆盖。

## 当前能力和边界

本地已实现并测试：同步/异步连接与严格 TLS、参数校验、文本及服务端预处理执行、多结果 reader、精确数值和日期时间、事务、取消/超时、顺序字段流、只读分页游标、首批目录集合、MySQL 数据库切换、逐条 batch、数据源/DI/日志和本地追踪指标。

- 公共握手目前只接受能通过认证后状态确认的 Oracle 模式。**缺少可信的 MySQL 模式判定规则，公共 MySQL 模式连接仍被拒绝。** MySQL 的本地测试使用显式认证后夹具；设置 `ExpectedMode=MySql` 不会绕过判定。
- `Pooling=true` 被拒绝，完整会话重置及版本白名单尚未验证。默认不使用连接池。
- MySQL 二进制 Input 参数的 `Value` 可为可读 `Stream`（推断或显式 `DbType.Binary`），以固定 8 KiB 块通过 LONG_DATA 上传后执行；不在内存中拼接全流。`Size`/`Precision`/`Scale` 须为 0，执行总字节预算为 64 MiB，超限失败。源从当前位置读至 EOF，由调用方独占、释放及重新定位；`Prepare` 不读源，重复执行不自动重绕。异步使用源 `ReadAsync` 和命令总预算，源须合作取消；阻塞的同步读或忽略 token 的异步读须等待源返回。EXECUTE前的上传失败会销毁已被上传数据污染的原会话，不发送SQL；首块上传前的源失败保留可用会话。EXECUTE发送后无可信终态按结果未知处理，可信服务端ERR保留错误码和可用会话。Oracle PIECE_DATA、文本流和客户端 prepare 回退尚不支持；真实数据库/OBProxy互通及公共 MySQL 建连仍待 A05 验收。
- 五资产可按命令设置 `UseCursorOffset=true`（默认 false）并指定正 `FetchSize`，启用 Oracle >=2.2.5 的扩展 Fetch 请求；仍为向前只读 reader，REF CURSOR 保持原路径。首次执行与 PrepareExecute 共用严格 `ob_version()` 探测及物理会话缓存；低版本或不可识别版本明确拒绝。每页另消费并验证尾 OK 后才继续或完成。NEXT/零偏移请求及累计位置校验依据协议源码推导，只有本地夹具证据，真实版本/OBProxy兼容尚未验收；未提供滚动 API。
- 现代 net8/net9/net10 的活动本地事务提供 `Save(name)`、`Rollback(name)` 及对应 Async，`SupportsSavepoints=true`；MySQL 另支持 `Release(name)` / Async，Oracle 明确拒绝释放。名称限 1–30 个 ASCII 字符，首字符字母，其余字母/数字/下划线；前导下划线（例如 ORM 自动生成的 `__EFSavePoint`）、引号及 Unicode 名称暂不支持。保存点不结束事务，业务命令仍须绑定原事务；回滚到保存点后须重新取得此前的 LOB、REF CURSOR 和复杂类型描述/值。Standard 资产不补保存点 API。仅有本地协议证据，真实数据、锁、DDL、重名和不存在名称的服务端语义尚未验收。
- 默认支持文本命令和输入参数，参数化执行使用服务端 prepare。`OceanBaseCommand.AllowClientSidePrepareFallback=true` 可按命令显式启用回退：仅自动 prepare 首响应明确返回 `1295/HY000` 且完整通过协议边界验证时，在原会话/事务/超时预算内发送一次文本 SQL；不缓存失败结论，下次执行仍先 prepare。显式 `Prepare()`、FetchSize、Oracle PrepareExecute、执行阶段错误、断线和取消均不降级或重放。不支持环境 `TransactionScope`；显式 XA 的开发边界见下述 D09。locator LOB 需显式启用下述选项。
- `Server` 候选默认按声明顺序尝试；`Load Balance=RoundRobin`（builder 属性 `LoadBalance`）轮换每次显式 `Open` 的首选地址，TCP socket 建连失败时环形尝试其余地址一次。轮转状态按现代数据源共享；直接连接按实例跨 Close/Open 保留，重新设置连接串或地址源时重置。没有全局轮转状态，已有会话/事务/reader 不迁移，执行故障不自动重连或重放 SQL。
- 五资产可设置 `OceanBaseConnection.AddressResolver`，现代数据源通过 `OceanBaseDataSourceBuilder.UseAddressResolver(...)` 固定配置。应用实现 `IOceanBaseAddressResolver` 的同步 `Resolve(token)` 和异步 `ResolveAsync(token)`，每次 Open 调用匹配方法一次，返回现有 Server 语法的可信同租户地址列表（最多65536字符/256候选，省略端口继承 Port）。结果替换本次候选，空/非法/失败不回退种子或旧结果；配置 Server 仍必填并用于发现前诊断。发现、TCP、TLS和认证共用总连接预算；异步取消后不采用迟到结果，同步实现须合作取消，异步方法须及时返回 Task。resolver 及其资源由应用持有并保证并发安全，取消回调须及时返回且不抛错；不内置 OCP/TNS 协议或角色路由。每个新会话仍重新 TLS/认证/模式初始化，真实拓扑兼容待验收。
- D09 当前提供不可变身份值 `OceanBaseXid(formatId, globalTransactionId, branchQualifier)`；格式号保留有符号 Int32（-1 为保留的 NULLXID），全局 ID 为1–64字节，分支为0–64字节，两者均不可为 null。构造时复制输入，`GetGlobalTransactionId()` / `GetBranchQualifier()` 返回独立数组，按格式号和两段字节实现值相等及散列，默认对象字符串不输出身份。创建值不会启动或加入事务。五目标已提供 `connection.GetXaResource()` 的 MySQL / Oracle 阶段控制与完整恢复快照：`Start/End/Prepare/Commit/Rollback/Recover` 均有同步和 Task 异步入口，绑定原物理会话；获取资源后连接直到 Close 专用于 XA，业务命令只允许在 Active 分支内执行且 Transaction 为 null，本地事务/切库拒绝，Close 不隐式完成分支。MySQL 公开建连仍受 A05 身份确认门槛限制，此路径仅经显式认证后事实的本地标准/OB20夹具验证；Oracle DBMS_XA 要求 `Use Oracle Prepare Execute=true` 及原有版本门槛（2.2.76以上），首次操作确认关闭自动提交并保持至 Close；结构化绑定身份，Prepare 返回 Prepared 或 ReadOnly，非成功返回码由 `OceanBaseException.XaErrorCode` 保留，与 ERR 包的 ServerErrorCode 区分。恢复上限 MySQL 65536 / Oracle 16383 分支；真实协调器、数据库/OBProxy和异机验收仍开放。恢复格式域、阶段及错误规则见架构 D09，调用方不得用原始事务控制SQL绕过资源API。
- 客户端回退支持基础输入整数、有限浮点、精确 decimal、二进制、NULL 及 Oracle 普通字符串；日期/时区、national、JSON、LOB、复杂值与输出仍要求服务端预处理，NULL 不绕过特殊类型限制。Oracle 字符串保持 VARCHAR2 语义，双写单引号且拒绝 NUL，转义后最多4000 UTF-8字节，RAW最多2000字节；MySQL 非 NULL 字符串因缺可信会话 collation 明确拒绝，不硬编码排序规则。MySQL SQL 仅接受 SQL_MODE/ANSI_QUOTES 各种引号规则下占位符位置一致的语法，执行式注释/不确定语法明确拒绝。最终 SQL 受64MiB报文上限约束；文本结果沿用既有 reader/字段元数据规则。回退值可能出现在服务端 SQL 日志，驱动日志仍不记录 SQL 或参数。真实语法/版本兼容尚待实库验收，公共 MySQL 建连仍受 A05 限制。
- Oracle 类型字典使用 `GetComplexType(schemaName, typeName, transaction)` / `GetComplexTypeAsync`，名称按字典原样精确匹配；返回不可变 `OceanBaseComplexType`，通过 `Members` 查看有序属性或单个集合元素，`CreateValue(...)` 构造 `OceanBaseComplexValue` 快照，`GetValues()` 返回独立数组及 RAW 字节。嵌套值必须由对应成员的 `ComplexType` 构造。仅支持 NUMBER、VARCHAR2（含 VARCHAR）/CHAR、RAW、DATE、TIMESTAMP 及其嵌套，不支持循环定义和 LOB/cursor/national charset 成员。
- 命名 ARRAY/STRUCT 结果列须在同一连接及事务中预载类型，使用服务端预处理（二进制结果；无参数查询可先 `Prepare()`）或 REF CURSOR 读取。`GetValue` / `GetFieldValue<OceanBaseComplexValue>` 返回原字典描述及嵌套值快照，字段元数据的 ProviderType 为 `OceanBaseDbType.Array` / `Struct`，DataTypeName 保留类型名。SequentialAccess 只物化当前复杂字段，后续大字段仍流式读取；分页时核对 schema/type/version。文本复杂结果、未预载的命名类型及未知匿名元素布局均明确拒绝，包括 NULL 复杂结果元数据；线上版本不等于字典版本证明，外部类型 DDL 后仍须主动刷新。
- 匿名标量集合二进制结果按列元素 metadata 读取 NUMBER、VARCHAR2/CHAR、RAW、DATE 和 TIMESTAMP，无须预载字典。值的 `Type.IsAnonymous` 为 true，`TypeName` 为空，reader 的 DataTypeName 为 `ARRAY`；普通/顺序读取复用相同预算及分页元素身份校验。匿名结果描述及值只用于读取，`CreateValue` 和参数/输出绑定仍拒绝；匿名复杂元素缺少完整布局时同样拒绝。
- 匿名集合输入使用 `connection.CreateArrayType("RAW", transaction)` 等工厂；标量名称仅接受不区分大小写的 NUMBER、VARCHAR2、CHAR、RAW、DATE、TIMESTAMP。另一重载接受同会话预载的命名 `OceanBaseComplexType` 作为对象/集合元素，不查询字典或推断别名。返回描述通过 `CreateValue(...)` 构造值，NULL 输入须显式设置 `ComplexType`；普通预处理及 PrepareExecute 均可输入。工厂描述绑定原连接、物理会话和事务代次，清缓存后失效；匿名标量集合支持 PrepareExecute 的 Output/InputOutput/ReturnValue，输出声明和完整终态规则见下一项；匿名复杂元素集合仅接受 Input，读取结果产生的匿名描述仍只读。
- 命名复杂参数将 `OceanBaseComplexValue` 赋给 `Value`，非 NULL 输入/InputOutput 可推断类型；NULL、Output/ReturnValue 必须设置 `OceanBaseParameter.ComplexType`。该属性同时设置对应的 Array/Struct provider type；设置 `DbType`、非复杂 provider type 或 `ResetDbType()` 会清除声明。值须使用相同描述，`Size`/`Precision`/`Scale` 必须为 0；跨连接、关闭重开、事务切换或清缓存后在发包前拒绝。复杂输出需启用 PrepareExecute，metadata 须精确匹配声明，含 NULL/零行；完整响应成功后才发布，尾 ERR、取消及未读尽关闭保留旧值。参数类型头的 version0 不代表自动检测外部 DDL。
- 匿名标量集合输出使用连接工厂描述：Output/ReturnValue 须显式设置 `ComplexType`，InputOutput 可从工厂值推断；支持上述六种标量。响应必须是匿名集合，元素布局须与声明一致（NUMBER 接受 0/3/246 规范化），NULL/零行也核验；线上匿名 schema 不提供命名类型身份。输出值保留执行快照的工厂描述，可按相同会话规则继续构造或绑定，修改原参数声明不改变在途输出类型。匿名复杂元素结果缺少完整身份，输出继续拒绝；无声明输出和只读结果描述仍不能绕过校验。
- Oracle 数组批量执行使用 `((OceanBaseCommand)command).ExecuteArray(rowCount)` / `ExecuteArrayAsync(rowCount, token)`，五资产均可用，要求启用 PrepareExecute 并显式绑定当前事务。行数为 1–65535，每个 Input/InputOutput 是同长度工厂匿名集合；Output/ReturnValue 须声明工厂匿名集合类型。一次请求执行整组参数，不自动提交、回滚或重放；支持 INSERT/UPDATE/DELETE/MERGE、BEGIN/DECLARE/CALL 及 StoredProcedure，拒绝 SELECT/DDL 和 FetchSize。
- 数组结果 `Items` 按零起始输入位置返回 Succeeded/Failed/Unknown、服务端错误码和逐项 DML 更新数；PL/RETURNING 首列是位置而非计数，逐项计数为 null，完整结果的 `RecordsAffected` 是服务端累计数。行内错误通过 Failed 返回，尾 ERR 或协议/网络错误仍抛异常；`command.ArrayBindingResult` 保留已收到的部分结果，未确认后缀为 Unknown，未发 SQL 时为 null。成功仅表示该项执行成功，事务仍由调用者负责。
- 数组 OUT/RETURNING 复用上述六标量及已预载命名复杂元素，每项可返回多行；`Items[i].OutputRows` 按 SQL 输出绑定顺序排列。完整响应及 OB20 边界通过后，输出参数 Value 发布为 `object[][]`（第一维输入位置、第二维该位置的返回行，零行为空数组、NULL 为 DBNull）；失败保持旧 Value。再次执行 InputOutput 前须重新提供工厂集合值。结果快照独立复制 RAW，累计输出最多 65535 个值和 64 MiB 原始字段字节；服务端错误文本不公开。本地协议验证不代表真实 OceanBase/OBProxy 兼容。
- 类型字典查询预约当前物理会话，须显式传入当前事务，活动 reader 期间拒绝查询。关闭重开、事务切换或 `ClearComplexTypeCache(transaction)` 使全部旧描述/值失效；值内容仍可检查。外部类型 DDL 后须显式清缓存并重载，不自动检测跨会话 DDL。类型图最多 32 层；当前会话缓存累计最多 65535 个成员、65535 个命名类型和 4MiB 字典 UTF-8 文本，额度满时可清空重载；每次字典查询沿用连接 CommandTimeout。
- `Use Oracle Prepare Execute=true`（builder 属性 `UseOraclePrepareExecute`，默认 false）启用 Oracle `StoredProcedure`、函数 `ReturnValue` 及 Text 的标量 OUT/InputOutput/RETURNING。首次执行查询 `ob_version()` 并要求至少 `2.2.76`；过程名须为限定标识符，值通过二进制绑定。输出按结果列元数据解码，内部输出行不暴露给 reader；所有结果及协议边界成功后一次发布，失败或未读尽即关闭保留旧值。零行输出为 `DBNull`，多行输出、重复 OUT 占位符、此路径的独立 `Prepare()` 和 `FetchSize` 明确拒绝。真实 OceanBase/OBProxy 往返待验收。
- Oracle REF CURSOR 输出使用 `OceanBaseDbType.RefCursor`（仅 Output/ReturnValue，Size/Precision/Scale 为 0），并启用上述 PrepareExecute。非 NULL 值为 `OceanBaseRefCursor`，调用 `GetDataReader()` / `GetDataReaderAsync()` 读取一次；默认每页 128 行，可传正数 fetchSize 和 SequentialAccess/CloseConnection。对象绑定原连接及事务代次，事务结束、连接关闭重开后不可再打开；父 command 释放不关闭返回的游标。多个游标可依次读取，同一连接只允许一个活动 reader；同一服务端 ID 的输出别名共享对象。读尽自动回收游标，未读取的对象须 Dispose，未读完关闭 reader/游标会销毁原会话。重执行命令不会替调用方释放旧输出对象。REF CURSOR 输入/输入输出及普通结果列读取暂不支持，真实互通待验收。
- `Use Lob Locators=true`（builder 属性 `UseLobLocators`，默认 false）要求同时启用 PrepareExecute，并由服务端协商 locator 能力。Oracle BLOB/CLOB 列及显式 `OceanBaseDbType.Blob` / `Clob` 输出返回 `OceanBaseLob`，不会把 locator 暴露成正文。`ByteLength` 始终是字节数；BLOB 调用 `OpenRead()`，CLOB 调用严格 UTF-8 的 `OpenTextReader()`，每个 handle 只打开一次，且须先消费完原 reader。原会话分块读取，活动流占用连接；释放流或 handle 后可继续使用连接，读取中取消/超时可能销毁会话。现代资产支持 stream/handle 异步释放。
- LOB handle 绑定原连接、物理会话和事务代次；连接关闭重开或事务结束后失效。同上下文 handle 可作为输入/输入输出绑定；Output/ReturnValue 须显式声明 Blob/Clob，输出在完整终态后原子发布。BLOB 每块最多 32767 字节，CLOB 每块最多 8191 个 Unicode 标量；不拼接整 LOB，也不另建连接。
- LOB 修改使用 `WriteBytes(byteOffset, buffer, offset, count)`、`WriteText(characterOffset, text)`、`Truncate(length)` 及对应 Async 方法。偏移从 0 开始，BLOB 按字节、CLOB 按 Unicode 标量计数，Truncate 使用相同单位；须消费完原 reader 并释放活动流。整个多块修改独占原会话，沿用原事务及超时，不自动提交。每块完整成功后更新 locator；多块非原子，失败可能保留成功前缀，不自动回滚或重放，失败后须重新取得 handle。未确认的修改报告结果未知，取消/超时或协议错误可能销毁会话。
- `connection.CreateTemporaryLob(OceanBaseDbType.Blob/Clob, transaction)` / `CreateTemporaryLobAsync` 创建 SESSION 生命周期、cache=FALSE 的临时 LOB，沿用连接 CommandTimeout；活动事务须显式传入。创建者 handle 调用 `FreeTemporary()` / `FreeTemporaryAsync()` 释放服务端对象，成功或在途释放失败后源 handle 均不可复用；普通查询/输出 handle 没有释放权。必须在事务结束和 Dispose 前释放，并先关闭活动流。`Dispose` 仅清理客户端资源和取消在途操作；未显式释放或释放失败的临时对象可能保留至原连接关闭。创建 SQL 开始发送后发生失败（包括尾部 ERR），或成功后取消/会话变化导致不能安全返回 handle 时，销毁原会话回收资源，不影响关闭后重开的新会话；未发送创建 SQL 的版本探测可信 ERR/不支持版本保留可用会话。真实 OceanBase/OBProxy 资源语义仍待联调。
- `Use OB20=true`（builder 属性 `UseOb20`，默认 false）请求 OB20；仅在握手连接属性和服务端 OB20 能力均确认后启用，否则保留标准报文。支持流式分帧、CRC 校验、逐命令边界和游标分页；协议错误销毁连接，不重放已发送 SQL。真实 OceanBase/OBProxy 互通待验收。
- `Enable Full Link Trace=true`（builder 属性 `EnableFullLinkTrace`，默认 false）要求同时启用 OB20，并由服务端确认追踪能力；缺能力时继续普通 OB20。支持 legacy/new-extra 格式，按服务端控制策略采样及记录，关联现有 Activity 的 trace ID；无监听者也可使用环境上下文或内部生成 ID。仅发送固定模块、ID 和时间，不发送 SQL、参数、凭据、端点或任意 Activity 标签/baggage。完成记录随下一请求发送，关闭连接不额外刷新最后一条记录；真实采样及 SHOW TRACE 结果待验收。
- `GetSchema` 提供 `MetaDataCollections`、`DataSourceInformation`、`Tables`、`Columns`、`Procedures`、`ProcedureParameters`，现代三资产另有真实异步入口。两例程集合同时包含过程/函数；限制依序为 catalog、schema、例程名、类型或参数名、包名，非 null 值精确匹配，空串及 `%`/`_` 不作模式。Oracle 包/重载/子程序身份和参数原始类型名保留，未知字段为 `DBNull`；返回行位置为 0、方向为 `RETURN`。这是目录查询，不会自动生成或绑定参数；真实字典字段/权限及版本兼容尚未验证。Oracle 模式不支持 `ChangeDatabase`。
- batch 逐条执行，不隐式创建事务，没有整批原子性保证。需要原子性时由调用方显式绑定本地事务。
- MySQL 四位 YEAR 已实现本地编解码、元数据及参数支持：`ushort`，0 或 1901–2155，显式参数类型 `OceanBaseDbType.Year`；无两位年份推断。真实往返待验收，公共 MySQL 建连仍受 A05 门槛限制。
- MySQL BIT(1–64) 已实现本地编解码、元数据及参数支持：统一使用 `ulong`，BIT(1) 也不转 bool。显式 `OceanBaseDbType.Bit` 参数的 `Size` 为位宽，0 表示 64；超出声明位宽在发包前失败，`Precision`/`Scale` 仅支持 0。真实往返待验收，公共 MySQL 建连仍受 A05 门槛限制。
- MySQL JSON 已实现本地文本/预处理读写与元数据，使用 `string` 和严格 UTF-8，保留 JSON 文本 `null` 与 SQL NULL 的区别；支持顺序 `GetTextReader`。显式 `OceanBaseDbType.Json` 仅接受字符串/NULL，`Precision`/`Scale` 仅支持 0；语法由服务端验证，不隐式序列化对象。真实往返待验收，公共 MySQL 建连仍受 A05 门槛限制。
- Oracle INTERVAL 的本地编解码、元数据和参数支持使用 `OceanBaseIntervalYearToMonth` / `OceanBaseIntervalDayToSecond` 精确值，保留符号、前导精度和纳秒；标准/provider-specific 访问均返回对应结构。显式类型或 `DbType.Object` 下的精确值可绑定，`Precision`/`Scale` 不允许舍入；`ToTimeSpan()` 只在范围与 ticks 精度内成功，年月区间不转固定时长。真实往返待验收。
- Oracle NCHAR/NVARCHAR2 已实现本地文本/预处理读写及顺序字符流：按独立会话 national 编码处理 UTF-8/UTF-16BE，保留尾部空格，未知编码明确失败；真实列往返及版本兼容性仍待验收。

## 使用入口

所有资产均可通过标准 factory 创建对象；下面只创建连接，不访问服务器：

```csharp
using OceanBaseConnector;

using var connection = OceanBaseProviderFactory.Instance.CreateConnection();
connection.ConnectionString = "Server=localhost;User ID=test_user;Password=replace-me;ExpectedMode=Oracle";
using var command = connection.CreateCommand();
command.CommandText = "select 1 from dual";
```

五目标均可通过可选 DI 包注册连接：

```csharp
using Microsoft.Extensions.DependencyInjection;

services.AddOceanBaseConnection(connectionString);
```

每次解析 `OceanBaseConnection` 或 `DbConnection` 得到新的关闭连接，注入可选宿主日志，由解析它的容器作用域释放；注册/解析均不连接数据库。已有其他 provider 的 `DbConnection` 注册会保留，优先注入具体类型。连接之间不共享可变状态、不自动池化；直接连接的打开前连接串仍按原契约含凭据。数据源注册不会隐式注册连接。

在 .NET 8/9/10 应用中可共享数据源：

```csharp
using OceanBaseConnector;

using var source = new OceanBaseDataSourceBuilder(connectionString).Build();
using var connection = source.CreateConnection();
// await connection.OpenAsync(cancellationToken);
```

引用可选 DI 包后，在已有宿主的服务集合注册 `services.AddOceanBaseDataSource(connectionString)`，普通的 `OceanBaseDataSource` 与 `DbDataSource` 解析为同一单例。`AddKeyedOceanBaseDataSource(key, connectionString)` 提供独立的 keyed 单例。数据源由容器释放；调用方释放自己借出的连接。可通过 builder 的 `UseLoggerFactory` 接入宿主日志。

默认 `TLS Mode=VerifyFull` 验证系统信任链及主机名，TLS 失败不降级。连接串 builder 输出和打开前的连接串包含调用方提供的凭据，应由应用保管；数据源及打开后的连接默认隐藏密码，显式 `Persist Security Info=true` 可保留。驱动日志/诊断不记录 SQL、参数值或凭据。

当 `OceanBaseException.IsExecutionOutcomeUnknown` 为 true 时，已发送的 SQL 可能已执行。该物理会话被销毁，驱动不自动重放 SQL；应用应根据业务幂等性和事务结果核对状态，不应盲目重试。

## 故障与结果未知处理

先判断失败发生在哪一步，再结合异常和业务操作记录处理。`IsExecutionOutcomeUnknown=false` 仅表示未被驱动标记为结果未知，不保证语句没有副作用，也不构成重试许可。连接的 `State=Open` 不能单独证明 reader 已读完、事务已结束或上一条业务操作成功。

| 场景 | 当前驱动行为 | 应用处理 |
| --- | --- | --- |
| 参数/选项验证失败，或执行入口收到预取消 token | 入口拒绝；预取消不发送业务 SQL | 修正输入或取消来源；不要把验证错误列为瞬时故障反复重试 |
| SQL 可能已发送，连接断开、取消或超时且没有可信终态 | 抛出 `OceanBaseException`，`IsExecutionOutcomeUnknown=true`，销毁原物理会话；取消/超时原因在 `InnerException` | 保存业务操作标识及状态，释放旧连接；根据业务记录从新连接核对结果，禁止直接重放写操作、COMMIT 或整个 batch |
| 收到可信服务端 ERR | 保留 `ServerErrorCode`/`SqlState`，未知标记为 false；普通非游标命令可结束交换并释放租约，游标 ERR 会销毁会话 | 按错误和事务状态处理；错误码不能证明之前已完成的批次前缀没有生效 |
| 关闭尚未消费完响应的 reader | 关闭 reader 会销毁未完成交换的物理会话；`Close`/`Dispose` 可能正常返回，未知状态通过日志/诊断报告 | 若需要证明结果完成，先读尽当前结果和后续所有结果；不能把释放成功当作 SQL 成功或已回滚的证明 |
| COMMIT/ROLLBACK 的终态已确认，随后恢复自动提交失败 | 返回已确认的提交/回滚结果，结束该事务并销毁会话；另记录 `TransactionRestoreAutocommitFailed` | 保留已确认结果，释放连接后重新建连；不要因会话清理失败重跑已提交业务 |
| COMMIT 收到可信 ERR | 会话仍为 Open 且事务仍活动时，保留事务供显式回滚；会话已被销毁时解除事务关联；若响应不可信则按结果未知处理 | 会话仍可用时按事务状态显式回滚；否则重新连接核对业务状态，不在同一失败事务中盲目重试提交 |

单独释放字段流或字段 TextReader 不代表命令完成；所属 reader 仍须读尽所有结果或关闭。上表的取消/超时不是服务端 `KILL QUERY` 完成证明；本地中断传输不能证明服务器停止执行。显式 `Prepare` 阶段失败也可能销毁会话，但不能据此认定业务 Execute 已发生。`Cancel()`、关闭连接和超时都不会触发驱动自动重放。

批次逐条执行；后续条目停止不撤销已经完成的前缀。现代目标的 `DbException.BatchCommand` 可定位关联原始条目，条目更新计数和 `RecordsAffected` 只描述已知计数；查询/未执行条目可能同为 `-1`，不能用它构造自动断点续跑。需要整批原子性时，执行前显式绑定同一连接的本地事务，并处理提交本身的未知结果。

多地址只在 TCP 建连故障时按配置顺序尝试，共享总连接预算。选中端点后的 TLS/认证失败，以及已发送 SQL 的失败，都不会透明切换到另一个端点继续执行。`ExpectedMode` 是认证结果的交叉检查，不能用于强制或猜测模式。

排障可记录驱动版本、应用目标/实际运行时、脱敏端点和模式、异常类型、错误码/SQLSTATE、未知标记、取消/超时原因类别及业务操作标识。不要把密码、原始连接串、SQL/参数值或未经脱敏的抓包写进报告。连接、命令、事务日志类别分别为 `OceanBaseConnector.Connection`、`.Command`、`.Transaction`；日志可包含端点和耗时，不携带原始异常文本。`ActivitySource`/`Meter` 名称均为 `OceanBaseConnector`，指标为 `oceanbase.client.operations` 和秒单位 `oceanbase.client.operation.duration`，固定标签仅包含操作、模式、结果和未知标记。监听器回调失败会被隔离；诊断通道首次初始化失败时，该通道可能在当前程序集生命周期内不可用，不能根据缺少 span 推断 SQL 没有执行。

在本次验证的 .NET 8.0.31 中，应用自行创建的 `MeterListener` 若未设置 `MeasurementsCompleted`，释放时可能仍保留已启用的指标订阅，残留回调会影响后续采集和无监听开销。宿主应设置该完成回调（无清理状态时可为空操作），或在释放前显式调用 `DisableMeasurementEvents` 解除订阅；驱动不拥有或释放应用的监听器。

## 兼容性证据

| 部署组合 | 真实握手/TLS/SQL/事务验收 |
| --- | --- |
| MySQL 模式，直连 | 未验证，公共握手另有上述判定缺口 |
| Oracle 模式，直连 | 未验证 |
| MySQL 模式，经 OBProxy | 未验证，公共握手另有上述判定缺口 |
| Oracle 模式，经 OBProxy | 未验证 |

OceanBase 和 OBProxy 最低版本均未确定，没有已验证的版本白名单。上述每个部署组合的 TLS/认证互通仍未验证；当前本地实现的限制如下，不代表该组合已通过实库验收：

| TLS 配置 | `mysql_native_password` | `mysql_clear_password` | `sha256_password` | `caching_sha2_password` |
| --- | --- | --- | --- | --- |
| `VerifyFull`（默认） | 本地实现 | 通过身份验证后发送密码 | 通过身份验证后发送密码 | fast auth 或 TLS 完整密码 |
| `Required` | 本地实现，不验证身份 | 拒绝发送密码 | 显式配置 RSA 后发送密文 | fast auth，或显式配置 RSA 后完整认证 |
| `Disabled` | 本地实现，不建立 TLS | 拒绝发送密码 | 显式配置 RSA 后发送密文 | fast auth，或显式配置 RSA 后完整认证 |

以上均未经实库验证。SHA-2 使用严格 UTF8，拒绝无效 Unicode；连接串拒绝内嵌 NUL。空密码使用协议“无密码”标记，不请求公钥或发送 RSA 密文。fast auth 成功消息后仍须等待最终 OK，初始响应后允许一次账户插件切换，完整认证阶段拒绝重复挑战/切换。

RSA 配置优先使用 `Server RSA Public Key`（builder属性 `ServerRsaPublicKey`）提供可信的 SubjectPublicKeyInfo `PUBLIC KEY` PEM 文本；不读取文件，应用可自行加载后赋值。PEM 最长16KiB，支持2048–8192位RSA和不超过64位的正奇数指数。使用协议要求的 OAEP-SHA1，密码UTF8字节加NUL不得超过密钥字节数减42；固定key无效时直接失败，不回退检索。初始SHA256密文超过255字节时需要服务端协商 `CLIENT_PLUGIN_AUTH_LENENC_CLIENT_DATA`，不满足时明确失败。默认脱敏连接串不含PEM；完整builder序列化保留它。

无固定key时，只有显式设置 `Allow Public Key Retrieval=true`（默认false）才向服务端获取公钥。**获取的公钥没有身份验证，可被中间人替换；优先使用 VerifyFull 或可信固定公钥。** RSA只保护密码，`TLS Mode=Disabled`下的业务SQL与结果仍为明文。SHA-2 的 VerifyFull 完整密码分支目前只有状态/编码测试，没有私有 CA 成功链证据。证书故障应核对信任链、端点主机名及 `TLS Server Name`，驱动不会自动降低 TLS 要求。即使插件/TLS 条件满足，公共 MySQL 模式判定缺口仍会阻止该模式连接。

`mysql_old_password` 已接入本地认证状态机：支持显式插件及命名/单字节旧切换，散列前8字节ASCII挑战，密码按Java基线的UTF16字符规则（忽略空格/制表符）。空密码为空响应，非空响应8字节加NUL；仍要求protocol41握手。旧散列较弱，仅允许 `VerifyFull`，`Required`/`Disabled`均在发送旧响应前拒绝，不受RSA选项放行。本地成功证据仅为固定向量和状态/编码，TLS环回验证较弱通道拒绝；私有CA成功链及实库互通仍未验收。

`client_ed25519` 已接入初始及命名插件切换：严格保留32字节二进制挑战（包括尾零），UTF8密码经SHA512展开后签名，非空响应64字节，空密码为空响应。使用 BouncyCastle.Cryptography 2.7.0 的 ExpandedKey API，五资产均声明此运行时依赖。签名认证本身不加密业务内容；仍按所选TLS策略建连，默认 `VerifyFull`。本地Java固定向量、状态/环回及包消费证据不替代实库验收。

`dialog`/PAM 已接入本地状态机：首轮主 `Password`，后续依次使用 `Password2`、`Password3` 等连接字符串键（整数2到Int32.MaxValue，拒绝前导零），缺下一项时失败。每轮按严格UTF8发送密码加NUL，空密码也带NUL，要求 `TLS Mode=VerifyFull`；提示仅触发下一凭据，不做交互回调。`ToSanitizedConnectionString(false)`、选项诊断及默认DataSource输出去除全部密码；builder的 `ConnectionString` 和显式 `ToSanitizedConnectionString(true)` 保持完整往返，应当保护。附加凭据随选项冻结和地址切换保留。本地多轮成功仅为状态/编码证据，Required/Disabled环回证明拒绝；可信TLS多轮及实库仍待验收。

`auth_gssapi_client` 已接入本地状态机，仅 `net8.0`/`net9.0`/`net10.0` 资产使用系统 `NegotiateAuthentication` 和当前系统票据，且要求 `TLS Mode=VerifyFull`。两个 .NET Standard 资产明确拒绝该插件，即使运行于现代宿主。仅允许 Kerberos，拒绝 NTLM/Negotiate 及未知机制；系统协商须完成双向认证、实际机制仍为 Kerberos，再等待数据库最终 OK。可用 `Service Principal Name`（builder属性 `ServicePrincipalName`，默认空）覆盖服务端 SPN；最长1024字符，不含控制符或无效Unicode，不改写realm、不做反向DNS。原始token直接传输，不加NegotiateStream帧。系统同步协商可能等待KDC，取消/超时不能强制中断原生调用，驱动在调用返回及网络阶段检查预算。本地测试仅覆盖确定性平台替身、协议状态及弱TLS拒绝，尚无真实凭据/KDC、可信TLS成功链或实库验收；Java Windows native的其他机制及Standard支持仍开放。

Windows 上已在实际 .NET 8.0.31、9.0.20 各执行完整本地 Unit 1206 项和 DI 8 项，全部通过且无跳过，另有实际 .NET 8/9/10 包消费样例。日常测试项目仍统一声明 net10.0；8/9 使用临时构建属性验证，Microsoft 通用库依赖分别保持 8/9 主版本。非 Windows、真实数据库和完整旧运行时兼容矩阵尚未验收。.NET Standard 资产的存在不代表所有实现该标准的运行时都已验证。详见仓库 `docs/obconnector-dotnet-progress.md`，设计与实施范围见同目录的架构设计和开发方案。

## 0.1.0-alpha 交付与回退说明（未发布）

当前交付是源码和本地生成的两个同版本包，功能及未支持项见上文。没有已验证的正式版或候选版升级来源，不承诺与 Java JDBC API、官方两个独立 .NET 驱动的连接串或程序集二进制兼容；不能仅替换 DLL 就假定应用无需修改。发布前仍需完成 R01 许可/来源/包名审查和 G1–G3 的真实兼容验收，本地包验证通过不授权发布。

升级前保留当前应用完整发布目录、包版本/还原锁定信息、CPM 配置和脱敏运行配置。核心和 DI 包一起升级至同一版本，按应用目标保持 Microsoft 通用库主版本对应；不要只替换其中一个包，也不要因使用 .NET 10 SDK 而把旧目标依赖统一升级到 10.x。先在目标环境验证连接、参数化 SQL、类型精度、reader 清理和显式事务，再决定是否放量；当前缺少这些实库证据，不能将开发验证称为放量批准。

需要回退时，先停止产生新的业务操作并处理在途 reader/事务，保留未知结果的业务核对记录，再整体恢复此前**已验证**的应用发布包及其依赖和配置。在没有已验证上版时，应停止放量并排查，不能把任意历史 Git 提交当作安全回退版本。回退驱动不会撤销已提交的数据、自动完成未知事务或回滚应用执行过的 DDL；这些状态必须单独核对。数据源由 DI 容器持有时，协调宿主生命周期重建容器/数据源；释放数据源不会强制关闭已经打开的连接，不应在仍有在途操作时仅替换 DLL。

## 本地构建和包验证

需要 .NET 10 SDK；完整包消费验证还需要 PowerShell 7.1+ 和实际 .NET / ASP.NET Core 8、9、10 运行时。日常测试宿主均为 .NET 10。

```powershell
dotnet restore OceanBaseConnector.slnx
dotnet build OceanBaseConnector.slnx -c Release --no-restore
dotnet test tests/OceanBaseConnector.UnitTests/OceanBaseConnector.UnitTests.csproj -c Release --no-build --no-restore
dotnet test tests/OceanBaseConnector.ConsumerTests/OceanBaseConnector.ConsumerTests.csproj -c Release --no-build --no-restore
dotnet test tests/OceanBaseConnector.DependencyInjection.Tests/OceanBaseConnector.DependencyInjection.Tests.csproj -c Release --no-build --no-restore
dotnet pack OceanBaseConnector.slnx -c Release --no-build --no-restore -o TestResults/packages
pwsh -NoProfile -File eng/Verify-Packages.ps1 -PackageDirectory TestResults/packages
```

Windows 上安装 .NET Framework 4.8 targeting pack 和兼容桌面运行时后，可给最后一条命令追加 `-VerifyNetFramework`。验证器临时生成 net48 宿主，加载本次核心包的 netstandard2.0 资产并检查基本公开契约；宿主自动生成的依赖绑定重定向需随应用配置一起交付。本机实际为 Framework 4.8.1（Release 533509），本项仅有基础包消费证据，未覆盖原版4.8、完整SQL/TLS或实库；正式Framework支持仍未确定。仓库日常测试工程继续声明net10.0。

包验证器核对包内容、依赖和实际还原的 SHA512，在独立缓存中只通过本地包消费五个资产，运行时不连接数据库。Standard 样例在隔离的 .NET 10 宿主运行；现代样例分别使用实际 .NET 8/9/10。生成目录和还原证据保留在 `TestResults/package-validation/`。这项检查不会发布任何包。

项目开源许可证尚待维护者确定，R01 许可和包名/商标审查未关闭。第三方依赖和参考来源见包内 `THIRD-PARTY-NOTICES.md`；参考实现的许可证不自动成为本项目许可证。
