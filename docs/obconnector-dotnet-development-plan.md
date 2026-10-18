# OceanBase Connector for .NET Implementation Plan

> **For agentic workers:** 执行时逐项读取[架构设计](obconnector-dotnet-architecture-design.md)和本计划；每项完成后更新[进度台账](obconnector-dotnet-progress.md)，提交实现前运行该项的验证命令。

**Goal:** 交付一个直接连接 OceanBase/OBProxy、同时支持 MySQL 和 Oracle 兼容模式的托管 ADO.NET provider，并在明确的服务端版本矩阵上证明其行为。

**Architecture:** 一个公开 provider、一个共享协议主链，每个物理会话保存模式、版本、能力和事务状态。同步与异步入口共享编码和状态机，分别使用同步与异步网络 I/O；C 阶段才开放经完整重置验证的池化。

**Tech Stack:** C#、.NET 10 SDK；单个 `netstandard2.0;net8.0;net9.0;net10.0` NuGet 包；ADO.NET、`TcpClient`/`SslStream`、`System.Numerics.BigInteger`、xUnit。协议核心不引入 Oracle/MySQL 原生客户端。

**Spec:** [架构设计](obconnector-dotnet-architecture-design.md)，Java 行为基线为 [Connector/J 架构提纲](obconnector-j-architecture-outline.md)固定的提交 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`。这两个文档是设计和对照依据，不是 .NET 已实现能力的证明。

## 全局约束与准入

- 当前排期以 A/B/C 均进入首个正式版本为基线，G0-5 决定是否调整；D 按独立能力交付。没有真实双模式测试和明确版本矩阵，不能把 A/B/C 标记为发布完成。
- 首版默认 `TLS Mode=VerifyFull`、`Pooling=false`、`CLIENT_FOUND_ROWS`；不自动重试已发送 SQL，不隐式客户端参数插值，不加入 `TransactionScope`。变更这些公开语义须先修订架构设计。
- `netstandard2.0` 定义公共类型身份；现代 ADO.NET API 用条件编译，仅在 `net8.0+` 暴露。每个目标单独编译，.NET 8/9/10 运行测试，另用 `netstandard2.0` 资产做消费测试。
- 仓库统一使用中央包管理(CPM)：根目录 `Directory.Packages.props` 固定全部 NuGet 包版本并开启传递依赖钉扎；工程的 `PackageReference` 一律不带 `Version`。新增依赖只改 `Directory.Packages.props`。
- 许可和包名在发布前审定；只参考 Java 行为和协议，不直接移植其 LGPL-2.1 实现。
- 进入 A 的真实互通验收前，应取得两个兼容模式的独立租户、服务端和 OBProxy 版本、认证插件、可验证的 TLS 证书及可控故障网络。缺少环境时只报告夹具验证。
- 所有任务先写会失败的边界用例，再实现，再运行该用例和所属阶段回归；原始协议夹具保留来源、版本、模式和预期字段，不存真实凭据。

## 交付目录与接口边界

| 位置 | 职责 |
| --- | --- |
| `Directory.Packages.props` | 中央包管理：全部 NuGet 包版本的唯一来源，传递依赖钉扎 |
| `src/OceanBaseConnector/OceanBaseConnector.csproj` | 唯一产品程序集和多目标条件编译；包元数据在发布门槛确定 |
| `src/OceanBaseConnector/OceanBaseConnection*.cs`、`OceanBaseCommand*.cs`、`OceanBaseDataReader*.cs`、`OceanBaseTransaction*.cs` | ADO.NET 公共对象；拆分文件按对象职责，不复制四套协议 |
| `src/OceanBaseConnector/OceanBaseParameter*.cs`、`OceanBaseProviderFactory.cs`、`OceanBaseConnectionStringBuilder.cs` | 参数、工厂和配置的公开契约 |
| `src/OceanBaseConnector/Internal/Transport/` | TCP、SSLRequest、TLS、同步/异步字节流与关闭取消 |
| `src/OceanBaseConnector/Internal/Protocol/` | 标准帧、握手、认证、命令、OK/ERR、文本/二进制行与多结果；D 阶段才加 OB20 |
| `src/OceanBaseConnector/Internal/Session/` | `PhysicalSession`、模式/能力快照、独占执行、statement ID、事务与失效状态 |
| `src/OceanBaseConnector/Internal/Sql/`、`Internal/Values/` | 占位符词法器、参数编码、模式感知列解码和 provider-specific 值 |
| `src/OceanBaseConnector/Internal/Pooling/` | C 阶段的池、会话完整重置与池键 |
| `tests/OceanBaseConnector.UnitTests/` | 不依赖服务端的字节夹具、API/状态机和错误路径测试 |
| `tests/OceanBaseConnector.IntegrationTests/` | 独立 MySQL/Oracle 租户、OBProxy、TLS、故障与重置测试；没有环境时跳过并明确报告 |
| `tests/OceanBaseConnector.ConsumerTests/` | `netstandard2.0` 编译消费工程和 .NET 8/9/10 运行消费工程；打包后用独立宿主显式加载 `netstandard2.0` 资产，验证工厂、条件 API 和公开值类型身份 |

`PhysicalSession` 独占 socket、包序号、模式、版本、协商能力及服务端 statement ID；一次只有一个命令或 reader 持有它。公开 `OceanBaseConnection` 是逻辑借用者。`OceanBaseCommand` 在发送前验证参数、事务和环境事务；`OceanBaseDataReader` 在关闭/读尽前持有响应；任何未读包、结果未知、重置失败都使物理会话失效。`SessionMode` 只能由认证 OK 的服务端状态确定，`ExpectedMode` 只做事后校验。

对外扩展类型先固定 `OceanBaseDbType`、`OceanBaseDecimal`、`OceanBaseTimestamp`、`OceanBaseTimestampWithTimeZone`、`OceanBaseTimestampLocalTimeZone`、`OceanBaseDate`、`OceanBaseDateTime` 的 `netstandard2.0` 类型身份；`OceanBaseDecimal` 的低精度列访问及范围内写入在 B05/B06 落地，宽精度编解码及其余精确时间类型在 C03 落地。不得让 `GetFieldType` 随行值变化。`OceanBaseException` 含服务端错误码、SQLSTATE、模式和 `IsExecutionOutcomeUnknown`，不含默认 SQL/参数/凭据。

## 阶段与日程基线

相对周数从双模式环境与产品决定可用的第 1 周算起，按 1 名全职开发者估算；代码评审、环境故障和服务端差异可能改变范围。周数是排期基线，不是已承诺发布日期。G0 未关闭时可先做 A01 工程骨架和 A03 不依赖实库的协议夹具，实际 W1 日期及 G1 关闭仍以 G0 完成为前提。任务可在依赖满足时并行评审，阶段门槛仍按顺序关闭。

| 门槛 | 相对窗口 | 完成定义 |
| --- | --- | --- |
| G0 准入 | W0 | 记录目标 OceanBase/OBProxy 版本、租户、TLS/认证、测试运行方式和 §9 产品决定；环境缺失列为阻塞项 |
| G1 协议基础 A | W1-W5 | A01-A07 完成；双模式真实连接/关闭与 TLS 拒绝场景有证据 |
| G2 ADO.NET 核心 B | W6-W17 | B01-B09 完成；双模式 CRUD、参数、低精度 `OceanBaseDecimal` provider-specific 访问、事务、取消、元数据和四目标消费证据齐全 |
| G3 生产连接 C | W18-W34 | C01-C08 完成；池化仅对通过完整重置的组合开放，精确类型和负载故障场景通过 |
| G4 发布候选 | W35-W37 | R01-R02 完成；兼容矩阵、许可证/包名和 NuGet 包验证齐全 |
| D 扩展 | G4 后逐项排期 | D01-D15 各自有独立版本/模式矩阵，不阻塞 G4，除非产品决定更改 |

## Review Focus

以下五类输入必须在所属任务的测试中落地，不允许只靠正常路径测试推断：

1. TLS 被拒绝或证书名称不符：认证数据未发出且不降级；A04/A05。
2. Oracle 模式位或能力反馈缺失：在任何模式相关值解码前失败；A05/A07。
3. 取消恰好发生在部分发包后：报结果未知、销毁会话且不重放；B08。
4. 低精度 NUMBER/DECIMAL 在 B 阶段已有 provider-specific 精确访问；MySQL `DECIMAL(65,20)`、Oracle 高精度/纳秒值在 C 阶段精确往返，B 阶段显式失败；B05/B06/C03。
5. 应用执行 `SET`/`ALTER SESSION` 后归池：下一借用者看不到前一会话状态；C01/C08。

## A. 协议基础

### A01 多目标工程与测试入口

**产物：**解决方案、唯一产品工程、三个测试工程、CI 构建入口。以 .NET 10 SDK 构建四目标；测试工程分别运行于 .NET 8/9/10，消费测试显式引用 `netstandard2.0` 资产。

- [ ] 建立多目标工程和目标条件编译样例；验证不存在现代 API 泄漏到 `netstandard2.0`。
- [ ] 建立协议夹具格式：二进制数据、来源、服务端版本、模式、脱敏说明和期望解析结果。
- [ ] 运行 `dotnet restore`、`dotnet build -c Release`、各测试目标的 `dotnet test -c Release`；将命令及日志位置登记到进度台账。

### A02 连接串与公开配置

**产物：**`OceanBaseConnectionStringBuilder`、不可变内部连接选项；解析 `Server`、`Port`、`User ID`、`Password`、`Database`、超时、`TLS Mode`、`TLS Server Name`、`Persist Security Info`、`ExpectedMode`、`Pooling`。未实现的选项不得静默接受。

- [ ] 用测试固定空值、非法端口/超时、同义词冲突、未知键、密码序列化与公开连接串脱敏。
- [ ] 解析一次并冻结内部配置；密码不进入异常/默认日志，池键规则留给 C01。
- [ ] 运行连接串单测及四目标编译；在进度台账记录公开键清单。

### A03 标准包帧与字节游标

**产物：**协议帧读写器，处理 3 字节长度、1 字节序号、最大长度分片、命令边界序号重置、截断/越界/恶意长度。解析前检查剩余字节和整数溢出，不按不可信长度直接分配。

- [ ] 用固定字节夹具先覆盖单包、多分片、零长度尾包、错序、截断、过长长度编码和服务端 ERR。
- [ ] 分离包流与命令解析；同步/异步网络入口使用同一帧规则，异步不调用同步读写。
- [ ] 运行协议单测，包含随机截断输入与资源上限断言。

### A04 TCP、SSLRequest 与 TLS

**产物：**`TcpClient` 建连、超时、`SslStream` 升级与端点/`TLS Server Name` 验证；`VerifyFull` 默认使用 OS 信任根，`Required` 和 `Disabled` 仅显式配置。

- [ ] 夹具测试严格顺序：初始握手、检查 `CLIENT_SSL`、SSLRequest、TLS 握手、认证响应；无 TLS 能力、证书错误及升级失败时认证字节数为 0。
- [ ] 以可验证私有 CA、错误主机名、IP 连接加显式 DNS 名称分别运行跨平台 TLS 测试；取消时关闭传输。
- [ ] 记录每种失败的异常类别与无明文降级证据。

### A05 认证、模式与能力

**产物：**初始握手/认证切换/OK 解析，按版本矩阵选择已验证插件；发送 `CLIENT_SUPPORT_ORACLE`、`CLIENT_SESSION_TRACK`、`CLIENT_FOUND_ROWS`、多结果等必要位。保存请求、服务器能力与实际生效能力；解析 `OB_SERVER_USE_ORACLE_MODE` 和 `ob_capability_flag`。

- [ ] 夹具先覆盖 Oracle 位设置/未设置、无法可信识别模式、未知插件、能力缺失和认证错误；模式未知时不交付可执行连接。
- [ ] 只在已验证 TLS 上允许 cleartext 类插件；认证切换同样检查这个条件。
- [ ] 运行夹具单测与两模式真实握手测试，记录服务端和 OBProxy 版本、插件与能力位。

### A06 物理会话、错误与生命周期

**产物：**`PhysicalSession` 的 `Connecting/Open/Busy/Broken/Closed` 状态与独占租约；关闭/错误释放 socket、statement ID、结果状态。`OceanBaseException` 保留 SQLSTATE、服务端码及安全端点信息。

- [ ] 测试同一物理会话并发执行拒绝、未读响应不得复用、错误后失效、重复释放幂等和日志脱敏。
- [ ] 实现同步/异步关闭的资源所有权；不让后台清理线程替代显式状态转换。
- [ ] 运行状态机单测及网络断开夹具。

### A07 G1 真实连接门槛

**产物：**在明确版本的 MySQL/Oracle 租户和 OBProxy 上打开、识别模式、关闭连接；记录 TLS 和认证组合。G1 是互通证据，不是仅有夹具的绿色构建。

- [ ] 两模式直连及 OBProxy 组合各跑连接/关闭、模式不符拒绝、错误证书和不支持认证。
- [ ] 将实际版本/端点类别、执行命令、脱敏日志及失败项填入兼容矩阵。
- [ ] 仅在全部必需组合通过时关闭 G1；其余记录为阻塞，不把夹具通过写成兼容通过。

## B. ADO.NET 核心

### B01 标准 provider 对象

**产物：**`OceanBaseConnection`、`OceanBaseCommand`、`OceanBaseParameter`、`OceanBaseDataReader`、`OceanBaseTransaction`、`OceanBaseProviderFactory`；工厂创建正确派生类型，未实现的方法明确失败。

- [ ] 测试创建/关闭/重复使用、`DbProviderFactories.RegisterFactory`、`ConnectionString` 脱敏、`CommandBehavior.CloseConnection`，以及 B 阶段 `ChangeDatabase`/`ChangeDatabaseAsync` 在发包前报不支持。
- [ ] 将对象只连接到 A 阶段会话，不暴露内部 socket 或双模式两套公开类型。
- [ ] 在四目标编译并运行工厂/消费测试。

### B02 文本命令、OK/ERR 与多结果

**产物：**`COM_QUERY`、文本行、OK/ERR、多结果状态；`ExecuteNonQuery`、`ExecuteScalar`、`ExecuteReader` 同步与异步路径共享状态机。

- [ ] 夹具覆盖 `SELECT`、DML、空结果、多个结果集、结果集后更新计数、服务器 ERR 和有未消费响应的关闭。
- [ ] 验证默认 `CLIENT_FOUND_ROWS` 下无实际变化的 `UPDATE` 返回匹配行数；`RecordsAffected` 仅按服务端 OK 计数累计。
- [ ] 两模式分别运行文本 CRUD 与异步网络阻塞测试。

### B03 Oracle 占位符词法器与参数验证

**产物：**Oracle 命名 `:name` 到位置参数的映射；跳过单/双引号、注释、成对及非成对分隔符的 `q'...'` 字面量和 `:=` 赋值；重复名称展开到每个位置，无法可靠识别的字面量或绑定语法及缺失/多余绑定在发包前失败。`ParameterName` 接受有/无 `:` 前缀，去前缀后不区分大小写，参数集合中规范化后重名的多个项一律拒绝。MySQL SQL 不经客户端占位符改写，参数数量以 B04 的服务端 prepare 返回值为准。

- [ ] 用普通字符串、`q'[...]'`/`q'{...}'`/`q'(...)'`/`q'<...>'`、非成对分隔符 `q'!:name!'`、未闭合字面量、双引号、注释、`:=`、重复名称/大小写和无参数 SQL 写用例；确认仅实际绑定被改写，无法可靠识别时发包前失败。
- [ ] 实现 Oracle 单次词法扫描，保留原始参数顺序，不做客户端值插入；未知 Oracle 绑定语法明确失败。
- [ ] 运行词法器单测及 Oracle/MySQL 参数化集成测试。

### B04 服务端预处理与语句所有权

**产物：**`COM_STMT_PREPARE/EXECUTE/CLOSE`、二进制参数/结果、同一物理会话上的 statement ID；MySQL 参数数量以服务端 prepare 响应为准，Oracle 改写后数量须与响应一致。不支持 prepare 的 SQL 明确失败。`PrepareAsync` 在现代目标真实异步。

- [ ] 夹具覆盖参数类型、NULL、重复执行、多结果、错误关闭、断线后旧 statement ID 不复用。
- [ ] 实现同步/异步发包与响应；保持 `Prepare()` 强制准备，失败不转成客户端插值。
- [ ] 两模式运行预处理 CRUD 和 `PrepareAsync` 非同步退化测试。

### B05 基础值、字符集与不支持类型

**产物：**模式感知编解码支持基础整数、浮点、可表示的 `decimal`、低精度列的 `OceanBaseDecimal` provider-specific 读取和范围内参数写入、文本/二进制、NULL、常见日期时间及含时间的 Oracle `DATE`；宽精度和延期类型显式失败。

- [ ] 夹具覆盖有符号/无符号边界、乱码、NULL/空串、Oracle `DATE` 时分秒、低精度 NUMBER/DECIMAL 经 `OceanBaseDecimal` 精确读取和写入、MySQL `DECIMAL(65,20)` 在 C 前失败、NCHAR/INTERVAL/JSON/BIT/YEAR 未支持时失败。
- [ ] 编码依据 `DbType`/显式 `OceanBaseDbType`，解码依据模式、列定义、字符集、精度；普通 `string` 参数不声称支持 NCHAR/JSON。
- [ ] 两模式运行基本类型往返和字段元数据一致性测试。

### B06 逐行 reader 与列元数据

**产物：**逐行 `Read`/`ReadAsync`、`NextResult`/`NextResultAsync`、`GetSchemaTable`、`IDbColumnSchemaGenerator.GetColumnSchema`、固定的 `GetFieldType`/provider-specific 字段类型；低精度 NUMBER/DECIMAL 列的 `GetProviderSpecificFieldType` 与 `GetProviderSpecificValue` 使用 `OceanBaseDecimal`。B 阶段允许有上限的当前行缓冲，不缓存整个结果集。

- [ ] 测试跨多结果读取、早关、`CommandBehavior.CloseConnection`、零/空值、低精度列的标准/provider-specific 类型随行稳定、未消费响应的会话处理。
- [ ] 实现 reader 独占物理会话；`ExecuteScalar`/`ExecuteNonQuery` 安全消费或使会话失效。
- [ ] 两模式对照服务端列定义验证名称、精度、长度、可空性和异步读取。

### B07 本地事务与环境事务拒绝

**产物：**`BeginTransaction`、隔离级别逐模式映射、显式 `DbCommand.Transaction` 绑定、提交/回滚/释放；现代事务异步入口真实异步。`TransactionScope`/`EnlistTransaction` 在发包前拒绝。

- [ ] 测试遗漏、错误连接、已结束事务、重复开启、隔离级别错误、关闭时回滚及回滚失败销毁。
- [ ] 测试连接打开前/后进入 `TransactionScope`，所有调用方协议入口拒绝但内部清理可继续。
- [ ] 两模式运行提交、回滚、断线后结果未知测试。

### B08 取消、超时与结果未知

**产物：**发送前取消返回 `OperationCanceledException`；发包开始且无可信终态响应时返回 `OceanBaseException(IsExecutionOutcomeUnknown=true)` 并销毁物理会话。`DbCommand.Cancel()` 使用相同状态机。

- [ ] 故障注入覆盖发送前、部分发送、服务端执行后未返回、完整 ERR/OK 已收到、`Commit`/`Rollback` 中断。
- [ ] 实现调用方 token、命令超时与连接关闭的协调；仅在取消已确认且响应读尽后保留会话。
- [ ] 运行同步/异步故障测试并确认任何已发送 SQL 都未自动重放。

### B09 G2 公共 API 与双模式验收

**产物：**B01-B08 的四目标编译/消费、双模式 CRUD、参数、低精度 `OceanBaseDecimal` provider-specific 访问、事务、reader、取消证据；公开 API 审查记录，列明与 Java 的有意功能差距。

- [ ] 运行 `dotnet build -c Release`、`dotnet test -c Release`，并按 .NET 8/9/10 分别执行测试及 `netstandard2.0` 包消费。
- [ ] 在双模式直连和 OBProxy 上运行 B 阶段集成套件，登记版本、失败用例及已知不支持项。
- [ ] 仅当必需组合全部通过、无 P0/P1 缺陷时关闭 G2。

## C. 生产连接

### C01 池、完整重置与会话隔离

**产物：**按规范化连接选项、凭据身份/材料和 TLS 目标分组的可选池；默认不池化。仅在版本/模式组合的完整重置已验证后接受 `Pooling=true`。

- [ ] 测试 `SET`/`ALTER SESSION`、schema、用户变量、临时对象、未完成事务、准备语句、reader 和重置失败不会跨借用者泄漏。
- [ ] 归还时读尽/销毁未消费响应、回滚、服务端重置、重做初始化；任何无法证明已清理的状态销毁物理会话。
- [ ] 测试池上限、等待超时、凭据轮换和故障清池；记录可池化的精确版本矩阵。

### C02 多地址建连与故障边界

**产物：**连接建立前顺序尝试候选地址；每个新物理会话重新 TLS/认证/模式/能力/初始化。任何已发送 SQL、事务、reader、locator 不迁移、不自动重放。

- [ ] 测试第一地址连接失败后第二地址成功，以及发包后断线不切换重试。
- [ ] 验证逐地址 TLS 名称、超时预算与模式校验。
- [ ] 直连和 OBProxy 故障测试记录目标版本及拓扑。

### C03 精确数值、纳秒与零日期

**产物：**在 B 阶段 `OceanBaseDecimal` 基础上扩展宽精度读写编解码；`BigInteger` 系数/scale 保真，时间值保留纳秒、offset/时区信息；MySQL `DATE`/`DATETIME` 列的 provider-specific 类型整列稳定，零日期不映射 `DateTime.MinValue`。

- [ ] 测试 MySQL `DECIMAL(65,20)`、Oracle 无精度/负 scale `NUMBER`、`TIMESTAMP(9)`、TZ/LTZ、同列混合正常值与零日期在文本/预处理路径的字段类型、provider-specific 值及往返。
- [ ] `GetDecimal`、`GetDateTime`、`DateTimeOffset` 只在可精确表示时转换，否则抛转换/溢出异常；provider-specific 类型整列稳定。
- [ ] 两模式实库测试记录会话时区、字符集与服务端列定义。

### C04 大值、SequentialAccess 与服务端游标

**产物：**`SequentialAccess` 不隐式缓存整值；游标以物理会话和 statement ID 为作用域，关闭/取消/断线时释放或销毁。

- [ ] 测试大值分块读取、乱序访问限制、提前关闭、游标分页、取消和无界内存增长。
- [ ] 只有模式/版本/能力条件经测试满足才启用游标；不支持时明确失败。
- [ ] 在两模式真实租户上测量内存峰值、吞吐和资源释放。

### C05 数据库目录元数据

**产物：**`DbConnection.GetSchema` 的首批集合、限制参数与现代 `GetSchemaAsync`；C 阶段实现 MySQL 模式 `ChangeDatabase` 和真实异步的 `ChangeDatabaseAsync`，Oracle 模式在发包前明确报不支持。MySQL 与 Oracle 模式的目录元数据分别使用对应系统视图，不复用错误方言。

- [ ] 固定首批集合清单及列形状：`MetaDataCollections`、`DataSourceInformation`、`Tables`、`Columns`；其他集合报不支持。
- [ ] 双模式测试名称大小写、schema、限制参数、权限不足和异步入口不阻塞网络等待；验证 MySQL 切换后的 `Database`/查询目标、失败时状态不变，Oracle 模式拒绝切换且不发包。
- [ ] 记录与 Java 元数据范围的差距，作为 D 阶段候选。

### C06 现代数据源与逐条 DbBatch

**产物：**`net8.0+` 的 `DbDataSource`、`DbBatch` 与 factory 入口；`DbBatch` 先逐条执行，保留每条 `RecordsAffected` 和失败前缀，不自动保证原子性。

- [ ] 实现后测试 `CanCreateBatch=true`、batch 事务绑定、逐条计数、失败位置、异步批量和结果未知；实现前不发布空壳 batch。
- [ ] `DbDataSource` 使用同一配置和池，公开连接串仍脱敏；旧目标不出现现代 API。
- [ ] 在 .NET 8/9/10 分别运行消费与集成测试。

### C07 诊断与性能基线

**产物：**`Activity`、`DiagnosticListener`/`EventSource` 的连接、命令、池和错误事件；默认无 SQL、绑定值或凭据。抓包单独显式开启并擦除认证/参数数据。

- [ ] 测试四目标事件名称/字段一致、敏感数据不出现、取消与结果未知被区分。
- [ ] 以预处理、多结果、大值和高并发池借还建立性能/内存基线；显著退化写入缺陷台账。
- [ ] 在两模式直连与 OBProxy 采集脱敏样本。

### C08 G3 生产门槛

**产物：**版本/模式/拓扑/TLS/认证/池化的公开兼容矩阵，重置隔离、故障和精确值的实测证据。不能完整重置的组合只发布直连能力。

- [ ] 运行全部四目标单测、双模式集成与故障套件，记录跳过、失败和环境阻塞。
- [ ] 审核池化白名单、内存/连接泄漏、取消后的 SQL 结果不确定性与可观测性。
- [ ] 无 P0/P1 缺陷且公开声明与实测一致时关闭 G3。

## D. 独立扩展切片

每个 D 项独立开关、独立版本/模式矩阵和回退行为；需求未明确或环境缺失时保持未开始。每项先写夹具和实库失败用例，完成后补公开 API 文档与资源释放测试。

| ID | 扩展 | 必须证明的边界 |
| --- | --- | --- |
| D01 | OB20、校验与完整链路追踪 | 能力协商、帧校验、标准包流回退、无敏感追踪 |
| D02 | Oracle 过程及 OUT/RETURNING | 参数方向、多个结果、失败时参数与事务状态 |
| D03 | REF CURSOR | 游标、事务和连接所有权及提前关闭 |
| D04 | LOB locator 与流式 LOB | locator 失效点、分块流、取消与内存上限 |
| D05 | ARRAY/STRUCT 与类型字典 | 嵌套值、类型缓存失效、模式差异 |
| D06 | bulk/array binding | 部分成功、失败位置、逐条更新计数 |
| D07 | 按需客户端预处理回退 | 安全文本编码、仅对明确不支持 prepare 的错误回退，断线不回退 |
| D08 | 拓扑发现与负载均衡 | 地址来源、选址、模式重验、已发送 SQL 不重放 |
| D09 | 显式 XA | 会话固定、恢复与真实事务协调器；`TransactionScope` 仍需独立设计 |
| D10 | Oracle NCHAR/national charset | 会话字符集识别、文本/预处理双向读写与元数据 |
| D11 | Oracle INTERVAL | 精度、符号、文本/二进制双向读写 |
| D12 | MySQL JSON | 文本/二进制值、NULL、元数据与双向读写 |
| D13 | MySQL BIT | 不同位宽、符号边界、元数据与双向读写 |
| D14 | MySQL YEAR | 合法范围、零值规则、元数据与双向读写 |
| D15 | 新认证插件 | 插件切换、TLS 先决条件和两模式实测 |

## Java 模块到任务索引

该索引用来检查[Java M01-M16 模块](obconnector-j-architecture-outline.md)是否有明确 .NET 落点；索引中的 D 项是有意延后，不代表首版功能缺失被忽略。

| Java 模块 | 主任务 |
| --- | --- |
| M01 入口与统一 API | B01、C06 |
| M02 URL、选项、地址 | A02、C02、D08 |
| M03 网络、TLS、认证 | A04、A05、D15 |
| M04 握手、模式、能力 | A05、D01 |
| M05 会话与本地事务 | A06、B07、C01、C05 |
| M06 SQL 与预处理 | B02-B04、D02、D07 |
| M07 参数和批量 | B03-B04、C06、D06 |
| M08 结果与游标 | B02、B06、C04、D03 |
| M09 类型、字符集与时间 | B05、C03、D10-D14 |
| M10 LOB 与复杂类型 | C04、D04-D05 |
| M11 元数据 | B06、C05、D02 |
| M12 HA 与地址发现 | C02、D08 |
| M13 内置池 | C01 |
| M14 XA | D09 |
| M15 诊断与 OB20 | C07、D01 |
| M16 构建与生态 | A01、B09、R01-R02 |

## 发布与交接

### R01 包与公开契约审查

- [ ] 固定 NuGet ID、版本、许可证声明和第三方归属；只打包四个目标资产和必要文档。
- [ ] 从空项目分别消费 `netstandard2.0`、.NET 8/9/10 资产，验证 provider factory、现代 API 可见性、公开类型身份和脱敏。
- [ ] 生成包并检查内容，公开不支持能力与兼容矩阵；不得把阶段 D 计划写成已支持。

### R02 候选版完整验证

- [ ] 再运行 G1-G3 全部必需套件与交付矩阵，留存命令、版本、日志链接及跳过原因。
- [ ] 对每个模式/版本/OBProxy/TLS/认证组合逐项标注“已验证/未验证/不支持”；无真实测试时不得标“已验证”。
- [ ] 完成发布说明、故障与结果未知指南、已知差异、回滚方案；通过最终代码和文档走查后才发布。

## 执行与进度规则

每项任务只有在代码、针对性测试、所属目标编译及进度证据都齐全时标为“完成”。G1/G2/G3/R02 还要求对应真实环境记录；跳过测试不等于通过。每次合并更新[进度台账](obconnector-dotnet-progress.md)的状态、证据、阻塞和下一任务。发现设计假设与真实服务端冲突时，先修订架构设计和本计划，再继续依赖任务。

统一验证入口（工程创建后执行；实际命令和退出码写入进度台账）：

```powershell
dotnet restore OceanBaseConnector.sln
dotnet build OceanBaseConnector.sln -c Release --no-restore
dotnet test tests/OceanBaseConnector.UnitTests/OceanBaseConnector.UnitTests.csproj -c Release --no-restore
dotnet test tests/OceanBaseConnector.ConsumerTests/OceanBaseConnector.ConsumerTests.csproj -c Release --no-restore
dotnet test tests/OceanBaseConnector.IntegrationTests/OceanBaseConnector.IntegrationTests.csproj -c Release --no-restore
dotnet pack src/OceanBaseConnector/OceanBaseConnector.csproj -c Release --no-build
```

单测和运行消费测试覆盖 .NET 8/9/10，并在 Windows/Linux CI 运行；`netstandard2.0` 消费工程只编译，另由独立宿主显式加载其包资产做运行验证，不把它当可执行测试目标。集成测试使用 `OB_MYSQL_CONNECTION_STRING`、`OB_ORACLE_CONNECTION_STRING`、`OB_MYSQL_PROXY_CONNECTION_STRING`、`OB_ORACLE_PROXY_CONNECTION_STRING`；关口验收设置 `OB_REQUIRE_INTEGRATION=1`，缺少必需环境时测试失败，日常开发可明确跳过并报告。证书、凭据和原始抓包不写入仓库。
