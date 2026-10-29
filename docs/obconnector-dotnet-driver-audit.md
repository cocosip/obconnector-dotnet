# 2026-10-29 范围走查与代码清理

基线`1705c1b`，当前分支。先以Java2.4.14固定源码和OceanBase官方V4.3.5文档重整范围，
再核对本地调用链、测试与导出。未将两种兼容模式解释为原生MySQL/Oracle完整实现。

## 交付内容

- [功能矩阵](obconnector-dotnet-progress.md)：逐模式连接/执行/类型/事务/协议/元数据/运维与发布范围。
- [缺失矩阵](obconnector-dotnet-completeness.md)：代码组合限制与缺失、候选能力、环境欠验收分开。
- [验收标准](acceptance-criteria.md)：从源码/fixture到完整实库与包发布，明确skip/cleanup/故障判据。
- [证据与删除决策](obconnector-dotnet-scope-audit.md)：固定官方来源，纠正multi-send、DataAdapter、NCLOB等旧推论。
- 历史逐日快照收敛为一份索引，删除重复交付/暂停入口，更新当前证据链接；不删除TLS等真实环境限制。

## 代码清理

删除A01公开空类/条件常量与其演示测试，保留并检查实际DataSource/Batch/factory资产边界；
删除三个零引用认证/解码辅助方法及两个未使用UTF-8批处理字节属性，真实会话编码计算不变。
这是对无用途脚手架和死代码的清理，不扩展矩阵中的缺失功能。
公开演示类型删除影响潜在外部引用者，升级需移除对OceanBaseModernApis/OceanBaseTargetSurface的调用。

TLS/认证、LOB/复杂类型、XA、OB20、发现/池均有真实实现，未找到可据证据整块删除的原生数据库专属模块。
不支持组合的校验是安全边界，不能作为半成品删掉。测试使用的TryRent保留。

完整默认回归暴露了另一个测试收尾竞态：PackageLiveConsumerTests在验证损坏行后提前关闭未读reader，
模拟服务器继续写剩余行/EOF时收到ConnectionReset/ConnectionAborted，最终等待Completion将其误报为失败。
只在负例断言已通过后接受这两类明确的IO断连；正常场景、其他错误及超时仍失败。产品执行逻辑未改。

## 本次本地证据

Windows，SDK10.0.403，.NET10.0.14；全部结果是本轮执行，历史实库没有重跑。

| 门禁 | 结果 |
| --- | --- |
| 五目标Release初次成功构建 | 0警告/0错误 |
| DI | 14/14通过 |
| 修改直接影响的TFM/API资产隔离 | 15/15通过 |
| 负例收尾修正专项 | 12/12通过 |
| 完整默认单测复跑 | 8547通过/0失败/0跳过；修正后的模拟服务器收尾竞态已关闭 |
| 默认消费者全量 | 3477通过/0失败/0跳过；同一过滤组复跑1339/1339通过 |
| Acceptance --list | 执行成功；保留matrix.tls-authentication deferred |
| 限定源码格式 | 通过；测试收尾修改后单独复核 |
| 文档检查 | 本地路径/锚点无失效；历史逐日快照已收敛为`docs/history/README.md` |
| 独立走查 | 文档两项链接问题已修正；代码删除及测试收尾复核无剩余实质问题 |

首次构建使用旧assets时引用了不存在的Visual Studio NuGet fallback目录；重新还原后解决。
在线漏洞数据源不可达，本次仅在restore命令临时使用NuGetAudit=false，没有修改产品/CI配置，
因此不主张本次完成依赖漏洞审计。运行中消费者占用DLL时的一次重复构建未通过，不当源码错误；
之后单独构建单测工程成功，最终串行门禁另记。具体TRX保存于本地TestResults/scope-cleanup，未提交。

## 验证边界

最终本地验证结果随本次变更记录；历史通过数不作为本次结果。
本次没有连接真实数据库、启用TLS或重跑SecurityTests，没有关闭[真实部署阻塞](compatibility.md)。
旧23项/八包不再作为当前入口；历史结论只保留在[历史证据索引](history/README.md)。
