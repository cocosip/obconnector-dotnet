# 功能完成与剩余项暂停检查点

日期：2026-10-29。MySQL 日期/时间普通 Prepare 回退模块已完成，按用户要求暂停，不进入新模块。
结论：**并非所有缺失功能都已完成**。下面的“完成”指已交付的支持子集，不代表整个23项矩阵或八个工作包关闭。
完整编号、源码入口及逐项欠验收见[完整性清单](obconnector-dotnet-completeness.md)和[具体功能矩阵](obconnector-dotnet-progress.md)。

## 本轮已交付的功能

| 功能 | 已完成的范围 | 提交 / 证据 |
| --- | --- | --- |
| MySQL 日期/时间普通客户端回退 | opt-in自动Prepare明确拒绝后的DATE/DATETIME/TIME及DateTime2输入；精度、NULL、零日期、会话时区、事务及不重试边界 | 当前模块；[记录](history/2026-10-29-mysql-temporal-fallback.md) |
| MySQL JSON、BIT/YEAR普通回退 | 严格字符串/精确数值字面量，沿用验证、快照和不重放规则 | `1ce67ac`、`6faa697`；[JSON](history/2026-10-29-mysql-json-fallback.md)、[BIT/YEAR](history/2026-10-29-mysql-bit-year-fallback.md) |
| MySQL / Oracle二进制INOUT流 | 已有prepared SET/CALL或PrepareExecute分片上传；源流归调用者，完整结果后发布输出 | `bcaf7fa`、`85a7246`；[MySQL](history/2026-10-29-mysql-inout-stream.md)、[Oracle](history/2026-10-29-oracle-inout-stream.md) |
| 文本多语句参数扩展 | 双模式普通标量/日期时间/二进制流；MySQL JSON/BIT/YEAR；Oracle interval、带时区时间及national输入 | `25603a9`、`23a1dab`、`e1b187a`、`7fc4d85`、`f011212`、`5e0d295`、`6e5929a`；范围见[矩阵](obconnector-dotnet-progress.md) |
| Oracle普通长文本及大二进制字面量 | 保留VARCHAR2/RAW身份，扩展NUL/长值及大byte[]；仍受目标和展开SQL预算限制 | `3bf5d57`、`5618f92`；[文本](history/2026-10-29-oracle-text-literals.md)、[二进制](history/2026-10-29-oracle-large-binary-literals.md) |
| 显式national编码及复杂形状验收 | 复用十二种严格编码映射；标量48/48、复杂PL输出/对象存储/数组DML等392/392直连通过 | `74f6497`、`c1e99dd`；[编码](history/2026-10-29-national-character-encodings.md)、[复杂验收](history/2026-10-29-complex-national-encoding-acceptance.md) |
| Windows公开NuGet实库消费 | 五资产及Framework4.8.1 host，共48/48双模式场景；仅所选API和单一Windows环境 | `cee6637`；[记录](history/2026-10-29-package-live-consumption.md) |

此前连接池、会话隔离、单资源环境事务、显式恢复、SchemaOnly/KeyInfo、LOCAL INFILE、
statement cache、批处理和诊断已有实现及局部验收；其完整并发、故障、部署和负载门禁仍保留在原矩阵。

最后模块的完整默认单测8548/8548、相关单测81/81、公开DLL96/96、直连92/92（新增时间40项）、
DI14/14均零失败/跳过；五目标Release构建零警告/错误，一次独立最终走查无Critical/Important/Minor。
直连以中继精确注入Prepare拒绝并核对真实存储，不等于复现租户原生1295。

## 仍未实现或保持受限的组合

| 项目 | 暂停时状态 |
| --- | --- |
| Oracle普通客户端回退的时间/interval/national输入 | 仍拒绝；多语句已支持这些输入，不等于普通回退已完成，后续扩展需独立OceanBase证据 |
| 多语句的Prepare、服务端cursor、输出及复杂输入 | 仍拒绝；现有输入/文本结果子集不扩大到这些组合 |
| Oracle输出offset | 仍拒绝；接受正FetchSize提示不等于输出分页完成 |
| 其他类型/元数据/复杂存储与结果形状 | 尚未逐项闭合；未知类型或缺少可信编码/身份仍显式拒绝 |

以下按当前范围审计**明确排除，不应记成“已实现”或默认待开发项**：REF CURSOR Input/InputOutput、
复杂cursor输入、命名LOB locator回绑、文本复杂结果、未解析复杂RETURNING身份、压缩OB20、proxy reroute、
独立bulk/multi-send、未定义诊断等待点。分布式ambient提升、透明活动会话迁移和未知写自动重放也无当前承诺。
DataAdapter/CommandBuilder是**已移除范围**，不是补齐完成；见[范围审计](obconnector-dotnet-scope-audit.md)。

## 服务端 / 权限阻塞与欠验收

| 类别 | 未关闭内容 |
| --- | --- |
| 当前Oracle租户与官方Java同样失败 | SJIS/Big5初始化、nested-table列、SELECT CURSOR(...)902、二进制TZ非空名称1882；不能用文本通过替代二进制支持 |
| 权限 | CREATE VIEW / CREATE SYNONYM返回1031，需满足权限前提后复验 |
| Oracle长NUL脚本 | 已验证12003字符多语句；更大两个SELECT脚本在.NET/官方Java存在超时，仍在支持证据外 |
| MySQL TIME上限小数 | ±838:59:59.999999实际存储会截到整秒；严格模式文本回退报1292而二进制仍截断成功，不能承诺两路径转换一致 |
| 真实部署 | 原生1295触发、OBProxy、真正OB20/trace/压缩、TLS/mTLS/企业CA、认证plugin/KDC、发现/HA拓扑 |
| 完整质量与发布矩阵 | 长期并发/资源、完整发送/执行/取消及协调器故障、其他服务端版本、非Windows、完整API/ORM及公开包实库覆盖 |

以上服务端/权限结论保留在[失败归因](history/2026-10-28-direct-failure-triage.md)、
[时区对照](history/2026-10-29-multi-statement-zoned-time-input.md)及相应模块记录；本次没有重跑全部历史失败。
本次不启用TLS/SecurityTests，不推送、不发布。暂停后不自动继续其他功能。
