# 历史证据索引

历史开发快照已收敛为当前文档中的结论，避免把过程记录误当成产品承诺。

当前仍有效的范围、缺失和验收标准分别维护在：

- [功能能力矩阵](../obconnector-dotnet-progress.md)
- [缺失矩阵](../obconnector-dotnet-completeness.md)
- [验收标准](../acceptance-criteria.md)
- [真实兼容性与未验收部署](../compatibility.md)
- [本次走查与代码清理](../obconnector-dotnet-driver-audit.md)

保留的真实环境边界：当前 OceanBase 4.4.2.2 MySQL/Oracle 租户均未启用 TLS，因此 TLS、企业 CA、mTLS、撤销和轮换仍是 `Deferred`；真实 OB20/trace、OBProxy、多节点、认证插件/KDC、完整 SecurityTests 及跨平台实库也没有被历史本地 fixture 关闭。Oracle SJIS/Big5、nested-table 列、CURSOR 列、named time zone、MySQL TIME 极限小数和权限/版本相关失败，均以当前兼容性页的表格为准。

删除的文件是重复的阶段快照、旧交付摘要、暂停检查点和逐项开发流水；删除不改变源码、测试结果或当前文档中的失败/跳过结论。
