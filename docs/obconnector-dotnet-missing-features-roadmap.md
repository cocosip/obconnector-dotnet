# OceanBase .NET 缺失功能开发清单

更新：2026-11-02。本文以当前源码及范围审计固定的 OceanBase 官方文档、Connector/J 2.4.14
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
| D-02 | 多语句、多结果、客户端参数展开及多种 batch 路径 | b/c 本轮复核：目标服务端拒绝脚本 server prepare；本轮 Java callable 脚本未得到成功证据；MySQL 用户变量结果不等于参数 OUT |
| D-03 | cursor fetch、REF CURSOR、标量 OUT/INOUT、带条件的 offset | 不代表 SELECT CURSOR 列形状、OUT offset 或 REF CURSOR 输入都支持 |
| D-04 | ObStruct/ObArray、复杂描述、LOB/cursor 读取及限定 writer | 不代表所有 named/anonymous 成员和 locator 回绑都已实现 |
| D-05 | ROWID 类型 209 的字符串结果路径 | 不能据此认定 UROWID 参数/结果已闭环 |
| D-06 | MySQL GEOMETRY 类型 255 的原始 byte[] 结果路径 | 不能据此认定专用输入、SRID 契约或完整 GIS API 已有 |
| D-07 | 字符集名称到 JVM charset 的选择、普通/national 编码路径 | 不提供 OceanBase 新增 collation 和 .NET 严格映射的独立互操作证明 |

### Q20 multi-send/pipelining

这是 Java Connector/J 2.4.14 的真实可选能力，不是旧矩阵臆测。Java 依据是
`DefaultOptions.java:470–490,553–560` 和 `AbstractQueryProtocol.java:729–732,1287–1326`，
对应的功能是把多个 prepared 执行组织成多发送路径；它与 MariaDB
`COM_STMT_BULK_EXECUTE` 不同。.NET 已增加默认关闭的 prepared batch multi-send 子集，覆盖
input-only DML、基本 scalar 排空、普通 `ExecuteReader`、数量/64 MiB 分组和发送状态保护；本地 loopback 已通过。
本轮补齐 MySQL 模式的 `Stream` 输入：每条执行前按 Connector/J 同一协议发送
`COM_STMT_SEND_LONG_DATA`，再发送 `COM_STMT_EXECUTE`；同步/异步路径均保持先发完整组、再按顺序排空响应。
Oracle piece-data 需要逐片确认，未纳入该流水化子集；启用 batch multi-send 时，Oracle Stream
输入会安全回退到已有的逐命令 piece-data 路径，不伪装成无响应流水化。

VPN 恢复后的 4.4.2.2 MySQL/Oracle 直连临时 profile 已覆盖 DML/scalar × sync/async，
**8/8 通过**；过程中修复了客户端每个 pipelined 响应边界的 packet sequence 重置。新增的
ExecuteReader Oracle 直连 profile sync/async **2/2 通过**；当前环境未提供 MySQL profile，未外推
MySQL reader 结果。该证据只把首个 input-only 子集提升到 `P4`，不外推 Oracle PrepareExecute、
输出/LOB/cursor/array、流、text fallback、代理/OB20、收益和更大错误排序矩阵。

### T17 XMLType、SDO_GEOMETRY、ANYDATA

这三项属于 OceanBase Oracle 兼容模式的服务端专用类型或 PL 能力，官方文档分别有独立说明，
所以不能写成“服务端不存在”。但它们的服务端存在不等于 Connector/J 或 .NET 已有通用值 codec：

| 类型 | OceanBase 判断 | Java 2.4.14 判断 | .NET 判断 |
| --- | --- | --- | --- |
| XMLType | 服务端 Oracle 模式条件能力；当前目标已确认 type 160 ownerless text 与 type 253 字符参数 wire | 没有确认到通用 XMLType wire/value codec；Java 的 SQLXML 路径明确拒绝 | 已有当前版本 String wire 子集；没有 binary/复杂 XML 专用 codec |
| SDO_GEOMETRY | 服务端 Oracle 模式条件空间类型 | 有通用 OBJECT/ARRAY 复杂值路径和 `SDO_` 元数据查询特判；没有确认专用 SDO/WKB/SRID 契约 | 没有专用 codec、空间 API 或 SRID/WKB 契约 |
| ANYDATA | 服务端 PL/系统类型，有独立限制，不能当普通持久列 | 没有确认到普通列通用 codec | 没有专用 codec；必须先确定是 PL 参数/结果还是持久列需求 |

因此 T17 仍需按形状拆分，不能把当前 XMLType 子集扩大成完整 codec。XMLType 的当前字符串
wire 子集为 `P4`，binary/复杂 payload 和更多 PL 方向仍为待证据；SDO/ANYDATA 的状态见
D-10/D-11。官方和 Java 范围依据见[范围审计](obconnector-dotnet-scope-audit.md)。

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
这确认了服务端功能存在；当前两个 4.4.2.2 租户仍不代表所有形状，但本轮已在 MySQL 直连 profile
确认 RoaringBitmap 查询为 BLOB/byte[]，并确认可通过现有 Binary 参数调用 `rb_build_varbinary`。

## 开发任务列表

下面的任务只列当前仍有开发价值的缺口。每项都包含边界，避免把所有输入、方向、模式和协议
组合一次性扩成不可验收的笛卡尔积。

### 第一阶段：补齐已有能力的受限组合

| ID | 当前进度 | 任务 | 需要修改/核对 | 前置条件 | 关闭条件 |
| --- | --- | --- | --- | --- | --- |
| D-01 | `P3`，代码提交 `cca74fb` | Oracle 普通 client-prepare fallback（G01/Q04；本轮本地代码模块完成） | 复用已有 Oracle 时间/INTERVAL/NCHAR 字面量；修复 national 无用 binary 编码，sync/async 一致 | 完整本地门禁和独立走查已通过；原生 1295、字面量身份、编码、时区和精度的目标实库仍待验收 | 原生 1295 profile 下存储后独立读回再进入 `P4`；不含 OUT/cursor/复杂值，不以 `P3` 宣称全部组合完成 |
| D-02 | `P2`；a1、d1～d3 达到 `P4` | 扩大双模式多语句参数和多结果（G02/Q05） | `OceanBaseCommand`、SQL 边界扫描、参数展开和结果消费 | 保持 SQL 总长度、引号、NUL、流和失败后会话状态边界；长 NUL 不稳定性保留 | 每个支持模式/方向/类型组合有结果、失败前缀和后缀断言；Prepare、cursor、OUT 和 d1～d3 以外的复杂输入继续明确拒绝或单独关闭 |
| D-03 | `P2`；c 的指定子集达到 `P4` | Oracle cursor/输出形状（G03/Q10/Q11） | `OracleCommandPlan`、`QueryResult.Cursor`、`OracleCursorOwner`、Fetch/offset | 已有四字节 cursor/OUT 子集；本轮 SELECT CURSOR 的父行/child FETCH 仍失败；扩大范围须有真实 wire | a/b/c 拆分见执行记录；本轮 30/31 保留原有 1 项列失败，PL OUT 成功不关闭结果列缺口 |
| D-04 | `P2`；a1/a3.1/b1 指定子集达到 `P4` | Oracle OBJECT/collection/复杂 LOB/cursor 成员（G04/T14/T15/Q16） | `ComplexValueCodec`、`ComplexTypeContext`、locator 生命周期、RETURNING 身份 | 子项见执行记录；命名根 NULL 输入已修复；BLOB OBJECT 成员 DML 已按同 wire 的 Java 诊断回放标为当前服务端限制；根 NULL OBJECT 输出身份、复杂 text 和其他 locator 形状仍未关闭；SQL OBJECT 与当前 PL-only cursor 成员形状已按 Java/.NET 同失败标为当前服务端限制 | 支持的成员方向、NULL、嵌套、locator 失效、DDL/reset 和旧资源释放均有证据；未知身份不误解码 |
| D-05 | `P4`，仅指定直连 profile | UROWID 和 ROWID 参数/结果闭环（G05/T12） | 新增显式 Urowid 参数类型、type209 输入和目录映射；旧 ROWID/String 结果兼容 | 普通 prepare/PrepareExecute、NULL/长值、OUT/INOUT/ReturnValue、RETURNING、独立读回已验收；见执行记录 | 指定 Oracle 4.4.2.2 直连验收；client literal/多语句与受控 1295 fallback 子集见 D-05b；跨部署、长期压力与 A-03 目录边界仍保留 |
| D-06 | `P2`；a 指定直连子集 `P4`，b 待服务端 SRS | GEOMETRY 输入和结果边界（G06/T05） | 复用Binary参数回绑SRID+WKB；补空间过程目录映射和DataTypes参数支持，旧结果Binary身份兼容 | SRID=0多形状/NULL/坏值/输出/独立存储已验证；当前SRS目录仅0，非零SRID拒绝见执行记录 | a 的范围与证据见下；b 取得真实非零 SRS 目录和往返后单独关闭；无GIS领域API，不更改服务端SRS配置 |
| D-07 | `P2`；指定成功子集 `P4`，Oracle EUCKR 普通客户端未关闭 | 字符集和 national/result 映射（G07/T19/T20） | `ClientCharacterSet`、`ResultCharacterSet`、strict codec、collation 表 | 每个新名称需要服务端枚举、.NET 严格映射和真实 roundtrip；不能用相近 code page 伪造 | MySQL GB2312/EUCKR 客户端/结果与 Oracle NCHAR/NVARCHAR2 national 目标直连已验收；Oracle 普通 EUCKR 初始化被服务端 900/42000 拒绝并保留；UJIS 等逐项确认后再加入，不支持名称继续发送前拒绝 |

### 第二阶段：参考 Java 的候选实现

| ID | 当前进度 | 任务 | Java/OceanBase 边界 | 前置条件 | 关闭条件 |
| --- | --- | --- | --- | --- | --- |
| D-08 | `P4`；指定 4.4.2.2 MySQL/Oracle 直连 profile 的 input-only DML/scalar/ExecuteReader 子集已验收；MySQL Stream 存储/读取、MySQL BLOB/TEXT 结果、Oracle Stream piece-data 回退、1295 text fallback 及 Oracle named OBJECT/collection 普通输入、普通/嵌套 OBJECT binary 结果与根 BLOB/CLOB locator 普通输入、保留调用方连接的根/命名复杂 LOB binary 结果及命名复杂 LOB 内容输入及 Oracle DML RETURNING 标量 OUT/INOUT、命名复杂值和保留调用方连接的 LOB 输出子集也已验收 | multi-send/pipelining（Q20） | Java 有实现；MariaDB bulk 不在范围；.NET 为默认关闭的 opt-in prepared batch send | 目标 profile 的 wire、错误排序、收益、取消和 unknown-outcome 仍需分别扩展；当前子集不含 Oracle 自有连接/CloseConnection 的 locator 结果、复杂 text LOB 结果、PL 输出、cursor、array binding；Connector/J 2.4.14 的 Oracle piece-data 同样逐片等待确认，未形成流水化协议依据，故不猜测实现；代理/OB20 和真实租户原生 1295 仍未验收；新增的 token/batch cancel/timeout 仅验证 loopback unknown-outcome 归属 |
| D-09 | `P4`；4.4.2.2 `oracle-direct` text+binary String wire 子集；PL XMLTYPE OUT/INOUT 已确认是服务端限制；完整 XML codec 仍未关闭 | XMLType wire 调查与有条件 codec（T17） | ownerless `XMLTYPE` 已有严格 UTF-8 String wire；`SYS.XMLTYPE` metadata 已有安全的 String 消费边界；`DbType.Xml` 参数使用已验证的 type 253 字符串 wire；同一 PL OUT/INOUT 形状在 Connector/J 2.4.14 与 .NET 分别返回 `600/0A000`、`600/42000`，按服务端限制跳过客户端扩展 | 当前子集覆盖普通 XMLTYPE 列、NULL、同步/异步 DML、metadata、PL 函数结果、显式 Prepare binary 结果；带 owner 的复杂 payload、坏值/最大值和跨版本证据仍待确认；PL OUT/INOUT 当前版本不再作为待开发项 | 保持当前子集的参数/结果/NULL/metadata 契约；取得稳定复杂 XML identity 和不同服务端 wire 后再扩展，不把 PL 失败伪装成客户端成功 |
| D-10 | `P4`（4.4.2.2 `oracle-direct` 指定子集）；显式 Prepare 的 SDO_GEOMETRY 整列读取与 generic OBJECT/ARRAY 输入已验收，专用空间契约仍未闭合 | SDO_GEOMETRY wire 调查与有条件 codec（T17） | 服务端有条件空间类型；Java 只有通用复杂值路径，未确认专用 SDO codec | 目标版本、wire、SRID、WKB/文本表示和安全大小上限 | 保持通用 OBJECT 路径；当前仅关闭 SYS.SDO_GEOMETRY 整列读取和 generic OBJECT/ARRAY 输入子集；若纳入 WKB/WKT、SRID 或非法几何契约，先补独立 wire/版本证据；不把 GEOMETRY bytes 冒充 SDO |
| D-11 | `P0 / 当前目标服务端限制`；ANYDATA PL 包调用在同一租户由 Java/.NET 同样拒绝 | ANYDATA PL 形状调查与有条件 codec（T17） | 当前 4.4.2.2 目标租户找不到 `SYS.ANYDATA`；Connector/J 与 .NET 对同一 `CONVERT*` 查询均失败 | 只有目标版本/租户实际提供 `SYS.ANYDATA` 包并给出参数/返回 wire 后重新开启；不为缺失服务端包增加客户端兜底 | 当前目标直接按服务端限制跳过客户端 API；新部署提供包后再定义有限 PL 形状、NULL、类型名和失败发布契约 |
| D-12 | `P4`（a/b/c 的当前版本通用路径已分别验收） | MySQL VECTOR/RoaringBitmap/ARRAY 的 wire 与 codec（T18；子项见下） | 服务端功能存在；4.4.2.2 的 VECTOR/ARRAY wire 为 CHAR 文本，RoaringBitmap 查询为 BLOB/byte[]；Oracle ARRAY 归 D-04 | 其他版本、稀疏/嵌套形状和专用 codec 需求仍需单独 profile 证据 | a/c 复用 String，b 复用 Binary 读取并通过 `rb_build_varbinary(?)` 写入；不新增未经证实的专用 codec |

D-12 分为以下三个子任务，状态都维护在本文，父项只有在所有子项结论闭合后才能关闭：

| 子项 | 进度/已确认 | 具体开发与验证范围 | 当前缺口 |
| --- | --- | --- | --- |
| D-12a VECTOR（当前版本稠密） | `P4`，4.4.2.2 MySQL 直连通用 String 子集 | `VECTOR(3)` DDL、NULL/文本值、参数绑定、显式/隐式 Prepare、结果 metadata 和独立读回；Java 2.4.14 同样返回 `CHAR`/`String` | 当前版本没有专用 wire/type code；`SPARSEVECTOR` 在目标服务端返回 1064，保留为版本边界 |
| D-12b RoaringBitmap | `P4`；4.4.2.2 MySQL 直连通用 Binary/SQL 函数子集 | DDL、NULL/空集合、`rb_from_string`、64 位值、raw metadata、`rb_to_string`/`rb_to_array`、`rb_build_varbinary(?)` 回绑和坏 raw 行为已验收；真实集成回归 sync/async **2/2** | 查询返回 `BLOB`/`byte[]`；直接把 `byte[]` 绑定到 `ROARINGBITMAP` 列由服务端返回 `1235/0A000`，属于服务端 prepared DML 限制；未新增专用 provider type |
| D-12c MySQL ARRAY | `P4`，4.4.2.2 MySQL 直连通用 String 子集 | `ARRAY(INT)` DDL、NULL/文本值、参数绑定、显式/隐式 Prepare、结果 metadata 和独立读回；Java 2.4.14 同样返回 `CHAR`/`String` | 当前版本没有专用 wire/type code；嵌套元素、专用参数 metadata 和其他版本仍待确认，不能用 Oracle 类型码 161 代替 |

### 第三阶段：已有代码的部署和故障验收

| ID | 当前进度 | 任务 | 当前缺口 | 关闭条件 |
| --- | --- | --- | --- | --- |
| A-01 | `P3`；MySQL/Oracle 直连各 10000 次借还及资源隔离 52/52 通过；持续运行失败根因未定，长测移至发布验收 | 池、reset、租约和 prepared cache（G10） | 60 分钟交错、清池和句柄/线程/内存稳定性未证实；原始异常被测试清理遮蔽的问题已修复 | 16 并发调用者、池上限、至少 10000 次借还、60 分钟交错运行；reader/LOB/cursor/XA 旧资源不污染新租约 |
| A-02 | `P3`；MySQL/Oracle 直连 XA 生命周期、recover 和池丢弃恢复 28/28、环境事务 COMMIT 回执丢失 16/16 通过 | PSPE、XA、unknown outcome（G11） | 环境事务 InDoubt 指定回执丢失子集已验收；协调器、节点/代理崩溃、取消顺序和分支恢复未全覆盖 | 发送前/发送中/响应中故障、独立查账、XA recover/决定后恢复、无自动重放全部有记录 |
| A-03 | `P3`；Oracle 直连基础目录/过程/NUMBER 18/18 通过，包重载 2/2、引用 package 标识符 2/2 通过 | Schema、目录和参数元数据（G13） | 包内 PL 类型和最小权限仍缺；大小写/引用 package 标识符已覆盖；无约束 NUMBER 已验收，A-03b 的 CHAR_LENGTH 已确认是当前目标服务端目录限制 | 双模式最小权限、唯一/歧义签名、更多大小写/引用标识符、视图/同义词权限错误及其余目录边界均按服务端真实结果关闭 |
| A-04 | `P3`；当前 Windows 包/运行时指定子集已验收 | 包、TFM 和平台（G14/R01） | 五资产、net8/net9/net10 独立 runtime、两个 netstandard 的 net10 host 及 netstandard2.0 的 net48 host 已复验；非 Windows、完整 API/ORM 未验收 | 每个声明支持的 OS/runtime/资产都有消费者、构建和目标实库记录；netstandard host 加载不能替代独立 runtime |
| A-05 | `P3`，长 NUL 复核未关闭 | 原生 prepare fallback、zero-date、极限 TIME 和长 NUL 失败复核 | 当前包含中继注入、SQL_MODE 前提和服务端边界；196608 字符 NUL 单表达式从 .NET 0/4 恢复 4/4，原因未定，见执行记录 | 原生目标 profile、SQL_MODE、确切长度和 text/binary 路径分别记录；失败不改写成成功 |

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
| G09 | D-08 | Java multi-send；.NET 已实现默认关闭的 prepared batch multi-send 子集，指定直连子集已验收，其余组合和部署仍待验收 |
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

1. **优先补实际功能缺口**：只扩展有确定编码、身份和资源归属的 D 项组合；每个模块做针对性测试、独立走查后提交，不以重复长测代替功能开发；
2. **A-01 持续压力与平台/部署大矩阵移至发布验收**：保留失败及未知根因，不继续反复运行一小时测试，也不把短跑计为长期通过；
3. **按前提补 A-02～A-05 的验收**：环境事务回执丢失和 Windows 包/运行时子集已经验收；其余协调器/节点故障、最小权限、非 Windows 及原生 1295 等仍需对应环境，不重复补已通过的子集；
4. **只有 S-01～S-05 获得真实前提后，才开启对应协议、认证、发现和字符集开发**。

仍未实现的具体组合及其开发入口如下；这些是待补契约的边界，不承诺全部组合自动纳入产品范围：

| 组合缺口 | 对应任务 | 进入开发前需要补齐 |
| --- | --- | --- |
| 脚本级 Prepare、服务端 cursor、typed OUT/INOUT 发布 | D-02b/c | 当前脚本 prepare 被服务端拒绝；typed 输出需定义映射、失败前缀、原子发布及变量/取消/释放隔离，单 PL 块输出不是该缺口 |
| Oracle 输出计划 / REF CURSOR offset | D-03b | 成功 wire、位置与归属契约；Java RefCursor 也排除普通 offset 分支，不能直接套用普通 SELECT FETCH |
| 命名复杂值中的 LOB locator 回绑及未证实的复杂成员方向 | D-04 | 可辨识的类型/locator wire 和生命周期；当前 BLOB DML、根 NULL/text/cursor 成员的已确认服务端限制另记，不作为盲改客户端的理由 |
| package-local PL/SQL RECORD 参数派生与绑定 | A-03d | 字段目录、输入/输出 payload 及成功交互；目前 Java 公开 API 也不支持绑定，不能归为服务端拒绝 |
| multi-send 的 anonymous/复杂 text LOB 结果、关闭连接时的 locator 结果、PL 输出、cursor/array binding 扩展 | D-08 | named OBJECT/collection 普通输入与普通 OBJECT binary 结果见 D-08i/j；根 BLOB/CLOB locator 输入/结果见 D-08k/l；命名复杂 LOB 内容输入见 D-08m；保留调用方连接的复杂 LOB binary 结果见 D-08n（OBJECT 实库、collection 仅 loopback）；DML RETURNING 标量 OUT/INOUT 见 D-08o，命名复杂值和 LOB 输出见 D-08p；其余需分组响应、错误顺序、资源归属及 unknown-outcome 契约 |

完整 XML codec、SDO 专用 WKB/WKT/SRID API、稀疏向量/嵌套 ARRAY 属于额外范围或 wire/版本待确认；
当前通用 String/Binary/OBJECT 路径已有验收，不能把“没有专用类型名”直接等同没有功能。

当前整体状态为：

| 类别 | 数量 | 当前结论 |
| --- | ---: | --- |
| 已有代码但组合受限 | 7 项（D-01～D-07） | 保留已验收子集；剩余按协议、版本与服务端前提逐项推进，不重复开发 P4 范围 |
| 已实现的流水化子集 | 1 项（D-08） | 保留已验收的 input-only 与 RETURNING 输出子集边界；扩展与收益、故障、代理验收分别推进 |
| 专用类型已有通用路径子集 | 3 项（D-09、D-10、D-12） | 已有指定版本 P4 证据；复杂 XML、空间专用契约、稀疏/嵌套及跨版本仍待证据 |
| 类型服务端阻塞 | 1 项（D-11） | 当前缺 SYS.ANYDATA 包；取得实际 PL 参数/结果 wire 后重开 |
| 已实现但欠验收 | 5 项（A-01～A-05） | 需要真实部署、故障、压力或平台证据 |
| 服务端前提阻塞 | 5 项（S-01～S-05） | 暂停代码开发，保留 Deferred/失败 |
| 范围排除 | 一组 | 不计入完成率和开发积压 |

任务达到 `P5` 的最低要求是：源码行为、公开 API、负向边界、单元/消费者测试、目标 profile
验收、清理结果和文档状态一致；`skip` 或 `deferred` 不计为完成。

## 执行记录与下一步

每完成一个代码模块，先测试、独立代码走查，处理问题后提交；在本节记录修改、证据、提交和
未关闭边界，再更新上方任务进度。全部记录留在本文。

### 开发优先级调整与 A-01 延后（2026-11-02）

用户明确要求优先开发缺失功能，停止以反复长测消耗开发时间。A-01 保持 `P3`，移至发布验收：
单 MySQL profile 的一次运行约 13 分 43 秒、11677 轮后失败，最早保留错误为 Open/reopen 池等待
超时，根因未确定；另一次运行约 35 分钟但仅 577 轮，Windows 506/507 事件证实中间进入待机，
该次不能作为连续负载证据，且不能用它解释前一次失败。各次自有 XA 分支和测试表均已清理并复核。
未提交的一秒/ThreadPool 采样改动已保存到忽略目录 `TestResults/roadmap/deferred-threadpool-sampling.patch`
后撤回；本轮不继续扩充压力诊断脚本。

本轮转而完成 D-08 的 named OBJECT/collection 普通输入、普通 OBJECT binary 结果及根 BLOB/CLOB
locator 普通输入子集，具体范围和证据见 D-08i/j/k；
没有新增 API，也没有把顺序回退计为流水化能力。

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

2026-11-02 使用项目 User Secrets 的 `oracle-direct` profile 重跑 `StandalonePrepareTests` 与
Oracle temporal script 验收，共 **38/38** 通过，覆盖同步/异步、普通/显式 Prepare、输出/REF
CURSOR、SequentialAccess 和脚本内 session time zone 切换。该租户本次仍未返回原生
`1295/HY000`，因此只确认已有回退代码在正向 Oracle profile 下没有回归，D-01 继续保持 `P3`，
不把中继注入的 1295 当作原生服务端证据。

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
| D-02a 已有输入/多结果闭环 | `P2`；a1/a2 达到 `P4` | 核对双模式的正常/NULL/边界、SequentialAccess、半读释放、错误前缀/未执行后缀和独立存储；只为发现的具体缺口补代码或测试 | MySQL 时间脚本存储见 a1，Oracle DATE/TIMESTAMP/LTZ 脚本存储见 a2；历史 Oracle 长 NUL 4 项失败及本轮恢复均保留，详见 A-05，不能只看总测试数 |
| D-02b 多语句 Prepare/server cursor | `P0 / 当前目标服务端限制`；目标双模式 profile 在 prepare 阶段拒绝脚本，Connector/J 同一 server-prepare 形状也失败，因此跳过客户端实现并保留发送前拒绝 | 只有目标版本/部署接受脚本级 statement/cursor prepare，并提供身份、顺序、失败前缀和生命周期证据时才重新开启；不把客户端参数展开、FetchSize hint 或现有 DbBatch 的顺序执行当作该功能完成 |
| D-02c 多语句 OUT/INOUT | `P0 / 待范围与输出契约`；本轮调查完成，功能未实现 | 普通脚本参数 OUT/INOUT 保留拒绝；Oracle 单 PL 块输出沿用 D-03；MySQL 用户变量可显式作为结果读取，已有路径不重复开发；尚无可移植的 Java callable 脚本成功证据 | 如纳入 typed 脚本输出，先定义发布时机、后续错误可见性、变量/资源生命周期和真实 wire；再做 sync/async、OUT/INOUT/NULL、原子发布、复用与独立存储；Java 客户端报错不归为服务端拒绝 |
| D-02d 复杂输入/locator/cursor 结果 | `P2`；d1～d3 达到 `P4` | 确认文本脚本可表示的复杂值身份、LOB 归属和 cursor wire；分别复用 D-03/D-04 已证明的能力，避免再开发一套 codec | 命名 OBJECT/嵌套/集合输入见 d1～d3；集合子树 NULL OBJECT、列存储、匿名/LOB/cursor/text 复杂结果仍未关闭，REF CURSOR 输入仍为范围排除 |

#### D-02b/c：脚本 Prepare、cursor 与输出的证据核对（调查完成，2026-10-30）

基线 `501c59b`，初始工作区干净。本轮保持产品拒绝保护，新增实库回归和 Java 对照证据；
调查完成不等于功能完成，b/c 保持 `P0`，D-02 父项仍为 `P2`。所有结论限于当前两个
OceanBase 4.4.2.2 直连 profile（Windows、TLS Disabled），不能外推其他版本、代理或部署。

| 调查形状 | 官方 Java 2.4.14 本轮结果 | 开发结论 |
| --- | --- | --- |
| 双模式 `SELECT ?; SELECT ?`（Oracle 每句带 `FROM DUAL`） | `useServerPrepStmts=true`、`emulateUnsupportedPstmts=false`；普通 prepareStatement 与显式 serverPrepareStatement 都在 prepare 阶段失败：MySQL `1235/0A000`，Oracle `600/0A000`；单 SELECT 对照均成功，实际类为 ServerSidePreparedStatement、参数数为 1 | 当前 profile 缺脚本级 server prepare 前提，保留 .NET 显式 Prepare 拒绝；Java protocol 的 `prepare` 字节码确认发送命令 22、完整 SQL 后读取 prepare 响应 |
| 相同脚本加 `useCursorFetch=true` / FetchSize=1 | server prepare 仍在上述阶段失败；显式 clientPrepareStatement 可返回两个精确结果，但实际类仍为 ClientSidePreparedStatement | 客户端脚本加 FetchSize 不构成脚本 cursor 支持；当前没有脚本 statement/cursor 生命周期证据，保留拒绝 |
| Oracle 一个 `BEGIN ? := 7; ? := 9; END;` | `useOraclePrepareExecute=false/true` 两项均返回 7、9；连续两个 `BEGIN…END; BEGIN…END;` 都报 `900/42000`：false 在 prepareCall，true 在 execute（D-03 复核生效选项后更正） | 单 PL 块内多条赋值已经可用，归 D-03；不改边界扫描器拆 PL 内部分号；带程序单元前缀的完整语法及非法尾部仍由服务端验证 |
| 双模式单过程与两个 `CALL p(?)` 的 callable 对照 | 单 CALL INOUT 从 7 变 8，独立连接读到 1 行；双 CALL 在 Java prepareCall 阶段抛 IndexOutOfBoundsException，独立连接读到 0 行；过程/表清理通过 | 这是 Java 客户端形状失败，不能声称 OceanBase 不支持过程或用户变量，也不能作为可移植的脚本 OUT 实现 |
| MySQL text 脚本 `SET` 用户变量、两个 CALL、最后 SELECT | Java 读到 8、1，独立连接验证两行；.NET 现有 text 路径也通过，见下 | SQL 用户变量和 reader 结果已有可用子集；这没有向 OceanBaseParameter 发布 OUT/INOUT，typed 脚本输出仍缺契约 |

D-02c 因此不能统称“服务端没有 OUT”：MySQL 过程和用户变量可用，Oracle 单程序单元输出
也可用。缺口是**任意脚本的参数输出计划与发布契约**，当前没有足够的参考/wire 证据把普通
脚本 OUT 纳入实现。若今后采用客户端编排，需要先确认输出映射、失败后是否发布、用户变量
隔离、取消和释放规则；本轮不新增选项、拆分器或把 Output 参数改为 Input。单 PL 输出/
REF CURSOR 的扩展继续由 D-03 跟踪，不重复新建任务。

本轮在 `MultiStatementTests.Preparation.cs` 新增以下实库验证：

- 双模式 × sync/async × Prepare、FetchSize、OUT、INOUT、NULL INOUT，共 20 项。输入流在
  脚本前缀 INSERT 中；拒绝后断言流位置 0、HasSentSql=false、输出值未变、独立连接计数 0；
  随后复用同一 command/物理连接执行单 INSERT，原流读到末尾，独立计数为 1。
- MySQL sync/async 共 2 项。整数 Input 初始化用户变量，包含 NULL INOUT 过程变量；按顺序
  消费四个 SET/CALL 空结果后读取最后 SELECT 的 8、1，Input 值保持 7，独立显式 Prepare
  查询验证两行。再让中间 CALL 触发 `1062`，断言已成功的前缀行保留、后缀没有执行、错误
  outcome 已知且原连接可复用；不对失败后的 typed 输出发布或变量值作额外支持声明。

| 检查 | 本轮结果 |
| --- | --- |
| IntegrationTests Release 构建 | 0 warning / 0 error |
| 目标实库 | **23/23**：新增 22 项加既有 Oracle 单 PL 块标量 OUT/INOUT 1 项；没有 skip，清理通过 |
| 初轮结果 | 20/22；两项用户变量测试误把第一个 SET 空结果当作最终 SELECT，按既有 reader 契约补齐 NextResult 后通过；没有修改产品行为来规避失败 |
| Unit 相关回归 | **160/160**：多语句、流、Oracle 输出计划、MySQL 过程生命周期和已 Prepare 命令变为脚本的边界；Release 构建 0 warning / 0 error；不是全量 Unit |
| 格式 | 新 C# 文件通过 `dotnet format whitespace --verify-no-changes --no-restore`；沙箱内 MSBuild pipe 拒绝访问，原命令在沙箱外通过 |
| 独立走查 | fresh-context reviewer `review_d02bc` 只读核对源码、测试、TRX、构建和 Java 日志/字节码后通过，无待修问题；未替代实库重跑或独立格式退出码验证 |
| 提交 | `2c3d479`（`test(integration): verify script preparation and output boundaries`）；门禁与独立走查通过，本地提交，未 push；本条仅关闭调查模块，不关闭 b/c 功能缺口 |

Java 程序、runner 和日志在本地忽略目录 `TestResults/java-gap-probe/`、
`TestResults/roadmap/d02bc-java-{oracle,mysql,output-oracle,output-mysql}.log`；当前/初轮实库
报告分别为 `d02bc-live.trx`、`d02bc-live-initial.trx`。没有增加新的任务文档。
本轮没有重跑全仓、完整多语句组、长期负载、其他平台/版本或真实 TLS/OB20/ODP/KDC。
下一模块进入 D-03，先对照现有输出/cursor 子集及服务端拒绝形状，避免重复实现已支持的 PL。

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
- [x] 运行新增实库组及适当本地回归，独立代码走查，处理问题后本地提交。
- [x] 本文记录实际范围、结果、提交号和仍未关闭项。

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
| 独立代码走查 | 指出 zero-date DATETIME 只比较日期的断言不足；现比较完整 OceanBaseDateTime（含时间、微秒和 Scale），重建 0 警告/0 错误、新实库 26/26 再次通过；最终代码与本文复核无剩余阻断问题 |
| 提交 | `54db11d`（`test(protocol): verify temporal script storage across session changes`）；本地提交，未 push |

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
#### D-02a2：Oracle 时间参数脚本的存储闭环（`P4`，2026-11-01）

本轮补齐 `oracle-direct` 的 DATE、TIMESTAMP(9) 和 TIMESTAMP WITH LOCAL TIME ZONE
脚本存储覆盖。用同一命令在 `+05:45` 写入第一行、切换 `-03:30` 后写入第二行，再写入
NULL 行，并直接查询确认脚本完成后的会话区确为 `-03:30`；验证同步/异步、
SequentialAccess/Default，以及**独立读回查询**的显式 Prepare/普通执行。脚本级 Prepare
仍按 D-02b 的服务端限制拒绝，不在本子项内宣称支持。
验证连接固定回到 `+05:45`，独立读取三行的 metadata、精确纳秒、LTZ 会话区和 NULL，随后
确认写连接仍可复用。LTZ 参数的初始会话区在脚本外先设置，这是驱动发送前的既有契约；脚本内
的时区切换仍覆盖第二次执行。

| 检查点 | 结果 |
| --- | --- |
| Oracle 4.4.2.2 直连实库 | **8/8**（sync/async × SequentialAccess/Default × 普通/显式 Prepare），0 失败/0 跳过 |
| 覆盖 | DATE、TIMESTAMP(9)、LTZ、NULL、session time zone 切换、metadata、独立连接读回和连接复用 |
| 代码走查 | 测试使用 Oracle `DateTime`/`OracleDate` 参数契约，LTZ 初始 session zone 与当前连接一致；未改变产品代码或服务端前提 |

本子项达到 `P4`，D-02/D-02a 父项仍保持 `P2`；长 NUL 的 4 项失败继续由 A-05 追查，
真实 TLS/ODP/OB20/KDC、其他 OS/runtime、长期负载、原生 1295 及 TIME 极限仍未验收。

#### A-05：长 NUL 不稳定性复核（2026-10-30，未关闭）

基线 `07240d4`，产品代码和原测试未改。本轮原样 .NET 单表达式 4/4 恢复通过，
Java 固定版的带空格单表达式也恢复通过；无空格单表达式和两个 SELECT 仍约 10 秒超时。
给 Java 加阶段标记确认失败在 `execute`，不是读取下一结果。另以普通字面量/REPLACE、
有/无 NUL、12003/98304/196608 字符做 12 个边界对照，均完整返回；带空格单表达式的
execute/executeQuery × 有/无前置健康查询 4 个形状也均通过，不能据此归因于预热或某个执行 API。

因此前轮 0/4 与本轮 4/4 都保留，单表达式只能记为当前复跑通过，不能宣称环境不稳定性已修复。
没有新增重试、提高超时、改写 SQL 或跳过失败用例。后续需要失败时段的服务端执行/网络诊断，
本轮不具备定位该时段内部状态的证据，A-05 继续未关闭；不阻塞其他有确定服务端证据的开发。
证据保留于忽略目录 `TestResults/roadmap/a05-nul-{boundary,execution,stage,direct-recheck}.log`
及 `a05-nul-current.trx`。

#### D-02d2：嵌套命名 OBJECT 输入（`P4`，2026-10-30）

本轮基线 `07240d4`。先验证两层命名 OBJECT 构造的脚本写入及独立 binary 读回，再扩展现有构造器。
选择复用现有不可变描述/值及标量字面量，逐层完整引用
类型名，所有层级合计同一 SQL 字节预算；对象 NULL、子对象 NULL、子对象全 NULL 保持不同。
沿用加载器既有深度/成员上限、类型会话/事务/缓存校验和 DDL 发送前拒绝，不新增 API/依赖。
集合、匿名类型、LOB/cursor、OUT、普通 fallback 和显式 Prepare/server cursor 仍不在本子项范围。

- [x] 实库构造/存储证据及参数展开红灯。
- [x] 有界嵌套展开、拒绝边界、独立存储、失败前缀和旧描述/流保护。
- [x] 构建、测试、独立走查、修正并本地提交；本文回填结果和提交号。

`ObjectLiteral` 先检查整棵命名描述，再将所有嵌套构造追加到同一个有界 builder；空对象也检查
子描述，因此不能用 NULL 绕过不支持的布局。描述按引用去重，避免一个合法共享子类型 DAG 在
深层递归中被指数级重复检查。值本身仍由既有 codec 执行 32 层/65535 成员校验，不新增另一套
对象/数组协议。普通 fallback 和其他拒绝项不受此扩展影响。

| 检查点 | 本轮证据/状态 |
| --- | --- |
| 服务端先决证据 | 手写两层构造的脚本实库 2/2 通过，独立连接预载父/子类型、Prepare + SequentialAccess 读回；包含子对象 NULL |
| 参数红/绿 | 实现前 sync/async 两项均因现有 NotSupportedException 失败；实现后嵌套、构造及原 flat 实库共 8/8 通过，0 跳过 |
| 独立存储 | 38 位 NUMBER、NVARCHAR2 补充字符、RAW 特殊字节、TIMESTAMP(9)、SQL 片段字符串精确读回；parent/child 字典身份均核对。独立连接 binary Default/SequentialAccess 分别读回 NULL 父对象、NULL 子对象、全 NULL 子对象和全 NULL 父属性 |
| 失败和清理 | 重复键保留第 6 行前缀，阻止第 7 行后缀；随后同写会话成功执行。所有创建表/类型由 DatabaseContext LIFO 清理，清理失败会使测试失败 |
| 本地边界 | 三层含引号类型名、NULL 形状、嵌套/重复绑定总预算；嵌套集合/匿名/LOB/cursor 即使 NULL 父对象也拒绝。flat/nested × sync/async 的 DDL/旧描述均在消费源流前拒绝 |
| 公开 DLL | 专项 36/36 通过：新 nested 12、原 flat 12、既有 dictionary 12，代表资产 × sync/async × 普通/OB20 合成协议；核对精确构造、重复绑定、NULL、旧描述拒绝、不可变 RAW 和复用 |
| 构建/最终回归 | 五资产 Release 构建 0 警告/0 错误；最终本地专项 183/183、DLL 消费者 36/36、实库 8/8 均通过，0 跳过；32 层共享描述回归包含在最终本地专项中 |
| 共享夹具回归 | SecurityTests 构建 0 警告/0 错误；原 encrypted dictionary 12/12 通过（沙箱外）。首轮 12 项在生成测试证书时被沙箱权限拒绝，原样重跑通过；只证明本地合成 TLS 默认分支没有被共享夹具改动破坏 |
| 格式和文档 | 六个修改 C# 文件的 whitespace 核验通过；只验证空白格式，不是全方案 analyzer。API 配置行更新为 nested named OBJECT，仍链接本文 |
| 独立走查 | 发现共享描述 DAG 的重复递归风险；加入 HashSet 去重及 32 层双引用/NULL 根回归。最终代码及文档走查无剩余阻断；已同步汇总中的长 NUL 历史失败/本轮恢复措辞，进度只记录在本文 |
| 提交 | `58693cc`（`feat(command): expand nested named Oracle object inputs in text scripts`）；本地提交，未 push |

实库覆盖两层嵌套，三层及 32 层共享描述为本地边界证据，不宣称任意深度实库均验收。
未新增真实 TLS/ODP/OB20/KDC、跨 OS/runtime 或长期压力证据；集合、匿名类型、LOB/cursor 成员、
OUT、fallback 和 text 复杂结果仍未关闭。子项达到 `P4` 不替代 D-02/D-02d/D-04 的父项。
本轮运行相关专项，不重复前模块的完整 Unit/Consumer，也没有重跑双模式整个多语句组；上述
183/36/8 不代替全量门禁，历史长 NUL 失败及本轮恢复通过记录均保留。
红/绿及回归证据保留于忽略目录 `TestResults/roadmap/d02d2-*.trx`；下一步调查集合构造在 PL 和
列存储中的服务端范围，nested-table 列的历史失败不得用已有 Oracle array binding 替代。

#### D-02d3：命名集合文本脚本输入（`P4`，2026-10-30）

基线 `135fbfe`。先分别验证命名集合类型、持久列和 `CALL` 脚本构造：已有 Oracle 二进制
PL 数组/集合绑定不能替代这些证据。当前 `CALL` 接收集合构造并能把成员写入普通表，因此复用
现有命名描述、不可变值、标量字面量和共享 SQL 字节预算扩展输入；支持集合 NULL、空集合、
NULL 标量/内部集合元素、非 NULL OBJECT（包括全 NULL 属性对象）和嵌套构造。
**集合子树中的 NULL OBJECT 继续拒绝**，不能因服务端把它变成全 NULL 属性对象而宣称身份保留。
未证实的列存储、嵌套形状或程序块解析不通过放宽扫描器解决。
匿名数组、LOB/cursor、OUT、普通 fallback 和显式 Prepare 仍不在本模块范围。

- [x] 核对服务端类型/持久列/PL 调用及独立存储范围。
- [x] 对可实现形状建立参数红灯，实现后验证空值、预算、方向/身份/DDL/流保护。
- [x] 构建、专项、实库、独立走查、修正及本地提交，在本文记录实际证据。

客户端改动只涉及现有构造器：集合元素使用同一个元素描述，递归保留完整类型引用、描述去重
和共享字节预算。空集合展开为 `"schema"."type"()`，NULL 集合展开为 NULL，两者不混淆；
没有新 API/选项/依赖，也没有修改语句扫描器、拆分程序块、增加重试或放开普通 fallback。

| 检查点 | 本轮证据/状态 |
| --- | --- |
| 服务端先决证据 | 手写 NUMBER 集合 CALL 脚本实库 2/2 通过，独立 binary/SequentialAccess 读回成员顺序、NULL 集合、空集合和 NULL 元素 |
| Java 范围对照 | 固定 Connector/J 2.4.14：NUMBER、OBJECT 元素、集合元素三种类型创建和 PL 构造均通过；三种持久集合列均 600/0A000，所有探测对象清理成功，未把 PL 成功当列存储通过 |
| 参数红/绿 | 新 builder 2 项、四种实库形状 × sync/async 共 8 项在实现前均因原 NotSupportedException 失败；最终支持/负向范围全部通过 |
| 实库存储闭环 | NUMBER 38 位精确数、负值、NVARCHAR2 补充字符/引号/尾空格、OBJECT 元素及集合元素；重复绑定/重复执行、NULL/empty 根集合、NULL 标量/内部集合、全 NULL 属性对象。独立连接 text/Prepare + SequentialAccess 逐行核对顺序、形状及精确值 |
| 错误前缀和会话 | ID=4 的 CALL 成功，ID=1 的重复键错误阻止 ID=5 的 CALL；独立读回只含成功前缀；同写会话可复用。表、过程、类型均由 DatabaseContext 清理，清理错误使测试失败 |
| NULL OBJECT 身份证据 | 首次新实库 10 项中 8 通过、2 失败：集合的 NULL OBJECT 在 PL 中报告非 NULL；固定 SQL 构造的 binary 读取再复核 0/2 通过，实际变成全 NULL 属性对象。Java NULL/CAST 同样不保留身份，TREAT 返回 932/22001；另经 ServerSidePreparedStatement + ObArray/ObStruct 读回，NULL 和全 NULL 属性元素都成为非 NULL 全 NULL 属性对象，与 .NET 一致；未用这些形状宣称成功 |
| NULL OBJECT 拒绝 | 直接或间接位于集合子树中的 NULL OBJECT 在首次展开时明确拒绝；实库 sync/async 均确认流 Position=0、独立表 count=0、连接仍可用。将元素改成非 NULL 全 NULL 属性对象后，原命令成功、流消费 1 字节、两行持久化 |
| 本地专项 | 184/184 通过，0 跳过；涵盖新增集合、多元素/嵌套元素、NULL/empty、完整引用、既有预算/DDL/旧描述/流及 32 层共享描述回归 |
| 公开 DLL | 48/48 通过：新集合 12、nested OBJECT 12、flat OBJECT 12、既有 dictionary 12，代表资产 × sync/async × 普通/OB20 合成协议；精确 RAW 集合构造、NULL/empty、不可变快照、缓存失效和复用 |
| 构建/工具/共享夹具 | 五资产 Release 和 SecurityTests 构建均 0 警告/0 错误；五个 C# 文件 whitespace 核验通过。既有共享 encrypted dictionary 12/12 通过（沙箱外本地证书），不是实库 TLS 支持证据 |
| 最终实库专项 | 20/20 通过，0 跳过：新 NUMBER 构造 2、四种集合参数 8、NULL OBJECT 发送前拒绝 2、原 flat/nested OBJECT 回归 8；不含历史失败探测，失败证据单独保留 |
| 独立走查 | 最终代码、本文及 API 行复核无剩余阻断；核对红/绿、身份失败探测和 Java binary 对照，支持边界与证据一致 |
| 提交 | `52f454d`（`feat(command): expand named Oracle collection inputs in text scripts`）；本地提交，未 push |

当前仍未关闭的集合缺口如下，不计入本子项通过：

| 缺口 | 状态 | 重新开启条件 |
| --- | --- | --- |
| 持久 nested-table 列 | `待服务端` | 目标版本的列 DDL、实际存储及独立 binary 身份证据；当前三种列创建均失败 |
| 集合子树 NULL OBJECT | `待服务端/待证据` | 构造值和 PL/二进制结果都能区分 NULL 对象与全 NULL 属性对象；确认后再移除发送前拒绝，不先增加 CAST/TREAT workaround |
| 匿名集合、LOB/cursor、输出及 text 复杂结果 | 归原 D-02d/D-03/D-04 | 逐项参数方向、locator 所有权和结果 wire 证据，不能由命名 CALL 输入替代 |

证据保留于忽略目录 `TestResults/roadmap/d02d3-*.trx`、`d02d3-java-shapes.log`、
`d02d3-java-object-null.log`、`d02d3-java-binary-object-null.log`。NULL OBJECT 的失败探测报告为 `d02d3-object-null.trx`；随后改为
验证产品发送前拒绝的负向用例，未改写失败探测报告或跳过失败来扩大支持声明。
本轮只跑相关专项，没有重跑完整 Unit/Consumer、全部多语句组、其他 OS/runtime、真实 TLS/ODP/OB20/KDC
或长期负载。D-02/D-02d/D-04 父项保持未完成；下一步核对 D-02b/c 的服务端协议/方向前提，
不继续为已有服务器失败的集合形状增加猜测性实现。

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

### D-03：输出与 cursor 的剩余形状核对（2026-10-30）

基线 `985cad4`，初始工作区干净。本轮未修改产品源码；核对普通 cursor 列的完整读取，
为已有 PL 输出/REF CURSOR 生命周期补齐实库验证，并纠正架构文档中“尚未连接服务端”的
旧描述。以下进度只适用于当前 OceanBase 4.4.2.2 Oracle 直连、Windows、TLS Disabled：

| 子项 | 进度 | 本轮结论 / 下一步 |
| --- | --- | --- |
| D-03a 普通 SELECT CURSOR 列 | `服务端限制（当前目标版本已验收形状）` | 本地四字节 binary/text 解析已有；当前单值 binary 执行报 902，多行父值可返回但 FETCH 报 1002，Java 对同一形状同码。text 十进制解析也无法得到可读 child；当前形状跳过客户端开发，若未来 profile 提供不同可读取形状再重新评估 |
| D-03b OUT / REF CURSOR offset | `P0 / 待范围与 wire` | command 输出计划仍拒绝 UseCursorOffset；返回的 REF CURSOR 用独立普通 FETCH。Java CursorResultSet 在构造时对 RefCursor 排除 offset，RefCursor 走 cursorFetchInternal；没有可直接移植的输出 offset 成功证据。不新增滚动 API；若纳入须先定义归属、位置、版本和服务端契约 |
| D-03c 已有输出/普通 cursor 的生命周期 | `P4`，仅下述子集 | 新增 sync/async、显式/隐式 Prepare、极小/极大父 FetchSize、NULL INOUT、输出 offset 发送前拒绝、服务端后段错误后的原子输出与恢复执行；普通 cursor/offset 和已有 REF CURSOR 释放/结果限制回归见报告；不关闭 a/b 或全部参数类型组合 |

#### D-03a/b 的服务端与 Java 证据

- 官方 Java 2.4.14 显式 serverPrepareStatement，`useOraclePrepareExecute=false/true` 分别
  运行：单常量子查询在 execute 报 `902/HY000`；带 `UNION ALL ... ORDER BY` 的两行子查询
  能返回父行与非空 cursor 对象，但**真正读取 child** 报 `1002/HY000`。仅检查 getObject
  非空或列 metadata 会误报支持；本轮没有关闭既有列失败。
- Java text 对照使用显式 ClientSidePreparedStatement，避免普通 Statement 在 Oracle 模式
  默认 FetchSize 下自动选择 server prepare。单常量形状读到十进制字符串 `7`（与子查询常量
  相同），多行形状读到十字节十进制值；通过同一 Java protocol 以返回数字发送普通 FETCH，
  两者均报 `1002/HY000`。这一步是调查 wire，不是 Java 已提供 text cursor API 的声明。
- .NET 公共 API 探针也确认：显式 Prepare 后单值形状 execute 报 `902/HY000`，多行形状
  child FETCH 报 `1002/HY000`；这与本轮既有列测试的失败及 Java 读取阶段一致。
  忽略目录探针直接读取 COM_QUERY 行：多行子查询 metadata 类型 163、字段长度十字节，
  不是四字节 ID。两个常量样本 × autocommit 开/关四项，按返回数字直接发送 FETCH 均报
  `1002/HY000`。没有增加十进制猜测解码或对 ID 改位的补丁。
- 因此当前目标版本已覆盖的 scalar/多行 `SELECT CURSOR` 列形状满足“服务端不支持且 Java 同样交互”的
  归因条件，标记为服务端限制并跳过客户端开发；这不扩展到尚未观察到的其他服务端形状。
- 同一 Java profile 的单 PL 块 `OPEN ... FOR` 返回两行 REF CURSOR 成功；只能证明 PL 形状。
  固定 Java 源码 `6adc311` 的 `CursorResultSet` / `RefCursor`、protocol 普通/offset FETCH
  也已对照；RefCursor 明确排除父类 offset 选择，不能从普通 SELECT offset 推导输出支持。

Java 证据采集在本轮补核了连接选项：驱动解析会回写 Properties，探针每次连接改用独立
clone，并记录 effectivePrepareExecute/effectiveServerPrep 等布尔值后重跑。D-03 上述
true/false 已确认实际生效；D-02b/c 的双模式 prepare/cursor 与 Oracle callable 对照也重跑，
拒绝/成功结论不变，但连续 PL 块在 PrepareExecute=true 的错误阶段应为 execute，已更正
上表原有的 prepareCall 记录。旧日志保留为 `*-before-clone.log`，不再用它证明选项组合。

#### D-03c 的实库闭环

新增 `Oracle/RefCursorLifecycleTests.cs`，8 项分别组合 sync/async、显式/隐式 Prepare、
父 FetchSize=1/Int32.MaxValue。每项使用自建过程、表和独立 observer，最终按依赖顺序清理：

1. NULL 与普通 INOUT 各执行一次。先打开 command 的 UseCursorOffset，确认拒绝且输入流未读、
   HasSentSql=false、输出保持原值、没有新行；关闭 offset 后在同 command/物理连接执行。
   显式 Prepare 不消费流、不插入行；成功后 INOUT 为 1/8，child 独立 FetchSize=1 读完 1/2/3。
2. 过程先 INSERT、修改 INOUT、OPEN cursor，再故意重复 INSERT 导致服务端错误 `1`。确认错误
   outcome 已知、旧标量和 cursor 参数值均保留、同一物理连接可复用，独立查询没有失败调用
   的新增行；没有把“输出未发布”误当成所有存储过程的通用无副作用保证。
3. 修正同 command 的输入后重新执行，INOUT 为 11；释放父 command 后读取并释放 child。
   独立显式 Prepare 查询逐行核对三次成功调用的 id/RAW 字节，最后原连接 SELECT 1 成功。

| 检查 | 本轮结果 |
| --- | --- |
| IntegrationTests / Unit Release 构建 | 均为 0 warning / 0 error；本轮无产品源码/API 改动，不另称五资产全量验收 |
| 新增实库生命周期组 | **8/8**，无 skip，清理通过；`d03-lifecycle.trx` |
| 扩大 Oracle 实库回归 | **30/31**，无 skip；新增 8 项、既有 REF CURSOR、标量/RETURNING/过程与普通/offset cursor。唯一失败仍为既有 PreparedCursorColumnSurvivesParentCommandDisposal 的列形状；不删用例、不改断言、不以 PL OUT 覆盖 |
| 本地相关 Unit | **417/417**：cursor、offset、REF CURSOR、列/嵌套资源及 Oracle 输出计划；不是全量 Unit；`d03-local.trx` |
| 格式 | 新文件通过 dotnet format whitespace verify；MSBuild pipe 需要沙箱外执行，`d03-format.log` 记录退出 0 |
| 独立走查 | fresh-context reviewer `review_d03` 只读核对代码、原始报告、Java 源码与日志后通过；Properties 修正与有效选项日志另经专项复核，无待修问题 |
| 提交 | `098c655`（`test(oracle): verify output and cursor lifecycle against OceanBase`）；新增 8 项、本地检查和独立走查通过，本地提交，未 push；扩大回归 30/31 中未关闭的列形状继续保持失败 |

报告在本地忽略目录 `TestResults/roadmap/d03-{lifecycle,live,local}.trx`，wire / Java 日志为
`d03-text-wire.log`、`d03-java-shapes.log`；探针分别在 `TestResults/d03-cursor-probe/` 和
`TestResults/java-gap-probe/CursorShapeProbe.java`。最初 Java 探针只检查父对象，已改为读取
child 并固定显式 client 工厂；初始观察不作为成功证据。
本轮没有验证其他版本/平台、OBProxy、TLS/OB20/KDC、长期资源压力或所有 OUT 类型组合。
下一模块进入 D-04，按已支持的 named/anonymous、输入/输出、普通列/PL 与 LOB 成员拆分，
不重复实现 D-02d1～d3，也不以 PL 成功关闭 nested-table 列存储。

### D-04：复杂值成员、身份与生命周期（2026-10-30）

基线 `62e1929`，初始工作区干净。先按实际参数/结果契约拆分，不直接移除现有拒绝：

| 子项 | 进度 | 当前范围与下一步 |
| --- | --- | --- |
| D-04a 命名 LOB 成员 | `P2`；a1/a3.1 为 `P4` | a1：OBJECT/嵌套 OBJECT/collection 的 PL IN/OUT/INOUT、NULL 成员、输出 locator 释放与标量存储子集已验收。a3.1：命名根 NULL 输入改用 type 6 后通过；a3.2：OBJECT/嵌套 OBJECT 根 NULL 输出身份为当前服务端限制。a2：BLOB OBJECT 成员 DML 在 .NET 与 Java 同 wire 诊断回放均返回 932；按当前目标服务端限制跳过客户端改动。命名 locator 回绑仍拒绝，不隐式读取外部 LOB |
| D-04b 匿名 LOB 数组 | `P2`；b1 为 `P4` | b1：DML 迭代 locator 输入、事务结束/释放/旧快照的发送前拒绝与同会话恢复已验收；更广 PL 输出、DDL/reset 交错与长期资源压力仍未关闭，不把 DML 数组当普通 PL 集合 |
| D-04c 复杂 RETURNING 身份 | `P4`；whole-column named OBJECT 子集已验收 | Oracle PrepareExecute 的 whole-column `RETURNING` 返回 `schema/name` 为空、`element=255` 的 opaque metadata；仅当输出参数显式声明同一已加载 named `ComplexType` 时绑定，并按存储 national encoding 解码。匿名数组、未知 element、未声明类型仍拒绝 |
| D-04d 复杂 cursor 成员 | `P4`（当前目标形状为服务端限制）；新版本/新 wire 仍需重开 | 同一租户 `CREATE TYPE ... AS OBJECT (c SYS_REFCURSOR)` 在 Java 返回 `PLS-00201`、.NET 返回 `42000`；PL-only `RECORD (C SYS_REFCURSOR, N NUMBER)` 在 Java 与 .NET 均使 package 变为 `INVALID`，`ALL_ERRORS` 返回 `OBE-00600/-4007 attribute type of record is cursor type not supported`。两端都没有可读取的 child cursor wire，因此跳过客户端实现；若后续出现可创建的形状，再单独验收 child FETCH/所有权 |
| D-04e 复杂 text 结果及其余 collection 形状 | `P4`（named OBJECT 标量/递归 OBJECT text 子集）；RAW/DATE/TIMESTAMP text 形状为当前服务端限制；collection 构造 text 结果已确认是 String；其余 `P2` | named OBJECT 的 NUMBER、VARCHAR2/CHAR、NCHAR/NVARCHAR2 及递归 named OBJECT text 结果已实现并验收；当前 RAW/DATE/TIMESTAMP text wire 重复同一 RAW payload，Connector/J 同交互也返回相同字符串且 `getObject` 为 null，因此跳过客户端 parser；命名 collection 构造结果 metadata 为 `VARCHAR2`，.NET/Connector-J 均按 String 返回，不新增 complex parser；LOB/cursor 仍明确拒绝。持久 nested-table 列和集合 NULL OBJECT 继续保留既有服务端阻塞 |

本模块先验证 a/b 已有实现的边界；若实库暴露缺陷，先保留失败证据、补最小回归，再决定
共享修复。所有新增计划、结果及提交号仍记录在本文：

本轮补充的 D-04d 对照使用同一 Oracle 4.4.2.2 直连 profile。SQL OBJECT 持久类型仍是
Connector/J `PLS-00201`、.NET `42000` 的创建阶段失败。新增 PL-only 负向回归创建
`TYPE R IS RECORD (C SYS_REFCURSOR, N NUMBER)`：Java 查询 `ALL_OBJECTS/ALL_ERRORS`
确认 package/package body 为 `INVALID`，错误为 `OBE-00600/-4007 attribute type of record is
cursor type not supported`；.NET 同一 DDL 后读取到相同 `INVALID` 和错误文本，sync/async **2/2**
通过。当前形状没有 child cursor wire，按服务端限制跳过客户端实现；若后续版本提供可创建的
对象或 PL-only 记录 wire，仍需重新采集 Java/.NET 的完整 child FETCH、所有权和释放证据。

- [x] 核对 codec、输出身份校验、现有实库及合成测试，确认 named payload 与 anonymous locator 的不同契约。
- [x] 验证命名 LOB 成员的嵌套、输出与独立存储闭环，记录真实成功/失败形状。
- [x] 验证匿名数组旧 locator 的执行前拒绝及修正后同会话恢复。
- [x] 运行相关构建/测试，独立只读代码走查；只为证据成立的子集更新进度。
- [x] 本地提交，将提交号和未关闭边界写回本文。

#### D-04a1 / b1：已实现 LOB 子集的实库生命周期

本轮新增 `Oracle/ComplexLobLifecycleTests.cs`，无产品源码/API/选项变化。验收只适用于
OceanBase 4.4.2.2 Oracle 直连、Windows、TLS Disabled：

- 命名输出 12 项：OBJECT、嵌套 OBJECT、collection × BLOB/CLOB × sync/async。IN 与
  INOUT 使用不同内容，逐一验证 OUT/INOUT 身份及内容；较大值超过 90 KB，CLOB 含补充字符。
  同 command 重执行验证 NULL 成员（collection 的两个 NULL 元素不当成空集合）。整个根值
  NULL 的调用在此基线返回 `600/HY000`，当时负向断言只确认旧输出不变、outcome 已知、会话仍可用，
  **不计为根 NULL 支持**；后续 a3.1 已修复输入，a3.2 继续跟踪输出身份。父命令释放后，先用返回 locator 作**普通标量参数**写入持久列，
  再读两个旧输出；独立连接显式 Prepare + SequentialAccess 核对存储 LOB、RAW 标记及行数。
  返回 locator 继续不能用来构造命名 LOB 输入；每个 handle/stream 由调用方释放。
- 匿名数组 12 项：BLOB/CLOB × sync/async × dispose/commit/rollback。失效前创建数组快照，
  在有效事务中执行旧快照应发送前拒绝，HasSentSql=false、独立行数不变、物理会话不变。
  用新 descriptor 构造旧 locator 也拒绝；在相同 command/物理会话换成有效 locator 后插入
  两行（非 NULL/NULL），提交后由独立显式 Prepare + SequentialAccess 查询核对每个 id/LOB。
  commit/rollback 场景会开启新的显式事务；没有放宽 ExecuteArray 的事务前提。

#### 本轮新确认的两个缺口（未关闭）

| 子项 | 证据与归类 | 下一步开发任务 / 关闭条件 |
| --- | --- | --- |
| D-04a2 BLOB OBJECT 绑定成员直接用于 DML | 最初 12 项存储探测 8 通过、4 失败：BLOB OBJECT/嵌套 OBJECT 在过程 INSERT 成员时报 `932/22000`；CLOB 三形状和 BLOB collection 成功。3 字节/90007 字节、直接成员/局部变量两种 bind 写法都失败；相同类型的服务端本地 RAW 构造或真实临时 BLOB 构造写入成功。PL GETLENGTH、对象复制与不含 DML 的复杂输出成功；TO_BLOB 转换也未解决 bind DML 失败。后续 Java wire replay 发出同样 bitmap/length-encoded bytes 后返回同码，当前目标版本按服务端 bind/DML 限制处理 | `P2 / 当前目标服务端限制`。保留客户端 payload framing 断言和 8 项回归；只有新版本/租户提供不同成员 wire 时重开，不自动改写用户 SQL |
| D-04a3 根 NULL 复杂 PL 绑定 | 初始 NULL 扩展探测 12/12 在根 NULL 调用报 `600/HY000`，成员 NULL 调用通过。后续已定位命名 NULL 的 type 160 编码缺陷并修复为 type 6；输出身份另行分离，见下节证据 | a3.1 `P4`：命名根 NULL 输入及指定 collection NULL 输出子集；a3.2 `服务端限制（当前目标版本 OBJECT/嵌套 OBJECT 输出身份）`：本地与 Java 均收到未置 NULL 位的 OBJECT 结果，当前形状跳过客户端改动 |

固定 Java 源码 `6adc311` 的 `ComplexUtil.storeComplexAttrData` 没有 TYPE_BLOB/TYPE_CLOB
writer 分支，default 拒绝；其 OBJECT/collection 输出测试含 LOB，并不证明相同的 payload
输入及 DML 组合完整可用。本轮没有运行 Java LOB 输入对照，不用 Java 静态能力替代上述
bind wire 的归因证据，也不对未知服务端形状增加猜测性编码。

| 检查 | 本轮结果 |
| --- | --- |
| 既有复杂 LOB 输入基线 | 12/12，通过且无 skip；`d04-baseline.trx` |
| 新生命周期组与扩大实库回归 | 40/40，通过且无 skip：新增 24、既有复杂 LOB 输入 12、既有标量 LOB 4；`d04-live-final.trx`。根 NULL 为上述负向断言，不是成功支持 |
| IntegrationTests Release 构建 | 0 warning / 0 error；`d04-build.log` |
| 本地相关 Unit | 1085/1085，无 skip：Complex/LOB 相关组；Unit Release 构建 0 warning / 0 error；`d04-local.trx` / `d04-unit-build.log`，不是完整 Unit |
| 格式 | 新文件 dotnet format whitespace verify 通过；MSBuild pipe 需要沙箱外运行，`d04-format.log` 记录退出 0 |
| 独立走查 | fresh-context reviewer `review_d04` 只读核对新测试、拥有者源码、原始通过/失败报告与 Java writer 分支；无阻断问题，批准本地提交；未运行实库或更改工作区 |
| 提交 | `7c75a14`（`test(oracle): verify complex LOB output and array lifecycles`）；新生命周期测试和事实边界记录，本地提交，未 push；无产品源码/API 变化 |

原始失败保留在忽略目录 `TestResults/roadmap/d04-named-{first,codes}.trx`、`d04-null-first.trx`
及 `d04-live.trx`，不能用最终 40/40 抹去这些未支持形状。`d04-blob-shapes.log` /
`d04-blob-shapes-with-cast.log` 与 `TestResults/d04-complex-probe/` 保留本地构造、bind、
读长度/复制/存储和输出的分阶段调查。最初匿名数组夹具漏开后继显式事务、BLOB 字符串常量
被按 hex 解释的失败已修正；改用明确 TO_BLOB(HEXTORAW(...)) 存储种子，未修改产品事务或字节语义。

本轮未验收复杂 RETURNING、复杂 cursor/text、DDL/reset 交错、跨平台/全资产消费者、长期压力、
真实 TLS/ODP/OB20/KDC；D-04/D-04a/D-04b 父项保持 `P2`。后续从 a2/a3 的类型身份和 NULL
wire 分解入手，或按 c/d/e 的先决证据推进，仍只在本文记录进度。

#### D-04a3 续：命名根 NULL 的输入 wire 与输出身份

基线 `06f35c2`，工作区初始干净。固定 Java `BasePrepareStatement.setNull` 的 JDBC 重载
使用 `NullParameter()`，发送 type 6、不发送复杂身份；当前 .NET 对命名 NULL 仍发送 type 160
及身份。Java 新实库对照中 NUMBER/BLOB/CLOB × OBJECT/collection/嵌套 OBJECT ×
PrepareExecute=false/true 共 18 个配置，过程内 `p IS NULL` 均为真；collection 的 NULL 输出
也保留，但 OBJECT/嵌套 OBJECT 的 getObject/wasNull 未报告 NULL，不能据此关闭输出身份。
Properties 每次 clone，日志记录有效选项，按阶段检查结果，不用探针退出 0 当作全部通过。

本轮先确认并修复命名根 NULL **输入编码**，保留同一 public ComplexType 的会话/事务/缓存和
模式验证。不改匿名数组的 wire，不猜测 OBJECT NULL 输出；若输入和输出证据不同，分别更新
子任务，D-04 父项不关闭。计划仍在本文：

- [x] 对照所有 Encode 调用、NULL header 测试、Java NULL 参数及执行编码。
- [x] 新实库输入回归（null/DBNull、首执行/值切换、普通 prepare/PrepareExecute）及本地协议回归先失败。
- [x] 依据对照仅修复共享编码，维护负向会话/字典/LOB negotiation 校验；确认旧输出测试的实际返回形状。
- [x] 相关 Unit、公开 DLL、目标实库、构建及格式检查；独立只读走查通过后本地提交。
- [x] 将最终证据、提交号和仍未支持的输出身份写回本文。

修复只改 `PreparedParameters.Encode` 的一个条件：命名复杂输入为 null/DBNull 时发送 type 6，
不写无值的 type 160 身份；ComplexType 仍保存在参数快照，原有 Oracle 模式、会话、事务、
缓存、类型匹配和 LOB 协商校验仍执行。非 NULL 命名值、匿名数组（含匿名根 NULL）及纯 OUT
的编码契约不变；没有新增 API、选项、SQL 重写、回退或重放。所有 Encode 调用已核对。

新增 `Oracle/ComplexNullInputTests.cs`：24 项组合覆盖 NUMBER/BLOB/CLOB、OBJECT/collection/
嵌套 OBJECT、sync/async；普通 prepare 只验收 NUMBER，LOB 保持 PrepareExecute/locator
协商前提。显式 Prepare 无 DML，随后同 command 四次切换 CLR null、非 NULL 全 NULL 成员值、
DBNull、非 NULL 全 NULL 成员值，独立连接显式 Prepare + SequentialAccess 读回四行 id 和
服务端 `p IS NULL` 标志 `1/0/1/0`，物理会话不变。首次和重执行根 NULL 输入均不再报 600。

既有 12 项 LOB 生命周期用例更新为正确的 NULL 输入语义：过程在修改 OUT/INOUT 之前确认
根 NULL 的 p 与 r 均满足 IS NULL，collection 的 OUT/INOUT 返回 DBNull；OBJECT/嵌套 OBJECT
仍返回非 NULL 的对象图及 NULL LOB 叶子。这一断言记录 a3.2 的现存失败边界，**不是 NULL
OBJECT 输出身份已保留**。旧 LOB handle 在这些重执行及父命令释放后仍可读取/绑定标量存储。

原始 wire 探针进一步使用 NUMBER 成员的三种形状、服务端直接 `p:=NULL` / type 6 bind 两种
赋值方式，6 项都读完最终响应：OBJECT/嵌套 OBJECT 的输出列 type 160、外层 NULL 位为 false，
binary row 为 `0000040131`；collection 的 NULL 位为 true，row 为 `00040131`。末列值 1
证明 PL 内 p IS NULL 成立。这发生在 .NET 值解码之前，与 Java OBJECT/collection 观察一致；
不能把属性 NULL 位当外层 NULL 位，也不能将所有全 NULL 对象改成 DBNull。Java 对 OBJECT/嵌套
OBJECT 的 `getObject/wasNull` 也未报告外层 NULL，满足“服务端不支持且 Java 同样交互”的归因条件；
因此 a3.2 的这两个形状标记为当前服务端限制并跳过客户端改动。collection 的可区分 NULL 结果和
其他复杂输出身份仍保持原边界，若未来服务端提供新的 OBJECT NULL wire，再重新评估。

| 检查 | 本轮结果 |
| --- | --- |
| 红/绿 | 新输入初次 36 项中，18 个 PrepareExecute 配置在目标执行失败，另 18 个在连接夹具中先因 locator/PrepareExecute 选项冲突失败；后者不计协议证据。修正夹具并按前提列出 24 项后全部通过。本地 8 项更新断言初次 6 失败（NULL header），修复后通过；原多字节标识符用例改用非 NULL、成员 NULL 的对象继续核对完整 identity 长度 |
| 目标实库回归 | **62/62**，无 skip：新输入 24、LOB 生命周期 24、既有复杂 LOB 输入 12、collection PL 输出 2；`d04-null-live-final.trx`。OBJECT NULL 输出边界仍如上，不作为身份成功 |
| 本地相关 Unit | **1085/1085**，无 skip；`d04-null-local-final.trx`。含 typed NULL、旧描述、LOB 协商、输出身份/原子发布及匿名数组回归；不是完整 Unit |
| 公开 DLL | **12/12** 普通/OB20 合成协议 + **12/12** 共享合成 TLS，代表资产 × sync/async；typed named NULL 明确检查 type 6、anonymous NULL 仍检查原 header；`d04-null-{consumer,security}.trx`。本地证书需要沙箱外执行，不是实库 TLS/OB20 证据 |
| 构建 | 五资产产品、Integration/Unit/Consumer/Security Release 均 0 warning / 0 error；`d04-null-*-build.log` 及 `d04-null-build.log` |
| 格式 | 五个 C# 文件 whitespace verify 退出 0；既有 ComplexParameterTests.cs 全文件仍有 48 个旧行格式诊断，逐行与 HEAD 对照均在未改变的行，新增/改动行未报格式问题，未全文件重排；`d04-null-format{,-baseline,-scoped}.log`。git diff --check 通过 |
| 独立走查 | fresh-context reviewer `review_d04_null` 只读核对全部 Encode 调用、描述/会话/LOB 校验、普通 prepare 与 PrepareExecute 的 NULL 切换、输出 metadata 所有权、架构/本文边界及原始 TRX；无待修问题，同意本地提交；未运行实库或修改文件 |
| 提交 | `322c388`（`fix(oracle): encode named complex null inputs as MYSQL_TYPE_NULL`）；输入修复、24 项实库回归、既有 NULL header/生命周期断言及架构事实同步；本地提交，未 push；a3.1 指定子集为 `P4`，a3.2 的 OBJECT/嵌套 OBJECT 输出身份按当前服务端限制跳过，D-04 父项仍为 `P2` |

调查证据在忽略目录 `d04-null-java{,-detailed}.log`、`d04-null-wire.log` 与
`TestResults/d04-null-wire-probe/`；Java runner 固定版本、每次 clone Properties、有效选项均有记录。
原 `d04-null-red.trx`、`d04-null-output.trx`、`d04-null-live.trx`、
`d04-null-local{,-red}.trx` 和初次格式报告全部保留，不以最终通过删除输入/输出失败历史。
本模块只将 a3.1 的上述输入和 collection 输出子集置为 `P4`；a3.2 的 OBJECT/嵌套 OBJECT 输出身份已按当前服务端限制跳过，D-04 父项仍不关闭。
未重跑完整 Unit/Consumer、所有 OS/runtime、真实代理/TLS/OB20/KDC、长期压力；BLOB OBJECT
成员 DML、复杂 RETURNING/cursor/text、匿名 PL 等剩余任务仍需后续开发与证据。

#### D-04a2：named BLOB OBJECT DML 回归覆盖（2026-10-31，当前目标服务端限制）

新增 `ComplexLobInputTests.NamedBlobObjectMembersRemainServerLimited`，覆盖 flat/nested named OBJECT、
3/90007 字节、sync/async，并用独立连接核对失败后没有写入行；同时补充 flat/nested named BLOB payload
framing 的本地断言。相关 Unit/协议组 **33/33** 通过。VPN 恢复后以 `oracle-direct` 直连
`172.16.2.45:2881` 重跑，8/8 均进入目标 DML 并稳定返回服务端 `932/22000`（大小、嵌套和
sync/async 均一致），没有读回行可验收。

固定 Connector/J 2.4.14 公开 API 仍没有真实 BLOB 成员 writer，`createStruct` 和
`StructImpl + Blob` 都在 `storeComplexAttrData` 前置失败；这条路径单独不能归因服务端。
随后使用同一 SDK 的 `StructImpl` 和 `ComplexUtil.storeComplexAttrData`，仅把已加载的 BLOB
成员 descriptor 在诊断探针中替换为 SDK 已支持的 RAW type=6，使其发出同样的 bitmap、
length-encoded bytes 和成员顺序。该 Java wire replay 在同一 4.4.2.2 `oracle-direct`
profile、同一 flat OBJECT、同一 6-byte payload 上到达服务端并返回 `OBE-00932/932/22000`，
独立查询行数为 0；.NET 的 flat/nested、3/90007 字节、sync/async 也稳定返回同码。

因此 D-04a2 现在标记为 `P2 / 当前目标服务端限制`，不修改 `ComplexValueCodec`，不把
Connector/J 公开 API 的前置 writer 错误冒充同一交互。只有目标版本/租户接受该成员 wire，
或出现新的服务端 BLOB OBJECT bind 形状时，才重新开启客户端实现。

#### D-04a2 服务端源码 wire 对照（2026-11-01）

固定 OceanBase 服务端源码提交 `5e8a63d` 后，named OBJECT 的成员输入路径可以逐层对照：
`ObRecordType::deserialize` 先跳过每个 OBJECT 的成员 NULL bitmap，再按成员顺序调用
`ObPLDataType::deserialize`；普通 BLOB 成员由 `ObSMUtils::get_mysql_type` 映射为
`MYSQL_TYPE_ORA_BLOB`，随后进入 `ObMPStmtExecute::parse_basic_param_value`。该分支读取
length-encoded string；BLOB 设置 binary collation，并将 payload 包装成带 LOB header 的
`ObLongTextType` 值。当前 `ComplexValueCodec` 的 named BLOB 编码正是同一层级的 bitmap、
length-encoded raw bytes，不需要匿名数组的 locator/header 输入格式。

因此本次源码对照没有发现 .NET writer 与服务端成员反序列化 wire 的差异；配合 Java wire
replay 的同码失败，失败范围收窄到服务端接收后的 BLOB OBJECT bind/DML 转换。D-04a2
按当前目标服务端限制关闭客户端开发，不改产品编码。源码依据：[ObRecordType::deserialize][O12-record-deserialize]、
[ObPLDataType::deserialize][O12-pl-deserialize]、[parse_basic_param_value][O12-basic-param]。

#### D-04a2 Connector/J 2.4.14 公开 API 实测（2026-11-01）

为确认“Java 同样失败”条件，使用同一 Oracle 4.4.2.2 `oracle-direct`、TLS Disabled
profile 运行 Connector/J 2.4.14。Java 能从服务端读取真实描述：根 `type=3`（OBJECT），
唯一成员 `attrType=10`（BLOB）。随后分别使用 `Connection.createStruct(type, byte[])`，
以及 `StructImpl` 配合 `com.oceanbase.jdbc.Blob` 构造值；两条公开构造路径都在客户端
`ComplexUtil.storeComplexAttrData` 阶段抛出 `SQLNonTransientConnectionException:
storeComplexAttrData exception`，异常链止于 `IOException`，目标表行数均为 0。

这两个 Java 调用没有发出与 .NET 相同形状的 BLOB OBJECT DML，也没有得到服务端 `932/22000`；
它们只证明公开 API 的 writer 边界。后续的 descriptor override wire replay 已补足同交互证据，
原始公开 API 输出仍保存在忽略目录 `.tmp/d04a2-java-public-api.log`，不提交凭据或探针源码。

#### D-04a2 Java wire replay（2026-11-01）

诊断探针保留服务端返回的根 OBJECT descriptor，仅把 BLOB 成员 descriptor 临时替换为
Connector/J 已支持的 RAW type=6，复用同一 `StructImpl`/`ComplexUtil.storeComplexAttrData`
writer。该替换不改变 OBJECT 根身份、NULL bitmap、成员顺序或 length-encoded payload，
只绕过 Connector/J 2.4.14 对真实 BLOB type=10 缺失 writer 的前置异常。相同 6-byte payload
在服务端返回 `OBE-00932/932/22000`，独立查询行数为 0；因此当前目标版本的该成员 wire
按 Java/.NET 同交互失败处理，客户端不增加猜测性编码。

#### D-04c：复杂 whole-column RETURNING 的 opaque metadata（2026-10-31）

新增 `Oracle/ComplexReturningTests.cs`，在 Oracle 4.4.2.2 `oracle-direct`、TLS Disabled、
`UseOraclePrepareExecute=true` 下覆盖 named OBJECT 的 `INSERT ... RETURNING value INTO :o`，
sync/async **2/2** 通过。服务端响应列固定为 type 160、空 schema/name、anonymous element 255；
它没有可解析的 source identity，但输出参数已显式绑定同一会话加载的 named `ComplexType`，因此可
安全使用该声明的完整 layout 解码。含 NCHAR/NVARCHAR2 成员的 whole-column RETURNING 另以
14 组客户端字符集 × 两种同步方向验证，**56/56** 通过；这一路径使用类型上下文记录的存储
national encoding，不能套用 PL output 的 session result encoding。

产品修复集中在 `ResultColumn.BindComplexOutput`：只放行空 identity + element 255 + 显式
named type 的精确形状；匿名数组、其他未知 element 和没有显式 ComplexType 的输出继续拒绝。
新增本地协议单测覆盖该正向绑定及匿名类型拒绝；原有输出值原子发布和 unknown-outcome 保护仍保留。

| 检查 | 本轮结果 |
| --- | --- |
| Oracle direct 实库 | `ComplexReturningTests` 2/2；`ComplexNationalTests.NationalObjectReturningUsesDeclaredOutputType` 56/56；无 skip |
| 本地协议单测 | opaque named OBJECT 绑定/匿名类型拒绝 1/1 |
| 构建 | IntegrationTests Release 0 warning / 0 error；NuGet 漏洞源不可达时使用 `NuGetAudit=false`，依赖已由本地缓存还原 |
| 未关闭边界 | D-04a2 已按当前目标服务端限制关闭客户端开发；复杂 cursor/text/其他 collection 形状、跨版本/代理/TLS/长期压力仍未关闭；D-04 父项保持 `P2` |

#### D-04e：named OBJECT 标量 text 结果（`P4` 子集，2026-10-31）

本轮先用同一 Oracle 4.4.2.2 `oracle-direct`、TLS Disabled 租户确认真实 wire：普通
text 查询返回 type 160，复杂 metadata 带 type name 但 schema 为空，payload 形如
`TYPE(7, 'a,b (c) 'q'')`；显式 Prepare 的 binary 路径继续保持原实现。这个证据足以固定
本轮最小 grammar，但不支持把所有复杂结果形状泛化为 text 可读。

实现覆盖预加载的 named、非 collection OBJECT 及递归 named OBJECT 成员，叶子限 NUMBER、
VARCHAR2/CHAR、NCHAR/NVARCHAR2，并处理 NULL、严格 UTF-8、引号/逗号/括号、完整 payload
消费、递归深度/成员预算和既有包大小上限。无 schema 的 text metadata 只有在缓存中存在唯一
同名 type 时才绑定；同名多 schema、缺 descriptor、anonymous/collection、RAW、时间、LOB、
cursor 和尾随/畸形 payload 均继续发送后拒绝或解码前拒绝。NULL 整列仍返回 `DBNull.Value`，
NULL 成员保留为 `null`；SequentialAccess 的 text 字段按普通 length-encoded framing 消费，
binary framing 未改变。

| 检查 | 本轮结果 |
| --- | --- |
| 单元与协议 | `ComplexValueCodecTests` **63/63**；共享 `TextCommandTests` **5594/5594** 通过；覆盖 NUMBER/字符成员、递归 OBJECT、NULL、引号/分隔符、严格 UTF-8、大小上限、深度上限、唯一/歧义 type 名称和未支持布局 |
| Oracle 实库 | `ComplexTextResultTests` **4/4** 通过，0 skip：sync/async × Default/SequentialAccess；递归 named OBJECT 两行覆盖子对象 NUMBER、NULL、逗号/括号/引号字符、后续 scalar 列和完整 reader 消费；连接串来自项目 User Secrets 的 `ConnectionStrings:Oracle`，未使用凭据环境变量 |
| 清理与范围 | 随机 type/table 由 `DatabaseContext` LIFO 清理；只关闭上述 named scalar text 子集，不关闭 collection/嵌套/LOB/cursor/RAW/时间或跨版本、代理、TLS、长期压力组合 |

该子集已达到 `P4`；D-04e 其余 collection/复杂成员形状仍为 `P2`，D-04 父项保持未完成。

#### D-04e nested OBJECT extension（2026-11-01）

Connector/J 2.4.14 对同一 Oracle 4.4.2.2 直连 profile 返回递归普通 text 结果，例如
`ROOT(CHILD(7, 'a,b (c) 'q''), 9)`。当前 .NET 原 parser 只接受根 scalar named OBJECT，
会在 descriptor 校验阶段拒绝 nested OBJECT；这属于客户端缺失能力，不是服务端限制。

`ComplexValueCodec.TextResultParser` 现在递归消费预加载的 named OBJECT descriptor，并将嵌套
数组交给现有 `OceanBaseComplexValue.FromResult` 做精确子类型 materialization。每一层校验
type name、成员数量、递归深度和总成员预算；collection、anonymous、RAW、时间、LOB、cursor
和缺失/歧义 descriptor 继续拒绝。binary OBJECT、参数 writer 和 public API 未改变。

| 检查 | 结果 |
| --- | --- |
| 红/绿 | 新增 nested 正向、collection/类型名/深度负向用例；新增后先按预期在旧 parser 红灯，修复后 `ComplexValueCodecTests` **63/63**、共享 `TextCommandTests` **5594/5594** 通过 |
| Oracle 实库 | `ComplexTextResultTests` **4/4** 通过，0 skip：`oracle-direct`、sync/async × buffered/sequential；User Secrets `ConnectionStrings:Oracle`，未设置凭据环境变量 |
| 走查边界 | 只扩展 Java 与目标 wire 已证明的递归 named OBJECT 标量叶子；collection/复杂成员、持久 nested-table 列和集合 NULL OBJECT 仍保持原拒绝/服务端边界 |
| 提交 | `186c813`（`✨ feat(oracle): support recursive named OBJECT text results`）；D-04e 递归 named OBJECT 标量子集达到 `P4`，其余形状仍为 `P2` |

#### D-04e RAW/DATE/TIMESTAMP text 成员（当前目标服务端限制，2026-11-01）

为判断是否继续扩大 text parser，使用项目 User Secrets 的 `oracle-direct` profile 建立
`OBJECT (r RAW(8), d DATE, ts TIMESTAMP(9))`，写入不同的 RAW、DATE 和 TIMESTAMP 值，
然后读取普通 text 结果。OceanBase 4.4.2.2 返回的 payload 是同一个 RAW 字符串重复到三个成员，
形如 `TYPE('0027FF\\0', '0027FF\\0', '0027FF\\0')`；最后一个字符串还带 NUL。该 payload
无法证明任何 DATE/TIMESTAMP 成员身份，.NET 当前 parser 在发送后安全拒绝未支持成员。

同一对象、同一租户、同一写入值用 Connector/J 2.4.14 重放：`ResultSet.getString` 返回完全
相同的重复 RAW payload，`ResultSet.getObject` 返回 `null`。因此这是服务端 text wire 形状，
不是 .NET parser 缺少日期格式；本轮不新增 RAW/DATE/TIMESTAMP text 解析或猜测性转换，按
“服务端不支持且 Java 同样交互”关闭该形状。后续若版本/租户提供每个成员正确身份的 text
payload，再重新采集 Java/.NET 对照并开启 D-04e；collection、LOB/cursor text 形状仍未关闭。

同一 profile 另建 `TABLE OF VARCHAR2(80)` 命名集合并执行构造查询。该结果 metadata 在 .NET
和 Connector/J 2.4.14 中都是 `VARCHAR2`，字段值都是普通 `String`，内容为类型名加字符串成员；
两 SDK 没有收到 type 160 collection wire。因此这是服务端选择的标量结果身份，不是 .NET
缺少 collection text parser；持久 nested-table 列、collection NULL OBJECT 身份和 LOB/cursor
复杂 text 结果仍按各自未关闭边界处理。

#### D-04e named OBJECT CLOB/BLOB text 结果（当前目标服务端限制，2026-11-02）

为关闭复杂成员的下一种形状，使用同一 Oracle 4.4.2.2 `oracle-direct` profile 建立
`OBJECT (child OBJECT (c CLOB, b BLOB, n NUMBER), marker NUMBER)`，通过普通 text 查询读取
非空对象。Connector/J 2.4.14 的 metadata 为 `1111/COMPLEX`，`getObject()` 返回 `null`；
`getString()` 返回的对象文本把 CLOB/BLOB 成员折叠成无法区分身份的重复字符串（本轮实测为
`ROOT(CHILD('clob text', 'clob text', clob text), 9)`），`getBytes()` 也只是整个文本字段的
字节，不提供成员 locator 或独立类型身份。

同一 DDL、写入值和查询在 .NET 中预加载 named OBJECT 描述后，sync/async **2/2** 都在结果
metadata 阶段以 `OceanBaseException` 结束，`IsExecutionOutcomeUnknown=true`，内部原因是
复杂 text 结果包含不支持的 LOB 成员类型；连接按未完整消费响应的安全边界进入 Broken。该
差异是两个 SDK 都没有可用的 CLOB/BLOB complex text materialization，根因是服务端 text
payload 没有可可靠解释的成员身份，不能通过继续扩大 parser 或猜测 locator 语义修复。

新增 `ComplexTextResultTests.NamedObjectLobTextResultsRemainServerLimited`，使用项目 User
Secrets 的 `ConnectionStrings:Oracle`，覆盖 sync/async、类型描述预加载和清理；实库 **2/2**
通过。当前版本按服务端 wire 限制跳过 CLOB/BLOB complex text parser；若后续版本提供每个成员
正确的 type identity、NULL 和 locator/text 编码，再重新进行 Java/.NET 对照。该结论不扩展到
binary complex LOB 路径或匿名 locator 输入；PL-only cursor 成员的当前目标形状另见 D-04d，已由
Java/.NET 同一 `-4007` 服务端错误关闭。

#### 已确认服务端限制的 Integration 契约（2026-11-02）

本轮将已由 .NET 与 Connector/J 对照确认的四类当前目标服务端限制写入 Integration
profile 的 `capabilities.serverLimitations`：Oracle EUCKR 普通客户端初始化、Oracle
SJIS/Big5 初始化、nested-table 持久列和 `SELECT CURSOR` 列形状。测试属性在 xUnit
发现期只跳过声明了对应限制的 profile；未声明限制的 profile 仍会执行并暴露新的服务端
或客户端失败。多 profile 运行时，只有所有选中 profile 都声明该限制才跳过整项测试。

本轮双模式限制专项结果为 **84 passed / 5 skipped / 0 failed**；这些 skip 保留了服务端
负向证据，不把客户端能力改写成已实现。配置解析、混合 profile 行为和限制元数据保留
均有单元回归。新版本或新租户提供不同 wire 后，应删除对应 limitation 声明并重新跑原始
正向用例，而不是扩大 skip 条件。

### D-05：UROWID 参数与结果闭环（2026-10-30）

基线 `ae40dba`。D-04a2 继续核对服务端命名成员 deserialize 与 Java writer；Java
`ComplexUtil.storeComplexAttrData` 未提供 BLOB/CLOB writer，没有足够证据选择新的成员编码。
本轮不改 D-04 产品代码、不关闭 a2，转入 D-05 的有证据范围。

服务端 `MYSQL_TYPE_OB_UROWID=209` 参数路径读取 length-encoded base64 文本，再由
`ObURowIDData::decode2urowid` 校验；结果路径同样返回 base64 文本。复用现有严格文本编码
和包预算，增加显式 `OceanBaseDbType.Urowid`，不猜测 UROWID 私有格式。普通字符串不会自动
推断成 UROWID。旧结果 `ROWID` / ProviderType.String 保持兼容，新增 DataTypes.UROWID 表示
显式参数身份；client literal/fallback 不在本模块支持范围。

- [x] 确认 type 209 参数/结果 wire，补协议、NULL、模式/声明、严格编码及目录映射测试。
- [x] 实库普通 prepare / PrepareExecute、sync/async、短/长真实 ROWID、NULL 切换与独立存储读回。
- [x] 补 PL OUT/INOUT/返回值、RETURNING、真实 UROWID 参数推导及失败后恢复；目录的其他类型边界转 A-03 跟踪。
- [x] 公开 DLL 消费者、相关回归、五资产构建和格式检查，独立只读走查。
- [x] 本地提交并回填证据、提交号和未验收范围；进度仅在本文维护。

新增实库 `Oracle/UrowidTests.cs` 10 项：

- 输入 4 项组合普通 prepare/PrepareExecute 与 sync/async。自建不同长度主键源表取得真实
  ROWID（长值超过 251 字符），同一显式 Prepare command 依次绑定 CLR null、短值、DBNull、
  长值，再绑定坏值 `invalid!`。确认服务端明确拒绝、outcome 已知、独立存储没有坏值行，
  换回有效值可在同一物理会话继续插入。独立连接分别用普通查询与显式 Prepare，逐行核对
  id/NULL/完整文本并消费 EOF；Size=1 不截断，结果 metadata 仍是 ROWID/string。
- 输出 6 项组合过程/函数/RETURNING 与 sync/async，使用真实长 ROWID。过程/函数通过真实
  DeriveParameters 得到 UROWID 的 IN/OUT/INOUT/ReturnValue；显式 Prepare 不插入数据、不发布
  输出。同一 command 四次切换 NULL/非 NULL，逐一核对输出。过程先修改 OUT/INOUT 再因重复
  主键失败，旧输出全部保留；修正 id 后在同一物理会话成功。独立连接显式 Prepare +
  SequentialAccess 核对最终五行（NULL/非 NULL），失败调用不增加行。

#### A-03 的新增目录复核任务

早期 UROWID 过程夹具的编号参数使用无约束 NUMBER 时，DeriveParameters 报
`Decimal argument scale cannot be represented`；改为 VARCHAR2 时，读取字符长度目录的第二条
查询被当前服务端拒绝。这不是 UROWID wire 的失败，也不能仅据这些异常断定服务器没有该类型。
两项均并入 A-03：固定实际 NUMERIC_SCALE、CHAR_LENGTH 可用性/查询错误码和最小权限，补独立
回归后决定映射或版本拒绝。最终夹具使用已有验收的 FLOAT 编号，**保留真实 UROWID 参数推导**，
未关闭上述其他类型的目录边界。A-03a 已完成；A-03b 的当前目标服务端限制已单独归因，见下文。

| 检查 | 本轮结果 |
| --- | --- |
| 本地红/绿 | 显式 API 初次编译缺枚举；Precision 声明与 ROWID/UROWID 目录映射分别先失败后修复；`d05-local-red.trx`、`d05-metadata-red.trx` 和 `d05-metadata-green.trx`。首个实库夹具未消费 EOF 导致连接不可用，补全消费后再验收，不据此改产品代码 |
| 目标实库回归 | **68/68**，无 skip：新增 UROWID 10、FloatDerivation 4、StandalonePrepare 30、ComplexNullInput 24；`d05-live-final.trx`。仅 OceanBase 4.4.2.2 Oracle 直连、Windows、TLS Disabled |
| 本地相关 Unit | **594/594**，无 skip；参数、Schema/DataTypes、OracleCommandPlan、ROWID 相关组，`d05-local.trx`；不是完整 Unit |
| 公开 DLL | **24/24**：新增绑定 12（3 个代表资产 × sync/async × 普通/OB20 合成协议），旧 ROWID 结果 12（3 个代表资产 × text/binary × buffered/sequential）；`d05-consumer.trx`。合成长度边界文本不是服务端有效 ID，真实值证据来自上述实库；不称真实 OB20 验收 |
| 构建 | 产品五 TFM Release 构建 0 warning/0 error，`d05-product-build.log`；Integration/Unit/Consumer Release 构建成功 |
| 格式 | 本次 9 个 C# 文件 scoped whitespace verify 退出 0，`d05-format-final.log`；沙箱内 MSBuild pipe 拒绝日志 `d05-format-{src,int,consumer}.log` 保留，沙箱外首次 scoped 检查仅报告新文件两处缩进，已修正；git diff --check 通过 |
| 独立走查 | fresh-context `review_d05` 只读核对 wire、共享校验、输出完整响应后的原子发布、目录映射、测试和原始证据计数；无 Critical/Important。最终文档复核发现缺失矩阵有一句旧的 UROWID 目录未验收口径，已修正为 FLOAT 编号签名下真实 Derive 已验收；无剩余问题，可本地提交。reviewer 未执行实库或修改文件 |
| 提交 | `e5d6210`（`feat(oracle): support UROWID parameter bindings and outputs`）；实现、协议/实库/消费者回归及契约文档，本地提交，未 push；D-05 的上述指定直连子集达到 `P4`，整体清单仍未完成 |

`d05-output-first/second/third.trx` 保留了 NUMBER/VARCHAR2 目录探测与夹具 CLR 值声明不匹配
的失败历史，最终 FLOAT 夹具通过 `d05-output-float.trx` 后再运行扩大回归，不删除旧证据。
D-05 为 `P4`（上述目标直连 profile 的功能验收完成），长期压力、代理、真实 TLS/OB20/KDC、
跨平台/独立 runtime 未验收；本节为初始二进制模块范围，后续 client literal/fallback 扩展见 D-05b。

#### D-05b：UROWID 客户端文本参数（2026-11-02，指定文本子集 `P4`）

目标：补齐已有 `OceanBaseDbType.Urowid` 的普通客户端 prepare 回退和多语句输入。
目标服务端拒绝最初的 `CAST(... AS UROWID)`；已验证 `CHARTOROWID` 对短/长 ROWID、NULL、
坏值的转换与原生 type209 相同，采用现有 Oracle 字符串转义结果外包 `CHARTOROWID(...)`，
保留类型身份；不依赖隐式 String 转换，不增加 provider type 或选项。
NULL 沿用现有 NULL literal；类型、方向、precision/scale、
严格编码及 SQL 包预算仍由 `PreparedParameters` 和 `ClientPreparedSql` 统一验证。

空字符串必须在文本展开时抛 `ArgumentException`，不能生成 NULL：实库证明 native type209
拒绝空字符串，而 `CHARTOROWID('')` 返回 NULL。该保护同时覆盖多语句和自动 prepare 拒绝后的
fallback，避免无效 UROWID 静默写成 NULL；不是更改 native binary 或普通 String 参数语义。
`DataTypes.UROWID.IsLiteralSupported` 仍为 false，与 Oracle RAW/时间类型的函数表达式路径一致，
不把驱动可生成的转换表达式报告为原生 literal 语法。

- [x] 用真实短/长 ROWID、NULL、空字符串和坏值确认目标服务端转换行为，再决定实现。
- [x] 在 `UrowidParameterTests` 固定显式转换、转义、NULL 与既有拒绝边界；先确认原实现失败，
  再仅修改 `ClientPreparedSql.Literal`，复用字符串路径。
- [x] 使用现有 `PrepareRefusalRelay` 和多语句入口验证 sync/async、独立存储、坏值失败后恢复；
  中继只提供精确 prepare 1295，不冒充原生服务端拒绝。
- [x] 定向测试、本次修改走查、矩阵/契约同步后本地提交；不跑长测或部署矩阵。

| 检查 | 本轮证据 |
| --- | --- |
| 功能 red | 原实现对普通/多语句两条断言 **2/2** 因统一拒绝 Urowid 失败；`d05b-literal-red.trx` |
| 空串 red | 初次放开后两条空串断言 **2/2** 因未抛异常失败；增加执行前拒绝后通过；`d05b-empty-red.trx` |
| 定向单测 | **71/71**，无 skip，约 2 秒；Urowid、ClientPreparedSql、ClientPrepareFallback，覆盖转义/注入/NUL、NULL、严格编码、类型/方向/precision 校验及既有回退拒绝/取消边界；`d05b-literal-unit-verified.trx` |
| Oracle 直连 | 4.4.2.2 `oracle-direct` **16/16**，无 skip，约 10 秒；新增转换对照 **2**、真实多语句 **2**、受控 1295 fallback **2**，以及既有 Urowid binary/输出/RETURNING **10**；`d05b-literal-live-verified.trx` |
| 实库断言 | 短/超过 251 字节的长 ROWID、NULL/DBNull、空串和坏值边界、同 command 修正后恢复、独立连接显式 Prepare + SequentialAccess 核对全部存储；每条 fallback 共 8 次匹配 prepare/拒绝、0 次转发 prepare，无重复写入 |
| 构建与走查 | 产品五 TFM 和集成项目最终构建均 0 警告/0 错误；本次修改独立走查无阻塞；`git diff --check` |

最初 `CAST(:p AS UROWID)` 对照 **0/2**，保留 `d05b-cast-contract.trx`；随后
`CHARTOROWID(:p)` 的短/长/NULL/坏值对照 **2/2**，`d05b-chartorowid-contract.trx`。
首轮扩大实库 **14/16** 暴露上述空串语义差异，不以当轮文本入口成功写入 NULL 计为正确支持。
最终报告明确证明 native 空串拒绝、直接转换为 NULL、驱动文本输入提前拒绝三者的边界。

该子项只扩展 Oracle Input，不扩展 OUT/INOUT、显式 Prepare/cursor 的 fallback，也不改写
服务端错误、重放执行或自行验证 base64 格式。原生服务端 1295、代理/TLS/OB20、跨版本与长期
压力仍未验收；D-01 的原生 fallback 前提保持未关闭。

### D-06：MySQL GEOMETRY 的原始字节与 SRID（2026-10-30，a 指定子集验收，b 待服务端）

基线 `9a209e2`，初始工作区干净。现有结果 type255 为 `byte[]` / ProviderType.Binary；
服务端参数 type255 和 BLOB 均走 length-encoded bytes，结果 `geometry_cell_str` 移除服务端
内部版本头后返回 SRID + WKB。先验证现有 Binary 参数的直接存储语义，再决定是否需要新的
参数身份；不添加没有使用需求的 GIS 对象模型或坐标转换。计划/设计/证据仍只维护在本文。

- [x] 目标 MySQL profile 验证 GEOMETRY DDL、真实结果 metadata 与 SRID + WKB 字节布局。
- [x] SRID=0 输入/结果覆盖多个几何类型、NULL、长数据、错误值和同会话恢复；普通/显式
  prepare、text/binary 及 buffered/sequential 读取，用独立连接核对最终存储。
- [x] 按实库证据补齐参数目录和 DataTypes 元数据，先失败后通过的回归及公开 DLL 消费者。
- [x] 非零 SRID 调查：确认当前 SRS 目录仅 0，4326/3857/4490 返回 3548/SR001，保留 b 待服务端。
- [x] 构建、格式、相关回归及独立只读走查通过后本地提交，回填进度与未关闭边界。

| 子项 | 当前状态 | 任务 / 关闭边界 |
| --- | --- | --- |
| D-06a 原始二进制、SRID=0 | `P4`，指定直连子集 | 本轮补齐空间参数目录与实库闭环，使用现有 Binary/type252 输入、type255 结果；OceanBase 4.4.2.2 MySQL 直连、Windows、TLS Disabled 验收完成；不新增重复的专用 enum/parser |
| D-06b 非零 SRID/SRS | `P0 / 待服务端` | 当前 `INFORMATION_SCHEMA.ST_SPATIAL_REFERENCE_SYSTEMS` 只返回 SRID=0；服务端 ST_GeomFromText 在客户端绑定之前已拒绝 4326/3857/4490。需要部署方提供真实 SRS 数据及版本/权限前提，之后验证合法非零值、未知 SRS 拒绝、axis order、输入/结果与独立存储；不改写 SRID 为0，不自动导入/更改服务端目录 |

#### 实现与实库范围

既有 Binary 参数可直接存储完整 GEOMETRY 结果字节，稳定 wire 已成立；参数不必使用专用
type255。产品只改两个拥有者：`RoutineParameterDerivation` 将 MySQL GEOMETRY、POINT、
LINESTRING、POLYGON、MULTIPOINT、MULTILINESTRING、MULTIPOLYGON、GEOMETRYCOLLECTION /
GEOMCOLLECTION 映射到 Binary、Size=0，不从目录长度伪造截断尺寸；`DataTypes.GEOMETRY`
改为报告参数支持、保留 Binary 结果身份。普通 byte[] 的既有推断、包预算、快照、NULL、
流、客户端 hex 字面量和结果 decode 均复用，不改变 Oracle SDO_GEOMETRY 范围。

新增 `MySql/GeometryTests.cs` 10 项（sync/async × 显式/隐式 Prepare，以及2项 relay fallback）：

- 4项原始存储：七种基础几何形状、空 GEOMETRYCOLLECTION 和300点 LINESTRING，全部 SRID=0；
  raw 与服务端 ST_SRID/ST_AsBinary 交叉核对，同时用手写 POINT(1 2) hex 固定25字节布局。
  Size=1 不截断，较长样本跨251字节前缀。同 command 先 CLR null、九个样本，再尝试空 bytes、
  仅4字节头、截断 POINT、无 SRID 的WKB，服务端拒绝且 outcome 已知、存储行数未增；修正为
  DBNull 后同一物理会话继续。独立 observer 用 text/binary × buffered/sequential 共4条读取
  路径逐行核对 id、完整 bytes、SRS/WKB 和两端 NULL，并消费 EOF/NextResult。
- 4项目录与输出：真实 CREATE PROCEDURE / CREATE FUNCTION、DeriveParameters 得到 Binary，
  覆盖 IN/OUT/INOUT/ReturnValue。Prepare 不执行DML或发布输出；过程反复切换 NULL/POINT，
  先赋 OUT/INOUT 再重复主键失败（1062）时旧输出保持、同会话修正后成功；独立连接显式
  Prepare + SequentialAccess 核对五行。函数 POINT/NULL/POINT 返回 bytes，无隐式类型转换。
  真实过程/函数目录签名为通用 GEOMETRY；九种目录名称由 Unit/consumer 验证映射，七种几何
  值在 GEOMETRY 列中验收，不据此声称各空间子类型 routine DDL 均已实库验收。
- 2项 fallback：使用现有 relay 只注入 prepare 1295；Binary 仍用已有 hex literal 路径。四次
  NULL/POINT 切换、坏空 bytes 拒绝与恢复，总计6次拒绝/6次prepare/0次转发prepare，独立查询
  仅五行、逐字节一致；这不是目标服务端原生拒绝 prepare 的证据。

#### 检查与未关闭边界

| 检查 | 当前结果 |
| --- | --- |
| 红/绿 | `d06-metadata-red.trx` 13失败/4通过：9个空间目录类型缺映射、4个MySQL DataTypes参数支持仍false；最小修复后 `d06-metadata-green.trx` 17/17 |
| 实库 | `d06-live-final.trx` **42/42**，无skip：Geometry 10、MySqlParameterDerivation 2、TypeRoundTrip 13、MySqlInOutStream 17；仅 OceanBase 4.4.2.2 MySQL 直连、Windows、TLS Disabled |
| 完整 Unit | `d06-unit-full.trx` **8683/8683**，无skip；完整命令约9分钟。期间抓取诊断时 ConnectRetryTests.ExhaustedRoundsAttemptEveryCandidateAndReportTheLastEndpoint 仍在TCP等待，随后正常完成，不据此登记挂死缺陷或重跑替代原结果 |
| 公开 DLL | `d06-consumer.trx` **138/138**，无skip；新增空间目录54（3个代表资产 × sync/async × 9个目录名称），既有目录/Oracle FLOAT24、ENUM/SET/GEOMETRY读取60；均为本地合成协议，不是实库OB20/TLS |
| 构建 | 产品五 TFM Release 0 warning/0 error，`d06-product-build-final.log`；Integration/Unit/Consumer Release 构建成功。忽略目录 SRS 探针初次 restore 因 NuGet漏洞源不可用报NU1900，后续build/执行成功；不算产品构建警告 |
| 格式 | 本轮6个C#文件 scoped whitespace verify 退出0，`d06-format-final.log`；首次发现的新 switch labels 空白已修正；git diff --check 通过 |
| 独立走查 | fresh-context `review_d06` 只读核对产品映射、Binary/type252 编码、type255 解码、参数快照、成功后原子发布输出、失败保留旧输出、测试与原始TRX/SRS/格式证据；Critical=0、Important=0。唯一Minor为最终验收记录未回填，已修正；可本地提交。reviewer 未执行实库或修改文件 |
| 提交 | `735a24b`（`feat(mysql): derive spatial parameters and verify geometry byte roundtrips`）；实现、协议/实库/消费者回归及契约文档，本地提交，未push；D-06a 的上述指定直连子集达到 `P4`，D-06父项保持 `P2`，整体清单仍未完成 |

非零 SRID 初始失败在 `d06-raw-first.trx` / `d06-raw-seed.trx`，目录与三个非零 SRS 探针在
`TestResults/roadmap/d06-srs-probe.log` 和 `TestResults/d06-srs-probe/`。最初正向夹具使用4326
在造种阶段即失败，SRID=0 验收从 `d06-raw-zero.trx` 开始，未把非零失败改写成成功。
完整Unit运行期间另一次针对同输出目录的增量构建遭遇 xunit DLL 文件锁失败；未计测试结果，
原始完整运行持续到8683/8683终态，没有因观察超时重启。完整Unit诊断日志保留在忽略目录，
本轮生成的两份诊断dump已清理。

D-06 父项保持 `P2`；D-06a 仅上述 SRID=0 指定直连子集达到 `P4`，D-06b 仍待服务端。
非零 SRID、各空间子类型的真实 routine 目录签名、代理、真实 TLS/OB20、跨平台/独立 runtime
及长期压力尚未验收，不因本轮通过而关闭。

### D-07：GB2312/EUCKR 真实字符集验收（2026-10-30，目标子集已验收）

基线 `4de073a`，初始工作区干净。按已授权清单的 D-07 继续，使用现有严格 CP20936/51949
映射与两个直连 profile；不新增选项、相近 code page 兜底或依赖。客户端、普通结果和 Oracle
national 分开验收，失败先固定阶段/错误码，只有定位到驱动缺陷才按红/绿修复产品。
本节兼作设计、执行计划和进度账本，不另外创建计划文档。

- [x] `Types/EucCharacterSetTests`：双模式直连 × 两字符集 × sync/async，验证初始化状态、
  文本、普通 SQL/参数写入、显式 Prepare/NULL/长值、非法字符发送前拒绝及同会话恢复；
  独立 UTF8 连接核对存储，并用 text/binary × buffered/sequential 消费全部行/EOF/NextResult。
- [x] `Types/CharacterSetTests`：扩展 GB2312/EUCKR 的固定服务端 hex 结果，验证真实结果
  collation 下的 text/binary 和 sync/async；客户端存储测试另用每次1字符读取覆盖长值流式
  边界，不以客户端同编码回环代替独立读回。
- [x] `Oracle/NationalCharacterEncodingTests`：扩展两字符集 NCHAR/NVARCHAR2 输入、NULL、
  普通 prepare/PrepareExecute 与独立 UTF8 存储读回；复杂成员仍按 D-04 单独验收。
- [x] 运行目标实库与相关本地回归、构建/格式；记录每个成功子集与失败，不删除失败用例。
- [x] fresh-context 独立只读走查；修复阻塞问题后本地提交，回填实际提交号及未关闭边界。

影响范围为上述测试、本文及已有契约/兼容性事实；暂不改公共 API、凭据、服务端配置或字符集
目录。UJIS/EUCJPMS 等剩余名称继续留在 S-05。若 Oracle 初始化被服务端拒绝，保留该失败，
继续检查默认 UTF8 连接上的结果/national 能力；不得据此替换或跳过客户端失败。

#### D-07 检查记录

| 检查 | 当前结果 |
| --- | --- |
| 实库 | `d07-live-final.trx` **76通过/4失败/0skip，共80**：客户端16（MySQL8、Oracle GB2312 4通过，Oracle EUCKR 4失败）、结果8（新增2）、national56（新增8）。共新增26例，22通过/4失败；既有54例全部通过。仅 OceanBase 4.4.2.2 直连、Windows、TLS Disabled；不是完整Integration。此前 `d07-live-green.trx` 72/72只覆盖MySQL客户端子集，走查指出移除了Oracle复现入口，已恢复双模式，最终以80项为准 |
| 失败边界 | Oracle 普通客户端 EUCKR 初始化在驱动身份校验阶段返回服务端 `900/42000`，4个sync/async ×显式/隐式Prepare正向用例继续保持可运行和失败；未把它计入成功，也未修改产品拒绝/验证逻辑。`d07-probe.log` 使用默认UTF8连接发送相同ASCII初始化SQL：设置client/connection成功，随后设置results=utf8mb4返回900/42000且outcome已知；GB2312三句均成功。不把此拒绝扩大为所有EUCKR服务端能力不存在 |
| 本地 | `d07-unit.trx` 393/393，无skip；仅字符集相关Unit，产品未改动，不重复上轮完整Unit |
| 构建 | Integration Release build 通过，日志 `d07-build.log`；测试使用 `-m:1 -nr:false -p:UseSharedCompilation=false` |
| 格式 | 本轮3个C#文件 scoped whitespace verify 退出0，`d07-format.log`；git diff --check通过 |
| 走查 | fresh-context `review_d07` 只读核对测试、映射和TRX：无Critical；Important 1为最终源过滤移除Oracle失败入口，已恢复且最终80项真实复验；Minor 2为旧契约页声明无实库和数量未区分新增/既有，均已修正。产品代码未改动，未发现编码实现缺陷；服务端拒绝保留为未关闭项 |
| 提交 | `97a8e13`（`test(types): verify GB2312 and EUCKR storage and retain Oracle rejection`）；测试和契约事实同步，本地提交，未push；未改产品实现，未关闭D-07父项或4项服务端失败 |

| 子项 | 状态 / 关闭边界 |
| --- | --- |
| D-07a MySQL客户端/结果 | `P4`，仅当前GB2312/EUCKR直连子集；固定字节结果、长值、SQL/参数、NULL、非法字符与恢复有证据 |
| D-07b Oracle GB2312普通客户端 | `P4`，仅上述4项直连存储/结果子集；普通结果仍按初始化契约使用UTF8 |
| D-07c Oracle national | `P4`，GB2312/EUCKR NCHAR/NVARCHAR2指定直连子集；默认UTF8客户端、独立连接读回，不等于普通EUCKR客户端成功 |
| D-07d Oracle EUCKR普通客户端 | `P2 / 当前目标服务端限制`；当前 4.4.2.2 目标端点在同一初始化序列返回 `900/42000`，Connector/J 2.4.14 对同一序列也失败，因此跳过客户端改动。仅当目标版本/权限提供成功初始化并完成独立存储读回，或出现不同 wire 证据时重新评估；不放宽编码/身份验证 |

最初 `d07-client-first.trx` 的 Oracle GB2312 使用了不支持的 `CONNECTION_ID()` 查询，第二轮
误用GetInt32读取Oracle NUMBER。两者均为新夹具错误，已改为既有物理会话身份和EqualNumber
检查；最终4项GB2312通过。EUCKR的900始终独立保留。这里未新增产品实现，不把既有代码已
通过的验收测试伪记成产品红/绿。D-07父项保持P2，其余名称、二进制collation的逐项实库、
代理/TLS/OB20、其他版本/平台和复杂national成员仍未验收；Oracle 普通 EUCKR 初始化的失败已按当前目标服务端限制保留，未修改客户端编码或身份验证。

### A-03a：Oracle 未约束 NUMBER 参数推导（2026-10-30，指定 profile 已验收）

基线 `399cb6d`，初始工作区干净。使用 writing-plans/TDD 的逐步验证流程，计划直接维护于此。
`a03-probe.log` 已复核当前Oracle 4.4.2.2目录：未约束NUMBER的三个方向均为 precision=NULL、
scale=-85；FLOAT为126/-85且已有专门处理。NUMBER的-85是未约束标记，不应写入参数的真实
DecimalScale（合法最小值-84）。仅在Oracle数字参数precision缺失、scale恰为-85时保留未知
scale；真实负scale（含-1/-84）和已声明精度继续严格校验，MySQL规则不变。无需新增API。

- [x] `DeriveParametersMetadataTests` 补IN/OUT/INOUT/RETURN与NULL/-85元数据等价绑定的红灯；
  固定真实负scale、错误precision/scale以及MySQL拒绝不被放宽。
- [x] 在 `RoutineParameterDerivation.Map` 的数字目录映射归一化单一未约束标记，运行红/绿。
- [x] 复用公开DLL目录消费者验证三个代表资产的sync/async NUMBER推导与执行，保留原子失败。
- [x] 新增Oracle过程/函数真实Derive，显式/隐式Prepare、sync/async、NULL/高精度、
  OUT/INOUT/ReturnValue、Prepare不执行、失败保留输出、同会话恢复和独立存储读回。
- [x] 相关回归、构建/格式及fresh-context独立只读走查通过后本地提交，回填提交号与边界。

影响范围：共享目录映射、既有Unit与公开DLL消费者、Oracle实库夹具及契约事实；所有调用者
（DeriveParameters同步/异步）共用Map，普通GetSchema仍返回真实目录值，不伪造服务端元数据。
没有改变数值wire、数值范围或输出发布规则。回归重点是错误地把合法负scale全部当作未知、
把FLOAT二进制精度当小数精度，以及把MySQL缺少精度视作合法。

#### A-03a 验收记录与归因

| 检查 | 结果 |
| --- | --- |
| 单元红绿 | `a03-number-red.trx` 初始 14 项中 4 项因 `-85` 被拒；修复后 `a03-number-unit-green.trx` 154/154 |
| 消费者 | `a03-number-consumer-red.trx` 6 项初始全红；修复后 `a03-number-consumer-green.trx` 84/84，覆盖 3 个代表资产和 sync/async 合成协议 |
| 目标实库 | `a03-number-live-red.trx` 8 项初始全红；`a03-number-live-final.trx` 30/30，含新增 `NumberDerivationTests` 8 项（过程/函数、显式/隐式 Prepare、sync/async）及既有 FLOAT/ROWID/参数回归；OceanBase 4.4.2.2 Oracle 直连、Windows、TLS Disabled |
| Java 2.4.14 对照 | 同一租户/账号/版本：`AttributionProbe` 读取 NUMBER precision=NULL/scale=-85，Java `ParameterMetaData` 报 precision=0/scale=-85；IN/OUT/INOUT 六组值（NULL、7.2500、38 位整数、1E-130、1E125）精确通过。Java 源码 `OceanBaseCallableParameterMetaData` 同样只读 DATA_PRECISION/DATA_SCALE，未把 -85 当固定小数位 |
| 其他归因 | Java 与 .NET 对同一 EUCKR 初始化序列均在 `SET character_set_results` 返回 900/42000；Java 与 .NET 查询 `CHAR_LENGTH` 均返回 904/42S22，而 Java VARCHAR2 callable 成功。因此 EUCKR 仍不能归为 .NET 单方缺陷并保留 D-07d；CHAR_LENGTH 已单独归为 A-03b 当前服务端目录限制 |
| 回归/构建 | `a03-number-product-build.log` 五 TFM Release 0 warning/0 error；完整 Unit 为 8696/8697（唯一失败是沙箱 File.Replace 权限，`a03-tns-unsandboxed.trx` 同测试沙箱外 1/1）；不宣称完整 Unit 全绿 |
| 代码走查/提交 | fresh-context 独立只读走查完成，无 Critical/Important；scoped `dotnet format whitespace --verify-no-changes --no-restore` 和 `git diff --check` 通过；`a252aee`（`🐛 fix(oracle): derive unconstrained NUMBER parameters without a fixed scale`）已本地提交、未 push。A-03a 达到 `P4`，父 A-03 仍为 `P3` |

本项实现只在 Oracle 数字参数同时满足 precision 缺失、scale=-85 时将 scale 归一为未知；
真实 scale -84..127（包括 -1）、有 precision 的 -85、FLOAT/NUMBER_FLOAT 和 MySQL 规则不变。
普通 GetSchema/ProcedureParameters 仍保留服务端原始 -85，不伪造目录值。修复不改变 wire、
范围或输出原子发布规则。

#### A-03b：Oracle CHAR_LENGTH 目录的当前服务端限制（2026-10-31，跳过客户端改动）

目标 OceanBase 4.4.2.2 Oracle 直连 profile 的 `ALL_ARGUMENTS` 不提供 `CHAR_LENGTH`/`CHAR_USED`；
`VARCHAR2`、`CHAR`、`NVARCHAR2`、`NCHAR` 的 `DATA_LENGTH` 也为 NULL，直接查询字符长度返回
`904/42S22`。现有账号拥有本次过程创建、执行及 `ALL_ARGUMENTS` 可见性，因此这不是已知的
权限拒绝。Java Connector/J 2.4.14 在同一租户/账号对同一 `CHAR_LENGTH` 查询得到相同的
`904/42S22`；Java 的 VARCHAR2 callable 成功不构成反证，因为该调用不依赖目录字符长度查询。

客户端行为已经有明确边界：基础目录查询不依赖 `CHAR_LENGTH`；只有字符参数才发第二次长度查询，
服务端错误原样保留，且失败不会替换调用者已有参数。`OracleDerivationLoadsCharacterLengthsOnlyForCharacterArgumentsAndKeepsSignaturesAtomic`
专项 **14/14**、相关 Oracle 浮点/目录回归 **60/60** 通过；代码走查确认未把 `DATA_LENGTH` 字节数
或 `DATA_PRECISION` 字符集形态冒充字符数。A-03b 标记为**当前目标服务端目录限制，跳过客户端开发**；
只有出现提供字符长度的目标版本/权限/profile，或稳定的等价服务端目录来源，才重新评估。

#### A-03c：Oracle 基础目录与过程签名回归（2026-11-01，指定 profile）

在同一 `oracle-direct` User Secrets profile 上重跑现有目录/过程验收，确认近期 XA、批处理和时间脚本
改动没有污染公开元数据路径。Schema 基础集合、对象过滤、复合键/索引/外键、视图、Oracle 类型/序列、
同义词共 **7/7** 通过；标量 OUT/INOUT、RETURNING 和过程输出加目录发现共 **3/3** 通过；未约束
NUMBER 的过程/函数、隐式/显式 Prepare、同步/异步派生与存储共 **8/8** 通过，合计 **18/18**，
0 失败、0 跳过。

这次只增加当前验收证据，并修正了一个客户端 metadata 边界：Oracle PL 输出的 `DATE` 可能以
`type=12, scale=255, precision=255`（未知精度）返回；客户端现在按 DATE 固定无小数位归一化，
不再把合法输出拒绝为不支持。新建 package 的 `P(NUMBER, OUT NUMBER)` 与 `P(DATE, OUT DATE)`
两个 overload 按目录 `OVERLOAD` 派生参数并执行，sync/async **4/4** 通过；NUMBER 和 DATE 输出
均验证了实际值。最小权限和大小写/引用标识符仍没有足够 profile 覆盖，`CHAR_LENGTH` 继续按
A-03b 的服务端限制处理。没有把单一 Oracle profile 的通过结果提升为双模式或跨部署完成。

这项修复同时有单元回归 `OracleDateAcceptsUnknownWirePrecision` 和真实 Oracle 过程验收；只调整
结果列 metadata 归一化，没有扩大 DATE 的小数精度契约。

#### A-03d：package-local PL/SQL 类型仍缺少可移植绑定契约（2026-11-01，保留缺口）

在同一 `oracle-direct` User Secrets profile 上创建 package-local `RECORD` 类型：
`TYPE R IS RECORD (ID NUMBER, NAME VARCHAR2(20)); PROCEDURE P(X IN R, Y OUT R)`。
Oracle `ProcedureParameters` 真实目录返回 `DATA_TYPE=PL/SQL RECORD`、`TYPE_NAME=R`、
`TYPE_SUBNAME=<package>`，而 `TYPE_OWNER` 是带空格填充的内部数值标识（本次为 `500003`），
并非可通过 `ALL_TYPES`/`ALL_TYPE_ATTRS` 加载的 schema object。当前 .NET `DeriveParameters`
在发现 `TYPE_SUBNAME` 后按契约抛出 `NotSupportedException`，避免把 package-local identity
误当成 named OBJECT。

Connector/J 2.4.14 对同一目录形状可以返回两个 `PL/SQL RECORD` 参数元数据，但使用公开
`CallableStatement` API 绑定并注册该记录时返回客户端 `SQLFeatureNotSupportedException`
（`600/0A000`）。这证明当前证据是两个 SDK 都没有可移植的 package-local RECORD wire/API
绑定路径，不能把失败归为 OceanBase 服务端拒绝，也不能仅凭目录行新增一个 .NET provider type。
A-03d 保持为**客户端能力缺口/待协议证据**；只有取得记录字段描述、输入/输出 payload
布局和 Java 成功交互后，才重新开启实现与 sync/async、NULL、嵌套字段验收。普通
`GetSchema` 仍返回该真实目录值，不伪造类型 owner 或 descriptor。

新增 `Oracle/PackageLocalRecordTests.cs` 作为可复现边界回归：同一 User Secrets profile
的 `ProcedureParameters` 能读到 `PL/SQL RECORD`、`TYPE_NAME=R` 和 package-local
`TYPE_SUBNAME`。一次独立目录探针去掉驱动默认的 `DATA_LEVEL=0` 过滤后，`ALL_ARGUMENTS` 仍只有
`X/Y` 两个顶层 `PL/SQL RECORD` 行，均为 `DATA_LEVEL=0`；`ALL_PLSQL_TYPES`、
`ALL_PLSQL_TYPE_ATTRS` 对该 package 没有字段布局，`ALL_TYPE_ATTRS` 只返回 schema
object 属性。因此当前目标没有可用于构造字段 payload 的目录证据。`DeriveParameters` 的
sync/async **2/2** 均在发送前抛出 `NotSupportedException`，且不发布参数；另有 cursor 成员形状
使 package INVALID 的服务端回归 **2/2**。该测试确认的是当前客户端契约和清晰拒绝行为，
不是把 Java 的 `SQLFeatureNotSupportedException` 改写成服务端限制。

#### A-03e：引用 package 标识符的目录与参数派生（2026-11-01，指定 profile）

新增 Oracle 直连回归，创建大小写敏感的引用 package 名和引用过程名：
`"ObiPkg_x"."DoIt"(P_INPUT IN NUMBER, P_OUTPUT OUT NUMBER)`。`Procedures` 目录按
package/name restriction 找到唯一签名，`DeriveParameters`/`DeriveParametersAsync` 均能解析
带双引号的两段标识符，派生参数后执行并读回 `42`；同步/异步 **2/2** 通过。该结果关闭了
当前目标 profile 下引用 package 标识符的客户端解析缺口，但不外推未引用 Unicode 标识符、
跨 schema 权限或双模式行为；这些仍需独立 profile 和权限证据。

#### A-02a：Oracle XA 基础生命周期回归（2026-11-01，指定 profile）

在项目 User Secrets 的 `mysql-direct` 和 `oracle-direct` profile 上执行现有事务故障实库组：两阶段
Prepare/Recover 后提交或回滚、单阶段提交持久性和非法阶段转换，加上池丢弃后的分支恢复、同步/异步
以及提交/回滚组合，共 **28/28** 通过，0 失败、0 跳过。该矩阵覆盖 MySQL 和 Oracle 模式的
独立 recover 查账、决定后的数据结果、发送前状态校验、旧 XA 资源失效和新池租约恢复。

此前 `oracle-direct` 的响应丢失、整响应和部分响应 relay 故障矩阵 **16/16** 仍然有效；连同本轮
生命周期/池恢复矩阵，A-02 已有 **44** 个真实租户或受控 relay 用例证据，但仍未关闭：协调器/节点
崩溃、真正 in-doubt 决定、取消顺序和更完整 unknown-outcome 矩阵仍需单独故障注入。XA 单元测试中的
loopback 中断场景仍需与全量网络验收的偶发 IOException 分开看待。

执行修正：Oracle StoredProcedure契约要求PrepareExecute=true，初版夹具误将false用于过程输出，
且给Decimal类型编号赋CLR int；修正为既有显式/隐式Prepare组合和decimal编号，不扩大产品wire。
Unit和Integration并发构建共用产品输出曾报CS2012文件锁；之后相关构建/测试顺序执行，
这次构建错误不计入测试结果，未重启任何仍在执行的测试。

### D-12a / D-12c：VECTOR 与 MySQL ARRAY 的当前版本通用文本路径（2026-10-30，指定 profile 已验收）

本轮先用同一 MySQL 租户、同一账号和 OceanBase 4.4.2.2 做 Java 2.4.14 对照，再决定是否增加
专用 provider 类型。Java 探针 `TestResults/java-gap-probe/vector-java.log` 的结果为：

- `CREATE TABLE (... VECTOR(3))` 和 `CREATE TABLE (... ARRAY(INT))` 成功；
- 两列查询的 JDBC metadata 都是 JDBC type `CHAR`、type name `CHAR`、Java class `String`；
- 直接写入 `'[1,2,3]'` 成功并按同样文本读回；`ARRAY[1,2,3]` 语法返回 1064；
- `SPARSEVECTOR(3)` DDL 返回 1064，`TO_VECTOR('[1,2,3]')` 返回 1305；
- 因此本版本没有可证明的专用 vector/array wire 或 Java 专用 codec。新增专用 enum、二进制
  parser 或自动把字符串解析成数值向量会扩大契约并可能掩盖版本差异，本轮不做。

现有 .NET CHAR/String 路径已经能够绑定和读取这两个服务端列类型。本轮加入
`VectorArrayStringWireTests`，覆盖直连 profile 的 sync/async × implicit/explicit Prepare，
验证 DDL、NULL、文本参数、结果 metadata（`CHAR`/`String`）、行顺序和独立连接读回；
`d12-vector-array-live-direct.trx` 为 **4/4，无 skip**。这里的“完成”是当前版本通用文本子集的
验收，不是所有 VECTOR/ARRAY 版本、稀疏向量、嵌套元素或专用 API 已支持。

本子模块无产品源码改动；测试和本条证据用于防止后续把服务端专用类型误解为缺失的专用 codec。
D-12 父项现提升为 `P4`。D-12b 在 4.4.2.2 直连 profile 上确认了当前可复用路径：查询以
`BLOB`/`byte[]` 返回，`rb_from_string(?)` 和 `rb_build_varbinary(?)` 可用并可独立读回；直接
绑定 `byte[]` 到 `ROARINGBITMAP` 列稳定返回服务端 `1235/0A000`，因此不把该服务端限制记为
.NET 缺陷，也不新增 RoaringBitmap enum/parser。未来若服务端提供专用 prepared type，再单独
评估 wire/API；`SPARSEVECTOR`、嵌套 ARRAY 和其他版本仍按各自边界保留。

独立 fresh-context 只读走查无 Critical/Important/Minor；格式核验和 `git diff --check` 已通过；
`107c852`（`✅ test(mysql): verify vector and array string wire compatibility`）已本地提交，未 push。

### D-08：prepared batch multi-send 的 input-only DML 与 scalar 子集（2026-10-31，父项 `P2`；子集 `P4`）

已新增默认关闭的 `Use Batch Multi Send` 和 `Batch Multi Send Number`（默认 100，最小 1），并将
选项冻结到 endpoint/credential snapshot。启用后，`OceanBaseBatch.ExecuteNonQuery` 对普通文本、
input-only、非 stream、非 cursor、非 output、非 LOB、非 array binding、非 LOCAL INFILE 且能走
server prepared statement 的 DML 请求按数量和 64 MiB packet 上限分组：一组先写完所有
COM_STMT_EXECUTE，再按命令顺序读取 OK affected-count。错误归因到当前 batch command；未完成
响应时断开 session，取消不重放已发送请求。禁用选项继续使用原有逐命令 reader 路径。

`ExecuteScalar`/`ExecuteScalarAsync` 现在也复用该路径：普通 prepared 标量结果会按命令顺序完全排空，
只返回 batch 语义中的首个 scalar；cursor、LOB、复杂结果列仍在发送前拒绝。`ExecuteReader` 现在对同一
个 input-only、普通 prepared、非 stream/LOB/cursor/output/array 的子集先发送一组执行，再按命令顺序
读取结果；SELECT 命令的 `RecordsAffected` 仍按 ADO.NET 语义为 `-1`。本地 loopback 已覆盖 sync/async、
两条执行先于响应、数量分组、基本 scalar
结果、发送前 output/cursor/session-state SQL 拒绝、组内错误后 session 断开和发送后取消/unknown-outcome，
以及超过 64 MiB 总请求预算、恰好 64 MiB 预算和 `maxCount=1` 的分组边界，共 **146/146**
通过。`dotnet build -c Release`
的 netstandard2.0、netstandard2.1、net8.0、net9.0、net10.0 均 0 warning/0 error。

本轮修正了 multi-send 与普通执行路径的边界：只有可缓存的 DML/SELECT 进入该子集；发送前会解析并校验
Oracle session time zone/national character set，必要时按已解析上下文重新编码参数；共享 exchange 使用统一
的发送状态处理，并先发送积压的 prepared-statement close 包，避免丢失结果元数据或长期积压服务端句柄。

之前的实库运行被 VPN 阻塞；修复响应包序号边界后，临时 direct profile 的 MySQL/Oracle
矩阵 **12/12 通过**（DML/scalar/reader × sync/async，`BatchMultiSendNumber=2`）。首个子集的
服务端已知响应的 prefix/error/suffix 边界已在 D-08c 的 MySQL/Oracle 直连 4/4 子集中验收；更大
错误排序矩阵、收益基线和代理/OB20 行为仍未验收；Oracle PrepareExecute、Oracle piece-data
流水化、输出/LOB/cursor/array 仍明确不在首个子集，因此这些仍是 D-08 的后续缺口。reader 的 MySQL/Oracle
直连 profile sync/async 均已通过；本轮没有把这组结果外推到代理或其他版本。

新增 `Batches/MultiSendTests.cs`，覆盖 MySQL/Oracle create-objects profile 的 input-only
prepared DML、scalar 排空和 reader 顺序，sync/async 及 `BatchMultiSendNumber=2`。Oracle 用例显式
关闭 `UseOraclePrepareExecute`，以验证本子集要求的普通 server prepared wire；本地编译通过。本轮
从 User Secrets 读取 MySQL/Oracle 连接信息，并生成只包含 `mysql-direct`/`oracle-direct` 的临时
integration config；启用 `OB_REQUIRE_INTEGRATION=1` 后，限定集成组 **12/12 通过**。重跑同时
暴露并修复了客户端响应包序号未在每个 pipelined 响应边界重置的问题；loopback 夹具现按
MySQL 响应从序号 1 开始建模。结果只证明上述 input-only DML/scalar/reader 子集，不外推更大
Q20 范围或其他部署。fresh-context 代码走查核对了 eligible-shape 拒绝、发送前失败、响应顺序、
unknown-outcome/cancellation 和 reader 提前关闭；未发现待修问题。

#### D-08a：MySQL batch multi-send 的 Stream 输入（2026-11-01，`P4` 子集）

此前 `BuildBatchMultiSendRequest` 在发现 `Stream` 参数时直接拒绝。Connector/J 的同类路径在每条
prepared 执行前发送 `COM_STMT_SEND_LONG_DATA`，因此本轮把请求保存为执行包、statement id 和参数快照，
在同步/异步 multi-send 发送阶段先上传 MySQL long-data，再发送 execute。Oracle stream 继续拒绝，
因为现有 piece-data 交互需要逐片响应，不能伪装成无响应流水化。

新增 `BatchMultiSendWritesMySqlLongDataBeforeDrainingResponses`，验证两条 stream 输入的 statement id、
参数序号、完整字节和“先上传/执行、后响应”顺序，sync/async **2/2 通过**；完整 Unit **8727/8727**，
Release 构建和 netstandard2.1 构建均 0 warning / 0 error。代码提交为 `41b566d`。

随后新增 `MultiSendTests.MySqlStreamMultiSendStoresAndReadsBack`，使用项目 User Secrets 的
`mysql-direct` profile，覆盖 sync/async 两种调用：两条 `MemoryStream` BLOB 先批量写入，再通过
独立查询的 `GetStream` 逐行读回并逐字节核对。该真实 4.4.2.2 直连 profile **2/2 通过**，因此
MySQL Stream 的当前 input-only multi-send 存储/读取子集提升为 `P4`；这不是对代理、OB20 或其他
服务端版本的外推。

本子集仍未关闭取消、代理/OB20、LOB/OUT/cursor/array；Oracle piece-data 流水化没有 Connector/J
协议依据，不纳入当前 .NET 承诺，除非后续服务端给出明确的无等待确认协议。

#### D-08b：Oracle batch Stream 的 piece-data 安全回退（2026-11-01，`P4` 子集）

启用 `Use Batch Multi Send=true` 时，客户端现在先根据已协商的 session mode 和 batch 参数形状
判断是否包含 Oracle `Stream`。该组合不进入 multi-send 的“先发完、后排空”路径，而是复用普通
prepared 执行的 `COM_STMT_SEND_PIECE_DATA` 逐片确认流程；MySQL Stream 和普通 Oracle
input-only batch 仍保持各自原有路径。同步/异步 DataSource-owned batch 回归 **2/2 通过**，
确认 piece-data、execute、affected-count 和 owned session 关闭顺序均正确。

这关闭的是“启用 multi-send 选项后 Oracle Stream 被无条件拒绝”的客户端能力缺口，不宣称
Oracle piece-data 已支持流水化或吞吐收益。随后新增 `MultiSendTests.OracleStreamMultiSendUsesPieceDataAndReadsBack`，
在项目 User Secrets 的 `oracle-direct` profile 中明确关闭 `UseOraclePrepareExecute` 和 `UseLobLocators`，
覆盖 sync/async、3 字节和 90007 字节两条 BLOB Stream；真实 4.4.2.2 直连 **2/2 通过**，确认
piece-data fallback、批量 affected-count 和独立 `GetValue`/LOB 读回。PrepareExecute 下的 Stream、
代理/OB20、LOB/OUT/cursor/array 和收益基线仍待各自 profile；Connector/J 的 Oracle piece-data
也逐片等待确认，没有可移植的流水化协议依据。PrepareExecute
下的安全回退见 D-08d。

#### D-08c：真实服务端错误排序与 suffix 边界（`P4` 子集，2026-11-01）

新增 `Batches/MultiSendTests.PreparedMultiSendErrorPublishesPrefixAndLeavesSuffixUnsent`，使用项目
User Secrets 的 `mysql-direct` 与 `oracle-direct` 直连 profile，覆盖 sync/async 和
`BatchMultiSendNumber=2`。批次先发送一条成功 INSERT 与一条重复主键 INSERT，第三条置于下一组；
服务端错误必须归属第二条，第一条发布 `RecordsAffected=1`，错误项与下一组 suffix 保持 `-1`，
连接继续可复用且独立计数为 2。MySQL/Oracle 各 **2/2**，合计 **4/4** 通过。

该证据只关闭当前直连、input-only DML、已知响应错误的 prefix/error/suffix 子集；不外推错误
发生在已发送组内的 unknown-outcome、取消/超时、代理/OB20、PrepareExecute、LOB/OUT/cursor/array
或更大错误排序矩阵。它不是服务端限制归因，也不改变现有不自动重放契约。

#### D-08d：Oracle PrepareExecute Stream 的安全回退（`P4` 子集，2026-11-01）

新增 `Batches/MultiSendTests.OraclePrepareExecuteStreamMultiSendFallsBackToPieceData`，在项目
User Secrets 的 `oracle-direct` profile 中同时启用 `UseBatchMultiSend`、`UseOraclePrepareExecute`
和 `UseLobLocators`，覆盖 sync/async、3 字节和 90007 字节 BLOB Stream。测试确认含 Stream 的
批次不会进入无响应 multi-send，而是复用逐命令 piece-data；关闭 reader 后再打开 locator，独立
读回两条内容。真实 4.4.2.2 直连 **2/2** 通过。

该子项关闭的是三项选项叠加时的客户端安全回退缺口，不宣称 PrepareExecute 下的 Stream 已支持
流水化或吞吐收益；错误排序扩展、代理/OB20、LOB/OUT/cursor/array 和收益基线仍按各自 profile
继续验收。Connector/J 的 Oracle piece-data 也逐片等待确认，因此没有可移植的流水化协议依据。

#### D-08e：server prepare 1295 的 batch text fallback（`P4` 子集，2026-11-01）

此前 batch multi-send 在 `COM_STMT_PREPARE` 收到 `1295/HY000` 时直接拒绝，无法复用已经存在的
client-prepared SQL 展开路径。本轮仅对无 Stream、无 output/cursor、普通 input-only text 命令开放该回退：
每条 batch command 仍先尝试 server prepare；命中精确的 `1295/HY000` 后生成单条 `COM_QUERY`，并按
text protocol 解析和排空对应结果。server prepared 成功路径继续使用 binary protocol，Stream fallback
仍在发送前拒绝，避免重复消费流或伪造无响应流水化。

新增 `BatchMultiSendFallsBackToClientPreparedTextWhenServerPrepareIsUnsupported`，loopback 覆盖
两条命令先完成两次 prepare，再按顺序发送 `select (1)`/`select (2)` 并排空两个 text response，
sync/async **2/2 通过**。本次验证还包括 BatchLifecycleTests **307/307**、完整 UnitTests
**8733/8733** 和 solution Release build 五个产品目标框架 **0 warning/0 error**；这是客户端
wire/生命周期证据，不外推真实租户原生 1295、代理/OB20 或其他服务端版本。

该子项关闭的是 D-08 的客户端 text fallback 缺口；LOB/OUT/cursor/array、取消/超时扩展、代理/OB20
和收益基线仍未完成。Oracle piece-data 流水化没有 Connector/J 协议依据，暂不作为客户端缺失实现。

#### D-08f：已发送组的 pending response 取消归属（`P3`，2026-11-01）

新增 `BatchFaultTests.BatchMultiSendPendingResponseCancellationPublishesPrefixAndUnknownSecond`，
用 loopback 让第一条响应先成功、第二条响应保持挂起，分别覆盖 caller token、`batch.Cancel()` 和
batch timeout。三种路径均确认第一条 `RecordsAffected=2` 已发布；第二条保持 `-1`；错误归属第二个
batch command；`IsExecutionOutcomeUnknown=true`；连接进入 `Broken`；不会发送或重放 suffix。
该回归 **3/3 通过**；随后完整 UnitTests **8731/8731** 通过，solution Release build 五个产品目标框架
**0 warning/0 error**，新增测试文件的定向 whitespace 检查和 `git diff --check` 也通过。补齐的是
客户端状态归属证据，不是服务端已执行结果的查账或自动重放。

该子项不关闭 LOB/OUT/cursor/array、代理/OB20、收益基线，亦不代表
所有网络断点和更大 unknown-outcome 矩阵已验收；这些继续保持 D-08 的后续边界。

#### XA interrupted prepare CI 回归复核（2026-11-01）

CI 报告中的 `XaInterruptedPrepareIsUnknownAndNeverReplayed(disconnect)` 失败对应旧的
`Assert.False(preparing.IsCompleted)` 测试逻辑。`disconnect` 会在服务端脚本结束、调度器重新获得
控制前完成 pending task，这个时序本身不改变 unknown-outcome 契约。提交 `7eee906` 已只放宽
这一条时序断言；cancel、timeout、close 仍要求错误为 `IsExecutionOutcomeUnknown=true`，并保持
连接破坏/关闭和禁止重放检查。

当前源码重新编译后的该专项 **4/4** 通过；随后按 CI 的
`dotnet test tests/OceanBaseConnector.UnitTests/OceanBaseConnector.UnitTests.csproj -c Release
--no-restore --no-build` 完整重跑 **8741/8741**，0 失败、0 跳过。该结果只确认当前提交的测试
和交换状态契约，不把旧 runner 的失败当作新的驱动功能缺口。

#### D-08g：MySQL BLOB/TEXT binary result 的 batch reader（`P4` 子集，2026-11-01）

`BuildBatchMultiSendRequest` 原先把 type `249..252` 的所有结果列与 Oracle locator/complex
列一起拒绝。MySQL 这组类型在 binary protocol 中是普通 length-encoded 字节，已有
`ResultColumn.ReadBinaryField`、`DecodeBinary` 和 `OceanBaseDataReader` materialize 路径，
不需要持有 session-bound locator。因此现在仅在 MySQL mode 放行 `TINY/MEDIUM/LONG BLOB/TEXT`
结果；Oracle type `210/211`、cursor `163` 和 complex `160` 继续发送前拒绝。

新增 `BatchMultiSendReaderMaterializesMySqlBlobAndTextResults`，loopback 覆盖 BLOB（binary
charset）和 TEXT（字符 charset）两种 metadata，sync/async **4/4** 通过，验证两条响应均在
切换下一个 batch result 前完整读取，`GetValue`、field type 和 data type name 与普通 reader
一致。本子集没有放开 Oracle locator、LOB text/OUT/cursor/array，也没有把 loopback 结果外推到
代理、OB20 或其他 OceanBase 版本。

#### D-08h：complex/LOB input 的 multi-send 发送前边界（`P4`，2026-11-01）

走查发现 `BuildBatchMultiSendRequest{Async}` 原先只检查结果列和 OUT/cursor 形状；参数侧的
`PreparedParameters` 虽然能够为普通 Oracle 执行编码 type 160、210/211，却没有 multi-send
专用 wire 与 Connector/J 对照证据。若直接进入 COM_STMT_EXECUTE 流水线，会把未验收的复杂值或
locator 输入误报成已支持能力。

现在同步/异步构建请求在任何 prepare、执行包发送前拒绝 `ComplexType`、
`OceanBaseComplexValue`、`OceanBaseLob` 和 `OceanBaseDbType.Blob/Clob` 参数。新增 loopback
回归覆盖 Oracle anonymous complex input 与 typed LOB input，sync/async **4/4**；服务端观察到
零命令字节，连接仍保持 Open。该改动是边界收敛，不是把复杂值或 LOB 扩入流水化；Oracle
PrepareExecute 的 OUT/cursor/array 仍需 0xa1 专用请求和结果所有权，Oracle piece-data 仍逐片
等待确认。Connector/J 2.4.14 同样没有可移植的 piece-data 流水化依据，因此不猜测实现。

#### D-08i：named OBJECT/collection 普通输入流水化（2026-11-02，`P4` 指定子集）

`ValidateBatchMultiSendParameters` 现在允许已有命名描述符的 OBJECT/collection 普通输入，复用
`PreparedParameters` 的 type 160 编码、typed NULL、描述符所属连接/会话/事务/代际校验，以及
national 成员在获取会话编码后的重新编码。没有添加参数 codec、选项或顺序执行兜底。
anonymous、递归 LOB/cursor 成员、显式 LOB/locator、OUT、array binding 和复杂结果仍拒绝。
这更新了 D-08h 的 named 普通输入边界，不撤销其余发送前保护。

| 检查 | 本轮证据 |
| --- | --- |
| Red | 新的 OBJECT/collection × sync/async × 成功/中途错误 **8/8** 均先因统一 NotSupported 失败 |
| 精确 wire 与边界 | 新增单测 **12/12**：整组执行包先到达才返回响应、type 160 身份/值、typed NULL、逐条计数、失败前缀/错误归属/Broken、成功后复用，以及 named BLOB/CLOB 成员仍在 prepare 前拒绝 |
| 相关回归 | multi-send 与普通 named 参数专项 **50/50**，无 skip，约 1 秒；报告 `d08-named-complex-regression.trx` |
| Oracle 直连 | 4.4.2.2 `oracle-direct` 的 OBJECT/collection × sync/async **4/4**，无 skip，约 1 秒；分组上限 2，跨两组、national 文本/NULL/空集合、根 NULL 与全 NULL 属性对象、逐条计数、连接复用、独立连接存储读回及自有对象清理均通过；报告 `d08-named-complex-live-cast.trx` |
| 构建 | 产品五 TFM（netstandard2.0/2.1、net8/9/10）Release 构建 0 警告/0 错误；集成测试项目构建同样通过；未重复包/平台大矩阵 |
| SQL 类型上下文 | 首次 `TABLE(:p)` 在 prepare 阶段失败，**不**归为服务端不支持 collection；改为 `TABLE(CAST(:p AS named_type))` 后同一用例通过，保留首次失败报告 |
| 独立走查 | 核对 Start、national 编码、描述符所有权、prepared cache、1295 拒绝边界及 reader/scalar 共用路径，无剩余问题；未重复运行实库或长测 |

报告均留在忽略目录 `TestResults/roadmap/`。本轮不外推 anonymous、LOB/cursor、复杂结果、
代理/OB20、性能收益或长期稳定性；D-08 父项保留当前限定 `P4`，不提升为 `P5`。

#### D-08j：普通命名复杂结果（2026-11-02，指定 OBJECT 子集 `P4`）

补齐 multi-send 的普通 named OBJECT/collection binary 结果，复用普通命令已有的来源列 owner
解析和复杂值解码。所有目录查询在发送业务执行组前完成；每条响应只使用对应请求的来源列映射。
prepare 和实际执行 metadata 均拒绝 anonymous、LOB/cursor 成员、locator 及未知身份，不在读取
流水化响应期间插入目录查询。reader/scalar/nonquery 共用该路径，不扩大为 OUT、array binding
或压力验收。named collection 的已有 binary wire 在 loopback 验证，本轮不新增持久 nested-table
列承诺，也不将这一部分外推为当前服务端实库验收。

走查发现旧流水化路径在逐次发送时消费会话上下文，导致顶层 NCHAR/NVARCHAR2 或 LTZ 与复杂列
混合返回时丢失 national/时区。现按请求保存预检完成后的会话快照，每次开始读取响应时恢复；
1295 文本回退同样携带快照。复杂成员继续使用其既有存储编码，不借用顶层 national 解码规则。

| 检查 | 本轮证据 |
| --- | --- |
| 功能 red | named complex 结果的 reader/scalar/nonquery × owner/来源查询 × sync/async **12/12** 原先均因统一拒绝失败 |
| 混合上下文 red | 顶层 national/LTZ 加入同组结果后，reader **4 失败/8 通过**，失败为缺少可信 national 上下文；报告 `d08-mixed-context-red.trx` |
| 定向回归 | multi-send 与普通 source lookup 相关专项 **74/74**，无 skip，约 1 秒；含新增 24 项的 NULL、混合 national/LTZ、两响应来源隔离、错误归属及 prepare/live 资源拒绝；报告 `d08-mixed-context-verified.trx` |
| Oracle 直连 | 4.4.2.2 `oracle-direct` flat/nested OBJECT × sync/async **4/4**，无 skip，约 3 秒；三条带参数 SELECT 跨两组，覆盖 national/RAW 成员、顶层 NVARCHAR2/LTZ、全 NULL 属性、三 API/缓存复用、独立连接行数及对象清理；报告 `d08-mixed-context-live.trx` |
| 构建 | 产品五 TFM Release 与集成测试项目构建均为 0 警告/0 错误；未执行长测或全平台矩阵 |
| 独立走查 | 已修复 national/时区快照 Important；复核无剩余问题，未重复运行测试或长测 |

报告保存在 `TestResults/roadmap/`。实际执行 metadata 的拒绝发生在读行或分配 locator 前；
未知响应身份会断开连接，不合并其他请求的来源映射，不猜测 NULL 对象的身份。代理/OB20、
collection 的其他服务端结果形状与长期资源稳定性仍未关闭。

#### D-08k：根 BLOB/CLOB locator 普通输入（2026-11-02，指定子集 `P4`）

移除 multi-send 对根 BLOB/CLOB locator 输入的统一拒绝，复用普通命令已有的 type 210/211
编码；不读取或上传 LOB 内容，不新增协议交互。保留协商能力、原连接/物理会话/事务代际及
已释放状态校验；typed NULL 使用 type 6。batch 期间保持会话预留，调用方保留 handle 所有权。
1295 text fallback 继续拒绝 locator 文本化；anonymous、复杂 LOB/cursor 成员、locator 结果、
OUT/cursor/array binding 仍不进入流水化。这更新 D-08h/i 的根 locator 输入边界。

| 检查 | 本轮证据 |
| --- | --- |
| 功能 red | 新增 BLOB/CLOB × inline/external × sync/async **8/8** 原先因统一拒绝失败；`d08-lob-input-red.trx` |
| 定向回归 | **24/24**，无 skip，284 ms；整组执行包先于响应、精确 locator/NULL wire、显式类型/推断类型、调用方句柄与连接复用、未协商拒绝、跨连接/reopen/事务结束/释放拒绝、复杂 LOB/anonymous 边界；`d08-lob-input-focused.trx` |
| Oracle 直连 | 4.4.2.2 `oracle-direct` BLOB/CLOB × sync/async **4/4**，无 skip，约 1 秒；三条 INSERT 跨两组、typed NULL、Unicode/零字节内容、原 handle 再绑定、独立连接逐值读取及临时 LOB/表清理；`d08-lob-input-live.trx` |
| 构建 | 集成测试项目 Release 构建 0 警告/0 错误；相关产品 net10.0 同时构建 |
| 独立走查 | 无阻塞发现；确认复用既有归属校验、预编码无额外 I/O、1295 拒绝和 LOB/cursor 结果限制保留 |

报告保存在忽略目录 `TestResults/roadmap/`。未重复实库测试或运行压力、平台/代理矩阵；其他
版本/部署及长期资源稳定性仍待发布验收，不把本次 locator 输入扩大为 LOB 输出或成员支持。

#### D-08l：根 BLOB/CLOB locator 结果（2026-11-02，保留调用方连接的指定子集 `P4`）

普通 210/211 结果复用现有 `LobSession` 和 decoder；prepare/live metadata 使用同一规则验证
Oracle、locator 协商和 CLOB 字符集。仅在调用方持有连接且不使用 `CloseConnection` 时放行，
不返回立即失效的自有连接 locator。reader 完整排空并关闭后，调用方才能打开 LOB stream；
提前关闭仍断开有未读响应的会话，已取得的 locator 随之失效。scalar 捕获首个 LOB 后若后续
响应失败，释放未交付的 handle；已知末尾 ERR 排空后连接仍可恢复。nonquery/未读取字段
只排空字节，不分配 handle。anonymous、复杂 LOB/cursor、Oracle 249–252、OUT/array 不放开。
这更新 D-08j/k 的根 locator 结果边界，没有新增公开 API。

| 检查 | 本轮证据 |
| --- | --- |
| 功能 red | reader/scalar/nonquery × BLOB/CLOB × sync/async，加上 scalar 后续失败/reader 提前关闭，共 **16/16** 因旧统一拒绝失败；`d08-lob-result-red.trx` |
| 定向回归 | **46/46**，无 skip，442 ms；含输入回归、NULL、三响应跨两组、排空前禁止读取、排空后内容/资源归还、scalar 失败清理与连接恢复、提前关闭失效、owned/CloseConnection/未协商/坏 charset 发送前拒绝和复杂资源拒绝；`d08-lob-result-final.trx` |
| Oracle 直连 | 4.4.2.2 `oracle-direct` BLOB/CLOB × sync/async **4/4**，无 skip，约 1 秒；扩展 D-08k 的实库夹具，覆盖 reader/scalar/nonquery、NULL、三 SELECT 跨两组、排空前拒读、排空后内容、调用方复用及所有 handle/临时 LOB/表清理；`d08-lob-result-live.trx` |
| 构建 | 产品五 TFM 与集成项目 Release 均为 0 警告/0 错误 |
| 独立走查 | 无阻塞发现；确认预检/live 一致、连接归属贯通、scalar 异常清理和提前关闭失效 |

中间两类新夹具假设已修正：完全排空的最后一条已知 ERR 可以保持 Open；自有连接或
`CloseConnection` 失败时允许直接关闭会话，不要求额外 statement close 包。报告均保留，
不把这些夹具错误计为产品功能失败。未运行长测、消费者全量或部署矩阵。

同轮对 anonymous 普通输入做一次有界核对：`TABLE(CAST(:p AS named_table_type))` 的普通
COM_STMT_EXECUTE 路径在 sync/async **2/2** 被当前目标服务端拒绝，未取得可复用的成功交互。
保留 batch 发送前拒绝，不将该失败单独归因为服务端能力缺失，也不继续尝试其他 SQL 变体。
探针源码和报告仅保留在忽略目录 `TestResults/roadmap/AnonymousMultiSendProbe.cs`、
`d08-anonymous-input-baseline.trx`，未增加长期失败测试或扩大产品承诺。

#### D-08m：命名复杂 LOB 内容输入（2026-11-02，指定子集 `P4`）

移除 multi-send 对 named OBJECT/collection LOB 成员的统一输入拒绝，复用既有 type 160
writer：BLOB 成员接受 `byte[]`，CLOB 成员接受 UTF-8 文本，发送 length-encoded 内容；
不读 locator，不分配 LOB handle，不新增协议交互。协商能力、描述符和会话/事务代际校验
继续由 `Start` 执行。anonymous 和递归 cursor 成员仍拒绝；命名 locator 回绑、复杂 LOB
结果、OUT/array 和 1295 complex 文本化也未放开。本项不改变 D-04a2 已确认的 BLOB OBJECT
成员直接 DML 服务端限制，不自动改写用户 SQL。

| 检查 | 本轮证据 |
| --- | --- |
| 功能 red | OBJECT/collection × BLOB/CLOB × sync/async 新增 **8/8** 原先因统一拒绝失败；`d08-complex-lob-input-red.trx` |
| 定向回归 | **66/66**，无 skip，823 ms；精确整组四包 wire、根 NULL/type 6、成员 NULL、empty LOB/collection、连接复用与零 handle 分配；包含普通复杂 LOB、未协商、anonymous 和复杂资源结果拒绝；`d08-complex-lob-input-final.trx` |
| Oracle 直连 | 4.4.2.2 `oracle-direct` OBJECT/collection × BLOB/CLOB × sync/async **8/8**，无 skip，约 3 秒；四 INSERT 跨两组，PL 函数读取成员的 SUBSTR/RAWTOHEX 后写入普通列；独立 observer 核对 Unicode/零字节内容、NULL 和空集合，writer 保持可用，function/type/table 均清理；`d08-complex-lob-input-live.trx` |
| 构建 | 产品 net10.0 及集成项目 Release 均为 0 警告/0 错误；本轮只改 NET8+ 已有校验条件，未重复五 TFM 构建 |
| 独立走查 | 无阻塞发现；确认只放开内容输入，locator 回绑、anonymous/cursor、1295 和结果限制保留 |

报告保存在忽略目录 `TestResults/roadmap/`。未运行长测、全消费者或部署矩阵，也不把本轮
PL 成员读取成功外推为所有直接 DML、LOB 输出或其他版本/代理组合。

#### D-08n：命名复杂 LOB binary 结果（2026-11-02，保留调用方连接的 OBJECT 子集 `P4`）

放开 named OBJECT/collection 的 LOB binary 结果，复用既有复杂值 decoder 与 `LobSession`，
不增加 API 或 LOB 协议。仅限调用方持有且保留连接、已协商 locator 的场景，prepare/live
metadata 使用相同规则。reader 排空并关闭后才允许打开成员 stream；batch 释放和描述缓存
清除不影响已交付的 handle。nonquery 不 materialize LOB。匿名、复杂 text LOB、cursor、
自有连接/`CloseConnection`、Oracle 249–252、OUT/array 限制继续保留。

同时修复普通 command 和 batch 共用 decoder 的失败清理：后续成员解码失败、嵌套子树成功后
父成员失败、尾部字节非法或复杂值包装失败，均递归释放尚未交付的 LOB；scalar 已捕获复杂值
而后续响应失败时也释放全部成员，不复制 RAW 数据。

| 检查 | 本轮证据 |
| --- | --- |
| 功能 red | 新增 **32/32** 在修改前失败；其中普通 command 的两项部分解码测试暴露既有句柄泄漏；`d08-complex-lob-result-red.trx` |
| 定向回归 | **74/74**，无 skip，534 ms；OBJECT/collection × BLOB/CLOB × sync/async × reader/scalar/nonquery、跨两组排空、NULL/空 locator、部分/嵌套/尾部解码失败、scalar 后续 ERR、未协商及 CloseConnection 边界；`d08-complex-lob-result-verified.trx` |
| Oracle 直连 | 4.4.2.2 `oracle-direct` 平面/嵌套 OBJECT × BLOB/CLOB × sync/async **8/8**，无 skip，约 7 秒；服务端构造整列值，三个 SELECT 跨两组，reader/scalar/nonquery、排空前拒读、batch 释放/缓存清除后内容、NULL 成员、连接复用与独立行数核对；`d08-complex-lob-result-live.trx` |
| 构建 | 产品五 TFM 与集成项目 Release 均为 0 警告/0 错误 |
| 独立走查 | 产品与测试无待修发现；文档将 CLOB charset 校验限定到根 CLOB metadata 后关闭一项措辞问题；确认 retained connection/locator 协商边界、递归清理与不复制 RAW；走查未替代实库验证 |

collection 结果只有 loopback 证据，不计实库验收。当前 BLOB OBJECT 绑定成员直接 DML、
根 NULL OBJECT 身份、持久 nested-table 与复杂 text 的既有服务端限制不变。中间失败报告
`d08-complex-lob-result-final.trx` 的四项是新增夹具错误：未预加载 named 描述及假设私有连接
可预加载描述；已修正/移除无效夹具，不归因于产品。报告保留在忽略目录 `TestResults/roadmap/`。
未运行持续压力、全消费者或部署矩阵。

#### D-08o：Oracle DML RETURNING 标量 OUT/INOUT（2026-11-02，指定直连子集 `P4`）

复用 `OracleCommandPlan`、type `0xa1` 和 `QueryResult`，将每条 DML RETURNING 的输出计划
接入 multi-send；三条请求可跨两个分组，先发送整组再消费响应。输出行对 reader/scalar
保持隐藏；该条最终 OK、输出存在性和响应边界验证通过后，在取消预算锁内原子发布。后续
命令失败保留已成功的输出前缀，失败条仍是原值；已知最后 ERR 可以复用连接，中间 ERR
仍有待消费响应时断开。缺失输出标记必须在 `ResponseComplete` 前失败，不能把会话误判可用。

显式 Prepare 的 statement ID 转交给 request 的存活 owner，预检完成不再提前结束 owner；
批次 operation 统一清理部分构造、成功、失败和提前关闭路径。DML 的 Prepare metadata
可能把 OUT/INOUT 都描述为列，修复既有只接受纯 OUT 的 split-count 限制；仍须同时满足
输出列数等于输出参数数、输入参数数加列数等于绑定位置数，不放宽 PL 或错误计数。
NUMBER 输出继续遵守既有 provider/Decimal 契约，不增加向声明整数类型的自动转换。

| 检查 | 本轮证据 |
| --- | --- |
| 功能 red | 新增首批 **16/16** 原先因统一输出拒绝失败；`d08-returning-red.trx`。另两个显式 Prepare INOUT 用例在旧 split-count 校验下失败、两个 OUT 对照通过；`d08-returning-inout-prepare-red.trx` |
| 定向回归 | **120/120**，无 skip，约 2 秒；新功能、OUT/INOUT wire、reader/scalar/nonquery、跨组、NULL、已知前缀/末尾 ERR、取消/超时/截断/非法 tail、缺失输出标记/多行、显式 Prepare 与重复执行、资源声明/metadata 拒绝及既有输出/Prepare 回归；`d08-returning-final.trx` |
| Oracle 直连 | 4.4.2.2 `oracle-direct` sync/async × 显式/隐式 Prepare × OUT/INOUT **8/8**，无 skip，约 1 秒；三个 UPDATE RETURNING 跨两组，NUMBER 与 NVARCHAR2 Unicode/NULL，多输出原子发布，reader/scalar/nonquery 连续重用，独立 observer 核对实际存储及连接复用；`d08-returning-live-verified.trx` |
| 构建 | 产品五 TFM 与重建后的集成项目 Release 均为 0 警告/0 错误 |
| 独立走查 | 无阻塞问题；专项复核 statement owner、冻结声明与实际 metadata、terminal 前验证、每条发布及 INOUT split-count 条件 |

初轮实库 **4/8** 暴露了上述 INOUT Prepare 缺陷，以及用 Int64 接受 NUMBER 后直接回绑的
夹具假设；后者改用现有 Decimal 契约。一次 `--no-build` 复跑使用了集成项目旧输出，其
`d08-returning-live-final.trx` 不作为修复后证据；以重建后的 `*-live-verified.trx` 为准。
初轮单测的缺失标记问题已修复，extra-row 夹具捕获预期的对端断连；失败报告保留。

本项只放开可缓存且无会话/类型失效的 Oracle DML 标量 OUT/INOUT。任意 PL/StoredProcedure、
ReturnValue、复杂/LOB/cursor 输出和 array binding 仍未纳入流水化；声明和实际资源 metadata
即使 NULL 也不能绕过限制。MySQL 用户变量输出、代理/OB20、跨版本与持续压力未新增验收。
报告保存在忽略目录 `TestResults/roadmap/`；未运行长测、全消费者或部署矩阵。

#### D-08p：DML RETURNING 命名复杂值与 LOB 输出（2026-11-02，指定直连子集 `P4`）

在 D-08o 的输出计划中复用现有 binary decoder，支持显式声明的命名 OBJECT/collection，
以及保留调用方连接时的根 BLOB/CLOB 和命名复杂值中的 LOB 输出。声明读取 plan 创建时冻结的
类型，纯 OUT 的旧占位值不参与输入类型推断；INOUT 继续沿用已有输入校验。opaque whole-column
metadata 先绑定声明，再执行 batch 策略；同时覆盖 header 标记和 metadata EOF 标记两种输出路径。
根 BLOB/CLOB 即使结果为 NULL 也必须匹配声明，不放宽 anonymous、cursor 成员或关闭连接的限制。

输出仍在每条最终成功响应后发布。解码到后续列时失败、terminal ERR、取消和非法多行都递归
释放未发布的 LOB；成功前缀已发布的 handle 由调用者持有，batch 释放及缓存清除不提前释放它。
同一清理入口也修复普通 Oracle 输出在部分解码或 terminal ERR 后遗留未发布 LOB 的问题。
ArrayBinding 保留独立 metadata 校验，新增普通输出 ordinal 边界不作用于它的额外状态列。

| 检查 | 本轮证据 |
| --- | --- |
| 功能 red | 原实现 **50/50 失败**；包括 batch 的资源拒绝、纯 OUT 旧占位值触发的复杂类型校验，以及普通输出未发布 LOB 泄漏；`d08-resource-returning-red.trx` |
| 定向回归 | **222/222**，无 skip，约 5 秒；覆盖 RETURNING、Oracle 输出和数组绑定；新增用例包括 sync/async 三种 API、跨组、OBJECT/collection RAW/LOB、NULL/空值、两类输出标记、成功前缀、关闭连接拒绝、NULL metadata 不匹配、部分解码/多行/取消释放；`d08-resource-returning-unit-verified.trx` |
| Oracle 直连 | 4.4.2.2 `oracle-direct` **20/20**，无 skip，约 4 秒；新模块 **10/10** 为根 BLOB/CLOB、OBJECT RAW/BLOB/CLOB 的 sync 隐式/async 显式 Prepare，另含既有 batch 标量 **8/8** 和普通 OBJECT RETURNING **2/2**；`d08-resource-returning-live-verified.trx` |
| 实库断言 | 三个 UPDATE RETURNING 跨两组，根 NULL/成员 NULL、三种 API 连续重用、batch 释放/缓存清除后读取全部保留 handle、连接复用及独立 observer 行数 |
| 构建与走查 | 产品五 TFM、集成项目均 0 警告/0 错误；本次修改独立走查无剩余阻塞；`git diff --check` |

两轮扩展单测各有 **221/222**：先修正取消测试对既有 unknown-outcome 异常的错误预期，再使
负向 fixture 接受驱动提前断连时的写失败；不为此改变产品取消语义。实库首轮 **14/20**，
六个 OBJECT 夹具在建表 DDL 前取得描述，随后被 DDL 正确失效；已移到全部 DDL/seed 后加载。
这些失败报告保留，仅以上述最终报告作为通过证据。

collection 输出仍只有 loopback 证据；不重跑已知 persistent nested-table 服务端限制。
本轮未证明资源 INOUT 的全部实库组合、完整 Prepare 交叉矩阵、PL/StoredProcedure、cursor、
array pipeline、OBProxy/OB20 或其他版本；没有长测、全消费者测试或部署大矩阵。
后续仍按本文“仍未实现的具体组合”推进；无明确 wire/归属契约的项目不通过反复长测冒充开发完成。

### D-09：XMLType 字符串 wire 子集（2026-11-02，`P4` 子集；完整 codec 未关闭）

本轮先固定服务端形状，再核对 Java 和 .NET 是否已有可复用的客户端类型路径。OceanBase
官方 Oracle 模式文档[XMLType 概述][O8-xmltype]说明：XMLType 是 XML 数据的 SQL 数据类型，可以创建 XMLType 表或视图，
并可在 PL 存储过程中作为参数、返回值和变量使用；因此不能把它归类为“服务端不支持”。
文档没有给出可供 ADO.NET 直接采用的通用 type code、binary writer 或 NULL/字符集契约。

固定版 Connector/J 2.4.14 的对照结果如下：

- `OceanBaseConnection.createSQLXML()` 直接抛出 `SQLXML type is not supported`（[源码][J2-sqlxml]）；
- `internal/ColumnType.java` 没有 XML/XMLType 枚举或专用 type code（[源码][J3-columntype]）。未知协议类型的通用回退为
  BLOB，不能据此证明 XMLType 的列值可以安全按 BLOB 解码；
- 因此 Java 的 SQLXML 拒绝可以证明客户端 API 边界，不能证明 OceanBase 的 XMLType wire，
  也不能把 SQLXML 失败扩大为“服务端没有 XMLType”。

2026-11-02 在同一 Oracle 4.4.2.2 `oracle-direct` 租户建立 `OUT XMLTYPE` 与 `IN OUT XMLTYPE`
过程，分别用 Connector/J 2.4.14 的 `Types.VARCHAR`/`Types.SQLXML` 和 .NET 的 `DbType.Xml`
调用。Java 与 .NET 对四种组合给出相同服务端错误：OUT 为 `600/0A000`
（`OBE-00600 -4007 Not supported feature or function`），IN OUT 为 `600/42000`
（`OBE-00600 -5555 Incorrect number of arguments`）。因此这两个 PL 方向在当前版本标记为
服务端限制，不继续增加 XMLTYPE 专用输出 wire 或 SQLXML API。

当前 .NET 已补齐观察到的字符串 wire 子集：`DbType.Xml` 参数按 type 253 的字符参数写入，
`ResultColumn` 对 type 160、ownerless `XMLTYPE` 的 text/binary 结果按严格 UTF-8 解码为 String，
并公开 `XMLTYPE`/String metadata；NULL 仍按 `DBNull.Value` 发布。显式 Prepare 的 ownerless
binary XMLTYPE 现按 length-encoded bytes 严格 UTF-8 解码为 String，并跳过不适用的 complex-owner 查询。
对 PL 输出返回的 `SYS.XMLTYPE`，客户端只增加 metadata 消费和错误边界；当前服务端拒绝时保持
连接可用，不把该路径写成已支持的 XMLTYPE 输出功能。
该实现复用现有字符串编码和参数长度边界，没有新增未经证实的专用 `OceanBaseDbType` 或
binary/XML DOM API。

2026-10-31 的 Oracle 直连 profile（OceanBase 4.4.2.2）进一步固定了列形状：`CREATE TABLE (... XMLTYPE)`、
`XMLTYPE('<a>患者</a>')` 插入和 `XMLSERIALIZE(DOCUMENT x AS CLOB)` 均成功；普通 `SELECT x` 的复杂列
metadata 把类型名报告为 `XMLTYPE`，但 `ALL_TAB_COLUMNS` 返回的 `DATA_TYPE_OWNER` 为空。现在 .NET
对该 ownerless text 形状使用严格 UTF-8 String 路径，NULL 保持 `DBNull.Value`；序列化结果继续按现有
`CLOB` locator 路径读取。进一步用同一租户、同一版本的 Connector/J 2.4.14 做普通查询和 server-prepare
对照：JDBC metadata 都是 `1111/COMPLEX`、Java 类 `ComplexData`；非空 XML 行的 `getObject()`
仍为 null、`getBytes()` 长度为 0，只有 NULL 行报告 `wasNull=true`。该证据确认 Java 没有可用的
XMLType payload 解码路径；.NET 当前已针对同一观察到的 ownerless text wire 提供受限 String
路径。这仍不是完整 XMLType 原生 codec，也不能把 Java 的空值行为当成 .NET 应复制的结果语义。

2026-11-02 使用项目 User Secrets 的 `oracle-direct` profile 复核 `XmlProbeTests` 的正向用例 **2/2** 通过
（异步、同步），未设置数据库凭据环境变量。回归覆盖 XMLTYPE DDL、服务端构造值、`DbType.Xml`
非空/NULL DML、普通列的 type 160 ownerless text metadata/值、PL 函数返回的同类 text 结果，
以及 `XMLSERIALIZE→CLOB` 的既有 locator 路径；单元测试覆盖参数 bytes、NULL、metadata、严格
UTF-8 和 binary 结果的正常/坏字节边界；显式 Prepare 的同步/异步 binary 直连回归为 **3/3**。
新增 PL OUT/IN OUT 负向回归使用同一 profile、sync/async 共 **4/4**，断言服务端错误码、
SQLSTATE、连接保持 Open 且结果不被标记为 unknown；同一形状的 Connector/J 对照结果见上文。

因此 D-09 的当前 text+binary String wire 子集达到 `P4`，PL OUT/IN OUT 在当前目标版本已按
服务端限制关闭，不再列为客户端待开发。父任务仍未全部关闭，未关闭项是带 owner 的复杂 XML
payload、最大值边界和跨版本部署证据。
Connector/J 2.4.14 的 SQLXML API 仍明确拒绝；这只能说明 Java 没有对应公开 API，不能把这些
未验证形状归因为服务端限制。只有取得稳定 type identity 和完整 wire 后，才扩展现有 String 子集。

### D-10：SDO_GEOMETRY wire 形状调查（2026-11-01，`P4`）

官方 Oracle 模式文档[空间数据类型概述][O9-sdo]确认：当前版本支持二维和三维的七类
`SDO_GEOMETRY` 对象；支持默认构造、WKB/WKT 构造以及 WKB/WKT/GeoJSON 转换函数；表列中的
`SDO_GEOMETRY` 按 BLOB 存储。`SDO_GEOMETRY` 本身仍是包含 `SDO_GTYPE`、`SDO_SRID`、
`SDO_POINT`、`SDO_ELEM_INFO` 和 `SDO_ORDINATES` 的 Oracle OBJECT，坐标数组上限为
1,048,576 个元素。由此可以关闭“服务端是否存在该类型”的问题，但不能从 BLOB 存储说明
推出 PL OBJECT 参数的 wire 或客户端领域 API。

固定版 Connector/J 2.4.14 的范围比“完全没有 Java 路径”更窄：

- [`OceanBaseConnection`][J6-complex-connection] 的复杂类型加载对 `SDO_` 名称尝试 SYS 所有者，
  并提供通用 `createStruct`/`createArrayOf` 入口；
- [`ComplexDataType`][J6-complex-type]/[`ComplexUtil`][J6-complex-util] 能编码通用 OBJECT、
  COLLECTION 及其已知标量成员；
- 但固定版本没有发现 SDO 专用的 WKB/WKT/SRID 值类、领域方法或独立 SDO 测试，也没有确认
  `SDO_GEOMETRY` 列/PL 参数的实际 type code 和 NULL/非法几何行为。

当前 .NET 没有 `SDO_GEOMETRY` provider type、空间 API 或 SDO 专用结果身份；已有 Oracle
`Binary`/BLOB 路径可以承载服务端 BLOB 存储的原始字节，但这不等于支持 SDO OBJECT 的属性
访问、WKB/WKT 互转或 SRID 校验。已有 MySQL `GEOMETRY` type-255 支持不能复用为 Oracle
SDO，二者的对象结构和 wire 契约不同。

VPN 恢复后在当前 Oracle 4.4.2.2 直连 profile 做了最小实测：`CREATE TABLE (... SDO_GEOMETRY)`
和 `SDO_GEOMETRY(...)` 字面量插入成功，但读取 `g.sdo_gtype`/`g.sdo_point.x` 的查询返回
服务端 `904/42S22`。同一租户的 Connector/J 2.4.14 对照中，`SELECT g` 成功返回 JDBC
`1111/COMPLEX` 的 `StructImpl`，`getAttributes()` 返回五个对象属性；属性访问查询仍返回
`904/42S22`。因此当前只能把属性访问认定为服务端限制，不能把整列读取跳过。

同一租户的 .NET 原始 metadata 探针进一步确认：未显式 Prepare 时列发 MySQL type `255`，值为
`SDO_GEOMETRY(...)` 文本；显式 Prepare 时列发 Oracle complex type `160`、名字
`SDO_GEOMETRY`，但 owner lookup 返回 NULL，旧路径随后报
“The complex source column type owner is unavailable”。现已在该路径中限定支持 `SYS.SDO_GEOMETRY`
的 owner 回退和系统 OBJECT/ARRAY 描述缓存；`SdoGeometryTests` 在同一 Oracle 直连 profile
的 sync/async 两种执行方式均通过，覆盖非空对象、五个成员、嵌套 `SDO_POINT_TYPE`、NULL
成员和 NULL 行。属性访问仍保持服务端 `904/42S22`，因此没有扩大为属性表达式支持，也不能把
Oracle type `255` 直接当成 MySQL `GEOMETRY`。

在同一 `oracle-direct` profile 增加了 generic OBJECT/ARRAY 输入验收：从 `SYS.SDO_GEOMETRY` 描述创建包含非空
`SDO_ELEM_INFO_ARRAY` 和 `SDO_ORDINATE_ARRAY` 的值，绑定到 `SDO_GEOMETRY` 列，再通过独立
`SELECT` 读回并逐项比较；同步/异步新用例 **2/2** 通过。该测试证明的是已有通用复杂值编码路径对这一命名
OBJECT 形状的输入闭环，不代表新增 SDO 专用 provider type 或空间 API。

调查结论：D-10 的显式 Prepare 整列读取与 generic OBJECT/ARRAY 输入子集为 `P4 / 已实现并验收`；本次在当前
`oracle-direct` profile 复核 **4/4**（sync/async × 两个形状），其余仍为
`P0 / 待 wire 与契约`。当前没有 `SDO_GEOMETRY` provider type、专用 WKB/WKT/SRID API、非法几何校验、
坐标上限和独立空间语义契约；若纳入这些能力，仍需在目标 Oracle 直连 profile 中补 metadata、SRID、非法
几何、坐标上限和独立读回证据，不新增 SDO enum 或把 GEOMETRY bytes 冒充 SDO。

### D-11：ANYDATA PL 形状调查（2026-10-31，`P0`）

官方[ANYDATA 概述][O10-anydata]给出了明确的服务端边界：`ANYDATA` 是包含实例和类型描述的
自描述类型，当前**仅支持在 PL 中使用**，不支持作为表列或永久存储，也不支持包含嵌入式 LOB
的实例永久存储；该内容只适用于企业版 Oracle 模式。官方列出的 `CONVERT*` 构造函数和
`ACCESS*` 成员函数覆盖 NUMBER、日期/字符、RAW/LOB、OBJECT/COLLECTION、时间戳、区间、
NCHAR/NVARCHAR2、浮点和 UROWID 等多种内部类型；访问类型不匹配时返回 NULL。

这意味着 ANYDATA 不能按普通持久列类型建立一个“读写 bytes”契约。固定版 Connector/J 的
`ColumnType` 没有 ANYDATA protocol type，通用复杂值描述也只覆盖 OBJECT/COLLECTION 及已知
成员，不等于支持 PL ANYDATA 的自描述类型和逐段访问。当前 Oracle 4.4.2.2 直连 profile
进一步执行 `SELECT SYS.ANYDATA.CONVERTVARCHAR2('患者') FROM DUAL` 和
`SELECT SYS.ANYDATA.CONVERTNUMBER(7) FROM DUAL`：Connector/J 2.4.14 两条均返回
`OBE-00942`；.NET 对同一 SQL 均返回 `42S02`。这确认的是目标服务端没有可调用的
`SYS.ANYDATA` 包，满足 Java 同交互失败的服务端限制条件。

当前 .NET 同样没有 ANYDATA provider type、内部类型标识或公共 PL wrapper。可审计的现有路径
是让 SQL/PL 先调用 `SYS.ANYDATA.CONVERT*` 和 `ACCESS*`，再通过已有标量、LOB 或复杂值路径
传输；这属于服务端转换，不关闭 ANYDATA 客户端能力缺口。

调查结论：D-11 在当前目标版本标记为**服务端限制，跳过客户端开发**。只有后续目标版本/租户
能成功调用 `SYS.ANYDATA.CONVERT*`，并提供 Java/.NET 可观察的参数或返回 wire 时，才重新定义
有限 PL 参数/返回值集合及 NULL、类型名、未知内部类型、LOB、OBJECT/COLLECTION 和失败后的
输出发布规则；不把 ANYDATA 当作普通列或任意对象兜底。

### D-12b：RoaringBitmap wire 形状调查（2026-10-31，`P4`）

官方 MySQL 模式文档[位图类型概述][O11-roaring-overview]和[列定义示例][O11-roaring-ddl]
已经把服务端形状固定下来：`ROARINGBITMAP` 是存储无符号整数集合的独立类型，支持 64 位整数；
列 DDL 形如 `ROARINGBITMAP`，值可以由 `rb_from_string('1,2,3')` 或十六进制字节串构造，
客户端查询默认显示二进制结果（文档示例为 `0x010303010000000200000003000000`）。官方
函数还提供 `rb_build_varbinary`/`rb_to_varbinary`、`rb_to_string`、`rb_to_array` 以及
集合运算和基数函数。由此可以确认它不是 `BIT(1..64)` 的数值别名，也不是 VECTOR/ARRAY
的文本列。

固定版 Connector/J 2.4.14 的 `ColumnType` 没有 RoaringBitmap 专用枚举或类型码；本轮也没有
新增 .NET provider type。VPN 恢复后在 OceanBase 4.4.2.2 MySQL 直连 profile 实测：DDL 成功；
`rb_from_string('1,2,4294967295')`、空集合、NULL 和 `18446744073709551615` 插入成功；查询
metadata 为 `BLOB`/`System.Byte[]`，raw 长度 15，`rb_to_string` 返回原集合，`rb_to_array`
返回 `CHAR`/`String`。现有 `DbType.Binary` 参数经 `rb_build_varbinary(?)` 可插入并独立读回；
同一 `byte[]` 直接绑定到 `ROARINGBITMAP` 列稳定返回服务端 `1235/0A000`，坏 raw 也由服务端
返回相同错误。该结果把直接 native prepared binding 归为服务端限制，现有 .NET Binary + SQL
函数路径已覆盖当前版本可用子集，不新增 enum、parser 或 BIT/JSON/String 假映射。

补充了可复现的 `MySql/RoaringBitmapTests.cs` 真实集成回归：MySQL 直连 profile 的 sync/async
共 **2/2**，使用 User Secrets 连接串，不把凭据写入环境变量。测试覆盖 BLOB/`byte[]`
metadata、普通集合、空集合、NULL、`uint64` 最大值、`rb_to_string`/`rb_to_array`、显式
Prepare 的 `rb_build_varbinary(?)` 回绑，以及坏 raw 和直接 `byte[]` 绑定的已知服务端错误；
回绑后的 raw 和集合文本由同一连接再次读回，失败调用后行数保持不变。

### A-01：池容量与高并发借还专项（2026-11-01，部分验收）

新增 `Connections/PoolStressTests.cs`，沿用集成测试的 User Secrets profile，不读取或写入凭据
环境变量。每个模式分别运行同步和异步用例：16 个并发调用者、每个调用者 625 次打开/查询/释放，
总计 10000 次借还；池上限固定为 4，并通过实际 `PhysicalSession` 身份集合确认同时复用的物理会话
不超过上限。每次查询都走公开 `OceanBaseDataSource` 和 `OceanBaseConnection` API。

| 检查 | 结果 |
| --- | --- |
| Oracle 4.4.2.2 `oracle-direct` | **2/2**（sync/async），10000 次借还/项，0 失败、0 跳过；物理会话数不超过 4 |
| MySQL 4.4.2.2 `mysql-direct` | **2/2**（sync/async），10000 次借还/项，0 失败、0 跳过；物理会话数不超过 4 |
| 范围 | MySQL/Oracle 10000 次容量/复用专项及 Oracle reader/LOB/cursor/XA 旧租约隔离 **52/52** 已通过；60 分钟交错、清池和句柄/线程/内存稳定性仍未关闭 |

A-01 继续保持 `P3`。本轮没有产品源码改动；完成长期交错、清池和资源稳定性检查后再提升状态。

### A-01 续：持续资源交错验收执行计划（2026-11-02）

规格沿用 `acceptance-criteria.md` 的池/资源门槛，不扩大 SDK 的协议或部署范围。
复用 IntegrationTests 和现有 User Secrets profile；新增显式 opt-in 的持续测试，避免默认实库组隐式运行数小时。
16 个调用者共享 MaxPoolSize=4 的私有 DataSource，交错 sync/async、prepared cache、半读 reader、
已知错误、取消、Clear 和该模式的 LOB/cursor/XA；独立连接负责种子数据、查账与清理。
采样进程句柄/线程/内存和驱动池 session/lease/waiter 指标；结束后验证池归零和自有分支/数据清理。
短跑只证明工具和场景可执行，不关闭 60 分钟持续性门槛。无需增加产品 API 或依赖。

- [x] 为 opt-in 时长验证、池指标采集添加本地行为测试，先确认失败再实现。
- [x] 实现有界持续运行、资源场景、CSV 采样、故障传播和清理；补运行说明。
- [x] 五资产构建、格式检查和双模式短跑，修复夹具中发现的问题。
- [x] 完整本地回归、节奏/清理修正后的双模式短跑和最终独立走查；保留失败和未关闭边界。
- [ ] 双模式至少 60 分钟专项与资源曲线判读。

实现为 `Connections/PoolSoakTests*.cs` 和测试侧 duration/metrics 工具，不新增产品 API、依赖或默认开关。
`OB_POOL_SOAK_SECONDS` 未配置时明确跳过，非法配置失败；短跑不会冒充一小时验收。
采用独立全局 XA ID，run 前缀只用于清理归属；不让一个 worker 的 rollback 中止另一个 worker 的分支。
保留原始失败报告，包括 MySQL BLOB 容量、未排空 streamed reader 尾包、已完成 XA 专用租约上普通 SQL
以及并发 worker 共享全局 XA ID 的夹具失败；修正夹具而不放宽产品校验。

| 检查 | 本轮证据 |
| --- | --- |
| 红/绿 | duration 校验初次 9 项失败、metrics 初次 1 项失败；实现后本地 13/13；原始 TRX 保留在 `TestResults/roadmap/a01-soak-*-red.trx` |
| 限速前短跑 | `a01-soak-final.trx` **15/15**，0 skip：13 本地配置/指标 + MySQL/Oracle 各 10 秒；分别完成 2780/659 轮，pool session/lease/waiter 最终均为 0；不能外推后续节奏修正 |
| 节奏/清理修正后短跑 | `a01-soak-paced-final.trx` **15/15**，0 skip：13 本地配置/指标 + MySQL/Oracle 各 10 秒；分别完成 140/137 轮，八种场景各至少 16 次，最终 session/lease/waiter 均为 0；独立 XA/回滚数据/LOB 内容及表清理成功 |
| 短跑独立查账 | 自有 XA recover 无残留、DML 轮回全部回滚、外置 BLOB/CLOB 内容不变、对象清理由 DatabaseContext 完成；不是服务端 prepared/locator 目录逐项盘点 |
| 五资产 | `dotnet build OceanBaseConnector.slnx -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false`，0 warning/error |
| DI / 未 opt-in | DI **14/14**；完整 Integration no-opt **36 pass / 244 skip**，不把跳过算作实库成功 |
| 修正后完整本地回归 | `a01-unit-unsandboxed.trx` **8749/8749**，0 skip，独占运行约 10 分钟；`a01-soak-noopt-revised.trx` **36 pass / 244 skip**；六个新增文件限定格式检查再次通过 |
| 格式 | 六个新增 C# 文件限定 `dotnet format whitespace --verify-no-changes --no-restore` 通过；首次命名管道权限失败后沙箱外验证 |
| 独立走查 | 已修复发现的清理中断、仅 pre-cancel、LOB 旧流提前释放/假阳性、类型与 sync/async 绑定，以及 wait-holder 失败泄漏；最终无 Important/Critical |
| 首次一小时尝试 | 两种直连 profile 与完整 Unit 同时运行；约 1 分钟时均遇到 `AddressAlreadyInUse`，**2/2 失败**，不能计通过；TRX 为 `a01-soak-mysql-60m.trx` / `a01-soak-oracle-60m.trx` |
| 端口冲突排查 | 主机快照有 5768 个 TIME_WAIT，动态端口范围 16384；停止并行压力后原失败 Unit 组在沙箱内外均 **8/8**，尚不能仅凭该快照确定完整根因；原全量 Unit 已中止，另行完整复验 |
| 清理与持续负载修正 | 一张 Oracle 自有表清理因新连接失败而遗留，随后用 User Secrets 独立清理成功；持续测试改为预留 observer/recovery 连接并由 observer 删除自有表；先登记 CREATE 归属再发送，保留 unknown 失败；每轮持有租约额外 250ms，限制端口 churn 且保持等待争用，不作为吞吐基准 |

A-01 父项仍为 `P3`。持续验收工具已通过完整 Unit、双模式短跑和独立走查，可作为本地代码模块提交；
一小时专项结果和长期资源曲线尚未取得，不以限速后短跑或进程内指标关闭持续性验收。
未验收真实 TLS/ODP/HA、其他 OS/runtime，也不把客户端会话关闭等同服务器资源目录逐项归零。

#### A-01 持续测试失败与诊断修复（2026-11-02，未通过一小时验收）

两条限速 3600 秒长跑均提前失败，保留原 TRX/CSV，不标记通过：

| profile | 原始报告 | 实际持续时间 / 完成轮次 | 可确认的事实 |
| --- | --- | --- | --- |
| mysql-direct | `a01-soak-mysql-paced-60m.trx` | 约 30 分 26 秒 / 26281 | 最终池 sessions/leases/waiters 为 0；输出只保留 worker `TimeoutException` 类型，TRX 被数据库清理异常覆盖 |
| oracle-direct | `a01-soak-oracle-paced-60m.trx` | 约 30 分 21 秒 / 22362 | 最终池 sessions/leases/waiters 为 0；输出只保留 worker `InvalidOperationException` 类型，TRX 被数据库清理异常覆盖 |

失败前客户端采样未出现持续资源增长，但不足以排除服务端或驱动缺陷；仅凭约 30 分钟的时间点，
不能归因为预留连接空闲超时、网络或服务器。原测试的 `await using DatabaseContext` 在退出时抛出
DROP 失败，覆盖了此前聚合的 worker/reconciliation 错误，这是已定位的测试诊断缺陷。

修复仅限持续验收工具：把执行体及 setup 纳入同一数据库清理边界，执行和清理同时失败时保留两者；
始终保留采样/清池错误，等待全部 worker 结束后补齐其余 worker 错误。原验收断言、负载和超时未放宽。
`PoolSoakCleanupTests` 使用真实 DatabaseContext 的登记/逆序/逐项清理，覆盖执行与清理的四种成败组合：
`a01-cleanup-red.trx` 在旧语义下 1 fail / 3 pass，修复后 `a01-cleanup-green.trx` **4/4**。
`a01-cleanup-noopt.trx` 完整未 opt-in Integration 为 **40 pass / 245 skip**；限定格式检查通过。
最终重建后的 `a01-cleanup-smoke-final.trx` 为 **19/19**、0 skip，包含双模式短跑及配置/清理检查。
独立只读走查指出并已修复监控/worker 并发失败时丢失原错误的边界，复核无 Critical/Important。

本轮失败所属四张表及匹配 run-prefix 的 XA 分支已通过独立连接清理；XA Recover 后先 Close/Open
再发普通 DDL，避免误用 XA 专用会话。再次 Recover 与目录查询确认自有分支/表均无遗留；仅处理
原报告中的准确对象名和格式 `0x4F4253`/run-prefix，不清理其他测试或业务对象。
临时清理 helper 构建仅有缓存的 NU1900 漏洞源 TLS 警告，核心测试构建/验收不受此警告替代。
下一步以保留完整错误的版本重跑双模式长稳；A-01 继续为 `P3`。

#### A-01 诊断重跑：40 秒失败与单 profile 对照（2026-11-02）

基线 `1523dbd` 的两条 3600 秒诊断重跑均提前失败：
`a01-soak-mysql-diagnostic-60m.trx` 约 39 秒、335 轮；
`a01-soak-oracle-diagnostic-60m.trx` 约 40 秒（CSV 负载约 33 秒）、271 轮。
这次完整保留了 worker 及关联异常；MySQL 有 Open 握手读取时 peer 关闭、池等待超时，
Oracle 有 reader/XA Close/Open 时池等待超时及其后的取消。报告按 worker 排列，不能据此确定
最先发生的错误或认定两种模式有相同根因。两组最终池计数归零，自有对象与 XA 分支已再次独立复查无遗留。

Oracle 在 30 秒采样的 sessions=4/leases=0/waiters=12 也可能表示四个 Reset/Probe 正在进行：
EndBorrow 早于 Reset，而 TrackBorrowed 晚于 Probe，不能把该瞬间解读为空闲容量死锁。
独立只读走查未找到可由现有证据确定的容量泄漏或循环等待；不增加重试或放宽超时以绕过失败。
失败后基本连通性/旧 reader 租约复验 `a01-diagnostic-connectivity.trx` 为 **16 pass / 1 skip**，
skip 是既有 alternateDatabase 配置门槛；单独 Oracle `a01-soak-oracle-isolated-90s.trx` **1/1**、约 93 秒通过；
加入阶段诊断后单独 MySQL `a01-soak-mysql-isolated-90s.trx` **1/1**、约 91 秒通过。
这些对照不关闭间歇故障，也不把两个 test host 的结果等同单 profile 验收。

新增的测试诊断只把 Open/场景执行/限速等待一起纳入原有 worker 异常包装，记录单调经过秒数、
阶段和池计数，保留 inner exception；无端点/凭据/参数新日志，负载及超时保持原规格。
该版本 Integration Release 构建 0 warning/error、限定格式和 diff 检查通过；
`a01-phase-smoke.trx` 双模式短跑/配置/清理 **19/19**，`a01-phase-noopt.trx` **40 pass / 245 skip**。
独立只读复核无 Critical/Important。
下一轮以单 profile 运行，结合首个失败时间与调用栈继续定位；没有证据前不修改产品池实现。
原 30 分钟失败曲线保存为 `TestResults/roadmap/a01-resources-failed.svg`/`.json`，明确标记 FAILED，
不再把已终止的失败数据标为 RUNNING 或一小时成功。

### A-02 续：环境事务 COMMIT 回执丢失的实库闭环（2026-11-02）

规格沿用 X02/A-02 和 `acceptance-criteria.md` 的 unknown-outcome、独立查账、无重放与旧资源隔离要求。
已有 `AmbientEnlistment.SinglePhaseCommit` 和 Unit/公开 DLL 消费者会把未知 COMMIT 结果通知协调器为
`InDoubt`，但真实 Integration 仅验证普通事务回执丢失。复用 `TransactionResponseDropProxy`，
在读到真实 COMMIT OK 后截断整包或仅转发头部/marker；不增加产品 API、恢复协调器或新的 relay。

范围：新建 `Faults/AmbientResponseLossTests.cs`；双模式 × sync/async SQL × 自动/显式 enlist ×
整包/半包回执，共 16 项。`CommittableTransaction.Commit` 的协调器回调本身保持同步，不冒充异步提交 API。
每项确认 PRE-COMMIT 不可见、`TransactionInDoubtException` 和底层 unknown、协调器 InDoubt、连接 Broken、
真实 COMMIT 恰一次、独立已提交行数，以及容量 1 池新租约、旧事务晚释放、后续写入/回滚和自有对象清理。

- [x] 补真实故障用例，复用现有 fixture 与私有 profiles；不以已有合成成功冒充实库证据。
- [x] 构建与限定格式检查；A-01 持续测试期间使用独立构建输出，避免改写正在运行的程序集。
- [x] 双模式 16 项限定用例通过；A-01 停止后执行关联故障/环境事务回归，80/80、0 skip。
- [x] 独立走查新增测试和实库证据，无 Critical/Important；限定模块可本地提交。

准备结果：独立 `OutputPath=TestResults/roadmap/a02-ambient-build/` Release 构建 0 warning/error，
限定格式检查通过；`a02-ambient-noopt.trx` 为 36 pass / 245 skip，新实库用例在未 opt-in 时明确跳过。
首次构建仅有测试侧条件表达式把事务推断为 `DbTransaction` 的编译错误，补显式转换后通过，
不是产品缺陷或真实红绿。新增文件的独立只读走查无 Critical/Important；
`a02-ambient-live.trx` 双模式新用例 **16/16**，0 skip，约 2 秒；真实 OK、InDoubt、独立数据、
新租约/旧事务晚释放及对象清理均通过。`a02-ambient-related.trx` 的环境事务、普通事务回执丢失、
写入边界和中断事务关联回归 **80/80**，0 skip，约 47 秒（包含上述 16 项，不重复累计）。
最终独立只读走查已核对测试及 TRX，无 Critical/Important。产品源码无变更；A-02 父项仍为 `P3`，
真实节点/代理或协调器崩溃、XA 持久恢复及更多部署并未因此关闭。
限定用例调整为在持续测试期间单独 host 执行：长稳约 14 分钟时 TIME_WAIT=2877，动态端口范围
16384，已有余量；本组仅 16 项顺序执行，不启动全量 socket-heavy 回归。后续资源曲线须注明这段
短时负载，不能把同时运行不同进程误称完全隔离的主机基准。

### A-04 续：当前 Windows 包资产与独立运行时复验（2026-11-02）

当前主机实际已安装 .NET 8.0.33 / 9.0.22 / 10.0.14，可直接复用 `eng/Verify-Packages.ps1`
验证三个现代资产各自的 runtime；netstandard2.0/2.1 保留 net10 host 加载的身份，不当独立 runtime。
从当前源码生成新的本地 nupkg，验证包 hash、依赖与精确资产选择；使用独立 BaseOutputPath，
避免改写 A-01 已加载的程序集。不新增运行时安装、外部 CI、产品 API 或依赖。

- [x] 当前源码 core/DI 打包，各五资产均为 0 warning/error。
- [x] 运行现有包验证脚本，检查 net8/net9/net10 实际版本及 netstandard host；有 targeting pack 时另验 net48。
- [x] 用现有私有 profiles 通过子进程 stdin 验证包的双模式存储/读回/清理，避免连接串进入命令行和报告。
- [x] 独立走查包来源、运行时与实库证据的范围；无 Critical/Important，本地提交证据记录。

| 检查 | 本轮证据 |
| --- | --- |
| 包来源 | 当前源码基线 `ff0609e`；core/DI 各五资产 Release 打包成功，0 warning/error；新包在 `TestResults/roadmap/a04-packages/`；无发布或推送 |
| 包门禁 | `eng/Verify-Packages.ps1 -PackageDirectory TestResults/roadmap/a04-packages -VerifyNetFramework -VerifyLive` 通过；日志 `a04-package-live.log`，独立 restore graph 验证两个 package 的 SHA512、精确 TFM 资产和依赖组，不加载旧 feed 的同名包 |
| 现代运行时 | net8.0 → 8.0.33，net9.0 → 9.0.22，net10.0 → 10.0.14；消费者校验 `Environment.Version.Major`，每项 8 个 loopback 协议场景及 8 个真实 MySQL/Oracle、sync/async、buffered/sequential 场景 |
| netstandard host | netstandard2.0/2.1 均由 net10.0.14 host 加载；netstandard2.0 另由 .NET Framework 4.8.10008.0 / CLR 4.0.30319.42000、Release=534296 执行；这三项各 8 个 loopback + 8 个实库场景，不记作独立 netstandard runtime |
| 实库与清理 | 共 6 组 × 8 = **48/48**，每组输出均严格匹配成功和 cleanup 标记；两模式直连 4.4.2.2、TLS Disabled；覆盖 prepared 输入、text/binary/NULL、独立事务可见性、回滚、错误恢复与池重开；async 场景仍包含同步事务步骤，不宣称全程异步事务或全部 provider API/ORM |
| 私有配置 | 读取既有 User Secrets 及 profile 选项，按现有脚本契约在调用进程内临时提供其两个环境变量，完成后恢复；脚本通过重定向 stdin 把连接信息交给消费者，未放入命令行、文件或报告 |
| 范围 | 本机 WSL 未安装，无可用非 Windows 执行端点；不安装系统组件或以 Windows 结果外推 Linux/macOS；A-04 父项仍为 P3 |
| 独立走查 | 核对新包 SHA512、保留 restore graph、compile/runtime 资产、消费者源文件和日志；无 Critical/Important；`git diff --check` 通过 |

完整包、消费者、依赖图在 `TestResults/package-validation/e7f7abb582c74c799d356995d5666583/`；
日志只记录固定成功信息、资产和 runtime。包门禁与 A-02 的短时验证发生在 A-01 持续运行期间，
应在长稳资源曲线记录中保留该背景；这不是性能基准或生产部署证据。

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
[O8-xmltype]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/500.sql-reference/100.sql-syntax/300.common-tenant-of-oracle-mode/300.basic-elements-of-oracle-mode/100.built-in-data-types-of-oracle-mode/1100.xml-date-type-of-oracle-mode/100.xml-data-type-overview-of-oracle-mode.md
[J2-sqlxml]: https://github.com/oceanbase/obconnector-j/blob/6adc311ba5db8b2f19f98f3e6e4734667fdfdda1/src/main/java/com/oceanbase/jdbc/OceanBaseConnection.java#L1778-L1785
[J3-columntype]: https://github.com/oceanbase/obconnector-j/blob/6adc311ba5db8b2f19f98f3e6e4734667fdfdda1/src/main/java/com/oceanbase/jdbc/internal/ColumnType.java#L69-L118
[O9-sdo]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/500.sql-reference/100.sql-syntax/300.common-tenant-of-oracle-mode/300.basic-elements-of-oracle-mode/100.built-in-data-types-of-oracle-mode/1200.spatial-data-type-of-oracle-mode/100.spatial-data-type-overview-of-oracle-mode.md
[O10-anydata]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/600.pl-reference/300.pl-oracle/1400.pl-system-package-oracle/27800.anydata-type-oracle/100.anydata-type-overview-oracle.md
[J6-complex-connection]: https://github.com/oceanbase/obconnector-j/blob/6adc311ba5db8b2f19f98f3e6e4734667fdfdda1/src/main/java/com/oceanbase/jdbc/OceanBaseConnection.java
[J6-complex-type]: https://github.com/oceanbase/obconnector-j/blob/6adc311ba5db8b2f19f98f3e6e4734667fdfdda1/src/main/java/com/oceanbase/jdbc/extend/datatype/ComplexDataType.java
[J6-complex-util]: https://github.com/oceanbase/obconnector-j/blob/6adc311ba5db8b2f19f98f3e6e4734667fdfdda1/src/main/java/com/oceanbase/jdbc/extend/datatype/ComplexUtil.java
[O11-roaring-overview]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/500.sql-reference/100.sql-syntax/200.common-tenant-of-mysql-mode/100.basic-elements-of-mysql-mode/100.data-type-of-mysql-mode/1000.roaring-bitmap-data-type-of-mysql-mode/100.roaring-bitmap-data-type-overview-of-mysql-mode.md
[O11-roaring-ddl]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/500.sql-reference/100.sql-syntax/200.common-tenant-of-mysql-mode/100.basic-elements-of-mysql-mode/100.data-type-of-mysql-mode/1000.roaring-bitmap-data-type-of-mysql-mode/200.create-roaring-bitmap-columns-of-mysql-mode.md
[O12-record-deserialize]: https://github.com/oceanbase/oceanbase/blob/5e8a63d29dae9fde5e962b8fcc7b167d438fc343/src/pl/ob_pl_user_type.cpp#L2267-L2352
[O12-pl-deserialize]: https://github.com/oceanbase/oceanbase/blob/5e8a63d29dae9fde5e962b8fcc7b167d438fc343/src/pl/ob_pl_type.cpp#L1182-L1267
[O12-basic-param]: https://github.com/oceanbase/oceanbase/blob/5e8a63d29dae9fde5e962b8fcc7b167d438fc343/src/observer/mysql/obmp_stmt_execute.cpp#L2360-L2700
