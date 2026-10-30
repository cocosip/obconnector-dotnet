# 文档目录

当前范围、源码差距与验收分别只有一个维护入口，避免开发流水变成产品承诺。

| 文档 | 用途 |
| --- | --- |
| [功能能力矩阵](obconnector-dotnet-progress.md) | SDK需要提供什么、MySQL/Oracle差异、选项/类型/方向边界与当前实现 |
| [缺失矩阵](obconnector-dotnet-completeness.md) | 缺实现/受限/候选/欠验收/服务端权限阻塞/排除分别追踪 |
| [验收标准](acceptance-criteria.md) | 测试分层、最小断言、完整实库/安全/包门槛及关闭条件 |
| [范围依据与清理决策](obconnector-dotnet-scope-audit.md) | 固定Java/官方文档版本、纠正旧结论、删除代码依据 |
| [真实兼容性与阻塞](compatibility.md) | 当前真实环境没开TLS等未验收状态、保留失败/skip |
| [缺失功能开发清单](obconnector-dotnet-missing-features-roadmap.md) | **后续开发唯一跟踪入口**：Java/OceanBase/.NET差异、开发任务、服务端前提、进度和关闭记录 |
| [本次走查与清理记录](obconnector-dotnet-driver-audit.md) | 本次实际改动、验证和保留边界 |

参考契约：[架构/API细节](obconnector-dotnet-architecture-design.md)、[Java结构分析](obconnector-j-architecture-outline.md)、
[字符集](client-character-sets.md)、[凭据](credentials.md)、[Schema](schema.md)、
[未知结果恢复](unknown-outcome-recovery.md)、[网络故障](network-outcome-acceptance.md)、
[XA进程恢复](xa-process-recovery-acceptance.md)、[拓扑](topology-acceptance.md)、[OB20/trace](ob20-trace-acceptance.md)、
[协议诊断](protocol-diagnostics.md)、[I/O诊断](session-io-diagnostics.md)、[源流诊断](source-read-diagnostics.md)、[池指标](pool-metrics.md)。
架构文档含历史阶段设计，若其排期/范围与当前矩阵不同，以当前矩阵为准；API细节仍须核对源码。

`history/`只保留一份[历史证据索引](history/README.md)；详细阶段快照、重复交付摘要和暂停入口已删除。
当前文档中的失败、跳过和TLS未验收边界优先于任何历史成功记录。
