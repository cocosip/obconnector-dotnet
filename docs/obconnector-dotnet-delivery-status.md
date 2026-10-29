# 交付状态摘要

更新：2026-10-29。范围审计基线为`89a304d`；P2-01 DataAdapter/CommandBuilder已移除；本轮交付及门禁以下表为准，历史流水按需读取。

| 阅读目的 | 文档 |
| --- | --- |
| 具体哪些已支持、哪些未实现 | [开发进度与功能矩阵](obconnector-dotnet-progress.md) |
| 23项缺口、八包及关闭标准 | [完整性清单](obconnector-dotnet-completeness.md) |
| 实际部署支持和限制 | [兼容性](compatibility.md) |
| 追查某次测试/失败/走查 | [历史模块台账](history/obconnector-dotnet-progress.md) |
| 查协议依据与详细验收标准 | [历史审计参考](history/obconnector-dotnet-completeness.md) |
| 查整理前的交付总览 | [历史总览](history/obconnector-dotnet-delivery-status.md) |

## 当前交付与草稿

| 项目 | 状态 | 证据 / 剩余门禁 |
| --- | --- | --- |
| Oracle大byte[]客户端字面量 | 已实现并完成本轮验收 | 已删除共享2000字节客户端限制，保留HEXTORAW/RAW身份、快照与展开SQL预算；相关单测215/215、DI14/14、双模式多语句/流实库283/283（新增30项），五目标Release零警告/错误；Consumer扩大回归1171/1171、独立最终走查无遗留；完整默认单测8399/8399，零失败、零跳过。见[模块证据](history/2026-10-29-oracle-large-binary-literals.md) |
| 多语句二进制Stream输入 | 已实现并完成本轮验收 | 复用源流诊断、快照、引用身份、取消预算及展开后SQL包限制；相关单测547/547、公开DLL多语句消费者96/96、新增双模式独立存储/源失败46/46、既有多语句/流实库回归253/253、DI14/14，五目标Release零警告/错误；完整默认单测8384/8384、独立最终走查无遗留。见[模块证据](history/2026-10-29-multi-statement-stream-input.md) |
| national复杂编码直连验收 | 已完成本轮验收 | 十四种national设置，命名/嵌套OUT/INOUT、对象存储、匿名数组DML及RETURNING拒绝边界392/392，连同标量最终实库440/440；完整单测8344/8344、相关单测544/544、公开DLL消费者336/336、DI14/14，五目标Release零警告/错误；独立最终走查无遗留。见[验收证据](history/2026-10-29-complex-national-encoding-acceptance.md) |
| 显式national标量编码/结果 | 已实现并完成本轮验收 | 十二种既有严格映射；完整单测8344/8344、相关单测544/544、公开DLL消费者336/336、Oracle新标量直连48/48、双模式既有回归230/230、DI14/14，五目标Release零警告/错误；独立最终走查无遗留。见[编码证据](history/2026-10-29-national-character-encodings.md)；后续PL实库证据见本表复杂编码验收，其他存储及部署矩阵仍开放 |
| Oracle多语句national输入 | 已实现并完成本轮验收 | NChar/NVarchar2使用OceanBase Unicode字面量；相关单测413/413、公开DLL消费者120/120、双模式直连回归240/240（新增38项）、DI14/14，五目标Release零警告/错误；完整默认单测8128/8128，独立最终走查无遗留。见[模块证据](history/2026-10-29-multi-statement-national-input.md)；national结果/复杂存储及其他部署边界仍开放 |
| MySQL多语句JSON输入 | 已实现并完成本轮验收 | 官方Java文本路径及三种编码实库对照；支持显式Json输入并区分JSON null与SQL NULL，沿用字符串编码/字符集/SQL_MODE保护；完整默认单测8120/8120、消费者相关组72/72、双模式多语句/类型/JSON字符集实库202/202、DI14/14，五目标Release零警告/错误；独立走查无遗留。见[模块证据](history/2026-10-29-multi-statement-json-input.md)；national输出/复杂存储及其他多语句/部署边界仍开放 |
| 协议协商诊断 | 已提交`85e6fe6` | 保存的完整单测8091/8091、消费者340/340、重建专项/runner488/488；Release零警告/错误，一次最终走查无遗留 |
| OB20/trace显式验收 | **已提交，部分实库验收** | `bc020e2`/`c5b4c07`；专项/runner88/88、Release零警告/错误；直连4.4.2.2双模式 `declined-ob20` 各6通过。用户租户实际协商为MySQL，显式OB20各2项失败；真实OB20/trace、OBProxy、TLS/auth及完整单测环境门禁仍开放。见[证据](history/2026-10-27-ob20-trace-acceptance.md) |
| 池维护期间关闭 | 已完成本轮代码与回归 | 探活/reset 同步异步 4 项 RED→GREEN，物理池专项 23/23、完整单测 8126/8126、DI 14/14；五资产 Release 零警告/错误，独立走查无待修发现。见[模块证据](history/2026-10-28-pool-maintenance-shutdown.md)；长期压力和真实部署矩阵仍开放 |
| DataSource 关闭取消 Open | 已完成代码与回归 | 凭据/发现及非池化 Open 取消，保留已建立借用连接；相关单测160/160、完整单测8138/8138、DI14/14，五资产Release零警告/错误；新增直连关闭8/8，完整Connections矩阵128通过/4配置跳过，独立走查问题已修复。见[模块证据](history/2026-10-28-data-source-open-shutdown.md) |
| 单资源环境事务直连验收 | 已完成本轮矩阵 | 新增16/16通过，完整事务/XA/故障直连组67/67通过；独立走查后加强下一借用者的未提交事务隔离断言。见[模块证据](history/2026-10-28-ambient-transaction-acceptance.md)；事务管理器/代理/服务器故障门禁保留 |
| 环境事务管理器定时超时 | 已完成本轮直连矩阵 | 双模式同步异步4/4，真实超时事件、关闭前owner实际回滚、同物理会话的新事务及旧事务晚释放隔离；走查后补强原连接查询，最终事务/XA/故障相关组135/135。见[证据](history/2026-10-28-ambient-manager-timeout.md)；活动命令期间超时另4/4，坏会话销毁、新物理会话及旧资源晚释放隔离见[补验](history/2026-10-28-ambient-active-command-timeout.md)；in-doubt及管理器/代理崩溃仍开放 |
| 活动事务取消/超时后重建 | 已完成本轮直连矩阵 | 新增8/8，完整事务/XA/故障组75/75通过、0跳过；确认旧写回滚、新物理会话及旧事务晚释放隔离，独立走查无发现。见[证据](history/2026-10-28-interrupted-transaction-recovery.md)；完整发送/执行边界和真实节点/代理故障仍开放 |
| 文本多语句客户端门禁 | 已修复并验收 | 按Java客户端选项启用，不再要求服务端握手声明；本地回归RED→GREEN，专项13/13、双模式直连16/16、完整单测8140/8140、DI14/14；五资产Release零警告/错误，独立走查无发现。见[证据](history/2026-10-28-multi-statement-client-policy.md) |
| LOCAL INFILE分块/源失败 | 已完成本轮直连矩阵 | 同步异步共4/4通过；大于1MiB、256行全部内容、流释放及失败后新会话/无重放验证，独立走查无发现。见[证据](history/2026-10-28-local-infile-chunked-acceptance.md)；同步源阻塞/其他中断及代理组合仍保留 |
| Statement cache DDL/reset | 已完成本轮直连矩阵 | 双模式同步异步4/4，实际缓存复用、held/idle语句刷新及同物理会话reset后重备，独立走查无发现。见[证据](history/2026-10-28-statement-cache-live-lifecycle.md)；外部删列确认错误后条目淘汰及新结构读取另4/4，相关34/34，见[补验](history/2026-10-28-statement-cache-external-ddl.md)；特定reprepare错误及负载门禁保留 |
| MySQL INSERT rewrite真实语义 | 已完成本轮直连矩阵 | 6/6验证Unicode默认值、触发器、事务和重复键失败边界；相关回归245通过/4配置跳过，走查后加强直接字符串读取断言。见[证据](history/2026-10-28-batch-rewrite-live-semantics.md) |
| LOCAL INFILE源等待中断 | 已完成本轮直连矩阵 | 取消/超时新增2/2，协议组26/26；走查后修复失败路径任务等待，默认未启用实库18通过/137跳过。见[证据](history/2026-10-28-local-infile-interruption.md) |
| LOCAL INFILE源流校验失败释放 | 已修复并验收 | 修复工厂流在CanRead校验失败前的泄漏，新增单测4项RED→GREEN、相关19/19，直连4/4及协议/缓存38/38；五资产构建零警告/错误、完整单测8144/8144、DI14/14、独立走查无遗留。见[记录](history/2026-10-28-local-infile-validation-ownership.md) |
| 完整双模式直连复核 | 仍有失败，未整体关闭 | 截至`05d226e`及目录测试拆分，643项中625通过、12失败、6配置跳过；原18项中6项已修复，剩余12项保留；目录权限失败拆分为独立View/Synonym测试。默认未启用实库22通过/142跳过；当时源码完整单测8144/8144、DI14/14及五资产构建零警告/错误。Oracle SJIS/Big5初始化、nested-table及游标结果形状和目录权限问题保留；utf8mb3归一校验已修复，见[归因](history/2026-10-28-direct-failure-triage.md) |
| Unknown outcome 本地 pre-send 边界 | 已修复并验收 | 同步/异步帧写入前参数校验2/2：不误报执行结果未知、保持连接可用并成功执行下一条命令；见[证据](history/2026-10-28-unknown-outcome-presend.md)；实际写入后的完整发送/执行/取消及服务器/代理/协调器故障仍开放 |
| 写入请求/回包截断 | 已完成本轮直连矩阵 | 新增16/16，完整事务/XA/故障组91/91；独立核对实际零/一行、无重放、新租约及旧Command晚释放，走查无发现。见[证据](history/2026-10-28-write-outcome-boundaries.md)；真实服务器/代理/协调器故障仍开放 |
| 外置LOB读取回包中断 | 已完成本轮直连矩阵 | Oracle BLOB/CLOB同步异步及整包/半包中断8/8；非unknown、Broken、新会话及旧资源晚释放时新LOB完整内容，相关回归244通过/4配置跳过；后续修正转发器在共享Acceptance项目中的内部类型访问编译错误，夹具10/10、验收网络/拓扑14/14、故障/LOB直连68/68，独立走查无遗留。见[证据](history/2026-10-28-external-lob-read-interruption.md)；其他中断点和代理reset仍开放 |
| 连接池交叉借用隔离验收 | 已完成本轮直连矩阵 | MySQL/Oracle 直连共94通过、1配置跳过、0失败；Windows/.NET 10.0.14，SDK 11.0.100-rc.3 构建零警告/错误。证据见[兼容性记录](compatibility.md#2026-10-28-pool-cross-borrower-isolation-acceptance)；OBProxy、长期并发和完整资源边界仍开放 |
| MySQL 存储函数 ReturnValue | 已实现并完成直连验收 | 单ReturnValue+Input 使用 `SET @var = function(?)` 和隐藏 `SELECT`；同步/异步单测及真实 OceanBase MySQL 各通过1项；完整代理/版本/运行时矩阵仍开放 |
| TFM / 包消费 | 已完成 Windows 验收 | `Verify-Packages.ps1 -VerifyNetFramework` 验证两包的五目标资产、依赖组、nupkg 哈希和消费者 restore graph；net8.0/net9.0/net10.0 各运行8项协议样例，netstandard2.0/2.1由net10 host加载，面向`net48`的host在已安装.NET Framework 4.8.1上加载netstandard2.0并通过8项样例。见[记录](history/2026-10-28-package-consumption.md)；非Windows和完整生态矩阵仍开放 |
| 原23项审计项 / 八包 | P2-01 已移除，其余按证据分类 | 子功能已实现范围见矩阵；没有整体完成百分比 |
| 安全验收与默认CI隔离 | 已拆分，验证见记录 | 证书/TLS测试独立到主解决方案之外的SecurityTests，默认保留TCP/协议覆盖；详见[记录](history/2026-10-28-security-test-isolation.md)，真实TLS环境门禁保留 |
| Windows CI池夹具取消 | 已修复提交 `a7a313f` | 保留 `994a262` 的取消/等待/停止顺序，仅在取消已触发且socket错误为OperationAborted时正常退出；专项连续160/160、当时完整默认单测8003/8003，独立走查无遗留，见[记录](history/2026-10-28-windows-pool-fixture-cancellation.md)；远程CI尚未复验 |
| Oracle 数组 FetchSize 提示 | 已实现并完成本轮验收 | Java 2.4.14 及 Oracle 直连确认正提示不阻止数组执行；新增8项RED后通过、相关单测134/134、公开DLL消费者36/36、Oracle数组实库12/12、完整默认单测8011/8011、DI14/14；五资产Release零警告/错误，独立走查无遗留；内部结果不分页，offset仍拒绝，见[记录](history/2026-10-28-array-fetch-hint.md) |
| MySQL 过程/函数 FetchSize 提示 | 已实现并完成本轮验收 | Java 2.4.14 实库对照、36项原限制RED、相关单测100/100、公开DLL消费者48/48、MySQL过程/函数实库30/30、完整默认单测8053/8053、DI14/14；五资产Release零警告/错误，独立走查无遗留；内部结果不分页，输出语义保持，见[记录](history/2026-10-28-mysql-routine-fetch-hint.md) |
| Oracle参数化文本多语句 | 已实现并完成本轮验收 | Java双模式对照确认客户端绑定支持；新增正常用例6项RED→GREEN，相关单测47/47、公开DLL消费者12/12、当时双模式直连组24/24（新增Oracle参数8项）、完整默认单测8063/8063、DI14/14，五资产Release零警告/错误，独立最终走查无遗留；详见[记录](history/2026-10-28-oracle-multi-statement-parameters.md)。MySQL普通输入本轮已补，其他边界见矩阵 |
| MySQL参数化文本多语句 | 已实现并完成本轮验收 | 官方Java/实库对照；6项RED→GREEN、相关单测232/232、双模式公开DLL消费者24/24、双模式实库组78/78（新增MySQL54项，含12种非默认客户端字符集）、完整默认单测8102/8102（0失败、0跳过）、DI14/14、五资产Release零警告/错误，独立最终走查无待修发现；MySQL字符串保持值/collation/coercibility并支持NUL和SQL_MODE切换，解析字符集须稳定，详见[记录](history/2026-10-28-mysql-multi-statement-parameters.md) |
| 多语句时间参数输入 | 已实现并完成本轮回归 | MySQL DATE/DATETIME/TIMESTAMP/TIME 与 Oracle DATE/TIMESTAMP 客户端字面量，复用已有精度/范围校验；同步/异步 MySQL/Oracle 文本执行和精确 SQL 回归 5/5 通过，见[记录](history/2026-10-28-multi-statement-temporal-input.md) |
| Oracle多语句INTERVAL输入 | 已实现并完成本轮验收 | 官方Java文本路径与实库对照确认；支持两种精确区间、推断/显式类型、typed NULL和声明精度；完整默认单测8111/8111、公开DLL消费者36/36、双模式多语句实库90/90、DI14/14，五资产Release零警告/错误，独立走查无遗留；沙箱文件替换限制及复验见[记录](history/2026-10-29-multi-statement-interval-input.md) |
| Oracle多语句时区时间输入 | 已实现并完成本轮验收 | 支持DateTimeOffset及精确TZ/LTZ、typed NULL；保持LTZ会话校验、显式精度及中途切换时区的时间值；完整默认单测8116/8116、公开DLL消费者48/48、双模式多语句/类型实库140/140、DI14/14，五资产构建零警告/错误；独立走查后同步旧流拒绝断言，无遗留产品问题；命名地区、二进制非空名称及结果快照限制见[记录](history/2026-10-29-multi-statement-zoned-time-input.md) |
| 生产级完整验收 | 未完成 | 完整资源/并发、真实OBProxy/TLS/auth/HA/版本/平台/运行时及发布审查 |

## 优先处理的明确缺实现

1. 连接池完整 reset、资源隔离、并发和真实部署验收。
2. 已有 Java/OceanBase 依据但仍受限的 REF CURSOR 输出、复杂类型和字符集形状，先按真实服务端矩阵确认。
3. OB20 压缩/reroute 等当前 Java/协议依据不足的组合继续明确拒绝，不列为默认开发目标。
4. TLS/auth、代理/版本/平台和发布运行时的真实验收。

已有实现欠验收、明确拒绝和已移除功能是三类工作，不能把推测性 Oracle/MySQL 完整性当成项目需求。详细失败/跳过保留在历史记录；新连接信息不写入任何文档，实库验收独立于普通单测/CI。
