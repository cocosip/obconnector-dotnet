# 交付状态摘要

更新：2026-10-28。范围审计基线为`89a304d`，最近功能模块提交为`85e6fe6`；P2-01 DataAdapter/CommandBuilder 已按范围审计移除；主文档改为当前状态，历史流水不再默认加载。

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
| 原23项审计项 / 八包 | P2-01 已移除，其余按证据分类 | 子功能已实现范围见矩阵；没有整体完成百分比 |
| 生产级完整验收 | 未完成 | 完整资源/并发、真实OBProxy/TLS/auth/HA/版本/平台/运行时及发布审查 |

## 优先处理的明确缺实现

1. 连接池完整 reset、资源隔离、并发和真实部署验收。
2. 已有 Java/OceanBase 依据但仍受限的 REF CURSOR 输出、复杂类型和字符集形状，先按真实服务端矩阵确认。
3. OB20 压缩/reroute 等当前 Java/协议依据不足的组合继续明确拒绝，不列为默认开发目标。
4. TLS/auth、代理/版本/平台和发布运行时的真实验收。

已有实现欠验收、明确拒绝和已移除功能是三类工作，不能把推测性 Oracle/MySQL 完整性当成项目需求。详细失败/跳过保留在历史记录；新连接信息不写入任何文档，实库验收独立于普通单测/CI。
