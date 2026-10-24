# OceanBaseConnector .NET 完整驱动能力与缺口基线

**审计日期：** 2026-10-24
**初次源码基线：** .NET `06d58e1`；Java `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`（Connector/J 2.4.14）
**本模块开发起点：** `2bbecac`（有符号 decimal scale）；本次增加参数自动推导。Java 参考为 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`。
**最新状态：** P1-10 自动参数推导本地实现、完整回归及独立走查通过：完整单测 3900、完整消费者 2328（含新增 20 项）、DI 14 通过，Integration 9 通过/72 跳过；Release 构建 0 警告/0 错误。源码/契约和验证见第 15.18 节，修复前检查点见第 15.17 节；真实目录/权限/类型声明/版本/代理验收继续开放。
**适用仓库：** `D:\dotnet-code\obconnector-dotnet`
**参考实现：** 本仓库源码、测试和兼容性记录；本地 `D:\java-code\obconnector-j` 的 Connector/J 实现
**文档性质：** 能力审计、目标契约和验收基线；不代表尚未完成的功能已经实现

**最近复核：** 2026-10-25；自动参数推导见第 15.18 节，第 15.17 节的开发状态/失败/警告作为历史保留。有符号 decimal scale 本地交付见第 15.16 节；第 15.15 节保留修复前检查点及失败，不作为当前状态。DataTypes 修复、消费者覆盖、独立走查及最终执行结果见第 15.14 节；修复前失败和筛选遗漏保留于第 15.13 节，不作为当前状态。此前凭据实现、回归修复、独立走查和最终执行结果见第 15.12 节；修复前的文档检查点和失败报告保留于第 15.11 节。前次 Schema 报告复核见第 15.10 节。Schema 开发记录见第 15.9 节，标准 MySQL 压缩见第 15.7 节，客户端证书池化和显式清池见第 15.4/15.5 节。初次审计基线、历史失败和跳过均保留；尚未取得的真实验收继续开放。

初次审计的 .NET 提交为 `06d58e1`。其后 P0-06 的本地修复及 P1-04 的自定义服务器证书验证分别见 [池路由记录](obconnector-dotnet-progress.md#2026-10-24-p0-06-pooled-resolver-routing)和 [TLS 验证记录](obconnector-dotnet-progress.md#2026-10-25-p1-04-tls-server-validation-callback)。23 个缺口 ID 保持不变；本地实现不关闭尚未取得的真实服务器验收。

面向项目评审的汇总和交付门槛见 [驱动完整性走查报告](obconnector-dotnet-driver-audit.md)；本文件保留逐模块的验收要求和源码索引。

**阅读入口：** 第 4 节列出全部 23 项缺口，第 5 节定义功能契约，第 14 节逐项关联触发场景、源码/测试入口及关闭标准，第 16 节给出八个工作包。第 15 节按时间保留开发和验证记录；较早的“未实现”只描述当时基线，当前状态以第 15.18 节为准。源码存在、局部测试通过和模块交付是不同状态。

## 1. 目的和结论

本项目是一个面向 OceanBase MySQL 兼容模式和 Oracle 兼容模式的托管 ADO.NET provider。当前已经具备连接、认证、基础 SQL、参数绑定、结果读取、本地事务、XA、Oracle PrepareExecute、LOB、复杂类型、数组绑定、TLS、OB20 和诊断等基础模块。

当前驱动仍不能称为完整的生产级 ADO.NET 驱动。连接池已具备基础容量、等待、存活探测和生命周期控制，ambient transaction 已支持本地 volatile enlistment，但还需要补齐完整 reset、协议可选能力、活动连接故障恢复、企业 TLS、ADO.NET 命令行为、Oracle 结果形状、目录元数据和真实兼容矩阵。

本文件统一区分以下状态：

| 状态 | 含义 |
| --- | --- |
| 已实现 | 源码存在对应行为，并有本地单元测试、消费者测试或协议 fixture 支持 |
| 部分实现 | 只支持部分模式、类型、参数组合或目标框架 |
| 明确缺失 | 源码或测试明确拒绝该能力，必须新增实现才能支持 |
| 未验证 | 源码路径存在，但缺少真实 OceanBase、OBProxy、TLS、KDC 或目标运行时证据 |
| 开发中 | 工作区已有未提交实现，但回归或交付门槛未通过，不计为已交付能力 |

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
| 认证 | 部分实现 | native、old、clear、SHA-2、Ed25519、PAM、GSSAPI 及每次 Open 凭据提供/版本轮换有代码与本地回归；真实凭据服务、插件和 KDC 矩阵未闭合 |
| TLS | 部分实现 | 默认平台 `VerifyFull`；可选 `RemoteCertificateValidationCallback` 已接入并按引用隔离普通池；`ClientCertificate` 支持普通池化/直连及数据源快照，证书按引用和指纹隔离；撤销检查可配置。真实企业 CA/mTLS、轮换和跨平台撤销矩阵未闭合 |
| 基础 SQL | 已实现 | 文本执行、server prepare、多个结果、取消、超时和流式读取已有路径 |
| 多语句和压缩 | 部分实现 | `Allow Multi Statements=true` 可协商并执行文本多语句；标准 MySQL 压缩已有显式 Use Compression 路径；OB20 压缩 frame 仍未开放 |
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
| Schema 元数据 | 部分实现 | Oracle 20/MySQL 18 个集合、静态 DataTypes 及自动参数推导已有本地实现；后者只读目录，不扩大过程执行范围。真实目录/权限矩阵仍缺，见 [Schema 契约](schema.md)及第 15.18 节 |
| 批处理 | 部分实现 | `DbBatch` 顺序执行；没有 rewrite、multi-send、bulk 或跨命令 statement cache |
| 诊断 | 部分实现 | `ILogger`、`ActivitySource`、`Meter` 和池生命周期指标已有；协议 debug、slow-query profile、建连/探活细分指标未完整提供 |
| 真实兼容性 | 部分验证 | 当前记录主要是 Windows/.NET 10、直连 OceanBase 4.4.2.2、TLS disabled；Oracle 失败及未覆盖矩阵仍须闭合 |

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
| P1-02 | P1 | 压缩和 OB20 | 部分实现 | 标准 MySQL 压缩已有本地实现；先确认 OB20 压缩的客户端链路/版本/协商契约，再补实现；reroute 及真实压缩矩阵仍缺，见第 16.3 节 |
| P1-03 | P1 | LOCAL INFILE | 部分实现 | 完成真实服务器/代理互操作、取消和大文件矩阵；保持显式授权和路径安全 |
| P1-04 | P1 | TLS/mTLS | 部分实现 | 自定义服务器证书回调、客户端证书池化身份隔离和数据源证书快照已有本地实现（第 15.4 节）；真实企业 CA/mTLS、轮换和跨平台撤销矩阵仍缺 |
| P1-05 | P1 | ADO.NET 命令行为 | 部分实现 | `SchemaOnly`、`SingleRow`、`SingleResult`、`KeyInfo` 等常用行为 |
| P1-06 | P1 | Oracle 输出和游标 | 部分实现 | 完整参数方向、cursor 列和 PrepareExecute 形状 |
| P1-07 | P1 | 复杂类型 | 部分实现 | 已有嵌套 object/collection；补齐 LOB/cursor/national 成员和外部 DDL 缓存失效 |
| P1-08 | P1 | 类型和字符集 | 部分实现；有符号 decimal 参数 scale 本地完成 | DecimalScale 精确约束已有回归；扩展元数据、更多字符集和完整类型码及实库声明矩阵仍缺 |
| P1-09 | P1 | HA/发现 | 部分实现 | OCP/TNS、健康状态、重试退避和 proxy 拓扑 |
| P1-10 | P1 | Schema 元数据 | 部分实现；参数推导已有本地实现 | 常用目录、DataTypes 及自动参数推导有本地实现；真实目录/权限/版本/代理验收仍缺 |
| P1-11 | P1 | 凭据提供与轮换 | 本地实现；真实矩阵未验证 | 每次 Open 获取、全口令替换、版本排序、过期及旧会话退休已本地验证；真实凭据服务/plugin/KDC 验收仍开放 |
| P2-01 | P2 | DataAdapter | 明确缺失 | `DbDataAdapter` 和 `DbCommandBuilder`，或正式声明不支持 |
| P2-02 | P2 | Statement cache | 明确缺失 | 跨命令 prepared statement cache |
| P2-03 | P2 | 批处理优化 | 部分实现 | rewrite、multi-send、bulk 和统一 partial-failure 结果 |
| P2-04 | P2 | 诊断 | 部分实现 | 协议 debug、slow-query profile、建连/探活和资源等待 |
| P2-05 | P2 | TFM/生态 | 未验证 | 每个 TFM 的正式支持矩阵和 package consumer 验收 |
| P2-06 | P2（扩展矩阵）/ 发布阻塞（承诺范围） | 真实兼容性 | 部分验证 | 承诺范围必须有真实版本、OBProxy、TLS、认证、平台和故障报告，扩展范围再分期 |

### 4.1 除连接池外，具体还缺什么

以下按当前源码区分新增开发和验证工作，不另增 ID，也不把“已有一部分”写成“完全没有”。具体场景、源码入口和关闭标准仍以第 14 节为准。

| 类别 | 尚需完成的内容 | 归属 ID |
| --- | --- | --- |
| 尚无公开实现 | `KeyInfo`；`DbDataAdapter`/`DbCommandBuilder`；跨命令 statement cache | P1-05、P2-01、P2-02 |
| 新增本地实现，真实验收开放 | 自动参数推导已有 API、完整签名映射、会话/事务预留和失败原子性；实际目录/权限/版本/代理互操作仍需报告，见第 15.18 节 | P1-10 |
| 新增实现仍待真实验收 | 凭据提供/轮换 API、每次 Open 获取、版本退休、过期、取消和脱敏已有本地回归；真实短期凭据服务/plugin/KDC 未验收 | P1-11 |
| 协议扩展缺失 | OB20 压缩与 proxy reroute；先确认目标链路、版本和协商契约，不能根据服务器内部路径猜测客户端支持 | P1-02 |
| 发现与恢复不足 | 内置 OCP/TNS 或等价适配、健康缓存和退避；活动连接故障后的状态/重建契约；无透明迁移和自动重连 | P1-09、P0-05 |
| Oracle 形状不足 | REF CURSOR Input/InputOutput、cursor-valued column、PrepareExecute 与独立 Prepare/FetchSize 的组合；复杂类型的 LOB/cursor/national 成员及外部 DDL 缓存失效 | P1-06、P1-07 |
| 类型与命令契约不足 | 扩展类型码、metadata、字符集和精度矩阵；SchemaOnly 仍执行查询；Command/Batch/游标的行为范围不同 | P1-08、P1-05 |
| 事务与结果不确定性 | 本地 ambient 的超时/in-doubt/关闭竞态、提交或回滚响应丢失；XA 外部协调器恢复；写入不自动 replay | P0-03、P0-04、P2-06 |
| 性能与运维不足 | 顺序 Batch 之外的 rewrite/multi-send/bulk；建连/探活分段、协议 debug、慢查询和资源上限报告。Oracle array binding 与池指标已有 | P2-03、P2-04 |
| 已有实现仍欠真实验收 | TLS/mTLS/撤销/认证插件、文本多语句、LOCAL INFILE、标准 MySQL 压缩、Schema 新目录及 DataTypes 声明对应的服务器类型/DDL，以及版本/OBProxy/平台/包消费矩阵 | P1-01、P1-02、P1-03、P1-04、P1-10、P2-05、P2-06 |

连接池自身的隔离、并发、reset 和路由仍由 P0-01/02/06 跟踪。分布式事务提升当前明确不支持；不能把本地 `TransactionScope` 当成跨连接或跨资源的原子提交。DataAdapter、ORM、透明恢复及性能扩展是否进入首个正式版本须明确决定：本报告保留缺项，不将未确认的取舍写成已批准的排除。

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

当前只有显式 `Allow Multi Statements=true` 才会协商 `CLIENT_MULTI_STATEMENTS`；默认仍关闭。启用后仅允许纯文本、无参数、无 Prepare、无 server cursor、无 Oracle PrepareExecute 的命令，并要求服务端也声明该 capability；结果通过已有的 `NextResult` 路径消费。`CLIENT_LOCAL_FILES` 也只在显式 `Allow Local Infile=true` 时请求，并走应用流工厂；`CLIENT_COMPRESS` 仅在 `Use Compression=true` 且服务端声明支持时请求，认证最终 OK 后、身份确认前切换；不支持的服务器会在发送认证响应前拒绝。OB20 已支持部分校验和、extra info 和 trace 结构，但压缩 frame 和 proxy reroute 明确拒绝。

Oracle 匿名 `BEGIN`/`DECLARE` 块（包括 label）和 `CREATE [OR REPLACE] [EDITIONABLE|NONEDITIONABLE]` 的 procedure/function/package/trigger/type 程序单元按单个 SQL 请求发送，内部的分号不要求多语句 capability；程序单元完整语法和非法尾部仍由服务端解析，驱动不拆分或重写 PL/SQL。普通 SQL 的边界检查按当前兼容模式识别引号和注释：Oracle 使用 doubled quote、`q`/`nq` 引号和无空白限制的 `--`，反斜杠和 `#` 不按 MySQL 转义/注释处理。同步/异步 loopback 和现有 OUT/temporary LOB 回归提供本地证据，真实服务器程序单元及非法尾部验收仍需执行。

实现要求：

- 多语句默认关闭，启用时必须在连接配置中明确声明；
- 多语句只走文本协议；参数、显式 Prepare、FetchSize/server cursor、Oracle procedure/output 组合必须在发送前拒绝；
- 多结果、错误边界和参数绑定必须能区分每条语句；
- MySQL 压缩协议必须实现分片、压缩/未压缩混合包、序列号和最大包限制；
- OB20 压缩须先确认客户端链路和 capability 契约，再实现 header、payload、校验和和 frame 边界；内部 proxy-observer 支持不能替代客户端证据（第 16.3 节）；
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

**P1-11 凭据轮换：** 已新增 `IOceanBaseCredentialProvider`、`OceanBaseCredential`、连接 `CredentialProvider` 及现代 builder `UseCredentialProvider`，并通过最终本地回归。[凭据契约](credentials.md)列出具体 API、所有权和行为；第 15.12 节记录交付证据，第 15.11 节保留修复前失败。

以下是该缺项必须闭合的契约和验收清单。当前实现为每次逻辑 Open 获取一次快照、替换全部口令、提供者引用隔离及递增 Version 退休规则；本地实现不替代真实插件验收：

| 契约 | 必须明确的行为与最低用例 |
| --- | --- |
| 获取时点 | 区分逻辑 Open、闲置池复用、新物理连接、最小池预热和端点重试；不能出现某条路径悄悄使用旧静态密码。用真实握手字节验证用户名、主口令及多步认证口令来源。 |
| 静态配置优先级 | 明确提供者与连接字符串口令的替换或互斥关系；获取失败不得静默回退。无提供者时保留现有行为。 |
| 过期 | 区分登录凭据过期与已认证会话有效期；覆盖获取时已过期、排队/握手中到期、归还时到期，不得无界循环建连。是否允许活动命令继续须公开说明。 |
| 并发刷新 | 明确刷新由驱动还是调用方合并，以及新旧结果的排序依据；验证旧结果晚到不能把已退休身份重新放回池；当前实现采用非负递增 Version，同版本认证材料变化会拒绝。 |
| 池身份与退休 | 不同提供者/身份不得误共池；轮换后闲置、借出、预热和在途建连各有退休规则；保留容量、清池和等待者退出保证。 |
| 取消和总预算 | 获取与发现、排队、预热、握手共同遵守连接超时；取消后的晚到结果不得重新打开连接，失败和取消不得泄漏租约/池名额。同步回调的阻塞约束须单列。 |
| 所有权与配置 | 区分普通连接可配置时点和 data source 的固定策略；说明提供者的线程安全、生命周期、缓存与刷新职责，不能由驱动擅自释放调用方对象。 |
| 秘密与错误 | 提供者异常可能含 token；日志、Activity、Meter、连接字符串展示和可观测池身份均不得泄漏。错误须能区分获取失败、过期、取消和服务端拒绝，且不能鼓励未知写重试。 |

上述本地用例通过后，仍需真实短期凭据/认证插件的成功、过期、拒绝和轮换报告；GSSAPI/KDC 按承诺范围独立验收，不将口令回调等同于 Kerberos 支持。

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

当前过程调用另有限制：`CommandType.StoredProcedure` 仅在 Oracle 模式且启用 `Use Oracle Prepare Execute=true` 时进入过程执行路径；MySQL 模式当前只支持 `CommandType.Text` 的执行入口，不能把已有 `Procedures`/`ProcedureParameters` 目录解释成 MySQL StoredProcedure/OUT 参数执行已支持。参数自动推导只读取目录，不执行目标过程，也不扩大上述执行范围，详见第 15.18 节。源码入口为 `OceanBaseCommand.Start` 和 `Internal/Sql/OracleCommandPlan.cs`，归入 P1-05/06/10，不新增 ID。

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

Oracle NUMBER 的结果 metadata 可以含负 scale；`DbParameter.Scale` 保持 byte 契约，现通过 `int? OceanBaseParameter.DecimalScale` 显式声明有符号 scale。Oracle -84..127、MySQL 0..30 及精度/无舍入约束已有本地回归，赋值优先级见[参数契约](obconnector-dotnet-architecture-design.md#decimal-parameter-scale)，交付记录见第 15.16 节。自动参数推导沿用该表示，本地回归覆盖负值的保留及精度约束；真实目录/声明矩阵仍须验收。

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

当前 schema 为 Oracle 20/MySQL 18 个集合，包含 Restrictions、常用关系/对象目录及静态 DataTypes，已有最终本地回归（第 15.14 节），详见 [Schema 契约](schema.md)。双模式实库/权限/版本/OBProxy 验收仍开放。batch 顺序执行，prepared statement 主要限于命令生命周期，诊断能力已覆盖池生命周期，但还没有覆盖协议 debug 和 slow-query profile。

目标要求：

- 保留已有 `MetaDataCollections`、`DataSourceInformation`、`Tables`、`Columns`、`Procedures` 和 `ProcedureParameters` 六个集合；独立的 Views、Indexes、Keys、Constraints、Types、Sequences、Users 和 Synonyms 等目录已有本地实现，仍需真实验收；Types/Synonyms 仅 Oracle，模式/权限边界见 Schema 契约；
- 过程元数据能够安全映射到参数描述，但不能把不完整目录信息误当成完整签名；
- 实现有上限和淘汰策略的跨命令 statement cache；
- 为 batch 提供 rewrite、multi-send、bulk 或 array binding 的模式化选择；
- 暴露连接池总数、借出数、等待数、丢弃数、reset 失败数和连接建立耗时；
- 诊断 SQL 必须脱敏，能够区分等待、网络、服务器错误和 unknown outcome。

#### 5.10.1 目录已有实现之外的 ADO.NET 元数据缺项

以下三项不能用“已扩展 Schema 目录”统一关闭。前两项归入 P1-10/W6，第三项归入 P1-05/W5；不改变 23 个主 ID。

| 能力 | 当前源码事实 | 后续验收要求 |
| --- | --- | --- |
| `GetSchema("DataTypes")` | 静态表已本地实现，含标准 23 列及 9 个扩展列、双模式 CLR/provider 映射和条件；可信 UTF-16 正向/未知字符集拒绝、实际参数与列 codec、五资产消费者均有最终回归（第 15.14 节）。`Types` 仍仅描述 Oracle 命名类型 | 保持参数/结果/精度/scale/NULL/创建模板与实际 codec 一致，补真实服务器类型、DDL、字符集与版本/代理验收。自动推导和 KeyInfo 分别验收，不以静态映射关闭其他缺项。 |
| `DeriveParameters` / 自动推导 | 同步/异步 API、唯一目录身份、完整顶层签名、同会话事务和原子发布已完成本地验证，见第 15.18 节 | 保留 owner/package/重载、方向/顺序/返回值、精度/复杂类型、失败原子性及取消/事务回归；补真实目录、声明、权限、版本与 OBProxy 报告，不扩大目标过程执行范围。 |
| `CommandBehavior.KeyInfo` | Command 行为检查直接拒绝；`GetColumnSchema` 和 `GetSchema("Keys")` 不等于已支持该标志 | 验证主键/唯一键、组合键、可空性、来源列、别名/连接/表达式列和隐藏键列；无法可靠推导时公开限制，不为 adapter 伪造可更新性。 |

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
| M04 handshake/mode/capability | `HandshakeFlow`、`NegotiatedCapabilities` | 模式和部分能力协商存在；多语句、LOCAL INFILE 和标准 MySQL 压缩已有 opt-in 路径；OB20 压缩/reroute 仍缺失 |
| M05 session/transaction/cancel | `PhysicalSession`、`OceanBaseTransaction`、`AmbientEnlistment` | 本地事务、ambient enlistment、取消和 unknown outcome 有路径；活动连接迁移缺失 |
| M06 query/prepare/cursor | `OceanBaseCommand`、`PreparedStatement`、`QueryResult` | 基础文本/server prepare 和 cursor 有路径；statement cache、完整 CommandBehavior 未闭合 |
| M07 binding/sql parser/batch | `ParameterBindingPlan`、`OracleArrayBindingPlan`、`OceanBaseBatch` | 参数和数组绑定有路径；rewrite、multi-send、bulk 优化缺失 |
| M08 result/rows/multiple results | `OceanBaseDataReader`、`QueryResult` | 流式读取和多结果有路径；所有结果形状和资源边界仍需矩阵验证 |
| M09 types/charset/time | `Internal/Values`、`OceanBaseDbType` | 覆盖较广；扩展 type code、字符集、精度和跨模式边界仍有限制 |
| M10 LOB/complex | `OceanBaseLob`、`OceanBaseComplexType` | 基础 LOB 及嵌套 object/collection 存在；LOB/cursor/national 成员和外部 DDL 缓存失效未闭合 |
| M11 metadata/catalog | `OceanBaseConnection.Schema`、reader schema | Oracle 20/MySQL 18 个集合，含常用目录、过程参数目录及静态 DataTypes；真实目录/权限矩阵及自动参数推导未闭合 |
| M12 HA/failover | `EndpointSelector`、地址 resolver | Open 阶段候选地址重试存在；黑名单、退避、活动连接恢复和 reroute 缺失 |
| M13 pooling/reuse | `PhysicalSessionPool` | 复用、reset、容量、等待取消、生命周期、最小池预热及失败回滚、空池 registry 清理和池指标已实现；完整资源 reset 和真实 reset 兼容性未闭合 |
| M14 XA | `OceanBaseXaResource`、`OceanBaseXid` | 显式 XA 生命周期和 recover 有路径；外部 coordinator 崩溃恢复未验证 |
| M15 logging/diagnostics/OB20 | `DriverDiagnostics`、OB20 protocol classes | 日志、Activity、Meter、池指标和基础 OB20 存在；协议 debug、OB20 压缩/reroute 缺失；标准 MySQL 压缩已有路径 |
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
| Schema collection 范围 | `src/OceanBaseConnector/OceanBaseConnection.Schema.cs`、`OceanBaseConnection.Schema.Routines.cs`、`OceanBaseConnection.Schema.Catalog.cs`、`OceanBaseConnection.Schema.DataTypes.cs`；`tests/OceanBaseConnector.UnitTests/CatalogSchemaTests.cs`、`DataTypeSchemaTests.cs` 和消费者 `CatalogSchemaConsumerTests.cs`；[Schema 契约](schema.md) |
| capability 位和未开放协议能力 | `src/OceanBaseConnector/Internal/Protocol/CapabilityFlags.cs`、`OceanBaseCapabilityFlags.cs`、`src/OceanBaseConnector/Internal/Session/HandshakeFlow.cs` |
| TLS、客户端证书和撤销检查 | `src/OceanBaseConnector/OceanBaseConnection.cs`、`src/OceanBaseConnector/Internal/Transport/TlsHandshakeFlow.cs`、`TlsUpgrade.cs`、`tests/OceanBaseConnector.UnitTests/Transport/TlsComponentsTests.cs`、`ProviderConnectionTests.cs` |
| Oracle 输出、LOB、复杂类型和数组绑定 | `src/OceanBaseConnector/Internal/Sql/OracleCommandPlan.cs`、`OceanBaseLob*.cs`、`OceanBaseComplex*.cs`、`OceanBaseCommand.ArrayBinding.cs` |
| 类型、精度、时间和字符集限制 | `src/OceanBaseConnector/Internal/Values/*.cs`、`tests/OceanBaseConnector.UnitTests/*Type*Tests.cs`、`ZeroDateTests.cs`、`WideDecimalTests.cs` |
| 真实数据库和代理验收入口 | `tests/OceanBaseConnector.Acceptance/`、`tests/OceanBaseConnector.IntegrationTests/`、`docs/compatibility.md` |

## 12. 交付前的最小工作清单

这份清单是“完整驱动”可以对外发布前的最低闭环，不是所有可能的增强功能：

1. 保持池指标可观测；补齐并验证所有会话资源的 reset/丢弃规则。
2. 补齐 ambient transaction 的超时、提交/回滚响应丢失、连接关闭和真实故障测试；当前本地 enlistment 已实现，分布式提升仍明确不支持。
3. 完成多语句和 LOCAL INFILE 的真实互操作验收，验收标准 MySQL 压缩；先确认客户端 OB20 压缩协议契约再实现，并补 proxy reroute；未实现项必须保持显式拒绝。
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
| `01a8082` | 客户端证书按引用/指纹隔离池；池持有证书副本，现代源在 Build 时捕获证书快照 | 真实 mTLS、证书轮换/撤销、跨平台及私钥提供者矩阵 |
| `0e58d1f` | 全资产 ClearPool/ClearAllPools、现代源 Clear；旧代次退休、在途预热保护、容量唤醒和销毁计数修复 | 广泛并发/长期运行及真实服务器 reset/清池验收 |
| `8d8b799` | 显式标准 MySQL 压缩、严格有界 zlib 编解码、五资产及双模式本地连接/消费者回归 | 真实 OceanBase/OBProxy 压缩和跨平台矩阵；客户端 OB20 压缩协议前提及 reroute |
| `6fecad9` | Oracle 19/MySQL 17 个 Schema 集合、Restrictions、精确过滤和关系/对象目录 | 真实目录/权限/版本/代理矩阵；DataTypes、参数自动推导、KeyInfo 和 adapter 仍分别缺失 |

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
| P1-02 | 标准 MySQL 压缩已本地实现；OB20 压缩 frame 和 reroute 仍被拒绝，客户端压缩契约待确认，三项真实服务器/代理矩阵均未闭合。 | `Internal/Session/HandshakeFlow.cs`；`Internal/Protocol/MySqlCompressionStream.cs`、`ZlibBlock.cs`、`Ob20Packet.cs`、`Ob20Stream.cs`；`Protocol/MySqlCompressionTests.cs`、`CompressionConnectionTests.cs`、`Protocol/Ob20PacketTests.cs`、`Protocol/Ob20StreamTests.cs` | 先取得客户端链路、版本及 capability 证据（第 16.3 节）；验证协商、混合压缩/未压缩、分片、序列、校验、解压大小上限和畸形包；reroute 校验目标且不重放 unknown-outcome 请求；真实代理报告。 |
| P1-03 | LOCAL INFILE 本地协议实现已有，真实服务端请求、代理策略、64 MiB 上限及上传中断仍未验证。 | `Internal/Session/CommandExchange.cs`；`LocalInfileTests.cs` | 实库成功、关闭授权、工厂拒绝、边界/超限、服务器失败、上传取消；失败会话不回池且从不按服务器名称打开本地文件。 |
| P1-04 | 自定义验证回调已提供并隔离池策略；真实企业信任、撤销/轮换未验收。客户端证书池化/数据源快照及 26 项本地回归已完成（第 15.4 节）。回调只在物理 TLS 建立时执行，不能用更改捕获状态撤销旧会话。 | `Internal/Transport/TlsUpgrade.cs`、`TlsHandshakeFlow.cs`；`OceanBaseConnection.cs`、`OceanBaseDataSourceBuilder.cs`；`TlsServerValidationTests.cs`、ConsumerTests 的 `TlsValidationConsumerTests.cs`；`PooledClientCertificateTests.cs` 和 `ClientCertificateConsumerTests.cs` | 保留接受/拒绝/抛异常、主机名和链错误、预热/复用、回调引用身份、取消及重入关闭回归；保留池化证书隔离、source 所有权/释放竞态回归，补真实 TLS/mTLS、过期/轮换、撤销和 Windows/Linux 报告。不得以 `Required` 绕过身份验证完成验收。 |
| P1-05 | `KeyInfo` 拒绝；Batch 行为比 Command 更少；`SchemaOnly` 仍执行 SQL 并消费行，不能承诺无副作用/低成本元数据查询。 | `OceanBaseCommand.cs`；`OceanBaseDataReader.cs`；`OceanBaseBatch.cs`；`BehaviorOptionsTests.cs`、`ReaderSchemaTests.cs`、`BatchSurfaceTests.cs` | 命令/Batch/REF CURSOR 行为表、主键/唯一键元数据、SchemaOnly 副作用/锁/大结果用例；按实际实现公开限制，不把 SQL 前缀当成只读证明。 |
| P1-06 | REF CURSOR 的 Input/InputOutput、普通 cursor-valued column 和 PrepareExecute 独立 Prepare/FetchSize 组合被拒绝。 | `Internal/Sql/OracleCommandPlan.cs`；`OceanBaseCommand.cs`；`RefCursorTests.cs`、`OraclePrepareExecuteResponseTests.cs` | 参数方向 × NULL × 输出行数 × 多结果 × 取消/关闭矩阵；输入/输出游标明确所有权；服务端不支持的形状单列，不伪造通过。 |
| P1-07 | 嵌套 object/collection 已可编解码，但其中 LOB/cursor/national 成员或外部 DDL 改变描述时支持不足。 | `OceanBaseConnection.ComplexTypes.cs`；`Internal/Values/ComplexType.cs`、`ComplexValueCodec.cs`；`ComplexTypeDictionaryTests.cs`、`ComplexResultTests.cs` | 保留既有嵌套回归，扩展成员类型与 NULL；DDL 后旧描述失效、跨 session/transaction 不误用；深度/成员/字节限制和循环定义继续拒绝。 |
| P1-08 | 扩展列 metadata/未知类型码被拒绝；national 字符集限于已支持编码；MySQL 客户端 prepare fallback 的字符串因无可信 session collation 被拒绝。 | `Internal/Protocol/ResultColumn.cs`、`NationalCharacterSet.cs`；`Internal/Sql/ClientPreparedSql.cs`；`NationalCharacterTests.cs`、`ClientPrepareFallbackTests.cs`、`WideDecimalTests.cs` | 每种类型的参数/结果/NULL/精度/溢出表；编码不可表示时不静默替换；可信 collation 与 SQL mode 后才扩展字符串 fallback；不把禁止降级误称 server prepare 缺失。 |
| P1-09 | OCP/TNS 等发现、健康缓存/退避和代理拓扑没有内置实现；只注入 resolver 不能证明 HA。 | `IOceanBaseAddressResolver.cs`；`Internal/EndpointSelector.cs`；`MultiAddressTests.cs`、`TopologyExecutionTests.cs` | 地址更新、旧会话失效、错误身份/TLS 不重试、预算/退避、代理 reroute 和节点故障报告；与 P0-06 共同冻结池路由契约。 |
| P1-10 | 常用目录已本地实现；Types/Synonyms 为 Oracle-only，Users/Sequences 可能需要额外权限；真实字典/版本/代理矩阵未验证。DataTypes 及自动参数推导已有本地实现；真实目录互操作未验证。 | `OceanBaseConnection.Schema.cs`、`OceanBaseConnection.Schema.Routines.cs`、`OceanBaseConnection.Schema.Catalog.cs`、`OceanBaseConnection.Schema.DataTypes.cs`；`DataTypeSchemaTests.cs`、`CatalogSchemaTests.cs`、`ConnectionSchemaTests.cs`、`RoutineSchemaTests.cs`；另有 `OceanBaseCommand.Parameters.cs`、`OceanBaseConnection.Parameters.cs`、`Internal/Sql/RoutineParameterDerivation.cs`、`DeriveParameters*Tests.cs`，见第 15.18 节 | 保留 DataTypes 实际 codec/可信字符集正向与拒绝、五资产和完整形状、限制参数、精度、空目录、权限错误回归；完成集合/大小写/权限 × 双模式实库和代理矩阵；保留自动参数推导本地回归，并按第 5.10.1 节完成实库验收。 |
| P1-11 | 应用需要短期口令/token 或密钥轮换；本地实现已完成，真实过期/拒绝/轮换服务与插件矩阵仍未验证。 | `IOceanBaseCredentialProvider.cs`；`OceanBaseConnection.Credentials.cs`；`Internal/CredentialAcquisition.cs`；`Internal/Session/PhysicalSessionPool.Credentials.cs`；`CredentialProviderTests.cs`、`CredentialProviderLifecycleTests.cs`、`CredentialProviderDiagnosticsTests.cs`；消费者 `CredentialProviderConsumerTests.cs` | API 获取时点、取消、过期/刷新失败、并发刷新和旧会话失效测试；日志/trace/指标不包含凭据；本地插件测试外还需真实 plugin/KDC 成功及拒绝矩阵。 |

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

### 15.6 开发前文档复核的证据快照

该次文档整理以 `0e58d1f` 为基线复核池 registry 身份、清池 API、capability 构造、OB20 拒绝路径、CommandBehavior、Oracle 输出限制、复杂类型、Schema、provider factory、目标框架和 Acceptance 占位项。Java 参考提交重新核对为 `6adc311b`，池类和压缩模块仍在参考源码中；未运行 Java 测试或性能对比。

本地已有 `TestResults/pool-clearing/` 的四份 TRX 已核对：`unit-final.trx` 为 3556/3556 通过，`consumer-final.trx` 为受影响子集 180/180 通过，`di-final.trx` 为 14/14 通过，`integration-final.trx` 为共 79 项、9 通过/70 未执行。它们属于前次模块执行记录，不能把 180 项消费者子集写成完整消费者套件通过，也不能把 70 项跳过写成真实矩阵完成。报告位于本地忽略目录，未随仓库分发；原执行命令和退出码见进度台账。

该次只变更两份审计文档，检查本地链接/锚点、23-ID 一致性、工作包覆盖和 diff。不重复执行构建、C# 测试或 Go lint；不以该次文档校验增加运行时、数据库或代理兼容性声明。

### 15.7 2026-10-25 标准 MySQL 压缩

以 `0e58d1f` 为起点实现显式 `Use Compression`，默认 false；服务端不支持时在发送凭据前拒绝。最终认证 OK 后、身份查询前切换压缩，认证切换仍为普通包，TLS 位于压缩之下。五个资产支持同一标准 zlib 协议，两个兼容模式均有本地连接回归；当前与 Use OB20 的组合联网前拒绝。

实现包括压缩/原样块、分片与合并、独立外层序列回绕/逐命令重置、逻辑包上限、64 KiB 写缓存、有界接收/解压、严格结束/Adler/精确长度/尾随数据验证。LOCAL INFILE 在读取请求、上传和最终响应之间延续序列；读取失败、取消和 exchange 边界残余字节关闭传输。SharpZipLib >=1.4.2 为新增 MIT 运行时依赖，五个资产通过公共 API 使用，包验证和第三方清单同步。

首批 17 项回归在实现前全部失败，之后协议与公共连接测试扩展至 38 项；新增消费者 40 项覆盖五资产 × 双模式 × 同步/异步 × TCP/TLS，并验证认证切换、长请求、池 reset、探活和重新借用。独立走查未发现确定的 Critical/Important 缺陷；其指出的 LOCAL INFILE、流式短读及无响应完成边界测试已补充。本地 fixture 使用框架 ZLibStream，独立于产品的压缩实现。

最终完整单测 3594 通过、DI 14 通过；Integration 9 通过/70 跳过。完整消费者 2240 通过、0 失败/跳过；最终构建 0 错误/0 警告，构建后 38 项压缩单测和 40 项压缩消费者再次通过。已生成包通过五资产依赖组及独立 .NET 8/9/10、Standard 资产消费者验证；变更 C# 格式检查通过（工作区加载警告）。按要求执行 golangci-lint，因无 Go module 退出 7，不适用且不记为通过。在线 restore 未能访问源，切换现有用户缓存离线还原且关闭 NuGetAudit；不宣称完成本次漏洞源审计。

具体命令、报告和范围见[本模块台账](obconnector-dotnet-progress.md#2026-10-25-p1-02-standard-mysql-compression)。没有执行真实 OceanBase/OBProxy 压缩、压缩 TLS、跨平台或完整应用负载验收。P1-02 保持部分实现，OB20 压缩与 reroute 继续开发，23 个 ID 及发布门槛保持不变。

### 15.8 2026-10-25 完整文档交付复核

本次以已提交的 `8d8b799` 为源码基线，复核连接字符串的 Pooling/压缩默认值及组合拒绝、CLIENT_COMPRESS 协商、OB20 解析和 reroute 拒绝、CommandBehavior 允许集合、六类 Schema collection、provider factory 的 adapter/builder 返回值，以及五个目标资产声明。Java HEAD 重新核对为 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`，池类、标准压缩与 OB20 的分支和压缩 OB20 拒绝路径均读取确认；没有运行 Java 测试。

本次保留第 15.7 节及进度台账中的历史测试数，不把它们写成本次重跑结果。当前工作区没有 `TestResults/mysql-compression/` 原始 TRX，因此本次不能重新计算 3594/2240 等计数；复核依据是已提交的执行记录和现有源码/测试文件。以后关闭缺口时须重新取得可定位的原始报告，不能仅凭本摘要完成验收。`TestResults/ob20-protocol/` 的上游源码摘取是协议静态分析材料，不是运行报告。

本次交付只修改两份审计文档：校验 Markdown 链接/锚点、23 个缺口 ID、8 个工作包覆盖及 diff；不重跑构建、单元测试、包验证、Java 测试或实库验收。历史 Go lint 执行保留原记录，后续不再将其列为本 C# 仓库的模块验收要求。各项已实现/部分实现/明确缺失/未验证的边界保持，不因文档交付而关闭任何工作包。

### 15.9 2026-10-25 Schema 目录扩展

基于 `b38e4a8` 扩展 P1-10，Oracle 模式 19/MySQL 模式 17 个集合；新增关系/对象目录、Restrictions 描述、精确参数过滤、复合外键 ordinal 配对和 OceanBaseDecimal 序列值。实现、回归和独立走查记录见[本模块台账](obconnector-dotnet-progress.md#2026-10-25-p1-10-schema-catalog)。[Schema 契约](schema.md)列出全部集合、列/类型、限制参数、模式与权限语义。

本节记录 `6fecad9` 的历史状态：P1-10 的常用目录部分为“本地实现；实库未验证”；当时公开目录不提供自动 DeriveParameters、DataTypes、KeyInfo 或 DataAdapter。DataTypes 后续本地交付见第 15.14 节，开发中失败检查点保留于第 15.13 节；相关 ID 和真实验收门槛不变。历史六集合描述保留于此前复核快照，不作为当前能力声明。

### 15.10 2026-10-25 当前源码与完整文档交付复核

本次针对“根据走查问题给出完整文档”的交付，以已提交 .NET `6fecad9` 为基线。重新核对池默认值、借还/reset/清池与身份、Command/Batch 拒绝范围、Oracle 游标方向/PrepareExecute 限制、Schema 集合、provider factory、认证配置、目标 TFM/CI 和六个 Acceptance matrix 占位。Java HEAD 仍为 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`，池入口及 Pool/Pools 源码可定位，未运行 Java。

本次重新读取本地 `TestResults/schema-catalog/` 的已有 TRX，核实以下**前次模块执行证据**，未重新运行测试：

| 报告 | 读取结果 | 边界 |
| --- | --- | --- |
| `unit-final.trx` | 3693 总数，3693 通过，0 失败 | 完整单测；不是本次文档修改后新执行的结果 |
| `consumer-final.trx` | 66 总数，66 通过，0 失败 | 受影响消费者子集；不是完整消费者或五个独立运行时测试 |
| `di-final.trx` | 14 总数，14 通过，0 失败 | DI 测试 |
| `integration-final.trx` | 81 总数，9 通过，72 条结果为 `NotExecuted` | 原始 Counters 的 `notExecuted=0`，逐条结果的 outcome 才记录 72 跳过；不能只读取该计数器，也不能写为 81 个实库通过 |

已有 `TestResults-schema-build.log` 记录构建成功、0 警告/0 错误。这些忽略目录中的证据未随 Git 分发，原命令、退出码、失败回归与修复过程保留于第 15.9 节所链接的模块台账。压缩模块报告曾缺失的历史快照保留于第 15.8 节，不覆盖为新的验证声明。

本次补齐缺项分类、P1-11 契约验收清单及 DataTypes/自动参数推导/KeyInfo 的独立边界；同步报告和进度文档。只进行文档结构、链接/锚点、23-ID/8-工作包覆盖和 diff 检查；不修改产品代码，不重跑构建/测试/包验证，不连接真实数据库，不关闭任何缺口。

### 15.11 2026-10-25 未提交凭据模块的文档检查点

本节为修复前历史检查点，已由第 15.12 节的最终结果取代；失败与跳过记录保留，不代表当前回归状态。

本次交付以 `2dd1893` 为已提交基线，另行核对已有工作区开发，不修改产品或测试代码，不新启动测试，也不把开发过程中的通过结果替代全量门槛。本地报告位于忽略目录 `TestResults/credential-rotation/`，未随 Git 分发；下表为读取原始报告的结果。

| 证据 | 当前读取结果 | 判定与限制 |
| --- | --- | --- |
| `TestResults-credential-build-final.log` | Release 构建 0 警告、0 错误，11.18 秒 | 已有执行日志；编译成功不表示回归通过 |
| `focused-final.trx` | 71 通过，0 失败/跳过 | 凭据聚焦用例；不能替代全量单测 |
| `unit-final.trx` | 3764 总数，3762 通过、2 失败，进程退出 1 | **模块交付阻塞**；两项均为证书预热回滚测试的同步/异步分支 |
| `di-final.trx` | 14 通过 | 本地 DI 用例 |
| `integration-final.trx` | 9 通过、72 条结果为 NotExecuted | 未配置实库 profile；原 summary 的 notExecuted=0，须读取逐条结果，不能写成 81 个实库通过 |
| `consumer-final.trx` | 885 总数，853 通过、32 失败，进程退出 1 | **模块交付阻塞**；受影响消费者子集，非完整消费者套件，32 项新增凭据用例均未进入握手验证 |

两项失败位于 `PooledClientCertificateTests.FailedCertificateWarmupRollsBackAndRemovesItsPool`：第 181 行反射断言 registry `Get` 参数个数为 4，当前工作区因新增可选 credential provider 参数变为 5。该证据确定测试接线尚未同步；断言失败使后面的重建池断言未执行，不能据此宣称完整清理已通过，也不能仅凭此认定产品发生泄漏。后续必须更新调用/回归并重新验证，保留本次失败报告。

32 项消费者失败均位于 `CredentialProviderConsumerTests.EveryAssetAuthenticatesReusesRotatesAndFailsClosed` 第 41 行：`Activator.CreateInstance(providerType, asynchronous)` 绑定了 `(Type, bool nonPublic)` 重载，尝试调用不存在的无参构造函数，而 helper 实际只有 `(bool asynchronous)` 构造函数，因此抛出 MissingMethodException。后续需显式传构造参数数组并重跑；当前失败发生在初始化测试提供者时，不能记为五资产凭据握手/轮换已通过。以上两类测试接线问题均归入 P1-11 的交付阻塞，不新增或替换 23 个能力 ID。

工作区源码已有每次逻辑 Open 的同步/异步凭据快照、全部口令替换、提供者引用隔离、Version 排序、旧代次退休、过期/取消与统一建连预算、错误脱敏。预热失败回滚改为只销毁仍闲置的本次会话，以保护已被其他 Open 借出的会话；该并发路径和证书预热的回归须共同验收。具体契约见 [credentials.md](credentials.md)，是开发快照，尚非已提交/已发布 API。

P1-11 保持开发中；P0-01/02、P1-04 及其他 ID 的真实服务器、长期并发和资源隔离验收不因这批聚焦测试而关闭。消费者初始化和证书反射接线修复、全量回归、必要的独立复核、格式/包检查及模块提交仍须完成。当前没有真实凭据服务、认证插件轮换、KDC、OBProxy 或跨平台新增报告。

本次文档校验通过：9 份 Markdown 的 97 个本地链接/锚点、代码围栏，三张 23-ID 表、8 个工作包的唯一归属，以及变更文档的 UTF-8/CRLF 和 `git diff --check`。未修改已有产品/测试代码，也未提交工作区开发内容。

已有执行命令对应以下结果；本次仅核对日志/TRX，未再次运行。修复后应在同一 Release 构建上重新取得结果，并使用新的报告名保留失败证据：

```powershell
dotnet build OceanBaseConnector.slnx -c Release --no-restore
dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-restore --no-build --logger 'trx;LogFileName=unit-final.trx' --results-directory TestResults/credential-rotation
dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-restore --no-build --filter 'FullyQualifiedName~Credential|FullyQualifiedName~Pool|FullyQualifiedName~Topology|FullyQualifiedName~Tls|FullyQualifiedName~DataSource|FullyQualifiedName~Transaction' --logger 'trx;LogFileName=consumer-final.trx' --results-directory TestResults/credential-rotation
dotnet test tests/OceanBaseConnector.DependencyInjection.Tests -c Release --no-restore --no-build --logger 'trx;LogFileName=di-final.trx' --results-directory TestResults/credential-rotation
dotnet test tests/OceanBaseConnector.IntegrationTests -c Release --no-restore --no-build --logger 'trx;LogFileName=integration-final.trx' --results-directory TestResults/credential-rotation
```

### 15.12 2026-10-25 凭据提供与轮换本地交付

以 `2dd1893` 为开发起点完成 P1-11。五资产新增 `IOceanBaseCredentialProvider`、不可变 context/credential 和连接 `CredentialProvider`；现代 builder 新增 `UseCredentialProvider`，data source 固定捕获调用方提供者引用，不接管其生命周期。每次逻辑 Open 获取一次快照，包含闲置复用；替换用户、主口令及全部附加口令，同一快照用于预热和端点重试。提供者负责线程安全、缓存及刷新合并。

普通池增加提供者引用身份隔离；递增 Version 原子退休旧代次，低版本或同版本不同认证材料拒绝。闲置会话关闭；借出/在途会话不被中断，继续占用容量且归还销毁，旧快照不得继续预热或选取新租约。过期在获取、认证写入、握手完成、最终逻辑 Open 发布、闲置复用及归还/reset 后校验；同版本延长过期不延长已认证物理会话原有期限。活动命令不会仅因登录凭据过期而中断。同步取消需提供者合作，异步取消忽略迟到结果并观察迟到 fault；获取、发现、排队、预热、探活和握手共享连接预算。错误脱敏不保留可能带秘密的提供者异常。完整契约见 [credentials.md](credentials.md)。

独立走查发现并已复现/修复：闲置 COM_PING 期间逻辑快照过期；预热回滚错误销毁已被其他请求借出的会话；无限池等待未受凭据过期限制；Opening 日志回调延迟后发布过期逻辑快照。预热回滚现在只在池锁内销毁仍闲置的会话。RED 报告 `review-expiry-red.trx`、`review-warmup-red.trx` 和 `review-publication-red.trx` 保留；最终复核未发现剩余 Critical/Important。第 15.11 节的单测/消费者失败也保留：registry 清理测试改直接调用并保留实际 `NotSame` 验证；消费者显式传构造参数数组，消除错误 Activator 重载，32 项现在进入真实本地握手/轮换断言。

| 最终证据 | 结果 | 范围 |
| --- | --- | --- |
| `TestResults-credential-build-recheck.log` | Release 构建退出 0，0 警告/0 错误 | 全 solution，五资产编译 |
| `focused-recheck.trx` | 97 通过，0 失败/跳过 | 凭据 + 客户端证书池化聚焦回归 |
| `consumer-credential-recheck.trx` | 32 通过，0 失败/跳过 | 五资产普通连接、三现代资产私有源，双模式 × 同步/异步 |
| `unit-recheck.trx` | 3764 通过，0 失败/跳过，退出 0 | 完整单测 |
| `consumer-recheck.trx` | 885 通过，0 失败/跳过，退出 0 | 受影响消费者子集，**不是完整消费者套件** |
| `di-recheck.trx` | 14 通过，退出 0 | DI |
| `integration-recheck.trx` | 9 通过、72 条 NotExecuted，退出 0 | 未配置真实 profile；summary 的 notExecuted=0，不能作为实库完成依据 |
| `TestResults-credential-format-recheck.log` | 变更及新增 C# 的 format verify 退出 0 | 有工作区加载警告，未隐藏为无警告检查 |
| `TestResults-credential-pack.log` | 本地 pack 退出 0 | nupkg 的五资产 DLL SHA-256 与已验证 Release 输出一致，五依赖组和当前 README 已检查；未发布 |

所有报告位于本地忽略目录 `TestResults/credential-rotation/`，日志位于仓库根 `TestResults-credential-*.log`，未随 Git 分发。最终构建后未在消费者执行中重建 DLL。五资产消费者运行在 .NET 10 主机，不代表五种独立运行时；本次仅生成/核对包内容，没有新增独立 nupkg 运行时消费报告。未新增依赖，未重跑在线漏洞源审计。

最终重跑命令如下（结果目录同上）：

```powershell
dotnet build OceanBaseConnector.slnx -c Release --no-restore
dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-restore --no-build --filter 'FullyQualifiedName~CredentialProvider|FullyQualifiedName~PooledClientCertificate' --logger 'trx;LogFileName=focused-recheck.trx' --results-directory TestResults/credential-rotation
dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-restore --no-build --filter 'FullyQualifiedName~CredentialProvider' --logger 'trx;LogFileName=consumer-credential-recheck.trx' --results-directory TestResults/credential-rotation
dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-restore --no-build --logger 'trx;LogFileName=unit-recheck.trx' --results-directory TestResults/credential-rotation
dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-restore --no-build --filter 'FullyQualifiedName~Credential|FullyQualifiedName~Pool|FullyQualifiedName~Topology|FullyQualifiedName~Tls|FullyQualifiedName~DataSource|FullyQualifiedName~Transaction' --logger 'trx;LogFileName=consumer-recheck.trx' --results-directory TestResults/credential-rotation
dotnet test tests/OceanBaseConnector.DependencyInjection.Tests -c Release --no-restore --no-build --logger 'trx;LogFileName=di-recheck.trx' --results-directory TestResults/credential-rotation
dotnet test tests/OceanBaseConnector.IntegrationTests -c Release --no-restore --no-build --logger 'trx;LogFileName=integration-recheck.trx' --results-directory TestResults/credential-rotation
$credentialChanged = @(git diff --name-only -- '*.cs')
$credentialAdded = @(git ls-files --others --exclude-standard -- '*.cs')
$credentialFiles = @($credentialChanged + $credentialAdded | Sort-Object -Unique)
dotnet format OceanBaseConnector.slnx --no-restore --verify-no-changes --include $credentialFiles
dotnet pack src/OceanBaseConnector/OceanBaseConnector.csproj -c Release --no-build --no-restore -o TestResults/credential-rotation/packages
```

最终文档检查通过：9 份 Markdown、100 个本地链接/锚点、代码围栏、三张 23-ID 表、8 个工作包对全部 ID 的唯一覆盖，以及变更文档 UTF-8/CRLF 和 `git diff --check`。

P1-11 更新为“本地实现；真实凭据服务/plugin/KDC 矩阵未验证”。短期凭据服务、真实多步认证/轮换、OBProxy 和跨平台仍须验收；P0-01/02、P1-04 及其他能力 ID 不因本次交付关闭，完整驱动目标继续开放。第 15.11 节是修复前历史检查点，不代表本节重跑后的当前状态。

### 15.13 2026-10-25 DataTypes 未提交开发与文档交付检查点

本节为修复前历史检查点；失败和覆盖遗漏已由第 15.14 节修复及重跑结果取代，原证据保留，不作为当前模块状态。

本次按完整文档交付要求核对当前 `master`、源码、既有日志/TRX 和 Java 参考，不修改产品/测试代码、不启动新的构建或测试、不提交或推送。已提交基线为 `f815c16`；工作区的 DataTypes 实现及已有文档变更保留。第 15.12 节的凭据结果是该提交的历史通过记录，不能覆盖下面的当前失败。

**能力状态。** 工作区在公开集合清单末尾加入静态 `DataTypes`，Oracle 20/MySQL 18 个集合；已提交版本仍是 19/17。表包含标准 23 列和 9 个扩展列，`ProviderDbType` 为 `OceanBaseDbType` 整数，CLR 名称、普通/provider-specific 表示、精度/scale 条件、参数/结果支持、必要类型名、字符集/会话/协商前提均有描述。未知服务器属性保留 DBNull。返回独立 DataTable，不访问目录；继续要求连接已打开且空闲，并受现有事务/XA/取消保护。具体开发快照见 [Schema 契约](schema.md)。这不表示所有声明都已通过服务器建表或绑定验收。

**已有报告逐项复核。** 报告位于本地忽略目录 `TestResults/data-types/`，未随 Git 分发；本节将关键数值、失败及覆盖限制写入可审阅文档。

| 报告/日志 | 读取结果 | 判定 |
| --- | --- | --- |
| `TestResults-data-types-build-final.log` | Release 构建 0 警告/0 错误，11.86 秒 | 编译证据，不代替回归通过 |
| `api-red.trx` | 26 失败、1 通过 | 初始缺少公开集合的 RED，保留 |
| `behavior-green.trx` | 27 通过 | 早期聚焦结果；不能覆盖之后的全量失败 |
| `review-contract-red.trx` | 8 失败 | 缺少 ResultTypeCondition 的 RED，保留 |
| `focused-green.trx` | 74 通过、1 失败 | Oracle RAW 期望 wire code 252，实际 203；当前测试已改 203，但该失败报告仍保留，不改名为通过 |
| `unit-final.trx` | 总数 3792，3791 通过、1 失败；进程退出 1 | **当前交付阻塞**；不能写成全部通过 |
| `consumer-final.trx` | 50 通过 | 筛选 `FullyQualifiedName~Schema`，不包含下面的 16 个新增断言所在用例，不能声称 DataTypes 五资产通过 |
| `di-final.trx` | 14 通过 | DI 本地证据 |
| `integration-final.trx` | 9 通过、72 条结果 NotExecuted | 未配置真实 profile；summary 的 notExecuted=0 与逐条结果不一致，按逐条结果保留跳过 |
| `TestResults-data-types-format-final.log` | 留有工作区加载警告 | 格式日志不是测试或服务器兼容性证据；本次不重新运行格式检查 |

全量失败为 `TextCommandTests.DataTypesParameterIdentitiesBindActualScalarValues(mysql: False)`，异常为 `NotSupportedException: The session national character set is unknown.`。该测试含 NCHAR/NVARCHAR2，直接调用 `PreparedParameters.Encode` 时没有传第三个 `nationalCharacterSet` 参数；编码器对 type 207/208 要求可信 session national charset，缺省为 null 并拒绝。这确定了失败触发链，尚不能据此认定服务器 national 字符集路径有缺陷。后续应提供可信字符集完成正向编码回归，同时保留未知字符集明确拒绝的用例，不能移除生产校验或把类型表的条件改写为无条件支持。当前 Oracle 用例在此抛出，因此其后 timestamp/interval 样本也不能从该次报告推定已通过。

新增 DataTypes 消费者断言实际位于 `Ob20ConsumerTests.CatalogEveryAssetExposesModeAwareDirectories`，其完整名称不含 Schema。数据源共生成 16 个案例：两个 netstandard 资产各仅同步 × 双模式，三个现代资产各同步/异步 × 双模式，全部仍在 .NET 10 主机内加载。下一轮应显式筛选此方法，确认 TRX 中实际列出 16 项并全部通过；不能只看到进程退出 0 就宣称覆盖。

后续验证入口如下，**本次未执行**；先修复测试前提并重建，再以新报告名保留本次失败。消费者运行期间不得重建其加载的 DLL。

```powershell
dotnet build OceanBaseConnector.slnx -c Release --no-restore
dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-restore --no-build --filter 'FullyQualifiedName~DataTypes' --logger 'trx;LogFileName=focused-recheck.trx' --results-directory TestResults/data-types
dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-restore --no-build --logger 'trx;LogFileName=unit-recheck.trx' --results-directory TestResults/data-types
dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-restore --no-build --filter 'FullyQualifiedName~Schema|FullyQualifiedName~CatalogEveryAssetExposesModeAwareDirectories' --logger 'trx;LogFileName=consumer-recheck.trx' --results-directory TestResults/data-types
```

P1-10 仍为部分实现；DataTypes 子项为开发中，自动参数推导仍缺失，真实目录/权限/版本/OBProxy/字符集与 DDL 验收继续开放。独立静态走查不能抵消失败或缺失的执行证据；修复后须重新核查再决定交付。没有新增实库、独立运行时、nupkg 消费、非 Windows 或压力报告，也未关闭任何 ID/工作包。

本次文档校验通过：9 份 Markdown 的 115 个本地链接/锚点、代码围栏、UTF-8 无 BOM/CRLF；三张主表各完整保留 23 个唯一 ID，8 个工作包对 23 项唯一覆盖，原进度任务表行未改变，`git diff --check` 通过。

### 15.14 2026-10-25 DataTypes 本地交付

以 `f815c16` 为开发起点完成 `GetSchema("DataTypes")`。Oracle 20/MySQL 18 个集合；新增静态类型表不发送目录请求、无限制参数，每次返回独立的 invariant-culture DataTable，保留 open/idle、ambient/XA 和取消保护。表报告标准 23 列与 9 个扩展列；ProviderDbType 使用既有 OceanBaseDbType 整数，普通/provider-specific CLR 表示、精度/scale 条件、参数/结果方向、类型名及编码/会话/协商前提有明确契约，未知服务器属性保留 DBNull。详见 [Schema 契约](schema.md)。不增加自动参数推导、KeyInfo 或 adapter。

修复和独立走查：

- 保留初始缺少集合、ResultTypeCondition 和 RAW 252/203 期望错误的 RED。前轮全量 3791 通过/1 失败由测试漏传 national charset 引发；现在为正向样本提供可信 UTF-16，同时断言未知 charset 拒绝及“目标”的实际编码字节，生产校验未移除。Oracle 后续 timestamp/interval 样本也进入成功路径。
- 原 `FullyQualifiedName~Schema` 漏掉 `Ob20ConsumerTests.CatalogEveryAssetExposesModeAwareDirectories`；最终显式合并该方法筛选，TRX 中核对新增断言所在 16 项均实际执行并通过。
- 独立 reviewer `review_data_types_final` 发现 Integration/Acceptance 两处仍断言 Oracle19/MySQL17；已同步为20/18并复核。最终无剩余 Critical/Important；静态走查不替代实库验收。

| 最终证据 | 结果 | 范围与限制 |
| --- | --- | --- |
| `TestResults-data-types-build-verified.log` | Release 构建退出 0，0 警告/0 错误，3.42 秒 | 包含走查修正后的完整 solution；此前 recheck 构建同为 0 警告/0 错误 |
| `focused-recheck.trx` | 29 通过，退出 0 | DataTypes 聚焦回归，含实际参数及结果列映射 |
| `unit-recheck.trx` | 3792 通过，退出 0 | national charset 修复后的第一轮完整单测 |
| `unit-verified.trx` | 3792 通过，0 失败/跳过，退出 0 | 纳入实库入口数量修正后的最终构建，完整单测 |
| `consumer-verified.trx` | 66 通过，0 失败/跳过，退出 0 | Schema + Catalog 受影响子集，含实际 DataTypes 16 项；**不是完整消费者套件** |
| `di-verified.trx` | 14 通过，退出 0 | 同一最终构建的 DI 回归 |
| `integration-verified.trx` | 9 通过、72 条 NotExecuted，退出 0 | 未配置真实 profile；summary notExecuted=0 不代表无跳过 |
| `TestResults-data-types-format-verified.log` | 所有新增/变更 C# format verify 退出 0 | 保留工作区加载警告；包含 Integration/Acceptance 修正 |
| `TestResults-data-types-acceptance-list.log` | `--list` 退出 0 | 仅确认 runner 加载/枚举，不连接服务器、不算 acceptance 通过 |
| `TestResults-data-types-pack.log` | 本地 pack 退出 0 | nupkg 五资产 DLL SHA-256 与最终 Release 输出一致，README 字节一致、五依赖组已核对；未发布或新增独立 nupkg 运行时消费 |

报告位于本地忽略目录 `TestResults/data-types/`，日志位于仓库根 `TestResults-data-types-*.log`，未随 Git 分发。原始失败、早期窄筛选及最终新报告分名保留。消费者运行期间未重建 DLL；五资产均在 .NET 10 主机加载，不代表五个独立运行时。没有新增真实 OceanBase/OBProxy/DDL/字符集、非 Windows 或长期压力报告。

最终验证命令如下，消费者筛选按完整用例名核对，不能只依据进程退出码判断覆盖：

```powershell
dotnet build OceanBaseConnector.slnx -c Release --no-restore
dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-restore --no-build --filter 'FullyQualifiedName~DataTypes' --logger 'trx;LogFileName=focused-recheck.trx' --results-directory TestResults/data-types
dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-restore --no-build --logger 'trx;LogFileName=unit-verified.trx' --results-directory TestResults/data-types
dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-restore --no-build --filter 'FullyQualifiedName~Schema|FullyQualifiedName~CatalogEveryAssetExposesModeAwareDirectories' --logger 'trx;LogFileName=consumer-verified.trx' --results-directory TestResults/data-types
dotnet test tests/OceanBaseConnector.DependencyInjection.Tests -c Release --no-restore --no-build --logger 'trx;LogFileName=di-verified.trx' --results-directory TestResults/data-types
dotnet test tests/OceanBaseConnector.IntegrationTests -c Release --no-restore --no-build --logger 'trx;LogFileName=integration-verified.trx' --results-directory TestResults/data-types
$dataTypesFiles = @(@(git diff --name-only -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs') | Sort-Object -Unique)
dotnet format OceanBaseConnector.slnx --no-restore --verify-no-changes --include $dataTypesFiles
dotnet run --project tests/OceanBaseConnector.Acceptance -c Release --no-restore --no-build -- --list
```

最终文档校验通过：9 份 Markdown、116 个本地链接/锚点、代码围栏、UTF-8 无 BOM/CRLF、三张 23-ID 表、8 个工作包的唯一覆盖、历史任务表行保持，以及 `git diff --check`。

P1-10 仍为“部分实现；常用目录及静态 DataTypes 本地完成”。自动参数推导、真实目录/权限/版本/代理和服务器类型/DDL 验收继续开放；P1-05 的 KeyInfo、P2-01 的 adapter、其他 23 项及完整驱动目标不因本模块交付关闭。

### 15.15 2026-10-25 完整文档复核与有符号 Scale 开发检查点

本节是修复前历史检查点；消费者失败和后续筛选遗漏已由第 15.16 节修复/补跑结果取代，原证据保留。

本次按“根据走查问题给出完整文档”核对 `master`、Java 参考源码、工作区和既有日志/TRX；仅整理文档，不继续修改产品/测试代码、不启动新的构建/测试、不提交或推送。接收前一轮已经启动的完整单测和 format 进程结果，实际退出码均为 0。已提交基线为 `5146b62`；第 15.14 节 DataTypes 交付记录仍有效，但不能替代以下未提交变更的验证。

**文档覆盖。** [走查报告](obconnector-dotnet-driver-audit.md)供评审阅读；本文第 4/14 节保留全部 23 个 ID（P0 六项、P1 十一项、P2 六项），第 5 节列模块契约，第 7/12 节列测试与发布门槛，第 16 节的八个工作包安排依赖与验收产物。[进度台账](obconnector-dotnet-progress.md)保留执行历史，[兼容性记录](compatibility.md)保留真实成功、失败和跳过。负责人、日期和首版承诺支持范围尚未分配，阶段不是排期承诺。

**Java 与池结论。** 本次重新读取 `OceanBasePoolDataSource`、`OceanBasePooledConnection`、`internal/util/pool/Pool`/`Pools` 源码并核对 HEAD，确认 Java 有池入口与池实现。.NET 也已有基础物理会话池，默认 `Pooling=false`；剩余是完整状态隔离、资源生命周期、并发/长期运行和真实 reset 验收。没有运行 Java 负载测试，不据此认定两个驱动已同等完整。

**P1-08 开发范围。** 未提交文件为 `OceanBaseParameter.cs`、`PreparedParameters.cs`、`ClientPreparedSql.cs`、`DecimalScaleTests.cs`、`DecimalScaleConsumerTests.cs`。工作区增加 decimal 专用的 nullable signed override `DecimalScale`：setter 接受 -84..127，MySQL 编码仍仅接受 0..30 且声明 precision 时 scale 不超过 precision；Oracle 接受 -84..127。普通 `Scale` 赋值清除 override，非空 override 清除旧 Scale；负 override 的 byte Scale getter 返回 0，实际声明读取 DecimalScale。清除 override 后按普通 Scale/值推断，不恢复被覆盖的旧声明。server prepare 与客户端 fallback 共用精度约束，拒绝需舍入的值；typed NULL、非 decimal 和 complex/LOB 快路径也不得静默忽略声明。该段是开发快照，尚未成为已交付契约。

| 既有报告/日志 | 实际结果 | 判定 |
| --- | --- | --- |
| `api-red.trx` | 6 失败 | 初始缺少公开属性的 RED，保留 |
| `focused-green.trx` | 6 通过 | 初始聚焦回归 |
| `codec-green.trx` | 89 通过 | Scale/WideDecimal/ClientPrepareFallback 子集，不是全套消费者 |
| `TestResults-decimal-scale-build.log` | Release 0 警告/0 错误，7.70 秒 | 编译证据 |
| `unit-final.trx` | 3819 通过，0 失败/跳过；进程退出 0 | 当前未提交源码的完整单测 |
| `consumer-focused.trx` | 10 通过、10 失败 | **交付阻塞**；MySQL 用例通过，全部 Oracle 用例失败 |
| `TestResults-decimal-scale-format.log` | format verify 退出 0，有工作区加载警告 | 格式证据，不抵消消费者失败 |

报告位于本地忽略目录 `TestResults/decimal-scale/`，日志为根目录 `TestResults-decimal-scale-*.log`，未随 Git 分发。消费者覆盖五个目标资产 × 同步/异步 × 两种模式，共 20 项；全部在 .NET 10 主机内加载，不代表五种独立运行时。

**当前失败。** `DecimalScaleEveryAssetConstrainsWireAndPreservesTheSessionAfterRejection` 的 Oracle 本地服务端 fixture 在下一次有效执行时预期命令 23（COM_STMT_EXECUTE），实际收到命令 3 和 SELECT 前缀，断言终止服务端后客户端读取流关闭，报 `Command initialization failed before execution`。源码存在 Oracle 会话时区/字符集探测路径；还需核对 fixture 的会话状态 OK 与探测处理，才能确定并修复原因。本记录不把该失败认定为真实 Oracle NUMBER 编码失败，也不把它改写为通过；不能删除生产探测/校验来迎合 fixture。

**后续交付条件。** 先定位并修复消费者失败，保留原报告并用新文件名重跑；确认 20 项实际进入断言。再完成受影响调用/Batch/array/fallback 回归、必要的 DI/Integration/包检查和公共 API 文档同步。独立 reviewer `review_decimal_scale` 静态复核无剩余 Critical/Important，但未执行构建/测试，静态结论不能代替失败回归。只有验证完成后才能提交模块并更新能力状态。

参数自动推导仍明确缺失。后续必须处理 Oracle owner/package/overload、方向、负 scale、复杂类型及 MySQL unsigned/type attributes；失败/取消时保留原参数，使用相同会话/事务/超时且不能执行目标过程。目录存在不表示已支持推导或所有过程执行形状，见第 5.6 节。

本次没有新增真实数据库/OBProxy/TLS/KDC、非 Windows、独立运行时、压力或新包消费证据；未关闭任何缺口/工作包，历史失败和跳过保留。文档完成不等于完整驱动完成。

文档校验通过：9 份 Markdown、124 个本地链接/锚点、UTF-8 无 BOM/CRLF、代码围栏、三张 23-ID 表、八个工作包对全部 ID 的唯一覆盖、历史任务表行保持，以及 `git diff --check`。

### 15.16 2026-10-25 有符号 decimal scale 本地交付

以 `5146b62` 为开发起点，为 P1-08 增加 `OceanBaseParameter.DecimalScale`，解决 Oracle NUMBER 负 scale 无法通过 byte Scale 显式声明的问题，也是后续 P1-10 参数推导的表示前提。实现复用既有 DecimalCodec，不增加依赖，不更改结果解码或默认推断；P1-08 其余类型/字符集和 P1-10 自动参数推导继续开放。

**公开契约。** nullable int 的 DecimalScale 只适用于 decimal 参数，setter 范围 -84..127；编码时按模式限制 Oracle precision <=38/scale -84..127、MySQL precision <=65/scale 0..30，MySQL 非零 precision 时 scale 不得超过 precision。typed NULL 也校验声明；非 decimal、complex、LOB/cursor 快路径不得忽略 override。非空 DecimalScale 清除旧 byte Scale；Scale setter 清除 override；负 override 的 Scale getter 为 0，实际值从 DecimalScale 读取；清 null 不恢复被覆盖的旧 Scale，而是按剩余声明/值推断。Snapshot、普通执行、Oracle PrepareExecute/Batch 及支持的客户端 fallback 共享精确约束，需舍入时拒绝。array 本身不因此获得新的元素类型支持。见[完整参数契约](obconnector-dotnet-architecture-design.md#decimal-parameter-scale)。

**失败、修复与覆盖。**

- 保留初始缺少属性的 6 项 RED；单元测试覆盖正/负值的实际编码、Oracle 两端非零值、typed NULL、setter 状态不变、精度溢出/拒绝舍入、Scale 优先级/清除、snapshot、MySQL 模式边界和 client fallback。
- 旧消费者 `consumer-focused.trx` 的 10 通过/10 失败保留。raw OK 没有 tracking 变量，`ObserveSessionState` → `ApplySessionVariables` 清除时区/charset，下一 Oracle 命令合法发送 SELECT 探测；fixture 误预期 COM_STMT_EXECUTE 后断流。改用现有 `Report(false,1)` 返回完整会话状态；未修改生产探测或精度校验。修复后 20 项逐字断言第一次执行包、本地拒绝后的再次有效执行和连接复用。
- 第一轮 `consumer-verified.trx` 86 项通过，但筛选 ClientPrepare/ArrayBinding 没命中共享 Ob20ConsumerTests 类中的实际方法。该报告只证明 Scale/Batch/Catalog；最终以实际方法名补跑为 `consumer-covered.trx`，TRX 核实 Scale20、fallback30、array30、Batch50、Catalog16，共146项。
- 独立 reviewer `review_decimal_scale` 复核声明、typed NULL/快路径、snapshot/Oracle plan/array/Batch/fallback 和 fixture 根因，无剩余 Critical/Important。静态走查不替代执行或实库证据。

| 最终证据 | 结果 | 范围与限制 |
| --- | --- | --- |
| `TestResults-decimal-scale-build-recheck.log` | Release 退出 0，0 警告/0 错误，4.03 秒 | 完整 solution；修复 fixture 后未在消费者运行中重建 DLL |
| `codec-green.trx` | 89 通过 | Scale/WideDecimal/ClientPrepareFallback 聚焦单测；较早记录，最终完整回归见下行 |
| `unit-verified.trx` | 3819 通过，0 失败/跳过，退出 0 | 最终构建的完整单测；此前 unit-final 同为3819通过 |
| `consumer-recheck.trx` | 20 通过，退出 0 | 原来失败的 Scale 五资产 × 同步/异步 × 双模式 |
| `consumer-covered.trx` | 146 通过，0 失败/跳过，退出 0 | 实际方法名补齐后的受影响子集，**不是完整消费者套件** |
| `di-verified.trx` | 14 通过，退出 0 | DI 回归 |
| `integration-verified.trx` | 9 通过、72 条 NotExecuted，退出 0 | 未配置真实 profile；跳过不是服务器通过 |
| `TestResults-decimal-scale-format-verified.log` | format verify 退出 0，有工作区加载警告 | 修改/新增 C# 文件 |
| `TestResults-decimal-scale-pack.log` | 本地 pack 退出 0 | 五资产 DLL SHA-256 与已验证 Release 输出相同、内嵌 README 字节及五依赖组匹配；未发布 |

报告位于忽略目录 `TestResults/decimal-scale/`，根日志为 `TestResults-decimal-scale-*.log`，未随 Git 分发。五个资产消费者运行在 .NET 10 主机，不代表五种独立运行时；没有新增实库/OBProxy NUMBER 或 DECIMAL、非 Windows、压力、KDC、TLS 或独立 nupkg 运行时消费证据。没有新增依赖，未重跑在线漏洞源审计。

主要最终执行命令如下；全部保留新报告名，未覆盖旧失败：

```powershell
dotnet build OceanBaseConnector.slnx -c Release --no-restore

dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-restore --no-build --logger 'trx;LogFileName=unit-verified.trx' --results-directory TestResults/decimal-scale

dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-restore --no-build --filter 'FullyQualifiedName~DecimalScale|FullyQualifiedName~EveryAssetFallsBackOnlyAfterCompleteUnsupportedPrepare|FullyQualifiedName~Batch|FullyQualifiedName~EveryAssetExecutesArrayReturningAndKeepsOutputsAtomicAcrossOb20|FullyQualifiedName~CatalogEveryAssetExposesModeAwareDirectories' --logger 'trx;LogFileName=consumer-covered.trx' --results-directory TestResults/decimal-scale

dotnet test tests/OceanBaseConnector.DependencyInjection.Tests -c Release --no-restore --no-build --logger 'trx;LogFileName=di-verified.trx' --results-directory TestResults/decimal-scale

dotnet test tests/OceanBaseConnector.IntegrationTests -c Release --no-restore --no-build --logger 'trx;LogFileName=integration-verified.trx' --results-directory TestResults/decimal-scale

dotnet pack src/OceanBaseConnector/OceanBaseConnector.csproj -c Release --no-build --no-restore -o TestResults/decimal-scale/packages
```

最终文档检查通过：9 份 Markdown、131 个本地链接/锚点、UTF-8 无 BOM/CRLF、代码围栏、三张 23-ID 表、八个工作包的唯一覆盖和历史任务表行保持；`git diff --check` 通过。

P1-08 仅此参数声明子模块本地完成；23 个缺口、八个工作包和完整驱动目标仍按各自验收要求继续推进。后续 P1-10 需要实现自动参数推导，保留重载/方向/有符号 scale、类型属性与失败原子性，且不能因此宣称所有模式的过程执行形状已支持。

### 15.17 2026-10-25 参数推导开发与完整文档检查点

历史开发快照，后续修复及最终本地结果见第 15.18 节；原失败、警告和当时未执行项保留，不作为最终状态。

**状态：开发中，尚未提交，不作为已交付 API。** 本次文档复核的 HEAD 为 `2bbecac`，分支为 `master`；Java HEAD 经只读复核仍为 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`。Java 的 `OceanBasePoolDataSource`、`OceanBasePooledConnection`、`Pools` 和 `Pool` 源码存在；.NET 的池租用/归还、清池和 `Pooling=false` 默认值也已重新核实。连接池不是完全缺失，P0-01/02/06 继续追踪其隔离、故障、并发和真实拓扑验收。

#### 当时工作区实现边界

以下是工作区实现及后续验收契约，不能作为已发布包的使用承诺：

```csharp
void DeriveParameters(string? overload = null);
Task DeriveParametersAsync(CancellationToken cancellationToken = default);
Task DeriveParametersAsync(string? overload, CancellationToken cancellationToken = default);
```

入口位于 `OceanBaseCommand`，要求 `CommandType.StoredProcedure` 和打开且空闲的连接。读取目录后一次性替换参数，值初始化为 `DBNull.Value`；不会执行目标过程。MySQL 的 StoredProcedure 执行入口仍未支持，Oracle 的执行仍要求 PrepareExecute；目录推导成功不能证明目标过程可执行。

| 契约 | 当前实现与限制 |
| --- | --- |
| 名称与签名定位 | MySQL 接受一/二段名称及反引号；Oracle 接受一/二/三段名称，未引号名称转大写，引号名称保留大小写。Oracle 二段同时检查 owner.routine 与当前 schema 的 package.routine；有歧义则失败。未限定名称使用 DATABASE()/CURRENT_SCHEMA；只按可见目录定位，不承诺 synonym 自动解析。Oracle overload 是精确选择器，MySQL 不接受该选项。 |
| 元数据完整性 | 先选择唯一过程/函数，再按完整身份读取顶层参数；NULL DATA_TYPE 不能被过滤后伪装成无参数。重复/缺失位置、签名身份冲突、返回值不一致及不支持的属性明确拒绝。Oracle 参数名沿用 ASCII/unquoted 绑定限制和大小写不敏感的唯一性，MySQL 保留 Unicode 名称；返回参数名为 RETURN_VALUE，命名冲突拒绝。私有推导列不改变公开 GetSchema 表形状。 |
| 类型属性 | 保留 MySQL 整数 unsigned/ZEROFILL、TreatTinyAsBoolean、BIT 位宽和 decimal 声明一致性；不可表示的 unsigned decimal/float 拒绝。Oracle decimal 使用 DecimalScale 保留负 scale；字符长度与二进制字节长度分别映射 Size；未知/不可表示元数据拒绝。具体类型仍受既有 codec 和执行路径限制。 |
| 复杂类型 | 复用现有类型加载器，并保持同一预留物理会话、事务 epoch、超时和取消来源；package-local TYPE_SUBNAME 明确拒绝。已有复杂成员限制不变；失败前成功加载的有效类型缓存可能保留。 |
| 生命周期 | 整个推导预留同一会话，期间拒绝重叠 Execute/Prepare/Derive；沿用当前事务。CommandTimeout 是每次目录查询的预算，不是全部目录及类型查询的总截止时间。调用方 token、Cancel 和 Dispose 贯穿查询及查询间隙。 |
| 参数发布 | 集合成员版本校验与替换使用同一把锁；命令上下文、会话或事务变化会失败。失败/取消不发布半套参数；成功会替换旧参数和其值。该检查针对成员增删/替换，不承诺检测任意并发修改参数属性，调用方应避免此类并发。 |
| 限额与权限 | 每张目录表最多 1024 行、累计字符串 UTF-8 最多 4 MiB；沿用已有 reader/协议限制。权限不足应保留服务端错误，不能假造空签名或尝试更高权限目录。真实权限行为尚未验收。 |

源码入口：`src/OceanBaseConnector/OceanBaseCommand.Parameters.cs`、`OceanBaseConnection.Parameters.cs`、`Internal/Sql/RoutineParameterDerivation.cs`；关联修改覆盖 Command 忙状态、Schema routines、复杂类型加载及 ParameterCollection。测试入口为单测的 `DeriveParametersApiTests.cs`、`DeriveParametersMetadataTests.cs`、`DeriveParametersWireTests.cs`、`DeriveParametersLifecycleTests.cs`、`DeriveParametersComplexTests.cs`，以及消费者的 `DeriveParametersConsumerTests.cs`。

#### 已复核的本地证据与历史失败

以下报告来自已有开发运行，本次文档整理重新读取 TRX；没有为文档修改重跑单测。报告目录为忽略的 `TestResults/derive-parameters/`，应随模块交付归档，不能假定 Git 中包含这些文件。

| 报告 | 通过 / 失败 | 说明 |
| --- | --- | --- |
| api-red.trx | 0 / 3 | API 未实现时的 RED |
| api-green.trx | 3 / 0 | API 反射检查 |
| metadata-review-red.trx | 17 / 5 | BIT 位宽及 unsigned decimal/float 属性问题 |
| metadata-conflict-red.trx | 0 / 3 | decimal DTD 与精度/scale 冲突未拒绝 |
| metadata-green.trx | 28 / 1 | BIT 越界用例期望异常类型与既有 codec 不符；此报告本身不是通过 |
| wire-first.trx | 42 / 0 | 目录读取及相关协议回归子集 |
| lifecycle-first.trx | 48 / 1 | linked token 丢失调用方取消来源 |
| metadata-name-red.trx | 0 / 2 | MySQL Unicode 参数名及整数小数维度处理 |
| complex-first.trx | 8 / 1 | 复杂类型加载未转发原始取消来源 |
| behavior-green.trx | 132 / 0 | 修复后的参数推导、RoutineSchema、ComplexDictionary、ParameterCollection 相关子集；不是完整单测，也不是 132 项全新测试 |

收取此前已运行的 `dotnet build OceanBaseConnector.slnx -c Release --no-restore` 结果：退出码 0、0 错误、1 警告，日志 `TestResults-derive-parameters-build-first.log`。警告为消费者文件第 111 行的 xUnit2013（集合大小断言应使用 Assert.Single）。不能沿用上一模块的“0 警告”作为当前工作区结论。

新增消费者方法 `EveryAssetDerivesRoutineParametersWithSignedScaleAndAtomicFailure` 已编译，其数据源设计覆盖五资产 × 同步/异步 × 双模式共 20 项；本检查点没有消费者执行 TRX，不能记为 20 项通过。上一模块的 3819 单测、146 消费者、DI 14 和 Integration 9/72 属于第 15.16 节，不证明本次未提交修改已通过全量回归。

#### 当时模块交付前仍需完成

1. 补齐并运行 Oracle package/overload 的正向协议案例、函数返回顺序和名称冲突；覆盖本地事务绑定且无隐式提交、目录权限错误保持、集合 Add 与发布交错，以及 TYPE_OWNER/TYPE_NAME 不完整配对等边界。
2. 执行新增消费者的实际方法筛选，核对实际 20 项结果，避免通过筛选遗漏误报；五资产在 .NET 10 加载仍不是五个独立运行时验收。
3. 修复构建警告；在最终代码上完成 Release 构建、完整单测、受影响消费者、DI、Integration 和格式检查，记录命令、退出码及报告；失败/跳过保留原结果与原因。
4. 在最终代码和测试上完成独立走查、修复并复核；本检查点的静态走查反馈不替代上述执行门槛。同步公共契约及包说明后再作模块提交。
5. 单独取得真实双模式目录、权限、版本、过程/函数/重载、类型声明及 OBProxy 报告。局部模块交付也不关闭整个 P1-10 或完整驱动目标。

本次文档结构检查覆盖 9 份 Markdown、139 个本地链接/锚点、UTF-8 无 BOM/CRLF、代码围栏、三张 23-ID 表及八个工作包的唯一覆盖；进度页此前记录保持原文，`git diff --check` 通过。

本次仅更新文档，保留既有未提交产品/测试修改，不提交或推送。23 个 ID、八个工作包、旧失败和跳过不变；未新增真实数据库、OBProxy、TLS/KDC、独立运行时、跨平台、压力或本模块 nupkg 验收结论。

### 15.18 2026-10-25 自动参数推导修复与验证

**状态：该子模块本地实现及验证完成，真实互操作未验收。**

以 `2bbecac` 为开发起点增加 `OceanBaseCommand.DeriveParameters` 及两个异步重载，覆盖五个目标资产。公共签名和生命周期见[API 契约](obconnector-dotnet-architecture-design.md#routine-parameter-derivation)，类型映射和拒绝范围见[Schema 契约](schema.md#parameter-derivation)。第 15.17 节为修复前文档检查点，原失败与未执行项均保留。

实现先选择唯一可见的过程/函数身份，再读取完整顶层参数签名；Oracle owner/package/精确 overload、函数返回顺序和名称冲突、MySQL unsigned/ZEROFILL/BIT/decimal 属性、Oracle 有符号 scale 以及复杂类型描述均有明确处理。NULL 类型、缺损身份、重复/缺失序号和不可表示属性不被静默忽略。全过程复用预留会话、当前事务和 epoch，沿用父命令的每次查询超时及取消来源；最终按命令/会话/事务状态和集合成员版本原子发布。

本次关闭的确认问题及新增证据：

| 问题 | 复现与修复 |
| --- | --- |
| TYPE_OWNER/TYPE_NAME 仅有一项时被忽略 | `identity-red.trx` 为 6 通过/2 失败；补充身份配对检查。最终回归确认原参数引用、Owner、Value 保留，连接仍可用。 |
| 浮点维度与历史 YEAR 声明被丢弃 | 独立走查发现 FLOAT/DOUBLE 的宽度/小数维度没有表示契约，YEAR(2) 等却被映射为四位 Year。`declaration-red.trx` 六项先复现失败；现在明确拒绝不可表示属性，保留普通浮点与 YEAR/year(4) 的正向映射，失败保留原参数。 |
| 新消费者错误地在清理时只等 EOF | `consumer-first.trx` 20 项失败，均在目标断言完成后的 ObserveClose；失败路径释放预留后，metadata Dispose 合法发送 COM_STMT_CLOSE73。fixture 现在精确验证该包再等待关闭，`consumer-green.trx` 20 项通过；没有修改产品清理路径来迁就 fixture。 |
| 构建/格式问题 | 改用 Assert.Single 解决初次 xUnit2013 警告。初次格式检查退出 2（58 个空白诊断），限定本模块 14 个 C# 文件执行 whitespace 修正后，格式复核退出 0；工具仍报告 workspace-load warning。 |

新增边界还覆盖同步/异步 Oracle 三段 owner/package 和二段当前 schema/package 的重载选择、函数返回排序、返回名与大小写冲突、两个目录阶段的权限错误码/SQL state 保持、本地事务必须显式绑定且无隐式提交，以及 128 次并发 Add/派生发布的成员和所有权不丢失。现有取消/Dispose/timeout、命令/连接忙状态、集合 Clear/re-add、复杂类型同会话加载及取消来源回归全部保留。

报告存放在忽略的 `TestResults/derive-parameters/`，日志为根目录 `TestResults-derive-parameters-*.log`；以下只记录真实取得的执行结果：

| 检查 | 报告/日志 | 结果 |
| --- | --- | --- |
| 相关边界子集 | boundary-green.trx → declaration-green.trx | 149 → 159 通过，退出 0 |
| 新增五资产消费者 | consumer-green.trx | 实际 20 通过，五资产 × 同步/异步 × 双模式，退出 0 |
| 格式后 Release 构建 | build-verified.log | 0 警告/0 错误，退出 0 |
| 完整单测 | unit-verified.trx | 3900 通过，0 失败/跳过，退出 0；较前模块新增 81 项 |
| 完整消费者 | consumer-verified.trx | 2328 通过，0 失败/跳过，退出 0；已核对包含新增方法的全部 20 项 |
| DI | di-verified.trx | 14 通过，退出 0 |
| Integration | integration-verified.trx | 9 通过/72 个单独 NotExecuted，退出 0；没有新增真实互操作证据 |
| 格式复核 | format-verified.log | 14 个 C# 文件，退出 0；有 workspace-load warning |
| 本地 pack | pack-verified.log 及 packages/OceanBaseConnector.0.1.0.nupkg | 退出 0，五个 DLL hash 与当前资产一致、README 字节和五组 dependencies 一致；不代表独立运行时 nupkg 验收 |

独立 `review_derive_design` 最终源码复核无剩余 Critical/Important；浮点/YEAR 和元数据身份配对修复已复核。后续公共契约复核修正了“每张目录表 1024 行”的范围：该限额仅用于 routine/argument 表；命名复杂类型沿用既有成员/深度/缓存上限。审阅者未另行执行构建/测试，执行证据由主任务取得。

核心验证命令（TRX 和原始失败保留，格式仅改空白；其后完成最终构建与本地包检查）：

```powershell
dotnet build OceanBaseConnector.slnx -c Release --no-restore
dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-restore --no-build --logger 'trx;LogFileName=unit-verified.trx' --results-directory TestResults/derive-parameters
dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-restore --no-build --logger 'trx;LogFileName=consumer-verified.trx' --results-directory TestResults/derive-parameters
dotnet test tests/OceanBaseConnector.DependencyInjection.Tests -c Release --no-restore --no-build --logger 'trx;LogFileName=di-verified.trx' --results-directory TestResults/derive-parameters
dotnet test tests/OceanBaseConnector.IntegrationTests -c Release --no-restore --no-build --logger 'trx;LogFileName=integration-verified.trx' --results-directory TestResults/derive-parameters
```

最终文档复核通过：9 份 Markdown、146 个本地链接/锚点、UTF-8 无 BOM/CRLF、代码围栏、三张 23-ID 表、八个工作包唯一覆盖及此前进度记录保留；`git diff --check` 通过。最终包复核使用格式后构建产物。

此模块不执行目标、不扩大 MySQL StoredProcedure 或 Oracle 输出形状的支持、不提供 synonym 推导，不对任意并发参数属性修改作保证。每次查询预算不是总截止时间，失败可保留有效复杂类型缓存。真实 OceanBase/OBProxy 的字典/权限/声明/重载/版本报告、独立运行时与跨平台验收仍缺；P1-10 整体、23 个缺口、八个工作包和完整驱动目标继续开放。

## 16. 剩余交付工作包

以下拆分面向实施和评审，沿用第 4、14 节全部 23 个 ID；每个 ID 只归入一个主工作包，跨模块依赖不因此消失。它区分新增能力、已有实现的验证和范围决定，避免把已完成模块重新开发。所有工作包尚未整体关闭，负责人和交付日期未分配。

| 工作包 / 覆盖 ID | 剩余开发或范围决定 | 必须交付的验证产物 | 主要依赖 |
| --- | --- | --- | --- |
| W1 池与会话隔离：P0-01、P0-02、P0-06 | 补不可识别 session-state、动态 SQL/过程、reader/cursor/LOB/XA 的恢复或销毁规则；定义连接字符串归一化和是否需要统一 shutdown。已有容量、探活、预热、路由隔离、证书隔离、清池/退休及指标保留 | 双借用者污染/隔离报告；满池、清池、取消、预热、Dispose/reset、Close/Open 并发及长期运行资源曲线；双模式、直连/OBProxy reset 报告 | G0 契约；与 W2/W4 联合验收 |
| W2 事务与失败恢复：P0-03、P0-04、P0-05 | 闭合 ambient 本地事务、未知执行结果、断线后的连接/事务状态与应用重建契约；分布式提升明确拒绝，透明恢复另定范围 | 发送前/后、响应中、commit/rollback 回包丢失、超时/in-doubt、提前关闭的故障矩阵；异常分类、会话销毁及业务核对示例；无未知写 replay | W1 租约与隔离规则 |
| W3 协议选项：P1-01、P1-02、P1-03 | 标准 MySQL 压缩已本地实现；先确认客户端 OB20 压缩契约再补实现，并补 reroute；保留已实现的 opt-in 文本多语句和应用流 LOCAL INFILE，明确不支持的组合 | 客户端链路/版本/capability 依据；协商成功/拒绝、分片/序列/校验/解压限额；多结果中途错误；上传超限/取消/代理策略；真实 OceanBase/OBProxy 互操作 | G0 及第 16.3 节；reroute 依赖 W2/W4 |
| W4 信任、发现与凭据：P1-04、P1-09、P1-11 | 凭据提供/轮换已有本地实现，补真实凭据服务与插件验收；补发现与健康/退避策略，明确 OCP/TNS 或等价发现范围。现有 TLS 回调、证书副本及池身份无需重做 | 企业 CA/mTLS、证书与凭据过期/刷新失败、撤销、KDC/plugin 成功及拒绝；地址更新/错误身份/节点与代理故障；Windows/Linux 报告 | W1 池身份、W2 重试边界 |
| W5 命令、Oracle 与类型：P1-05、P1-06、P1-07、P1-08 | 补 KeyInfo；修正或明确 SchemaOnly 执行语义；补已承诺的 Oracle 输出方向/结果形状、复杂成员、DDL 缓存失效、类型码/字符集映射。有符号 DecimalScale 已本地完成，保留其精度和覆盖规则 | Command/Batch/REF CURSOR 行为表；NULL/方向/精度/溢出/编码/取消与资源释放矩阵；SchemaOnly 副作用/锁/大结果报告；服务端不支持形状单列 | G0；资源生命周期与 W1 联动 |
| W6 目录与 ADO.NET 生态：P1-10、P2-01 | 常用目录/限制参数及 DataTypes 已本地完成；自动参数推导已通过本地回归和独立走查（第 15.18 节）；补实库/权限/版本/代理矩阵；明确 DataAdapter/CommandBuilder 的实现或版本排除，不以 ADO.NET provider 代替 EF Core provider | 双模式目录/权限/大小写/空集合；类型映射与过程重载/参数方向；若纳入 adapter 则验证 CRUD、键与乐观并发；若排除则公开 API、异常及文档一致 | W5 键与结果元数据 |
| W7 性能与可观测性：P2-02、P2-03、P2-04 | 实现有限 statement cache；选择并实现承诺的 rewrite/multi-send/bulk 优化；补建连/探活分段及慢查询/协议诊断。保留顺序 Batch 和 Oracle array binding | 缓存容量/失效/关闭、批量部分失败/事务/取消/包限额；吞吐、延迟、峰值内存与资源上限对照；诊断脱敏和监听开销 | W1 generation/reset、W5 绑定规则 |
| W8 运行时与发布：P2-05、P2-06 | 明确版本、平台、TFM、Framework/ORM 的承诺范围；将六个 matrix.* 占位变成有环境和断言的案例；完成包名、来源/许可和发布审查 | 每个承诺 profile 的 nupkg consumer、实库/代理/TLS/认证/故障报告；保留 Oracle 失败及所有跳过原因；外部 coordinator crash recovery；可复现命令和清理结果 | 各包可并行积累证据，正式发布须满足第 7.3 节 |

### 16.1 压缩与 reroute 的独立交付边界

P1-02 包含三个独立子能力：标准 MySQL 压缩已有本地实现，OB20 压缩和 reroute 仍未交付。不能只开放开关或记录拒绝路径就宣布完成整个能力项：

1. **MySQL 压缩**：`Use Compression=true` 显式协商；最终认证 OK 后切换，支持压缩/原样块、块与 MySQL 包边界不对齐、外层序列回绕/逐命令重置，保持逻辑包上限。写缓存 64 KiB，接收和解压块受 24 位长度约束；严格检查完成状态、Adler、精确长度和尾随输入，失败/取消关闭传输。五个资产使用同一实现。当前与 `Use OB20=true` 的组合在联网前拒绝；真实 OceanBase/OBProxy 压缩矩阵未验证。
2. **OB20 压缩**：当前 `Ob20Packet.ParseHeader` 明确拒绝非零 uncompressed length。先完成第 16.3 节的客户端协议确认，再在保留连接/request/sequence 身份和头/载荷校验的基础上支持压缩；覆盖截断、损坏、超限、跨 frame 读取与取消，不能因解压引入无界内存。
3. **Proxy reroute**：当前 `Ob20Stream.TakeHeader` 拒绝未支持标志。需要明确可接受的代理响应、目标地址/TLS/租户校验、尝试次数与总预算、池路由更新及原请求状态；未知结果写入和活动事务不能因 reroute 被静默重放。

三项各自需要真实 OBProxy/服务器报告；本地编解码和 loopback 通过只能关闭本地开发验证。具体公共选项和支持的代理版本在实施前冻结，本文不把尚未实现的配置项写成现有 API。

### 16.2 关闭工作包和发布的规则

工作包关闭必须逐项满足第 14 节的最低证据及第 15.1 节的记录要求，再同步 README、兼容性、架构 API 和进度台账。明确排除某个可选能力时，保留原 ID 与排除理由，不能改写为“已实现”；承诺范围内的失败或缺证据不能用排除可选能力的方式掩盖。

优先收敛 W1/W2 的隔离和失败契约，再交付 W3/W4/W5 的功能与真实部署证据；W6/W7 按承诺范围推进，W8 随开发持续执行。任何阶段均保留“不自动重放未知写、TLS 不静默降级、失败会话不回池”的约束。达到第 7.3 节前，维持 experimental 表述。

### 16.3 OB20 压缩的客户端协议前提

本项继续保留在 P1-02；当前状态是“.NET 未实现，客户端链路的协商和版本契约待确认”，不能归因为“Java 已实现而 .NET 漏移植”。本次固定源码对照得到以下证据：

| 来源及固定版本 | 源码事实 | 可以得出的结论 |
| --- | --- | --- |
| Connector/J 2.4.14，`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1` | [Ob20PacketInputStream.java](https://github.com/oceanbase/obconnector-j/blob/6adc311ba5db8b2f19f98f3e6e4734667fdfdda1/src/main/java/com/oceanbase/jdbc/internal/io/input/Ob20PacketInputStream.java#L228) 在 uncompressLength 非零时拒绝；[AbstractConnectProtocol.java](https://github.com/oceanbase/obconnector-j/blob/6adc311ba5db8b2f19f98f3e6e4734667fdfdda1/src/main/java/com/oceanbase/jdbc/internal/protocol/AbstractConnectProtocol.java#L908) 优先选择 OB20，否则才选择标准压缩流 | 该 Java 参考版本没有提供压缩 OB20 的可照搬实现；标准压缩与普通 OB20 是不同路径 |
| OBProxy，`81eeae92526f5efbee516e863c7234532dfa6175` | [ob_2_0_protocol_utils.cpp](https://github.com/oceanbase/obproxy/blob/81eeae92526f5efbee516e863c7234532dfa6175/src/obproxy/proxy/mysqllib/ob_2_0_protocol_utils.cpp#L55) 的客户端响应路径注明 `cli - proxy not supports compressed ob20`；同文件压缩编码路径的头校验不包括外层压缩头 | 该源码明确区分链路；不能从内部压缩编码器的存在推定客户端可以直接协商使用，也不能把注释扩展成所有版本永久不支持 |
| OceanBase，`5e8a63d29dae9fde5e962b8fcc7b167d438fc343` | [ob_2_0_protocol_processor.cpp](https://github.com/oceanbase/oceanbase/blob/5e8a63d29dae9fde5e962b8fcc7b167d438fc343/deps/oblib/src/rpc/obmysql/ob_2_0_protocol_processor.cpp#L79) 按 `proxy_cap_flags_.is_ob_protocol_v2_compress()` 分支解码；头校验按压缩模式决定是否包含外层头 | 服务端有压缩处理路径，但仅此不能证明通用驱动具备设置该代理能力位的客户端契约 |

实现前必须取得三项依据：客户端直连/客户端到 OBProxy/OBProxy 到 observer 各链路的适用版本；客户端可用 capability 位及握手请求/确认和拒绝规则；外层压缩块、内层 OB20 frame 的边界/序列/校验定义。随后用明确版本的真实部署完成双向通信、禁用/拒绝、损坏包及取消验收。不能仅把现有 31 字节 OB20 frame 套入 zlib，就宣称协议已兼容。

若目标版本只支持代理内部压缩，须在此 ID 下记录该版本的客户端限制、替代配置及后续范围决定，不得改写为“.NET 已实现”。确认前维持 `Use Compression=true;Use OB20=true` 联网前拒绝；其他工作包可以继续，不依赖本项猜测协议。

相关文档：

- [Compatibility and known gaps](compatibility.md)
- [Verification status and remaining work](obconnector-dotnet-progress.md)
- [ADO.NET API reference](obconnector-dotnet-architecture-design.md)
- [Connector/J architecture outline](obconnector-j-architecture-outline.md)
