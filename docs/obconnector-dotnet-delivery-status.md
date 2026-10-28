# 交付状态摘要

更新：2026-10-28。范围审计基线为`89a304d`；P2-01 DataAdapter/CommandBuilder已移除；本轮交付及门禁以下表为准，历史流水按需读取。

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
| 协议协商诊断 | 已提交`85e6fe6` | 保存的完整单测8091/8091、消费者340/340、重建专项/runner488/488；Release零警告/错误，一次最终走查无遗留 |
| OB20/trace显式验收 | **已提交，部分实库验收** | `bc020e2`/`c5b4c07`；专项/runner88/88、Release零警告/错误；直连4.4.2.2双模式 `declined-ob20` 各6通过。用户租户实际协商为MySQL，显式OB20各2项失败；真实OB20/trace、OBProxy、TLS/auth及完整单测环境门禁仍开放。见[证据](history/2026-10-27-ob20-trace-acceptance.md) |
| 池维护期间关闭 | 已完成本轮代码与回归 | 探活/reset 同步异步 4 项 RED→GREEN，物理池专项 23/23、完整单测 8126/8126、DI 14/14；五资产 Release 零警告/错误，独立走查无待修发现。见[模块证据](history/2026-10-28-pool-maintenance-shutdown.md)；长期压力和真实部署矩阵仍开放 |
| DataSource 关闭取消 Open | 已完成代码与回归 | 凭据/发现及非池化 Open 取消，保留已建立借用连接；相关单测160/160、完整单测8138/8138、DI14/14，五资产Release零警告/错误；新增直连关闭8/8，完整Connections矩阵128通过/4配置跳过，独立走查问题已修复。见[模块证据](history/2026-10-28-data-source-open-shutdown.md) |
| 单资源环境事务直连验收 | 已完成本轮矩阵 | 新增16/16通过，完整事务/XA/故障直连组67/67通过；独立走查后加强下一借用者的未提交事务隔离断言。见[模块证据](history/2026-10-28-ambient-transaction-acceptance.md)；事务管理器/代理/服务器故障门禁保留 |
| 活动事务取消/超时后重建 | 已完成本轮直连矩阵 | 新增8/8，完整事务/XA/故障组75/75通过、0跳过；确认旧写回滚、新物理会话及旧事务晚释放隔离，独立走查无发现。见[证据](history/2026-10-28-interrupted-transaction-recovery.md)；完整发送/执行边界和真实节点/代理故障仍开放 |
| 文本多语句客户端门禁 | 已修复并验收 | 按Java客户端选项启用，不再要求服务端握手声明；本地回归RED→GREEN，专项13/13、双模式直连16/16、完整单测8140/8140、DI14/14；五资产Release零警告/错误，独立走查无发现。见[证据](history/2026-10-28-multi-statement-client-policy.md) |
| LOCAL INFILE分块/源失败 | 已完成本轮直连矩阵 | 同步异步共4/4通过；大于1MiB、256行全部内容、流释放及失败后新会话/无重放验证，独立走查无发现。见[证据](history/2026-10-28-local-infile-chunked-acceptance.md)；超限/真实取消/代理等门禁保留 |
| Statement cache DDL/reset | 已完成本轮直连矩阵 | 双模式同步异步4/4，实际缓存复用、held/idle语句刷新及同物理会话reset后重备，独立走查无发现。见[证据](history/2026-10-28-statement-cache-live-lifecycle.md)；外部删列确认错误后条目淘汰及新结构读取另4/4，相关34/34，见[补验](history/2026-10-28-statement-cache-external-ddl.md)；特定reprepare错误及负载门禁保留 |
| MySQL INSERT rewrite真实语义 | 已完成本轮直连矩阵 | 6/6验证Unicode默认值、触发器、事务和重复键失败边界；相关回归245通过/4配置跳过，走查后加强直接字符串读取断言。见[证据](history/2026-10-28-batch-rewrite-live-semantics.md) |
| LOCAL INFILE源等待中断 | 已完成本轮直连矩阵 | 取消/超时新增2/2，协议组26/26；走查后修复失败路径任务等待，默认未启用实库18通过/137跳过。见[证据](history/2026-10-28-local-infile-interruption.md) |
| 完整双模式直连复核 | 仍有失败，未整体关闭 | 截至`f51310d`，630项中606通过、18失败、6配置跳过；与此前600项快照的18项失败名称完全一致。默认未启用实库20通过/140跳过；字符集初始化、Oracle结果形状及目录权限问题保留，见[复核](history/2026-10-28-direct-profile-matrix.md) |
| 写入请求/回包截断 | 已完成本轮直连矩阵 | 新增16/16，完整事务/XA/故障组91/91；独立核对实际零/一行、无重放、新租约及旧Command晚释放，走查无发现。见[证据](history/2026-10-28-write-outcome-boundaries.md)；驱动本地pre-send及真实服务器/代理/协调器故障仍开放 |
| 外置LOB读取回包中断 | 已完成本轮直连矩阵 | Oracle BLOB/CLOB同步异步及整包/半包中断8/8；非unknown、Broken、新会话及旧资源晚释放时新LOB完整内容，相关回归244通过/4配置跳过；后续修正转发器在共享Acceptance项目中的内部类型访问编译错误，夹具10/10、验收网络/拓扑14/14、故障/LOB直连68/68，独立走查无遗留。见[证据](history/2026-10-28-external-lob-read-interruption.md)；其他中断点和代理reset仍开放 |
| 连接池交叉借用隔离验收 | 已完成本轮直连矩阵 | MySQL/Oracle 直连共94通过、1配置跳过、0失败；Windows/.NET 10.0.14，SDK 11.0.100-rc.3 构建零警告/错误。证据见[兼容性记录](compatibility.md#2026-10-28-pool-cross-borrower-isolation-acceptance)；OBProxy、长期并发和完整资源边界仍开放 |
| MySQL 存储函数 ReturnValue | 已实现并完成直连验收 | 单ReturnValue+Input 使用 `SET @var = function(?)` 和隐藏 `SELECT`；同步/异步单测及真实 OceanBase MySQL 各通过1项；完整代理/版本/运行时矩阵仍开放 |
| 原23项审计项 / 八包 | P2-01 已移除，其余按证据分类 | 子功能已实现范围见矩阵；没有整体完成百分比 |
| 生产级完整验收 | 未完成 | 完整资源/并发、真实OBProxy/TLS/auth/HA/版本/平台/运行时及发布审查 |

## 优先处理的明确缺实现

1. 连接池完整 reset、资源隔离、并发和真实部署验收。
2. 已有 Java/OceanBase 依据但仍受限的 REF CURSOR 输出、复杂类型和字符集形状，先按真实服务端矩阵确认。
3. OB20 压缩/reroute 等当前 Java/协议依据不足的组合继续明确拒绝，不列为默认开发目标。
4. TLS/auth、代理/版本/平台和发布运行时的真实验收。

已有实现欠验收、明确拒绝和已移除功能是三类工作，不能把推测性 Oracle/MySQL 完整性当成项目需求。详细失败/跳过保留在历史记录；新连接信息不写入任何文档，实库验收独立于普通单测/CI。
