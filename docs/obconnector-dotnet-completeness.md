# 源码走查与功能缺失矩阵

更新：2026-10-30；本阶段基线`f7f3d38`。范围与编号以[能力矩阵](obconnector-dotnet-progress.md)为准，
证据来源见[范围审计](obconnector-dotnet-scope-audit.md)。本次走查不等于重新执行所有历史实库测试。

分类：**缺实现**是已保留目标中的代码缺口；**受限**是已定义子集，不能默认要求任意组合；
**候选差距**有参考能力但未纳入当前目标；**欠验收**有代码而缺特定部署证据；
**服务端/权限阻塞**必须保留失败；**排除**不进入开发积压。

## 当前服务端前提判定

当前真实配置只有 OceanBase 4.4.2.2 的 MySQL/Oracle 直连 profile，均为 TLS Disabled；没有
proxy/ODP、多节点/OCP/TNS、OB20/trace 或非 native 认证/KDC profile。下列能力依赖这些服务端
前提，当前阶段统一标记为**服务端受限，暂不开发与修复**。本地协议 fixture、Java SPI 或
单上游 relay 只能保留边界证据，不能解除冻结。

| 项目 | 当前确认 | 暂不开发的原因 |
| --- | --- | --- |
| TLS/mTLS/企业 CA/撤销/轮换 | 两个真实 profile 明确 TLS Disabled | 没有真实 TLS 端点和证书链，代码修改无法形成 OceanBase 验收证据 |
| OB20/trace/压缩 OB20 | 没有 OB20 profile；强制 OB20 历史失败于服务端 `Layer=mysql` | 缺少服务端协商与双向帧，不能以本地帧 fixture 代替部署能力 |
| OBProxy/ODP/多节点/OCP/TNS | 没有真实代理或发现服务 profile | 单上游中继不能证明 HA、后端 reset、迁移或发现服务 |
| 非 native 认证/KDC/凭据轮换 | 当前只配置 native 账号直连 | 没有插件/KDC 正负端点，Java 插件注册不等于 OceanBase 支持 |
| UJIS/EUCJPMS/CP932/CP850/HP8/MACROMAN/SWE7 | 当前无对应 profile；SJIS/Big5 仅 MySQL 直连 4/4 通过，Oracle 历史失败保留 | 没有逐项服务端 roundtrip 和完整严格映射证据 |

上述冻结不删除现有 API、拒绝校验、负向测试或 Deferred 记录；获得真实端点后再按验收标准
重新立项。

## 代码与覆盖差距

| ID | 对应能力/模式 | 分类 / 现状 | 源码证据 | 关闭条件 / 下一步 |
| --- | --- | --- | --- | --- |
| G01 | Q04 Oracle普通prepare回退 | 阶段1已补齐已有 DATE/TIMESTAMP/时区、INTERVAL、NCHAR/NVARCHAR2 字面量；仍受限于普通单语句、输入参数和无输出/cursor/复杂值 | Internal/Sql/ClientPreparedSql.cs；CommandExchange.ClientPreparedSql | 继续补充前须独立确认字面量身份、编码/时区/精度和1295前提；不能用多语句支持替代 |
| G02 | Q05 双模式多语句 | 受限：Prepare、cursor、OUT/复杂输入不支持 | OceanBaseCommand.cs；ClientPreparedSql.cs | 当前限定输入+text结果；扩大前需模式/方向/wire证据，不默认补所有笛卡尔组合 |
| G03 | Q11 Oracle输出/游标 | 受限：输出offset拒绝；当前目标版本已验收的普通 `SELECT CURSOR` 列形状为服务端限制，Java 同样返回 902/1002 | OracleCommandPlan.cs；QueryResult.Cursor.cs；OracleCursorOwner.cs | 正FetchSize仅提示；若出现新的可读取列形状再单独验收，不能用 OUT 成功关闭当前结果列限制 |
| G04 | T14/T15 Oracle复杂值 | 受限：当前 OBJECT/嵌套 OBJECT NULL 输出身份为服务端限制且 Java 同样不报告外层 NULL；文本复杂结果、复杂cursor输入、named locator回绑、未解析RETURNING身份仍拒绝 | ComplexValueCodec.cs；ComplexTypeContext.cs；OracleCommandPlan.cs | collection 的可区分 NULL 结果及其余对象/集合、成员方向、named/anonymous、PL输出/存储逐项证实；不以属性 NULL 猜测根身份 |
| G05 | T12 Oracle UROWID | 部分：ROWID209字符串结果保持兼容；UROWID已有显式参数、NULL/type209 wire、DataTypes 和目录映射 | ResultColumn.cs:209、PreparedParameters、RoutineParameterDerivation、Schema.DataTypes | Oracle 4.4.2.2 直连已验证普通/PrepareExecute、OUT/INOUT/ReturnValue、RETURNING、坏值拒绝与恢复；UROWID真实Derive已在FLOAT编号签名验收，NUMBER/VARCHAR2目录边界见A-03；长压、代理、跨平台未验收；literal/fallback 不支持 |
| G06 | T05 MySQL GEOMETRY | 部分：type255原始byte[]结果可经Binary参数回绑；DataTypes参数支持及空间过程目录映射已补 | ResultColumn.cs:255、PreparedParameters:Binary、RoutineParameterDerivation、Schema.DataTypes | SRID=0直连输入/输出/独立存储与Binary fallback已验证；目标SRS目录仅0，非零SRID待服务端前提；无GIS领域API，进度见D-06 |
| G07 | T19/T20 两模式字符集 | 部分：GB2312、EUCKR 已按 OceanBase 4.4.2 的 collation 做严格客户端/结果映射；剩余新名称服务端前提和完整映射未证实，暂不扩展 | ClientCharacterSet.cs；ResultCharacterSet.Resolve；EucEncoding.cs；ClientCharacterSetTests/CharacterSetTests | MySQL GB2312/EUCKR客户端/结果、Oracle GB2312客户端及两族national指定直连子集见D-07；当前目标普通 Oracle EUCKR 初始化 900 已按 Java 同交互归为服务端限制；SJIS/Big5历史失败、其他版本与部署仍保留 |
| G08 | T17/T18 专用新类型 | XMLType/SDO/ANYDATA仍是候选差距；4.4.2.2 MySQL VECTOR/ARRAY 复用 String，RoaringBitmap 复用 Binary 读取及 SQL 函数写入，均无专用 codec 承诺 | OceanBaseDbType.cs、ResultColumn/PreparedParameters、ComplexValueCodec | 当前版本通用路径已验收；直接 RoaringBitmap `byte[]` prepared binding 的 `1235/0A000` 是服务端限制。其他版本、专用 wire、XMLType/SDO/ANYDATA 仍需单独证据，不把服务端转换等同原生类型支持 |
| G09 | Q20 multi-send | 部分：.NET 已有默认关闭的 prepared batch multi-send；可缓存 input-only DML/SELECT、基本 scalar 排空、时间区/国民字符集上下文、副作用 SQL 拒绝及 64 MiB 分组边界有 loopback 证据；4.4.2.2 MySQL/Oracle 直连临时 profile 的 DML/scalar × sync/async **8/8 通过** | Java DefaultOptions:470–490、AbstractQueryProtocol:1287–1326；`UseBatchMultiSend`、`OceanBaseBatch.MultiSend` | 首个子集仍缺收益基线、服务端错误排序扩展、代理/OB20 和 ExecuteReader、Oracle PrepareExecute、LOB/OUT/cursor/array；不和 MariaDB bulk 混淆 |
| G10 | C10/C11/Q17 池/reset/cache | 欠验收：有实现及部分实库；任意副作用/全部资源交错未证实 | PhysicalSessionPool、PhysicalSession.PreparedStatements、SessionStateSql | 持续并发容量/超时/关闭交错、句柄/线程/内存稳定；状态不可证明时销毁 |
| G11 | X02/X03/X04 事务与故障 | 欠验收：显式XA/PSPE/unknown有实现，故障点未全覆盖 | AmbientEnlistment、Connection.Xa*、CommandExchange.Io | in-doubt/管理器与节点/代理崩溃、取消顺序、分支恢复/无重放及独立查账 |
| G12 | C05–C09/P02–P04/C12–C14 | 服务端受限，暂不开发与修复；现有路径保留 | TLS/Authentication/Discovery/压缩/OB20已有路径 | 提供真实 TLS/认证/ODP/压缩/OB20/trace/发现 profile 后再按验收标准开启；当前限制不得删除 |
| G13 | M01/Q07 元数据与目录 | 受限/欠验收：唯一可见签名，包内PL类型/重载边界；A-03b 的 `CHAR_LENGTH` 缺失已确认是当前目标服务端目录限制，Java 同样收到 `904/42S22` | Connection.Schema.*、RoutineParameterDerivation.cs | 其余最小权限、大小写/引用标识符、包/重载和实际列身份；不以 `DATA_LENGTH`/`DATA_PRECISION` 伪造字符长度 |
| G14 | R01 发布/平台 | 欠验收：有五资产和Windows子集记录 | csproj、Verify-Packages.ps1、ConsumerTests | 实际OS/runtime/API/包路径逐项验证，netstandard host加载不算全平台 |
| G15 | C08 非native认证分支 | 服务端受限，暂不开发与修复；当前仅 native 账号直连 | AuthenticationConversation、GssapiAuthentication | 提供真实插件/KDC 端点、服务端文档和正负认证后再列部署支持；Java SPI 不能替代 |

没有发现产品源码TODO/FIXME/NotImplementedException式空实现。已确认的无用途代码是A01演示类型、
3个零引用方法和2个过时UTF-8快捷属性，删除范围与外部API影响见[清理决策](obconnector-dotnet-scope-audit.md)。
现有NotSupported检查不是完整性统计，更不是全部待开发任务。

## 必须保留的真实阻塞

| ID | 能力/模式 | 已记录事实 | 关闭所需证据 |
| --- | --- | --- | --- |
| E01 | TLS/mTLS/认证，两模式 | 当前真实租户TLS未开启；企业CA/撤销/轮换/plugin/KDC未验收 | 获得真实配置端点后执行正负握手；保留matrix.tls-authentication deferred |
| E02 | Oracle SJIS/Big5 | 初始化失败，官方Java同样复现900 | 版本/服务端问题确认或新部署通过，不能删用例或改无条件skip |
| E03 | Oracle nested-table列 | 创建类型可成功，创建列600/0A000，Java复现 | 区分PL集合/对象类型与持久列，取得目标版本支持后复验 |
| E04 | Oracle SELECT CURSOR列 | 当前 scalar/多行列形状分别在 execute/FETCH 返回 902/1002，Java 同样复现；text实际值不符既有四字节解析 | 当前形状按服务端限制跳过；只有拿到新的确切可读取形状/wire 后才重新处理，不能用 metadata 或 PL OUT 覆盖 |
| E05 | Oracle VIEW/SYNONYM | CREATE权限1031/HY000 | 具备权限再验，服务端支持不等于当前账号授权 |
| E06 | Oracle named TZ | 二进制非空地区名1882，强制Java server prepare同样失败 | 服务端区域文件/版本/模式组合支持；数字偏移通过不等于命名地区通过 |
| E07 | Oracle长NUL多语句 | 12003字符已记录通过，更大双SELECT在.NET/Java超时 | 隔离服务器执行/SQL展开预算，记录确切长度，不承诺无限长脚本 |
| E08 | MySQL TIME极限小数 | permissive binary截整秒；strict文本1292、binary仍截断成功 | 明示路径差异，不能静默截断/CAST/重试使二者看似一致 |
| E09 | 普通prepare fallback | 中继注入1295后真实存储通过，租户未复现原生1295 | 原生拒绝场景需独立验收，注入证据只关闭客户端分支 |
| E10 | OB20/trace/标准压缩/ODP | 直连只验证declined-ob20和压缩未声明回退；强制OB20因Layer=mysql失败 | 实际双向目标帧、trace控制/采样，collector另验；真实ODP不可用单上游中继代替 |
| E11 | MySQL零日期 | 有profile，但当前SQL_MODE前置不满足，未建表 | 合适SQL_MODE专用profile验证存储/读写，不在测试中私改租户基线 |
| E12 | Oracle `ALL_ARGUMENTS.CHAR_LENGTH` | 目标 OceanBase 4.4.2.2 目录缺少 `CHAR_LENGTH`/`CHAR_USED`，直接查询返回 `904/42S22`；Java Connector/J 同一查询同码 | 提供字符长度列或稳定等价目录来源的目标版本/profile 后复核；当前跳过客户端 fallback |
| E13 | Oracle OBJECT/嵌套 OBJECT NULL 输出身份 | 当前目标版本返回 type 160 且外层 NULL 位为 false；Java `getObject/wasNull` 同样不报告外层 NULL | 提供可区分根 NULL 的新结果 wire 后复核；当前跳过客户端改码，不把全 NULL 属性对象转换为 `DBNull` |

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
