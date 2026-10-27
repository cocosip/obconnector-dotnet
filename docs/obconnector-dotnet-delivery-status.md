# 交付状态摘要

更新：2026-10-27。已提交产品基线`85e6fe6`；主文档改为当前状态，历史流水不再默认加载。

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
| OB20/trace显式验收 | **开发中、未提交** | 草稿五profile/十case；专项/runner88/88通过；未完成全解决方案构建、完整单测、最终走查和提交，未跑实库 |
| 原23项缺口 / 八包 | 部分完成，整项关闭0 | 子功能已实现范围见矩阵；没有整体完成百分比 |
| 生产级完整验收 | 未完成 | 完整资源/并发、真实OBProxy/TLS/auth/HA/版本/平台/运行时及发布审查 |

## 优先处理的明确缺实现

1. Input/InputOutput REF CURSOR。
2. 命名复杂LOB定位器回绑、复杂cursor输入、文本复杂结果及未解析复杂RETURNING身份。
3. 其他national存储/输出、扩展元数据/类型；Oracle UTF8MB3/SJIS/Big5 Open失败。
4. OB20压缩和proxy reroute，先确认客户端链路/版本/capability。
5. Multi-send/pipelining或独立bulk，以及其他资源等待。
6. TLS/auth显式验收、当前OB20/trace草稿及完整部署验证。

已有实现欠验收与确实缺代码是两类工作，不能用增加诊断或测试数量代替功能支持。详细失败/跳过保留在历史记录；新连接信息不写入任何文档，实库验收独立于普通单测/CI。
