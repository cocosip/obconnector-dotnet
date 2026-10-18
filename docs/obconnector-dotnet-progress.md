# OceanBase Connector for .NET 开发进度台账

基线日期：2026-10-17。依据：[架构设计](obconnector-dotnet-architecture-design.md)和[开发方案](obconnector-dotnet-development-plan.md)。2026-10-18 已续接 A 阶段握手/物理会话、B01 宿主接入、B02 无参文本命令/逐行 reader/多结果、B03/B04 名称绑定/服务端预处理及 B05 低精度数值/provider-specific 访问/常见日期时间。完整元数据、事务和宽精度扩展尚未实现，真实互通无证据，不能将夹具通过等同于阶段门槛关闭。

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
| G1 协议基础 A | W1-W5 | 4/7 | 阻塞 | A01/A02/A03/A06 完成；A04/A05 待真实验证，A07 缺环境与可信版本矩阵；G0 未关闭 |
| G2 ADO.NET 核心 B | W6-W17（范围修订后待重估） | 0/9 | 进行中 | B01-B05 本地实现待真实环境验证；B06-B09 及五目标旧运行时消费/双模式实测仍待完成 |
| G3 生产连接 C | W18-W34 | 0/8 | 未开始 | C01-C08 + 池化白名单/精确类型/MySQL `ChangeDatabase`/内存故障实测 |
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
| B01 | W6-W7（待重估） | 标准 provider/factory、现代无池数据源、可选 DI 与基础日志；`ChangeDatabase` 暂拒绝 | 待验证 | 本地实现、四/三目标编译、环回建连、DI/消费测试及双包依赖检查通过；双模式真实建连、旧运行时和非 Windows 验收待补 |
| B02 | W7-W9 | 文本 SQL、OK/ERR、多结果和更新计数 | 待验证 | COM_QUERY 同步/异步、逐行文本 reader、OK/ERR/EOF/FE OK、多结果和更新计数已有 Windows 环回证据；两模式真实 CRUD/匹配行语义、非 Windows 和旧运行时待验证 |
| B03 | W8-W10 | Oracle 命名占位符词法器及各类 `q` 字面量；MySQL 参数数由服务端确定 | 待验证 | 本地完成词法扫描、名称/顺序映射与发送前绑定校验；基础参数执行由 B04 接入，真实参数化集成测试暂缓 |
| B04 | W9-W12 | 服务端 prepare/execute、二进制结果和 statement ID | 待验证 | 本地实现及 Windows 协议/环回、五目标编译、消费/DI 测试通过；双模式实库、旧运行时和非 Windows 待补 |
| B05 | W10-W13 | 基础值、低精度 `OceanBaseDecimal`、Oracle DATE 与未支持类型拒绝 | 待验证 | Codex，2026-10-18：本地精确数值/日期编解码、公开 provider-specific 访问、五目标消费及 Windows 协议/环回通过；双模式实库、旧运行时及非 Windows 待补，完整字段元数据留 B06 |
| B06 | W11-W14 | 逐行 reader、字段元数据及低精度 provider-specific 访问 | 未开始 | 待记录 |
| B07 | W13-W15 | 本地事务及环境事务拒绝 | 未开始 | 待记录 |
| B08 | W14-W16 | 取消、超时、结果未知和故障注入 | 未开始 | 待记录 |
| B09 | W17（待重估） | 五目标消费、数据源/DI/日志生命周期、低精度 provider-specific 数值和双模式 G2 验收 | 未开始 | 待记录 |

## C. 生产连接任务

| ID | 计划窗口 | 可验收产物 | 状态 | 负责人/完成证据/实际完成日 |
| --- | --- | --- | --- | --- |
| C01 | W18-W23 | 可选池、完整重置和版本白名单 | 未开始 | 待记录 |
| C02 | W18-W21 | 多地址建连与发包后不重试 | 未开始 | 待记录 |
| C03 | W20-W25 | 宽精度、纳秒、时区和整列稳定的零日期 provider-specific 值 | 未开始 | 待记录 |
| C04 | W23-W28 | SequentialAccess、大值与服务端游标 | 未开始 | 待记录 |
| C05 | W25-W29 | `GetSchema` 基础集合及异步入口、MySQL `ChangeDatabase` | 未开始 | 待记录 |
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
