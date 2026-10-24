# OceanBase .NET 驱动完整性走查报告

**走查日期：** 2026-10-24  
**仓库：** `D:\dotnet-code\obconnector-dotnet`  
**初次源码基线：** .NET `06d58e1`；Java `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1` / 2.4.14。

**本模块开发起点：** `557a05a`（自动参数推导已提交）；本次修复 SchemaOnly 的目标执行语义。Java 参考为 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`。
**目标：** 为 OceanBase MySQL 兼容模式和 Oracle 兼容模式定义一份可执行的完整驱动缺口清单。  
**结论级别：** 这是源码、测试、打包记录和 Connector/J 静态结构对照形成的审计报告，不把本地 fixture 当作真实服务器兼容性证明。

**最新状态：** P1-05 SchemaOnly 元数据专用路径本地交付：完整单测 3928、完整消费者 2348（含新增 20 项）、DI 14 通过，Integration 9 通过/72 跳过；Release 构建 0 警告/0 错误。独立走查无剩余 Critical/Important，完整证据及原始失败见[第 15.20 节](obconnector-dotnet-completeness.md#1520-2026-10-25-schemaonly-元数据专用路径)。KeyInfo、其他行为和真实准备/锁/结果形状/代理验收继续开放。

**本次交付：** SchemaOnly 子模块见第 13 节；第 11、12 节保留历次过程记录及[开发前文档快照](#12-当前交付快照2026-10-25)。当前能力以第 3、4、13 节和详细基线第 14 节为准。

**最近复核：** 2026-10-25；SchemaOnly 交付和原并行测试失败/重跑见第 13 节及详细基线第 15.20 节。自动参数推导见第 15.18 节，第 15.17 节保留修复前快照。有符号 Scale 交付见第 15.16 节，第 15.15 节失败作为历史保留。此前 DataTypes 开发修复、独立走查与最终执行结果见第 15.14 节；第 15.13 节的失败/筛选遗漏作为历史保留。前次文档整理重新读取 Schema 模块已有报告：完整单测 3693、受影响消费者 66、DI 14 通过，Integration 9 通过/72 跳过，原构建日志 0 错误/0 警告，详见[本次证据复核](obconnector-dotnet-completeness.md#1510-2026-10-25-当前源码与完整文档交付复核)。此前证书池化 `01a8082`、清池 `0e58d1f`、标准 MySQL 压缩 `8d8b799` 的执行结果分别保留于详细基线第 15.4/15.5/15.7 节；压缩报告缺失的历史快照见第 15.8 节。以上均不构成新增实库兼容性声明。

源码提交是初次走查基线；其后 P0-06 的本地修复和 P1-04 的自定义服务器证书验证分别记在[池路由记录](obconnector-dotnet-progress.md#2026-10-24-p0-06-pooled-resolver-routing)和 [TLS 验证记录](obconnector-dotnet-progress.md#2026-10-25-p1-04-tls-server-validation-callback)。本报告保留原缺口 ID 和未完成的真实验收要求。

## 1. 结论先行

当前 .NET 驱动已经超过“只有连接和简单 SQL”的阶段，但仍不能宣称是完整的生产级驱动。最关键的事实如下：

| 问题 | 结论 |
| --- | --- |
| Java 驱动有没有连接池 | 有。Connector/J 有 `OceanBasePoolDataSource`、`OceanBasePooledConnection`、`Pools`/`Pool`，覆盖池注册、借用/归还、最小/最大容量、空闲和生命周期清理以及池事件。 |
| .NET 当前有没有连接池 | 有基础物理会话池，默认 `Pooling=false`。显式启用后可复用已认证的 `PhysicalSession`，支持最大容量、等待超时/取消、借用前 `COM_PING`、空闲/总生命周期回收、显式清池和借出会话归还退休，归还时执行 reset；可识别的用户 session-state/temporary-object 语句会销毁会话。Closed 回调重开的池归属已有修复与回归，真正多线程压力仍缺证据。 |
| .NET 连接池是否完整 | 不是。池指标已经接入并有本地回归，但完整 session reset 和真实 OceanBase/OBProxy reset 兼容性仍未闭合；最小池预热及失败回滚、空池 static registry 清理已实现。 |
| 当前最大的生产风险 | 会话状态隔离、unknown outcome、ambient 本地事务的故障矩阵、活动连接故障恢复和真实代理/TLS/跨平台矩阵仍不完整。 |
| 是否已经达到 Java/.NET 成熟驱动水平 | 没有。现有实现可以继续开发和做受控试用，但 README 和包元数据应继续保持 experimental/未完成真实兼容性验证的表述。 |

本报告共追踪 **23 项缺口：P0 六项、P1 十一项、P2 六项**。其中有功能缺失、已实现功能的组合限制、源码推导的正确性问题及真实验收缺口，不能合并称为“全部尚未实现”。每项触发场景、源码/测试入口及关闭标准见[详细追踪表](obconnector-dotnet-completeness.md#14-缺口到场景源码和验收的追踪表)。第一版明确排除的能力也要有公开契约，不能把成熟驱动的所有可选特性都当成无条件发布要求。

## 2. 证据规则

本报告使用三种证据等级：

1. **源码/本地回归**：代码路径存在，并由单元测试、消费者测试或 loopback 协议 fixture 覆盖。
2. **包和运行时消费**：目标 TFM 能编译、加载和调用公共 API。
3. **真实互操作**：实际 OceanBase、OBProxy、TLS/KDC 或故障环境中的运行结果。

只有第 3 级证据可以把“未验证”升级为真实兼容。mock、自签名 `SslStream`、TCP loopback、协议 fixture 和包消费测试不能证明真实 SQL、目录、锁、拓扑、认证或代理行为。

初次走查以源码调用链发现 P0-06 resolver/池路由问题及 P1-05 SchemaOnly 执行语义。P0-06 随后已有同步/异步复现与本地修复，详见第 5 节和进度台账；SchemaOnly 的副作用/锁场景仍待专门回归。真实拓扑兼容不能由这些 loopback 回归推定。

现有真实数据库记录为 Windows/.NET 10、直连 OceanBase 4.4.2.2、TLS disabled：MySQL 模式 60 通过，Oracle 模式 79 通过、1 个 nested-table column 失败。OBProxy、其他版本、完整 TLS/plugin、KDC、故障注入和非 Windows 运行仍未闭合。

## 3. 当前能力矩阵

| 模块 | 状态 | 已有能力 | 主要缺口或边界 |
| --- | --- | --- | --- |
| 连接与配置 | 部分实现 | 连接字符串、超时、多地址、RoundRobin、模式确认、地址 resolver、空池 registry 清理 | OCP/TNS、健康发现、连接字符串归一化、完整版本/代理矩阵 |
| 物理连接池 | 部分实现 | 物理会话复用、最大容量、等待超时/取消、最小池预热及失败回滚、带 MySQL autocommit 校验的 `COM_PING`/reset、idle/total lifetime、空池 registry 清理、池指标、`ClearPool`/`ClearAllPools`/source `Clear` | 完整资源 reset、广泛并发/长期运行、真实 reset 兼容性 |
| 会话隔离 | 部分实现 | `COM_RESET_CONNECTION`、MySQL `COM_INIT_DB`、reader 消耗后可复用、prepared statement generation 失效、未释放 REF CURSOR/LOB 归还时销毁、可识别的用户 session-state/temporary-object 语句归还时销毁 | 不可识别的 session-state、XA 和失败丢弃的完整矩阵 |
| ADO.NET 核心 | 部分实现 | `DbConnection`、`DbCommand`、`DbParameter`、`DbDataReader`、provider factory、`CloseConnection`、`SequentialAccess`、`SingleRow`、`SingleResult`、仅 Prepare 目标的 `SchemaOnly` | `KeyInfo`、完整 schema metadata、`DbDataAdapter`、`DbCommandBuilder`、SchemaOnly 真实准备行为与形状矩阵 |
| .NET 8+ 资产 | 部分实现 | `DbDataSource`、`DbBatch`、savepoint、异步事务、GSSAPI 代码路径 | 现代 TFM 与旧 TFM 的完整行为矩阵、标准 `DbBatch` 生态验收 |
| 认证 | 部分实现 | native、old、clear、SHA-2、Ed25519、PAM、GSSAPI 路径 | 真实插件成功/拒绝矩阵、KDC、短期凭据服务及轮换实库验收 |
| TLS | 部分实现 | 默认平台信任库和主机名校验；可选服务器验证回调及按引用隔离的普通池；池化/直连 `ClientCertificate` 和 source 证书快照；可选撤销检查 | 真实企业 CA/轮换/撤销和跨平台语义、真实 mTLS 矩阵 |
| 基础 SQL | 已实现 | 文本执行、server prepare、参数绑定、多结果、流式读取、取消、超时 | 真实版本/代理/大数据量边界仍需验收 |
| 多语句 | 部分实现 | 显式 `Allow Multi Statements=true` 的文本多结果路径、默认关闭；Oracle 匿名块和程序 DDL 的内部分号按单个程序单元处理 | 真正的多语句参数/Prepare/cursor/Oracle 输出组合和真实服务器错误矩阵；程序单元非法尾部由服务端解析 |
| 压缩与 OB20 | 部分实现 | 显式 Use Compression 的标准 MySQL 压缩；基础 OB20、校验和、extra info、trace 结构 | OB20 压缩 frame、proxy reroute 及真实压缩矩阵 |
| LOCAL INFILE | 部分实现 | `Allow Local Infile=true` 才请求 `CLIENT_LOCAL_FILES`；应用通过 `LocalInfileStreamFactory` 提供流；同步/异步上传后读取最终 OK，失败会销毁物理会话 | 真实 OceanBase/OBProxy 互操作、代理策略、超过 64 MiB、服务器错误和中断恢复 |
| 本地事务 | 部分实现 | MySQL/Oracle 隔离级别、savepoint（有名称限制）、本地 ambient enlistment | 分布式提升、提交响应丢失、取消后的状态机 |
| Unknown outcome | 部分实现 | 异常可标记未知执行结果，驱动不自动 replay | 发送/响应/commit/rollback 故障状态机和真实故障验证 |
| XA | 部分实现 | Start/End/Prepare/Commit/Rollback/Recover，MySQL/Oracle 分支 | coordinator crash recovery、普通池与 XA 的完整隔离验证 |
| Oracle 输出与游标 | 部分实现 | PrepareExecute、OUT/RETURNING、REF CURSOR、LOB、数组绑定 | cursor 参数方向、cursor-valued column、Prepare/FetchSize 组合 |
| 复杂类型 | 部分实现 | object/collection 标量及嵌套成员、类型缓存和数组绑定 | LOB/cursor/national 成员、循环结构、文本结果、外部 DDL 后的缓存失效 |
| 类型与字符集 | 部分实现 | 常用数值、时间、YEAR、BIT、JSON、zero-date、Oracle 时间/interval | 扩展 type code、更多字符集/排序规则、精度边界 |
| HA 与恢复 | 部分实现 | Open 阶段 TCP 候选地址重试、注入式 resolver | 活动连接迁移、透明重连、退避、reroute、安全重试 |
| Schema 元数据 | 部分实现 | Oracle 20/MySQL 18 个集合、静态 DataTypes 及自动参数推导有本地回归 | 实库字典、权限/版本/代理和类型/DDL 矩阵；推导不扩大过程执行支持 |
| 批处理 | 部分实现 | `DbBatch` 顺序执行、Oracle array binding | rewrite、multi-send、bulk、跨命令 statement cache |
| 诊断 | 部分实现 | `ILogger`、`ActivitySource`、`Meter`、脱敏日志、池会话/借用/等待/丢弃/reset 失败/等待耗时指标 | 建连与探活细分指标、协议 debug、slow-query profile |
| 真实兼容性 | 部分验证 | 直连 4.4.2.2 的有限记录，包含 Oracle 失败 | OBProxy、TLS/plugin、版本、平台、网络故障和 XA 恢复矩阵 |

### 3.1 除连接池之外的待开发范围

尚无公开实现的常见能力包括：`KeyInfo`、DataAdapter/CommandBuilder 和跨命令 statement cache。自动参数推导已通过完整本地回归和独立走查，真实目录互操作继续开放。`DataTypes` 已完成本地实现及全量/五资产回归，真实服务器类型、DDL、编码和版本/代理范围仍须验收。协议侧缺 OB20 压缩/reroute，发现侧缺内置 OCP/TNS 或等价适配与健康/退避策略；Oracle 侧还有限定游标方向/结果形状、复杂成员和 DDL 缓存失效的缺口。

凭据提供/轮换已有本地实现：每次 Open 的认证快照、版本退休、过期和取消规则已通过最终回归。真实短期凭据服务和认证插件轮换仍须验收。

已有实现但尚未闭合的是：会话隔离、事务/unknown outcome 的故障矩阵，SchemaOnly 的真实准备行为与元数据形状，扩展类型/字符集，批量性能与诊断，以及真实版本/代理/TLS/认证/平台验收。常用 Schema 目录、标准 MySQL 压缩、客户端证书池化、清池和池指标均已有本地实现，不应重新列为完全缺失。

完整分类见[详细基线第 4.1 节](obconnector-dotnet-completeness.md#41-除连接池外具体还缺什么)；凭据契约清单见第 5.5 节，DataTypes/参数推导/KeyInfo 见第 5.10.1 节。部分可选能力是否进入首个正式版本尚未确定，不能把未确认的范围决定写成已排除。

## 4. 缺口清单和交付优先级

### P0：不完成就不能称为完整驱动

| ID | 缺口 | 当前问题 | 完成条件 |
| --- | --- | --- | --- |
| P0-01 | 连接池生命周期 | 指标、显式 ClearPool/ClearAllPools/source Clear 及旧会话退休已接入，Closed 回调重开归属已有回归；多线程压力、完整 reset 和真实服务器行为仍未闭合 | 配置一致的池可创建/清理；物理会话数、借用次数、等待、丢弃、reset 失败可观测，清池不破坏活动工作或容量上限，并通过压力及真实服务器验收 |
| P0-02 | 完整 session reset | prepared statement 已按物理会话 generation 失效；仍未覆盖所有借用者状态 | 事务、变量、临时对象、prepared statement、reader/cursor、LOB、XA 和 reset 失败丢弃均有 cross-borrower 测试 |
| P0-03 | Ambient transaction | 已支持本地 volatile enlistment；命令和 Batch 复用连接的本地事务 | 完成超时、in-doubt、响应丢失和真实故障矩阵；分布式提升仍明确拒绝 |
| P0-04 | Unknown outcome | 发送后断开、取消、提交响应丢失时无法总是判定服务端结果 | 明确发送前/发送后/响应中/commit/rollback 状态；写操作不自动重放；异常给出恢复动作 |
| P0-05 | 活动连接恢复 | 连接建立后的故障恢复契约和真实矩阵不完整；无透明迁移/自动重连 | 先闭合断线状态与应用重建连接契约；定义只读、幂等写、非幂等写边界，活动事务不被静默迁移；不要求自动重放未知写 |
| P0-06 | 池路由身份隔离 | 此前普通池误共享不同 resolver 的会话，已复现并本地修复；每次 Open 路由快照会清理旧端点闲置会话 | 保留引用身份、快照/预热、取消/预算、并发清理回归；补真实拓扑、代理/节点故障和跨平台报告 |

### P1：常见生产和 ADO.NET 能力

| ID | 缺口 | 目标 |
| --- | --- | --- |
| P1-01 | 多语句 | 保留显式启用的无参数文本多结果；明确参数/Prepare/cursor/Oracle 输出组合的拒绝，并验证真实服务器错误边界 |
| P1-02 | 压缩/OB20 | 标准 MySQL 压缩已有本地实现；先确认客户端 OB20 压缩的链路/版本/协商契约，再补实现；reroute 及真实服务器/代理验收仍缺 |
| P1-03 | LOCAL INFILE | 只允许应用提供的流，禁止驱动读取任意本地路径 |
| P1-04 | TLS/mTLS | 自定义服务器验证回调、池化客户端证书隔离和 source 快照已有本地实现；真实企业 CA/轮换/撤销/跨平台和 mTLS 矩阵仍待完成 |
| P1-05 | ADO.NET 行为 | `KeyInfo`、schema/parameter metadata、Batch/游标行为表；保留 `SchemaOnly` Prepare-only 回归并补真实准备行为验收，以及明确的 adapter 策略 |
| P1-06 | Oracle 输出/游标 | 完整参数方向、cursor-valued column、PrepareExecute 结果形状 |
| P1-07 | 复杂类型 | 保留已有嵌套 object/collection，补齐 LOB/cursor/national 成员和外部 DDL 缓存失效 |
| P1-08 | 类型/字符集 | 类型码清单、CLR/provider-specific 映射、字符集和精度契约 |
| P1-09 | HA/发现 | OCP/TNS 或等价发现、健康状态、退避和 OBProxy 拓扑 |
| P1-10 | Schema | 常用目录/限制参数及 DataTypes 静态映射已有本地实现和最终回归；自动参数推导已有本地交付，真实目录/权限/版本/代理矩阵待验收，见 [Schema 契约](schema.md) |
| P1-11 | 凭据提供/轮换 | 每次 Open 快照、全口令替换、版本退休、过期/取消及脱敏已有本地实现和最终回归；真实插件/凭据服务/KDC 验收仍缺 |

### P2：生态、性能和运营增强

| ID | 缺口 | 目标 |
| --- | --- | --- |
| P2-01 | `DbDataAdapter`/`DbCommandBuilder` | 实现或正式声明不支持，不能保持模糊状态 |
| P2-02 | statement cache | 有上限、淘汰、session generation 和失效策略 |
| P2-03 | 批处理优化 | rewrite、multi-send、bulk 与 partial failure 结果模型 |
| P2-04 | 诊断 | 池等待、建连、reset、丢弃、协议 debug 和 slow-query profile |
| P2-05 | TFM/包生态 | 每个目标 TFM 的 consumer、运行时和发布矩阵 |
| P2-06 | 真实兼容矩阵 | 扩展范围可分期；承诺支持的版本、直连/OBProxy、模式、TLS、认证、平台和故障报告始终是发布门槛 |

## 5. 连接池专项结论

### 5.1 当前实现路径

- `OceanBaseConnection.Open`/`OpenAsync` 从匹配配置的 `PhysicalSessionPool` 租用物理会话。
- 新建会话完成 TCP、TLS、认证和 OceanBase 身份确认；池内复用会话先执行 `COM_PING`。
- 逻辑连接关闭时，健康且非 XA 的物理会话执行 reset；MySQL 模式随后恢复初始 database。
- 达到最大容量时，后续 Open 等待归还；等待支持超时和取消。
- 空闲 lifetime、总 lifetime 和 `Min Pool Size` 的保留规则已存在；首次或后续租借前会补足 `Min Pool Size`，预热失败仅回滚本次新建且仍闲置的会话，不能销毁已被其他 Open 借出的会话。
- reader 未完成、LOB/cursor 活跃、XA 专用、reset 失败或生命周期过期的会话必须销毁，不得回池。
- 未释放的 Oracle REF CURSOR 会设置归还销毁标记；最后一个 cursor 释放后才清除该标记。
- 未释放的 LOB 或 temporary LOB 句柄会设置归还销毁标记；句柄释放后才允许池化 reset。
- 可识别的用户 `SET`、`ALTER SESSION` 和临时对象语句会设置永久归还销毁标记；当前不依赖未验证的服务器 reset 语义。
- reset 成功后必须递增物理会话 generation；旧 prepared statement 只能被本地丢弃，不能再次发送关闭或执行请求。

### 5.2 与成熟 Java 池的差距

Connector/J 的池实现包含池注册、最小/最大容量、生命周期调度、连接/语句事件和运营基础设施。当前 .NET 已有容量、等待、预热、探活、惰性回收和指标；以下内容仍需补齐或明确取舍：

1. 连接字符串归一化和进程退出行为；
2. session variable、临时表/对象、statement、cursor、LOB、XA 的完整 reset；
3. 建连、探活、协议 debug 和 slow-query profile 等诊断补充；
4. 在已有 Closed 回调重开回归之外，补多线程 Close/Open、池等待取消、借出连接与 data source disposal 的资源归属压力验证；
5. 真实 OceanBase 与 OBProxy 上的 reset、预热、超时和并发验证。
6. 普通全局池的 resolver 路由身份隔离，以及路由更新后已有物理会话的失效规则（P0-06）。

普通池已按区分大小写的原始连接字符串、resolver 和凭据提供者的引用身份、TLS 验证回调引用身份，以及客户端证书引用和 SHA-256 DER 指纹共同索引；resolver、回调和证书的引用身份不会因对象自定义值相等而合并。等价但顺序、别名不同的字符串仍可能产生多个池，是后续容量/复用效率问题。数据源保留私有池及 Build 时捕获的证书副本。每次逻辑 Open 只解析一次地址快照，移除不再匹配的闲置端点后再预热/借用；发现失败不回退，已借出连接不迁移。并发发现使用各自快照，不承诺全局拓扑版本排序；真实拓扑仍须验收。

显式清池关闭闲置会话，使借出会话和清池前的在途会话归还后销毁；借出会话继续计入容量，活动命令不会被中断。`ClearAllPools()` 只清理普通 registry 池的快照，私有源使用 `source.Clear()` 或针对源连接的 `ClearPool`，清理后仍允许重新 Open。P1-11 把凭据提供者引用加入普通池身份和清池匹配，返回的秘密及版本不进入可观测池标识。

`ClearPool` 按原配置及对象引用匹配，不读取证书内容，可清理同一证书对象的旧指纹池。清池不是强制终止所有活动连接的进程 shutdown；具体契约见[架构设计](obconnector-dotnet-architecture-design.md#explicit-pool-clearing)。

本轮池走查对上述生命周期风险做了代码复核，当前状态如下：

| 检查项 | 当前状态 | 仍需补的证据 |
| --- | --- | --- |
| idle lifetime 与 total lifetime 混用 | 已修正；归还路径只检查 total lifetime，候选闲置会话才检查 idle lifetime，并保留 `Min Pool Size` | 长时间借用、并发借还和时钟边界压力回归 |
| 旧 `Open` 完成覆盖新一轮打开 | 已有 per-open cancellation 和 generation；过期完成会被拒绝，清理只处理同一 generation；Open 失败和 Close 归还使用原池/key 快照，Closed 回调更换配置重开不再混用池 | 多线程 Close/Open 压力回归和活动事务交错 |
| Close 不能打断池等待 | 已有 `OpenCancellation`，同步/异步池等待使用该 token | 等待、Close、重新 Open 的并发回归 |
| 静态 registry 无清理 | 空池通过 `ReleaseIfEmpty` 移除并 Dispose；普通池已有显式 `ClearPool`/`ClearAllPools`，私有源有 `Clear`/Dispose；Dispose 后借出会话关闭时直接销毁，不发送 reset。没有统一强制关闭活动会话及禁止后续 Open 的 shutdown API | 非空池和长时间运行进程的生命周期报告；是否需要统一 shutdown 须单独确定契约 |

因此这些项目不再作为“已确认的当前 bug”报告；它们是已有保护但验证尚未闭合的池生命周期验收项。

### 5.3 LOCAL INFILE 的当前实现和边界

- `Allow Local Infile` 默认是 `false`；关闭时不发送 `CLIENT_LOCAL_FILES`，收到服务端 `0xfb` 会拒绝并按 unknown outcome 销毁本次物理会话。
- 开启后，应用必须在 `Open` 前设置 `OceanBaseConnection.LocalInfileStreamFactory`。工厂收到服务端文件名这一不透明值，返回的可读流由驱动释放；驱动永远不按该名称访问本地路径。
- 上传使用同步和异步两条路径，按 64 KiB 分块，发送空包结束并读取最终 OK。工厂缺失、空返回、不可读、读取异常、取消、超过默认 64 MiB 上限或最终响应异常都会使物理会话进入 unknown outcome 并销毁。
- `LocalInfileTests` 已覆盖默认拒绝、capability、同步/异步上传、文件名传递、工厂错误、读失败、取消、空终止包和最终 OK；这些是 loopback 协议证据，不是真实数据库兼容性证明。

仍需真实 OceanBase/OBProxy 验收 `LOAD DATA LOCAL INFILE`、代理安全策略、服务器错误、长文件、超限和中断恢复。LOCAL INFILE 也不等于普通参数流上传，两者的 API 和风险边界不能合并。

### 5.4 连接池验收矩阵

每一项都必须有“借用者 A 污染状态 → 归还 → 借用者 B 观察基线”的测试：

| 状态 | B 应观察到的结果 |
| --- | --- |
| 本地事务/自动提交 | 无活动事务，自动提交回到基线 |
| session variables | provider 修改过的变量恢复 |
| database/schema | 回到连接字符串初始值 |
| prepared statement | 无旧命令、旧 schema 或旧 statement 引用 |
| reader/cursor | 未读协议流不会进入 B；无法清理就销毁会话 |
| LOB/临时 LOB | stream、locator、临时对象按契约关闭/释放 |
| XA | branch 未完成时不得进入普通池 |
| reset 失败 | 物理会话从池移除，不可再次借用 |

## 6. Java 对照结论

Connector/J 的静态模块对照显示，Java 已把以下职责分开：连接入口、地址/发现、TLS/认证、会话/事务、查询/游标、类型/字符集、LOB/复杂类型、目录、HA、池、XA、诊断和发布测试。当前 .NET 对应模块大多已有骨架，但差距集中在契约闭合和真实验收，而不是缺少一个单独的 `Pool` 类。

Java 池证据位于参考仓库的 `src/main/java/com/oceanbase/jdbc/OceanBasePoolDataSource.java`、`OceanBasePooledConnection.java` 及 `internal/util/pool/Pool.java`、`Pools.java`。`Pool` 读取最小/最大容量并使用调度器维护，`Pools` 管理池注册和 executor。该结论已在本轮核对源码与 commit；未运行 Java 池负载测试，也不推定其所有行为都应原样移植到 ADO.NET。

对 .NET 的正确目标是复用 ADO.NET 语义，不复制 JDBC 类名：

- 用 `DbConnection`/`DbDataSource` 表达逻辑连接和数据源；
- 用 `PhysicalSession` 表达可复用物理会话；
- 用明确的 generation、租约和 reset 状态隔离 session、transaction、reader、LOB 和 XA；
- 用 OceanBase 模式/能力矩阵表达 MySQL/Oracle 差异；
- 用异常的 confirmed failure、unknown outcome 和 confirmed success 字段表达恢复边界。

## 7. 推荐实施顺序

| 阶段 | 内容 | 退出标准 |
| --- | --- | --- |
| G0 | 冻结公共 API、兼容矩阵、状态机和错误分类 | 每个缺口都有状态、测试入口和明确“不支持/未验证”边界 |
| G1 | 完整 session reset 和 XA 隔离 | cross-borrower、并发、取消、失败丢弃和 XA 隔离通过 |
| G2 | ambient transaction、取消和 unknown outcome | 本地 `TransactionScope` 提交/回滚 loopback 回归已覆盖；提交/回滚响应丢失和真实故障矩阵仍待完成 |
| G3 | 多语句、压缩、LOCAL INFILE、OB20 reroute/压缩 | capability 协商、旧服务器拒绝和真实服务器通过 |
| G4 | mTLS、信任/撤销、认证插件、HA/发现 | TLS/KDC/OBProxy/故障注入矩阵通过 |
| G5 | ADO.NET 行为、Oracle 输出、复杂类型、字符集和 Schema | 公共 API、模式差异和结果形状矩阵通过 |
| G6 | statement cache、batch 优化和诊断 | 资源上限、淘汰、性能和可观测性基线通过 |
| G7 | TFM、跨平台、包和发布验收 | 所有对外声明都有可复现报告 |

## 8. 发布门槛

在以下条件全部满足前，版本只能作为实验性驱动发布：

1. 池 reset 和 cross-borrower isolation 通过；
2. Oracle 真实验收中的失败已分类并处理，不能以 skip 掩盖；
3. 至少一个直连和一个 OBProxy 真实 profile 通过；
4. 至少一个真实 `VerifyFull` TLS profile 通过，mTLS/KDC 是否支持必须单独声明；
5. unknown outcome、XA crash recovery 和活动连接故障边界有报告；
6. 声明支持的每个 TFM 有 package consumer 和运行时证据；
7. README、连接字符串、公共 API、异常、测试和兼容性文档对同一能力给出一致结论。

## 9. 走查入口和参考文件

| 用途 | 文件 |
| --- | --- |
| 本报告 | [obconnector-dotnet-driver-audit.md](obconnector-dotnet-driver-audit.md) |
| 详细能力基线与验收要求 | [obconnector-dotnet-completeness.md](obconnector-dotnet-completeness.md) |
| 公共 API 和配置契约 | [obconnector-dotnet-architecture-design.md](obconnector-dotnet-architecture-design.md) |
| 历史验证、失败和剩余工作 | [obconnector-dotnet-progress.md](obconnector-dotnet-progress.md) |
| 真实兼容性和已知限制 | [compatibility.md](compatibility.md) |
| Java Connector/J 模块对照 | [obconnector-j-architecture-outline.md](obconnector-j-architecture-outline.md) |
| 每个缺口的场景、源码、测试入口与验收要求 | `docs/obconnector-dotnet-completeness.md` 第 14 节，共 23 个 ID |
| TLS、客户端证书和撤销检查 | `src/OceanBaseConnector/Internal/Transport/TlsHandshakeFlow.cs`、`TlsUpgrade.cs`、`src/OceanBaseConnector/OceanBaseConnection.cs`、`tests/OceanBaseConnector.UnitTests/Transport/TlsComponentsTests.cs`、`ProviderConnectionTests.cs` |
| 物理会话池 | `src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs` |
| 物理会话 reset/状态 | `src/OceanBaseConnector/Internal/Session/PhysicalSession.cs` |
| 连接池回归 | `tests/OceanBaseConnector.UnitTests/PoolingTests.cs`、`tests/OceanBaseConnector.UnitTests/Session/PhysicalSessionPoolTests.cs` |
| Close/Open 归属修复回归 | `tests/OceanBaseConnector.UnitTests/PoolOwnershipTests.cs` |
| 真实数据库入口 | `tests/OceanBaseConnector.IntegrationTests/`、`tests/OceanBaseConnector.Acceptance/` |

## 10. 明确不应偷换的结论

- Java 有连接池，不等于 Java 的每个 JDBC API 在本项目都已经对齐。
- .NET 有基础池化，不等于已经完成成熟连接池的状态隔离和运营能力。
- 本地 TLS、loopback 和 mock 不等于真实 OceanBase/OBProxy/TLS/KDC 兼容。
- OceanBase MySQL/Oracle 是兼容模式，不应写成原生 MySQL/Oracle 服务器支持。
- 服务端不支持 nested-table column，不能被记录成驱动已经支持该形状；procedure collection 参数是另一个能力。
- 未完成的功能必须明确拒绝或标记未验证，不能让深层协议错误替代公共契约。
- `SchemaOnly` 当前只 Prepare 目标，禁止目标执行及 fallback；这不代表准备阶段没有解析、权限检查、元数据锁或任何服务器行为，真实矩阵仍需验收。
- 缺少 DataAdapter/EF Core provider、没有透明 replay 与核心连接正确性是不同层级；明确的版本支持范围是验收依据。

## 11. 本次文档交付和后续执行

初次文档交付整理了能力基线、23 项可追踪缺口、Java 对照、源码/测试入口、实施依赖、实库矩阵和发布门槛。后续开发已补充 P0-06 的池路由隔离与快照生命周期，保留复现、修复、独立走查和验证记录；尚未完成的真实矩阵继续保留为缺口。

完整清单见[能力与缺口基线](obconnector-dotnet-completeness.md)第 4、5、14、15 节。后续每个 ID 关闭时须记录提交、回归、命令/退出码、运行时/服务端 profile、失败/跳过和报告位置；当前未分配负责人/日期。已有开发进度台账及历史任务编号保持原样。

2026-10-25 DataTypes 交付时基线更新至 `5146b62`，包含 DataTypes、客户端证书池化隔离、source 证书快照、显式清池/退休、标准 MySQL 压缩、Schema 常用目录和凭据提供/轮换。DataTypes 的本地交付和新增集合数量见第 15.14 节。连接池的容量、预热、清理和指标已具备；完整隔离、并发/长期运行及真实兼容性仍开放。23 个缺口继续逐项验收，局部模块交付不等于完整驱动验收。

后续执行按[剩余交付工作包](obconnector-dotnet-completeness.md#16-剩余交付工作包)分为八组，覆盖全部 23 项：每组列出尚需实现的内容、必须保留的行为及验收产物。负责人、日期和承诺支持范围仍待项目分配；本报告不把建议阶段写成已确认排期。上述文档整理只检查链接、编号和 diff；随后的标准 MySQL 压缩开发、测试和独立走查单列于[模块记录](obconnector-dotnet-completeness.md#157-2026-10-25-标准-mysql-压缩)。

此前另补充[OB20 压缩协议前提](obconnector-dotnet-completeness.md#163-ob20-压缩的客户端协议前提)：Java 2.4.14 同样拒绝压缩 OB20，固定版本的 OBProxy 客户端响应代码注明该链路不支持；服务端有代理能力位控制的压缩路径，不能据此直接推定通用客户端支持。该项继续开放，先确认客户端链路、版本及协商契约，再实施与验收。


2026-10-25 后续已扩展 Schema 常用目录，具体契约见 [schema.md](schema.md)，开发、测试与独立走查见[模块台账](obconnector-dotnet-progress.md#2026-10-25-p1-10-schema-catalog)。这不关闭真实字典兼容矩阵，也不将此前真实六集合结果扩展解释为新目录已验收。

本次文档交付补全明确缺失/部分实现/欠验收的分类、凭据获取/过期/并发刷新/池退休/取消和脱敏清单，以及 DataTypes/参数自动推导/KeyInfo 的分别验收要求。已复核现存 Schema TRX，保留所有历史失败、跳过、缺失报告及 23 个 ID；未修改产品代码或关闭任何工作包。

DataTypes 修复前的 3791 通过/1 失败及消费者筛选遗漏保留于[历史检查点](obconnector-dotnet-completeness.md#1513-2026-10-25-datatypes-未提交开发与文档交付检查点)，已由[第 15.14 节](obconnector-dotnet-completeness.md#1514-2026-10-25-datatypes-本地交付)的完整单测 3792、消费者 66（含实际 16 项）通过结果取代。23 个 ID、8 个工作包及历史实库失败/跳过均保留；自动参数推导和真实验收继续开放。

此前完整文档复核另明确两项容易混淆的边界：Oracle NUMBER 结果 metadata 可表示负 scale，开发基线 `5146b62` 的 byte Scale 参数 API 却不能显式声明负 scale；新增 DecimalScale 已完成本地回归和独立走查，原消费者失败及筛选遗漏保留在历史记录中；真实声明矩阵仍待验收。过程目录与过程执行也不同：当前 CommandType.StoredProcedure 只走 Oracle PrepareExecute，MySQL 不具备该执行入口。自动推导在已提交基线尚缺，工作区进展以以下检查点为准。均沿用现有 ID 追踪，不改变 23 项缺口和八个工作包。

有符号 DecimalScale 模块交付、消费者 fixture 根因、最终146项实际覆盖和历史报告见[第15.16节](obconnector-dotnet-completeness.md#1516-2026-10-25-有符号-decimal-scale-本地交付)。

此前文档检查点（历史快照）以 `2bbecac` 及其未提交工作区为准：自动参数推导已有三种同步/异步签名、唯一目录签名定位、类型属性映射、同会话/事务预留、取消及集合成员原子发布；相关子集 132 项通过，新增五资产消费者仅编译，Release 构建仍有 1 个 xUnit2013 警告。报告逐项保留此前 RED/失败，不把上一模块全量结果沿用为本模块结果。最终 package/overload、事务/权限、并发和不完整类型身份等边界，以及完整回归、消费者和最终独立走查仍待完成，详见[第15.17节](obconnector-dotnet-completeness.md#1517-2026-10-25-参数推导开发与完整文档检查点)。该次只修改文档；真实矩阵及全部 23 项/八个工作包继续开放。

后续自动参数推导已完成本地交付：新增 81 项单测和 20 项五资产消费者；完整单测 3900、完整消费者 2328、DI 14 通过，Integration 9 通过/72 跳过。修复类型身份配对、浮点/YEAR 声明丢失及 fixture 清理问题，完整回归、独立源码/契约走查和格式检查通过。详情及全部历史报告见[第15.18节](obconnector-dotnet-completeness.md#1518-2026-10-25-自动参数推导修复与验证)。真实目录/权限/声明/版本/OBProxy 验收仍缺，不关闭整个 P1-10、任何工作包或完整驱动目标。

## 12. 当前交付快照（2026-10-25）

本节为 SchemaOnly 开发前的历史快照；其中执行语义已由后续模块修复，当前契约见第 3、4 节及 API 文档。

本次只整理走查文档，源码基线为 `557a05a`，未继续实现 SchemaOnly 或其他模块。以下是对当前源码及已有报告的复核，不是新一轮测试执行，也不替代第 4 节全部 23 项缺口和详细基线第 14 节的逐项验收。

### 12.1 关键结论与当前证据

| 结论 | 本次复核入口 | 当前处置与验收要求 |
| --- | --- | --- |
| Java 有连接池 | 参考仓库 `OceanBasePoolDataSource`、`OceanBasePooledConnection`、`internal/util/pool/Pool` 和 `Pools`；HEAD 与参考版本一致 | Java 源码事实已确认；未运行 Java 池或将 JDBC API 直接视为 ADO.NET 要求 |
| .NET 有基础池化，默认关闭 | [配置默认值](../src/OceanBaseConnector/OceanBaseConnectionStringBuilder.cs)、[租用/归还与容量](../src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs)、[reset/销毁状态](../src/OceanBaseConnector/Internal/Session/PhysicalSession.cs) | P0-01/02/06 继续追踪完整隔离、并发/长期运行及真实双模式/代理验收；不能因已有 Pool 类关闭这些缺口 |
| SchemaOnly 仍发送并执行目标 SQL | [命令同步/异步执行](../src/OceanBaseConnector/OceanBaseCommand.cs)、[reader 隐藏行](../src/OceanBaseConnector/OceanBaseDataReader.cs)、[现有测试](../tests/OceanBaseConnector.UnitTests/TextCommandTests.cs)中的 `SchemaOnlyReturnsMetadataWithoutRowsAndKeepsConnectionUsable` | 现有测试服务端收到查询并返回数据行，只证明调用者看不到行及连接后续可用。P1-05 还需证明目标不执行，或明确公开执行限制；副作用函数、锁和大结果成本尚无专门实库证据 |
| KeyInfo、DataAdapter/CommandBuilder 尚缺 | [命令行为允许集合](../src/OceanBaseConnector/OceanBaseCommand.cs)、[factory 能力与 null 返回](../src/OceanBaseConnector/OceanBaseProviderFactory.cs) | P1-05/P2-01；补实现或作明确版本范围决定，不能由已有 reader schema 推定完整支持 |
| 自动参数推导已有本地交付 | [公开入口](../src/OceanBaseConnector/OceanBaseCommand.Parameters.cs)、[目录加载](../src/OceanBaseConnector/OceanBaseConnection.Parameters.cs)、[签名映射](../src/OceanBaseConnector/Internal/Sql/RoutineParameterDerivation.cs) | P1-10；保留成功替换/失败保留的契约，补真实目录/权限/重载/版本/代理验收；MySQL StoredProcedure 执行仍未因此增加 |
| 标准 MySQL 压缩已有，OB20 压缩/reroute 仍缺 | [标准压缩流](../src/OceanBaseConnector/Internal/Protocol/MySqlCompressionStream.cs)、[OB20 压缩拒绝](../src/OceanBaseConnector/Internal/Protocol/Ob20Packet.cs)、[reroute 标志拒绝](../src/OceanBaseConnector/Internal/Protocol/Ob20Stream.cs) | P1-02 三项分别追踪；先确认客户端链路及协商契约，见[协议前提](obconnector-dotnet-completeness.md#163-ob20-压缩的客户端协议前提) |
| Oracle 游标方向和复杂成员有限制 | [Oracle 输出计划](../src/OceanBaseConnector/Internal/Sql/OracleCommandPlan.cs)、[复杂成员类型](../src/OceanBaseConnector/Internal/Values/ComplexType.cs) | P1-06/07；Input/InputOutput REF CURSOR 等组合明确拒绝，扩展时须同步所有权、NULL、取消和释放矩阵 |
| Batch 按命令顺序执行 | [Batch](../src/OceanBaseConnector/OceanBaseBatch.cs)、[逐个启动命令的 reader](../src/OceanBaseConnector/OceanBaseBatchReader.cs) | P2-02/03；顺序执行和 Oracle array binding 已有，跨命令缓存、rewrite/multi-send/bulk 不应被写为已实现 |
| 五个目标资产不等于五种独立运行时通过 | [项目目标](../src/OceanBaseConnector/OceanBaseConnector.csproj)、[CI](../.github/workflows/ci.yml)、已有消费者报告 | P2-05/06；本次重新读取报告，未执行跨运行时、跨平台或真实 OceanBase/OBProxy 测试 |

### 12.2 已有报告复核与发布边界

重新读取 `TestResults/derive-parameters/` 的四份 `*-verified.trx`，按每个 `UnitTestResult` 的 outcome 统计：单测 3900 通过，消费者 2328 通过，DI 14 通过，Integration 9 通过/72 NotExecuted。消费者中的 `EveryAssetDerivesRoutineParametersWithSignedScaleAndAtomicFailure` 实际 20 项，覆盖五资产、同步/异步及双模式 fixture。报告和日志位于忽略目录，未纳入文档交付；原执行命令、失败与格式警告保留于[第 15.18 节](obconnector-dotnet-completeness.md#1518-2026-10-25-自动参数推导修复与验证)。

当前仍保留真实验收的 MySQL 60 通过、Oracle 79 通过/1 失败及其环境限制；本次没有重跑或扩展这些结论。Integration 跳过不能视为真实兼容通过。源码复核也没有证明所有编解码分支或未测并发组合正确。

交付内容包括：能力矩阵、23 项有稳定 ID 的缺口、触发场景/源码/测试/关闭条件、Java 模块对照、八个工作包及其依赖、测试矩阵、发布门槛和历史证据。下一步按[工作包](obconnector-dotnet-completeness.md#16-剩余交付工作包)逐项实施及验收；W1/W2 的隔离和失败契约优先，未达到发布门槛前维持 experimental。负责人、日期和首个正式版本的可选能力范围仍待项目确定，本文不代替这些范围决定。

## 13. SchemaOnly 模块（2026-10-25）

开发起点 `557a05a`；同步/异步 SchemaOnly 仅 Prepare 单条目标查询，读取准备时列元数据，禁止目标执行、fetch、参数上传及客户端 fallback。参数名称/顺序/数量仍需匹配，值不参与绑定验证。FetchSize/offset hint 不启用游标；prepared 元数据可复用，空 reader 立即完成 exchange，CloseConnection 保留原会话关闭契约。命名复杂列可能查询目录，准备阶段仍可能解析、检查权限和取得元数据锁。

独立走查发现的已释放 LOB 值仍被校验问题已用 RED 复现并修复；相关单测 30、完整单测 3928、完整消费者 2348（含新增 20 项）、DI 14 通过，Integration 9 通过/72 跳过；构建 0 警告/0 错误，格式及本地 pack 通过。原并行运行保留五项网络/时序失败，其 22 项子集和完整重跑均通过，未改产品/断言，不据此宣称长期负载时序已验收。详细契约、历史失败和最终结果见[第 15.20 节](obconnector-dotnet-completeness.md#1520-2026-10-25-schemaonly-元数据专用路径)。P1-05 的 KeyInfo、其他行为和真实准备/结果形状/代理矩阵继续开放。
