# 源码走查与功能缺失矩阵

更新：2026-11-04；收敛基线 `73ebf16`。本表回答：**现在确定缺什么、下一步具体做什么、什么条件算完成**。
能力范围见[功能矩阵](obconnector-dotnet-progress.md)，来源见[范围审计](obconnector-dotnet-scope-audit.md)，
实施步骤、历史测试及提交证据统一维护在[开发清单](obconnector-dotnet-missing-features-roadmap.md)。
本次只整理范围和任务，不增加支持声明，不代表重跑数据库或完成整个驱动。

## 本轮范围和完成口径

- 目标证据基线是 OceanBase **4.4.2.2 的 MySQL/Oracle 租户兼容模式**；原生 Oracle/MySQL 的能力不能直接移入本表。
- 每个必补项须同时具备 OceanBase 文档/服务端协议/实库证据、明确的 .NET 调用契约及当前实现缺口。JDBC 接口、Java 选项和 Java 支持某种组合均不足以立项。
- **代码缺口、欠验收、服务端/环境阻塞、待范围决策、指定范围已完成分别列账。** 未证明的组合保留记录，不能报成确定缺失，也不能报成已经完成或已排除。
- G01～G15 是本轮固定核对单元。现有 D/A/S 编号只作实现和证据索引；其历史子章节数量不是未完成任务数量。不得因一个用例通过，再自动追加方向、嵌套层数、API、部署或数据类型组合。
- 新增必补范围必须先在本表写明 OceanBase 依据、使用场景、影响面及有限关闭条件，并由用户明确确认；不能边实现边扩大完成标准。

**功能开发完成**：已纳入范围的代码缺口逐项实现并通过对应测试、独立走查；尚未决定的范围必须显式保留，不能靠移出表格宣布全部完成。集成测试和长稳测试不阻塞代码完成，只在拟发布声明对应能力时作为发布证据。
**发布验收完成**：拟声明支持的版本/部署/OS/runtime 固定后，对应验收和清理全部通过；缺环境、隔离、skip/deferred 均不算该声明的通过。
两者分别报告，不以提交数、测试数或父项长期标 P2/P3 代替结论。

## 当前仍未结清的事项

| 性质 | 现有编号 | 当前缺什么 | 下一步 |
| --- | --- | --- | --- |
| 客户端代码路径已补，实库协议/权限仍待验收 | G13 / A-03d | package-local PL/SQL RECORD 已支持字段目录派生、标量字段 descriptor 和原子参数发布；当前目标仍只返回顶层行，因此继续明确拒绝 | 取得目标服务端字段行及成功输入/输出 wire 后执行 sync/async、NULL/失败和最小权限验收；没有字段行时保持明确拒绝 |
| 代码已有，扩展实库隔离 | G04 / D-04f2 | 匿名 INTERVAL 集合的根 NULL、失败后旧输出和独立事务查账未完成 | 先取得事故日志和隔离复现环境，再执行原定用例；普通连接恢复不能解封 |
| 已实现路径缺真实协议/部署证据 | G01、G09 | 原生 1295 回退；匿名及部分 collection 的 multi-send 结果只有本地证据 | 分别取得原生拒绝 profile、可复现的真实结果 metadata；不再增加同义 fixture 冒充实库 |
| 发布证据待补（不阻塞代码完成） | G10、G11、G13、G14 | 长稳、其余真实故障、RECORD 成功 wire/最小权限、特定发布声明、zero-date/长 NUL 边界 | 只有拟发布声明涉及对应能力时才执行；A-01 已移至发布验收，当前不重跑长压 |
| 服务端或部署前提阻塞 | G03、G04、G06～G08、G12、G15 | 具体失败形状、SRS/包/版本、TLS/协议/代理/认证端点 | 保留已有失败和安全拒绝；前提改变后只重开对应行 |
| 待范围决策，尚非必补功能 | G02、G04、G08、G09 | 任意 Oracle 脚本资源输出、跨语句 locator、更多 PL/游标/数组流水化、专用 XML/空间 codec 等 | 见下方逐项处置表；先决定是否纳入，不能直接开始编码 |

候选组合不能合并计为已确定的必补功能。已纳入的 RECORD 能力缺口、隔离验收、发布欠账和阻塞
仍分别保留；本次分类不代表它们已完成，不据此计算整个驱动的完成率。

## 当前服务端前提判定

当前真实配置只有 OceanBase 4.4.2.2 的 MySQL/Oracle 直连 profile，均为 TLS Disabled；没有
proxy/ODP、多节点/OCP/TNS、OB20/trace 或非 native 认证/KDC profile。下列能力依赖这些服务端
前提，当前阶段统一标记为**服务端受限，暂不开发与修复**。本地协议 fixture、Java SPI 或
单上游 relay 只能保留边界证据，不能解除冻结。

| 项目 | 当前确认 | 暂不开发的原因 |
| --- | --- | --- |
| TLS/mTLS/企业 CA/撤销/轮换 | 两个真实 profile 明确 TLS Disabled | 没有真实 TLS 端点和证书链，代码修改无法形成 OceanBase 验收证据 |
| OB20/trace/标准压缩 | 没有 OB20 profile；强制 OB20 历史失败于服务端 `Layer=mysql` | 缺少服务端协商与双向帧，不能以本地帧 fixture 代替部署能力；压缩 OB20 仍属排除项 |
| OBProxy/ODP/多节点/OCP/TNS | 没有真实代理或发现服务 profile | 单上游中继不能证明 HA、后端 reset、迁移或发现服务 |
| 非 native 认证/KDC/凭据轮换 | 当前只配置 native 账号直连 | 没有插件/KDC 正负端点，Java 插件注册不等于 OceanBase 支持 |
| Oracle普通CP932/UJIS/EUCJPMS | 三族严格映射、MySQL存储/结果和Oracle national已补齐（D-07f/g/h），普通Oracle初始化由Java同交互确认900/42000 | 仅按D-07f/g/h关闭成功子集，SJIS/Big5/EUCKR旧失败及其他部署仍保留 |

上述冻结不删除现有 API、拒绝校验、负向测试或 Deferred 记录；获得真实端点后再按验收标准
重新立项。

<a id="bounded-gap-matrix"></a>

## 固定缺口、处理动作与关闭条件（G01～G15）

“指定范围已完成”只覆盖行内列出的现有能力；同一行的候选、阻塞和欠验收仍单列，不能相互抵消。

| ID / 索引 | 现状与确切剩余项 | 具体处理动作 | 有限关闭条件 |
| --- | --- | --- | --- |
| G01 / D-01、D-05b、A-05 | Oracle 普通输入及命名复杂输入的受控 fallback 已实现；**欠验收：租户原生 1295**，不是继续补所有输出组合 | 提供能原生返回 1295 的目标 SQL/profile；复用现有参数和存储回读用例，只在出现真实客户端缺陷时修码 | sync/async 实际触发原生拒绝后回退；值/NULL、精度/编码及独立存储一致，失败无误重放；没有该 profile 就保留未验收 |
| G02 / D-02 | **指定子集已完成**：双模式已列明输入/多结果、MySQL 标量脚本 OUT/INOUT/ReturnValue；**待范围决策**：Oracle 任意脚本输出、资源复杂输出及跨语句 locator；脚本 Prepare/cursor 在当前服务端被拒绝 | 保留现有脚本拒绝；候选先按下表决定契约。服务端失败只在版本/可接受 SQL 改变后复核，不以 DbBatch 或单 PL 块冒充脚本支持 | 现有子集按 D-02a1/a2/c1/c2/d1～d3 的 P4 证据结账；未纳入组合不追加成必过项，也不宣称已支持 |
| G03 / D-03 | **指定子集已完成**：REF CURSOR 输出、前向 offset 及父默认值；**服务端限制**：SELECT CURSOR 列当前在 execute/FETCH 失败 | 保留现有列形状失败和发送前保护；只有出现目标 OceanBase 成功列 wire 才重开 child FETCH/释放验证；JDBC 滚动/可更新 ResultSet 不纳入 | D-03b1/b2/c 已验收范围保持完成；列形状须新实库成功证据，不能用 REF CURSOR OUT 通过替代 |
| G04 / D-04、D-04f2 | named OBJECT/collection、LOB locator Input 与独立输出及 INTERVAL 指定子集已有 P4；**欠验收且隔离**：D-04f2；根 NULL 身份、特定 BLOB DML/text/cursor 成员及 locator 自身 INOUT 有服务端失败；其他组合尚未纳入 | D-04f2 先查事故日志并在隔离环境定位，再执行原定根 NULL、空集合、错误后旧输出、事务内写入/提交后独立查账；其余已知失败不循环探测 | 原定用例 sync/async、隐式/显式 Prepare 通过且主体/清理错误均可见、对象清理成功后才能关闭 D-04f2；事故前提未齐就保持隔离，P4 子集不重复开发 |
| G05 / D-05、D-05b | **指定范围已完成**：UROWID 普通/PrepareExecute、NULL、OUT/INOUT/ReturnValue/RETURNING 及已列明 text 输入 | 无新增功能开发；原生 fallback 只随 G01 验收，跨部署随 G12/G14，不能重复挂任务 | 保留现有 P4 及受控中继证据边界；没有新失败不重开该功能 |
| G06 / D-06 | **指定范围已完成**：SRID=0 GEOMETRY Binary/目录；**服务端前提缺失**：非零 SRS | 提供实际含非零 SRS 且可往返的 OceanBase profile，再用原始 SRID+WKB 参数/结果/NULL/坏值独立读回；不在测试中改服务端 SRS | 指定非零 SRS 的 sync/async 独立存储结果一致或记录明确服务端拒绝；专用 GIS API 不在当前必补范围 |
| G07 / D-07、S-05 | **清单内映射指定子集已完成**；普通 Oracle 部分字符集初始化及其他部署有已记录失败 | 仅在服务端初始化/枚举改变后复核对应失败名称；严格 codec 不用相近 code page 替代，不自动扩大字符集名单 | D-07～h 的成功/失败结论分别保留；当前没有一个可直接新增的已确认名称，其他部署验收按实际 profile 单列 |
| G08 / D-09～D-12 | **指定通用路径已完成**：XML/String、SDO/generic OBJECT、VECTOR/ARRAY/String、RoaringBitmap/Binary+SQL；ANYDATA/SPARSEVECTOR 及部分形状当前服务端失败；完整 XML/空间专用 codec 未纳入 | 缺包/版本的只等待对应前提；额外 codec 必须先说明现有通用路径不能满足的 .NET 用例和真实 wire，不凭类型名追加实现 | 当前已验收通用子集按 P4 结账；阻塞保持可见；专用 codec 和其他嵌套未决不算已完成或确定代码欠账 |
| G09 / D-08 | **指定 opt-in multi-send 子集已完成**；**欠实库证据**：匿名和部分 collection 结果；**待范围决策**：PL 同组、cursor/array binding、更多复杂 text LOB 组合 | 先取得真实匿名/collection 结果 metadata 来源，再复用既有 decoder 验证正常/NULL/空、顺序/错误、LOB 释放和独立结果；没有来源不继续造 fixture。额外组合先走范围决策 | 只对已实现且有真实形状的结果补一次 sync/async 验收；现有 DML/LOB/屏障等 P4 不重跑扩组；未决定的流水化组合不能阻止已验收子集结账 |
| G10 / A-01 | **发布欠验收**：16 并发/10000 借还及资源隔离已有；一小时稳定性未通过，用户已延期 | 到发布验收时按现有 PoolSoak 固定配置执行每模式 60 分钟；保留原始失败、资源曲线与清理结果，先定位再决定是否重跑 | 池上限、旧资源隔离和停止后资源/自有对象清理均满足 acceptance-criteria；短跑和最后 SELECT 成功不替代一小时结果 |
| G11 / A-02 | XA 生命周期、已列明响应丢失、决定落盘前/后 worker 终止及 MySQL PREPARE 丢失已验收；**欠证据**：其余真实取消/超时顺序、节点/ODP 失联与恢复 | 使用受控且有权限的故障 profile，先固定断点/预期 unknown/Recover/独立查账用例清单，再执行；只处理自有 XID，不再自动枚举组合。通用持久协调器仍排除 | 固定清单逐例证明结果分类、无重放、旧资源拒绝、独立恢复与清理；缺节点/ODP 环境保持欠验收，不能称已完成全部故障覆盖 |
| G12 / S-01～S-03 | **部署欠验收/阻塞**：TLS/mTLS、OB20/trace/标准压缩、真实 ODP/发现/多节点 | 分别提供证书正负端点、实际协商/双向帧及 trace/collector、真实代理/发现 profile；按开发清单 S 表执行，禁用降级和单上游 relay 替代 | 只关闭已取得端点的那一格部署；压缩 OB20、透明迁移等原排除项不改成目标，也不宣称未配置即服务端不支持 |
| G13 / A-03、A-03d | **代码路径已实现**：package-local RECORD 读取字段行、构造 scalar descriptor、派生并原子发布；**欠验收**：当前服务端没有字段行、真实输入/输出 wire、最小权限及目录权限边界；CHAR_LENGTH 缺列等为已记录服务端限制 | 代码复用现有 complex descriptor/绑定和原子发布；字段缺失、嵌套/重复/乱序/不支持类型在发送前拒绝；取得真实字段和 wire 后只补定向 sync/async、NULL、失败与权限验收，禁止伪造 owner、字段或字符长度 | 真实字段行和成功输入/输出 wire 下，RECORD 参数派生/绑定、NULL/错误不发布及 sync/async 全部通过才算实库支持；当前目标无字段行时的安全拒绝已由边界和单元测试覆盖。权限用例结果与账号实际授权一致，不能由管理员通过替代 |
| G14 / A-04 | Windows 包/五资产及既有 runtime 子集已验收；.NET Core/.NET 已支持从 Windows 交叉发布 Linux；Linux 实机数据库验收只在发布明确承诺 Linux runtime 行为时需要 | 用 Windows 完成五资产构建、包消费者和 `dotnet publish` 资产检查；只有声明 Linux 实机/特定 ORM 行为时再补对应实机报告 | 当前发布范围按托管 .NET API 和交叉发布结果结账；不把 Linux 实机数据库测试当作代码开发前置条件 |
| G15 / S-04 | **待服务端支持证据和端点**：非 native 插件/KDC/凭据轮换；Java SPI 存在不足以证明目标 OceanBase 提供该认证 | 先确定目标 OceanBase 版本实际提供的插件/机制并配置正负认证、轮换端点，再测试现有认证路径；没有依据不新增插件 API | 实际支持的机制完成正负认证/轮换/脱敏及对应平台验证；未确认机制保持待证据，不算确定功能缺口或已排除 |

A-05 的 zero-date 需要专用允许零日期的 SQL_MODE profile；不改租户基线。长 NUL 需要失败时段的执行/网络诊断，
不反复重放长 SQL。两者仍是边界验收欠账，分别保留 G01/G11 的故障及原始 A-05 记录，不能报成两个新功能。

<a id="scope-decisions"></a>

## 尚未纳入必补范围的组合：先决策，后开发

| 现有索引 | 未决定的能力 | 纳入前必须拿出的证据/方案 | 当前动作 |
| --- | --- | --- | --- |
| D-02b/c | 脚本级 Prepare/cursor、Oracle 任意脚本 OUT/INOUT/复杂资源输出 | 目标 OceanBase 能接受的脚本/协议；明确输出位置、完整成功才发布、失败前缀及取消后的资源归属；单 PL 块/多条 DbBatch 不替代 | 保留拒绝和当前失败，未纳入；拿到证据并经用户确认才写有限实现项 |
| D-04 的其他方向 | multi-statement locator、复杂 locator 自身 INOUT、未证实 cursor/匿名成员形状 | 成功 wire、类型身份/NULL 表示、LOB/cursor 所有权及释放；当前 900/932 等失败形状需新服务端依据 | 保留已验收 Input/独立输出子集，不自动补其所有排列 |
| D-08 其他组合 | 普通匿名输入/声明输出、PL 同组流水化、cursor/array binding、复杂 text LOB | 单条普通命令成功协议、组响应顺序/错误归属、资源生命周期及可复现 .NET 用例；ExecuteArray 不代替普通输入 | 现有 guard/屏障保留；不得把 Java 可选优化或 API 的存在当成 OceanBase 必补契约 |
| D-09/D-10/D-12 额外范围 | 完整 XML codec、专用 SDO WKB/WKT/SRID API、稀疏向量及更多数组嵌套 | 明确现有 String/Binary/OBJECT 路径不能解决的使用场景、目标版本实际支持和参数/结果 wire | 通用路径按现有证据支持；候选不计入必补或完成率 |

上述“未纳入”保留范围决策欠账，并非将原问题宣布解决；用户确认纳入后只能修改对应现有行，
同时固定用例及关闭条件，不能由实现 agent 自行派生一串新的常驻子任务。

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
