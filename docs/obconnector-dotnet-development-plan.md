# OceanBase Connector for .NET Implementation Plan

> **For agentic workers:** 执行时逐项读取[架构设计](obconnector-dotnet-architecture-design.md)和本计划；每项完成后更新[进度台账](obconnector-dotnet-progress.md)，提交实现前运行该项的验证命令。

**Goal:** 交付一个直接连接 OceanBase/OBProxy、同时支持 MySQL 和 Oracle 兼容模式的托管 ADO.NET provider，并在明确的服务端版本矩阵上证明其行为。

**Architecture:** 一个公开 provider、一个共享协议主链，每个物理会话保存模式、版本、能力和事务状态。同步与异步入口共享编码和状态机，分别使用同步与异步网络 I/O；C 阶段才开放经完整重置验证的池化。

**Tech Stack:** C#、.NET 10 SDK；单个 `netstandard2.0;netstandard2.1;net8.0;net9.0;net10.0` 核心 provider 包和可选的现代 DI 集成包，同版本统一发布；ADO.NET、`TcpClient`/`SslStream`、标准日志抽象、`System.Numerics.BigInteger`、xUnit。协议核心不引入 Oracle/MySQL 原生客户端。

**Spec:** [架构设计](obconnector-dotnet-architecture-design.md)，Java 行为基线为 [Connector/J 架构提纲](obconnector-j-architecture-outline.md)固定的提交 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`。这两个文档是设计和对照依据，不是 .NET 已实现能力的证明。

**.NET 接入修订（2026-10-18）：**架构设计 §2.1–§2.7将现代数据源、可选 DI 和基础日志前移到 B01；C06 保留 batch，C07 保留追踪/指标与性能。Java 仍是统一协议与模式行为的主要依据，`oceanbase-dotnet` 只作有限的宿主接入参考。B01 范围扩大，原周窗口只保留为估算，进入 B 前需重估，不由任务数量不变推断工作量不变。

## 全局约束与准入

- 当前排期以 A/B/C 均进入首个正式版本为基线，G0-5 决定是否调整；D 按独立能力交付。没有真实双模式测试和明确版本矩阵，不能把 A/B/C 标记为发布完成。
- 首版默认 `TLS Mode=VerifyFull`、`Pooling=false`、`CLIENT_FOUND_ROWS`；不自动重试已发送 SQL，不隐式客户端参数插值，不加入 `TransactionScope`。变更这些公开语义须先修订架构设计。
- `netstandard2.0` 定义公共类型身份；数据源/batch 等现代 ADO.NET API 用条件编译，仅在 `net8.0+` 暴露；`netstandard2.1` 另覆写其参考 API 已有的命令 `PrepareAsync`/`DisposeAsync`、连接/reader 的 `CloseAsync`/`DisposeAsync`、事务 `CommitAsync`/`RollbackAsync`/`DisposeAsync` 和连接受保护的 `BeginDbTransactionAsync`；`ChangeDatabaseAsync` 在尚未实现前明确拒绝。每个产品目标单独编译；全部测试及消费宿主统一使用 `net10.0`，显式选择并隔离加载 `netstandard2.0`/`netstandard2.1` 资产；B09/R01 另验证旧运行时消费。
- 仓库统一使用中央包管理(CPM)：根目录 `Directory.Packages.props` 固定全部 NuGet 包版本并开启传递依赖钉扎；工程的 `PackageReference` 一律不带 `Version`。产品及未来所有程序集的包版本统一由根 `Directory.Build.props` 的 `Version` 属性提供,csproj 不得各自声明版本。新增依赖只改 `Directory.Packages.props`。
- Microsoft 通用库按产品 TFM 条件集中定版：`netstandard2.0`/`netstandard2.1`/`net8.0` 使用 8.x 基线，`net9.0` 使用 9.x，`net10.0` 使用 10.x；具体补丁见架构设计 §2.3。统一发布版本不要求依赖主版本相同，.NET 10 SDK/测试宿主也不抬高旧资产依赖。验收逐目标 restore 图、程序集引用和 nuspec 依赖组，避免传递钉扎将旧目标提升到 10.x。
- 当前依用户要求暂不执行实库及非 Windows 运行测试；缺失证据保留在台账，不阻止依赖已满足的本地模块开发、走查和提交，也不关闭真实兼容门槛。
- 许可和包名在发布前审定；只参考 Java 行为和协议，不直接移植其 LGPL-2.1 实现。
- 进入 A 的真实互通验收前，应取得两个兼容模式的独立租户、服务端和 OBProxy 版本、认证插件、可验证的 TLS 证书及可控故障网络。缺少环境时只报告夹具验证。
- 所有任务先写会失败的边界用例，再实现，再运行该用例和所属阶段回归；原始协议夹具保留来源、版本、模式和预期字段，不存真实凭据。

## 交付目录与接口边界

| 位置 | 职责 |
| --- | --- |
| `OceanBaseConnector.slnx`、`global.json` | .NET 10 SDK 的统一解决方案和 SDK 主版本约束 |
| `Directory.Build.props` | 全部产品、程序集与包版本唯一来源 Version |
| `Directory.Packages.props` | 中央包管理：全部 NuGet 包版本的唯一来源，传递依赖钉扎 |
| `src/OceanBaseConnector/OceanBaseConnector.csproj` | 唯一核心 provider 程序集和五目标条件编译；日志仅依赖标准抽象 |
| `src/OceanBaseConnector/OceanBaseDataSource*.cs` | B01 的现代 builder/数据源、冻结配置与生命周期；C01 后拥有独立可选池 |
| `src/OceanBaseConnector.DependencyInjection/` | B01 新增的可选集成包，目标 net8.0/net9.0/net10.0；容器工厂创建统一数据源并接入宿主日志，无模式分支 |
| `src/OceanBaseConnector/OceanBaseConnection*.cs`、`OceanBaseCommand*.cs`、`OceanBaseDataReader*.cs`、`OceanBaseTransaction*.cs` | ADO.NET 公共对象；拆分文件按对象职责，不复制多套协议 |
| `src/OceanBaseConnector/OceanBaseParameter*.cs`、`OceanBaseProviderFactory.cs`、`OceanBaseConnectionStringBuilder.cs` | 参数、工厂和配置的公开契约 |
| `src/OceanBaseConnector/Internal/Transport/` | TCP、SSLRequest、TLS、同步/异步字节流与关闭取消 |
| `src/OceanBaseConnector/Internal/Protocol/` | 标准帧、握手、认证、命令、OK/ERR、文本/二进制行与多结果；D 阶段才加 OB20 |
| `src/OceanBaseConnector/Internal/Session/` | `PhysicalSession`、模式/能力快照、独占执行、statement ID、事务与失效状态 |
| `src/OceanBaseConnector/Internal/Sql/`、`Internal/Values/` | 占位符词法器、参数编码、模式感知列解码和 provider-specific 值 |
| `src/OceanBaseConnector/Internal/Pooling/` | C 阶段的池、会话完整重置与池键 |
| `tests/OceanBaseConnector.UnitTests/` | 不依赖服务端的字节夹具、API/状态机和错误路径测试 |
| `tests/OceanBaseConnector.IntegrationTests/` | 独立 MySQL/Oracle 租户、OBProxy、TLS、故障与重置测试；没有环境时跳过并明确报告 |
| `tests/OceanBaseConnector.ConsumerTests/` | 统一 .NET 10 消费宿主；编译样例显式选择 netstandard2.0，隔离加载覆盖 netstandard2.0/netstandard2.1 资产；发布时另验证包、工厂和公开值类型身份 |
| `tests/OceanBaseConnector.DependencyInjection.Tests/` | B01 新增，net10.0；内置容器释放、普通/key 注册隔离、配置与日志测试 |

`PhysicalSession` 独占 socket、包序号、模式、版本、协商能力及服务端 statement ID；一次只有一个命令或 reader 持有它。公开 `OceanBaseConnection` 是逻辑借用者。`OceanBaseCommand` 在发送前验证参数、事务和环境事务；`OceanBaseDataReader` 在关闭/读尽前持有响应；任何未读包、结果未知、重置失败都使物理会话失效。`SessionMode` 只能由认证 OK 的服务端状态确定，`ExpectedMode` 只做事后校验。

对外扩展类型先固定 `OceanBaseDbType`、`OceanBaseDecimal`、`OceanBaseTimestamp`、`OceanBaseTimestampWithTimeZone`、`OceanBaseTimestampLocalTimeZone`、`OceanBaseDate`、`OceanBaseDateTime` 的 `netstandard2.0` 类型身份；`OceanBaseDecimal` 的低精度列访问及范围内写入在 B05/B06 落地，宽精度编解码及其余精确时间类型在 C03 落地。不得让 `GetFieldType` 随行值变化。`OceanBaseException` 含服务端错误码、SQLSTATE、模式和 `IsExecutionOutcomeUnknown`，不含默认 SQL/参数/凭据。

## 阶段与日程基线

相对周数从双模式环境与产品决定可用的第 1 周算起，按 1 名全职开发者估算；代码评审、环境故障和服务端差异可能改变范围。周数是排期基线，不是已承诺发布日期。G0 未关闭时可先做 A01 工程骨架和 A03 不依赖实库的协议夹具，实际 W1 日期及 G1 关闭仍以 G0 完成为前提。任务可在依赖满足时并行评审，阶段门槛仍按顺序关闭。

| 门槛 | 相对窗口 | 完成定义 |
| --- | --- | --- |
| G0 准入 | W0 | 记录目标 OceanBase/OBProxy 版本、租户、TLS/认证、测试运行方式和 §9 产品决定；环境缺失列为阻塞项 |
| G1 协议基础 A | W1-W5 | A01-A07 完成；双模式真实连接/关闭与 TLS 拒绝场景有证据 |
| G2 ADO.NET 核心 B | W6-W17（接入修订后待重估） | B01-B09 完成；现代数据源/DI 生命周期与基础日志、双模式 CRUD、参数、低精度 `OceanBaseDecimal` provider-specific 访问、事务、取消、元数据和五目标消费证据齐全 |
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

**产物：**slnx 解决方案、唯一五目标产品工程、统一 net10.0 的测试及消费工程、CI 入口。消费测试显式选择并隔离加载 netstandard2.0/netstandard2.1 资产。

- [x] 建立多目标工程和目标条件编译样例；验证不存在现代 API 泄漏到 `netstandard2.0`/`netstandard2.1`。
- [x] 建立协议夹具格式：二进制数据、来源、服务端版本、模式、脱敏说明和期望解析结果。
- [x] 运行 `dotnet restore`、`dotnet build -c Release`、各测试目标的 `dotnet test -c Release`；将命令及日志位置登记到进度台账。

### A02 连接串与公开配置

**产物：**`OceanBaseConnectionStringBuilder`、不可变内部连接选项；解析 `Server`、`Port`、`User ID`、`Password`、`Database`、超时、`TLS Mode`、`TLS Server Name`、`Persist Security Info`、`ExpectedMode`、`Pooling`。未实现的选项不得静默接受。

- [x] 用测试固定空值、非法端口/超时、同义词冲突、未知键、密码序列化与公开连接串脱敏。
- [x] 解析一次并冻结内部配置；密码不进入异常/默认日志，池键规则留给 C01。
- [x] 运行连接串单测及五目标编译；在进度台账记录公开键清单。

### A03 标准包帧与字节游标

**产物：**协议帧读写器，处理 3 字节长度、1 字节序号、最大长度分片、命令边界序号重置、截断/越界/恶意长度。解析前检查剩余字节和整数溢出，不按不可信长度直接分配。

- [x] 用固定字节夹具先覆盖单包、多分片、零长度尾包、错序、截断、过长长度编码和服务端 ERR。
- [x] 分离包流与命令解析；同步/异步网络入口使用同一帧规则，异步不调用同步读写。
- [x] 运行协议单测，包含随机截断输入与资源上限断言。

### A04 TCP、SSLRequest 与 TLS

**产物：**`TcpClient` 建连、超时、`SslStream` 升级与端点/`TLS Server Name` 验证；`VerifyFull` 默认使用 OS 信任根，`Required` 和 `Disabled` 仅显式配置。

- [x] 本地字节/环回覆盖握手、CLIENT_SSL 检查、SSLRequest 与 TLS 升级顺序；TLS 失败后不继续认证。测试严格握手零认证字节、无 TLS 能力零发送、不受信证书零认证字节及同步/异步一致性。
- [ ] 以可验证私有 CA、错误主机名、IP 连接加显式 DNS 名称分别运行跨平台 TLS 测试；取消时关闭传输。
- [x] 本地失败测试断言连接拒绝、预取消、TLS 错误的异常和资源释放；无 TLS 能力或升级失败时拒绝连接，不明文降级。

A04 本地实现及 Windows 环回已完成，整体仍为待验证：可信私有 CA 成功链、真实错误主机名、实库 TLS 与非 Windows 证据未取得。依用户要求暂不执行缺环境部分，不把这些验收项混写为代码未实现。

### A05 认证、模式与能力

**产物：**初始握手/认证切换/OK 解析，按版本矩阵选择已验证插件；发送 `CLIENT_SUPPORT_ORACLE`、`CLIENT_SESSION_TRACK`、`CLIENT_FOUND_ROWS`、多结果等必要位。保存请求、服务器能力与实际生效能力；解析 `OB_SERVER_USE_ORACLE_MODE` 和 `ob_capability_flag`。

- [x] 合成夹具/环回覆盖 Oracle 位设置/未设置、无法可信识别模式、未知插件、能力缺失和认证错误；模式未知时不交付可执行连接。
- [x] 仅 VerifyFull 成功升级后允许 cleartext 类插件；认证切换复用同一安全检查；无验证 TLS 的 clear password 路径已有拒绝测试。
- [x] 本地握手/认证测试通过：native、认证切换、模式/能力快照、错误码和脱敏、同步/异步路径均有证据。
- [ ] 取得可信版本及插件矩阵后实现允许的 MySQL 模式判定规则；当前缺 Oracle 位安全拒绝，不能称已完成 MySQL 模式认证支持。
- [ ] 两模式真实握手和 OBProxy 验收，记录服务端/代理版本、插件与能力位；与非 Windows 测试均按用户要求暂缓。

当前实现边界（2026-10-18）：native challenge 和仅 VerifyFull 下的 clear password 编码尚无实库验收。没有已验证版本矩阵时，缺少 Oracle 状态位的连接明确失败，不能假设 MySQL；补齐 G0 后须以真实证据建立版本白名单再运行 A07。合成夹具中的 4.2.1 不是兼容承诺。

### A06 物理会话、错误与生命周期

**产物：**`PhysicalSession` 的 `Connecting/Open/Busy/Broken/Closed` 状态与独占租约；关闭/错误释放 socket、statement ID、结果状态。`OceanBaseException` 保留 SQLSTATE、服务端码及安全端点信息。

- [x] 测试同一物理会话并发执行拒绝、未读响应不得复用、错误后失效、重复释放幂等和日志脱敏。
- [x] 实现同步/异步关闭的资源所有权；不让后台清理线程替代显式状态转换。
- [x] 运行状态机单测及网络断开夹具。

### A07 G1 真实连接门槛

**产物：**在明确版本的 MySQL/Oracle 租户和 OBProxy 上打开、识别模式、关闭连接；记录 TLS 和认证组合。G1 是互通证据，不是仅有夹具的绿色构建。

- [ ] 两模式直连及 OBProxy 组合各跑连接/关闭、模式不符拒绝、错误证书和不支持认证。
- [ ] 将实际版本/端点类别、执行命令、脱敏日志及失败项填入兼容矩阵。
- [ ] 仅在全部必需组合通过时关闭 G1；其余记录为阻塞，不把夹具通过写成兼容通过。

## B. ADO.NET 核心

### B01 标准 provider、现代数据源与宿主接入

**产物：**`OceanBaseConnection`、`OceanBaseCommand`、`OceanBaseParameter`、`OceanBaseDataReader`、`OceanBaseTransaction`、`OceanBaseProviderFactory`；现代资产的 `OceanBaseDataSourceBuilder`/`OceanBaseDataSource` 和可选 DI 包；数据源级 `ILoggerFactory`。工厂创建正确派生类型，尚未实现的方法明确失败；SQL、reader、事务的执行能力仍由 B02-B08 逐项交付，不发布空壳能力声明。

- [x] 测试创建/关闭/重复使用、`DbProviderFactories.RegisterFactory`、`ConnectionString` 脱敏，以及 B 阶段 `ChangeDatabase`/`ChangeDatabaseAsync` 在发包前报不支持。
- [x] 将对象只连接到 A 阶段会话，不暴露内部 socket 或双模式两套公开类型。
- [x] 实现核心 builder/无池数据源、现代 factory 入口与日志上下文，可选 DI 工程及 slnx 项目项。五个资产的直接连接构造重载均可传入 `ILoggerFactory`，无全局工厂，基线资产也有日志配置入口。核心只引用日志抽象；集成包引用核心和 DI 抽象，两个包同一根 Version，依赖按 TFM 由 CPM 管理。
- [x] 实现普通/带 key 数据源注册；容器工厂延迟 Build，先取得宿主 logger 再执行配置回调，无日志服务时可用；默认不注册连接。数据源 Singleton，key/重复注册/通用别名冲突遵循架构设计 §2.5，不创建第二个容器。
- [x] 测试 Build/DI 解析不联网、配置冻结、来源数据源的连接配置不可替换、独立数据源不串配置、打开失败释放、数据源关闭与打开竞争、关闭后新打开拒绝、活动连接由调用者完成、释放幂等和外部 logger factory 不被释放。
- [x] 测试同步/异步连接事件、凭据脱敏、原始服务端异常不泄露、日志工厂/过滤/写入抛错不妨碍建连与清理；取消保留原 token，回调关闭不乱序发布状态。命令结果未知日志随 B02/B08 验收。
- [x] 新增 net10.0 DI 测试：容器创建/释放、scope validation、配置覆盖、无日志宿主及不同 key 隔离。
- [x] 五目标核心、三目标 DI 编译；运行工厂/隔离消费和逐目标依赖图测试，检查两包同版本及精确核心依赖。
- [ ] 取得可信 MySQL 模式版本证据后验证双模式公开建连路径；当前缺模式位仍安全拒绝，不为合成 MySQL 成功路径绕过门槛。实库、旧运行时和非 Windows 证据仍待补充。
- [ ] reader 的 `CommandBehavior.CloseConnection` 随 B06 验收。

### B02 文本命令、OK/ERR 与多结果

**产物：**`COM_QUERY`、文本行、OK/ERR、多结果状态；`ExecuteNonQuery`、`ExecuteScalar`、`ExecuteReader` 同步与异步路径共享状态机。

- [x] 夹具/环回覆盖 `SELECT`、DML、空结果、多个结果集、结果集后更新计数、服务器 ERR、有未消费响应的关闭，以及传统 EOF/协商后的 FE OK 终止。
- [x] 保留默认 `CLIENT_FOUND_ROWS` 能力；`RecordsAffected` 仅累计服务端 OK 的计数，不按客户端 SQL 猜测，也不把 SELECT 行数算入更新计数。真实不变 UPDATE 的匹配行语义仍待实库证明。
- [x] Windows 环回验证异步建命令/逐行读取等待网络，文本路径不缓存整个结果；同步调用同步 I/O。预取消/不支持参数/行为/环境事务在发包前失败；发包后取消、超时或关闭销毁会话并报告结果未知。B08 继续覆盖更完整的故障矩阵。
- [x] 使用 B01 日志上下文记录命令完成/错误/取消/结果未知，默认不记录 SQL 或绑定值；已收到可信 OK/ERR 后，日志回调触发取消也不覆盖终态。
- [ ] 两模式分别运行真实文本 CRUD 和不变 UPDATE 匹配行验证；依用户要求实库及非 Windows 测试暂不执行，MySQL 模式可信版本门槛保持不变。

B02 交付的 reader 支持基础有/无符号整数、浮点、UTF-8 字符串、binary 字符串和 NULL；保留物理会话模式，Oracle 类型码 3（NUMBER）当时明确拒绝，不借用 MySQL INT 映射。B05 已补精确数值、日期及低精度 provider-specific 访问，完整字段元数据仍属 B06；national charset、LOB 和其他扩展值不猜测解码。仅接受 `CommandBehavior.Default`/`CloseConnection`，`SequentialAccess` 等行为在对应能力实现前拒绝。响应硬上限为每包 64 MiB、每结果 4096 列/4 MiB 元数据、每命令一百万包；超限销毁会话。超出平台计时器范围的超时在取得会话前失败，不遗留 Busy 会话。

### B03 Oracle 占位符词法器与参数验证

**产物：**Oracle 命名 `:name` 到位置参数的映射；跳过单/双引号、注释、成对及非成对分隔符的 `q'...'` 字面量和 `:=` 赋值；重复名称展开到每个位置，无法可靠识别的字面量或绑定语法及缺失/多余绑定在发包前失败。`ParameterName` 接受有/无 `:` 前缀，去前缀后不区分大小写，参数集合中规范化后重名的多个项一律拒绝。MySQL SQL 不经客户端占位符改写，参数数量以 B04 的服务端 prepare 返回值为准。

- [x] 用普通字符串、`q'[...]'`/`q'{...}'`/`q'(...)'`/`q'<...>'`、非成对分隔符 `q'!:name!'`、未闭合字面量、双引号、注释、`:=`、重复名称/大小写和无参数 SQL 写用例；确认仅实际绑定被改写，无法可靠识别时发包前失败。
- [x] 实现 Oracle 单次词法扫描，保留原始参数顺序，不做客户端值插入；未知 Oracle 绑定语法明确失败。
- [x] 运行词法器单测及同步/异步公共命令环回测试，验证错误绑定不发包且会话仍可用。
- [ ] Oracle/MySQL 参数化集成测试依用户要求暂缓；合法参数实际执行由 B04 接入后验收。

B03 当前支持 ASCII `[A-Za-z_][A-Za-z0-9_$#]*` 名称（ParameterName 可带一个 `:`），按 OrdinalIgnoreCase 匹配；数字、带引号、Unicode 名称及 Oracle 字面量外的 `?` 显式不支持。`q`/`nq` 大小写均可，分隔符为单个非空白/非控制/非代理项 BMP 字符；单/双引号双写转义，反斜杠无转义含义。块注释不嵌套、以首个 `*/` 结束。规范化后的重复名称、缺失/多余参数和不支持的参数方向在命令租约/发包前拒绝，参数 Value 不参与 SQL 重写；集合本身的名称查找语义不变。校验依据已认证模式，关闭连接先报无可用会话。B03 单独交付映射与校验，服务端 Prepare 及合法基础参数执行已由 B04 接入。

### B04 服务端预处理与语句所有权

**产物：**`COM_STMT_PREPARE/EXECUTE/CLOSE`、二进制参数/结果、同一物理会话上的 statement ID；MySQL 参数数量以服务端 prepare 响应为准，Oracle 改写后数量须与响应一致。不支持 prepare 的 SQL 明确失败。`PrepareAsync` 和命令 `DisposeAsync` 在 netstandard2.1/net8.0/net9.0/net10.0 覆写并真实异步；netstandard2.0 仅提供其参考 API 支持的入口。

- [x] Windows 字节级/环回覆盖基础参数类型、有/无符号边界、跨字节 NULL 位图、类型变化、重复执行、多结果、畸形二进制行、错误关闭、断线后旧 statement ID 不复用。
- [x] 实现同步/异步发包与响应；保持 `Prepare()` 强制准备，失败不转成客户端插值。自动准备与执行共享一个租约/超时预算；只有实际执行发送后故障才报 SQL 结果未知。
- [x] Windows 环回确认 net10.0 和隔离加载的 netstandard2.1 资产 PrepareAsync 等待网络，不退回同步 I/O；覆盖执行、同步/异步释放和忙会话延迟关闭。
- [ ] 两模式运行真实预处理 CRUD、目标运行时非同步退化与类型往返测试；依用户要求实库、非 Windows 和旧运行时暂缓，缺 Oracle 模式位的握手仍安全拒绝。

B04 交付基础 bool/整数/浮点/普通 UTF-8 字符串/byte[]/NULL 编解码，B05 已扩展精确数值/日期；执行入口在发送其准备/执行包前拒绝显式 DbType 与 CLR 值不匹配及未支持类型，显式 Prepare/PrepareAsync 不检查 Value 编码。语句 ID 仅在同一命令、相同改写 SQL 和同一物理会话复用；变更、重连和释放时失效，空闲关闭或忙时排队到下次命令前。服务端参数数不符不执行 SQL，准备元数据 ERR 关闭已知 ID，终态执行 ERR 保留可复用会话；未消费响应则销毁会话。B06 已补 reader 字段元数据、B07 已接入本地事务；B08 本地故障矩阵已补齐，真实环境验收仍未完成，不据此关闭 G2。

### B05 基础值、字符集与不支持类型

**产物：**模式感知编解码支持基础整数、浮点、可表示的 `decimal`、低精度列的 `OceanBaseDecimal` provider-specific 读取和范围内参数写入、文本/二进制、NULL、常见日期时间及含时间的 Oracle `DATE`；宽精度和延期类型显式失败。

- [x] 夹具覆盖有符号/无符号边界、乱码、NULL/空串、Oracle `DATE` 时分秒、低精度 NUMBER/DECIMAL 经 `OceanBaseDecimal` 精确读取和写入、MySQL `DECIMAL(65,20)` 在 C 前失败、NCHAR/INTERVAL/JSON/BIT/YEAR 未支持时失败。
- [x] 编码依据 `DbType`/显式 `OceanBaseDbType`，解码依据模式、列定义、字符集、精度；普通 `string` 参数不声称支持 NCHAR/JSON。
- [ ] 两模式运行真实基本类型往返和字段元数据一致性测试；依用户要求暂缓，合成报文及 Windows 环回不能替代。

B05 本地实现及 Windows 验证已完成，整体待验证。低精度读列使用固定 decimal/OceanBaseDecimal 类型，元数据缺失、p>28 或负 scale 明确拒绝；Oracle 优化 NUMBER 类型码 3 的四字节布局独立处理。参数保留显式/推断 scale 的差异、不舍入；日期时间保留 Oracle DATE 的时分秒，MySQL TIME 按有符号时长处理，Oracle TIMESTAMP 202 按原始 12 字节布局处理，精确范围及拒绝规则见架构 §5。公开 reader 文本/二进制、同步/异步命令前置校验和五目标隔离资产消费已有测试；真实 Oracle 空字符串/NULL、TIMESTAMP scale 变体、旧运行时及非 Windows 验收仍缺证据。GetSchemaTable/GetColumnSchema 留 B06。

### B06 逐行 reader 与列元数据

**产物：**逐行 `Read`/`ReadAsync`、`NextResult`/`NextResultAsync`、`GetSchemaTable`、`IDbColumnSchemaGenerator.GetColumnSchema`、固定的 `GetFieldType`/provider-specific 字段类型；低精度 NUMBER/DECIMAL 列的 `GetProviderSpecificFieldType` 与 `GetProviderSpecificValue` 使用 `OceanBaseDecimal`。B 阶段允许有上限的当前行缓冲，不缓存整个结果集。

- [x] 测试跨多结果读取、早关、`CommandBehavior.CloseConnection`、零/空值、低精度列的标准/provider-specific 类型随行稳定、未消费响应的会话处理；零日期继续按 C03 边界拒绝。
- [x] 实现 reader 在响应未消费完时独占物理会话；`ExecuteScalar`/`ExecuteNonQuery` 安全消费或使会话失效。
- [ ] 两模式对照真实服务端列定义验证名称、精度、长度、可空性和异步读取；合成列定义/Windows 环回已通过，实库暂缓。

B06 本地实施步骤（现有 master，基线 b772c0f）：

- [x] `ResultColumn` 保留数据库/表/原始表列名、原始长度与 flags；新增内部 `ResultDbColumn`，把它们映射到跨五目标的 `DbColumn`，不增加依赖。名称、类型名及解码共用同一列定义，MySQL 字符长度按已支持字符集宽度换算，Oracle 普通字符长度按其元数据单位保留；超出 int 的长度饱和为 int.MaxValue。MySQL 数据库名映射 BaseCatalogName，Oracle 映射 BaseSchemaName。
- [x] `OceanBaseDataReader` 实现 `IDbColumnSchemaGenerator` 及 schema table；返回独立快照，未提供的唯一性/表达式/可更新性保持未知；ProviderType 在 B06 时未知，已由 B09 补齐，ProviderSpecificDataType 与实际 provider-specific 读取一致。无列结果的 schema table 为 null、column schema 为空；推进结果时不复用旧元数据。
- [x] 先以公开文本/二进制、同步/异步环回及内部 MySQL/Oracle 列定义测试证明缺口，再验证大小/精度/NULL/别名/多结果/提前关闭/独占/安全消费和五目标消费；编译、完整 Windows 单元/消费/DI、format、包矩阵、独立走查通过后提交。实库及非 Windows/旧运行时验收继续留空。

B06 本地实现边界见架构 §5：GetColumnSchema/GetSchemaTable 已有跨五目标消费证据；独立源码走查未发现需修复问题，另补零行 SELECT 保留 schema 的回归。真实字符长度/Oracle 空值语义、真实服务端元数据及旧运行时/非 Windows 仍未验证。B06 当时的 ProviderType 未知项已在 B09 补齐标量枚举与参数映射，不伪装协议码。

### B07 本地事务与环境事务拒绝

**产物：**`BeginTransaction`、隔离级别逐模式映射、显式 `DbCommand.Transaction` 绑定、提交/回滚/释放；现代事务异步入口真实异步。`TransactionScope`/`EnlistTransaction` 在发包前拒绝。

B07 本地实现：沿 Java 的 `SET SESSION TRANSACTION ISOLATION LEVEL ...`、`set autocommit = 0` 会话路径，在第一条业务语句时由服务端实际开始事务；不向 Oracle 模式发送 PL/SQL `BEGIN`。Unspecified 明确选择 MySQL RepeatableRead / Oracle ReadCommitted；MySQL 支持四个常规隔离级别，Oracle 首先只开放 ReadCommitted/Serializable（Java 允许 RepeatableRead 不能代替实库证据）。提交/回滚后恢复 `set autocommit = 1`。终态提交/回滚已确认但恢复失败时销毁会话并保留已确认结果，单独记录恢复失败；终态 ERR 保留活动事务供回滚，未确认响应则销毁。关闭/释放清理复用该路径，忙会话直接销毁，不向未读响应插入回滚。模式映射有内部测试；公共握手仍只接受已认证 Oracle 模式，不能据此宣称 MySQL 事务已互通。

- [x] Windows 环回测试遗漏、错误连接、已结束事务、重复开启、隔离级别错误、关闭时回滚及回滚失败销毁。
- [x] Windows 环回测试连接打开前/后进入 `TransactionScope`，已实现的调用方协议入口拒绝但内部清理可继续。
- [x] Oracle 合成环回验证同步/异步提交、回滚、断线/取消后结果未知，以及五资产公开事务消费；现代资产延迟响应证明 Begin/End 不阻塞调用线程。
- [ ] 两模式实库提交、回滚、隔离级别及断线验收；实库、非 Windows 及旧运行时暂缓。
- [x] 沿用 B01 日志上下文，覆盖事务完成/失败及结果未知；不记录 SQL/参数或原始服务端错误文本，日志异常不能掩盖提交结果。

2026-10-18 本地验证：五目标核心/三目标 DI Release build 0 警告/错误；Windows Unit455、Consumer34、DI8 通过（`TestResults/b07-{unit,consumer,di}.trx`）。独立走查指出的 reader 异步释放同步回滚、延迟关闭后重开事件顺序已有失败后通过的回归；日志工厂在事务执行前创建类别，避免惰性创建重入。CloseConnection reader 的现代异步清理沿用连接异步回滚；关闭先发布逻辑 Closed，旧会话清理不能销毁新会话。格式、双包和资产/依赖版本检查通过。整体仍待验证，G2 不关闭；下一模块 B08。

2026-10-19 中断走查续接修正：直接检查 NETStandard.Library.Ref 2.1.0 的 netstandard.dll 元数据，确认连接/reader/事务异步生命周期 API 已存在；将四处仅 NET8_0_OR_GREATER 的条件扩大至 NETSTANDARD2_1，复用同一异步实现。先以隔离消费的 2.1 五种延迟回滚清理和事务覆写共6项失败证明缺口，修复后17项定向通过，补充 COMMIT/ROLLBACK 等待网络及预取消原 token 的检查。完整验证与延期边界见进度台账；不把本次修复当作 C03 TZ/LTZ 子项完成。

### B08 取消、超时与结果未知

**产物：**发送前取消返回 `OperationCanceledException`；发包开始且无可信终态响应时返回 `OceanBaseException(IsExecutionOutcomeUnknown=true)` 并销毁物理会话。`DbCommand.Cancel()` 使用相同状态机。

- [x] 本地故障注入覆盖发送前、部分头/负载发送、请求已收到但无终态响应、完整 ERR/OK 与取消竞争、`Commit`/`Rollback` 中断。合成环回不证明真实服务端执行，实库执行后断线另验收。
- [x] 协调调用方 token、Cancel、预算超时与连接关闭；外部 token 保留、内部预算到期映射 TimeoutException。完整终态响应不被迟到取消覆盖，传输可保守销毁；重开后的新会话不受旧命令 Cancel/Dispose 影响。
- [x] 同步/异步确定性故障测试验证不重放；异步部分发送测试使用真实预算 token 等待中止，并断言精确已写前缀、结果未知/准备区别、Broken 状态及日志归因。
- [ ] 真实双模式/OBProxy 验证执行后断线、提交/回滚中断、取消与网络时序；非 Windows 与旧运行时验收依用户要求/现有环境暂未执行。

2026-10-19 本地验证：Release build 核心五目标/DI 三目标0警告/错误，Windows Unit498、Consumer39、DI8 全通过（TestResults/b08-{unit,consumer,di}.trx）。独立走查建议的部分异步发送取消已补7例，其中旧逻辑的超时3例失败后修复通过；接收路径超时与日志误分类亦有回归。五资产消费在 net10.0 宿主通过预取消与命令超时，格式、双包、依赖分组与统一版本校验通过。B08 本地实现完成但保持待验证，下一模块 B09；A05 可信 MySQL 模式判定缺口及 G0/G1/G2 门槛不变。

### B09 G2 公共 API 与双模式验收

本地实施步骤（2026-10-19）：先以字段 schema/参数编码回归固定 ProviderType 缺口；在 OceanBaseDbType 末尾追加基础标量族而保持既有0..9数值，OceanBaseParameter 映射到已支持的 DbType/编解码，ResultColumn/ResultDbColumn 提供与该枚举一致的整数 ProviderType。普通字符串与字节族归一为 String/Binary，原始数据库类型仍由 DataTypeName 区分，不伪装原始协议码；NULL 独立映射且只接受空值。更新现有 reader/五资产消费测试，核查 factory/DI/日志及明确不支持的公共入口；最终运行五目标/三目标 build、Windows 单测/消费/DI、格式、双包及资产/依赖组校验，独立源码走查后提交。实库/非 Windows 继续暂缓；net10.0 宿主不能替代旧运行时验收。

**产物：**B01-B08 的核心五目标编译/消费、现代数据源/DI 生命周期与基础日志、双模式 CRUD、参数、低精度 `OceanBaseDecimal` provider-specific 访问、事务、reader、取消证据；公开 API 审查记录，列明与 Java 的有意功能差距。

- [x] .NET10 SDK 的 Release slnx 核心五目标/DI 三目标编译和 Windows 单测/消费/DI 通过，日常宿主保持 net10.0；独立核心样例取得五资产在 Windows .NET8/9/10 的基础消费证据，范围见下。
- [ ] 旧运行时现代 DI、全部核心 API 和 .NET Framework4.8 正式支持仍需 R01/版本矩阵验收，不能由基础样例替代。
- [x] 基线隔离消费解析日志依赖，核心无反向 DI 引用，Standard 无现代数据源类型；数据源/DI/factory 的配置、安全与生命周期本地证据通过，实际模式/SQL 互通仍受下列实库门槛约束。
- [x] 公共 API 审核及与 Java 的有意差距归档到架构 §5；补齐 ProviderType/标量参数映射、保持原枚举身份，修复命令事件类别过滤。
- [ ] 在双模式直连和 OBProxy 上运行 B 阶段集成套件，登记版本、失败用例及已知不支持项。
- [ ] 仅当必需组合全部通过、无 P0/P1 缺陷时关闭 G2。

2026-10-19 B09 本地结果：新增65项类型/模式单元测试（首批49项先失败后通过），补5资产既有 schema/parameter 消费断言；新增4项命令日志类别回归先红后绿。最终 Release build0警告/错误，Windows Unit567、Consumer39、DI8 全通过（TestResults/b09-{unit,consumer,di}.trx）。格式/diff、双包及 ZIP/XML5/3资产、8依赖组、统一0.1.0-alpha和精确核心依赖检查通过。源码独立走查无剩余需修复项。

运行时基础消费：`RuntimeConsumerSample.cs` 用 SDK10 csc 编译为独立 Smoke.dll，reference pack 选择8/9/10、仅引用对应产品 DLL 和 project.assets.json 对应 runtime 包；把依赖放入各自私有目录，runtimeconfig 固定该 Microsoft.NETCore.App 主版本、LatestPatch。生成的 compile.rsp/runtimeconfig 与 results.txt 位于忽略目录 artifacts/b09-runtime-smoke，各目录执行 `dotnet <SDK>/Roslyn/bincore/csc.dll -noconfig @<目录>/compile.rsp`，再执行 `dotnet <目录>/Smoke.dll <运行时主版本> <日志主版本> tests/fixtures/protocol/initial-handshake-oceanbase.json <产品TFM>`。结果：Standard2.0/2.1/net8→8.0.31/日志8，net9→9.0.20/日志9，net10→10.0.12/日志10，5次均通过预取消/Oracle环回/decimal/schema/关闭验证。源码纳入版本管理，生成材料仅作本地证据，日常测试工程仍只面向 net10.0；未覆盖真实数据库、所有 API 或旧运行时 DI。

B09 本地审核/补齐完成、状态待验证；双模式直连/OBProxy、非 Windows、完整旧运行时消费与 A05 MySQL 判定仍未完成，G2 不关闭。下一模块 C01 池与完整会话重置；在可信完整重置版本白名单建立前仍拒绝 Pooling=true。

## C. 生产连接

### C01 池、完整重置与会话隔离

2026-10-19 核查：暂无通过真实完整重置验证的版本/模式白名单。Java 2.4.14 的 OceanBaseConnection.reset（2473行起）也只在特定 MariaDB 版本启用 COM_RESET_CONNECTION，否则依赖驱动跟踪状态，不能证明原始会话 SQL/临时对象均清理。C01 保持阻塞，公开 Pooling=true 继续拒绝；不实现测试专用的池化放行开关。按当前暂缓实库测试的要求，先推进不依赖池化的 C02。

**产物：**由数据源拥有、按规范化连接选项、凭据身份/材料和 TLS 目标分组的可选池；默认不池化。仅在版本/模式组合的完整重置已验证后接受 `Pooling=true`。不同显式数据源不共享物理会话，池日志继承各自上下文；数据源释放后清空空闲连接、活动借用归还即销毁，打开/释放竞争不能产生游离会话。直接连接入口的池作用域须与显式数据源隔离，沿用同一池/重置实现。

- [ ] 测试 `SET`/`ALTER SESSION`、schema、用户变量、临时对象、未完成事务、准备语句、reader 和重置失败不会跨借用者泄漏。
- [ ] 归还时读尽/销毁未消费响应、回滚、服务端重置、重做初始化；任何无法证明已清理的状态销毁物理会话。
- [ ] 测试池上限、等待超时、凭据轮换和故障清池；记录可池化的精确版本矩阵。

### C02 多地址建连与故障边界

**产物：**连接建立前顺序尝试候选地址；每个新物理会话重新 TLS/认证/模式/能力/初始化。任何已发送 SQL、事务、reader、locator 不迁移、不自动重放。

本地实施步骤（2026-10-19，沿用已授权的 C02 范围）：

1. 在既有 Server/Host 键支持逗号分隔的 host[:port]、[IPv6][:port] 和不带端口的 IPv6；省略端口继承 Port（默认2881）。保留原有键和单地址，候选两侧空格忽略，空候选/非法主机/端口在配置边界拒绝；不新增 JDBC URL、角色或发现语法。用 builder 非法输入测试及公共环回首地址失败用例先证明缺口。
2. Internal/OceanBaseEndpoint 负责解析，OceanBaseConnectionOptions 冻结只读候选，并派生保留 TLS/凭据/模式等全部配置的单端点快照；PhysicalSession 同步/异步仅在 TCP 建连的 socket 故障时顺序尝试。整个 Open 共用一个超时/取消预算（0无限），不因候选数重置；一旦 TCP 建立，TLS/认证/协议/模式失败即终止，不掩盖配置或安全失败。
3. 每次 Open 从首候选开始；连接和数据源入口共用物理会话代码。TLS SNI/证书名及认证 _server_host 使用实际主机，显式 TLS Server Name 仍优先。DataSource 保留配置的 Server 列表，成功连接、关闭、命令、事务日志记录实际端点；默认日志不含密码或完整连接串。
4. Windows 环回验证同步/异步及五资产消费、显式/继承端口、IPv6解析、全部失败、取消/关闭/总预算、TLS名称与拒绝、模式不匹配，以及发包后断线不尝试备用地址。编译核心五目标/DI三目标、单元/消费/DI、格式、双包和依赖矩阵后独立走查并提交。真实直连/OBProxy、非Windows继续留空。

- [x] Windows 合成环回测试第一地址连接失败后第二地址成功，以及发包后断线不切换重试；真实拓扑留下一项。
- [x] Windows 环回验证逐地址 TLS SNI/认证主机、显式 TLS 名称、证书拒绝、取消/关闭/总超时和模式校验；可信私有 CA 成功链仍待环境。
- [ ] 直连和 OBProxy 故障测试记录目标版本及拓扑。

2026-10-19 本地结果：新增60项单元、10项五资产同步/异步消费；首批26项地址/输入回归先全失败后全通过。Release slnx 核心五目标/DI三目标0警告/错误，Windows Unit627、Consumer49、DI8通过（TestResults/c02-{unit-final,consumer,di}.trx）；独立走查修正了总预算测试对环回拒绝耗时的错误假设，调整后2项单独通过（c02-budget-reviewed.trx）。总预算回归证明整个Open的耗时上界，共享预算不重置另由源码走查确认：Open/OpenAsync均在候选循环外仅创建一次预算；TCP及异步握手使用同一预算token，同步TLS/认证由该预算注册关闭传输来中断。格式、diff、双包及5/3资产、8依赖组、统一0.1.0-alpha和分TFM的Microsoft.Extensions8/9/10检查通过；NuGet README提示仍留R01。

实际Windows运行时消费沿用B09的RuntimeConsumerSample.cs，增加首地址拒绝后的公开查询，使用相同SDK10 csc/reference pack/私有包依赖/LatestPatch隔离方式；五资产分别在8.0.31（两Standard及net8）、9.0.20、10.0.12通过，日志程序集主版本分别8/9/10。复现脚本、响应文件和结果位于本地artifacts/c02-runtime-smoke（未纳入版本管理），方法与B09一致。此样例没有扩大为旧运行时DI、全部API或真实双模式验收。C02本地实现完成、整体待验证；C01继续阻塞，下一可本地推进模块C03，G3保持0/8。

### C03 精确数值、纳秒与零日期

**产物：**在 B 阶段 `OceanBaseDecimal` 基础上扩展宽精度读写编解码；`BigInteger` 系数/scale 保真，时间值保留纳秒、offset/时区信息；MySQL `DATE`/`DATETIME` 列的 provider-specific 类型整列稳定，零日期不映射 `DateTime.MinValue`。

2026-10-19 分步实施（C03在所有子项完成前保持进行中）：先实现精确数值，后实现纳秒/时区和零日期，不把数值子项通过标为整个C03完成。数值改动贯通DecimalCodec、ResultColumn、ResultDbColumn、PreparedParameters、OceanBaseDataReader及五资产消费；先用DECIMAL(65,20)、NUMBER(38)、未知精度标记、负scale和精确转换回归证明缺口，再补实现。标准类型由列声明固定：可靠p<=28且0<=s<=p为decimal，其余为OceanBaseDecimal；NULL不缩窄类型。Oracle p=255/s=-85按Java ColumnDefinition归一为未知p=0/s=0；未知精度不伪造schema精度，p0/s0按payload保留系数/scale；p0且已知非零scale时按该scale精确重标度，校验精度上限使用Oracle38/MySQL65，schema精度仍未知；声明负scale时精确重标度，不舍入。MySQL最大p65/s30，Oracle声明p38/s[-84,127]及非零值绝对值[1e-130,1e126)范围，超界明确失败；未知NUMBER按最多38个有效数字和该范围校验。GetDecimal/GetFieldValue<decimal>只作精确转换。参数不经过CLR decimal或浮点缩窄；沿Java BigDecimalParameter的长度编码普通十进制文本，不发送科学记数法，负scale扩成整数，服务端目标列元数据决定读回scale。DbParameter.Scale是byte，负scale从OceanBaseDecimal.Scale表达，不新增不兼容的Scale覆盖。所有本地编译/回归/打包和独立走查通过后提交数值子项；实库与非Windows继续暂缓。

- [x] 数值子项本地编译、测试与独立走查：MySQL `DECIMAL(65,20)`、Oracle 无精度/负 scale `NUMBER`；文本/预处理字段类型、provider-specific 读取和写回，五资产消费及模式边界回归。Unit672/Consumer54/DI8、格式与双包资产/依赖矩阵通过，详见进度台账。
- [x] `GetDecimal`/`GetFieldValue<decimal>` 只作精确转换；声明列类型不随当前值或 NULL 改变。修复未声明参数精度时的误限制、MySQL负scale的声明精度漏检及零值误拒绝，均有失败后通过的回归。
- [x] 不带时区的Oracle `TIMESTAMP(0..9)`本地读写/精确转换/整列类型/五目标消费及独立走查；Unit716/Consumer59/DI8，编译、格式、打包矩阵通过，详见台账。
- [x] 不带时区的 `GetDateTime`/`GetFieldValue<DateTime>` 只在100ns可精确表示时转换，否则溢出；所有p的provider-specific类型固定为OceanBaseTimestamp。
- [x] TZ 文本/预处理读取、参数写回、稳定标准/provider-specific类型、精确 DateTimeOffset 转换和五资产消费；独立走查的参数长度及负offset问题已回归修复，详见台账。
- [x] LTZ记录服务端会话时区标识，查询补全/结果快照、精确201读写、参数时区一致性及五资产消费；本地验证见进度台账，真实互通待验。
- [ ] MySQL同列混合正常值与零日期在文本/预处理路径的字段类型、provider-specific值及往返。
- [ ] 两模式实库测试记录会话时区、字符集与服务端列定义。

2026-10-19 纳秒子项执行细化：先贯通不带时区的 Oracle TIMESTAMP(0..9)，TZ/LTZ 和 MySQL 零日期仍保留后续子项。OceanBaseTimestamp 为五目标共享只读值，保存 DateTimeKind.Unspecified 的整秒 WholeSeconds、0..999999999 的 Nanosecond 和0..9的 Scale；构造时拒绝混入亚秒/时区或不符合scale的纳秒，不舍入。ToDateTime 仅在纳秒为100的倍数时成功；年份范围仍为1..9999，超出范围明确失败。标准字段p<=7为DateTime、p8/9为OceanBaseTimestamp，provider-specific访问对所有p都固定为OceanBaseTimestamp，NULL不改变类型。沿Java type202/12字节布局读取两种结果协议，维持载荷scale和列scale一致校验；参数默认保留provider的Scale（普通DateTime仍默认7），显式Scale只允许精确重标度，不改Java固定写9的实现但记录差异。修改到达TemporalCodec、ResultColumn/DbColumn、OceanBaseParameter/PreparedParameters、reader和五资产消费；先验证缺口，再补公开值、codec、公开环回及消费回归，最后完整编译/测试/独立走查/打包后提交。重点回归p8/9小值和NULL不缩窄、纳秒1/999999999、边界日期、非法长度/scale/小数、错误模式以及发包前拒绝。

2026-10-19 TZ/LTZ 执行细化：先交付可独立验证的 TZ 全链路，再处理 LTZ 的可信会话时区来源，C03 始终保持进行中。TZ 的公共 OceanBaseTimestampWithTimeZone 组合已有 OceanBaseTimestamp、分钟精度 offset、可选时区名称及缩写，不用客户端时区规则重算服务端 offset；DateTimeOffset 输入保留本地字段和 offset，ToDateTimeOffset 仅精确转换，越界或纳秒不能整除100时失败。所有p0..9的标准/provider字段类型固定为该类型；type200 两种结果协议共用前12字节时间+小时符号/分钟幅值+可选名称/缩写长度字段，严格检查长度、UTF8、offset及scale；结果与参数均采用小时符号/分钟幅值，小时为零时分钟带符号；结果外层长度编码，参数单字节长度并在超过255字节时发包前拒绝。名称/缩写各按UTF-8限255字节，实际参数总长度另校验；固定server源码证据及名称优先于offset的语义见架构§5，实库接受范围仍待验。参数默认保留scale、显式scale仅精确重标度；DbType.DateTimeOffset和追加的OceanBaseDbType.TimestampWithTimeZone映射到Oracle200，MySQL及不匹配的显式类型在发包前拒绝，旧枚举数值不变。覆盖公共值、codec、同步/异步文本/二进制环回、读写回、NULL稳定schema、恶意载荷和五资产消费；编译/完整测试/格式/包矩阵和独立走查后提交。LTZ不得采用Java默认客户端时区；下一步明确会话时区查询或追踪/失效与结果快照，不能用认证时快照解释任意后续SQL。

2026-10-19 TZ子项本地验证：Unit772/Consumer76/DI8全部通过，核心五目标/DI三目标Release编译0警告/错误，格式、diff、双包和资产/依赖版本矩阵通过。独立走查发现并修复参数单字节长度、负小时分钟幅值、结果名称内部长度编码三项问题；结果最大530字节、参数最大255字节，均有先失败后通过的边界回归。五资产消费宿主仍为net10，真实互通、非Windows及旧运行时未验收；C03保持进行中，LTZ/零日期继续后续实现。


2026-10-19 LTZ会话时区前置实施：先修复认证之后的会话时区状态通路，仍不开放type201。PhysicalSession保存独立于ConnectResult认证快照的可失效time_zone事实；认证报告作为初始来源，任意SQL发送前捕获执行快照并立即使物理会话当前值失效，避免把可能修改会话的SQL之后的旧值复用。prepare/statement-close不执行SQL，不作此失效；连接关闭/故障清除状态。QueryResult在读取每个结果头包前，从执行开始快照/上一报告建立每结果的时区快照，该快照不因本结果终态更新而回写；OK头、弃用EOF的OK终态和事务控制OK均消费time_zone报告，后续结果以最新报告为来源，每结果开始再次使会话当前值和后续来源失效，覆盖报告A→无报告→第三结果未知的链条。经典EOF或ERR不伪造会话变量，也不把认证默认值补回。时间值解码前还需可靠的按需/执行前SESSIONTIMEZONE查询补全未知状态；本前置提交不声称LTZ已实现或可信追踪已证明。

修改贯通PhysicalSession、CommandExchange、QueryResult与事务控制OK路径，不新增公开配置或依赖。先用Windows同步/异步协议回归验证认证快照独立、任意SQL失效、终态/多结果更新、结果快照独立、非执行准备不失效和故障/关闭清空；再实现最小共享状态方法。完成编译、完整测试、格式/打包/版本矩阵和独立审查后提交；LTZ后续仍须查询来源、参数时区一致性及type201完整读写，不能用此状态通路替代LTZ交付。

2026-10-19 LTZ状态前置验证：25项定向回归（首批12失败、审查补充2失败后均通过）纳入完整Windows Unit797，Consumer76/DI8通过；核心5/DI3目标Release编译0警告/错误，格式/diff、打包和资产/依赖矩阵通过，独立复审无剩余必须修复项。QueryResult捕获已移到读取头包前，后续ERR也使时区失效。完整LTZ查询/参数/值编解码和零日期仍待后续实现。


2026-10-19 LTZ完整链路实施：公共只读OceanBaseTimestampLocalTimeZone组合OceanBaseTimestamp和SessionTimeZone（非空、严格UTF-8<=255字节、无空白/控制字符；SYSTEM未解析标记拒绝，数字offset严格±HH:MM/±14小时）。不解析本机时区、不提供推测DateTimeOffset；ToDateTime/GetDateTime仅精确转换且Kind=Unspecified。未初始化default值无时区，不能写入。所有p0..9标准/provider-specific类型固定，新增OceanBaseDbType.TimestampLocalTimeZone末尾28、DbType.DateTime2仅在显式provider身份或provider值时选择Oracle201，不更改普通TIMESTAMP的202身份。

执行前若Oracle物理会话没有可用时区报告，在现有独占租约/超时预算内发送SELECT SESSIONTIMEZONE FROM DUAL。只接受单个字符串列、单行非NULL、<=255字节的严格标识和可信终态，不改变用户RecordsAffected、不单独记录命令，不把查询失败标记成用户SQL已执行；先检查所有普通绑定/值，查询完成且LTZ参数标识与当前时区完全一致后才prepare/execute。已报告且合法的时区可直接使用；每次SQL仍沿前置通路失效，不长期缓存。prepare-only不查询，事务控制不因不返回LTZ增加查询。内部查询支持经典EOF/FEOK，在同一交换中读完不释放租约，未知响应销毁不重放；额外往返是此正确性选择的成本，暂不新增配置。

结果column绑定QueryResult每结果冻结的时区标识；未知时保留稳定类型及DBNull（仍校验模式/scale），仅非NULL201值明确拒绝解码，不在活动reader中插入查询或改为全量缓存。任意后续SQL或多结果没有可靠报告时不能拿旧标识解释LTZ；有报告的多结果使用新标识。201前12字节沿202时间布局，两协议读取/参数写入，纳秒/scale严格不截断。修改到达新公共值、TemporalCodec、ResultColumn/QueryResult、PreparedParameters/Parameter/DbType、reader、CommandExchange内部查询与OceanBaseCommand执行路径；同步/异步/五资产回归和未知时区/发包前参数不匹配/查询故障/结果快照边界贯通，完整验证/独立走查后提交。固定Java及server证据见架构§5；数据库及非Windows验收继续暂缓。

2026-10-19 LTZ子项本地验证：初始8项缺口由失败转通过；独立走查再复现3项冲突报告/显式Timestamp覆盖失败，修复后49项定向通过。最终Unit846/Consumer81/DI8，核心五/DI三目标Release编译0警告0错误，格式、diff、打包资产/依赖矩阵通过；当前状态及真实环境缺口见台账。C03保持进行中，剩余MySQL零日期及真实验收。

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

### C06 逐条 DbBatch 与数据源批量入口

**产物：**`net8.0+` 的 `DbBatch`、factory 和 B01 数据源的批量入口；`DbBatch` 先逐条执行，保留每条 `RecordsAffected` 和失败前缀，不自动保证原子性。基础数据源不再延后到本任务。

- [ ] 实现后测试 `CanCreateBatch=true`、batch 事务绑定、逐条计数、失败位置、异步批量和结果未知；实现前不发布空壳 batch。
- [ ] 数据源 batch 沿用其冻结配置、日志、独立可选池和事务规则，公开连接串仍脱敏；旧目标不出现现代 API。
- [ ] 在 .NET 8/9/10 分别运行消费与集成测试。

### C07 诊断与性能基线

**产物：**`ActivitySource` 本地操作追踪、`Meter` 聚合指标及禁用开销/性能基线；基础 `ILogger` 接入已由 B01 交付，命令/事务和池日志随对应任务实现。五目标 API/包依赖验证；本地追踪不依赖 OB20，不预先堆叠其他诊断桥接。默认无 SQL、绑定值或凭据；指标不用 SQL/连接 ID/任意 key 作高基数标签。抓包单独显式开启并擦除认证/参数数据。

- [ ] 测试五目标事件名称/字段一致、敏感数据不出现、取消与结果未知被区分。
- [ ] 以预处理、多结果、大值和高并发池借还建立性能/内存基线；显著退化写入缺陷台账。
- [ ] 在两模式直连与 OBProxy 采集脱敏样本。

### C08 G3 生产门槛

**产物：**版本/模式/拓扑/TLS/认证/池化的公开兼容矩阵，重置隔离、故障和精确值的实测证据。不能完整重置的组合只发布直连能力。

- [ ] 编译全部五目标，运行 .NET 10 单测、隔离消费、双模式集成与故障套件，记录跳过、失败和环境阻塞。
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
| M15 诊断与 OB20 | B01、B02、B07、C01、C07、D01 |
| M16 构建与生态 | A01、B09、R01-R02 |

## 发布与交接

### R01 包与公开契约审查

- [ ] 固定核心/集成包 NuGet ID、许可证声明和第三方归属；两个包从根 Version 取同一版本，统一打包和发布，检查 nuspec 中集成包的核心依赖为根 Version 派生的精确区间；核心五资产、集成包三现代资产及必要文档齐全。
- [ ] 从空项目分别消费 `netstandard2.0`、`netstandard2.1`、.NET 8/9/10 资产，验证 provider factory、现代 API 可见性、公开类型身份和脱敏。
- [ ] 生成包并检查内容，公开不支持能力与兼容矩阵；不得把阶段 D 计划写成已支持。

### R02 候选版完整验证

- [ ] 再运行 G1-G3 全部必需套件与交付矩阵，留存命令、版本、日志链接及跳过原因。
- [ ] 对每个模式/版本/OBProxy/TLS/认证组合逐项标注“已验证/未验证/不支持”；无真实测试时不得标“已验证”。
- [ ] 完成发布说明、故障与结果未知指南、已知差异、回滚方案；通过最终代码和文档走查后才发布。

## 执行与进度规则

每项任务只有在代码、针对性测试、所属目标编译及进度证据都齐全时标为“完成”。G1/G2/G3/R02 还要求对应真实环境记录；跳过测试不等于通过。每次合并更新[进度台账](obconnector-dotnet-progress.md)的状态、证据、阻塞和下一任务。发现设计假设与真实服务端冲突时，先修订架构设计和本计划，再继续依赖任务。

统一验证入口（工程创建后执行；实际命令和退出码写入进度台账）：

```powershell
dotnet restore OceanBaseConnector.slnx
dotnet build OceanBaseConnector.slnx -c Release --no-restore
dotnet test tests/OceanBaseConnector.UnitTests/OceanBaseConnector.UnitTests.csproj -c Release --no-restore
dotnet test tests/OceanBaseConnector.ConsumerTests/OceanBaseConnector.ConsumerTests.csproj -c Release --no-restore
dotnet test tests/OceanBaseConnector.IntegrationTests/OceanBaseConnector.IntegrationTests.csproj -c Release --no-restore
dotnet pack src/OceanBaseConnector/OceanBaseConnector.csproj -c Release --no-build
```

日常测试和消费工程统一运行于 .NET 10，Windows/Linux CI 构建产品五目标；基线样例用 SetTargetFramework 和 SkipGetTargetFrameworkProperties 显式选择 netstandard2.0，隔离加载测试覆盖 netstandard2.0/netstandard2.1，不把 Standard 资产当可执行测试目标。旧运行时证据在 B09/R01 单独取得。集成测试使用 `OB_MYSQL_CONNECTION_STRING`、`OB_ORACLE_CONNECTION_STRING`、`OB_MYSQL_PROXY_CONNECTION_STRING`、`OB_ORACLE_PROXY_CONNECTION_STRING`；未知认证插件用例另需 `OB_UNSUPPORTED_AUTH_CONNECTION_STRING`（默认 VerifyFull，仅使用测试账户）；关口验收设置 `OB_REQUIRE_INTEGRATION=1`，缺少必需环境时测试失败，日常开发可明确跳过并报告。证书、凭据和原始抓包不写入仓库。
