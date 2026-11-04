# 真实兼容性与未验收部署

更新：2026-11-04。本页保留当前支持声明的限制，不重复开发流水。
范围以[能力矩阵](obconnector-dotnet-progress.md)为准，判定按[验收标准](acceptance-criteria.md)。
下面结果来自各自注明日期的报告；2026-11-03新增D-07e/f/g/h字符集指定存储和结果专项，
以及D-08ab的multi-send会话/DDL屏障、D-08ac的batch named OBJECT/collection locator输入、D-12c1的一至六层INT ARRAY和D-02c1的MySQL脚本输出专项。
未重跑完整实库矩阵，也没有关闭既有失败。

D-04k：本轮 Oracle 4.4.2.2 `oracle-direct`（TLS Disabled）新增 named OBJECT/collection/nested-collection BLOB/CLOB locator Input 与独立标量 OUT/INOUT/函数 ReturnValue，显式 Prepare/重复执行/提交后独立读回 **36/36** 通过、无 skip。该证据不关闭复杂 locator INOUT 的 `900/42000`、长期压力或其他部署边界。

D-02c2：当前 `mysql-direct` 已可运行。脚本标量 ReturnValue 的基础发布、新增存储/失败前缀与后缀/batch/半读关闭，以及既有 OUT/INOUT 回归合计 **6/6** 通过、无 skip；仅关闭该直连功能子集，其他部署仍欠验收。

D-04l：同一 Oracle profile 的短 LOB（BLOB 6 bytes / CLOB 7 chars）named locator Input 与独立 complex OUT/函数 ReturnValue（与输入同一 named type）、根 BLOB/CLOB OUT、REF CURSOR OUT 组合 **36/36** 通过、无 skip，复用已有产品实现。含显式 Prepare、PL 错误不发布、两次执行、父命令释放后在原事务内读取资源、独立句柄释放与提交后观察存储；不扩展 D-04j 的复杂 locator 自身 INOUT、复杂 cursor 成员、脚本或同组流水化范围。

D-03b2：同一 Oracle 4.4.2.2 直连 profile 的父 REF CURSOR offset 默认值与显式 reader 覆盖已验收，相关组 **46/46** 无 skip：原显式 reader 12、父默认/覆盖24、函数返回2、过程生命周期8。覆盖空/三行、Default/SequentialAccess/SingleRow、Prepare、父释放、错误后旧输出保留与同会话恢复；真实端点使用 legacy EOF，DEPRECATE_EOF/OB20 仍仅本地协议证据。

D-08ad：当前 MySQL/Oracle 4.4.2.2 直连、TLS Disabled、未请求 OB20、Windows/.NET 10 的普通
prepared UPDATE 短时对照 **4/4** 通过（sync/async，每配置5次；顺序与 multi-send 分组1/4/16）。
80个样本全部提交、独立查账和测试清理通过；分组16相对顺序路径命令吞吐在本次负载中为
4.15×～8.62×。分组1已存在执行路径开销差异，不能把全部收益归因于分组；资源仅为进程级粗诊断。
原始样本、分位、资源、失败率口径和复现命令见路线图 D-08ad；不外推生产SLO、其他负载或长期稳定性。

D-04m：named OBJECT/scalar collection/nested collection BLOB/CLOB locator Input 与独立标量 INOUT、同一 named type 的 complex OUT/函数 ReturnValue、根 LOB OUT、REF CURSOR OUT，现已在 mixed batch routine屏障中通过 Oracle 4.4.2.2 直连 Text/过程/函数 × sync/async **36/36**。覆盖显式 Prepare、PL错误的已执行前缀与未执行后缀、旧输出保留、原batch重复执行、batch释放后原事务内读取资源及提交后独立查账；复用已有产品代码。只关闭短LOB/调用方连接的指定组合，不代表同组PL流水化、locator自身INOUT或其他部署。

D-04f1：匿名 INTERVAL 工厂已接入现有204/205布局；`ExecuteArray` 的 INSERT/RETURNING、NULL、负值/纳秒和重复 Prepare 执行已通过当前 Oracle 4.4.2.2 直连8项正向验证。过程输出的未知精度仍安全拒绝，4项负向回归单列；普通0x17匿名绑定、PL集合输出和multi-send不由此关闭。

D-04f2：匿名INTERVAL结果描述已接入204/205，沿用嵌入精度和显式输出身份校验；本地及消费者完成，首轮PL本地显式精度9集合OUT直连4/4。扩展根NULL/错误后发布/独立事务查账的实库未完成，事故用例保持隔离且不计通过；恢复后的两模式基础检查及已知遗留表清理已完成，当前保持P3，详见路线图；未知精度255仍拒绝。

## 环境矩阵

2026-11-04恢复复查曾成功登录两模式4.4.2.2并完成普通/Prepare查询，旧遗留表已清理；
随后D-04f2扩展复测0/4，首例循环执行时响应中断、其余Open失败，两端点再次无greeting。
当时新增遗留表 `OBI_1FF78DFC852E4074915A` 清理失败；用户要求等待数据库稳定。
该扩展用例现明确隔离Skip，保留P3及失败记录；不能据此认定数据库损坏原因、服务端版本不支持
或功能验收成功。事故时间和阶段见路线图，恢复后先基础检查，不自动重跑事故用例。

用户再次通知恢复后的基础复核：两模式均成功登录4.4.2.2，普通及显式Prepare查询返回1；
两个已知任务表均确认不存在（后一次遗留表已精确DROP并查目录确认0）。仅关闭此次连通性与
任务对象清理问题，不关闭D-04f2扩展验收或事故根因调查；事故用例仍隔离，未重跑。

| 组合 | 历史证据 | 仍未验收 |
| --- | --- | --- |
| OceanBase4.4.2.2 MySQL直连、Windows、TLS Disabled | CRUD/类型/事务/池/LOCAL INFILE/批处理等子集；新增专项各有记录 | 全版本、全部API/负载/故障组合，不承诺原生MySQL兼容 |
| OceanBase4.4.2.2 Oracle直连、Windows、TLS Disabled | PL输出/LOB/复杂值/数组/XA/字符集等子集；同时保留真实失败 | 下表服务端/权限与形状失败，全版本/全部组合，不承诺原生Oracle兼容 |
| GB2312/EUCKR 客户端/结果映射 | MySQL 直连客户端/结果与 Oracle NCHAR/NVARCHAR2 national 真实 4.4.2.2 roundtrip；严格 CP20936/51949、collation 24/86/19/85 | 当前目标 Oracle 普通客户端 EUCKR 被服务端 900/42000 拒绝，Connector/J 同一序列也失败，按服务端限制跳过客户端改动；其他版本/代理仍未验收 |
| CP932/UJIS/EUCJPMS 客户端/结果/national映射 | 官方全表与MySQL4.4.2.2转换一致；每族MySQL客户端2、canonical/alias结果2和Oracle national2成功，见D-07f/g/h | 普通Oracle客户端均在第三条SET results=utf8mb4返回900/42000，Connector/J同交互匹配；不关闭其他部署、runtime及存储组合 |
| multi-send 会话/DDL 屏障 | 4.4.2.2 MySQL/Oracle直连sync/async **4/4**：自有连接NonQuery的DDL/后续新列更新、调用方Scalar与reader的时区切换、独立存储核对；安全段保持流水化 | SET/DDL等单独执行，不是同组流水化；脚本正向/错误/取消/旧类型与缓存失效另有本地证据；其他部署及跨平台未验收，见D-08ab |
| multi-send pending response 取消查账 | 4.4.2.2 MySQL/Oracle直连、Windows/.NET 10、TLS Disabled/无压缩/OB20，sync cancel/timeout及async token/cancel/timeout **10/10**；中继放行第一条真实OK、扣住第二条，独立查账确认id1/2已提交、id3下一组未发送，见D-08f1 | 第二条unknown-outcome、连接Broken、无自动重放；取消不代表回滚，不推断同组后缀未执行；单上游中继不是ODP/HA/节点故障证据，其他断点/部署仍欠验收 |
| multi-send named OBJECT/collection BLOB/CLOB locator 输入 | 4.4.2.2 Oracle直连 sync/async × OBJECT、scalar collection、nested collection × BLOB/CLOB **12/12**：batch constructor + hidden root LOB 输入，三条 INSERT 跨两组，函数读取成员并由独立连接核对存储 | 仅关闭 batch input-only locator 子集；LOB 输出、OUT/INOUT、cursor、代理/OB20 和其他版本未验收，见D-08ac |
| MySQL 一至六层 INT ARRAY | 4.4.2.2 直连 sync/async × implicit/explicit Prepare **4/4**；String 参数、独立 text/binary 逐值读回、NULL/空叶数组/不等长子数组，见 D-12c1 | 二维顶层 `[]` 参数与 SQL 字面量均返回 `7602/22000`；七层 DDL `1235/0A000`；其余六种元素的单层验收见D-12c2，其他嵌套/版本和专用metadata未验收 |
| MySQL 其余单层 ARRAY 元素 | 4.4.2.2 直连sync/async × implicit/explicit Prepare **4/4**；TINYINT/SMALLINT/BIGINT/FLOAT/DOUBLE/VARCHAR(256)，NULL元素/空/SQL NULL、整数边界、Unicode/转义/控制字符、坏输入及独立text/binary存储读回，见D-12c2；已有INT/VECTOR短回归合计12/12 | 数组metadata仍为CHAR/String，输出为服务端表示；FLOAT数组将16777216格式化为16777200，独立binary ELEMENT_AT仍为16777216，不能承诺数组文本无损回绑；VARCHAR数组输出保留原始换行/NUL且不完整转义反斜杠，不能当JSON解析。其他嵌套/版本和专用metadata未验收 |
| REF CURSOR reader前向offset FETCH | Oracle4.4.2.2直连sync/async **12/12**，空/三行、Default/SequentialAccess/SingleRow、父command释放与连接复用；D-03b1 | 显式 reader 重载及父 REF CURSOR offset 默认值均已支持；父选项未启用时既有签名仍用普通 FETCH；真实legacy EOF，DEPRECATE_EOF仅本地验证；无滚动定位，父 command 的已声明 REF CURSOR 输出现可保存 offset 默认值，reader 显式 bool 可覆盖，页大小独立（D-03b2）；未声明 REF CURSOR 的输出计划与 array binding 仍拒绝，其他部署未验收 |
| MySQL 脚本标量 OUT/INOUT | 4.4.2.2 直连 sync/async **2/2**，普通/NULL INOUT、OUT、独立 binary 存储核对、reader 半读关闭、失败前缀/未执行后缀、batch 脚本屏障及后续更新，见 D-02c1 | 客户端变量编排；成功后统一发布不代表提交，后续错误保留已执行 SQL；Oracle 脚本输出、Prepare/cursor 和资源/复杂输出仍拒绝；ReturnValue 已按 D-02c2 验收，其他部署/版本未验收 |
| Oracle 命名复杂值 INTERVAL 成员 | 4.4.2.2 直连 sync/async **10/10**：OBJECT、OBJECT集合及标量INTERVAL集合的二进制输入/PL OUT/INOUT、NULL元素/空与typed NULL集合、精度，以及脚本文本输入后的独立存储核对，见 D-04f | 复用既有精确provider值与codec；命名客户端构造器输入已支持；命名OBJECT/嵌套OBJECT INTERVAL text结果另已通过当前直连三组精度及sync/async×buffered/sequential **24/24**，含独立binary对照（D-04f3），其余复杂text边界保持；匿名INTERVAL factory和ExecuteArray DML子集另见D-04f1，过程输出scale=-1且payload含255精度，仍安全拒绝；未外推跨版本/部署或流水化专门验收 |
| Oracle 命名BLOB成员locator字节 | 4.4.2.2 直连 sync/async **2/2** 对照确认：根locator有效，named成员把locator字节当作内容；显式SQL构造器配合根locator可用，见 D-04g。客户端 a4 已在 `oracle-direct` 以 BLOB/CLOB × OBJECT/collection/nested-collection × sync/async **12/12** 通过 constructor + hidden root LOB 自动绑定；显式 Prepare/PrepareAsync 与重复执行 **12/12** 通过并复用同一 statement；D-04i 又补齐单条全 Input、无输出 StoredProcedure 的显式 Prepare/PrepareAsync sync/async **2/2** | StoredProcedure 复杂 OBJECT locator InputOutput 在目标 4.4.2.2 直连 sync/async **2/2** 复核时，局部 OBJECT 回写 OUT bind 返回 `900/42000`，按服务端/协议边界不开发；独立标量输出见 D-04k，独立 complex/根 LOB/REF CURSOR 输出见 D-04l，mixed batch routine屏障的同类输入与独立标量/资源输出见 D-04m（36/36），batch input-only见 D-08ac；复杂 cursor 成员及 multi-statement locator 仍不在范围；StoredProcedure 仍是会话屏障，不承诺跨调用缓存；不能仅放开named writer，也不能据此认定所有其他替代协议或更复杂 collection 形状均支持 |
| 真实TLS/企业CA/mTLS/撤销/轮换 | **当前两种模式真实环境未开TLS，未验收** | 必须保留matrix.tls-authentication deferred；自签名SslStream只属本地证据 |
| 认证plugin/KDC/凭据服务 | native账号直连与本地客户端分支 | 非native正负认证、平台协商、实际轮换；Java插件注册不是OB部署证明 |
| OBProxy/ODP、真正多节点、OCP/TNS服务 | 本地parser与受控单上游客户端路由 | 真实发现服务、节点/代理故障、后端reset/迁移，单上游中继不是HA |
| 标准压缩 | 未声明CLIENT_COMPRESS时按Java回退成功 | 真正压缩帧、损坏/取消/代理/TLS组合 |
| OB20/trace | 两模式declined-ob20回退通过；显式ob20因Layer=mysql失败 | 真正OB20双向帧、legacy/modern trace控制/采样/collector、ODP；不能称已实库支持 |
| .NET资产/平台 | 五资产与Windows实际host所选公开包实库48/48历史记录 | 非Windows实库、完整API/ORM；netstandard经host加载不等于独立runtime |

## 当前服务端前提与开发冻结

本地 User Secrets 当前只有 `mysql-direct` 与 `oracle-direct` 两个直连 profile，目标为
OceanBase 4.4.2.2；两者均明确设置 `TLS Mode=Disabled`。没有 proxy/ODP、多节点/OCP/TNS、
OB20/trace 或非 native 认证/KDC profile。只读握手专项 4/4 通过，OB20 专项 2 项因没有
`ob20` profile 跳过；这证明当前配置状态，不证明服务端永远不支持这些能力。

因此下列项目暂时冻结开发与修复，直到提供对应的真实服务端端点、版本和正负验收数据：

| 项目 | 当前证据 | 开发决定 |
| --- | --- | --- |
| TLS/mTLS/企业 CA/撤销/轮换 | 两个真实 profile 均为 TLS Disabled，没有证书端点 | 暂不开发；保留 TLS API、负例和 Deferred 标记 |
| OB20/trace/压缩 OB20 | 没有 OB20 profile；历史强制 OB20 因服务端 `Layer=mysql` 失败 | 暂不开发；普通协议回退和拒绝边界继续保留 |
| OBProxy/ODP/多节点/OCP/TNS/后端迁移 | 没有真实 proxy 或发现服务 profile；本地 relay 只验证客户端路由 | 暂不开发；本地拓扑 fixture 不升级为 HA 结论 |
| 非 native 认证、KDC、凭据轮换 | 当前只有 native 账号直连，未提供插件/KDC 端点 | 暂不开发；Java SPI 存在不计为 OceanBase 部署能力 |
| 本轮新增字符集 | D-07e已补CP850/HP8/MacRoman及SWE7结果；D-07f/g/h已补CP932/UJIS/EUCJPMS严格映射与指定成功子集 | 仅按逐项证据关闭成功子集；Oracle普通SJIS/Big5/EUCKR/CP932/UJIS/EUCJPMS的Java匹配失败保留，其他部署继续冻结 |

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
