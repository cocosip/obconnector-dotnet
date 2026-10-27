# 开发进度与功能矩阵

更新：2026-10-28。范围审计基线：`89a304d`；最近功能模块提交为`f5d4d76`；当前范围审计移除了 DataAdapter/CommandBuilder；下表同步最新调整。

**已实现**表示支持“已支持范围”一列且有本地回归，不等于所有部署均已验收。**部分实现**表示仍有明确缺口；**未实现**表示当前不支持；**开发中**不计交付。实库结果只覆盖记录的 profile。
原清单的23个审计编号保留用于追溯，P2-01 已移出当前范围；其余工作包不能整体关闭，也不按提交数/单测数计算完成率。

## 连接、池与事务

| 功能 | 开发状态 | 已支持范围 | 还缺什么 / 验收边界 |
| --- | --- | --- | --- |
| 双模式连接与基础SQL | 已实现 | 模式/产品身份确认、文本/准备执行、参数、多结果、同步/异步、取消/超时 | 其他版本、代理、故障组合仍需验收 |
| 连接池基本生命周期 | 已实现 | 容量/等待/取消、预热、探活、空闲/总生命周期、清池、池指标 | 并发交错、长期负载和资源上限未完整验收 |
| 池化会话隔离 | 部分实现 | reset代际、保守状态SQL退休、Oracle自动提交、reader/LOB/cursor/XA保护；Oracle 直连资源生命周期矩阵 32/32 通过 | 任意隐式副作用、完整资源交错及代理reset；直连专项不能推及全部状态 |
| 池路由身份隔离 | 已实现 | resolver身份、每次Open快照、旧闲置端点退休、借出连接保留 | 受控单上游路由验收已通过；真实集群/代理HA未验收 |
| 本地事务 / savepoint | 已实现 | 模式相关事务、现代TFM异步API及保存点 | 保留隔离级别/标识符限制；跨版本故障组合待验收 |
| 单资源TransactionScope | 已实现 | PSPE登记、所有权、取消/晚回调、终态清理 | 分布式提升/durable prepare不支持；真实事务管理器故障矩阵待验收 |
| Unknown outcome处理 | 已实现 | 不确定结果分类、坏会话销毁、新租约及业务核对示例；不自动重放未知写 | 已有16项受控网络验收；完整发送/执行/取消/服务器故障边界仍缺 |
| 活动连接故障恢复 | 部分实现 | 连接级ERR置Broken、旧事务失效、显式Close/Open | 透明迁移/自动重连/replay未实现且须先定契约；完整重建验收仍缺 |
| 显式XA | 已实现 | 生命周期/recover、专用会话隔离；记录决定后协调器进程kill/restart验收 | 通用持久协调器不是现有产品能力；其他崩溃点/服务器/代理恢复未完整验收 |

## Oracle、复杂类型与字符集

| 功能 | 开发状态 | 已支持范围 | 还缺什么 / 验收边界 |
| --- | --- | --- | --- |
| Oracle Prepare和普通输出 | 部分实现 | 独立Prepare、OUT/RETURNING、普通输出正FetchSize提示、数组绑定 | 输出offset/数组正FetchSize仍拒绝；其余形状和部署组合待补 |
| REF CURSOR输出和结果列 | 已实现 | 输出、文本/二进制四字节游标ID、二进制复杂cursor成员及执行所有权；Oracle 直连输出/分页/释放矩阵 15/16 通过 | `SELECT CURSOR(...)` 结果列在当前租户执行阶段返回 `902/HY000`；其余真实方向/分页/资源/代理矩阵仍未完整验收 |
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
| 数值/时间及常见扩展类型 | 已实现 | NUMBER/FLOAT保真、signed scale、TZ/LTZ/INTERVAL、ROWID、Inf/NaN、MySQL BLOB族/ENUM/SET/原始GEOMETRY | 不代表所有类型码均支持；扩展元数据和未知类型仍拒绝 |
| 客户端字符集 | 部分实现 | SQL/普通及JSON参数、复杂national输入、DBCS fallback、CLOB输入预校验 | Oracle UTF8MB3重映射、SJIS/Big5的Open失败仍在；非UTF8歧义表达式需ASCII别名 |
| national存储/输出 | 部分实现 | 已有可信national元数据及受支持结果路径 | 存储complex要求AL16UTF16，其他存储/匿名输出形状待补 |
| JSON / CLOB结果 | 已实现 | 当前严格UTF-8协议契约 | 非默认输入的存储/转换、结果元数据、其他版本/代理仍待验收，不应无依据改为客户端编码解码 |

## 协议、信任与发现

| 功能 | 开发状态 | 已支持范围 | 还缺什么 / 验收边界 |
| --- | --- | --- | --- |
| 标准MySQL压缩 | 已实现 | 显式请求、有界编解码、校验、序列和取消保护；服务端未声明 `CLIENT_COMPRESS` 时按 Java 降级为普通协议，双模式直连 `SELECT 1` 已通过 | 不能与OB20同时启用；当前租户未开启压缩，真实压缩帧、代理、TLS 和跨平台矩阵待验收 |
| 普通OB20 / trace | 已实现 | 普通帧、校验、legacy/modern trace编解码及协商诊断；显式 profile 会拒绝服务端实际降级 | 用户租户实测协商为 MySQL，未提供实际 OB20/trace 帧；OBProxy、服务器策略和 trace 发送仍待专用环境 |
| OB20压缩 | 明确不支持 | 非零uncompressed length仍拒绝 | 先取得客户端链路/版本/capability 依据，否则不实现 |
| Proxy reroute | 明确不支持 | 当前拒绝相关响应标志 | 没有明确目标/TLS/租户及未知结果契约前不实现 |
| 文本多语句 | 已实现 | 默认关闭，显式开启后文本多结果 | 参数/Prepare/cursor/Oracle输出组合仍拒绝；扩展前需明确范围 |
| LOCAL INFILE | 已实现 | 显式授权、应用流输入、安全边界、同步/异步；显式 `localInfileProfile` 已在直连 OceanBase MySQL 4.4.2.2 完成两项上传验收 | 代理策略、大文件及取消组合待验收 |
| TLS/mTLS | 已实现 | 平台校验、自定义回调、客户端证书、池身份隔离、撤销选项 | 企业CA、负向握手、轮换和跨平台撤销未完整验收 |
| 认证/凭据轮换 | 已实现 | 已有认证插件路径；每次Open提供凭据、版本轮换、过期/取消和旧会话退休 | 真实插件、凭据服务及KDC矩阵未验收 |
| 发现与路由策略 | 已实现 | TCP重试预算/退避、健康缓存、TNS/OCP、组/权重/策略、各级黑名单/尝试上限 | TCP可达不代表数据库健康；其他策略边界和真实发现/节点/代理故障待验收 |

## ADO.NET、性能与发布

| 功能 | 开发状态 | 已支持范围 | 还缺什么 / 验收边界 |
| --- | --- | --- | --- |
| Command/Batch行为 | 部分实现 | Prepare-only SchemaOnly、有界KeyInfo、SingleRow/SingleResult、Batch元数据及结果限制；双模式直连已补 16 项 SchemaOnly/KeyInfo 矩阵（含组合主键、别名投影、可空唯一键和实际隐藏值读取），修复 MySQL 目录大小写匹配 | 完整游标/结果形状、目录权限、锁与副作用矩阵待补 |
| MySQL存储过程 | 已实现 | 集合顺序Input/Output/InputOutput、多结果和隐藏输出 | ReturnValue、FetchSize、SchemaOnly/KeyInfo、INOUT stream等仍拒绝 |
| Schema / 参数推导 | 已实现（直连验收部分完成） | 双模式目录、DataTypes、原子参数派生、FLOAT及INOUT归一化；本次双模式 Schema 实库用例 10 项通过，CommandBehavior 元数据补验 16 项通过，过程参数推导再通过 MySQL 2 项和 Oracle 4 项 | Oracle Views/Synonyms 建对象在当前租户返回 `1031/HY000`，不作为客户端 workaround；权限/大小写/重载/版本/代理目录待验收，见[Schema 记录](history/2026-10-28-schema-acceptance.md)和[参数推导记录](history/2026-10-28-routine-derivation-acceptance.md) |
| DataAdapter / CommandBuilder | 已移出支持范围 | Connector/J 没有对应能力，当前 CRUD 使用显式 `OceanBaseCommand` | 不再提供 public 类型、factory 入口或自动 CRUD 生成 |
| Statement cache | 已实现 | opt-in idle LRU、容量/关闭、上下文/DDL/reset代际失效 | 真实reprepare、资源和负载报告待验收 |
| 顺序Batch / INSERT rewrite | 已实现 | 顺序执行、Oracle array binding、opt-in MySQL INSERT合并及失败归属 | 真实触发器/默认值/警告、性能和部分失败组合待验收 |
| Multi-send / 独立bulk | 明确不支持 | 尚无 Java/OceanBase 流水线/独立bulk协议依据 | 不列为默认开发项 |
| 现有诊断 | 已实现 | 日志/Activity/Meter、池等待/租约、建连阶段、慢命令、协议帧/协商、会话I/O/源流读取 | 其他未定义等待点不列为功能缺口；真实负载、资源曲线和监听开销待验收 |
| TFM / 包消费 | 部分验证 | 五资产；Windows有实际运行时及nupkg协议子集证据 | 单一.NET10 host的五资产消费不等于五运行时；非Windows及完整承诺矩阵仍缺 |
| 完整发布验收 | 未完成 | 双模式直连4.4.2.2、Windows/.NET10、TLS disabled的若干专项已有报告 | OBProxy、真实TLS/auth、完整资源/故障/版本/平台矩阵及发布审查未闭合 |

## 当前开发项与门禁

| 项目 | 当前进度 | 尚未完成 |
| --- | --- | --- |
| Consumer CI耗时优化 | **已调整**：普通 PR/分支 push 不执行 consumer；仅 tag 的 Windows 发布腿运行 acceptance；行为矩阵保留 `netstandard2.0`、`netstandard2.1`、`net10.0` 三类实现路径，五资产依赖/目标面契约仍保留 | 本地本轮列举用例由 5918 降至 3396；完整 TLS consumer 仍受 Windows PFX 凭据环境限制，需在 CI 或可用证书环境复跑 |
| OB20/trace显式验收（P2-06） | **已提交，部分实库验收**：代码/文档见`bc020e2`、`c5b4c07`；专项/runner **88/88**，Release零警告/错误；直连4.4.2.2双模式 `declined-ob20` 各6通过 | 用户租户实际协商为 `Layer=mysql`，显式 `ob20` 各2项按契约失败；真实 OB20/trace、OBProxy、TLS/auth 和完整单测环境门禁仍未闭合。见[模块证据](history/2026-10-27-ob20-trace-acceptance.md) |
| 验收占位 | 已提交基线仅保留TLS/auth DeferredReason；OB20/trace已替换为可执行profile | 实际服务端未启用OB20时必须保留失败，不能用普通MySQL fallback关闭OB20门禁 |

最新**已交付**模块是 P1-02 标准 MySQL 压缩协商修正：按 Connector/J 规则，在服务端未声明 `CLIENT_COMPRESS` 时降级普通协议；本地受影响压缩/诊断筛选 126/130 通过（4 个 TLS 用例受 Windows 测试证书环境影响），真实 MySQL/Oracle 直连同步/异步 4/4 通过；证据见[标准压缩记录](history/2026-10-28-standard-compression-fallback.md)。此前**已交付**模块是本次 P0-02/P1-06 Oracle 资源与 REF CURSOR 直连验收：资源生命周期矩阵 32/32 通过，REF CURSOR 输出矩阵 15/16 通过，唯一失败是服务端拒绝 `SELECT CURSOR(...)` 结果列；证据见[资源与游标记录](history/2026-10-28-oracle-resource-cursor-acceptance.md)。更早的 P1-05/P1-10 元数据直连补验、LOCAL INFILE 实库验收仍见对应历史台账。这些是历史证据，不是本次文档整理的新运行。

开发优先级：OB20/trace 草稿已提交，后续只推进有 Java/OceanBase 依据且纳入范围的验收。没有输入协议依据的 REF CURSOR/复杂形状和未定义批量/诊断扩展保持拒绝，不以诊断或文档交付替代核心功能支持。

## 维护规则与证据入口

- 每次交付直接更新对应矩阵行；本文件不再追加逐次测试流水。
- 在“当前开发项”只保留当前未交付模块；完成后移入矩阵并更新提交及证据链接。
- 详细测试、失败/跳过、走查及提交记录写入[历史台账](history/obconnector-dotnet-progress.md)，按需打开。
- 23项缺口与八工作包映射见[完整性清单](obconnector-dotnet-completeness.md)；详细协议要求和关闭标准见[审计参考](history/obconnector-dotnet-completeness.md)。
- 实际支持声明见[兼容性](compatibility.md)；实库仅显式验收，普通单测/CI不依赖用户环境，连接信息不进入文档或产物。
