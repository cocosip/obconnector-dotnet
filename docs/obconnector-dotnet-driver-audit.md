# OceanBase .NET 驱动完整性走查报告

**走查日期：** 2026-10-24  
**仓库：** `D:\dotnet-code\obconnector-dotnet`  
**目标：** 为 OceanBase MySQL 兼容模式和 Oracle 兼容模式定义一份可执行的完整驱动缺口清单。  
**结论级别：** 这是源码、测试、打包记录和 Connector/J 静态结构对照形成的审计报告，不把本地 fixture 当作真实服务器兼容性证明。

## 1. 结论先行

当前 .NET 驱动已经超过“只有连接和简单 SQL”的阶段，但仍不能宣称是完整的生产级驱动。最关键的事实如下：

| 问题 | 结论 |
| --- | --- |
| Java 驱动有没有连接池 | 有。Connector/J 有 `OceanBasePoolDataSource`、`OceanBasePooledConnection`、`Pools`/`Pool`，覆盖池注册、借用/归还、最小/最大容量、空闲和生命周期清理以及池事件。 |
| .NET 当前有没有连接池 | 有基础物理会话池。`Pooling=true` 可复用已认证的 `PhysicalSession`，支持最大容量、等待超时/取消、借用前 `COM_PING`、空闲/总生命周期回收，归还时执行 reset；可识别的用户 session-state/temporary-object 语句会销毁会话，旧池 Dispose 后同一逻辑连接会重新取得新池。真正的 Close/Open 并发仍需专门回归。 |
| .NET 连接池是否完整 | 不是。池指标已经接入并有本地回归，但完整 session reset 和真实 OceanBase/OBProxy reset 兼容性仍未闭合；最小池预热及失败回滚、空池 static registry 清理已实现。 |
| 当前最大的生产风险 | 会话状态隔离、unknown outcome、ambient 本地事务的故障矩阵、活动连接故障恢复和真实代理/TLS/跨平台矩阵仍不完整。 |
| 是否已经达到 Java/.NET 成熟驱动水平 | 没有。现有实现可以继续开发和做受控试用，但 README 和包元数据应继续保持 experimental/未完成真实兼容性验证的表述。 |

## 2. 证据规则

本报告使用三种证据等级：

1. **源码/本地回归**：代码路径存在，并由单元测试、消费者测试或 loopback 协议 fixture 覆盖。
2. **包和运行时消费**：目标 TFM 能编译、加载和调用公共 API。
3. **真实互操作**：实际 OceanBase、OBProxy、TLS/KDC 或故障环境中的运行结果。

只有第 3 级证据可以把“未验证”升级为真实兼容。mock、自签名 `SslStream`、TCP loopback、协议 fixture 和包消费测试不能证明真实 SQL、目录、锁、拓扑、认证或代理行为。

现有真实数据库记录为 Windows/.NET 10、直连 OceanBase 4.4.2.2、TLS disabled：MySQL 模式 60 通过，Oracle 模式 79 通过、1 个 nested-table column 失败。OBProxy、其他版本、完整 TLS/plugin、KDC、故障注入和非 Windows 运行仍未闭合。

## 3. 当前能力矩阵

| 模块 | 状态 | 已有能力 | 主要缺口或边界 |
| --- | --- | --- | --- |
| 连接与配置 | 部分实现 | 连接字符串、超时、多地址、RoundRobin、模式确认、地址 resolver、空池 registry 清理 | OCP/TNS、健康发现、连接字符串归一化、完整版本/代理矩阵 |
| 物理连接池 | 部分实现 | 物理会话复用、最大容量、等待超时/取消、最小池预热及失败回滚、带 MySQL autocommit 校验的 `COM_PING`/reset、idle/total lifetime、空池 registry 清理、池指标 | 完整资源 reset、真实 reset 兼容性 |
| 会话隔离 | 部分实现 | `COM_RESET_CONNECTION`、MySQL `COM_INIT_DB`、reader 消耗后可复用、prepared statement generation 失效、未释放 REF CURSOR/LOB 归还时销毁、可识别的用户 session-state/temporary-object 语句归还时销毁 | 不可识别的 session-state、XA 和失败丢弃的完整矩阵 |
| ADO.NET 核心 | 部分实现 | `DbConnection`、`DbCommand`、`DbParameter`、`DbDataReader`、provider factory、`CloseConnection`、`SequentialAccess`、`SingleRow`、`SingleResult`、只读文本查询的 `SchemaOnly` | `KeyInfo`、完整 schema metadata、`DbDataAdapter`、`DbCommandBuilder` |
| .NET 8+ 资产 | 部分实现 | `DbDataSource`、`DbBatch`、savepoint、异步事务、GSSAPI 代码路径 | 现代 TFM 与旧 TFM 的完整行为矩阵、标准 `DbBatch` 生态验收 |
| 认证 | 部分实现 | native、old、clear、SHA-2、Ed25519、PAM、GSSAPI 路径 | 真实插件成功/拒绝矩阵、KDC、凭据注入和轮换 |
| TLS | 部分实现 | `VerifyFull`、`Required`、`Disabled`、主机名校验；直连非池化 `ClientCertificate`；可选撤销检查 | 企业 CA/自定义信任、跨平台撤销语义、池化证书身份策略和真实证书/mTLS 矩阵 |
| 基础 SQL | 已实现 | 文本执行、server prepare、参数绑定、多结果、流式读取、取消、超时 | 真实版本/代理/大数据量边界仍需验收 |
| 多语句 | 部分实现 | 显式 `Allow Multi Statements=true` 的文本多结果路径、默认关闭 | 参数/Prepare/cursor/Oracle 组合、压缩和真实服务器错误矩阵 |
| 压缩与 OB20 | 部分实现 | 基础 OB20、校验和、extra info、trace 结构 | MySQL 压缩、OB20 压缩 frame、proxy reroute |
| LOCAL INFILE | 部分实现 | `Allow Local Infile=true` 才请求 `CLIENT_LOCAL_FILES`；应用通过 `LocalInfileStreamFactory` 提供流；同步/异步上传后读取最终 OK，失败会销毁物理会话 | 真实 OceanBase/OBProxy 互操作、代理策略、超过 64 MiB、服务器错误和中断恢复 |
| 本地事务 | 部分实现 | MySQL/Oracle 隔离级别、savepoint（有名称限制）、本地 ambient enlistment | 分布式提升、提交响应丢失、取消后的状态机 |
| Unknown outcome | 部分实现 | 异常可标记未知执行结果，驱动不自动 replay | 发送/响应/commit/rollback 故障状态机和真实故障验证 |
| XA | 部分实现 | Start/End/Prepare/Commit/Rollback/Recover，MySQL/Oracle 分支 | coordinator crash recovery、普通池与 XA 的完整隔离验证 |
| Oracle 输出与游标 | 部分实现 | PrepareExecute、OUT/RETURNING、REF CURSOR、LOB、数组绑定 | cursor 参数方向、cursor-valued column、Prepare/FetchSize 组合 |
| 复杂类型 | 部分实现 | object/collection 标量成员、类型缓存和数组绑定 | 嵌套成员、LOB/cursor/national 成员、循环结构、文本结果 |
| 类型与字符集 | 部分实现 | 常用数值、时间、YEAR、BIT、JSON、zero-date、Oracle 时间/interval | 扩展 type code、更多字符集/排序规则、精度边界 |
| HA 与恢复 | 部分实现 | Open 阶段 TCP 候选地址重试、注入式 resolver | 活动连接迁移、透明重连、退避、reroute、安全重试 |
| Schema 元数据 | 部分实现 | 六类 `GetSchema` collection、异步 schema | Tables/Views/Indexes/Keys/Constraints/Types/Sequences 等扩展 |
| 批处理 | 部分实现 | `DbBatch` 顺序执行、Oracle array binding | rewrite、multi-send、bulk、跨命令 statement cache |
| 诊断 | 部分实现 | `ILogger`、`ActivitySource`、`Meter`、脱敏日志、池会话/借用/等待/丢弃/reset 失败/等待耗时指标 | 建连与探活细分指标、协议 debug、slow-query profile |
| 真实兼容性 | 未验证 | 直连 4.4.2.2 的有限记录 | OBProxy、TLS/plugin、版本、平台、网络故障和 XA 恢复矩阵 |

## 4. 缺口清单和交付优先级

### P0：不完成就不能称为完整驱动

| ID | 缺口 | 当前问题 | 完成条件 |
| --- | --- | --- | --- |
| P0-01 | 连接池生命周期 | 指标已接入并有本地回归；完整 reset、Close/Open 并发归属和真实服务器行为仍未闭合 | 配置一致的池可创建/清理；总数、借出、等待、丢弃、reset 失败可观测，并通过真实服务器验收 |
| P0-02 | 完整 session reset | prepared statement 已按物理会话 generation 失效；仍未覆盖所有借用者状态 | 事务、变量、临时对象、prepared statement、reader/cursor、LOB、XA 和 reset 失败丢弃均有 cross-borrower 测试 |
| P0-03 | Ambient transaction | 已支持本地 volatile enlistment；命令和 Batch 复用连接的本地事务 | 完成超时、in-doubt、响应丢失和真实故障矩阵；分布式提升仍明确拒绝 |
| P0-04 | Unknown outcome | 发送后断开、取消、提交响应丢失时无法总是判定服务端结果 | 明确发送前/发送后/响应中/commit/rollback 状态；写操作不自动重放；异常给出恢复动作 |
| P0-05 | 活动连接恢复 | 连接建立后不透明迁移、不自动重连 | 定义只读安全重试、幂等写和非幂等写边界；活动事务不被静默迁移 |

### P1：常见生产和 ADO.NET 能力

| ID | 缺口 | 目标 |
| --- | --- | --- |
| P1-01 | 多语句 | 默认安全关闭；显式开启后支持无参数文本多结果并保持连接可复用 | 参数/Prepare/cursor/Oracle 输出组合、真实服务器错误边界 |
| P1-02 | 压缩/OB20 | MySQL 压缩、OB20 压缩 frame、reroute 的协商、编解码和拒绝路径 |
| P1-03 | LOCAL INFILE | 只允许应用提供的流，禁止驱动读取任意本地路径 |
| P1-04 | TLS/mTLS | 企业信任根/自定义验证、跨平台撤销语义和真实证书矩阵；客户端证书直连非池化路径已实现 |
| P1-05 | ADO.NET 行为 | `SchemaOnly`、`KeyInfo`、schema/parameter metadata，以及明确的 adapter 策略 |
| P1-06 | Oracle 输出/游标 | 完整参数方向、cursor-valued column、PrepareExecute 结果形状 |
| P1-07 | 复杂类型 | 嵌套 object/collection、LOB/cursor/national 成员和缓存失效 |
| P1-08 | 类型/字符集 | 类型码清单、CLR/provider-specific 映射、字符集和精度契约 |
| P1-09 | HA/发现 | OCP/TNS 或等价发现、健康状态、退避和 OBProxy 拓扑 |
| P1-10 | Schema | 常用目录集合、限制数量、过程参数元数据和权限失败语义 |

### P2：生态、性能和运营增强

| ID | 缺口 | 目标 |
| --- | --- | --- |
| P2-01 | `DbDataAdapter`/`DbCommandBuilder` | 实现或正式声明不支持，不能保持模糊状态 |
| P2-02 | statement cache | 有上限、淘汰、session generation 和失效策略 |
| P2-03 | 批处理优化 | rewrite、multi-send、bulk 与 partial failure 结果模型 |
| P2-04 | 诊断 | 池等待、建连、reset、丢弃、协议 debug 和 slow-query profile |
| P2-05 | TFM/包生态 | 每个目标 TFM 的 consumer、运行时和发布矩阵 |
| P2-06 | 真实兼容矩阵 | 版本、直连/OBProxy、模式、TLS、认证、平台和故障报告 |

## 5. 连接池专项结论

### 5.1 当前实现路径

- `OceanBaseConnection.Open`/`OpenAsync` 从匹配配置的 `PhysicalSessionPool` 租用物理会话。
- 新建会话完成 TCP、TLS、认证和 OceanBase 身份确认；池内复用会话先执行 `COM_PING`。
- 逻辑连接关闭时，健康且非 XA 的物理会话执行 reset；MySQL 模式随后恢复初始 database。
- 达到最大容量时，后续 Open 等待归还；等待支持超时和取消。
- 空闲 lifetime、总 lifetime 和 `Min Pool Size` 的保留规则已存在；首次或后续租借前会补足 `Min Pool Size`，预热失败会回滚本次新建会话。
- reader 未完成、LOB/cursor 活跃、XA 专用、reset 失败或生命周期过期的会话必须销毁，不得回池。
- 未释放的 Oracle REF CURSOR 会设置归还销毁标记；最后一个 cursor 释放后才清除该标记。
- 未释放的 LOB 或 temporary LOB 句柄会设置归还销毁标记；句柄释放后才允许池化 reset。
- 可识别的用户 `SET`、`ALTER SESSION` 和临时对象语句会设置永久归还销毁标记；当前不依赖未验证的服务器 reset 语义。
- reset 成功后必须递增物理会话 generation；旧 prepared statement 只能被本地丢弃，不能再次发送关闭或执行请求。

### 5.2 与成熟 Java 池的差距

Connector/J 的池实现还包含池注册、最小/最大容量、生命周期调度、连接/语句事件和运营基础设施。当前 .NET 只完成物理 session 租约和基础回收；以下内容仍需补齐或明确取舍：

1. 连接字符串归一化和进程退出行为；
2. session variable、临时表/对象、statement、cursor、LOB、XA 的完整 reset；
3. 建连、探活、协议 debug 和 slow-query profile 等诊断补充；
4. Close/Open 并发、池等待取消、借出连接与 data source disposal 的资源归属验证；
5. 真实 OceanBase 与 OBProxy 上的 reset、预热、超时和并发验证。

本轮池走查对上述生命周期风险做了代码复核，当前状态如下：

| 检查项 | 当前状态 | 仍需补的证据 |
| --- | --- | --- |
| idle lifetime 与 total lifetime 混用 | 已修正；归还路径只检查 total lifetime，候选闲置会话才检查 idle lifetime，并保留 `Min Pool Size` | 长时间借用、并发借还和时钟边界压力回归 |
| 旧 `Open` 完成覆盖新一轮打开 | 已有 per-open cancellation 和 generation；过期完成会被拒绝，清理只处理同一 generation | Close/Open 交错压力回归，以及 DataSource 场景的资源归属 |
| Close 不能打断池等待 | 已有 `OpenCancellation`，同步/异步池等待使用该 token | 等待、Close、重新 Open 的并发回归 |
| 静态 registry 无清理 | 空池通过 `ReleaseIfEmpty` 移除并 Dispose；仍没有进程级统一 shutdown API | 非空池、`DbDataSource.Dispose` 和长时间运行进程的生命周期报告 |

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
| 本报告 | `docs/obconnector-dotnet-driver-audit.md` |
| 详细能力基线与验收要求 | `docs/obconnector-dotnet-completeness.md` |
| 公共 API 和配置契约 | `docs/obconnector-dotnet-architecture-design.md` |
| 历史验证、失败和剩余工作 | `docs/obconnector-dotnet-progress.md` |
| 真实兼容性和已知限制 | `docs/compatibility.md` |
| Java Connector/J 模块对照 | `docs/obconnector-j-architecture-outline.md` |
| TLS、客户端证书和撤销检查 | `src/OceanBaseConnector/Internal/Transport/TlsHandshakeFlow.cs`、`TlsUpgrade.cs`、`src/OceanBaseConnector/OceanBaseConnection.cs`、`tests/OceanBaseConnector.UnitTests/Transport/TlsComponentsTests.cs`、`ProviderConnectionTests.cs` |
| 物理会话池 | `src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs` |
| 物理会话 reset/状态 | `src/OceanBaseConnector/Internal/Session/PhysicalSession.cs` |
| 连接池回归 | `tests/OceanBaseConnector.UnitTests/PoolingTests.cs`、`tests/OceanBaseConnector.UnitTests/Session/PhysicalSessionPoolTests.cs` |
| 真实数据库入口 | `tests/OceanBaseConnector.IntegrationTests/`、`tests/OceanBaseConnector.Acceptance/` |

## 10. 明确不应偷换的结论

- Java 有连接池，不等于 Java 的每个 JDBC API 在本项目都已经对齐。
- .NET 有基础池化，不等于已经完成成熟连接池的状态隔离和运营能力。
- 本地 TLS、loopback 和 mock 不等于真实 OceanBase/OBProxy/TLS/KDC 兼容。
- OceanBase MySQL/Oracle 是兼容模式，不应写成原生 MySQL/Oracle 服务器支持。
- 服务端不支持 nested-table column，不能被记录成驱动已经支持该形状；procedure collection 参数是另一个能力。
- 未完成的功能必须明确拒绝或标记未验证，不能让深层协议错误替代公共契约。

