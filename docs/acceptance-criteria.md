# SDK 验收范围与标准

版本：2026-10-29。与[功能矩阵](obconnector-dotnet-progress.md)和[缺失矩阵](obconnector-dotnet-completeness.md)一起使用。
验收对象是某个功能在**指定部署组合**上的行为，不是“连接成功”或“测试数很多”。

## 支持声明的单位

每份报告至少绑定：源码commit、官方/Java参考版本、OceanBase真实产品版本、租户模式、
直连或ODP及其版本、标准/压缩/OB20、TLS策略/认证plugin、客户端OS/runtime/所选包资产、
相关选项、case ID、pass/fail/skip/deferred及原因、cleanup结果和脱敏报告路径。
不在文档/日志中记录真实地址、租户/账号/密码、证书私钥、SQL参数或完整连接串。

证据逐层累积，不能跨层代替：

| 层 | 工程/入口 | 证明什么 | 不能证明什么 |
| --- | --- | --- | --- |
| 源码 | scope-audit中O/J依据及产品实现 | 需求适用性、实际分支、拒绝点 | 实库/平台可用 |
| 默认回归 | UnitTests、DependencyInjection.Tests | 解析、协议/资源状态、安全边界及DI | 真实租户功能 |
| 公开DLL消费者 | ConsumerTests、NetStandard sample | 真实API/资产隔离、合成TCP协议 | 独立运行所有TFM、实库、真实TLS |
| 本地安全 | 独立SecurityTests | OS证书/密钥与合成TLS/mTLS分支 | 企业CA、真实TLS/auth/OBProxy |
| 完整实库 | IntegrationTests显式profiles | 选定模式/部署的公开API完整组 | 未选profile/跳过项与其他部署 |
| 验收报告 | Acceptance十五组+显式专项profile | 固定case结果/环境/清理，可复现摘要 | 替代完整Integration或凭exit0称全通过 |
| 包发布 | Verify-Packages.ps1及-VerifyLive | nupkg/依赖/hash/资产选择/消费者与所选实库 | 所有OS/API/ORM兼容 |

## 各功能的最小通过标准

| 能力ID | 必须检查的行为 | 测试入口/覆盖归属 |
| --- | --- | --- |
| C01–C04/C16 | 严格配置/范围/脱敏、两模式真实身份/版本、ExpectedMode拒绝、Open/重开、取消、factory、权限下ChangeDatabase | connection；Integration Connections |
| C05–C09 | 可信链+正确主机成功；错名/不可信/过期/缺客户端证书失败且不降级；凭据过期/轮换/脱敏 | SecurityTests+真实TLS/plugin profiles；当前E01开放 |
| C10–C14 | 容量上限/等待取消/预热/清池；同物理会话reset与替换分别证明；旧reader/LOB/cursor/XA晚释放不影响新租约；路由隔离 | connection/faults；Integration Connections（PoolIsolation/PoolResourceIsolation）及发现专项；真实HA另验 |
| Q01–Q07 | text/prepare/回退/多语句分开；参数值/NULL/方向/类型、引号/q引用、失败不误执行、OUT原子发布 | crud/routines；Integration Commands/Oracle/Types及对应多语句专项 |
| Q08–Q13/Q23 | 真实列metadata、全部行/多结果、半读关闭、typed getter/越界、cursor分页/释放；SchemaOnly不执行；KeyInfo实际隐藏键 | reader/schema/oracle；Integration Readers/Commands/Oracle |
| Q14–Q19 | 已执行前缀、失败条目/后缀、受影响行/输出、事务/触发器/默认值、流所有权/分块/限额/取消、cache失效 | batch/streams/oracle；Integration Batches/Streams/Oracle及相应专项 |
| T01–T12/T19/T20 | 类型推断/显式输入、NULL、边界/溢出、精确值、列身份、存储后独立读取、text和binary结果、编码坏字节 | types及对应Integration类型/字符集组；不能只SELECT参数或只看列名 |
| T13–T15 | named/anonymous、输入/输出、普通列/PL、嵌套/NULL分别覆盖；真实大LOB读写/失效/DDL/reset/旧资源 | lob/complex/oracle；Integration Oracle/Connections资源专项 |
| X01–X04/Q22 | 提交/回滚独立连接可见性、savepoint、DDL边界、XA recover/记录决定后恢复；未知结果独立查账且无重放 | transactions/xa/faults；Integration Transactions/Xa/Faults及ambient专项 |
| P01–P05 | 实际协商与双向帧、分片/序号/checksum/截断/取消、拒绝组合；trace服务器控制和导出分开 | diagnostics与ob20TraceProfile；真实压缩/OB20当前E10开放 |
| M01–M03 | 权限/目录准确性、未授权错误不伪造；无敏感标签/高基数；监听卸载/开销/生命周期 | schema/diagnostics；对应Unit/Consumer/Integration |
| R01 | 五资产构建、基线无现代API泄漏、实际runtime包消费；非Windows必须有对应记录 | eng/Verify-Packages.ps1、Consumer；-VerifyLive另验 |

每个支持输入至少有：正常值、typed NULL、边界、错误值/错误模式、同步/异步（若提供）、
存储/输出后独立核对及失败后的会话状态。涉及模式差异必须各自执行，不能以Oracle通过代替MySQL。
输出、cursor、LOB、复杂值还必须验证所有权和释放；不支持形状须在发送前拒绝并保留连接可用（已发送故障另循unknown契约）。
新类型/选项须有O/J或独立wire依据、公共API与默认/退出行为、负向测试、README契约和真实验收计划后才纳入矩阵。

## 当前验收基线与可复现门槛

以下是本次整理确定的最低检查基线，不是已通过结果，也不扩展到原生数据库功能全集。

| 维度 | 要求 |
| --- | --- |
| 服务端 | 现有候选版本4.4.2.2，两模式各自独立；V4.3.5仅作为文档参考，其他版本必须另建报告 |
| 基本profile | 两模式直连、标准协议、UTF8基线；当前TLS Disabled结果只标实验环境，不能覆盖默认VerifyFull |
| 功能profile | 声明支持的prepare/locator/pool/cache/batch/LOCAL INFILE等必须启用对应profile；不启用只证明默认路径 |
| 运行时 | 声明支持的OS/runtime分别执行；五资产各有编译和公开包消费者。netstandard2.0/2.1须记录实际host，现代资产须各记录net8/9/10；Framework消费单列Windows CLR版本 |
| 安全/代理 | 若声明TLS/mTLS、ODP、OB20/trace/压缩可用，必须补对应真实端点，零失败且没有因缺配置而跳过；当前这些门槛保持开放 |
| 模式/方向 | 适用的sync/async、text/server-prepare、输入/输出、普通列/PL、NULL/边界分别通过；回退/多语句不可替代普通prepare |
| 功能结果 | 完整Integration所有适用组零失败，Acceptance每个启用case通过且cleanup零失败；配置skip/deferred单列为未验收 |

池和资源至少增加一份可复现的压力报告：每模式16个并发调用者、私有池MaxPoolSize=4、
不少于10000次借还/真实查询，验证峰值物理会话不超过4、等待取消不泄漏、所有任务在配置预算内结束。
另做不少于60分钟持续运行，包含清池/归还/Dispose交错、reader半读、错误/取消及该模式资源；
停止并释放后，驱动自有会话、prepared ID、locator/cursor、后台任务必须归零（保留的有界缓存须单列），
独立数据库连接确认自有事务/分支/对象清理。记录句柄/线程/内存曲线和实际命令，
有增长则解释并复现，不能仅凭最后一条SELECT成功关闭G10。这是最低正确性/持续性基线，不是生产容量承诺。

故障专项至少分别记录已有16个网络unknown case、6个受控路由case、4个XA进程恢复case，
并将真实节点/ODP/协调器其他崩溃点作为独立未验收项。通过受控故障不自动关闭真实HA。
性能优化至少保留5次相同工作负载的基线/优化对照及分位延迟、吞吐、资源和失败率；
当前没有硬件无关的吞吐SLO，不能因此声称性能已达标或实现了Java性能等价。

## 执行与判定

以下命令从仓库根目录运行，默认门禁不访问真实数据库：

```powershell
dotnet build OceanBaseConnector.slnx -c Release
dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-build
dotnet test tests/OceanBaseConnector.DependencyInjection.Tests -c Release --no-build
dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-build
dotnet run --project tests/OceanBaseConnector.Acceptance -c Release --no-build -- --list
```

真实验证按[Integration说明](../tests/OceanBaseConnector.IntegrationTests/README.md)设置私有profiles、
`OB_REQUIRE_INTEGRATION=1`和过滤范围；主张“双模式完整实库”须执行两模式全部适用组，不能仅连接或专项。
Acceptance按[runner说明](../tests/OceanBaseConnector.Acceptance/README.md)选择profile：普通十五组之外，
networkFaultProfile、topologyFaultProfile、xaCrashProfile、ob20TraceProfile、localInfileProfile、
zeroDateProfile各自要求前提，不能用普通组成功代替。

- 声明范围内必须零失败，所有自建表/分支/日志清理成功。任何失败/cleanup失败均未通过。
- **skip/deferred不是通过。** Acceptance exit0允许存在跳过；审阅每个原因。只有模式确实不适用的用例可从该模式范围排除。
- 因未开TLS/未授权/缺真实OB20等跳过，保持“欠验收/阻塞”；不能改成“不需要支持”。
- 源码/fixture/中继注入/直连分别标注。单上游中继不是HA，注入1295不是原生1295，自签名TLS不是可信企业部署。
- 完成某模块须实现/测试/文档一致、无未处理重要走查问题；完成整个SDK还须支持声明覆盖的模式/部署/版本/平台全部闭合。
- 性能优化须有同硬件/数据/并发/协议的基线与结果，记录吞吐/分位延迟/资源/失败率。没有测量不能声称更快。

SecurityTests独立于主slnx/默认CI；真实环境未开TLS时保留deferred，不强行执行成功。
当前Windows/Linux CI配置只证明其实际执行的构建/默认回归；tag消费者/Windows包门禁不等于其他平台实库。
历史未通过项见[兼容性](compatibility.md)，本次没有通过补写验收标准关闭它们。
