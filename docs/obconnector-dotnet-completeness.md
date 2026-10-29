# 源码走查与功能缺失矩阵

更新：2026-10-29；检查基线`1705c1b`。范围与编号以[能力矩阵](obconnector-dotnet-progress.md)为准，
证据来源见[范围审计](obconnector-dotnet-scope-audit.md)。本次走查不等于重新执行所有历史实库测试。

分类：**缺实现**是已保留目标中的代码缺口；**受限**是已定义子集，不能默认要求任意组合；
**候选差距**有参考能力但未纳入当前目标；**欠验收**有代码而缺特定部署证据；
**服务端/权限阻塞**必须保留失败；**排除**不进入开发积压。

## 代码与覆盖差距

| ID | 对应能力/模式 | 分类 / 现状 | 源码证据 | 关闭条件 / 下一步 |
| --- | --- | --- | --- | --- |
| G01 | Q04 Oracle普通prepare回退 | 阶段1已补齐已有 DATE/TIMESTAMP/时区、INTERVAL、NCHAR/NVARCHAR2 字面量；仍受限于普通单语句、输入参数和无输出/cursor/复杂值 | Internal/Sql/ClientPreparedSql.cs；CommandExchange.ClientPreparedSql | 继续补充前须独立确认字面量身份、编码/时区/精度和1295前提；不能用多语句支持替代 |
| G02 | Q05 双模式多语句 | 受限：Prepare、cursor、OUT/复杂输入不支持 | OceanBaseCommand.cs；ClientPreparedSql.cs | 当前限定输入+text结果；扩大前需模式/方向/wire证据，不默认补所有笛卡尔组合 |
| G03 | Q11 Oracle输出/游标 | 受限：输出offset拒绝；普通cursor列形状有失败 | OracleCommandPlan.cs；QueryResult.Cursor.cs；OracleCursorOwner.cs | 正FetchSize仅提示；单独解决/确认实际cursor值形状，不能以OUT成功关闭结果列失败 |
| G04 | T14/T15 Oracle复杂值 | 受限：文本复杂结果、复杂cursor输入、named locator回绑、未解析RETURNING身份拒绝 | ComplexValueCodec.cs；ComplexTypeContext.cs；OracleCommandPlan.cs | 对象/集合、成员方向、named/anonymous、PL输出/存储逐项证实；拒绝不当空实现删除 |
| G05 | T12 Oracle UROWID | 部分：ROWID209已有字符串结果路径，DataTypes 已暴露结果专用元数据；UROWID独立往返仍未证实 | ResultColumn.cs:209类型；OceanBaseConnection.Schema.DataTypes；无专用public UROWID类型 | Java目录/空表metadata不足；需真实值、绑定、结果、metadata验证后声明；当前不支持ROWID参数/字面量 |
| G06 | T05 MySQL GEOMETRY | 部分：类型255已有原始byte[]结果路径，DataTypes 已暴露结果专用元数据；不承诺参数/GIS领域API | ResultColumn.cs 类型255；OceanBaseConnection.Schema.DataTypes；PreparedParameters.cs | 原始字节/SRID和真实读写需独立验收；领域库不是当前必需 |
| G07 | T19/T20 两模式字符集 | 候选差距：官方较新GB2312/UJIS/EUCKR/EUCJPMS/CP932/CP850/HP8/MACROMAN/SWE7等未完整映射 | ClientCharacterSet.cs白名单；ResultCharacterSet.Resolve | 固定服务端版本/真实collation和严格roundtrip后扩充；未知ID继续拒绝，不套用系统近似编码 |
| G08 | T17/T18 专用新类型 | 候选差距：XMLType/SDO/ANYDATA、MySQL vector/bitmap/SQL array无专用codec承诺 | OceanBaseDbType.cs、ResultColumn/PreparedParameters、ComplexValueCodec | 先确认版本、Java或wire类型与应用需求；服务端转换成既有标量可另验，不等于原生类型支持 |
| G09 | Q20 multi-send | 候选差距：Java已实现，.NET缺实现 | Java DefaultOptions:470–490、AbstractQueryProtocol:1287–1326；本地无选项 | 先证明目标OB部署和可测收益，定义部分失败/响应排序/资源限额；不和MariaDB bulk混淆 |
| G10 | C10/C11/Q17 池/reset/cache | 欠验收：有实现及部分实库；任意副作用/全部资源交错未证实 | PhysicalSessionPool、PhysicalSession.PreparedStatements、SessionStateSql | 持续并发容量/超时/关闭交错、句柄/线程/内存稳定；状态不可证明时销毁 |
| G11 | X02/X03/X04 事务与故障 | 欠验收：显式XA/PSPE/unknown有实现，故障点未全覆盖 | AmbientEnlistment、Connection.Xa*、CommandExchange.Io | in-doubt/管理器与节点/代理崩溃、取消顺序、分支恢复/无重放及独立查账 |
| G12 | C05–C09/P02–P04/C12–C14 | 欠部署验收，非缺API | TLS/Authentication/Discovery/压缩/OB20已有路径 | 逐项补真实TLS/认证/ODP/压缩/OB20/trace/发现profile，当前环境限制不得删除 |
| G13 | M01/Q07 元数据与目录 | 受限/欠验收：唯一可见签名，包内PL类型/重载边界 | Connection.Schema.*、RoutineParameterDerivation.cs | 最小权限、大小写/引用标识符、包/重载和实际列身份；权限失败不加客户端伪造 |
| G14 | R01 发布/平台 | 欠验收：有五资产和Windows子集记录 | csproj、Verify-Packages.ps1、ConsumerTests | 实际OS/runtime/API/包路径逐项验证，netstandard host加载不算全平台 |
| G15 | C08 非native认证分支 | 待适用性证明，非两模式通用支持 | AuthenticationConversation、GssapiAuthentication | 原生MySQL/MariaDB插件名不能证明OB支持；真实端点/服务端文档+正负认证后才能列部署支持 |

没有发现产品源码TODO/FIXME/NotImplementedException式空实现。已确认的无用途代码是A01演示类型、
3个零引用方法和2个过时UTF-8快捷属性，删除范围与外部API影响见[清理决策](obconnector-dotnet-scope-audit.md)。
现有NotSupported检查不是完整性统计，更不是全部待开发任务。

## 必须保留的真实阻塞

| ID | 能力/模式 | 已记录事实 | 关闭所需证据 |
| --- | --- | --- | --- |
| E01 | TLS/mTLS/认证，两模式 | 当前真实租户TLS未开启；企业CA/撤销/轮换/plugin/KDC未验收 | 获得真实配置端点后执行正负握手；保留matrix.tls-authentication deferred |
| E02 | Oracle SJIS/Big5 | 初始化失败，官方Java同样复现900 | 版本/服务端问题确认或新部署通过，不能删用例或改无条件skip |
| E03 | Oracle nested-table列 | 创建类型可成功，创建列600/0A000，Java复现 | 区分PL集合/对象类型与持久列，取得目标版本支持后复验 |
| E04 | Oracle SELECT CURSOR列 | prepared902/HY000且Java复现；text实际值不符既有四字节解析 | 拿到确切支持形状/wire后处理，不能用metadata或PL OUT成功覆盖 |
| E05 | Oracle VIEW/SYNONYM | CREATE权限1031/HY000 | 具备权限再验，服务端支持不等于当前账号授权 |
| E06 | Oracle named TZ | 二进制非空地区名1882，强制Java server prepare同样失败 | 服务端区域文件/版本/模式组合支持；数字偏移通过不等于命名地区通过 |
| E07 | Oracle长NUL多语句 | 12003字符已记录通过，更大双SELECT在.NET/Java超时 | 隔离服务器执行/SQL展开预算，记录确切长度，不承诺无限长脚本 |
| E08 | MySQL TIME极限小数 | permissive binary截整秒；strict文本1292、binary仍截断成功 | 明示路径差异，不能静默截断/CAST/重试使二者看似一致 |
| E09 | 普通prepare fallback | 中继注入1295后真实存储通过，租户未复现原生1295 | 原生拒绝场景需独立验收，注入证据只关闭客户端分支 |
| E10 | OB20/trace/标准压缩/ODP | 直连只验证declined-ob20和压缩未声明回退；强制OB20因Layer=mysql失败 | 实际双向目标帧、trace控制/采样，collector另验；真实ODP不可用单上游中继代替 |
| E11 | MySQL零日期 | 有profile，但当前SQL_MODE前置不满足，未建表 | 合适SQL_MODE专用profile验证存储/读写，不在测试中私改租户基线 |

来源和历史运行上下文集中见[真实兼容性与未验收部署](compatibility.md)及[历史证据索引](history/README.md)。
本次范围整理未重新证明这些服务器状态仍相同。

## 明确排除与历史ID归并

不列为缺实现：原生Oracle Net/OCI、LONG/LONG RAW/BFILE/NCLOB、Aurora/Galera、MariaDB bulk、
压缩OB20/proxy reroute、透明活动会话迁移/未知写自动重放、分布式ambient提升/通用持久协调器、
REF CURSOR输入、DataAdapter/CommandBuilder、未定义的“其他资源等待”。
排除依据不同：有的是官方不支持，有的是参考协议不足，有的是产品主动不承诺，不能混写。

| 旧23项 | 当前追踪 |
| --- | --- |
| P0-01/P0-02/P0-06 | C10–C14、G10/G12 |
| P0-03/P0-04/P0-05 | X02–X04、G11；透明迁移为排除 |
| P1-01/P1-05 | Q01–Q10/Q13/Q14/Q19、G01/G02/G13 |
| P1-02/P1-03/P1-04 | P02–P05/Q18/C05/C06、G12/E01/E10 |
| P1-06/P1-07/P1-08 | Q11/Q12/T01–T20、G03–G08 |
| P1-09/P1-10/P1-11 | C12/C13/M01/C09、G12/G13/G15 |
| P2-01 | R02，已移除范围，不计完成 |
| P2-02/P2-03/P2-04 | Q15–Q21/M02/M03；multi-send为候选，bulk为排除 |
| P2-05/P2-06 | R01与所有真实验收门槛，不作为功能API |

旧完整性清单和暂停快照已删除，历史结论仅在[历史证据索引](history/README.md)中保留摘要，
不再用八包/阶段编号驱动无依据开发。关闭G/E项须按[验收标准](acceptance-criteria.md)记录证据，
失败可能揭示驱动缺陷，不能预先一概归为服务端问题。
