# 真实兼容性与未验收部署

更新：2026-10-29。本页保留当前支持声明的限制，不重复开发流水。
范围以[能力矩阵](obconnector-dotnet-progress.md)为准，判定按[验收标准](acceptance-criteria.md)。
下面结果来自注明日期的历史报告，本次范围整理没有重新运行实库，也没有关闭既有失败。

## 环境矩阵

| 组合 | 历史证据 | 仍未验收 |
| --- | --- | --- |
| OceanBase4.4.2.2 MySQL直连、Windows、TLS Disabled | CRUD/类型/事务/池/LOCAL INFILE/批处理等子集；新增专项各有记录 | 全版本、全部API/负载/故障组合，不承诺原生MySQL兼容 |
| OceanBase4.4.2.2 Oracle直连、Windows、TLS Disabled | PL输出/LOB/复杂值/数组/XA/字符集等子集；同时保留真实失败 | 下表服务端/权限与形状失败，全版本/全部组合，不承诺原生Oracle兼容 |
| 真实TLS/企业CA/mTLS/撤销/轮换 | **当前两种模式真实环境未开TLS，未验收** | 必须保留matrix.tls-authentication deferred；自签名SslStream只属本地证据 |
| 认证plugin/KDC/凭据服务 | native账号直连与本地客户端分支 | 非native正负认证、平台协商、实际轮换；Java插件注册不是OB部署证明 |
| OBProxy/ODP、真正多节点、OCP/TNS服务 | 本地parser与受控单上游客户端路由 | 真实发现服务、节点/代理故障、后端reset/迁移，单上游中继不是HA |
| 标准压缩 | 未声明CLIENT_COMPRESS时按Java回退成功 | 真正压缩帧、损坏/取消/代理/TLS组合 |
| OB20/trace | 两模式declined-ob20回退通过；显式ob20因Layer=mysql失败 | 真正OB20双向帧、legacy/modern trace控制/采样/collector、ODP；不能称已实库支持 |
| .NET资产/平台 | 五资产与Windows实际host所选公开包实库48/48历史记录 | 非Windows实库、完整API/ORM；netstandard经host加载不等于独立runtime |

## 未关闭失败与前提

| 模式/项目 | 历史结论 | 处理边界 |
| --- | --- | --- |
| Oracle SJIS/Big5初始化 | Java独立复现900 | 保留失败，不写成所有字符集可用 |
| Oracle nested-table列 | Java复现600/0A000；类型创建/PL使用不能替代列存储 | 区分类型定义和持久列能力 |
| Oracle SELECT CURSOR列 | prepared902/HY000且Java复现；text值形状不符四字节解析 | 不把列metadata或PL OUT成功当值解码通过 |
| Oracle VIEW/SYNONYM | CREATE返回1031/HY000 | 权限不足，不代表OceanBase没有视图/同义词 |
| Oracle二进制named TZ | 非空地区名1882，Java server-prepare同样失败 | 数字偏移/文本通过不能替代binary地区名 |
| Oracle长NUL脚本 | 12003字符多语句通过，更大双SELECT在.NET/Java超时 | 长度和路径限制保留，发送后可能超时 |
| MySQL TIME极限小数 | ±838:59:59.999999存储截至整秒；strict文本1292，binary仍截断成功 | 不承诺两路径转换一致，不做静默客户端截断 |
| MySQL零日期 | 当前SQL_MODE前提失败，未建表 | profile存在不等于零日期存储已验收 |
| 原生prepare1295 | 通过中继注入拒绝后实际存储验证 | 只证明客户端回退分支，未复现租户原生1295 |
| ChangeDatabase/特殊端点 | alternateDatabase、专用不支持auth/disabled-autocommit、真实OB20/trace等配置缺失 | 配置skip保持开放，不能计为通过 |

2026-10-28全直连复核快照为**625通过、12失败、6配置跳过，共643项**；
这是当次选择和代码下的结果，不是当前工程测试总数或当前整体通过声明。
之后的单模块成功不能自动关闭所有12项失败，UTF8MB3修复前的6项则不再列为当前缺陷。
历史SecurityTests完整671项当次未完整执行，只有专项11/11；不是当前安全套件通过计数。

## 证据索引

当前入口只有本页、[功能矩阵](obconnector-dotnet-progress.md)、[缺失矩阵](obconnector-dotnet-completeness.md)
和[验收标准](acceptance-criteria.md)。历史过程记录已收敛到[历史证据索引](history/README.md)，
不再作为单独的功能或完成率入口；[安全测试入口](../tests/OceanBaseConnector.SecurityTests/README.md)仍保留。

完整资源交错/长期压力、unknown outcome所有发送/执行/取消点、ambient in-doubt/管理器崩溃、
XA其他崩溃点/服务器/代理恢复、跨版本/跨平台/发布审查继续开放。详见[缺失矩阵](obconnector-dotnet-completeness.md)。
