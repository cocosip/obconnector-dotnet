# OceanBaseConnector .NET 完整驱动能力与缺口基线

**审计日期：** 2026-10-24
**源码基线：** .NET `06d58e1`；Java `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`（Connector/J 2.4.14）
**适用仓库：** `D:\dotnet-code\obconnector-dotnet`
**参考实现：** 本仓库源码、测试和兼容性记录；本地 `D:\java-code\obconnector-j` 的 Connector/J 实现
**文档性质：** 能力审计、目标契约和验收基线；不代表尚未完成的功能已经实现

**最近复核：** 2026-10-25；客户端证书池化已由 `01a8082` 交付，本次显式清池以此为起点。本文同步源码与本地验证记录；初次审计基线和历史结果保留原日期，尚未取得的真实验收继续开放。

上述提交是初次审计基线。本文同时记录其后 P0-06 的本地修复及 P1-04 的自定义服务器证书验证，实施与测试结果见 [池路由记录](obconnector-dotnet-progress.md#2026-10-24-p0-06-pooled-resolver-routing)和 [TLS 验证记录](obconnector-dotnet-progress.md#2026-10-25-p1-04-tls-server-validation-callback)。23 个缺口 ID 保持不变；本地实现不关闭尚未取得的真实服务器验收。

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
| .NET 当前有没有连接池？ | 有基础池化，但不是完整连接池。 | 默认 `Pooling=false`，需显式启用。`Pooling=true` 通过 [PhysicalSessionPool.cs](../src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs) 复用已认证物理会话；归还时执行 `COM_RESET_CONNECTION`，MySQL 模式再执行 `COM_INIT_DB`。当前已支持最大容量、等待超时、最小池预热及失败回滚、空闲/总生命周期回收、借用前存活探测、空池 registry 清理和池指标；完整 reset 与真实服务器 reset 兼容性仍未闭合。 |
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

### 1.3 文档使用方式和走查假设

本次按长期运行的 .NET 服务评估：多个请求并发借用连接，同一进程长期复用数据源，会使用事务、流式 reader、LOB 和两种兼容模式。没有约定并发数、延迟或吞吐 SLA，因此本文不编造容量承诺；压力验收须先记录连接上限、并发数、运行时长及资源预算。

- 评审总体差距：先读[走查报告](obconnector-dotnet-driver-audit.md)及本文第 4 节。
- 确定每项工作：按第 14 节的触发场景、源码/测试入口和最低证据拆分；第 5 节补充 API 和模式边界。
- 安排实施和发布：使用第 6、7、12、15 节；负责人、交付日期及承诺支持范围需实际分配，阶段表不是排期承诺。
- 核查已有结果：查[进度台账](obconnector-dotnet-progress.md)和[兼容性记录](compatibility.md)，保留失败和跳过，不用本次源码复核覆盖历史实库报告。

23 项是能力差距，包含已证实缺失、已实现但组合受限、尚待故障/并发验证和实库验收；不是 23 个已复现 bug。发现源码已有保护时，应保留为验证项，而非继续声称存在确定缺陷。

## 2. 当前能力基线

| 模块 | 当前状态 | 主要边界 |
| --- | --- | --- |
| 连接和配置 | 部分实现 | 连接字符串、模式确认、多地址、超时、TLS、地址解析和池容量/等待/生命周期选项已存在；静态池清理和最小池预热已实现，真实兼容性未闭合 |
| ADO.NET 基础对象 | 部分实现 | `DbConnection`、`DbCommand`、`DbParameter`、`DbDataReader`、`DbProviderFactory` 已有；`SingleRow`/`SingleResult` 和按查询前缀过滤的 `SchemaOnly` 已支持，但 SchemaOnly 仍执行查询；`KeyInfo`、`DbDataAdapter` 和 `DbCommandBuilder` 仍缺失 |
| .NET 8+ 资产 | 部分实现 | `DbDataSource`、`DbBatch`、savepoint、异步事务、GSSAPI 只在现代 TFM 可用 |
| 认证 | 部分实现 | native、old、clear、SHA-2、Ed25519、PAM、GSSAPI 有代码；真实插件和 KDC 矩阵未闭合 |
| TLS | 部分实现 | 默认平台 `VerifyFull`；可选 `RemoteCertificateValidationCallback` 已接入并按引用隔离普通池；`ClientCertificate` 支持普通池化/直连及数据源快照，证书按引用和指纹隔离；撤销检查可配置。真实企业 CA/mTLS、轮换和跨平台撤销矩阵未闭合 |
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
| 复杂类型 | 部分实现 | 标量及嵌套 object/collection 有路径；LOB、cursor、national 成员、循环结构和文本结果不支持 |
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
| P0-01 | P0 | 连接池 | 部分实现 | 容量/等待/预热/回收/指标及显式清池已有本地实现；补并发交错、资源上限、长期运行及真实行为验收，完整 reset 与 P0-02 联合关闭 |
| P0-02 | P0 | 会话隔离 | 部分实现 | 借用/归还确认 autocommit 基线；prepared statement 已按物理会话 generation 失效，未消费 REF CURSOR、活动 LOB 句柄以及可识别的用户 session-state/temporary-object 语句已标记为归还时销毁，仍需完成不可识别状态、reader、XA 和泄漏矩阵 |
| P0-03 | P0 | Ambient transaction | 本地实现 | 打开连接自动 enlist、命令/Batch 绑定同一本地事务、提交/回滚/prepare/in-doubt 状态机和真实故障矩阵；分布式提升明确拒绝 |
| P0-04 | P0 | Unknown outcome | 部分实现 | 连接断开、取消、提交失败后的可判定恢复边界 |
| P0-05 | P0 | 活动连接恢复 | 部分实现 | 先闭合断线状态与应用重建连接契约；透明迁移和自动重试另行定义范围 |
| P0-06 | P0 | 池的路由身份隔离 | 本地实现；真实拓扑未验证 | resolver 引用身份隔离、每次 Open 路由快照及旧端点闲置会话清理已有回归；真实拓扑/故障矩阵待验收 |
| P1-01 | P1 | 多语句 | 部分实现 | 默认关闭；显式开启后支持文本多结果，并明确拒绝参数、Prepare、cursor 和 Oracle 输出组合 |
| P1-02 | P1 | 压缩和 OB20 | 部分实现 | MySQL 压缩、OB20 压缩 frame 和 reroute |
| P1-03 | P1 | LOCAL INFILE | 部分实现 | 完成真实服务器/代理互操作、取消和大文件矩阵；保持显式授权和路径安全 |
| P1-04 | P1 | TLS/mTLS | 部分实现 | 自定义服务器证书回调、客户端证书池化身份隔离和数据源证书快照已有本地实现（第 15.4 节）；真实企业 CA/mTLS、轮换和跨平台撤销矩阵仍缺 |
| P1-05 | P1 | ADO.NET 命令行为 | 部分实现 | `SchemaOnly`、`SingleRow`、`SingleResult`、`KeyInfo` 等常用行为 |
| P1-06 | P1 | Oracle 输出和游标 | 部分实现 | 完整参数方向、cursor 列和 PrepareExecute 形状 |
| P1-07 | P1 | 复杂类型 | 部分实现 | 已有嵌套 object/collection；补齐 LOB/cursor/national 成员和外部 DDL 缓存失效 |
| P1-08 | P1 | 类型和字符集 | 部分实现 | 扩展元数据、更多字符集和完整类型码 |
| P1-09 | P1 | HA/发现 | 部分实现 | OCP/TNS、健康状态、重试退避和 proxy 拓扑 |
| P1-10 | P1 | Schema 元数据 | 部分实现 | 常用目录集合和参数元数据 |
| P1-11 | P1 | 凭据提供与轮换 | 明确缺失 | 建连时获取凭据、刷新失败和旧池身份失效的公开契约 |
| P2-01 | P2 | DataAdapter | 明确缺失 | `DbDataAdapter` 和 `DbCommandBuilder`，或正式声明不支持 |
| P2-02 | P2 | Statement cache | 明确缺失 | 跨命令 prepared statement cache |
| P2-03 | P2 | 批处理优化 | 部分实现 | rewrite、multi-send、bulk 和统一 partial-failure 结果 |
| P2-04 | P2 | 诊断 | 部分实现 | 协议 debug、slow-query profile、建连/探活和资源等待 |
| P2-05 | P2 | TFM/生态 | 未验证 | 每个 TFM 的正式支持矩阵和 package consumer 验收 |
| P2-06 | P2（扩展矩阵）/ 发布阻塞（承诺范围） | 真实兼容性 | 部分验证 | 承诺范围必须有真实版本、OBProxy、TLS、认证、平台和故障报告，扩展范围再分期 |

## 5. 分模块要求和验收标准

### 5.1 连接池和会话隔离

当前 `Pooling=true` 已由 [PhysicalSessionPool.cs](../src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs) 管理物理会话；逻辑连接关闭时通过 `COM_RESET_CONNECTION` 清理服务端状态，并用 `COM_INIT_DB` 恢复初始 database。MySQL 模式的 reset 和借用探活都会要求服务端明确返回 `Autocommit` 状态位；缺失该状态时物理会话立即销毁。池已支持最大容量、等待超时、等待取消、最小池预热及失败回滚、借用前 `COM_PING` 存活探测、连接空闲/总生命周期回收、最小空闲保留规则、空池 registry 清理和池生命周期指标；每次 Open 使用独立的取消和 generation，过期的 Open 完成会被拒绝。可识别的用户 `SET`、`ALTER SESSION` 和临时对象语句会让物理会话归还时销毁，避免把无法由当前 reset 证据覆盖的状态交给下一借用者；旧池已 Dispose 时，同一逻辑连接会重新取得 registry 中的新池。Closed 回调更换配置并重开的池归属已由 `06d58e1` 修复并有回归；真正多线程 Close/Open 和等待取消交错仍需压力验证。完整资源 reset 和真实 OceanBase/OBProxy reset 兼容性仍需验证。

当前已接入的指标名称为 `oceanbase.client.pool.sessions`、`borrowed`、`waiters`、`discarded`、`reset.failures` 和 `wait.duration`，分别记录物理会话数、借用次数、等待者数、销毁次数、reset 失败次数和池等待耗时。它们证明本地生命周期可观测，不证明真实服务器兼容性。

显式清理入口为所有资产的 `OceanBaseConnection.ClearPool(connection)` / `ClearAllPools()`，以及现代 `OceanBaseDataSource.Clear()`。清池关闭闲置会话，借出会话不被中断、归还时销毁；清理前在途建连/预热也不能把旧代次会话留作后续复用。ClearAllPools 只处理普通 registry 池，私有源需显式 Clear。详见 [清池 API 契约](obconnector-dotnet-architecture-design.md#explicit-pool-clearing)。

此前走查提出的四个池生命周期风险已对当前代码复核：归还路径只按 total lifetime 判断，闲置候选才按 idle lifetime 判断；每次 `Open` 有独立 cancellation 和 generation，过期完成不会清理新一轮打开；`Close` 会取消同步/异步池等待；空池 registry 会被移除并 Dispose。数据源或池 Dispose 后，已经借出的会话在随后关闭时会直接销毁，不再发送 reset 网络请求；同步和异步回归已覆盖该资源归属。仍需补充 Close/Open 交错、长时间借用和非空池 shutdown 的压力回归与生命周期报告。

Closed `StateChange` 回调可能更换连接配置并立即重新打开，因此 `Close` 在锁内分离旧会话时会同时保存原池、registry key 和命令超时；同步/异步归还不再读取回调后的新配置。Open 失败清理也保存其原 registry key。同步/异步与旧/新连接池化开关的 8 个组合，以及更换配置后空池移除的 2 个回归已覆盖该隔离；这不替代多线程压力、活动事务和真实数据库矩阵。

当前已补齐 prepared statement 的跨借用者边界：`COM_RESET_CONNECTION` 成功后递增物理会话 generation；命令缓存的 statement 只有在 session 和 generation 都一致时才复用。旧 generation 的 statement 不再发送 `COM_STMT_CLOSE`，因为服务端 reset 已经关闭它。同步和异步池复用回归均覆盖了“准备、执行、关闭、重新借用、重新准备、执行”路径。未消费的 Oracle REF CURSOR 和未释放的 LOB/temporary LOB 句柄会把物理会话标记为归还时销毁，释放最后一个资源后才允许池化 reset。

目标设计必须将“逻辑 `OceanBaseConnection`”和“物理 `PhysicalSession`”分离：

- `Open` 从匹配配置的池租用物理会话；
- `Close` 归还逻辑连接，不能直接关闭健康物理会话；
- reader、LOB、cursor、prepared statement 和 transaction 持有租约；
- 有活动资源时禁止归还；清理失败时直接丢弃物理会话；
- 借用和归还都必须有超时、取消和并发保护；
- source disposal 必须定义池和已借出连接的行为；
- XA 连接必须从普通池隔离，直到 branch 完成或物理连接被丢弃。

**P0-06 路由身份边界：** 早期走查发现普通全局池只按连接字符串索引，B 的不同 resolver 能取得 A 的健康会话；已用同步/异步回归复现并修复。当前 registry 按原始连接字符串和 resolver 的引用身份隔离，不受 resolver 覆盖的 `Equals`/`GetHashCode` 影响；更换关闭连接的 resolver 会选择对应池。每次池化逻辑 Open 只解析一次，快照用于闲置候选检查、最小池预热和新会话建连。已不在快照中的闲置 host/port 会话直接销毁，不能用最小池保留规则绕过；已借出的会话继续当前工作，之后按下一借用者的快照检查。相同主机名背后的 DNS IP 变化不在 host/port 成员检查范围。

发现失败或取消不得回退到池内旧会话/配置地址；自定义 resolver 的池化 Open 以 `Connection Timeout` 统一限制发现、等待、预热、探活与握手。异步 resolver 迟到的结果不会被用于建连。registry 领取池时会原子登记在途 Rent，防止其他发现失败错误清理仍有请求的空池；清理始终使用原池持有的 resolver 身份。现代数据源仍保留各自私有池；同一 resolver 的普通连接可共享对应池。每次 Open 使用自己的地址快照，不承诺并发发现结果的全局版本排序，也不迁移活动事务或 replay SQL。真实发现服务、DNS/代理/节点故障和跨平台证据仍待补齐。

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

Oracle 匿名 `BEGIN`/`DECLARE` 块（包括 label）和 `CREATE [OR REPLACE] [EDITIONABLE|NONEDITIONABLE]` 的 procedure/function/package/trigger/type 程序单元按单个 SQL 请求发送，内部的分号不要求多语句 capability；程序单元完整语法和非法尾部仍由服务端解析，驱动不拆分或重写 PL/SQL。普通 SQL 的边界检查按当前兼容模式识别引号和注释：Oracle 使用 doubled quote、`q`/`nq` 引号和无空白限制的 `--`，反斜杠和 `#` 不按 MySQL 转义/注释处理。同步/异步 loopback 和现有 OUT/temporary LOB 回归提供本地证据，真实服务器程序单元及非法尾部验收仍需执行。

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

当前 TLS 使用 `SslStream`，默认 `VerifyFull`。`OceanBaseConnection.ClientCertificate` 可在打开前注入客户端证书；为了避免证书身份跨池边界，池按证书引用及 SHA-256 DER 指纹隔离，并持有内部副本；普通连接每次 Open 要求原证书对象有效且带私钥。现代 builder 的 `UseClientCertificate` 在 Build 时捕获副本，即使 Pooling=false，Build 后释放原证书也不影响该源。`Check Certificate Revocation=true` 会把平台撤销检查传给 TLS 握手，默认关闭。`Required` 仍然只提供加密，不验证服务端身份；`VerifyFull` 使用平台信任库和主机名校验。GSSAPI 仅有现代 TFM 代码，真实 KDC 尚未验证。

**自定义验证的当前契约：** 五个资产均可通过 `OceanBaseConnection.RemoteCertificateValidationCallback` 设置 BCL 回调；现代数据源通过 builder 的 `UseRemoteCertificateValidationCallback` 固定策略。null 保留平台校验，非 null 替代最终校验决策，可由应用实现私有 CA 验证或证书固定，必须处理证书链、主机名、有效期及撤销错误。回调只允许 `VerifyFull`，其他模式在联网前拒绝。普通池按回调引用身份隔离，预热和新物理会话沿用该策略，拒绝/抛异常均在数据库认证前终止且不换端点；复用会话不重复 TLS 验证。捕获状态变化不自动失效既有会话，策略退休需重建数据源并关闭/释放旧源及其借出连接。详见 [TLS API 契约](obconnector-dotnet-architecture-design.md#tls-server-validation)。这仍不构成真实企业 CA 或 mTLS 验收。

**证书所有权和轮换：** 调用方原证书始终由调用方管理，池和数据源只释放内部副本。source 连接的配置固定，getter 返回原配置引用，握手使用内部副本；Open/Build 时不得并发修改原对象。复用会话不重新验证证书。普通连接可用 ClearPool/ClearAllPools 清理旧身份；私有数据源用 Clear 或释放旧源。清池关闭闲置会话，借出会话完成当前工作后在归还时销毁；清池不替换配置，换证书仍需新身份/新源。生命周期自动回收仍是惰性的，长期运行和真实验收继续由 P0-01 跟踪。详见 [客户端证书契约](obconnector-dotnet-architecture-design.md#tls-client-certificates)。

目标要求：

- 支持客户端证书和私钥的安全加载；当前由应用加载并注入 `X509Certificate2`；应用管理原对象，池/数据源管理内部副本；
- 支持企业 CA、系统证书库和可审计的自定义验证；
- 撤销检查已经有 `Check Certificate Revocation` 配置入口；仍需验证各目标平台的撤销语义和失败分类；
- `Required` 模式必须继续明确表示“不验证身份”，不能被隐式使用；
- native、old、clear、SHA-2、Ed25519、PAM、GSSAPI 都要有成功和拒绝矩阵；
- 凭据来源不能只依赖明文连接字符串，应允许应用注入安全凭据提供者。

**P1-11 凭据轮换：** 应用目前可以自行读取密钥服务后构建连接字符串；这不等于驱动已经提供建连时凭据回调。后续须定义凭据在新物理连接/池复用时的获取时点、过期和刷新失败、日志脱敏及旧身份会话的失效规则。不要把 token 本身放入可观测池标识；分期实施时需明确要求应用重建数据源的替代流程。

### 5.6 ADO.NET 公共契约

当前 `DbConnection`、`DbCommand`、`DbParameter`、`DbDataReader`、`DbProviderFactory` 已有基础实现。`SingleRow` 会只返回当前结果集的第一行并消费剩余行，`SingleResult` 会消费后续结果以保持连接可复用；`SchemaOnly` 接受带空白或注释前缀的 `SELECT`、`SHOW`、`DESCRIBE`、`DESC`、`EXPLAIN` 和 `VALUES` 文本查询，执行请求后隐藏并消费行，仅向调用方提供元数据。前缀检查会拒绝直接的写语句，但不能证明 SELECT 中的函数无副作用，也不避免查询、锁或大结果传输成本；它尚不是严格的“不执行查询”元数据契约。`KeyInfo`、`DbDataAdapter` 和 `DbCommandBuilder` 仍未实现；`DbBatch` 也只接受 `CloseConnection` 和 `SequentialAccess`。

目标要求：

- 支持常用 `CommandBehavior`，或在文档中明确 provider 级别限制；当前 `CloseConnection`、`SequentialAccess`、`SingleRow` 和 `SingleResult` 已有本地回归；
- `SchemaOnly` 应限制为经验证的元数据获取路径；当前仅过滤可识别的查询前缀，尚不能保证完整 SQL 无副作用；
- 补齐 `SchemaOnly` 的元数据获取路径，或明确公开其执行查询的限制；用带可观察副作用的函数、锁和大结果验证，不能以 `Read=false` 证明查询没有执行；
- `SingleRow` 和 `SingleResult` 必须正确结束或取消服务端结果流；
- 完善 `GetSchemaTable`、`GetColumnSchema`、prepared metadata 和参数 metadata；
- 明确是否支持 `DbDataAdapter`、命令生成、更新回写和设计时工具；
- 每个目标 TFM 都要验证相同 API 的编译和运行时行为。

### 5.7 Oracle 扩展和复杂类型

当前 Oracle 功能覆盖较多，但限制集中在输出形状、游标、复杂成员和缓存生命周期。

嵌套 object/collection 已由 `ComplexType`、`ComplexValueCodec` 的递归路径和 `ComplexTypeDictionaryTests`、`ComplexParameterTests`、`ComplexResultTests` 覆盖；不能再把“所有嵌套成员”列为缺失。当前明确拒绝的是 LOB/cursor/national 等成员、循环类型及超限布局。真实服务端结果形状仍需分别验证。

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

- 保留已有 `MetaDataCollections`、`DataSourceInformation`、`Tables`、`Columns`、`Procedures` 和 `ProcedureParameters` 六个集合；增加独立的 Views、Indexes、Keys、Constraints、Types、Sequences、Users 和 Synonyms 等目录能力，并定义模式差异；
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
| M03 transport/TLS/auth/credential SPI | `HandshakeFlow`、`TlsHandshakeFlow`、认证实现 | 多种认证、池化/直连客户端证书、数据源证书快照、撤销检查和自定义服务器验证回调已接入；真实企业 CA/KDC/mTLS、凭据轮换仍未闭合 |
| M04 handshake/mode/capability | `HandshakeFlow`、`NegotiatedCapabilities` | 模式和部分能力协商存在；多语句和 LOCAL INFILE 已有 opt-in 路径，压缩等 capability 未开放 |
| M05 session/transaction/cancel | `PhysicalSession`、`OceanBaseTransaction`、`AmbientEnlistment` | 本地事务、ambient enlistment、取消和 unknown outcome 有路径；活动连接迁移缺失 |
| M06 query/prepare/cursor | `OceanBaseCommand`、`PreparedStatement`、`QueryResult` | 基础文本/server prepare 和 cursor 有路径；statement cache、完整 CommandBehavior 未闭合 |
| M07 binding/sql parser/batch | `ParameterBindingPlan`、`OracleArrayBindingPlan`、`OceanBaseBatch` | 参数和数组绑定有路径；rewrite、multi-send、bulk 优化缺失 |
| M08 result/rows/multiple results | `OceanBaseDataReader`、`QueryResult` | 流式读取和多结果有路径；所有结果形状和资源边界仍需矩阵验证 |
| M09 types/charset/time | `Internal/Values`、`OceanBaseDbType` | 覆盖较广；扩展 type code、字符集、精度和跨模式边界仍有限制 |
| M10 LOB/complex | `OceanBaseLob`、`OceanBaseComplexType` | 基础 LOB 及嵌套 object/collection 存在；LOB/cursor/national 成员和外部 DDL 缓存失效未闭合 |
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
| 显式清池、旧代次销毁和容量唤醒 | `src/OceanBaseConnector/OceanBaseConnection.Pooling.cs`、`Internal/Session/PhysicalSessionPool.cs`、`OceanBaseDataSource.cs`；`tests/OceanBaseConnector.UnitTests/PoolClearingTests.cs`、`tests/OceanBaseConnector.ConsumerTests/PoolClearingConsumerTests.cs` |
| ambient transaction 本地实现 | `src/OceanBaseConnector/OceanBaseConnection.cs`、`OceanBaseConnection.Transactions.cs`、`src/OceanBaseConnector/Internal/Session/AmbientEnlistment.cs`、`tests/OceanBaseConnector.UnitTests/TransactionTests.cs` |
| CommandBehavior 和不支持的命令形状 | `src/OceanBaseConnector/OceanBaseCommand.cs`、`src/OceanBaseConnector/OceanBaseBatch.cs` |
| Schema collection 范围 | `src/OceanBaseConnector/OceanBaseConnection.Schema.cs`、`OceanBaseConnection.Schema.Routines.cs` |
| capability 位和未开放协议能力 | `src/OceanBaseConnector/Internal/Protocol/CapabilityFlags.cs`、`OceanBaseCapabilityFlags.cs`、`src/OceanBaseConnector/Internal/Session/HandshakeFlow.cs` |
| TLS、客户端证书和撤销检查 | `src/OceanBaseConnector/OceanBaseConnection.cs`、`src/OceanBaseConnector/Internal/Transport/TlsHandshakeFlow.cs`、`TlsUpgrade.cs`、`tests/OceanBaseConnector.UnitTests/Transport/TlsComponentsTests.cs`、`ProviderConnectionTests.cs` |
| Oracle 输出、LOB、复杂类型和数组绑定 | `src/OceanBaseConnector/Internal/Sql/OracleCommandPlan.cs`、`OceanBaseLob*.cs`、`OceanBaseComplex*.cs`、`OceanBaseCommand.ArrayBinding.cs` |
| 类型、精度、时间和字符集限制 | `src/OceanBaseConnector/Internal/Values/*.cs`、`tests/OceanBaseConnector.UnitTests/*Type*Tests.cs`、`ZeroDateTests.cs`、`WideDecimalTests.cs` |
| 真实数据库和代理验收入口 | `tests/OceanBaseConnector.Acceptance/`、`tests/OceanBaseConnector.IntegrationTests/`、`docs/compatibility.md` |

## 12. 交付前的最小工作清单

这份清单是“完整驱动”可以对外发布前的最低闭环，不是所有可能的增强功能：

1. 保持池指标可观测；补齐并验证所有会话资源的 reset/丢弃规则。
2. 补齐 ambient transaction 的超时、提交/回滚响应丢失、连接关闭和真实故障测试；当前本地 enlistment 已实现，分布式提升仍明确不支持。
3. 完成多语句和 LOCAL INFILE 的真实互操作验收，再实现 MySQL 压缩、OB20 压缩和 proxy reroute；未实现项必须保持显式拒绝。
4. 验收企业信任/自定义验证回调，补认证插件、mTLS 池策略和 HA/发现的真实矩阵；撤销检查已有入口，仍需跨平台验收。
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
| `a8dd9a8` | 显式授权的 LOCAL INFILE capability、同步/异步流上传和未知结果处置 | 真实 OceanBase/OBProxy `LOAD DATA LOCAL INFILE`、代理策略、长文件和中断恢复 |
| `d39d1f9` | 直连非池化客户端证书、TLS 撤销检查配置和池化证书身份保护 | 自定义验证已由 `3f523d8` 补齐；真实企业信任、跨平台撤销语义和 mTLS 矩阵仍开放 |
| `729f534` | 已 Dispose 的池收到随后归还的借出会话时直接销毁 | Dispose 与已经开始的 reset 并发、非空池 shutdown 压力矩阵 |
| `5241d65` | Oracle 匿名块/程序 DDL 单语句边界、模式化引号和注释 | 真实程序单元语法及非法尾部由服务器解析的验收 |
| `06d58e1` | Close/Open 原池、registry key 和 timeout 快照；失败 Open 清理保留原 key | 多线程压力、延迟失败完成和真实服务器矩阵 |
| `71d440d` | 普通池按 resolver 引用身份隔离；每次 Open 路由快照、过期端点闲置会话清理和在途 Rent 保护 | 真实发现服务、DNS/代理/节点故障和跨平台报告 |
| `3f523d8` | `RemoteCertificateValidationCallback` 和现代 builder 入口；VerifyFull 限定、回调引用身份隔离及握手拒绝路径 | 真实企业 CA、轮换/撤销、mTLS 和跨平台信任矩阵 |

## 14. 缺口到场景、源码和验收的追踪表

以下 23 个 ID 覆盖当前盘点的缺口；编号是本报告的能力项，不改写原开发进度任务或历史阶段。表中测试文件是**现有扩展入口**，不是“该缺口已经通过”的声明。源码路径以 `src/OceanBaseConnector/` 为根，单元测试路径以 `tests/OceanBaseConnector.UnitTests/` 为根。负责人和交付日期尚未分配，不以阶段顺序推定承诺。

### 14.1 正确性、隔离和事务

| ID | 具体触发场景和影响 | 源码 / 现有回归入口 | 关闭缺口的最低证据 |
| --- | --- | --- | --- |
| P0-01 | 满池等待时 Close、重开、source Dispose 交错；可能影响资源归属、等待者退出和连接数上限。回调重开归属已修复，多线程压力仍缺。 | `OceanBaseConnection.cs`；`Internal/Session/PhysicalSessionPool.cs`；`PoolOwnershipTests.cs`、`Session/PhysicalSessionPoolTests.cs` | 同步/异步压力下无超额建连、双重归还或等待泄漏；保留公开清池/全池清理、换策略旧池退休及容量等待回归；清理超时、Dispose/reset 交错、长借用和预热失败都有报告。 |
| P0-02 | 存储过程或动态 SQL 改变 session 状态，未被 SQL 分类器识别，下一借用者可能继承状态。当前直接可识别语句会销毁会话。 | `Internal/Session/PhysicalSession.cs`；`PoolingTests.cs`、`TemporaryLobLifecycleTests.cs`、`RefCursorTests.cs` | 事务、变量、临时对象、reader、statement、LOB、cursor、XA 各有 A 污染/B 验证的真实双模式和代理测试；不能恢复的状态强制销毁。 |
| P0-03 | `TransactionScope` 超时、连接提前关闭或 commit 回包丢失；本地 volatile enlistment 不能视为 durable XA 或跨资源原子提交。 | `Internal/Session/AmbientEnlistment.cs`；`OceanBaseConnection.Transactions.cs`；`TransactionTests.cs`、`TransactionFailureTests.cs` | commit/rollback/prepare/in-doubt 和 dispose 竞态有回归；多连接/多资源组合有明确拒绝或经验证的支持契约；提升仍不得被静默当成本地事务。 |
| P0-04 | 业务写入或提交已发送，响应断开/取消；应用错误重试会重复写入。 | `OceanBaseException.cs`；`Internal/Session/CommandExchange.cs`；`CommandFaultTests.cs`、`TransactionFailureTests.cs` | 在发送前、发送后、响应中、commit/rollback 响应丢失注入故障；验证异常分类、会话销毁、禁止 replay 及应用核对操作结果的示例。 |
| P0-05 | 已打开连接遇到节点/代理故障，现有候选重试只覆盖建连 TCP 失败。 | `Internal/Session/PhysicalSession.cs`；`Internal/EndpointSelector.cs`；`TopologyExecutionTests.cs` | 先证明故障后连接/事务状态稳定且应用能安全重建；若新增透明恢复，须分别证明只读、幂等写、非幂等写和活动事务边界。完整驱动不要求自动重放未知写。 |
| P0-06 | 不同 resolver 共池、路由更新后复用旧会话的问题已复现并本地修复；真实拓扑/故障矩阵未验证。 | `OceanBaseConnection.cs` 的 `AddressResolver`/`PrepareOpen`；`PhysicalSessionPoolRegistry`/`PhysicalSessionPool.Rent`（同在池文件中）；`PoolRoutingTests.cs` | 保留双服务端、引用身份、快照/预热、失败/取消/总预算、并发发现、Closed 回调清理和借出会话回归；补真实发现/代理/跨平台报告。 |

### 14.2 协议、公共契约和常见部署

| ID | 具体触发场景和影响 | 源码 / 现有回归入口 | 关闭缺口的最低证据 |
| --- | --- | --- | --- |
| P1-01 | opt-in 多语句带参数、Prepare、cursor 或 Oracle 输出时拒绝；允许文本多语句后错误/多结果边界尚缺实库报告。 | `OceanBaseCommand.cs`；`Internal/Sql/SqlStatementBoundary.cs`；`TextCommandTests.cs`、`OracleStatementBoundaryTests.cs` | 成功/中间失败/取消后的结果消费与会话可复用性；不支持组合发送前拒绝；两个模式及 OBProxy 真实验证。Oracle 程序单元内部分号不是多语句缺口。 |
| P1-02 | 服务端/代理要求压缩 frame 或 reroute，当前 capability 不开放或包被拒绝。 | `Internal/Session/HandshakeFlow.cs`；`Internal/Protocol/Ob20Packet.cs`、`Ob20Stream.cs`；`Protocol/Ob20PacketTests.cs`、`Protocol/Ob20StreamTests.cs` | 协商、混合压缩/未压缩、分片、序列、校验、解压大小上限和畸形包；reroute 校验目标且不重放 unknown-outcome 请求；真实代理报告。 |
| P1-03 | LOCAL INFILE 本地路径已有，真实服务端请求、代理策略、64 MiB 上限及上传中断仍未验证。 | `Internal/Session/CommandExchange.cs`；`LocalInfileTests.cs` | 实库成功、关闭授权、工厂拒绝、边界/超限、服务器失败、上传取消；失败会话不回池且从不按服务器名称打开本地文件。 |
| P1-04 | 自定义验证回调已提供并隔离池策略；真实企业信任、撤销/轮换未验收。客户端证书池化/数据源快照及 26 项本地回归已完成（第 15.4 节）。回调只在物理 TLS 建立时执行，不能用更改捕获状态撤销旧会话。 | `Internal/Transport/TlsUpgrade.cs`、`TlsHandshakeFlow.cs`；`OceanBaseConnection.cs`、`OceanBaseDataSourceBuilder.cs`；`TlsServerValidationTests.cs`、ConsumerTests 的 `TlsValidationConsumerTests.cs`；`PooledClientCertificateTests.cs` 和 `ClientCertificateConsumerTests.cs` | 保留接受/拒绝/抛异常、主机名和链错误、预热/复用、回调引用身份、取消及重入关闭回归；保留池化证书隔离、source 所有权/释放竞态回归，补真实 TLS/mTLS、过期/轮换、撤销和 Windows/Linux 报告。不得以 `Required` 绕过身份验证完成验收。 |
| P1-05 | `KeyInfo` 拒绝；Batch 行为比 Command 更少；`SchemaOnly` 仍执行 SQL 并消费行，不能承诺无副作用/低成本元数据查询。 | `OceanBaseCommand.cs`；`OceanBaseDataReader.cs`；`OceanBaseBatch.cs`；`BehaviorOptionsTests.cs`、`ReaderSchemaTests.cs`、`BatchSurfaceTests.cs` | 命令/Batch/REF CURSOR 行为表、主键/唯一键元数据、SchemaOnly 副作用/锁/大结果用例；按实际实现公开限制，不把 SQL 前缀当成只读证明。 |
| P1-06 | REF CURSOR 的 Input/InputOutput、普通 cursor-valued column 和 PrepareExecute 独立 Prepare/FetchSize 组合被拒绝。 | `Internal/Sql/OracleCommandPlan.cs`；`OceanBaseCommand.cs`；`RefCursorTests.cs`、`OraclePrepareExecuteResponseTests.cs` | 参数方向 × NULL × 输出行数 × 多结果 × 取消/关闭矩阵；输入/输出游标明确所有权；服务端不支持的形状单列，不伪造通过。 |
| P1-07 | 嵌套 object/collection 已可编解码，但其中 LOB/cursor/national 成员或外部 DDL 改变描述时支持不足。 | `OceanBaseConnection.ComplexTypes.cs`；`Internal/Values/ComplexType.cs`、`ComplexValueCodec.cs`；`ComplexTypeDictionaryTests.cs`、`ComplexResultTests.cs` | 保留既有嵌套回归，扩展成员类型与 NULL；DDL 后旧描述失效、跨 session/transaction 不误用；深度/成员/字节限制和循环定义继续拒绝。 |
| P1-08 | 扩展列 metadata/未知类型码被拒绝；national 字符集限于已支持编码；MySQL 客户端 prepare fallback 的字符串因无可信 session collation 被拒绝。 | `Internal/Protocol/ResultColumn.cs`、`NationalCharacterSet.cs`；`Internal/Sql/ClientPreparedSql.cs`；`NationalCharacterTests.cs`、`ClientPrepareFallbackTests.cs`、`WideDecimalTests.cs` | 每种类型的参数/结果/NULL/精度/溢出表；编码不可表示时不静默替换；可信 collation 与 SQL mode 后才扩展字符串 fallback；不把禁止降级误称 server prepare 缺失。 |
| P1-09 | OCP/TNS 等发现、健康缓存/退避和代理拓扑没有内置实现；只注入 resolver 不能证明 HA。 | `IOceanBaseAddressResolver.cs`；`Internal/EndpointSelector.cs`；`MultiAddressTests.cs`、`TopologyExecutionTests.cs` | 地址更新、旧会话失效、错误身份/TLS 不重试、预算/退避、代理 reroute 和节点故障报告；与 P0-06 共同冻结池路由契约。 |
| P1-10 | 请求 Indexes/Keys/Constraints/Types/Sequences 等集合会拒绝；已有 Tables/Columns/Procedures/ProcedureParameters 不能再列为全缺失。 | `OceanBaseConnection.Schema.cs`、`OceanBaseConnection.Schema.Routines.cs`；`ConnectionSchemaTests.cs`、`RoutineSchemaTests.cs` | 集合/限制参数/大小写/权限不足/空目录 × 双模式矩阵；参数目录与 DeriveParameters/命令自动推导是不同能力，分别声明。 |
| P1-11 | 应用需要短期口令/token 或密钥轮换；目前只能自行取密钥并构造连接配置，无建连时凭据提供者。 | `OceanBaseConnectionStringBuilder.cs`；`OceanBaseDataSourceBuilder.cs`；`Internal/Session/HandshakeFlow.cs`；`ProviderConnectionTests.cs` | API 获取时点、取消、过期/刷新失败、并发刷新和旧会话失效测试；日志/trace/指标不包含凭据；本地插件测试外还需真实 plugin/KDC 成功及拒绝矩阵。 |

### 14.3 生态、性能、诊断和验证

| ID | 具体触发场景和影响 | 源码 / 现有验证入口 | 关闭缺口的最低证据 |
| --- | --- | --- | --- |
| P2-01 | Factory 返回 `null` 的 DataAdapter/CommandBuilder 无法供 DataSet 更新或命令生成工具使用。 | `OceanBaseProviderFactory.cs`；`ProviderSurfaceTests.cs` | 实现适用的 CRUD/并发冲突/键元数据契约，或公开列为不支持并从该版本承诺范围排除；ADO.NET provider 不自动等于 EF Core provider。 |
| P2-02 | 多次创建新 Command 会重复 server prepare；没有跨命令、有限容量的缓存。 | `OceanBaseCommand.cs`；`Internal/Session/PreparedStatement.cs`；`PreparedCommandTests.cs`、`PoolingTests.cs` | 容量、淘汰、statement close、reset generation、DDL/schema 切换及并发上限；实测 prepare 次数和延迟收益，不以缓存引入会话泄漏。 |
| P2-03 | DbBatch 逐条执行，有网络往返成本；Oracle array binding 已有，不能记为“没有批量”。 | `OceanBaseBatch.cs`；`OceanBaseCommand.ArrayBinding.cs`；`BatchFaultTests.cs`、`OracleArrayBindingExecutionTests.cs` | 对已承诺的 rewrite/multi-send/bulk 路径提供行数/OUT、部分失败、取消、包上限和事务边界；与现有顺序模式作吞吐/内存对照。 |
| P2-04 | 现有日志/Activity/Meter 和池指标存在，建连/探活分段、协议 debug 和慢查询 profile 不完整。 | `Internal/DriverDiagnostics.cs`；`DiagnosticsTests.cs`、`CommandLogCategoryTests.cs` | 指标单位和聚合语义、异常分类、监听开关开销、SQL/凭据脱敏；明确 `borrowed` 是借用次数，不能据此报告当前借出连接数。 |
| P2-05 | 两个包均产出五个 TFM，但现代 API 条件编译；Framework/ORM 消费与非 Windows 部署不能由一次 net10 单测推定。 | 两个 src 项目 `.csproj`；`.github/workflows/ci.yml`；`eng/Verify-Packages.ps1`；ConsumerTests、DI tests | 对每个承诺运行时运行实际 nupkg consumer；Windows/Linux、标准/现代 API 差异和 Framework 支持决定有报告。savepoint 的当前标识符规则不接受 `__EFSavePoint`，ORM 集成需单独验收。 |
| P2-06 | Acceptance 的六个 `matrix.*` 目前是带 `DeferredReason` 的占位 skip；普通 test 通过不能关闭部署验收。 | `tests/OceanBaseConnector.Acceptance/DatabaseCases.cs`；IntegrationTests；`docs/compatibility.md` | 实现受控测试环境和断言，保存 JSON/TRX、版本/profile/清理结果；承诺范围缺证据仍阻塞发布，新增范围才可后移。 |

## 15. 交付与验证记录规则

### 15.1 每项关闭必须留下的记录

每个 ID 的实施记录至少包含：公开支持范围与拒绝范围、变更提交、回归用例、执行命令及退出码、TFM/OS、服务端与 OBProxy 版本、TLS/认证 profile、通过/失败/跳过数和报告位置。真实报告还需记录资源清理和未知结果核对；不得提交密码、私钥或完整秘密配置。

优先级表示实施顺序，不替代发布门槛。P0-01/02/06 应共同收敛池的配置、路由和会话隔离；P0-03/04/05 共同收敛事务与故障恢复。P1-04/09/11 的信任、路由和凭据策略必须进入池隔离设计；P1-05/10 是 DataAdapter/命令生成的前置能力。性能项可分期，任何公开承诺的能力均必须验收。

### 15.2 初次文档复核记录（开发修复前）

- 核对 .NET HEAD `06d58e1`、Java HEAD `6adc311b`，以及池、连接、TLS、Command/Reader/Batch、复杂类型、Schema、认证配置、目标框架、CI 和 Acceptance 占位路径。
- 保留进度台账中上一轮的完整测试记录：UnitTests 3444、ConsumerTests 2112、DI 14 通过，Integration 9 通过/70 跳过。它们是此前修复的记录，不是本轮重跑结果，也不是当前完整实库验收。
- 保留真实部署的历史记录与失败：4.4.2.2 直连、TLS disabled；MySQL 60 通过，Oracle 79 通过/1 失败。原始报告缺失的限制见兼容性文档，本轮没有重新连接数据库。
- 初次文档整理阶段只修改文档，校验本地链接、编号追踪和 diff，未运行代码测试。后续 P0-06 已进入开发并有复现/修复回归，当前状态以第 5.1 节及进度台账为准；SchemaOnly 副作用语义仍待专门回归。

### 15.3 完整性的定义

“完整”应以版本承诺的契约闭合为准，不能靠“每个模块有一个类”或“所有测试为绿色”判定。原生 MySQL/Oracle、未知写自动 replay、无边界本地文件访问继续排除；分布式提升、adapter、ORM provider、压缩、发现和性能扩展应逐项确定支持版本或明确排除。未支持可以是清晰契约，已承诺却只具备 fixture、占位 skip 或模糊错误则仍是缺口。

### 15.4 2026-10-25 客户端证书池化本地实现与验证

本模块移除了客户端证书与 Pooling 的互斥，增加证书引用/指纹身份、池拥有的证书副本和现代 source 的 Build 快照。源码入口是 [connection](../src/OceanBaseConnector/OceanBaseConnection.cs)、[pool/registry](../src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs)、[TLS helper](../src/OceanBaseConnector/Internal/Transport/TlsUpgrade.cs)、[builder](../src/OceanBaseConnector/OceanBaseDataSourceBuilder.cs) 和 [data source](../src/OceanBaseConnector/OceanBaseDataSource.cs)。公开所有权、联网前拒绝、轮换和普通池清理限制见第 5.5 节与 [API 契约](obconnector-dotnet-architecture-design.md#tls-client-certificates)。

| 检查 | 已记录结果与边界 |
| --- | --- |
| 初始回归 | 修改前 20 项中 18 失败、2 通过；之后补充 Build 拒绝、释放所有权及 source Dispose/握手竞态，共 26 项聚焦单测通过。不是相同数量用例的简单前后相减。 |
| 最终构建与完整单测 | Release solution build 退出 0，0 错误、9 个 NU1900 漏洞源访问警告；全量 UnitTests 3533 通过，无失败/跳过。 |
| 最终受影响消费者 | 两组共 154 通过，无失败/跳过：32 项客户端证书用例（五资产普通连接与现代 source，同步/异步、池化/非池化）、10 项池路由用例，以及 112 项 TLS/验证回调/source batch 用例。消费者由 net10 主机加载各目标资产，不是五个独立运行时的实库测试。 |
| DI / Integration | DI 14 通过；Integration 9 通过、70 跳过。没有配置的真实 profile 继续缺证据。 |
| 代码走查 | 独立只读走查未发现确定的生产代码缺陷；指出的消费者 fixture 编译接线已修复，要求的 source Dispose/握手竞态已补四项回归。 |
| 证书原地 Import | 当前运行时抛 PlatformNotSupportedException，不宣称覆盖原地成功换证；已有 Reset 失效和替换实例回归，其他运行时/私钥提供者仍需验证。 |
| Go lint | 按要求执行 golangci-lint，因没有 Go module 退出 1，不适用于 C#，不能记为 lint 通过。C# 检查范围和命令见进度台账。 |
| 真实互操作 | 未执行 OceanBase/OBProxy mTLS、企业 CA、轮换、撤销、Linux 或其他私钥提供者矩阵；本地 SslStream mTLS 只证明证书呈现、会话隔离和所有权。 |

命令、退出码和本地报告路径见 [本模块验证台账](obconnector-dotnet-progress.md#2026-10-25-p1-04-pooled-client-certificates)。P1-04 继续为部分实现；23 个 ID 及原开发任务编号不因局部交付而改写。先前的纯文档复核已通过两份审计文档的 26 个本地链接/锚点和三表 23-ID 一致性检查，最终扩展文档检查另记在台账，不将旧检查冒充本次完整门槛。

### 15.5 2026-10-25 显式清池与旧身份退休

已增加全资产的 ClearPool/ClearAllPools 和现代源的 Clear，接口范围及源/普通池隔离见第 5.1 节。19 项初始回归全部 RED 后转为 GREEN；独立走查指出旧代次预热取消后的等待者唤醒和预热失败残留引用的指标重复扣减，已分别用同步/异步测试复现，再修复共享名额释放及 Destroy 路径。最终聚焦 23 项通过；最终完整单测 3556 通过，受影响消费者 180 通过，构建 0 错误/9 个 NU1900 警告，DI 14 通过，Integration 9 通过/70 跳过。C# 六个变更文件格式及 60 个本地链接/锚点校验通过；Go lint 因没有 Go module 退出 1，不适用。完整命令、报告及限制以[本模块进度记录](obconnector-dotnet-progress.md#2026-10-25-p0-01-explicit-pool-clearing)为准；P0-01 的长期运行、广泛并发及真实服务器验收继续开放。

相关文档：

- [Compatibility and known gaps](compatibility.md)
- [Verification status and remaining work](obconnector-dotnet-progress.md)
- [ADO.NET API reference](obconnector-dotnet-architecture-design.md)
- [Connector/J architecture outline](obconnector-j-architecture-outline.md)
