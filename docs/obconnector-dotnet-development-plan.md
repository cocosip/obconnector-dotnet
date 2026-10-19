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

对外扩展类型先固定 `OceanBaseDbType`、`OceanBaseDecimal`、`OceanBaseTimestamp`、`OceanBaseTimestampWithTimeZone`、`OceanBaseTimestampLocalTimeZone`、`OceanBaseDate`、`OceanBaseDateTime` 的 `netstandard2.0` 类型身份；`OceanBaseDecimal` 的低精度列访问及范围内写入在 B05/B06 落地，宽精度编解码及其余精确时间类型在 C03 落地。不得让 `GetFieldType` 随行值变化。`OceanBaseException` 含服务端错误码、SQLSTATE 和 `IsExecutionOutcomeUnknown`，现代目标还通过继承的 `BatchCommand` 关联批次条目；异常不提供模式或端点属性，安全端点和模式通过日志/诊断关联，不含默认 SQL/参数/凭据。

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
- [x] Windows 本地环回验证静默 TLS 握手中的同步/异步总超时、关闭及异步调用方取消；确认已进入 TLS 后再中断，服务端在显式清理前确认 EOF/断开，会话不可复活。
- [ ] 以可验证私有 CA、错误主机名、IP 连接加显式 DNS 名称分别运行跨平台 TLS 测试。修改系统信任库会触发 Windows 人工确认，本轮按用户反馈暂缓；不在默认单元测试中安装/删除信任根。
- [x] 本地失败测试断言连接拒绝、预取消、TLS 错误的异常和资源释放；无 TLS 能力或升级失败时拒绝连接，不明文降级。

A04 本地实现及 Windows 环回已完成，整体仍为待验证：可信私有 CA 成功链、真实错误主机名、实库 TLS 与非 Windows 证据未取得。2026-10-19 增补 `SilentTlsHandshakeIsInterruptedAndClosesTransport` 五项不操作证书库的回归，覆盖 TLS ClientHello 后的超时、取消、关闭与对端释放；产品代码无需修改。私有 CA 尝试因系统确认打断已撤销，残留专用证书已清除，不计为完成证据。依用户要求暂不执行缺环境或需人工确认的部分，不把这些验收项混写为代码未实现。

### A05 认证、模式与能力

**产物：**初始握手/认证切换/OK 解析，按版本矩阵选择已验证插件；发送 `CLIENT_SUPPORT_ORACLE`、`CLIENT_SESSION_TRACK`、`CLIENT_FOUND_ROWS`、多结果等必要位。保存请求、服务器能力与实际生效能力；解析 `OB_SERVER_USE_ORACLE_MODE` 和 `ob_capability_flag`。

- [x] 合成夹具/环回覆盖 Oracle 位设置/未设置、无法可信识别模式、未知插件、能力缺失和认证错误；模式未知时不交付可执行连接。
- [x] 仅 VerifyFull 成功升级后允许 cleartext 类插件；认证切换复用同一安全检查；无验证 TLS 的 clear password 路径已有拒绝测试。
- [x] 本地握手/认证测试通过：native、认证切换、模式/能力快照、错误码和脱敏、同步/异步路径均有证据。
- [ ] 取得可信版本及插件矩阵后实现允许的 MySQL 模式判定规则；当前缺 Oracle 位安全拒绝，不能称已完成 MySQL 模式认证支持。
- [ ] 两模式真实握手和 OBProxy 验收，记录服务端/代理版本、插件与能力位；与非 Windows 测试均按用户要求暂缓。

当前实现边界（2026-10-18）：native challenge 和仅 VerifyFull 下的 clear password 编码尚无实库验收。没有已验证版本矩阵时，缺少 Oracle 状态位的连接明确失败，不能假设 MySQL；补齐 G0 后须以真实证据建立版本白名单再运行 A07。合成夹具中的 4.2.1 不是兼容承诺。

### A06 物理会话、错误与生命周期

**产物：**`PhysicalSession` 的 `Connecting/Open/Busy/Broken/Closed` 状态与独占租约；关闭/错误释放 socket、statement ID、结果状态。`OceanBaseException` 保留 SQLSTATE、服务端码和结果未知标记；安全端点和模式通过日志/诊断关联。

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
- [x] reader 的 `CommandBehavior.CloseConnection` 已有 B06/后续隔离消费的本地契约回归；真实双模式行为仍随 B06 实库验收。

### B02 文本命令、OK/ERR 与多结果

**产物：**`COM_QUERY`、文本行、OK/ERR、多结果状态；`ExecuteNonQuery`、`ExecuteScalar`、`ExecuteReader` 同步与异步路径共享状态机。

- [x] 夹具/环回覆盖 `SELECT`、DML、空结果、多个结果集、结果集后更新计数、服务器 ERR、有未消费响应的关闭，以及传统 EOF/协商后的 FE OK 终止。
- [x] 保留默认 `CLIENT_FOUND_ROWS` 能力；`RecordsAffected` 仅累计服务端 OK 的计数，不按客户端 SQL 猜测，也不把 SELECT 行数算入更新计数。真实不变 UPDATE 的匹配行语义仍待实库证明。
- [x] Windows 环回验证异步建命令/逐行读取等待网络，文本路径不缓存整个结果；同步调用同步 I/O。预取消/不支持参数/行为/环境事务在发包前失败；发包后取消、超时或关闭销毁会话并报告结果未知。B08 继续覆盖更完整的故障矩阵。
- [x] 使用 B01 日志上下文记录命令完成/错误/取消/结果未知，默认不记录 SQL 或绑定值；已收到可信 OK/ERR 后，日志回调触发取消也不覆盖终态。
- [ ] 两模式分别运行真实文本 CRUD 和不变 UPDATE 匹配行验证；依用户要求实库及非 Windows 测试暂不执行，MySQL 模式可信版本门槛保持不变。

B02 交付的 reader 支持基础有/无符号整数、浮点、UTF-8 字符串、binary 字符串和 NULL；保留物理会话模式，Oracle 类型码 3（NUMBER）当时明确拒绝，不借用 MySQL INT 映射。B05 已补精确数值、日期及低精度 provider-specific 访问，完整字段元数据仍属 B06；national charset、LOB 和其他扩展值不猜测解码。B02 交付时仅接受 `CommandBehavior.Default`/`CloseConnection`；C04 已新增本地 `SequentialAccess` 字段流，其他未实现行为仍在发包前拒绝。响应硬上限为每包 64 MiB、每结果 4096 列/4 MiB 元数据、每命令一百万包；超限销毁会话。超出平台计时器范围的超时在取得会话前失败，不遗留 Busy 会话。

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
- [x] MySQL DATE/DATETIME/TIMESTAMP同列混合正常值、零分量与NULL，文本/预处理路径的稳定标准/provider-specific类型、精确参数写回及五资产消费；仅内部模式夹具，不等同公开MySQL握手或实库支持。
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

2026-10-19 C03零日期实施计划（沿用已确认的§5契约）：

- [x] 先以文本/二进制正常、全零、部分零及NULL回归复现provider类型缺口；标准DateTime不随行改变，零分量转换抛InvalidCastException，IsDBNull仅识别协议NULL。
- [x] 新增五目标共享OceanBaseDate(year,month,day)/OceanBaseDate(DateTime)及OceanBaseDateTime(date,hour,minute,second,microsecond,scale)/OceanBaseDateTime(DateTime)，无时区。Date组件年0..9999、月0..12、日0..31，非零月/日需合法日历（year0按400年检查闰日）；零分量保留，不支持ALLOW_INVALID_DATES的其他非法日历。DateTime微秒0..999999、scale0..6且禁止舍入，default表示全零。提供Date分量、IsValidDate/IsValidDateTime、ToDateTime与固定格式ToString，不新增零值转NULL/最小日期配置。
- [x] TemporalCodec新增MySQL DATE10/DATETIME12/TIMESTAMP7原始分量读写；文本严格格式、二进制0/4/7/11长度及分量/精度边界，DATE仅0/4，不归一化非法数据。ResultColumn整列provider类型分别Date/DateTime，标准值仍精确转换DateTime；reader的GetProviderSpecificValue(s)/GetFieldValue<T>/两套schema贯通，Oracle路径不变。
- [x] PreparedParameters和Parameter自动推断Date/DateTime，provider值仅在MySQL对应类型写入，显式不匹配/Oracle及精度损失在发包前拒绝；零值写0长度，部分零按分量发送；不新增枚举或依赖。既有Timestamp参数映射保持MySQL DATETIME12，不承诺恢复时区瞬时点。
- [x] codec/参数红绿、同步/异步reader和预处理混合行、畸形输入/精度/模式边界、五资产类型/消费验证；MySQL模式仅测试内部夹具，A05公开握手继续拒绝无可信模式位。Release slnx编译、Windows完整测试、格式/打包版本矩阵、独立走查并修复后更新三份文档提交；实库/非Windows继续暂缓，C03整体记待验证而非完成。

2026-10-19 零日期本地验证：首批9项失败回归修复后通过；最终49项定向通过，覆盖严格文本/二进制、含零分量、带非零时钟的零日期、微秒/scale、TIMESTAMP混合reader、NULL及模式/精度拒绝。完整Windows Unit895/Consumer91/DI8全部通过，核心五/DI三目标编译0警告0错误、格式/diff/双包及版本矩阵通过。C03全部本地子项已实现，状态改为待验证，保留实库/非Windows及A05公开MySQL握手缺口；下一可本地推进模块C04，C01完整重置仍阻塞。

### C04 大值、SequentialAccess 与服务端游标

**产物：**`SequentialAccess` 不隐式缓存整值；游标以物理会话和 statement ID 为作用域，关闭/取消/断线时释放或销毁。

本地实施顺序：先补 PacketChannel 的单所有者增量 payload 流，再接 QueryResult/reader 的有序字段读取和字符解码，最后实现有明确启用条件的 prepare/execute/fetch 游标。增量报文流只读取调用方请求的字节，跨帧继续验证序号及累计 64 MiB 上限；未消费完成时禁止新读包、发包、重置序号或替换传输。提前释放、损坏或中途取消关闭传输，完整消费后才允许下一报文，保持未知执行结果不重放。目前增量报文、QueryResult/reader 顺序字段流及游标分页均已完成本地实现，C04 转为待验证；真实验收仍开放。

- [x] 增量报文流：同步/异步、续帧及空终结帧、序号/累计限额、截断/取消/提前关闭；用按需生成的大报文断言分块读取和固定内存，而非预先分配完整测试载荷。
- [x] QueryResult/reader 本地接入：文本/预处理逐字段读取、GetBytes/GetChars/GetStream/GetTextReader、字段与偏移前进限制、NULL、多结果和关闭/取消资源归属。
- [x] 本地 reader 大值分块读取、乱序访问限制、提前关闭、取消和有界分配回归。
- [x] 服务端游标本地分页、提前关闭与取消生命周期测试。
- [x] 本地按已认证模式和协商 EOF 能力选择游标协议；无确认/不支持的响应失败并销毁会话，不回退重执行。
- [ ] 真实模式/版本/能力矩阵验收后才声明相应组合支持；本地夹具不作为版本白名单。
- [ ] 在两模式真实租户上测量内存峰值、吞吐和资源释放。

2026-10-19 增量报文底层：新增内部 OpenPayload/OpenPayloadAsync，既有整包读取共用相同帧校验和失败关闭规则。29项新测试覆盖按需读取、32 MiB生成载荷/同步循环分配少于256 KiB、续帧和精确满帧终结、混合整包/流式序号回绕、部分I/O取消、累计限额、非法参数和提前释放；与既有报文测试共54项通过。先验证21项缺失API失败，再实现通过；整包超限未销毁传输的2项回归亦先红后绿。Release核心五/DI三目标0警告0错误，完整Windows Unit924/Consumer91/DI8通过，独立只读走查无必须修复项。该内存断言只覆盖内部同步流读取的托管分配，不是实库reader内存峰值或吞吐基线。C04进行中，当次仅交付报文底层，reader字段流见下方续接；实库/非Windows/新的旧运行时验收未执行。

2026-10-19 reader 续接：文本/预处理二进制、旧 EOF/弃用 EOF 已接入同步/异步字段流；只预读行前缀和 NULL 位图。GetBytes/GetChars/GetStream/GetTextReader 及异步字段 getter 遵守 ordinal/offset 前进、NULL、显式整字段物化和流失效规则。GetChars 空 buffer 长度探测在顺序模式拒绝；字符读取严格 UTF-8、保留 BOM 和代理项边界；默认模式不变。跳列/行/结果用共享 8 KiB 缓冲区排空，提前关闭销毁未消费会话。详见现有架构文档的顺序读取契约。

走查发现的缓存字符/耗尽流绕过取消与连接关闭、完整终结包被后到取消误判、字段自身 token 未进入命令预算等问题均增加先失败回归并修复；非法二进制字段 framing 亦转为未知结果并废弃会话。五资产隔离消费由 net10 宿主运行，不等于旧运行时验收。32 MiB 生成行用例验证 ExecuteReader 在正文到达前返回、跨续帧流读取且同步 drain 托管分配少于 1 MiB；这不是实库内存峰值或吞吐基线。Release slnx核心五/DI三目标编译0警告0错误，完整Windows Unit978/Consumer101/DI8通过（TestResults/c04-reader-*-final.trx）；54项顺序读取/零日期本地回归及五资产顺序流/纳秒provider消费覆盖当前契约。当次 C04 仍进行中，剩余服务端游标、真实两模式大值/吞吐和资源验收；不放宽 A05 的公共 MySQL 握手或 C01 完整重置门槛。

C04 游标本地实施（2026-10-19，沿用既有 TDD/独立走查，合并进现有文档）：

- [x] 入口：OceanBaseCommand.FetchSize 默认0不请求游标，正数强制服务端prepare并快照为本次分页行数；负数拒绝。PreparedStatement保留列计数，非结果语句发execute前拒绝；PreparedParameters.Execute只读cursor flag=1。Oracle + ClientDeprecateEof按Java协议证据省略execute/fetch元数据EOF，由首次fetch确认终态；其他组合严格验证EOF/FE-OK游标确认包，不放宽A05。
- [x] 读取：QueryResult按模式/能力处理execute元数据（Oracle弃用EOF例外见上），其余确认CursorExists后开始fetch；9字节COM_STMT_FETCH、statement ID/行数LE32，每次请求重置序号但保持同一命令租约/总超时，不重新执行SQL。MySQL直接读二进制行，Oracle先验证重发列元数据一致；继续批次必须CursorExists且有进展，LastRowSent才结束租约。多结果游标暂拒绝并销毁会话，不伪造支持。
- [x] 生命周期：默认与SequentialAccess共用分页控制，HasRows只预取首行，NextResult读尽剩余页；提前Close/取消/超时/断线销毁原会话，缓存statement不跨会话复用。测试翻页、空最终页、语句复用、两模式差别、非法flag/元数据/超额行、异步纯I/O和未知结果不重放。
- [x] 验证提交：核心5/DI3编译、Windows Unit/Consumer/DI、格式/双包/版本矩阵，独立走查并修复；更新本页、架构及进度后提交当前master。真实两模式/OBProxy与非Windows仍暂缓，不据本地回归关闭互通验收。

本轮走查的租约释放早于游标ERR销毁、二进制行冒充FE-OK确认、中间页遗漏会话报告、初始空结果重复清除报告等缺陷均以先失败回归修复。同步/异步、默认/顺序读取、两模式/两类EOF共16种分页组合及Oracle弃用EOF空结果首次fetch已覆盖；五资产隔离消费覆盖Oracle旧/弃用EOF。确认包形态和省略规则见架构§3，实库游标资源/内存/吞吐仍待验证，C04本地代码完成不关闭G3。最终Release核心五/DI三目标0警告0错误，Windows Unit1037/Consumer111/DI8全部通过（TestResults/c04-cursor-*-final-checked.trx）；格式、双包、统一版本及按TFM的Microsoft.Extensions依赖矩阵通过，独立复查无剩余必须修复项。

### C05 数据库目录元数据

**产物：**`DbConnection.GetSchema` 的首批集合、限制参数与现代 `GetSchemaAsync`；C 阶段实现 MySQL 模式 `ChangeDatabase` 和真实异步的 `ChangeDatabaseAsync`，Oracle 模式在发包前明确报不支持。MySQL 与 Oracle 模式的目录元数据分别使用对应系统视图，不复用错误方言。

- [x] 固定首批集合清单及列形状：`MetaDataCollections`、`DataSourceInformation`、`Tables`、`Columns`；其他集合报不支持。
- [x] 双模式测试名称大小写、schema、限制参数、权限不足和异步入口不阻塞网络等待；验证 MySQL 切换后的 `Database`/查询目标、失败时状态不变，Oracle 模式拒绝切换且不发包。
- [x] 记录与 Java 元数据范围的差距，作为 D 阶段候选。

C05 本地执行顺序（2026-10-19，沿用现有文档与原生实现/独立走查）：

- [x] 数据库切换模块：新增连接 Database partial，MySQL 使用 COM_INIT_DB 原样发送严格 UTF-8 名称（不拼接 USE SQL）；拒绝 null/空/NUL/非法 UTF-16/超包，Oracle 在发包前拒绝。共享 CommandExchange 的独占租约/Command Timeout/未知结果不重放；同步使用同步 I/O，Standard2.1和现代目标覆写真正异步入口。
- [x] 状态归属：物理会话持有当前 Database，只有合法终态 OK 且租约仍有效时更新；失败不更新，配置/数据源保持原始数据库，关闭重开恢复配置。旧响应不能改变重开的新会话；当前 Database 在完成回调前发布。成功切换后，同 SQL 的预处理语句必须按准备时数据库重新判定复用，不能继续查询旧库。允许空闲的显式本地事务内切换但不改变事务绑定/提交状态；忙 reader/命令及环境事务在发包前拒绝。
- [x] 测试数据库模块：同步/异步、原样特殊字符/大小写名称、成功/拒绝/异常报文、部分 I/O、取消/超时/关闭重开、日志重入、预处理复用、事务与数据源隔离；五资产消费仍用内部认证后 MySQL 事实夹具，绝不放宽 A05 公共握手。
- [x] 目录模块：固定四集合列形状与每集合限制顺序；对照 Java 的两模式目录 SQL，使用协议绑定值，不将限制参数拼成 SQL；同步/异步共享结果装载，保留权限错误和取消语义。
- [x] 数据库切换模块：核心5/DI3编译、Windows Unit1073/Consumer120/DI8、格式/双包/版本矩阵通过，独立走查无必须修复项；更新三份文档并在当前 master 提交。新增36项单测及9项五资产消费，TestResults/c05-database-*-final.trx。
- [x] 目录模块实现后独立执行相同编译/测试/走查/提交关口；真实目录权限、事务/切换互通、跨平台与旧运行时仍待验收。

2026-10-19 数据库切换本地验证：首批5项失败回归修复后通过；36项定向覆盖精确名称传输、模式/名称/环境事务拒绝、已知ERR保留原状态、非法OK/响应、部分发送和取消/超时、reader独占、本地事务绑定、回调发布、数据源隔离及关闭重开。预处理比较在获取租约之后执行，旧ID由既有队列清理；失败切换保留旧ID。Standard2.0同步与其余四资产同步/异步隔离消费9项通过，宿主仍net10。Release slnx核心五/DI三目标0警告0错误，完整Windows Unit1073/Consumer120/DI8全部通过；独立走查无必须修复项。当时C05保持进行中，目录子模块的后续实现见下；不据协议夹具宣称真实切换、事务/目录权限或A05公共MySQL握手已验证。

C05 目录集合实施细化（2026-10-19；实现选择，复用既有命令/reader）：

- [x] 新建 `OceanBaseConnection.Schema.cs`：五目标覆写三个同步 GetSchema；现代三目标覆写三个 GetSchemaAsync，静态集合直接返回任务，目录读取使用 ExecuteReaderAsync/ReadAsync/NextResultAsync。所有集合要求打开且空闲的连接、拒绝环境事务；限制数组先快照，绑定当前显式事务并固定物理会话，防止关闭重开后换模式执行旧查询。
- [x] 固定集合：MetaDataCollections 三列（CollectionName:string、NumberOfRestrictions:int、NumberOfIdentifierParts:int），四行限制数为0/0/4/4，对象标识部分数为0/0/3/4。Tables 四列 TABLE_CATALOG/TABLE_SCHEMA/TABLE_NAME/TABLE_TYPE 均为string；限制顺序同四列，类型归一 TABLE/VIEW。Columns 十二列为 TABLE_CATALOG/TABLE_SCHEMA/TABLE_NAME/COLUMN_NAME:string、ORDINAL_POSITION:int、COLUMN_DEFAULT/IS_NULLABLE/DATA_TYPE:string、CHARACTER_MAXIMUM_LENGTH:long、NUMERIC_PRECISION/NUMERIC_SCALE/DATETIME_PRECISION:int；限制顺序为前四列。
- [x] DataSourceInformation 使用 ADO.NET 标准17列（枚举列使用对应枚举类型）；产品名OceanBase，版本保留认证原文，参数语法和引号按模式；没有可靠证据的规范版本/正则/长度/连接运算符等保持DBNull，不从MySQL兼容前缀猜OceanBase版本或大小写配置。
- [x] null限制表示不筛选，空字符串和%/_为原样精确值；不去引号、不做大小写折叠，匹配由服务端比较规则决定。MySQL目录映射catalog、schema为DBNull；Oracle映射schema=OWNER、catalog为DBNull，限制不存在的标识部分返回空表。MySQL使用INFORMATION_SCHEMA.TABLES/COLUMNS，Oracle使用ALL_OBJECTS（仅TABLE/VIEW）/ALL_TAB_COLUMNS；按标识及列序号排序，NULL元数据保留DBNull，Oracle NUMBER精确转换为固定整数列。
- [x] 首批失败回归覆盖四集合、两模式同步/异步目录、限制安全绑定、NULL和数值；补齐事务、权限错误、超时/取消/部分发送/关闭、空结果/非法结果形状和真实异步I/O及五资产消费。复用原错误、独占和未知结果语义，不增加重放或方言回退。
- [x] 记录Java差距：首批不提供JDBC的模式/类型/键/索引/过程/同义词等完整集合，不复用Java模式匹配和字符串插值；本地环回不证明真实字典列/权限、标识大小写配置、默认值LONG及列序号版本语义。编译、完整测试、格式/包矩阵、独立走查、三文档同步后提交。

目录模块本地验证：首批13项失败转绿，最终44项定向/8项五资产消费通过；独立走查发现的数值先舍入问题用2项失败回归修复并复查通过。Release核心五/DI三目标0警告0错误，Windows完整Unit1117/Consumer128/DI8通过（TestResults/c05-schema-*-final.trx）；格式、UTF-8无BOM/CRLF、diff、核心5/DI3包资产及统一版本/按TFM的依赖矩阵通过。C05转待验证，真实目录/权限/版本互通仍留空，下一可本地推进C06。

### C06 逐条 DbBatch 与数据源批量入口

**产物：**`net8.0+` 的 `DbBatch`、factory 和 B01 数据源的批量入口；`DbBatch` 先逐条执行，保留每条 `RecordsAffected` 和失败前缀，不自动保证原子性。基础数据源不再延后到本任务。

本地实施步骤（沿用架构批量契约，本轮自行实施、独立只读走查）：

- [x] 先以公共 DbBatch 入口的失败用例固定顺序执行、空批次、逐条计数、失败命令及不执行后缀。
- [x] 新增 OceanBaseBatch/BatchCommand/BatchCommandCollection 和内部批量 reader；复用现有 OceanBaseCommand，开始时快照所有命令和参数（byte[]复制），保留各命令准备缓存。集合/命令只归属一个 batch，活动期间禁止修改集合及配置。
- [x] PhysicalSession 增加批量所有者预约；普通命令、目录、切库及事务操作不能插入批量语句之间，旧批次不能进入重开后的会话。失败或 reader 关闭释放预约；提前关闭不执行后缀，未读响应仍销毁原会话。
- [x] reader 按 NextResult 逐条启动，支持现有多结果、字段类型/schema、SequentialAccess 和真正异步；ExecuteScalar 取第一个有列结果的首行首列（无行则 null）后排空全部后缀。Timeout 对每条命令（含准备和读取）生效，0 无限；Cancel 覆盖当前与未启动后缀，不自动开启事务。
- [x] 数据源入口复用平台 DbDataSource 包装，自动打开/关闭/释放；其 Prepare、Connection/Transaction 按平台拒绝，命令通过 factory.CreateBatchCommand 创建。五资产隔离和现代三资产消费覆盖 API 边界。
- [x] 完整编译、Windows 单元/消费/DI、格式/包矩阵和独立走查修复后，更新三份文档并在当前分支提交；实库和非 Windows 暂缓。

- [x] 实现后测试 `CanCreateBatch=true`、batch 事务绑定、逐条计数、失败位置、异步批量和结果未知；实现前不发布空壳 batch。
- [x] 数据源 batch 沿用其冻结配置、日志、独立可选池和事务规则，公开连接串仍脱敏；旧目标不出现现代 API，池化仍按 C01 拒绝。
- [ ] 在 .NET 8/9/10 分别运行消费与集成测试。

2026-10-19 本地实现、Windows 完整测试和独立走查已完成，C06 待验证。另以独立编译样例在实际 .NET 8.0.31/9.0.20/10.0.12 验证批量消费及对应 Logging 8/9/10；完整集成和非 Windows 暂缓，最后一项不据此勾选。证据及走查修复见进度台账末尾 C06 记录。

### C07 诊断与性能基线

**产物：**`ActivitySource` 本地操作追踪、`Meter` 聚合指标及禁用开销/性能基线；基础 `ILogger` 接入已由 B01 交付，命令/事务和池日志随对应任务实现。五目标 API/包依赖验证；本地追踪不依赖 OB20，不预先堆叠其他诊断桥接。默认无 SQL、绑定值或凭据；指标不用 SQL/连接 ID/任意 key 作高基数标签。抓包单独显式开启并擦除认证/参数数据。

本地实施步骤（已有设计下自行实施、独立只读走查）：

- [x] 在现有公共连接/命令/reader及事务夹具先增加诊断失败回归：源名、父上下文/Current恢复、终态前不结束、已知ERR/取消/超时/未知结果、监听器异常及重入；使用固定操作/模式/结果标签验证无敏感数据。
- [x] 新增 Internal/DriverDiagnostics，集中 ActivitySource/Meter 及按操作启用的诊断对象；CommandExchange结束时在释放租约和batch失败清理后上报，OceanBaseCommand在Start返回后区分prepare/execute，ChangeDatabase和连接/事务复用现有生命周期，不在内部锁内调用监听器。
- [x] 两个Standard资产通过CPM引用DiagnosticSource8.0.1，现代目标使用运行时API；五资产隔离监听器消费验证同一事件/标签和程序集引用版本，普通测试宿主保持net10。
- [x] Windows可重复本地基线覆盖无监听/采样拒绝/启用、预处理缓存、多结果、顺序大值；记录实际耗时/分配及环境，显著退化记入缺陷台账。池借还因C01阻塞不伪造测量。
- [x] 完整slnx编译、Windows单元/消费/DI、包依赖/格式检查，独立走查修复后更新三份既有文档，在当前分支提交不推送。

- [x] 测试五目标事件名称/字段一致、敏感数据不出现、取消与结果未知被区分。
- [ ] 以预处理、多结果、大值和高并发池借还建立性能/内存基线；显著退化写入缺陷台账。
- [ ] 在两模式直连与 OBProxy 采集脱敏样本。

2026-10-19 本地诊断、脱敏及Windows预处理/多结果/顺序大值基线已完成，C07待验证。Unit1205、Consumer152、DI8、实际.NET8/9/10样例及包矩阵通过；Activity.CurrentChanged走查缺陷已以同步/异步回归修复。原性能验收项包含尚未开放的池借还，保持未勾选；真实两模式/OBProxy和非Windows仍暂缓，数据与复现命令见进度台账末尾。

### C08 G3 生产门槛

**产物：**版本/模式/拓扑/TLS/认证/池化的公开兼容矩阵，重置隔离、故障和精确值的实测证据。不能完整重置的组合只发布直连能力。

- [ ] 编译全部五目标，运行 .NET 10 单测、隔离消费、双模式集成与故障套件，记录跳过、失败和环境阻塞。
- [ ] 审核池化白名单、内存/连接泄漏、取消后的 SQL 结果不确定性与可观测性。
- [ ] 无 P0/P1 缺陷且公开声明与实测一致时关闭 G3。

## D. 独立扩展切片

每个 D 项独立开关、独立版本/模式矩阵和回退行为。按当前开发要求，可独立验证的本地实现继续推进，不因 G4 或实库环境尚缺而全部搁置；依赖部署的联调暂缓，不能以本地夹具替代真实验收。每项先写失败夹具，补公开 API 文档与资源释放测试，并单独记录真实版本/模式验证缺口。

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

### D14 本地实现范围（2026-10-19）

沿用现有 `OceanBaseDbType.Year`（枚举值 9），标准及 provider-specific 访问均为 `ushort` / `DbType.UInt16`；只支持四位 YEAR，保留零值及 1901–2155，拒绝 YEAR(2)、两位年份推断、日期转换和非法精度。结果类型码 13，文本四个 ASCII 数字，二进制固定两字节小端；NULL 的模式/元数据亦须先验证。显式 Year 参数先校验模式、精度和 UInt16 范围，复用现有无符号 SMALLINT 参数编码，不假设执行请求接受 YEAR 类型码；普通 UInt16 仍不受年份范围限制。

实施顺序：失败编解码/边界及公共环回测试 → 共用结果/参数/Schema 路径实现 → 五资产公共 API 消费 → Release 全目标、完整 Unit/Consumer/DI → 独立走查、修复、文档和本地提交。同步/异步、文本/预处理、默认/SequentialAccess、后续字段及会话复用均覆盖。协议行为参考固定 Java 基线的 `BinaryRowProtocol` YEAR 两字节读取及 `ShortParameter` SMALLINT 写入，不复制其源码。真实 OceanBase YEAR 列 INSERT/SELECT、版本/OBProxy 验收另行保留；A05 公共 MySQL 模式门槛不变，本地测试仅注入认证后的模式事实。

### D13 本地实现范围（2026-10-19）

沿用 `OceanBaseDbType.Bit`（枚举值 8），标准及 provider-specific 访问统一为 `ulong` / `DbType.UInt64`，BIT(1) 也不隐式转 bool。结果类型码 16，列 Length 为位数，只支持 1–64 位、scale=0；两个行协议都以长度编码包裹大端原始字节，要求字节数为 ceil(位数/8)，未使用高位必须为零。保留 2^63 和 UInt64.MaxValue，不按有符号整数解释或解码为文本。Schema 的 ColumnSize 为位数；NumericPrecision/NumericScale 不填十进制精度。

显式 Bit 参数只接受 UInt64，Size=0 表示 64 位，Size=1–64 声明位宽；验证范围且不截断，Precision/Scale 仅支持 0，Oracle 模式含 NULL 预先拒绝。复用现有 unsigned BIGINT 参数编码，不假定执行请求支持 BIT 类型码；服务端目标列位宽和转换仍须实库验证。普通 UInt64 参数不受此位宽规则影响。先写全位宽/符号边界失败测试，再实现共享编解码、元数据和参数映射，覆盖同步/异步、文本/预处理、默认/SequentialAccess、NULL、后续字段及会话复用、五资产公共 API 消费；全目标编译、完整测试与独立走查通过后本地提交。协议参考固定 Java 基线 RowProtocol.parseBit 的大端解释，不复制源码；真实 BIT 列 INSERT/SELECT 与模式/版本矩阵另行验收，A05 公共 MySQL 判定不变。

长度依据：[固定 OceanBase 源码的 bit_cell_str](https://github.com/oceanbase/oceanbase/blob/5e8a63d29dae9fde5e962b8fcc7b167d438fc343/deps/oblib/src/rpc/obmysql/ob_mysql_util.cpp) 在 TEXT/BINARY 两分支均发送 `(bit_len + 7) / 8` 字节；Java 固定基线 `RowProtocol.parseBit` 以大端解释。只作为协议行为依据，不将源码核对计作真实服务端验证。

### D12 本地实现范围（2026-10-19）

沿用 `OceanBaseDbType.Json`（枚举值 7），标准及 provider-specific 访问为 `string` / `DbType.String`。MySQL 结果类型码 245，文本与二进制行中均为长度编码的 UTF-8 JSON 文本，不是数据库内部二进制 JSON。接受 binary collation 63（JSON 线上文本的特殊情况）及已支持的 UTF-8 collation，拒绝其他字符集和 Oracle 模式，NULL 也先验证元数据；非数值类型的 scale 不按数值精度解释（含字符串常见标记 31）。JSON 字符串 `null`、空 JSON 字符串 `""` 与 SQL NULL 独立；返回文本不再序列化、不转 double/decimal，不丢失宽精度数字或空白。Schema 为 JSON/Json/string，IsLong=true；Length 不能可靠给出 JSON 文本的字符上限，ColumnSize 不填，NumericPrecision/Scale 不填。

显式 Json 参数使用现有 UTF-8 VAR_STRING 编码，mode=MySQL、Precision/Scale=0，接受 string/null/DBNull，不猜测对象或 JsonDocument 序列化；语法/目标列转换由服务端验证，不新增客户端 JSON 解析器。参数化保留现有长度限制和严格 UTF-8，异常在发包前抛出；普通 string 参数仍不自动变为 Json。默认与 SequentialAccess 结果复用现有 GetString/GetChars/GetTextReader 和异步字段读取，不复制流/取消/资源管理逻辑。

先写失败编解码/元数据/参数测试，再实现共用路径，补齐同步/异步、文本/预处理、默认/顺序读取、Unicode/宽精度文本、NULL/多结果/后续字段、字段流及生命周期、五资产公共 API 消费。全目标编译、完整 Unit/Consumer/DI 与独立走查通过后本地提交。协议依据为固定 Java 基线 `ColumnType.JSON` 及两种 RowProtocol 的字符串读取，以及[固定 OceanBase json_cell_str](https://github.com/oceanbase/oceanbase/blob/5e8a63d29dae9fde5e962b8fcc7b167d438fc343/deps/oblib/src/rpc/obmysql/ob_mysql_util.cpp) 将内部 JSON 打印为长度编码文本；只参考行为，不复制源码。真实 JSON 列 INSERT/SELECT、语法拒绝和目标版本/OBProxy 往返另行验收，A05 公共 MySQL 门槛不变。

### D10 本地实现范围及步骤（2026-10-19）

Oracle 207/208 对应 NVARCHAR2/NCHAR，标准及 provider-specific 值为 string，显式 provider 参数映射 DbType.String；普通 string 参数不隐式变为 national 参数。两种行协议共用长度编码字节，NCHAR 尾部空格及 Unicode 原样保留。按当前会话 NCHARACTER_SET_CONNECTION 解码/绑定，支持 UTF8/UTF8MB4 和无 BOM 的严格 UTF16BE；不按列的存储字符集猜测线上编码。national 为 NULL/binary 时，参数回退到报告的 CHARACTER_SET_CONNECTION，结果回退到 CHARACTER_SET_RESULTS；results 为 NULL/binary 时根据列的源 collation 解码，未知编码明确拒绝，不替换乱码。Size 按 CLR UTF-16 code unit 计数并拒绝超长，不截断；Precision/Scale 只允许0。NULL 结果保留字段类型身份且不需要解码；NULL 参数沿用既有类型6/NULL位图。Oracle 列长度已由服务端换算为字符数，Schema 直接保留。

会话事实沿用现有时区的租约、查询、发送前失效和每结果快照模式：只有三个 charset 变量均有会话报告时才能复用，否则在 Oracle 业务执行前用同租约/总预算查询 `SELECT @@NCHARACTER_SET_CONNECTION, @@CHARACTER_SET_RESULTS, @@CHARACTER_SET_CONNECTION FROM DUAL`。查询必须是三个字符串列、一行、单结果；不支持的合法编码只在实际 national 值访问/参数绑定时拒绝，避免阻断普通列。每次 SQL 发送和每结果起始使旧事实失效，无报告的后续结果不猜测编码。national 查询保留先前只读时区查询的结果；不增加连接选项、不修改用户会话、不缓存完整业务结果。参数先在取得租约前验证类型/Unicode/Size，再用已识别编码在 prepare 前编码并检查字节预算。

- [x] 先运行失败 metadata/参数回归，再实现 national 编码、会话查询/快照/失效、参数与顺序字符流。
- [x] 覆盖 UTF8/UTF16BE、fallback、NULL、坏 Unicode、发包前拒绝、两种结果/同步异步/顺序访问、会话变更及错误取消，补五资产公共 API 消费。
- [x] Release 全目标、完整 Unit/Consumer/DI、独立走查和 diff 检查通过后本地提交。真实 NCHAR/NVARCHAR2 列往返、目标版本/OBProxy 与跨平台仍单列延期。

协议参考固定 Java 基线 `AbstractConnectProtocol`、`OBNStringParameter` 和 `RowProtocol`；固定 OceanBase `5e8a63d29dae9fde5e962b8fcc7b167d438fc343` 的 `ObMPStmtExecute::parse_basic_param_value`、`ObQueryDriver::convert_string_value_charset` 与 `ObMySQLResultSet::to_mysql_field`。前两者分别按会话 national 编码处理输入/结果，后者将 national 列 length 按存储字符集最大宽度换算成字符数。仅参考行为，不复制第三方源码。

### D11 本地实现范围及步骤（2026-10-19）

新增不可变 `OceanBaseIntervalYearToMonth` / `OceanBaseIntervalDayToSecond`，标准/provider-specific 均返回精确值，DbType.Object，沿用枚举值 5/6。分量为非负 magnitude 加独立符号，前导精度 0–9（0 仅允许前导分量为零）、年月/天不超过 999999999、月 0–11、时/分/秒 0–23/59/59、纳秒 0–999999999，DS scale 0–9，不允许舍入。默认结构值为合法零；ToString 使用不受当前区域影响的明确符号文本，保留声明精度；DS 的 ToTimeSpan 仅在 ticks 精度和范围内精确转换。

Oracle 类型码 204/205，两种行协议均为长度编码的 7/14 字节小端专用数据：符号、年/月/前导精度，或符号、天/时/分/秒/纳秒/前导精度/scale。字段精度须与列 scale 一致：YM scale=前导精度，DS scale=前导精度*10+小数精度。非 Oracle（包括 NULL）、畸形长度/分量/精度/符号明确拒绝，不猜测普通字符串或 TimeSpan。Schema 返回精确 CLR 类型和 provider 身份；不把区间精度当作 NumericPrecision/Scale。

显式参数或 DbType.Object 下的精确值可绑定，NULL 保留既有 null bitmap 规则；Precision=0 表示采用值的前导精度，非零为 1–9 的声明，Scale 未设置采用值的 scale，显式 0 不能截断纳秒。参数改变精度仅在值能精确表示时允许。复用命令预先编码、默认/顺序读取及多结果生命周期，不新增依赖。固定参考为 Java 基线的 INTERVAL 参数和两种 RowProtocol，以及 OceanBase `5e8a63d29dae9fde5e962b8fcc7b167d438fc343` 的 `ob_mysql_util.cpp`、`ob_time_convert.cpp`、`ob_timezone_info.h`；只参考协议行为，不复制代码。

- [x] 先运行已有入口上的失败测试，覆盖 7/14 字节、类型身份、NULL、两模式及精度边界。
- [x] 实现两种精确值、共用 codec、参数和 ResultColumn/Schema 接入，验证正负最大值、所有精度、范围与纳秒拒绝。
- [x] 补齐同步/异步×文本/预处理×默认/SequentialAccess、参数复用/发包前拒绝、NULL/后续列/多结果及五资产公共 API 消费。
- [x] Release 全目标编译和完整 Unit/Consumer/DI，通过独立走查及 diff 检查后本地提交。真实 INTERVAL 列往返、目标版本/OBProxy 验收单独延期。

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
- [x] 从空项目分别消费 `netstandard2.0`、`netstandard2.1`、.NET 8/9/10 资产，验证 provider factory、现代 API 可见性、公开类型身份和脱敏。
- [x] 生成包并检查内容，公开不支持能力与兼容矩阵；不得把阶段 D 计划写成已支持。

本地实施顺序（2026-10-19，沿用现有设计，不发布）：

1. 增加 `eng/Verify-Packages.ps1` 和独立包消费样例，先对现有实际 nupkg 验证 README/归属文档缺失的失败；验证器必须检查 nuspec、五/三资产、精确核心依赖和按 TFM 的 Microsoft 版本。
2. 在 `src/Directory.Build.props` 统一产品包说明、源码仓库和包内文档，两个产品显式固定现有 NuGet ID；根 Version/CPM 不变。添加根 README 和第三方依赖/参考来源说明；项目许可证由维护者决定，未确定前不虚构 SPDX 声明或关闭许可关口。
3. 用一次 slnx pack 生成两个包。验证器创建独立目录、NuGet 缓存和本地包源映射，只通过 PackageReference 消费本次生成的包，核对还原资产与包 SHA512。临时消费样例编译两个 Standard 和三个现代目标；Standard 在隔离的 .NET 10 宿主验证，现代样例分别在实际 .NET 8/9/10 上验证 factory、公开类型、现代入口、DI 和凭据隐藏。日常测试工程仍统一 net10。
4. 验证缺失包文档及错误 DI 核心版本区间会被拒绝；在 Windows CI 接入统一 pack 和该验证器。编译、完整本地测试、只读代码走查并修复问题后，更新现有三份文档和提交；C08/R02 真实环境关口保持开放。

2026-10-19 上述本地步骤已实施并验证：旧包缺 README 先失败，新包通过五资产实际还原/消费；损坏包文档及放宽 DI 核心依赖均被拒绝。完整 Windows Unit1205、Consumer152、DI8、五/三目标编译及独立只读走查通过；走查的 PowerShell 最低版本问题已修复为 7.1+。第一项包含尚未决定的项目许可证、来源和包名/商标审查，保持未勾选；R01 进行中，不发布，也不关闭 C08/R02。

### R02 候选版完整验证

- [ ] 再运行 G1-G3 全部必需套件与交付矩阵，留存命令、版本、日志链接及跳过原因。
- [ ] 对每个模式/版本/OBProxy/TLS/认证组合逐项标注“已验证/未验证/不支持”；无真实测试时不得标“已验证”。
- [ ] 完成发布说明、故障与结果未知指南、已知差异、回滚方案；通过最终代码和文档走查后才发布。

本地交付材料准备（2026-10-19，不代替候选版验收）：

1. 从当前命令/reader/事务/多地址/认证代码和已有回归核对错误结果、提前关闭、已知提交与清理失败语义；将故障处置、版本升级/回退、日志诊断及认证/TLS限制合并进包内根 README，不另建设计文档。
2. 在现有兼容矩阵区分未验证与明确不支持，不虚构 OceanBase/OBProxy 最低版本或已验证升级来源；保留公共 MySQL 判定、池化及真实矩阵缺口。
3. 编译并运行 Windows 本地测试，用统一 pack 与实际五资产包消费验证重新检查随包交付的 README；独立只读走查指南与代码/测试的一致性，修复后更新三份现有文档并提交。G1–G3 与 R01 许可关口未通过前，不勾选 R02 最终发布项、不创建发布或推送。

2026-10-19 本地材料已补齐并通过独立走查：修正字段流释放/reader完成和可信COMMIT ERR的会话存活边界，同时按源码更正异常公开契约。验证发现的同步TCP共享句柄释放缺陷已在d8d7072修复，压力回归的端口清理及主异常保留问题亦已闭环；最终五/三目标编译0警告0错误、Windows Unit1206/Consumer152/DI8、实际五资产包消费全部通过，失败与最终报告均记录在台账。R02真实矩阵及最终发布验收仍未勾选，许可与生产关口未关闭，不发布或推送。

R02 Windows 历史现代运行时本地回归补验（2026-10-19，沿用五目标产品契约）：

1. 日常测试项目及 slnx 继续声明 net10.0；本次发布验收临时同时传入 `-p:TargetFrameworks=net8.0 -p:TargetFramework=net8.0`（net9同理），用实际8/9运行时构建和执行现有完整 Unit/DI 套件，不把net10程序集回退运行冒充旧运行时验证。还原/输出产生的临时资产不提交，最后还原并编译默认五/三目标和net10测试宿主。
2. 只调整测试宿主兼容性：证书夹具在net8使用该框架的PFX导入API，net9及以后使用X509CertificateLoader；异步Prepare隔离回归加载Standard2.1和当前实际运行时对应的现代资产，避免旧宿主误加载net10产品。先记录旧逻辑编译失败及错误资产加载失败，再验证修正后的真实异步行为。完整net8回归发现MeterListener在8.0.31缺少MeasurementsCompleted时Dispose保留订阅，测试监听器补齐空完成回调并保留全部诊断/分配断言，README说明应用监听器所有权与清理要求。
3. 每个运行时完整执行本地协议、取消/超时、类型、事务、游标、批次、诊断和DI测试；再执行默认net10验证，逐项记录实际运行时、编译警告、通过/失败/跳过。Consumer的五资产隔离宿主仍为net10，已有8/9包样例和局部环回证据单独保留，不将这些本地测试称为完整实库或旧运行时支持矩阵。
4. 独立走查测试边界及临时构建的依赖主版本，更新现有三份文档后提交；真实数据库、非Windows、.NET Framework4.8、许可及G0/G1/G3关口继续开放，D项依既定前置条件保持未开始。

2026-10-19 补验结果：Windows 实际 .NET 8.0.31、9.0.20 各 Unit1206/DI8 全通过，恢复默认 slnx 后 .NET10.0.12 Unit1206/Consumer152/DI8 全通过，全部零失败、零跳过。五/三目标 Release 编译0警告0错误；两包重新打包并以独立缓存验证五资产消费通过，8/9依赖图保持对应主版本。首轮net8的9项诊断失败及证书/资产加载问题保留红绿记录，不计作通过；完整证据、监听清理原因和仍开放关口见进度台账末尾。

R02 Windows Framework 包消费补验（2026-10-19；仅本地证据，不作正式支持决定）：

1. 复用现有五资产包消费样例，在 `Verify-Packages.ps1` 增加显式 `-VerifyNetFramework` 选项；只在 Windows 上启用，临时生成 net48 可执行宿主并引用已编译的 Standard2.0 消费库及本次核心包。仓库测试工程仍为net10，不新增日常Framework工程或产品目标。
2. 使用已安装的4.8参考程序集、自动生成宿主binding redirects，不给产品新增依赖；独立还原图/包哈希必须确认选中本次包的netstandard2.0资产。执行桌面CLR并输出实际Framework描述及注册表Release，不能将4.8目标编译当作4.8原版运行证据。
3. 保留现有factory、公共类型/现代API边界、8主版本日志/诊断、预取消/脱敏断言；先记录旧脚本拒绝新参数，再运行完整包验证、默认slnx编译及适当本地回归，独立走查后更新现有文档并提交。
4. 当前本机安装4.8.1（Release533509），只能记为net48目标在4.8.1上的基础包消费；不覆盖同步/异步SQL、TLS、完整核心API、真实数据库或4.8原版。§9/G0正式Framework支持决定、实库与非Windows验收继续开放。

本地验证已通过；生成Framework子宿主后重建父消费工程暴露源码glob包含子目录的问题，已排除framework-host/**/*.cs并用先失败/后通过重建验证。最终编译、包消费、独立走查及证据登记见进度台账末尾。

## D15 SHA-2 认证本地实施（2026-10-19）

本轮先完成 `caching_sha2_password` 与 `sha256_password` 的 TLS 路径和 caching fast auth；D15 不因此整体关闭。沿用现有连接选项、TLS 验证、PacketChannel 序号、连接总预算、异常脱敏和 A05 模式确认，不新增依赖。RSA 公钥配置/检索、其他认证插件和两模式真实服务端验收仍为余项，禁止自动获取未验证公钥或降低 TLS 要求。

1. 在现有 HandshakeFlow 增加最小内部 AuthenticationConversation，记录当前插件、一次插件切换及等待最终结果状态；同步/异步只共享决策，不互相包装 I/O。caching 初始 token 使用 `SHA256(password) XOR SHA256(SHA256(SHA256(password)) || seed)`，20字节挑战；空密码为空token。新SHA插件拒绝含NUL或无效Unicode的密码，错误不包含凭据。
2. `01 03` 不发送响应，继续等待最终 OK/ERR；`01 04` 仅在已成功 VerifyFull 的 TLS 后发送严格UTF8密码及NUL。sha256 初始/切换响应同样只允许 VerifyFull；未知插件、畸形/重复AuthMoreData、重复切换或最终阶段再次挑战均明确失败。保持 native/clear 的既有契约及失败即清理传输规则。
3. 先用公开握手环回测试复现未支持的失败，再覆盖同步/异步、初始/切换、固定token向量、最终OK/ERR、乱序/截断、弱TLS拒绝、取消/超时和脱敏。纯状态测试验证 VerifyFull 分支的编码决策；不得安装信任根、触发信任确认或绕过产品验证冒充成功TLS链。五资产消费走真实DbConnection入口，区分状态测试与真实TLS成功链证据。
4. 独立走查并修复问题，核心五/DI三目标Release编译和完整Unit/Consumer/DI通过后更新README、架构和进度证据，本地提交不推送。实库、异机和私有CA成功链继续暂缓，A05/C01及完整D15余项保持开放。

协议依据：固定Java `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1` 的 `CachingSha2PasswordPlugin` 与 `Sha256PasswordPlugin`，仅核对协议，不复制实现；相比Java的useSsl判断，发送完整密码保留本项目更严格的VerifyFull边界。

本轮SHA-2子模块已完成本地实现、独立走查及验证：核心五/DI三目标Release编译0警告0错误，完整Unit1514（新增SHA-2为72）、Consumer292（新增SHA-2为40）、DI8全部通过且无跳过；红绿和夹具修正记录见进度台账。D15保持进行中，未实现项和真实验收仍开放，不发布、不推送。

### D15 RSA 后续实施（2026-10-19）

1. 增加规范连接键 `Server RSA Public Key`（默认空，PEM SubjectPublicKeyInfo PUBLIC KEY文本）和 `Allow Public Key Retrieval`（默认false）。固定PEM由调用方提供，避免驱动引入文件读取或五目标不同的PEM文件API；冻结选项、多地址副本、序列化/脱敏和公共builder同步覆盖。默认日志/公开脱敏连接串不包含PEM，完整builder序列化可往返。无效固定key不回退检索。
2. 非VerifyFull的SHA-2完整认证在配置固定key时使用RSA OAEP-SHA1加密严格UTF8密码加NUL、与20字节seed循环XOR后的内容。无固定key只有显式Allow选项才请求：sha256初始/切换发01，caching full阶段发02，等待01前缀的PEM，再发送密文等待最终OK/ERR。初始sha256取钥请求后仍允许一次账户插件切换；切换后/full阶段严格等待key，无key不得OK。空密码发送无密码标记，不请求key或加密；已验证TLS仍优先现有非空密码路径，clear插件不因RSA选项开放。
3. 新增最小内部RsaPassword，用BCL RSA.ImportParameters/Encrypt保证Standard2.0兼容；复用ByteCursor严格解析SPKI DER，仅RSA OID、正整数、完整无尾随数据、16KiB PEM上限、2048–8192位模数、正奇数且不超过64位指数。拒绝私钥、弱key、非规范DER、过长密码、乱序/重复/缺失key，错误不回显key/password。公钥获取本身无身份验证，README明确中间人风险和默认关闭；TLS Disabled不加密业务SQL。
4. TDD先以公开握手环回/独立RSA私钥解密验证未支持失败，再覆盖两插件、初始/切换、固定/获取、同步/异步、空/Unicode/边界密码、恶意DER/PEM、取消/超时/最终ERR及五资产公开消费。独立走查、全目标编译及完整Unit/Consumer/DI通过后更新证据并本地提交；实库、异机和VerifyFull成功链仍暂缓，其他插件保持余项，不据本轮关闭D15或项目。

RSA子模块已完成本地实现与独立复查：修复初始sha256取钥后账户插件切换，补齐空密码无密码标记；依据MySQL8服务端sha256入口接受空包/单NUL的规则，不把RSA密文NUL当作空密码线协议。最终五/三目标Release编译0警告0错误，Unit1596（新增82）、Consumer332（新增40）、DI8全通过；红绿、沙箱TLS权限失败与最终证据见台账。D15其他插件及真实关口仍开放。

### D01 OB20 报文与校验子模块（2026-10-19）

先实现内部报文层，D01 在能力协商、流式传输和完整链路追踪接入前保持进行中。依据固定 Java 基线 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1` 的 `OceanBaseProtocolV20`、`Ob20PacketInputStream` / `Ob20PacketOutputStream` 和 `OceanBaseCRC16` / `OceanBaseCRC32C`；只核对线协议，不复制实现或查表常量。

1. 先写 OB20 固定字节夹具和校验失败测试。用独立 Java 基线校验器核对夹具，不能只以同一编码器往返证明正确。7字节压缩外头、24字节OB20头、4字节尾，全部小端；CRC16/ARC覆盖头前29字节，尾CRC32C从0开始且不做最终异或，0字段表示关闭对应校验。
2. 新增内部 `Ob20Checksum` 和 `Ob20Packet`，复用ByteCursor，编码/解码完整单帧；独立ParseHeader先验证身份、请求/序号、长度及上限，供后续流式实现于正文分配前调用。外头uncompressed length非零明确拒绝；不猜测或解压。24位包长度同时限制payload及extra；无截断/尾随数据。extra存在时严格验证4字节长度，不把extra交给MySQL解析器；legacy/new extra内容仅作为不透明字节保留，具体追踪语义留后续子模块。保留flags/reserved及外层序号，不自行解释未知标志。
3. 覆盖固定校验向量、分段增量CRC、边界长度/请求/序号、篡改与截断、关闭校验、extra及未知标志；验证正文/extra是原帧视图，避免解码二次复制。此阶段无公开选项或网络行为变化，不启用OB20、不宣称实库兼容。
4. 核心五/DI三目标Release编译、完整Unit/Consumer/DI、独立走查及diff检查通过后更新架构/台账并本地提交。之后继续能力协商和PacketChannel流式接入；实库及异机矩阵暂缓。

报文/校验子模块本地证据：固定Java原校验类独立执行确认头CRC16 `bb3d`、raw CRC32C `58e3fa20`（输入123456789），以及完整固定帧。40项OB20回归已实际执行；Release核心五/DI三目标0警告/错误，完整Unit1636、Consumer332、DI8全部通过且无跳过，独立只读走查无必须修复项。该子模块未接入公开API，未新增Consumer案例；既有Consumer完整回归不代表OB20网络消费已验证。详细命令/报告见进度台账末尾，D01仍进行中。

### D01 OB20流式传输及协商后续（2026-10-19）

1. 内部Ob20Stream包装当前标准/TLS流，固定64KiB写缓冲、8KiB extra跳过缓冲；收包按调用方读取量返回，不缓存完整OB20正文。头先校验、CRC按段累积，帧正文耗尽时先消费/验证尾再返回最后一段；extra只校验长度后按段跳过。不请求session sync/追踪扩展；proxy reroute明确失败，不重放SQL。每请求最多100万OB20帧，防止空帧循环。
2. PacketChannel每个逻辑写请求启动新的24位request ID、OB20序号从0开始，响应沿发送后序号继续并允许byte回绕；内层MySQL分片/序号仍由原实现处理。命令成功、prepare元数据、内部探测及游标分页切换前验证OB20 last及无剩余内层字节；COM_STMT_CLOSE显式无响应完成。读写/取消/校验失败沿原租约销毁会话，错误不重试。
3. 新连接键`Use OB20`及builder属性UseOb20，默认false，接入冻结/多地址/序列化。仅请求时在已协商连接属性写`__proxy_capability_flag=256`；认证完成、模式确定后，只有连接属性能力与服务端ob_capability_flag的OB_PROTOCOL_V2均确认才包装包流，否则继续标准流。认证始终标准帧，TLS保持现有策略；不放宽A05/C01。
4. 先写失败流式/协商测试，再实现；覆盖同步/异步、短读/分段写入、跨OB20/MySQL帧、extra、CRC与last错误、取消/关闭/超时、无响应关闭、公开五资产环回及标准回退。Release全目标、完整Unit/Consumer/DI、独立走查及文档复查后本地提交。完整链路追踪仍为D01后续，真实矩阵继续暂缓。

本轮协商/传输子模块已实现并通过本地验证：核心五/DI三目标Release编译0警告/错误，完整Unit1662（OB20共66，流式新增26）、Consumer394（OB20新增62）、DI8全部通过且无跳过。独立走查发现握手属性名遗漏`_flag`，已用固定Java常量复核并先复现20失败后修复；再次走查无必须修复项。公开测试含可信ERR后普通连接复用及状态探测、三页cursor、第二页CRC/缺last和等待fetch响应时取消；未覆盖OB20经TLS环回或读取部分正文后的取消，不作这两项已验证声明。证据见进度台账末尾；D01完整链路追踪与真实矩阵仍开放，不关闭项目目标。

### D01 完整链路追踪本地实施（2026-10-19）

沿用既有五目标、ActivitySource及单租约OB20通道，不新增依赖或独立日志系统。协议参考固定Java基线FullLinkTrace、OceanBaseProtocolV20、ObObj和OceanBaseSerialize，仅核对行为与字段，不复制实现。

1. 连接键`Enable Full Link Trace`/builder属性EnableFullLinkTrace默认false，开启时要求Use OB20=true。冻结/多地址/序列化接入；握手请求FULL_LINK_TRACE、NEW_EXTRA_INFO与SHOW_TRACE，只有服务端确认OB20及FULL_LINK_TRACE才生效，缺失追踪能力继续普通OB20。支持legacy ObObj varchar键值及new-extra TLV两种格式，不依赖新格式能力才能追踪。
2. 内部Ob20TraceContext负责两格式extra与嵌套TLV、服务器control采样/记录策略、span及driver/show-trace日志；复用ByteCursor并逐层限定长度。启用追踪时单帧extra上限64KiB，仅在尾CRC验证后应用控制信息；无追踪时仍按原路径分段跳过。拒绝已知字段畸形长度、重复、非法数值/策略，跳过未知TLV，校验legacy字符串终止符及有界变长整数。不因服务端文本而分配不受控内存或写日志。
3. PacketChannel/Ob20Stream每请求首帧附加extra，CRC包含extra，分片/请求/last/无响应关闭语义不变。CommandExchange在execute/prepare/探测/cursor共享本地Activity的trace ID，每线协议请求独立128位span；无监听者时生成内部上下文，事务控制传递外层Activity上下文。driver记录仅固定模块、随机ID及微秒时间戳，下一请求发出，单个待发记录有界；不采集SQL、参数、凭据、端点、任意Activity标签或baggage，不创建额外SQL或重放操作。
4. TDD先固定TLV/legacy/UUID线序、控制与上下文测试的失败证据，再实现并补真实公开环回、同步/异步、五资产、采样0/1、关闭/缺能力回退、错误/CRC/extra上限、prepare/游标/事务及故障清理。独立走查并修复、Release全目标编译及完整Unit/Consumer/DI通过后同步现有文档和本地提交。真实服务端采样/SHOW TRACE、版本/代理矩阵与异机继续暂缓；不以夹具关闭生产验收。

本轮本地实现、独立走查修复及回归已完成：核心五/DI三目标Release编译0警告/错误，完整Unit1689（新增追踪27）、Consumer464（新增追踪70）、DI8全部通过且无跳过。修复legacy大小写/有界非最短varint、未知重复TLV及signed level，红绿证据见进度台账末尾。完成记录仅随下一请求发送，关闭不刷新最后一条；query时间只校验，未导出指标或新增AppInfo定制API。D01正式状态待验证，OB20经TLS环回、真实采样/SHOW TRACE和版本矩阵仍开放；其他未实现模块不据本轮关闭。

### D02 Oracle PrepareExecute报文子模块（2026-10-19）

先补过程/OUT/RETURNING所需的扩展线协议基础，之后接入公开命令与输出生命周期；本子模块不改变CommandType或ParameterDirection的支持状态。沿用固定Java基线ComStmtPrepareExecute.send/read与JDBC4ServerPreparedStatement.calculateCheckSum，只核对字段和行为，不复制实现或新增依赖。

1. 新内部OraclePrepareExecutePacket组装0xA1、statement ID、非游标/iteration=1、长度编码UTF-8 SQL、32位参数数目、既有PreparedParameters编码、executeMode=0/closeCount=0/raw SQL CRC32C/reserved=0。完整逻辑包在分配前限制64MiB；参数数量限制响应可表达的UInt16，检查编码块最低长度及零参数一致性。
2. 解析17字节扩展响应头：OK、statement ID、列数、参数数、filler、warning、extend flags和hasResultSet；拒绝截断/尾随、坏marker/filler/布尔值及超过4096的列数。保留未知扩展位供执行层决定支持边界，不将头OK误当SQL终态，也不在此分配列/参数数组。
3. 先用独立固定字节、SQL长度前缀/Unicode/CRC、参数块、所有头截断与恶意边界测试证明缺实现，再实现；用固定Java原CRC类独立输出请求夹具。全目标Release编译、完整Unit/Consumer/DI、独立走查与文档复核通过后本地提交。后续必须接入扩展响应元数据、结果/尾OK、OUT原子发布和事务/资源清理，不能据本子模块宣布D02完成；真实服务端联调继续暂缓。

报文子模块已实现并本地验证：Release核心五/DI三目标0警告/错误，完整Unit1710（新增21）、Consumer464、DI8全部通过且无跳过。独立只读走查核对固定Java布局及CRC，未发现必须修复项；独立夹具是手工字段构造加原Java CRC，不冒称实际执行Java发送方法。尚未接公开API，Consumer仅为既有完整回归；D02结果/尾包、输出发布和生命周期接入仍须继续，实库和异机暂缓。

### D02 扩展响应流与执行终态（2026-10-19）

在接入公开OUT发布前，先贯通QueryResult对PrepareExecute响应的实际流式消费。复用既有列定义、二进制行、SequentialAccess及多结果路径，新增可选内部Oracle参数数目与只读响应头；默认查询路径不变。先验证Oracle/binary前提，读取17字节头、核对参数数量、累计4MiB元数据预算并消费参数定义/可选EOF，随后直接读取响应头指定的列及行，不误读为普通列数包。无结果时直接读最终OK/ERR；有结果时行终结不释放租约，须再读尾OK/ERR；尾OK才更新计数及会话、验证OB20边界并完成交换，尾包MoreResults继续普通结果。同步/异步及顺序路径分别推进底层I/O，取消/截断/错误不得丢失未决状态或重放SQL。

TDD以真实TCP环回覆盖两EOF格式、有/无结果、普通/顺序、同步/异步、参数计数/元数据/头/尾错误、等待尾包时取消/超时、可信尾ERR与后续复用、多结果及状态观察。保持公开过程/OUT/RETURNING拒绝，本子模块不发布输出参数；其后继续公共入口、版本/选项门槛及OUT原子发布。全目标编译、完整Unit/Consumer/DI、独立走查和文档同步通过后提交；实库和异机继续暂缓。

响应子模块已实现并本地验证：定向69项（既有报文21、新响应48）通过；Release核心五/DI三目标0警告/错误，完整Unit1758、Consumer464、DI8全部通过且无跳过。独立只读走查核对Java布局、租约、两类读取路径、错误及会话快照，未发现必须修复项。初次取消断连失败来自测试错误地等待EndOfStreamException，修正为精确接受协议关闭或TCP连接重置；早关闭夹具增加服务端写完行终结的屏障，避免清理与服务端写竞争；不改变产品异常或传输策略。公开入口、OUT结果解释和生命周期仍继续实施，D02不关闭。

### D02 公开过程与标量输出接入（2026-10-19）

继续现有内部请求/响应模块，增加默认关闭的Use Oracle Prepare Execute连接选项；仅已认证Oracle会话可使用，首次执行以同一租约/预算查询`select ob_version() from dual`，严格解析并按2.2.76门槛缓存物理会话版本，不使用握手兼容版本或故障后回退。公开StoredProcedure仅接受安全的限定过程/函数名，生成PL/SQL块；Text保留现有词法器，只在显式扩展选项且存在输出方向时使用PrepareExecute。InputOutput发送原始快照，Output/ReturnValue发送NULL；重复输出位置和多个ReturnValue在发包前拒绝。

内部PL OUT及RETURNING结果隐藏于公开reader，按位置复用二进制类型解码，标量输出至多一行、零行映射DBNull；多行明确拒绝，数组输出归D06。所有输出先暂存，完整响应及OB20边界验证成功后、释放租约/日志回调前一次发布；尾ERR、后续结果错误、取消/超时和提前关闭均保留原值。返回的statement ID立即纳入既有延迟关闭队列，命令清理在空闲会话刷新。继续覆盖同步/异步、SequentialAccess、事务、后续多结果、默认选项拒绝、版本探测及故障清理；完成编译、完整测试、独立走查并同步文档后本地提交。实库和异机不做，REF CURSOR/LOB/复杂输出等归各自后续模块，不扩大本模块声明。

本轮标量接入已本地实现：Release核心五/DI三目标0警告/错误，完整Unit1840、Consumer494、DI8全部通过、零跳过。独立走查发现并修复最终OK后取消/关闭仍可能发布OUT的竞态，4项修前失败、修后通过；再次走查无新增必须修复项。新增30项公共消费覆盖五资产的标准、OB20和尾部夹带包边界。证据与限制见台账末尾；D02保持进行中，复杂/多行输出、独立Prepare/FetchSize及真实互通不据本轮宣称支持。

### D03 REF CURSOR 本地实施（2026-10-19）

沿用Oracle PrepareExecute入口与2.2.76门槛，追加OceanBaseDbType.RefCursor和一次性OceanBaseRefCursor输出对象；纯OUT/ReturnValue发送NULL，拒绝游标输入/输入输出。Oracle类型163的二进制值为4字节LE游标ID，不能当普通整数或文本解码。输出对象同标量先暂存、完整响应及边界成功后发布；NULL保留DBNull。

1. 先以真实TCP环回写失败测试，覆盖过程输出、固定cursor ID、COM_STMT_FETCH及COM_STMT_CLOSE、两EOF/同步异步/Sequential与分页。复用QueryResult的逐页读取及元数据一致性检查，首fetch取得列定义后直接读行，不再次误发fetch；不缓存完整结果。
2. GetDataReader/GetDataReaderAsync绑定原物理会话及事务代次、原命令超时，默认每页128行，可传正数fetchSize和默认/SequentialAccess/CloseConnection行为。每个对象仅打开一次reader，同一连接独占规则不变；获取租约失败可重试，发起读取后失败不重放。事务结束/后续事务代次或连接重开均不得让旧游标进入新上下文。
3. 每次扩展执行保留父statement直到所有返回游标释放；完成读取、Dispose或失败只释放一次，复用延迟关闭队列，空闲Dispose刷新。未读完关闭仍销毁会话；未读取的对象须Dispose，连接关闭释放服务端全部资源。所有输出失败不泄露暂存游标，标量已有生命周期保持回归。
4. 补多游标、空/NULL、错误/畸形/取消、事务/连接/父command清理边界与五资产公共消费。独立走查、全目标Release编译、完整Unit/Consumer/DI、文档同步和本地提交；实库/异机继续暂缓，不将夹具视为服务器兼容证明。

D03本地输出/读取实现及独立走查修复已完成：修后Release核心五/DI三目标0警告/错误，完整Unit1876（新增36）、Consumer524（新增30）、DI8全通过、零跳过。终态取消/关闭竞态4项先红后绿，连既有C04终态契约4项一起验证通过；详情见台账末尾。正式状态待验证，实库、扩展经TLS、异机及新打包/旧运行时矩阵未执行，输入/输入输出及普通结果列cursor值未支持。后续继续D04，不关闭整体目标。

### D04 本地实施顺序（2026-10-19）

沿用现有字段流、Oracle PrepareExecute和原会话独占规则，不迁移locator，不通过第二条连接读取旧事务的数据。分为下列可独立走查的交付；每项先有失败测试，再实现、编译、完整单测、走查和本地提交，D04整体在全部接入前保持进行中。

1. 内部`LobLocator.Parse(byte[])`：识别V1的40字节头及V2的8字节内存头，返回版本、字节长度、内联数据的借用视图或行外标记。严格验证版本、头长度、长度/偏移、扩展与rowkey边界；不按未受信长度分配数据，不将locator当普通BLOB/CLOB。覆盖空值、V1兼容locator、V2 simple/extern/disk、行内/行外、截断、溢出及大行外长度。此步不改变握手或公开支持声明。
2. 在握手能力和显式选项门槛后接入210/211类型、元数据、参数及D02输出；公开LOB对象绑定物理会话和事务代次，校验输出原子发布、连接重开、事务结束、Dispose和reader独占。默认未启用时继续拒绝locator类型，不把协议夹具当作实库互通。
3. 使用原会话执行DBMS_LOB.READ的有界分块Stream/TextReader，同步/异步取消、超时、Unicode字符与字节偏移分别验证，复用既有命令预算和错误清理；明确原reader消费/释放后才能获取独占租约，不缓冲整LOB或另建连接规避占用。补齐LOB输入/输出、流式读取及五资产公开消费。
4. 复核全部D04契约，更新README、架构和台账。真实版本/OBProxy、LOB写入/临时对象服务端语义、扩展经TLS与异机验收单独保留；实现范围不足时不得将D04改为完成。

协议依据仅参考行为：固定Java `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`的`Lob.java`、`Blob.java`、`Clob.java`，及固定OceanBase `5e8a63d29dae9fde5e962b8fcc7b167d438fc343`的`deps/oblib/src/common/object/ob_object.h/.cpp`（ObLobLocator、ObMemLobCommon、ObLobCommon、get_inrow_data/get_lob_data_byte_len）。V2前4字节在该服务端基线中是带is_mem_loc标记的ObLobCommon，不是V1的LOB! magic；不照搬Java忽略该头的宽松处理。未知布局明确拒绝，后续按版本证据扩展。

### D04 公开读取与绑定本地接入（2026-10-19）

在内部解析提交`cc0cc1d`之后，接入默认关闭的UseLobLocators、ClientSupportLobLocator及独立client属性V2请求，保留OB20/追踪的proxy属性命名空间。公开Blob/Clob类型及OceanBaseLob，标准/provider getter均返回handle；D02输出在完整终态后原子发布，InputOutput按原值推断的类型冻结并与回复匹配。参数和打开流在连接锁内校验原物理会话、事务引用及epoch，旧连接、已结束事务、已释放handle和能力不足均在发包前拒绝。

内联数据借用现有payload；行外BLOB/CLOB通过原会话DBMS_LOB.READ分块读取。每次只持有一块正文，byte/scalar偏移分别推进；NUMBER amount只接受精确正整数并检查实际buffer、进展和总字节长度。原reader须先完整消费；流占用预约，Dispose取消在途读取，异步读取/清理不借同步网络入口。现代公共stream/handle支持异步释放，Standard2.0保留内部异步清理。真实数据库临时对象生命周期和修改语义尚无证据，不新增FREETEMPORARY猜测，也不将客户端Dispose宣称为数据库对象销毁。

已补输出/返回值/输入输出、NULL/尾ERR、错误类型、畸形locator、跨连接/重开/事务代次、Dispose、取消/原timeout、有界Unicode分块及五资产标准/OB20边界测试；失败回归、全量验证及独立复查证据见进度台账末尾。D04整体仍进行中，LOB修改和临时对象API的本地可实施部分继续评估；实库、OBProxy、扩展经TLS、异机及发布矩阵暂缓，不据本子模块关闭整体目标。

### D04 LOB 修改本地实施（2026-10-19）

本轮沿现有读取/绑定契约增加原会话WRITE/TRIM，参考固定Java Blob.updateBlobToServer/trimBlobToServer和Clob对应方法的DBMS_LOB参数方向，仅参考行为。公开OceanBaseLob.WriteBytes/WriteBytesAsync（byte[]片段）、WriteText/WriteTextAsync（严格UTF8文本）及Truncate/TruncateAsync；位置为.NET常用的0起始offset，BLOB单位字节、CLOB单位Unicode标量，TRIM长度沿相同单位。同步路径同步I/O，异步路径直接异步I/O，不引入依赖或新连接。

- [x] 先固定失败用例：WRITE每块BLOB最多32767字节/CLOB最多8191标量；请求使用上一块成功返回的locator，输出只在完整终态后更新handle；TRIM参数和后续读取使用新locator。
- [x] 实现共享内部LobMutation，预约原会话覆盖整个多块修改，绑定原事务epoch/timeout；handle Dispose取消在途修改。输入范围、UTF16有效性和Int64偏移溢出全部在首包前校验。完整成功后更新locator，NULL/不匹配输出破坏会话并使旧handle失效。
- [x] 覆盖同步/异步、Unicode跨块、>Int32位置、空输入/预取消、活动reader/流、事务结束/连接重开、已知ERR/部分前缀/尾中断/超时和Dispose。WRITE/TRIM具有副作用，未确认终态保留既有结果未知语义；多块非原子，不自动回滚已成功前缀或重放失败块。
- [x] 补五资产公共消费，独立只读走查并修复，Release核心五/DI三目标编译、完整Unit/Consumer/DI通过后同步README/架构/台账并本地提交。临时对象创建/所有权管理按后续子模块推进，实库资源语义、OBProxy、TLS扩展和异机联调暂缓。

### D04 临时 LOB 创建与显式所有权（2026-10-19）

沿用现有选项、Oracle PrepareExecute和原会话/事务代次契约。连接新增CreateTemporaryLob(OceanBaseDbType, OceanBaseTransaction?)及Async入口，只接受Blob/Clob；显式事务按命令相同规则绑定，使用连接CommandTimeout。固定PL/SQL块声明BLOB/CLOB局部变量，再调用CREATETEMPORARY，cache=FALSE、dur=DBMS_LOB.SESSION，避免NULL协议类型无法选择重载及猜测PL/SQL BOOLEAN线上编码；最后赋给OUT locator。完整终态后创建拥有释放权的新handle，失败不交付所有权。创建SQL开始发送后失败（包括尾ERR）或成功后无法安全交付，销毁原会话避免遗留资源；未发送创建SQL的版本探测可信ERR/不支持版本保留可用会话。

创建者handle新增FreeTemporary/FreeTemporaryAsync，普通查询/输出handle没有释放权。释放持原预约、复用修改取消/超时/未知结果策略。固定类型的PL/SQL局部变量接收输入locator并调用FREETEMPORARY，另返回固定字符串完成标记；不把服务端释放后标记invalid的locator交给普通解析器。成功后客户端handle失效，在途失败沿修改契约失效；不能跨连接、事务代次或活动流使用。Dispose保持现有客户端清理契约，必须在事务结束及Dispose前显式FreeTemporary；未显式释放或释放失败的SESSION临时对象可能保留至原会话结束，不引入后台清理或自动提交。

协议依据为OceanBase官方文档固定提交`2a5ae3a23a51729d065cec716508eb26cb064106`的Oracle DBMS_LOB CREATETEMPORARY/FREETEMPORARY/ISTEMPORARY说明（固定链接见架构D04临时对象段）；Java createBlob/createClob只创建本地空对象，不作服务端临时创建依据。官方文档证明接口契约，本地夹具不替代实库资源验收。

- [x] 首先固定创建/释放同步异步、BLOB/CLOB、typed局部变量及OUT空值/完整输出、显式所有权拒绝和未释放Dispose无数据库调用的失败测试。
- [x] 实现连接工厂、所有权标记及原会话Free操作，覆盖事务/连接重开、活动流、取消/超时、尾ERR/NULL/坏输出与创建交付终态竞态；创建后尾ERR回收、版本探测失败保留会话回归已通过。
- [x] 补五资产标准/OB20成功与尾包拒绝消费，Release全目标及完整Unit/Consumer/DI通过，独立走查修复及文档同步完成；本地验证见进度台账，不推送。实库和异机继续暂缓。

本地验证：Release核心五/DI三目标0警告/错误，完整Unit2054（新增46项）、Consumer724（新增80项）、DI8全通过且零跳过。独立走查修复typed局部变量重载及创建后尾ERR无主资源问题，红绿证据见台账；D04本地范围已接入，状态待验证，真实资源/版本/OBProxy与扩展经TLS关口保持开放。后续推进D05，整体目标未完成。

### D05 ARRAY/STRUCT 本地实施顺序（2026-10-19）

沿固定Java基线`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`的ColumnDefinition、BinaryRowProtocol.getComplexArray/getComplexStruct和ComplexUtil行为实现，不复制源码。复杂结果没有统一的外层长度，必须由完整类型描述确定字段边界；参数NULL位图从bit0开始，结果及嵌套结果从bit2开始，两方向不能互用同一序列化格式。NUMBER保留OceanBaseDecimal，日期/纳秒复用已有精确codec，不猜测未支持成员。

- [x] 独立嵌套编解码：Internal/Values/ComplexType.cs保存只读成员布局，ComplexValueCodec.cs以ByteCursor解析结果、以BinaryWriter编码参数。先固定Oracle对象/集合、嵌套NULL与跨位图边界、空值、Unicode、宽NUMBER、RAW、DATE/TIMESTAMP及后续字段报文；拒绝MySQL、畸形长度、无效标量、过深嵌套/过多成员与参数类型不匹配。tests/OceanBaseConnector.UnitTests/ComplexValueCodecTests.cs先红后绿，完整编译/Unit/Consumer/DI与独立走查后提交。本层不开放公共复杂值入口。
- [x] 类型字典及显式生命周期：按schema+type精确身份从Oracle字典装载完整嵌套描述，先定义同会话查询、活动reader/事务、缓存失效及刷新规则，再接入公共ARRAY/STRUCT值构造。不能沿Java实现偷偷另建查询连接，不能跨schema仅按大写typeName复用。本地验证见进度台账“D05同会话类型字典与公开值构造”；复杂列/参数/输出继续下一子模块。

本子模块实施决定：公开GetComplexType/GetComplexTypeAsync(schemaName, typeName, transaction)和ClearComplexTypeCache(transaction)，名称按字典原样精确绑定，不解析SQL标识符或同义词。OceanBaseComplexType/Members保存不可变完整布局，CreateValue构造OceanBaseComplexValue并检查成员/嵌套类型、精确标量、字节与成员预算；RAW及返回数组复制，嵌套必须使用对应描述构造的值。仅Oracle模式；当前不支持LOB/cursor/national charset成员及循环类型图，明确拒绝。

同一物理会话预约覆盖ALL_TYPES及ALL_TYPE_ATTRS/ALL_COLL_TYPES所有查询；显式绑定当前事务，固定原会话/事务epoch，每个查询沿用连接CommandTimeout，异步入口使用真实异步I/O及调用方取消。活动reader/预约/环境事务不发包；结果列形状、owner/name/kind、对象属性总数与连续序号、集合唯一元素均核验。类型图最多32层；当前会话缓存累计最多65535成员、65535命名类型及4MiB字典UTF8文本，额度满时可清空重载。失败不发布部分缓存或扣减额度。缓存键为区分大小写的schema+type二元组；关闭重开、事务切换及显式清空使全部旧描述/值失效，清空同时处理父子依赖。外部DDL不自动感知，业务须在无活动reader时清空并重载；不伪造服务端类型version。公开值快照可以在失效后检查，但不能再构造或绑定。该子模块独立测试/五资产消费、全量build/Unit/Consumer/DI及只读走查后提交；公共复杂列/参数仍由下一协议子模块接入。
- [ ] 协议及公开接入：复杂列元数据/类型信息、二进制行边界与顺序读取、参数附加类型头、D02输出原子发布及五资产同步/异步标准/OB20消费。文本协议没有可信复杂值定义时明确拒绝，不能当byte[]/string跳过。每层保留原会话错误/取消/结果未知契约。
- [ ] 复核完整D05后同步README/架构/台账，本地完整验证和独立走查通过后提交；真实字典/DDL可见性、服务端往返和版本/OBProxy资源语义单独延期，不因局部codec通过关闭D05。

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
