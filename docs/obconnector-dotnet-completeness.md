# OceanBaseConnector .NET 完整驱动能力与缺口基线

**审计日期：** 2026-10-24
**适用仓库：** `D:\dotnet-code\obconnector-dotnet`
**参考实现：** 本仓库源码、测试和兼容性记录；本地 `D:\java-code\obconnector-j` 的 Connector/J 实现
**文档性质：** 能力审计、目标契约和验收基线；不代表尚未完成的功能已经实现

面向项目评审的单页结论和交付门槛见 [驱动完整性走查报告](obconnector-dotnet-driver-audit.md)；本文件保留逐模块的验收要求和源码索引。

## 1. 目的和结论

本项目是一个面向 OceanBase MySQL 兼容模式和 Oracle 兼容模式的托管 ADO.NET provider。当前已经具备连接、认证、基础 SQL、参数绑定、结果读取、本地事务、XA、Oracle PrepareExecute、LOB、复杂类型、数组绑定、TLS、OB20 和诊断等基础模块。

当前驱动仍不能称为完整的生产级 ADO.NET 驱动。连接池已具备基础容量、等待、存活探测和生命周期控制，ambient transaction 已支持本地 volatile enlistment，但还需要补齐完整 reset、协议可选能力、活动连接故障恢复、企业 TLS、ADO.NET 命令行为、Oracle 结果形状、目录元数据和真实兼容矩阵。

本文件统一区分四种状态：

| 状态 | 含义 |
| --- | --- |
| 已实现 | 源码存在对应行为，并有本地单元测试、消费者测试或协议 fixture 支持 |
| 部分实现 | 只支持部分模式、类型、参数组合或目标框架 |
| 明确缺失 | 源码或测试明确拒绝该能力，必须新增实现才能支持 |
| 未验证 | 源码路径存在，但缺少真实 OceanBase、OBProxy、TLS、KDC 或目标运行时证据 |

“已实现”不等于“服务器兼容已确认”。真实兼容性以 [compatibility.md](compatibility.md) 和本文件第 9 节为准。

### 1.1 对走查问题的直接结论

| 问题 | 结论 | 证据和边界 |
| --- | --- | --- |
| Java 驱动有没有连接池？ | 有。 | Connector/J 源码包含 `OceanBasePoolDataSource`、`OceanBasePooledConnection`、`Pools`/`Pool`，职责包括借用、归还、最小/最大容量、空闲/生命周期清理和池事件。本仓库的 [obconnector-j-architecture-outline.md](obconnector-j-architecture-outline.md) 第 M13 节记录了该静态走查；没有把它当作 Java 运行时兼容性证明。 |
| .NET 驱动以前有没有连接池？ | 在连接池提交之前没有。 | 原先每个 `OceanBaseConnection` 都直接持有一个物理会话，关闭即释放会话，缺少逻辑连接和物理会话的分层。 |
| .NET 当前有没有连接池？ | 有基础池化，但不是完整连接池。 | `Pooling=true` 通过 [PhysicalSessionPool.cs](../src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs) 复用已认证物理会话；归还时执行 `COM_RESET_CONNECTION`，MySQL 模式再执行 `COM_INIT_DB`。当前已支持最大容量、等待超时、最小池预热及失败回滚、空闲/总生命周期回收、借用前存活探测、空池 registry 清理和池指标；完整 reset 与真实服务器 reset 兼容性仍未闭合。 |
| 是否已经达到 Java/.NET 生产驱动水平？ | 没有。 | 还缺少会话变量/临时对象的完整识别与 reset 矩阵、LOB/cursor 的完整 reset、ambient 的分布式提升和故障矩阵、活动连接恢复、协议可选能力、企业 TLS、完整 ADO.NET 行为和真实 OceanBase/OBProxy 验收。 |

### 1.2 审计范围和证据等级

本次走查读取了以下范围：

- `src/OceanBaseConnector` 的连接、会话、协议、传输、命令、事务、XA、LOB、复杂类型、Schema、批处理和诊断实现；
- `tests/OceanBaseConnector.UnitTests`、`ConsumerTests`、`IntegrationTests` 和 `Acceptance` 的回归、消费者和真实数据库入口；
- `README.md`、`docs/compatibility.md`、架构设计、进度记录和 Connector/J 结构分析；
- 项目目标框架、NuGet 打包/消费者验证和 CI 配置。

本文件把证据分成三层：

1. **源码/本地回归证据**：能证明驱动在当前代码和 loopback/fixture 上的行为；
2. **消费者/包证据**：能证明公共 API 在目标 TFM 上可以引用和运行；
3. **真实互操作证据**：必须由 OceanBase、OBProxy、TLS/KDC 或故障环境实际运行得出。

只有第三层证据才能把“未验证”升级为“真实兼容”。mock、协议 fixture 和自签名 `SslStream` 不能替代真实服务器、代理或身份基础设施。

## 2. 当前能力基线

| 模块 | 当前状态 | 主要边界 |
| --- | --- | --- |
| 连接和配置 | 部分实现 | 连接字符串、模式确认、多地址、超时、TLS、地址解析和池容量/等待/生命周期选项已存在；静态池清理和最小池预热已实现，真实兼容性未闭合 |
| ADO.NET 基础对象 | 部分实现 | `DbConnection`、`DbCommand`、`DbParameter`、`DbDataReader`、`DbProviderFactory` 已有；`SingleRow`/`SingleResult` 和只读文本查询的 `SchemaOnly` 已支持，`KeyInfo`、`DbDataAdapter` 和 `DbCommandBuilder` 仍缺失 |
| .NET 8+ 资产 | 部分实现 | `DbDataSource`、`DbBatch`、savepoint、异步事务、GSSAPI 只在现代 TFM 可用 |
| 认证 | 部分实现 | native、old、clear、SHA-2、Ed25519、PAM、GSSAPI 有代码；真实插件和 KDC 矩阵未闭合 |
| TLS | 部分实现 | `VerifyFull`、`Required`、`Disabled` 有代码；客户端证书、可配置信任和撤销策略缺失 |
| 基础 SQL | 已实现 | 文本执行、server prepare、多个结果、取消、超时和流式读取已有路径 |
| 多语句和压缩 | 部分实现 | `Allow Multi Statements=true` 可协商并执行文本多语句；MySQL 压缩和 OB20 压缩 frame 仍未开放 |
| LOCAL INFILE | 部分实现 | `Allow Local Infile=true` 才协商 `CLIENT_LOCAL_FILES`，应用流工厂负责提供输入；真实服务器、代理和长文件矩阵未验证 |
| OB20 | 部分实现 | 普通 OB20、校验和、trace 结构有代码；压缩 frame 和 proxy reroute 缺失 |
| 本地事务 | 部分实现 | MySQL/Oracle 隔离级别和 savepoint 有限制；ambient 支持本地 `TransactionScope` enlistment，分布式提升不支持 |
| XA | 部分实现 | 显式生命周期和 recover API 有代码；外部 coordinator crash recovery 未验证 |
| 连接池 | 部分实现 | `Pooling=true` 可复用物理会话，支持最大容量、等待超时、最小池预热及失败回滚、借用前存活探测、惰性生命周期回收、空池 registry 清理和池指标；完整资源 reset 和真实服务器矩阵未闭合 |
| HA 和恢复 | 部分实现 | Open 阶段 TCP 候选地址重试；活动连接没有透明迁移、重连或 replay |
| MySQL 类型 | 部分实现 | 常用数值、时间、YEAR、BIT、JSON、零日期已有映射；字符集和扩展类型仍有限制 |
| Oracle 类型 | 部分实现 | DATE、TIMESTAMP、TZ/LTZ、INTERVAL、NCHAR、RAW、LOB、复杂类型已有部分实现 |
| Oracle 输出 | 部分实现 | 过程、OUT、RETURNING、REF CURSOR 和数组绑定有路径；若干参数方向和结果形状不支持 |
| 复杂类型 | 部分实现 | 标量 object/collection 有路径；LOB、cursor、national 成员、循环结构和文本结果不支持 |
| Schema 元数据 | 部分实现 | 只有六类 `GetSchema` collection，未覆盖完整目录 |
| 批处理 | 部分实现 | `DbBatch` 顺序执行；没有 rewrite、multi-send、bulk 或跨命令 statement cache |
| 诊断 | 部分实现 | `ILogger`、`ActivitySource`、`Meter` 和池生命周期指标已有；协议 debug、slow-query profile、建连/探活细分指标未完整提供 |
| 真实兼容性 | 未验证 | 当前记录主要是 Windows/.NET 10、直连 OceanBase 4.4.2.2、TLS disabled |

## 3. 完整驱动的目标边界

完整驱动的目标不是无条件支持所有 SQL，而是满足以下契约：

1. 标准 ADO.NET 生命周期可预测：打开、关闭、取消、事务、reader、LOB、连接池和异常状态不会泄漏。
2. 连接池复用不会把上一个借用者的事务、变量、对象或结果资源带给下一个借用者。
3. MySQL 兼容模式和 Oracle 兼容模式共享一套公共 API，模式差异由连接状态和明确的能力矩阵表达。
4. 协议可选能力只有在完成协商、编解码和失败恢复后才对外开放。
5. 已确认的成功、已确认的失败和 unknown outcome 必须区分，驱动不能因为重试方便而重复执行不确定的写操作。
6. 每项公开能力必须有本地回归测试，并且在适用时有真实 OceanBase 或 OBProxy 验收案例。
7. 不支持的能力必须在 API、连接字符串、文档和测试中保持一致，不能只在深层协议路径中偶发失败。

## 4. 缺口总表

优先级定义：

| 优先级 | 含义 |
| --- | --- |
| P0 | 影响连接正确性、状态隔离、事务安全或核心生产负载；完成前不能宣称完整驱动 |
| P1 | 影响常见 ADO.NET、OceanBase/OBProxy 或 Oracle 生产场景；应进入正式版本计划 |
| P2 | 性能、运维、可选生态和兼容范围增强；核心正确性完成后实施 |

| ID | 优先级 | 模块 | 当前状态 | 目标 |
| --- | --- | --- | --- | --- |
| P0-01 | P0 | 连接池 | 部分实现 | 完成完整 reset、容量/等待/回收和真实行为验收 |
| P0-02 | P0 | 会话隔离 | 部分实现 | 借用/归还确认 autocommit 基线；prepared statement 已按物理会话 generation 失效，未消费 REF CURSOR、活动 LOB 句柄以及可识别的用户 session-state/temporary-object 语句已标记为归还时销毁，仍需完成不可识别状态、reader、XA 和泄漏矩阵 |
| P0-03 | P0 | Ambient transaction | 本地实现 | 打开连接自动 enlist、命令/Batch 绑定同一本地事务、提交/回滚/prepare/in-doubt 状态机和真实故障矩阵；分布式提升明确拒绝 |
| P0-04 | P0 | Unknown outcome | 部分实现 | 连接断开、取消、提交失败后的可判定恢复边界 |
| P0-05 | P0 | 活动连接恢复 | 明确缺失 | 明确的重连、迁移和安全重试策略 |
| P1-01 | P1 | 多语句 | 部分实现 | 默认关闭；显式开启后支持文本多结果，并明确拒绝参数、Prepare、cursor 和 Oracle 输出组合 |
| P1-02 | P1 | 压缩和 OB20 | 部分实现 | MySQL 压缩、OB20 压缩 frame 和 reroute |
| P1-03 | P1 | LOCAL INFILE | 部分实现 | 完成真实服务器/代理互操作、取消和大文件矩阵；保持显式授权和路径安全 |
| P1-04 | P1 | TLS/mTLS | 部分实现 | 客户端证书、信任配置和撤销策略 |
| P1-05 | P1 | ADO.NET 命令行为 | 部分实现 | `SchemaOnly`、`SingleRow`、`SingleResult`、`KeyInfo` 等常用行为 |
| P1-06 | P1 | Oracle 输出和游标 | 部分实现 | 完整参数方向、cursor 列和 PrepareExecute 形状 |
| P1-07 | P1 | 复杂类型 | 部分实现 | 嵌套成员、LOB/cursor/national 成员和缓存失效 |
| P1-08 | P1 | 类型和字符集 | 部分实现 | 扩展元数据、更多字符集和完整类型码 |
| P1-09 | P1 | HA/发现 | 部分实现 | OCP/TNS、健康状态、重试退避和 proxy 拓扑 |
| P1-10 | P1 | Schema 元数据 | 部分实现 | 常用目录集合和参数元数据 |
| P2-01 | P2 | DataAdapter | 明确缺失 | `DbDataAdapter` 和 `DbCommandBuilder`，或正式声明不支持 |
| P2-02 | P2 | Statement cache | 明确缺失 | 跨命令 prepared statement cache |
| P2-03 | P2 | 批处理优化 | 部分实现 | rewrite、multi-send、bulk 和统一 partial-failure 结果 |
| P2-04 | P2 | 诊断 | 部分实现 | 协议 debug、slow-query profile、建连/探活和资源等待 |
| P2-05 | P2 | TFM/生态 | 未验证 | 每个 TFM 的正式支持矩阵和 package consumer 验收 |
| P2-06 | P2 | 真实兼容性 | 未验证 | 版本、OBProxy、TLS、认证、平台和故障矩阵 |

## 5. 分模块要求和验收标准

### 5.1 连接池和会话隔离

当前 `Pooling=true` 已由 [PhysicalSessionPool.cs](../src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs) 管理物理会话；逻辑连接关闭时通过 `COM_RESET_CONNECTION` 清理服务端状态，并用 `COM_INIT_DB` 恢复初始 database。MySQL 模式的 reset 和借用探活都会要求服务端明确返回 `Autocommit` 状态位；缺失该状态时物理会话立即销毁。池已支持最大容量、等待超时、等待取消、最小池预热及失败回滚、借用前 `COM_PING` 存活探测、连接空闲/总生命周期回收、最小空闲保留规则、空池 registry 清理和池生命周期指标；每次 Open 使用独立的取消和 generation，过期的 Open 完成会被拒绝。可识别的用户 `SET`、`ALTER SESSION` 和临时对象语句会让物理会话归还时销毁，避免把无法由当前 reset 证据覆盖的状态交给下一借用者；旧池已 Dispose 时，同一逻辑连接会重新取得 registry 中的新池。Close 打断池等待和真正并发 Close/Open 的资源归属仍需专门回归。完整资源 reset 和真实 OceanBase/OBProxy reset 兼容性仍需验证。

当前已接入的指标名称为 `oceanbase.client.pool.sessions`、`borrowed`、`waiters`、`discarded`、`reset.failures` 和 `wait.duration`，分别记录物理会话数、借用次数、等待者数、销毁次数、reset 失败次数和池等待耗时。它们证明本地生命周期可观测，不证明真实服务器兼容性。

此前走查提出的四个池生命周期风险已对当前代码复核：归还路径只按 total lifetime 判断，闲置候选才按 idle lifetime 判断；每次 `Open` 有独立 cancellation 和 generation，过期完成不会清理新一轮打开；`Close` 会取消同步/异步池等待；空池 registry 会被移除并 Dispose。它们不再作为已确认的当前 bug，但仍需补充 Close/Open 交错、长时间借用、DataSource Dispose 和非空池 shutdown 的压力回归与生命周期报告。

当前已补齐 prepared statement 的跨借用者边界：`COM_RESET_CONNECTION` 成功后递增物理会话 generation；命令缓存的 statement 只有在 session 和 generation 都一致时才复用。旧 generation 的 statement 不再发送 `COM_STMT_CLOSE`，因为服务端 reset 已经关闭它。同步和异步池复用回归均覆盖了“准备、执行、关闭、重新借用、重新准备、执行”路径。未消费的 Oracle REF CURSOR 和未释放的 LOB/temporary LOB 句柄会把物理会话标记为归还时销毁，释放最后一个资源后才允许池化 reset。

目标设计必须将“逻辑 `OceanBaseConnection`”和“物理 `PhysicalSession`”分离：

- `Open` 从匹配配置的池租用物理会话；
- `Close` 归还逻辑连接，不能直接关闭健康物理会话；
- reader、LOB、cursor、prepared statement 和 transaction 持有租约；
- 有活动资源时禁止归还；清理失败时直接丢弃物理会话；
- 借用和归还都必须有超时、取消和并发保护；
- source disposal 必须定义池和已借出连接的行为；
- XA 连接必须从普通池隔离，直到 branch 完成或物理连接被丢弃。

reset 最低验收矩阵：

| 场景 | 归还前动作 | 验收 |
| --- | --- | --- |
| 本地事务 | rollback、恢复 autocommit | 下一借用者看到无活动事务 |
| session variables | 恢复 provider 修改过的变量；可识别的用户变更直接销毁会话 | 新借用者获得基线值 |
| ChangeDatabase | 恢复初始 database/schema | 不泄漏上一个借用者的 database |
| prepared statement | reset 后按 generation 失效并重新准备 | 不引用旧 session/database 或已关闭的 statement ID |
| reader/cursor | reader 未读时销毁连接；未释放 REF CURSOR 时归还销毁连接 | 不把未读协议流或活动 cursor 交给下一借用者 |
| LOB/临时对象 | 关闭外部 stream、locator 和 temporary LOB；活动句柄或可识别的临时对象语句销毁连接 | 不把活动 LOB 句柄、临时对象或未知 session 状态交给下一借用者 |
| XA | 禁止普通归还 | branch 状态不会进入普通池 |
| reset 失败 | 记录原因并销毁连接 | 池永远不返回污染会话 |

### 5.2 事务、取消和 unknown outcome

当前显式本地事务和 XA 有实现；ambient transaction 会在打开连接时登记 volatile enlistment，并复用本地事务协议。取消或连接断开时也不能证明服务端是否已经执行完成，提交/回滚响应丢失仍按 unknown outcome 处理。

目标要求：

- 明确 local transaction、ambient transaction 和 XA 的互斥关系；当前本地 ambient 与显式本地/XA 冲突会在发送前拒绝；
- `TransactionScope` 提交、回滚、超时和连接关闭必须有状态机；
- 发送 SQL 后发生取消时，异常必须带有可判定的 unknown-outcome 信息；
- commit/rollback 的响应边界必须保留，不能因为 socket 关闭而错误报告相反结果；
- 重试策略必须按只读、幂等写、非幂等写和事务提交分别定义；
- pool reset 不能自动替应用程序重试未知写操作。

验收必须覆盖：发送前取消、发送后取消、响应中断、提交响应丢失、回滚响应丢失、事务 dispose、连接 dispose 和池归还。

### 5.3 协议、压缩和 OB20

当前只有显式 `Allow Multi Statements=true` 才会协商 `CLIENT_MULTI_STATEMENTS`；默认仍关闭。启用后仅允许纯文本、无参数、无 Prepare、无 server cursor、无 Oracle PrepareExecute 的命令，并要求服务端也声明该 capability；结果通过已有的 `NextResult` 路径消费。`CLIENT_LOCAL_FILES` 也只在显式 `Allow Local Infile=true` 时请求，并走应用流工厂；`CLIENT_COMPRESS` 仍未开放。OB20 已支持部分校验和、extra info 和 trace 结构，但压缩 frame 和 proxy reroute 明确拒绝。

实现要求：

- 多语句默认关闭，启用时必须在连接配置中明确声明；
- 多语句只走文本协议；参数、显式 Prepare、FetchSize/server cursor、Oracle procedure/output 组合必须在发送前拒绝；
- 多结果、错误边界和参数绑定必须能区分每条语句；
- MySQL 压缩协议必须实现分片、压缩/未压缩混合包、序列号和最大包限制；
- OB20 压缩必须实现 header、payload、校验和和 frame 边界；
- proxy reroute 必须实现重路由通知、目标校验和请求生命周期；
- 扩展 resultset metadata 必须按协商能力解析，未知扩展不能静默丢弃；
- 每个新增 capability 都要有拒绝路径和旧服务器兼容测试。

### 5.4 LOCAL INFILE 和大数据上传

当前 .NET 已实现本地协议路径，但只在应用显式授权时启用。`Allow Local Infile=true` 才会在握手中请求 `CLIENT_LOCAL_FILES`；服务端发送 `0xfb` 后，驱动把文件名作为不透明字符串传给 `OceanBaseConnection.LocalInfileStreamFactory`，由应用返回可读 `Stream`。驱动不会把文件名当成本地路径，也不会自行打开文件。

同步和异步命令都以 64 KiB 缓冲区上传，发送空包结束，再读取服务端最终 OK；流工厂缺失、返回 `null`、不可读、读取失败、取消、超过当前 `PacketChannel.MaxPacketSize`（默认 64 MiB）或最终响应不是 OK 时，当前物理会话会按 unknown outcome 处理并销毁，不回连接池。驱动会释放工厂返回的流；应用不应在上传完成前自行关闭该流。

目标要求：

- 默认拒绝服务器请求；
- 提供显式授权和应用提供的 `Stream`，不允许驱动自行读取任意本地路径；
- 限制请求文件名、大小、读取消耗和取消行为；
- 不能在 batch、事务和连接池 reset 中留下未完成上传；
- 真实环境验证服务器请求、拒绝路径、取消和连接销毁。

当前结论：本地 loopback 已覆盖 capability、同步/异步上传、文件名传递、空包终止、最终 OK、工厂错误、读失败和取消；真实 OceanBase/OBProxy 的 `LOAD DATA LOCAL INFILE`、代理策略、超过 64 MiB 的文件、服务器错误和中断恢复仍未验证。LOCAL INFILE 不是任意二进制参数上传的替代品，普通参数流和该协议的授权边界分别维护。

### 5.5 TLS、认证和凭据

当前 TLS 使用 `SslStream`，默认 `VerifyFull`，但没有客户端证书、信任根、自定义验证和撤销策略配置；GSSAPI 仅有现代 TFM 代码，真实 KDC 尚未验证。

目标要求：

- 支持客户端证书和私钥的安全加载；
- 支持企业 CA、系统证书库和可审计的自定义验证；
- 撤销检查必须可配置，默认策略必须有明确文档；
- `Required` 模式必须继续明确表示“不验证身份”，不能被隐式使用；
- native、old、clear、SHA-2、Ed25519、PAM、GSSAPI 都要有成功和拒绝矩阵；
- 凭据来源不能只依赖明文连接字符串，应允许应用注入安全凭据提供者。

### 5.6 ADO.NET 公共契约

当前 `DbConnection`、`DbCommand`、`DbParameter`、`DbDataReader`、`DbProviderFactory` 已有基础实现。`SingleRow` 会只返回当前结果集的第一行并消费剩余行，`SingleResult` 会消费后续结果以保持连接可复用；`SchemaOnly` 支持带空白或注释前缀的 `SELECT`、`SHOW`、`DESCRIBE`、`DESC`、`EXPLAIN` 和 `VALUES` 文本查询，返回列元数据并消费服务端行，不执行写语句；`KeyInfo`、`DbDataAdapter` 和 `DbCommandBuilder` 仍未实现。

目标要求：

- 支持常用 `CommandBehavior`，或在文档中明确 provider 级别限制；当前 `CloseConnection`、`SequentialAccess`、`SingleRow` 和 `SingleResult` 已有本地回归；
- `SchemaOnly` 只接受明确的只读文本前缀；存储过程、写语句和无法识别的前缀必须在发送前拒绝；
- `SingleRow` 和 `SingleResult` 必须正确结束或取消服务端结果流；
- 完善 `GetSchemaTable`、`GetColumnSchema`、prepared metadata 和参数 metadata；
- 明确是否支持 `DbDataAdapter`、命令生成、更新回写和设计时工具；
- 每个目标 TFM 都要验证相同 API 的编译和运行时行为。

### 5.7 Oracle 扩展和复杂类型

当前 Oracle 功能覆盖较多，但限制集中在输出形状、游标、复杂成员和缓存生命周期。

目标要求：

- REF CURSOR 支持 Output、ReturnValue、Input、InputOutput 和 cursor-valued columns；
- PrepareExecute 输出路径支持符合契约的 prepare、fetch 和多结果组合；
- complex object/collection 支持嵌套 object、LOB、cursor、national 成员和合法的递归检测；
- 类型描述必须绑定 connection/session/transaction generation；
- 外部 DDL 后的缓存失效可以显式执行，也应提供可预测的自动失效边界；
- 服务端不支持的 nested-table column 必须和驱动不支持的 procedure parameter 分开记录。

### 5.8 类型、字符集和元数据

当前类型映射覆盖大量常用值，但遇到未识别 type code、扩展 metadata、非支持字符集或超出精度边界时会失败。

目标要求：

- 建立 MySQL/Oracle 模式的类型码清单；
- 为每个类型定义 CLR 值、provider-specific 值、参数绑定和 NULL 行为；
- 覆盖更多字符集和排序规则，并禁止静默替换导致数据损失；
- 对 decimal、timestamp、interval 和 zero-date 保持精度和范围契约；
- 对未知类型提供稳定、可诊断的错误，而不是在 reader 深处出现模糊失败。

### 5.9 HA、发现和恢复

当前只支持配置地址、RoundRobin 和注入式 `IOceanBaseAddressResolver`。Open 阶段可在 TCP 连接失败时切换候选地址，认证和会话建立后不做透明迁移。

目标要求：

- OCP/TNS 或其他 OceanBase 服务发现适配器；
- 地址、租户和认证身份绑定校验；
- 连接建立前的重试预算和退避；
- 活动连接断线后的明确状态，不自动迁移未完成事务；
- 只读安全重试与写操作 unknown outcome 的不同策略；
- 直连和 OBProxy 的路由、重试、reroute、故障恢复矩阵。

### 5.10 Schema、批处理、缓存和诊断

当前只提供六类 schema collection，batch 顺序执行，prepared statement 主要限于命令生命周期，诊断能力已覆盖池生命周期，但还没有覆盖协议 debug 和 slow-query profile。

目标要求：

- 增加常用 Tables、Views、Indexes、Keys、Constraints、Types、Sequences、Users 和 Synonyms 元数据；
- 过程元数据能够安全映射到参数描述，但不能把不完整目录信息误当成完整签名；
- 实现有上限和淘汰策略的跨命令 statement cache；
- 为 batch 提供 rewrite、multi-send、bulk 或 array binding 的模式化选择；
- 暴露连接池总数、借出数、等待数、丢弃数、reset 失败数和连接建立耗时；
- 诊断 SQL 必须脱敏，能够区分等待、网络、服务器错误和 unknown outcome。

## 6. 实施依赖和阶段

不能先单独实现连接池再补 reset。推荐按以下阶段推进：

| 阶段 | 内容 | 前置条件 | 退出标准 |
| --- | --- | --- | --- |
| G0 | 冻结公共 API、状态机、错误分类和兼容矩阵 | 当前文档和架构设计评审 | 每个缺口有 owner、状态和验收案例 |
| G1 | 物理会话租约、reset、池、XA 隔离 | G0 | 池借用/归还/失败丢弃/并发隔离测试通过 |
| G2 | Ambient transaction、取消和 unknown outcome | G1 | 本地 `TransactionScope` 提交/回滚 loopback 回归通过；提交丢失、取消和真实故障矩阵仍待完成 |
| G3 | 多语句、压缩、LOCAL INFILE、OB20 reroute/压缩 | G0 | capability 协商、旧服务器拒绝和真实服务器通过 |
| G4 | TLS/mTLS、认证插件、HA 和发现 | G0 | TLS/KDC/OBProxy/故障注入矩阵通过 |
| G5 | ADO.NET 行为、Oracle 输出、复杂类型、类型字符集 | G0 | 公共 API 和模式差异测试通过 |
| G6 | Schema、statement cache、batch 优化、诊断 | G1、G5 | 负载、资源、性能和可观测性基线通过 |
| G7 | 跨 TFM、跨平台和发布验收 | G1-G6 | 所有声明支持的矩阵有可复现报告 |

## 7. 测试和验收矩阵

### 7.1 本地回归

- 同步和异步路径必须成对测试；
- 每个连接池 reset 项必须有“污染前”和“下一借用者观察到的结果”；
- 每个协议 capability 必须有启用、禁用、服务器不支持和畸形包测试；
- 每个参数方向和结果形状必须有 NULL、错误、取消和连接关闭测试；
- 不允许只用 mock 证明真实服务器 SQL、锁、目录、拓扑或认证行为。

### 7.2 真实 OceanBase

至少需要：

| 维度 | 最低矩阵 |
| --- | --- |
| 模式 | MySQL、Oracle |
| 路径 | 直连、OBProxy |
| TLS | Disabled、VerifyFull、Required、mTLS |
| 认证 | native、SHA-2、Ed25519、PAM、GSSAPI（适用时） |
| 版本 | 当前部署版本、最低目标版本、最新验证版本 |
| 负载 | 短连接、池复用、并发事务、长 reader、LOB、大批量 |
| 故障 | TCP 失败、TLS 失败、认证失败、响应中断、提交响应丢失、proxy reroute |
| 运行时 | Windows/.NET 8/9/10、Linux/.NET 8/9/10 |

### 7.3 发布阻塞条件

以下条件未完成前，版本只能标记为实验性：

1. 连接池 reset 和 cross-borrower isolation 未通过；
2. Oracle 真实验收仍有未分类失败；
3. OBProxy 和至少一个真实 TLS profile 未通过；
4. unknown outcome、XA crash recovery 和活动连接故障边界没有报告；
5. 声明支持的 TFM 没有 package consumer 和运行时证据；
6. 文档、连接字符串、异常和测试对同一能力的状态不一致。

## 8. 明确不纳入第一版的内容

为了避免“完整驱动”变成无边界目标，以下内容可以在第一版明确排除，但必须写入公开兼容性文档：

- 原生 MySQL 或原生 Oracle 服务器兼容；
- 自动重放所有未知结果的 SQL；
- 连接关闭时自动完成未完成 XA branch；
- 不受限制的本地文件读取；
- 把服务端不支持的 Oracle nested-table column 伪装成驱动已支持；
- 没有真实服务器证据的版本、OBProxy、KDC 或跨平台支持声明。

排除不等于静默失败。必须在连接字符串、公开 API、异常消息、README、兼容性矩阵和测试中保持一致。

## 9. 当前证据和限制

- 当前记录的真实数据库结果主要是 Windows/.NET 10、直连 OceanBase 4.4.2.2、TLS disabled。
- MySQL 模式记录为 60 个通过；Oracle 模式为 79 个通过、1 个失败，失败与测试服务器的 nested-table column 能力有关，但仍需保留为验收失败记录。
- OBProxy、其他 OceanBase 版本、完整 TLS/plugin、真实 Kerberos/KDC、网络故障、XA coordinator crash recovery 和完整非 Windows 矩阵仍未闭合。
- 本文是基于当前源码、测试、文档和本地 Connector/J 参考源码的能力审计，不是对每个字节编解码器的逐行证明。

## 10. Java 对照后的模块缺口

下表把 Connector/J 的主要职责和当前 .NET 状态对齐。Java 模块名称用于定位能力，不要求 .NET 复制 Java 类名；.NET 应按 ADO.NET 的生命周期和 API 习惯实现同等契约。

| Connector/J 参考模块 | .NET 当前对应物 | 对照结论 |
| --- | --- | --- |
| M01 Driver/DataSource/JDBC | `OceanBaseProviderFactory`、`OceanBaseConnection`、`OceanBaseDataSource` | 基础入口存在；`DbDataAdapter`、`DbCommandBuilder` 等生态仍缺失 |
| M02 URL/options/address/discovery | `OceanBaseConnectionStringBuilder`、`EndpointSelector`、`IOceanBaseAddressResolver` | 多地址和注入 resolver 存在；OCP/TNS、缓存和健康发现缺失 |
| M03 transport/TLS/auth/credential SPI | `HandshakeFlow`、`TlsHandshakeFlow`、认证实现 | 多种认证和 TLS 路径存在；mTLS、企业信任/撤销和真实 KDC 未闭合 |
| M04 handshake/mode/capability | `HandshakeFlow`、`NegotiatedCapabilities` | 模式和部分能力协商存在；多语句和 LOCAL INFILE 已有 opt-in 路径，压缩等 capability 未开放 |
| M05 session/transaction/cancel | `PhysicalSession`、`OceanBaseTransaction`、`AmbientEnlistment` | 本地事务、ambient enlistment、取消和 unknown outcome 有路径；活动连接迁移缺失 |
| M06 query/prepare/cursor | `OceanBaseCommand`、`PreparedStatement`、`QueryResult` | 基础文本/server prepare 和 cursor 有路径；statement cache、完整 CommandBehavior 未闭合 |
| M07 binding/sql parser/batch | `ParameterBindingPlan`、`OracleArrayBindingPlan`、`OceanBaseBatch` | 参数和数组绑定有路径；rewrite、multi-send、bulk 优化缺失 |
| M08 result/rows/multiple results | `OceanBaseDataReader`、`QueryResult` | 流式读取和多结果有路径；所有结果形状和资源边界仍需矩阵验证 |
| M09 types/charset/time | `Internal/Values`、`OceanBaseDbType` | 覆盖较广；扩展 type code、字符集、精度和跨模式边界仍有限制 |
| M10 LOB/complex | `OceanBaseLob`、`OceanBaseComplexType` | 基础 LOB/复杂类型存在；嵌套 LOB/cursor/national 成员和缓存失效缺失 |
| M11 metadata/catalog | `OceanBaseConnection.Schema`、reader schema | 仅六类 Schema collection；完整目录和参数元数据缺失 |
| M12 HA/failover | `EndpointSelector`、地址 resolver | Open 阶段候选地址重试存在；黑名单、退避、活动连接恢复和 reroute 缺失 |
| M13 pooling/reuse | `PhysicalSessionPool` | 复用、reset、容量、等待取消、生命周期、最小池预热及失败回滚、空池 registry 清理和池指标已实现；完整资源 reset 和真实 reset 兼容性未闭合 |
| M14 XA | `OceanBaseXaResource`、`OceanBaseXid` | 显式 XA 生命周期和 recover 有路径；外部 coordinator 崩溃恢复未验证 |
| M15 logging/diagnostics/OB20 | `DriverDiagnostics`、OB20 protocol classes | 日志、Activity、Meter、池指标和基础 OB20 存在；协议 debug、压缩/reroute 缺失 |
| M16 packaging/SPI/tests | 多 TFM csproj、消费者和集成套件 | 包和消费者验证存在；跨平台、真实版本/代理/认证矩阵未闭合 |

## 11. 源码证据索引

以下路径是后续实施和复核的入口；行号随代码变化，引用文件和类型比固定行号更稳定。

| 结论 | 主要源码/测试入口 |
| --- | --- |
| 物理会话池、复用、reset 和 XA 隔离 | `src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs`、`PhysicalSession.cs`、`tests/OceanBaseConnector.UnitTests/Session/PhysicalSessionPoolTests.cs`、`tests/OceanBaseConnector.UnitTests/PoolingTests.cs` |
| 连接关闭时归还或销毁会话 | `src/OceanBaseConnector/OceanBaseConnection.cs`、`src/OceanBaseConnector/OceanBaseDataSource.cs` |
| ambient transaction 本地实现 | `src/OceanBaseConnector/OceanBaseConnection.cs`、`OceanBaseConnection.Transactions.cs`、`src/OceanBaseConnector/Internal/Session/AmbientEnlistment.cs`、`tests/OceanBaseConnector.UnitTests/TransactionTests.cs` |
| CommandBehavior 和不支持的命令形状 | `src/OceanBaseConnector/OceanBaseCommand.cs`、`src/OceanBaseConnector/OceanBaseBatch.cs` |
| Schema collection 范围 | `src/OceanBaseConnector/OceanBaseConnection.Schema.cs`、`Schema.Routines.cs` |
| capability 位和未开放协议能力 | `src/OceanBaseConnector/Internal/Protocol/CapabilityFlags.cs`、`OceanBaseCapabilityFlags.cs`、`src/OceanBaseConnector/Internal/Session/HandshakeFlow.cs` |
| Oracle 输出、LOB、复杂类型和数组绑定 | `src/OceanBaseConnector/OceanBaseConnection.Xa.cs`、`OceanBaseLob*.cs`、`OceanBaseComplex*.cs`、`OceanBaseCommand.ArrayBinding.cs` |
| 类型、精度、时间和字符集限制 | `src/OceanBaseConnector/Internal/Values/*.cs`、`tests/OceanBaseConnector.UnitTests/*Type*Tests.cs`、`ZeroDateTests.cs`、`WideDecimalTests.cs` |
| 真实数据库和代理验收入口 | `tests/OceanBaseConnector.Acceptance/`、`tests/OceanBaseConnector.IntegrationTests/`、`docs/compatibility.md` |

## 12. 交付前的最小工作清单

这份清单是“完整驱动”可以对外发布前的最低闭环，不是所有可能的增强功能：

1. 完成可观测指标；补齐所有会话资源的 reset/丢弃规则。
2. 补齐 ambient transaction 的超时、提交/回滚响应丢失、连接关闭和真实故障测试；当前本地 enlistment 已实现，分布式提升仍明确不支持。
3. 完成多语句和 LOCAL INFILE 的真实互操作验收，再实现 MySQL 压缩、OB20 压缩和 proxy reroute；未实现项必须保持显式拒绝。
4. 完成 mTLS、企业信任/撤销、认证插件和 HA/发现的可配置契约及真实矩阵。
5. 补齐 ADO.NET 命令行为、Oracle 输出/游标、复杂类型、字符集、Schema 和 statement cache 的公开契约。
6. 为每个声明支持的 TFM、OceanBase 版本、直连/OBProxy、MySQL/Oracle 模式和 TLS profile 生成可复现报告。

在第 1、2、4 和 6 项没有闭合前，README 和 NuGet 元数据应继续使用 `experimental`/“未完成真实兼容性验证”的表述。

## 13. 最近已落地的走查修复

以下内容已经有源码和本地回归证据，但仍不等于真实 OceanBase/OBProxy 兼容性：

| 提交 | 已落地内容 | 仍未闭合 |
| --- | --- | --- |
| `837e37c` | 连接池生命周期指标 | 真实服务器指标和长期运行报告 |
| `4832bcd` | reset 后 prepared statement 按物理会话 generation 失效 | 真实 reset 对 statement 的行为 |
| `1e922c3` | 可识别用户 session-state/temporary-object 语句导致物理会话销毁 | 存储过程、动态 SQL 和不可识别状态 |
| `3dd57fb` | 活动 LOB/temporary LOB 归还时销毁物理会话 | 真实服务器 LOB 清理 |
| `1fec7b9` | 活动 REF CURSOR 归还时销毁物理会话 | 真实服务器 cursor 清理 |
| `0e4162e` | 只读文本 `SchemaOnly` 元数据行为 | `KeyInfo` 和真实目录元数据 |
| `0b39a62` | 本地 ambient `TransactionScope` enlistment、命令/Batch 事务绑定 | 分布式提升、响应丢失和真实 coordinator 故障 |

相关文档：

- [Compatibility and known gaps](compatibility.md)
- [Verification status and remaining work](obconnector-dotnet-progress.md)
- [ADO.NET API reference](obconnector-dotnet-architecture-design.md)
- [Connector/J architecture outline](obconnector-j-architecture-outline.md)
