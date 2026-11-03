# 源码走查与功能缺失矩阵

更新：2026-11-03；本阶段基线`7df19b1`，本轮补齐 D-08w multi-send SequentialAccess reader 与 XMLTYPE 顺序字段。范围与编号以[能力矩阵](obconnector-dotnet-progress.md)为准，
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
| G01 | Q04 Oracle普通prepare回退 | 阶段1已补齐已有 DATE/TIMESTAMP/时区、INTERVAL、NCHAR/NVARCHAR2 字面量；`oracle-direct` User Secrets 实库正向 Prepare/输出/时间脚本复核 **38/38** 通过，但本次未触发原生 `1295/HY000`；仍受限于普通单语句输入，无输出/cursor/LOB/anonymous；命名 OBJECT/collection 构造器已复用于普通及 batch 受控 fallback（D-01b） | Internal/Sql/ClientPreparedSql.cs；CommandExchange.ClientPreparedSql | 继续补充前须独立确认字面量身份、编码/时区/精度和1295前提；不能用多语句支持替代 |
| G02 | Q05 双模式多语句 | 受限：普通输入/多结果及 MySQL、Oracle 时间脚本存储已有直连子集；Oracle 命名 OBJECT、嵌套 OBJECT 和集合输入 D-02d1～d3 已达 P4；Prepare、cursor、OUT 和其余复杂组合仍不支持 | OceanBaseCommand.cs；ClientPreparedSql.cs；MultiStatementTests.Temporal.cs | 当前限定输入+text结果；集合子树 NULL OBJECT、locator、匿名复杂值及复杂结果仍保留限制；扩大前需模式/方向/wire证据，不默认补所有笛卡尔组合 |
| G03 | Q11 Oracle输出/游标 | 受限：输出offset拒绝；当前目标版本已验收的普通 `SELECT CURSOR` 列形状为服务端限制，Java 同样返回 902/1002 | OracleCommandPlan.cs；QueryResult.Cursor.cs；OracleCursorOwner.cs | 正FetchSize仅提示；若出现新的可读取列形状再单独验收，不能用 OUT 成功关闭当前结果列限制 |
| G04 | T14/T15 Oracle复杂值 | 受限：当前 OBJECT/嵌套 OBJECT NULL 输出身份为服务端限制且 Java 同样不报告外层 NULL；named BLOB OBJECT 成员 DML 在 .NET 与 Java 同 wire 诊断回放均返回 `932/22000`，按当前目标服务端限制跳过客户端改动；named OBJECT 标量/递归 text 结果已支持；当前 RAW/DATE/TIMESTAMP text 成员收到服务端重复 RAW payload，Connector/J 同交互也返回相同字符串并且 `getObject` 为 null，按服务端限制跳过；命名 collection 构造的 text 结果 metadata 为 `VARCHAR2`，.NET/Connector-J 均按 String 返回，不需要 complex parser；CLOB/BLOB named OBJECT text 结果在 Java `getObject` 为 null、payload 无可靠成员身份，.NET 预加载描述后 sync/async **2/2** 安全拒绝，按当前服务端 wire 限制跳过；SQL OBJECT 与 PL-only `RECORD` 的当前 cursor 成员形状均无法形成有效对象，Java/.NET 同为服务端限制；其他复杂 cursor、named locator回绑、未解析RETURNING身份仍拒绝 | ComplexValueCodec.cs；ComplexTypeContext.cs；OracleCommandPlan.cs | 新版本/租户提供不同 BLOB 成员或复杂 text wire 时重开 D-04a2/D-04e；若出现可创建的 cursor 成员形状，重新验收 child FETCH/所有权；其余对象/集合、成员方向、named/anonymous、PL输出/存储继续逐项证实；不以属性 NULL 猜测根身份 |
| G05 | T12 Oracle UROWID | 部分：ROWID209字符串结果保持兼容；UROWID已有显式参数、NULL/type209 wire、DataTypes 和目录映射 | ResultColumn.cs:209、PreparedParameters、RoutineParameterDerivation、Schema.DataTypes | Oracle 4.4.2.2 直连已验证普通/PrepareExecute、OUT/INOUT/ReturnValue、RETURNING、坏值拒绝与恢复；UROWID真实Derive已在FLOAT编号签名验收，NUMBER/VARCHAR2目录边界见A-03；长压、代理、跨平台未验收；client text 输入已通过 CHARTOROWID 接入多语句与限定 1295 fallback，短/长/NULL、坏值/空串拒绝及恢复有直连/受控中继证据（D-05b）；原生 1295 未复现 |
| G06 | T05 MySQL GEOMETRY | 部分：type255原始byte[]结果可经Binary参数回绑；DataTypes参数支持及空间过程目录映射已补 | ResultColumn.cs:255、PreparedParameters:Binary、RoutineParameterDerivation、Schema.DataTypes | SRID=0直连输入/输出/独立存储与Binary fallback已验证；目标SRS目录仅0，非零SRID待服务端前提；无GIS领域API，进度见D-06 |
| G07 | T19/T20 两模式字符集 | 部分：GB2312、EUCKR 已按 OceanBase 4.4.2 的 collation 做严格客户端/结果映射；剩余新名称服务端前提和完整映射未证实，暂不扩展 | ClientCharacterSet.cs；ResultCharacterSet.Resolve；EucEncoding.cs；ClientCharacterSetTests/CharacterSetTests | MySQL GB2312/EUCKR客户端/结果、Oracle GB2312客户端及两族national指定直连子集见D-07；当前目标普通 Oracle EUCKR 初始化 900 已按 Java 同交互归为服务端限制；SJIS/Big5历史失败、其他版本与部署仍保留 |
| G08 | T17/T18 专用新类型 | XMLType 已补齐当前 4.4.2.2 直连的 text+binary String wire 子集；`SYS.XMLTYPE` PL OUT/INOUT 与 Connector/J 同租户同形状均返回服务端 `600/0A000`、`600/42000`，按服务端限制跳过；SDO 已有显式 Prepare 整列读取与 generic OBJECT/ARRAY 输入子集，但没有专用 codec；ANYDATA 在当前 4.4.2.2 目标租户因 `SYS.ANYDATA` 缺失且 Java 同交互失败，标记服务端限制；MySQL VECTOR/ARRAY 复用 String，RoaringBitmap 复用 Binary 读取及 SQL 函数写入，均无专用 codec 承诺 | OceanBaseDbType.cs、ResultColumn/PreparedParameters、OracleCommandPlan、Schema.DataTypes、ComplexValueCodec、CommandExchange.ComplexColumns、RoaringBitmapTests.cs、XmlProbeTests.cs | 当前版本通用路径已验收；XMLTYPE type 160 ownerless text/binary 结果按严格 UTF-8 映射为 String，`SYS.XMLTYPE` metadata 有单元边界，`DbType.Xml` 按 type 253 字符串参数写入，NULL、metadata、同步/异步 DML 与 PL 函数结果真实 Oracle 直连 **text 2/2、binary 3/3**；PL OUT/INOUT 负向回归 sync/async **4/4**，并有 Java 2.4.14 同错误证据；带 owner 的复杂 XML payload、坏值/最大值和跨版本部署仍未确认。RoaringBitmap 真实 MySQL 直连 sync/async **2/2** 覆盖 BLOB/`byte[]`、空/NULL/64 位、SQL 函数回绑和坏 raw；直接 `byte[]` prepared binding 的 `1235/0A000` 是服务端限制。ANYDATA 的 Java/.NET 同失败证据已记录；不把通用 SDO 子集或服务端转换等同专用类型 codec |
| G09 | Q20 multi-send | 部分：.NET 已有默认关闭的 prepared batch multi-send；可缓存 input-only DML/SELECT、基本 scalar 排空、MySQL BLOB/TEXT binary result materialize、时间区/国民字符集上下文、副作用 SQL 拒绝、64 MiB 分组边界、MySQL Stream long-data 发送和 server prepare `1295/HY000` 的 client-prepared text fallback 有 loopback 证据；Oracle Stream 在该选项下安全回退到逐命令 piece-data；named OBJECT/collection 普通输入及 named complex binary 结果已有实现；根 BLOB/CLOB locator 普通输入和保留调用方连接的结果复用已有编码/decoder；命名复杂 LOB 内容输入已复用既有 writer，保留调用方连接的命名复杂 LOB binary 结果已复用既有 decoder；Oracle DML RETURNING 标量 OUT/INOUT、命名复杂值及保留调用方连接的 LOB 输出已复用输出计划并按每条最终成功响应发布；匿名集合 binary 结果已复用普通 reader decoder（D-08s，仅 loopback）；anonymous 输入与 cursor 成员发送前拒绝；4.4.2.2 MySQL/Oracle 直连临时 profile 的 DML/scalar × sync/async **8/8 通过**，真实 MySQL Stream 存储/`GetStream` 回读 **2/2 通过**，Oracle Stream piece-data fallback 存储/LOB 读回 **2/2 通过**，已知响应错误的 prefix/error/suffix 边界 **4/4 通过**，Oracle PrepareExecute + locator Stream 安全回退 **2/2 通过**，1295 text fallback sync/async loopback **2/2 通过**，MySQL BLOB/TEXT result loopback sync/async **4/4 通过**，第一响应成功后第二响应挂起的 caller token/batch cancel/timeout **3/3 通过**；Connector/J 2.4.14 的 Oracle piece-data 路径也是每片等待确认，未提供 piece-data 流水化证据，因此 .NET 不猜测增加该协议 | Java DefaultOptions:470–490、AbstractQueryProtocol:1287–1326；`UseBatchMultiSend`、`OceanBaseBatch.MultiSend`、`CommandExchange.BatchLongData`、`CommandExchange.PieceData`、`ClientPreparedSql` | 流水化仍缺收益基线、代理/OB20、Oracle 自有连接 Scalar 的 locator 结果及显式 LOB OUT/INOUT、complex text LOB 结果、PL 输出、cursor/array；named OBJECT/collection 普通输入已通过精确 wire/错误边界单测及 Oracle 直连 sync/async 独立存储 **4/4**（D-08i）；普通/嵌套 OBJECT binary 结果与顶层 national/LTZ 混合读取、scalar/nonquery 排空已通过 Oracle 直连 **4/4**（D-08j），collection 结果仅有 loopback 证据；根 BLOB/CLOB locator 输入与保留调用方连接的 reader/scalar/nonquery 结果已分别通过 Oracle 直连 **4/4**（D-08k/l）；命名复杂 LOB 内容输入已通过 Oracle 直连 **8/8**（D-08m）；命名复杂 LOB binary 结果的平面/嵌套 OBJECT 已通过 Oracle 直连 **8/8**，collection 仅有 loopback 证据（D-08n）；Oracle DML RETURNING 标量 OUT/INOUT 已通过 Oracle 直连 **8/8**（D-08o）；命名复杂值及根/OBJECT LOB RETURNING 输出通过 Oracle 直连 sync 隐式/async 显式 Prepare **10/10**，collection 输出仅 loopback（D-08p）；XMLTYPE String 结果及 bound Prepare 的目录身份解析已支持（D-08q）；命名 OBJECT 标量/嵌套 text 结果已接入受控 1295 fallback（D-08r）；命名 OBJECT/collection 普通输入构造器也已接入受控 fallback（D-01b）；匿名输入/声明输出、cursor 成员、复杂 text LOB 结果及自有连接 Scalar 的 locator 结果及显式 LOB OUT/INOUT仍未纳入流水化；关闭连接 reader 的读取窗口已补齐，根远程 BLOB/CLOB 直连 **8/8**（D-08u）；未知结果的更大错误排序矩阵仍待验收；data source NonQuery 已支持普通 LOB 行无句柄排空，根远程 BLOB/CLOB 直连 **4/4**、匿名 binary LOB 仅 loopback（D-08v）；SequentialAccess reader 已复用流式字段与失败清理，双模式普通字段直连 **8/8**、Oracle LOB 窗口 **16/16**（含 Default 对照8）、XML **4/4**（含 Default 对照2），命名/匿名复杂顺序路径仅 loopback（D-08w）；不和 MariaDB bulk 混淆 |
| G10 | C10/C11/Q17 池/reset/cache | 欠验收：MySQL/Oracle 4.4.2.2 直连均完成 16 并发、10000 次借还；Oracle reader/LOB/cursor/XA 旧租约隔离 **52/52**；长期交错和资源稳定性仍未证实 | PhysicalSessionPool、PhysicalSession.PreparedStatements、SessionStateSql、PoolStressTests.cs | 继续完成 60 分钟交错、清池和句柄/线程/内存稳定性；状态不可证明时销毁 |
| G11 | X02/X03/X04 事务与故障 | 欠验收：MySQL/Oracle 直连 XA 生命周期、recover 和池丢弃恢复 **28/28**，Oracle 响应丢失/relay 切断 **16/16** 通过；显式XA/PSPE/unknown 的故障点仍未全覆盖 | AmbientEnlistment、Connection.Xa*、CommandExchange.Io | in-doubt/管理器与节点/代理崩溃、取消顺序、分支恢复/无重放及独立查账 |
| G12 | C05–C09/P02–P04/C12–C14 | 服务端受限，暂不开发与修复；现有路径保留 | TLS/Authentication/Discovery/压缩/OB20已有路径 | 提供真实 TLS/认证/ODP/压缩/OB20/trace/发现 profile 后再按验收标准开启；当前限制不得删除 |
| G13 | M01/Q07 元数据与目录 | 受限/欠验收：Oracle 直连基础目录、过程签名和未约束 NUMBER 回归 **18/18** 通过；package NUMBER/DATE overload 按 `OVERLOAD` 派生并执行 sync/async **4/4** 通过；引用 package 标识符派生 sync/async **2/2** 通过；未引用 BMP Unicode 字母过程名的执行与派生共用校验，中文独立过程/package 直连 sync/async **4/4**（A-03f）；中文 IN/OUT/INOUT 参数原名派生并执行及普通 DML/脚本独立读回，新增直连 sync/async **6/6**（A-03g）；普通 package-local `PL/SQL RECORD` 目录可读且公开绑定边界 sync/async **2/2** 仍是客户端能力缺口；独立探针去掉 `DATA_LEVEL=0` 后 `ALL_ARGUMENTS` 仍只有顶层行，`ALL_PLSQL_TYPES`/`ALL_PLSQL_TYPE_ATTRS` 没有字段布局；另有 `RECORD` cursor 成员使 package `INVALID` 的回归 **2/2**，Java/.NET 同为服务端限制；A-03b 的 `CHAR_LENGTH` 缺失已确认是当前目标服务端目录限制，Java 同样收到 `904/42S22` | Connection.Schema.*、RoutineParameterDerivation.cs、ResultColumn.cs、PackageLocalRecordTests.cs | 其余双模式最小权限、更多大小写/引用标识符、视图/同义词权限错误和实际列身份；普通 package-local RECORD 需字段描述和成功 Java/.NET wire；不以 `DATA_LENGTH`/`DATA_PRECISION` 伪造字符长度 |
| G14 | R01 发布/平台 | 欠验收：当前 Windows 两包五资产 hash/依赖/资产选择通过；net8/9/10 独立 runtime、两个 netstandard 的 net10 host 及 netstandard2.0 的 net48 host 共 48 项双模式包实库场景通过 | csproj、Verify-Packages.ps1、ConsumerTests | A-04 记录各 runtime 和限定场景；完整 API/ORM、非 Windows 及其他部署仍待验收，netstandard host 加载不算全平台 |
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
| E14 | Oracle named BLOB OBJECT 成员 DML | .NET 4.4.2.2 直连 flat/nested、3/90007 字节、sync/async 均到达目标 DML 后返回 `932/22000`；Connector/J 公开 BLOB writer 先失败，但同 SDK 的 RAW descriptor wire replay 发出相同 bitmap/length-encoded bytes 后也返回 `932/22000`，行数为 0 | 当前目标版本按服务端限制跳过客户端开发；若新版本/租户提供不同成员 wire，再重新比较 Java/.NET 并开启 D-04a2 |
| E15 | Oracle named CLOB/BLOB OBJECT text 结果 | .NET 预加载 named OBJECT 描述后 sync/async **2/2** 以 unsupported complex text member 安全拒绝；Connector/J 2.4.14 同查询 metadata 为 `1111/COMPLEX`、`getObject()` 为 null，`getString()` 只有无法区分成员身份的重复文本 | 当前目标版本 text payload 没有可靠成员身份，按服务端限制跳过 parser；后续版本提供稳定 type identity/NULL/locator 编码后再重开 D-04e |

Integration tests now carry these confirmed limitations as profile metadata in
`capabilities.serverLimitations`. Discovery-time skipping is limited to the named profile
and limitation; profiles without the declaration still execute the test and expose a changed
server behavior. This keeps server-limited cases visible without turning an unverified failure
into a passing client result.

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
| P2-02/P2-03/P2-04 | Q15–Q21/M02/M03；multi-send 已有可选实现子集，bulk 为排除 |
| P2-05/P2-06 | R01与所有真实验收门槛，不作为功能API |

旧完整性清单和暂停快照已删除，历史结论仅在[历史证据索引](history/README.md)中保留摘要，
不再用八包/阶段编号驱动无依据开发。关闭G/E项须按[验收标准](acceptance-criteria.md)记录证据，
失败可能揭示驱动缺陷，不能预先一概归为服务端问题。
