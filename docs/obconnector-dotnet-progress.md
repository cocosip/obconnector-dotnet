# OceanBase Connector for .NET 开发进度台账

基线日期：2026-10-17。依据：[架构设计](obconnector-dotnet-architecture-design.md)和[开发方案](obconnector-dotnet-development-plan.md)。2026-10-19 已续接 A 阶段握手/物理会话、B01 宿主接入、B02 无参文本命令/逐行 reader/多结果、B03/B04 名称绑定/服务端预处理、B05 低精度数值/provider-specific 访问/常见日期时间、B06 reader 字段元数据、B07 本地事务/关闭回滚、B08 本地取消/超时故障矩阵、B09 公共 API/标量 ProviderType 审核及 C02 多地址 TCP 建连、C03宽精度/纳秒/TZ/LTZ/零日期及 C04 报文增量/顺序字段流及服务端游标、C05数据库切换本地子项。C03/C04待验证，C05进行中，目录元数据尚未实现，真实互通无证据，不能将夹具通过等同于阶段门槛关闭。

## 进度口径

- 状态只用 `未开始`、`进行中`、`待验证`、`阻塞`、`完成`。`待验证` 表示代码或夹具已完成，但所属真实环境/目标框架验收尚未完成；不能计入完成数。
- 每个任务完成时填写证据：提交或 PR、`dotnet` 命令和结果、测试报告、实测 OceanBase/OBProxy 版本、模式与脱敏日志位置。外部链接可能失效时同时保存可复核的摘要。
- G1/G2/G3 仅在阶段所有任务为 `完成` 且真实租户矩阵通过时关闭；R02 还要求包消费和许可证/发布审查。失败、跳过与环境不可用分别记录，不折算成通过。
- 进度按已完成任务数及阶段门槛汇报；不使用未经工作量校准的百分比。相对周从 G0 环境和产品决定完成后开始，默认 1 名全职开发者，窗口是估算而非发布日期。
- 更新节奏：任务进入 `进行中` 前在末列写负责人；每周更新任务状态、实际完成时间、证据和阻塞；每个阶段关口再复核公开能力声明。末列的 `待记录` 表示负责人、证据和完成日均未填写。范围变化时同步修订架构、开发方案、任务总数和本台账。

## G0 实施准入

以下五项是排定 W1 和关闭 G1 的前提；G0 未关闭时可先开展 A01 工程骨架、A03 协议夹具，实际 W1 日期仍待定。产品决定详见架构设计 §9；负责人填角色或姓名，不能用“已口头确认”作为完成证据。

| ID | 准入项 | 当前状态 | 完成证据 | 负责人 |
| --- | --- | --- | --- | --- |
| G0-1 | 明确首发 OceanBase/OBProxy 最低版本及两模式组合 | 未开始 | 版本矩阵和租户信息（脱敏） | 产品/测试环境负责人待指定 |
| G0-2 | 准备独立 MySQL/Oracle 租户、直连与 OBProxy、可控故障网络 | 未开始 | 连接测试命令、环境说明和维护窗口 | 测试环境负责人待指定 |
| G0-3 | 明确 TLS 证书/私有 CA、首发认证插件和允许的明文配置 | 未开始 | 证书名称、信任链、插件及安全决策记录 | 产品/安全负责人待指定 |
| G0-4 | 决定 .NET Framework 4.8 是否正式支持 | 未开始 | 架构设计 §9 决策记录及消费矩阵更新 | 产品负责人待指定 |
| G0-5 | 决定首版是否包含 C 阶段及其中池化、高精度能力 | 未开始 | 架构设计 §9 决策记录；若改变 A-C 基线，同步改排期和发布门槛 | 产品负责人待指定 |

## 阶段门槛

| 门槛 | 计划窗口 | 当前进度 | 关口状态 | 关闭证据 |
| --- | --- | --- | --- | --- |
| G0 准入 | W0 | 0/5 | 未开始 | 五项决策/环境记录 |
| G1 协议基础 A | W1-W5 | 4/7 | 阻塞 | A01/A02/A03/A06 完成；A04 本地实现完成、待真实/跨平台验收；A05 还缺可信 MySQL 模式判定规则及版本/插件验收；A07 缺环境与可信版本矩阵；G0 未关闭 |
| G2 ADO.NET 核心 B | W6-W17（范围修订后待重估） | 0/9 | 进行中 | B01-B09 本地实现/审核已具证据；核心五资产有 Windows8/9/10基础运行时消费，但真实双模式、完整旧运行时消费/DI及跨平台未验收 |
| G3 生产连接 C | W18-W34 | 0/8 | 进行中 | C01缺可信完整重置白名单而阻塞；C02/C03本地实现完成待真实环境验证；C04顺序字段流及服务端游标本地完成、真实资源/性能与版本矩阵待验证；关口未关闭 |
| G4 发布候选 R | W35-W37 | 0/2 | 未开始 | R01-R02 + 包/许可证/兼容矩阵 |
| D 独立扩展 | G4 后按需求 | 0/15 | 未开始 | 每项独立功能、版本和模式证据；不计入 G4 |

## A. 协议基础任务

| ID | 计划窗口 | 可验收产物 | 状态 | 负责人/完成证据/实际完成日 |
| --- | --- | --- | --- | --- |
| A01 | W1 | 四目标解决方案、单测/集成/消费入口 | 完成 | 2026-10-18；提交 `✨ feat(obconnector-dotnet): scaffold A01 multi-target solution, test entries and CI`；`dotnet build OceanBaseConnector.sln -c Release` 四目标 0 警告 0 错误；UnitTests 11×3 TFM 通过、ConsumerTests 3×3 TFM 通过（含 MetadataReader 验证 netstandard2.0 资产无现代 API 泄漏）、IntegrationTests 1×3 TFM 按环境门槛跳过、`dotnet pack` 成功；CI 入口 `.github/workflows/ci.yml`（Windows/Linux 矩阵，GitHub 上首次运行待推送后验证）；夹具格式见 `tests/fixtures/README.md` + 样例 `err-packet-handshake-failure.json` |
| A02 | W1-W2 | 连接串 builder、不可变选项、脱敏 | 完成 | 2026-10-18；`OceanBaseConnectionStringBuilder` + internal `OceanBaseConnectionOptions`（`Build()` 冻结快照，测试证实后续 builder 变更不影响快照）。公开键：`Server`(同义词 `Host`)、`Port`(1-65535，默认 2881)、`User ID`(`Uid`)、`Password`(`Pwd`)、`Database`、`Connection Timeout`(默认 15)、`Command Timeout`(默认 30)、`TLS Mode`(`VerifyFull` 默认/`Required`/`Disabled`，仅名称匹配、拒绝数字串)、`TLS Server Name`、`Persist Security Info`(默认 false)、`ExpectedMode`(`MySQL`/`Oracle`)、`Pooling`(默认 false)；未知键、同义词冲突（`Server`+`Host` 等）、非法端口/超时/布尔/枚举均抛 `ArgumentException`；`KEY=` 空值遵循 ADO.NET 语义移除键、`Build()` 校验必填 `Server`/`User ID`；密码可经 `ToSanitizedConnectionString(true)` 序列化、默认脱敏，异常消息经测试不含密码/冲突值。单测 62×3 TFM 通过（`ConnectionStringBuilderTests`），四目标 Release 构建 0 警告 0 错误 |
| A03 | W1-W3 | 标准帧/序号/分片/越界夹具 | 完成 | 2026-10-18；`Internal/Protocol/{ByteCursor,PacketChannel,ProtocolException}`。帧规则：3 字节长度 + 1 字节序号；负载 ≥0xFFFFFF 分片、整倍数以空帧终止；`BeginCommand()` 命令边界双序号归零；序号按帧递增、模 256 回绕（257 帧用例验证）；单帧与续帧链均在分配前执行逻辑包上限校验（默认 64MB，可注入更小值验证拒绝）；`ByteCursor` 全部读取经无符号比较防负数/溢出绕过。夹具：`framed-err-packet.json`（帧化 ERR：长度 72、序号 0、错误码 1045、SQLSTATE 28000）由加载器消费并用游标解析断言。测试 98×3 TFM 通过：单包/多分片/空终止帧/错序/截断（含 5 种随机种子×32 截断点）/上限拒绝；`SpyStream` 断言异步读写路径同步调用计数为 0。同步与异步共享 `TakeHeader`/`PrepareHeader`/上限校验，命令解析未混入帧层 |
| A04 | W2-W4 | TCP、SSLRequest、TLS 验证与拒绝场景 | 待验证 | **本地实现/Windows 环回已完成，真实与跨平台验收未完成。** 2026-10-18；`Internal/Transport/{TcpTransport,TlsUpgrade,TlsHandshakeFlow}` + `Internal/Protocol/{CapabilityFlags,SslRequestPacket}` + `PacketChannel 共享双向序号/ReplaceUnderlyingStream`。TCP 同步（BeginConnect+WaitOne 超时）与异步（现代目标 ct 重载 / 两个 Standard 目标关闭 socket 中止）互不包装；拒绝/超时/取消均释放 socket，异常仅含端点信息。SSLRequest 固定 32 字节（能力 LE32 + 16MB + utf8mb4(45) + 23 零填充），字节级断言。TLS：默认 VerifyFull 走 OS 信任链+主机名校验（SslStream 无自定义回调）；Required 显式接受任意证书；Disabled 不发 SSLRequest 不升级；失败即销毁传输，无降级路径。环回测试（运行时自签证书）覆盖严格顺序（初始握手→CLIENT_SSL 检查→SSLRequest 序号 1→TLS→零认证字节）、VerifyFull 拒绝不受信证书（服务端 TLS 流解密后 0 字节）、服务端无 CLIENT_SSL 时不发任何字节、Disabled 零字节、同步/异步路径一致；`TLS Server Name` 优先于端点主机名（IP+显式 DNS 名称用例）。私有 CA/跨平台/真实主机名不匹配留待 A07 实测记录。测试 113×3 TFM 通过 |
| A05 | W3-W4 | 认证、模式位、能力协商与夹具 | 待验证 | **本地认证/Oracle 模式/能力实现及合成验证已完成；可信 MySQL 判定、版本/插件矩阵和真实验收未完成。** Codex，2026-10-18：续接 GLM 原型，修复双向序号、session-track 嵌套长度、切换种子 NUL、无插件 native 回退、TLS 能力/字符集/最大包一致性、长属性 lenenc、错误回显脱敏、长度整数边界；合成夹具及环回通过。无可信版本矩阵时拒绝未设置 Oracle 位的连接，MySQL 真实连接暂未开放；插件与版本白名单仍待 G0/G1 |
| A06 | W3-W5 | 物理会话状态、独占和异常映射 | 完成 | Codex，2026-10-18：PhysicalSession 的 Closed/Connecting/Open/Busy/Broken、独占 lease、未确认响应销毁、同步/异步打开关闭、全过程连接预算与取消、幂等释放、安全异常；环回测试验证截断、关闭不复活、超时、打开后 caller token 取消不投毒。B 阶段再扩展尚不存在的 statement/reader 资源 |
| A07 | W5 | 双模式直连/OBProxy 真实连接门槛 | 阻塞 | Codex，2026-10-18：替换永久失败占位符；四项真实测试覆盖四种连接组合、同步/异步打开关闭、模式不符、TLS 名称错误、未知插件。OB_* 环境未配置，日常运行跳过；强制关口缺环境实际失败。还需租户/TLS/插件/确切版本与可信 MySQL 模式判定；G1 不关闭 |

## B. ADO.NET 核心任务

| ID | 计划窗口 | 可验收产物 | 状态 | 负责人/完成证据/实际完成日 |
| --- | --- | --- | --- | --- |
| B01 | W6-W7（待重估） | 标准 provider/factory、现代无池数据源、可选 DI 与基础日志；`ChangeDatabase` 暂拒绝 | 待验证 | 本地实现、五/三目标编译、环回建连、DI/消费测试及双包依赖检查通过；双模式真实建连、旧运行时和非 Windows 验收待补 |
| B02 | W7-W9 | 文本 SQL、OK/ERR、多结果和更新计数 | 待验证 | COM_QUERY 同步/异步、逐行文本 reader、OK/ERR/EOF/FE OK、多结果和更新计数已有 Windows 环回证据；两模式真实 CRUD/匹配行语义、非 Windows 和旧运行时待验证 |
| B03 | W8-W10 | Oracle 命名占位符词法器及各类 `q` 字面量；MySQL 参数数由服务端确定 | 待验证 | 本地完成词法扫描、名称/顺序映射与发送前绑定校验；基础参数执行由 B04 接入，真实参数化集成测试暂缓 |
| B04 | W9-W12 | 服务端 prepare/execute、二进制结果和 statement ID | 待验证 | 本地实现及 Windows 协议/环回、五目标编译、消费/DI 测试通过；双模式实库、旧运行时和非 Windows 待补 |
| B05 | W10-W13 | 基础值、低精度 `OceanBaseDecimal`、Oracle DATE 与未支持类型拒绝 | 待验证 | Codex，2026-10-18：本地精确数值/日期编解码、公开 provider-specific 访问、五目标消费及 Windows 协议/环回通过；双模式实库、旧运行时及非 Windows 待补，完整字段元数据留 B06 |
| B06 | W11-W14 | 逐行 reader、字段元数据及低精度 provider-specific 访问 | 待验证 | Codex，2026-10-18：两套字段 schema/独立快照、多结果和生命周期、本地五目标编译及 Windows 单元/消费/DI 均通过；独立走查无需修复项。双模式真实元数据、非 Windows 和旧运行时待补，ProviderType 契约留 B09 审核 |
| B07 | W13-W15 | 本地事务及环境事务拒绝 | 待验证 | Codex，2026-10-18：本地显式绑定、逐模式隔离映射、同步/异步事务与关闭回滚、日志和未知结果已实现；Unit455/Consumer34/DI8、五目标编译及独立复查通过；实库、非 Windows 和旧运行时待补 |
| B08 | W14-W16 | 取消、超时、结果未知和故障注入 | 待验证 | Codex，2026-10-19：本地故障矩阵已完成，新增43项单元/5项五资产消费测试；修复内部预算超时误判为调用方取消及 Canceled 日志，终态竞争/部分发送/旧命令重开隔离通过；Unit498/Consumer39/DI8、五目标核心/三目标 DI build、格式/pack/依赖分组校验通过，详见本页末；实库/非 Windows/旧运行时待验证 |
| B09 | W17（待重估） | 五目标消费、数据源/DI/日志生命周期、低精度 provider-specific 数值和双模式 G2 验收 | 待验证 | Codex，2026-10-19：本地公共 API 审核、标量 ProviderType/参数映射和 Command 类别修复完成；Unit567/Consumer39/DI8、五/三目标build零警告错误、格式/pack/依赖组通过，Windows核心五资产在8/9/10运行时基础消费通过；详见架构§5和本页末，实库/非Windows/完整旧运行时仍待验收 |

## C. 生产连接任务

| ID | 计划窗口 | 可验收产物 | 状态 | 负责人/完成证据/实际完成日 |
| --- | --- | --- | --- | --- |
| C01 | W18-W23 | 可选池、完整重置和版本白名单 | 阻塞 | 2026-10-19 Codex；无真实完整重置白名单，Java reset不能代替OceanBase实测；保持Pooling=true拒绝，先推进独立C02 |
| C02 | W18-W21 | 多地址建连与发包后不重试 | 待验证 | 2026-10-19 Codex；Server列表/共享预算/仅TCP建连故障切换/逐端点TLS及认证；Windows Unit627、Consumer49、DI8及五资产8/9/10独立运行时消费通过；实库/非Windows未验收，见末尾记录 |
| C03 | W20-W25 | 宽精度、纳秒、时区和整列稳定的零日期 provider-specific 值 | 待验证 | 2026-10-19 Codex；数值、TIMESTAMP(0..9)、TZ/LTZ及零日期本地实现齐备；Unit895/Consumer91/DI8、五/三目标编译/格式/打包及独立走查通过；真实验收/A05公开MySQL握手仍缺，详见本页末 |
| C04 | W23-W28 | SequentialAccess、大值与服务端游标 | 待验证 | 2026-10-19 Codex；增量报文、顺序字段流和FetchSize只读游标分页本地完成；Unit1037/Consumer111/DI8及核心五/DI三目标编译通过，独立复查缺陷均已修复；真实两模式/OBProxy大值/游标资源及性能矩阵未验收，详见本页末 |
| C05 | W25-W29 | `GetSchema` 基础集合及异步入口、MySQL `ChangeDatabase` | 进行中 | 2026-10-19 Codex；数据库切换本地完成，Unit1073/Consumer120/DI8、核心五/DI三编译及独立走查通过；四个目录集合/限制/异步仍待实现，真实验收暂缓 |
| C06 | W27-W31 | 逐条 `DbBatch`、factory 和已有数据源的批量入口 | 未开始 | 待记录 |
| C07 | W29-W32 | 本地 ActivitySource/Meter、脱敏和性能基线 | 未开始 | 待记录 |
| C08 | W33-W34 | 生产版本/模式矩阵与 G3 审核 | 未开始 | 待记录 |

## 发布与可选扩展

| ID | 计划窗口 | 可验收产物 | 状态 | 负责人/完成证据/实际完成日 |
| --- | --- | --- | --- | --- |
| R01 | W35-W36 | NuGet 包、许可/包名审查、五目标消费 | 未开始 | 待记录 |
| R02 | W36-W37 | 完整回归、发布说明与兼容矩阵 | 未开始 | 待记录 |
| D01 | G4 后 | OB20、校验与完整链路追踪 | 未开始 | 待记录 |
| D02 | G4 后 | Oracle 过程及 OUT/RETURNING | 未开始 | 待记录 |
| D03 | G4 后 | REF CURSOR | 未开始 | 待记录 |
| D04 | G4 后 | LOB locator 与流式 LOB | 未开始 | 待记录 |
| D05 | G4 后 | ARRAY/STRUCT 与类型字典 | 未开始 | 待记录 |
| D06 | G4 后 | bulk/array binding | 未开始 | 待记录 |
| D07 | G4 后 | 按需客户端预处理回退 | 未开始 | 待记录 |
| D08 | G4 后 | 拓扑发现与负载均衡 | 未开始 | 待记录 |
| D09 | G4 后 | 显式 XA | 未开始 | 待记录 |
| D10 | G4 后 | Oracle NCHAR/national charset | 未开始 | 待记录 |
| D11 | G4 后 | Oracle INTERVAL | 未开始 | 待记录 |
| D12 | G4 后 | MySQL JSON | 未开始 | 待记录 |
| D13 | G4 后 | MySQL BIT | 未开始 | 待记录 |
| D14 | G4 后 | MySQL YEAR | 未开始 | 待记录 |
| D15 | G4 后 | 新认证插件 | 未开始 | 待记录 |

## 兼容矩阵记录模板

每行都填写服务端与 OBProxy 的**具体版本**、TLS/认证方式、执行的测试套件和结果。下列四行只是必需组合的空白基线，不表示兼容。

| 模式 | 路径 | OceanBase 版本 | OBProxy 版本 | TLS/认证 | A/B/C 结果 | 证据 |
| --- | --- | --- | --- | --- | --- | --- |
| MySQL | 直连 | 待测 | 不适用 | 待测 | 未验证/未验证/未验证 | 待记录 |
| Oracle | 直连 | 待测 | 不适用 | 待测 | 未验证/未验证/未验证 | 待记录 |
| MySQL | OBProxy | 待测 | 待测 | 待测 | 未验证/未验证/未验证 | 待记录 |
| Oracle | OBProxy | 待测 | 待测 | 待测 | 未验证/未验证/未验证 | 待记录 |

## 风险与变更记录

| ID | 风险/决定 | 影响 | 当前处理 | 关闭证据 |
| --- | --- | --- | --- | --- |
| K01 | 双模式真实环境和版本尚未确定 | G0 未关闭，所有互通结论不可发布 | 取得版本和可重复测试入口 | 待记录 |
| K02 | TLS 默认 VerifyFull 需要可验证的证书 | 无证书环境不能以默认设置连接 | G0-3 确认证书/信任根；不得静默降级 | 待记录 |
| K03 | 服务端完整重置能力随版本/模式变化 | 池化可能泄漏会话状态 | C01/C08 建立白名单；其余组合只直连 | 待记录 |
| K04 | Java 默认客户端预处理，.NET B 只服务端预处理 | 部分 Java 可执行的参数化 SQL 在 .NET B 失败 | B04 显式失败并记录差距；D07 按需求处理 | 待记录 |
| K05 | `netstandard2.0` 可消费范围大于已测试运行时 | 旧运行时行为可能不同 | B09/R01 记录实际消费运行时；.NET Framework 4.8 是否正式支持由 G0-4 决定 | 待记录 |
| K06 | 首版 A-C 的范围尚未由产品确认 | 若 G0-5 改为 B 阶段发布，G3/G4 排期和对外能力需重设 | G0-5 决定后重定基线，不把未实现的 C 能力写进发布说明 | 待记录 |

## 进度文档走查记录

2026-10-18 .NET 接入核查：按用户要求，将数据源/DI/日志设计合并到现有架构设计 §2.1–§2.7，没有独立设计文档。主依据仍为 Java `obconnector-j` 的统一连接、协议和会话模式；Npgsql 10 参考宿主生命周期，`oceanbase-dotnet` 只作有限 DI/日志用法参考。现代 builder/数据源、可选 DI 集成包及标准日志前移 B01，C06 保留 batch，C07 保留本地追踪/指标。核心与集成包采用 CPM、同一根 Version、统一发布；全部测试宿主仍为 .NET 10。现有协议/TLS/模式快照无需重写，后续增加数据源与日志上下文；本次未改产品代码、项目文件或依赖，B/C 状态不变。任务 ID 和数量保持不变，B01/G2 范围扩大，周窗口须重估；A07/G0/G1 的真实环境阻塞不变。

2026-10-18 续接：CPM 与全局 Version 已实现，本次采用 slnx、global.json、统一 .NET 10 测试/消费宿主，产品保留四目标。A04 原标完成，但私有 CA、真实主机名及跨平台验收无证据，修正为待验证。独立审查发现的 native 回退、同步取消、长属性编码、打开成功/取消竞争已修复；缺模式位安全拒绝，不伪造版本白名单。详见 [续接记录](implementation-stage-a.md)。

2026-10-18：补记工程约定——仓库统一使用中央包管理(CPM),`Directory.Packages.props` 为全部 NuGet 包版本唯一来源并开启传递依赖钉扎,工程 `PackageReference` 不带 `Version`;产品及未来所有程序集的包版本统一由根 `Directory.Build.props` 的 `Version` 属性提供,csproj 不得各自声明版本。该约定此前仅口头约定未落文档,现已写入架构设计 §2 与开发方案"全局约束与准入"及交付目录,并完成存量工程改造(A01 三个测试工程与产品工程版本属性上收)与回归验证(`dotnet pack` 仍产出 0.1.0-alpha)。

2026-10-17：核对开发方案与本台账的任务 ID、阶段计数、相对周窗口、Java M01-M16 映射、文档链接和状态口径。D 阶段按独立扩展拆分，G0 中互不依赖的产品决定拆开；当前基线为 G0 5 项、A 7 项、B 9 项、C 8 项、R 2 项、D 15 项，代码任务合计 41 项，全部 `未开始`。G0 尚未关闭，W1 的实际起始日期未定；没有 .NET 编译或 OceanBase 互通证据。下一次走查在 G0 关闭及每个阶段门槛前进行，逐条核对证据而非只检查状态栏。

2026-10-17：修正开发前走查的四项文档问题：低精度 `OceanBaseDecimal` provider-specific 契约纳入 B05/B06 和 G2，宽精度仍在 C03；C03 固定 MySQL 日期列正常值/零值的 provider-specific 类型；B03 补非成对 `q` 分隔符与未闭合字面量；`ChangeDatabase` 在 B 阶段拒绝，C05 验证 MySQL 模式同步/异步切换，Oracle 模式继续拒绝。G0 未关闭不妨碍 A01/A03 的独立准备，但 W1 排期及 G1 关口仍待 G0 完成。任务数量和状态未变。

## 周报更新格式

每周在本节追加一条，不覆盖旧记录：

```text
日期：YYYY-MM-DD
本周完成：任务 ID；提交/PR；测试命令与结果；实测模式/版本
进行中与下周目标：任务 ID；预计关闭门槛
阻塞：任务 ID；原因；负责人；需要的动作和预计解除时间
范围/设计变化：关联的架构或开发方案章节；无变化写“无”
```

2026-10-17 基线：仅建立架构设计、开发方案和进度台账；G0 未关闭，A/B/C/D/R 均未开始。尚无可报告的 .NET 编译、测试或 OceanBase 互通证据。

2026-10-18 续接验证：四目标 Release build 0 警告/0 错误；slnx 全套运行 UnitTests 158 通过、ConsumerTests 5 通过、IntegrationTests 4 因环境跳过。TRX 记录在本地 TestResults/。强制 OpenClose 关口在缺四种连接串时退出 1（预期门槛失败）。format whitespace、diff 检查、pack、中央版本/包四目标资产断言通过；独立代码审查及修复后复核未发现新重要问题。产品/包版本 0.1.0-alpha，没有发布、远端 CI 或真实数据库互通结论。下一步先补 G0 和可信 MySQL 模式版本证据，再执行 A07；B 阶段仍未开始。

2026-10-18 B01 本地实施与验证：已实现一个公开连接/工厂、现代无池数据源、可选普通/key DI 注册和数据源级连接日志；command/parameter 为标准对象入口，SQL、reader、事务执行仍显式报不支持，不宣称可执行 SQL。核心和 DI 包同取根 Version=0.1.0-alpha，集成包精确依赖核心 [0.1.0-alpha]。Microsoft.Extensions 通用依赖按 TFM 在 CPM 中选择：netstandard2.0/net8.0 使用 Logging 8.0.3、DI 抽象 8.0.2，net9.0 使用 9.0.10，net10.0 使用 10.0.0；全部测试宿主仍为 net10.0。逐目标依赖回归先失败 5/7，修复后检查 7 个 restore 图和 PE 程序集引用通过；ZIP/XML 检查两个包的 4/3 目标资产、依赖组和统一版本通过。

B01 验证命令：dotnet restore OceanBaseConnector.slnx；dotnet build OceanBaseConnector.slnx -c Release --no-restore（0 警告/0 错误）；分别对 UnitTests、ConsumerTests、DependencyInjection.Tests 执行 dotnet test -c Release --no-build --no-restore，184/13/8 全通过，TRX 位于 TestResults/b01-unit.trx、b01-consumer.trx、b01-di.trx。dotnet format OceanBaseConnector.slnx whitespace --verify-no-changes --no-restore 与 git diff --check 通过；两产品工程 dotnet pack -c Release --no-build --no-restore 成功。正式包 README/许可等元数据仍在 R01，当前 pack 保留缺 README 提示。

B01 独立审查及修复：日志回调在 Opened 关闭、在 Closed 重开同一连接的状态事件乱序均由先失败回归复现，改为先发送状态事件再调用外部日志；另覆盖连接并发、日志工厂/过滤/写入抛错、实际 DI 日志覆盖、取消与清理、PersistSecurityInfo、配置冻结和参数归属。复核未发现剩余重要问题。依用户要求未运行真实数据库和非 Windows 测试，未取得旧运行时运行证据；无可信 MySQL 模式版本时继续安全拒绝缺失模式位。B01 保持待验证、G2 保持进行中，下一功能模块为 B02，不将本地通过等同于双模式兼容或可发布。

2026-10-18 B02 本地实施：复用一个 PhysicalSession/CommandLease 和 PacketChannel，增加 COM_QUERY 同步/异步执行、逐行文本 reader、NULL/空字符串、基础标量、结果切换和 OK 更新计数累加；ExecuteScalar 跳过前置更新计数取得首个结果集单元，再消费剩余响应。传统 EOF 与协商后的 DEPRECATE_EOF（无列 EOF、FE OK 行终止）均覆盖。关闭未读 reader 销毁会话，CloseConnection 另关闭逻辑连接；服务器终态 ERR 可安全复用会话，失败 reader 不能再访问下一命令。LOCAL INFILE、不合法列/行/长度/包数和超限元数据安全拒绝，不触及文件系统、不重放 SQL。B05/B06 之前的精确数值、日期、完整元数据等边界保持显式不支持；特别是 Oracle 类型码 3 不误作 MySQL INT。

B02 本地验证：dotnet build OceanBaseConnector.slnx -c Release --no-restore 通过，0 警告/0 错误；各 net10.0 宿主 dotnet test -c Release --no-build --no-restore：UnitTests 228、ConsumerTests 13、DependencyInjection.Tests 8 全通过。TRX 位于 TestResults/b02-unit.trx、b02-consumer.trx、b02-di.trx。B02 新增 44 项测试用例，包括公共环回执行和模式感知类型边界；红绿回归覆盖超大超时遗留 Busy、缓冲行忽略 Cancel、失败 reader 触碰下一命令、读取 token 原因、终态 OK/ERR 被日志回调取消覆盖、EOF 协商不匹配和 Scalar 前置更新计数。发包后的取消/超时/关闭返回 IsExecutionOutcomeUnknown=true 并保留原因；无发包的预取消/不支持功能/环境事务拒绝仍可继续建命令。

B02 format whitespace、git diff --check、两个产品包 pack 通过；包仍为统一 0.1.0-alpha，正式 README/发布元数据由 R01 补齐。独立审查发现的 EOF 协商、超大超时占用和终态 ERR/取消竞态均已修复并回归。此次没有运行数据库或非 Windows 测试；MySQL 可信版本/两模式真实 CRUD/真实不变 UPDATE 行数及旧运行时证据仍缺失，B02 保持待验证、G2 保持进行中。下一功能模块为 B03 参数占位符词法与验证。


2026-10-18 目标框架补充：按用户要求，在核心单包中增加 netstandard2.1；现为 netstandard2.0/netstandard2.1/net8.0/net9.0/net10.0 五目标，DI 包仍为 net8.0/net9.0/net10.0，全部测试及消费宿主仍为 net10.0。两个 Standard 目标与 net8.0 同用 Microsoft.Extensions 8.x 基线，net9.0/10.0 各用对应主版本；CPM、根 Version 和两包统一发布规则不变。同步修订现有架构设计、开发计划和本台账，原四目标记录保留为历史证据。

netstandard2.1 验证：消费回归先失败 4 项，确认缺少资产及 restore 图；补目标、CPM 条件和平台 API 判断后通过。TCP 两个 Standard 目标都使用关闭 socket 的取消路径，SqlState 只在现代目标 override；2.1 TLS 采用平台已有的令牌重载。Release slnx 构建 0 警告/0 错误；UnitTests 228、ConsumerTests 18、DependencyInjection.Tests 8 全通过。消费测试检查两个 Standard 资产的实际目标框架标记、独立加载、现代类型隔离、日志依赖、工厂对象，以及握手等待取消后的关闭状态。逐目标检查 8 个 restore 图及程序集引用，ZIP/XML 检查两包 5/3 个资产、8 个 nuspec 依赖组、统一 0.1.0-alpha 和精确核心依赖通过；format whitespace 通过。真实数据库、非 Windows 和旧运行时消费测试依用户要求暂缓，B01/B02 与 G2 的待验证门槛不变；pack 仍提示缺包 README，由 R01 处理。

netstandard2.1 独立只读审查：核对已安装的 .NET Standard 2.1 参考 API、条件编译、CPM 与五/三目标边界、消费测试和文档，未发现需修复问题；git diff --check 通过。


2026-10-18 B03 本地实施：增加 Oracle 命名绑定单次词法扫描及位置映射，支持单/双引号双写、成对/非成对 q 与 nq 字面量、非嵌套块注释、CR/LF 行注释及 :=；重复名称按 SQL 顺序引用同一参数对象，不插入 Value。ParameterName 可带单个冒号，ASCII 名称按 OrdinalIgnoreCase 匹配；规范化重名、缺失/多余、未闭合字面量和不支持绑定语法在发包前失败。MySQL SQL 与参数集合顺序原样保留，不猜测 SQL_MODE 或本地数问号。非 Input 参数方向当前拒绝。

B03 命令入口使用已认证会话模式，在获取命令租约及发送之前校验，空参数集合也检测未绑定 :name。关闭连接不能靠 ExpectedMode 确定模式；原脚手架的关闭连接和无名称参数异常预期已改为具体校验错误。合法参数执行及 Prepare 仍由 B04 接入，本模块不宣称已有参数化执行能力。普通无参文本仍走 B02。

B03 验证：词法/映射初始 52 例中 40 例红，公共同步/异步两例红确认缺失绑定 SQL 曾到达对端；实现后通过。另有注释内再次出现 /* 的红绿回归，确认按首个 */ 结束。最终 dotnet build OceanBaseConnector.slnx -c Release --no-restore 五目标核心及三目标 DI 编译 0 警告/0 错误；UnitTests 282、ConsumerTests 18、DependencyInjection.Tests 8 全通过，TRX 位于 TestResults/b03-unit.trx、b03-consumer.trx、b03-di.trx。format whitespace、git diff --check、两个产品包 pack 通过；包版本仍统一为 0.1.0-alpha，README 提示留 R01。独立只读走查未发现文档范围内需修复问题。依用户要求未执行数据库、非 Windows 或旧运行时测试；B03 保持待验证，G2 保持进行中，下一模块为 B04 服务端预处理与语句所有权。

2026-10-18 B04 本地实施：实现 COM_STMT_PREPARE/EXECUTE/CLOSE、基础二进制参数/结果、多结果和命令/物理会话内的 statement ID 所有权。Oracle 使用 B03 名称映射顺序，MySQL 保留 SQL 并由服务端参数数校验。Prepare 强制准备，无客户端插值或 SQL 重放。自动准备和执行使用同一租约/超时；准备中断不报告未知 SQL 执行，实际发送执行后的不完整响应则销毁会话并报告结果未知。改变 SQL、重连、命令释放均使旧 ID 失效；其他 reader 忙时延迟关闭到下一命令前。

B04 修复与验证：初始六个公开协议用例红后转绿；服务端参数数不符曾误包装为数据库异常，修复后保留 ArgumentException 并关闭 ID、复用会话。准备元数据中途 ERR 的已知 ID 泄漏由红绿回归修复。独立审查发现旧清理超时可能关闭后续命令，改为只取消所属租约并增加跨租约回归。netstandard2.1 参考 API 已包含 PrepareAsync/DisposeAsync，补齐真实异步覆写；隔离资产入口回归先红后绿，环回进一步验证该资产的准备等待、执行及异步释放。

B04 最终 Windows 验证：dotnet build OceanBaseConnector.slnx -c Release --no-restore 核心五目标/DI 三目标 0 警告、0 错误；UnitTests 327、ConsumerTests 19、DependencyInjection.Tests 8 全通过，TRX 位于 TestResults/b04-unit.trx、b04-consumer.trx、b04-di.trx。覆盖有/无符号边界、IEEE 浮点、UTF-8、NULL 位图跨字节、长度编码边界、类型变化、同步/异步执行、重复绑定、多结果、准备/执行错误和取消、畸形二进制行、未读响应关闭、重连和忙会话清理。format whitespace --verify-no-changes、git diff --check 和双包 pack 通过；ZIP/XML 验证两包 5/3 个目标资产、8 个依赖组、统一 0.1.0-alpha 及集成包精确核心依赖，README 提示仍留 R01。框架/依赖策略仍为核心 netstandard2.0/netstandard2.1/net8.0/net9.0/net10.0，DI net8.0/net9.0/net10.0；Standard 与 net8 使用 Microsoft.Extensions 8.x，net9/10 使用对应主版本，CPM 和统一根版本不变。

B04 只支持基础 bool/整数/浮点/普通 UTF-8 字符串/byte[]/NULL；精确数值、日期和 Oracle NUMBER 仍属 B05，完整字段元数据属 B06，事务属 B07。没有可信 MySQL 模式版本证据时仍拒绝缺 Oracle 模式位的握手，不为环回成功绕过安全门槛。依用户要求不执行实库、非 Windows 或旧运行时验收；net10.0 宿主加载 Standard 资产不能替代旧运行时证据。B04 保持待验证、G2 保持进行中，下一模块为 B05。

B04 独立复审：源码/测试未发现剩余需修复问题；文档复核后明确显式 Prepare 不校验 Value 编码、执行入口才在自身发包前校验，并补充 netstandard2.1 的命令异步入口范围。

2026-10-18 A04/A05 进度校准：此前台账已列本地证据，但开发计划中的本地完成项漏勾，现按代码和测试补齐，真实环境验收仍独立保留未勾。A04 本地实现完成，A05 本地安全认证/Oracle 模式/能力实现完成但 MySQL 允许规则仍需可信版本证据。重新运行 Windows Transport/Handshake 筛选套件 54 项全部通过（TestResults/a04-a05-refresh.trx）；没有执行实库、可信私有 CA 成功链或非 Windows 测试，不把待验证改为整体完成，G1 仍阻塞。

2026-10-18 B05 本地实施：统一模式感知的精确数值、日期时间和 RAW 编解码；新增公共 OceanBaseDecimal（BigInteger 系数/scale、精确 ToDecimal）、可选 OceanBaseDbType 覆盖，以及低精度列的 GetProviderSpecificFieldType/Value/Values 和 GetFieldValue<OceanBaseDecimal>。Oracle 列定义读取独立 precision/有符号 scale；类型码 3 按优化整数布局、246 按长度编码十进制文本解析，不混用 MySQL INT。低精度列的类型随元数据固定，宽精度/未知精度即使值很小或 NULL 也拒绝。Oracle DATE 保留时分秒，TIMESTAMP 202 两协议读取原始 12 字节；MySQL DATE/DATETIME/TIMESTAMP 和有符号 TIME 采用各自线格式，禁止隐式舍入、日期截断或 Kind 转换。协议依据核对 Java 基线 6adc311，具体边界见架构 §5。

B05 回归与审查：初始缺失精确数值/时间用例及公共同步/异步入口回归由红转绿；原 B03/B04 的 decimal/Oracle NUMBER 未支持预期更新为新契约，其他延期类型继续拒绝。独立只读审查发现仅设 Precision 时误用 Scale=0，以及二进制 TIME 天数溢出绕过；分别增加省略/显式 scale 区分、推断 scale 超精度及 8/12 字节天数 35/10,675,200/uint.MaxValue 回归，先红后绿。修复后源码复审未发现剩余需修复问题，文档复审发现的两处旧状态声明已同步修正。

B05 最终 Windows 验证：dotnet build OceanBaseConnector.slnx -c Release --no-restore 核心五目标/DI 三目标编译 0 警告、0 错误；UnitTests 369、ConsumerTests 24、DependencyInjection.Tests 8 全通过，TRX 位于 TestResults/b05-unit.trx、b05-consumer.trx、b05-di.trx。消费测试逐个隔离加载五目标资产，校验 OceanBaseDecimal 构造/转换/类型身份、参数推断和覆盖重置；宿主统一 net10.0。format whitespace --verify-no-changes、git diff --check 和双包 pack 通过；ZIP/XML 校验 5/3 个资产、8 个依赖组、统一 0.1.0-alpha、集成包精确核心依赖及 Microsoft.Extensions 按 Standard/net8→8.x、net9→9.x、net10→10.x 分组。包 README 提示仍留 R01。

B05 保持待验证、G2 保持进行中，下一模块为 B06。依用户要求未执行实库、非 Windows 或旧运行时验收；真实 Oracle 空字符串/NULL 语义、TIMESTAMP scale 变体和两模式往返仍无证据。可信 MySQL 模式版本矩阵仍未建立，缺 Oracle 模式位时继续安全拒绝，不为合成测试放行。宽精度/负 scale、纳秒/时区、零日期留 C03，完整字段元数据留 B06，不以本地测试关闭 G1/G2。

2026-10-18 B06 本地实施：保留列定义的数据库名、别名/原始表列名、长度和 flags；新增内部 DbColumn 映射，统一提供 GetSchemaTable 与 IDbColumnSchemaGenerator.GetColumnSchema。五目标接口均已核对可用的 .NET Standard 2.0 参考 API，无新依赖。两套 schema 返回独立快照，标准/provider-specific 类型沿用 B05；MySQL 字符长度按已支持字符集换算、Oracle 普通字符长度保留服务器单位、大长度先换算再饱和。零行 SELECT 保留 schema，最终 NextResult(false) 清空旧列。未提供的 key/unique/updateability/表达式等不猜测；ProviderType 暂未知，B09 需补齐枚举覆盖契约，见架构 §5。

B06 测试与走查：公开文本/二进制、同步/异步四例先因 schema 缺失失败后通过；字符长度/uint 上限 19 例中 7 例先红，换算实现后全部通过。另覆盖整数精度/符号、中文来源名称、无效 UTF-8、NULL/空串、多结果/更新计数、快照不可变、零行 SELECT、提前关闭/读尽的 CloseConnection、未读响应独占及 scalar 转换失败销毁。关闭未读 TCP 可能产生 FIN 或 RST，环回断言限定为 ProtocolException/IOException 两类关闭结果。五目标隔离消费经标准 ADO.NET 接口读取实际 schema/值及 provider 类型。独立源码、关联生命周期和最终文档走查无需修复项，已补走查建议的零行 schema 回归。

B06 最终 Windows 验证：dotnet build OceanBaseConnector.slnx -c Release --no-restore 核心五目标/DI 三目标 0 警告、0 错误；UnitTests 410、ConsumerTests 29、DependencyInjection.Tests 8 全通过，报告 TestResults/b06-unit.trx、b06-consumer.trx、b06-di.trx。format whitespace --verify-no-changes、git diff --check、两包 pack 通过；ZIP/XML 校验 5/3 目标资产、8 个依赖组、统一 0.1.0-alpha、精确核心依赖及 Standard/net8→8.x、net9→9.x、net10→10.x 依赖分组通过。包 README 提示仍归 R01。全部测试宿主仍 net10.0，隔离加载资产不等于旧运行时通过。

B06 保持待验证、G2 保持进行中，下一模块为 B07 本地事务。依用户要求继续暂缓实库及非 Windows，旧运行时亦无验收证据；MySQL 可信模式版本矩阵及 Oracle 字符长度/真实空字符串语义仍待环境。不改变 A04/A05、G0/G1 的缺口，不因字段 schema 已实现声称数据库目录 GetSchema 或两模式实库兼容已完成。

2026-10-18 B07 本地事务：沿 Java 会话隔离/自动提交协议，固定原物理会话并显式校验命令事务；默认 MySQL RepeatableRead / Oracle ReadCommitted，Oracle 只开放 ReadCommitted/Serializable。完整提交/回滚 OK 已确认后恢复自动提交失败时销毁会话并保留已确认结果；断线/取消无终态则报告结果未知，绝不重放。事务释放、连接关闭及 CloseConnection reader 的现代异步清理走真实异步回滚；忙会话销毁，环境事务中仍可清理。关闭事件先于旧会话回滚，延迟清理不会影响新会话。

验证：`dotnet build OceanBaseConnector.slnx -c Release --no-restore` 五目标核心/三目标 DI 0 警告/错误；Windows 单元455、消费34、DI8 全部通过（`TestResults/b07-unit.trx`、`b07-consumer.trx`、`b07-di.trx`）；事务新增45项，其中首批6项由桩失败转通过。独立走查发现的 reader 异步清理同步回滚、重开后迟到 Closed 事件均经失败后通过的回归修复，复查无剩余需修复项。五资产经公共 ADO.NET 事务接口消费，现代三资产延迟响应验证异步 Begin/End；两个 Standard 资产仅同步事务入口，未作为旧运行时验收。日志使用独立 Transaction 类别并在执行前创建 logger，后端失败不改变已确认结果。格式/diff 检查、双包生成和 ZIP/XML 资产5/3组、依赖8组、统一0.1.0-alpha与 DI 精确核心依赖检查通过；无新增依赖，Microsoft.Extensions 仍按目标8/9/10分组。NuGet README 提示仍留 R01。

B07 保持待验证，G2 仍0/9、进行中，下一模块 B08。MySQL 仅有内部隔离映射测试，公共握手仍受 A05 可信版本规则缺口阻止；实库及非 Windows 按用户要求暂缓，旧运行时未验证。A04/A05、G0/G1 缺口不变。


2026-10-19 B08 本地故障矩阵：统一交换状态机覆盖发送前取消、部分头/负载写入失败、部分异步发送时 Cancel/token/超时、已发送但无终态响应、完整 OK/ERR 与取消竞争、多结果尚未结束、Commit/Rollback 中断及 Close 后重开的旧命令取消/释放。未发 SQL 时取消保留可复用会话；准备中断销毁会话但不声明 SQL 执行结果未知；发送执行请求且无可信终态时报告结果未知并销毁，绝不重放。完整终态响应优先于解析前取消，即使传输已保守销毁，仍保留已确认成功/服务端错误；带 MoreResults 的中间 OK 不算完整完成。合成“收到请求但不返回”的环回不证明服务端确实执行，真实执行后断线留待实库验收。

B08 修复及审查：异步 I/O 抛出的内部预算 token 原先被当作外部取消，现保留外部 token、按调用方取消/Cancel/预算到期归因，超时返回 TimeoutException 原因。日志仅把直接或 OceanBaseException 包装的 OperationCanceledException 标为 Canceled，不把超时内部中止标成调用方取消。接收路径超时8例中4例先红后绿；审查建议补齐部分异步发送后取消，新增7例，恢复旧逻辑时其中超时3例失败、恢复修复后全绿。独立只读复查无剩余需修复项。

B08 最终 Windows 验证：Release slnx 核心五目标/DI 三目标编译0警告/0错误；Unit498、Consumer39、DI8 全通过（TestResults/b08-unit.trx、b08-consumer.trx、b08-di.trx），相比 B07 新增43项单元与5项消费测试。五资产在 net10.0 宿主分别验证公共预取消 token 和真实环回命令超时；不等同旧运行时验证。format whitespace --verify-no-changes、diff 检查和双包 pack 通过；ZIP/XML 校验核心5/DI3资产、8依赖组、统一0.1.0-alpha、DI 精确核心版本、Microsoft.Extensions 的 Standard/net8→8.x、net9→9.x、net10→10.x 分组，无新依赖。包 README 提示继续留 R01。

B08 保持待验证，下一模块 B09 公共 API 审核；G2 仍0/9、进行中。实库与非 Windows 按用户要求暂缓，旧运行时亦无验收证据。A04 本地已完成、待真实/跨平台 TLS 验收；A05 可信 MySQL 模式判定规则仍是实现缺口，版本/插件/真实握手证据亦缺，不把该缺口归为单纯环境验证，也不放行缺 Oracle 模式位的公共握手。G0/G1 门槛不变。


2026-10-19 B09 本地审核与修复：保留 OceanBaseDbType0..9，追加10..26基础标量族，reader两套schema的ProviderType使用公开枚举整数；模式相关NUMBER/OracleDate/Timestamp及String/Binary归一映射见架构§5。参数override复用严格DbType编码与模式检查，Null只允许空值，延期类型仍拒绝。新增65项类型/模式测试，首批49项证明缺口后通过，包含Oracle TIME非空/typed-null拒绝；5资产既有消费用例补ProviderType/override断言。公开能力与Java有意差距归档进既有架构文档，不另建文档。

B09日志核查确认命令1100/1101原先误用Connection类别，导致只启用Command类别的应用看不到命令日志；新增同步/异步成功/失败4项公共环回回归先全红后全绿。命令logger在构造时安全创建，沿用既有过滤/写入异常隔离，不变更事件ID、脱敏或日志工厂所有权；DI创建logger数量预期同步更新。独立源码走查无剩余需修复项。

最终Windows验证：`dotnet build OceanBaseConnector.slnx -c Release --no-restore`核心五/DI三目标0警告0错误；Unit567、Consumer39、DI8全通过（TestResults/b09-unit.trx、b09-consumer.trx、b09-di.trx），相比B08增加69项单元。format whitespace、diff、双pack及ZIP/XML5/3资产/8依赖组/统一0.1.0-alpha/DI精确核心版本/按TFM划分Microsoft.Extensions8/9/10全部通过；未新增依赖，NuGet README提示留R01。

独立运行时基础消费：版本管理的RuntimeConsumerSample.cs经SDK10 csc和8/9/10 reference pack编译；私有依赖目录及LatestPatch runtimeconfig防止net10宿主统一旧依赖。Standard2.0/2.1/net8→.NET8.0.31+日志程序集8，net9→9.0.20+日志9，net10→10.0.12+日志10，五次均通过原token预取消、已认证Oracle环回查询、NUMBER/decimal、两套ProviderType及CloseConnection。命令、响应文件和结果位于artifacts/b09-runtime-smoke，复现方式见开发计划B09。日常xUnit工程仍net10.0；此基础样例不代表旧运行时DI、全部API、.NET Framework4.8或真实双模式兼容通过。

B09保持待验证，G2仍0/9；下一模块C01。按用户要求未执行实库和非Windows测试，真实两模式/OBProxy及完整运行时矩阵仍缺。A04本地完成、真实TLS验收未完；A05可信MySQL模式判定仍是实现缺口，不以样例Oracle成功放行MySQL。C01必须保留无可信完整重置白名单时拒绝池化的边界。


2026-10-19 C01核查与C02实施：未取得任何可信的OceanBase/OBProxy完整重置版本/模式白名单；Java2.4.14 reset在非特定MariaDB组合依赖驱动跟踪状态，也不能证明原始SET/ALTER SESSION和临时对象清理。C01记为阻塞，Pooling=true继续拒绝。按暂缓实库要求先推进独立C02，未添加测试专用池化开关或将销毁连接冒充连接池。

C02使用既有Server/Host键的有序逗号候选，支持host[:port]、[IPv6][:port]和无端口IPv6；省略端口继承Port。解析/冻结位于OceanBaseEndpoint与OceanBaseConnectionOptions，PhysicalSession同步/异步仅在TCP建连socket故障时尝试下一地址；全Open共享预算、0无限。每次显式重开从首候选开始，TLS/认证/协议/模式错误及所有业务执行均不切换、不重放。逐端点快照保留TLS/凭据/模式/数据库配置；DataSource保留原列表，SNI和认证_server_host使用实际主机，成功/关闭/命令/事务日志记录实际端点并脱敏。

C02本地验证：首批26项非法候选/公开fallback测试先全失败后全通过；共新增60项单元和10项五资产同步/异步消费。Release slnx五/三目标0警告/错误，Unit627、Consumer49、DI8通过（TestResults/c02-unit-final.trx、c02-consumer.trx、c02-di.trx）。覆盖全部失败、模式不匹配、认证错误、握手/SQL断线不重试、取消/关闭、无超时配置、总超时、数据源、重开首候选、未用候选不接触、逐地址TLS和显式名称、证书拒绝及密码保护。独立走查发现总预算测试错误依赖拒绝耗时，已删除对末候选必须未接触的断言；现回归仅证实Open总耗时上界（2项单独通过），共享预算不重置由循环外一次CreateBudget的源码确认：TCP及异步握手传预算token，同步TLS/认证由预算注册关闭传输中断；无剩余需修复项。

五资产实际Windows基础消费使用扩展的RuntimeConsumerSample.cs，新增首地址失败后的查询；Standard2.0/2.1/net8在8.0.31、net9在9.0.20、net10在10.0.12，日志程序集8/9/10全部通过。私有依赖、SDK10 csc响应文件和LatestPatch runtimeconfig方法沿用B09，材料在artifacts/c02-runtime-smoke。格式/diff、双包5/3资产及8依赖组、统一0.1.0-alpha和Microsoft.Extensions按TFM8/9/10检查通过；未新增依赖，包README提示留R01。

C02保持待验证，G3仍0/8、进行中；C01阻塞且A05可信MySQL模式判定仍是实现缺口，下一可本地推进模块C03。真实两模式/OBProxy、非Windows、IPv6网络和可信私有CA成功链未执行；旧运行时DI/完整API矩阵亦未验收，不把基础环回或源码走查替代实测。

2026-10-19 C03 数值子项续接与修复：DecimalCodec/ResultColumn 贯通 MySQL DECIMAL(65,20)、Oracle NUMBER(38)、未知精度标记和负 scale；标准类型由声明固定，宽精度/未知精度列的小值和 NULL 不缩窄。GetDecimal/GetFieldValue<decimal> 只精确转换，参数用普通十进制文本写回，不经 decimal/double。p0/s0 保留载荷系数/scale；p0 且已知非零 scale 使用 Oracle38/MySQL65 精度上限精确重标度，schema 精度仍未知。参数仅声明 Scale 时不凭空套用 Precision=38，合法大指数不误拒绝；两项回归先红后绿（c03-scale-red.trx、c03-number-green-final.trx）。

独立走查发现并修复两项 MySQL 负 scale 问题：声明 Precision 未计算展开后的整数零位（Precision5 错误接受123000），以及零系数因负 scale 被误判超界。分别有1项、2项先红后绿的回归（c03-precision-red.trx、c03-zero-red.trx；最终数值筛选68项通过，c03-number-targeted.trx）。修复按模式限定，不改 Oracle 的有符号 scale 语义；最终只读复查无剩余代码问题，文档澄清已合入既有设计/计划。旧 B 阶段“拒绝宽精度”的测试预期同步调整，保留越界、unsigned、需舍入、恶意指数/scale 和输入长度拒绝；公开同步/异步、文本/二进制测试覆盖读取、稳定 schema、精确转换与参数写回。

本地验证：净增45项单元、5项五资产消费；Release slnx 核心5目标/DI3目标编译0警告/错误，Windows Unit672、Consumer54、DI8全部通过（TestResults/c03-number-unit-final.trx、c03-number-consumer-final.trx、c03-number-di.trx），测试宿主均为net10。格式、git diff检查及双包生成通过，ZIP/XML核查核心5资产、DI3资产、8依赖组、统一0.1.0-alpha、DI精确核心依赖和Microsoft.Extensions按Standard/net8→8、net9→9、net10→10分组均通过。包缺少README的既有提示继续留R01。

C03 仍为进行中，下一步为纳秒/TZ/LTZ及MySQL零日期子项；不将数值子项提交当成C03整体完成，G3仍0/8。未执行实库、非Windows或新的旧运行时验收；MySQL数值映射来自内部夹具，公开五资产环回为认证Oracle模式，A05可信MySQL规则仍缺，不为数值测试放开握手。C01完整重置白名单及G0/G1环境缺口不变。

2026-10-19 C03 不带时区的纳秒子项：新增五目标共享OceanBaseTimestamp，保留Unspecified整秒、纳秒及scale；构造和ToDateTime禁止舍入，年份范围保持1..9999。文本/二进制type202结果共用12字节解码，p<=7标准DateTime、p8/9标准OceanBaseTimestamp，所有p的provider-specific访问和两套schema固定为OceanBaseTimestamp，小值/NULL不缩窄。参数推断DateTime2/复用Timestamp身份，仅Oracle TIMESTAMP接受provider值；默认保留provider scale，DateTime默认7，显式0..9仅精确重标度。普通MySQL时间、Oracle DATE及延期TZ/LTZ仍保持原有边界。

测试与走查：首批3项缺口回归全红后全绿（c03-nanos-red.trx、c03-nanos-value-green.trx），随后codec/公开环回12项先失败后通过（c03-nanos-codec-red.trx、c03-nanos-codec-green.trx）。覆盖1ns/999999999ns、7/8/9位、边界日期、非法组件/载荷/scale、typed-null、默认值、Kind、两种结果协议及同步/异步读取/写回；新增空载荷回归暴露type202被误归为零日期，现先校验TIMESTAMP长度、明确ProtocolException。独立只读走查无必须修复项；补充建议的9→8/8→9成功精确重标度与DateTime显式scale9回归，保留参数原值，最终文档合入既有设计/计划。

本地验证：新增44项Unit、5项五资产消费；Release slnx核心5目标/DI3目标0警告/错误，Windows Unit716、Consumer59、DI8全部通过（TestResults/c03-nanos-unit-final.trx、c03-nanos-consumer.trx、c03-nanos-di.trx）；全部日常测试宿主net10，五资产消费在隔离加载上下文经标准ADO.NET读取/转换/写回。格式、diff及双包检查通过，5/3资产、8依赖组、统一0.1.0-alpha、DI精确核心版本及Microsoft.Extensions按Standard/net8→8、net9→9、net10→10保持正确。既有NuGet README提示仍留R01。

C03继续进行中，下一步TZ/LTZ和MySQL零日期；G3仍0/8，C01/A05及G0/G1缺口不变。Java OBTIMESTAMPParameter固定写scale9，而本驱动选择保留显式/provider scale，结果端仍严格要求列scale等于载荷scale；这两项策略和1..9999外年份的协议范围需后续真实矩阵核验。未执行数据库服务器、非Windows或新的旧运行时测试，不以本地夹具关闭互通验收。

2026-10-19 中断走查续接：先核对历史审查和修复提交，B01关闭事件、B02终态/取消、B04语句清理、B05精度/TIME及C03数值/纳秒问题均已有回归和提交。再次走查发现 netstandard2.1 连接、reader、事务异步生命周期错误受 NET8 条件限制；直接读取 NETStandard.Library.Ref 2.1.0 参考程序集确认 API 后，将四处条件扩至 NETSTANDARD2_1，复用共享异步实现。ChangeDatabaseAsync 同时提供明确的不支持失败，不新增目录切换能力；数据源/batch 目标和 Microsoft.Extensions 版本矩阵保持原约定。

新增消费回归先复现6项失败（TestResults/review-async-red.trx）：2.1资产五种清理入口阻塞于延迟ROLLBACK，且事务异步覆写缺失。修复后定向17项全通过（review-async-green-final.trx），覆盖异步Begin/Commit/Rollback、原token预取消、连接CloseAsync/DisposeAsync、事务DisposeAsync及reader CloseAsync/DisposeAsync等待回滚和自动提交恢复；2.1与net10的延迟清理均经公开ADO.NET接口验证。最终Windows Unit716、Consumer71、DI8全部通过（review-fixes-{unit,consumer,di}.trx）；Release slnx核心五/DI三目标0警告0错误，格式/diff、双pack及5/3资产、8依赖组、统一0.1.0-alpha、DI精确核心依赖和Microsoft.Extensions8/9/10检查通过。NuGet README提示继续留R01。

独立只读复查已核对共享异步路径、预取消、延迟回滚测试和红绿TRX；指出的架构旧段落已修正，三份文档最终复核无剩余需修复项。本次只修复走查遗漏，不推进TZ/LTZ：C03保持进行中，G3仍0/8；A04/A05、C01及真实环境关口不变。未执行实库、非Windows或新的旧运行时测试；隔离资产消费宿主仍为net10，不能代替旧运行时验收。


2026-10-19 C03 TZ 子项与中断走查续接：新增五目标共享 OceanBaseTimestampWithTimeZone，贯通 type200 的文本/二进制读取、固定标准/provider-specific字段类型与schema、精确 DateTimeOffset 转换、参数自动推断及显式 TimestampWithTimeZone（末尾27，既有枚举不变）。本地字段、纳秒、offset及服务端名称/缩写一并保留；不以客户端时区重新解释。LTZ会话时区及MySQL零日期仍未实现，C03继续进行中。

独立走查发现并修复三项协议问题：一是服务端负小时结果使用分钟幅值，(-3,+30)为-03:30，不能以异号为由拒绝；二是结果使用长度编码但参数必须单字节长度，251..255字节不能调用通用WriteBytes，超过255字节须发包前拒绝；三是结果内部名称/缩写也用长度编码，251..255字节占3字节前缀，不能沿用参数单字节字段，最大结果载荷相应为530字节。根据固定服务端提交5e8a63d29dae9fde5e962b8fcc7b167d438fc343的stmt_execute与ob_time_convert补证，参数(-0:30)和非9 scale有源码支持，仍不等于目标版本互通验收。名称存在时服务端可优先按其时区表/缩写解析，不承诺矛盾名称/offset写回后仍保留offset；布局和链接已合并架构§5，未新增独立设计文档。

缺口测试先有9项codec与5项公共接口失败后通过；本轮审查修复又以c03-tz-review-red.trx复现7项失败，修复后c03-tz-review-green.trx的47项通过。回归覆盖两种结果协议、同步/异步公开读取/参数写回、正负/不足小时偏移、DST重复本地时刻、0..9精度、NULL与schema稳定、精确转换/重标度、严格UTF-8、畸形载荷、530字节结果以及250/251/255参数和256/526拒绝；超长参数公开回归确认未发送准备/执行包且会话可继续使用。完整首轮3项失败为旧TZ未支持断言，已替换为Guid发送前拒绝并移除已支持类型200的延期断言，保留其他未支持类型检查。

长名称结果另以c03-tz-name-red.trx复现251/255字节名称两项失败，修复ReadTimeZoneIdentity长度编码及最大载荷后，c03-tz-targeted-final.trx定向57项全通过；NULL/超限内部长度在分配前拒绝。参数内部与外部长度仍保持单字节，不把长名称结果夹具当作可写参数。

最终本地验证：Release slnx核心五目标/DI三目标0警告0错误，Windows Unit772、Consumer76、DI8全部通过（TestResults/c03-tz-unit-final.trx、c03-tz-consumer-final.trx、c03-tz-di-final.trx）。五资产消费均由net10宿主隔离加载，不能代替旧运行时验收。格式/diff、双包和ZIP/XML资产/依赖检查通过：核心5资产、DI3资产、8依赖组、统一0.1.0-alpha、DI精确核心版本、Microsoft.Extensions按两个Standard/net8→8、net9→9、net10→10。既有NuGet README提示留R01。独立只读最终复审已核对三项修复、红绿证据、稳定类型/typed-null/精确转换及三份文档，无剩余必须修复项。

C03仍进行中，下一步LTZ可信会话时区与MySQL零日期；G3仍0/8，A04/A05真实验收、A05可信MySQL模式判定、C01完整重置白名单及G0/G1缺口不变。按用户要求未运行数据库服务器及非Windows测试，也未新增旧运行时验收；源码和环回证据不代替目标版本互通。当前提交仅完成TZ子项和本轮走查修复，不把C03整体标记完成。


2026-10-19 C03 LTZ前置状态通路：核查发现命令OK/FEOK已解析的会话变量原被丢弃，仅靠认证SessionVariables会误用旧时区。现PhysicalSession保存独立可失效time_zone报告，SQL发送时原子捕获/清空，QueryResult每结果冻结快照并清空后续来源，合法终态OK/事务控制报告更新，经典EOF/ERR不猜测。prepare/close不执行SQL，未发包取消不失效；Close/Break清空且租约身份校验阻止晚到响应污染新命令或复活关闭会话。未增加网络查询、公开配置或依赖；type201仍未开放，查询补全/参数时区一致性继续后续实现。

回归先复现12项缺口失败（c03-ltz-state-red.trx）；最小实现后23项定向通过（c03-ltz-state-green.trx），覆盖同步/异步、认证快照独立、三结果报告A→无报告→未知、经典EOF/FEOK、空/有行预读、SQL ERR/未发送取消、准备/关闭、事务逐Control、关闭/故障和旧租约隔离。初次绿运行两项测试误将关闭时ProtocolException当成EndOfStreamException，修正服务端断言后通过，不作为产品缺陷记录。该报告状态不是已验证的时区解释保证，不以原始SYSTEM/任意名称直接解析LTZ。完整验证和走查结果续记于下。

独立走查补充修复：前一结果报告A后，后一结果首包ERR会在进入Header前抛出，原实现未清除A。同步/异步两项回归先失败（c03-ltz-state-review-red.trx），将快照捕获/失效移到Receive之前后，c03-ltz-state-green.trx最终25项全部通过；连接仍可用但时区为未知，不回填认证值。

最终本地验证：Release slnx核心五目标/DI三目标0警告0错误，Windows Unit797、Consumer76、DI8全部通过（TestResults/c03-ltz-state-unit-final.trx、c03-ltz-state-consumer-final.trx、c03-ltz-state-di-final.trx）；25项状态回归纳入完整单元套件。格式/diff、双包及ZIP/XML的5/3资产、8依赖组、统一0.1.0-alpha、DI精确核心依赖及Microsoft.Extensions按两个Standard/net8→8、net9→9、net10→10全部通过。独立只读最终复审已确认ERR修复和三份文档，无剩余必须修复项。既有NuGet README提示仍留R01。

C03继续进行中：LTZ可靠查询补全、type201全链路和参数时区一致性、MySQL零日期仍需实现；不把会话报告通路当作LTZ交付。A04/A05真实验收、A05可信MySQL判定、C01完整重置和G0/G1/G3缺口不变。遵用户要求未执行实库或非Windows，net10隔离消费不替代旧运行时验收。


2026-10-19 中断续接LTZ全链路：上次只留下计划和8项缺口测试，未将未实现功能计入交付。本次新增五目标共享OceanBaseTimestampLocalTimeZone和末尾枚举28，贯通201文本/二进制、固定schema/provider类型、精确DateTime转换及参数写回。保留本地字段/纳秒/scale/服务端会话标识，不用客户端时区推算DST或恢复原始offset。NULL可在未知时区下保持稳定类型，但模式和scale照常校验；非NULL值没有有效结果快照时失败。MySQL201及错误显式类型（含typed NULL模式边界）拒绝。

Oracle业务执行前复用有效报告，缺失/SYSTEM/非法时同租约和取消/超时预算查询SELECT SESSIONTIMEZONE FROM DUAL；严格限定一列UTF-8字符串、一行有效非NULL标识，排空终态后发布，内部查询不标记用户SQL已发送。普通参数错误在查询前拒绝，LTZ标识与当前会话不匹配在prepare/execute前拒绝。无持续报告时会增加后续执行的一轮查询；活动reader不查询，多结果没有新报告时不沿用旧时区。依据固定Java及server源码的本地字段、会话上下文与MYSQL_ONLY追踪配置说明已合并架构§5，未新增设计文档。

独立走查发现并修复两项问题：内部查询标量/终态报告冲突后仍保留报告，可能影响下次LTZ解释；显式Timestamp被LTZ值自动覆盖为201。c03-ltz-review-red.trx的3项同步/异步回归先失败，修复后49项定向通过（c03-ltz-review-green.trx）：已排空的冲突清空报告并失败，连接保持Open但下一次必须重查；显式Timestamp+LTZ在发包前拒绝。覆盖纳秒/NULL/schema、文本/预处理公开读写、逐字时区匹配、查询额外行/结果/非法UTF-8/NULL/超长/断连、同预算超时、原caller token及同步Cancel、服务端ERR后重查、普通绑定先拒绝和真实201多结果快照。五资产增加LTZ隔离消费，仍由net10宿主运行。

首次完整回归的失败分别为旧测试夹具未补答新查询、deprecated EOF补答格式不符，以及Windows断连时服务器仍发送终态的竞争；已显式适配查询流程并消除无效响应写入。沙箱内TLS证书导入失败在正常权限下重跑，不改动产品TLS实现。最终Release slnx核心五/DI三目标0警告0错误；Windows Unit846、Consumer81、DI8全部通过（TestResults/c03-ltz-unit-final.trx、c03-ltz-consumer-final.trx、c03-ltz-di-final.trx）。格式/diff、双包ZIP/XML检查通过：核心5资产、DI3资产、8依赖组、统一0.1.0-alpha、DI精确核心版本、Microsoft.Extensions按两个Standard/net8→8、net9→9、net10→10。独立复审确认生产代码无剩余必须修复项，三份文档已同步当前口径；既有NuGet README提示保留R01。

C03继续进行中，剩余MySQL零日期及真实验收；G3仍0/8，A04/A05真实验收、A05可信MySQL模式判定和C01完整重置缺口不变。本次未执行数据库服务器、非Windows或新的旧运行时测试；本地源码/环回/隔离消费不代替目标版本与OBProxy互通，SESSIONTIMEZONE权限、时区表和真实DST仍需验证。


2026-10-19 C03零日期子项：新增五目标共享OceanBaseDate/OceanBaseDateTime，保留全零及部分零的日期分量、微秒和scale；默认值为全零，不采用DateTime.MinValue或NULL替代。MySQL DATE10/DATETIME12/TIMESTAMP7的标准类型仍固定DateTime，正常值标准读取不变，零分量GetValue/GetDateTime/GetFieldValue<DateTime>/GetValues抛InvalidCastException。Provider-specific类型及两套schema整列分别固定Date/DateTime值类型，混合正常/零值和协议NULL可稳定访问；IsDBNull只判断协议NULL。参数自动推断Date/DateTime，仅MySQL对应类型接受provider值，错误模式/类型/精度在发包前拒绝。部分零或零日期带非零时钟使用4/7/11字节字段，仅全部分量为零时编码零长度；零长度读取也保存列声明scale。

先以9项公共类型/稳定元数据/零日期转换失败（c03-zero-red.trx）证明缺口；实现后9项通过，扩展至49项定向全部通过（c03-zero-targeted.trx），覆盖正常/全零/部分零、闰日/边界年份、严格格式、二进制长度/分量/微秒、精度不截断、模式拒绝、同步/异步文本和预处理混合reader、provider批量访问、NULL、写回和TIMESTAMP7。五资产消费增加10项DATE/DATETIME混合值和参数写回。初次扩展测试4项失败源于测试中default字面量选择了DateTime重载（合法0001-01-01）而非OceanBaseDate重载，改为显式default(OceanBaseDate)后通过；这不作为产品缺陷或零日期归一化记录。原有DATE scale6测试夹具按契约改为0，非法声明另有拒绝回归。

独立只读走查未发现必须修复项，建议的TIMESTAMP混合行和少于声明精度小数已补入定向回归。最终Release slnx核心五/DI三目标0警告0错误；Windows Unit895、Consumer91、DI8全部通过（TestResults/c03-zero-unit-final.trx、c03-zero-consumer-final.trx、c03-zero-di-final.trx）。格式/diff、双包ZIP/XML资产/版本检查通过：核心5资产、DI3资产、8依赖组、统一0.1.0-alpha、DI精确核心依赖及Microsoft.Extensions按两个Standard/net8→8、net9→9、net10→10。NuGet README既有提示继续留R01。

三份现有文档已合并零日期构造/转换、协议、参数及精度契约，未新增设计文档。C03本地子项已齐备，状态从进行中改为待验证，G3仍0/8；下一可本地推进模块C04。A05可信MySQL认证仍未实现，reader及五资产MySQL测试通过明确内部模式夹具替换认证后事实完成，不增加产品测试开关或放宽握手，不据此宣称公开MySQL连接可用。SQL_MODE对零分量/年份0的接受、TIMESTAMP时区语义及目标版本写回scale待实库确认；其他ALLOW_INVALID_DATES非法日历仍拒绝。未执行数据库服务器、非Windows或新的旧运行时测试，五资产消费宿主为net10；A04/A05真实验收、C01完整重置及G0/G1缺口不变。


2026-10-19 C04报文增量读取前置：PacketChannel新增内部OpenPayload/OpenPayloadAsync，打开只读取4字节帧头，正文按调用方请求读取；续帧/空终结帧保持序号和累计64 MiB上限。未完整消费的payload独占通道，禁止另一次读包/写包、重置序号或替换TLS传输；提前释放、截断、损坏和正文取消关闭传输，预取消打开不消费空闲通道。ReadPacket同步/异步改为共用同一实现并保留显式数组返回；普通reader仍缓存当前行，未开放SequentialAccess或游标，不更改参数/模式/租约策略。

初始21项增量API缺失回归失败后通过（c04-payload-red.trx、c04-payload-green.trx）；整包超限未关闭传输的2项回归先红后绿（c04-buffered-red.trx、c04-packet-green.trx）。新测试扩展至29项，含32 MiB按需生成载荷、同步循环分配小于256 KiB、正文不预读、续帧/精确满帧空终结、混合读包序号回绕、分片I/O/中途取消、累计上限提前拒绝及提前关闭；报文定向共54项通过（c04-packet-targeted.trx）。该分配断言不代表公开reader或真实服务端内存/吞吐验收。

最终Release slnx核心五/DI三目标0警告0错误；Windows Unit924、Consumer91、DI8通过（TestResults/c04-payload-unit-final.trx、c04-payload-consumer-final.trx、c04-payload-di-final.trx）。格式/diff和UTF-8无BOM/CRLF检查通过；双包核心5资产、DI3资产、8依赖组及统一0.1.0-alpha/DI精确核心版本/Microsoft.Extensions 8/9/10矩阵核对通过，既有NuGet README提示保留R01。独立只读走查已检查整包/增量路径、帧状态、失败资源清理及握手/CommandExchange调用，无必须修复项。三份文档同步C04进行中；下一步QueryResult/reader有序字段流和字符解码，再按模式/版本/能力证据实现游标。Java 2.4.14标准输入流仍拼接整包，游标由不同模式/执行选项决定并有普通/Oracle扩展fetch差别，不直接照搬为已验证.NET能力。实库、非Windows和新的旧运行时测试暂未执行；A05可信MySQL判定、C01完整重置和G0/G1/G3缺口不变。

2026-10-19 C04 reader 续接及中断走查修复：QueryResult/CommandExchange 接入单所有者增量 payload，SequentialAccess 同步/异步支持文本与预处理二进制、旧 EOF/弃用 EOF、逐字段流与显式整字段物化；默认 reader 保留当前行缓冲。GetBytes/GetChars/GetStream/GetTextReader、异步 Stream/TextReader/值 getter、NULL 和 provider-specific 精度遵守字段/偏移只前进约束；跳列/行/结果复用 8 KiB 缓冲区排空。GetChars 的 null buffer 字符长度探测在顺序模式拒绝，严格 UTF-8 保留 BOM 和代理项；整值/字节/字符访问不混用，前一个字段流在换列/行/结果或关闭时失效。未读完提前关闭销毁会话，CloseConnection 同时关闭逻辑连接。

独立只读走查发现的缓存字符及已耗尽流绕过 Cancel/超时/连接关闭已修复；进一步回归修复完整终结 EOF/ERR 收完后、解析前取消被误报未知结果，以及字段自身 token 取消未进入命令预算。新增生命周期8项先红后绿、字段token2项先红后绿；前序非法二进制字段framing2项亦由先失败回归覆盖。未知结果销毁会话并禁止重放，已知服务端终结结果不被后到取消覆盖。42项新顺序读取用例及12项复用零日期读写回归覆盖跨帧32 MiB按需生成大值、正文未到达即可返回reader、同步drain托管分配低于1 MiB、NULL/偏移/异步传输/跳列/多结果/UTF-8/提前关闭和阻塞读取token/Cancel/超时/连接关闭；内存断言不是实库内存峰值或吞吐基线。

Release slnx核心五/DI三目标编译0警告0错误；完整Windows Unit978、Consumer101、DI8通过（TestResults/c04-reader-unit-final.trx、c04-reader-consumer-final.trx、c04-reader-di-final.trx）。最初沙箱全量单测的10项TLS用例因Windows证书凭据/私钥存储访问失败，获授权的沙箱外完整重跑全部通过，未修改TLS校验策略。格式、diff、UTF-8无BOM/CRLF与双包检查通过，核心5/DI3资产、统一0.1.0-alpha、DI精确核心依赖及Microsoft.Extensions 8/9/10矩阵通过；既有NuGet README提示保留R01。新增五资产隔离顺序流消费及纳秒provider写回各5项，宿主为net10；不能代替旧运行时完整验收。三份既有文档合并契约、勾选本地完成项并明确C04仍进行中；剩余服务端游标、真实两模式/OBProxy大值内存/吞吐及资源验收。未执行实库、非Windows或新的旧运行时测试；A05可信MySQL判定、C01完整重置及G0/G1/G3缺口不变。

2026-10-19 C04 服务端游标及中断走查修复：新增统一 OceanBaseCommand.FetchSize，默认0为普通结果流，正数快照分页行数、强制服务端prepare并设置只读execute flag=1，负数拒绝；无结果列语句在execute前拒绝。QueryResult同步/异步、默认/SequentialAccess共用COM_STMT_FETCH控制，保持同一statement ID/物理会话/命令租约和总超时，每页重置报文序号。MySQL直接读二进制行，Oracle校验重发列元数据。旧EOF及MySQL FE-OK严格确认游标；Oracle协商弃用EOF时按固定Java源码省略execute/fetch元数据EOF，由首次fetch确认结果，空结果也不跳过确认。继续页必须CursorExists且有进展，LastRowSent才释放租约；超额行、元数据变化、多结果/OUT、不确认及无进展均失败，不回退重执行。HasRows预取首行/顺序前缀，NextResult读尽所有页，提前关闭或取消销毁原会话；读尽可复用statement，FetchSize改回0恢复普通结果流。

独立走查发现两项必须修复缺陷并以4项先失败回归修复：游标ERR先释放租约后销毁会话，日志重入可发送新命令；以及0x00二进制行可冒充弃用EOF的游标确认包。当前CommandExchange在Complete/回调前销毁游标会话，保留已知1064错误和IsExecutionOutcomeUnknown=false；游标确认包必须以0xFE开头，恶意行确认在执行结果未知状态下销毁会话且不发fetch。另以2项先失败回归修复中间页未消费会话状态报告，避免沿用旧时区；再以2项先失败回归修复初始LastRowSent空结果把终结报告重复清除，最终包及非终态报告均只观察一次。此前reader token不能中断部分fetch写入的2项回归亦已修复，取消进入同一命令预算，不重放未知SQL。独立差异复查已核对这些修复及Oracle弃用EOF通路，无剩余必须修复项。

本次新增59项游标单测，定向Cursor共70项通过（其中11项为既有ByteCursor测试）；分页覆盖同步/异步×默认/顺序×两模式×旧/弃用EOF共16组合，另测空最终页/初始空结果、非法响应、元数据、分页总预算、部分写入取消、失败日志重入、提前关闭、NextResult排空和statement复用。五资产隔离游标消费10项覆盖Oracle旧/弃用EOF。红绿证据为TestResults/c04-cursor-review-{red,green}.trx、c04-cursor-oracle-eof-red.trx、c04-cursor-state-red.trx、c04-cursor-empty-state-red.trx及c04-cursor-complete-green.trx。

最终 dotnet build OceanBaseConnector.slnx --no-restore -c Release：核心五/DI三目标0警告0错误；Windows完整Unit1037、Consumer111、DI8通过且0跳过（TestResults/c04-cursor-unit-final-checked.trx、c04-cursor-consumer-final-checked.trx、c04-cursor-di-final-checked.trx）。完整单测在已授权沙箱外运行以访问Windows TLS测试证书存储，未降低TLS校验。dotnet format whitespace --verify-no-changes、git diff --check、UTF-8无BOM/CRLF和双包检查通过；TestResults/c04-cursor-pack中核心5/DI3资产、统一0.1.0-alpha、DI精确核心版本[0.1.0-alpha]及Microsoft.Extensions按TFM为8/9/10的依赖组均通过，既有NuGet包README提示仍留R01。

三份既有文档已合并当前契约、源码依据、进度及验证证据；C04由进行中改为待验证，下一可本地推进模块C05。G3仍0/8，A04真实/跨平台验收、A05可信公开MySQL握手、C01完整重置白名单及G0/G1缺口不变。未执行真实两模式/OBProxy、非Windows或新的旧运行时验收；隔离消费宿主为net10，不能代替旧运行时完整验收。offset/滚动游标、REF CURSOR和locator LOB未实现，不据本地夹具宣称目标版本互通或真实游标资源/大值性能已验证。当前master完成本阶段提交，不推送。

2026-10-19 C05 数据库切换模块：MySQL ChangeDatabase 使用 COM_INIT_DB 原样编码名称，Standard2.1及现代目标覆写真正异步入口，Standard2.0不补不存在的API。Oracle/环境事务/忙会话在发送前拒绝；null/空/NUL/非法UTF-16/超包拒绝，不拼接USE SQL。物理会话保存当前Database，合法单一零计数OK后、租约释放/日志回调前发布；已知ERR保留原Database并允许后续命令，非法响应/部分发送/取消/超时销毁会话并报告执行结果未知，不重放。配置和数据源保持原始Database，关闭/重开恢复配置；旧回复不能更新新会话。允许当前空闲的本地事务，但不改变其对象、绑定和提交状态。准备语句保存准备时Database，获得执行租约后重核对，不同则重新prepare，旧ID由既有关闭队列清理，失败切换保留原ID。

首批5项失败证明尚未实现切换/预取消后，补实现通过（TestResults/c05-database-red.trx、c05-database-green.trx）。新增36项单测覆盖同步/异步、大小写/特殊字符名称精确字节、已知错误、参数/模式/环境事务拒绝、6种非法响应×同步异步、部分写入、预取消/网络等待取消/总超时/关闭、reader独占及本地事务绑定、完整终态与后到取消、日志回调发布/数据源隔离、旧响应后重开及成功/失败的statement复用。五资产9项隔离消费覆盖Standard2.0同步及Standard2.1/net8/net9/net10同步/异步，真实查询目标仍为协议夹具，使用明确的认证后MySQL事实夹具，不放宽A05。定向36项、消费9项通过（TestResults/c05-database-lifecycle.trx、c05-database-consumer-targeted.trx）。独立只读走查覆盖相关会话/日志/配置/事务/预处理路径，无必须修复项；没有委托实现。

最终Release dotnet build OceanBaseConnector.slnx --no-restore -c Release 核心五/DI三目标0警告0错误；完整Windows Unit1073、Consumer120、DI8全部通过且0跳过（TestResults/c05-database-unit-final.trx、c05-database-consumer-final.trx、c05-database-di-final.trx）。完整单测沿用已授权沙箱外TLS证书存储访问，未修改TLS规则。格式、diff、UTF-8无BOM/CRLF与双包检查通过；TestResults/c05-database-pack中核心5/DI3资产、统一0.1.0-alpha、DI核心精确区间[0.1.0-alpha]及Microsoft.Extensions按TFM的8/9/10依赖组核对通过，既有NuGet README提示仍留R01。三份现有文档合并当前契约和实施记录，不新增设计文档；当前master提交，不推送。

C05仍进行中：MetaDataCollections、DataSourceInformation、Tables、Columns及限制参数/现代GetSchemaAsync尚未实现；后续完成目录模块再转待验证，不将数据库切换子项当作C05整体完成。原始SQL USE的状态报告/追踪不属于本模块已验证契约，当前Database跟踪通过ChangeDatabase改变的数据库，详见架构§4。真实两模式/OBProxy、非Windows与新的旧运行时验收均未执行；隔离消费宿主net10不代替旧运行时完整验收。A04/A05真实关口、A05可信公共MySQL握手、C01完整重置及G0/G1/G3缺口保持开放。
