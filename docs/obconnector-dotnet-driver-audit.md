# OceanBase .NET 驱动完整性走查报告

**走查日期：** 2026-10-24  
**仓库：** `D:\dotnet-code\obconnector-dotnet`  
**初次源码基线：** .NET `06d58e1`；Java `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1` / 2.4.14。

**当前文档源码基线：** `master` / `96f544d`；ambient 单资源与初始化取消已提交，模块开发起点为 `f15716e`，交付证据见第 21 节；Java 固定参考 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`。已提交缓存模块和全部历史快照保留。
**目标：** 为 OceanBase MySQL 兼容模式和 Oracle 兼容模式定义一份可执行的完整驱动缺口清单。  
**结论级别：** 这是源码、测试、打包记录和 Connector/J 静态结构对照形成的审计报告，不把本地 fixture 当作真实服务器兼容性证明。

**最新状态：** 可观察 DDL 的复杂类型自动失效已本地实现，完整单测 4347、完整消费者 2478、专项消费者 40 通过，本地包/独立复核完成。见[第 22 节](#22-复杂类型可观察-ddl-自动失效2026-10-25)及[详细证据](obconnector-dotnet-completeness.md#1528-2026-10-25-复杂类型可观察-ddl-自动失效)。23 项和八工作包继续开放，不能宣称完整生产级驱动。

**本次交付：** 第 1.1/1.2 节汇总欠项，[第 22 节](#22-复杂类型可观察-ddl-自动失效2026-10-25)记录当前 DDL 模块；第 20 节保留修复前检查点；第 19 节记录已提交缓存模块；第 12、14、17 节保留修复前文档快照，第 13、15、16 节保留 SchemaOnly、KeyInfo、DataAdapter 的交付证据。全部 23 项仍以第 3、4 节和[详细追踪表](obconnector-dotnet-completeness.md#14-缺口到场景源码和验收的追踪表)为准。

**最近复核：** 2026-10-25；当前模块见第 22 节及详细基线第 15.28 节，修复前第 20/15.26 节保留。此前构建/测试、原失败、警告与跳过留在对应模块记录；本地回归不构成新增真实服务器、OBProxy 或独立运行时兼容性声明。

源码提交是初次走查基线；其后 P0-06 的本地修复和 P1-04 的自定义服务器证书验证分别记在[池路由记录](obconnector-dotnet-progress.md#2026-10-24-p0-06-pooled-resolver-routing)和 [TLS 验证记录](obconnector-dotnet-progress.md#2026-10-25-p1-04-tls-server-validation-callback)。本报告保留原缺口 ID 和未完成的真实验收要求。

**当前阅读入口：** [第 1.1 节](#11-本次评审摘要与剩余交付清单)汇总剩余工作，[第 1.2 节](#12-明确缺失和受限组合实施入口)列出具体开发入口，[第 22 节](#22-复杂类型可观察-ddl-自动失效2026-10-25)记录当前模块与真实验收边界；第 17 节的 P0-02 前缀发现及较早 KeyInfo/adapter 缺失描述是历史状态，不覆盖后续实现。

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

### 1.1 本次评审摘要与剩余交付清单

**当前文档复核日期：2026-10-25。** 本次文档以 `master` / `96f544d` 为已提交基线，包含会话识别、statement cache 和 ambient 单资源及初始化取消；最终回归和独立复核见第 21 节，原取消缺口保留在第 20 节历史检查点。Java 参考 HEAD 本次再次核实为 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`。第 12、14、17 节保留早期文档快照，W1—W8 清单表示当前欠项。

Java 和 .NET 的连接池都已存在；.NET 默认 `Pooling=false`。最新识别模块通过保守销毁可识别的状态会话来避免复用，**不等于完整 reset 已实现，也不证明真实借用者隔离已通过**。无法静态识别的函数/触发器副作用、资源与 XA、并发/长期运行及真实服务器 reset 仍须验收。

以下是完整驱动当前的执行清单。编号沿用原 23 个 ID，不增加平行清单；每项具体触发场景、源码/测试入口及关闭标准见[详细追踪表](obconnector-dotnet-completeness.md#14-缺口到场景源码和验收的追踪表)，工作包依赖见[第 16 节](obconnector-dotnet-completeness.md#16-剩余交付工作包)。

| 工作包及缺口 | 当前剩余工作 | 对使用者的影响 | 最低验收产物 |
| --- | --- | --- | --- |
| W1 池与会话：P0-01、P0-02、P0-06 | 保留已有容量、预热、探活、清池、路由身份及会话识别；补完整状态/资源/XA 隔离、并发与长期运行；定义配置归一化和 shutdown 范围 | 同一进程多个请求复用会话时，完整状态恢复及资源上限尚缺证明 | 双借用者状态矩阵；满池/取消/清池/Close/Open/Dispose/reset 交错与资源曲线；双模式直连及 OBProxy reset 报告 |
| W2 事务与恢复：P0-03、P0-04、P0-05 | 保留已交付的单资源/终态/身份、自动初始化取消及池/协调器恢复回归；补真实故障状态机与结果核对/重建契约 | 断线后应用可能无法确认写入或提交结果，必须核对业务状态；不得盲目重试 | 故障注入矩阵、in-doubt/超时结果、会话丢弃及重建示例；证明未知写无自动重放；分布式提升明确拒绝 |
| W3 协议：P1-01、P1-02、P1-03 | 标准压缩、opt-in 文本多语句和应用流 LOCAL INFILE 已有；补组合/错误矩阵、OB20 压缩契约与实现、reroute | 可选协议组合仍受限，不能把标准压缩等同于 OB20 压缩；代理重定向未交付 | capability/版本依据、分片/序号/校验/解压限额、多结果中途错误、上传取消及真实代理报告 |
| W4 信任与发现：P1-04、P1-09、P1-11 | TLS 回调/客户端证书隔离、凭据快照/轮换已有；补健康发现、退避、OCP/TNS 或等价适配，以及真实企业认证验证 | 企业 CA、撤销、短期凭据或节点变更部署尚不能依据本地 fixture 获得兼容承诺 | 企业 CA/mTLS/撤销、插件/KDC/凭据过期刷新失败、地址更新/错误身份、Windows/Linux 报告 |
| W5 命令与类型：P1-05、P1-06、P1-07、P1-08 | 保留 SchemaOnly、KeyInfo 和 signed DecimalScale；补 MySQL StoredProcedure 执行及输出契约、Oracle 输出方向/结果形状、复杂 LOB/cursor/national 成员及类型/字符集；保留自动 DDL 失效回归并补真实验收 | 目录可推导参数不等于过程可执行；复杂结果、目录键和扩展编码不能按基础 SELECT 的通过结果推定支持 | 双模式过程/输出与 Command/Batch/REF CURSOR 行为表、NULL/精度/溢出/编码/资源释放、真实目录及 Prepare 行为/锁报告 |
| W6 目录与生态：P1-10、P2-01 | 常用 Schema、DataTypes、参数推导、同步 adapter/有界命令生成已有；补真实目录/权限/版本和 CRUD/并发验收 | 生成 UPDATE/DELETE 要求完整可见行键；复杂形状需显式命令，推导不扩大过程执行支持 | 双模式目录/权限/重载/参数方向，实际 CRUD/乐观并发/事务/锁、独立运行时报告 |
| W7 性能与诊断：P2-02、P2-03、P2-04 | 有限 idle statement cache 已本地实现，补真实 DDL/reprepare/资源/负载验收；选择并实现承诺的 rewrite/multi-send/bulk 优化；补分段建连/探活、慢查询与协议诊断 | 当前 Batch 逐项执行，性能和诊断完整性尚不能按成熟驱动承诺 | 缓存容量/失效/关闭、部分失败/取消/包限额，吞吐/延迟/峰值内存对照及脱敏/监听开销报告 |
| W8 平台与发布：P2-05、P2-06 | 明确承诺的服务器/代理版本、平台/TFM/ORM 范围；补 matrix.* 真实案例、包来源/许可和发布检查 | 包含五种 TFM 资产不等于五个运行时已独立验证，实验性标签仍须保留 | 每个承诺 profile 的包消费、实库/代理/TLS/认证/故障报告；保留失败及跳过原因、XA 外部协调器恢复证据 |

上述工作中，W1/W2 的隔离和失败处理优先；W3—W6 按实际部署与公共 API 承诺交付，W7 的优化以测量为依据，W8 随所有模块持续积累证据。依赖及关闭要求以详细基线第 16 节为准；负责人和日期尚未分配，本文不把建议写成排期承诺。

**仍须确定的支持范围：** OB20 压缩的客户端版本/协商，OCP/TNS 或等价发现方式，批量优化方式，统一 shutdown，以及正式承诺的 TFM/平台/ORM 范围。分布式 ambient 提升当前明确不支持；EF Core provider 是独立生态工程。这些边界需公开记录，不能默认为已经实现或已经排除。Java 2.4.14 也拒绝压缩 OB20，不能把它列为现成的 Java 移植能力。

**当前证据入口：** 第 22 节及[详细记录第 15.28 节](obconnector-dotnet-completeness.md#1528-2026-10-25-复杂类型可观察-ddl-自动失效)区分原始失败、修复后的最终报告和真实验收边界；缓存竞争修复前后报告保留在第 19 节。会话识别模块的 4188/2408 报告留在第 18 节，不能替代缓存模块的验证。当前仍缺真实 DDL/OBProxy、资源负载和独立运行时证据。

**完整驱动的完成定义：** 每个承诺能力必须同时具备公共契约、对应实现、可复现回归和要求的真实环境证据；明确不支持的组合应在发送前拒绝并有文档。23 项是跟踪项，包含缺实现、受限实现与欠验收，并非 23 个已复现缺陷。现有 Oracle nested-table column 失败、全部跳过和第 8 节发布门槛继续保留，本次文档交付不关闭任何完整缺口。

### 1.2 明确缺失和受限组合：实施入口

以下是当前源码可直接定位的功能限制，用于从 W1—W8 汇总进入具体开发。它不是新增缺口编号，也不是新复现的故障清单；完整 23 项及验收要求仍见第 4 节和详细基线第 14 节。

| 触发场景 / 归属 | 当前行为及影响 | 源码入口 | 补齐与验收要求 |
| --- | --- | --- | --- |
| MySQL `CommandType.StoredProcedure`，P1-05 / W5 | 没有对应执行计划，执行入口拒绝；已有过程目录和参数推导不提供执行能力。文本 CALL 也不能替代完整 OUT/InputOutput 契约 | [OceanBaseCommand.Start](../src/OceanBaseConnector/OceanBaseCommand.cs) | 定义过程名、参数顺序/方向、返回值、多结果和输出发布时间；覆盖同步/异步、取消、错误及池复用，并取得 MySQL 模式实库报告 |
| Oracle 过程的独立 Prepare 或 FetchSize，P1-06 / W5 | PrepareExecute 执行时准备；独立 Prepare 和正 FetchSize 的组合拒绝 | [OceanBaseCommand.Start](../src/OceanBaseConnector/OceanBaseCommand.cs) | 明确可支持的准备/执行/获取路径；分别测试输出、多个结果、提前关闭和服务端资源释放；不承诺服务端无法提供的形状 |
| REF CURSOR 输入或列值，P1-06 / W5 | 参数只接受 Output/ReturnValue；Input/InputOutput 拒绝。列元数据可识别 REF CURSOR，但普通列值解码拒绝 | [OracleCommandPlan](../src/OceanBaseConnector/Internal/Sql/OracleCommandPlan.cs)、[ResultColumn](../src/OceanBaseConnector/Internal/Protocol/ResultColumn.cs) | 补参数方向与 cursor-valued column 的所有权契约；验证父 reader/statement/事务/连接提前结束、取消及池归还，不泄漏或误用 cursor ID |
| object/collection 含 LOB、cursor、national 成员，P1-07 / W5 | 标量及嵌套 object/collection 已有；这些成员的 wire layout 不支持，复杂列还要求二进制协议 | [ComplexType](../src/OceanBaseConnector/Internal/Values/ComplexType.cs)、[ResultColumn](../src/OceanBaseConnector/Internal/Protocol/ResultColumn.cs) | 按成员类型补双向绑定、NULL、嵌套、编码和资源释放；循环或超限类型保持确定拒绝；与服务端 nested-table column 失败分开验收 |
| 类型 DDL 后再次使用描述，P1-07 / W5 | 已有 `ClearComplexTypeCache`；本次增加 Oracle 文本 CREATE/ALTER/DROP/RENAME/TRUNCATE 目标发送时的自动失效，之后须重新加载描述并重建绑定值；外部或不透明 DDL 仍须显式清理 | [OceanBaseConnection.ComplexTypes](../src/OceanBaseConnector/OceanBaseConnection.ComplexTypes.cs)、[ComplexTypeDictionaryTests](../tests/OceanBaseConnector.UnitTests/ComplexTypeDictionaryTests.cs) | 明确可观察 DDL、外部变更和手动清理的边界；保证旧描述拒绝、新描述完整发布、活动结果快照稳定；自动失效的完整本地/真实证据分别验收，不能由 statement cache 的存在推定 |
| OB20 压缩或 reroute，P1-02 / W3 | 压缩帧和 reroute 标志拒绝；标准 MySQL 压缩是另一条已实现路径。固定 Java 2.4.14 也拒绝压缩 OB20 | [Ob20Packet](../src/OceanBaseConnector/Internal/Protocol/Ob20Packet.cs)、[Ob20Stream](../src/OceanBaseConnector/Internal/Protocol/Ob20Stream.cs) | 先取得客户端链路、版本、capability 与校验规范；再实现并用真实代理验证，详见[协议前提](obconnector-dotnet-completeness.md#163-ob20-压缩的客户端协议前提) |
| 节点故障或拓扑变化，P0-05、P1-09 / W2、W4 | 已有地址列表与 resolver，候选切换限 Open 阶段 TCP 失败；没有内置 OCP/TNS、健康退避或活动连接迁移 | [EndpointSelector](../src/OceanBaseConnector/Internal/EndpointSelector.cs)、[PhysicalSession](../src/OceanBaseConnector/Internal/Session/PhysicalSession.cs) | 先交付故障后状态与应用重建契约；发现适配、健康退避和透明恢复分别定义范围；真实节点/代理故障中不得重放未知写或迁移未完成事务 |
| 高吞吐 DbBatch，P2-03 / W7 | 逐条执行，Oracle array binding 已有；尚无通用 rewrite/multi-send/bulk 优化 | [OceanBaseBatchReader](../src/OceanBaseConnector/OceanBaseBatchReader.cs) | 选择目标模式和优化方式，验证部分失败、受影响行数、输出顺序、事务与取消；以吞吐、延迟和峰值内存实测决定交付 |
| 排查慢建连/慢查询，P2-04 / W7 | 已有操作 span、日志和池指标；建连/探活分段、协议 debug、慢查询 profile 未完整提供 | [DriverDiagnostics](../src/OceanBaseConnector/Internal/DriverDiagnostics.cs)、[ConnectionLog](../src/OceanBaseConnector/Internal/ConnectionLog.cs) | 定义阶段耗时与慢查询诊断契约；覆盖脱敏、监听关闭开销和应用回调异常；不能记录口令、认证载荷或敏感参数 |

**现有实现的验证工作仍是主任务：** 池容量和清理、完整借用者隔离、PSPE/unknown outcome、TLS/凭据轮换、SchemaOnly/KeyInfo、DataTypes/参数推导、DataAdapter 和 statement cache 均须按第 1.1 节取得相应真实报告。不能因本表集中列出待开发入口，就遗漏已有模块的压力、故障、权限和独立运行时验收。

**范围决定单列：** 分布式 ambient 提升当前明确拒绝，显式 XA 不等于自动提升或 durable coordinator；EF Core provider 属独立工程；OB20 压缩、发现适配、批量优化和正式平台承诺按第 1.1 节冻结范围。负责人、期限及性能 SLA 尚未指定，本文不虚构承诺。

**本次文档核对边界：** 当前源码与 DDL 模块证据见第 22 节；本轮完整单测 4347、完整消费者 2478、专项消费者 40、DI 14 与 Integration 9 通过/72 NotExecuted 已核查；11 个加载程序集的哈希和时间戳未变。第 21 节的 4291/2478 属上一模块历史。真实数据库仍保留 MySQL 60 通过、Oracle 79 通过/1 失败；CI 配置包含 Windows/Ubuntu 和 Windows 包消费者运行时设置，不等于本轮取得了对应成功报告。

## 2. 证据规则

本报告使用三种证据等级：

1. **源码/本地回归**：代码路径存在，并由单元测试、消费者测试或 loopback 协议 fixture 覆盖。
2. **包和运行时消费**：目标 TFM 能编译、加载和调用公共 API。
3. **真实互操作**：实际 OceanBase、OBProxy、TLS/KDC 或故障环境中的运行结果。

只有第 3 级证据可以把“未验证”升级为真实兼容。mock、自签名 `SslStream`、TCP loopback、协议 fixture 和包消费测试不能证明真实 SQL、目录、锁、拓扑、认证或代理行为。

初次走查以源码调用链发现 P0-06 resolver/池路由问题及 P1-05 SchemaOnly 执行语义。P0-06 随后已有同步/异步复现与本地修复，详见第 5 节和进度台账；SchemaOnly 已交付 Prepare-only 路径及本地回归，真实副作用/锁/准备形状验收仍待完成。真实拓扑兼容不能由这些 loopback 回归推定。

现有真实数据库记录为 Windows/.NET 10、直连 OceanBase 4.4.2.2、TLS disabled：MySQL 模式 60 通过，Oracle 模式 79 通过、1 个 nested-table column 失败。OBProxy、其他版本、完整 TLS/plugin、KDC、故障注入和非 Windows 运行仍未闭合。

## 3. 当前能力矩阵

| 模块 | 状态 | 已有能力 | 主要缺口或边界 |
| --- | --- | --- | --- |
| 连接与配置 | 部分实现 | 连接字符串、超时、多地址、RoundRobin、模式确认、地址 resolver、空池 registry 清理 | OCP/TNS、健康发现、连接字符串归一化、完整版本/代理矩阵 |
| 物理连接池 | 部分实现 | 物理会话复用、最大容量、等待超时/取消、最小池预热及失败回滚、带 MySQL autocommit 校验的 `COM_PING`/reset、idle/total lifetime、空池 registry 清理、池指标、`ClearPool`/`ClearAllPools`/source `Clear` | 完整资源 reset、广泛并发/长期运行、真实 reset 兼容性 |
| 会话隔离 | 部分实现 | `COM_RESET_CONNECTION`、MySQL `COM_INIT_DB`、reader 消耗后可复用、prepared statement generation 失效、未释放 REF CURSOR/LOB 归还时销毁、可识别的用户 session-state/temporary-object 语句归还时销毁 | 不可识别的 session-state、XA 和失败丢弃的完整矩阵 |
| ADO.NET 核心 | 部分实现 | `DbConnection`、`DbCommand`、`DbParameter`、`DbDataReader`、provider factory、`CloseConnection`、`SequentialAccess`、`SingleRow`、`SingleResult`、仅 Prepare 目标的 `SchemaOnly`、有界键/隐藏列 `KeyInfo`、同步 DataAdapter/单表命令生成 | 真实目录/CRUD、生成形状与可见键限制、完整 schema metadata、SchemaOnly 真实准备行为与形状矩阵 |
| .NET 8+ 资产 | 部分实现 | `DbDataSource`、`DbBatch`、savepoint、异步事务、GSSAPI 代码路径 | 现代 TFM 与旧 TFM 的完整行为矩阵、标准 `DbBatch` 生态验收 |
| 认证 | 部分实现 | native、old、clear、SHA-2、Ed25519、PAM、GSSAPI 路径 | 真实插件成功/拒绝矩阵、KDC、短期凭据服务及轮换实库验收 |
| TLS | 部分实现 | 默认平台信任库和主机名校验；可选服务器验证回调及按引用隔离的普通池；池化/直连 `ClientCertificate` 和 source 证书快照；可选撤销检查 | 真实企业 CA/轮换/撤销和跨平台语义、真实 mTLS 矩阵 |
| 基础 SQL | 已实现 | 文本执行、server prepare、参数绑定、多结果、流式读取、取消、超时 | 真实版本/代理/大数据量边界仍需验收 |
| 多语句 | 部分实现 | 显式 `Allow Multi Statements=true` 的文本多结果路径、默认关闭；Oracle 匿名块和程序 DDL 的内部分号按单个程序单元处理 | 真正的多语句参数/Prepare/cursor/Oracle 输出组合和真实服务器错误矩阵；程序单元非法尾部由服务端解析 |
| 压缩与 OB20 | 部分实现 | 显式 Use Compression 的标准 MySQL 压缩；基础 OB20、校验和、extra info、trace 结构 | OB20 压缩 frame、proxy reroute 及真实压缩矩阵 |
| LOCAL INFILE | 部分实现 | `Allow Local Infile=true` 才请求 `CLIENT_LOCAL_FILES`；应用通过 `LocalInfileStreamFactory` 提供流；同步/异步上传后读取最终 OK，失败会销毁物理会话 | 真实 OceanBase/OBProxy 互操作、代理策略、超过 64 MiB、服务器错误和中断恢复 |
| 本地事务 | 部分实现；本地单资源模块 | MySQL/Oracle 隔离级别、savepoint；PSPE 单资源、终态/身份及自动初始化取消/失败清理回归 | 真实模式/版本/代理/事务管理器故障和恢复示例；分布式提升明确拒绝 |
| Unknown outcome | 部分实现 | 异常可标记未知执行结果，驱动不自动 replay | 发送/响应/commit/rollback 故障状态机和真实故障验证 |
| XA | 部分实现 | Start/End/Prepare/Commit/Rollback/Recover，MySQL/Oracle 分支 | coordinator crash recovery、普通池与 XA 的完整隔离验证 |
| Oracle 输出与游标 | 部分实现 | PrepareExecute、OUT/RETURNING、REF CURSOR、LOB、数组绑定 | cursor 参数方向、cursor-valued column、Prepare/FetchSize 组合 |
| 复杂类型 | 部分实现 | object/collection 标量及嵌套成员、类型缓存和数组绑定 | LOB/cursor/national 成员、循环结构、文本结果；可观察 DDL 自动失效本地实现已有，外部/不透明 DDL 须显式清理，真实 DDL 验收仍缺 |
| 类型与字符集 | 部分实现 | 常用数值、时间、YEAR、BIT、JSON、zero-date、Oracle 时间/interval | 扩展 type code、更多字符集/排序规则、精度边界 |
| HA 与恢复 | 部分实现 | Open 阶段 TCP 候选地址重试、注入式 resolver | 活动连接迁移、透明重连、退避、reroute、安全重试 |
| Schema 元数据 | 部分实现 | Oracle 20/MySQL 18 个集合、静态 DataTypes 及自动参数推导有本地回归 | 实库字典、权限/版本/代理和类型/DDL 矩阵；推导不扩大过程执行支持 |
| 批处理与准备缓存 | 部分实现 | `DbBatch` 顺序执行、Oracle array binding、opt-in 有限物理会话 idle LRU | rewrite、multi-send、bulk，以及真实缓存/资源/负载验收 |
| 诊断 | 部分实现 | `ILogger`、`ActivitySource`、`Meter`、脱敏日志、池会话/借用/等待/丢弃/reset 失败/等待耗时指标 | 建连与探活细分指标、协议 debug、slow-query profile |
| 真实兼容性 | 部分验证 | 直连 4.4.2.2 的有限记录，包含 Oracle 失败 | OBProxy、TLS/plugin、版本、平台、网络故障和 XA 恢复矩阵 |

### 3.1 除连接池之外的待开发范围

跨命令 statement cache 已有 opt-in 本地实现和初步回归，真实 DDL/reprepare、资源/负载/代理验收仍缺。DataAdapter/CommandBuilder 已有本地填充、映射/事件、CRUD 和乐观并发实现；自动 UPDATE/DELETE 要求完整可见行键，复杂形状需显式命令，真实 CRUD/锁/目录及运行时验收仍缺。KeyInfo 已有单基表键/隐藏列与复杂查询 unknown 的本地契约，真实目录验收仍缺。自动参数推导已通过完整本地回归和独立走查，真实目录互操作继续开放。`DataTypes` 已完成本地实现及全量/五资产回归，真实服务器类型、DDL、编码和版本/代理范围仍须验收。协议侧缺 OB20 压缩/reroute，发现侧缺内置 OCP/TNS 或等价适配与健康/退避策略；Oracle 侧还有限定游标方向/结果形状、复杂成员的缺口；可观察 DDL 自动失效已有本地实现，外部/不透明 DDL 仍须显式清理且真实 DDL 未验收。

凭据提供/轮换已有本地实现：每次 Open 的认证快照、版本退休、过期和取消规则已通过最终回归。真实短期凭据服务和认证插件轮换仍须验收。

已有实现但尚未闭合的是：会话隔离、事务/unknown outcome 的故障矩阵，SchemaOnly 的真实准备行为与元数据形状，扩展类型/字符集，批量性能与诊断，以及真实版本/代理/TLS/认证/平台验收。常用 Schema 目录、标准 MySQL 压缩、客户端证书池化、清池和池指标均已有本地实现，不应重新列为完全缺失。

完整分类见[详细基线第 4.1 节](obconnector-dotnet-completeness.md#41-除连接池外具体还缺什么)；凭据契约清单见第 5.5 节，DataTypes/参数推导/KeyInfo 见第 5.10.1 节。部分可选能力是否进入首个正式版本尚未确定，不能把未确认的范围决定写成已排除。

## 4. 缺口清单和交付优先级

### P0：不完成就不能称为完整驱动

| ID | 缺口 | 当前问题 | 完成条件 |
| --- | --- | --- | --- |
| P0-01 | 连接池生命周期 | 指标、显式 ClearPool/ClearAllPools/source Clear 及旧会话退休已接入，Closed 回调重开归属已有回归；多线程压力、完整 reset 和真实服务器行为仍未闭合 | 配置一致的池可创建/清理；物理会话数、借用次数、等待、丢弃、reset 失败可观测，清池不破坏活动工作或容量上限，并通过压力及真实服务器验收 |
| P0-02 | 完整 session reset | prepared statement 按物理会话 generation 失效；注释/多语句/不透明调用识别及目标发送销毁标记已有本地回归，完整状态/资源/并发和真实 reset 仍缺 | 保留第 18 节识别与复用回归；事务、变量、临时对象、prepared statement、reader/cursor、LOB、XA 和 reset 失败丢弃均有真实 cross-borrower 测试 |
| P0-03 | Ambient transaction | PSPE 单资源/终态、初始化与晚回调身份及取消/失败清理已有本地交付 | 保留本地回归与原始 RED；补真实故障矩阵、业务结果核对和重建示例；分布式提升明确拒绝 |
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
| P1-05 | ADO.NET 行为 | 补 MySQL StoredProcedure 参数方向、输出和多结果执行契约（第 1.2 节）；保留 KeyInfo 键/隐藏列与复杂形状 unknown 契约及回归，补真实目录和完整 schema/parameter metadata、Batch/游标行为表；保留 `SchemaOnly` Prepare-only 回归并补真实准备行为验收，以及明确的 adapter 策略 |
| P1-06 | Oracle 输出/游标 | 完整参数方向、cursor-valued column、PrepareExecute 结果形状 |
| P1-07 | 复杂类型 | 保留已有嵌套 object/collection、显式/可观察 DDL 自动失效；补齐 LOB/cursor/national 成员和真实 DDL 验收 |
| P1-08 | 类型/字符集 | 类型码清单、CLR/provider-specific 映射、字符集和精度契约 |
| P1-09 | HA/发现 | OCP/TNS 或等价发现、健康状态、退避和 OBProxy 拓扑 |
| P1-10 | Schema | 常用目录/限制参数及 DataTypes 静态映射已有本地实现和最终回归；自动参数推导已有本地交付，真实目录/权限/版本/代理矩阵待验收，见 [Schema 契约](schema.md) |
| P1-11 | 凭据提供/轮换 | 每次 Open 快照、全口令替换、版本退休、过期/取消及脱敏已有本地实现和最终回归；真实插件/凭据服务/KDC 验收仍缺 |

### P2：生态、性能和运营增强

| ID | 缺口 | 目标 |
| --- | --- | --- |
| P2-01 | `DbDataAdapter`/`DbCommandBuilder` | 保留本地填充、映射/事件、原始值/NULL、行键/并发和事务回归；补双模式真实 CRUD/锁/目录与运行时报告；生成值回填及复杂形状限制须公开 |
| P2-02 | statement cache | 本地 idle LRU、容量/淘汰/关闭、epoch 和 reset generation 已实现；补真实 DDL/reprepare、资源/负载/代理验收 |
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
| G2 | ambient transaction、取消和 unknown outcome | 保留 PSPE 及初始化取消的本地交付；真实响应丢失/故障矩阵及应用恢复指引仍待完成 |
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
- DataAdapter 的形状限制、EF Core provider 尚缺、没有透明 replay 与核心连接正确性是不同层级；明确的版本支持范围是验收依据。

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

## 14. 当前完整文档交付（2026-10-25）

本节保留 KeyInfo 开发前的文档交付快照；后续本地实现与验收边界见第 15 节，不以本节的“仍缺”覆盖新实现。

本次以已提交的 `de15ec2` 为源码基线，整理完整驱动所缺的开发、验证和范围决定。面向长期运行、并发借用连接的服务；未约定吞吐、延迟和并发 SLA。本文没有把所有未验收项都认定为已复现缺陷，也没有把 Java 的可选功能直接变成 .NET 的发布承诺。

### 14.1 文档组成与使用顺序

| 文档 | 解决的问题 |
| --- | --- |
| 本报告第 3、4 节 | 当前有什么、缺什么、先做什么；保留 P0 六项、P1 十一项、P2 六项 |
| [完整性基线第 5 节](obconnector-dotnet-completeness.md#5-分模块要求和验收标准) | 每个模块的功能契约、模式差异及验收要求 |
| [逐项追踪表](obconnector-dotnet-completeness.md#14-缺口到场景源码和验收的追踪表) | 每个 ID 的触发场景、影响、源码/测试入口及关闭标准 |
| [八个工作包](obconnector-dotnet-completeness.md#16-剩余交付工作包) | 剩余开发、依赖和必须交付的验证产物；每个 ID 有且只有一个主工作包 |
| [API 设计](obconnector-dotnet-architecture-design.md)、[Schema 契约](schema.md) | 公开 API、配置、参数及模式/TFM 限制 |
| [进度台账](obconnector-dotnet-progress.md)、[兼容性记录](compatibility.md) | 已提交实现、历史执行命令、失败/跳过和真实部署证据 |
| [Java 结构对照](obconnector-j-architecture-outline.md) | 参考实现模块来源；不作为 Java 运行验收或 .NET 兼容性证明 |

### 14.2 当前仍需补齐的能力

下表是评审索引，完整的 23 项要求仍以第 4 节和逐项追踪表为准；一个类别可以横跨多个 ID。

| 类别 / ID | 源码事实与实际影响 | 剩余交付及最低验收 |
| --- | --- | --- |
| 连接池与隔离：P0-01/02/06 | Java 有内置池；.NET 默认关闭池化，已有容量、等待/取消、预热、清池/退休、路由身份隔离及指标。完整状态 reset、并发和长期运行证据仍不足，不能宣称成熟池化 | 双借用者验证事务/变量/临时对象/reader/statement/LOB/cursor/XA；无法恢复则销毁；补满池、Dispose/reset/Close/Open 交错、资源上限及双模式/代理实库报告 |
| 事务与故障恢复：P0-03/04/05 | 本地 ambient 和 unknown-outcome 分类已有；分布式提升被拒绝，活动连接无透明迁移。响应丢失后盲目重试可能重复写入 | 发送前/后、响应中、commit/rollback 丢包、超时/in-doubt/提前关闭故障矩阵；异常恢复指引和应用重建示例；未知写禁止自动 replay |
| 协议选项：P1-01/02/03 | 文本多语句、标准 MySQL 压缩和应用流 LOCAL INFILE 已有；组合受限，OB20 压缩/reroute 被拒绝 | 保留显式授权及发送前拒绝；补中途失败/取消、包与解压上限、上传及代理矩阵；OB20 压缩先取得[客户端协议依据](obconnector-dotnet-completeness.md#163-ob20-压缩的客户端协议前提) |
| 信任、认证和发现：P1-04/09/11 | TLS 回调、客户端证书隔离、凭据提供/轮换已有本地实现；内置 OCP/TNS、健康缓存/退避及真实企业身份矩阵不足 | 企业 CA/mTLS、撤销/轮换、plugin/KDC、短期凭据过期/刷新失败；发现更新、错误身份/节点/代理故障、总预算和跨平台报告 |
| ADO.NET 行为：P1-05 | Command 拒绝 KeyInfo；Batch 仅接受 CloseConnection/SequentialAccess。SchemaOnly 已改为目标 Prepare-only，不能再列为“仍执行目标” | 补主键/唯一键、组合键、来源/别名/连接/表达式及隐藏键列契约；不从单列 flags 臆造唯一性或可更新性；行为组合和真实准备/锁/结果形状验收 |
| Oracle 与类型：P1-06/07/08 | 部分 REF CURSOR 方向、cursor-valued column、PrepareExecute 组合、复杂 LOB/cursor/national 成员、外部 DDL 缓存失效和扩展字符集/类型仍不足 | 方向/NULL/形状/精度/溢出/编码及资源所有权矩阵；保留已完成的嵌套类型和有符号 DecimalScale 回归；服务端不支持的形状单独记录 |
| 目录与生态：P1-10、P2-01 | DataTypes、常用目录和参数推导已有；Factory 的 DataAdapter/CommandBuilder 返回 null，DataSet 更新和命令生成工具无法使用。过程目录不等于 MySQL StoredProcedure 执行支持 | 补目录/权限/重载/大小写/空集合/版本/代理验收；adapter 若纳入版本则实现 CRUD/并发冲突，若排除则正式公开范围决定；EF Core provider 另行验收 |
| 性能与运维：P2-02/03/04 | 无跨命令 statement cache；Batch 顺序执行，已有 Oracle array binding；建连/探活分段和慢查询/协议诊断不足 | 有限缓存的失效/淘汰/statement close；承诺批量路径的部分失败/取消/事务/包上限及性能对照；诊断脱敏、单位和开销 |
| 运行时与发布：P2-05/06 | 五个 TFM 资产不等于五种独立运行时；六个 matrix.* 仍为 DeferredReason 占位。历史 Oracle 实库失败和 cleanup failure 保留 | 确定支持 profile；实际 nupkg consumer、版本/模式/直连与代理/TLS/认证/平台/故障/XA 恢复报告；承诺范围缺证据仍阻塞发布 |

本次重读了 Command/Batch 行为白名单、ResultDbColumn、provider factory、池配置/reset、OB20 拒绝路径、项目/CI 和 Acceptance 的六个占位项；Java 本地 HEAD 仍为上述参考提交，PoolDataSource 调用池实现。其余模块沿用已有源码走查及记录，本次没有重新逐行审计所有 codec、SQL 或并发路径。

### 14.3 已有报告复核与交付边界

以下数字来自本次重新解析 `TestResults/schema-only/` 下已有 TRX 的每个 UnitTestResult，属于历史执行结果复核，没有在本次运行测试：

| 报告 | 实际记录 |
| --- | --- |
| unit-recheck.trx | 3928 通过，0 失败/跳过 |
| consumer-verified.trx | 2348 通过，0 失败/跳过；其中 SchemaOnly 新增消费者方法实际 20 项 |
| di.trx | 14 通过 |
| integration.trx | 9 通过、72 NotExecuted；不计为新实库互操作证据 |
| unit-verified.trx / timing-recheck.trx | 原并行运行 3923 通过/5 失败；五个方法的 22 项独立子集通过，原失败保留 |

同时重读已有 Release 构建日志的 0 警告/0 错误及格式日志的 workspace-load warning；未将日志重读表述为本次构建或格式检查通过。源码、链接、编号及文档结构检查另记在[本次文档复核](obconnector-dotnet-completeness.md#1521-2026-10-25-schemaonly-交付后完整文档复核)。

后续先完成 W1/W2 的隔离与失败契约，再按 W3-W7 补功能及矩阵，W8 持续积累发布证据。每个 ID 都需留下实现提交、回归、命令/退出码、运行环境、失败/跳过及报告位置；负责人、日期和首个正式版本可选范围仍未分配。本次文档不关闭任何 ID、工作包或完整驱动目标。

## 15. KeyInfo 模块（2026-10-25）

从 `de15ec2` 开发：在同一会话/事务中 Prepare 原目标、读取有界参数化键目录，再执行目标或返回 SchemaOnly 元数据。单基表且能证明保留行语义的 SELECT 优先完整主键、否则非空唯一键，缺失成员追加为隐藏尾列；原 CommandText/Prepare 缓存不变。FieldCount 包含隐藏列，VisibleFieldCount 保留原投影数量。可空单列 UNIQUE 与行键分别标记，组合成员不误标单列唯一。复杂或不确定形状保持原 SQL/unknown，不虚构来源、表达式或更新性；详细边界见 [API 契约](obconnector-dotnet-architecture-design.md#key-information-and-hidden-columns)。

独立 review_keyinfo_design 发现的 Oracle 非唯一支撑索引/约束成员与 owner 身份、NQ 引号、参数表达式动态元数据三项 Important 已用 RED 复现并修复；最终产品/测试走查无剩余 Critical/Important，审阅者未代替主任务运行完整套件。最终完整单测 4013、专项 85、完整消费者 2368（含新增五资产实际 20 项）及 DI 14 通过；Integration 9 通过/72 NotExecuted。构建 0 警告/0 错误；完整证据见[第 15.22 节](obconnector-dotnet-completeness.md#1522-2026-10-25-keyinfo-键元数据与隐藏列)。原沙箱 TLS/PFX 117 项失败、重叠构建警告、筛选零项及所有 RED/修复记录保留。

真实字典 SQL/权限/DDL/锁、版本/OBProxy、跨平台及独立运行时矩阵仍开放；整个 P1-05、23 个缺口和八个工作包不因本子模块关闭。

## 16. DataAdapter / CommandBuilder 模块（2026-10-25）

从 `d7e8b5a` 开发 P2-01：五资产新增 typed adapter/builder 和 factory 入口，基于 System.Data.Common 完成同步填充、DataSet 映射、AddWithKey、原始值/NULL、行事件、顺序 CRUD、ContinueUpdateOnError 和 DBConcurrencyException；生成命令继承源事务/超时，不自动跨行提交或重试。自动 UPDATE/DELETE 需要完整可见行键，未知来源、隐藏键和上下文相关复杂绑定需显式命令；自动命令不回填 identity/default。MySQL 限定库名不被基类丢弃，可空 UNIQUE 不会被误作行键。公开 API 和缓存/引号边界见 [adapter 契约](obconnector-dotnet-architecture-design.md#data-adapters-and-command-generation)。

独立 review_data_adapter 找到 Scale=0 错误声明小数秒精度的问题，两个实际 RED 复现并通过清除声明修复；最后产品/测试复核无剩余 Critical/Important。最终完整单测 4047（新增实际 34）、完整消费者 2388（新增实际 20）、DI 14 通过，Integration 9 通过/72 NotExecuted，均退出 0；边界子集 94 和消费者子集 21 通过。最终构建 0 警告/0 错误、十文件格式退出 0（保留 workspace-load warning）；本地双包/五资产哈希及嵌入 README 已核对，不构成独立运行时验收。结果和所有原失败见[详细记录第 15.23 节](obconnector-dotnet-completeness.md#1523-2026-10-25-dataadapter--commandbuilder)。较早章节的尚缺只描述当时快照。

未新增真实 OceanBase/OBProxy、权限/DDL/锁、跨平台或独立运行时证据，23 个缺口和八工作包均不因本地子模块而整体关闭。

## 17. 完整文档交付与当前待办（2026-10-25）

本节为会话识别子模块开发前的文档快照，所引行号和“未修复”描述对应 `4dd20a7`；当前模块及证据见第 18 节和详细基线第 15.24 节。

本次按已提交源码 `4dd20a7` 整理，工作负载假设是长期运行、多个请求并发借还连接的服务。Java 本地参考 HEAD 已重新核对为 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`。没有约定并发量、吞吐或延迟 SLA，不能据此承诺性能。本次只改文档；DataAdapter 是最近交付的功能模块，会话隔离仍为下一项待修工作。

### 17.1 文档覆盖与事实结论

Java 驱动已有内置连接池：`OceanBasePoolDataSource` / `OceanBasePooledConnection` 提供入口，`Pools` / `Pool` 处理注册、容量、借还、探活、reset 和清理。已静态核对这些调用；未运行 Java 池测试。.NET 当前也已有可选基础池化，默认 `Pooling=false`，并非仍然完全没有连接池；最大容量、等待/取消、预热、探活、生命周期、清池、路由/证书/凭据身份隔离和指标都有本地实现。**完整会话隔离、并发压力和真实 reset 验收尚未完成，当前仍是实验性驱动。**

| 评审需要回答的问题 | 完整内容所在位置 |
| --- | --- |
| 当前有何能力、什么完全没有、什么只完成一部分？ | 本报告第 3、4 节；[详细基线第 4.1 节](obconnector-dotnet-completeness.md#41-除连接池外具体还缺什么) |
| 每个问题的场景、影响、源码/测试依据和关闭标准是什么？ | [全部 23 项追踪表](obconnector-dotnet-completeness.md#14-缺口到场景源码和验收的追踪表)，保留 P0 六项、P1 十一项、P2 六项 |
| 最终功能契约、模式差异和最小验收是什么？ | [分模块要求](obconnector-dotnet-completeness.md#5-分模块要求和验收标准)、[公开 API/配置](obconnector-dotnet-architecture-design.md)、[目录](schema.md)、[凭据](credentials.md) |
| 开发依赖、实施顺序、要留下什么证据？ | [W1—W8 工作包](obconnector-dotnet-completeness.md#16-剩余交付工作包)，23 项各有一个主归属；本报告第 7、8 节 |
| 已做到哪里，哪些通过、失败或跳过？ | [进度台账](obconnector-dotnet-progress.md)、详细基线第 15 节和 [真实兼容记录](compatibility.md)；历史失败和跳过不被后续本地通过覆盖 |
| Java 对照是否等于 .NET 必须复制全部实现？ | 本报告第 6 节及 [Java 模块图](obconnector-j-architecture-outline.md)；按 ADO.NET 语义确定范围，Java 静态源码不等于互操作证据 |

### 17.2 除连接池外仍需完成的内容

| 工作类别 | 当前未闭合内容 | 对应 ID / 工作包 |
| --- | --- | --- |
| 事务与失败恢复 | ambient 超时/in-doubt/关闭竞态、commit/rollback 响应丢失、unknown-outcome 状态及应用重建契约；不自动重放未知写入 | P0-03/04/05，W2 |
| 协议选项 | OB20 压缩/reroute 尚缺；文本多语句、标准 MySQL 压缩和应用流 LOCAL INFILE 已实现但组合、失败及真实代理矩阵不足 | P1-01/02/03，W3 |
| 信任、发现和凭据 | 内置 OCP/TNS 或等价发现范围、健康状态/退避；TLS/mTLS、撤销/轮换、凭据服务/plugin/KDC 的真实验收 | P1-04/09/11，W4 |
| ADO.NET、Oracle 和类型 | Command/Batch/游标行为矩阵；REF CURSOR 方向及结果形状；复杂类型 LOB/cursor/national 成员、外部 DDL 缓存失效；扩展 metadata/类型码/字符集 | P1-05/06/07/08，W5 |
| 目录和生态 | 常用 Schema、DataTypes、参数推导、KeyInfo、DataAdapter/CommandBuilder 已有本地实现；真实目录/权限/DDL/生成 SQL/锁/并发与运行时证据仍缺。生成值回填及复杂查询更新限制须公开 | P1-10、P2-01，W6；键元数据与 W5 联动 |
| 性能和运维 | 跨命令 statement cache 明确缺失；rewrite/multi-send/bulk 范围与实现；建连/探活分段、slow-query 和协议诊断；压力与内存上限报告 | P2-02/03/04，W7 |
| 运行时和发布 | 独立运行时的实际 nupkg 消费、Windows/Linux、真实版本/模式/直连与 OBProxy/TLS/认证/网络故障及 XA crash recovery；六个 matrix.* 占位仍需实现 | P2-05/06，W8 |

以上不是“全部重写”。已实现能力应保留回归并补验收；明确拒绝的组合只有在正式纳入支持范围后才扩展。分布式提升、透明迁移、自动重连、EF Core provider 以及额外发现/性能路径不能因 Java 有类似模块就默认承诺。可选范围、负责人、日期及性能目标仍待确定；承诺范围内的安全、隔离和真实兼容证据仍是发布门槛。

### 17.3 优先处理：P0-02 的具体识别缺口

当前 [PhysicalSession.cs](../src/OceanBaseConnector/Internal/Session/PhysicalSession.cs) 第 304—314 行仅对开头固定前缀进行比较。因此前置注释、ALTER/CREATE 多词关键字之间的注释或非单空格、后续多语句、MySQL 可执行注释及不透明过程调用都可能漏掉“归还时销毁”的标记。[OceanBaseCommand.cs](../src/OceanBaseConnector/OceanBaseCommand.cs) 第 254 行又在长度、多语句、绑定等校验之前标记，Prepare 或未发送的拒绝命令可能造成额外销毁。现有 [PoolingTests.cs](../tests/OceanBaseConnector.UnitTests/PoolingTests.cs) 第 17—19 行只为该状态理论提供无注释 SET/临时表两例。

具体输入、影响、统一修复边界和验收要求已补入[详细基线第 5.1.1 节](obconnector-dotnet-completeness.md#511-当前会话状态识别的具体缺口)。这是源码走查发现，尚未实施修复或新增回归；不能表述为已复现实库污染，因为 `COM_RESET_CONNECTION` 对具体状态的恢复能力仍需实库验证。优先补 A 执行/归还、B 借用后的物理身份及基线验证，兼顾普通查询和无执行命令的安全复用；随后完成 W1/W2 的资源、并发及故障矩阵。P0-01/02/06 仍归属 W1，不因本次文档整理关闭。

### 17.4 本次核实的证据和未检查范围

本次重新读取当前池化配置/状态标记/命令校验路径、factory、Oracle PrepareExecute/扩展 metadata 拒绝边界、两包 TFM、Acceptance 占位、Java 池入口/借还/reset/清理，并解析以下已有报告的逐条 UnitTestResult。其余 codec、事务、目录及并发分支沿用前次走查记录，本次没有重新逐行走查全仓库。

| 已有本地报告，位于 `TestResults/data-adapter/` | 本次重新解析的结果 |
| --- | --- |
| `unit-verified.trx` | 4047 Passed，0 Failed/NotExecuted |
| `consumer-verified.trx` | 2388 Passed，0 Failed/NotExecuted |
| `di.trx` | 14 Passed |
| `integration.trx` | 9 Passed、72 NotExecuted；不增加真实服务器验收证据 |
| `temporal-actual-red.trx` | 2 Failed，作为已修问题的原始失败保留；修复记录见第 16 节 |

已有构建日志记录 0 警告/0 错误，格式日志仍有 workspace-load warning；包清单声明两包五资产及嵌入 README 与当时工作区一致，五资产仅在 .NET10 host 加载。本次未重跑构建、单测、格式、打包或实库测试。历史包内 README 对应原交付内容，不因本次更新 README 就成为新包验证结果。

本次只验证文档的相对链接/锚点、围栏、23-ID 及工作包覆盖、历史记录保留和 diff。未新增 Java 运行、真实 OceanBase/OBProxy、跨平台、独立运行时或压力报告。完整驱动目标、23 项和八工作包均保持开放；后续每项仍需实现提交、失败与通过回归、独立走查及对应验收证据。

## 18. 会话状态识别与发送边界模块（2026-10-25）

从 `4dd20a7` 补 P0-02 本地识别子模块。注释/词间空白、后续语句、可执行注释、匿名/带标签块及不透明过程不再依赖固定前缀；MySQL 用户变量和命名锁按保守规则销毁。Prepare、发送前拒绝、KeyInfo/SchemaOnly 元数据及长参数上传不提前标记，目标发送前才落地持久标记。公共 API/配置不变，五资产共用同一执行边界；[详细契约](obconnector-dotnet-architecture-design.md#pooled-session-state-after-user-sql)。

独立 review_session_state 找到命名锁的普通标识符/表与 CTE 清单误判、表达式 FROM 漏判，均有真实失败回归并修复；最后源码复核无剩余 Critical/Important。保留的 Minor 是嵌套 CTE 锁同名声明可保守销毁，已公开代价。SQL_MODE 引号反斜杠也会保守销毁；词法识别不证明任意存储函数、触发器或真实 reset 安全。

最终完整单测 4188 通过（实际新增 141、未移除旧例），DI 14 通过，Integration 9 通过/72 NotExecuted；均退出 0。完整消费者 2408 通过，实际新增 20 项五资产用例、无旧例移除，退出 0；均在同一 .NET10 host 运行。Release 构建 0 警告/0 错误，11 文件格式退出 0（保留 workspace-load warning）；本地双包/五资产哈希与 README 已核对，不构成独立运行时证据。过程中的全部失败及修复、最终命令/报告见[第 15.24 节](obconnector-dotnet-completeness.md#1524-2026-10-25-会话状态识别与发送边界)。此前第 17 节及第 5.1.1 节描述修复前文档快照；本地实现不关闭整个 P0-02、23 项或八工作包。

## 19. 跨命令 statement cache 模块（2026-10-25）

开发起点 `6d399fd`。`Statement Cache Size` 默认 0，可配置 0..1024；每个物理会话缓存 idle prepared IDs，按精确绑定后 SQL 复用并按归还顺序淘汰。命令持有期间保持独占，容量仅限制 idle cache，应用持有的命令仍须 Dispose。正常参数执行或显式 Prepare 才使用 server prepare，不改变普通文本 SQL 的选择。完整契约见 [API](obconnector-dotnet-architecture-design.md#prepared-statement-cache)。

共享目标发送边界负责上下文失效，成功 ChangeDatabase 也使缓存和仍被命令持有的旧 epoch 失效；reset/传输关闭忘记旧 IDs。完整结果和匹配参数数目才可进入缓存；执行错误、未读结果、元数据新准备、Oracle PrepareExecute/重写 KeyInfo 的 owner 保留原保护。新单测覆盖独占/重复归还、LRU、两模式 reset、DDL/会话/取消、SchemaOnly、部分元数据/参数数目、游标终止及新流参数；五资产消费者专项 20 通过。以下为租约竞争修复前的历史结果：完整单测 4235（实际新增 47、无旧例移除）、DI 14 通过，Integration 9 通过/72 NotExecuted，均退出 0；Release 构建 0 警告/错误，14 文件格式验证退出 0（保留 workspace-load warning）。本地包两个包/各五资产哈希和 README 匹配；完整消费者 2428 通过、0 失败/跳过、退出 0，实际新增 20 项且无旧例移除，期间未重建加载的程序集；五资产只在同一 .NET10 host 运行，不是独立运行时证据。此段记录竞争修复前的阶段证据；过程失败、fixture 修正和最终证据见[第 15.25 节](obconnector-dotnet-completeness.md#1525-2026-10-25-跨命令-statement-cache)。

提交前另复现同步/异步两例租约窗口：A 在检查 epoch 后等待连接锁，B 先执行 DDL，A 仍上传旧 ID 参数。原 RED 两例实际为预期 PREPARE(22)、收到 LONG_DATA(24)。共享 EnsurePrepared/Async 现于独占租约内重新检查 Database、Generation、CacheEpoch，失配关闭/重准备；49 项专项通过。独立增量复核无剩余可操作问题；修复后最终完整单测 4237 通过（新增 49）、完整消费者 2428 通过（新增 20），无旧例移除、0 失败/跳过，均退出 0；DI 14、Integration 9 通过/72 NotExecuted，退出 0。最终 Release 构建 0 警告/错误，15 文件格式检查退出 0（workspace-load warning 保留）；首次新增格式检查的一处空白诊断/退出 2 和修正过程留在详细记录。最终本地包的五资产哈希及嵌入 README 匹配。原报告另存 before-epoch-race-*，五份原始失败报告的失败例均对应最终通过；消费者运行期间未重建程序集，仍只证明 .NET10 host 上的五资产，不是独立运行时。

独立 review_statement_cache 的源码/测试/契约走查无 Critical/Important；Minor 拒绝措辞已澄清为本地发送前拒绝，服务端拒绝已发送 DDL 仍保守失效。审阅者未运行测试。两模式合成对照均验证 8 个命令只需 1 次 prepare，默认关闭则 8 次；修复前 TRX 整个测试时长 MySQL 141.84→26.81 ms、Oracle 158.71→20.00 ms，含人为 15 ms prepare 延迟及握手开销，不是真实 SQL 性能承诺；外部 DDL/函数/触发器没有依赖追踪，驱动不自动 replay。真实 DDL/reprepare、资源/负载、版本/OBProxy 和独立运行时仍须验收。此前第 12/14/17 节的提交前/修复前快照保留；第 1.1 节为当前阅读入口，不能用其旧缺失描述覆盖新模块。23 项和八工作包均继续开放。

## 20. Ambient 单资源事务模块（2026-10-25）

**本节是完整走查文档检查点，不是模块交付声明。** 已提交 HEAD 为 `f15716e`；工作区存在未提交的 ambient 实现及测试。第 1.1 节汇总全部八工作包，详细基线第 4、14 节覆盖全部 23 项缺口，既有第 12—19 节及详细第 15.1—15.25 节保留历史状态。

原 volatile enlistment 会允许多个数据库连接各自本地提交，也可能在协调器提交通知抛异常后再次通知而掩盖未知提交。工作区改用协调器单资源槽（PSPE）：接受后再初始化，第二资源/提升发送事务 SQL 前拒绝，确认 COMMIT、可信拒绝和未知结果分别报告 Committed、Aborted、InDoubt；通知不再捕获于数据库操作 catch 中。独立走查发现 Initialize 等待期间 Close/Open 的竞态，双模式真实线程交错 2 RED 后已通过原 Open generation 在 exchange 锁内复核修复，晚回调再绑定原本地事务；池 A/B 回归核对同一物理会话。完整行为见 [ambient API](obconnector-dotnet-architecture-design.md#ambient-transactions)。

**剩余 Important：自动 OpenAsync 登记初始化忽略取消令牌。** `OceanBaseConnection.cs` 的 OpenAsync 调用同步 EnlistTransaction，`AmbientEnlistment.Initialize` 转入 `OceanBaseConnection.Transactions.cs` 的 BeginAmbientTransaction，后者传 `CancellationToken.None`。控制 SQL 等待响应期间取消 OpenAsync 无法中断此阶段；Command Timeout=0 时可无界等待。这是已核实的源码缺口，尚无专门实际取消 RED/GREEN，不能由当前 4285 项通过推定已支持。最小修复为自动登记内部路径携带 opening token，保留公开同步 API；验收需双模式实际阻塞回包、调用者 token、连接/池/协调器槽清理和无 replay，再验证五资产消费者。scope timeout 可能等同步 Initialize 返回再回滚，不能替代 wire cancellation。

| 已有报告（本次重读，非新运行） | 结果与限制 |
| --- | --- |
| 原始失败 | `initial-red.trx` 6 失败；`initialization-race-red.trx` 2 失败，均保留 |
| 专项和全量单测 | `pool-green.trx` 52 通过；`unit-verified.trx` 4285 通过、0 失败/跳过，相对已提交缓存模块新增 48 项、无旧例移除 |
| 消费者 | `consumer-focused.trx` 40 通过：五资产 × 两模式 × 同步/异步 × 确认/丢失 COMMIT；实际两个 TCP fixture、公共 API、第二资源无事务 SQL、无 replay。全量消费者未完成 |
| DI / Integration | DI 14 通过；Integration 9 通过、72 个单独 NotExecuted，未启用实库；保留跳过，不能只看汇总 Counters |
| 构建 / 格式 | 已有 Release build 0 警告/错误；9 个 C# 文件格式验证保留 workspace-load warning；不证明取消缺口已修复 |
| 包 / 真实环境 | 本模块最终包哈希/嵌入 README 尚未验证；无新增 OceanBase/OBProxy/跨平台/独立运行时证据 |

独立 reviewer 未运行测试，已核对终态、竞态修复、池身份和消费者可信性；仍有上述 Important，故不能记为复核通过。原始产物位于忽略目录 `TestResults/ambient-single-phase/` 及根目录对应日志，需要另行归档才能交接。详细命令、报告、触发条件和关闭标准见 [第 15.26 节](obconnector-dotnet-completeness.md#1526-2026-10-25-ambient-单资源事务开发与完整文档检查点)。

本次同步 README、兼容性、API、完整基线和进度台账，逐项区分功能缺失、受限实现、开发中与欠真实验收。下一步先修复并回归自动初始化取消，再完成本模块最终全量消费者、构建/测试/格式、独立复核及包验证；之后继续真实事务故障、结果核对/重建和 W1—W8 验收。此前 2428 全量消费者与旧包仅验证上一个模块；同一 .NET10 host 加载五资产不等于五运行时。历史 Oracle nested-table column 失败、跳过及全部 23 项继续开放。

## 21. Ambient 单资源事务与初始化取消本地交付（2026-10-25）

开发基线 `f15716e`。PSPE 单资源登记、可信提交/拒绝/未知终态、初始化 Open generation 与原本地事务身份已闭合本地路径；第二数据库资源在其本地事务 SQL 前拒绝；后续提升明确拒绝并中止原本地事务，未知写/提交无 replay。第 20 节保留原取消缺口及当时未提交状态；本节是修复后的交付，不覆盖此前失败/警告/跳过。

独立走查的两个 Important 均有实际复现：Initialize 等协调器时 Close/Open 误用新会话（双模式 2 RED）现于获取 exchange 的同一锁内复核原 generation；自动 OpenAsync 初始化忽略取消（双模式 × 两条控制 SQL 的 4 RED）现把有效 opening token 传至同步 Initialize 的事务 exchange。取消响应等待后清本地/ambient 状态、丢弃会话、释放池容量并保留 exact caller token；成功 Open 后取消/释放原 CTS 不影响 commit。scope timeout 仍不能替代 wire cancellation；显式同步登记没有取消参数。

原始 6 RED、初始化竞态 2 RED、取消 4 RED 均保留并对应最终通过。取消测试最初因 scope Dispose 早于断线清理而中止的日志保留，修正测试清理后才取得 4 个可退出的真实超时失败。五资产取消消费者实际使用同 scope/事务和 maxpool1/wait1s 配置，第一次控制回包挂起后取消，第二条物理连接重新初始化并执行精确 SQL；Close/ROLLBACK/restore/reset、晚到 rollback 及 ClearPool→EOF 验证池/协调器清理。独立 reviewer 未运行测试，源码/测试复核无剩余可操作 Critical/Important/Minor。

最终 Release build 0 警告/错误；九文件格式验证退出 0，保留 workspace-load warning。全量单测 4291、完整消费者 2478、DI 14 通过，0 失败/跳过，均退出 0；Integration 9 通过/72 个单独 NotExecuted，未启用实库。相对上一已提交模块实际新增 54 单测/50 消费者，无旧例移除。完整消费者期间未重建加载的程序集；五资产仍只在同一 .NET10 host 运行。最终本地 no-build pack 退出 0，两包各五 DLL 哈希及嵌入 README 与构建/工作区匹配，未发布。原 4285 单测/40 专项消费者是取消修复前历史，50 专项消费者则包含新取消测试，均保留。

命令、最终报告、原始失败和验收边界见[第 15.27 节](obconnector-dotnet-completeness.md#1527-2026-10-25-ambient-单资源事务与初始化取消本地交付)。产物位于忽略目录 TestResults/ambient-single-phase 及根目录对应日志，需要独立交接时另行归档。本地交付不提供 durable prepare/跨资源原子提交或分布式恢复，也不关闭 P0-03/04/05 的真实 OceanBase/OBProxy/事务管理器故障、跨平台/独立运行时和长期负载验收；全部 23 项/八工作包及既有 Oracle 失败/历史跳过保留。

## 22. 复杂类型可观察 DDL 自动失效（2026-10-25）

开发基线 `96f544d`。Oracle 文本 CREATE/ALTER/DROP/RENAME/TRUNCATE 的目标发送边界增加独立元数据代际；绑定、字典加载/发布及新结果快照均拒绝旧上下文。支持默认关闭 statement cache 的场景，Prepare/发送前失败不失效；已发送但被服务端拒绝的 DDL 保守失效。重载描述并重建值后才能绑定，原返回值仍可读。外部或不透明 DDL 沿用显式清理，不提供数据库变更通知或自动 replay。

8 个确认的旧描述 RED 后取得专项 25 通过；新增边界进入全量单测 4347 通过（实际新增 56、移除 0），五资产专项消费者 40 通过。完整消费者 2478 通过/0 失败/0 跳过，10 分 32 秒，无用例新增/移除；运行期间 11 个程序集哈希和时间戳均未变。最终 Release build 0 警告/错误，10 文件格式退出 0（workspace warning），DI 14 通过，Integration 9 通过/72 NotExecuted；两本地包各五 DLL 和 README 校验一致，未发布。独立源码/测试 reviewer 无 Critical/Important；最终文档复核的一处 Minor 活跃摘要旧措辞已修正，无剩余可操作问题。reviewer 未自行运行测试。初始夹具失败及原始报告保留，见[详细交付记录](obconnector-dotnet-completeness.md#1528-2026-10-25-复杂类型可观察-ddl-自动失效)。

P1-07 的 LOB/cursor/national 成员和真实 DDL/锁/隐式提交/代理矩阵仍未闭合；本地 fixture 与同一 .NET10 host 的五资产消费不建立真实部署或独立运行时承诺。23 项/八工作包、历史失败和跳过全部保留。
