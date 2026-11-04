# 源码走查与功能缺失矩阵

更新：2026-11-04；本轮起点`daaafae`，新增 D-03b2 父 REF CURSOR offset 默认值传递，D-02c2 已补齐 MySQL 文本脚本标量 ReturnValue，并完成当前 mysql-direct 指定功能实库闭环（P4）；D-04j 已完成服务端边界复核，候选改动已撤销；D-08ac 已补齐 batch named OBJECT、scalar collection、nested collection BLOB/CLOB locator 输入子集，Oracle 直连 12/12。当前 `mysql-direct` 已可运行，ReturnValue 新增及相关实库回归 6/6；旧的 profile 阻塞已解除。范围与编号以[能力矩阵](obconnector-dotnet-progress.md)为准，
证据来源见[范围审计](obconnector-dotnet-scope-audit.md)。本次走查不等于重新执行所有历史实库测试。

D-04k 新增：named OBJECT/collection BLOB/CLOB locator **Input** 可与独立标量 OUT/INOUT 共存于单条 Text PL/StoredProcedure，函数可带标量 ReturnValue；显式 Prepare 与重复执行的 Oracle 4.4.2.2 `oracle-direct` 矩阵 **36/36** 通过。复杂 locator 值自身的 INOUT 仍按 D-04j 保持拒绝，不扩展其服务端边界。

D-04l 新增短 LOB 验收（BLOB 6 bytes / CLOB 7 chars）：同一 locator Input 可与独立 complex OUT/函数 ReturnValue（与输入同一 named type）、根 BLOB/CLOB OUT、REF CURSOR OUT 共存；现有共享输出计划无需新增产品代码。Oracle 4.4.2.2 `oracle-direct` 的 Text/过程/函数 × BLOB/CLOB × OBJECT/collection/nested-collection × sync/async **36/36** 通过，含错误后旧输出保留、重复执行、父 command 释放后在原事务内读取资源及提交后独立查账；复杂 locator 自身 INOUT、复杂 cursor 成员和脚本不在该范围。

D-03b2 新增：父 command 的 `UseCursorOffset=true`（正 FetchSize）现可用于已声明 REF CURSOR Output/ReturnValue。既有 reader 重载继承执行时快照，显式 bool 可覆盖；子 reader 页大小仍独立，隐藏输出行不使用 offset。Oracle 4.4.2.2 直连相关实库 **46/46**（含原显式 reader 12、父默认/覆盖24、函数2、过程生命周期8），不扩展滚动、array、标量-only 输出或 cursor-valued 列范围。

D-04m：named OBJECT/scalar collection/nested collection BLOB/CLOB locator Input 与独立标量 INOUT、同一 named type 的 complex OUT/函数 ReturnValue、根 LOB OUT、REF CURSOR OUT，现已在 mixed batch routine屏障中通过 Oracle 4.4.2.2 直连 Text/过程/函数 × sync/async **36/36**。覆盖显式 Prepare、PL错误的已执行前缀与未执行后缀、旧输出保留、原batch重复执行、batch释放后原事务内读取资源及提交后独立查账；复用已有产品代码。只关闭短LOB/调用方连接的指定组合，不代表同组PL流水化、locator自身INOUT或其他部署。

D-04f1：匿名 INTERVAL 工厂已接入现有204/205布局；`ExecuteArray` 的 INSERT/RETURNING、NULL、负值/纳秒和重复 Prepare 执行已通过当前 Oracle 4.4.2.2 直连8项正向验证。过程输出的未知精度仍安全拒绝，4项负向回归单列；普通0x17匿名绑定、PL集合输出和multi-send不由此关闭。

D-04f2：匿名INTERVAL结果描述已接入204/205，沿用嵌入精度和显式输出身份校验；本地及消费者完成，首轮PL本地显式精度9集合OUT直连4/4。扩展根NULL/错误后发布/独立事务查账的实库未完成，事故用例保持隔离且不计通过；恢复后的两模式基础检查及已知遗留表清理已完成，当前保持P3，详见路线图；未知精度255仍拒绝。

分类：**缺实现**是已保留目标中的代码缺口；**受限**是已定义子集，不能默认要求任意组合；
**候选差距**有 .NET 使用场景及数据库/协议或 ADO.NET 契约依据但未纳入当前目标；**欠验收**有代码而缺特定部署证据；
**服务端/权限阻塞**必须保留失败；**排除**不进入开发积压。

Java/JDBC 专属接口及 JVM 集成不列为候选差距、缺实现或欠验收，具体对照见
[范围决策表](obconnector-dotnet-scope-audit.md#jdbc-专属接口不进入-net-矩阵)。
G03 保留的是数据库 REF CURSOR/游标列的限定形状，不是 JDBC ResultSet 接口对齐；
G09 中未定义契约的流水化组合也不自动构成必补功能。

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
| Oracle普通CP932/UJIS/EUCJPMS | 三族严格映射、MySQL存储/结果和Oracle national已补齐（D-07f/g/h），普通Oracle初始化由Java同交互确认900/42000 | 仅按D-07f/g/h关闭成功子集，SJIS/Big5/EUCKR旧失败及其他部署仍保留 |

上述冻结不删除现有 API、拒绝校验、负向测试或 Deferred 记录；获得真实端点后再按验收标准
重新立项。

## 代码与覆盖差距

| ID | 对应能力/模式 | 分类 / 现状 | 源码证据 | 关闭条件 / 下一步 |
| --- | --- | --- | --- | --- |
| G01 | Q04 Oracle普通prepare回退 | 阶段1已补齐已有 DATE/TIMESTAMP/时区、INTERVAL、NCHAR/NVARCHAR2 字面量；`oracle-direct` User Secrets 实库正向 Prepare/输出/时间脚本复核 **38/38** 通过，但本次未触发原生 `1295/HY000`；仍受限于普通单语句输入，无输出/cursor/LOB/anonymous；命名 OBJECT/collection 构造器已复用于普通及 batch 受控 fallback（D-01b） | Internal/Sql/ClientPreparedSql.cs；CommandExchange.ClientPreparedSql | 继续补充前须独立确认字面量身份、编码/时区/精度和1295前提；不能用多语句支持替代 |
| G02 | Q05 双模式多语句 | 受限：普通输入/多结果及 MySQL、Oracle 时间脚本存储已有直连子集；Oracle 命名 OBJECT、嵌套 OBJECT 和集合输入 D-02d1～d3 已达 P4；MySQL脚本标量OUT/INOUT已按D-02c1复用变量初始化/私有SELECT/原子发布，标量ReturnValue已有 sync/async loopback 与指定 MySQL 直连闭环（D-02c2，P4）；Prepare、cursor、Oracle脚本输出及资源/复杂输出仍不支持 | OceanBaseCommand.cs；ClientPreparedSql.cs；MultiStatementTests.Temporal.cs | MySQL标量脚本输出已补齐，ReturnValue 指定 MySQL 直连验收见 D-02c2（相关实库 6/6）；其余模式/输出资源边界保留；引用类型名的递归 OBJECT text 多结果及半读切换见 D-04e2；集合子树 NULL OBJECT、locator、匿名复杂值及其余复杂结果仍保留限制；扩大前需模式/方向/wire证据，不默认补所有笛卡尔组合 |
| G03 | Q11 Oracle输出/游标 | 受限：REF CURSOR reader已补齐显式前向offset FETCH（D-03b1），首批及后续尾包均验证；父 command 的已声明 REF CURSOR 输出现可保存 offset 默认值，reader 显式 bool 可覆盖，页大小独立（D-03b2）；未声明 REF CURSOR 的输出计划与 array binding 仍拒绝；当前目标版本已验收的普通 `SELECT CURSOR` 列形状为服务端限制，Java 同样返回 902/1002 | OracleCommandPlan.cs；QueryResult.Cursor.cs；OracleCursorOwner.cs | 正FetchSize仅提示；若出现新的可读取列形状再单独验收，不能用 OUT 成功关闭当前结果列限制 |
| G04 | T14/T15 Oracle复杂值 | 受限：当前 OBJECT/嵌套 OBJECT NULL 输出身份为服务端限制且 Java 同样不报告外层 NULL；named BLOB OBJECT 成员 DML 在 .NET 与 Java 同 wire 诊断回放均返回 `932/22000`，按当前目标服务端限制跳过客户端改动；named OBJECT 标量/递归 text 结果已支持；命名OBJECT/collection的INTERVAL成员二进制输入/结果、PL OUT/INOUT与客户端构造输入已补齐指定子集（D-04f）；匿名INTERVAL工厂与ExecuteArray DML输入/RETURNING见D-04f1，PL未知精度输出仍拒绝；空格/括号/Unicode 引用类型名已补齐普通读取、多结果与 batch 受控回退（D-04e2），引号/NULL 前缀/名字自身前后空白仅有本地 parser 证据；当前 RAW/DATE/TIMESTAMP text 成员收到服务端重复 RAW payload，Connector/J 同交互也返回相同字符串并且 `getObject` 为 null，按服务端限制跳过；命名 collection 构造的 text 结果 metadata 为 `VARCHAR2`，.NET/Connector-J 均按 String 返回，不需要 complex parser；CLOB/BLOB named OBJECT text 结果在 Java `getObject` 为 null、payload 无可靠成员身份，.NET 预加载描述后 sync/async **2/2** 安全拒绝，按当前服务端 wire 限制跳过；SQL OBJECT 与 PL-only `RECORD` 的当前 cursor 成员形状均无法形成有效对象，Java/.NET 同为服务端限制；其他复杂cursor、未解析RETURNING身份仍拒绝；D-08ac 已补齐 batch named OBJECT、scalar collection、nested collection BLOB/CLOB locator input-only，Oracle 直连 12/12；D-04g已证实named BLOB成员按内容接收locator字节，a4 已在 Oracle 4.4.2.2 `oracle-direct` 以 BLOB/CLOB × OBJECT/collection/nested-collection × sync/async **12/12** 验收 constructor + hidden root LOB 自动绑定，并以显式 Prepare/PrepareAsync 与重复执行 **12/12** 关闭该子集的 statement-cache 缺口；D-04j 对 StoredProcedure 复杂 OBJECT locator InputOutput 做了 loopback 与 Oracle 4.4.2.2 sync/async **2/2** 复核，局部 OBJECT 回写 OUT bind 返回 `900/42000`，按服务端边界不开发；独立标量输出见 D-04k，独立 complex/根 LOB/REF CURSOR 输出见 D-04l，mixed batch routine屏障的同类输入与独立标量/资源输出见 D-04m（36/36），batch input-only见 D-08ac；复杂 cursor 成员及 multi-statement locator 仍不在范围 | ComplexValueCodec.cs；ComplexTypeContext.cs；NamedComplexLobBinding.cs；OracleCommandPlan.cs；OceanBaseCommand.cs | a4/D-04i 指定直连子集已关闭，named collection 内嵌 named OBJECT 的 LOB 形状已有 loopback/实库证据；后续转向仍有独立服务端或参数方向前提的项。StoredProcedure 仍是会话屏障，不承诺跨调用 statement-cache；新版本/租户提供不同 BLOB 成员或复杂 text wire 时仍重开 D-04a2/D-04e；不以属性 NULL 猜测根身份 |
| G05 | T12 Oracle UROWID | 部分：ROWID209字符串结果保持兼容；UROWID已有显式参数、NULL/type209 wire、DataTypes 和目录映射 | ResultColumn.cs:209、PreparedParameters、RoutineParameterDerivation、Schema.DataTypes | Oracle 4.4.2.2 直连已验证普通/PrepareExecute、OUT/INOUT/ReturnValue、RETURNING、坏值拒绝与恢复；UROWID真实Derive已在FLOAT编号签名验收，NUMBER/VARCHAR2目录边界见A-03；长压、代理、跨平台未验收；client text 输入已通过 CHARTOROWID 接入多语句与限定 1295 fallback，短/长/NULL、坏值/空串拒绝及恢复有直连/受控中继证据（D-05b）；原生 1295 未复现 |
| G06 | T05 MySQL GEOMETRY | 部分：type255原始byte[]结果可经Binary参数回绑；DataTypes参数支持及空间过程目录映射已补 | ResultColumn.cs:255、PreparedParameters:Binary、RoutineParameterDerivation、Schema.DataTypes | SRID=0直连输入/输出/独立存储与Binary fallback已验证；目标SRS目录仅0，非零SRID待服务端前提；无GIS领域API，进度见D-06 |
| G07 | T19/T20 两模式字符集 | 部分：GB2312、EUCKR 已按 OceanBase 4.4.2 的 collation 做严格客户端/结果映射；CP850/HP8/MACROMAN客户端/结果及SWE7仅结果的精确映射已补齐（D-07e）；CP932严格完整映射及MySQL客户端/结果、Oracle national指定子集已补齐（D-07f）；UJIS完整三字节映射及指定存储/结果成功子集已补齐（D-07g）；EUCJPMS精确变体及同范围指定成功子集已补齐（D-07h）；普通Oracle初始化限制和其他部署仍开放 | ClientCharacterSet.cs；ResultCharacterSet.Resolve；EucEncoding.cs；ClientCharacterSetTests/CharacterSetTests | MySQL GB2312/EUCKR客户端/结果、Oracle GB2312客户端及两族national指定直连子集见D-07；当前目标普通 Oracle EUCKR 初始化 900 已按 Java 同交互归为服务端限制；D-07e新增双模式客户端12、Oracle national6、MySQL结果4，共22项直连通过；D-07f新增CP932直连6通过、普通Oracle初始化2失败且Java匹配900/42000，不把声明skip算成功；D-07g新增UJIS同范围6成功、普通Oracle2失败且Java匹配；D-07h新增EUCJPMS同范围6成功、普通Oracle2失败且Java匹配；SWE7不作为客户端编码；SJIS/Big5历史失败、其他版本与部署仍保留 |
| G08 | T17/T18 专用新类型 | XMLType 已补齐当前 4.4.2.2 直连的 text+binary String wire 子集；`SYS.XMLTYPE` PL OUT/INOUT 与 Connector/J 同租户同形状均返回服务端 `600/0A000`、`600/42000`，按服务端限制跳过；SDO 已有显式 Prepare 整列读取与 generic OBJECT/ARRAY 输入子集，但没有专用 codec；ANYDATA 在当前 4.4.2.2 目标租户因 `SYS.ANYDATA` 缺失且 Java 同交互失败，标记服务端限制；MySQL VECTOR/ARRAY 复用 String，一至六层 INT ARRAY 的 NULL/空叶数组/不等长子数组已验收（D-12c1），二维顶层 [] 的参数/字面量均被 7602/22000 拒绝，其他元素/版本仍开放；RoaringBitmap 复用 Binary 读取及 SQL 函数写入，均无专用 codec 承诺 | OceanBaseDbType.cs、ResultColumn/PreparedParameters、OracleCommandPlan、Schema.DataTypes、ComplexValueCodec、CommandExchange.ComplexColumns、RoaringBitmapTests.cs、XmlProbeTests.cs | 当前版本通用路径已验收；XMLTYPE type 160 ownerless text/binary 结果按严格 UTF-8 映射为 String，`SYS.XMLTYPE` metadata 有单元边界，`DbType.Xml` 按 type 253 字符串参数写入，NULL、metadata、同步/异步 DML 与 PL 函数结果真实 Oracle 直连 **text 2/2、binary 3/3**；PL OUT/INOUT 负向回归 sync/async **4/4**，并有 Java 2.4.14 同错误证据；带 owner 的复杂 XML payload、坏值/最大值和跨版本部署仍未确认。RoaringBitmap 真实 MySQL 直连 sync/async **2/2** 覆盖 BLOB/`byte[]`、空/NULL/64 位、SQL 函数回绑和坏 raw；直接 `byte[]` prepared binding 的 `1235/0A000` 是服务端限制。ANYDATA 的 Java/.NET 同失败证据已记录；不把通用 SDO 子集或服务端转换等同专用类型 codec |
| G09 | Q20 multi-send | 部分：.NET 已有默认关闭的 prepared batch multi-send；可缓存 input-only DML/SELECT、基本 scalar 排空、MySQL BLOB/TEXT binary result materialize、时间区/国民字符集上下文、不可流水化SQL由普通command屏障隔离（D-08ab）、64 MiB 分组边界、MySQL Stream long-data 发送和 server prepare `1295/HY000` 的 client-prepared text fallback 有 loopback 证据；Oracle Stream 在该选项下安全回退到逐命令 piece-data；named OBJECT/collection 普通输入及 named complex binary 结果已有实现；batch named OBJECT、scalar collection、nested collection BLOB/CLOB locator 输入已通过 loopback/Oracle 直连 sync/async 12/12（D-08ac）；根 BLOB/CLOB locator 普通输入和保留调用方连接的结果复用已有编码/decoder；命名复杂 LOB 内容输入已复用既有 writer，保留调用方连接的命名复杂 LOB binary 结果已复用既有 decoder；Oracle DML RETURNING 标量 OUT/INOUT、命名复杂值及保留调用方连接的 LOB 输出已复用输出计划并按每条最终成功响应发布；匿名集合 binary 结果已复用普通 reader decoder（D-08s，仅 loopback）；anonymous 输入与 cursor 成员发送前拒绝；4.4.2.2 MySQL/Oracle 直连临时 profile 的 DML/scalar × sync/async **8/8 通过**，真实 MySQL Stream 存储/`GetStream` 回读 **2/2 通过**，Oracle Stream piece-data fallback 存储/LOB 读回 **2/2 通过**，已知响应错误的 prefix/error/suffix 边界 **4/4 通过**，Oracle PrepareExecute + locator Stream 安全回退 **2/2 通过**，1295 text fallback sync/async loopback **2/2 通过**，MySQL BLOB/TEXT result loopback sync/async **4/4 通过**，第一响应成功后第二响应挂起的 caller token/batch cancel/timeout **3/3 通过**；Connector/J 2.4.14 的 Oracle piece-data 路径也是每片等待确认，未提供 piece-data 流水化证据，因此 .NET 不猜测增加该协议 | Java DefaultOptions:470–490、AbstractQueryProtocol:1287–1326；`UseBatchMultiSend`、`OceanBaseBatch.MultiSend`、`CommandExchange.BatchLongData`、`CommandExchange.PieceData`、`ClientPreparedSql` | 普通 prepared DML 短时收益对照已有双模式直连 sync/async **4/4**（D-08ad，每配置5次）；流水化仍缺其他负载收益、代理/OB20、Oracle 自动提交远程 DML LOB 输出（D-08z）、complex text LOB 结果、PL 同组流水化输出、cursor/array；named OBJECT/collection 普通输入已通过精确 wire/错误边界单测及 Oracle 直连 sync/async 独立存储 **4/4**（D-08i）；普通/嵌套 OBJECT binary 结果与顶层 national/LTZ 混合读取、scalar/nonquery 排空已通过 Oracle 直连 **4/4**（D-08j），collection 结果仅有 loopback 证据；根 BLOB/CLOB locator 输入与保留调用方连接的 reader/scalar/nonquery 结果已分别通过 Oracle 直连 **4/4**（D-08k/l）；命名复杂 LOB 内容输入已通过 Oracle 直连 **8/8**（D-08m）；命名复杂 LOB binary 结果的平面/嵌套 OBJECT 已通过 Oracle 直连 **8/8**，collection 仅有 loopback 证据（D-08n）；Oracle DML RETURNING 标量 OUT/INOUT 已通过 Oracle 直连 **8/8**（D-08o）；命名复杂值及根/OBJECT LOB RETURNING 输出通过 Oracle 直连 sync 隐式/async 显式 Prepare **10/10**，collection 输出仅 loopback（D-08p）；XMLTYPE String 结果及 bound Prepare 的目录身份解析已支持（D-08q）；命名 OBJECT 标量/嵌套 text 结果已接入受控 1295 fallback（D-08r）；命名 OBJECT/collection 普通输入构造器也已接入受控 fallback（D-01b）；普通DML匿名输入/声明输出、cursor 成员、复杂 text LOB 结果仍未纳入流水化；自动提交远程 DML LOB 读取受目标版本事务边界限制（D-08z）；关闭连接 reader 的读取窗口已补齐，根远程 BLOB/CLOB 直连 **8/8**（D-08u）；未知结果的更大错误排序矩阵仍待验收；data source NonQuery 已支持普通 LOB 行无句柄排空，根远程 BLOB/CLOB 直连 **4/4**、匿名 binary LOB 仅 loopback（D-08v）；SequentialAccess reader 已复用流式字段与失败清理，双模式普通字段直连 **8/8**、Oracle LOB 窗口 **16/16**（含 Default 对照8）、XML **4/4**（含 Default 对照2），命名/匿名复杂顺序路径仅 loopback（D-08w）；整批 SingleRow/SingleResult 已保留 opt-in multi-send 与隐藏结果计数/RETURNING，双模式限读及 Oracle 隐藏输出直连 **10/10**，LOB 限读窗口/隐藏句柄排空仅 loopback（D-08x）；不和 MariaDB bulk 混淆；混合StoredProcedure/Oracle PL及SET/DDL等不可缓存SQL已支持执行屏障，安全段仍流水化（D-08ab），后段预检延迟到完整屏障响应结束；双模式存储/OUT及Oracle匿名集合OUT/时区变更直连 **10/10**（D-08y），不代表PL同组流水化；自有连接 Scalar 的根远程 LOB、只读 PL INOUT 和 inline RETURNING OUT 已支持，由 batch 保留原连接，释放全部句柄后方可重执行；直连16成功路径及8事务边界负向通过（D-08z），匿名复杂 Scalar 仅 loopback；mixed batch routine 屏障已支持显式根 REF CURSOR OUT/ReturnValue，调用方游标独立于batch寿命，自有连接保留至资源释放，全部响应排空前禁止FETCH；Oracle直连sync/async×调用方/自有连接 **4/4**，现代DLL消费 **6/6**（D-08aa）；不代表PL同组或SELECT/DML RETURNING cursor流水化 |
| G10 | C10/C11/Q17 池/reset/cache | 欠验收：MySQL/Oracle 4.4.2.2 直连均完成 16 并发、10000 次借还；Oracle reader/LOB/cursor/XA 旧租约隔离 **52/52**；长期交错和资源稳定性仍未证实 | PhysicalSessionPool、PhysicalSession.PreparedStatements、SessionStateSql、PoolStressTests.cs | 继续完成 60 分钟交错、清池和句柄/线程/内存稳定性；状态不可证明时销毁 |
| G11 | X02/X03/X04 事务与故障 | 欠验收：MySQL/Oracle 直连 XA 生命周期、recover 和池丢弃恢复 **28/28**，Oracle 响应丢失/relay 切断 **16/16** 通过；显式XA/PSPE/unknown 的故障点仍未全覆盖 | AmbientEnlistment、Connection.Xa*、CommandExchange.Io | in-doubt/管理器与节点/代理崩溃、取消顺序、分支恢复/无重放及独立查账 |
| G12 | C05–C09/P02–P04/C12–C14 | 服务端受限，暂不开发与修复；现有路径保留 | TLS/Authentication/Discovery/压缩/OB20已有路径 | 提供真实 TLS/认证/ODP/压缩/OB20/trace/发现 profile 后再按验收标准开启；当前限制不得删除 |
| G13 | M01/Q07 元数据与目录 | 受限/欠验收：Oracle 直连基础目录、过程签名和未约束 NUMBER 回归 **18/18** 通过；package NUMBER/DATE overload 按 `OVERLOAD` 派生并执行 sync/async **4/4** 通过；引用 package 标识符派生 sync/async **2/2** 通过；未引用 BMP Unicode 字母过程名的执行与派生共用校验，中文独立过程/package 直连 sync/async **4/4**（A-03f）；中文 IN/OUT/INOUT 参数原名派生并执行及普通 DML/脚本独立读回，新增直连 sync/async **6/6**（A-03g）；带空格的引用 IN/OUT/INOUT 形参已可原名派生并按位置调用，普通过程/package/函数直连 sync/async 新增 **6/6**（A-03h）；双模式函数的合成返回名已避让真实形参，exact/后缀冲突直连 sync/async 新增 **8/8**（A-03i）；仅大小写不同形参的客户端精确身份有本地证据，当前实库创建/调用失败不计为支持；普通 package-local `PL/SQL RECORD` 目录可读且公开绑定边界 sync/async **2/2** 仍是客户端能力缺口；独立探针去掉 `DATA_LEVEL=0` 后 `ALL_ARGUMENTS` 仍只有顶层行，`ALL_PLSQL_TYPES`/`ALL_PLSQL_TYPE_ATTRS` 没有字段布局；另有 `RECORD` cursor 成员使 package `INVALID` 的回归 **2/2**，Java/.NET 同为服务端限制；A-03b 的 `CHAR_LENGTH` 缺失已确认是当前目标服务端目录限制，Java 同样收到 `904/42S22` | Connection.Schema.*、RoutineParameterDerivation.cs、ResultColumn.cs、PackageLocalRecordTests.cs | 其余双模式最小权限、更多大小写/引用标识符、视图/同义词权限错误和实际列身份；普通 package-local RECORD 需可验证字段描述、输入/输出布局和成功目标服务端交互；Java 对照按需单列；不以 `DATA_LENGTH`/`DATA_PRECISION` 伪造字符长度 |
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
