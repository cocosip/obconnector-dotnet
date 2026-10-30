# OceanBase .NET 缺失功能开发清单

更新：2026-10-30。本文以当前源码及范围审计固定的 OceanBase 官方文档、Connector/J 2.4.14
静态对照为依据。服务端状态沿用兼容性报告，除执行记录明确列出的复验外，不代表本日重跑。
它回答三个问题：

1. 参考 Java 驱动的能力，哪些确实存在，哪些只是名称相近；
2. OceanBase 服务端是否有对应能力，还是当前环境没有前提；
3. .NET 当前缺什么、下一步做什么、什么证据可以关闭任务。

本文是后续开发的**唯一跟踪入口**。以后新增任务、状态变化、服务端阻塞、实现结果和关闭证据
都直接补充在本文，不再为同一批缺失功能拆分新的任务文档。能力矩阵、缺失矩阵、兼容性记录和
验收标准只作为本文的依据，不承担开发进度记录。

## 状态定义

| 状态 | 含义 |
| --- | --- |
| `部分实现` | 已有可用代码，但只覆盖明确子集；扩大范围前需要单独任务和测试。 |
| `代码缺失` | 当前目标中需要的能力没有 .NET 产品入口或实现。 |
| `待证据` | 需要先确认 Java wire、OceanBase 版本或真实服务端行为，不能直接写代码。 |
| `待服务端` | 客户端路径已有或可保留，但当前没有对应端点、协议协商或权限，暂不开发。 |
| `待验收` | 代码已有，缺真实部署、长期压力、故障或平台证据。 |
| `排除` | 明确不属于本产品范围，不建立开发任务。 |

进度采用 `P0` 到 `P5`：

| 进度 | 关闭含义 |
| --- | --- |
| `P0` | 未进入实现；尚未开始或正在补齐范围、版本、wire 和服务端证据。 |
| `P1` | 范围、版本、wire 和服务端前提已确认，可以进入实现或验收准备。 |
| `P2` | 已有本地协议/API实现或测试，仍有代码范围缺口。 |
| `P3` | 代码和本地/消费者测试完成，尚未完成目标实库验收。 |
| `P4` | 指定服务端 profile 的功能验收完成，仍可能缺长期压力或跨平台证据。 |
| `P5` | 代码、文档、测试、部署矩阵和关闭证据全部齐全。 |

## Java、OceanBase 与 .NET 的确认结果

### 已有受限能力的 Java 基线

下表回答“Java 是否自带”，不把 Java 的某条路径等同于所有组合都可用。对应源码入口和固定
版本见[Java 结构分析](obconnector-j-architecture-outline.md) M06～M10 及[范围审计](obconnector-dotnet-scope-audit.md) J1～J6。

| 本文任务 | Java 2.4.14 已有能力 | 仍需独立确认的部分 |
| --- | --- | --- |
| D-01 | client/server prepare、`emulateUnsupportedPstmts` 回退和模式专用参数字面量 | .NET 的精确 1295/HY000、安全边界和每种字面量仍按自身契约验收 |
| D-02 | 多语句、多结果、客户端参数展开及多种 batch 路径 | 不代表多语句可任意混用 Prepare、cursor、OUT 或复杂输入 |
| D-03 | cursor fetch、REF CURSOR、标量 OUT/INOUT、带条件的 offset | 不代表 SELECT CURSOR 列形状、OUT offset 或 REF CURSOR 输入都支持 |
| D-04 | ObStruct/ObArray、复杂描述、LOB/cursor 读取及限定 writer | 不代表所有 named/anonymous 成员和 locator 回绑都已实现 |
| D-05 | ROWID 类型 209 的字符串结果路径 | 不能据此认定 UROWID 参数/结果已闭环 |
| D-06 | MySQL GEOMETRY 类型 255 的原始 byte[] 结果路径 | 不能据此认定专用输入、SRID 契约或完整 GIS API 已有 |
| D-07 | 字符集名称到 JVM charset 的选择、普通/national 编码路径 | 不提供 OceanBase 新增 collation 和 .NET 严格映射的独立互操作证明 |

### Q20 multi-send/pipelining

这是 Java Connector/J 2.4.14 的真实可选能力，不是旧矩阵臆测。Java 依据是
`DefaultOptions.java:470–490,553–560` 和 `AbstractQueryProtocol.java:729–732,1287–1326`，
对应的功能是把多个 prepared 执行组织成多发送路径；它与 MariaDB
`COM_STMT_BULK_EXECUTE` 不同。当前 .NET 没有选项、协议入口或执行队列，因此属于**代码缺失**。

OceanBase 服务端是否在目标版本、目标模式和目标连接路径上能正确处理该路径，当前没有真实
profile 证据。因此它的状态是 `P0 / 代码缺失 + 待证据`，满足前提后再进入实现。

### T17 XMLType、SDO_GEOMETRY、ANYDATA

这三项属于 OceanBase Oracle 兼容模式的服务端专用类型或 PL 能力，官方文档分别有独立说明，
所以不能写成“服务端不存在”。但它们的服务端存在不等于 Connector/J 或 .NET 已有通用值 codec：

| 类型 | OceanBase 判断 | Java 2.4.14 判断 | .NET 判断 |
| --- | --- | --- | --- |
| XMLType | 服务端 Oracle 模式条件能力 | 没有确认到通用 XMLType wire/value codec；Java 的 SQLXML 路径明确拒绝 | 没有专用 codec；当前只能使用服务端转换成已支持标量 |
| SDO_GEOMETRY | 服务端 Oracle 模式条件空间类型 | 没有确认到通用 SDO 专用 codec | 没有专用 codec、空间 API 或 SRID/WKB 契约 |
| ANYDATA | 服务端 PL/系统类型，有独立限制，不能当普通持久列 | 没有确认到普通列通用 codec | 没有专用 codec；必须先确定是 PL 参数/结果还是持久列需求 |

因此 T17 要加入开发清单，但第一步不是直接实现，而是先固定目标版本、可用形状、参数/结果
方向和 wire。当前状态是 `P0 / 待证据`。官方和 Java 范围依据见[范围审计](obconnector-dotnet-scope-audit.md)。

### T18 VECTOR、RoaringBitmap、MySQL ARRAY

T18 原来的名称混合了三类不同能力，必须拆开：

| 名称 | 判断 | 是否新建 .NET 任务 |
| --- | --- | --- |
| Oracle ARRAY/collection | Java 有 `ObArray`/`ArrayImpl` 和有前提的 array binding；.NET 已有 T14/Q16 的命名/匿名数组和 DML array binding 子集 | 不新建重复任务，归入 D-04 扩大现有复杂值范围 |
| MySQL VECTOR/SPARSEVECTOR | 官方有向量类型和列 DDL；稀疏向量从 V4.3.5 BP2 开始支持 | 纳入 D-12a，先确认目标租户及参数/结果 wire，再决定专用 codec |
| MySQL RoaringBitmap | 官方明确提供存储/处理无符号整数集合的高效压缩位图类型 | 纳入 D-12b；不能用现有 BIT(1..64) 支持代替 |
| MySQL ARRAY | 官方明确支持 `ARRAY(INT)` / `INT[]` 列及受限嵌套，不是 Oracle collection 或 DML 数组绑定 | 纳入 D-12c；当前 .NET 没有专用 MySQL ARRAY 契约 |

**T17 和 T18 都有 OceanBase 服务端功能依据，应纳入本清单。** T18 不是“Java 已完整支持但
.NET 漏写”：Java 固定版的 `ColumnType.ARRAY(161)` 明确属于 Oracle 模式，不能证明其已实现
MySQL ARRAY；未确认该 Java 版本有上述三种 MySQL 类型的专用 codec。当前 .NET 的 Oracle
ARRAY 已有部分实现，三种 MySQL 类型则缺专用契约及独立验收，不能从既有标量或复杂值解码推断支持。

本日已在线复核同一固定文档提交的[向量][T18-vector]、[位图][T18-bitmap]、[数组概述][T18-array]
和[数组列 DDL][T18-array-ddl]，后者列出支持元素和最多六层嵌套。
这确认了服务端功能存在，尚未证明当前两个 4.4.2.2 租户支持所有形状，也未确定 binary 参数/结果的类型码。

## 开发任务列表

下面的任务只列当前仍有开发价值的缺口。每项都包含边界，避免把所有输入、方向、模式和协议
组合一次性扩成不可验收的笛卡尔积。

### 第一阶段：补齐已有能力的受限组合

| ID | 当前进度 | 任务 | 需要修改/核对 | 前置条件 | 关闭条件 |
| --- | --- | --- | --- | --- | --- |
| D-01 | `P3`，代码提交 `cca74fb` | Oracle 普通 client-prepare fallback（G01/Q04；本轮本地代码模块完成） | 复用已有 Oracle 时间/INTERVAL/NCHAR 字面量；修复 national 无用 binary 编码，sync/async 一致 | 完整本地门禁和独立走查已通过；原生 1295、字面量身份、编码、时区和精度的目标实库仍待验收 | 原生 1295 profile 下存储后独立读回再进入 `P4`；不含 OUT/cursor/复杂值，不以 `P3` 宣称全部组合完成 |
| D-02 | `P2`；a1、d1 达到 `P4` | 扩大双模式多语句参数和多结果（G02/Q05） | `OceanBaseCommand`、SQL 边界扫描、参数展开和结果消费 | 保持 SQL 总长度、引号、NUL、流和失败后会话状态边界；本轮长 NUL 失败保留 | 每个支持模式/方向/类型组合有结果、失败前缀和后缀断言；Prepare、cursor、OUT 和 d1 以外的复杂输入继续明确拒绝或单独关闭 |
| D-03 | `P2` | Oracle cursor/输出形状（G03/Q10/Q11） | `OracleCommandPlan`、`QueryResult.Cursor`、`OracleCursorOwner`、Fetch/offset | 已有四字节 cursor/OUT 子集；扩大范围需要真实支持的 cursor 列和 OUT wire；不能用 PL OUT 替代 SELECT CURSOR 列 | 普通 cursor、REF CURSOR OUT、offset、释放和半读分别通过；服务端明确拒绝的形状保留失败 |
| D-04 | `P2` | Oracle OBJECT/collection/复杂 LOB/cursor 成员（G04/T14/T15/Q16） | `ComplexValueCodec`、`ComplexTypeContext`、locator 生命周期、RETURNING 身份 | 先按 named/anonymous、输入/输出、普通列/PL、LOB/cursor 成员拆矩阵 | 支持的成员方向、NULL、嵌套、locator 失效、DDL/reset 和旧资源释放均有证据；未知身份不误解码 |
| D-05 | `P2` | UROWID 和 ROWID 参数/结果闭环（G05/T12） | `ResultColumn`、`PreparedParameters`、`DataTypes`、绑定和存储读取 | 需要真实 UROWID 值、参数 wire、列 metadata；当前只有 ROWID 结果路径 | UROWID 输入、输出、普通列、metadata、NULL/边界和独立读回通过；不支持形状发送前拒绝 |
| D-06 | `P2` | GEOMETRY 输入和结果边界（G06/T05） | 类型推断、参数绑定、结果 metadata、原始 bytes/SRID 行为 | 需要真实 MySQL GEOMETRY 列、值格式和版本约束；领域空间 API 另行评估 | 原始字节和 SRID 行为有明确契约；参数/结果/NULL/坏值有测试；若无稳定 wire，只保留结果-only |
| D-07 | `P2` | 字符集和 national/result 映射（G07/T19/T20） | `ClientCharacterSet`、`ResultCharacterSet`、strict codec、collation 表 | 每个新名称需要服务端枚举、.NET 严格映射和真实 roundtrip；不能用相近 code page 伪造 | GB2312/EUCKR 完成真实租户验收；UJIS 等逐项确认后再加入，不支持名称继续发送前拒绝 |

### 第二阶段：参考 Java 的候选实现

| ID | 当前进度 | 任务 | Java/OceanBase 边界 | 前置条件 | 关闭条件 |
| --- | --- | --- | --- | --- | --- |
| D-08 | `P0` | multi-send/pipelining（Q20） | Java 有实现；MariaDB bulk 不在范围；OceanBase 目标版本和错误排序尚未证实 | 目标 OB 版本、MySQL/Oracle 模式 profile、协议抓包/收益基线、取消和 unknown-outcome 规则 | 先完成证据任务进入 `P1`；实现须有 opt-in 选项和单会话所有权；正常/错误/取消/超时/顺序和吞吐对照通过；失败不自动重放 |
| D-09 | `P0` | XMLType wire 调查与有条件 codec（T17） | 服务端有条件能力；Java 没有确认的通用 SQLXML/XMLType codec | 明确是 PL 参数/输出、普通列还是服务端转换；确认 wire 类型和空值/字符编码 | 先记录可实现形状或阻塞结论；实现时参数、结果、NULL、坏值、版本拒绝和 metadata 一致 |
| D-10 | `P0` | SDO_GEOMETRY wire 调查与有条件 codec（T17） | 服务端有条件空间类型；Java 没有确认的通用 SDO codec | 目标版本、wire、SRID、WKB/文本表示和安全大小上限 | 先记录可实现形状或阻塞结论；实现时输入/结果/NULL/SRID/非法几何有明确行为；不把 GEOMETRY bytes 冒充 SDO |
| D-11 | `P0` | ANYDATA PL 形状调查与有条件 codec（T17） | 服务端 PL/系统类型，不能默认作为普通持久列；Java 没有确认普通列 codec | 先确定 PL 参数/返回值形状和可枚举内部类型；确认是否值得提供 public API | 先记录可实现形状或阻塞结论；实现仅限明确的 PL 形状；内部未知类型拒绝，不增加任意对象兜底 |
| D-12 | `P0`（官方功能已核实） | MySQL VECTOR/RoaringBitmap/ARRAY 的 wire 与 codec（T18；子项见下） | 服务端功能存在；目标部署、Java 专用路径和 wire 尚未验收；Oracle ARRAY 归 D-04 | 锁定 OB 版本、模式、DDL、列/参数 wire、Java 路径和真实 profile | 每个子项分别记录 `支持/服务端拒绝/版本条件/无证据`；专用 API/codec 仅实现证据成立的形状 |

D-12 分为以下三个子任务，状态都维护在本文，父项只有在所有子项结论闭合后才能关闭：

| 子项 | 进度/已确认 | 具体开发与验证范围 | 当前缺口 |
| --- | --- | --- | --- |
| D-12a VECTOR/SPARSEVECTOR | `P0`；官方类型、列 DDL、NaN/Inf 限制和稀疏向量版本已核实 | 确认维度/元素精度、NULL、空值、坏值、text/binary 列身份；评估显式 provider 类型、绑定、结果及存储独立读回；稠密/稀疏分开 | 目标 profile、参数/结果 wire、大小预算及 Java 专用路径待确认 |
| D-12b RoaringBitmap | `P0`；官方无符号整数集合与 64 位能力已核实 | 确认列 DDL、输入/输出格式、整数边界、集合语义、NULL/空集合/坏数据；确定是否暴露原始 bytes 或 provider 值并补独立读回 | 目标 profile、序列化版本、wire 类型与严格校验待确认；不能复用 BIT 数值契约 |
| D-12c MySQL ARRAY | `P0`；官方 ARRAY/[] 列、元素子集、最多六层嵌套和无默认值限制已核实 | 确认元素类型/NULL/空数组/嵌套、参数/结果 metadata、text/binary；定义独立于 Oracle ARRAY 的绑定和解码，补存储及拒绝边界 | 目标 profile、类型描述/元素 wire、Java 专用路径待确认；不能用 Oracle 类型码 161 代替 |

### 第三阶段：已有代码的部署和故障验收

| ID | 当前进度 | 任务 | 当前缺口 | 关闭条件 |
| --- | --- | --- | --- | --- |
| A-01 | `P3` | 池、reset、租约和 prepared cache（G10） | 长期并发、资源交错、清池、句柄/线程/内存稳定性未证实 | 16 并发调用者、池上限、至少 10000 次借还、60 分钟交错运行；reader/LOB/cursor/XA 旧资源不污染新租约 |
| A-02 | `P3` | PSPE、XA、unknown outcome（G11） | in-doubt、协调器、节点/代理崩溃、取消顺序和分支恢复未全覆盖 | 发送前/发送中/响应中故障、独立查账、XA recover/决定后恢复、无自动重放全部有记录 |
| A-03 | `P3` | Schema、目录和参数元数据（G13） | 包内 PL 类型、重载、大小写、引用标识符和最小权限边界 | 双模式最小权限、唯一/歧义签名、包/重载、视图/同义词权限错误均按服务端真实结果关闭 |
| A-04 | `P3` | 包、TFM 和平台（G14/R01） | 当前有五资产和 Windows 子集；非 Windows、独立 runtime、完整 API/ORM 未验收 | 每个声明支持的 OS/runtime/资产都有消费者、构建和目标实库记录；netstandard host 加载不能替代独立 runtime |
| A-05 | `P3`，长 NUL 复核未关闭 | 原生 prepare fallback、zero-date、极限 TIME 和长 NUL 失败复核 | 当前包含中继注入、SQL_MODE 前提和服务端边界；本轮 196608 字符 NUL 单表达式 .NET/Java 均失败，详见 D-02a1 | 原生目标 profile、SQL_MODE、确切长度和 text/binary 路径分别记录；失败不改写成成功 |

### 原缺失矩阵编号映射

这张表保留旧编号和新任务之间的关系，避免开发过程中出现“任务已拆分但原矩阵仍显示未处理”的情况。

| 原编号 | 新任务 | 说明 |
| --- | --- | --- |
| G01 | D-01 | Oracle 普通 client-prepare fallback |
| G02 | D-02 | 双模式多语句和多结果 |
| G03 | D-03 | Oracle 输出、cursor 和 offset |
| G04 | D-04 | Oracle OBJECT/collection/复杂 LOB/cursor 成员 |
| G05 | D-05 | UROWID/ROWID 闭环 |
| G06 | D-06 | MySQL GEOMETRY |
| G07 | D-07、S-05 | 字符集本地映射和真实服务端 roundtrip 分开追踪 |
| G08 | D-09、D-10、D-11、D-12 | T17 专用类型和 T18 版本相关类型先拆证据任务 |
| G09 | D-08 | Java multi-send，当前 .NET 代码缺失 |
| G10 | A-01 | 池/reset/cache 长期验收 |
| G11 | A-02 | 事务、XA、unknown outcome 故障验收 |
| G12 | S-01、S-02、S-03、S-05 | TLS、协议扩展、发现/HA、字符集服务端前提 |
| G13 | A-03 | 元数据和目录边界 |
| G14 | A-04 | 包、TFM 和平台 |
| G15 | S-04 | 非 native 认证、KDC 和凭据轮换 |

## 服务端前提任务：暂不进入代码开发

这些项目的客户端路径和拒绝保护应保留，但当前环境没有足够证据支持继续改代码。

| ID | 能力 | 当前状态 | 重新开启条件 |
| --- | --- | --- | --- |
| S-01 | TLS/mTLS/企业 CA/撤销/轮换 | 当前两个真实 profile 都是 TLS Disabled | 真实证书链、主机名、客户端证书、撤销和轮换正负端点 |
| S-02 | OB20/Full Link Trace/标准压缩 | 当前只有普通协议回退；强制 OB20 因 `Layer=mysql` 失败 | 真实 OB20/trace/压缩 profile、双向帧和服务器采样/collector证据 |
| S-03 | OBProxy/ODP/多节点/OCP/TNS/后端迁移 | 只有本地 parser、单上游 relay 和受控路由 fixture | 真实代理/发现服务、多节点、后端 reset、路由和迁移 profile |
| S-04 | 非 native 认证/KDC/凭据轮换 | 当前只有 native 账号直连 | 服务端插件/KDC 正负认证、平台协商和实际轮换端点 |
| S-05 | 剩余新字符集 | MySQL SJIS/Big5 有专项；Oracle SJIS/Big5 仍失败，其余新名称无 profile | 逐项服务端 collation、严格映射和两模式 roundtrip |

S-01 至 S-05 不是“永远不支持”，也不是可以用本地 fixture、Java SPI 或单上游中继关闭的
代码任务。当前未关闭的失败或前提还包括 Oracle nested-table 列、`SELECT CURSOR` 列、
VIEW/SYNONYM 权限、二进制非空地区名 TZ、MySQL zero-date 的 SQL_MODE 前提，以及尚未复现的
原生 1295；应分别保留失败、阻塞或 deferred 状态，不能删除用例。

## 明确排除

以下不进入开发进度：原生 Oracle Net/OCI wire、`LONG/LONG RAW/BFILE/NCLOB`、Aurora/Galera、
MariaDB bulk protocol、压缩 OB20/proxy reroute、透明活动会话迁移和未知写自动重放、分布式
ambient 提升/通用持久协调器、REF CURSOR 输入、DataAdapter/CommandBuilder，以及没有定义
语义的“其他资源等待”。它们分别是官方限制、参考协议不足或当前产品范围选择，不是遗漏的任务。

## 开发顺序与当前总进度

建议顺序如下：

1. **先审查 D-01～D-07**：这些是当前已有代码的受限组合；按各任务前提区分能直接修复的代码缺陷与需要实库/wire 的扩展，不为清空拒绝列表而盲目实现；
2. **并行做 D-08 的证据准备和 D-09～D-12 的服务端/wire调查**：先确认是否值得实现，再建立 public API；
3. **完成 A-01～A-05 的验收**：这些不一定增加功能，但决定现有功能能否进入支持声明；
4. **只有 S-01～S-05 获得真实前提后，才开启对应协议、认证、发现和字符集开发**。

当前整体状态为：

| 类别 | 数量 | 当前结论 |
| --- | ---: | --- |
| 已有代码但组合受限 | 7 项（D-01～D-07） | 可以按任务逐项推进 |
| 明确代码缺失/候选 | 5 项（D-08～D-12） | 先证据，不能直接承诺完成 |
| 已实现但欠验收 | 5 项（A-01～A-05） | 需要真实部署、故障、压力或平台证据 |
| 服务端前提阻塞 | 5 项（S-01～S-05） | 暂停代码开发，保留 Deferred/失败 |
| 范围排除 | 一组 | 不计入完成率和开发积压 |

任务达到 `P5` 的最低要求是：源码行为、公开 API、负向边界、单元/消费者测试、目标 profile
验收、清理结果和文档状态一致；`skip` 或 `deferred` 不计为完成。

## 执行记录与下一步

每完成一个代码模块，先测试、独立代码走查，处理问题后提交；在本节记录修改、证据、提交和
未关闭边界，再更新上方任务进度。全部记录留在本文。

### D-01：Oracle 普通 prepare 回退的 national 编码修复（2026-10-30）

本轮源码基线 `68d186b`；DATE/TIMESTAMP/TZ/LTZ、INTERVAL、NCHAR/NVARCHAR2 的既有
字面量扩展已在 `96c38d5`，本轮不重复开发。走查发现普通回退已经生成 `u'...'` Unicode SQL，
却仍执行不再使用的 national binary 参数编码，导致 unsupported 编码或 ASCII/latin1 不可表示值
在发送 SQL 前报错。现在 sync/async 都仅在实际 server-prepared / Oracle PrepareExecute 路径
重编码，保留这些二进制执行路径的严格校验；公开 API 和 opt-in 默认行为不变。

| 检查点 | 本轮证据/状态 |
| --- | --- |
| 回归复现 | 新增 8 个 sync/async NChar/NVarchar2 用例；修复前全部因预期的 NotSupportedException/EncoderFallbackException 失败 |
| 修复验证 | 同 8 个用例修复后全部通过；检查 exact 1295/HY000 后发送的完整 Unicode SQL、重复执行两次和连接复用 |
| 五资产 Release 构建 | `dotnet build OceanBaseConnector.slnx -c Release --no-restore`，0 警告/0 错误 |
| 完整默认单测 | 8644/8644 通过，0 失败/0 跳过（`dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-build`） |
| 完整公开 DLL 消费者 | 3549/3549 通过，0 失败/0 跳过（`dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-build`）；netstandard 资产由 .NET 10 host 加载，不等于独立 runtime |
| DI | 14/14 通过 |
| Acceptance 入口 | `--list` 成功；只证明 case 清单，TLS/auth deferred 保留，没有执行实库验收 |
| 独立代码走查 | 复查无剩余重要或次要问题；独立专项 308/308 通过，覆盖 fallback、national binary、KeyInfo 和 PrepareExecute |
| 限定源码格式 | 两个修改的 C# 文件通过 `dotnet format whitespace ... --verify-no-changes --no-restore`；不是全方案 analyzer 声明 |
| 代码提交 | `cca74fb`（`fix(command): skip unused national encoding after prepare fallback`）；本地提交，未 push |
| 未关闭 | 原生 1295 及目标实库字面量身份/编码/时区/精度验收；OUT、cursor、stream、复杂值不扩入普通回退 |

红/绿及全量 TRX 保存于本地忽略目录 `TestResults/roadmap/d01-*.trx`；不把模拟 TCP、
OB20 fixture 或历史中继注入当成本轮原生 OceanBase 证据。本轮没有验证真实 OceanBase、ODP、
TLS/KDC、远端 CI、其他 OS 或生产负载。
消费者全量耗时 30 分 6 秒；诊断采样确认 DiscoveryBlacklist 的本地拒绝连接用例仍在推进，
最终正常完成；没有通过跳过用例、缩短矩阵或改动发现/路由代码取得通过结果。

下一模块是 **D-02 的范围核对**：先列清现有多语句输入/多结果子集及 Prepare、cursor、OUT、
复杂输入的具体缺口与 wire 前提；已支持的字面量/流不重复实现，缺证据的组合保留拒绝，
确认可实现的子项后才进入测试、实现、走查和提交。

### D-02：已核对的实现边界与待开发子项

源码 `OceanBaseCommand.Start` 已将 opt-in 多语句选为 text 执行，并明确拒绝显式 Prepare、
server cursor 和 Oracle 输出计划；`ClientPreparedSql` 只展开有确定字面量语义的输入。
`CommandExchange.ClientPreparedSql` 已提供有界 binary Input 流展开，不能把它重复列为缺实现。
以下是剩余任务；d1 的本轮实现与证据见下节，父项保持 `P2`：

D-02b～D-02d 先调查是否纳入产品范围，不代表必须实现；允许以有依据的保留拒绝、范围排除
或转交 D-03/D-04 关闭调查。只有明确纳入后，才适用各项实现和验收条件。

| 子项 | 进度 | 开发任务与先决条件 | 验证/关闭依据 |
| --- | --- | --- | --- |
| D-02a 已有输入/多结果闭环 | `P2`；a1 达到 `P4` | 核对双模式的正常/NULL/边界、SequentialAccess、半读释放、错误前缀/未执行后缀和独立存储；只为发现的具体缺口补代码或测试 | MySQL 时间脚本存储见 a1；本轮 Oracle 长 NUL 4 项失败保留，不能只看总测试数 |
| D-02b 多语句 Prepare/server cursor | `P0`，待范围/wire | 确认服务端是否提供脚本级 statement/cursor 身份及生命周期；若只能拆成单语句，须先定义结果顺序、失败前缀与资源契约，再评估是否纳入 OceanBaseCommand | 证据成立且明确纳入范围后再实现；无支持证据时保持发送前拒绝，不把现有 DbBatch 的顺序执行当作该功能已完成 |
| D-02c 多语句 OUT/INOUT | `P0`，待模式/方向证据 | 分别确认 MySQL 过程变量和 Oracle PrepareExecute 的脚本形状；定义输出何时发布、后续语句失败时是否可见，不能直接把 OUT 插值成输入 | sync/async、OUT/INOUT/NULL、错误后原子发布、连接复用及实库独立核对；普通单过程 OUT 成功不能关闭脚本任务 |
| D-02d 复杂输入/locator/cursor 结果 | `P2`；d1 达到 `P4` | 确认文本脚本可表示的复杂值身份、LOB 归属和 cursor wire；分别复用 D-03/D-04 已证明的能力，避免再开发一套 codec | 命名/匿名、NULL、嵌套、半读/取消/释放和旧租约安全逐项验证；REF CURSOR 输入仍为范围排除 |

#### D-02a1：MySQL 时间参数脚本的存储闭环（`P4`，2026-10-30）

本轮基线 `adc5ac8`，工作区干净。D-02a 的源码已有 DATE/DATETIME/TIME 字面量；当前多语句
实库组没有相应的独立存储用例，单语句 fallback 的中继注入不能关闭脚本验收。本模块补齐
`MultiStatementTests.Temporal.cs`：DATE、DATETIME(6)、TIMESTAMP(6)、TIME(6) 的普通值、
provider 值、NULL、zero-date、0/3/6 位精度，以及脚本内 sql_mode/time_zone 改变的存储语义。
TIMESTAMP 按执行该条语句时的会话时区转换；DATETIME 不转换。zero-date 明确先设置宽松 SQL_MODE。

本轮执行当前双模式整个多语句组，核对正常结果、半读释放、错误前缀和服务端边界；新增
时间测试通过独立 UTC 连接分别 text/prepare 读回，并验证坏精度/非法值在脚本首条 SQL 前拒绝。
本模块不新增 public API，不改变 TIME 极限裁剪、zero-date 服务端条件或原生 1295 的未关闭结论。
已有时间字面量实现通过本模块验收，因此只补测试和本文，不为补齐测试而重复编写产品代码。

- [x] 当前双模式多语句组基线及清理结果（保留 4 项失败，见下文）。
- [x] 新增 MySQL 时间脚本的独立存储、结果 metadata、NULL/zero-date、精度/时区及发送前拒绝测试。
- [ ] 运行新增实库组及适当本地回归，独立代码走查，处理问题后本地提交。
- [ ] 本文记录实际范围、结果、提交号和仍未关闭项。

| 检查点 | 本轮证据/状态 |
| --- | --- |
| 实库时间专项 | `mysql-direct` 26/26 通过，0 失败/0 跳过；24 个值用例覆盖 sync/async × Default/SequentialAccess × 0/3/6 位 × CLR/provider 值，另有 2 个拒绝用例 |
| 独立存储断言 | DATE、DATETIME(6)、TIMESTAMP(6)、TIME(6) metadata 和精确值；独立 UTC 连接 text/显式 Prepare 两次读回。正/负 49:02:03 时间跨度、NULL、zero-date 和全表行数；原参数值及写连接可复用 |
| 会话语义 | 脚本先使用 `+05:45`，再使用 `-03:30`；TIMESTAMP 按对应语句会话时区转 UTC，DATETIME 保留墙上时间。严格模式切换 NO_BACKSLASH_ESCAPES，zero-date 仅在显式宽松 SQL_MODE 后写入 |
| 发送前拒绝 | NULL TIME 的 Scale=7、次微秒 TIME、UTC DateTime 分别拒绝；同连接和独立连接均确认表中 0 行，前缀 INSERT 未执行 |
| 清理 | 所有新建表由 DatabaseContext 的 LIFO 清理完成；清理错误会使所属测试失败，没有跳过清理断言 |
| 构建 | IntegrationTests Release 构建 0 警告/0 错误，`--no-restore --disable-build-servers -m:1 -nr:false` |
| 本地专项 | ClientPreparedSqlTests、MySqlMultiStatementParameterTests、MySqlTemporalFallbackTests 共 43/43 通过，0 跳过；产品代码未改，本轮不重复前模块的完整 Unit/Consumer 门禁 |
| 格式 | 新增测试文件 `dotnet format whitespace ... --verify-no-changes --no-restore` 通过；沙箱内 MSBuild 管道权限失败后在沙箱外核验 |
| 独立代码走查 | 指出 zero-date DATETIME 只比较日期的断言不足；现比较完整 OceanBaseDateTime（含时间、微秒和 Scale），重建 0 警告/0 错误、新实库 26/26 再次通过；未发现产品代码或清理问题 |
| 提交 | 走查后本地提交，提交号待回填 |

本轮双模式整个 `MultiStatementTests` 基线为 **360 项，356 通过、4 失败、0 跳过**。
失败全部是既有 `OracleLargeNulSingleTextExpressionsPreserveEveryCharacter` 的 sync/async ×
Default/SequentialAccess；约 10 秒后报告响应未完整消费、执行结果未知。独立重跑仍为 0/4 通过。
它执行 196608 字符的 `a\0b` 单表达式，两次执行中第一次即失败；不是新增 MySQL 时间用例失败。

同一目标的 Java Connector/J 2.4.14 对照也在约 10 秒后抛出 `SQLNonTransientConnectionException`
（08000）：`REPLACE(` 后有/无空格 × 单 SELECT/双 SELECT 共 4 个形状均失败。
Java 使用 text、UTF-8、allowMultiQueries 和 10 秒 socket timeout；不是通过放宽 .NET 超时或删改断言
取得结果。此前文档记录的单表达式成功保留为历史证据，**不能继续代表当前环境已通过**。
两个客户端同失败尚不足以定位根因，需继续核对服务端执行、会话/网络状态及版本变化；暂不归因
为 .NET 缺陷或已确认的服务端限制，也不放开/收紧公共能力声明。

本地忽略目录保留 `TestResults/roadmap/d02a-temporal-live.trx`、`d02a-local.trx`、
`d02a-live-baseline.trx`、`d02a-long-nul-recheck.trx` 及 `d02a-java-nul-recheck.log`。
仅 MySQL 时间脚本子项达到 `P4`；D-02/D-02a 保持 `P2`，长 NUL 转交 A-05 继续复核。
下一步先追查该失败，再核对 Oracle 时间脚本的独立存储覆盖；真实 TLS/ODP/OB20/KDC、其他
OS/runtime、长期负载、原生 1295 及 TIME 极限仍未验收，不以本子项通过关闭它们。

#### D-02d1：命名 OBJECT 的标量输入（`P4`，2026-10-30）

现有 `oracle-direct` 真实服务端已通过 2/2 同步/异步用例：脚本使用完整引用的 schema/type
构造 NUMBER/VARCHAR2 对象并写入，独立连接预载精确类型、Prepare 后读回成员及对象 NULL。
这只确认 SQL 构造语法及存储，不是参数展开实现的通过证据；TRX 为本地忽略目录中的
`TestResults/roadmap/d02-constructor-evidence.trx`。读取连接必须自行预载类型，不能依赖写入连接的缓存。

本模块选择复用已有 `OceanBaseComplexValue` 和标量字面量，将**扁平命名 OBJECT** 展开为
`"schema"."type"(...)`。成员限现有 NUMBER、VARCHAR2/CHAR、NVARCHAR2/NCHAR、RAW、DATE、
TIMESTAMP；标识符严格引用，NULL 对象与全 NULL 属性对象保持不同。只用于已有 opt-in 文本脚本；
普通 prepare fallback、集合、嵌套 OBJECT、LOB/cursor 成员、OUT 和显式 Prepare/server cursor
继续拒绝。这些未关闭部分仍归 D-02d/D-03/D-04，不以本子模块完成替代父任务。

实现涉及 `ClientPreparedSql`、`OceanBaseCommand` 的限制提示、`OceanBaseConnection.ComplexTypes`
字典名称识别，以及 Unit/Consumer/Integration 测试；不新增 API/选项/依赖。复用执行前的类型会话/事务/缓存校验；含 DDL 的复杂参数脚本发送前
拒绝，以免前一语句使描述失效。展开逐段计入 SQL 字节上限，且在消费任何 Input 流之前验证全部参数。

- [x] 确认真实构造语法和独立存储读回前提。
- [x] 新增参数展开失败用例，确认因现有拒绝而失败。
- [x] 实现有界构造展开及 DDL/模式/方向/类型拒绝，保留原所有权校验。
- [x] 验证引用/注入边界、成员精度、NULL、重复执行、错误后会话和流未消费；公开 DLL 与实库独立读回。
- [x] 运行构建及适当回归，独立代码走查，修复后在本文记录证据。
- [x] 本地提交，并将实现提交号写入本文。

本轮发现的必要前提修复：真实 `ALL_TYPE_ATTRS` 返回 `TIMESTAMP(9)`，既有加载器只认
`TIMESTAMP`，导致该类型在参数发送前就无法加载。新增 `(0)/(6)/(9)` 三个用例均复现拒绝后，
按精确白名单接收 `TIMESTAMP(0..9)`；保留原字典名称，不把带时区或任意前缀当 TIMESTAMP。
此项同时补齐 D-04 的既有字典加载缺口，不代表 D-04 的其他成员/方向已经完成。

| 检查点 | 本轮证据/状态 |
| --- | --- |
| 参数展开红/绿 | 最初 2 个 builder 用例因现有 NotSupportedException 失败；最终专项（含字典回归和公开命令）20/20 通过 |
| 字典名称红/绿 | TIMESTAMP(0)/(6)/(9) 新增 3 个用例修复前失败；修复后专项包含这 3 个用例并全部通过 |
| 引用和预算 | schema/type 中双引号、字符串中 SQL 片段、所有支持标量、对象 NULL/全 NULL；单对象成员展开及重复绑定合计超 64 MiB 时拒绝 |
| 生命周期负向 | 含 DDL 的脚本、过期缓存描述拒绝且 Input 流 Position 保持 0；随后同会话命令成功；未放开集合/嵌套/LOB/cursor/OUT/fallback |
| 公开 DLL 专项 | 12/12 通过，代表资产 × sync/async × 普通/OB20 合成协议；精确构造 SQL、重复值/NULL/全 NULL、旧描述拒绝和复用 |
| Oracle 实库专项 | 4/4 通过：构造语法证据 2 个，参数闭环 2 个（sync/async）；所有创建对象由 DatabaseContext 成功清理，0 跳过 |
| 实库存储断言 | 38 位 NUMBER、普通/国字符、CHAR/NCHAR、RAW、DATE、TIMESTAMP(9)；独立连接预载类型、Prepare + SequentialAccess 读回；后续重复键错误保留前缀、阻止后缀，同会话可继续使用 |
| 构建与回归 | 五资产 Release 构建 0 警告/0 错误；完整 Unit 8659/8659 通过，0 跳过（沙箱外重跑）；DI 14/14 通过 |
| 完整公开 DLL 消费者 | 3561/3561 通过，0 失败/0 跳过，耗时 29 分 13 秒；三种产品实现资产在 .NET 10 host 下验证，不等于独立 runtime 或所有 OS |
| 工具与文档检查 | 限定本次 C# 文件的 `dotnet format whitespace ... --verify-no-changes --no-restore` 通过；`Acceptance --list` 通过（仅列清单）；本文本地链接和 G01–G15 映射检查通过 |
| 独立代码走查 | 初次及最终差异复核均无阻断问题；独立专项 12/12 通过，并核对实库/专项/DI TRX；未代替全量或实库执行 |
| 提交 | `5ef8477`（`feat(command): expand named Oracle object inputs in text scripts`）；本地提交，未 push |

本地忽略目录保留 `TestResults/roadmap/d02-*.trx`。此前夹具漏回协议/NLS 查询及测试直接强转
Oracle NUMBER 的失败已修正；没有修改产品 NUMBER 返回类型或删除失败断言。
首轮完整 Unit 为 8658 通过、1 失败，唯一失败是既有 TNS 用例的 `File.Replace` 被沙箱权限拒绝；
相同用例沙箱外 1/1 通过后，原样重跑完整 8659 项并全部通过。格式工具首次也因命名管道权限
无法连接 MSBuild，沙箱外检查及必要空白修正后通过；不把权限失败计为产品功能通过。
本轮只验收 Oracle 直连标准协议下的上述新形状，OB20 仍为合成协议证据；未扩大真实 TLS/ODP/KDC、
其他 OS/runtime、长期负载的支持声明。集合、嵌套 OBJECT、LOB/cursor 成员及复杂 text 结果仍未关闭。

**本子模块已完成并达到 `P4`；D-02/D-02d 父项仍为 `P2`，整体开发清单未完成。**
后续先核对 D-02a 的既有输入/结果闭环，再按 D-02b～d 的服务端/wire 前提逐项推进；新增
集合、嵌套和复杂 text 结果支持前，各自补独立证据，继续在本文记录，不新增并行进度文档。

## 依据

- [功能能力矩阵](obconnector-dotnet-progress.md)
- [缺失矩阵](obconnector-dotnet-completeness.md)
- [范围依据与清理决策](obconnector-dotnet-scope-audit.md)
- [Java 结构分析](obconnector-j-architecture-outline.md)
- [OceanBase 真实兼容性与阻塞](compatibility.md)
- [验收标准](acceptance-criteria.md)

[T18-vector]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/640.ob-vector-search/700.ob-vector-search-reference/100.ob-vector-data-type.md
[T18-bitmap]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/500.sql-reference/100.sql-syntax/200.common-tenant-of-mysql-mode/100.basic-elements-of-mysql-mode/100.data-type-of-mysql-mode/1000.roaring-bitmap-data-type-of-mysql-mode/100.roaring-bitmap-data-type-overview-of-mysql-mode.md
[T18-array]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/500.sql-reference/100.sql-syntax/200.common-tenant-of-mysql-mode/100.basic-elements-of-mysql-mode/100.data-type-of-mysql-mode/1200.array-data-type-of-mysql-mode/100.array-data-type-overview-of-mysql-mode.md
[T18-array-ddl]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/500.sql-reference/100.sql-syntax/200.common-tenant-of-mysql-mode/100.basic-elements-of-mysql-mode/100.data-type-of-mysql-mode/1200.array-data-type-of-mysql-mode/200.create-array-columns-of-mysql-mode.md
