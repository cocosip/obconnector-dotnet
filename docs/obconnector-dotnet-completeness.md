# 驱动完整性清单与剩余开发

更新：2026-10-29。范围审计基线为`89a304d`；P2-01 DataAdapter/CommandBuilder已移除；本轮模块及验证见下表，完整直连失败保留在[复核记录](history/2026-10-28-direct-profile-matrix.md)。
日常查看[具体功能矩阵](obconnector-dotnet-progress.md)；本文件保留原23项审计编号（其中P2-01已移除）和剩余工作包，不再重复交付流水。

MySQL日期/时间普通回退已完成，按用户要求暂停。整体仍未全部完成；已交付、受限、排除及环境门禁集中见[暂停检查点](obconnector-dotnet-pause-checkpoint.md)。

## 23项缺口矩阵

“已实现范围”是已有代码和本地证据；“仍缺开发”与“仍欠验收”分开。P2-01 已从当前产品范围移除，其余大项仍不能据本表宣称整体关闭。

| ID | 模块 | 已实现范围 | 仍缺开发 / 明确不支持 | 仍欠验收 |
| --- | --- | --- | --- | --- |
| P0-01 | 连接池 | 容量/等待/预热/探活/回收/清池/指标、重入和通知取消修复；池销毁中止未完成物理 Open/预热及池持有的探活/reset session；DataSource 关闭覆盖凭据/发现及非池化 Open，已建立连接保持可用，见[源关闭证据](history/2026-10-28-data-source-open-shutdown.md) | 其他并发/Dispose交错、长期资源与shutdown范围 | 持续资源上限、完整部署矩阵 |
| P0-02 | 会话隔离 | reset代际、状态SQL退休、Oracle自动提交、reader/LOB/cursor/XA隔离；池销毁原子关闭 `Closed/Connecting` 借出和预热 session，保留 `Open/Busy` 借出连接并关闭池持有的探活/reset；双模式直连池隔离专项94通过/1跳过，Oracle直连资源生命周期矩阵32/32通过；外置BLOB/CLOB真实读取回包中断8/8及新租约资源隔离，见[证据](history/2026-10-28-external-lob-read-interruption.md) | 任意隐式副作用、完整资源/中断/交错覆盖 | 代理reset、其他读取中断点、完整借用者隔离 |
| P0-03 | Ambient | 单资源PSPE、取消/晚回调/终态保护；双模式直连16/16验证自动入列、数据库可见性及下一借用者事务隔离，见[证据](history/2026-10-28-ambient-transaction-acceptance.md)；本地管理器定时超时另4/4，关闭前实际回滚及后续同会话新事务隔离见[补验](history/2026-10-28-ambient-manager-timeout.md)；活动命令超时/会话销毁/新事务隔离另4/4，见[补验](history/2026-10-28-ambient-active-command-timeout.md) | 分布式提升和durable prepare不支持；完整失败覆盖待补 | in-doubt、管理器崩溃和代理故障 |
| P0-04 | Unknown outcome | 分类、销毁/新租约、本地帧写入前校验（同步/异步2/2）、事务回包丢失16项与写入上游请求/回包截断16项、应用核对示例，见[本地边界](history/2026-10-28-unknown-outcome-presend.md)和[写入边界](history/2026-10-28-write-outcome-boundaries.md) | 完整发送/执行/取消故障边界；不重放未知写 | 服务器/代理/协调器全故障矩阵 |
| P0-05 | 活动连接恢复 | 连接ERR置Broken、旧事务失效、显式Close/Open；取消/超时事务双模式直连8/8验证新租约及旧事务晚释放隔离，见[证据](history/2026-10-28-interrupted-transaction-recovery.md) | 无透明迁移/自动重连/replay，先定契约；补完整重建 | 节点/代理断连和恢复 |
| P0-06 | 池路由隔离 | resolver身份、Open快照、闲置退休和借出保留 | 其他边界/压力覆盖；无活动迁移/DNS-IP身份保证 | 单上游受控路由已通过，独立节点/代理HA仍缺 |
| P1-01 | 多语句 | opt-in文本多结果；Oracle命名/MySQL位置普通标量及MySQL JSON/BIT/YEAR、Oracle NChar/NVarchar2输入（BIT/YEAR为数值表达式，目标列负责转换；新增实库28/28，最终门禁见[模块记录](history/2026-10-29-multi-statement-bit-year-input.md)），MySQL字符串支持NUL、SQL_MODE切换和literal collation/coercibility；MySQL DATE/DATETIME/TIMESTAMP/TIME 与 Oracle DATE/TIMESTAMP/TIMESTAMP WITH TIME ZONE/TIMESTAMP WITH LOCAL TIME ZONE/INTERVAL YEAR TO MONTH/INTERVAL DAY TO SECOND 客户端字面量；Oracle普通字符串保留VARCHAR2并支持NUL/长值，受展开SQL预算及服务端目标限制；NUL多语句12003字符通过，两个SELECT脚本在.NET的196608字符用例及官方Java的98301/196608字符用例中超时，仍属未支持验收形状，发送后可能失败，见[文本字面量](history/2026-10-29-oracle-text-literals.md)；Oracle大byte[]保留HEXTORAW/RAW身份并取消通用2000字节客户端限制，目标RAW列限制保留，见[大二进制记录](history/2026-10-29-oracle-large-binary-literals.md)；二进制Stream输入在发送前有界编码，双模式独立存储/源失败专项46/46，见[流输入](history/2026-10-29-multi-statement-stream-input.md)；此前双模式多语句直连组202/202（新增national输入38项，含存储/NCHAR填充/表达式元数据/NULL/长值/失败边界；结果编码范围不扩展），见[national输入](history/2026-10-29-multi-statement-national-input.md)；此前JSON存储/字符集/SQL_MODE及时间精度/时区切换，见[JSON输入](history/2026-10-29-multi-statement-json-input.md)及时间参数本地回归通过，见[MySQL](history/2026-10-28-mysql-multi-statement-parameters.md)、[Oracle](history/2026-10-28-oracle-multi-statement-parameters.md)及[时间参数](history/2026-10-28-multi-statement-temporal-input.md)、[区间输入](history/2026-10-29-multi-statement-interval-input.md)、[时区输入](history/2026-10-29-multi-statement-zoned-time-input.md) | national结果使用独立编码规则，后续结果仍要求新可信状态；MySQL字符串绑定要求解析字符集稳定，显式SET字符集/动态执行/CALL/quoted SET标识符组合拒绝；隐式函数/触发器副作用不承诺；Prepare/cursor/输出及复杂输入仍拒绝，其余形状继续分类 | 更多结果形状、版本/代理 |
| P1-02 | 压缩/OB20 | 标准MySQL压缩、普通OB20及trace；服务端未声明 `CLIENT_COMPRESS` 时按 Java 降级为普通协议，双模式直连验收通过 | 压缩OB20、proxy reroute 当前明确拒绝；除非取得客户端协议依据，不列为默认开发项 | 真实压缩帧/OBProxy、TLS、跨平台及损坏/取消矩阵 |
| P1-03 | LOCAL INFILE | opt-in应用流上传、安全边界；大于1MiB分块/源失败恢复4/4直连见[证据](history/2026-10-28-local-infile-chunked-acceptance.md)，异步源等待期间取消/超时2/2见[中断记录](history/2026-10-28-local-infile-interruption.md)；修复CanRead校验失败前的工厂源流泄漏，单测4项RED→GREEN、直连4/4，见[释放记录](history/2026-10-28-local-infile-validation-ownership.md)；64MiB逻辑包上限及未知结果/坏会话同步/异步2/2见[上限记录](history/2026-10-28-local-infile-overlimit.md) | 同步源阻塞及其他协议/中断组合仍待验收 | 代理策略及完整中断行为 |
| P1-04 | TLS/mTLS | 服务器回调、客户端证书、池隔离、撤销选项 | 发现实际差异后补轮换/平台回归 | 企业CA、负向握手、过期/轮换、平台撤销 |
| P1-05 | Command/Batch | SchemaOnly/KeyInfo/结果限制、MySQL过程/函数（正FetchSize提示不分页）、Batch元数据；MySQL二进制INOUT流已接入prepared SET分块上传，专项/公开DLL/实库证据见[记录](history/2026-10-29-mysql-inout-stream.md)；双模式直连补验 16 项 SchemaOnly/KeyInfo 矩阵（含组合主键、别名投影、可空唯一键和实际隐藏值读取），并修复 MySQL 目录大小写匹配 | 完整行为/结果形状矩阵，函数仅支持单 ReturnValue+Input，现有拒绝组合保留 | 权限/目录/prepare副作用/锁/游标 |
| P1-06 | Oracle输出/游标 | 二进制INOUT流已接入prepared分片上传，原子发布遵循服务端元数据，见[记录](history/2026-10-29-oracle-inout-stream.md)；Prepare、OUT/RETURNING、输出提示、四字节cursor解析；Oracle 直连输出/分页/释放矩阵 15/16 通过 | Input/InputOutput REF CURSOR 没有 Java 输入绑定依据，当前发送前拒绝；数组正FetchSize提示已接受，内部结果不分页，见[记录](history/2026-10-28-array-fetch-hint.md)；输出offset仍拒绝；准备执行 `SELECT CURSOR(...)` 返回902且Java同样失败；文本元数据可返回，但实际游标值与现有四字节解析不兼容，见[归因](history/2026-10-28-direct-failure-triage.md) | 真实方向/分页/资源/版本/代理；不得承诺该结果列形状可用 |
| P1-07 | 复杂类型 | 嵌套二进制结果、LOB/cursor结果、命名LOB内容/匿名locator输入、DDL失效；十四种national设置的PL输出/对象存储/数组DML及拒绝边界392/392，见[编码验收](history/2026-10-29-complex-national-encoding-acceptance.md) | 命名LOB定位器回绑、复杂cursor输入、文本复杂结果及未解析RETURNING身份没有足够协议依据，当前拒绝；其他形状按服务端矩阵决定 | DDL、真实复杂SQL、资源生命周期和更多部署profile |
| P1-08 | 类型/字符集 | MySQL普通日期/时间输入回退已补齐，完整默认单测8548/8548、公开DLL96/96、直连92/92（新增40项），独立最终走查无遗留；精度/NULL/会话时区与TIME边界见[时间回退](history/2026-10-29-mysql-temporal-fallback.md)； NUMBER/FLOAT、scale、常见扩展类型、客户端编码、MySQL DBCS fallback/Open、CLOB转码；MySQL BIT/YEAR普通客户端回退复用精确数值，见[专项与实库中继证据](history/2026-10-29-mysql-bit-year-fallback.md)；MySQL JSON普通客户端回退已实现，沿用严格字符串编码及每次拒绝后的会话状态查询，相关单测78/78、公开DLL84/84、扩大直连142/142及完整默认单测8529/8529通过，一次独立最终走查无遗留，见[JSON记录](history/2026-10-29-mysql-json-fallback.md)；utf8mb3接受服务端utf8mb4归一名，保留mb3输入限制，原6项实库通过；显式national/results编码复用十二种严格映射，标量直连48/48、复杂national矩阵392/392，见[标量编码](history/2026-10-29-national-character-encodings.md)与[复杂验收](history/2026-10-29-complex-national-encoding-acceptance.md) | 其余national存储/输出、扩展类型/metadata；当前租户二进制TZ非空名称返回1882且强制Java服务端prepare同样失败，见[时区对照](history/2026-10-29-multi-statement-zoned-time-input.md)；Oracle SJIS/Big5初始化失败由官方Java独立复现，见[归因](history/2026-10-28-direct-failure-triage.md) | JSON/CLOB转换/存储、完整字符集/版本/代理 |
| P1-09 | HA/发现 | TCP重试/健康缓存、TNS/OCP、组/权重/黑名单/上限 | 其他发现/策略边界；TCP健康不等于数据库健康 | 实际发现服务/节点/代理故障 |
| P1-10 | Schema | 双模式目录、DataTypes、原子参数派生、FLOAT/INOUT；本次直连 Schema 用例 10 项通过，CommandBehavior 元数据补验 16 项通过，过程参数推导再通过 MySQL 2 项和 Oracle 4 项 | 包内PL/SQL参数类型拒绝；当前 Oracle 租户 `CREATE VIEW`/`CREATE SYNONYM` 返回 `1031/HY000`，不增加客户端 workaround | 权限、大小写、重载、版本、代理及其他目录矩阵 |
| P1-11 | 凭据轮换 | 每次Open取值、过期/取消、版本退休、脱敏 | 实际provider/plugin差异回归待补 | 凭据服务、plugin/KDC、拒绝/刷新/轮换 |
| P2-02 | Statement cache | opt-in idle LRU、上下文/DDL/reset代际失效；双模式直连4/4实际缓存命中、held/idle DDL刷新和reset重备，见[证据](history/2026-10-28-statement-cache-live-lifecycle.md)；外部删列确认错误后失败条目淘汰和新结构读取4/4，见[补验](history/2026-10-28-statement-cache-external-ddl.md) | 其他外部DDL/特定reprepare错误/资源/负载回归 | 资源/延迟和代理 |
| P2-03 | 批处理优化 | 顺序Batch、Oracle array binding、MySQL INSERT rewrite；直连6/6验证默认值/触发器/事务及已确认prefix、失败group、未执行suffix，见[证据](history/2026-10-28-batch-rewrite-live-semantics.md) | multi-send/pipelining、独立bulk 没有当前 Java/OceanBase 契约，明确不列为待实现 | 更多部分失败/触发器/警告/限额与吞吐 |
| P2-04 | 诊断 | 日志/Activity/Meter、池/阶段/慢命令/协议/I/O/源流 | 其他资源等待不是已定义产品契约，不列为待实现 | 真实负载、资源曲线和监听开销 |
| P2-05 | TFM/生态 | 五资产、Windows实际运行时nupkg协议子集；新增显式双模式实库公开包消费48/48，含独立存储/事务可见性/结果/错误/池化重开和清理；面向net48的host在已安装Framework4.8.1上通过，见[实库记录](history/2026-10-29-package-live-consumption.md)及[原协议记录](history/2026-10-28-package-consumption.md) | 完整平台/API矩阵、ORM支持范围 | 非Windows、完整功能/实库消费者矩阵和其他部署 |
| P2-06 | 真实兼容性 | 验收工具、双模式直连和网络/XA/路由专项 | TLS/auth占位；OB20/trace已提交但真实环境仍受限；发布审查 | OBProxy、TLS/auth、允许零日期SQL_MODE、完整资源/故障/版本/平台 |

## 本轮源码复核边界（2026-10-29）

结论仍为**没有全部关闭**。本轮已核对当前源码中的拒绝入口，不能仅根据历史表格把限制当成已完成：

| 项目 | 当前源码事实 | 处置 |
| --- | --- | --- |
| 文本多语句组合 | [OceanBaseCommand](../src/OceanBaseConnector/OceanBaseCommand.cs) 拒绝 Prepare/cursor/输出组合；[ClientPreparedSql](../src/OceanBaseConnector/Internal/Sql/ClientPreparedSql.cs) 仍限定输入形状 | 已支持子集继续验收；没有依据的组合不扩展 |
| Oracle 输出 offset | [OceanBaseCommand](../src/OceanBaseConnector/OceanBaseCommand.cs) 仍显式拒绝 output + offset | 正 FetchSize 提示通过不等于输出分页已完成 |
| 复杂类型 | [ResultColumn](../src/OceanBaseConnector/Internal/Protocol/ResultColumn.cs) 拒绝文本复杂结果；[ComplexValueCodec](../src/OceanBaseConnector/Internal/Values/ComplexValueCodec.cs) 拒绝命名 locator 回绑；[RoutineParameterDerivation](../src/OceanBaseConnector/Internal/Sql/RoutineParameterDerivation.cs) 拒绝包内参数类型 | 保持受支持形状及明确拒绝，先取得实际协议/服务端依据 |
| OB20 压缩/reroute | [Ob20Packet](../src/OceanBaseConnector/Internal/Protocol/Ob20Packet.cs) 拒绝压缩帧；[Ob20Stream](../src/OceanBaseConnector/Internal/Protocol/Ob20Stream.cs) 拒绝 reroute 标志 | 普通 OB20 合成测试不能关闭这些组合或真实 OBProxy 门禁 |
| TLS/auth 验收 | [DatabaseCases](../tests/OceanBaseConnector.Acceptance/DatabaseCases.cs) 仍保存 `matrix.tls-authentication` 的 DeferredReason | 代码存在与真实部署验收分开；本轮不启用 SecurityTests/TLS |
| 已移除范围 | [OceanBaseProviderFactory](../src/OceanBaseConnector/OceanBaseProviderFactory.cs) 无 DataAdapter/CommandBuilder 入口 | 记为范围移除，不记为开发完成 |

此前 national 多语句草稿的失败已由后续模块修正，不能继续把[早期复核现场](history/2026-10-29-gap-completion-review.md)当当前失败。
最新普通文本扩展、值前空格修正和大 NUL 多语句限制见[文本证据](history/2026-10-29-oracle-text-literals.md)；
此前大 byte[] 扩展及逐项门禁见[二进制证据](history/2026-10-29-oracle-large-binary-literals.md)。
已实现欠验收、明确排除、服务端/权限限制是不同结论；23 个编号不构成可直接计算的完成百分比。

## 八个工作包

| 包 | 覆盖ID | 下一步交付重点 |
| --- | --- | --- |
| W1 池与会话 | P0-01、P0-02、P0-06 | 完整并发/资源/隐式状态与代理reset |
| W2 事务与恢复 | P0-03、P0-04、P0-05 | 发送/执行/取消边界、活动连接重建和协调器故障 |
| W3 协议选项 | P1-01、P1-02、P1-03 | 已有协议路径的真实上传/压缩和组合验收；OB20压缩/reroute 先保持拒绝 |
| W4 信任与发现 | P1-04、P1-09、P1-11 | 企业TLS/plugin/KDC、轮换、实际发现与HA |
| W5 命令与类型 | P1-05、P1-06、P1-07、P1-08 | 已有输出/复杂类型/编码路径的真实验收；输入游标及无依据复杂形状保持拒绝 |
| W6 目录与生态 | P1-10 | 真实目录/权限/派生 |
| W7 性能与诊断 | P2-02、P2-03、P2-04 | statement cache、已有批处理路径、负载与诊断开销；不扩展 multi-send/bulk 或未定义等待点 |
| W8 运行时与发布 | P2-05、P2-06 | 显式验收profile、承诺部署/运行时和发布证据 |

## 关闭标准与发布边界

每项功能须具备：明确支持/拒绝契约、源码、对应回归、成功构建、完整单测、一次独立最终走查、同步矩阵及本地提交。适用的实库/代理/运行时证据另行验证，不能用合成回环替代。

正式发布仍缺：完整reset/资源隔离、已归因的Oracle服务端/权限失败在满足前提后复验、真实OBProxy和TLS、完整unknown/XA/活动连接故障报告，以及非Windows和完整生态矩阵。此前局部通过不取消这些门槛。

不自动重放未知写，不静默降级TLS，失败会话不回池。分布式ambient提升、透明活动会话迁移、通用durable coordinator没有现成承诺，扩展前须明确范围。真实连接仅用于显式验收，普通单测/CI不依赖用户环境。证书生成/导入、TLS/mTLS握手及TLS消费者用例独立到 [SecurityTests](../tests/OceanBaseConnector.SecurityTests/README.md)，不在主解决方案或CI中执行；纯配置/协议和TCP覆盖保留。

## 按需查阅

- [当前具体功能矩阵、草稿和门禁](obconnector-dotnet-progress.md)
- [交付状态摘要](obconnector-dotnet-delivery-status.md)
- [详细审计、协议依据、场景/源码/关闭标准及15.1—15.107历史](history/obconnector-dotnet-completeness.md)
- [逐模块测试/走查/失败与提交记录](history/obconnector-dotnet-progress.md)
- [兼容性及已记录限制](compatibility.md)

日常更新矩阵和当前开发项，不再向本文件追加逐次交付记录；详细证据只在需要核查时打开。
