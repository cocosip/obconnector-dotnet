# 2026-10-27 OB20/trace显式验收交付记录

基于`953d993`开始实现，文档矩阵整理提交`a318903`后继续验证。产品代码/API/依赖不变；验收用五个显式profile、十个同步/异步case替换原OB20占位，仅TLS/auth仍Deferred。支持边界见[验收契约](../ob20-trace-acceptance.md)，当前状态只维护在[功能矩阵](../obconnector-dotnet-progress.md)。

| 门禁 | 结果 |
| --- | --- |
| RED | 12失败，暴露缺少配置和可执行case；原日志保留 |
| 初次专项 | 73通过/15失败：14项合成服务端漏回Oracle会话状态查询；1项把全局listener其他订阅误判为本捕获器泄漏 |
| 修正专项/runner | 88/88通过，无跳过；覆盖双模式五profile、握手不能代替命令帧、错误类型/身份/格式、缺trace策略、错误结果、截断和同步/异步取消/释放、报告脱敏 |
| Release全解决方案 | 串行构建通过，零警告/错误，32.81秒 |
| 完整单测首轮 | 8148通过/1失败/零跳过，8分51秒；唯一失败为历史TNS File.Replace，原日志和TRX保留 |
| 完整单测复跑 | 未形成可引用的完整绿门禁；PFX/SSPI和静默TLS环境失败仍保留，不以隔离结果代替 |
| 同产物TNS隔离复核 | 1/1通过；不替代完整运行，根因仍未确认 |
| Opt-out集成 | 15通过/116跳过/零失败，未提供环境 |
| 独立最终走查 | 一次，未发现Critical/Important/Minor问题；不复审，最终门禁由主执行者收集 |
| 格式 | 初次只报新测试对象初始化器空白，修正后八文件验证退出0；保留通常的workspace-load警告 |
| 实库/OBProxy/TLS/collector | 未运行，不声明兼容或端到端导出成功 |

捕获器仅固定标量/布尔汇总，按逻辑流与listener/ChannelId匹配；不保存字典、payload或trace ID。
默认profile关闭、examples不变、无DDL、不改服务器设置/凭据/TLS。显式trace profile若没有服务器策略使请求发送extra-info则失败，不能静默算作通过。

## 2026-10-28真实租户补充

用户提供的直连 OceanBase 4.4.2.2、Windows/.NET 10.0.14、TLS Disabled 验证显示：MySQL 和 Oracle
各自运行 `declined-ob20` 同步/异步 profile，均为 6 passed、0 failed；服务器报告实际 `Layer=mysql`。
两种模式的 `ob20` profile 各 2 项均失败于同一协商差异（请求 OB20、实际 MySQL），没有把普通 fallback
算作 OB20 成功。trace-legacy/trace-modern 未在该服务器上伪造成功；真实 OB20/trace 仍需要服务端开启
对应 capability/策略，OBProxy、TLS/auth 和 collector 仍未验收。

忽略的本地证据位于`TestResults/ob20-trace-acceptance-*`。原始失败均保留；没有修改TNS源码、加重试或跳过断言。
