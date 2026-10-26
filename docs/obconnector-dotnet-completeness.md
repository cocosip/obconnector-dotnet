# OceanBaseConnector .NET 完整驱动能力与缺口基线

**Current delivery overview:** [all 23 gaps, committed implementation and remaining work](obconnector-dotnet-delivery-status.md).
The overview separates remaining development from real-environment validation; historical records below retain their original scope.

**最近复核：** 2026-10-27，基于master当前工作区开发；保持master，全部23项及八工作包仍开放。
**当前开发模块：** P1-07/W5 Oracle 复杂类型 cursor 结果成员；二进制 object/collection 中的 type163 现在接受恰为四字节小端游标标识，并复用当前执行的 `OracleCursorOwner`。同步/异步、缓冲/SequentialAccess 本地正向4项及畸形4项通过；复杂 cursor 输入、文本复杂结果和真实 SQL/代理兼容性仍未验证，见第15.88节。
**上一开发模块：** P1-06/W5 Oracle 文本游标值列；文本结果 type163 现在接受恰为四字节小端游标标识，并复用已有会话绑定的 `OceanBaseRefCursor` 所有权。同步/异步、缓冲/SequentialAccess 本地回归4项通过，见第15.87节。
**最近完成模块：** P1-08/P1-10 Oracle FLOAT/NUMBER_FLOAT参数派生、INOUT目录归一及按需字符长度补查；完整单测7656、专项189及受影响消费者168通过，独立最终走查无遗留问题，见[最新交付记录](obconnector-dotnet-progress.md#2026-10-27-oracle-float-routine-derivation)及第15.84节。新增实库过程/函数用例仅编译，未运行。
**此前完成模块：** Oracle NUMBER_FLOAT206精确结果、binary precision元数据及CommandBuilder绑定（184a767）；完整单测7578、专项204及受影响消费者229通过，独立走查无遗留问题，见第15.83节。2项实库和2项既有NUMBER对照均在初始握手失败，未完成认证或SQL，不能声称FLOAT存储成功。
此前JSON客户端字符集（b94a421）完整单测7543、专项111、消费者570通过；其10项实库与2项默认字符集对照握手失败保留在第15.82节。
此前CLOB整输入预校验（1da3ea8）、客户端SQL/参数字符集（b66cda1）、会话协议I/O耗时（f0edabf）及当前借出会话指标（33c052b）保留其独立记录；[输入字符集契约](client-character-sets.md)记录当前边界。
**当前验证：** 复杂结果专项439项通过，包含同步/异步、缓冲/SequentialAccess、匿名类型和畸形游标回归，均零失败/跳过；串行Release解决方案构建零警告/错误，产品源码格式验证通过（workspace-load warning）。完整单测已有一次7544通过/141失败（总计7685），其中包含本次更新前的匿名 type163 旧断言；该断言已按现新契约移除，未重跑全套。真实 OceanBase/OBProxy 互操作与其他工作包仍未验证。
**历史限制：** 前一模块构建曾因与testhost并行产生DLL锁而失败，原始记录保留。历史TNS文件替换失败根因仍未确认，八次独立及后续完整运行未复现；没有修改TNS或添加绕过。
**剩余开发：** 其余national/JSON/CLOB/元数据边界、输入/双向及复杂 cursor 参数、复杂整列 RETURNING、压缩OB20/代理reroute、多发送/独立bulk及六项验收占位等，逐项见[统一进度及剩余工作](obconnector-dotnet-delivery-status.md)。
前一模块2项Oracle FLOAT实库及2项既有NUMBER对照连接尝试均止于初始greeting，未完成认证或SQL，服务器/网络根因未明；后续仍需其他资源指标、负载及完整部署验收。历史JSON/TNS及握手失败均保留原记录。
**初次审计：** 2026-10-24，.NET `06d58e1`；Java 固定参考 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1` / Connector/J 2.4.14。
**适用仓库：** `D:\dotnet-code\obconnector-dotnet`；参考本仓库源码/测试及 `D:\java-code\obconnector-j` 固定源码。
**文档性质：** 能力审计、公共目标契约与验收基线；已实现、部分实现、明确缺失、未验证和开发检查点分开记录。23 项及八工作包尚未整体完成。


**阅读入口：** 池等待结果诊断见第15.86节；地址级覆盖见第15.65节、组上限见第15.64节、分组黑名单覆盖见第15.63节；REF CURSOR结果限制见第15.62节；批次结果限制及共享SingleRow修正见第15.61节；Batch元数据及源条目创建见第15.60节；诊断卸载修复见第15.59节；池通知交接见第15.58节，协议帧诊断保留于第15.57节，二进制游标列保留于第15.56节，黑名单/尝试上限保留于第15.55节，分组/策略/权重保留于第15.54节，OCP HTTP地址发现保留于第15.53节，TNS文件发现保留于第15.52节，端点TCP健康缓存保留于第15.51节，TCP重试保留于第15.50节，连接级ERR保留于第15.49节，Closed回调修复保留于第15.48节，complex national保留于第15.47节，Oracle输出提示见第15.46节，事务响应丢失及核对示例见第15.45节，外置 LOB 验收见第 15.44 节，内联资源验收见第 15.43 节，原文档检查点见第 15.42 节；第 4 节列全部 23 项缺口；第 5 节定义契约；第 14 节逐项列触发场景、源码/测试入口及关闭证据；第 16 节拆为八工作包。[完整走查报告](obconnector-dotnet-driver-audit.md)第 1.1/1.2 节汇总欠项，第 36 节记录当前 reader 租约修复，第 35 节保留 XA 池隔离验收，第 34 节保留 Oracle 自动提交模块，第 33 节保留文档及 RED 检查点，第 32 节保留已提交 NUMBER 修复，第 31 节保留慢命令诊断；第 30/29 节保留阶段诊断与 Oracle Prepare 交付；第 28 节保留开发前文档检查点，第 8 节是发布门槛。

**历史边界：** 第 5.1.3 节及走查报告第 25 节保留归一化修复前发现；第 5.1.1/15.19/15.21 节及报告第 17 节保留早期会话识别状态，后续识别契约见第 5.1.2/15.24 节。第 15.1—15.40 节保留此前实现、开发检查点、原始失败与跳过；它们不覆盖当前状态，源码存在、本地通过与真实验收分别判断。

此前 [P0-06 池路由](obconnector-dotnet-progress.md#2026-10-24-p0-06-pooled-resolver-routing)和 [P1-04 TLS 回调](obconnector-dotnet-progress.md#2026-10-25-p1-04-tls-server-validation-callback)的本地交付保留。本文不删除缺口 ID、不以当前摘要抹去 Oracle 失败，也不将单一 .NET10 host 的五资产消费记为五种独立运行时。

## 1. 目的和结论

本项目是一个面向 OceanBase MySQL 兼容模式和 Oracle 兼容模式的托管 ADO.NET provider。当前已经具备连接、认证、基础 SQL、参数绑定、结果读取、本地事务、XA、Oracle PrepareExecute、LOB、复杂类型、数组绑定、TLS、OB20 和诊断等基础模块。

当前驱动仍不能称为完整的生产级 ADO.NET 驱动。连接池已具备基础容量、等待、存活探测和生命周期控制，ambient transaction 已采用协调器单资源登记并传播自动初始化的 opening token，但还需要补齐完整 reset、协议可选能力、活动连接故障恢复、企业 TLS、ADO.NET 命令行为、Oracle 结果形状、目录元数据和真实兼容矩阵。

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
| .NET 当前有没有连接池？ | 有基础池化，但不是完整连接池。 | 默认 `Pooling=false`，需显式启用。`Pooling=true` 通过 [PhysicalSessionPool.cs](../src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs) 复用已认证物理会话；归还时执行 `COM_RESET_CONNECTION`，Oracle 模式再设置并验证自动提交，MySQL 模式再执行 `COM_INIT_DB`。当前已支持最大容量、等待超时、最小池预热及失败回滚、空闲/总生命周期回收、借用前存活探测、空池 registry 清理和池指标；完整 reset 与真实服务器 reset 兼容性仍未闭合。 |
| 是否已经达到 Java/.NET 生产驱动水平？ | 没有。 | 还缺少任意隐式会话副作用与资源的完整 reset/隔离矩阵、ambient 真实故障矩阵、活动连接故障恢复契约、协议可选能力、企业 TLS、完整 ADO.NET 行为和真实 OceanBase/OBProxy 验收。分布式 ambient 提升当前明确拒绝，显式 XA 不提供自动提升或 durable coordinator。 |

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

### 1.4 开发目标的当前能力与历史提交统计

目标开始于2026-10-24 16:24（Asia/Shanghai），到已提交功能master/f71a979实查68次本地提交：44功能、17修复、4专项验收、3文档；已包含本次分组黑名单模块，不包含随后进度文档提交，提交次数不代表完整能力数。23个大缺口整项关闭0，八工作包整体关闭0。各ID大小不同，混合实现与部署/故障验收，尚无加权分母，不能用提交/单测数声称完成百分比。

以下能力表随本地实现更新；上述提交统计仅为 `f71a979` 历史快照，当前模块门禁与提交状态见最新进度台账。

| 范围 | 当前本地子模块 | 核心剩余 |
| --- | --- | --- |
| 池/会话 | 复用、容量等待、预热回收清池指标、路由/证书/配置隔离、自动提交、reader/LOB/cursor/XA保护 | 完整隐式状态、故障、并发/长期资源及代理reset |
| 事务/恢复 | 单资源ambient、所有权/取消、响应丢失及新租约核对 | 完整发送/服务器/协调器故障、活动连接重建 |
| 命令/生态 | SchemaOnly、KeyInfo、SingleRow/Result、adapter/builder、目录/推导、两模式过程、OraclePrepare/提示 | 游标方向/结果形状、目录/CRUD及部署验收 |
| 协议/信任 | 标准压缩、LOCAL INFILE、opt-in多语句、TLS/mTLS及凭据轮换、TCP整轮退避、TNS/OCP组/地址黑名单及组上限 | OB20压缩/reroute、其他发现策略边界及企业认证；TCP健康缓存见第15.51节，TNS/OCP适配见第15.52/15.53节 |
| 类型/性能 | 嵌套complex/DDL失效、字符集/NUMBER/scale、idle cache、分段/慢命令诊断 | 扩展成员/其余形状、批量优化、其他资源等待诊断及负载 |
| 发布 | 五资产本地消费和部分直连报告 | 承诺版本/平台/独立运行时/OBProxy/TLS/认证/故障及包发布验收 |

当前开发点：Oracle FLOAT参数派生见第15.84节；客户端SQL/参数编码保留第15.80节，组TCP上限及地址级覆盖保留第15.64/15.65节。
具体已交付、验证结果和仍欠功能见[最新模块记录](obconnector-dotnet-progress.md#2026-10-27-oracle-float-routine-derivation)及[交付总览](obconnector-dotnet-delivery-status.md)。

## 2. 当前能力基线

| 模块 | 当前状态 | 主要边界 |
| --- | --- | --- |
| 连接和配置 | 部分实现 | 连接字符串、模式确认、多地址、超时、TLS、地址解析和池容量/等待/生命周期选项已存在；静态池清理和最小池预热已实现，真实兼容性未闭合 |
| ADO.NET 基础对象 | 部分实现 | `DbConnection`、`DbCommand`、`DbParameter`、`DbDataReader`、`DbProviderFactory` 已有；`SingleRow`/`SingleResult` 已有；`SchemaOnly` 仅 Prepare 目标并读取列元数据，禁用执行/fallback/参数上传；`KeyInfo` 已有单基表键/隐藏列本地实现及复杂查询 unknown 契约；DataAdapter/CommandBuilder 已有同步填充、映射、事件和有界单表 CRUD 生成的本地实现；实库/运行时验收仍缺 |
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
| HA 和恢复 | 部分实现 | Open 阶段 TCP 候选整轮重试及可取消固定退避；活动连接没有透明迁移、重连或 replay |
| MySQL 类型 | 部分实现 | 常用数值、时间、YEAR、BIT、JSON、零日期已有映射；字符集和扩展类型仍有限制 |
| Oracle 类型 | 部分实现 | DATE、TIMESTAMP、TZ/LTZ、INTERVAL、NCHAR、RAW、LOB、复杂类型已有部分实现 |
| MySQL 过程 | 部分实现 | 集合顺序 Input/Output/InputOutput、显式 Prepare、多结果和隐藏输出；关闭排空；PS_MULTI_RESULTS 必需，ReturnValue、FetchSize、SchemaOnly/KeyInfo 和 INOUT stream 拒绝；实际部署矩阵仍缺 |
| Oracle 输出 | 部分实现 | 过程、OUT、RETURNING、REF CURSOR 和数组绑定有路径；若干参数方向和结果形状不支持 |
| 复杂类型 | 部分实现 | 标量及嵌套 object/collection 有路径；NCHAR/NVARCHAR2成员已有有界实现；BLOB/CLOB命名及匿名集合结果已有本地物化；cursor、其他national存储及复杂整列RETURNING身份、循环和文本结果仍受限；可观察 DDL 发送时自动失效已有本地实现，外部/不透明 DDL 须显式清理，真实 DDL 验收仍缺 |
| Schema 元数据 | 部分实现 | Oracle 20/MySQL 18 个集合、静态 DataTypes 及自动参数推导已有本地实现；后者只读目录，不扩大过程执行范围。真实目录/权限矩阵仍缺，见 [Schema 契约](schema.md)及第 15.18 节 |
| 批处理和准备缓存 | 部分实现 | `DbBatch` 默认顺序；opt-in MySQL INSERT rewrite见第15.72节，multi-send/bulk仍缺；已有有限物理会话缓存，真实缓存/负载验收仍缺 |
| 诊断 | 部分实现 | `ILogger`、`ActivitySource`、`Meter`、含当前租约数的[池指标](pool-metrics.md)及建连/探活分段已有本地实现；慢命令阈值、协议帧诊断及[会话I/O耗时](session-io-diagnostics.md)已有本地实现；其他资源等待和完整部署性能验收仍缺 |
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
| P0-01 | P0 | 连接池 | 部分实现 | 容量/等待/预热/回收/指标、清池及内部有效配置归一化已有本地实现（第 5.1.4 节）；Closed回调内池化Open已先完成捕获清理再借用（第15.48节），保留其他日志重入边界；获通知后取消的交接修复见第15.58节；补其余并发交错、资源上限、长期运行及完整真实验收，reset 与 P0-02 联合关闭 |
| P0-02 | P0 | 会话隔离 | 部分实现；自动提交与 reader 租约子模块已实现 | Oracle 新连接及 reset 后显式初始化及异常销毁已有本地/直连专项（第 5.1.5 节）；prepared statement 已按物理会话 generation 失效，未消费 REF CURSOR、活动 LOB 句柄以及可识别的用户 session-state/temporary-object 语句已标记为归还时销毁，注释/多语句/不透明调用识别及仅目标发送标记已有本地回归（第 5.1.2 节），XA 专用会话销毁和 prepared 分支跨关闭恢复已有直连专项（第 5.1.6 节）；旧 CloseConnection reader 的逻辑租约隔离已有本地/消费者/直连证据（第 5.1.7 节）；持久内联 LOB/REF CURSOR 同端点专项已有本地及直连验收，外置持久 LOB 已有真实完整读取/复用及本地中途请求隔离证据（第 5.1.8/15.43/15.44 节）；任意隐式副作用、完整 reader/cursor/LOB、XA 故障与协调器、并发及部署泄漏矩阵仍缺 |
| P0-03 | P0 | Ambient transaction | 本地单资源实现；真实矩阵未闭合 | PSPE 拒绝第二数据库资源/提升，提交确认/拒绝/未知、初始化与晚回调身份、自动初始化取消及池/协调器恢复已有本地证据（第 15.27 节）；保留命令/Batch 绑定，补真实模式/版本/代理/事务管理器故障和恢复示例；不提供 durable prepare |
| P0-04 | P0 | Unknown outcome | 部分实现；响应丢失专项已有双模式直连证据 | 提交/回滚确认回包丢失、unknown分类/新租约及应用核对见第5.2.1/15.45节；完整发送边界、服务器/代理/协调器故障与恢复仍缺 |
| P0-05 | P0 | 活动连接恢复 | 部分实现 | 连接级ERR的Broken/旧事务失效、unknown及显式Close/Open已有本地实现（第15.49节）；补完整故障及真实重建矩阵，透明迁移和自动重试另行定义范围 |
| P0-06 | P0 | 池的路由身份隔离 | 本地实现；真实拓扑未验证 | resolver 引用身份隔离、每次 Open 路由快照及旧端点闲置会话清理已有回归；真实拓扑/故障矩阵待验收 |
| P1-01 | P1 | 多语句 | 部分实现 | 默认关闭；显式开启后支持文本多结果，并明确拒绝参数、Prepare、cursor 和 Oracle 输出组合 |
| P1-02 | P1 | 压缩和 OB20 | 部分实现 | 标准 MySQL 压缩已有本地实现；先确认 OB20 压缩的客户端链路/版本/协商契约，再补实现；reroute 及真实压缩矩阵仍缺，见第 16.3 节 |
| P1-03 | P1 | LOCAL INFILE | 部分实现 | 完成真实服务器/代理互操作、取消和大文件矩阵；保持显式授权和路径安全 |
| P1-04 | P1 | TLS/mTLS | 部分实现 | 自定义服务器证书回调、客户端证书池化身份隔离和数据源证书快照已有本地实现（第 15.4 节）；真实企业 CA/mTLS、轮换和跨平台撤销矩阵仍缺 |
| P1-05 | P1 | ADO.NET 命令行为 | 部分实现 | MySQL StoredProcedure 已有集合顺序参数/输出/多结果本地实现（第 15.30 节），保留方向、capability 和提前关闭限制；补真实 TLS/代理/版本矩阵；`SchemaOnly` Prepare-only、`SingleRow`/`SingleResult` 已有本地实现；`KeyInfo` 已有有界键元数据/隐藏列实现；Batch元数据/源条目创建见第15.60节，跨批次结果限制见第15.61节；REF CURSOR结果限制见第15.62节；其余 Command/Batch/游标行为矩阵及真实目录/准备/锁/结果形状验收仍缺 |
| P1-06 | P1 | Oracle 输出和游标 | 部分实现；独立 Prepare、输出 FetchSize 提示及文本/二进制 cursor 列已有本地实现 | 保留准备与所有权转移、正提示不对内部输出发FETCH（第15.46节）；二进制cursor列/嵌套所有权及文本四字节标识列已有本地实现（第15.56/15.87节）；补完整参数方向、其余结果形状和部署矩阵；输出offset/数组正FetchSize仍拒绝 |
| P1-07 | P1 | 复杂类型 | 部分实现 | 已有嵌套 object/collection、BLOB/CLOB、cursor结果成员、显式清理及可观察 DDL 自动失效本地实现；保留第15.47节national实现及限定直连证据，继续补复杂 cursor 参数、其他national边界及复杂整列RETURNING身份和真实DDL/资源验收，外部或不透明 DDL 需显式清理 |
| P1-08 | P1 | 类型和字符集 | 部分实现；DecimalScale、普通结果字符集及客户端编码子模块已有实现，输入编码见第15.80节 | 有符号 scale、严格结果解码/宽度及 Oracle 未知精度 NUMBER 保真已有回归；其余national/JSON/CLOB边界、其他字符集、扩展元数据/类型码及完整实库矩阵仍缺 |
| P1-09 | P1 | HA/发现 | 部分实现 | TCP重试预算/退避、端点TCP健康缓存、TNS/OCP适配、分组/策略/权重及全局黑名单/尝试上限已有本地实现（第15.50—15.55节），分组黑名单、组尝试上限及地址覆盖见第15.63—15.65节；其他发现策略边界、proxy拓扑及真实健康/故障验收仍缺 |
| P1-10 | P1 | Schema 元数据 | 部分实现；参数推导已有本地实现 | 常用目录、DataTypes 及自动参数推导有本地实现；真实目录/权限/版本/代理验收仍缺 |
| P1-11 | P1 | 凭据提供与轮换 | 本地实现；真实矩阵未验证 | 每次 Open 获取、全口令替换、版本排序、过期及旧会话退休已本地验证；真实凭据服务/plugin/KDC 验收仍开放 |
| P2-01 | P2 | DataAdapter | 部分实现；本地模块 | 同步填充、映射/事件、显式或自动参数化 CRUD；保留可见行键、生成值回填、复杂形状限制，补真实 CRUD/并发/运行时验收 |
| P2-02 | P2 | Statement cache | 部分实现 | 有限 idle LRU、上下文/reset 失效和关闭已有本地实现；真实 DDL/reprepare、资源/负载/代理与运行时验收仍缺 |
| P2-03 | P2 | 批处理优化 | 部分实现 | opt-in INSERT rewrite及合并组失败归属已有本地路径；multi-send/bulk及真实负载验收仍缺 |
| P2-04 | P2 | 诊断 | 部分实现 | 建连/探活分段、慢命令阈值、协议帧、会话I/O耗时及当前池租约指标已有本地实现；补其他资源等待及性能验收 |
| P2-05 | P2 | TFM/生态 | 部分验证；诊断卸载缺陷已修复 | 每个TFM的正式支持矩阵和package consumer验收；第15.58节保留原默认CLR崩溃与条件通过；第15.59节保留诊断卸载修复；第15.74节新增Windows六配置nupkg协议子集，完整运行时/平台/实库矩阵仍缺 |
| P2-06 | P2（扩展矩阵）/ 发布阻塞（承诺范围） | 真实兼容性 | 部分验证 | 承诺范围必须有真实版本、OBProxy、TLS、认证、平台和故障报告，扩展范围再分期 |

### 4.1 除连接池外，具体还缺什么

以下按当前源码区分新增开发和验证工作，不另增 ID，也不把“已有一部分”写成“完全没有”。具体场景、源码入口和关闭标准仍以第 14 节为准。

| 类别 | 尚需完成的内容 | 归属 ID |
| --- | --- | --- |
| 已有本地实现、欠真实验收 | opt-in 跨命令 idle statement cache，保留命令独占和失效规则 | P2-02 |
| DataAdapter 本地实现，真实验收开放 | 填充/映射/事件、原始值/NULL 与参数化 CRUD 已有本地回归；自动更新需完整可见行键，实际目录/SQL/锁及运行时仍需验收 | P2-01 |
| KeyInfo 本地实现，真实验收开放 | 单基表查询的主键/唯一键及隐藏列；复杂/不确定形状保留原 SQL 和 unknown 元数据；目录/权限/DDL/版本/代理仍需验收 | P1-05 |
| 新增本地实现，真实验收开放 | 自动参数推导已有 API、完整签名映射、会话/事务预留和失败原子性；实际目录/权限/版本/代理互操作仍需报告，见第 15.18 节 | P1-10 |
| 新增实现仍待真实验收 | 凭据提供/轮换 API、每次 Open 获取、版本退休、过期、取消和脱敏已有本地回归；真实短期凭据服务/plugin/KDC 未验收 | P1-11 |
| MySQL 过程本地实现，部署验收开放 | 已有按集合顺序绑定、私有变量输出及关闭排空实现和回归；默认策略直连用例通过，仍须真实 TLS/代理/版本/资源和独立运行时验收，见第 15.30 节 | P1-05 |
| 协议扩展缺失 | OB20 压缩与 proxy reroute；先确认目标链路、版本和协商契约，不能根据服务器内部路径猜测客户端支持 | P1-02 |
| 发现与恢复不足 | 保留TCP重试预算/退避及端点TCP健康缓存，TNS文件及OCP HTTP适配已本地实现（第15.52/15.53节），补其他发现策略边界与真实节点/代理健康验收；活动连接故障后的状态/重建契约；无透明迁移和自动重连 | P1-09、P0-05 |
| Oracle 形状不足 | REF CURSOR Input/InputOutput、文本及二进制 cursor-valued column 的真实验收、输出offset/数组正FetchSize组合；独立Prepare及普通输出正提示已有实现；复杂 cursor 结果已具本地二进制路径，仍缺复杂 cursor 参数、其他national存储/匿名结果及复杂整列RETURNING身份；可观察 DDL 自动失效已有本地实现，外部/不透明 DDL 须显式清理，真实 DDL 验收仍缺 | P1-06、P1-07 |
| 类型与命令契约不足 | 扩展类型码、metadata、字符集和精度矩阵；SchemaOnly 元数据专用路径已有本地回归，真实边界仍需验收；Command/Batch/游标的行为范围不同 | P1-08、P1-05 |
| 事务与结果不确定性 | 保留单资源/终态/身份/取消及失败清理本地回归，补真实超时/in-doubt/关闭/响应丢失矩阵、XA 外部协调器恢复；写入不自动 replay | P0-03、P0-04、P2-06 |
| 性能与运维不足 | opt-in MySQL INSERT rewrite已有本地路径，multi-send/bulk及真实负载仍缺；建连/探活分段、慢命令阈值报告和协议帧诊断已有；补资源上限报告。Oracle array binding 与池指标已有 | P2-03、P2-04 |
| 已有实现仍欠真实验收 | TLS/mTLS/撤销/认证插件、文本多语句、LOCAL INFILE、标准 MySQL 压缩、Schema 新目录及 DataTypes 声明对应的服务器类型/DDL，以及版本/OBProxy/平台/包消费矩阵 | P1-01、P1-02、P1-03、P1-04、P1-10、P2-05、P2-06 |

连接池自身的隔离、并发、reset 和路由仍由 P0-01/02/06 跟踪。分布式事务提升当前明确不支持；不能把本地 `TransactionScope` 当成跨连接或跨资源的原子提交。DataAdapter/CommandBuilder 已有本地实现；ORM、透明恢复及性能扩展是否进入首个正式版本须明确决定：本报告保留边界和缺项，不将未确认的取舍写成已批准的排除。

## 5. 分模块要求和验收标准

### 5.1 连接池和会话隔离

当前 `Pooling=true` 已由 [PhysicalSessionPool.cs](../src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs) 管理物理会话；逻辑连接关闭时通过 `COM_RESET_CONNECTION` 清理服务端状态，MySQL 模式再用 `COM_INIT_DB` 恢复非空初始 database；Oracle 模式不发送 `COM_INIT_DB`，而在 reset 成功后显式设置并验证自动提交（第 5.1.5 节）。MySQL 模式的 reset 和借用探活都会要求服务端明确返回 `Autocommit` 状态位；缺失该状态时物理会话立即销毁。池已支持最大容量、等待超时、等待取消、最小池预热及失败回滚、借用前 `COM_PING` 存活探测、连接空闲/总生命周期回收、最小空闲保留规则、空池 registry 清理和池生命周期指标；每次 Open 使用独立的取消和 generation，过期的 Open 完成会被拒绝。可识别的用户 `SET`、`ALTER SESSION` 和临时对象语句会让物理会话归还时销毁，避免把无法由当前 reset 证据覆盖的状态交给下一借用者；旧池已 Dispose 时，同一逻辑连接会重新取得 registry 中的新池。Closed 回调更换配置并重开的池归属已由 `06d58e1` 修复并有回归；真正多线程 Close/Open 和等待取消交错仍需压力验证。完整资源 reset 和真实 OceanBase/OBProxy reset 兼容性仍需验证。

当前已接入的指标名称为 `oceanbase.client.pool.sessions`、`leased`、`borrowed`、`waiters`、`discarded`、`reset.failures` 和 `wait.duration`，分别记录注册物理会话数、当前借出物理租约增减、累计借用次数、等待者数、销毁次数、reset 失败次数和池等待耗时。`leased`在归还入口即减少，不计reset；迟接入监听器只得到后续增减量，不能重建绝对值。完整边界见[池指标契约](pool-metrics.md)。它们证明本地生命周期可观测，不证明真实服务器兼容性。

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

**P0-06 路由身份边界：** 早期走查发现普通全局池只按连接字符串索引，B 的不同 resolver 能取得 A 的健康会话；已用同步/异步回归复现并修复。当前 registry 按有效配置身份（第 5.1.4 节）和 resolver 的引用身份隔离，不受 resolver 覆盖的 `Equals`/`GetHashCode` 影响；更换关闭连接的 resolver 会选择对应池。每次池化逻辑 Open 只解析一次，快照用于闲置候选检查、最小池预热和新会话建连。已不在快照中的闲置 host/port 会话直接销毁，不能用最小池保留规则绕过；已借出的会话继续当前工作，之后按下一借用者的快照检查。相同主机名背后的 DNS IP 变化不在 host/port 成员检查范围。

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

**2026-10-25 实库发现与修复：** P0-02/W1 验收曾在 Oracle 4.4.2.2 直连/TLS disabled/Windows/.NET10 复现新连接及池 reset 后隐式 INSERT 对另一连接不可见。现已在 identity 后和成功 reset 后设置并确认 autocommit，失败销毁会话；原四个同步/异步实库失败均已同名通过。原始失败与 NUMBER 前置修复见第 15.37 节，初始化契约见第 5.1.5 节，最终证据及尚缺矩阵见第 15.39 节。此项沿用 P0-02，不新增或关闭完整缺口 ID。

### 5.1.1 当前会话状态识别的具体缺口

本节保留 `4dd20a7` 开发前的走查场景；后续本地实现契约见第 5.1.2 节，执行结果见第 15.24 节。下表的“当前源码”指修复前基线，不覆盖后续实现；真实 reset/跨借用者验收仍开放。

本节针对 `4dd20a7` 的源码复核，归入 **P0-02 / W1**，不另增缺口 ID。`PhysicalSession.MarkSessionStateChanged` 只做 `TrimStart()` 和固定前缀比较；当前识别的是 `SET`、`ALTER SESSION`、`CREATE TEMPORARY`、`CREATE GLOBAL TEMPORARY` 和 `DECLARE GLOBAL TEMPORARY`。`RequiresDiscardOnReturn` 将该标记与活动 LOB/cursor 资源标记一起用于池归还判定。已有 `PoolingTests.UserSessionStateChangeDoesNotReturnPhysicalSessionToPool` 仅覆盖无前置注释的 SET 和 CREATE TEMPORARY 两例，不能证明以下组合已闭合。

| 输入或操作 | 当前源码行为 / 影响 | 必须补的验收 |
| --- | --- | --- |
| `/* trace */ SET SESSION time_zone = '+00:00'`；`-- trace` 换行后 SET；MySQL `# trace` 换行后 SET | 前置注释不会被 TrimStart 去掉，固定前缀不命中，绕过保守销毁标记 | 双模式合法注释、空白及组合用例；A 执行后 Close，B 不复用需销毁的物理会话 |
| `ALTER /* trace */ SESSION ...`、`ALTER` 换行后 `SESSION ...`；`CREATE /* trace */ TEMPORARY TABLE ...` | 多词关键字依赖固定单个空格，注释、换行或制表符可能使识别漏掉 | 按模式处理词间 trivia；验证合法状态修改与仅在字面量/注释出现的关键字 |
| 显式启用文本多语句后的 `SELECT 1; SET SESSION time_zone = '+00:00'` | 只检查整个文本起始位置，不能识别后续语句的状态变更 | A 消费全部结果后归还，B 的基线与物理会话身份；同时覆盖多语句关闭时的发送前拒绝 |
| MySQL 可执行注释，如 `/*! SET SESSION time_zone = '+00:00' */` | 前缀不命中；普通注释跳过器也不能直接当作可执行注释识别器 | 对目标版本实际执行的注释保持保守处理；验证普通注释、版本条件、引号、SQL mode 的边界 |
| `CALL p()`、Oracle `BEGIN p(); END;` / `DECLARE ...`、StoredProcedure 命令名，过程内部改变状态 | 调用文本没有受识别的前缀，过程或动态 SQL 的内部副作用不可由该比较证明安全 | 先规定不透明调用的销毁/恢复策略；本地验证标记、资源归属与复用，实库验证合法过程、动态 SQL 和实际 reset |
| `Prepare()` 状态语句，或以 SET 开头但随后因多语句/参数校验被拒绝的命令 | `OceanBaseCommand.Start` 在长度、组合、绑定及编码校验前调用标记；未执行命令也可能导致额外销毁 | 无目标执行的 Prepare、发送前拒绝、预取消不得因状态分类误损复用；执行路径仍须在可能发送前落实销毁策略 |

上述是**源码识别遗漏及误标记风险**，没有新增实库污染复现。对于漏标记路径，归还时仍有 `COM_RESET_CONNECTION`；它是否足以恢复该状态，必须由具体 OceanBase 模式、版本及 OBProxy 报告证明，不能仅凭漏标记就认定 B 一定继承了 A 的状态。误标记主要增加建连和认证开销；它与实际状态泄漏的严重程度不同。

最小修复应落实到所有执行入口共享的分类及发送生命周期，不分别为 ExecuteNonQuery/Reader/Scalar、同步/异步、Batch、PrepareExecute 增加互相不一致的补丁。不能把词法识别包装成任意 SQL 副作用的完整证明；用户变量赋值、锁、动态执行或无法识别的调用仍需要公开的保守规则及真实 reset 证据。

关闭本子缺口至少需要：先用上述输入留下失败回归，再验证修复；以 Max Pool Size=1 的本地协议 fixture 证明 A/B 身份、销毁或安全复用、reset 失败丢弃和容量归属；以真实双模式/直连与 OBProxy 验证变量、临时对象、过程副作用及基线。保留普通只读查询、引号/注释内关键字、无目标执行 Prepare/SchemaOnly 的复用回归，避免用“所有命令都销毁”掩盖池化失效。本次仅记录问题和验收要求，未实施上述修复或运行这些新用例。

### 5.1.2 会话状态识别与目标发送边界

P0-02 本地实现使用共享 `SessionStateSql`，按兼容模式识别普通注释、词间空白和多个语句的开头。SET/RESET/USE、ALTER SESSION、临时表、显式事务/XA、CALL/DO/EXECUTE/PREPARE 等不透明执行，Oracle 匿名/带标签块和 StoredProcedure 名称均采用归还时销毁。MySQL 可执行注释、用户变量和命名锁调用按保守规则处理；普通注释、字面量、列/别名以及表/CTE 列清单不会仅因包含关键字或锁函数名就被认作执行调用。

分类结果附在单次 CommandExchange 上，仅在目标 `executesSql` 发送前、取消检查之后写入物理会话的持久标记。Prepare、SchemaOnly/KeyInfo 目录阶段、内部元数据和 long-data 上传不因此提前标记；发送前的长度、行为组合、参数等拒绝也不误损复用。执行中的错误可能使服务端副作用不确定，因此目标发送后即使收到错误，保守销毁仍成立。协议/传输失败本身仍可独立销毁会话。

池化状态分类器未跟踪SQL_MODE；第15.73节每次字符串降级的局部snapshot不改变此分类策略。MySQL引号中出现反斜杠会保守销毁，可能增加合法转义字符串的建连成本；quoted命名锁调用也按模式歧义保守识别。这是公开的复用成本，不是声称驱动已经解析任意SQL。任意存储函数/触发器等隐式副作用、完整reader/LOB/cursor/XA、reset失败/并发及真实双模式/代理隔离矩阵仍须继续验收；未证明其reset的应用应关闭池化。原始XA SQL的销毁标记不能替代branch/coordinator恢复。

详细的本地 RED/修复、独立走查与最终验证见第 15.24 节；公开契约见 [API 文档](obconnector-dotnet-architecture-design.md#pooled-session-state-after-user-sql)。本子模块不关闭整个 P0-02 或 W1。

### 5.1.3 普通池配置身份尚未归一化

**归属：P0-01 / W1；修复前发现。** 本节保留 `c2fe307` 源码及原 Release/net10.0 DLL 的未修复观察；后续本地实现见第 5.1.4/15.31 节。该基线将 builder 序列化后的连接字符串传给 registry，`PoolKey` 对文本做 ordinal 比较，没有按有效配置生成统一身份。关键词别名已在 builder 解析时统一，不能把“存在别名”本身列为公共连接分池的原因。

最小探针使用 `Server=pool-audit.invalid;User ID=audit;Pooling=true`，无真实口令、不解析域名、不建立网络连接；只通过当前 DLL 的 builder 和内部 registry 检查池对象身份，最后移除创建的空池：

| 等效配置变体 | 当前观察 | 实际影响 |
| --- | --- | --- |
| 同样的键值改变顺序，均设置 `Max Pool Size=1` | 序列化文本不同，池对象不同 | 容量按两个池分别计算，不能依赖它们共享一个上限 |
| 省略 Port 与显式 `Port=2881` | 文本不同，池对象不同 | 配置模板显式写默认值时可能新建另一池 |
| `Pooling=true` 与 `Pooling=TRUE` | 文本不同，池对象不同 | 有效布尔值相同仍分池 |
| 同位置的 `Server` 改成 `Host`，其他内容相同 | 文本相同，池对象相同 | 此别名已统一，作为反例保留 |

`ClearPool` 同样使用保存的配置文本匹配。因此用等效但不同文本的普通连接清池，不会匹配另一写法的池；这是当前文本身份契约的结果，不代表 ClearPool 对相同身份失效。新目标是按有效配置统一普通池身份，保持公开 ConnectionString 的序列化和口令隐藏行为。这里的容量和清池影响由源码调用链推导；本次探针只验证对象身份，没有运行实库连接数、满池等待或活动清池测试。

**源码入口：** [ConnectionString setter / PrepareOpen](../src/OceanBaseConnector/OceanBaseConnection.cs)、[builder 的类型 getter 与 ValidateValue](../src/OceanBaseConnector/OceanBaseConnectionStringBuilder.cs)、[registry Get / GetForRent / ReleaseIfEmpty / Clear / PoolKey](../src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs)、[公开 ClearPool](../src/OceanBaseConnector/OceanBaseConnection.Pooling.cs)。修复应统一领取、归还失败清理、显式清池和 ClearAll 身份，不能只修改 Open。

**实现边界与验收：**

1. 按现有类型 getter 和有效默认值统一顺序、布尔/整数/枚举及省略默认值；`Pool Wait Timeout` 缺省跟随 `Connection Timeout`，不能固定写成 15。规范化结果须可再次规范化。
2. 所有配置项都参与身份。保留 Server/候选顺序、用户、database、口令及 PasswordN、TLS/SPN 等文本的 ordinal 差异；可选 ExpectedMode 未指定与显式指定保持区分。使用标准 serializer 转义，防止分号或引号造成身份碰撞。
3. 保留 resolver、credential provider、验证回调和证书的引用身份及证书指纹；私有 DataSource 池仍私有。内部身份含秘密，不得输出到日志、异常或公开连接字符串。
4. 本地回归须覆盖等效配置共池、不同身份不合并、同步/异步共享容量和等待取消、等效配置清池、ClearAll、失败 Open 清理及 Closed 回调改配置后的原池归属；五资产消费及真实双模式/代理行为另留报告。

临时使用约束是集中复用同一份连接配置与身份对象，并使用对应连接清池；这不能替代归一化实现、完整 reset 或真实隔离验收。已有忽略目录中的实施计划不计为交付代码；本节不关闭 P0-01。

### 5.1.4 普通池的有效配置身份

普通 registry 现在按全部配置项的有效类型值和默认值统一身份，忽略关键词顺序、别名、布尔/整数/枚举表示及省略/显式默认值的差异。`Pool Wait Timeout` 缺省使用当前 `Connection Timeout`。每个选项仍参与身份；Server/候选顺序、用户、database、口令/RSA/PAM、TLS/SPN 等文本保持 ordinal 差异，不推定 DNS/租户等价；ExpectedMode 未指定仍区别于显式模式。私有 DataSource、resolver/credential-provider/验证回调/证书引用与证书指纹边界不变。

身份从原始已解析 builder 捕获，而非从公开 ConnectionString 反推：后者可能把 `Password2=""` 序列化为 `Password2=`，再解析便丢失“存在的空因子”。Open、失败清理、Close/discard 快照和 ClearPool 均保留原始存在性；规范串按数字排序 PasswordN 并显式引用空因子，可经 ClearAll/Release 再解析。规范串含秘密，仅用于内部身份，公开序列化和 PersistSecurityInfo 不变。registry 解析在全局锁外完成，领取池与 BeginRent 仍在同一锁内。

等效配置的普通连接共享容量、探活/reset 和清池身份；未打开的等效连接也可清池，活动借用者继续工作、归还退休。Closed 回调之前已捕获旧池和身份，回调改配置不能重定向其清理。保留原Closed-before-cleanup事件顺序；事件内池化Open/OpenAsync在正常借用前先完成捕获清理链，容量1可复用自身归还的会话，不再等待本次Close。嵌套事件、同/异逻辑连接、异常/取消及已完成作用域资源释放见[当前契约](obconnector-dotnet-architecture-design.md#pooled-close-callbacks)和第15.48节。Close清理的事务ILogger已在会话/池释放后发布（第15.67节）；显式事务及其他日志/监听回调仍不承诺任意重入安全。回调抛错也必须完成原会话归还；不通过另建等效池绕过上限，也不宣称任意重入已解决。

源码集中在 builder、registry 以及连接的 Open/DetachForClose/ClearPool 身份快照；public/five-asset 回归保留复用、容量/取消、闲置/借出/reset 中清池及空因子隔离。独立走查发现并用实际 RED 修复公开序列化、Close 原池清理问题；完整结果见第 15.31 节。完整 reset、长期资源/并发和全部真实代理/平台验收仍开放，不关闭 P0-01/W1。

<a id="515-oracle-自动提交基线已确认缺陷与待实现契约"></a>
### 5.1.5 Oracle 自动提交基线

**归属 P0-02 / W1，与 W2 事务验收联动；从 `4810907` 开发。** 已增加 Oracle 建连和 reset 后显式初始化，本地及实库专项、全量门禁与独立走查已完成，详细证据见第 15.39 节。原始缺陷不仅影响连接池：未显式开启事务的 INSERT 在全新非池连接和 reset 后均对观察连接不可见。原始实库失败及 18 项 RED 检查点完整保留于第 15.37/15.38 节；旧显式事务回滚通过不能替代隐式提交证据。

**实现与契约。** [ConfirmIdentity / ConfirmIdentityAsync](../src/OceanBaseConnector/Internal/Session/HandshakeFlow.Identity.cs) 确认身份后无条件为 Oracle 发送 `set autocommit = 1`，不能仅依赖认证状态位；只有设置确认后才发布 Open。[ResetCore](../src/OceanBaseConnector/Internal/Session/PhysicalSession.cs) 先完成 COM_RESET_CONNECTION，再设置自动提交，最后递增 generation，避免在 reset 前提交上一借用者的遗留写入。共享校验要求普通 OK、affected rows/insert ID 为 0、Autocommit 已设置且无活动/只读事务、多结果、cursor/last-row/output 标志。ERR、畸形包、取消、超时及 Close 均销毁会话；失败池会话不复用且容量可恢复。MySQL disabled-autocommit 登录继续拒绝，不发送 Oracle 设置命令。详见[公共契约](obconnector-dotnet-architecture-design.md#oracle-autocommit-baseline)。

建连把 SET 响应的 system variables 合并覆盖认证值；reset 清除原状态，仅保留 SET 返回的 timezone/national 元数据。同步/异步、压缩与 OB20 帧序号及既有取消/超时规则保留。新增 SET 不标记为用户会话状态污染；每次 Oracle 物理建连与成功池 reset 增加一个 round trip，没有绕过该基线的新选项。

**Java 对照。** 固定版本 [sendSessionInfos](https://github.com/oceanbase/obconnector-j/blob/6adc311ba5db8b2f19f98f3e6e4734667fdfdda1/src/main/java/com/oceanbase/jdbc/internal/protocol/AbstractConnectProtocol.java#L1117) 同样说明建连必须先设置、不能只信任此前 Autocommit 位；本报告不声称 Java reset 也已实现该初始化，未运行 Java 测试。Oracle reset 修复依据本驱动实库失败与后续回归。

**当前证据。** [初始化响应矩阵](../tests/OceanBaseConnector.UnitTests/Session/HandshakeFlowTests.OracleAutocommit.cs) 覆盖同步/异步 Open/reset × 14 种响应；[生命周期与元数据](../tests/OceanBaseConnector.UnitTests/Session/HandshakeFlowTests.OracleAutocommitLifecycle.cs) 覆盖 12 项 pending SET 取消/Close/超时、8 项认证覆盖/保留及 reset 清除/更新，共 76 项通过。只读事务状态 0x2000 独立出现的 4 项先 RED，随后加入明确拒绝。失败时不发布 Open、不提前增加 generation，socket 关闭且单容量池能领取新会话；五资产 public API 另验证失败销毁后实际新 TCP/认证/SQL 可用。

[真实池隔离套件](../tests/OceanBaseConnector.IntegrationTests/Connections/PoolIsolationTests.cs) 验证 fresh 隐式写在 Close 前后可见、显式事务 Close 回滚及下一借用者隐式提交、prepared/generation 三轮真实复用、用户 timezone 变更后丢弃并恢复基线、未读 reader 和 Oracle 活动 temporary LOB 丢弃。修复前 18 通过/4 失败/1 跳过，修复后同名用例 22 通过/0 失败/1 跳过；ChangeDatabase 因缺 alternateDatabase 未验收。实库仅 Windows/.NET10、OceanBase4.4.2.2 双模式直连、TLS disabled，不关闭完整资源/XA/隐式副作用/并发隔离或 OBProxy/跨平台验收，P0-02/W1 继续开放。

### 5.1.6 XA 专用会话的池隔离

归属 P0-02/W1，与 W2 联合验收。`GetXaResource()` 即将物理会话专用于 XA，直到 Close；即使尚未 Start，或分支已 Commit/Rollback，也不能退回普通池。Close 销毁该会话，不发送 reset 或隐式 XA End/Commit/Rollback。旧资源绑定原逻辑连接及物理会话，逻辑连接重开后其同步/异步 API 均须拒绝。

[本地协议回归](../tests/OceanBaseConnector.UnitTests/PooledXaIsolationTests.cs) 覆盖双模式、同步/异步和 idle/active/ended/suspended/prepared/committed/rolledback 七阶段，共 28 项。严格验证旧 socket EOF、没有额外控制命令、MaxPoolSize=1 下第二次真实握手和 SQL 成功；同逻辑连接重开后十二个旧资源入口拒绝。ClearPool 在替代 SQL 成功之后调用，不能掩盖容量泄漏。

[真实数据库验收](../tests/OceanBaseConnector.IntegrationTests/Connections/PoolXaIsolationTests.cs) 新增 20 项：未启动、已提交、已回滚专用会话仍被丢弃；替代借用者隐式写在 Close 前可见，本地事务可回滚；prepared 分支在旧连接 Close 后可由新会话 Recover，随后明确 Commit/Rollback，再次 Recover 不含该 XID，观察者行数符合决策。测试失败时在 owner 释放前尝试 End、Close，再通过显式非池连接回滚，聚合主失败和清理失败；只有最后回滚的 NOTA 可容忍。

本次已有行为全部通过，没有生产代码/API/依赖修改或新增 bug 修复声明。仅验证 OceanBase 4.4.2.2 双模式直连、Windows/.NET10、TLS disabled；active/suspended 的 socket 销毁证据来自本地协议，未扩成对应的实库分支存续承诺。服务端断开时的分支处理仍须遵循 XA 恢复契约，Close 不代表业务回滚已获确认。响应丢失、外部 coordinator crash、代理、完整资源及长期并发矩阵继续开放，详见第 15.40 节。

### 5.1.7 CloseConnection reader 的逻辑租约隔离

归属 P0-02/W1，并与 P1-05 的 reader/Batch 行为共同验收。**确认缺陷：** reader 带 `CommandBehavior.CloseConnection`，读完并确认没有下一结果后保留 reader，显式 Close/Open 同一个逻辑连接；若池返回原 `PhysicalSession`，旧 reader 的 Dispose 曾仅核对物理对象引用，误关闭新租约。原本地四项和真实 MySQL/Oracle 同步/异步四项均已复现，不能继续以“已有 CloseConnection”推断生命周期完整。

现在普通 reader 从命令启动时保存逻辑 `_openGeneration`，Batch 从预留会话时保存；捕获和 Close 的会话引用/代数比较都在连接 `_gate` 内。旧 reader 的延迟关闭只作用于原 Open 租约；即使物理对象相同，也不能关闭新租约。代数不是物理会话 reset 的 generation，不能在 reader 构造或 Dispose 时读取当前值。公开 Close/CloseAsync 仍直接关闭当前连接；同一租约中的 CloseConnection 仍正常关闭，不承诺同一连接支持任意并发 SQL。

源码入口：[CommandExchange](../src/OceanBaseConnector/Internal/Session/CommandExchange.cs)、[命令捕获与 DetachForClose](../src/OceanBaseConnector/OceanBaseConnection.Transactions.cs)、[Batch 预留](../src/OceanBaseConnector/OceanBaseConnection.Batch.cs)、[Batch operation](../src/OceanBaseConnector/OceanBaseBatch.cs)、[连接关闭](../src/OceanBaseConnector/OceanBaseConnection.cs)、[普通 reader](../src/OceanBaseConnector/OceanBaseDataReader.cs)与[Batch reader](../src/OceanBaseConnector/OceanBaseBatchReader.cs)。[本地八项回归](../tests/OceanBaseConnector.UnitTests/PooledReaderCloseTests.cs)还覆盖 Batch 释放预留后、延迟 Close 回调前的重开窗口；[消费者二十项](../tests/OceanBaseConnector.ConsumerTests/PooledReaderCloseConsumerTests.cs)在五资产上通过公共 API 验证同 socket 的 reset/ping/新 SQL。[真实四项](../tests/OceanBaseConnector.IntegrationTests/Connections/PoolReaderLeaseTests.cs)断言复用同一物理对象及递增 generation，并在旧 reader 关闭后执行真实 SQL。完整门禁、原失败及限制见第 15.41 节；完整资源、压力、代理和部署矩阵仍开放。

### 5.1.8 reader、LOB 与 REF CURSOR 的剩余验收

**归属 P0-02 / W1，关联 P1-05/P1-06；当前基线 `a426623`。** reader 的原逻辑租约关闭保护已交付，不表示所有资源都已通过隔离验收。源码的 `RequiresDiscardOnReturn` 同时考虑活动 LOB、cursor 归还保护和用户会话状态；验收必须区分“已有保护通过”与“发现缺陷需要修改”。不为已经正确的行为重复新增实现。

| 资源场景 | 当前证据 | 仍须完成的验收 |
| --- | --- | --- |
| 完成后保留的 CloseConnection reader | 已提交模块有本地、五资产消费者和双模式直连证据，见第 5.1.7/15.41 节 | 扩展未读/部分读取、多结果、取消与 Close/Open/Dispose 并发，不将原租约专项外推为全部资源通过 |
| 持久 BLOB/CLOB 的句柄、已开流、部分读、读完但未释放、全部释放 | 同步/异步 20 项本地及 20 项 Oracle 直连用例通过，实库 locator 全部内联，见第 15.43 节 | 同一端点、MaxPoolSize=1，未释放句柄使旧会话销毁；全部释放的只读 LOB 允许健康 reset/复用；旧句柄的读、写、截断、参数绑定和 Dispose 不影响新借用者 |
| 外置持久 BLOB/CLOB 完整读取与 in-flight 中断 | Oracle 直连 4 项断言实际外置 locator、完整内容及释放后同会话复用；本地 18 项受控请求中途取消/Close/句柄释放/流释放/超时，见第 15.44 节 | 保留同端点单容量池的新认证/新 SQL 及旧资源隔离；真实中途 fault/cancel、其他版本/代理、并发与长期资源矩阵仍缺 |
| Oracle 输出 REF CURSOR 未打开、部分读、读完、释放 | fixture 修正后本地 8 项及 Oracle 直连 8 项通过；原 partial/complete 四项 fixture 失败保留 | 保留旧 cursor/reader 不影响新租约的证据；完整 fault/cancel/并发/代理矩阵仍缺。各阶段还受不透明 SQL 标记销毁保护，不能单独证明 cursor 计数或释放后复用 |
| reset、清池和资源释放交错 | 已有前序回归，但完整矩阵仍开放 | 同步/异步、普通连接/私有源、成功/失败/超时/取消及真实服务器/代理；清池前先证明下一借用者可执行 SQL，不用清池掩盖容量或污染问题 |

LOB 验收使用普通 SELECT 读取持久 locator，避免在被测池会话上创建临时 LOB 或执行不透明过程掩盖活动句柄保护。第 15.43 节的二十个真实 locator 均为内联；第 15.44 节另用独立非池连接准备大值并释放临时种子，四个实际外置 locator 完整读取后复用同物理会话。本地中途请求测试不等于真实服务器中途取消验收。完整关闭仍要求资源上限、真实故障/取消、并发、版本/代理及平台矩阵；第 15.42 节及后续原始失败与修正均保留。

### 5.2 事务、取消和 unknown outcome

当前显式本地事务和 XA 有实现；ambient 模块在打开时通过 PSPE 占用协调器单资源槽，再初始化同一本地事务。第二数据库资源和分布式提升明确拒绝，不能静默组成多个独立本地提交。取消或连接断开时也不能证明服务端是否已经执行完成，提交/回滚响应丢失仍按 unknown outcome 处理。

目标要求：

- 明确 local transaction、ambient transaction 和 XA 的互斥关系；当前本地 ambient 与显式本地/XA 冲突会在发送前拒绝；
- `TransactionScope` 提交、回滚、超时和连接关闭必须有状态机；
- 发送 SQL 后发生取消时，异常必须带有可判定的 unknown-outcome 信息；
- commit/rollback 的响应边界必须保留，不能因为 socket 关闭而错误报告相反结果；
- 重试策略必须按只读、幂等写、非幂等写和事务提交分别定义；
- pool reset 不能自动替应用程序重试未知写操作。

验收必须覆盖：发送前取消、发送后取消、响应中断、提交响应丢失、回滚响应丢失、事务 dispose、连接 dispose 和池归还。ambient 的确认提交/拒绝/未知、忙碌 reader、Open 初始化交错、晚回调及池 A/B 路径已有本地证据；自动 OpenAsync 初始化现传播有效 opening token，取消中断后丢弃会话、清理登记及池容量；成功 Open 后 token 不影响后续提交/回滚，证据见第 15.27 节。契约及同步初始化/双重超时预算见 [ambient API](obconnector-dotnet-architecture-design.md#ambient-transactions)。真实双模式、代理和事务管理器故障矩阵仍需验收。

### 5.2.1 提交/回滚响应丢失与应用核对

现有 `CommandExchange` 在事务SQL已发送但最终响应不完整时报告 `IsExecutionOutcomeUnknown=true`，销毁会话并使旧事务失效；不自动重放。新租约只恢复普通连接能力，不接管旧事务；旧事务的迟到Dispose不能影响新租约。服务器已确认COMMIT后恢复autocommit失败时保留已确认提交语义，不能反向报告已回滚。

第15.45节的双模式实库16项通过本地明文中继，先读取真实服务器回包，再丢弃全部或仅交付原长度头与一个payload字节。独立非池观察者核对实际提交/回滚，新租约在清池前能隐式写和显式回滚；它不证明服务器崩溃、外部协调器或真实代理恢复。仅选择原本TLSdisabled、无压缩/OB20的单端点profile，不降级其他配置。

[应用核对指南](unknown-outcome-recovery.md)给出同事务operation_id标记及新连接查询示例。找到权威的已提交标记可识别业务完成；单次查不到不证明回滚，查询失败也不能改变原结果。应用必须保留待核对状态，在权威最终结果和幂等/并发约束明确后再决定补偿或重试。该示例不是驱动自动恢复或持久协调器功能。

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

当前 `DbConnection`、`DbCommand`、`DbParameter`、`DbDataReader`、`DbProviderFactory` 已有基础实现。`SingleRow` 只返回第一结果集的第一行并消费剩余行，不暴露其他结果集，`SingleResult` 会消费后续结果以保持连接可复用；`SchemaOnly` 接受带空白或注释前缀的 `SELECT`、`SHOW`、`DESCRIBE`、`DESC`、`EXPLAIN` 和 `VALUES` 文本查询，仅发送服务端 Prepare 获取元数据，不发送目标 COM_QUERY/EXECUTE/FETCH 或参数数据，也不使用客户端 fallback。多语句、过程及输出参数拒绝；输入参数名称/顺序/数量仍需匹配，但值不编码/读取/验证，FetchSize/offset 不启用。缓存的有效 statement 可复用；空 reader 的 exchange 已完成，HasRows=false、RecordsAffected=-1，CloseConnection 仍在 reader 释放时关闭原会话。命名复杂类型可能执行目录查询，但不执行目标。准备阶段仍有解析、权限及元数据锁成本，准备时列形状也可能与执行时不同；真实服务器副作用/锁/大结果验收仍开放。`KeyInfo` 的同会话键目录和单基表隐藏列已有本地实现，复杂形状保留原 SQL/unknown，详见 [KeyInfo 契约](obconnector-dotnet-architecture-design.md#key-information-and-hidden-columns)；DataAdapter/CommandBuilder 的本地填充、映射、行事件、CRUD/乐观并发已实现，公共边界见 [adapter 契约](obconnector-dotnet-architecture-design.md#data-adapters-and-command-generation)；现代资产 `DbBatch` 已支持 `SchemaOnly`/`KeyInfo` 及其与 `CloseConnection`/`SequentialAccess` 的组合；并支持跨整个批次的 `SingleRow`/`SingleResult`，见[批次结果限制](obconnector-dotnet-architecture-design.md#batch-result-limits)。

目标要求：

- 支持常用 `CommandBehavior`，或在文档中明确 provider 级别限制；当前 `CloseConnection`、`SequentialAccess`、`SingleRow` 和 `SingleResult` 已有本地回归；
- `SchemaOnly` 应限制为经验证的元数据获取路径；当前通过 Prepare-only 路径和禁止执行/回退的协议回归约束目标；
- 保留 `SchemaOnly` 的 Prepare-only、取消、错误、缓存及资源回归，并用真实副作用函数、锁和大结果验证服务端准备行为；不能仅以 `Read=false` 作为证据；
- `SingleRow` 和 `SingleResult` 必须正确结束或取消服务端结果流；
- 完善 `GetSchemaTable`、`GetColumnSchema`、prepared metadata 和参数 metadata；
- 保留 DataAdapter 填充/映射/行状态/事件/并发回归，补真实 CRUD/锁和设计时消费；自动命令不回填 identity/default，复杂形状用显式命令；
- 每个目标 TFM 都要验证相同 API 的编译和运行时行为。

MySQL `CommandType.StoredProcedure` 已有独立执行计划：集合顺序绑定 Input/Output/InputOutput，要求有效 PS_MULTI_RESULTS，拒绝 ReturnValue、FetchSize、SchemaOnly/KeyInfo；关闭 reader 会排空并收集隐藏输出，输出按实际 SELECT 元数据解码。Oracle 仍要求 `Use Oracle Prepare Execute=true`。完整[API 契约](obconnector-dotnet-architecture-design.md#mysql-stored-procedures)与第 15.30 节区分实现和真实部署验收；参数推导只读目录，不证明过程执行兼容性。入口为 `OceanBaseCommand.Start`、`Internal/Sql/MySqlCommandPlan.cs`、`OracleCommandPlan.cs`，沿用原 P1-05/06/10。

### 5.7 Oracle 扩展和复杂类型

当前 Oracle 功能覆盖较多，但限制集中在输出形状、游标、复杂成员和缓存生命周期。

独立 `Prepare/PrepareAsync` 已接入 OUT/RETURNING、过程/函数的真实 server prepare，不执行目标或消费输入；statement 下次执行转交游标所有者，后续隐式执行重新准备。Oracle DML 的纯 OUT 拆分元数据采用精确计数例外，PL 不放宽；同步/异步与资源/错误契约见[公共准备契约](obconnector-dotnet-architecture-design.md#oracle-standalone-prepare)和第 15.34 节。该准备模块未开放正FetchSize输出组合；本次第15.46节已接受正提示，内部输出保持执行响应流式读取，返回cursor仍由GetDataReader(fetchSize)分页。明确规则见[输出提示契约](obconnector-dotnet-architecture-design.md#oracle-output-fetch-hint)；输出offset/数组正FetchSize及其他方向/列值仍受限。

嵌套 object/collection 已由 `ComplexType`、`ComplexValueCodec` 的递归路径和 `ComplexTypeDictionaryTests`、`ComplexParameterTests`、`ComplexResultTests` 覆盖；不能再把“所有嵌套成员”列为缺失。NCHAR/NVARCHAR2成员已有方向编码及限定实库证据，见第15.47节；LOB/cursor、其他national存储/匿名结果、复杂整列RETURNING身份、循环与超限布局仍受限。

目标要求：

- REF CURSOR 支持 Output、ReturnValue、Input、InputOutput 和 cursor-valued columns；
- PrepareExecute 输出路径支持符合契约的 prepare、fetch 和多结果组合；
- complex object/collection 支持嵌套 object、LOB、cursor、national 成员和合法的递归检测；
- 类型描述必须绑定 connection/session/transaction generation；
- 外部 DDL 后的缓存失效可以显式执行，也应提供可预测的自动失效边界；
- 服务端不支持的 nested-table column 必须和驱动不支持的 procedure parameter 分开记录。

当前已经提供 `OceanBaseConnection.ClearComplexTypeCache(transaction)`：在空闲且事务绑定正确的 Oracle 会话上显式失效，之后应重新 `GetComplexType` 并创建绑定值；旧描述的 session/transaction/cache generation 检查已有本地回归。本次增加独立于 statement cache 的物理会话元数据代际：Oracle 文本 CREATE/ALTER/DROP/RENAME/TRUNCATE 在目标发送时使全部描述失效，包括失败的 DDL；Prepare、元数据/控制查询和发送前拒绝不失效。重新加载和重建绑定值后才能执行；已返回值保持可读，新结果不继承旧字典。外部或不透明 DDL 仍需显式清理，真实 DDL/锁/隐式提交/代理兼容性继续开放。完整边界见[公共契约](obconnector-dotnet-architecture-design.md#complex-type-ddl-invalidation)。

### 5.8 类型、字符集和元数据

当前类型映射覆盖大量常用值，但遇到未识别 type code、扩展 metadata、非支持字符集或超出精度边界时会失败。

普通结果字符集已有共享严格映射与流式 decoder，覆盖 ASCII、MySQL latin1/CP1252、GBK、GB18030-2005、UTF-16/LE 和列明的 UTF-8 collation；GB18030-2022本地开发/验证见第15.75节。完整 ID、宽度、非法字节拒绝及客户端/national/JSON/CLOB 边界见[结果字符集契约](obconnector-dotnet-architecture-design.md#result-character-sets)。MySQL ColumnSize 使用 charset 最大宽度，Oracle 长度语义保持不变。六字符集历史实库专项仅覆盖 4.4.2.2 MySQL 直连；UTF16/LE 使用 NULL 保留表达式原编码，直接 SET 的 1231/42000 失败保留，详见第 15.32 节。2022实库、其他编码及全部 collation/模式/版本/代理矩阵继续开放。

Oracle NUMBER 的结果 metadata 可以含负 scale；`DbParameter.Scale` 保持 byte 契约，现通过 `int? OceanBaseParameter.DecimalScale` 显式声明有符号 scale。Oracle -84..127、MySQL 0..30 及精度/无舍入约束已有本地回归，赋值优先级见[参数契约](obconnector-dotnet-architecture-design.md#decimal-parameter-scale)，交付记录见第 15.16 节。自动参数推导沿用该表示，本地回归覆盖负值的保留及精度约束；真实目录/声明矩阵仍须验收。

客户端SQL/普通参数编码本地实现见第15.80节及[输入字符集契约](client-character-sets.md)。
该配置与结果解码、独立national参数及JSON/CLOB边界分别定义；不将普通输入编码扩大为完整类型支持。

目标要求：

- 建立 MySQL/Oracle 模式的类型码清单；
- 为每个类型定义 CLR 值、provider-specific 值、参数绑定和 NULL 行为；
- 覆盖更多字符集和排序规则，并禁止静默替换导致数据损失；
- 对 decimal、timestamp、interval 和 zero-date 保持精度和范围契约；
- 对未知类型提供稳定、可诊断的错误，而不是在 reader 深处出现模糊失败。

### 5.9 HA、发现和恢复

当前支持配置地址、RoundRobin、注入式 `IOceanBaseAddressResolver`、opt-in TCP整轮重试/固定退避（第15.50节）、有界端点TCP健康缓存（第15.51节）、TNS文件适配（第15.52节）、OCP HTTP适配（第15.53节）、分组/策略/权重（第15.54节）及全局黑名单/尝试上限（第15.55节）、分组黑名单覆盖（第15.63节）。count>0的逻辑Open共享Connection Timeout，包括池等待和多连接预热；每次物理建立仍有有限轮数，TCP成功后不重试。认证和会话建立后不做透明迁移。

`Host Recheck Seconds`默认0、范围0..3600秒。启用后在既有起点顺序中把近期TCP失败候选放到最后，保留全候选fallback、单次物理Open/重试轮次的冻结顺序和既有预算。缓存按普通池/私有源或非池selector寿命隔离，最多1024个精确host/port，池Clear/凭据退休/Dispose隔离池缓存的迟到反馈；非池提示随selector存活，不由清池API清除；不探测业务健康。详见[公共契约](obconnector-dotnet-architecture-design.md#endpoint-tcp-health)。

目标要求：

- 已有TNS文件与OCP HTTP地址适配（第15.52/15.53节）；补其他发现策略边界与其他OceanBase发现适配；
- 地址、租户和认证身份绑定校验；
- 保留已实现的连接建立前重试预算和固定退避，补真实节点/代理故障及部署验收；
- 活动连接断线后的明确状态，不自动迁移未完成事务；
- 只读安全重试与写操作 unknown outcome 的不同策略；
- 直连和 OBProxy 的路由、重试、reroute、故障恢复矩阵。

### 5.10 Schema、批处理、缓存和诊断

当前 schema 为 Oracle 20/MySQL 18 个集合，包含 Restrictions、常用关系/对象目录及静态 DataTypes，已有最终本地回归（第 15.14 节），详见 [Schema 契约](schema.md)。双模式实库/权限/版本/OBProxy 验收仍开放。batch 仍顺序执行；prepared statement 已有 opt-in 有限跨命令 idle cache，命令持有和缓存失效规则见第 15.25 节，真实 DDL/reprepare 和负载验收未闭合。诊断能力已覆盖池生命周期及建连/探活分段；固定标签、错误分类和计量边界见[公共契约](obconnector-dotnet-architecture-design.md#connection-phase-diagnostics)。[慢命令阈值报告](obconnector-dotnet-architecture-design.md#slow-query-diagnostics)已有实现；协议帧诊断已有本地实现；其他资源等待和部署性能验收仍缺。

目标要求：

- 保留已有 `MetaDataCollections`、`DataSourceInformation`、`Tables`、`Columns`、`Procedures` 和 `ProcedureParameters` 六个集合；独立的 Views、Indexes、Keys、Constraints、Types、Sequences、Users 和 Synonyms 等目录已有本地实现，仍需真实验收；Types/Synonyms 仅 Oracle，模式/权限边界见 Schema 契约；
- 过程元数据能够安全映射到参数描述，但不能把不完整目录信息误当成完整签名；
- 保留已有有限跨命令 idle LRU、独占和租约内上下文检查回归，补真实 DDL/reprepare、资源和负载验收；
- 为 batch 提供 rewrite、multi-send、bulk 或 array binding 的模式化选择；
- 暴露连接池总数、借出数、等待数、丢弃数、reset 失败数和连接建立耗时；
- 诊断 SQL 必须脱敏，能够区分等待、网络、服务器错误和 unknown outcome。

#### 5.10.1 目录已有实现之外的 ADO.NET 元数据缺项

以下三项不能用“已扩展 Schema 目录”统一关闭。前两项归入 P1-10/W6，第三项归入 P1-05/W5；不改变 23 个主 ID。

| 能力 | 当前源码事实 | 后续验收要求 |
| --- | --- | --- |
| `GetSchema("DataTypes")` | 静态表已本地实现，含标准 23 列及 9 个扩展列、双模式 CLR/provider 映射和条件；可信 UTF-16 正向/未知字符集拒绝、实际参数与列 codec、五资产消费者均有最终回归（第 15.14 节）。`Types` 仍仅描述 Oracle 命名类型 | 保持参数/结果/精度/scale/NULL/创建模板与实际 codec 一致，补真实服务器类型、DDL、字符集与版本/代理验收。自动推导和 KeyInfo 分别验收，不以静态映射关闭其他缺项。 |
| `DeriveParameters` / 自动推导 | 同步/异步 API、唯一目录身份、完整顶层签名、同会话事务和原子发布已完成本地验证，见第 15.18 节 | 保留 owner/package/重载、方向/顺序/返回值、精度/复杂类型、失败原子性及取消/事务回归；补真实目录、声明、权限、版本与 OBProxy 报告，不扩大目标过程执行范围。 |
| `CommandBehavior.KeyInfo` | 同会话、执行前有界目录快照；主键优先/非空唯一键候选及隐藏尾列已有本地实现；可空唯一约束不冒充行键，组合成员不冒充单列唯一；复杂查询保留原形状及 unknown，见第 15.22 节 | 验证主键/唯一键、组合键、可空性、来源列、别名/连接/表达式列和隐藏键列；无法可靠推导时公开限制，不为 adapter 伪造可更新性。 |

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
| M01 Driver/DataSource/JDBC | `OceanBaseProviderFactory`、`OceanBaseConnection`、`OceanBaseDataSource` | 基础入口及 DataAdapter/CommandBuilder 本地实现已有；真实 CRUD/目录/运行时和 ORM 生态仍待验收 |
| M02 URL/options/address/discovery | `OceanBaseConnectionStringBuilder`、`EndpointSelector`、`IOceanBaseAddressResolver` | 多地址、注入resolver、端点TCP健康缓存、TNS文件及OCP HTTP适配存在；其他发现策略边界和真实健康验收仍缺 |
| M03 transport/TLS/auth/credential SPI | `HandshakeFlow`、`TlsHandshakeFlow`、认证实现 | 多种认证、池化/直连客户端证书、数据源证书快照、撤销检查和自定义服务器验证回调已接入；真实企业 CA/KDC/mTLS、凭据轮换仍未闭合 |
| M04 handshake/mode/capability | `HandshakeFlow`、`NegotiatedCapabilities` | 模式和部分能力协商存在；多语句、LOCAL INFILE 和标准 MySQL 压缩已有 opt-in 路径；OB20 压缩/reroute 仍缺失 |
| M05 session/transaction/cancel | `PhysicalSession`、`OceanBaseTransaction`、`AmbientEnlistment` | 本地事务、ambient enlistment、取消和 unknown outcome 有路径；活动连接迁移缺失 |
| M06 query/prepare/cursor | `OceanBaseCommand`、`PreparedStatement`、`QueryResult` | 基础文本/server prepare 和 cursor 有路径；statement cache、完整 CommandBehavior 未闭合 |
| M07 binding/sql parser/batch | `ParameterBindingPlan`、`OracleArrayBindingPlan`、`OceanBaseBatch` | 参数、数组绑定及opt-in MySQL INSERT rewrite有本地路径；multi-send/bulk仍缺 |
| M08 result/rows/multiple results | `OceanBaseDataReader`、`QueryResult` | 流式读取和多结果有路径；所有结果形状和资源边界仍需矩阵验证 |
| M09 types/charset/time | `Internal/Values`、`OceanBaseDbType` | 覆盖较广；扩展 type code、字符集、精度和跨模式边界仍有限制 |
| M10 LOB/complex | `OceanBaseLob`、`OceanBaseComplexType` | 基础 LOB 及嵌套 object/collection 存在；LOB/cursor/national 成员和真实 DDL 验收未闭合；可观察 DDL 自动失效已有，外部/不透明 DDL 沿用显式清理 |
| M11 metadata/catalog | `OceanBaseConnection.Schema`、reader schema、`OceanBaseCommand.DeriveParameters` | Oracle 20/MySQL 18 个集合，含常用目录、过程参数目录及静态 DataTypes；自动参数推导已本地交付，真实目录/权限/推导矩阵仍未闭合 |
| M12 HA/failover | `EndpointSelector`、地址 resolver | Open阶段候选重试、整轮TCP退避和端点TCP健康缓存已有本地实现；活动连接恢复、发现适配、reroute及真实故障验收仍缺 |
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
| KeyInfo 键目录、来源验证与隐藏列 | `src/OceanBaseConnector/OceanBaseCommand.KeyInfo.cs`、`Internal/Session/CommandExchange.KeyInfo.cs`、`Internal/Sql/KeyInfoQuery.cs`、`Internal/Protocol/KeyInfoMetadata.cs`；`tests/OceanBaseConnector.UnitTests/KeyInfoTests.cs`、`KeyInfoPlanningTests.cs` 和消费者 `KeyInfoConsumerTests.cs` |
| DataAdapter 填充、映射/事件和命令生成 | `src/OceanBaseConnector/OceanBaseDataAdapter.cs`、`OceanBaseCommandBuilder.cs`、`OceanBaseProviderFactory.cs`；`tests/OceanBaseConnector.UnitTests/DataAdapterTests.cs`、`DataAdapterBehaviorTests.cs`、`DataAdapterSurfaceTests.cs` 和消费者 `DataAdapterConsumerTests.cs` |
| Schema collection 范围 | `src/OceanBaseConnector/OceanBaseConnection.Schema.cs`、`OceanBaseConnection.Schema.Routines.cs`、`OceanBaseConnection.Schema.Catalog.cs`、`OceanBaseConnection.Schema.DataTypes.cs`；`tests/OceanBaseConnector.UnitTests/CatalogSchemaTests.cs`、`DataTypeSchemaTests.cs` 和消费者 `CatalogSchemaConsumerTests.cs`；[Schema 契约](schema.md) |
| capability 位和未开放协议能力 | `src/OceanBaseConnector/Internal/Protocol/CapabilityFlags.cs`、`OceanBaseCapabilityFlags.cs`、`src/OceanBaseConnector/Internal/Session/HandshakeFlow.cs` |
| TLS、客户端证书和撤销检查 | `src/OceanBaseConnector/OceanBaseConnection.cs`、`src/OceanBaseConnector/Internal/Transport/TlsHandshakeFlow.cs`、`TlsUpgrade.cs`、`tests/OceanBaseConnector.UnitTests/Transport/TlsComponentsTests.cs`、`ProviderConnectionTests.cs` |
| Oracle 输出、LOB、复杂类型和数组绑定 | `src/OceanBaseConnector/Internal/Sql/OracleCommandPlan.cs`、`OceanBaseLob*.cs`、`OceanBaseComplex*.cs`、`OceanBaseCommand.ArrayBinding.cs` |
| 类型、精度、时间和字符集限制 | `src/OceanBaseConnector/Internal/Values/*.cs`、`tests/OceanBaseConnector.UnitTests/*Type*Tests.cs`、`ZeroDateTests.cs`、`WideDecimalTests.cs` |
| 真实数据库和代理验收入口 | `tests/OceanBaseConnector.Acceptance/`、`tests/OceanBaseConnector.IntegrationTests/`、`docs/compatibility.md` |

## 12. 交付前的最小工作清单

这份清单是“完整驱动”可以对外发布前的最低闭环，不是所有可能的增强功能：

1. 保持池指标可观测；补齐并验证所有会话资源的 reset/丢弃规则。
2. 保留 PSPE 单资源、终态/身份和自动初始化取消本地交付；补真实超时、响应丢失、连接关闭故障矩阵及应用核对/重建示例。分布式提升明确不支持。
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
| P0-01 | 等效配置的容量与清池身份已本地统一，空 PAM 因子及旧池快照保留；Closed回调同步/异步池化Open先完成本次捕获清理（第5.1.4/15.48节），原等待缺陷已修复；通用日志重入边界保留。满池 Close/Open、source Dispose、长期资源/并发及完整真实验收尚缺。 | `OceanBaseConnection.cs`、`OceanBaseConnection.Transactions.cs`、`OceanBaseConnectionStringBuilder.cs`、`OceanBaseConnection.Pooling.cs`；`Internal/Session/PhysicalSessionPool.cs`；`PoolConfiguration*Tests.cs`、`PoolOwnershipTests.cs`、`PoolClearingTests.cs`、`Session/PhysicalSessionPoolTests.cs` | 保留有效配置/凭据策略/私有源隔离及五资产回归、双模式直连报告；补压力下无超额建连、双重归还或等待泄漏，清理超时、Dispose/reset 交错、长借用、预热失败及真实代理报告。 |
| P0-02 | Oracle fresh/reset 自动提交初始化已本地实现且直接服务器专项通过，见第 5.1.5 节；旧 reader 延迟 Close 的逻辑租约缺陷已修复，源码与完整证据见第 5.1.7/15.41 节；内联 LOB/REF CURSOR 阶段与外置持久 LOB 完整读取/本地中断验收见第 5.1.8/15.43/15.44 节；注释、多语句、不透明过程/动态执行和目标发送标记已有本地实现；任意函数/触发器隐式副作用及真实资源/reset 矩阵仍未闭合，不能认定所有状态已隔离。 | `Internal/Session/HandshakeFlow.Identity.cs`、`Internal/Sql/SessionStateSql.cs`、`Internal/Session/CommandExchange.cs`、`PhysicalSession.cs`；`Session/HandshakeFlowTests.OracleAutocommit*.cs` 与 IntegrationTests 的 `Connections/PoolIsolationTests.cs`；`SessionStateSqlTests.cs`、`PoolSessionStateTests.cs`、`PoolSessionStateLifecycleTests.cs`、`PoolingTests.cs`、`TemporaryLobLifecycleTests.cs`、`RefCursorTests.cs`、`PooledResourceIsolationTests.cs`、`PooledExternalLobLifecycleTests.cs` 与真实 `Connections/PoolResourceIsolationTests.cs`/`PoolResourceIsolationTests.ExternalLobs.cs`；五资产 `SessionStateConsumerTests.cs` | 保留 Oracle fresh/reset 隐式写、初始化错误/取消/超时销毁与真实复用证据，补版本/代理/平台矩阵；保留第 5.1.2/15.24 节回归；事务、变量、临时对象、reader、statement、LOB、cursor、XA 各有 A 污染/B 验证的真实双模式和代理测试；不能恢复的状态强制销毁。 |
| P0-03 | PSPE 拒绝第二资源/提升并区分 InDoubt；Initialize 与晚回调绑定原 Open/事务；自动初始化取消及同 scope/单容量池重开已有本地证据，仍不能视为跨资源原子提交或真实故障验收。 | `Internal/Session/AmbientEnlistment.cs`；`OceanBaseConnection.cs`、`OceanBaseConnection.Transactions.cs`；`AmbientTransaction*Tests.cs`、五资产消费者；原 `TransactionTests.cs`/`TransactionFailureTests.cs` 保留 | 保留两条初始化控制 SQL 取消、exact caller token、池/协调器清理、成功 Open 后 token 独立及既有终态/身份回归；补真实模式/版本/代理/事务管理器故障及恢复示例，不以本地 PSPE 关闭整个 P0-03。 |
| P0-04 | 业务写入或提交已发送，响应断开/取消；应用错误重试会重复写入。受控真实提交/回滚回包丢失及新租约专项见第5.2.1/15.45节，完整故障矩阵仍缺。 | `OceanBaseException.cs`；`Internal/Session/CommandExchange.cs`；`CommandFaultTests.cs`、`TransactionFailureTests.cs`、`PooledTransactionResponseLossTests.cs`；IntegrationTests的`Faults/TransactionResponseLossTests.cs`；[应用核对指南](unknown-outcome-recovery.md) | 在发送前、发送后、响应中、commit/rollback 响应丢失注入故障；验证异常分类、会话销毁、禁止 replay 及应用核对操作结果的示例。 |
| P0-05 | 已打开连接遇到节点/代理故障，现有候选重试只覆盖建连 TCP 失败。 | `Internal/Session/PhysicalSession.cs`；`Internal/EndpointSelector.cs`；`TopologyExecutionTests.cs` | 保留第15.49节连接级ERR与显式重建本地回归，补真实节点/代理和完整连接/事务故障；若新增透明恢复，须分别证明只读、幂等写、非幂等写和活动事务边界。完整驱动不要求自动重放未知写。 |
| P0-06 | 不同 resolver 共池、路由更新后复用旧会话的问题已复现并本地修复；真实拓扑/故障矩阵未验证。 | `OceanBaseConnection.cs` 的 `AddressResolver`/`PrepareOpen`；`PhysicalSessionPoolRegistry`/`PhysicalSessionPool.Rent`（同在池文件中）；`PoolRoutingTests.cs` | 保留双服务端、引用身份、快照/预热、失败/取消/总预算、并发发现、Closed 回调清理和借出会话回归；补真实发现/代理/跨平台报告。 |

### 14.2 协议、公共契约和常见部署

| ID | 具体触发场景和影响 | 源码 / 现有回归入口 | 关闭缺口的最低证据 |
| --- | --- | --- | --- |
| P1-01 | opt-in 多语句带参数、Prepare、cursor 或 Oracle 输出时拒绝；允许文本多语句后错误/多结果边界尚缺实库报告。 | `OceanBaseCommand.cs`；`Internal/Sql/SqlStatementBoundary.cs`；`TextCommandTests.cs`、`OracleStatementBoundaryTests.cs` | 成功/中间失败/取消后的结果消费与会话可复用性；不支持组合发送前拒绝；两个模式及 OBProxy 真实验证。Oracle 程序单元内部分号不是多语句缺口。 |
| P1-02 | 标准 MySQL 压缩已本地实现；OB20 压缩 frame 和 reroute 仍被拒绝，客户端压缩契约待确认，三项真实服务器/代理矩阵均未闭合。 | `Internal/Session/HandshakeFlow.cs`；`Internal/Protocol/MySqlCompressionStream.cs`、`ZlibBlock.cs`、`Ob20Packet.cs`、`Ob20Stream.cs`；`Protocol/MySqlCompressionTests.cs`、`CompressionConnectionTests.cs`、`Protocol/Ob20PacketTests.cs`、`Protocol/Ob20StreamTests.cs` | 先取得客户端链路、版本及 capability 证据（第 16.3 节）；验证协商、混合压缩/未压缩、分片、序列、校验、解压大小上限和畸形包；reroute 校验目标且不重放 unknown-outcome 请求；真实代理报告。 |
| P1-03 | LOCAL INFILE 本地协议实现已有，真实服务端请求、代理策略、64 MiB 上限及上传中断仍未验证。 | `Internal/Session/CommandExchange.cs`；`LocalInfileTests.cs` | 实库成功、关闭授权、工厂拒绝、边界/超限、服务器失败、上传取消；失败会话不回池且从不按服务器名称打开本地文件。 |
| P1-04 | 自定义验证回调已提供并隔离池策略；真实企业信任、撤销/轮换未验收。客户端证书池化/数据源快照及 26 项本地回归已完成（第 15.4 节）。回调只在物理 TLS 建立时执行，不能用更改捕获状态撤销旧会话。 | `Internal/Transport/TlsUpgrade.cs`、`TlsHandshakeFlow.cs`；`OceanBaseConnection.cs`、`OceanBaseDataSourceBuilder.cs`；`TlsServerValidationTests.cs`、ConsumerTests 的 `TlsValidationConsumerTests.cs`；`PooledClientCertificateTests.cs` 和 `ClientCertificateConsumerTests.cs` | 保留接受/拒绝/抛异常、主机名和链错误、预热/复用、回调引用身份、取消及重入关闭回归；保留池化证书隔离、source 所有权/释放竞态回归，补真实 TLS/mTLS、过期/轮换、撤销和 Windows/Linux 报告。不得以 `Required` 绕过身份验证完成验收。 |
| P1-05 | MySQL `CommandType.StoredProcedure` 已有有序参数/输出与关闭排空实现，仍有限定方向/capability/行为和真实部署验收（第 15.30 节），目录/参数推导不证明执行兼容性；`KeyInfo` 已有主键/唯一键与单基表隐藏列实现，复杂形状保留 unknown；Batch已支持SchemaOnly/KeyInfo及源条目创建（第15.60节），SingleRow/SingleResult跨批次限制及共享SingleRow修正见第15.61节；REF CURSOR支持SingleRow/SingleResult（第15.62节）；`SchemaOnly` 已改为 Prepare-only 并禁止目标执行/fallback/参数上传；真实准备行为及结果形状矩阵仍缺。 | `OceanBaseCommand.cs`；`OceanBaseDataReader.cs`；`OceanBaseBatch.cs`；`KeyInfoTests.cs`、`KeyInfoPlanningTests.cs`、`MySqlProcedureTests.cs`、`MySqlProcedureLifecycleTests.cs`、`SchemaOnlyTests.cs`、`BehaviorOptionsTests.cs`、`ReaderSchemaTests.cs`、`BatchSurfaceTests.cs`、`BatchMetadataTests.cs`、`LimitedResultBehaviorTests.cs`、`LimitedCursorBehaviorTests.cs`、`RefCursorTests.cs`；消费者 `KeyInfoConsumerTests.cs`、`SchemaOnlyConsumerTests.cs`、`BatchMetadataConsumerTests.cs`、`LimitedResultConsumerTests.cs`、`RefCursorConsumerTests.cs` | MySQL 过程名、参数方向/输出、多结果及取消/错误/池复用的执行回归与实库报告；命令/Batch/REF CURSOR 行为表、主键/唯一键元数据、SchemaOnly 副作用/锁/大结果用例；按实际实现公开限制，不把 SQL 前缀当成只读证明。 |
| P1-06 | REF CURSOR Input/InputOutput仍拒绝；普通二进制和文本四字节标识列/嵌套所有权已有本地实现（第15.56/15.87节），实库仍待验收；独立Prepare及正命令FetchSize提示已有实现，内部输出不分页（第15.46节）。输出offset与ExecuteArray正FetchSize仍拒绝，其余方向/结果形状与部署矩阵继续开放。 | `Internal/Sql/OracleCommandPlan.cs`；`OceanBaseCommand.cs`；`RefCursorTests.cs`、`OraclePrepareExecuteResponseTests.cs`、`CursorColumnTests.cs` | 参数方向 × NULL × 输出行数 × 多结果 × 取消/关闭矩阵；输入/输出游标明确所有权；服务端不支持的形状单列，不伪造通过。 |
| P1-07 | 嵌套 object/collection 已可编解码，NCHAR/NVARCHAR2已有有界支持；LOB/cursor、其他national存储/匿名结果和复杂整列RETURNING身份仍受限。可观察 DDL 发送时已自动失效，外部/不透明 DDL 须显式清理；真实 DDL/锁/隐式提交仍未验证。 | `OceanBaseConnection.ComplexTypes.cs`；`Internal/Values/ComplexType.cs`、`ComplexValueCodec.cs`；`ComplexTypeDictionaryTests.cs`、`ComplexResultTests.cs` | 保留既有嵌套回归，扩展成员类型与 NULL；DDL 后旧描述失效、跨 session/transaction 不误用；深度/成员/字节限制和循环定义继续拒绝。 |
| P1-08 | 普通结果字符集已有严格映射/宽度及流式解码；2022本地开发/验证见第15.75节；其他编码、扩展 metadata/未知类型码仍拒绝；national 限已支持编码；MySQL 客户端字符串降级已有可信UTF-8会话子集（第15.73节），非UTF-8及无反斜杠模式NUL仍拒绝。 | `Internal/Protocol/ResultCharacterSet.cs`、`ChineseEncoding.cs`、`ResultColumn.cs`、`ResultDbColumn.cs`、`NationalCharacterSet.cs`；`Internal/Sql/ClientPreparedSql.cs`、`MySqlLiteralContext.cs`；`Gb180302022Tests.cs`、`CharacterSetTests.cs`、`CharacterSetReaderTests.cs`、`CharacterSetConsumerTests.cs`、`NationalCharacterTests.cs`、`ClientPrepareFallbackTests.cs`、`MySqlClientStringPrepareTests.cs`、`WideDecimalTests.cs` | 每种类型的参数/结果/NULL/精度/溢出表；编码不可表示时不静默替换；字符串降级的真实collation/coercibility及SQL mode/代理验收继续开放；不把禁止降级误称server prepare缺失。 |
| P1-09 | 整轮TCP重试/预算、端点TCP健康缓存、TNS/OCP适配、分组/策略/权重及分组黑名单覆盖已有本地实现（第15.63节）；其他发现策略边界、代理拓扑及真实健康/故障验收仍缺；只注入resolver不能证明HA。 | `IOceanBaseAddressResolver.cs`；`Internal/EndpointSelector.cs`；`MultiAddressTests.cs`、`TopologyExecutionTests.cs`、`ConnectRetryTests.cs`、`EndpointHealthTests.cs`、`EndpointHealthCacheTests.cs`、`OceanBaseTnsAddressResolver.cs`、`TnsAddressResolverTests.cs`、`OceanBaseOcpAddressResolver.cs`、`OcpAddressResolverTests.cs`、`DiscoverySnapshot.cs`、`DiscoveryPolicyTests.cs`、`GroupBlacklistTests.cs` | 地址更新、旧会话失效、错误身份/TLS 不重试、预算/退避、代理 reroute 和节点故障报告；与 P0-06 共同冻结池路由契约。 |
| P1-10 | 常用目录已本地实现；Types/Synonyms 为 Oracle-only，Users/Sequences 可能需要额外权限；真实字典/版本/代理矩阵未验证。DataTypes 及自动参数推导已有本地实现；真实目录互操作未验证。 | `OceanBaseConnection.Schema.cs`、`OceanBaseConnection.Schema.Routines.cs`、`OceanBaseConnection.Schema.Catalog.cs`、`OceanBaseConnection.Schema.DataTypes.cs`；`DataTypeSchemaTests.cs`、`CatalogSchemaTests.cs`、`ConnectionSchemaTests.cs`、`RoutineSchemaTests.cs`；另有 `OceanBaseCommand.Parameters.cs`、`OceanBaseConnection.Parameters.cs`、`Internal/Sql/RoutineParameterDerivation.cs`、`DeriveParameters*Tests.cs`，见第 15.18 节 | 保留 DataTypes 实际 codec/可信字符集正向与拒绝、五资产和完整形状、限制参数、精度、空目录、权限错误回归；完成集合/大小写/权限 × 双模式实库和代理矩阵；保留自动参数推导本地回归，并按第 5.10.1 节完成实库验收。 |
| P1-11 | 应用需要短期口令/token 或密钥轮换；本地实现已完成，真实过期/拒绝/轮换服务与插件矩阵仍未验证。 | `IOceanBaseCredentialProvider.cs`；`OceanBaseConnection.Credentials.cs`；`Internal/CredentialAcquisition.cs`；`Internal/Session/PhysicalSessionPool.Credentials.cs`；`CredentialProviderTests.cs`、`CredentialProviderLifecycleTests.cs`、`CredentialProviderDiagnosticsTests.cs`；消费者 `CredentialProviderConsumerTests.cs` | API 获取时点、取消、过期/刷新失败、并发刷新和旧会话失效测试；日志/trace/指标不包含凭据；本地插件测试外还需真实 plugin/KDC 成功及拒绝矩阵。 |

### 14.3 生态、性能、诊断和验证

| ID | 具体触发场景和影响 | 源码 / 现有验证入口 | 关闭缺口的最低证据 |
| --- | --- | --- | --- |
| P2-01 | Factory 已创建 adapter/builder，本地支持填充、映射/事件、显式及有界自动 CRUD。隐藏/缺失行键、未知查询形状和需绑定上下文的列不能自动生成更新。 | `OceanBaseDataAdapter.cs`、`OceanBaseCommandBuilder.cs`；`DataAdapter*Tests.cs`、`DataAdapterConsumerTests.cs` | 保留 CRUD/原始值/NULL/并发冲突/事务/资源回归，补双模式真实目录、生成 SQL、锁和独立运行时消费；公开生成值回填及显式命令限制。ADO.NET provider 不等于 EF Core provider。 |
| P2-02 | opt-in idle LRU 已避免跨命令重复 prepare；默认关闭，命令仍独占其 ID。真实 DDL/reprepare、资源及负载效益仍待验收。 | `OceanBaseCommand.cs`；`Internal/Session/PhysicalSession.PreparedStatements.cs`、`PreparedStatement.cs`；`StatementCache*Tests.cs`、五资产消费者 | 容量、淘汰、statement close、reset generation、DDL/schema epoch 及并发上限；保留报文计数和合成延迟对照，补真实 prepare 次数/延迟、资源与代理报告。 |
| P2-03 | DbBatch默认逐条，opt-in MySQL INSERT rewrite及group failure本地路径见第15.72节；Oracle array binding已有。multi-send/bulk仍缺。 | `OceanBaseBatch.InsertRewrite.cs`、`MySqlInsertRewrite.cs`、`BatchInsertRewrite*Tests.cs`；`OceanBaseCommand.ArrayBinding.cs` | 对已承诺路径补真实行数/部分失败、取消、包上限和事务边界；与顺序模式作吞吐/内存对照。保留OUT限制及未知写不重放。 |
| P2-04 | 现有日志/Activity/Meter、池指标及九阶段建连/探活直方图已有实现；慢命令阈值报告已有实现；协议帧诊断及其卸载修复已有本地实现；其他资源等待与负载验收仍缺。 | `Internal/DriverDiagnostics.cs`、`DriverDiagnostics.ConnectionPhases.cs`；`SlowQueryDiagnosticsTests.cs`、`ConnectionPhaseDiagnosticsTests.cs`、`DiagnosticsTests.cs`、`CommandLogCategoryTests.cs` | 指标单位和聚合语义、异常分类、监听开关开销、SQL/凭据脱敏；明确 `borrowed` 是借用次数，不能据此报告当前借出连接数。 |
| P2-05 | 两个包均产出五个 TFM，但现代 API 条件编译；第15.74节已有Windows实际8/9/10及Framework的nupkg协议子集，不覆盖完整功能/ORM与非Windows；第15.58节原崩溃检查点和第15.59节诊断卸载修复及默认642项回归继续保留。 | 两个 src 项目 `.csproj`；`.github/workflows/ci.yml`；`eng/Verify-Packages.ps1`；`tests/PackageConsumer/ProtocolScenarios.cs`；ConsumerTests、DI tests | 对每个承诺运行时运行实际 nupkg consumer；Windows/Linux、标准/现代 API 差异和 Framework 支持决定有报告。savepoint 的当前标识符规则不接受 `__EFSavePoint`，ORM 集成需单独验收。 |
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

### 15.19 2026-10-25 完整走查文档交付复核

本次按用户要求整理完整文档，不实施新模块。重新核对的源码基线是 `557a05a`，Java HEAD 为 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`。关键路径和已有 TRX 复核汇总见[报告第 12 节](obconnector-dotnet-driver-audit.md#12-当前交付快照2026-10-25)；不把此前参数推导开发起点 `2bbecac` 继续写成当前源码基线。

当前 SchemaOnly 路径仍执行目标 SQL，尚未有该模块的新实现、回归或提交；现有测试只证明元数据可读、行被隐藏及后续连接可用。参数推导、DataTypes、有符号 DecimalScale、凭据轮换、标准 MySQL 压缩及池能力保持各自已取得的本地交付状态，不重新列为完全缺失，也不因这些交付关闭真实兼容性缺口。

本次只复核现有报告，未新跑 build/test/pack 或任何实库：完整单测 3900、消费者 2328、DI 14 通过，Integration 9 通过/72 个单独 NotExecuted；新增推导消费者按方法名确认实际 20 项。原始命令、失败、警告和环境边界保留于第 15.18 节及此前历史。文档检查覆盖本地链接/锚点、23 个 ID 的三张表、八工作包唯一覆盖、代码围栏、UTF-8 无 BOM/CRLF 及 diff；产品代码、兼容性台账和历史验收结果保持原样。

交付后全部 23 项及八个工作包继续追踪，关闭标准仍为第 14 节、第 16.2 节和发布门槛；本次文档交付不代表完整驱动已经完成。

### 15.20 2026-10-25 SchemaOnly 元数据专用路径

**状态：该子模块本地实现、完整回归及独立走查通过；真实互操作未验收。**

开发起点 `557a05a`。此前第 15.19 节和走查报告第 12 节是修复前文档快照，其中“仍执行目标”的描述不再作为当前实现结论。该子模块只处理 P1-05 的 SchemaOnly；KeyInfo、Batch/REF CURSOR 行为差异及整个 P1-05 的其他要求继续开放。

实现及公共边界：

- 同步/异步均复用现有 COM_STMT_PREPARE，使用准备时列元数据构造没有数据行的 QueryResult；不发送目标 COM_QUERY、COM_STMT_EXECUTE、COM_STMT_FETCH 或参数上传。
- 单条、已接受查询前缀的 CommandType.Text；拒绝过程、多语句和输出参数。输入参数名称、顺序和数量仍须匹配；Value 不编码、读取或按绑定校验，包括 stream、未支持的标量值及已释放 LOB。
- FetchSize/offset hint 不启用游标；即使 AllowClientSidePrepareFallback=true，Prepare 拒绝也不回退执行，保留服务端错误码/SQL state。
- 有效 prepared statement 可复用，保留 database、物理会话及 generation 失效/清理规则。列对象按当前 LOB/复杂类型上下文复制，不改写缓存元数据；命名或来源不完整的复杂列沿用既有描述及来源字典查询。
- 元数据 reader 无行，HasRows=false、RecordsAffected=-1；exchange 已完成，连接可继续使用。CloseConnection 仍在 reader 释放时关闭其原会话。Prepare 阶段的取消、超时、连接中断沿用既有错误映射，不谎报目标 SQL 已执行。
- Prepare 不是零成本操作；仍可能解析、检查权限及取得元数据锁。驱动保证的是不发送目标执行请求；准备阶段的真实行为、依值变化的结果形状、版本和代理矩阵另行验收。不以本地 fixture 证明服务端无任何副作用。

源码为 OceanBaseCommand.Start/ExecuteReaderCore、QueryResult.FromPrepared 和 ResultColumn.WithContext；本地测试为 SchemaOnlyTests、原 TextCommandTests 的两项回归，以及消费者 EveryAssetSchemaOnlyPreparesWithoutExecutionOrFallback。测试直接约束目标请求和 statement 关闭包，覆盖同步/异步、双模式 fixture、传统/省略 EOF、缓存、无流读取/上传、准备拒绝、取消/超时、CloseConnection、LOB、匿名/命名/来源推导复杂类型。五资产在 .NET10 上加载不是五个独立运行时，也不是实际 MySQL 模式服务器验收。

独立 review_schema_only 发现 StartCommand 即使 validateComplexTypes=false 仍验证 LOB 值；新增公共读取/释放 LOB 后的两项 RED 均复现 ObjectDisposedException。SchemaOnly 现在传入空参数绑定列表，最终相关回归通过；审阅者确认无剩余 Critical/Important。补充六项命名/来源复杂列回归后，相关单测共 30 项通过。

报告在忽略目录 TestResults/schema-only，日志为根目录 TestResults-schema-only-*.log。保留以下失败和修复，不能删除或改写成通过：

| 历史证据 | 结果与原因 |
| --- | --- |
| 初次测试编译 | 错用 DbCommand 的 provider 属性且缺 DbDataReader 扩展命名空间；改为 OceanBaseCommand 和 System.Data.Common |
| prepare-red.trx | 8 失败；首个 fixture 重复发送结果元数据，是无效 fixture，不能单独作为产品 RED |
| target-red.trx | 元数据 fixture 修正后 8 失败，但后续命令边界仍待修正；最终 RED 证明以下述隔离副本为准 |
| baseline-final-fixture-red.trx | 最终 fixture 加载上一模块保留包的 net10 DLL，8 项全部失败；当前 DLL 同一组 8 项通过。使用 TestResults 内隔离 test host，不覆盖正在验证的资产，确认旧路径仍执行目标而违反 Prepare-only 契约 |
| prepare-green.trx | 初次实现后 8 失败，元数据断言已通过，后续独立查询缺 fixture BeginCommand；修正命令边界后 behavior-green 为 10 通过 |
| lifecycle-first.trx | 20 通过/2 失败；LOB fixture 未开启依赖的 Oracle PrepareExecute，修正配置 |
| lob-value-red.trx | 2 失败；独立走查发现的真实 LOB 输入校验问题，已修复 |
| 新复杂元数据回归初次编译 | DbColumn 没有 ProviderType 公共属性；改用其字符串索引器，保留准确 API 使用 |
| lifecycle-green.trx / complex-green.trx | 24 / 30 通过，退出 0 |
| consumer.trx | 新增五资产消费者实际 20 通过，退出 0 |
| 完整单测初次运行 unit.trx | 3922 通过，退出 0；不包含其后新增的六项复杂元数据回归，最终完整结果另列 |
| unit-verified.trx | 与完整消费者/格式并行时 3923 通过/5 失败，退出 1；ConnectSyncRepeatedSuccessDoesNotPoisonLaterConnections、ConnectSyncSucceedsAndRefusedWraps、RsaKeyWaitHonorsConnectionBudgetAndInterruption、IdleSessionsCannotBypassDiscoveryCancellationOrBudget、SilentTlsHandshakeIsInterruptedAndClosesTransport 的网络/时间预算场景失败；未改产品或断言 |
| timing-recheck.trx | 上述五个方法的全部 22 用例独立子集复核通过，退出 0；完整单测重跑结果另列，不把此子集当成完整通过 |
| DI / Integration | 14 通过 / 9 通过与 72 个单独 NotExecuted，退出 0；没有新增真实互操作证据 |
| 构建 / 格式 | build-verified 为 0 警告/0 错误；格式 first 和 verified 均退出 0，有 workspace-load warning；本地 pack 退出 0，五 DLL hash 及当前 README 匹配，不构成独立运行时包验收 |

最终交付证据：

| 检查 | 报告/日志 | 结果 |
| --- | --- | --- |
| 完整单测重跑 | unit-recheck.trx | 3928 通过，0 失败/跳过，退出 0；较上一模块新增 28 项（相关子集 30 含原有两项） |
| 完整消费者 | consumer-verified.trx | 2348 通过，0 失败/跳过，退出 0；已确认包含新增方法的实际 20 项 |
| DI / Integration | di.trx / integration.trx | 14 通过 / 9 通过与 72 个单独 NotExecuted，退出 0 |
| Release 构建 | TestResults-schema-only-build-verified.log | 0 警告/0 错误，退出 0 |
| 六个 C# 文件格式复核 | TestResults-schema-only-format-verified.log | 退出 0；保留 workspace-load warning |
| 本地 pack | TestResults-schema-only-pack.log | 退出 0，五 DLL hash 和内嵌 README 与当前 Release 输出/源码一致；无发布或独立运行时包消费声明 |
| 独立源码/测试/契约走查 | review_schema_only | LOB 修复及六项复杂列回归后无剩余 Critical/Important；审阅者未代替主任务执行完整套件 |

原并行回归的五个网络/时间预算失败保留；22 项方法子集及完整重跑均通过，但这不证明高负载时序根因已经修复或长期稳定性已验收。没有放宽断言、跳过失败或修改这些底层实现。

核心命令：

```powershell
dotnet build OceanBaseConnector.slnx -c Release --no-restore
dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-restore --no-build --logger 'trx;LogFileName=unit-recheck.trx' --results-directory TestResults/schema-only
dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-restore --no-build --logger 'trx;LogFileName=consumer-verified.trx' --results-directory TestResults/schema-only
dotnet test tests/OceanBaseConnector.DependencyInjection.Tests -c Release --no-restore --no-build --logger 'trx;LogFileName=di.trx' --results-directory TestResults/schema-only
dotnet test tests/OceanBaseConnector.IntegrationTests -c Release --no-restore --no-build --logger 'trx;LogFileName=integration.trx' --results-directory TestResults/schema-only
```

源码、API、兼容性、README 和进度台账同步；文档链接/锚点、围栏、UTF-8 无 BOM/CRLF、三张 23-ID 表、八工作包覆盖及 diff 检查通过。此交付不关闭整个 P1-05、23 个缺口、八个工作包或完整驱动目标。

### 15.21 2026-10-25 SchemaOnly 交付后完整文档复核

本节是 KeyInfo 开发前的历史文档快照；后续开发与验证见第 15.22 节。

本次源码基线 `de15ec2`，工作区初始干净；只整理文档，不继续 KeyInfo 开发，不新增产品、配置或测试 API。Java 本地参考 HEAD 经只读核实仍为 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`。本次没有运行 Java 池或将参考类的存在视为运行兼容性证明。

交付内容为[走查报告第 14 节](obconnector-dotnet-driver-audit.md#14-当前完整文档交付2026-10-25)：文档阅读顺序、各类缺口的实际影响和最低验收、当前源码与历史报告的证据边界。第 4/14 节的 23 个稳定 ID、第 16 节的八工作包、开发历史、真实 Oracle 失败和所有跳过均保留。更新页首当前基线及 P1-05 总表，避免把已交付 SchemaOnly、SingleRow/SingleResult 再列为尚无实现。

本次重新解析已有 SchemaOnly TRX：完整单测 3928、完整消费者 2348（包含新增方法实际 20 项）、DI 14 通过；Integration 9 通过/72 NotExecuted。原并行运行 3923 通过/5 失败及后续 22 项方法子集通过均复核保留。重读原构建 0 警告/0 错误和格式 workspace-load warning。上述结果属于已有报告复核，未在本次重新执行构建、测试、格式、pack、独立运行时或真实数据库验收，也不证明原时序问题已修复。

本次文档检查通过：9 份 Markdown 的 191 个相对文件链接/锚点及围栏，三张各 23-ID 缺口表，八工作包对 23 项的唯一归属，第 15.1—15.20 节历史保留及本节新增，三份修改文件的 UTF-8 无 BOM/CRLF，以及 `git diff --check`。不修改历史测试结论，不关闭任何 ID、工作包或完整驱动目标。

### 15.22 2026-10-25 KeyInfo 键元数据与隐藏列

开发起点 `de15ec2`；本次保留第 15.21 节及走查报告第 14 节的开发前文档快照，继续开发 P1-05。全部 23 个 ID 和八工作包不变。

契约与实现：

- 同步/异步 KeyInfo 在同一 CommandExchange/物理会话/事务内先 Prepare 原目标、查询参数化键目录，然后才上传参数长数据和执行目标；无额外连接、无客户端 Prepare fallback。每次执行重新获取目录快照。
- 单基表、能证明保留行语义的 SELECT 优先完整主键，否则选择完整非空唯一键；可空单列 UNIQUE 的 IsUnique 可以为 true，但不选作 IsKey。组合键成员不单独标 unique；Oracle 有效 P/U 约束独立读取 ALL_CONS_COLUMNS，单独读取可用普通唯一索引，owner 长度前缀及 C/I 身份避免跨 owner/约束/索引同名混淆。MySQL prefix/functional 成员使整个候选不适用。
- 缺失键成员追加到投影尾部，引用来源标识符并避开别名冲突；再次 Prepare 获得类型并校验原列前缀及隐藏来源。FieldCount 包含隐藏列，VisibleFieldCount 为原投影列数，schema 报告 IsHidden/IsKey/IsUnique。原 CommandText/_prepared 缓存保持原 SQL；当前执行独立持有并队列关闭改写 statement，关闭不插入活动 reader/cursor 响应。
- 简单投影（不含括号）、单命名来源和可选 schema/alias、WHERE 的嵌套过滤、无函数 ORDER BY、LIMIT/OFFSET/FETCH/FOR 可进入改写；词法处理注释、引号和 Oracle Q/NQ。DISTINCT/UNIQUE、聚合/分组、join、set/CTE/derived、视图/synonym、缺失来源和 SQL_MODE 不确定形状不改写，不虚构行键/表达式/可更新性；未知结果使用实际执行元数据。目录/行键相关来源改变时明确失败。
- 目录上限 4096 行/4 MiB 字符串，新增列受 4096 列/包长度限制。SchemaOnly 仍不执行或上传目标参数，FetchSize/offset 不启用；正常 cursor/顺序读取及原有行为组合沿用生命周期。多语句/过程/OUT/array 组合发送前拒绝；目录准备/读取阶段的错误或取消不声称目标 unknown outcome；目标发送后的取消仍按普通 unknown-outcome 规则处理。详见 [API 契约](obconnector-dotnet-architecture-design.md#key-information-and-hidden-columns)。

原始失败及修复记录（报告在忽略目录 `TestResults/key-info/`）：

| 报告/事件 | 原始结果和处置 |
| --- | --- |
| 首次测试编译 | 缺少 System.Data.Common 扩展方法命名空间，修正测试 using 后执行实际 RED |
| primary-red.trx / primary-first.trx | 原实现拒绝 KeyInfo 的 4 项 RED；首轮实现 4 项通过 |
| hidden-first.trx | 20 项通过，含双模式/同步异步/SchemaOnly与实际隐藏复合键及缓存复用 |
| planning-red.trx / planning-green.trx | 4 失败/24 通过：Oracle SELECT UNIQUE 和 ORDER BY aggregate 不得改写；修复后相关全部 48 项通过 |
| lifecycle-first.trx | 80 项通过，含复杂查询保持原样、目录错误及视图/不可见目录 |
| unit-first.trx | 沙箱完整运行 3891 通过/117 失败；87 个 PFX CryptographicException、3 个直接 TLS AuthenticationException、20 个诊断文本断言失败、5 个超时及2个连接异常。期间另触发重叠构建并产生 MSB3026 复制重试；本次结果不作为最终完整验收。未修改 TLS/证书产品代码或放宽断言 |
| review-first-red.trx | 3 失败/8 通过，复现 NQ 和缺少 owner 区分的 Oracle 目录 SQL；修正来源词法及目录身份 |
| constraint-source-red.trx | 错误的参数筛选实际选择 0 项，不能作为 RED 证明 |
| constraint-source-actual-red.trx | 实际 2 失败/2 通过，Oracle 键目录须包含有效约束及其 ALL_CONS_COLUMNS 成员，不能仅靠唯一物理索引 |
| dynamic-metadata-red.trx | 2 失败；参数表达式的执行元数据可变，未知键结果改用实际列快照，增强结果仅保护相关来源 ordinal |
| review-first-green.trx / unit-recheck.trx | 专项 83 通过；本机权限环境完整 4011 通过，0 失败/跳过；当时尚不含后增的两项取消回归 |
| cancel-first.trx / keyinfo-verified.trx | 新增目录阶段取消且输入流未被读取的 2 项通过；最终专项 85 通过，包含隐藏键与 MySQL fetch cursor 同一 statement 的生命周期 |
| consumer-first.trx | 五资产 × 同步/异步 × 本地双模式 fixture 实际 20 项通过；每项执行 SchemaOnly 及顺序读取两轮并校验原缓存、隐藏值和 statement close |

最终验证记录：

| 检查 | 报告/日志 | 实际结果 |
| --- | --- | --- |
| 完整单测 | unit-verified.trx | 4013 通过，0 失败/跳过，退出 0；含实际 85 项 KeyInfo 新用例 |
| 完整消费者 / 新增五资产消费者 | consumer-verified.trx / consumer-first.trx | 完整 2368 通过，0 失败/跳过，退出 0；含新增实际 20 项，与子集结果一致 |
| DI / Integration | di.trx / integration.trx | 14 通过 / 9 通过与 72 个单独 NotExecuted，退出 0 |
| Release 构建 | TestResults-key-info-build-verified.log | 0 警告/0 错误，退出 0 |
| 十个 C# 文件格式 | TestResults-key-info-format-verified.log | 退出 0，保留 workspace-load warning |
| 最终独立产品/测试/契约走查 | review_keyinfo_design | 三项 Important 修复后无剩余 Critical/Important；未代替主任务运行完整套件 |

核心命令：

```powershell
dotnet build OceanBaseConnector.slnx -c Release --no-restore
dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-restore --no-build --logger 'trx;LogFileName=unit-verified.trx' --results-directory TestResults/key-info
dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-restore --no-build --logger 'trx;LogFileName=consumer-verified.trx' --results-directory TestResults/key-info
dotnet test tests/OceanBaseConnector.DependencyInjection.Tests -c Release --no-restore --no-build --logger 'trx;LogFileName=di.trx' --results-directory TestResults/key-info
dotnet test tests/OceanBaseConnector.IntegrationTests -c Release --no-restore --no-build --logger 'trx;LogFileName=integration.trx' --results-directory TestResults/key-info
```

文档最终检查通过：9 份 Markdown、200 个相对链接/锚点及围栏，三张完整 23-ID 表、八工作包唯一归属、第 15.1—15.22 节及原进度历史保留；六份修改文档使用 UTF-8 无 BOM/CRLF，`git diff --check` 通过。本模块未重新 pack 或发布包。

真实字典 SQL/权限/临时对象/DDL并发、版本、OBProxy、锁、跨平台及独立运行时验收仍开放；本地固定源码核对确认 Oracle 目录存在所用约束状态列，但不能替代服务器解析/行为证明。此子模块不关闭整个 P1-05、其他 23 项、八个工作包或完整驱动目标。

### 15.23 2026-10-25 DataAdapter / CommandBuilder

开发起点 `d7e8b5a`，master。P2-01 新增所有五资产的 OceanBaseDataAdapter / OceanBaseCommandBuilder 和 factory 创建入口；不引入依赖。第 15.22 节保留此前 KeyInfo 交付。

契约：基于框架实现同步 Fill/FillSchema、DataTable/DataSet 映射、AddWithKey、行状态及原始值/NULL 绑定、RowUpdating/RowUpdated、AcceptChanges 和 ContinueUpdateOnError；顺序 UpdateBatchSize=1。生成命令继承源连接/事务/超时，不自动创建跨行事务、commit 或重试写；UPDATE/DELETE 零影响行报告 DBConcurrencyException。详见 [API 契约](obconnector-dotnet-architecture-design.md#data-adapters-and-command-generation)。

生成元数据来自同会话 SchemaOnly|KeyInfo。仅证明的单基表来源可生成；UPDATE/DELETE 要求完整可见行键，INSERT 可在已知基表无键时生成。隐藏键/未知形状不臆造更新目标；组合键成员全部保留，可空 UNIQUE 不能被框架误作行键。MySQL 数据库身份仅在 builder 本地目录中移到 schema 限定位置，防止框架丢掉 catalog；不改 reader 公共元数据。Oracle 使用双引号/:pN，MySQL 使用反引号/?。自动命令不回填 identity/default/RETURNING；locator LOB/复杂/cursor 绑定需要显式命令。RefreshSchema 使目录缓存失效并释放旧生成命令，下次生成时重取目录；adapter/command clone 不作为支持契约。

开发/走查证据保留在忽略目录 TestResults/data-adapter/ 及根目录 TestResults-data-adapter-*.log：

| 报告/事件 | 实际结果及处置 |
| --- | --- |
| initial-red.trx / initial-green.trx | 旧 factory 返回 null，实际 5 失败；实现后同 5 项通过 |
| temporal-red 编译 | 新 fixture 参数数量 int 未转换为 ushort，修正编译后再执行真正 RED；不视为产品失败复现 |
| temporal-actual-red.trx | 实际 2 失败：生成参数 Scale=0 强制零小数秒，MySQL/Oracle 合法时间均被拒绝 |
| behavior-first.trx | 81 通过/2 失败：重取 GetInsertCommand 后框架重建并清空参数 Values，测试须重设绑定值；未改变产品断言或框架行为 |
| behavior-green.trx / boundaries-green.trx | 90 / 94 通过，包含已有 builder 相关回归；新增事务/RefreshSchema、隐藏/可空/复合/缺失键、原始值/NULL、实际 CRUD、冲突/事件、引用/引号及精度测试 |
| consumer-first.trx | 21 通过：新增五资产 × 普通/AddWithKey × 双模式 fixture 实际 20 项，加已有 factory consumer 1 项；覆盖 DataSet 列映射、主键、实际生成更新和 statement 关闭 |
| 独立 review_data_adapter | 1 项 Important 由真实 RED 复现并修复：ClearScaleDeclaration 清除两种显式 scale，再应用已知精度；最终产品/测试复核无剩余 Critical/Important。审阅者未代替主任务执行完整套件 |

核心命令（均在同一 Windows/.NET10 host 执行）：

```powershell
dotnet build OceanBaseConnector.slnx -c Release --no-restore
dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-build --no-restore --logger 'trx;LogFileName=unit-verified.trx' --results-directory TestResults/data-adapter
dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-build --no-restore --logger 'trx;LogFileName=consumer-verified.trx' --results-directory TestResults/data-adapter
dotnet test tests/OceanBaseConnector.DependencyInjection.Tests -c Release --no-build --no-restore --logger 'trx;LogFileName=di.trx' --results-directory TestResults/data-adapter
dotnet test tests/OceanBaseConnector.IntegrationTests -c Release --no-build --no-restore --logger 'trx;LogFileName=integration.trx' --results-directory TestResults/data-adapter
dotnet pack OceanBaseConnector.slnx -c Release --no-build --no-restore --output TestResults/data-adapter/packages
$adapterFiles = @(
  'src/OceanBaseConnector/OceanBaseDataAdapter.cs',
  'src/OceanBaseConnector/OceanBaseCommandBuilder.cs',
  'src/OceanBaseConnector/OceanBaseParameter.cs',
  'src/OceanBaseConnector/OceanBaseProviderFactory.cs',
  'tests/OceanBaseConnector.UnitTests/DataAdapterTests.cs',
  'tests/OceanBaseConnector.UnitTests/DataAdapterBehaviorTests.cs',
  'tests/OceanBaseConnector.UnitTests/DataAdapterSurfaceTests.cs',
  'tests/OceanBaseConnector.UnitTests/ProviderSurfaceTests.cs',
  'tests/OceanBaseConnector.ConsumerTests/DataAdapterConsumerTests.cs',
  'tests/OceanBaseConnector.ConsumerTests/TargetSurfaceIsolationTests.cs'
)
dotnet format whitespace OceanBaseConnector.slnx --no-restore --verify-no-changes --include $adapterFiles
```

文档检查：9 份 Markdown 的 208 个相对链接/锚点及围栏、三张完整 23-ID 表、八工作包唯一覆盖和历史 15.1—15.23/此前 audit/progress 保留均通过；六份修改文档使用 UTF-8 无 BOM/CRLF，diff 检查通过。

最终门槛：完整单测 unit-verified.trx 实际 4047 通过，0 失败/跳过，退出 0，含本模块实际新增 34 项；DI 14 通过，Integration 9 通过/72 个单独 NotExecuted，均退出 0。完整消费者 consumer-verified.trx 实际 2388 通过，0 失败/跳过，退出 0，含新增 20 项，与 consumer-first 子集一致。本地 `dotnet pack --no-build --no-restore` 退出 0，两个包各五个 DLL SHA-256 和嵌入 README 与构建输出/工作区一致，记录见 package-validation.json；未发布包，也不声称独立运行时消费。最终 Release 构建 0 警告/0 错误，格式检查限定本模块十个 C# 文件、退出 0，workspace-load warning 保留。五资产在同一 .NET10 host 加载，不等于五种独立运行时。真实 OceanBase/OBProxy 的字典、CRUD/锁、权限、DDL、版本、跨平台和独立运行时验收仍开放；23 项和八工作包不整体关闭。

### 15.24 2026-10-25 会话状态识别与发送边界

从 `4dd20a7` 开发 P0-02 的本地识别子模块：共享词法识别覆盖前置/词间注释与空白、后续多语句、Oracle 匿名/带标签块、StoredProcedure 名称、MySQL 可执行注释/用户变量/命名锁及引号歧义。CommandExchange 仅在目标发送前、取消检查后落地持久销毁标记；Prepare、发送前拒绝、目录/元数据及 long-data 流量不因该分类提前标记。物理池容量/归还规则不另建分支，仍使用原 CanReturn 销毁路径。公开语义和代价见第 5.1.2 节及 [API 契约](obconnector-dotnet-architecture-design.md#pooled-session-state-after-user-sql)。

测试覆盖普通查询/字面量/注释与常见标识符复用、Max Pool Size=1 的双借用者同步/异步及两模式物理身份、双结果后销毁、实际 Prepare/缓存 Execute、无输出 Oracle PrepareExecute 过程名和五资产公开消费者。所有服务器均为本地合成 peer，既不执行真实 SQL，也不证明真实 reset；Oracle/MySQL 模式分别报告认证状态和身份。跨借用者结论是驱动是否按规则销毁/复用，不是实库状态污染已消除。

原始报告保留于 `TestResults/session-state/`，日志为根目录 `TestResults-session-state-*.log`：

| 报告 | 过程结果与处理 |
| --- | --- |
| initial-red.trx | 实际 12 失败 / 2 通过：8 个遗漏及 4 个 Prepare/拒绝误标记 |
| initial-green.trx | 12 通过 / 2 失败；两例 Prepare 测试缺少 statement close 后的 BeginCommand，修正 fixture 序号 |
| focused-first.trx / focused-green.trx | 前次 92 通过 / 22 失败来自 Oracle peer 状态/时区报告；修正合成握手及状态报告后 114 通过 |
| lock-identifiers-red.trx / review-red.trx | 3 失败 / 12 通过；扩大为 6 失败 / 96 通过，覆盖真实 A/B 标识符复用及 raw-XA 识别；修复调用括号判断后专项 126 通过 |
| relation-identifiers-red.trx | 6 失败 / 106 通过：表名/限定表名/CTE 列清单误判；增加声明上下文 |
| relation-identifiers-green.trx / boundaries-green.trx | 前次 136 通过 / 2 失败源于零输出 PrepareExecute fixture 错报输出 flag；修正为 flags=0 后 138 通过 |
| quoted-lock-red.trx | 实际 2 失败 / 22 通过；修复 quoted 锁调用的保守识别 |
| unit-before-expression.trx | 上述阶段完整单测 4182 通过，退出 0；不是表达式 FROM 修复后的最终报告 |
| expression-lock-red.trx | 实际 4 失败 / 117 通过，包含嵌套 TRIM/SUBSTRING 及 A/B；删除过宽的 FROM/JOIN/UPDATE 排除 |

独立 review_session_state 提出的锁名称作为普通标识符、表/CTE 列清单误判和表达式 FROM 漏判均经实际 RED 修复；最终源码/生命周期复核无剩余 Critical/Important，审阅者未运行测试。Minor：嵌套 CTE 的声明名恰与锁函数同名仍可能保守销毁，已在源码 shortcut 和契约公开；不因此新增完整 SQL 语法解析器。任意函数/触发器副作用、完整资源/XA/并发及真实 reset 验收仍开放。

最终门槛：完整单测 unit-verified.trx 实际 4188 通过，0 失败/跳过，退出 0；相对上一模块实际新增 141 项、未移除旧用例。DI 14 通过，Integration 9 通过/72 个单独 NotExecuted，均退出 0。完整消费者 consumer-verified.trx 实际 2408 通过，0 失败/跳过，退出 0；相对上一模块新增 20 项（五资产 × 双模式 × 同步/异步），无旧例移除。所有资产仅在 .NET10 host 加载，不等于五种独立运行时。最终 Release 构建 0 警告/0 错误；11 文件格式验证退出 0，保留 workspace-load warning。本地 pack --no-build --no-restore 退出 0，两个包各五个 DLL SHA-256 和当前 README 匹配，记录见 package-validation.json；未发布，未证明独立运行时。red-green-validation.json 已核对九份原失败/fixture 报告中的失败用例均在最终完整单测中通过，原报告保留。真实 OceanBase/OBProxy、平台和独立运行时不因本地子模块关闭；23 个缺口、八工作包和完整驱动目标均保持开放。

文档检查：9 份 Markdown、232 个相对链接/锚点、围栏、三张 23-ID 表及八工作包唯一覆盖通过；15.1—15.23、audit 13—16 和此前 dated progress 保留。六份修改文档为 UTF-8 无 BOM/CRLF；diff 检查通过，Git 对三个测试文件的 LF→CRLF 提示保留。

### 15.25 2026-10-25 跨命令 statement cache

开发起点 `6d399fd`。新增所有五资产的 `Statement Cache Size`（0..1024，默认 0），并保持 options 的端点/凭据副本、配置序列化和池身份传播。每个物理会话有可按精确绑定后 SQL 借出的 idle LRU；命令持有期间不进入淘汰集合，归还才进入缓存，重复键和超容量走原关闭队列。实现范围及资源上限见 [API 契约](obconnector-dotnet-architecture-design.md#prepared-statement-cache)。

独立缓存 epoch 与服务器 reset generation 分离：本地 DDL/会话/不透明目标发送、成功 ChangeDatabase 使 idle 及尚被命令持有的旧上下文失效；发送前取消、Prepare 和元数据不提前清缓存。reset/断线忘记旧 ID，避免向 reset 后的复用会话发送 stale close。SchemaOnly/KeyInfo 的新准备、辅助目录及 Oracle PrepareExecute owner 保留原生命周期；未读响应销毁会话、执行错误不进入 idle cache，且没有执行 replay。

过程证据保留于忽略目录 `TestResults/statement-cache/` 和根目录 `TestResults-statement-cache-*.log`：initial-red 实际 9 失败（配置未知）；接通配置、修正测试构造后 behavior-red 9 失败/1 通过（旧关闭/重复准备路径）；initial-green 8 通过/2 失败来自 MySQL fixture 使用了 Oracle 命名参数，修正为位置参数后 behavior-green 10 通过；lifecycle-first 21、ownership-first 29 通过。首次完整构建有一处 DbCommand.FetchSize 测试静态类型编译错误，修正为现有 SetFetchSize 后第二次构建 0 警告/错误。boundary-first 41 通过/2 超时来自未读响应 fixture 未提供首行，补首行后 boundary-green 43 通过；保持真实未读终止响应与 socket 销毁断言。五资产专项消费者 20 通过，涵盖两模式/同步异步、复用及 DDL 失效。

以下完整结果为发现租约窗口前的历史验证，不验证后续修复；原报告已另存为 before-epoch-race-*。独立 review_statement_cache 已完成源码/设计及测试/契约走查，无 Critical/Important；一项 Minor 文档措辞已修正为“本地发送前拒绝不使缓存失效”，明确服务端拒绝已发送 DDL 仍保守失效。审阅者未运行构建或测试。最终 Release 构建 0 警告/错误，14 文件格式验证退出 0（保留 workspace-load warning），完整单测 before-epoch-race-unit-verified.trx 实际 4235 通过、0 失败/跳过、退出 0；相对上一模块实际新增 47 项且无旧用例移除。DI 14 通过；Integration 9 通过/72 个单独 NotExecuted，均退出 0。完整消费者 before-epoch-race-consumer-verified.trx 实际 2428 通过、0 失败/跳过，退出 0，耗时 10 分 12 秒；相对上一模块实际新增 20 项且无旧用例移除。全部五资产均在同一 .NET10 host 加载，不是五种独立运行时；期间未重建产品/消费者 DLL。此段记录竞争修复前的阶段证据。合成对照每轮 8 个新命令，每次 prepare 人为延迟 15 ms：两模式均断言 prepare 从 8 次降为 1 次；最终 TRX 的整个测试时长为 MySQL 141.84→26.81 ms、Oracle 158.71→20.00 ms。修复前原始明细在 before-epoch-race-synthetic-delay-comparison.json；包含握手/fixture 开销，不能写成真实 SQL 延迟收益。当时本地 pack --no-build --no-restore 退出 0，before-epoch-race-package-validation.json 核对两个包各五资产 DLL 哈希和当时 README 匹配；未发布。before-epoch-race-red-green-validation.json 已将四份原始 RED/fixture 报告的失败用例对应到最终单测通过，原报告保留。

提交前新增的真实线程交错回归先暂停命令 A 的参数验证，使其随后等待连接锁；命令 B 在该锁内重入并实际发送 DDL，再允许 A 获取会话。epoch-race-red.trx 同步/异步实际 2 失败：应发送 PREPARE(22)，却继续发送旧 ID 的 LONG_DATA(24)。根因是 Start 的 epoch 检查早于独占租约，租约内原 helper 只复核数据库名。统一改为 InvalidatePreparedContext，在 EnsurePrepared/Async 已持有租约时复核 Database、Generation、CacheEpoch，不匹配则关闭旧 ID 并重新准备；不重放执行。epoch-race-green.trx 实际 49 项缓存专项通过。独立 reviewer 再次核对产品、关闭/reset 和交错可信性，无剩余可操作 Critical/Important/Minor，未运行测试。

修复后最终完整单测 unit-verified.trx 实际 4237 通过、0 失败/跳过，退出 0；相对上一模块实际新增 49 项、无旧例移除。完整消费者 consumer-verified.trx 实际 2428 通过、0 失败/跳过，退出 0；新增 20 项、无旧例移除，含全部五资产缓存消费者。DI 14 通过；Integration 9 通过/72 个单独 NotExecuted，退出 0，未启用真实数据库。最终 Release 构建 0 警告/0 错误；原先 4235/2428 的报告保留为 before-epoch-race-*，不冒充修复后报告。期间未重建消费者加载的程序集；五资产仍只在 .NET10 host 运行。最终本地 pack 退出 0，package-validation.json 核对两个包各五个 DLL 哈希及嵌入 README 与最终构建/工作区匹配；未发布。red-green-validation.json 重新核对五份原始失败报告（含竞争 RED）的失败用例均在最终完整单测通过，原报告保留。首次新增 15 文件格式检查退出 2，有一处新测试空白诊断，修正后退出 0，workspace-load warning 保留。合成时长只描述修复前原始阶段，不能替代真实数据库测量。

本次命令从仓库根目录执行；测试均使用 `-c Release --no-build --no-restore`，TRX 使用 `--logger 'trx;LogFileName=<报告名>' --results-directory TestResults/statement-cache`。报告不是新的真实服务器 profile。

| 命令/检查 | 退出码与产物 |
| --- | --- |
| `dotnet build OceanBaseConnector.slnx -c Release --no-restore` | 0；`TestResults-statement-cache-build-final.log` |
| `dotnet test tests/OceanBaseConnector.UnitTests`，附上述公共参数 | 0；`unit-verified.trx`，4237 通过、0 失败/跳过 |
| `dotnet test tests/OceanBaseConnector.ConsumerTests`，附上述公共参数 | 0；`consumer-verified.trx`，2428 通过、0 失败/跳过 |
| `dotnet test tests/OceanBaseConnector.DependencyInjection.Tests`，附上述公共参数 | 0；`di.trx` |
| `dotnet test tests/OceanBaseConnector.IntegrationTests`，附上述公共参数 | 0；`integration.trx`，72 项未执行，未 opt-in 实库 |
| `dotnet format whitespace OceanBaseConnector.slnx --no-restore --verify-no-changes --include <本模块15个C#文件>` | 0；`TestResults-statement-cache-format-final.log`，workspace-load warning 保留 |
| `dotnet pack OceanBaseConnector.slnx -c Release --no-build --no-restore -o TestResults/statement-cache/packages` | 0；`TestResults-statement-cache-pack-final.log`、`package-validation.json` |

本模块的本地结果不关闭真实 DDL/reprepare、外部副作用、资源/负载、OBProxy、跨平台或独立运行时；23 个 ID、八工作包及历史实库失败和跳过保持开放。

### 15.26 2026-10-25 ambient 单资源事务开发与完整文档检查点

**状态：开发中，未提交，不关闭 P0-03/04/05 或 W2。** 当前 HEAD 为 `f15716e`；以下 PSPE 行为属于其上的工作区。本文此次交付为走查文档，保留现有实现和原始报告，不把未完成模块记为正式交付。

#### 发现、影响和当前处理

| 发现 | 触发场景和影响 | 当前处理 / 剩余验收 |
| --- | --- | --- |
| 原 volatile 登记不能限制数据库资源数 | 同一 ambient 事务加入两条数据库连接，分别本地提交，无法保证整体原子性 | 工作区采用 `IPromotableSinglePhaseNotification`，协调器接受单资源后才初始化；第二资源/提升发送事务 SQL 前拒绝；本地双模式测试已有，真实事务管理器验收仍缺 |
| 原通知异常处理会掩盖提交未知结果 | `Prepared()` 同步触发提交并抛异常后，catch 再调用 `ForceRollback`，可能用非法第二次通知覆盖原异常 | 工作区按确认提交/已知拒绝/未知提交报告 Committed/Aborted/InDoubt；通知放在数据库操作 catch 外；保留恢复失败后已确认提交的结果，不重放 SQL |
| Initialize 与 Close/Open 交错会误用新会话 | 先验证 A，等待协调器期间关闭并打开 B，再 Initialize 时错误启动 B 的事务 | 独立复核发现并以真实线程交错复现双模式 2 RED；工作区在原登记时捕获 Open generation，并在取得事务 exchange 的同一锁内复核；晚回调另校验原本地事务实例；池 A/B 断言复用同一物理会话 |
| **自动登记初始化忽略 OpenAsync 取消令牌（Important，未修复）** | OpenAsync 发布会话后调用同步 EnlistTransaction；Initialize 到 BeginAmbientTransaction 使用 `CancellationToken.None`。控制 SQL 等回包时取消调用者 token 无法中断该阶段；Command Timeout=0 时缺少有限命令截止时间 | 源码调用链已核实，尚无该位置的专门 RED/GREEN。最小修复：自动登记内部路径传递本次 opening token，公开同步 EnlistTransaction 保持原签名；需双模式阻塞回包取消、调用者 token、失败会话销毁、无池/协调器槽泄漏及无 replay 回归，并覆盖五资产消费者 |
| scope timeout 不是初始化 wire cancellation | 协调器超时发生在同步 Initialize 等待期间，回滚可能等待 Initialize 返回 | 现有测试只证明返回后最终回滚且无 replay，不能声称立即取消网络等待；CommandTimeout 与 scope timeout 独立，修复调用者取消后仍需保留此区别 |

关键入口为 `OceanBaseConnection.cs` 的 Open/OpenAsync/EnlistTransaction、`OceanBaseConnection.Transactions.cs` 的 BeginAmbientTransaction/StartTransaction，以及 `Internal/Session/AmbientEnlistment.cs`。现有新增测试为 `AmbientTransactionTests`、`AmbientTransactionLifecycleTests`、`AmbientTransactionConcurrencyTests`、`AmbientTransactionFailureTests`、`AmbientTransactionPoolTests` 和消费者 `AmbientTransactionConsumerTests`。API 与终态表见 [ambient 契约](obconnector-dotnet-architecture-design.md#ambient-transactions)。

#### 已有本地证据（本次读取报告，不是本次重新运行）

报告位于忽略目录 `TestResults/ambient-single-phase/`，日志位于根目录 `TestResults-ambient-single-phase-*.log`；不会随 Markdown 自动分发。需要交接完整原始证据时须另行归档这些产物。

| 报告 / 检查 | 实际结果与边界 |
| --- | --- |
| `initial-red.trx` → `initial-green.trx` | 原始 6 失败保留；首轮 10 通过（含原 4 项） |
| `lifecycle-first.trx` | 32 通过 |
| `initialization-race-red.trx` → `initialization-race-green.trx` | 原始双模式 2 失败保留；修复后专项 34 通过 |
| `failure-first.trx` → `pool-green.trx` | 44 → 52 通过；含原 4 项，新增 48 项，覆盖命令超时、scope 延迟回滚、busy reader、回滚丢包、池 A/B 身份 |
| `unit-verified.trx` | 全量 4285 通过、0 失败/跳过；相对缓存模块 4237 实际新增 48 项，无旧例移除；这不覆盖上述未测取消缺口 |
| `consumer-focused.trx` | 专项 40 通过，五资产 × 两模式 × 同步/异步 × 确认/丢失提交；通过公共 API 操作实际两个 TCP fixture，验证第二资源无事务 SQL、首资源精确一次且无 replay；不是全量消费者 |
| `di.trx` | 14 通过、0 失败/跳过 |
| `integration.trx` | 9 通过；81 个结果中 72 个单独 NotExecuted，未启用实库。Counters 的 notExecuted=0 不代表没有跳过，须检查每个 UnitTestResult |
| `TestResults-ambient-single-phase-build-first.log` | 已有 Release solution build 0 警告/0 错误，五资产构建；不是独立运行时测试 |
| `TestResults-ambient-single-phase-format-first.log` | 已有 9 个 C# 文件 scoped whitespace 检查，保留 workspace-load warning |

以上测试使用 `dotnet test <测试项目> -c Release --no-build --no-restore --logger 'trx;LogFileName=<报告名>' --results-directory TestResults/ambient-single-phase`；专项另带过滤器。构建使用 `dotnet build OceanBaseConnector.slnx -c Release --no-restore`。报告反映当时构建产物，不验证后续修改；修复取消传播后必须重新构建并验证受影响资产。

独立 `review_ambient_single_phase` 已完成源码/测试/契约走查，未运行测试：初始化 generation 竞态已复现修复，rollback 测试已加强为精确 ROLLBACK 报文；当前剩余 Important 为自动登记取消传播。完整消费者、取消修复后的最终构建/格式/单测/DI/Integration、最终独立复核、两包五资产哈希及嵌入 README 验证均未完成，不能写成模块已通过交付门槛。上一模块 2428 消费者及旧包只验证其自身版本，不替代本模块。

#### 下一步和关闭标准

1. 在自动 OpenAsync 的 Initialize 已到服务器且响应暂停时取消 token，先取得双模式实际失败；有界清理 fixture，不能靠放行响应后才断言取消。
2. 在自动登记共享路径传播 opening token，保留 Open generation、原事务实例和通知一次的保护；证明取消时连接和会话终态、池容量及协调器槽无泄漏，且没有 SQL 重放。
3. 修复后完成五资产专项和全量消费者、完整单测/DI/Integration、构建与必要格式检查、独立源码/测试/文档复核、包哈希及 README 对照；保留所有原始失败/跳过，再记录模块交付。
4. 本地模块完成后，继续双模式/版本/OBProxy/事务管理器真实故障和应用结果核对/重建示例；单资源 PSPE 不提供 durable prepare、跨数据库原子提交或分布式恢复。五资产均在 .NET10 host 加载，Windows 本地 fixture 不证明独立运行时或跨平台互操作。

本次文档检查同步总体摘要、23-ID 表、详细追踪、W2、README、兼容性、API 和进度台账；第 15.1—15.25 节原文保留。真实 OceanBase 既有 Oracle nested-table column 失败、其他历史失败和未执行项继续保留，八工作包均未整体关闭。

本次文档验收：9 份 Markdown 的 255 个相对链接/锚点有效；三张表各完整覆盖 23 个 ID，八工作包无重复且覆盖全部缺口；第 15.1—15.25 节与 HEAD 原文一致；六份改动文档保持 UTF-8 无 BOM/CRLF。`git diff --check` 无空白错误，现有 AmbientEnlistment.cs 的 LF→CRLF 提示保留。本次只做文档/报告结构检查，未重跑构建、测试或打包。检查产物为忽略目录 `TestResults/ambient-single-phase/document-validation.json`。

### 15.27 2026-10-25 ambient 单资源事务与初始化取消本地交付

开发起点 `f15716e`。完成 P0-03 的本地单资源登记及 P0-04/05 的失败/回调边界，不关闭整个 W2 或真实故障验收。第 15.26 节和走查报告第 20 节为修复前检查点，保留其原始 4285/40 报告及当时未关闭的取消问题；以下记录修复后的最终资产与结果。

实现采用 `IPromotableSinglePhaseNotification`：协调器接受资源后才开始本地事务，第二数据库资源在其本地事务 SQL 前拒绝；后续 promotion 明确拒绝并中止原本地事务；clone 登记幂等。可信 COMMIT 确认/拒绝/响应丢失分别报告 Committed/Aborted/InDoubt，确认提交后恢复 autocommit 失败仍保留 Committed，失败物理会话丢弃；通知在数据库操作 catch 外，仅报告一次。Initialize 捕获原 Open generation，并在获取事务 exchange 的同一锁内复核；完成回调再要求原本地事务实例，不能影响 Close/Open 或池的下一借用者。没有 durable prepare、跨资源原子提交、分布式恢复或 SQL replay。发送前拒绝只针对第二资源自己的本地事务 SQL，不能解读为后续 promotion 发生时原事务尚未发 SQL；提升失败仍须中止原本地事务。

独立复核新增的 Important 已闭合：自动 Open/OpenAsync 通过私有登记入口把 `context.Token` 送至 Initialize 的同一事务 exchange；公开同步 EnlistTransaction 保持原签名且使用 None。取消中断控制响应后，BeginFailed、登记 catch 与 FailedOpen 依次清理本地/ambient 状态、销毁旧会话并释放池容量，OpenAsync 对外保留原 caller token。成功 Open 结束 exchange 注册后，后续 commit/rollback 不使用 opening token。scope timeout 仍不能立即中断同步 Initialize，它与 CommandTimeout/调用者取消是不同机制。[公共契约](obconnector-dotnet-architecture-design.md#ambient-transactions)保留此边界。

原始过程报告保留在忽略目录 `TestResults/ambient-single-phase/` 和根目录 `TestResults-ambient-single-phase-*.log`：initial-red 实际 6 失败；initialization-race-red 实际双模式 2 失败，修复后专项 34 通过；failure-first 44、pool-green 52 通过。取消回归首次因测试把 scope Dispose 排在断线清理之前而等待初始化，手工中止并保留 initialization-cancellation-interrupted.log，确认无遗留测试进程后调整有界清理顺序；不计为产品通过或正常 RED。之后 initialization-cancellation-red 实际 4 TimeoutException：双模式 × 两条初始化控制 SQL，在服务器确实收到请求后取消，原路径仍无法在 3 秒内退出。修复后 initialization-cancellation-green 为相关 ambient 73 项通过。

新增五资产取消消费者 10 项（双模式）真实依次接受两个物理 TCP 会话：`Command Timeout=0`、池最大容量 1/等待 1 秒，第二初始化 SQL 已到达但不回复；取消后断言原 token/Closed，再用同一 scope、CommittableTransaction、连接及池配置重开，完成初始化和精确业务 SQL，随后 Close 的 ROLLBACK/restore/reset、晚到 rollback 及 ClearPool→EOF。它实际验证协调器槽和池容量释放；不是只有状态断言。consumer-cancellation-focused 共 50 项通过（原 40 + 新 10）。另有双模式成功 Open 后取消并 Dispose caller CTS 再 COMMIT 的回归，证明后续事务不受 opening token 生命周期影响。

独立 review_ambient_single_phase 完成产品、生命周期、真实交错、五资产消费者和取消契约复核，无剩余可操作 Critical/Important/Minor；审阅者未运行构建或测试。两次 Important（初始化代次竞态、取消传播）均有实际 RED 后修复，Minor rollback 测试已改为精确 ROLLBACK 报文断言；最终文档复核另区分第二资源发送前拒绝与后续 promotion 回滚，并明确初始化失败释放资源后允许同 scope 重试，成功登记后 Close 的重开限制仍保留。所有原始失败均保留并对应最终通过；未改弱现有失败断言或删除旧用例。

| 最终命令 / 产物 | 结果与证据范围 |
| --- | --- |
| `dotnet build OceanBaseConnector.slnx -c Release --no-restore` | 0；build-final.log，0 警告/错误，五产品资产及 DI 资产均编译 |
| `dotnet test tests/OceanBaseConnector.UnitTests` | 0；unit-final.trx，4291 通过、0 失败/跳过；相对 4237 实际新增 54 项，无旧例移除；原 unit-verified.trx 的 4285 为取消修复前历史 |
| `dotnet test tests/OceanBaseConnector.ConsumerTests` | 0；consumer-final.trx，2478 通过、0 失败/跳过；相对 2428 实际新增 50 项，无旧例移除，包含所有 ambient 50 项；运行期间未重建被加载程序集 |
| `dotnet test tests/OceanBaseConnector.DependencyInjection.Tests` | 0；di-final.trx，14 通过、0 失败/跳过 |
| `dotnet test tests/OceanBaseConnector.IntegrationTests` | 0；integration-final.trx，9 通过/72 个单独 NotExecuted，未启用真实数据库；按每例结果核对，不依赖汇总 notExecuted 属性 |
| `dotnet format whitespace OceanBaseConnector.slnx --no-restore --verify-no-changes --include <本模块9个C#文件>` | 0；format-final.log，保留 workspace-load warning |
| `dotnet pack OceanBaseConnector.slnx -c Release --no-build --no-restore -o TestResults/ambient-single-phase/packages` | 0；pack-final.log；package-validation.json 校验两包各五资产 DLL 哈希和嵌入 README 与最终构建/工作区一致；未发布 |
| 文档 / 结果追踪 | document-validation.json 核对链接、三张 23-ID 表、八工作包和历史章节；red-green-validation.json 核对实际新增/移除与三份原始 RED 映射；git diff --check 无空白错误 |

测试从仓库根目录执行，附公共参数 `-c Release --no-build --no-restore --logger 'trx;LogFileName=<报告名>' --results-directory TestResults/ambient-single-phase`；专项再带过滤器。表内日志名均以前缀 `TestResults-ambient-single-phase-` 位于根目录，TRX/JSON 位于忽略结果目录；需要独立交接时另行归档，不因提交 Markdown 自动分发。

五资产均在同一 .NET10 host 运行，不是五种独立运行时。无新增真实 OceanBase/OBProxy/事务管理器、跨平台或长期负载报告；P0-03/04/05 仍须真实故障/结果核对和应用重建验收。既有 Oracle nested-table column 失败及全部历史跳过继续保留；23 项和八工作包仍开放。

### 15.28 2026-10-25 复杂类型可观察 DDL 自动失效

开发基线 `96f544d`。本模块补 P1-07 的自动失效边界；已有 `ClearComplexTypeCache` 显式 API 保留。物理会话增加独立 complex-metadata epoch，在 Oracle 文本 CREATE/ALTER/DROP/RENAME/TRUNCATE 目标请求开始发送时前进一次，覆盖允许批次中的后续 DDL，失败的 DDL 也保守失效。复用原 SQL scanner 的注释、引号和程序单元边界，不新建 SQL 解析器；与 statement-cache 开关/epoch 独立，默认 cache=0 也生效。

绑定验证、上下文创建、字典完成发布及新 exchange 的字典快照均检查该 epoch。旧 named/nested/匿名工厂描述和已有参数值不能重新绑定；已返回值的 immutable data/layout 仍可读取。重新 `GetComplexType` 后重建值；不自动加载目录或重放 SQL。Prepare、SchemaOnly、目录/控制查询、参数校验失败及发送前取消不使类型失效。其他连接或不透明 procedure/BEGIN/DECLARE/function/trigger 中的 DDL 仍需应用显式清理，见[公共契约](obconnector-dotnet-architecture-design.md#complex-type-ddl-invalidation)。

**RED/GREEN 与历史失败：** 最初测试编译时把 `DbParameter` 当作 provider 参数，修正后取得首轮 8 失败；其中 4 个是脚本漏重置包序号，另 4 个已复现旧描述未拒绝。修正脚本后 `initial-red-confirmed.trx` 的 8 例均为旧描述构造未抛异常；产品修复后首轮专项 25 通过。相关回归的 150 通过/2 失败是测试未考虑结果读取错误被包装为 OceanBaseException；随后 151 通过/1 失败为客户端按预期断线后脚本仍写 EOF 的竞态。测试预期及脚本修正后均进入最终全量通过报告；原报告保留，不把这些夹具失败写成服务端问题。

| 门槛 | 命令 / 产物 | 当前证据 |
| --- | --- | --- |
| Release 构建 | `dotnet build OceanBaseConnector.slnx -c Release --no-restore`；`TestResults-complex-type-ddl-build-final.log` | 退出 0，0 警告/错误；五种产品资产和相关测试入口均构建 |
| 全量单测 | `dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-restore --no-build --logger 'trx;LogFileName=unit-final.trx' --results-directory TestResults/complex-type-ddl` | 退出 0，4347 通过/0 失败/0 跳过；相对上模块 4291 实际新增 56、移除 0 |
| 五资产专项消费者 | 同消费者项目以 `FullyQualifiedName~EveryAssetLoadsExactComplexDictionary` 过滤；`consumer-focused.trx` | 退出 0，40 通过，扩展现有用例，覆盖五资产 × 同步/异步 × 普通/OB20 × 本地明文/TLS；DDL 后旧值拒绝、快照可读、目录布局改变/重新加载及显式清理均检查 |
| 完整消费者 | `dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-restore --no-build --logger 'trx;LogFileName=consumer-final.trx' --results-directory TestResults/complex-type-ddl` | 退出 0，2478 通过/0 失败/0 跳过，10 分 32 秒；没有新增/移除用例，40 例增加 DDL 场景；期间 11 个加载程序集的哈希/时间戳均未变 |
| DI | 对应测试项目 `--no-build --no-restore`；`di-final.trx` | 退出 0，14 通过/0 失败/0 跳过 |
| 集成入口 | 对应测试项目 `--no-build --no-restore`；`integration-final.trx` | 退出 0，9 配置测试通过/72 个单独 NotExecuted；未启用真实数据库 |
| 格式 | `dotnet format OceanBaseConnector.slnx --no-restore --verify-no-changes --include <10 changed C# files>`；`TestResults-complex-type-ddl-format-final.log` | 退出 0；保留 workspace-load warning |
| 本地包 | 两个产品项目 `dotnet pack -c Release --no-build --no-restore -o TestResults/complex-type-ddl/packages`；`package-validation.json` | 两包各五 DLL 哈希与构建输出匹配，嵌入 README 与工作区完全一致；未发布 |

单测覆盖默认/启用 statement cache、准备/直接执行、服务端拒绝、发送前校验/取消、Prepare-only、普通/不透明 SQL、注释/引号/多语句、嵌套/匿名类型、DDL 后新结果不继承旧布局，以及 Batch 后续旧绑定发送前拒绝。独立 reviewer 源码/测试走查无 Critical/Important；最终契约复核发现一处 Minor：第 4.1 节活跃摘要仍把外部 DDL 失效写成缺失，现已同步为自动/显式失效和真实验收边界，没有剩余可操作问题。reviewer 未运行测试。完整消费者已退出 0；`result-validation.json` 按结果明细及名称/ID 的多重集核对 56 新单测、0 移除和 8 RED 最终通过，避免重复显示名称被去重；最初辅助脚本按名称字典计数的错误已修正。提交前继续核对最终文档和包证据。

产物位于忽略目录 `TestResults/complex-type-ddl/` 与根目录 `TestResults-complex-type-ddl-*.log`，独立交接时须另行归档。本地 fixture 不证明真实 ALTER TYPE/目录/隐式提交/锁/版本或 OBProxy 行为，五资产在同一 .NET 10 host 运行，不是五个独立运行时验收。P1-07/W5 仍保留 LOB/cursor/national 成员及真实 DDL/资源验收；全部 23 项/八工作包和既有 Oracle nested-table column 失败/历史跳过保留。

### 15.29 2026-10-25 MySQL 过程开发与完整文档检查点

**状态与范围：** 已提交基线 `master` / `1bb13cb`；工作区过程实现及测试未提交。本次仅整理完整文档，不把在研代码计入已交付，不关闭 P1-05/W5 或其他 23 项。全部八工作包、历史交付、失败和跳过保持原义。Java HEAD 本次静态核对仍为 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`，不包含 Java 构建或负载验证。

**工作区源码与使用者契约：**

| 内容 | 当前开发行为及影响 | 入口 |
| --- | --- | --- |
| 过程名与参数 | 解析一/二段名称并重新引用；按集合顺序绑定 Input/Output/InputOutput，名称不重排；ReturnValue 拒绝，函数使用文本 SELECT | `Internal/Sql/MySqlCommandPlan.cs`、`OceanBaseCommand.cs` |
| 协议与限制 | 要求有效 PS_MULTI_RESULTS；显式 Prepare 可用、禁止客户端插值 fallback；FetchSize、SchemaOnly、KeyInfo 拒绝；Input stream 走 long-data，INOUT stream 拒绝 | `OceanBaseCommand.cs`、`MySqlProcedureLifecycleTests.cs` |
| 输出与互斥 | 每次执行以临时 prepared SET 初始化私有变量，prepared CALL 后隐藏 SELECT 取输出；普通结果与输出抓取占同一 exchange；一行完整成功后统一发布，失败保留旧值且无重放 | `MySqlCommandPlan.cs`、`QueryResult.MySqlProcedure.cs`、`CommandExchange.cs` |
| 输出类型 | 按私有 SELECT 的实际列元数据解码，不强制转成参数声明 DbType；MySQL user variable 的时间/字符串语义需调用方知晓 | [API 草案](obconnector-dotnet-architecture-design.md#mysql-stored-procedures-development-checkpoint) |
| reader / Batch / 池 | MySQL 过程 Close/Dispose 排空普通结果并取输出，因此可发生网络等待/超时/异常；Batch 关闭统计当前过程，不执行后续命令；会话归还时退休，CALL 不进入 idle cache | `OceanBaseDataReader.cs`、`OceanBaseBatchReader.cs`、`OceanBaseBatchCommand.cs` |
| DDL 边界 | MySQL CREATE PROCEDURE/FUNCTION/TRIGGER/EVENT、OR REPLACE 和 DEFINER 的程序体分号按一个程序单元识别，具体语法/尾部仍由服务器验证；最终语法复核未完成 | `Internal/Sql/SqlStatementBoundary.cs`、`MySqlProcedureTests.cs` |

初始化 SET 已属于发送 SQL；之后断线可能保守报告 unknown outcome，即使尚不能证明业务 CALL 已发送。输出发布不等于事务提交；不能根据异常标志盲目重放过程。提前关闭行为与 Oracle 现有策略不同，必须在公共契约中说明。

**已生成报告的复核结果（本次没有重跑）：** 原件均在忽略目录 `TestResults/mysql-procedure/`，日志在根目录 `TestResults-mysql-procedure-*.log`；跨工作区交接需另行归档，Git 文档不包含原始 TRX。

| 报告 | 结果 | 解释与有效范围 |
| --- | --- | --- |
| `initial-red.trx` | 16 失败 | 原 MySQL StoredProcedure 入口拒绝 |
| `focused-green.trx` | 16 失败 | 初版夹具未声明 PS_MULTI_RESULTS；不能计为产品通过 |
| `focused-green-2.trx` | 8 通过 / 8 失败 | 提前关闭未排空响应；保留失败记录 |
| `focused-green-3.trx` | 16 通过 | 基础执行/输出/关闭矩阵，早于后续新增测试 |
| `ddl-boundary-red.trx` | 3 失败 | 过程体分号被误判多语句 |
| `definer-red.trx` | 3 通过 / 4 失败 | DEFINER 语法识别不完整 |
| `batch-close-red.trx` | 2 通过 / 2 失败 | Batch 提前关闭未刷新最终 RecordsAffected |
| `lifecycle-2.trx` | 35 通过 | 当时生命周期子集，不能覆盖最新新增测试 |
| `related-1.trx` | 53 通过 | 含过程、DDL/DEFINER、Batch 与相关 Oracle 选择集；不等于全量单测 |
| `consumer-focused.trx` | 40 通过 / 40 失败，共 80 | 五资产 × 同步/异步 × OB20/plain × TLS/plaintext × EOF 两形态；失败全部为 encrypted=true，本地 PKCS#12 导入抛 CryptographicException，尚未进入 TLS/过程交换。成功部分也不是实库证明 |
| `real-mysql-initial.trx` | 4 失败 | 初始创建未成功，清理不存在对象掩盖主因；保留原报告 |
| `real-mysql-diagnosis.trx` | 4 失败 | 明确定位创建过程被发送前多语句检查拒绝 |
| `real-mysql-2.trx` | 4 通过 | 直连 OceanBase 4.4.2.2/MySQL，Windows/.NET10、TLS disabled；当时测试仍显式开启 AllowMultiStatements，后续移除此设置尚未重跑 |

已读取的 `TestResults-mysql-procedure-build.log` 记录 Release build 0 警告/0 错误，但早于最新单测/Integration 修改。曾有测试编译错误（DbCommand.FetchSize 与 PacketChannel.Dispose 调用不匹配），修正后才取得前述构建；原日志保留。初步独立走查发现的 Batch 行数和 DEFINER 缺口已有局部修复及报告；扩展后的源码、测试与文档尚缺最终增量复核，不能视为最终审查批准。测试证书失败的根因尚未在本检查点修复或验证，不把它推定为数据库不兼容或已解决的环境问题。

**尚未闭合的交付条件：**

1. 编译并运行最新初始化错误/参数数目、capability/fallback、SingleRow/SingleResult 与池退休测试；核对设置 SQL、取消、超时、释放和 unknown outcome 的边界。
2. 处理 TLS 夹具证书加载失败，重跑五资产专项消费者；在最终代码冻结后执行完整单测、完整消费者、DI、Integration 与格式检查。消费者期间不得重建其加载资产；五 TFM 资产仍不是五种独立运行时。
3. 对扩展 DEFINER 扫描、输出响应验证、提前关闭与 Batch 行数取得最终独立复核；已有局部 review 不替代最终 review。
4. 重跑移除 AllowMultiStatements 后的四个真实过程用例及适当的 MySQL 实库套件。Integration 旧的“过程必拒绝”改为不存在过程的 1305 确认错误、错误的 SchemaOnly 拒绝改为未知 behavior bit128，两处是待重验的过时断言修正，不能静默移除失败。
5. 最终构建、两包/五资产哈希与嵌入 README 校验后再作模块交付；不得套用第 15.28 节的全量/包记录。OBProxy、真实 TLS、独立平台/运行时、故障/负载/资源与完整驱动验收继续开放。

本轮保留现有全部实现/测试修改，仅完成文档更新和结构/链接/证据复核；没有新增测试执行、打包、真实环境验收、提交或发布。历史 MySQL 60 通过、Oracle 79 通过/1 失败与 cleanup failure 不因过程专项 4 通过而被替换。当前过程模块与整个驱动均未完成。


本次文档校验：九份 Markdown 的 310 个本地链接/锚点有效；三张完整 23-ID 表与八工作包归属检查通过。第 15.1—15.28 节及走查报告第 12—22 节历史正文保持不变；433 个源码/项目文件的编辑前后哈希一致。UTF8 无 BOM/CRLF 检查通过，git diff --check 无空白错误；现有 Integration CommandTests.cs 的 LF→CRLF 提示保留，不代表本次改了该测试。验证摘要位于忽略目录 TestResults/documentation-handoff/validation.json。

### 15.30 2026-10-25 MySQL 存储过程执行本地交付

开发起点为 `master` / `1bb13cb`。MySQL `CommandType.StoredProcedure` 增加一/二段过程名、集合顺序 Input/Output/InputOutput、显式 Prepare、普通多结果及输出收集。每次以短生命周期 prepared SET 初始化私有变量，prepared CALL 后用隐藏 SELECT 获取输出；同一 exchange 保持独占，完整单行成功后才统一发布，失败保留旧值且不重放。输出按 SELECT 元数据解码，不强制转换为参数 DbType。ReturnValue、FetchSize、SchemaOnly/KeyInfo、INOUT stream 拒绝，要求有效 PS_MULTI_RESULTS；Input stream 沿用 long-data。过程会话归还时退休，CALL statement 仍由 command 独占，不进入 idle cache。

过程 reader 关闭会排空当前普通结果和私有输出，可能等待网络、超时或抛异常；Batch 关闭只排空当前过程并刷新最终行数，不执行后续项。MySQL CREATE 程序单元的内部分号、OR REPLACE 和支持的 DEFINER 形式按单个程序处理，具体语法仍由服务端裁决，不需要开启文本多语句。契约见 [API](obconnector-dotnet-architecture-design.md#mysql-stored-procedures)。初始化 SET 已是发送 SQL，之后断线可能保守标未知；输出查询的确认错误也可能发生在 CALL 已成功之后，不能据此推断没有业务副作用或盲目重试。

**确认的失败与走查修复：** 第 15.29 节的原始过程拒绝、DDL/DEFINER 和 Batch 提前关闭失败保留。增量独立走查发现：旧 Batch reader 关闭后读取 RecordsAffected，会把同一 BatchCommand 后一次执行的行数覆盖为旧值。`batch-stale-reader-red.trx` 双模式 × 同步/异步 4 例实际失败（期望 5、实际 2），修复在 close 的最终刷新后清除 `_current`，以后只读旧 operation 的快照；最终全部通过。又补初始化 SET 等待中的取消/断线/超时、输出列数错误及私有结果 MoreResultsExists，验证无 CALL/重放、输出保留、Broken/discard、exact async caller token 和 TimeoutException。专项 `lifecycle-final.trx` 67 通过。

初始专项消费者 `consumer-focused.trx` 保留 40 通过/40 失败；所有失败都是沙箱账户导入本地 PKCS#12 私钥失败，尚未进行 TLS/过程交换。同一组已构建的 80 用例在常规用户环境 `consumer-environment-check.trx` 全通过，未改驱动、证书夹具或平台信任库。本地自签 TLS fixture 仍不代表真实服务器 TLS 验收。

**最终门禁与原件：** 均使用 Release 资产；原始 TRX/校验 JSON 在忽略目录 `TestResults/mysql-procedure/`，日志为根目录 `TestResults-mysql-procedure-*.log`，需独立交接时另行归档。

| 门禁 | 命令 / 报告 | 本次结果与边界 |
| --- | --- | --- |
| 全量构建 | `dotnet build OceanBaseConnector.slnx -c Release --no-restore`；build-final.log | 0 警告/0 错误。之后仅以 --no-dependencies 重建最终 Integration 断言，不重建消费者所加载的 DLL |
| 全量单测 | UnitTests 项目 `dotnet test -c Release --no-build --no-restore`；`unit-final.trx` | 4414 通过，0 失败/跳过；较 DDL 模块新增 67，旧用例移除 0，按名称/ID 多重集核对 |
| 完整消费者 | ConsumerTests 项目 `dotnet test -c Release --no-build --no-restore`；`consumer-final.trx` | 2558 通过，0 失败/跳过，11 分 15 秒；新增 80、移除 0，按名称/ID 多重集核对；11 个加载程序集哈希和时间戳均未变 |
| DI | DependencyInjection.Tests 同样 no-build/no-restore；`di-final.trx` | 14 通过，0 失败/跳过 |
| 无 opt-in Integration | IntegrationTests 同样 no-build/no-restore；`integration-verified.trx` | 9 配置回归通过，76 个逐项 NotExecuted |
| 默认策略实库专项 | `OB_REQUIRE_INTEGRATION=1`、`OB_INTEGRATION_PROFILES=mysql-direct`，过滤 MySqlProcedure；`real-mysql-default-policy.trx` | 5 通过：4 个同步/异步/提前关闭过程用例加不存在过程的确认错误 1305；未开启 AllowMultiStatements |
| 适用 MySQL 实库套件 | 同 profile，`--filter FullyQualifiedName!~Oracle`；`real-mysql-applicable.trx` | 74 通过（含 9 配置回归）、5 跳过；Windows/.NET10、直连 OceanBase 4.4.2.2、TLS disabled |
| 格式 | `dotnet format whitespace ... --no-restore --verify-no-changes --include <17 C# files>`；format-verified-final.log | 退出 0，保留 workspace-load warning。首次格式诊断退出 1，修正空白后通过，原日志保留 |
| 本地包 | `dotnet pack OceanBaseConnector.slnx -c Release --no-build --no-restore -o TestResults/mysql-procedure/packages`；`package-validation.json` | 退出 0；两包各五 DLL 与构建输出逐字节/哈希一致，嵌入 README 与工作区一致；未发布 |

实库整套首次未排除 Oracle-only 数据源，`real-mysql-final.trx` 记录 73 通过、20 失败、5 跳过：19 个 Oracle-only 用例无所选 profile 数据，另 1 个旧 EnlistTransaction(null) 断言仍期待 NotSupported，现按已实现契约改验 ArgumentNullException。保留原报告，不把 no-data 错误算服务端 SQL 失败；适用套件的显式过滤不证明 Oracle 已通过。本模块另纠正“过程必拒绝”为不存在过程的确认错误 1305，以及把错误的 SchemaOnly 拒绝断言改为未定义 behavior bit128。这三处过时断言及四个新增过程测试均进入最终 Integration 构建和实库/配置报告。

MySQL 实库套件的 5 个跳过分别缺少 autocommitDisabled、unsupportedAuth、alternateDatabase、ob20、trace profile 能力。历史 MySQL 60 通过、Oracle 79 通过/1 失败及 cleanup failure 保留，不以本次专项或 MySQL 适用套件覆盖。首次 16 过程拒绝、16 capability 夹具错误、8 提前关闭、3 DDL、4 DEFINER、2 Batch 行数和 4 旧 reader 失败均按名称对应最终单测通过；无旧测试被删除以获得通过。

独立增量源码/测试复核确认所有已发现 Critical/Important/Minor 均已闭合，reviewer 未运行测试。最终 `result-validation.json` 核对全量结果、名称/ID 多重集、原始失败转通过及冻结程序集；文档复核的旧摘要入口已纠正。五资产只在同一 .NET10 host 消费，不能推定五种独立运行时；真实 TLS/OBProxy/版本/故障/资源/负载及整个 P1-05/W5 仍开放。所有 23 个缺口 ID、八工作包和历史失败/跳过保留，整个驱动未完成。

最终文档复核无剩余可操作问题；九份 Markdown / 309 个本地链接及锚点校验通过，三张完整 23-ID 表和八工作包归属保持一致。第 15.1—15.29 节及走查报告第 12—23 节历史正文不变，UTF8 无 BOM/CRLF 检查通过；git diff --check 无空白错误，两个现有 Integration 文件的 LF→CRLF 提示保留。详见 `document-validation.json`。

### 15.31 2026-10-25 普通池配置归一化

**开发起点：`master` / `c2fe307`；归属 P0-01 / W1。** 内部身份按 builder 的全部有效选项及依赖默认值规范化；顺序、别名、typed spelling、显式默认值共池，所有文本、秘密与策略引用/证书指纹、私有 source 边界保留。统一 registry 领取、清池及移除；解析在 Gate 外，领取/BeginRent 仍原子。Open、失败清理、DetachForClose 和 ClearPool 捕获原始 builder，避免公开序列化丢失 quoted-empty PasswordN；规范串仍可幂等重解析。公开 ConnectionString 不变；契约见第 5.1.4 节及 [API](obconnector-dotnet-architecture-design.md#ordinary-pool-configuration-identity)。

**实际失败与独立走查：** initial-red 为 8 失败/1 通过，initial-green 为 9 通过；boundary 扩展 250 通过。独立 reviewer 发现公开字符串丢失空因子；empty-factor-red 的双模式/同步异步 4 失败已由原始 builder 快照修复。review-green 的 266 通过/1 失败为新私有 source 测试缺 User ID，修正 fixture 后不以修改产品验证绕过。close-boundary-red 的 2 个空因子 discard 清理失败已由 DetachForClose 同步捕获规范身份修复。

close-boundary-red 另外 2 个回调测试原期望“在 Closed 内同步重新领取等效配置且立即成功”；归一化后同池满容量，实际超时。按共享容量契约修订为 `ClosedCallbackCannotBypassEquivalentPoolCapacityAndFailureReturnsOriginalSession`：验证有限超时、finally 归还和 Close 完成后复用，**不把这两例记为放宽容量后的产品修复**。不延后 Closed 事件，因为会破坏既有 Closed logger 重开和异步 rollback 期间重开的事件顺序。`Pool Wait Timeout=0` 可无限等待，公开要求 Close 完成后重开；任意重入/并发仍属于 W1 待验收。最终增量 source review 无剩余可操作问题，reviewer 未运行全量测试。

**已完成验证：** 最终 Release solution build 0 警告/错误；相关 lifecycle-reviewed 292 通过；全量单测 unit-final 4494 通过/0 失败/0 跳过；DI 14 通过；无 opt-in Integration 9 通过/78 逐用例 NotExecuted。实库专项 real-pool-final 6 通过；双模式 Connections 套件 real-connections 26 通过/3 NotExecuted，缺 alternate database、unsupported auth 和 autocommit-disabled 配置。以上实库限已配置 mysql-direct/oracle-direct、Windows/.NET10；没有新增 OBProxy/企业 TLS/PAM 服务/跨平台或独立运行时报告。

**消费者最终门禁：** 完整 consumer-final 2638 通过/0 失败/0 跳过，用时 11 分 30 秒。新增 80 个五资产 × 双模式 × 同步/异步 × 4 场景用例：复用/闲置清池、共享容量取消及借出清池、reset 中清池、公开空因子隔离。与上一过程模块逐项比较 testName/testId 多重集，单测及消费者均实际新增 80、无移除；原始失败与最终用例的对应及两例契约修订单列在 result-validation.json。五资产均在同一 .NET10 host，不等于五个独立运行时。

两个本地包 --no-build/--no-restore 生成，各 5 DLL 与输出逐字节相同、README 精确匹配，未发布；scoped format 最终退出 0，保留 workspace-load warning、最初 6 条 whitespace 错误及修正日志。最初冻结脚本的 glob 错选 5 个依赖副本，16≠11 断言中止；修正为明确 11 个路径，在原消费者运行早期采样，未重建或重启测试。最终确认 11 个文件哈希/时间戳与运行早期样本一致，且最后修改时间均早于完整消费者启动；没有重建或重启。不把失败脚本或运行早期采样记为成功的预运行快照。

所有报告保存在忽略目录 `TestResults/pool-configuration/`，根日志为 `TestResults-pool-configuration-*.log`。原始 RED、fixture 失败及历史 MySQL/Oracle/证书/清理失败保留。P0-01、全部 23 项和八个工作包继续开放，完整 reset、资源/压力、OBProxy 和独立运行时/正式发布门槛不由本模块替代。

### 15.32 2026-10-25 普通结果字符集

**开发起点 `master/abd0077`；归属 P1-08 / W5。** 新增共享 ResultCharacterSet 映射及严格 ChineseEncoding，普通 ResultColumn 与 MySQL ColumnSize 共用实际最大字符宽度。全部八族的明确 ID/失败契约见[公共 API](obconnector-dotnet-architecture-design.md#result-character-sets)。使用官方 System.Text.Encoding.CodePages >=8.0.0，不做全局 RegisterProvider；两个 Standard 包依赖组声明该依赖，现代资产使用运行时支持。SQL/字符串参数仍 UTF-8；JSON/CLOB、binary63 与可信 national 规则不扩大。

**真实 RED 与独立走查。** initial-red 为84失败/24通过，原 collation8负例因现已支持，明确改为65535未知ID，而非删除拒绝测试。reviewer 发现 CP936 单字节80/FF接受问题，gbk-red 为2失败/10通过；严格语法验证后拒绝。CP54936 属旧映射，gb18030-red 为2失败/98通过；修正 A8BC→U+1E3F、8135F437→U+E7C7，整值/分块一致。独立对照 OceanBase v4.3.5_CE 全表：latin1全部256字节、GBK已定义双字节一致；GB18030双字节仅A8BC差异，1,087,996个有效四字节仅8135F437差异。来源快照和全表核对日志保存在忽略目录；未复制上游映射表到产品。

**fixture 与清理。** reader-green 的64失败/262通过是 Oracle type3 NUMBER 被 fixture 按 Int32 读取，改用实际 Int32 type9，未改变产品转换。shared fixture 在失败时取消服务预算并始终等待任务，保留主异常；最终 reviewer 的 Minor（非法字节测试主断言失败时跳过服务任务等待）也按相同原则修正。相关最终524通过，覆盖双模式、文本/二进制、同步/异步、缓冲/顺序、GetString/GetChars/GetTextReader、分块、NULL、宽度及连接复用；160个五资产消费者覆盖八样本。

**真实服务器。** 4.4.2.2 mysql-direct、Windows/.NET10、TLS disabled 的最终 real-character-sets-source-encoding 为6通过；每例内部验证8种协议/读取/同步组合。初次42字面量 Int32断言失败改为实际Int64；其后报告保留SET utf16/utf16le的1231/42000失败（最终分例4通过/2失败）。服务端禁止该会话设置，最终两例使用 character_set_results=NULL + CONVERT 源表达式，验证string FieldType和精确值，防止仅得到binary63字节却声称文本成功。中途 integration-cases-build 19个测试源码编译错误及修复日志保留，不记为产品缺陷。其余四族保留显式结果字符集设置，末尾重置UTF8并验证复用。没有 Oracle 新增实库字符集、TLS、OBProxy、其他版本/全ID/独立运行时报告。

**最终门禁：** 最终 Release solution build 0警告/错误；测试清理修正后仅重建Unit项目，集成fixture修正后仅重建Integration项目，均0警告/错误，产品及消费者不重建。完整 unit-final 4915通过/0失败/0跳过；DI14通过；无opt-in integration-final-cases 为9通过/79逐用例NotExecuted。完整 consumer-final 2798通过/0失败/0跳过，耗时13分36秒，正常用户上下文退出0；沙箱原unit为4778通过/137失败、consumer为1952通过/846失败，全部原报告保留。最终独立增量复核无剩余Critical/Important/Minor；reviewer不代替主任务全量执行证据。

按testName及testId多重集对比上一pool模块：消费者实际新增160、无移除；单测新增422、移除1，净增421，其中唯一移除是collation8负例改为65535，已单列为期望修订。全部84+2+2产品RED及64fixture失败同名映射到最终通过，细节见result-validation.json。运行前冻结的11个core/DI/Standard-consumer DLL哈希及时间戳在原沙箱与最终正常用户消费者运行后完全一致，修改时间均早于两次启动；没有在消费者运行期间重建产品/消费者。

两个本地包均 --no-build/--no-restore 生成，各5个DLL与冻结输出逐字节一致，README/LICENSE/THIRD-PARTY-NOTICES精确匹配当前文件。已运行eng脚本的内容/依赖检查部分；没有运行其新缓存restore及独立nupkg消费者部分。最初临时提取脚本因PSScriptRoot为空失败，传明确仓库位置后进入检查；其后错误要求现代资产也声明CodePages，实际现代资产使用runtime而被prune，已按Standard包依赖/现代runtime修正检查与文档，最终退出0；两个原失败日志保留，不记为产品缺陷。scoped format最终退出0，保留workspace-load warning及最初whitespace诊断/修正日志；文档检查核对三张23ID表、八工作包、344本地链接、UTF8-noBOM/CRLF，及相对固定abd0077的15.1–15.31与走查12–26历史原文不变。未发布或推送。

**环境与证据边界。** 正常 NuGet restore 出现 NU1900/NU1301 和 SEC_E_NO_CREDENTIALS；使用本机缓存源及单次 NuGetAudit=false 完成 restore，未更改仓库审计策略，漏洞在线审计未完成。默认沙箱完整测试存在 X509CertificateLoader.ImportPfx 的 Windows 用户密钥导入失败，原失败与正常用户复跑分别保留，不修改证书工厂、TLS 校验或产品绕过。全部报告位于忽略目录 TestResults/character-sets/，日志为 TestResults-character-sets-*.log，交接需另行归档。五资产只在同一 .NET10 host 运行；P1-08、全部23项和八工作包及完整驱动目标保持开放，历史 Oracle nested-table-column 及清理失败/跳过不改写。

### 15.33 2026-10-25 完整文档复核与 Oracle 独立 Prepare 检查点

**本次范围。** 以已提交的 `master/6b357f0` 为准更新文档；只整理现状，不交付新功能。第 4 节的 23 项、第 14 节的场景/源码/验收追踪、第 16 节的八工作包共同构成完整清单；本次不新增平行编号，也不关闭任何完整缺口。Java 固定参考 HEAD 再次核对为 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`。

**当前未交付功能。** `OceanBaseCommand.Start` 仍对 Oracle 输出/过程的独立 `Prepare/PrepareAsync` 和正 `FetchSize` 拒绝；不是普通文本命令的所有 Prepare 都缺失。工作区新增 `OracleStandalonePrepareTests.cs`，现有 `TestResults/oracle-standalone-prepare/initial-red.trx` 为 12 失败、0 通过，均命中该拒绝。用例覆盖同步/异步、两种 EOF 策略、文本输出/过程/函数三种形状；尚无产品修改或 GREEN，测试写入不等于功能实现。

P1-06 后续实施必须分别确认以下契约，当前均不作支持承诺：

1. 独立准备只解析目标，不执行目标、不读取/上传输入值、不发布输出；准备本身仍可能产生服务端解析、权限检查或元数据锁成本。
2. 已准备 ID 的下次执行、命令释放、REF CURSOR 存活、连接重开及池 generation 变化有唯一且明确的所有权；不能泄漏或复用过期 ID。
3. 准备错误、畸形元数据、取消、终态输出发布及提前关闭均有回归；流式参数、LOB、复杂输出和普通 Prepare 不回退。
4. 独立 Prepare、输出路径 FetchSize、REF CURSOR 输入/列值分别验收，不以完成其中一项宣布 P1-06 全部完成；实库过程/函数/游标证据另记。

**已有证据复核。** 本次读取上模块现存 TRX：完整单测 4915 通过、消费者 2798 通过、DI 14 通过；无 opt-in Integration 为 9 通过/79 NotExecuted；MySQL 直连字符集专项 6 通过。以上是第 15.32 节的既有执行结果，不是本次重跑，也不覆盖新增的 12 项失败。五资产仍由同一 .NET10 host 消费；原 Oracle 失败、沙箱证书导入失败和在线 NuGet 审计未完成继续保留。

**文档验收与范围。** 校验本地链接/锚点、三表 23-ID 一致性、八工作包唯一归属、UTF-8 无 BOM/CRLF 和 diff；相对固定 `6b357f0` 保留第 15.1—15.32 节及走查报告第 12—27 节历史原文。未重跑构建、单测、实库或 Java 测试，未提交代码或发布包。负责人、日期、性能 SLA 与正式平台支持范围仍待分配，不能以文档齐全替代驱动交付。

### 15.34 2026-10-25 Oracle 独立 Prepare

开发基线 `master/6b357f0`，P1-06/W5。公共契约见[Oracle standalone preparation](obconnector-dotnet-architecture-design.md#oracle-standalone-prepare)。沿用 COM_STMT_PREPARE、SQL 规范化和 OracleCursorOwner；准备无目标执行/输入编码或上传/输出发布/目标状态副作用，可信错误不 fallback。重复准备保留命令独占 ID；下次执行原子转交 owner，普通命令与数组都携带 ID，返回 cursor 可以跨命令 Dispose 存活。后续隐式执行使用 ID0，未新增持久 Oracle 缓存；数据库/session/generation/epoch 失效规则保留。

**原始失败与修复。** initial-red 12 项全部因原 standalone guard 失败。initial-green 的 12 失败发生在目标成功后的 follow-up query：fixture 漏 BeginCommand，修正后 12 通过。真实 real-first 的 10 失败是 profile[0] 误取首字符的测试数据错误；修正后 8 通过/2 RETURNING 失败。诊断确认 2 个逻辑 bind 对应 1 个 wire 参数和 1 个输出列；本地 returning-red 4 失败后补精确纯 OUT DML 例外。split-procedure-red 2 失败后限制首关键字为 INSERT/UPDATE/DELETE/MERGE，不放宽 PL；原始 PreparedStatement metadata 不被改写。

array-red 4 失败确认已有 BuildArray 忽略 retained ID，已传入；context-first 的 38 通过/6 失败包含这 4 项及 2 个 stream fixture 错误（误按 MySQL COM24 期待 Oracle COM162 hex piece），修正后 context-green 44 通过。消费者专项 60 通过，覆盖五资产、同步/异步、TCP/TLS、OB20 及畸形尾包；只是同一 .NET10 host 的资产消费。

**独立走查。** reviewer 提出 retained field/Dispose 竞争与 DML 负例覆盖不足两项 Important。将上下文失效、取得 statement 与转移合入共享 GetPrepared 命令锁；无产品测试钩子的锁竞争回归 review-race-red 确认 1 项 NullReferenceException/3 通过。DML 新增 INOUT、错误输出列数及错误总数三个负例；原实现这三项通过，刻意移除保护的 mutation 报告为 3 失败，恢复完整保护后复验，不能将 mutation 写成产品原有缺陷。首轮全量 unit-first-full 为 4957 通过/2 失败，属于旧测试仍要求 standalone Prepare 拒绝，改为 TableDirect 拒绝；保留测试名、原失败和新正例，不以删测试消除失败。

**真实互操作。** real-returning-green 10 通过；在完整保护恢复后的 real-related 为 16 通过（含现有过程/游标）。Oracle4.4.2.2 oracle-direct、Windows/.NET10、TLS disabled；五种形状 × 同步/异步证明准备时表仍为空、输出不变，执行后目标结果正确，cursor 可跨命令释放读取。并非真实 TLS/OBProxy、其他版本、元数据锁或完整故障矩阵。

**最终门禁：** 最终 Release solution build 0 警告/错误，独立走查修正后的 review-green 50 通过，完整 unit-final 4963 通过、DI 14 通过；无 opt-in integration-final-with-arrays 为 9 通过/82 NotExecuted。真实 real-final-with-arrays 20 通过，含准备后数组的同步/异步执行；只重建 Integration 测试项目并设置 BuildProjectReferences=false，未重建产品/消费者。完整 consumer-final 2858 通过/0 失败/0 跳过，正常用户上下文退出 0，耗时 12 分 23 秒；11 个运行前冻结程序集的哈希/时间戳保持不变，均早于消费者启动。按 testName/testId 多重集核对：单测新增 48、消费者新增 60，均无移除；原始所有单测失败均映射到同名最终通过，刻意 mutation 单列为覆盖验证。两项独立走查 Important 已按实际 RED/负例 mutation → GREEN 与完整套件验证，无剩余已确认 Critical/Important 问题；评审本身不代替主任务验证。两个 --no-build/--no-restore 本地包各 5 个 DLL，与冻结输出一致，README/LICENSE/NOTICE 精确匹配，依赖组与上模块完全一致；未执行独立 nupkg 消费者或新缓存 restore。scoped format 包含九个 C# 文件，退出 0 并保留 workspace-load warning。所有报告位于忽略目录 `TestResults/oracle-standalone-prepare/`，根日志前缀 `TestResults-oracle-standalone-prepare-`；交接需另行归档。

**保留范围。** 输出执行正 FetchSize、REF CURSOR IN/INOUT/列值、复杂成员及其他 P1-06 形状继续开放；普通准备仅忽略执行 FetchSize。原 Oracle nested-table column 失败、全部跳过、在线 NuGet 审计未完成及第 15.1—15.33/走查第 12—28 节历史不改写；全部 23 项、八工作包和完整驱动目标不关闭。

### 15.35 2026-10-25 建连阶段诊断

从 `master/d6a9169` 开发，归入 P2-04/W7。新增 `oceanbase.client.connection.phase.duration`（秒），仅 `oceanbase.connection.phase` / `oceanbase.outcome` 两个标签；九阶段、可选阶段省略、重试/预热、池复用、linked-token 与 Open 分类差异、旧池等待聚合及回调约束见[公共契约](obconnector-dotnet-architecture-design.md#connection-phase-diagnostics)。保留既有 operation spans/counters，不将阶段次数当作 Open 次数，也不声称其耗时相加等于总耗时。

**原始失败与独立走查。** initial-red 为 10 失败；first-green 为 38 通过/10 失败，暴露 partial class 字段先于 Meter 初始化的问题，已将 histogram 初始化移至既有顺序字段。phases-green 为 62 通过/4 失败：两例 fixture 错误清除 CLIENT_SSL 位，两例未提供 time-zone session-state；修正 fixture 后沙箱 phases-complete 为 76 通过/2 PFX 导入失败，正常用户 phases-user 为 78 通过。初次完整专项编译的可选参数方法组错误保留于日志，不计为产品缺陷。

独立 reviewer 指出 `pool.wait` 在计时结束前执行清理监听器，会误包含应用回调耗时；按用户影响列 Important。实际同步/异步两项 `review-wait-red` 均失败，补 Stop 固定单调时钟终点、清理后再发布，`review-wait-green` 两项通过。未新增产品测试钩子；测试断言在 metric 回调外执行，避免被产品异常隔离吞掉。无其他已确认产品缺陷或延后 Minor。首次全量单测 unit-first-full 为 5003 通过/2 失败：既有 TcpTransport 重复连接压力用例发生接收超时，以及 identity 同步超时用例未在 5 秒观察窗口内完成；原报告和日志保留。相同程序集单独复跑这两个方法的 6 个用例全部通过，只说明未单独复现，不能据此认定环境根因或算全量门禁通过。消费者最终 2863 项通过（732.6 秒），随后以相同冻结程序集单独重跑完整单测，5005 项通过（208.5 秒），均 0 失败/跳过；首次失败的同名用例最终通过，但尚未确认最初的超时根因。两次全量之间未放宽断言、排除用例或修改产品实现。

**最终验证。** Release solution build --no-restore 退出 0、0 警告/错误；本模块 C# 文件 dotnet format whitespace --verify-no-changes 退出 0，既有 workspace-load warning 保留。DI 14 通过；未启用实库的 Integration 9 通过/83 NotExecuted，不能写成 92 项实库通过。real-final 8 通过，包括双模式的同步/异步新增阶段测试及原诊断回归，实际 Open 后 SELECT；目标为 OceanBase4.4.2.2 MySQL/Oracle direct、TLS disabled、Windows/.NET10，不创建对象。

完整单测相对上一模块新增 42、消费者新增 5，按 testName/testId 多重集核对均无移除；所有原始单测失败映射至同名最终通过。11 个启动前冻结 DLL 哈希及纳秒时间戳未变且早于最终消费者启动。两个 --no-build/--no-restore 本地包各五个 DLL 及 README/LICENSE/NOTICE 与工作区完全匹配，依赖组与上一模块相同；未执行新缓存 restore、独立 nupkg 消费者、发布或在线 NuGet 漏洞审计。五资产测试均在同一 .NET10 host，不是五个独立运行时。六份公共文档保留三表 23-ID、八工作包及第 15.1—15.34/走查第 12—29 节原文，通过链接/锚点、编码、历史和 diff 检查。

**交付边界。** 原始日志与 TRX 保存在忽略目录 `TestResults/connection-phases/` 及 `TestResults-connection-phases-*.log`，交接需另行归档。本模块不关闭 P2-04，也不关闭全部 23 项或八工作包。未承诺完整协议 debug、慢查询、其他资源等待、性能 SLA 或独立运行时/OBProxy/TLS 互操作。

### 15.36 2026-10-25 慢命令诊断

从 `master/b2fce9d` 开发，P2-04/W7。新增 `Slow Query Threshold` / `SlowQueryThreshold`，0..Int32.MaxValue 毫秒，默认 0 关闭；端点/凭据快照和有效池配置身份保留。复用 exchange 单调时钟，清理/回调前取样；仅 execute/cursor.fetch 完成时达到阈值才报告一次，包括 reader 消费、错误、取消和 timeout。独立 Warning1102、四固定标签 counter 与现有 span 标记均脱敏；[公共契约](obconnector-dotnet-architecture-design.md#slow-query-diagnostics)明确与服务器执行耗时、现有 operation duration 以及未结束请求的区别。

**原始失败。** 初始测试先后有 CS0246（缺 using）和 CS1729（错误使用私有 endpoint 构造器）编译错误，修正为现有 Parse；可编译 initial-red 实际 25 项全部因未知配置失败。first-green 为 160 通过/6 失败：三例 options fixture 缺 User ID、两例沙箱 PFX 导入失败，以及既有 phase 等待测试期望一次却实际两次。后者按每次实际 wait invocation 的既有契约校正为非空且所有样本均为 pool.wait/elapsed，保留超时、单物理连接和容量复用断言；不改变池等待或指标实现。正常用户 focused-user 为 173 通过/7 失败，来自多命令 fixture 未回报已被 target SQL 消耗的 time-zone 状态，第二条请求实际为 SESSIONTIMEZONE 控制查询；回报状态后 focused-green 184 通过。

**独立走查。** Fresh-context reviewer 完整检查配置/池身份、同步异步/隐式准备/SchemaOnly/KeyInfo、普通及 REF CURSOR reader、错误与清理、计时边界、三通道异常和脱敏，未确认任何新增可操作缺陷或延后 Minor。确认旧 wait 测试修正符合逐次 wait 契约。其未执行构建/测试，未判断最终文档/包、真实部署/负载、无限阻塞回调和既有日志行为；这些由本记录的执行证据及剩余边界分别说明。

**最终执行证据。** complete-focused184、consumer-focused25、real-focused12 均通过。完整单测 unit-final5049（217.0 秒）、完整消费者 consumer-final2873（762.0 秒）通过，0 失败/跳过；DI14 通过，无 opt-in Integration9 通过/84NotExecuted，不能把未执行项算实库通过。Release solution build --no-restore 退出0、0警告/错误；本模块文件 dotnet format whitespace --verify-no-changes 退出0，既有 workspace-load warning 保留。实库12例为当前四组诊断的双模式直连用例，新增慢 reader 为同步/异步各一；4.4.2.2、TLSdisabled、Windows/.NET10、SELECT only，不创建对象。

相对 b2fce9d，单测新增44（43慢诊断+1池配置）、消费者新增10，按名称/ID多重集核对均无移除；三份原始失败TRX所有失败项均映射同名最终通过，两个测试编译错误独立保留。11个消费者启动前冻结DLL哈希/纳秒时间戳不变并早于启动；测试期间未重建这些输出。两个 no-build/no-restore 本地包各5DLL及README/LICENSE/NOTICE匹配工作区，依赖组与上一模块一致。未新缓存restore、独立nupkg消费者、在线NuGet漏洞审计或发布；五资产仅同一.NET10 host消费，非五独立运行时。六公共文档保留三表23-ID、八工作包和第15.1—15.35/走查12—30原文，通过379本地链接/锚点、UTF8无BOM/CRLF及diff检查。日志/TRX 位于忽略目录 `TestResults/slow-query/` 与 `TestResults-slow-query-*.log`，交接须另行归档。原失败与历史记录不删除；本模块不关闭 P2-04、全部 23 项或八工作包。

### 15.37 2026-10-25 Oracle 未知精度 NUMBER 结果读取

**归属与根因。** 从 master/5ea71b2 开发，P1-08/W5，是 W1 实库隔离验收发现的前置缺陷。Oracle4.4.2.2 的 prepared SELECT :value FROM DUAL 返回 type246/precision0/scale255(-1)，读取整数7时被旧解码当作负scale声明而抛出需要舍入。真实 NUMBER(*,-1) 表及 CAST 也返回 precision0/scale-1，不能凭此组合发明未知scale标记。固定Java源码保留元数据但直接解析实际BigDecimal载荷；本次未运行Java。

**实现与公共行为。** Oracle precision未知的结果不再施加元数据scale约束，保留服务端返回的精确系数/值scale及38位有效数字/指数范围检查；已知precision、MySQL及参数声明保持严格校验。Schema保留原始precision/scale，不将值scale写成列定义。返回文本40的表示改为系数40/值scale0，原来人为重缩放为4/-1；数值不变。公共契约见[未知精度结果](obconnector-dotnet-architecture-design.md#oracle-number-results-with-unknown-precision)。

**RED与修正。** 新数值单测number-red为6失败/3通过；number-focused84通过。既有两个unknown-precision拒绝用例改为已知precision5的真实约束保护，一个wide40用例的系数/scale期望按返回载荷修正；不是将合法负scale全部忽略。相对上一单测报告按名称新增12/移除3、净增9：新增数值用例9，两个用例改名和一组期望数据变更；原始6RED均映射最终同名通过。

**最终门禁。** Release solution build及消费者--no-dependencies编译均退出0、0警告/错误。首次完整单测5057通过/1失败：既有PoolRoutingTests.IdleSessionsCannotBypassDiscoveryCancellationOrBudget同步timeout例在第一次Open的1秒TCP建连超时，尚未进入目标发现取消场景；未修改其实现/测试/预算。独立路由专项5通过后，完整单测number-unit-repeat5058通过、0失败/跳过；原报告保留，不把未复现原因写成已证实环境结论。受影响消费者number-consumer-complete40通过（含新增五资产未知精度读取5例及原始读者schema/Decimal/Precision回归），不是完整消费者套件；先前25通过保留。DI14通过，无opt-in Integration9通过/86NotExecuted。Oracle直连数值专项number-real-final4通过：同步/异步prepared整数/小数绑定及真实表/CAST负scale舍入，随机OBI表按LIFO清理；4.4.2.2、TLSdisabled、Windows/.NET10。五资产在同一.NET10 host运行，不是五个独立运行时。数值验证器核对五资产DLL时间早于消费者启动，三测试host的产品副本与当前net10输出相同。未打包、独立nupkg消费、在线审计或发布。首次格式检查因既有相邻InlineData空白失败，修正后限定format退出0，workspace-load warning保留。

**独立走查。** 早期reviewer核对真实元数据歧义及Java解码/初始化对照；最终独立reviewer检查结果/typed/provider/schema、参数和PL输出路径及回归，无确认新增可操作缺陷或延后Minor；新增五资产消费者作增量复核同样无发现。reviewer未运行门禁、未核验凭据或独立实库，也未宣称完整池隔离或部署验收。系数/scale表示变化已公开。

**仍开放的W1实库发现。** 初始池套件real-first12通过/3失败/1跳过，其中两例Oracle NUMBER失败及一例未知createObjects标签装配错误；此前另有两处CS1503事务转换编译错误。装配修正后real-second14通过/4失败/1跳过：Oracle NUMBER2失败以及事务Close后的隐式写不可见2失败。oracle-probe4失败还确认全新非池连接的隐式写也对观察连接不可见；declared-metadata2通过确认真实负scale形状。两个临时raw metadata诊断探针各2失败，探针代码已移除，非正常门禁。Oracle建连及pool reset后的autocommit初始化尚未实现，归P0-02/W1并与W2联动；不能将本次数值4通过记为它已修复。原池验收草稿保存在忽略目录，下一模块恢复并补初始化失败/取消/帧/会话销毁回归。alternateDatabase未配置的跳过保留；OBProxy、TLS、跨平台、长期资源、完整reader/cursor/LOB/XA隔离仍开放。

原始报告、计划和草稿位于忽略目录TestResults/pool-isolation/及TestResults-pool-isolation-*.log，交接需另行归档。第15.1—15.36及走查12—31原文保留；全部23项、八工作包和完整驱动目标不关闭。

### 15.38 2026-10-25 完整走查文档与 W1 RED 检查点

**交付范围。** 以当前已提交 `master/4810907` 为基础复核完整文档。第 4 节、第 14 节及走查报告第 4 节保留同一组 23 项，第 16 节保留八工作包；每项均能追踪当前状态、触发场景、源码/测试入口、影响与关闭证据。第 5 节定义模式/API 边界，第 7 节定义测试与发布门槛。23 项是缺实现、受限组合和欠验收的综合清单，不是 23 个已复现 bug；所有完整项仍开放。

**本次更新。** 首页从 NUMBER 开发基线更新到该修复已提交 `4810907`；新增第 5.1.5 节，将 Oracle fresh/reset 自动提交缺陷显式纳入 P0-02 的总表、追踪表和 W1，而不增设编号。源码与固定 Java HEAD 再次核对；COM_INIT_DB 描述明确仅适用于 MySQL。文档保留诊断、字符集、准备、目录和池已有实现，未将欠验收写成完全未实现。

**当前工作区和证据。** 生产代码没有该初始化修复。两份共享身份 fixture 有未提交调整，另有新增初始化单测和恢复的实库池隔离测试；这些不计为交付。已完成的 filtered Release 单测命令退出 1，18 执行 / 18 失败 / 0 通过 / 0 跳过；失败机制及报告见第 5.1.5 节。整理后的池实库套件尚未重跑，尚无本模块 GREEN、全量回归或独立最终代码审查。上一模块的 5058 单测通过、40 个受影响消费者通过等仅属于第 15.37 节原报告，不能冒充当前含 RED 工作区的全量通过。

**剩余实施与关闭。** 优先完成 Oracle 建连/reset 初始化及失败销毁，补错误/取消/超时/帧/容量回归，再执行真实双借用者矩阵、受影响消费者和全量门禁，独立走查后方可提交功能模块。W2 的 unknown outcome、W3 的 OB20/reroute、W4 的企业认证/发现、W5 的 Oracle 形状/类型、W6 的目录/CRUD、W7 的性能/运维和 W8 的部署/包验收按第 16 节继续；负责人、日期、SLA 和正式支持范围尚未分配，不据此编造计划承诺。

**本次文档验收。** 检查本地链接/锚点、三表 23-ID、八包唯一归属、Markdown 结构、UTF-8 无 BOM/CRLF 和 diff；相对 `4810907` 保留第 15.1—15.37 节及走查报告第 12—32 节历史原文。未额外运行构建、全量测试或实库；未提交、推送或发布。本次完成文档检查点，不将驱动或开发总目标标记完成。

### 15.39 2026-10-25 Oracle 自动提交与真实池隔离

**范围与契约。** 从 `master/4810907` 开发 P0-02/W1 子模块。身份确认成功后无条件发送 Oracle `set autocommit = 1`；reset 先 COM_RESET_CONNECTION 再设置，两步确认后才增加 generation。普通 OK 必须 affected rows/insert ID 为 0、Autocommit 开启，不能含活动/只读事务、多结果、cursor/last-row/output 标志；ERR 保留 code/SQLSTATE 且不传播服务端消息。失败、取消、超时和关闭销毁会话并回收池容量。建连覆盖合并 SET 的 system variables，reset 只保留 SET 的 timezone/national 元数据；MySQL disabled-autocommit 拒绝契约不变。每次 Oracle 物理建连/成功 reset 增加一个往返，无新公开选项/API/依赖。见[公共契约](obconnector-dotnet-architecture-design.md#oracle-autocommit-baseline)与第 5.1.5 节。

**RED 与失败保留。** Open18项、reset18项分别先全部 RED；加入独立只读事务状态0x2000后4项先 RED，随后明确拒绝。初次专项构建有两处 CS0103（fixture 的 oracle/_oracle 及 ServerIdentity namespace），修正后继续；沙箱专项344通过/32失败，包含 TLS/PFX 上下文失败及误给 MySQL reset 脚本增加 Oracle SET 的两例超时，修正模式脚本并在正常用户上下文复核376通过。首次完整单测5130通过/4失败，均为 Oracle statement-cache 和 ambient reset fixture 未消费新增 SET；补 exact SET/OK，原断言保留，相关8项通过，再完整5134通过。未提高预算、排除或移除旧例。限定格式初次退出2，修正新增测试 object initializer 空白后最终退出0；原 workspace-load warning 保留。初次包核验脚本误用 NOTICE 文件名，改为实际 THIRD-PARTY-NOTICES.md 后通过，未修改包内容。

**最终本地验证。** Release solution build和新增 consumer build均0警告/错误；全量单测5134通过/0失败/0跳过（218秒），新增76项、移除0，按 testName 多重集和 testId 核对；原 RED/首次全量及专项失败均映射最终同名通过。专项初始化/生命周期76通过：Open/reset×14响应56项、pending SET取消/Close/超时12项、metadata8项。完整消费者2898通过/0失败/0跳过，新增当前模块20项 public five-asset 初始化失败/单容量恢复用例；专项90通过。DI14通过；无opt-in Integration9通过/93逐用例NotExecuted。报告在忽略目录 TestResults/pool-isolation/autocommit-*.trx；命令使用 Release、最终门禁 no-build/no-restore，正常用户执行 TLS/PFX 套件。

**真实服务器。** 修复前池隔离18通过/4失败/1跳过；修复后22通过/0失败/1跳过，四个同名 Oracle fresh/reset 隐式写失败转为通过。最终整个 Connections 加 OracleNumberTests 为52通过/4跳过；缺 alternateDatabase（两个套件）、unsupported-auth 和 disabled-autocommit 专项配置，跳过未关闭。实际证明 fresh 写在 Close 前后可见、显式事务 Close 回滚、下一借用者隐式提交、prepared/generation 三轮复用、用户 timezone 污染丢弃/恢复、未读 reader 及 Oracle 活动 temporary LOB 丢弃。仅 OceanBase4.4.2.2、双模式直连、TLS disabled、Windows/.NET10；不扩展到 OBProxy/企业 TLS/全部资源或长期并发。

**消费者、运行时和包。** 消费者启动前冻结11个DLL哈希/mtime，全程及 no-build/no-restore pack后不变；更新普通/压缩/shared reset、19处 Oracle OB20 fixture的实际请求序号，MySQL序号保留。两个 standalone raw-wire samples也消费初始化，分别在真实 .NET8.0.31/9.0.20/10.0.12 运行，共6项 smoke通过；仅两个样例，不是三个运行时的完整单测/部署验收。五资产全量消费者仍在同一 .NET10 host，不等于五独立运行时。两个本地包各五DLL及 README/LICENSE/THIRD-PARTY-NOTICES 与工作区一致，依赖组相对5ea71b2不变；未独立nupkg消费、新缓存依赖恢复、在线漏洞审计或发布。

**独立走查与剩余工作。** 独立 reviewer 检查 initializer、exchange/lease/pool、失败容量、帧与共享 fixture；要求补 pending SET生命周期和metadata测试，均已补齐并复核。0x2000独立标志从验证边界决策变为明确拒绝；补20项消费者及旧fixture修正后增量走查无剩余可操作缺陷或延后minor。公共契约也已独立复核；reviewer未执行门禁、核验凭据或独立实库，最终结果由上述报告提供。第15.38和走查报告33作为原文保留，全部23项/八工作包继续开放，完整reader/cursor/LOB/XA、任意隐式副作用、并发/长期资源、服务器版本/代理/信任/跨平台与正式发布门槛仍待完成。

### 15.40 2026-10-25 XA 池隔离与 prepared 分支恢复验收

**范围。** 从 master/ffadc7a 补 P0-02/W1 的 XA 验收缺项；先走查 GetXaResource/StartXa、Close 和 pool CanReturn，再验证专用标记直到 Close 的既有契约。本次只新增两个测试文件和同步文档，未改变生产代码、公开 API、默认池化策略或依赖；不能写成新功能实现或已复现生产缺陷修复。上一自动提交模块及其所有失败/跳过原文保留。

**新增回归与真实证据。** 本地 28 项覆盖双模式 × 同步/异步 × 七阶段，首次即通过；没有人为制造生产 RED。旧 socket 必须直接 EOF，不接受 reset/隐式 XA 完成；MaxPoolSize=1 的新握手和 SQL 成功在 ClearPool 之前，同逻辑重开后的旧资源十二入口均拒绝。新增实库 20 项首次与最终均通过：12 项未启动/已提交/已回滚专用会话销毁、下一借用者自动提交和本地事务，8 项 prepared 关闭后 Recover/明确 Commit 或 Rollback/再次恢复无该 XID/观察者行数。已有只读正常复用回归随 Connections 一起运行，避免以销毁全部会话掩盖池失效。

**门禁。** 首次全量单测 5,162 通过（3 分 34 秒），走查修订测试清理后最终重复 5,162 通过、0 失败/跳过；较上一模块 5,134 增加本地 28 项，旧测试源码没有移除。首次及最终真实 Connections + XaTests 均 76 通过、4 跳过，其中新增 20 项；四项仍为两个 alternateDatabase、unsupported-auth 和 disabled-autocommit 配置缺失。无 opt-in Integration 首次及最终 9 通过、95 NotExecuted，两个新增 theory 在禁用时保留可见跳过。Release solution 和最终 integration 增量构建均 0 警告/错误；限定两新增 C# 文件格式检查退出 0，workspace-load warning 保留。文档校验脚本在暂存后曾仅统计未暂存文件而误判变更数，改为合并 staged/unstaged/untracked 清单后复核，未改测试或历史结果。命令使用 Release、最终测试 no-build/no-restore，正常用户上下文执行完整 TLS/PFX 单测和真实配置；报告在忽略目录 TestResults/pool-xa-isolation/pooled-xa-*.trx。

**独立走查。** reviewer 要求把数据库失败清理移到 owner 的释放之前，跟踪 attempted/active/completed，依次尝试 End、Close、独立回滚并聚合主失败与清理失败；要求始终等待本地服务 Task。另发现 db.OpenAsync 会继承 profile 的池化，已在清理处显式 Pooling=false；避免 observer 占满普通池时阻塞清理。完成分支不额外 End/回滚，最后回滚才容忍 NOTA。修订后的代码已复核，无剩余可操作问题；reviewer 不执行测试，最终计数由上述报告核实。

**边界与剩余。** 当前仅 OceanBase 4.4.2.2 双模式直连、Windows/.NET10、TLS disabled，不能证明外部协调器崩溃、响应丢失、真实 active/suspended 断开恢复、OBProxy、企业信任、长期并发或完整 reader/cursor/LOB 隔离。未重新执行消费者、DI、包消费、三个现代运行时样例或在线漏洞审计；上一模块的 2,898 消费者、14 DI 及六样例仅是历史结果。本轮无生产/公共资产变化，不借用历史计数作为当前执行。全部 23 ID、八工作包及发布门槛仍开放；未 push 或发布。

### 15.41 2026-10-25 旧 reader 延迟关闭与池租约隔离

**范围与根因。** 从 master/00b3976 继续 P0-02/W1 reader 隔离，关联 P1-05，不新增缺口 ID。普通 reader 完成 exchange 后可以留下未 Dispose 的对象；Close/Open 经 reset 重用相同 PhysicalSession 时，仅比较会话引用会接受旧关闭回调。修复复用既有逻辑 `_openGeneration`，在 StartCommand 和 ReserveBatch 的连接锁内捕获，通过 exchange/Batch operation 传至全部四个 reader 同步/异步关闭入口，在 DetachForClose 同锁核对会话与代数。公开 API、默认 Pooling=false、依赖及协议不变；当前租约的 CloseConnection 语义保留。详见[第 5.1.7 节](#517-closeconnection-reader-的逻辑租约隔离)及[公共契约](obconnector-dotnet-architecture-design.md#closeconnection-reader-ownership)。

**原始 RED 与受控负向验证。** 修改生产代码前，普通 reader 双模式同步/异步本地 4 失败，真实直连 4 失败：预期 Open、实际 Closed，wire 还观测到预期 SQL=3 却收到 reset=31。最初 fixture 的 CS0266 是 DbDataReader 类型声明问题，修正后才得到有效 RED，编译失败不算缺陷复现。Batch 回归在实现后补充，通过持有连接 gate、等待 Dispose task 释放 reservation、重开并复用同物理会话来确定性覆盖回调窗口；临时只去掉 generation 比较，4 项全部失败，finally 按原字节恢复。这是受控移除保护验证，不是原始未修改基线 RED。

**回归及开发过程。** 首轮普通新旧 reader 相关选择 12 通过。随后用文件名 PooledReaderClose 过滤 partial TextCommandTests，退出 0 但零匹配，不计通过；改用实际两个方法名后 8 通过。五资产消费者首轮 10 通过/10 失败，Oracle reset 后需要 SELECT SESSIONTIMEZONE FROM DUAL，fixture 漏答；复用既有 SessionTimeZoneFixture 后聚焦二十项全部通过，完整消费者也包含该二十项，未为 fixture 修改产品。单元 fixture 清理按独立 reviewer 意见逐步收集错误，保证 Cancel/Stop 和两个任务各自观察，保留主失败及服务失败。消费者通过同 socket 接收 reset、ping、目标新 SQL证明复用，不依赖内部类型；实库四项直接断言对象及 generation，并在关闭连接/数据源前确认新租约可查询。

| 最终门禁 | 实际结果 | 范围 |
| --- | --- | --- |
| Release solution build | 0 warning / 0 error | 核心与 DI 五资产及全部测试项目；测试期间不重建替换消费者 DLL |
| 完整 UnitTests | 5,170 通过，0 失败/跳过 | 相比上一模块 5,162 新增八项，既有未读/当前租约/SchemaOnly/REF CURSOR/LOB/Batch 回归保留 |
| 完整 ConsumerTests | 2,918 通过，0 失败/跳过 | 相比上一已记录消费者 2,898 新增二十项；五资产均在单一 .NET10 host 运行 |
| DI | 14 通过 | 本次实际执行，不借用历史结果 |
| 未 opt-in Integration | 9 通过 / 96 NotExecuted | 数据库用例跳过，配置回归执行 |
| 真实 Connections + XaTests + RefCursorTests + LobTests | 87 通过 / 4 配置跳过 | 包含新四项、既有池/XA与 Oracle 游标/LOB专项；不是完整实库套件 |
| 限定十个 C# 的 dotnet format | 退出 0 | 保留已知 workspace-load warning；另执行 verify-no-changes |

真实证据仅适用于 OceanBase 4.4.2.2、mysql-direct/oracle-direct、Windows/.NET10、TLS disabled。四项跳过仍是 alternateDatabase、unsupported-auth、disabled-autocommit 等未配置能力，不能计为已验收。独立 reviewer 未发现生产修复遗漏；先提出的测试清理 Minor 已修正并复核，reviewer 未代替执行测试。

**复现命令和报告。** Release solution build 后，使用 `dotnet test <project> -c Release --no-build --no-restore --logger trx` 分别运行 UnitTests、ConsumerTests、DependencyInjection.Tests；Integration 无 opt-in 跑一次，真实选择设置 OB_REQUIRE_INTEGRATION=1、OB_INTEGRATION_PROFILES=mysql-direct,oracle-direct，filter 为 `FullyQualifiedName~Connections|FullyQualifiedName~XaTests|FullyQualifiedName~RefCursorTests|FullyQualifiedName~LobTests`。新本地选择为 `FullyQualifiedName~CompletedCloseConnectionReaderCannotCloseAReusedLogicalLease|FullyQualifiedName~BatchCloseCannotCloseALeaseReopenedAfterReservationRelease`，不要按 partial 文件名过滤。正常用户上下文提供既有 User Secrets 与 TLS/PFX fixture。原始/受控 RED、零测试、消费者 fixture 失败、最终 TRX、构建及格式日志保存于忽略目录 `TestResults/pool-reader-close/` 和根 `TestResults-pool-reader-close-*.log`；报告不含凭据。

**文档与产物核对。** 444 个本地链接/锚点有效，三份缺口表各保留 23 ID，八工作包及第 15.1—15.40/报告第 12—35 节历史逐字保留；16 个交付文件均为 UTF-8 无 BOM/CRLF。首轮编码校验发现新增实库测试为 LF，规范换行后通过，无语义改动。独立复核校准 TRX 跳过统计，按个体 NotExecuted 结果计数，不使用该格式中为零的 Counters.notExecuted。旧 5,162 单测与 80 实库 case 均保留，新增八项单测及包含新 reader 四项/既有游标 LOB 扩选在内的十一项实库；消费者新增二十项均通过，十一份被加载资产的哈希与时间戳在执行期间不变。验证 JSON 随报告保存在忽略目录。

**剩余与交付边界。** 本模块关闭该确定缺陷，不关闭完整 P0-02、P1-05、W1 或全部 23 项/八工作包。任意隐式 session 副作用、完整 reader/cursor/LOB 资源及 reset 矩阵、XA 响应丢失/协调器故障、任意 Close/Open/Dispose 并发及长期负载仍须逐项验收。未重跑包消费、独立运行时 smoke、Linux/macOS、OBProxy、企业信任链/mTLS 或完整数据库套件；不覆盖历史 Oracle nested-table column 等失败。第 15.1—15.40 和走查报告第 12—35 节不改写。未切换分支、push 或发布。

### 15.42 2026-10-25 完整走查文档交付与资源验收检查点

**本次范围与版本。** 当前已提交代码是 `master/6c45173`；本次只更新能力基线、走查报告和进度台账。完整文档保留第 2 节能力矩阵、第 4 节 23 个缺口（P0 六项/P1 十一项/P2 六项）、第 5 节契约、第 10 节 Java 对照、第 14 节逐项触发/源码/关闭标准、第 16 节八工作包及依赖。负责人、期限、性能 SLA 和尚待决定的支持范围没有被虚构为承诺；23 项及八包仍未整体关闭。

**源码复核。** 已核对当前连接池与逻辑 Open generation、`PhysicalSession.RequiresDiscardOnReturn`、Oracle cursor 所有权和释放路径、未知执行结果异常属性、OB20 拒绝路径、Oracle 参数方向和复杂值限制、地址选择器，以及现有测试入口。Java HEAD 再次读取得到固定参考 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`；其 `OceanBasePoolDataSource`、`OceanBasePooledConnection`、`Pools`/`Pool` 均存在。结论仍是两端都有池，.NET 默认关闭池化、需显式启用，完整隔离和部署证据仍不足；不把 Java 的内部类或本地 fixture 当成真实兼容承诺。

**读取既有报告，未重新执行套件。** 以下直接按原始 TRX 的个体 `UnitTestResult` outcome 统计；该格式的 Counters.notExecuted 不能代替跳过条目。报告目录是本地忽略目录，不随源码文档发布。

| 既有报告（`TestResults/pool-reader-close/`） | 实际结果 | 含义与边界 |
| --- | --- | --- |
| `reader-close-unit-final.trx` | 5,170 通过 | 上一已提交模块完整单测；不包含后续未提交资源草稿 |
| `reader-close-consumer-final.trx` | 2,918 通过 | 五种资产在同一 .NET10 host 的消费者，不是五个独立运行时 |
| `reader-close-di-final.trx` | 14 通过 | 上一模块 DI 报告 |
| `reader-close-noopt-final.trx` | 9 通过 / 96 NotExecuted | 未启用数据库 profile 的集成结果，跳过不算实库通过 |
| `reader-close-real-final.trx` | 87 通过 / 4 NotExecuted | 限定 Connections/XA/REF CURSOR/LOB；直连 OceanBase4.4.2.2、Windows/.NET10、TLS disabled，非完整部署矩阵 |

**当前未提交资源草稿。** 工作区原有 `tests/OceanBaseConnector.UnitTests/PooledResourceIsolationTests.cs` 保留不改。读取 `TestResults/pool-resource-isolation/pool-resource-first.trx` 并收取原运行退出状态：退出 1，28 项中 24 通过/4 失败，无跳过。本次没有重跑或修正测试，也没有据此修改生产代码。

- `PersistentLobResourcesControlSameEndpointPoolReuse`：BLOB/CLOB × 同步/异步 × handle/open/partial/complete/disposed，共 20 项通过；这是本地协议证据，仍是未提交验收草稿。
- `OracleOutputCursorRetirementProtectsTheNextSameEndpointBorrower`：共 8 项；unopened/disposed 的同步/异步 4 项通过；partial/complete 的同步/异步 4 项失败。原始错误包含重新握手超时及测试端读取连接关闭；不能仅凭汇总将其归为产品缺陷或已确认环境问题。
- 失败根因尚未确认，需核对实际 fetch、资源释放/statement-close 报文及测试预期；原始 TRX 为准，不沿用交接摘要中不同的失败阶段描述。没有有效产品 RED/修复/复测前，不能宣布该资源子模块已完成。

**接续工作。** 先查明四项失败，再完成同端点/单容量池的持久 LOB 与 REF CURSOR 生命周期矩阵和真实验收；既有实现符合契约的部分按验收记录，不伪造新增修复。需要产品修改时先保留可复现 RED，再补本地/受影响消费者/真实回归与独立复核。W1 的并发/长期资源、W2 的响应丢失及协调器恢复和 W3—W8 的开发/部署缺口仍按第 14/16 节推进，不因这份完整文档而关闭。

**文档检查范围。** 核对本地链接/锚点、三张 23-ID 表、八工作包唯一归属、历史记录保留、UTF-8 无 BOM/CRLF 及 diff。本次不重跑构建、全量测试、真实数据库、包消费或发布；不改分支、不提交代码、不 push。既有 Oracle nested-table column 失败、清理失败及所有配置跳过保留，完整驱动继续保持 experimental。

### 15.43 2026-10-25 持久 LOB 与 REF CURSOR 的同端点池隔离验收

**范围。** 从 `master/6c45173` 继续 P0-02/W1，并关联 P1-05/P1-06；只增加本地及真实数据库测试，无生产实现、公共 API、配置或依赖变更。已有保护符合契约，本模块记为验收交付，不虚构新修复。源码为 `PhysicalSession.RequiresDiscardOnReturn`、`PhysicalSessionPool`、LOB context/reservation 与 `OracleCursorOwner`；新增[本地测试](../tests/OceanBaseConnector.UnitTests/PooledResourceIsolationTests.cs)和[真实测试](../tests/OceanBaseConnector.IntegrationTests/Connections/PoolResourceIsolationTests.cs)。第 15.42 节及报告第 37 节原样保留此前文档检查点及失败状态。

**验收行为。** 同端点且 MaxPoolSize=1：BLOB/CLOB × 同步/异步 × handle/open/partial/complete/disposed，共二十项；未释放句柄销毁原物理会话，完全释放的只读持久 LOB 才允许 reset/init/ping 后复用，并验证 generation 递增。旧句柄 OpenRead/OpenTextReader、已开 reader/stream 的读取、写入、截断和参数绑定均拒绝；释放旧资源后，新借用者在清池前执行 SQL。实库原非池 setup 连接独立核对长度及完整文本/RAW 内容，防止等长覆写被长度断言漏掉。

REF CURSOR 的 unopened/partial/complete/disposed × 同步/异步共八项：原命令释放后 cursor 保持正确所有权，旧资源拒绝再次读取，旧 reader/cursor Dispose 后新租约仍能执行真实 SQL。本地 peer 证明旧传输关闭、同端点重新握手及新 SQL；实库验证不同物理对象。所有阶段还受不透明过程/匿名 PL 调用的 session-state 退休保护，不能将 complete/disposed 的销毁单独归因于活动 cursor 计数，也不能据此证明释放 cursor 后可复用。

**原始失败与根因。** `TestResults/pool-resource-isolation/pool-resource-first.trx` 为 24 通过/4 失败，失败是 REF partial/complete 的同步/异步。源码及报文边界核对确认两项 fixture 假设错误：部分读取后销毁传输可返回 TCP reset，原测试只接受 EOF，导致 peer 提前失败且新连接等不到握手；完整 reader 结束只排队 statement close，草稿在 cursor.Dispose 前等待这两个包，形成等待。现只在显式 cursor.Dispose 阶段要求 ID91/42 关闭包；丢弃接受 EOF 或 IOException 内层精确的 SocketError.ConnectionReset，随后仍必须完成新握手/新 SQL，不放行其他异常、超时或网络包。修正后本地 28 项通过。这不是产品 RED/修复。

首轮真实 `pool-resource-real-focused.trx` 为 8 通过/20 失败：patient-data 实际为十二字节，我在 fixture 错写期望十一。ByteLength 改为按 UTF-8 实际字节数断言，数据库长度期望十二，原报告保留；`pool-resource-real-corrected.trx` 二十八项通过。后续独立走查要求补完整内容观察，已用同一非池 setup 验证 CLOB string 和 BLOB byte[]；没有为断言新建 locator 影响池归还。最终相关实库回归包含该观察者检查，所有原失败均同名通过。

**最终门禁。** 报告均位于本地忽略目录 `TestResults/pool-resource-isolation/`，本次实际运行，不以历史结果代替：

| 检查 | 实际结果 |
| --- | --- |
| Release solution 及最终观察者集成项目构建 | 退出 0，零警告/错误 |
| `pool-resource-unit-final.trx` | 完整单测 5,198 通过，无失败/跳过，约 3 分 43 秒 |
| `pool-resource-noopt-final.trx` | 9 通过 / 98 NotExecuted；没有数据库 opt-in |
| `pool-resource-real-final.trx` | Connections + XaTests + RefCursorTests + LobTests，115 通过 / 4 配置跳过，约 29 秒 |
| 两个新增 C# 文件 scoped whitespace format 及 verify | 退出 0；既有 workspace-load warning 保留 |

原 5,170 项单测和含跳过在内的 91 个相关实库 case 均保留；新增本地二十八项、实库二十八项。四个实库跳过分别是两个 alternateDatabase 场景、unsupported-auth 端点和 autocommit-disabled 端点配置缺失；按个体 NotExecuted 计数，不将跳过计入通过。清理逐项独立尝试并聚合主失败；真实资源/连接/私有 source 释放后才执行专属表清理，本地 serving task 始终被观察。

**走查与限制。** 独立 reviewer 分别核对本地 fixture、生产资源保护路径、真实测试和观察者内容断言，长度检查不足已补齐，无剩余可操作问题；reviewer 没有执行这些门禁。当前实库限定已配置的 OceanBase4.4.2.2 双模式直连、Windows/.NET10、TLS disabled；新增资源只适用 Oracle profile，二十个持久 locator 的实际日志均为 inline=True。本地 CLOB partial 可能被文本 reader 预读，本模块不证明外置 DBMS_LOB.READ 分块、中途取消、任意并发/长期负载或其他部署兼容。

消费者/DI、打包、独立运行时、Linux/macOS、OBProxy、企业信任链和完整数据库套件没有重跑，源代码/API/依赖未变；前序消费者2,918/DI14仅为历史结果。第 15.1—15.42、报告第 12—37 节及 Oracle nested-table column 等原始失败完整保留。完整 P0-02/W1、23 ID 和八工作包仍开放；继续外置资源/fault/cancel/并发与 W2 响应丢失及协调器恢复验收。本模块完成门禁及走查后在当前分支本地提交，不 push 或发布。

### 15.44 2026-10-25 外置持久 LOB 读取与池生命周期验收

**范围与源码。** 从 `master/a426623` 继续 P0-02/W1；仅测试及文档，没有生产/API/配置/依赖修改。沿用 `LobReadStream` 的原 session/事务/epoch/reservation、分块 `DBMS_LOB.READ` 与 `IsLobRead`/`executesSql:false`；BLOB 每块至多32767字节，CLOB 每块至多8191个 Unicode scalar，流位置仍按UTF-8字节。新增[本地验收](../tests/OceanBaseConnector.UnitTests/PooledExternalLobLifecycleTests.cs)、[真实验收](../tests/OceanBaseConnector.IntegrationTests/Connections/PoolResourceIsolationTests.ExternalLobs.cs)，复用并补强[资源清理助手](../tests/OceanBaseConnector.UnitTests/PooledResourceIsolationTests.cs)。Java 固定参考的 `BlobOracleTest.testBlobINOutRow` 有64KiB/65KiB数据；本轮不更改全局 `ob_default_lob_inrow_threshold`，也未重跑Java测试。

**真实外置证据。** 独立非池连接创建专属表，用临时LOB写入并插入持久值，Free/Dispose种子后才打开单容量私有池；被测连接仅普通SELECT。BLOB为100000字节、字节i为i%251；CLOB为20000次“患者😀z”、220000个UTF-8字节。BLOB/CLOB×同步/异步四项都必须 `HasInlineData == false`，日志明确为inline=False；断言完整byte[]/string，全部资源释放后 `RequiresDiscardOnReturn=false`，Close/Open复用同PhysicalSession且generation递增，再执行SQL。首轮 `external-lob-real-first.trx` 四项即通过；最终相关套件包含同名四项，不以大小或已有内联结果推断外置。

**本地 in-flight 证据。** BLOB/CLOB×同步/异步的Close、handle Dispose、stream/text Dispose、原命令超时，以及异步caller取消，共18项。单一listener/同端点/MaxPoolSize=1；peer验证0xa1请求包含DBMS_LOB.READ并发出started门控，保持响应未返回再中断。旧transport必须EOF或精确ConnectionReset；重新认证新会话、旧资源读取/截断拒绝、迟到Dispose后新SQL成功，最后才ClearPool。caller取消保留原token；dispose是取消异常，Close是非unknown的OceanBaseException，超时是pending本身的TimeoutException。原命令1秒预算在五秒内完成，命令后来改30秒不改变locator预算；该宽松边界不宣称精确计时。原SELECT不因只读分块中断被标unknown。真实中途取消/fault不在本轮实库证据内。

**原失败与走查。** `external-lob-unit-first.trx` 为14通过/4失败，均因测试错误期待Close返回OperationCanceledException。`DetachForClose`/`FinishClose`直接销毁传输而未取消caller，`CommandExchange.Fail`按只读分块包装为非unknown OceanBaseException；修正精确类型断言后 `external-lob-unit-corrected.trx` 十八项通过。没有产品RED或生产修复。独立走查确认主体及真实外置证据，提出两处测试不足：清理丢弃未检查的pending错误、超时宽松上限不能排除默认预算。已在最终源码保留未观察读取错误并与primary/peer/cleanup聚合，补两项聚合回归；仅同一已在主体检查的异常去重，并加入五秒完成断言。走查没有执行门禁。

**最终门禁。** 原始报告在本地忽略目录 `TestResults/external-lob-pool/`，需要另行归档，命令输出为 `TestResults-external-lob-*.log`：

| 检查 | 实际结果 |
| --- | --- |
| Release solution build，`--no-restore` | 退出0，零警告/错误 |
| `external-lob-unit-before-review.trx` | 走查建议调整前完整单测5216通过；不是最终源码结果 |
| `external-lob-unit-final.trx`，Release `--no-build --no-restore` | 完整单测5218通过，无失败/跳过；新增18项请求中断、2项异常聚合，原5198项保留 |
| `external-lob-real-final.trx`，相同选项，双直连profiles及Connections/XaTests/RefCursorTests/LobTests过滤 | 119通过/4配置NotExecuted；原119个case保留，新增4项通过 |
| `external-lob-noopt-final.trx`，无opt-in、相同构建选项 | 9通过/99逐用例NotExecuted；新增theory无profile时计一个跳过 |
| 四个C#文件scoped whitespace format及verify | 退出0；既有workspace-load warning保留 |

四个配置跳过仍是两个alternateDatabase、unsupported-auth与autocommit-disabled端点；不算通过。本地pending和peer均观察，清理逐项尝试并聚合主失败；真实reader/stream/lob、连接/source和setup释放后再清理表。原四个Close失败最终同名通过，原本地及相关实库case无移除。走查建议调整前的完整5216结果另存，不覆盖最终5218源码门禁。

**限制与后续。** 实库仅配置的OceanBase4.4.2.2双模式直连、Windows/.NET10、TLSdisabled，新增四项仅Oracle；五资产并非五独立运行时。真实分块读取/释放后健康复用与本地请求中断分别记载，真实in-flight fault/cancel、任意并发/长期资源、OBProxy、企业信任链、其他版本/平台及完整reader/cursor/LOB/reset仍开放。消费者/DI/包/运行时样例/完整数据库套件未重跑，生产资产/API不变，历史2918/14不计本次门禁。第15.1—15.43及报告12—38、Oracle nested-table column原失败完整保留。23项与八包仍开放；继续W1资源故障/并发及W2响应丢失/协调器恢复，不push或发布。

### 15.45 2026-10-25 事务响应丢失与池恢复验收

**范围。** 从master/4c15e3c继续P0-04/W2，关联P0-02/P0-05。测试及文档交付，无生产/API/配置/依赖修改。既有unknown-outcome/事务脱离/池销毁保护符合契约，不虚构新增生产修复。[本地16项](../tests/OceanBaseConnector.UnitTests/PooledTransactionResponseLossTests.cs)、[真实16项](../tests/OceanBaseConnector.IntegrationTests/Faults/TransactionResponseLossTests.cs)、[测试中继](../tests/OceanBaseConnector.IntegrationTests/Infrastructure/TransactionResponseDropProxy.cs)、[中继自身4项](../tests/OceanBaseConnector.IntegrationTests/Faults/TransactionResponseDropProxyTests.cs)及[应用核对指南](unknown-outcome-recovery.md)。

**受控真实故障。** MySQL/Oracle×同步/异步×COMMIT/ROLLBACK×整包丢失/载荷截断，共16项。独立非池连接创建随机专属表并观察结果；池连接完成业务写入，观察者在提交前看不到。中继仅匹配已armed的sequence0/COM_QUERY/精确目标SQL，请求原样转发；先读完真实服务器一个响应frame，再完全丢弃或交付原长度header和一个payload字节后关闭两端。最大frame1MiB，只接受原本TLSdisabled、无compression/OB20、单端点profile，不更改DB全局设置、不降级TLS、不记录认证载荷。

客户端必须得到unknown-outcome的OceanBaseException、Broken及失效旧事务；中继记录真实响应marker0、独立观察者核对实际提交1行/回滚0行。MaxPoolSize=1、同中继端点Close/Open得到新物理会话；旧Commit/Rollback拒绝，旧事务迟到Dispose后，新租约隐式写可见、新事务回滚正确，最后才source.Clear/Close。目标请求跨两会话计数：旧目标一次；rollback场景再有新事务显式ROLLBACK一次，不将这次正常请求误判为replay。首轮real16即通过；本地16项也首轮通过，证明失效/替代生命周期而不代替真实数据结果。

**fixture原失败和走查。** 初构建CS0266/CS1503来自两处同步BeginTransaction结果缺显式强转，修正后0警告/错误。relay-first两项失败：C#变长`\x03COMMIT`将C并入十六进制转义，发出0x3c而非COM_QUERY的0x03，目标未命中导致超时；改为固定`\u0003`后corrected2通过。该原报告另记录Dispose与下一Accept竞争的Not listening次生错误。独立走查主体通过，指出该清理竞态；只在Accept边界且停止token已取消时接受NotListening/精确Socket中止，其他错误保留。新增两项正反回归：停止后返回null，未取消时仍抛InvalidOperationException；reviewed4通过。以上均为测试基础设施修正，不是生产RED。中继两条转发Task和上层主失败/清理均被观察或聚合，无延后走查项；reviewer未执行门禁。

**应用示例。** 同事务写入稳定operation_id标记，unknown后用新连接读权威已提交结果。标记缺失或核对失败保持待核对，不把未知写包装成安全重试。指南中现代TFM的C#查询示例抽取后独立编译。补强ambient隔离时误用不存在的Enlist属性，`TestResults-transaction-loss-guide-build-final.log`真实结果为CS0117/1错误，原失败保留，不能引用旧成功日志覆盖。改用BCL的TransactionScopeOption.Suppress并启用AsyncFlow后，`guide-build-suppressed.log`重新编译0警告/错误；核对查询不重新登记旧scope。没有声明对该示例执行了应用事务或外部协调器恢复测试。

**最终门禁。** 本地忽略报告`TestResults/transaction-response-loss/`，原输出`TestResults-transaction-loss-*.log`须另行归档：

| 检查 | 本轮实际结果 |
| --- | --- |
| Release solution build --no-restore | 退出0，零警告/错误 |
| `transaction-loss-unit-final.trx`，Release --no-build --no-restore | 完整5234通过，无失败/跳过；原5218保留，新增16 |
| `transaction-loss-real-final.trx`，同构建选项，两种直连profile | Connections/Transactions/Faults/XaTests/RefCursorTests/LobTests合计160通过/4配置NotExecuted；含4项无需真实数据库的本地中继校验，不能全部称为实库case |
| `transaction-loss-real-first.trx` | 新增真实16通过；最终同名保留 |
| `transaction-loss-noopt-final.trx`，同构建选项，无opt-in | 13通过/100逐项NotExecuted；通过中新增4项中继校验 |
| 四新增C#的scoped whitespace format及verify | 退出0，既有workspace-load warning保留 |
| `guide-compile/Guide.csproj` | 从指南抽取的查询示例编译0警告/错误，仅.NET10 |

原5218单测和上模块123个相关case（119通过/4配置跳过）均按名称保留；新16个核心单测/16个实库case/4个非DBfixturecase分别计数。四个配置跳过仍为两个alternateDatabase、unsupported-auth/autocommit-disabled端点。独立观察、旧资源与source先关闭，中继任务结束后清理专属表；清理失败聚合主失败。限定格式与UTF8-noBOM/CRLF、链接/锚点、三张23-ID表、八包唯一归属及历史保留检查不代替行为验收。

**边界与继续。** 实库仅已配置OceanBase4.4.2.2双模式直连、Windows/.NET10、TLSdisabled。故障发生在真实服务器已返回响应之后，不证明响应发送前/业务执行中断、服务器crash、事务管理器crash、真实OBProxy/TLS/跨平台/长期并发。消费者/DI/打包/独立运行时/完整数据库套件未重跑；历史2918/14不计本次门禁。完整P0-03/P0-04/P0-05/W2及23项、八包仍开放，继续发送边界/超时/in-doubt及协调器恢复。第15.1—15.44与报告12—39、全部既有失败/跳过保留。本地master提交，不push或发布。

### 15.46 2026-10-25 Oracle 输出执行与 FetchSize 提示

**交付范围。** 从master/a07957e继续P1-06/W5。[共享命令实现](../src/OceanBaseConnector/OceanBaseCommand.cs)不再拒绝Oracle文本OUT/RETURNING、过程/函数执行的正命令FetchSize；显式Prepare与隐式PrepareExecute都适用，公共属性值不变。[契约](obconnector-dotnet-architecture-design.md#oracle-output-fetch-hint)明确该值仅是server-cursor每次FETCH的行数提示，内部输出并不是fetch cursor：共享operation的有效fetchSize为0，请求仍cursorFlag0，不对内部输出发COM_STMT_FETCH，不虚称实现输出结果分页。返回REF CURSOR独立使用GetDataReader(fetchSize)，默认128。普通查询游标分页保持原行为；输出UseCursorOffset与ExecuteArray正FetchSize发送前仍拒绝，MySQL过程限制不变。

**实现依据与边界。** 固定Java参考6adc311/2.4.14的AbstractQueryProtocol对非SELECT使用NO_CURSOR，对internal/callable输出走非cursor result。这里只借鉴协议区分，不把Java源码当运行验证。最小修改在Start统一形成同步/异步快照；PrepareExecute编码、QueryResult保护、输出原子发布、父statement/cursor所有权和错误/取消状态机沿用原实现。没有新增API/配置/依赖，也没有增加REF CURSOR输入/列值或其他结果形状。

**RED与覆盖。** 新增24项在原代码全部因明确FetchSize拒绝而失败（output-fetch-red.trx）；本次生产调整后初步26通过（含2项限制回归）。复用原测试core并保留所有旧case，新增46本地项：准备/执行所有权16、REF CURSOR独立分页8、RETURNING空输出与INOUT8、多结果/提前关闭4、已知尾部或后续错误4、取消/超时不发布不重放4、无参数过程2。父命令hint1与返回cursor FETCH2逐包区分，int.MaxValue提示不成为巨量FETCH；等待尾部、旧值、会话分类和close均保持原断言。最终聚焦57通过，包含11项原offset/数组限制回归。消费者新增30项覆盖五资产×同步异步×普通/OB20/非法trailing包，检查cursorFlag0与完成后CLOSE，保留原错误输出和Broken断言；全部在同一.NET10host运行。

**真实证据。** 新增20项覆盖Oracle单profile×同步异步×显式/隐式准备×block/RETURNING/procedure/function/REF CURSOR。标量输出、真实写入行数与Prepare前无写入分别断言；cursor返回7/8/9三行，在父命令FetchSize2时使用GetDataReader1，父命令释放后仍完整读取。首轮44相关项通过但未包含普通Reader；最终补ReaderTests后58通过，含双模式普通cursor和Oracleoffset，共20新增/38既有，无跳过。没有修改全局参数，专属对象由既有DatabaseContext清理。

| 最终检查 | 实际结果与原始入口 |
| --- | --- |
| Release solution --no-restore | 0警告/0错误；TestResults-oracle-output-fetch-build.log |
| 完整核心单测 --no-build --no-restore | output-fetch-unit-final.trx：5280通过，无失败/跳过；新增46，原5234同名保留 |
| 受影响消费者，同构建选项 | output-fetch-consumers-final.trx：510通过，无失败/跳过；新增30/原相关480；不是重跑全部消费者 |
| 双模式直连相关实库，同构建选项 | output-fetch-real-final.trx：58通过，无失败/跳过；StandalonePrepare/RefCursor/Procedure/ArrayBinding/Reader；全部20新增同名保留 |
| 无opt-in Integration，同构建选项 | output-fetch-noopt-final.trx：13通过/101逐项NotExecuted；不将未展开理论用例数当真实用例数 |
| 七个变更C#的限定format/verify | 退出0；既有workspace-load warning保留，不等于无workspace警告 |

原始报告保留于忽略目录TestResults/oracle-output-fetch/和根目录TestResults-oracle-output-fetch-*.log；RED24未覆盖，源码独立走查无可操作发现，reviewer未运行测试。结构核对通过：三张23ID表、八包唯一归属、原历史章节/旧case完整保留、504个本地链接/锚点有效，精确14文件及UTF8-noBOM/CRLF通过。未重跑完整消费者/DI/包消费/独立运行时/完整数据库套件。实库为既有OceanBase4.4.2.2双模式直连、Windows/.NET10、TLSdisabled；新增20仅Oracle，不外推代理、企业TLS、其他平台/版本或完整输出故障矩阵。第15.1—15.45/走查12—40及历史失败/跳过保留，全部23ID/八包和P1-06/W5仍开放；本地master提交，不push/发布。

### 15.47 2026-10-25 复杂类型 national 成员与方向编码

开发基线 `master/a1dddde`，P1-07/W5。NCHAR208/NVARCHAR2207成员接入命名/嵌套object及collection、标量匿名工厂数组和ExecuteArray。CreateValue仅做严格Unicode/shape/预算验证，无I/O；执行期按当前可信连接编码重新编码，递归检测覆盖普通/KeyInfo/数组路径。普通VARCHAR2/CHAR仍UTF8，NULL位图、深度/成员/字节上限、会话/事务/DDL代数、结果快照及输出原子性保留。[完整契约](obconnector-dotnet-architecture-design.md#complex-national-members)。

实库证伪了初始复用scalarnational编码的方案：4.4.2.2的NLS_NCHAR_CHARACTERSET=AL16UTF16，普通复杂表列national原始字节为UTF16BE，外层collation63；复杂输入按connection字符集解释，PL输出按sessionnational转换。字典发现national成员时，在同一预留会话/事务内查询NLS_DATABASE_PARAMETERS，继承原超时和取消来源，仅接受AL16UTF16；先读完合法单行响应再校验/缓存，不猜外层63或sessionnational。普通binary named成员用存储编码，PL OUT/INOUT用结果snapshot national编码。

仍不支持其他数据库national编码、无可信存储上下文的匿名普通非NULL national结果、复杂整列RETURNING未解析源身份、LOB/cursor成员、循环及text复杂结果。匿名工厂PL输出只有本地/五资产证据，实库匿名数组只覆盖DML。RETURNING不能靠同名类型猜owner：当前拒绝保留旧输出、报告unknown并销毁会话，应用须核对写入；8个拒绝边界不算正向兼容性。历史nested-table表列失败与collection过程参数成功分别记录。

| 原始报告或阶段 | 结果与含义 |
| --- | --- |
| layout RED / real RED | `complex-national-red.trx`2fail；`real-red.trx`16fail，均原代码不支持national布局/字典；实库SET/DDL已成功 |
| 初始编码实现 | `real-first.trx`4pass/12fail：错误套用scalar编码；`direction-red.trx`4pass/2fail确认golden输入编码；原预期保留 |
| 编码修正 | `real-corrected.trx`16pass，原正向矩阵通过 |
| 专项fixture迭代 | focused-first日志CS1503漏显式事务转换；focused-second23pass/24fail、third39pass/8fail、fourth47pass；修正BeginCommand、DML每迭代结果行及national查询回复，未削弱逐包/字节断言 |
| NLS独立走查 | 先拒绝再读EOF会误丢弃健康连接；`nls-mutation-red.trx`4fail实际复现；先完整消费再校验/发布后拒绝、复用和重新加载通过 |
| expanded真实 | `real-expanded.trx`40pass/8fail，nested16及匿名DML16通过；8fail为复杂整列RETURNING身份问题，未修复，后单列负向边界 |
| 首轮完整单测 | `unit-final.trx`5323pass/3fail：两条旧207/208拒绝预期过时；一次既有同步partial REF CURSOR替代握手超时，原报告保留且未改该路径 |
| 首轮相关消费者 | `consumer-first.trx`140pass/40fail：新fixture关闭命令前漏BeginCommand；请求/输出字节已通过，非产品RED |
| 首轮相关实库 | `real-final.trx`63pass/2fail：历史collection表列服务端拒绝；旧objectfixture在建表DDL前加载描述，触发正确失效保护 |
| 后续完整单测 | `complex-national-unit-reviewed.trx`5325pass/1fail：既有RSA测试等待服务器收到握手响应超时，尚未进入公钥等待阶段；RSA专项`rsa-investigation.trx`10pass，未独立复现，不能断言产品根因已修复 |
| 沙箱串行诊断 | `complex-national-unit-serial.trx`5187pass/139fail，其中109项直接报Windows PFX导入CryptographicException，另有超时、认证及消息断言失败。相同TLS专项在沙箱外`tls-investigation.trx`5pass；不以这5项替代全量门禁，也不逐项推定139个失败的根因 |
| 最终完整单测 | `complex-national-unit-verified.trx`5326pass，沙箱外禁用collection并行；未改RSA/TLS超时或断言。新46、总数未减少；三条旧拒绝case按新契约迁移，不声称所有旧名称原样保留 |
| 最终相关消费者 | `complex-national-consumer-reviewed.trx`180pass，新40+旧140子集；五TFM×两national×同步异步×NCHAR/NVARCHAR2，均为同一.NET10host |
| 最终相关实库 | `complex-national-real-reviewed.trx`64pass/1fail：新48正向+8RETURNING负向通过；旧object先DDL后load修正后通过；历史collection表列服务器拒绝保留 |
| no-opt-in | `complex-national-noopt-final.trx`13pass/105NotExecuted，不把跳过算实库验收 |
| 构建/格式 | Release solution0警告/错误，18变更C#限定format/verify退出0，既有workspace加载警告保留 |

源码入口：ComplexType/ComplexValueCodec、NationalCharacterSet、ComplexTypeContext、Connection.ComplexTypes、ComplexColumnMetadata、PreparedParameters、OraclePrepareExecutePacket、OceanBaseCommand/OceanBaseComplexType及ResultColumn。新增46单测含golden/nested/NULL/framing、坏字节/surrogate、预算、工厂/目录、拒绝后复用、原子输出及数组。原NVARCHAR2名称拒绝case改为NVARCHAR2(4)，原207/208不支持元数据两例移至正向NULL/空集合映射；其余旧单测保留。相关180消费者还覆盖旧ordinarynational、complexdictionary及Oracle输出OB20/TLS子集，不是全量consumer。

最终65实库过滤为ComplexNationalTests、ComplexTypeTests、ArrayBindingTests、OraclePrepare，profile仅oracle-direct。48正向覆盖NCHAR/NVARCHAR2、UTF8/UTF16 national、同步/异步、命名对象/集合、嵌套及匿名DML；普通对象buffered/sequential核对后列42。8个RETURNING guard不计正向支持。旧complex对象fixture只把建表DDL移到加载/构造值前，未放松生产freshness。首轮NuGet网络还原失败、consumer CS0173、诊断未完整读完的失败日志也保留。

独立review发现并修复NLS问题，复核无剩余可操作源码发现；reviewer未运行门禁。实库仅Oracle4.4.2.2直连、Windows/.NET10/TLSdisabled/DBAL16UTF16；五资产非五独立运行时。不推定OBProxy、企业TLS、其他数据库national配置/平台、完整DDL/资源或负载兼容。未重跑全量consumer/DI/包/独立运行时/完整数据库。原始报告位于忽略目录TestResults/complex-national及TestResults-complex-national-*.log，交接需另存。全部23IDs/八包及15.1—15.46/报告12—41历史保留；只本地master提交，不push/发布。

### 15.48 2026-10-25 Closed 回调中的满池重开与清理资源释放

从 `master/80ddfbe` 开发，归属P0-01/W1。旧路径先发Closed事件，再回滚旧事务和归还物理会话；回调同步Open同一容量1池时等待自己尚未归还的槽位，有限等待超时，PoolWaitTimeout=0可无限等待。修复在Closed事件的ExecutionContext内保存捕获清理链，池Rent/RentAsync在已有租赁reservation的try/finally内先完成整条链，再正常借用；同一或另一逻辑连接均覆盖，嵌套A.Close→B.Close→A.Open先清理B和A。[公共契约](obconnector-dotnet-architecture-design.md#pooled-close-callbacks)。

逻辑Open预留先于旧资源清理，阻止Closed日志回调抢占同一逻辑连接。旧session/transaction/exchange/endpoint/pool/key/timeout均使用Detach快照；清理CAS只执行一次，共享完成/异常，执行时屏蔽整条ambient链防止递归。同步借用同步清理，异步借用等待异步清理；新借用取消不能遗落旧清理，异常释放原rent预留。事务/重置失败仍销毁旧会话，不绕过容量或复用未清理资源。没有回调Open时保留早期Closed事件及原日志顺序，外部Open与pending async rollback的原交错和旧reader代数检查保留。

独立走查发现P2引用保留：回调派生长期任务可继承旧scope，两个清理delegate持续引用旧连接资源；先领取delegate再清空scope字段，异步还须在finally释放跨await提升的局部delegate并恢复Current，之后才发布共享完成/异常。四条弱引用回归保持scope与捕获ExecutionContext存活，覆盖同步/异步、成功/异常；不使用延时或放宽GC断言。

| 原始报告或阶段 | 结果与含义 |
| --- | --- |
| red.log / red.trx | fixture状态int→byte编译错误；首轮16失败被正常EOF误报掩盖，原报告保留 |
| close-callback-red-corrected.trx | 16失败均为实际pool wait Timeout，产品RED |
| close-callback-green.trx | 12通过/4失败：原自等待已绕过，MySQL fixture错误假定默认隔离；改为显式ReadCommitted，不改变产品默认 |
| close-callback-expanded.trx / focused.trx | 68通过；后98通过，含原回调/日志/事务归属回归；它们不替代最终全量 |
| build.log / build-corrected.log | 实库fixture漏OceanBaseTransaction显式转换的CS1503保留；修正后Release0警告/错误 |
| close-callback-unit-final.trx | 5414通过，新增memory4尚未编入且P2尚未修复，不作最终门禁 |
| close-callback-consumer.trx | 173通过，但过滤器漏掉新partial Ob20ConsumerTests中的40条回调用例，也未纳入20条旧reader租约消费者；纠正选集后重跑，不把173称为包含新40 |
| close-callback-memory-red.trx | 4失败，确认scope持有旧资源 |
| close-callback-memory-green.trx | 清空scope字段后3通过/1异步成功失败；原报告保留 |
| close-callback-memory-green-local.trx | 显式释放异步提升局部delegate后4通过；随后按review调整为先释放/恢复Current，再发布TCS |
| close-callback-unit-verified.trx | 首次最终源码全量5417通过/1失败；既有OracleOutputCursorRetirementProtectsTheNextSameEndpointBorrower同步partial在重开握手超时，原报告保留 |
| close-callback-handshake-recheck.trx | 原失败方法8项及scope18项共26通过；源码未改、超时/断言未放宽，未确认原超时根因 |
| close-callback-unit-accepted.trx | 同一最终二进制完整复跑5418通过，沙箱外禁用collection并行；对5326基线新增92，无总数减少。不把复跑视作原超时根因已修复 |
| close-callback-consumer-verified.trx | 最终233通过：原173+新回调40+旧reader20；五目标资产共用一个.NET10host |
| close-callback-real.trx | memory修复前历史74通过/1NotExecuted，新增16及58既有通过；不是最终源码实库门禁 |
| close-callback-real-verified.trx | 最终74失败/1NotExecuted；两profile首次Open初始greeting读取前端点关闭。独立TcpClient连接后不发送凭据/SQL，读取8字节也返回0；当前实库验证受阻，未证明最终兼容。ChangeDatabase因未配置alternateDatabase能力跳过 |
| close-callback-noopt-verified.trx | 限相关pool及Configuration选集9通过/12NotExecuted；不是历史13/105选集 |
| build-verified.log / format final | 最终Release0警告/错误；9C#限定format/verify退出0，原workspace加载警告保留 |

新增92单测=74公共路径+18scope回归：基本64涵盖双模式、同步/异步Close、活动事务、同/异逻辑借用者、同步/异步Open和等待0/1；另嵌套4、抛错4、日志预留2。scope覆盖竞争/异常共享、全链屏蔽与恢复、延迟ExecutionContext、清理失败的两种rent预留、新借用取消和GC资源释放。既有2条 `ClosedCallbackCannotBypassEquivalentPoolCapacityAndFailureReturnsOriginalSession` 迁移为 `ClosedCallbackReopensEquivalentPoolAfterReturningOriginalSession`，由预期超时改为成功同物理会话复用，仍断言仅接受1个socket；不声称全部旧测试名称不变。

新增40消费者=五TFM×双模式×同步/异步Close×同步/异步Open，公共API、PoolWaitTimeout=0、同socket reset/ping/回调内及回调后SQL逐包验证。新增16实库=双profile×同步/异步Close×活动事务×同/异逻辑借用者；私有MaxPool1、真实表隐式提交/回滚、相同物理对象及generation递增。建表后先Close/Open建立干净基线，避免DDL状态销毁干扰。这16项在memory修复前历史报告通过，最终复验均在初始Open失败，未执行本模块行为；不能用历史结果代替最终源码实库验收。历史实库环境为4.4.2.2 mysql-direct/oracle-direct、Windows/.NET10/TLSdisabled；不是OBProxy、企业TLS、跨平台或五独立运行时验收。

源码入口：[CloseCallbackScope](../src/OceanBaseConnector/Internal/Session/CloseCallbackScope.cs)、[pool rent](../src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs)、[captured detach](../src/OceanBaseConnector/OceanBaseConnection.Transactions.cs)、[Close入口](../src/OceanBaseConnector/OceanBaseConnection.cs)；回归为CloseCallbackPoolingTests、Session/CloseCallbackScopeTests、PoolConfigurationLifecycleTests、CloseCallbackConsumerTests及Integration/Connections/CloseCallbackPoolTests。原始报告和manifest在忽略目录TestResults/close-callback，交接需另存。

本修复只移除Closed事件等待自身未完成归还的路径。事务日志回调同步Open另一逻辑连接并争用自身满池，仍可等待未完成事务清理；其他借用者引起的正常竞争也仍存在，不声称通用日志重入、完整并发、长期资源或reset验收完成。独立最终源码/测试走查未发现新可操作问题，reviewer未执行门禁；当前单测和消费者证据支持本地保存，实库端点恢复后仍需重跑最终报告，未改变服务器/全局配置。历史nested-table表列拒绝、复杂整列RETURNING身份及RSA等待未确认根因均未被本模块修复或覆盖。全量consumer/DI、包、独立运行时、完整实库与发布未重跑；全部23ID/八包开放，只本地master提交，不push/发布。

### 15.49 2026-10-25 连接级服务器错误与显式连接重建

从 `master/917e9cc` 开发，归属 P0-05/W2，并覆盖 P0-02/P0-04 的失败会话及 unknown 边界。固定 Java 参考仍为 Connector/J2.4.14 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`；FailoverProxy 对 SQLSTATE 前缀08及精确70100/1927的分类提供源码依据，不意味着本驱动实现了 Java 的 failover 策略。

共同 `CommandExchange.CheckPacket` 入口将 ordinal `08*` 或精确 `1927/70100` 识别为连接级失败。先验证完整响应边界，再 Break，再 Complete(error) 释放交换；保留错误码/SQLSTATE及通用脱敏消息。普通 `1317/70100` 查询中断、`1927/HY000`、`1064/42000` 不按此规则分类为fatal，保留原清理/退休语义；干净普通命令的邻居回归仍可继续执行，脏piece或cursor错误原有销毁规则保留。失效连接不回池；旧事务 Connection=null、再次执行拒绝，显式 Close/Open 才重新获取凭据、发现地址及新租约。不自动重放、发送 Batch 后缀或迁移事务。[公共契约](obconnector-dotnet-architecture-design.md#connection-level-server-errors)。

unknown采用 `connectionFailure && 当前_sqlSent`，不用整个交换的_anySqlSent，也不限08007：代理连接错误不能证明已发送业务未执行。Prepare、业务前显式executesSql:false的目录请求和piece确认不标记业务unknown；已确认父响应后的显式非执行请求不反向污染父操作。Control隔离/autocommit、COMMIT/ROLLBACK、ChangeDatabase等保留原执行标记；FETCH不能只按opcode推断，原execution仍待完成的cursor和独立REF CURSOR交换不同。

独立走查发现Oracle piece确认的旧分支会在Break后再次ResponseComplete，覆盖可信错误为code0；改为互斥分支。另对完整裸协议ERR读完后取消的竞态，PacketChannel仅在closed、无activePayload且未启用OB20/压缩时接受已消费边界；已关闭帧协议仍保守失败，不能因Broken跳过校验。坏帧不保留不可信code/state，取消的帧协议仍按原unknown/cancellation映射处理。

| 原始报告 | 结果与证据范围 |
| --- | --- |
| fatal-server-error-red.trx / green.trx | 原始最小同步/异步2失败（Expected Broken / Actual Open）后2通过 |
| expanded.trx | 初始扩展34通过/12失败；夹具请求序列、reader预读及Batch缺少会话变量报告造成；原报告保留 |
| expanded-v3.trx | 修正夹具后58通过；expanded-v2日志中的ReceiveAsync参数编译失败保留 |
| expanded-red.trx | 用917e9cc精确生产文件复跑同58条：48失败/10通过；finally恢复当前生产字节，证明原缺陷与普通ERR/坏帧控制 |
| piece-red.trx | 独立走查发现的piece错误码丢失：2失败，Expected2013/Actual0；修正互斥清理分支 |
| cancel-red.trx | 解析前取消3失败/3通过，其中piece2已通过，另1同步API无callerToken故未真正取消；移除该无效theory行，最终仅保留有效裸取消3条 |
| focused.trx | 66通过，含后来删除的无效同步callerToken行，且不含原最小2条；不作最终总数依据 |
| unit-final.trx | 首次全量5484通过/1既有partial-cursor夹具失败，服务器tail写reset阻断替代连接accept；原报告保留 |
| recheck.trx | 67新增及原cursor八种阶段共75通过，包含夹具修正后的partial路径 |
| unit-accepted.trx | 最终完整5485通过=5418基线+新增67；旧单测不删减、不排除 |
| consumer-final.trx / consumer-accepted.trx | 原142通过/30失败均为Oracle必选探测夹具假设；改为optional-probe后同172条全部通过，产品二进制不变 |
| real-final.trx | 仅真实CommitRollbackAndDisposeSemantics双profile×同步/异步4条，全部首次Open初始greeting前失败，未执行fatalERR或事务行为 |
| noopt-final.trx | 限配置及相同事务选集9通过/2NotExecuted，不代表全量实库 |

上述表内文件前缀均为 `fatal-server-error-`，原始TRX、日志及manifest在忽略目录TestResults/fatal-server-errors，交接须另存。新增67单测=原最小2+双模式/同步异步/池开关/三种fatal码24+普通邻居6+prepare/commit/rollback/ChangeDatabase8+流式首行前后4+Batch2+OB20/压缩边界8+父响应已确认2+resolver/旧reader2+piece2+有效裸取消3+关闭帧取消4。新消费者80=五资产×双模式×同步异步×三fatal码60，加五资产×同步异步×标准/OB20的piece20；172还包含既有piece20、reader20、压缩TCP/TLS40及OB20故障12。同一.NET10host不代表五独立运行时。

首次完整单测的当前夹具根因有精确服务器栈：PooledResourceIsolationTests在partial分支读第一行后Close，服务器尚在写终止包而reset；随后无法接受替代连接，后者读greeting超时。采用RunContinuationsAsynchronously的pageSent，在服务器尾包写完后通知partial客户端再Close；客户端仍不读第二行/尾包，原资源退休/旧对象/新租约断言保留，不吞异常、不放宽超时。该证据只解释当前失败，历史15.48及更早超时仍保留原未确认结论。

最终Release0警告/错误，6C#限定format/verify通过并保留workspace加载警告。独立最终代码/测试走查未发现新可操作问题，reviewer未运行门禁；文档复核收紧普通ERR保留清理语义及显式非执行metadata边界。真实端点独立无凭据/SQL探针读8字节返回0，耗时5035ms；当前四条实库回归受阻，不把本地ERR注入称为真实服务器故障验收。未改服务器/全局配置、未增加API/选项/依赖。完整发送、并发/长期资源、真实OBProxy/TLS/认证/平台/独立运行时/协调器/包验收均未闭合；历史national、nested-table、RETURNING及RSA问题保留。全部23ID及八包开放，仅本地master提交，不push/发布。

### 15.50 2026-10-25 TCP 建连重试预算与退避

**开发基线与范围。** `master/0ec3e1c`，P1-09/W4。新增 `Connect Retry Count`（默认0，0..10，额外完整候选轮）及 `Connect Retry Interval`（默认1，1..60秒，即使count0仍验证）。所有五资产均提供 builder 属性/常量、规范序列化、不可变配置及端点/凭据复制；策略进入有效池身份，省略与显式默认值等效。[公共契约](obconnector-dotnet-architecture-design.md#connect-retry-policy)。未新增依赖。

**行为。** 每次物理建连选取一次 Sequential/RoundRobin 起点；整列表 TCP 失败后才等待，轮内候选之间不等待，最多候选数×(count+1)次，最终失败不额外等待。仅 Attach 前 IOException 内含 SocketException 可重试。TCP一旦成功，greeting/TLS/auth/identity/初始化/业务错误直接返回；没有活动会话自动重连、事务迁移或SQL replay。每次逻辑Open只获取一次凭据/解析一次地址，预热及各轮复用同一快照。

**共享预算及走查修复。** count>0把逻辑Open纳入既有共享预算，包含池等待/预热/探活、各TCP及退避、握手及自动ambient初始化。因此Pool Wait Timeout即便更长或为0仍受Connection Timeout约束；count0原等待语义不变，Connection Timeout0仅取消时间上限，不取消次数上限。独立走查发现无callbacks的MinPool2原先每条物理连接重置超时；普通池/数据源×同步异步4RED均在Timeout2、两次1200ms greeting下错误成功，现共享令牌修复并检查后续显式Open恢复容量。应用同步回调仍须及时返回并响应取消。

**证据。** 最终完整单测5549通过（新增64：重试61、池身份3），相关五资产消费者174通过（新增52）；Release全解决方案及修正后消费者构建0警告/错误，7C#限定格式验证通过并保留既有workspace警告。五资产在同一.NET10host消费；无凭据/无SQL探针5022ms读0greeting，本模块没有新增实库互操作验收。

**原始失败。** 最小6RED均未知关键字；补配置后2通过/4TCP失败，随后首轮70通过、扩大113通过。保留CS1729私有端点构造误用、消费者CS0103局部Expect误用；首个相关消费者174条中162通过/12失败，均path2finally在DataSource Dispose后再次Clear，修正为自有源仅Dispose、普通连接才静态清池后同174条全通过，五个产品DLL不变；以及并行build/test导致3个MSB3026占用警告；之后顺序构建。预热4RED按预期未抛Timeout。首个125专项121通过/4失败，均新增双候选夹具在Listen前Accept；添加listening信号后保留同名用例和原断言。文档脚本首次默认GBK读取UTF8失败，没有文件写入，随后显式UTF8。沙箱首次NuGet审计NU1900，正常环境后续构建成功；不将在线依赖审计记作通过。

**边界与后续。** RR专项覆盖首次Open的全轮顺序，不声称旋转后所有起点已穷举。五资产消费者同一.NET10host，非五独立runtime。OCP/TNS或等价发现适配、健康缓存、代理拓扑/reroute、真实故障/负载/企业信任及跨平台/独立运行时/发布验收仍缺；23ID及八工作包不关闭。原有实库握手前失败保留，本模块本地TCP夹具不替代实库互操作。只本地master提交，无push或发布。

### 15.51 2026-10-26 端点TCP健康缓存

**开发基线与配置。** `master/bc84ba1`，P1-09/W4。新增 `Host Recheck Seconds` / `OceanBaseConnectionStringBuilder.HostRecheckSeconds` / `HostRecheckSecondsKey`，默认0（关闭），范围0..3600秒；五资产均保留规范序列化、不可变快照、端点/凭据复制和有效池身份。省略与显式0等效，不同值隔离普通池。[公共契约](obconnector-dotnet-architecture-design.md#endpoint-tcp-health)。没有新增依赖。

**候选与反馈。** 每次物理Open先取既有Sequential/RoundRobin起点，再按近期TCP失败稳定分组：未记录近期失败的候选在前，近期失败候选在后；所有候选和重复地址均保留，全部失败时仍可尝试恢复。顺序在本次物理Open及其重试轮次内冻结，TTL到期不会修改已选轮次。只记录Attach前IOException内含SocketException且预算未取消的失败，包括最后一次失败；TCP成功立即清除该端点记录。greeting/TLS/认证/身份/SQL错误及取消或已到期预算不污染TCP记录。它不证明服务器、租户或业务健康，也不触发活动连接迁移和SQL replay。

**容量、归属及代次。** 每个缓存最多1024个精确host/port键（ordinal，不合并大小写或DNS别名），用单调时钟并惰性过期；满时淘汰最早失败。普通非池连接按自己的selector寿命保存，非池数据源按共享selector保存；普通池的匹配借用者/预热共享池缓存，私有数据源池独立。配置、resolver/凭据/TLS策略的既有池身份隔离不变。对池缓存，Clear、凭据版本退休和Dispose替换缓存对象；非池缓存随selector存活，清池API不清其记录；旧借用者/在途尝试继续其生命周期，迟到成功或失败不能写入新代次，预留容量后再Clear再创建的竞态也与新缓存隔离。空普通池registry回收会销毁缓存，不为了保留健康记录延长空池寿命；没有进程级全局地址表或后台探测。

**回归与走查。** 公共路径覆盖同步/异步、MySQL/Oracle、同一非池连接重开、两个普通池借用者、私有池/非池数据源，以及普通池/私有源MinPoolSize2预热；覆盖TTL、显式0、全部失败后恢复、最后失败、取消、greeting/TLS/auth/identity终止边界、清池交错和策略隔离。内部单测验证稳定轮转/重复项、成功清除、已冻结顺序、1024上限、1500地址变更、4000并发操作及Clear/凭据代次。成功清除、完整轮转与预留后构造竞态主要为内部单测证据。五资产消费者验证公共选项和第二次物理建连选择，不将资产数当独立运行时数。

**验证。** 最终完整单测5620通过（新增71：健康缓存69、池身份2），相关五资产消费者226通过（新增52），均零失败/跳过；Release全解决方案0警告/错误，12C#限定格式验证通过并保留既有workspace警告。五资产同一.NET10host消费；本模块没有新增实库互操作验收。

受限首轮完整单测5480通过/140失败：109为PFX导入、5为Schannel凭据、20为VerifyFull预期不匹配、5为silent TLS等待超时、1为既有异步拓扑超时；相关消费者146通过/80失败，80项均在TestCertificateFactory的PFX导入。相同10个TLS/清池用例普通用户环境通过后，保持源码、夹具、预算和五资产二进制不变，完整单测5620通过（9分21秒）、同226消费者通过（6分31秒）。所有初次失败均同名通过，原报告不删除；拓扑超时原因尚未确认，不声称修复其根因。完整5549单测基线及174消费者基线全部保留。报告位于忽略目录TestResults/endpoint-health，执行日志为根目录TestResults-endpoint-health-*.log，应单独归档。

**保留原始失败。** 初始18项未知关键字RED；只加配置后2通过/16失败，重复Open仍先尝试失败地址；首轮84通过、扩大126通过。故意删除RetireSessions换缓存行时六个Clear/轮换用例全部失败，随后恢复原始源码，六项加八项策略隔离和一个预算边界共15项通过。原超时夹具曾假定loopback拒绝耗时，独立走查指出跨平台不成立；现从首次失败TCP计量回调开始监听并延迟1200ms，使Connection Timeout1在失败反馈前到期。该用例证明本地预算/反馈边界，不是实际网络TCP超时或Linux运行证据。首次受限全量的TLS/PFX环境失败及普通用户复测另见最终验证，不删除原报告。

**参考与后续。** Java固定参考`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`的`loadBalanceBlacklistTimeout`默认50秒，仅作源码先例；.NET默认0并保留全候选fallback。OCP/TNS或等价发现适配、proxy拓扑/reroute、真实节点/代理故障与TCP健康策略验收、企业信任、长期负载、跨平台/独立运行时及发布矩阵仍开放。此前探针和直连报告均为历史；本模块没有新增实库互操作。23ID及八工作包不关闭；仅本地master提交，不push或发布。

### 15.52 2026-10-26 TNS文件地址发现

**开发基线与接口。** `master/9dc3f06`，P1-09/W4。新增五资产公共不可变 `OceanBaseTnsAddressResolver(filePath, netServiceName, expectedServiceName = null)`，接入既有 `IOceanBaseAddressResolver`、连接 AddressResolver 与现代数据源 builder。每次逻辑 Open 重新读取文件，发现失败不得借用池内旧会话；不更改凭据、database、TLS 或既有池归属。[公共契约与示例](obconnector-dotnet-architecture-design.md#tns-file-discovery)。没有新增依赖。

**支持与限额。** 显式路径在构造时转为绝对路径；标准及外层括号 alias、逗号别名、大小写不敏感别名匹配、注释、单双引号及双写引号均支持。整个文件检查语法、重复别名与 IFILE；仅选中描述符检查支持语义。DESCRIPTION 接受直接 ADDRESS 或单个 ADDRESS_LIST，以及可选 CONNECT_DATA/SERVICE_NAME；地址仅 TCP，保留顺序/重复项及 IPv4/IPv6。严格 UTF-8（可有开头 BOM）、最多1MiB实际字节、16层描述符、4096个节点与别名合计、256地址及65536规范输出字符。未知选项、策略/权重/分组、DESCRIPTION_LIST、TCPS、重复字段及非法地址均显式失败，不静默扁平化。SERVICE_NAME 如存在，必须提供精确 ordinal 匹配的期望值；配置期望也要求文件含该字段。这只核对文件元数据，不验证真实服务器或租户身份。

**文件更新、重试与取消。** 调用方必须用完整文件替换发布；不保证原位写入的快照一致性。读取句柄允许 ReadWrite/Delete，共享更新并不保证每次并发 Open 成功。仅打开时的 FileNotFoundException、Win32共享/锁冲突0x80070020/21最多尝试5次，间隔4次25ms可取消等待；持续缺失/锁定仍失败。访问拒绝、目录不存在及读取/解码/解析失败不重试，无旧地址缓存回退。100ms是配置等待总量，不是原生文件IO的硬截止时间。异步入口将可能阻塞的打开移出调用线程，读取和发布检查取消；操作系统阻塞调用不能强制中止，既有逻辑Open守卫拒绝迟到结果。预取消/有限等待取消测试不等于全面的实际中途文件读取取消验收。

**回归与原始失败。** 保留初始缺少公共类型12RED及12GREEN、PORT附加候选注入1RED，以及地址/编码/别名/服务/精确限额、重复地址、并发替换、删除文件失败与实际Open更新回归。400k字符重复别名曾分配8,995,488字节，现提前检查计数/重复并有分配限额回归；空引号别名/键3RED后统一拒绝。File.Move覆盖在受限及普通用户环境均失败；改File.Replace后仍观察到短暂共享冲突及FileNotFound，不能归为仅沙箱问题。先补共享等待，再以缺失文件预算/取消2RED补齐同一有限打开重试，专项99项通过；其后新增的三个空名称案例纳入最终全量。Windows共享冲突用例有平台守卫，不是Linux证明。全部原始报告保留于忽略目录TestResults/tns-discovery，根目录TestResults-tns-discovery-*.log应另行归档。

**最终验证。** 最终完整单测5687通过（新增67），相关五资产消费者290通过（新增64），均零失败/跳过；Release全解决方案0警告/错误（23.62秒），5C#限定格式验证通过并保留既有workspace加载警告。单测9分13秒，消费者6分57秒；五资产在同一.NET10host运行，无新增实库互操作验收。

**边界与后续。** 五资产消费者在同一.NET10host运行，验证公共类型/构造、同步/异步及两模式下直连/普通池，现代三资产另含私有池和非池数据源；文件删除后失败、恢复为新地址后连接新服务，不把资产数当独立运行时数。本模块没有新增真实数据库、代理或网络文件系统互操作证据。OCP、完整TNS策略/分组、proxy拓扑/reroute、真实健康/故障/负载、企业信任、跨平台/独立运行时及发布验收仍开放。全部23ID及八工作包保持开放；只本地master提交，不push或发布。

### 15.53 2026-10-26 OCP HTTP地址发现

**开发基线与接口。** `master/5cbe5c9`，P1-09/W4。新增五资产公共不可变 `OceanBaseOcpAddressResolver(httpClient, server, netServiceName, expectedServiceName = null)`，使用既有 AddressResolver/UseAddressResolver。调用方持有并配置并发安全的HttpClient（HTTP认证、TLS、proxy及redirect），驱动不销毁它，也不传送数据库口令。每次逻辑Open执行一次GET到固定 `/api/v2/obproxy/loadBalanceInfo?appName=<转义名称>`，请求no-cache/no-store，失败不使用旧池地址。[公共契约与示例](obconnector-dotnet-architecture-design.md#ocp-http-discovery)。无新增产品依赖。

**HTTP与信任边界。** server须为HTTP(S)origin，禁止userinfo、非根path、query与fragment；app名称非空并trim。只接受HTTP200、JSON/+json、未指定或UTF-8 charset、没有残留Content-Encoding的响应。返回RequestUri存在且不同则拒绝；调用方应禁用自动重定向，该检查不能撤销handler已跟随的请求。response/request/stream在成功、错误和取消时均释放，HttpClient保留。驱动无HTTP重试/缓存/磁盘回退；调用方handler仍控制自己的缓存/重试/transport行为。实际最多1MiB字节，最多读取上限+1，并提前拒绝过大Content-Length。Send和读取传token并在解析/发布前检查；ResponseHeadersRead下HttpClient.Timeout不单独覆盖整个body，直接调用须传取消token，连接Open继续使用既有统一预算。任意handler/stream忽略取消时不能强制中断，逻辑Open拒绝迟到结果；单次有界长字符串/空白CPU扫描不承诺即时中断。

**JSON与地址模型。** 严格UTF-8（开头BOM可选）及JSON语法，最多16层value/4096个value节点（含root、属性值、数组元素），下一key分配前检查预算；全部对象decoded key按ordinal拒绝重复，__type全局拒绝。root.data须含精确ordinal NET_SERVICE_NAME及DESCRIPTION；可选successful/success必须true、status必须数值200，其他envelope元数据只解析/限额不用于路由。data/descriptor未知成员拒绝。DESCRIPTION只接受单个ADDRESS_LIST组、可选CONNECT_DATA.SERVICE_NAME及可选OBLB=ON标记；ADDRESS数组只含TCP/HOST/PORT，PORT为十进制整数数值或数字字符串。保留地址顺序与重复，通过安全引号转换复用TNS的256地址/65536规范字符及端口/IPv6校验。SERVICE_NAME必须与显式期望精确匹配，期望也要求字段；两种名称检查均是发现元数据绑定，不确认实际服务器/租户身份。完整OCP组、权重、blacklist、显式策略及重试字段仍拒绝，不静默扁平化；.NET既有端点选择策略继续适用，未移植Java全局load-balance引擎。

**原始失败、修正与回归。** 初始公共类型12RED后12GREEN。扩展68项66通过/2失败：首个__type被平台JSON-to-XML reader隐藏成属性；精确node夹具错误少算successful/status两个value，4084改4082（4096总量）。专用JSON11项3通过/8失败、结构9项3通过/6失败，证实平台reader接受部分非法数字、缺失1/2/3闭括号及object/array尾逗号；因此放弃该平台映射，改为有界严格JSON解析。新解析87GREEN后，数字路径Take跳空白导致11项7通过/4失败（合法status后空白及三种非法number），改为Raw token后98GREEN，最终扩展101GREEN。现有一项测试对照框架JsonDocument检查313个固定/确定性随机metadata片段；这只是局部语法回归，不是所有JSON输入的形式证明。其余覆盖选定HTTP状态、媒体/UTF8/redirect、精确byte/node/depth/endpoint/canonical边界、服务/地址注入、并发32请求、Send及body取消、声明过大body不读流及释放、实际Open的错误/idle/新端点。原报告保留于忽略目录TestResults/ocp-discovery，日志为根目录TestResults-ocp-discovery-*.log，应另行归档。

**最终验证。** 最终完整单测5788通过（新增101），相关五资产消费者354通过（新增64），均零失败/跳过；Release全解决方案0警告/错误（14.57秒），5C#限定格式验证通过且无诊断。单测9分14秒，消费者7分20秒；五资产同一.NET10host运行，没有新增真实OCP/网络HTTP或实库互操作验收。

**剩余工作。** 五资产消费者在同一.NET10host验证公开API、同步/异步、双模式、直连/普通池，现代三资产加私有池和非池数据源；HTTP503时拒绝借用idle，恢复200并更新地址后实际连接第二个协议服务并执行命令。HTTP本身由受控HttpMessageHandler提供，不是实际OCP部署/网络HTTP、企业TLS、Linux或独立运行时证据。OCP/TNS完整策略/分组/权重、proxy拓扑/reroute、真实HA/负载/认证及发布矩阵仍开放。全部23ID与八工作包保留，整体驱动仍未完成；只本地master提交，不push/发布。

### 15.54 2026-10-26 发现分组、策略与权重

**范围与契约。** 从master/1416c55实现P1-09/W4的TNS/OCP分组选择，沿用既有公共resolver API。内部每次发现独立产生immutable DiscoverySnapshot，保留地址、策略、权重和组边界，经普通/私有池、预热及非池路径原样传至physical Open；public Resolve/Async仍返回声明顺序的规范membership字符串，不编码策略。自定义string-only包装不会保留内置策略。shared resolver不保存last-response字段。

组按声明顺序；description OBLB_GROUP_STRATEGY=ROTATION接受但不是轮转组。组内支持ROTATION声明顺序、RANDOM Fisher-Yates、SERVERAFFINITY正权重无放回选择，零权重按声明顺序保留fallback。description策略向组继承，组策略/ON覆盖；OBLB/LOAD_BALANCE=ON无显式策略时默认RANDOM，没有策略/ON则保留连接LoadBalance的组内行为。LOAD_BALANCE_STRATEGY与OBLB_STRATEGY等价，别名重复拒绝；组ON可覆盖继承策略为RANDOM。RoundRobin每条physical route仅取一次counter，复用于各组取模。WEIGHT仅在有效SERVERAFFINITY下合法，默认1，0..Int32.MaxValue，每组至少一个正值，64bit总和；保留重复地址。详见[公共契约](obconnector-dotnet-architecture-design.md#discovery-group-selection)。

健康偏好在策略之后、每组内部应用当前pool-generation缓存；健康零权重可以先于近期TCP失败的正权重，但后组不能跨越前组。每次物理建立仅选择一次，跨TCP重试轮冻结。只在Attach前TCP连接失败推进；TLS/认证/身份错误终止、不重放SQL。所有组合计256地址/65536规范字符及既有byte/node/depth限制保留。每次逻辑Open发现一次、在idle复用前验证；权重/组序/策略变更不主动重分配membership仍有效的idle连接，Clear后新physical才重新选择。

**原始失败与修正。** 实际Open的32项RED全部失败于旧策略拒绝，基础实现32GREEN。池健康回归4RED证实策略初稿读取selector缓存而池反馈写入generation缓存；改用会话当前缓存后通过。alias2RED证实LOAD_BALANCE_STRATEGY未支持，现支持并拒绝重复别名。新增冻结retry夹具最初8秒不足以覆盖Windows六次拒绝TCP：扩展报告252通过/2超时失败，调整夹具预算为30秒，不修改生产预算。测试开发还修正LoopbackServer不实现disposable的编译用法，改显式Stop并等待Completion。独立源码走查发现Framework逐连接new Random的同tick seed集中风险；改默认共享加锁随机源，内部CountingRandom仅用于确定性冻结retry测试，复核确认关闭。没有把统计抽样当成流量比例承诺。

旧策略拒绝测试有意演进为正/负契约：TnsAddressResolverTests.UnsupportedPoliciesProtocolsAndIncludesAreNeverIgnored映射至PoliciesProtocolsAndIncludesAreValidated；OcpAddressResolverTests.ExplicitPoliciesAndUnknownDescriptorMembersCannotBeFlattened映射至SupportedPoliciesAndUnknownDescriptorMembersAreValidated；MultipleGroupsAndAddressPoliciesCannotBeSilentlyIgnored映射至GroupShapesAndAddressPoliciesAreValidated。原参数行保留；已支持策略/多组改为声明membership断言，空组改FormatException，未知策略/权重无有效策略/协议仍拒绝。基线比对显式使用这三个名称映射，不声称旧测试名逐字保留。

消费者门禁最初筛选误包含整个Ob20ConsumerTests类；仅列举测试确认实际2587项（该类2029项而非目标TCP重试52项）后主动取消，session89911退出1且确认无遗留testhost，不计为通过。原取消日志、列表及consumer-scope-correction.json保留。纠正为精确建连重试方法后，运行前仅列举确认610项，复用同一冻结产物；不是因静默日志或观察超时而重启。

**验证。** 最终完整单测5879通过（新增91），相关五资产消费者610通过（新增256），均零失败/跳过；Release全解决方案0警告/错误（12.86秒），12C#限定格式验证退出0，保留既有workspace加载警告。单测9分40秒，精确消费者8分15秒；五资产同一.NET10host运行，没有新增真实OCP/网络HTTP、数据库或独立运行时验收。

证据在忽略目录TestResults/discovery-groups与根日志TestResults-discovery-groups-*.log，需另行归档。新增测试覆盖确定性抽样/大权重/零值/重复/覆盖/别名/跨组限额/health边界、并发32OCP响应快照、权重变更的idle保留和Clear后重建、跨retry冻结及TCP成功后失败不推进；五资产消费者覆盖双模式/同步异步/直连普通池及现代私有池/非池源，普通池和私有池MinPoolSize=2验证每次Open只发现一次的预热关联。五资产都在同一.NET10host；受控HttpMessageHandler和loopback协议服务不是实际OCP/网络HTTP、真实数据库或独立运行时证据。

**仍需完成。** 显式blacklist/发现侧retry、其他策略边界、proxy拓扑/reroute、真实HA/负载/企业认证及独立平台/运行时/包发布验收继续保留。23ID及八工作包均开放，整体目标未完成；不新增依赖/公共API/配置，不改服务器/全局状态，只本地master提交，不push/发布。

### 15.55 2026-10-26 发现黑名单与TCP尝试上限

**范围。** 从master/888bd1c实现P1-09/W4的description全局OBLB_BLACKLIST与OBLB_RETRY_ALL_DOWNS。沿用内置TNS/OCP resolver和immutable snapshot，不增加公共API、连接选项或依赖。公共Resolve仍仅输出声明membership；策略通过内部快照传递普通/私有池、预热和非池physical Open。完整[契约与示例](obconnector-dotnet-architecture-design.md#discovery-blacklist-and-attempt-ceiling)定义严格字段和单位。

NORMAL首个TCP失败阻断；RETRYDURATION在DURATION毫秒窗口按每个事件计数，RETRYTIMES0等同1，同tick事件不合并，精确窗口边界计入。TIMEOUT单位秒，默认50，0可立即探测；过期失败探测立即再次阻断，成功清匹配策略/端点。任一字段显式存在时启用该引擎，缺cap默认120，缺blacklist默认NORMAL/TIMEOUT50；均无则保留旧行为。cap为0..Int32.MaxValue实际TCP总次数，在组/eligible/fallback/额外cycle间共享，非额外整轮数量。跳过BLACK不扣cap，与固定Java扣候选后skip的细节不同。

顺序按既有组/策略/健康一次冻结；eligible跳过BLACK，未达阈值失败在当前组重排队，随后一次不受黑名单过滤且不重排队的fallback。ConnectRetryCount仅增加完整cycle；ConnectRetryInterval在有实际TCP尝试的pass过渡时退避，全BLACK eligible立即fallback。逻辑Open总预算仍约束所有阶段，每个预热physical单独计cap。只记录Attach前、预算未取消的SocketException包装IO；TCP成功先清匹配记录，后续greeting/TLS/auth/identity错误终止，绝不重放SQL。

缓存属于现有pool generation或非池selector，策略有效值与精确端点隔离，cap变更不清同策略历史；旧generation晚反馈不能污染新池。单调时钟/锁，1024条目/16384失败事件，上限驱逐最早反馈条目及其全历史，可能额外探测，不承诺持久排除。状态测试覆盖同tick、窗口、过期探测、并发、容量和策略/代数隔离；真实Open覆盖跨组cap、threshold重排队、期限/取消/Close、后TCP错误终止、清池晚反馈和零预算idle。

**独立走查发现与裁定。** MinPoolSize=2剩余一个valid idle时，补建预热优先于借idle，cap0会阻断现有idle。新增回归先保留RED，再仅对显式cap0跳过补建；membership/expiry/probe/credential检查仍执行，无idle不建TCP。此策略明确优先于最低池补建，非零cap恢复原预热语义。五资产消费者覆盖双模式、同步/异步、直连/普通池、现代私有池/非池源，池路径实际丢弃一个预热会话后以cap0复用另一个，并在Clear后重建；每次Open只发现一次。

**原始报告。** Open RED44全部失败于旧字段拒绝，首轮GREEN44通过；状态首轮33通过。扩展首轮的malformed-before-idle断言错误期待未包装NotSupported，修正为公共OceanBaseException.InnerException；其余原始结果与最终映射保留于TestResults/discovery-blacklist和根TestResults-discovery-blacklist-*.log。原DiscoveryPolicyTests.UnsupportedPoliciesAreNotSilentlyIgnored有意改名PoliciesAreAcceptedOrRejectedExplicitly，保留全部7参数行：cap=2改支持，BLACKLIST=ON改FormatException，其他仍拒绝。OCP原空blacklist/cap参数行保留并改正向membership断言。

**验证。** 最终完整单测5980通过（新增101），相关五资产消费者738通过（新增128），均零失败/跳过；Release全解决方案0警告/错误（17.80秒），16C#限定格式及验证退出0，保留既有workspace加载警告。单测11分47秒，精确消费者15分30秒；五资产同一.NET10host运行，没有新增真实OCP/网络HTTP、数据库或独立运行时验收。

受控HTTP handler与loopback wire peers只证明本地实现，不是实际OCP/网络HTTP、真实数据库/代理或独立平台运行时验收。组/地址本地覆盖、其他发现策略、proxy拓扑/reroute、真实HA/负载/企业认证及发布验收仍开放；23ID/八工作包未整体关闭。只本地master提交，不push/发布。

### 15.56 2026-10-26 Oracle 二进制游标列与嵌套资源所有权

**范围。** 开发基线master/75d3751，归属P1-06/W5并关联W1。普通Oracle二进制type163列返回现有
OceanBaseRefCursor；不新增公共API、配置或依赖。[公共契约](obconnector-dotnet-architecture-design.md#oracle-cursor-valued-columns)
要求普通无参数命令显式Prepare（KeyInfo/正FetchSize等已有binary路径自动准备），消费完整父响应后再读取子游标。文本列及Input/InputOutput仍开放，不表示P1-06整项关闭。

父statement在发送执行前从命令/cache转交执行owner；普通COM_STMT_EXECUTE未经prepare声明且没有父owner的cursor列拒绝并销毁会话；嵌套fetch及过程所属普通结果沿用已有owner。
逐ID登记与发布：GetValue/provider/generic兼容访问返回同一single-use句柄，错误类型/协变数组赋值在发布前拒绝；
IsDBNull、跳过、排空和NULL不发布。缓冲行及SequentialAccess的跳过路径都登记ID，结束时关闭未访问兄弟。
嵌套fetch使用子owner，正常完成和Dispose/DisposeAsync统一延迟释放祖先，直到后代全部结束；忙父响应的提前fetch
失败不消费句柄。命令仍在执行时Dispose保留取消语义；普通reader未读完关闭仍销毁会话，不改为静默排空。

根执行及全部后代共享4096个不同ID预算（包含跳过ID），同一执行内别名同句柄；0、祖先环及其他后代scope已拥有的
ID拒绝，不能静默回收身份。上限/畸形帧退休会话，不丢弃关闭责任。原session、事务epoch及逻辑租约保护保留；
活cursor阻止回池。KeyInfo的最终重写statement也移交；SchemaOnly只返回元数据，不执行或创建句柄。

**独立走查与RED。** 首批8RED明确失败于旧普通列decoder拒绝。首次实现验证的8失败来自测试错误要求Dispose活动命令
后父reader继续读取，已修正为父响应结束后再Dispose，保留既有取消契约。扩展17首轮16通过/1根预算RED，改为共享预算；
临时恢复旧Dispose释放路径的4个嵌套回归全部RED，随后恢复正确实现。走查另发现KeyInfo独立路径遗漏、输出参数owner-wide
Publish误保留未访问列、错误typed getter先发布后转换；均已补回归和共享路径修复。重写KeyInfo旧关闭路径及协变数组
回归保留6RED/18通过。顺序资源限额异常已有通用安全消息，不要求暴露4096文本；原错误断言报告保留。
首次80消费者失败源于新测试脚本遗漏驱动时区/national控制查询，修正脚本后80通过，不修改生产协议以迎合fixture。

**父查询分页修复。** 固定Java在prepare列含CURSOR时强制NO_CURSOR；本实现同步、异步和最终KeyInfo metadata统一将父FetchSize作为提示，execute flag0、无父FETCH，子cursor独立分页。UseCursorOffset在参数上传/目标执行前拒绝，SchemaOnly仍忽略提示。新增8正数提示/4offset回归原始12RED全部失败；修复后相关218通过。此前6044全量报告保留为pre-fetch-hint，不作为修复后验收。测试初次编译的DbCommand属性错误已修正，不计RED证据。

**验证。** 最终完整单测6056通过（新增76），相关五资产消费者561通过（新增160），均零失败/跳过；父FetchSize修复后相关218通过。
单测8分20秒，消费者1分18秒。Release全解决方案0警告/错误（14.66秒）；15C#限定格式及验证退出0，保留既有workspace加载警告。
独立源码/契约/证据走查无未处理阻塞发现；76DLL及25源码/项目哈希在两项最终门禁期间不变。
五资产消费者在同一.NET10host运行，不代表五个独立运行时；新实库入口1失败于greeting前，未执行SQL。

原始TRX与日志保留于忽略目录TestResults/cursor-columns及根TestResults-cursor-columns-*.log，需要独立归档。
五资产消费者在同一.NET10host加载运行，不是五种独立运行时。固定Java的公共JDBC4ResultSet.getObject专门CURSOR分支
通过getComplexCursor调用四字节binary解码；先前只读generic codec返回null的观察不代表Java公共API无实现。
该源码证据与本地wire peer均不能证明实际服务器SELECT CURSOR支持、元数据及statement依赖。
新真实验收入口PreparedCursorColumnSurvivesParentCommandDisposal已实际运行：oracle-direct 1失败/0跳过，栈定位Open/HandshakeFlow70，greeting前断开，未执行SQL。普通用户项目命令因隔离账户NuGet导入路径没有执行测试（无TRX），改用已构建DLL的dotnet vstest得到这份实际失败报告；没有把空命令exit0当作通过。
保留文本/输入方向/其他输出和复杂形状、资源/故障/真实部署及发布矩阵；23ID和八工作包仍全部开放，只本地master提交。

### 15.57 2026-10-26 协议帧诊断

基于master/e0d1d30，P2-04/W7。新增标准DiagnosticListener OceanBaseConnector.Protocol，Frame/Error/Boundary
仅提供固定scalar只读字典；mysql/compressed/ob20共享本地ChannelId，并按listener实例区分不同程序集。
覆盖握手、普通命令、reset/probe、流式读取、同步/异步。读Frame为完整头部观察，早于校验/完整body；
写Frame为下一层stream接受，不代表flush、TCP/server交付或SQL成功。不输出SQL、payload/auth/extra-info、
用户名/端点或异常对象/文本。公共订阅、字段类型及边界详见[协议诊断契约](protocol-diagnostics.md)。

复用已安装DiagnosticSource，无新增依赖、provider公共API或配置。禁用无eventdictionary分配；回调同步、
异常隔离、恢复Activity.Current、抑制同线程递归，驱动不保存队列。创建listener的应用回调异常会使本程序集
协议诊断禁用，但不得破坏连接。观察者必须及时返回且不重入；不承诺任意应用副作用可恢复。

独立走查发现：外部Close先发生时closed/disposed guard漏报，改为独立Interlocked错误去重；缓冲续帧头部
异常在Dispose时误记incomplete，现StartFrame统一保留实际异常。4续帧RED保留；最初public cancel误接受
server同名listener事件，原4pass不作为证明，按client greeting ChannelId筛选后4全部RED。另2boundaryRED
证明非法响应边界缺少事件；增加观察但不改变原错误/连接/未读payload行为。首批6无事件RED及全部报告保留。

首轮最终全量6122通过/1失败，失败为LOB夹具把30位小数的NUMBER声明成scale0；输出行解码提前拒绝，
客户端销毁连接与server尾部EOF/OK写入竞态。原始失败及同产物4项调查通过均保留，后者不证明路径正确。
新增精确错误断言得到3通过/1失败，确认原用例走命令初始化错误而非LOB amount校验；夹具按数值写入实际
scale后，93项LOB/诊断相关回归通过。仅修正两处测试文件，不吞IO错误、不放宽产品数值校验。

夹具修正后另一轮全量6122通过/1失败，为既有DriverDiagnostics off分配断言观测98352bytes；
该Start调用不经过新协议诊断路径，相关产品/原测试未修改。同冻结产物下分配3项及完整诊断相关192项
均通过，但不足以证明该次异常分配的根因或修复；原始报告保留，零分配断言不放宽，负载/分配调查仍开放。

第三轮全量6122通过/1失败，为异步CloseCallbackScope立即GC的弱引用断言；同产物整类18项调查通过。
源码机制允许Task完成内联恢复测试时，最外层清理生产者栈尚未退出；未采集GC root栈，不能把该解释
当作已证明的具体根。测试改为5秒预算内让出执行并重复GC，保留最终弱引用断言及scope/context KeepAlive。
临时故障注入保留清理字段，4项均在预算后失败；原产品源码逐字节恢复后89项相关回归通过。产品释放逻辑
未改，故障注入不在交付源码中；该检查证明有界等待仍检出持续字段泄漏，不证明生产内存/负载预算。

最终完整单测6123通过（新增67），相关五资产消费者341通过（新增170），均零失败/跳过；LOB/诊断专项93通过，scope/诊断相关89通过及三轮各4项scope通过。
Release全解决方案0警告/错误（6.84秒）；11C#限定格式验证退出0，保留既有workspace加载警告。
独立源码/契约/证据走查无未处理阻塞发现；最终单测期间76DLL及36源码/项目哈希不变；消费者341通过使用的26项provider/hostDLL哈希与最终产物相同。
五资产消费者在同一.NET10host运行，不代表五个独立运行时；无新增实库、真实OBProxy/企业TLS、跨平台或生产负载验收。

五资产普通/压缩、双模式、同步异步和普通池Max1两次借用验证auth切换、reset、probe以及source/filter/write
回调失败；另用既有OB20公共执行fixture验证inner/outer身份相关。均在同一.NET10host；无新增真实数据库、
代理或TLS/跨平台/独立运行时验收。原15.1—15.56/报告12—51历史保留；其他资源等待/性能及完整23ID/八包
继续开放。原始TRX/log在忽略目录TestResults/protocol-diagnostics及根TestResults-protocol-diagnostics-*.log，需另行归档。

### 15.58 2026-10-26 连接池通知后的取消交接

**历史检查点：** 本节保留首次修复池交接后、诊断卸载缺陷修复前的失败与条件通过；当前结果见第15.59节。

**基线与归属。** master/ffceec8，P0-01/W1。确认容量为1、两个Open等待的场景：归还或销毁
释放一个通知，实际取得通知的等待者在再次TakeCandidate之前取消，旧EndRent仅减少renters，
未把仍可用的idle/创建容量通知给另一等待者。后者一直等待，可能直到池超时；零等待超时下也没有
后续归还可唤醒它。不是reset污染或服务器卡顿。

**修复。** 沿用SemaphoreSlim与现有池锁，EndRent在锁内检查idle或sessions+opening低于上限，
存在可用资源时通知下一等待者。同步/异步、普通池/私有源共用这一退出点。通知仅令调用方重试，
仍由TakeCandidate在锁内唯一领取idle或预留opening，不绕过容量、不承诺FIFO/生产唤醒时限，
不新增公共API、配置或依赖。[公共契约](obconnector-dotnet-architecture-design.md#pool-wait-handoff)。

**复现和覆盖。** 用既有Meter wait.duration同步回调及AsyncLocal标记，取消实际取得通知的调用方，
不依赖Semaphore FIFO、随机sleep或产品测试钩子。创建容量的同步/异步四组合先4项RED；公共
Open/Close双模式八组合先8项RED；最小修复后同名12项GREEN。后续扩展失败reset释放容量、
私有源及第三借用者仍受最大容量限制。公共路径断言取消者Closed、幸存者复用原PhysicalSession、
单次认证、实际probe/reset及两次SQL成功。同步Open通过Close取消，异步Open通过调用方token取消。
源码/测试入口：[池](../src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs)、
[单测](../tests/OceanBaseConnector.UnitTests/Session/PoolWaitHandoffTests.cs)、
[共享公共夹具](../tests/OceanBaseConnector.UnitTests/TestInfrastructure/PoolWaitHandoffFixture.cs)、
[五资产消费者](../tests/OceanBaseConnector.ConsumerTests/PoolWaitHandoffConsumerTests.cs)。

**走查与功能验收。** 独立走查确认核心容量/通知逻辑，发现夹具顺序Drain异常会跳过后续
清理；已改为同时观察两任务，并用finally独立保证owner、池和server清理，不吞非取消异常。
默认配置最终完整单测6147通过（保留此前6123及新增24），通知交接五资产消费者64通过，均零
失败/跳过。此前池/凭据/事务/阶段诊断相关982通过；Release全解决方案0警告/错误（11.67秒）；
5C#限定格式验证退出0，保留既有workspace加载警告。冻结76DLL及522源码/项目文件哈希一致。

**默认宿主门禁仍失败。** 同一冻结产物的完整632项消费者默认三轮分别完成263/256/225项后
以Internal CLR error 0x80131506中止，单独宿主也会复现。crash收集模式完成261项后报告
0xC0000005并产生转储；仅关闭TieredPGO的一轮完成374项后仍以0x80131506中止。五轮均无
新增handoff用例结果，不能按已完成项数宣称整轮通过。单独route10及reader/route/配置组合110
通过，不证明组合运行安全。没有通过修改断言、吞异常或排除失败用例让合并门禁变绿。

**对照及转储的限度。** 全量单测及其他宿主终止后，临时恢复git ffceec8的原池源码，构建五
资产；原池的默认既有568项通过，同选择632项/crash收集模式为568通过、64通知交接按预期
超时失败、零跳过。它证明64项在旧实现下确实失败，但不同运行条件不能定位CLR根因。对照
结束已逐字节恢复最终池源码及79项build文件，核验76DLL/522源码项目与最终单测一致。
149MB转储无ExceptionStream；已有本地ClrMD读取发现ExecutionEngineException，并有线程的
IP位于clrjit.dll、托管栈含Prestub/Build/PrepareOpen。不能把该线程直接当成已证明的故障线程，
也不能仅凭错误码和托管源码未发现unsafe就宣布运行时存在确定缺陷。

**条件通过不是修复。** 相同冻结产物、原632项选择，仅在测试子进程设置
DOTNET_TieredCompilation=0时632项全部通过/零跳过（2分12秒）；恢复默认配置后单独运行本
模块64项全部通过（22秒）。原默认合并门禁仍失败，分层编译关联是调查线索，不是根因结论、
产品配置建议或发布验收。没有修改全局/产品运行时配置或产品依赖，未加入JIT workaround。
默认宿主问题沿用P2-05/W8保留，需在默认承诺配置下定位并完成验收。

日志/TRX/转储和哈希保存于忽略目录TestResults/pool-wakeup-handoff，根日志前缀为
TestResults-pool-wakeup-handoff-；需另行归档。五资产全部运行于同一.NET10.0.12 host，
不是五个独立运行时。独立源码/契约走查没有发现通知交接修复的其他阻断项；本地保存该修复
不表示默认消费者门禁或发布门禁通过。

**保留范围。** 本模块是已复现并修复的池通知丢失，不替代任意并发交错、长期资源/负载、隐式
会话副作用或真实OBProxy/reset/平台验收。不新增实库或企业TLS证据，不关闭23ID或八工作包。
前15.1—15.57及报告12—52历史完整保留；只本地master提交，不push/发布。

### 15.59 2026-10-26 可卸载诊断线程状态损坏与默认回归恢复

**归属与定位。** 在P0-01交付门禁中继续调查P2-05/W8，同时修复P2-04诊断实现。
关闭测试集合并行仍在160项后以0x80131506中止，排除了集合并行为必要条件。
源码唯一ThreadStatic位于ProtocolDiagnostics._emitting；它在无监听器时也会被访问。
公开[运行时问题135441](https://github.com/dotnet/runtime/issues/135441)描述旧collectible类型
线程静态槽被重用后，旧线程退出会在新LoaderAllocator释放错误句柄的机制；公开报告只是线索，
不是本项目的验收结果。直接链接原ProtocolDiagnostics的独立进程先获得三次对照正常、三次
线程退出后新程序集静态对象损坏；泛型static试验未复现，改为独立非泛型GC-static盒后可复现，
所有试验日志保留。

**真实资产回归。** 新增[独立进程探针](../tests/OceanBaseConnector.CollectibleProbe/Program.cs)
和[消费者](../tests/OceanBaseConnector.ConsumerTests/CollectibleDiagnosticsConsumerTests.cs)，
对五个实际product DLL各执行保持旧线程存活/退出两场景。旧线程实际调用该资产的
ProtocolDiagnostics.Boundary，丢弃Assembly引用；旧ALC必须收集，再装载新ALC并初始化16个
独立GC-static盒。退出前所有Marker正确，旧线程退出、GC及内存压力后再次核对Marker和Tag。
旧实现五项control通过、五项exit检测到1个损坏而失败；修改后同名10项全部通过。
可能的堆损坏只发生于子进程，宿主并发排空stdout/stderr，45秒超时后Kill并等待退出。
探针加入solution及consumer项目构建依赖，不添加NuGet包，不修改产品API、服务器或全局配置。

**修复与语义。** [ProtocolDiagnostics](../src/OceanBaseConnector/Internal/Protocol/ProtocolDiagnostics.cs)
改为static readonly ThreadLocal<bool>，T使用框架bool，不在可卸载产品类型声明ThreadStatic。
仍按当前物理线程抑制同步观察者重入，不使用会流入其他异步执行上下文的AsyncLocal。
旧ALC可收集也由回归显式验证。首次线程访问允许ThreadLocal槽分配；不改变禁用事件不分配
每帧dictionary、观察者异常隔离、Activity恢复和协议数据脱敏契约。
[公共契约](protocol-diagnostics.md)与[卸载边界](obconnector-dotnet-architecture-design.md#protocol-diagnostic-unloading)。

**最终证据。** 原632项选择加新增10项，在默认.NET10.0.12配置下642项全部通过、零失败/跳过
（2分37秒）；没有关闭分层编译、PGO或排除旧用例。完整单测6147通过（保留此前6123及新增24，零失败/跳过，8分11秒）。
此前池交接24项新增单测、64项消费者的RED/GREEN保留于15.58。Release全solution零警告/错误
（3.33秒），8C# scoped format verify退出0，既有workspace加载警告保留。
普通用户solution restore纠正诊断期间混用沙箱缓存造成的missing-analyzer构建失败；首次NuGet
审计网络失败也保留，未修改包依赖。最终Release DLL、源码/项目均冻结核对。
独立走查已核对线程保护语义、旧ALC回收、子进程隔离与清理；最终报告及冻结哈希已由独立只读复核核对，无剩余阻断项；reviewer未运行构建或测试。

**结论范围。** 已确认并修复可卸载诊断状态损坏，原受影响组合默认回归恢复；没有逐一解析
此前每个崩溃的native故障点，不能断言所有历史CLR故障有同一原因。第15.58的原始失败、
原池568pass/64RED与禁用分层编译632条件通过不删除。五资产仍共用同一Windows/.NET10host；
默认642通过不代表独立五运行时、跨平台、实库、OBProxy、企业TLS或负载验收。
P2-05及全部23ID/八工作包继续开放。日志/TRX/冻结哈希位于忽略目录TestResults/pool-wakeup-handoff，
根日志前缀TestResults-pool-wakeup-handoff-，需要另行归档。

### 15.60 2026-10-26 Batch 元数据与数据源条目创建

基于master/d7a5354，归属P1-05/W5。[Batch](../src/OceanBaseConnector/OceanBaseBatch.cs)开放
SchemaOnly/KeyInfo，复用原Command/CommandExchange准备、键目录、隐藏列和reader元数据路径。
NextResult逐项推进，元数据耗尽后仍需关闭reader释放批次预留；提前关闭跳过未开始条目。
SchemaOnly不执行目标或编码/读取输入值，但KeyInfo/命名复杂类型可能读取目录，服务器准备仍有
解析/权限/元数据锁成本。SingleRow/SingleResult、过程/输出/多语句元数据组合仍拒绝。

公共72项首轮36通过/36失败：所有数据源分支在source.CreateBatch().CreateBatchCommand执行前
抛NotImplementedException。[固定.NET10源码](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Data.Common/src/System/Data/Common/DbDataSource.cs)
确认私有sealed DbBatchWrapper的该方法直接抛出；此前consumer通过factory创建条目，未覆盖此入口。
[DataSource](../src/OceanBaseConnector/OceanBaseDataSource.cs)现创建内部拥有连接的OceanBaseBatch，
复用原条目/集合/执行/计数。执行时Open，reader自动CloseConnection，scalar/nonquery排空释放，
失败/Dispose清理自有连接；保持Connection/Transaction/Prepare拒绝、源timeout、ambient及池/reset
代次检查。普通连接Batch所有权不变；没有新增公共类型、依赖或运行时配置。

[新增51项单测](../tests/OceanBaseConnector.UnitTests/BatchMetadataTests.cs)覆盖两模式/同步异步、
两条元数据、重复准备复用、未消费参数流、隐藏键/可见列、提前关闭、后续prepare/catalog错误、
超时/精确取消token、预留释放、旧CloseConnection租约、输出限制、数据源全部执行入口。
源重复执行分别断言池化单认证/四次归还/三次探活与非池化四次认证。单位元数据MySQL场景使用
内部模式override；[新增72项公共消费者](../tests/OceanBaseConnector.ConsumerTests/BatchMetadataConsumerTests.cs)
使用实际双模式loopback握手，覆盖三现代资产、同步异步、普通连接/数据源及三个元数据行为组合。
失败路径取消后await服务端任务，保留非取消异常并按需聚合客户端异常。

原始记录保留：首批12项旧入口全部NotSupported RED，开放后同名12GREEN。扩展单位259通过/2失败，
是新断言误将创建未入集合条目当作修改活动Batch，改为断言集合Add拒绝；随后269项265通过/4失败，
Oracle重复执行夹具用原始OK遗漏时区状态，改为现有tracked session report，不放宽产品校验。
消费者中间轮138通过/24失败，是源会话销毁后误要求最后pending COM_STMT_CLOSE；按原生命周期
修正，活会话仍逐包断言关闭。所有原始TRX/log保留TestResults/batch-metadata及根同名前缀日志。

最终完整单位6198通过（保留此前6147及新增51，8分7秒），受影响单位269、消费者162通过（含新增72），
均零失败/跳过。Release全解决方案0警告/错误（2.39秒）；4C#限定格式验证退出0，保留既有workspace加载警告。
最终源码/项目524项和Release DLL1266项哈希未变；本地链接、三张23ID表及八工作包结构核验通过。
独立只读源码/测试走查无剩余产品阻断项；报告/文档/测试名及冻结哈希已独立只读复核，无剩余阻断项；reviewer未运行构建/测试。
三资产消费者共享Windows/.NET10host，不是三个独立运行时。无新增实库/OBProxy/企业TLS、跨平台
或生产负载验收。其他Batch/游标行为、全部23项和八工作包继续开放；只本地提交，不推送。

### 15.61 2026-10-26 SingleRow/SingleResult 跨批次结果限制

基于master/27bd176，归属P1-05/W5。核对官方[CommandBehavior.SingleRow契约](https://github.com/dotnet/dotnet-api-docs/blob/main/xml/System.Data/CommandBehavior.xml)
后确认共享reader偏差：此前SingleRow在NextResult后再次暴露每个结果的首行，标准要求只返回首结果首行。
[共享reader](../src/OceanBaseConnector/OceanBaseDataReader.cs)现让SingleRow进入既有SingleResult排空路径；
首结果为空或只有affected count时，也不搜索后续结果。依赖旧每集首行行为的调用方需移除SingleRow并显式控制读取。

[Batch入口](../src/OceanBaseConnector/OceanBaseBatch.cs)支持两标志；[BatchReader](../src/OceanBaseConnector/OceanBaseBatchReader.cs)
在NextResult循环处理全部隐藏结果和条目，成功耗尽才返回false，完整保留counts/outputs、异常归属与unknown规则。
Read=false只表示当前结果结束；需NextResult才处理后续批次条目。提前Close仍跳过未启动后缀；不能因flag隐式执行它们。
同会话预留、SequentialAccess、原CloseConnection租约、数据源owned connection及SchemaOnly不执行目标的规则保留。
无新增公共API、依赖、配置、SQL改写、事务或replay。[当前公共契约](obconnector-dotnet-architecture-design.md#batch-result-limits)
取代第15.60节当时保留的SingleRow/SingleResult限制，历史记录不改写。

[新增62单位](../tests/OceanBaseConnector.UnitTests/LimitedResultBehaviorTests.cs)：命令首结果三形状12、
整批限制/计数12、Batch首空/count4、隐藏同命令/后缀ERR8、发送后token/Cancel/timeout6、提前关闭12、
SchemaOnly|KeyInfo组合8。最后8项同时设置SingleRow和SingleResult，变化维度是async/mysql/keyInfo，
不是单独穷举两标志。旧MySQL过程4项加强为NextResult=false、Close前确认输出发布和计数；非法Batch标志改检64。
[公共132消费者](../tests/OceanBaseConnector.ConsumerTests/LimitedResultConsumerTests.cs)含五资产command60
（三首形状、同步异步、双模式）及三现代资产connection/sourceBatch72（3标志组合、同步异步、双模式）；
含显式CloseConnection与source自动关闭。同一peer上的prefix/suffix及最终断开检测不允许悄悄重新建连。

首批24项全部RED：12命令NextResult误返回true，12Batch拒绝标志；修改后原24及4过程/1非法标志共29GREEN。
扩展218单位和132公共消费者均零失败/跳过。一次扩展构建因新测试缺Common命名空间而2错误；补using后构建
零警告/错误。最终完整单位6260通过（原6198逐名保留、新增62，8分12秒），相关消费者294通过（原162及新增132），
均零失败/跳过。最终Release全解决方案0警告/错误、2.40秒；7C#限定格式验证退出0，既有workspace加载警告保留。
526份源码/项目及1266份Release DLL冻结hash未变；源码/测试增量独立只读走查无阻断，最终独立只读复核已确认原始TRX、保留用例、冻结hash及文档范围，无剩余阻断项；reviewer未执行构建/测试。
原报告/日志留TestResults/batch-result-behaviors及根同名前缀，需另行归档。
五资产共享Windows/.NET10host，不是五个独立运行时；无新增实库/OBProxy/企业TLS、跨平台或长期负载验收。
其余Command/Batch/游标组合及全部23ID/八包仍开放。本地master提交，不推送。

### 15.62 2026-10-26 REF CURSOR 结果限制

基于master/7976fb3，归属P1-05/W5。[OceanBaseRefCursor](../src/OceanBaseConnector/OceanBaseRefCursor.cs)
入口开放SingleRow/SingleResult，复用共享reader及既有cursor分页/校验；不改变fetchSize、协议或父命令输出。
SingleRow只暴露首行，后续Read/NextResult排空隐藏页；SingleResult不减少同一cursor的可见行。
SchemaOnly/KeyInfo/未知位仍在单次领取前拒绝；SequentialAccess/CloseConnection、原token/命令timeout、
独立fetch非unknown、单次使用、父statement所有权和提前关闭销毁未读session保持。[公共契约](obconnector-dotnet-architecture-design.md#ref-cursor-result-limits)
适用于输出和二进制列返回的同一cursor对象；没有增加输入方向、文本cursor列或其他Oracle形状。

[新增84单位](../tests/OceanBaseConnector.UnitTests/LimitedCursorBehaviorTests.cs)：三标志组合×同步异步×EOF/FE-OK×
buffered/sequential的正常跨页24及不Read直接NextResult24；空结果/CloseConnection/非法位不消费12；
fetch故障12；下一页caller取消/原timeout6；提前reader/cursor关闭6。故障覆盖ERR、no-progress、extra-row、
changed-metadata；no-progress可在首个GetDataReader失败，不能把全部12称为已暴露首行后的隐藏页错误。
原core复用且旧case名称保留。首轮84全失败：78项直接拒绝或异常类型不符，6项在取消夹具ready门控等待超时；
后六项不独立视为产品RED。修正后初步102及相关220全部通过，无失败/跳过。

[新增90公共消费者](../tests/OceanBaseConnector.ConsumerTests/RefCursorConsumerTests.cs)：五资产×同步异步×
standard/OB20/非法trailing×三flags。三行42/43/44分两页，SingleRow只见42，SingleResult全见；后续FETCH仍2行，
验证最终边界、非unknown、cursor92先于parent91关闭及同一peer断开。原TLS/准备/提示case保留；清理观察peer错误。

[新增12实库用例](../tests/OceanBaseConnector.IntegrationTests/Oracle/RefCursorTests.cs)只执行OPEN SELECT，无DDL；
同步异步×三flags×空/三行，fetchSize1，父命令先释放，排空后实际SELECT1，再验证空cursor的CloseConnection。
首轮real-limited12失败在profile查找阶段：误将string profile索引为首字母o；修正枚举后real-corrected12均在
Open/HandshakeFlow70读取greeting前失败，没有执行SQL/游标FETCH，不计真实兼容通过；保留原始失败。
无opt-in noopt-final为配置9通过/cursor5NotExecuted，跳过不是通过；未改变配置或服务器全局参数。

最终Release全解决方案0警告/错误2.71秒；五C#限定格式验证退出0，既有workspace加载警告保留。
冻结527源码/项目和1069份活动src/tests Release DLL；排除历史probe/saved-build副本，不作新平台/运行时证据。
最终完整单测6344通过（原6260逐名保留、新增84，8分26秒）；相关消费者370通过（原280逐名保留、新增90，1分14秒），均无失败/跳过；
源码/fixture增量独立只读走查无阻断，最终独立只读复核已确认原始TRX、保留用例、冻结hash、文件/历史/链接及文档边界，无剩余阻断项；reviewer未构建或测试。
报告留TestResults/ref-cursor-result-behaviors及根同名前缀，需单独归档。全部23ID/八包、其他Command/Batch/游标组合、
Oracle形状、真实版本/代理/TLS/平台/负载及发布门槛继续开放；本地master提交，不推送。

### 15.63 2026-10-26 发现分组黑名单覆盖

基于master/fa5d47f，归属P1-09/W4。TNS/OCP的ADDRESS_LIST现在可提供局部OBLB_BLACKLIST，
整块替换DESCRIPTION策略；缺省字段用既有NORMAL/TIMEOUT50，空块同样恢复默认，不逐字段合并。
省略组块继承全局；只有局部策略且无根cap时默认共享cap120，其他组保持无策略。
显式根cap独立出现时仍启用默认全局策略。ADDRESS级黑名单、组内OBLB_RETRY_ALL_DOWNS仍明确拒绝。
[公共契约](obconnector-dotnet-architecture-design.md#discovery-blacklist-and-attempt-ceiling)已同步。

[DiscoverySnapshot](../src/OceanBaseConnector/Internal/DiscoverySnapshot.cs)保留不可变组策略，
[EndpointAttemptPlan](../src/OceanBaseConnector/Internal/EndpointAttemptPlan.cs)按当前组选择有效策略，
反馈绑定最后一次实际候选；等效策略值+精确endpoint共享历史，不同策略互不清除/污染。
合格阶段跳过已屏蔽候选，未达阈值时只在组内重排；fallback只访问有有效策略的组，且不按阈值重排。
无策略组每个配置cycle只访问一次；冻结分组顺序、额外轮次、共享actual-TCP cap/退避/总预算保持。
池代数/凭据退休/selector归属、cap0允许健康idle但不补建、TCP成功后握手错误终止、不重放SQL均保持。
Java固定6adc311的LoadBalanceInfo明确本地黑名单不支持且解析被注释，组配置字段标记unused；
本次为.NET定义的新增能力，不是Java运行时或精确候选计数对齐证据。

[新增38单位](../tests/OceanBaseConnector.UnitTests/GroupBlacklistTests.cs)：覆盖TNS/OCP局部阈值、共享上限、
local-only/cap120、无策略组fallback隔离、等效/不同策略历史、空块及partial整块替换、畸形/重复/unknown/局部cap，
真实loopback TCP拒绝/成功、同步异步和池预热；重载不改变旧快照，畸形局部策略在idle租用前失败，修复后重用同一peer。
原始group-red14全部失败；group-green为88通过/6失败，后六项均是测试builder漏User ID，
不计产品缺陷；仅补测试必需配置，group-corrected24通过。扩展后相关单位230通过，零失败/跳过，1分37秒。

[消费者](../tests/OceanBaseConnector.ConsumerTests/DiscoveryBlacklistConsumerTests.cs)保留旧全局128入口，
新增局部128：五资产×双模式×同步异步×普通直连/池及现代source池/非池×TNS/OCP。
局部threshold2要求连续两次TCP失败后切换；池预热、脏租约销毁、cap0健康idle重用/非池零TCP失败、
Clear后的新代数再探测、恢复cap及后续SQL均有断言；OCP每次Open重新获取，预期3次HTTP请求。
消费者最终256通过（旧128逐名保留、新增128），零失败/跳过。
最终完整单测6382通过（原6344逐名保留、新增38），零失败/跳过。

最终Release全解决方案0警告/错误，耗时00:00:04.90；7个C#限定格式验证退出0，既有workspace加载警告保留。
冻结529份源码/项目与1069份活动src/tests Release DLL，排除历史probe/saved-build副本。
独立只读源码/夹具走查未发现阻断，reviewer未构建/测试；最终独立只读复核已确认原始TRX、旧用例保留、冻结hash、15文件范围、历史/ID/链接及真实验收边界，无剩余阻断项；证据链接Minor已修正。
报告保留TestResults/group-blacklist及根同名前缀，包含原始失败，需要单独归档；忽略报告不随提交发布。

本模块可复现门禁（在仓库根使用已恢复依赖的普通用户环境；先构建五资产，再运行测试）：

```powershell
dotnet build OceanBaseConnector.slnx -c Release --no-restore
dotnet test tests/OceanBaseConnector.UnitTests/OceanBaseConnector.UnitTests.csproj -c Release --no-build --no-restore --logger 'trx;LogFileName=unit-final.trx' --results-directory TestResults/group-blacklist
dotnet test tests/OceanBaseConnector.ConsumerTests/OceanBaseConnector.ConsumerTests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~DiscoveryBlacklistConsumerTests' --logger 'trx;LogFileName=consumer-final.trx' --results-directory TestResults/group-blacklist
```

五资产同一Windows/.NET10 host；controlled HTTP和loopback peer不是真实OCP/OBProxy/数据库验收。
本模块不重复计前一REF CURSOR模块12项greeting前失败为通过，不新增实库兼容性证据。
本次用独立Python原始TCP连接同一已配置端点，仅等服务器握手、不发送凭据/SQL；TCP成功但约5秒后
首4字节头尚未到达即EOF（header_bytes=0），与先前驱动失败阶段一致。证据greeting-probe.json；
可确认失败不依赖驱动认证/游标代码，具体端点/服务端/中间网络原因仍未确定，不能据此修改协议或记为通过。
全部23ID/八包、其他发现边界、真实拓扑/故障/平台/负载及发布门槛继续开放；本地master提交，不推送。

### 15.64 2026-10-26 发现分组 TCP 尝试上限

基于 `2a2e139`，归属 P1-09/W4。ADDRESS_LIST 接受 `OBLB_RETRY_ALL_DOWNS`，
同 DESCRIPTION 使用十进制 0..Int32.MaxValue；OCP 接受整数 token 或数字字符串。
局部上限按每次物理建连、每个组累计实际 TCP 尝试，跨 eligible/fallback 和额外轮次
不重置；耗尽后继续下一组，不超过共享全局上限或逻辑 Open 总预算。
省略时不加组限制；局部上限本身不激活黑名单、不增加轮次或改变全局缺省。
全零组不做预热补建，仍允许已有健康 idle 租用，清池后不得新建 TCP。

首轮新增 34 项为 1 通过/33 失败，失败包括原解析器明确拒绝局部字段。
初步相关回归 173 通过。独立走查发现全组零预算仍会先补建而阻止 idle 租用，
新增四项同步/异步×TNS/OCP 回归全部 RED；修复共享预热预算判断并扩展公共消费者。
此前分组黑名单中的两项局部 cap 拒绝断言改为负值拒绝，合法值由新用例验证。
快照重载、计数隔离、共享上限、跳过项反馈、池预热和无 idle 零 TCP 均有断言。

最终门禁及提交结果见进度台账；原始 TRX/日志保留在
`TestResults/local-completeness/group-limit-*`（忽略目录，不随包/提交发布）。
本模块未访问真实数据库，不新增服务器/代理、独立运行时、平台或性能兼容结论。
地址级策略、其他工作包及 23 个 ID 的整体关闭继续开放；不自动重放 SQL。

### 15.65 2026-10-26 发现地址级黑名单覆盖

基于 `414b7cc`，归属 P1-09/W4。TNS/OCP 的 ADDRESS 可提供一个 OBLB_BLACKLIST，
整块覆盖 ADDRESS_LIST，其后才是 DESCRIPTION；空块或 partial 块使用已有默认值，不合并。
显式策略随 immutable endpoint occurrence 排序，重复 host/port 可采用不同有效策略；
等值策略和精确 endpoint 继续共享历史，成功只清除实际候选的有效策略历史。
仅地址级策略时共享上限缺省 120，不激活其他地址的黑名单或 fallback。
组/共享 TCP 上限、总期限、反馈边界、池代数与健康 idle 规则保留，无 SQL replay。

新增 39 项首次为 4 通过/35 失败，明确复现原有地址块拒绝；覆盖 TNS/OCP、整块继承、
空/partial 块、重复地址/等值策略历史、权重/健康/RoundRobin 排序、组上限、无策略 fallback、
重载快照及同步/异步的池预热、坏配置在 idle 租用前失败与修复恢复。
最终完整单测、消费者、构建及独立走查结果见最新进度台账；原始 RED/日志保留于
`TestResults/local-completeness/address-blacklist-*`，忽略目录不随提交或包发布。
未访问真实数据库；不新增真实 OCP/OBProxy、独立运行时、平台或负载兼容结论。
其余工作包和全部 23 个 ID 的整体关闭继续开放。

### 15.66 2026-10-26 BLOB 家族结果码

P1-08/W5、P1-10/W6：共享结果读取原先只接受252，补齐249/250/251的
TINY/MEDIUM/LONG BLOB或TEXT名称、Binary/String身份、严格collation解码及IsLong/宽度。
沿用既有length-encoded缓冲/流式路径，无新增wire格式或依赖；MySQL DataTypes补六种声明。
Oracle SQL声明和LOB locator契约保留独立，接受共享wire码不代表实库一定发出这些码。

新增99项单测（96种公开reader组合和3项严格/二进制边界），既有字符集回归扩展到三种码；
首次235项为4通过/231失败，相关GREEN537通过。新消费者120项只覆盖MySQL/异步，
双模式/同步路径由单测覆盖。最终完整门禁及独立走查见最新进度台账。
保留空内容、NULL、邻列、后续命令及未知/损坏字符集断言；本模块未新增大包边界或实库证据。
报告位于忽略目录`TestResults/local-completeness/blob-family-*`。其余本地开发及全部ID继续开放。

### 15.67 2026-10-26 Close 事务 ILogger 重入

P0-01/W1：原先Close捕获清理在归还/销毁会话前同步发布事务日志；同一满池的回调Open
因此等待自身清理。现仅对这次Close的事务日志缓存completion字段快照，在exchange释放、
会话归还/销毁和普通池登记释放后、Closed日志前发布。保留恢复警告/回滚成功或失败顺序，
显式Commit/Rollback/事务Dispose仍即时记录。日志回调重开并创建新事务后，旧Close不再触碰新租约。

新增100单测和40消费者；首次有限等待48项均因池超时失败。初次GREEN的单连接测试桩无法
接受错误后替换连接，中断于86通过/18失败，保留原始报告；修正后相关368通过，随后增加四项
日志回调启动新事务后抛异常的所有权回归。一次测试变量类型编译错误已修复；最终Release
构建零警告/错误，完整单测6660、相关消费者227通过，无失败/跳过。单测覆盖双模式/同步异步、原/其他借用者、无限/有限等待及回滚/恢复失败；
五资产消费者覆盖双模式及同步异步成功路径。仅本地证据，未访问实库。
Activity/Meter、命令日志及其他重入/并发/长期资源与真实部署验收继续开放，全部ID不因此关闭。

### 15.68 2026-10-26 ENUM、SET 和 GEOMETRY 结果码

P1-08/W5、P1-10/W6：247/248分别报告ENUM/SET，collation63保持byte[]，其他受支持collation
严格解码，SET不拆分、不修剪、不映射CLR枚举。255仅MySQL模式，保留整个不透明byte[]，
忽略collation且不转换SRID/WKB。复用既有length编码及buffered/sequential text/prepared路径。
MySQL DataTypes增加使用现有String的ENUM/SET标签列表模板；不增加Oracle相应SQL声明、
GEOMETRY SQL目录项、空间API或参数类型。

新增81单测及100消费者；RED99项为19通过/80失败，相关GREEN480全通过，Release构建
零警告/错误。完整单测6741、相关消费者380及目录消费者16通过，无失败/跳过。单测覆盖适用双模式、同步异步及严格编码/未知collation/
原始字节/空值/NULL/后续字段与命令复用；五资产消费者仅MySQL异步执行。
固定Java参考的ENUM/GEOMETRY getObject及通用length编码提供源码先例；其SET getObject
分支未实现，不能以类型枚举存在宣称Java完整支持。此处SET按共享length编码及字符集契约实现。
未访问实库，不证明服务器实际返回这些码、ENUM/SET SQL部署或空间能力；其余类型码与全部ID继续开放。

### 15.69 2026-10-26 Oracle ROWID 结果码

P1-08/W5：209报告ROWID，使用现有String身份及严格session UTF-8；包括collation63或未知
collation在内均保留字符串，不解析、不归一化标识符。MySQL明确拒绝。复用既有length编码
及text/prepared buffered/sequential路径，保留空值/NULL/原始长度元数据；没有增加公开值类型、
参数wire码或SQL目录声明。固定Java JDBC4ResultSet.getObject将ROWID包装为RowIdImpl(getString)，
二进制行使用通用length编码，是源码依据而非实库验收。

新增15单测（8项公开reader组合、3项collation元数据、3项畸形UTF-8及1项MySQL拒绝）和
20项Oracle异步五资产消费者。RED为1通过/14失败；相关GREEN464通过，无失败/跳过。
最终Release构建零警告/错误，完整单测6756、相关消费者400通过，无失败/跳过；候选包及
实际8/9/10的六次既有本地样例运行通过，Framework4.8仅API/配置烟测。独立走查无阻塞。
未访问实库，不证明真实ROWID发出、SQL/参数或部署
行为。NUMBER_FLOAT206的精度/scale契约及其他Oracle资源形状继续开放，全部ID不因此关闭。

### 15.70 2026-10-26 Oracle 浮点特殊文本结果

P1-08/W5，基于7d05ce4：Oracle type4/5文本结果新增完整、大小写不敏感的`Inf`、`-Inf`、
`NaN`短token，保留Single/Double的CLR及provider身份。有限Single继续直接解析，避免先转
Double再转换造成二次舍入；保留负零。既有CLR可解析格式仍可用；MySQL文本、IEEE二进制
行及参数编码保持原契约。固定Java TextRowProtocol.getNanAndInfForOracle提供源码依据，
不照搬其前缀匹配。

新增20单测及40项Oracle异步五资产消费者。RED为8失败/12通过；相关GREEN77通过。
最终Release构建零警告/错误，完整单测6776及消费者40通过、无失败/跳过，独立源码/文档
走查无阻断，git diff --check通过。覆盖特殊/有限值、负零、NULL、类型/schema、
后续字段及命令复用；五资产消费者同一.NET10host。未访问实库，真实发出格式、SQL及部署
互操作仍开放。上一模块包/独立运行时结果属于历史证据，全部23项及八工作包继续开放。

### 15.71 2026-10-26 national Unicode collation 映射一致性

P1-08/W5，基于f3c484d：national fallback及三列字符集名称查询此前仅接受6个UTF-8和3个
UTF-16BE ID，现复用普通结果的118个UTF-8及29个UTF-16BE已知ID。共享家族判定没有改变
普通结果映射/宽度；保留严格解码及可信National > Results > collation优先级。fallback仍
拒绝其他编码、UTF16LE、binary63和未知ID，不扩展参数/complex输入编码或会话名称范围。

净新增33单测：2项遍历147个ID、4项严格字节/增量解码、23项其他编码/holes/未知值拒绝及
可信设置覆盖、4项新增309/342同步异步查询；既有54双路径保留。RED35为10失败/25通过，
其中包含既有2项；相关GREEN371通过，无失败/跳过。Release构建零警告/错误，源码走查
无阻断；相关既有消费者480通过，无失败/跳过（同一.NET10host，新增查询ID由单测直接覆盖）；
完整单测6809通过，无失败/跳过，最终源码/文档走查无阻断，git diff --check通过。
门禁期间观察到主机时钟跳变，原因未确定，原始报告保留。未访问实库，不证明national
存储、实际协商或部署互操作，全部23项及八工作包继续开放。

### 15.72 2026-10-26 MySQL Batch INSERT rewrite

P2-03/W7，基于7c1a602。现代OceanBaseBatch新增默认关闭的RewriteBatchedInserts，仅NonQuery
合并相邻完全相同的单tuple MySQL INSERT VALUES，保留服务器参数绑定。Oracle、Reader/Scalar/
Prepare、非Input、Stream及不支持/不确定语法仍逐条；Stream不预读、不代调用方释放。四种
SQL_MODE扫描比较tuple边界及每个marker，262144-token保守上限回退。chunk限制65535行/参数，
分别预算含命令字节的UTF8 SQL及含header/bitmap/types/value的64MiB请求。服务器上限可能更小。

成功total==rows时每项1，否则各项-1、NonQuery保留独立总数。失败保留确认前缀，group/suffix
未知；合并异常BatchCommand=null，新只读BatchCommandGroup保留原命令引用及顺序，非合并为null。
活动临时命令在开始前登记，可被Dispose/Async取消；合并失败日志回调期间不提前释放预留。
沿用显式/ambient事务、原会话/Open generation及调用方取消来源，不隐式提交、不重放未知写。
合并改变statement原子性/autocommit、default/expression/trigger/warning及timeout粒度，不能承诺
逐项语义相同。公共细节见[契约](obconnector-dotnet-architecture-design.md#batch-insert-rewrite)。

新增78单测及12现代资产消费者；聚焦92通过。最终完整单测6887、受影响消费者353通过，
无失败/跳过；Release构建零警告/错误，独立源码/测试/文档走查问题已回归修复，diff检查通过。
五资产消费者同一.NET10host；新rewrite覆盖三现代资产。参数/行及64MiB边界为反射planning检查，
不等于真实大包运输。定时取消用例曾复现token丢失，但不保证每次都在parser阶段取消。
完整门禁及中止旧候选记录见进度文档。开发期保留option、
execution及parser初始RED；另复现并修复失败回调提前释放、comment中NUL、规划取消token丢失、单例Dispose竞态及null SQL异常类型。
测试对端sequence/queued close、缺失using及Oracle/MySQL默认隔离级别误用的失败日志保留。
本轮没有访问实库。真实SQL、触发器/默认值/错误计数、实际包限额、OBProxy、TLS/压缩及吞吐/
内存对照仍待验收；multi-send/pipelining及独立bulk协议仍未实现。全部23项及八工作包继续开放。

### 15.73 2026-10-26 MySQL client string preparation

P1-08/W5, developed from6fc1891. Existing opt-in automatic fallback now accepts ordinary MySQL
strings after exact1295/HY000. Every string fallback reads client/connection charset, connection
collation and SQL mode on the same exchange, with the original transaction, timeout/cancellation
and frozen bindings. No session cache or new option. UTF8/UTF8MB3/UTF8MB4 only; ordinary literals
retain session collation, use mode-aware escaping and reject invalid/unrepresentable Unicode.
Four-mode marker scanning plus a single-statement boundary prevents earlier statements from changing
parse state. Existing64MiB budget includes expanded literals. Discovery failure stays before target
execution; a rejected complete result can permit reuse (including an empty result), while an
incomplete or corrupt response retires the session.
See the [public contract](obconnector-dotnet-architecture-design.md#mysql-client-string-preparation).

New units66 and consumers20; focused111, complete units6953 and affected consumers403 passed, no
failures/skips. Release solution build passed with zero warnings/errors. Independent source/test/docs
review found no remaining blockers. Public checks cover changed SQL modes on repeated fallback,
rejected charset then recovery/requery, malformed shape/NULL/type/encoding/size/rows/results, discovery
ERR/connection ERR/disconnect, sync/async cancel/timeout and original caller token, explicit transaction,
concurrent command rejection and frozen parameter/text/opt-in snapshots. Five assets are loaded in
one.NET10host for sync/async standard/OB20 consumers; this is not five independent runtime evidence.

Retained initialRED4 proves missing trusted state. A declared-type validation-order regression was
reproduced and fixed; fixture failures for invalid Unicode in InlineData and Windows reset versus EOF
remain separately recorded. Runtime surrogate construction and reset-command reads now verify the
intended malformed data and prevent a sent target from passing as a sequence error. Logs/TRX are in
ignored TestResults/local-completeness/mysql-client-strings-*. No real database was accessed. Non-UTF-8
clients, NUL under NO_BACKSLASH_ESCAPES, real literal/parameter collation and coercibility, mode/version/
proxy/TLS/runtime and performance matrices remain open. This does not close P1-08, all23 IDs or eight
work packages, or establish that all remaining offline implementation is complete.

### 15.74 2026-10-26 Packaged protocol runtime verification

P2-05/W8, developed from0484943. Verify-Packages.ps1 now additionally runs a portable protocol
companion through the actual restored product nupkgs. Eight scenarios/profile cover Oracle/MySQL,
sync/async commands and buffered/sequential binary readers: automatic integer prepare/bind, metadata/
value/NULL, statement close, known terminal ERR/recovery, transaction commit/autocommit restoration,
capacity-one reset/reborrow/ping and physical close on pool clear. Standard2.0 transaction begin/commit
and Close use synchronous APIs, including its async-command cases. No product code, dependency or
release policy changed. Peer bounds, timeout cancellation and combined failure reporting apply.

Windows48/48 scenarios passed without skips across net8/8.0.32, net9/9.0.21, net10/10.0.13,
Standard2.0 and2.1/10.0.13, and Standard2.0/Framework4.8.10008.0 (Release534296). Both product hashes,
asset and dependency selection were checked in fresh restore caches. All consumer builds passed with
zero warnings/errors. Four isolated negative controls rejected corrupted row value, bound request,
server-error expectation and major runtime roll-forward; the error expectation runs on desktop CLR.
Modern Main explicitly asserts its target runtime major (major10 for Standard ALC), supplementing
the existing dependency-major checks. The roll-forward negative control does not establish an earlier
wrong-host false pass. Explicit Framework host type/message/inner
reporting replaced unprintable unhandled async exception diagnostics and returns a nonzero exit code.
The stack-formatting cause remains unproven. Candidate harness mismatches and logs are retained in
the [progress record](obconnector-dotnet-progress.md#2026-10-26-packaged-protocol-runtime-verification).

This adds local Windows packaged protocol evidence only. No real database was accessed; complete
feature/runtime/platform/real server/OBProxy/TLS/auth/fault/load/publication matrices remain open.
All23 gap IDs/eight work packages and further offline implementation remain open.

### 15.75 2026-10-26 GB18030-2022 ordinary results

P1-08/W5, developed from3196a45. All ten2022 ordinary-result collations in the pinned v4.3.5_CE map
(216–222/330/345/360) select a separate strict encoding using the existing ChineseEncoding decoder
and18 reversible PUA/assigned-scalar swaps.2005 IDs/mappings and the prior U+1E3F correction are
preserved. Width4 metadata, text/prepared and buffered/sequential reads share the same path. No new
dependency/public option or client/national/JSON/CLOB encoding contract is introduced. See the
[fixed sources and public contract](obconnector-dotnet-architecture-design.md#result-character-sets).

InitialRED20 demonstrated explicit unsupported collations; GREEN20 and affected498 passed, no
failures/skips. Fresh OpenJDK21.0.12.1+1 output matches all63,488 BMP scalar encoding and23,940
two-byte/39,420 BMP four-byte decoding digest expectations. Every remap pair tests both directions,
legacy mappings and all incremental split/count/reset boundaries. Unsupported216/222 fixtures were
replaced with unknown223/324 holes; strict malformed/truncated/out-of-range checks remain. Release
solution build passed with zero warnings/errors. Complete units7042 and affected consumers420 passed
without failures/skips,8minutes11seconds and1minute56seconds respectively. Independent source/tests/
contracts review found no remaining blockers. Five-asset consumers add20 MySQL async four-shape cases
on one.NET10host; dual-mode sync/async ordinary-column fixtures run in the unit suite. Pure codec cases
cover ordinary string/BLOB families; no new public2022 BLOB or tiny-buffer/endpoint-specific matrix.

No real database was accessed. Historical2005 real reports do not validate2022; actual emission,
storage, complete collation/mode/version/proxy/TLS/runtime/platform and remaining offline functionality
stay open. All23 IDs/eight work packages remain open.

### 15.76 2026-10-26 DEC8 and TIS620 ordinary results

P1-08/W5, developed fromd842fee. All five DEC8 and five TIS620 IDs in the fixed v4.3.5_CE map now
use strict single-byte result tables and width1 metadata. C1 controls remain controls; these are not
Latin1/Windows874 aliases. All defined byte assignments match the pinned server tables. Fourteen DEC8
zero placeholders and nine TIS620 FFFD placeholders fail explicitly, including counts/incremental
reads; this deliberately applies the strict result policy to the server's lenient TIS620 placeholders.
No new option/dependency, client charset negotiation or national/JSON/CLOB expansion is introduced.

RED12 showed explicit unsupported-collation errors; GREEN12 and expanded focused576 passed without
failures/skips. Independent server-table regeneration matched exhaustive256-byte SHA256 expectations.
Every ID tests reverse BMP representation/refusal; shared fixtures add32 dual-mode sync/async reader
cases and pure codec/schema coverage for ordinary string/BLOB types. Tiny decoder buffers, reset/count,
array bounds and malformed sequential/Open-state are covered; valid shared readers execute reuse SQL.
Release solution build passed with zero warnings/errors. Affected five-asset consumers460 passed
without failures/skips, adding40 MySQL async four-shape cases on one.NET10host. Independent source/
tests/contracts review found no blockers. Full units7140 passed without failures/skips in8minutes9seconds;
final diff whitespace check passed. Full gate results belong in the
[module record](obconnector-dotnet-progress.md#2026-10-26-dec8-and-tis620-ordinary-results).

No real database was accessed. SJIS/Big5/HKSCS/HKSCS31 and other missing capabilities, actual server
emission/storage and complete deployment matrices remain open. All23 IDs/eight work packages remain
open; historical real reports do not validate this module.

### 15.77 2026-10-26 SJIS ordinary results

P1-08/W5, developed from37a0aba. All five SJIS IDs in the fixed v4.3.5_CE map now use a strict
CP932 adapter with seven source-specific mapping corrections and width2 metadata. It supports all
6879 assigned pairs plus ASCII/halfwidth, rejects Windows extensions/unassigned/malformed bytes,
preserves pending decoder state and rejects reverse Windows aliases. Both5C/815F decode backslash;
the server's canonical reverse encoding is815F. No client charset option, parameter encoding,
dependency or national/JSON/CLOB expansion is introduced.

RED11 showed explicit unsupported-collation errors; GREEN11 and expanded focused637 passed without
failures/skips. Fresh independent source-table regeneration matches exhaustive256-single/16640-pair
decode and65536-BMP encode/refusal digests. Count/reset/split/tiny-output/overflow-retry tests and
all-ID pure codec/schema coverage accompany16 new dual-mode reader cases. Valid readers verify
NULL/exact values/reuse SQL; two malformed sequential cases verify the consumed connection stays Open.
Release solution build passed with zero warnings/errors. Affected five-asset consumers480 passed
without failures/skips, adding20 MySQL async four-shape cases on one.NET10host. Independent source/
tests/contracts review found no blockers. Full units7201 passed without failures/skips in8minutes12seconds;
README lists the current result families. Final documentation and diff checks passed. Full results belong in the
[module record](obconnector-dotnet-progress.md#2026-10-26-sjis-ordinary-results).

No real database was accessed. Big5/HKSCS/HKSCS31 and other missing capabilities, actual server
emission/storage and complete deployment matrices remain open. All23 IDs/eight work packages remain
open; earlier reports retain their historical scope.

### 15.78 2026-10-26 Big5 ordinary results

P1-08/W5, developed from4821d54 on master. All five Big5 IDs in the fixed v4.3.5_CE map now select
a strict lazy CP950-derived lookup with15 scattered and249 C6/C7 corrections, plus width2 metadata.
It supports13710 defined double-byte positions and128 ASCII characters, with exact reverse mappings.
Seven source FFFD placeholders, zero holes, Windows extensions/aliases and malformed/truncated bytes
fail. Incremental count/reset/invalid-flush/overflow preserve decoder state. No client option,
parameter/national/JSON/CLOB encoding change, resource or dependency is introduced.

RED7 showed explicit unsupported-collation errors; GREEN7 and expanded focused698 passed without
failures/skips. Fresh source-table regeneration matches exhaustive256-single/32768-pair decode and
65536-BMP encoding/refusal digests. Every valid pair additionally tests split/count/reverse behavior;
all-split/tiny Convert/bounds/state cases and all-ID nine-wire-type codec/schema coverage accompany16
new dual-mode reader cases. Valid readers execute reuse SQL; malformed sequential cases verify the
consumed connection stays Open. Release solution build passed with zero warnings/errors. Initial
independent source/tests review found no blockers. Affected five-asset consumers500 passed without
failures/skips, adding20 MySQL async four-shape cases on one.NET10host. Final independent docs and
provenance-facts review found no blockers. Full units7262 passed without failures/skips in8minutes11seconds;
final staged diff whitespace checks passed. Full results belong in the
[module record](obconnector-dotnet-progress.md#2026-10-26-big5-ordinary-results).

No real database was accessed. HKSCS/HKSCS31 and other missing capabilities, actual server emission/
storage and complete deployment matrices remain open. All23 IDs/eight work packages remain open;
earlier reports retain their historical scope.

### 15.79 2026-10-26 HKSCS and HKSCS31 ordinary results

P1-08/W5, developed from4ce3a35 on master. All eight pinned HKSCS/HKSCS31 IDs now select distinct
strict scalar lookups with width2 metadata.3186 BMP corrections/62 undefined-slot removals adapt CP950;
1651 revised Plane2 replacements distinguish HKSCS31. Each family supports19848 defined positions,
decoding to19754 distinct scalars;66 source reverse preferences and U+2593-to-F9FE/U+FFED asymmetry
are retained. Malformed/unrepresentable values fail; supplementary counts and decoder retries retain
pending state. Ordinary-result support does not expand client/parameter/national/JSON/CLOB encoding.

RED14 captured unsupported collations; GREEN14 passed after strict CP950 duplicate refusals were
handled before applying source corrections. Independent source-table regeneration matches exhaustive
decode/BMP/Plane2 digests. All-ID codec/schema coverage and32 new dual-mode readers accompany strict
malformed/state/bounds tests. Release solution build passed with zero warnings/errors; affected
five-asset consumers540 passed without failures/skips on one.NET10host. Independent source/tests
review found no blockers; documentation review's current-summary omission was corrected.
Complete units7353 passed without failures/skips in8minutes15seconds; final diff whitespace checks
passed. The
[module record](obconnector-dotnet-progress.md#2026-10-26-hkscs-and-hkscs31-ordinary-results) retains
commands, results and limitations. No real database was accessed.

The [delivery overview](obconnector-dotnet-delivery-status.md) now tracks all23 original IDs, separates
implemented subfeatures from remaining code and external gates, and gives commit evidence. All23 IDs/
eight packages remain open because whole-gap closure criteria are broader than these local modules.

### 15.80 2026-10-27 Client SQL and parameter character sets

P1-08/W5, developed from33c052b on master. The `Character Set` option adds twelve strict,
ASCII-preserving input families with canonical pool identity, managed initialization/reset and
four-value session validation. Text/prepare/database requests, ordinary scalar/complex/array inputs,
procedures, metadata parameters and byte budgets use the chosen encoding. Authentication and
initial identity remain UTF-8; results keep existing column decoders and UTF8MB4 result settings.
The [public contract](client-character-sets.md) records the exact names and restrictions.

Verified state is cached. Classified stateful/opaque SQL and reported mismatches invalidate it;
the next input payload rechecks before upload/execution. Generated ASCII XA controls bypass the
probe. Rechecks can change session diagnostic functions after conservative classification, and
arbitrary hidden function/trigger effects are not continuously detected. JSON parameters remain
default-utf8mb4-only; MySQL string fallback remains UTF-8-only. Standalone national parameters are
independent; complex national restrictions and CLOB UTF-8 reads retain their existing boundaries.

Focused units166 and final format verification passed. Serial Release build passed with zero
warnings/errors. Final complete units7491 and affected consumers560 passed, zero failures/skips;
independent final source/tests/docs review has no remaining actionable finding. The
[module ledger](obconnector-dotnet-progress.md#2026-10-27-client-sql-and-parameter-character-sets)
retains initial RED, the first full-run failures and their corrections. No real database was
accessed; all23 IDs/eight packages retain their complete closure requirements.

### 15.81 2026-10-27 CLOB input charset validation before writes

P1-08/W5, developed fromb66cda1 on master. Whole-input UTF-8 validation alone did not prevent
an unrepresentable suffix under a configured client charset from failing after a completed
DBMS_LOB.WRITE prefix. Twelve sync/async protocol regressions across six charsets reproduced
one write when none should occur. Non-default input is now validated in the same8191-scalar
chunks as writes, all before ownership/I/O, preserving bounded encoding temporaries. Default
UTF-8, scalar offsets, locator replacement and later server/network partial-failure semantics
retain their existing contracts. Rejected input leaves the original handle usable.

Four positive boundary cases check supplementary scalars, exact default/GB18030-2022 wire bytes,
amounts/offsets and returned locator reuse. Focused26, serial Release build and format verification
passed. Final complete units7507 passed in8minutes39seconds; affected units455 and consumers1000
passed, all with zero failures/skips. Independent final source/tests/docs review has no remaining
actionable finding. Consumers load five assets in one.NET10host.
The [module ledger](obconnector-dotnet-progress.md#2026-10-27-clob-input-charset-validation-before-writes)
retains initial RED, the stopped earlier full run, invalid-response fixture race, assertion
correction and review findings. No real-server claim or broader JSON/national/CLOB storage support
is introduced; all23 gaps/eight work packages retain their remaining criteria.

### 15.82 2026-10-27 JSON parameters with client character sets

P1-08/W5, developed from1da3ea8 on master. Explicit MySQL JSON input now uses its existing
type253 string binding and the selected strict client codec. Only the redundant managed-charset
prohibition is removed; JSON results remain UTF8, SQL NULL differs from JSON text null, and
Oracle/precision/scale restrictions and client-literal fallback limits remain. Source-backed
server JSON-expression conversion is documented in the [contract](client-character-sets.md).

Twelve codec RED cases reproduced the old prohibition; focused111 and five-asset affected
consumers570 now pass without failures/skips. Release solution build has zero warnings/errors;
format verification passed with a workspace-load warning. Complete units7543 passed without failures/
skips in8minutes30seconds; independent source/callers/tests/contracts review found no actionable issue.
All ten new real-storage cases failed at the initial greeting before authentication
or JSON; both existing default-charset controls failed there too and a raw TCP probe received no
greeting bytes. Underlying server/network cause is unknown; storage/proxy compatibility is unproved.
Original evidence is retained in the
[module ledger](obconnector-dotnet-progress.md#2026-10-27-json-parameters-with-client-character-sets).
All23 IDs/eight work packages remain open.

### 15.83 2026-10-27 Oracle NUMBER_FLOAT results

P1-08/W5, developed from b94a421 on master. Oracle result206 now uses exact
OceanBaseDecimal/Decimal provider identity with FLOAT type name in both row protocols.
Schema precision1..126 is binary digits, unknown0/255 is null and variable scale is
null; only unknown wire scale-85/-1 is accepted. Fixed decimal constraints are skipped
while existing Oracle NUMBER range validation remains. DataTypes and CommandBuilder
honor this distinction; generated parameters/rebinding use existing NUMBER246 without
copying binary precision. Scalar PL and existing NUMBER-array outputs retain exact
values, NULL handling and atomic publication. No new enum/value type/dependency is added.

Fixed source evidence, complete scope and test outcomes are in the
[contract](obconnector-dotnet-architecture-design.md#oracle-float-results-with-binary-precision)
and [module ledger](obconnector-dotnet-progress.md#2026-10-27-oracle-float-results).
Independent review found and verified the CommandBuilder precision-copy fix.
Complete units7578, focused204 and affected consumers229 passed, zero failures/skips;
serial Release build has zero warnings/errors and changed-file format passed with
a workspace-load warning. Final independent review has no remaining actionable issue.
New real Oracle cases and existing NUMBER controls all failed at the initial greeting,
before authentication or SQL; no real FLOAT storage/version/proxy success is claimed.
FLOAT routine derivation, new complex member codecs and other missing shapes remain;
all23 IDs/eight packages remain open. Earlier NUMBER_FLOAT-open statements are historical
to their recorded candidates, including15.69.

### 15.84 2026-10-27 Oracle FLOAT routine derivation

P1-08/P1-10, W5/W6, developed from184a767 on master. FLOAT/NUMBER_FLOAT signatures
derive existing Decimal parameters with Precision0 and undeclared scale. Binary
precision1..126 or NULL/-1 and variable scale NULL/-85/-1 are accepted; dimensioned
DATA_TYPE/fixed-scale/other precision shapes fail. Exact NUMBER246 binding leaves
server FLOAT coercion/rounding intact. MySQL FLOAT remains Single. Shared directory
SQL recognizes IN/OUT and INOUT and exposes raw NUMBER_FLOAT numeric metadata.

The pinned ALL_ARGUMENTS view lacks CHAR_LENGTH. Oracle argument discovery now omits
it first and only queries it for five character families, with unchanged bound
criteria/session/transaction and full other-column signature comparison. Character
Size still uses character count, never byte length. Missing CHAR_LENGTH preserves
the server error and original parameters; old-server character derivation remains
unsupported when this metadata is absent. No API/enum/dependency was added.

The [contract](obconnector-dotnet-architecture-design.md#routine-parameter-derivation)
and [module ledger](obconnector-dotnet-progress.md#2026-10-27-oracle-float-routine-derivation)
record fixed sources,78 new units,20 new five-asset consumers, original RED/stopped
fixture evidence and review corrections. Focused189 and affected consumers168 passed,
zero failures/skips; Release build has zero warnings/errors, format exited0 with a
workspace-load warning. Complete units7656 passed in8m49s, zero failures/skips;
final independent source/tests/docs/report review has no remaining actionable issue. New real
procedure/function cases compile only; no real DDL/derive/INOUT/rounding/version/proxy
success is claimed. Previous greeting failures retain their independent records.
All23 IDs/eight packages remain open;15.83's FLOAT-derivation-open statement describes
its earlier candidate rather than this implementation.

### 15.85 2026-10-27 Oracle复杂类型 BLOB/CLOB 结果成员

P1-07/W5, developed from `103509c` on `master`. Complex member codes 210/211 now load in named
descriptors and anonymous collection metadata. Binary result materialization reads the
length-encoded locator and publishes an `OceanBaseLob` tied to the active physical session,
transaction and reader epoch when `Use Lob Locators=true`; NULL members and buffered/sequential skip
paths do not create handles. Non-empty complex LOB input and InputOutput parameters remain rejected
because the parameter wire contract is not established. Cursor members, further national/anonymous
shapes and whole-complex RETURNING remain open.

The module regressions cover dictionary loading/input rejection, synchronous and asynchronous named
and anonymous BLOB/CLOB result handles, anonymous BLOB/CLOB element metadata, and metadata rejection
before materialization when locators are not negotiated. The old guard produced the initial RED;
focused coverage is 13 passed and the complete complex unit family is 426 passed, all
with zero failures/skips. The no-TLS affected consumer selection is 220 passed; the broader
Complex/Lob filter is 680 passed and 260 failed while constructing TLS/PFX fixtures. A complete
unit attempt is 7521 passed/143 failed; its feature-specific stale BLOB assertions were corrected
afterward, while the remaining failures are the pre-existing certificate/SSPI environment cases.
Release solution build has zero warnings/errors, production-source format verification passes with
the existing workspace-load warning, and independent source/tests/docs review has no actionable
finding. All evidence is local loopback; no real OceanBase/OBProxy complex LOB interoperability,
DDL support or external locator read is claimed.

### 15.86 2026-10-27 P2-04 池等待结果诊断

P2-04/W7：既有 `oceanbase.client.pool.wait.duration` 直方图现在增加标准
`oceanbase.outcome` 标签，值为 `acquired`、`timeout`、`canceled` 或 `error`。
同步和异步等待在信号量边界分类；原有从本次 Rent 开始累计的耗时、容量、取消和超时行为不变。
标签在等待者记账完成、phase finish 回调前发布，因此不包含监听器回调耗时。

回归先在旧无标签实现上复现取消和超时结果缺失，随后同步/异步 2 项通过。受影响诊断选择
228/230 通过；2 项 TLS phase 用例在导入 Windows PFX 夹具时失败，尚未进入池等待路径。
本模块没有访问真实 OceanBase/OBProxy，也没有给出负载或监听器开销结论。池信号量之外的其他
资源等待、持续运行和部署性能验收继续开放。

### 15.87 2026-10-27 Oracle 文本游标值列

P1-06/W5：Oracle 文本结果中的 type163 列现在复用二进制游标列的四字节小端
标识解码和 `OracleCursorOwner` 发布路径。文本执行也创建并绑定当前执行 owner；游标
仍保持单次读取、原会话/事务/reader 代际和未消费资源销毁规则。非四字节值、NULL、
非 Oracle 模式和没有执行 owner 的元数据继续拒绝。

新增同步/异步、缓冲/SequentialAccess loopback 回归4项：文本 `SELECT` 返回游标列，父
reader/command 先关闭，随后子游标分页读取并复用连接。四项均通过；既有二进制
cursor-column 选择76项仍通过。
Connector/J 固定参考的文本行实现仍不发布游标对象，因此该能力是 .NET 的本地协议扩展，
不是 Java 对齐证明。未访问真实 OceanBase/OBProxy；当前 oracle-direct 探针仍在 greeting
前失败，真实 SQL、版本、代理和部署兼容性继续未验证。聚焦游标选择80项通过；完整单测
7533通过、139项因既有 Windows TLS/PFX/SSPI 夹具失败，0跳过，总计7672项。Release
构建和限定格式检查通过。REF CURSOR Input/InputOutput、
复杂 cursor 成员及其他 Oracle 输出形状仍开放。

### 15.88 2026-10-27 Oracle 复杂类型 cursor 结果成员

P1-07/W5：复杂 object/collection 描述现在接受成员类型 163。二进制结果中的非 NULL 成员必须是四字节小端游标识，并通过当前执行的 `OracleCursorOwner` 发布为 `OceanBaseRefCursor`。父 reader 或 command 关闭后，子游标仍保持原会话、事务和代际所有权；缓冲和 `SequentialAccess` 均沿用同一生命周期。普通复杂列只在布局确实包含 cursor 成员时才进入 cursor owner 路径，保留原有 prepared statement 复用。

新增同步/异步、缓冲/`SequentialAccess` 正向回归 4 项及四字节以外标识的会话失效 4 项；另外覆盖了匿名集合的 type163 布局元数据。复杂 cursor 参数、文本复杂结果、真实 OceanBase/OBProxy 互操作与其他 Oracle 结果形状仍未验证。

### 15.89 2026-10-27 Oracle 输出游标标识严格校验

P1-06/W5：Oracle 纯 `Output`/`ReturnValue` 的 REF CURSOR 输出捕获现在复用普通列和复杂成员的
四字节小端标识校验。校验在查找 `OracleCursorOwner` 之前执行，尾随字节不会被静默丢弃，也不会
被错误归类为“没有 originating command”。新增直接 `OracleCommandPlan` 回归 1 项通过；该项只
证明本地输出 framing，REF CURSOR Input/InputOutput、其他输出形状和真实 OceanBase/OBProxy
互操作仍开放。

## 16. 剩余交付工作包

以下拆分面向实施和评审，沿用第 4、14 节全部 23 个 ID；每个 ID 只归入一个主工作包，跨模块依赖不因此消失。它区分新增能力、已有实现的验证和范围决定，避免把已完成模块重新开发。所有工作包尚未整体关闭，负责人和交付日期未分配。

| 工作包 / 覆盖 ID | 剩余开发或范围决定 | 必须交付的验证产物 | 主要依赖 |
| --- | --- | --- | --- |
| W1 池与会话隔离：P0-01、P0-02、P0-06 | 保留 Oracle fresh/reset 自动提交基线及真实直连专项（第 5.1.5 节），补部署矩阵；保留会话识别/不透明调用及目标发送销毁规则；内部配置归一化、容量/清池统一及原始空因子身份已有本地实现（第 5.1.4 节），补部署验收；保留 XA 专用销毁及 prepared 跨关闭恢复的直连专项（第 5.1.6 节）；保留旧 reader 延迟关闭租约隔离及八项本地/二十项消费者/四项直连回归（第 5.1.7 节）；保留第 5.1.8/15.43/15.44 节内联 LOB/REF CURSOR 阶段及外置持久 LOB 完整读取与本地中断专项；补隐式副作用、完整 reader/cursor、外置 LOB 真实中断及XA 故障恢复与隔离，保留第15.48节Closed事件内满池重开与资源释放回归及第15.58节通知后取消交接修复，补其他日志重入/并发/长期资源，决定统一 shutdown 范围。已有容量、探活、预热、路由/证书隔离、清池/退休及指标保留 | 保留等效配置及五资产/双模式直连证据；补双借用者污染/隔离、满池、清池、取消、预热、Dispose/reset、Close/Open 交错及长期资源曲线、真实 OBProxy reset 报告 | G0 契约；与 W2/W4 联合验收 |
| W2 事务与失败恢复：P0-03、P0-04、P0-05 | 保留已交付的 PSPE 单资源/终态、初始化与晚回调/池身份、自动初始化取消/池与协调器恢复及超时本地回归；保留第5.2.1/15.45节真实响应丢失及单容量池恢复、[应用核对示例](unknown-outcome-recovery.md)；保留第15.49节连接级ERR退休、当前请求unknown及显式重建本地回归；补真实事务管理器/代理故障及完整重建矩阵；分布式提升拒绝，透明恢复另定范围 | 发送前/后、响应中、commit/rollback 回包丢失、超时/in-doubt、提前关闭的故障矩阵；异常分类、会话销毁及业务核对示例；无未知写 replay | W1 租约与隔离规则 |
| W3 协议选项：P1-01、P1-02、P1-03 | 标准 MySQL 压缩已本地实现；先确认客户端 OB20 压缩契约再补实现，并补 reroute；保留已实现的 opt-in 文本多语句和应用流 LOCAL INFILE，明确不支持的组合 | 客户端链路/版本/capability 依据；协商成功/拒绝、分片/序列/校验/解压限额；多结果中途错误；上传超限/取消/代理策略；真实 OceanBase/OBProxy 互操作 | G0 及第 16.3 节；reroute 依赖 W2/W4 |
| W4 信任、发现与凭据：P1-04、P1-09、P1-11 | 凭据提供/轮换已有本地实现，补真实凭据服务与插件验收；保留第15.50节TCP整轮重试/固定退避及共享预算、第15.51节端点TCP健康缓存；补真实节点/代理健康与故障验收，保留第15.52节TNS文件及第15.53节OCP HTTP适配，保留第15.54节分组/策略/权重，保留第15.55节全局黑名单/尝试上限及第15.63节分组覆盖，补其他发现边界；组内尝试上限及地址级黑名单已有本地实现（第15.64/15.65节）。现有 TLS 回调、证书副本及池身份无需重做 | 企业 CA/mTLS、证书与凭据过期/刷新失败、撤销、KDC/plugin 成功及拒绝；地址更新/错误身份/节点与代理故障；Windows/Linux 报告 | W1 池身份、W2 重试边界 |
| W5 命令、Oracle 与类型：P1-05、P1-06、P1-07、P1-08 | 保留第15.62节REF CURSOR结果限制；保留第15.61节批次结果限制及共享SingleRow修正；保留第15.60节Batch元数据及源条目创建；保留 KeyInfo 键/隐藏列和复杂查询 unknown 契约及回归，补真实目录验收；保留 SchemaOnly Prepare-only 本地回归并补真实准备行为验收；保留 MySQL StoredProcedure 参数方向/输出/多结果实现及回归并补部署验收（第 15.30 节），保留 Oracle 独立 Prepare/所有权及 DML 元数据回归（第 15.34 节）、输出正FetchSize提示与独立cursor分页契约（第15.46节），保留第15.47节national方向编码，保留第15.56节二进制cursor列/嵌套所有权及第15.87节文本cursor列；继续补输入方向/结果形状、复杂LOB/cursor和其他national边界、扩展类型码/其余字符集；保留第 15.32 节的普通结果映射/严格 decoder/宽度并补完整部署验收；保留可观察 DDL 自动失效回归并补真实验收。有符号 DecimalScale 已本地完成，保留其精度和覆盖规则 | Command/Batch/REF CURSOR 行为表；NULL/方向/精度/溢出/编码/取消与资源释放矩阵；SchemaOnly 副作用/锁/大结果报告；服务端不支持形状单列 | G0；资源生命周期与 W1 联动 |
| W6 目录与 ADO.NET 生态：P1-10、P2-01 | 常用目录/限制参数及 DataTypes 已本地完成；自动参数推导已通过本地回归和独立走查（第 15.18 节）；补实库/权限/版本/代理矩阵；DataAdapter/CommandBuilder 已有本地填充、映射/事件和有界 CRUD，补真实验收，不以 ADO.NET provider 代替 EF Core provider | 双模式目录/权限/大小写/空集合；类型映射与过程重载/参数方向；保留 adapter CRUD、键、原始值/NULL 和乐观并发本地回归；补实际 SQL/锁及独立运行时报告 | W5 键与结果元数据 |
| W7 性能与可观测性：P2-02、P2-03、P2-04 | 保留已有 opt-in idle statement cache，补真实 DDL/reprepare/资源/负载验收；保留第15.72节opt-in MySQL INSERT rewrite及group failure，补multi-send/bulk及真实性能验收；保留建连/探活九阶段直方图及固定标签回归，保留慢命令阈值/脱敏/回调隔离回归；保留第15.57节协议帧诊断；补其他资源等待和负载验收。保留顺序 Batch 和 Oracle array binding | 缓存容量/失效/关闭、批量部分失败/事务/取消/包限额；吞吐、延迟、峰值内存与资源上限对照；诊断脱敏和监听开销 | W1 generation/reset、W5 绑定规则 |
| W8 运行时与发布：P2-05、P2-06 | 明确版本、平台、TFM、Framework/ORM 的承诺范围；保留第15.74节Windows六配置nupkg协议子集；保留第15.59节诊断卸载修复及默认642项回归；第15.58节原始崩溃与条件通过仍保留；将六个 matrix.* 占位变成有环境和断言的案例；完成包名、来源/许可和发布审查 | 每个承诺 profile 的 nupkg consumer、实库/代理/TLS/认证/故障报告；保留 Oracle 失败及所有跳过原因；外部 coordinator crash recovery；可复现命令和清理结果 | 各包可并行积累证据，正式发布须满足第 7.3 节 |

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
