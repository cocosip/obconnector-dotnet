# OceanBase Connector for .NET 开发进度台账

基线日期：2026-10-17。依据：[架构设计](obconnector-dotnet-architecture-design.md)和[开发方案](obconnector-dotnet-development-plan.md)。当前仓库只有文档，没有 .NET 工程或驱动实现；下表中的任务均为计划，不能将文档完成等同于功能完成。

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
| G1 协议基础 A | W1-W5 | 0/7 | 未开始 | A01-A07 + 双模式/OBProxy TLS 实测 |
| G2 ADO.NET 核心 B | W6-W17 | 0/9 | 未开始 | B01-B09 + 四目标消费、低精度 provider-specific 数值和双模式 CRUD/事务/故障实测 |
| G3 生产连接 C | W18-W34 | 0/8 | 未开始 | C01-C08 + 池化白名单/精确类型/MySQL `ChangeDatabase`/内存故障实测 |
| G4 发布候选 R | W35-W37 | 0/2 | 未开始 | R01-R02 + 包/许可证/兼容矩阵 |
| D 独立扩展 | G4 后按需求 | 0/15 | 未开始 | 每项独立功能、版本和模式证据；不计入 G4 |

## A. 协议基础任务

| ID | 计划窗口 | 可验收产物 | 状态 | 负责人/完成证据/实际完成日 |
| --- | --- | --- | --- | --- |
| A01 | W1 | 四目标解决方案、单测/集成/消费入口 | 完成 | 2026-10-18；提交 `✨ feat(obconnector-dotnet): scaffold A01 multi-target solution, test entries and CI`；`dotnet build OceanBaseConnector.sln -c Release` 四目标 0 警告 0 错误；UnitTests 11×3 TFM 通过、ConsumerTests 3×3 TFM 通过（含 MetadataReader 验证 netstandard2.0 资产无现代 API 泄漏）、IntegrationTests 1×3 TFM 按环境门槛跳过、`dotnet pack` 成功；CI 入口 `.github/workflows/ci.yml`（Windows/Linux 矩阵，GitHub 上首次运行待推送后验证）；夹具格式见 `tests/fixtures/README.md` + 样例 `err-packet-handshake-failure.json` |
| A02 | W1-W2 | 连接串 builder、不可变选项、脱敏 | 完成 | 2026-10-18；`OceanBaseConnectionStringBuilder` + internal `OceanBaseConnectionOptions`（`Build()` 冻结快照，测试证实后续 builder 变更不影响快照）。公开键：`Server`(同义词 `Host`)、`Port`(1-65535，默认 2881)、`User ID`(`Uid`)、`Password`(`Pwd`)、`Database`、`Connection Timeout`(默认 15)、`Command Timeout`(默认 30)、`TLS Mode`(`VerifyFull` 默认/`Required`/`Disabled`，仅名称匹配、拒绝数字串)、`TLS Server Name`、`Persist Security Info`(默认 false)、`ExpectedMode`(`MySQL`/`Oracle`)、`Pooling`(默认 false)；未知键、同义词冲突（`Server`+`Host` 等）、非法端口/超时/布尔/枚举均抛 `ArgumentException`；`KEY=` 空值遵循 ADO.NET 语义移除键、`Build()` 校验必填 `Server`/`User ID`；密码可经 `ToSanitizedConnectionString(true)` 序列化、默认脱敏，异常消息经测试不含密码/冲突值。单测 62×3 TFM 通过（`ConnectionStringBuilderTests`），四目标 Release 构建 0 警告 0 错误 |
| A03 | W1-W3 | 标准帧/序号/分片/越界夹具 | 完成 | 2026-10-18；`Internal/Protocol/{ByteCursor,PacketChannel,ProtocolException}`。帧规则：3 字节长度 + 1 字节序号；负载 ≥0xFFFFFF 分片、整倍数以空帧终止；`BeginCommand()` 命令边界双序号归零；序号按帧递增、模 256 回绕（257 帧用例验证）；单帧与续帧链均在分配前执行逻辑包上限校验（默认 64MB，可注入更小值验证拒绝）；`ByteCursor` 全部读取经无符号比较防负数/溢出绕过。夹具：`framed-err-packet.json`（帧化 ERR：长度 72、序号 0、错误码 1045、SQLSTATE 28000）由加载器消费并用游标解析断言。测试 98×3 TFM 通过：单包/多分片/空终止帧/错序/截断（含 5 种随机种子×32 截断点）/上限拒绝；`SpyStream` 断言异步读写路径同步调用计数为 0。同步与异步共享 `TakeHeader`/`PrepareHeader`/上限校验，命令解析未混入帧层 |
| A04 | W2-W4 | TCP、SSLRequest、TLS 验证与拒绝场景 | 完成 | 2026-10-18；`Internal/Transport/{TcpTransport,TlsUpgrade,TlsHandshakeFlow}` + `Internal/Protocol/{CapabilityFlags,SslRequestPacket}` + `PacketChannel.AlignSendSequenceToReceived/ReplaceUnderlyingStream`。TCP 同步（BeginConnect+WaitOne 超时）与异步（现代目标 ct 重载 / ns2.0 关闭 socket 中止）互不包装；拒绝/超时/取消均释放 socket，异常仅含端点信息。SSLRequest 固定 32 字节（能力 LE32 + 16MB + utf8mb4(45) + 23 零填充），字节级断言。TLS：默认 VerifyFull 走 OS 信任链+主机名校验（SslStream 无自定义回调）；Required 显式接受任意证书；Disabled 不发 SSLRequest 不升级；失败即销毁传输，无降级路径。环回测试（运行时自签证书）覆盖严格顺序（初始握手→CLIENT_SSL 检查→SSLRequest 序号 1→TLS→零认证字节）、VerifyFull 拒绝不受信证书（服务端 TLS 流解密后 0 字节）、服务端无 CLIENT_SSL 时不发任何字节、Disabled 零字节、同步/异步路径一致；`TLS Server Name` 优先于端点主机名（IP+显式 DNS 名称用例）。私有 CA/跨平台/真实主机名不匹配留待 A07 实测记录。测试 113×3 TFM 通过 |
| A05 | W3-W4 | 认证、模式位、能力协商与夹具 | 未开始 | 待记录 |
| A06 | W3-W5 | 物理会话状态、独占和异常映射 | 未开始 | 待记录 |
| A07 | W5 | 双模式直连/OBProxy 真实连接门槛 | 未开始 | 待记录 |

## B. ADO.NET 核心任务

| ID | 计划窗口 | 可验收产物 | 状态 | 负责人/完成证据/实际完成日 |
| --- | --- | --- | --- | --- |
| B01 | W6-W7 | 标准 provider 对象和 factory；`ChangeDatabase` 暂拒绝 | 未开始 | 待记录 |
| B02 | W7-W9 | 文本 SQL、OK/ERR、多结果和更新计数 | 未开始 | 待记录 |
| B03 | W8-W10 | Oracle 命名占位符词法器及各类 `q` 字面量；MySQL 参数数由服务端确定 | 未开始 | 待记录 |
| B04 | W9-W12 | 服务端 prepare/execute、二进制结果和 statement ID | 未开始 | 待记录 |
| B05 | W10-W13 | 基础值、低精度 `OceanBaseDecimal`、Oracle DATE 与未支持类型拒绝 | 未开始 | 待记录 |
| B06 | W11-W14 | 逐行 reader、字段元数据及低精度 provider-specific 访问 | 未开始 | 待记录 |
| B07 | W13-W15 | 本地事务及环境事务拒绝 | 未开始 | 待记录 |
| B08 | W14-W16 | 取消、超时、结果未知和故障注入 | 未开始 | 待记录 |
| B09 | W17 | 四目标消费、低精度 provider-specific 数值和双模式 G2 验收 | 未开始 | 待记录 |

## C. 生产连接任务

| ID | 计划窗口 | 可验收产物 | 状态 | 负责人/完成证据/实际完成日 |
| --- | --- | --- | --- | --- |
| C01 | W18-W23 | 可选池、完整重置和版本白名单 | 未开始 | 待记录 |
| C02 | W18-W21 | 多地址建连与发包后不重试 | 未开始 | 待记录 |
| C03 | W20-W25 | 宽精度、纳秒、时区和整列稳定的零日期 provider-specific 值 | 未开始 | 待记录 |
| C04 | W23-W28 | SequentialAccess、大值与服务端游标 | 未开始 | 待记录 |
| C05 | W25-W29 | `GetSchema` 基础集合及异步入口、MySQL `ChangeDatabase` | 未开始 | 待记录 |
| C06 | W27-W31 | 现代 `DbDataSource` 和逐条 `DbBatch` | 未开始 | 待记录 |
| C07 | W29-W32 | 诊断、脱敏和性能基线 | 未开始 | 待记录 |
| C08 | W33-W34 | 生产版本/模式矩阵与 G3 审核 | 未开始 | 待记录 |

## 发布与可选扩展

| ID | 计划窗口 | 可验收产物 | 状态 | 负责人/完成证据/实际完成日 |
| --- | --- | --- | --- | --- |
| R01 | W35-W36 | NuGet 包、许可/包名审查、四目标消费 | 未开始 | 待记录 |
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
