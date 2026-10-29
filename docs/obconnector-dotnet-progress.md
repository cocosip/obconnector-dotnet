# 开发进度与功能矩阵

更新：2026-10-29。范围审计基线：`89a304d`；DataAdapter/CommandBuilder已移除；本轮模块及证据以下表为准。

**已实现**表示支持“已支持范围”一列且有本地回归，不等于所有部署均已验收。**部分实现**表示仍有明确缺口；**未实现**表示当前不支持；**开发中**不计交付。实库结果只覆盖记录的 profile。
原清单的23个审计编号保留用于追溯，P2-01 已移出当前范围；其余工作包不能整体关闭，也不按提交数/单测数计算完成率。

## 连接、池与事务

| 功能 | 开发状态 | 已支持范围 | 还缺什么 / 验收边界 |
| --- | --- | --- | --- |
| 双模式连接与基础SQL | 已实现 | 模式/产品身份确认、文本/准备执行、参数、多结果、同步/异步、取消/超时 | 其他版本、代理、故障组合仍需验收 |
| 连接池基本生命周期 | 已实现 | 容量/等待/取消、预热、探活、空闲/总生命周期、清池、池指标；池销毁会中止未完成物理 Open、预热以及池持有的探活/reset；DataSource 关闭取消尚在凭据/发现阶段及非池化 Open，保留已建立借用连接，见[源关闭记录](history/2026-10-28-data-source-open-shutdown.md)；双模式直连最大池2、4 worker同步/异步并发 4 项验收 | 并发交错、长期负载和资源上限未完整验收 |
| 池化会话隔离 | 部分实现 | reset代际、保守状态SQL退休、Oracle自动提交、reader/LOB/cursor/XA保护；池销毁时原子关闭 `Closed/Connecting` 借出 session 及预热 session，已建立 `Open/Busy` 借出连接保持可用；池持有的探活/reset 即时关闭，见[维护关闭记录](history/2026-10-28-pool-maintenance-shutdown.md)；双模式 Connections 直连矩阵128通过、4配置跳过；新增Oracle外置BLOB/CLOB读取回包中断8/8，旧资源晚释放不影响新租约完整内容，见[中断记录](history/2026-10-28-external-lob-read-interruption.md) | 任意隐式副作用、其他中断点、完整资源交错及代理reset；直连专项不能推及全部状态 |
| 池路由身份隔离 | 已实现 | resolver身份、每次Open快照、旧闲置端点退休、借出连接保留 | 受控单上游路由验收已通过；真实集群/代理HA未验收 |
| 本地事务 / savepoint | 已实现 | 模式相关事务、现代TFM异步API及保存点 | 保留隔离级别/标识符限制；跨版本故障组合待验收 |
| 单资源TransactionScope | 已实现 | PSPE登记、所有权、取消/晚回调、终态清理；双模式直连16项验证自动入列、提交/回滚可见性及迟到回调与下一借用者事务隔离，见[验收记录](history/2026-10-28-ambient-transaction-acceptance.md)；本地管理器定时超时4/4，关闭前真实回滚及同会话复用隔离见[补验](history/2026-10-28-ambient-manager-timeout.md)；活动命令期间超时另4/4，验证坏会话销毁及新事务隔离，见[活动命令补验](history/2026-10-28-ambient-active-command-timeout.md) | 分布式提升/durable prepare不支持；in-doubt、管理器崩溃及代理故障矩阵待验收 |
| Unknown outcome处理 | 已实现 | 不确定结果分类、坏会话销毁、新租约及业务核对示例；本地帧写入前校验同步/异步2/2不再误报unknown且保留会话；不自动重放未知写；事务回包丢失16项及写入上游请求/回包截断16项直连验收，见[本地边界](history/2026-10-28-unknown-outcome-presend.md)和[写入边界](history/2026-10-28-write-outcome-boundaries.md) | 完整发送/执行/取消/服务器故障边界仍缺 |
| 活动连接故障恢复 | 部分实现 | 连接级ERR置Broken、旧事务失效、显式Close/Open；双模式直连新增取消/超时事务8/8，验证新物理会话、旧事务晚释放及实际提交数据，见[重建记录](history/2026-10-28-interrupted-transaction-recovery.md) | 透明迁移/自动重连/replay未实现且须先定契约；节点/代理及完整重建验收仍缺 |
| 显式XA | 已实现 | 生命周期/recover、专用会话隔离；记录决定后协调器进程kill/restart验收 | 通用持久协调器不是现有产品能力；其他崩溃点/服务器/代理恢复未完整验收 |

## Oracle、复杂类型与字符集

| 功能 | 开发状态 | 已支持范围 | 还缺什么 / 验收边界 |
| --- | --- | --- | --- |
| Oracle Prepare和普通输出 | 部分实现 | 独立Prepare、OUT/RETURNING、普通输出及数组绑定正FetchSize提示（内部结果不分页），见[数组提示](history/2026-10-28-array-fetch-hint.md) | 输出offset仍拒绝；其余形状和部署组合待补 |
| REF CURSOR输出和结果列 | 部分实现 | 输出、四字节游标ID解析、二进制复杂cursor成员及执行所有权；Oracle 直连输出/分页/释放矩阵 15/16 通过 | 当前租户准备执行 `SELECT CURSOR(...)` 返回 `902/HY000`，官方Java同样失败；文本返回CURSOR元数据但实际值不符合四字节解析，不能承诺该形状可用，见[归因](history/2026-10-28-direct-failure-triage.md)；其余部署/资源矩阵仍待验收 |
| REF CURSOR Input/InputOutput | 明确不支持 | Java 没有对应输入绑定路径，当前 OracleCommandPlan 在发送前拒绝 | 除非取得明确的 Java/OceanBase 输入协议依据，否则不实现 |
| 复杂类型二进制结果 | 已实现 | 嵌套object/collection、命名/匿名布局、BLOB/CLOB/cursor成员；直连 OceanBase 4.4.2.2 的 object 表列形状已通过 | collection nested-table 表列在该服务端返回600/0A000；其他真实SQL形状、版本、资源生命周期仍待验收 |
| 命名复杂LOB内容输入 | 已实现 | BLOB字节、严格UTF-8 CLOB文本、嵌套命名元素 | 接受内容不等于接受定位器；其他字符集/版本/代理待验收 |
| 匿名标量LOB数组输入 | 已实现 | 同连接/会话/事务/代际的LOB定位器，执行时重验所有权 | 默认/GB18030已有直连专项；其他profile待验收 |
| CLOB定位器输入转码 | 已实现 | 发送副本转码；V1支持，V2限已认证产品4.4.1+；原句柄不变 | 旧版/未知V2保持原字节；默认/GB18030含外置大CLOB通过，其余部署待验收 |
| 命名复杂LOB定位器回绑 | 明确不支持 | 当前拒绝OceanBaseLob，使用内容输入替代 | 没有明确定位器协议前不扩展 |
| 复杂cursor输入 | 明确不支持 | 非空type163成员无参数编码，匿名工厂也不支持 | 没有明确输入协议前不扩展 |
| 文本复杂类型结果 | 明确不支持 | 当前 ResultColumn 要求二进制协议 | 没有明确文本语法、编码、身份和资源语义前不扩展 |
| 复杂整列RETURNING | 受限 | 已解析的类型身份及既有输出路径 | 未解析身份继续拒绝；不把它列为默认开发项 |
| 类型描述缓存 | 已实现 | 显式清理、可观察DDL自动失效、嵌套与大小限制 | 外部/不透明DDL须显式清理；递归定义不支持，真实DDL矩阵待验收 |
| 数值/时间及常见扩展类型 | 已实现 | NUMBER/FLOAT保真、signed scale、TZ/LTZ/INTERVAL、ROWID、Inf/NaN、MySQL BLOB族/ENUM/SET/原始GEOMETRY | 不代表所有类型码均支持；扩展元数据和未知类型仍拒绝；当前租户二进制TZ非空名称返回1882，强制Java服务端prepare同样失败，不能用文本数字名称通过替代，见[对照](history/2026-10-29-multi-statement-zoned-time-input.md) |
| 客户端字符集 | 部分实现 | SQL/普通及JSON参数、复杂national输入、MySQL SJIS/Big5 Open、DBCS fallback、CLOB输入预校验；修复utf8mb3对服务端utf8mb4归一名的过严校验，原6项实库通过，输入限制与池身份保留 | Oracle SJIS/Big5初始化失败已由官方Java独立复现；非UTF8歧义表达式需ASCII别名，见[归因](history/2026-10-28-direct-failure-triage.md) |
| national存储/输出 | 部分实现 | 可信显式national/results编码复用十二种严格映射，覆盖标量二进制输入及普通/PL结果；多字节流式解码保留状态。Oracle标量直连48/48；十四种设置的命名/嵌套PL OUT/INOUT、对象存储、匿名数组DML及拒绝边界实库392/392，见[复杂编码验收](history/2026-10-29-complex-national-encoding-acceptance.md)；本地字节及消费者证据见[编码模块](history/2026-10-29-national-character-encodings.md) | 缺少新可信状态仍拒绝；collation fallback仅Unicode，存储complex要求AL16UTF16；嵌套表列、其他存储/匿名输出及部署形状仍待验收 |
| JSON / CLOB结果 | 已实现 | 当前严格UTF-8协议契约 | 非默认输入的存储/转换、结果元数据、其他版本/代理仍待验收，不应无依据改为客户端编码解码 |

## 协议、信任与发现

| 功能 | 开发状态 | 已支持范围 | 还缺什么 / 验收边界 |
| --- | --- | --- | --- |
| 标准MySQL压缩 | 已实现 | 显式请求、有界编解码、校验、序列和取消保护；服务端未声明 `CLIENT_COMPRESS` 时按 Java 降级为普通协议，双模式直连 `SELECT 1` 已通过 | 不能与OB20同时启用；当前租户未开启压缩，真实压缩帧、代理、TLS 和跨平台矩阵待验收 |
| 普通OB20 / trace | 已实现 | 普通帧、校验、legacy/modern trace编解码及协商诊断；显式 profile 会拒绝服务端实际降级 | 用户租户实测协商为 MySQL，未提供实际 OB20/trace 帧；OBProxy、服务器策略和 trace 发送仍待专用环境 |
| OB20压缩 | 明确不支持 | 非零uncompressed length仍拒绝 | 先取得客户端链路/版本/capability 依据，否则不实现 |
| Proxy reroute | 明确不支持 | 当前拒绝相关响应标志 | 没有明确目标/TLS/租户及未知结果契约前不实现 |
| 文本多语句 | 部分实现 | 默认关闭，显式开启后文本多结果；Oracle命名/MySQL位置普通标量输入（数字/字符串/二进制/NULL）及MySQL JSON、Oracle NChar/NVarchar2输入，以及 MySQL DATE/DATETIME/TIMESTAMP/TIME、Oracle DATE/TIMESTAMP/TIMESTAMP WITH TIME ZONE/TIMESTAMP WITH LOCAL TIME ZONE/INTERVAL YEAR TO MONTH/INTERVAL DAY TO SECOND；MySQL字符串支持NUL和SQL_MODE切换，保持literal collation/coercibility；Oracle大byte[]保留HEXTORAW/RAW身份并取消通用2000字节客户端限制，目标RAW列限制保留，见[大二进制记录](history/2026-10-29-oracle-large-binary-literals.md)；二进制Stream输入在发送前有界编码，双模式独立存储/源失败专项46/46，见[流输入](history/2026-10-29-multi-statement-stream-input.md)；此前双模式多语句直连组202/202（新增national输入38项，含存储/NCHAR填充/表达式元数据/NULL/长值/失败边界；结果编码范围不扩展），见[national输入](history/2026-10-29-multi-statement-national-input.md)；此前JSON存储/字符集/SQL_MODE及时间精度/时区切换，见[JSON输入](history/2026-10-29-multi-statement-json-input.md)及时间参数同步/异步本地回归通过，见[MySQL参数](history/2026-10-28-mysql-multi-statement-parameters.md)、[Oracle参数](history/2026-10-28-oracle-multi-statement-parameters.md)、[时间参数](history/2026-10-28-multi-statement-temporal-input.md)、[区间输入](history/2026-10-29-multi-statement-interval-input.md)、[时区输入](history/2026-10-29-multi-statement-zoned-time-input.md)和[门禁修复](history/2026-10-28-multi-statement-client-policy.md) | national文本输入使用ASCII Unicode字面量，后续national结果仍要求新可信编码状态；LTZ绑定保留当前会话时区匹配检查并固定来源时区，TZ名称优先于偏移、缩写为结果元数据；命名地区时区仍受服务端时区表限制；MySQL字符串要求稳定解析字符集，显式SET字符集/动态执行/CALL/quoted SET标识符组合拒绝，任意函数/触发器隐式改变字符集不承诺；Prepare/cursor/输出及复杂输入仍拒绝，其余形状继续分类 |
| LOCAL INFILE | 已实现 | 显式授权、应用流输入、安全边界、同步/异步；大于1MiB分块及源失败恢复4/4直连验收见[证据](history/2026-10-28-local-infile-chunked-acceptance.md)；异步源等待期间真实取消/超时2/2，见[中断记录](history/2026-10-28-local-infile-interruption.md)；CanRead为false或抛错时也释放工厂源流，新增直连4/4，见[修复记录](history/2026-10-28-local-infile-validation-ownership.md)；超出64MiB逻辑包上限的本地协议同步/异步回归2/2，见[上限记录](history/2026-10-28-local-infile-overlimit.md) | 代理策略、同步源阻塞及其他中断/协议组合待验收 |
| TLS/mTLS | 已实现 | 平台校验、自定义回调、客户端证书、池身份隔离、撤销选项 | 企业CA、负向握手、轮换和跨平台撤销未完整验收 |
| 认证/凭据轮换 | 已实现 | 已有认证插件路径；每次Open提供凭据、版本轮换、过期/取消和旧会话退休 | 真实插件、凭据服务及KDC矩阵未验收 |
| 发现与路由策略 | 已实现 | TCP重试预算/退避、健康缓存、TNS/OCP、组/权重/策略、各级黑名单/尝试上限 | TCP可达不代表数据库健康；其他策略边界和真实发现/节点/代理故障待验收 |

## ADO.NET、性能与发布

| 功能 | 开发状态 | 已支持范围 | 还缺什么 / 验收边界 |
| --- | --- | --- | --- |
| Command/Batch行为 | 部分实现 | Prepare-only SchemaOnly、有界KeyInfo、SingleRow/SingleResult、Batch元数据及结果限制；双模式直连已补 16 项 SchemaOnly/KeyInfo 矩阵（含组合主键、别名投影、可空唯一键和实际隐藏值读取），新增双模式 Batch SingleRow/SingleResult 同步异步 4 项直连验收，修复 MySQL 目录大小写匹配 | 完整游标/结果形状、目录权限、锁与副作用矩阵待补 |
| MySQL过程/函数 | 已实现 | 过程集合顺序Input/Output/InputOutput；函数单ReturnValue+Input、隐藏输出和多结果；过程/函数正FetchSize提示不分页，见[提示记录](history/2026-10-28-mysql-routine-fetch-hint.md)；MySQL过程/函数直连组30/30（含正提示24项）通过 | 函数不接受Output/InputOutput混用；SchemaOnly/KeyInfo、INOUT stream仍拒绝；真实部署矩阵仍缺 |
| Schema / 参数推导 | 已实现（直连验收部分完成） | 双模式目录、DataTypes、原子参数派生、FLOAT及INOUT归一化；本次双模式 Schema 实库用例 10 项通过，CommandBehavior 元数据补验 16 项通过，过程参数推导再通过 MySQL 2 项和 Oracle 4 项 | Oracle Views/Synonyms 建对象在当前租户返回 `1031/HY000`，不作为客户端 workaround；权限/大小写/重载/版本/代理目录待验收，见[Schema 记录](history/2026-10-28-schema-acceptance.md)和[参数推导记录](history/2026-10-28-routine-derivation-acceptance.md) |
| DataAdapter / CommandBuilder | 已移出支持范围 | Connector/J 没有对应能力，当前 CRUD 使用显式 `OceanBaseCommand` | 不再提供 public 类型、factory 入口或自动 CRUD 生成 |
| Statement cache | 已实现 | opt-in idle LRU、容量/关闭、上下文/DDL/reset代际失效；双模式直连4/4验证实际缓存命中、held/idle语句DDL刷新及池reset重备，见[证据](history/2026-10-28-statement-cache-live-lifecycle.md)；外部删列确认错误后条目淘汰及重建命令读取新结构4/4，见[补验](history/2026-10-28-statement-cache-external-ddl.md) | 其他外部DDL、特定reprepare错误、资源和负载报告待验收 |
| 顺序Batch / INSERT rewrite | 已实现 | 顺序执行、Oracle array binding、opt-in MySQL INSERT合并及失败归属；MySQL直连6/6验证默认值/触发器、提交/回滚及重复组的prefix/suffix边界，见[证据](history/2026-10-28-batch-rewrite-live-semantics.md) | 更多触发器/默认表达式/警告、性能和部分失败组合待验收 |
| Multi-send / 独立bulk | 明确不支持 | 尚无 Java/OceanBase 流水线/独立bulk协议依据 | 不列为默认开发项 |
| 现有诊断 | 已实现 | 日志/Activity/Meter、池等待/租约、建连阶段、慢命令、协议帧/协商、会话I/O/源流读取 | 其他未定义等待点不列为功能缺口；真实负载、资源曲线和监听开销待验收 |
| TFM / 包消费 | 已实现（Windows验收） | 五资产nupkg内容、依赖组、五目标资产选择及包消费者协议样例均通过；net8.0、net9.0、net10.0分别在对应运行时执行，netstandard2.0/2.1由net10 host加载，并由面向`net48`的host在已安装.NET Framework 4.8.1上加载netstandard2.0通过，见[包消费记录](history/2026-10-28-package-consumption.md) | 单一Windows主机不等于非Windows或完整生态承诺矩阵；其他平台仍缺 |
| 完整发布验收 | 未完成 | 双模式直连4.4.2.2、Windows/.NET10、TLS disabled的若干专项已有报告 | OBProxy、真实TLS/auth、完整资源/故障/版本/平台矩阵及发布审查未闭合 |

## 当前开发项与门禁

| 项目 | 当前进度 | 尚未完成 |
| --- | --- | --- |
| Consumer CI耗时优化 | **已调整**：普通 PR/分支 push 不执行 consumer；仅 tag 的 Windows 发布腿运行 acceptance；行为矩阵保留 `netstandard2.0`、`netstandard2.1`、`net10.0` 三类实现路径，五资产依赖/目标面契约仍保留 | 安全拆分时默认消费者完整2,860/2,860通过，证书/TLS已独立到不参与CI的SecurityTests；本次数组提示消费者专项另见对应记录。远程CI耗时尚未验证，完整安全套件仍待显式环境执行 |
| OB20/trace显式验收（P2-06） | **已提交，部分实库验收**：代码/文档见`bc020e2`、`c5b4c07`；专项/runner **88/88**，Release零警告/错误；直连4.4.2.2双模式 `declined-ob20` 各6通过 | 用户租户实际协商为 `Layer=mysql`，显式 `ob20` 各2项按契约失败；真实 OB20/trace、OBProxy、TLS/auth 和完整单测环境门禁仍未闭合。见[模块证据](history/2026-10-27-ob20-trace-acceptance.md) |
| 验收占位 | 已提交基线仅保留TLS/auth DeferredReason；OB20/trace已替换为可执行profile | 实际服务端未启用OB20时必须保留失败，不能用普通MySQL fallback关闭OB20门禁 |

最新交付模块为 Oracle 大byte[]客户端字面量：保留HEXTORAW/RAW身份，删除共享2000字节客户端限制，覆盖多语句及受1295/HY000门禁的普通fallback；相关单测215/215、扩大Consumer回归1171/1171、新增Oracle直连30/30及双模式多语句/流回归283/283、DI14/14，五目标Release零警告/错误，完整默认单测8399/8399；独立最终走查无遗留。目标RAW列限制保留，真正实库1295 fallback未验证，见[模块证据](history/2026-10-29-oracle-large-binary-literals.md)。此前交付模块为多语句二进制Stream输入：相关单测547/547、公开DLL多语句消费者96/96、新增双模式独立存储及源失败46/46、既有多语句/流实库回归253/253和DI14/14通过，五目标Release零警告/错误；完整默认单测8384/8384、独立最终走查无遗留，见[模块证据](history/2026-10-29-multi-statement-stream-input.md)。此前 national 复杂编码十四种设置的命名/嵌套OUT/INOUT、对象存储、匿名数组DML及拒绝边界392/392，连同标量最终440/440，见[验收证据](history/2026-10-29-complex-national-encoding-acceptance.md)。

此前功能模块为显式 national 标量编码/结果：十二种严格映射及分块解码修正，完整单测8344/8344、相关单测544/544、公开DLL消费者336/336、Oracle新标量直连48/48、双模式既有回归230/230及DI14/14通过，五目标Release零警告/错误，独立最终走查无遗留，见[编码证据](history/2026-10-29-national-character-encodings.md)。此前模块为 Oracle 多语句 national 输入：NChar/NVarchar2 使用 Unicode 字面量，专项及实库回归已通过，完整单测8128/8128、独立最终走查无遗留，见[模块证据](history/2026-10-29-multi-statement-national-input.md)。此前模块补齐 P0-01/P0-02 探活/reset 期间关闭，见[证据](history/2026-10-28-pool-maintenance-shutdown.md)。此前**已交付**模块是 P0-01/P0-02 池关闭交错：池销毁时已借出或仍在 `Min Pool Size` 预热中的 session 会被取消并释放，已建立 `Open/Busy` 连接保持可用；回归见[池关闭记录](history/2026-10-28-pool-opening-shutdown.md)。此前**已交付**模块是 P0-01 连接池受控并发直连验收：双模式最大池 2、4 worker 的同步/异步 4 项通过，确认容量等待、借用归还和连接复用，证据见[兼容性记录](compatibility.md#2026-10-28-p0-01-pool-concurrency-acceptance)。此前**已交付**模块是 P1-05 Command/Batch 结果限制直连验收：双模式 Batch 的 SingleRow/SingleResult 同步异步 4 项通过，确认隐藏批次项排空且连接可复用，证据见[兼容性记录](compatibility.md#2026-10-28-commandbatch-limited-results-acceptance)。此前**已交付**模块是 P1-08 客户端字符集修正：MySQL SJIS/Big5 Open 按 Connector/J 发送统一 `SET NAMES`，直连同步/异步 4/4 通过；Oracle 同类 Open 由当前租户返回 `900/42000`，保持失败边界；本地字符集/国民字符回归 227/227 通过，证据见[兼容性记录](compatibility.md#2026-10-28-dbcs-client-charset-open-behavior)。此前**已交付**模块是 P1-02 标准 MySQL 压缩协商修正：服务端未声明 `CLIENT_COMPRESS` 时按 Java 降级普通协议，真实 MySQL/Oracle 直连同步/异步 4/4 通过，证据见[标准压缩记录](history/2026-10-28-standard-compression-fallback.md)。更早的 P0-02/P1-06 Oracle 资源与 REF CURSOR、P1-05/P1-10 元数据及 LOCAL INFILE 实库验收仍见对应历史台账。这些是历史证据，不是本次文档整理的新运行。

开发优先级：OB20/trace 草稿已提交，后续只推进有 Java/OceanBase 依据且纳入范围的验收。没有输入协议依据的 REF CURSOR/复杂形状和未定义批量/诊断扩展保持拒绝，不以诊断或文档交付替代核心功能支持。

## 维护规则与证据入口

- 每次交付直接更新对应矩阵行；本文件不再追加逐次测试流水。
- 在“当前开发项”只保留当前未交付模块；完成后移入矩阵并更新提交及证据链接。
- 详细测试、失败/跳过、走查及提交记录写入[历史台账](history/obconnector-dotnet-progress.md)，按需打开。
- 23项缺口与八工作包映射见[完整性清单](obconnector-dotnet-completeness.md)；详细协议要求和关闭标准见[审计参考](history/obconnector-dotnet-completeness.md)。
- 实际支持声明见[兼容性](compatibility.md)；实库仅显式验收，普通单测/CI不依赖用户环境，连接信息不进入文档或产物。

## 测试执行边界（2026-10-28）

真实数据库只由显式启用的 IntegrationTests/验收控制台访问。生成/导入证书、TLS/mTLS握手、TLS认证及TLS消费者用例移至独立 [SecurityTests](../tests/OceanBaseConnector.SecurityTests/README.md)，该项目不在主解决方案或普通/tag CI中。默认UnitTests与ConsumerTests保留纯配置、协议及TCP覆盖；混合矩阵按编译入口分别使用TCP/TLS行，不以跳过安全用例冒充验收通过。证据见[拆分记录](history/2026-10-28-security-test-isolation.md)。真实服务端TLS支持仍未验收。
