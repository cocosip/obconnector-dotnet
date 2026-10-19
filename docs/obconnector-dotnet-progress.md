# OceanBase Connector for .NET 开发进度台账

基线日期：2026-10-17；当前进度更新：2026-10-19。依据：[架构设计](obconnector-dotnet-architecture-design.md)和[开发方案](obconnector-dotnet-development-plan.md)。

## 当前开发进度

当前已实现 B01–B09 的本地核心能力和 C02–C07 的本地功能；R01/R02 包消费及运行时验证工具已落地，`7a70bd6` 增补 A04 静默 TLS 握手中断回归，`da03fe6`/`31b2c7a` 完成 D14 YEAR/D13 BIT 本地实现。`808dfed` 完成 D12 MySQL JSON、`8a21dbd` 完成 D11 Oracle INTERVAL 本地实现；D10 NCHAR/NVARCHAR2 本地实现与验证见末尾。连接池 C01 尚未实现，A05 的公共 MySQL 模式判定尚未完成，D02已接入显式Oracle过程与标量OUT/RETURNING，本地收尾记录见末尾，D03 REF CURSOR输出已本地接入（验证见末尾），D04已开始内部locator解析，D05–D09尚未开发；D01 的OB20报文/校验、能力协商、流式传输和完整链路追踪已有本地实现，验证与限制见末尾。D15 的 SHA-2/RSA 路径已有本地实现，其他插件及真实验收仍开放。真实 OceanBase/OBProxy 互通没有验收证据，项目仍为实验版本 `0.1.0-alpha`。

| 范围 | 当前已实现及本地验证 | 尚未完成 |
| --- | --- | --- |
| A01/A02/A03/A06 | 工程、连接配置、标准报文/序号/分片、物理会话状态与独占生命周期已完成；后续命令、statement、reader资源管理已接入 | 真实服务端互通另随阶段门槛验收 |
| A04 | TCP、SSLRequest、TLS升级/拒绝、同步/异步取消及Windows环回已实现；同步建连借用等待句柄缺陷已修复并有压力回归 | 私有CA成功链、真实证书名及跨平台验收 |
| A05/A07 | 安全认证、Oracle模式位、能力协商、认证错误/脱敏及真实环境测试入口已实现 | 可信MySQL模式判定及版本/插件白名单；双模式直连/OBProxy真实验收。当前公共MySQL连接仍被拒绝 |
| B01–B09 | 统一provider/factory、现代数据源/DI/日志；文本与预处理命令、命名绑定、逐行/多结果reader、字段元数据、基础/provider-specific类型、本地事务、取消/超时/结果未知及公共API审核已完成本地实现和走查 | 双模式真实CRUD、参数/类型往返、事务、故障及跨平台验收；本地夹具不能证明服务端兼容 |
| C02–C04 | 多地址仅建连前切换；宽精度数值、纳秒/TZ/LTZ/零日期；SequentialAccess字段流及服务端游标已实现并有本地回归 | 真实模式/版本/OBProxy的大值、游标、精确值、资源和性能验收 |
| C05–C07 | MySQL ChangeDatabase、首批四个GetSchema集合/现代异步入口；逐条DbBatch及数据源入口；ActivitySource/Meter、脱敏及Windows协议性能基线已实现并走查 | 真实目录权限/字典、切库、批次及诊断验收；池借还性能尚无实现基础 |
| C01/C08 | 未开放Pooling=true，公开入口明确拒绝；生产验收缺口已列明 | 连接池及完整重置实现、可信版本/模式白名单、生产矩阵与G3验收 |
| R01/R02 | 同版本双包、五资产实际包消费、故障/回退README、8/9完整本地Unit/DI及Windows Framework基础包消费工具已完成 | 许可证/来源/包名审核、正式Framework支持决定、真实兼容矩阵和最终发布审核 |
| D14 | 四位 MySQL YEAR 本地实现，验证记录见末尾；真实验收开放 | 真实 YEAR 列往返、版本/OBProxy矩阵 |
| D13 | MySQL BIT(1–64) 本地实现，验证记录见末尾；真实验收开放 | 真实 BIT 列往返、版本/OBProxy矩阵 |
| D12 | MySQL JSON 文本/预处理读写、元数据及字段流本地实现，记录见末尾 | 真实 JSON 列往返、语法验证和版本/OBProxy矩阵 |
| D11 | Oracle INTERVAL 两种精确值、文本/预处理读写及元数据本地实现，记录见末尾 | 真实列往返、版本/OBProxy 矩阵 |
| D10 | Oracle NCHAR/NVARCHAR2 独立会话字符集、文本/预处理读写、顺序字符流及元数据本地实现，记录见末尾 | 真实列往返、版本/OBProxy 矩阵 |
| D15 | SHA-2 fast/TLS、固定RSA及显式公钥获取本地实现，验证记录见末尾 | 其他插件、私有CA成功链及两模式实库矩阵 |
| D01 | OB20帧/校验、显式协商与标准回退、流式传输及脱敏完整链路追踪已有本地实现，记录见末尾 | OB20经TLS环回、真实采样/SHOW TRACE及版本/代理矩阵；关闭不刷新末条待发记录 |
| D02 | 显式Oracle过程/函数、标量OUT/InputOutput/RETURNING、内部结果隐藏、输出原子发布及事务/statement清理已有本地实现，见末尾 | 真实版本/OBProxy互通；多行/复杂输出及REF CURSOR分别归D06/D05/D03，本路径独立Prepare/FetchSize不支持 |
| D03 | REF CURSOR输出对象、原会话/事务代次绑定、分页reader和父statement所有权已本地实现，见末尾 | 实库/OBProxy矩阵；游标输入/输入输出及普通结果列cursor值不支持 |
| D04 | 内部V1/V2 locator解析已实施，验证记录见末尾；公开LOB入口尚未接入 | 原会话/事务绑定、类型/参数/输出、分块流及真实资源语义继续实施 |
| D05–D09 | 尚未开发 | 复杂类型、批量优化、客户端回退、拓扑/XA继续逐项实施 |

工程现状：核心目标为 `netstandard2.0;netstandard2.1;net8.0;net9.0;net10.0`，DI目标为 `net8.0;net9.0;net10.0`；日常测试工程统一声明 `net10.0`，使用SDK10和slnx。包依赖集中管理，两个Standard及net8使用8.x Microsoft通用库，net9使用9.x，net10使用10.x；核心/DI包版本统一取根Version。

上一轮 Windows 验证结果如下（历史运行时快照；本轮新结果见后文“本轮本地范围复核”）：

| 实际运行时 | Unit通过 | Consumer通过 | DI通过 | 失败/跳过 |
| --- | --- | --- | --- | --- |
| .NET 8.0.31 | 1206 | 实际包消费另验 | 8 | 0/0 |
| .NET 9.0.20 | 1206 | 实际包消费另验 | 8 | 0/0 |
| .NET 10.0.12 | 1206 | 152 | 8 | 0/0 |

报告为 `TestResults/runtime8-unit-final.trx`、`runtime8-di.trx`、`runtime9-unit.trx`、`runtime9-di.trx`及 `runtime10-{unit,consumer,di}-final.trx`。最新Release slnx核心五目标/DI三目标编译为0警告、0错误（`TestResults/framework-slnx-build-final.log`）；默认五资产和可选Framework包验证均通过（`framework-default-package-final.log`、`framework-package-final.log`）。Framework仅证明net48目标在本机4.8.1上的基础Standard2.0包消费，不代表完整SQL/TLS或原版4.8支持。

最近已提交：`d8d7072`修复同步TCP等待句柄；`0e8229b`补齐8/9完整本地回归并修复测试兼容；`1aeba69`新增可选Windows Framework包消费及生成工程重建修复。已报告走查问题的修复和红绿证据见后文。

后文阶段计数按正式验收完成项统计，B/C显示0项通过关口不代表没有实现；各模块落地时的测试数量为历史快照，最新全量结果以本轮复核表为准。

## 本轮本地范围复核（2026-10-19）

按“暂不部署其他电脑或数据库服务端”的要求，逐项核对开发方案所有未勾选项及源码中的未支持入口。本轮只补齐 A04 的本地 TLS 握手中断验证；没有放行缺少真实依据的 MySQL 模式或池化，也没有将延期扩展写成已支持。

新增 `PhysicalSessionTests.SilentTlsHandshakeIsInterruptedAndClosesTransport` 五项回归：服务端先接收 SSLRequest 和 TLS 握手记录，再保持静默；同步/异步总连接超时、同步/异步关闭及异步调用方取消均须结束建连，对端须在测试主动清理前观察到断开。测试还校验最终状态、调用方 token、无认证后会话信息及不可取得命令租约。产品已有实现通过，无需改动产品代码。独立只读走查核对了 PhysicalSession、TLS 升级及环回资源路径，未发现需修复问题。

私有 CA 测试尝试触发 Windows 安装/删除信任根的人工确认，用户要求停止后，已终止测试进程、撤销全部相关代码并清除唯一残留的专用测试根证书，重新枚举确认无残留。该尝试的通过/失败/中止报告均不作为本轮完成证据；今后默认单元测试不安装或删除信任根。可信私有 CA 成功链和名称验证继续待专用环境验收。

本轮实际环境为 Windows、SDK 10.0.402、.NET 10.0.13。已重新执行：

| 检查 | 本轮结果 | 证据 |
| --- | --- | --- |
| Release slnx，核心五目标/DI三目标 | 0 警告、0 错误 | `dotnet build OceanBaseConnector.slnx -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false`；`TestResults/local-scope-build-reviewed.log` |
| A04 静默 TLS 中断定向回归 | 5 通过，0 失败/跳过 | `TestResults/tls-interruption-final/*.trx` |
| 完整 Unit | 1211 通过，0 失败/跳过 | `TestResults/tls-interruption-unit-full/*.trx` |
| 五资产 Consumer | 152 通过，0 失败/跳过 | `TestResults/tls-interruption-consumer/*.trx` |
| DI | 8 通过，0 失败/跳过 | `TestResults/tls-interruption-di/*.trx` |
| 双包及五目标实际包消费 | 两包内容/依赖/哈希与全部消费通过；现代资产实际运行时 8.0.32/9.0.21/10.0.13，Standard 资产宿主 10.0.13 | `TestResults/local-scope-package-verification.log`；`TestResults/package-validation/c33a1bda8cc848fc982dffcddcee342d/` |

测试命令为 `dotnet test tests/<对应工程>/<对应工程>.csproj -c Release --no-build --no-restore -m:1 -nr:false --logger trx --results-directory TestResults/<上表目录>`；定向回归另加 `--filter FullyQualifiedName~SilentTlsHandshakeIsInterruptedAndClosesTransport`。包命令为 `dotnet pack OceanBaseConnector.slnx -c Release --no-build --no-restore -m:1 -nr:false -o TestResults/local-scope-packages` 和 `pwsh -NoProfile -File eng/Verify-Packages.ps1 -PackageDirectory TestResults/local-scope-packages`。正常 Windows TLS/私钥权限下执行，不更改驱动证书验证策略。首次沙箱还原/TLS权限失败、测试夹具试验和并发构建文件锁失败均不计为通过；还原使用仓库内 `TestResults/nuget-packages` 及 `NUGET_SCRATCH=TestResults/nuget-scratch`，最终恢复固定的 xunit.runner.visualstudio 2.8.2，不采用离线近似版本。未重新执行历史 .NET 8/9 完整 Unit/DI 或 Framework 矩阵；上述实际8/9包样例只补基础消费证据。

此前本地范围复核将 D 阶段整体归为环境阻塞，判断过宽：用户暂缓的是异机/数据库部署联调，可独立实现和本地测试的扩展仍须继续。下表保留真实环境和发布门槛，D 项改为逐项推进本地实现；项目整体仍未完成，原阶段验收状态保持开放。

| 剩余项 | 本轮处理及原因 |
| --- | --- |
| A04 私有 CA、非 Windows，A07/B09/C08/R02 真实互通与故障矩阵 | 需人工信任配置、其他电脑或 OceanBase/OBProxy 环境，暂缓 |
| A05 公共 MySQL 模式判定 | 缺少真实版本/模式/插件证据，不能用缺失 Oracle 位猜测 MySQL，继续安全拒绝 |
| C01 池与完整重置、C07 池性能 | 缺少服务端完整重置与隔离证明，继续拒绝 Pooling=true |
| D01–D15 独立扩展 | 从 D14 本地编解码、参数、元数据开始推进；其余本地可实现部分继续评估，不以 G4 或环境缺失整体搁置；目标服务端/模式往返和拓扑/协调器证明单独延期 |
| G0 产品决定、R01 许可证/来源/包名与正式 Framework 支持 | 需要维护者决定或发布审查，不能由本地编译/夹具替代；不发布或推送 |

## 中断走查问题核销（2026-10-19）

首次核销已将原走查记录与当时 `0f43bf2` 源码、历史修复提交和实际执行的回归逐项核对；再次续接已找回 B01–B05 原始走查报告，并补齐 B01/B02/B04 及 C06/C07 的修复索引。以下问题均已有修复提交，本节续接复核不新增产品代码。

| 已报告问题 | 修复提交 | 当前回归证据 |
| --- | --- | --- |
| Opened/Closed 日志回调关闭或重开连接，导致状态事件倒序 | `7af799f` | ProviderConnectionTests：OpenedLoggerClosingConnectionPreservesStateEventOrder、ClosedLoggerReopeningConnectionPreservesStateEventOrder |
| 协商弃用 EOF 后仍要求旧 EOF；超大 CommandTimeout 取得租约后抛错并遗留 Busy | `a142f70` | TextCommandTests：NegotiatedDeprecatedEofOmitsMetadataEofAndUsesOkTerminator、UnrepresentableTimeoutDoesNotAcquireOrStrandTheSession |
| 终态 ERR 被失败日志回调触发的取消覆盖；Scalar 未跳过前置更新计数 | `a142f70` | TextCommandTests：ServerErrorWinsOverCancellationFromFailureLogger、ScalarSkipsInitialUpdateCountsToReachFirstResultSet |
| 旧预处理清理超时回调可能破坏新命令；netstandard2.1 缺少异步 prepare/清理覆盖 | `a431ee8` | PhysicalSessionTests：ExpiredLeaseCancellationCannotBreakSubsequentCommand；PreparedCommandTests：PrepareAsyncWaitsAsynchronouslyForMetadata。取消回调绑定原租约，完成后的旧租约不能破坏新操作 |
| Decimal 仅声明 Precision 时误按 Scale=0；畸形 TIME 天数参与溢出运算 | `b772c0f` | ValueCodecTests：DecimalPrecisionOnlyInfersScaleWhileExplicitZeroRequiresExactInteger、MalformedTimeDayCountIsRejectedBeforeTickArithmetic |
| netstandard2.1 异步事务/连接/reader 清理被错误条件编译排除 | `e6b50d9` | TransactionConsumerTests：AsyncCleanupReturnsBeforeDelayedRollbackAndWaitsForRestoration，含 Standard2.1 的真实异步延迟回滚 |
| 预读字符/耗尽字段流绕过命令取消和关闭；完整终结包误判结果未知；字段 token 未进入命令预算 | `230d596` | SequentialAccessTests：缓存字段生命周期、完整终态与取消、字段独立 token 回归 |
| 游标 ERR 先释放租约再销毁会话；二进制行冒充 FE-OK 确认；分页/初始空结果遗漏或重复处理会话报告；部分 fetch 写入不响应 reader token | `b6881a6` | CursorTests：错误日志重入、二进制伪确认、逐页及空结果会话报告、部分写入取消回归 |
| 目录整数转换先舍入微小小数，导致非法值误通过整数校验 | `fa37cd5` | ConnectionSchemaEdgeTests：ConnectionSchemaNeverRoundsTinyFractionsBeforeIntegerValidation，覆盖长小数字符串和浮点表示 |
| 旧 reader 污染新批次计数或关闭重开后的连接；字段失败遗留批次预约；取消异常丢失调用方 token | `f93c15e` | BatchTests：ClosedBatchReaderCountsAreAnIndependentSnapshotAcrossReuse；BatchFaultTests：OldCloseConnectionReaderCannotCloseReopenedSession、BatchFieldStreamFailureReleasesOperationWithoutNeedingExtraClose、BatchPartialSendCancellationRetainsPrefixAndUnknownAttribution、BatchCancellationBetweenStatementsPreservesOriginalCallerToken、BatchNextResultPartialSendRetainsItsOwnReaderToken |
| 诊断初始化回调抛错导致类型初始化失败；Activity.CurrentChanged 抛错逃逸并遗留 Busy 租约 | `e371ff4` | DiagnosticsConsumerTests：EveryAssetPublishesSafeDiagnosticsWithoutInitializationPoisoning，五资产初始化异常隔离；DiagnosticsTests：ThrowingAmbientContextChangeCallbackCannotStrandCommandOwnership，同步/异步并验证后续命令可用 |

历史续接复核（代码基线 `e371ff4`）：已核对原始走查报告、当前修复路径及回归，未发现上述报告中仍未修复的产品缺陷。重新执行 Release slnx 编译，核心五目标/DI 三目标 0 警告、0 错误；完整 Windows Unit1205、Consumer152、DI8 全部通过且零跳过，报告为 `TestResults/review-recovery-{unit,consumer,di}.trx`。解析新报告另确认本表中 20 组具名回归均实际执行并通过，包含 Standard2.1 延迟异步回滚及 C06/C07 走查修复；原顺序读取/游标回归亦在完整单测中执行。完整单测使用正常 Windows TLS 证书存储权限，未修改 TLS 策略。本轮仅补齐核销记录，实库、非 Windows、完整旧运行时兼容矩阵仍未验证，A05/C01 和各真实验收关口保持开放。

首次核销重新执行 `dotnet build OceanBaseConnector.slnx --no-restore -c Release`：核心五目标、DI 三目标，0 警告/0 错误。Windows 完整 Unit1073、Consumer120、DI8 全部通过且零跳过；报告为 `TestResults/interrupted-review-{unit,consumer,di}.trx`，另解析 TRX 确认当时列出的回归实际执行并通过。完整单测沿用已授权的 Windows TLS 测试证书存储访问，未调整 TLS 策略。

当时此核销不改变阶段验收状态（C05后续进展见末尾目录模块记录）：A04 待验证；A05 仍缺可信公开 MySQL 模式判定；C04 待验证；C05 仅数据库切换子项落地，目录集合仍未实现；C01 完整重置白名单仍阻塞。真实数据库、非 Windows 及新的旧运行时验收未执行。

2026-10-19 再次续接复核：`fa37cd5` 已实现 C05 目录集合并修复目录数值舍入问题，C05 当前为待验证。再次核对现有代码、对应回归和已有独立走查结论，未发现上述已报告问题仍待修复；该次复核没有新增产品代码，也没有启动 C06。后续 C06 实现和新发现问题修复见本页末记录。

本次重新编译 Release slnx：核心五目标、DI 三目标，0 警告/0 错误；完整 Windows Unit1117、Consumer128、DI8 全通过且零跳过，报告为 `TestResults/review-resume-{unit,consumer,di}.trx`。解析报告确认上表对应回归均实际执行并通过，包含目录长小数字符串/浮点拒绝、游标失败日志重入/伪确认/状态报告/部分发送取消及字段缓存生命周期。实库、非 Windows 与新的旧运行时验收仍按既有约定暂不执行，阶段关口不变。

## 续接验证新增修复（2026-10-19）

R02 本地交付材料验证期间，完整单元测试首次出现 `DataSourceBatchReaderOwnsConnectionAndSourceDisposalRejectsNewOpens(false)` 同步建连失败，内因为 `TcpTransport.Connect` 的 `WaitOne` 访问已释放 `SafeWaitHandle`；这不是前表已报告问题的漏修。核对 .NET 10 runtime 的 Socket.BeginConnect → TaskToAsyncResult → Task.AsyncWaitHandle 实现，确认快速完成的连接可能共享已完成 Task 的等待句柄，驱动不能释放该借用句柄。修复提交为 `d8d7072`，移除错误的 using 释放，socket、取消注册、超时和错误清理流程保留。

新增真实 loopback 回归 `ConnectSyncRepeatedSuccessDoesNotPoisonLaterConnections`：8 个并发调用方各完成 1024 次同步建连并读取服务端字节。修复前复现同一 ObjectDisposedException（首次 TestResults/tcp-wait-handle-red.trx；修正清理后的 TestResults/tcp-wait-handle-cleanup-red-final.trx），修复后连同传输及原失败用例 14 项通过（最终 TestResults/tcp-wait-handle-cleanup-green-final.trx）。首次全量复跑因该压力回归积累 TIME_WAIT 耗尽 Windows 临时端口而失败，不能计为通过；测试服务端改为收到应用确认字节后以零 linger 和 Close(0) 关闭，避免残留连接影响后续测试，不修改产品 socket 策略；清理只在传播客户端主异常时忽略次生服务端异常，成功路径仍暴露服务端失败。最终验证与独立走查记录见本页末 R02 记录。

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

| 门槛 | 计划窗口 | 验收完成项数 | 关口状态 | 当前实现与验收差距 |
| --- | --- | --- | --- | --- |
| G0 准入 | W0 | 0/5 | 未开始 | 五项决策/环境记录 |
| G1 协议基础 A | W1-W5 | 4/7 | 阻塞 | A01/A02/A03/A06 完成；A04 本地实现完成、待真实/跨平台验收；A05 还缺可信 MySQL 模式判定规则及版本/插件验收；A07 缺环境与可信版本矩阵；G0 未关闭 |
| G2 ADO.NET 核心 B | W6-W17（范围修订后待重估） | 0/9 | 进行中 | B01-B09本地实现/走查完成；实际8/9各Unit1206/DI8、10 Unit1206/Consumer152/DI8及五资产包消费通过；真实双模式/跨平台及完整支持矩阵未验收 |
| G3 生产连接 C | W18-W34 | 0/8 | 进行中 | C01缺可信完整重置白名单而阻塞；C02-C07本地实现完成待真实环境验证，C04真实资源/性能与版本矩阵、C07池性能仍待验收；C08因真实环境证据缺失而阻塞，关口未关闭 |
| G4 发布候选 R | W35-W37 | 0/2 | 进行中 | R01/R02本地双包/五资产、8/9完整本地回归、Framework4.8.1基础消费及交付材料已完成；许可/来源/包名、正式支持范围及真实矩阵未验收 |
| D 独立扩展 | 本地可独立部分继续，实库验收延期 | 0/15 | 进行中 | D11–D14 本地实现和验证见末尾；其余逐项实施；每项真实版本和模式证据仍须补齐，不计入 G4 |

## A. 协议基础任务

| ID | 计划窗口 | 可验收产物 | 状态 | 负责人/完成证据/实际完成日 |
| --- | --- | --- | --- | --- |
| A01 | W1 | 五目标核心、三目标DI、slnx及统一net10测试/集成/消费入口 | 完成 | 2026-10-19当前工程：SDK10、OceanBaseConnector.slnx、CPM及根Version统一；核心含Standard2.0/2.1和net8/9/10，日常测试工程net10。最新Release编译0警告0错误、完整本地回归及双包/五资产消费通过，报告见首页摘要；CI入口.github/workflows/ci.yml，远端及非Windows执行仍无验收证据；夹具见tests/fixtures |
| A02 | W1-W2 | 连接串 builder、不可变选项、脱敏 | 完成 | 2026-10-18；`OceanBaseConnectionStringBuilder` + internal `OceanBaseConnectionOptions`（`Build()` 冻结快照，测试证实后续 builder 变更不影响快照）。公开键：`Server`(同义词 `Host`)、`Port`(1-65535，默认 2881)、`User ID`(`Uid`)、`Password`(`Pwd`)、`Database`、`Connection Timeout`(默认 15)、`Command Timeout`(默认 30)、`TLS Mode`(`VerifyFull` 默认/`Required`/`Disabled`，仅名称匹配、拒绝数字串)、`TLS Server Name`、`Persist Security Info`(默认 false)、`ExpectedMode`(`MySQL`/`Oracle`)、`Pooling`(默认 false)；未知键、同义词冲突（`Server`+`Host` 等）、非法端口/超时/布尔/枚举均抛 `ArgumentException`；`KEY=` 空值遵循 ADO.NET 语义移除键、`Build()` 校验必填 `Server`/`User ID`；密码可经 `ToSanitizedConnectionString(true)` 序列化、默认脱敏，异常消息经测试不含密码/冲突值。单测 62×3 TFM 通过（`ConnectionStringBuilderTests`），四目标 Release 构建 0 警告 0 错误 |
| A03 | W1-W3 | 标准帧/序号/分片/越界夹具 | 完成 | 2026-10-18；`Internal/Protocol/{ByteCursor,PacketChannel,ProtocolException}`。帧规则：3 字节长度 + 1 字节序号；负载 ≥0xFFFFFF 分片、整倍数以空帧终止；`BeginCommand()` 命令边界双序号归零；序号按帧递增、模 256 回绕（257 帧用例验证）；单帧与续帧链均在分配前执行逻辑包上限校验（默认 64MB，可注入更小值验证拒绝）；`ByteCursor` 全部读取经无符号比较防负数/溢出绕过。夹具：`framed-err-packet.json`（帧化 ERR：长度 72、序号 0、错误码 1045、SQLSTATE 28000）由加载器消费并用游标解析断言。测试 98×3 TFM 通过：单包/多分片/空终止帧/错序/截断（含 5 种随机种子×32 截断点）/上限拒绝；`SpyStream` 断言异步读写路径同步调用计数为 0。同步与异步共享 `TakeHeader`/`PrepareHeader`/上限校验，命令解析未混入帧层 |
| A04 | W2-W4 | TCP、SSLRequest、TLS 验证与拒绝场景 | 待验证 | **本地实现/Windows 环回已完成，真实与跨平台验收未完成。** 2026-10-18；`Internal/Transport/{TcpTransport,TlsUpgrade,TlsHandshakeFlow}` + `Internal/Protocol/{CapabilityFlags,SslRequestPacket}` + `PacketChannel 共享双向序号/ReplaceUnderlyingStream`。TCP 同步（BeginConnect+WaitOne 超时）与异步（现代目标 ct 重载 / 两个 Standard 目标关闭 socket 中止）互不包装；拒绝/超时/取消均释放 socket，异常仅含端点信息。SSLRequest 固定 32 字节（能力 LE32 + 16MB + utf8mb4(45) + 23 零填充），字节级断言。TLS：默认 VerifyFull 走 OS 信任链+主机名校验（SslStream 无自定义回调）；Required 显式接受任意证书；Disabled 不发 SSLRequest 不升级；失败即销毁传输，无降级路径。环回测试（运行时自签证书）覆盖严格顺序（初始握手→CLIENT_SSL 检查→SSLRequest 序号 1→TLS→零认证字节）、VerifyFull 拒绝不受信证书（服务端 TLS 流解密后 0 字节）、服务端无 CLIENT_SSL 时不发任何字节、Disabled 零字节、同步/异步路径一致；`TLS Server Name` 优先于端点主机名（IP+显式 DNS 名称用例）。私有 CA/跨平台/真实主机名不匹配留待 A07 实测记录。测试 113×3 TFM 通过 |
| A05 | W3-W4 | 认证、模式位、能力协商与夹具 | 待验证 | **本地认证/Oracle 模式/能力实现及合成验证已完成；可信 MySQL 判定、版本/插件矩阵和真实验收未完成。** Codex，2026-10-18：续接 GLM 原型，修复双向序号、session-track 嵌套长度、切换种子 NUL、无插件 native 回退、TLS 能力/字符集/最大包一致性、长属性 lenenc、错误回显脱敏、长度整数边界；合成夹具及环回通过。无可信版本矩阵时拒绝未设置 Oracle 位的连接，MySQL 真实连接暂未开放；插件与版本白名单仍待 G0/G1 |
| A06 | W3-W5 | 物理会话状态、独占和异常映射 | 完成 | Codex，2026-10-18：PhysicalSession 的 Closed/Connecting/Open/Busy/Broken、独占 lease、未确认响应销毁、同步/异步打开关闭、全过程连接预算与取消、幂等释放、安全异常；环回测试验证截断、关闭不复活、超时、打开后 caller token 取消不投毒。statement/reader资源已由B04/B06接入，后续生命周期修复见走查核销记录 |
| A07 | W5 | 双模式直连/OBProxy 真实连接门槛 | 阻塞 | Codex，2026-10-18：替换永久失败占位符；四项真实测试覆盖四种连接组合、同步/异步打开关闭、模式不符、TLS 名称错误、未知插件。OB_* 环境未配置，日常运行跳过；强制关口缺环境实际失败。还需租户/TLS/插件/确切版本与可信 MySQL 模式判定；G1 不关闭 |

## B. ADO.NET 核心任务

| ID | 计划窗口 | 可验收产物 | 状态 | 负责人/完成证据/实际完成日 |
| --- | --- | --- | --- | --- |
| B01 | W6-W7（待重估） | 标准provider/factory、现代无池数据源、可选DI与基础日志；ChangeDatabase见C05 | 待验证 | 本地实现、五/三目标编译、环回建连、实际8/9/10 DI/消费及双包依赖验证通过；双模式真实建连、完整支持矩阵及非Windows验收待补 |
| B02 | W7-W9 | 文本 SQL、OK/ERR、多结果和更新计数 | 待验证 | COM_QUERY 同步/异步、逐行文本 reader、OK/ERR/EOF/FE OK、多结果和更新计数已有 Windows 环回证据；两模式真实CRUD/匹配行语义、非Windows及完整支持矩阵待验收 |
| B03 | W8-W10 | Oracle 命名占位符词法器及各类 `q` 字面量；MySQL 参数数由服务端确定 | 待验证 | 本地完成词法扫描、名称/顺序映射与发送前绑定校验；基础参数执行已由B04接入，真实参数化集成测试暂缓 |
| B04 | W9-W12 | 服务端 prepare/execute、二进制结果和 statement ID | 待验证 | 本地实现及 Windows 协议/环回、五目标编译、消费/DI 测试通过；双模式实库、非Windows及完整支持矩阵待验收 |
| B05 | W10-W13 | 基础值、低精度 `OceanBaseDecimal`、Oracle DATE 与未支持类型拒绝 | 待验证 | 本地精确数值/日期编解码、公开provider-specific访问、五目标消费及Windows协议/环回通过；完整字段元数据已由B06实现，宽精度及精确时间由C03实现；真实类型往返和完整兼容矩阵未验收 |
| B06 | W11-W14 | 逐行 reader、字段元数据及低精度 provider-specific 访问 | 待验证 | 两套字段schema/独立快照、多结果和生命周期、本地五目标编译及Windows单元/消费/DI通过；ProviderType已由B09审核补齐。双模式真实元数据、非Windows及完整兼容矩阵待验收 |
| B07 | W13-W15 | 本地事务及环境事务拒绝 | 待验证 | Codex，2026-10-18：本地显式绑定、逐模式隔离映射、同步/异步事务与关闭回滚、日志和未知结果已实现；Unit455/Consumer34/DI8、五目标编译及独立复查通过；实库、非Windows及完整支持矩阵待验收 |
| B08 | W14-W16 | 取消、超时、结果未知和故障注入 | 待验证 | Codex，2026-10-19：本地故障矩阵已完成，新增43项单元/5项五资产消费测试；修复内部预算超时误判为调用方取消及 Canceled 日志，终态竞争/部分发送/旧命令重开隔离通过；Unit498/Consumer39/DI8、五目标核心/三目标 DI build、格式/pack/依赖分组校验通过，详见本页末；实库/非Windows及完整支持矩阵待验收 |
| B09 | W17（待重估） | 五目标消费、数据源/DI/日志生命周期、低精度 provider-specific 数值和双模式 G2 验收 | 待验证 | Codex，2026-10-19：本地公共 API 审核、标量 ProviderType/参数映射和 Command 类别修复完成；Unit567/Consumer39/DI8、五/三目标build零警告错误、格式/pack/依赖组通过，Windows核心五资产在8/9/10运行时基础消费通过；详见架构§5和本页末，实库/非Windows/完整旧运行时仍待验收 |

## C. 生产连接任务

| ID | 计划窗口 | 可验收产物 | 状态 | 负责人/完成证据/实际完成日 |
| --- | --- | --- | --- | --- |
| C01 | W18-W23 | 可选池、完整重置和版本白名单 | 阻塞 | 尚未实现连接池及完整重置；缺可信真实版本/模式白名单，Java reset不能代替OceanBase实测；Pooling=true继续拒绝。独立C02-C07已有本地实现 |
| C02 | W18-W21 | 多地址建连与发包后不重试 | 待验证 | 2026-10-19 Codex；Server列表/共享预算/仅TCP建连故障切换/逐端点TLS及认证；Windows Unit627、Consumer49、DI8及五资产8/9/10独立运行时消费通过；实库/非Windows未验收，见末尾记录 |
| C03 | W20-W25 | 宽精度、纳秒、时区和整列稳定的零日期 provider-specific 值 | 待验证 | 2026-10-19 Codex；数值、TIMESTAMP(0..9)、TZ/LTZ及零日期本地实现齐备；Unit895/Consumer91/DI8、五/三目标编译/格式/打包及独立走查通过；真实验收/A05公开MySQL握手仍缺，详见本页末 |
| C04 | W23-W28 | SequentialAccess、大值与服务端游标 | 待验证 | 2026-10-19 Codex；增量报文、顺序字段流和FetchSize只读游标分页本地完成；Unit1037/Consumer111/DI8及核心五/DI三目标编译通过，独立复查缺陷均已修复；真实两模式/OBProxy大值/游标资源及性能矩阵未验收，详见本页末 |
| C05 | W25-W29 | `GetSchema` 基础集合及异步入口、MySQL `ChangeDatabase` | 待验证 | 2026-10-19 Codex；切换及四个目录集合/限制/现代异步本地完成，核心五/DI三编译、Windows完整测试与五资产消费、独立走查通过；字典/权限/版本真实验收暂缓，证据见末尾目录模块记录 |
| C06 | W27-W31 | 逐条 `DbBatch`、factory 和已有数据源的批量入口 | 待验证 | 2026-10-19 Codex；现代三资产逐条执行/会话预约/快照/准备复用/计数/失败归因及数据源入口本地完成；Windows Unit1172、Consumer142、DI8和实际8/9/10样例通过，走查问题已修复；实库及非Windows暂缓，见末尾C06记录 |
| C07 | W29-W32 | 本地 ActivitySource/Meter、脱敏和性能基线 | 待验证 | 2026-10-19 Codex；固定操作/模式/结果/未知标签、会话/命令/事务追踪及两个指标本地完成，监听器异常隔离和独立走查修复通过；Unit1205/Consumer152/DI8、五/三目标编译与实际8/9/10样例通过；Windows协议基线已记录，池/实库/跨平台待验收 |
| C08 | W33-W34 | 生产版本/模式矩阵与 G3 审核 | 阻塞 | 2026-10-19 已核对本地证据与生产验收差距；G3仍0/8，缺真实两模式/OBProxy/TLS/认证/重置白名单矩阵，按约定暂缓，不用协议夹具关闭门槛 |

## 发布与可选扩展

| ID | 计划窗口 | 可验收产物 | 状态 | 负责人/完成证据/实际完成日 |
| --- | --- | --- | --- | --- |
| R01 | W35-W36 | NuGet 包、许可/包名审查、五目标消费 | 进行中 | 2026-10-19 本地包/五资产消费完成，另补Windows Framework4.8.1上net48宿主的Standard2.0基础包消费及绑定重定向；独立走查见末尾，正式Framework支持/许可/来源/包名审查仍开放，不发布 |
| R02 | W36-W37 | 完整回归、发布说明与兼容矩阵 | 进行中 | README本地交付材料及独立走查已完成；Windows实际8.0.31/9.0.20各Unit1206/DI8、默认10.0.12 Unit1206/Consumer152/DI8与五资产包消费通过；最终真实矩阵与发布审查待验收，不关闭候选版门槛 |
| D01 | 本地实现已有；真实验收待环境 | OB20、校验与完整链路追踪 | 待验证 | OB20编解码/校验、协商、流式传输和脱敏追踪已有本地实现；TLS环回、真实采样/SHOW TRACE与版本矩阵仍开放，关闭不刷新末条记录 |
| D02 | 本地先行，实库延期 | Oracle 过程及 OUT/RETURNING | 进行中 | 公开过程及标量OUT/RETURNING已接入，完整回归/走查见末尾；保留标量范围及独立Prepare/FetchSize限制，真实互通开放 |
| D03 | 本地先行，实库延期 | REF CURSOR | 待验证 | REF CURSOR输出、流式分页及所有权本地实现/完整回归/独立走查修复完成，见末尾；真实版本/OBProxy互通开放 |
| D04 | G4 后 | LOB locator 与流式 LOB | 进行中 | 内部locator解析见末尾；公开绑定与流尚未接入 |
| D05 | G4 后 | ARRAY/STRUCT 与类型字典 | 未开始 | 待记录 |
| D06 | G4 后 | bulk/array binding | 未开始 | 待记录 |
| D07 | G4 后 | 按需客户端预处理回退 | 未开始 | 待记录 |
| D08 | G4 后 | 拓扑发现与负载均衡 | 未开始 | 待记录 |
| D09 | G4 后 | 显式 XA | 未开始 | 待记录 |
| D10 | G4 后 | Oracle NCHAR/national charset | 待验证 | Codex，2026-10-19：独立会话编码、两种结果/参数读写、顺序字符流及五资产本地实现，验证见末尾；真实列往返和版本/OBProxy 暂缓 |
| D11 | 本地先行，实库延期 | Oracle INTERVAL | 待验证 | 精确值/编解码/参数/Schema及公共消费本地完成，完整测试与独立走查记录见末尾；真实往返开放 |
| D12 | 本地先行，实库延期 | MySQL JSON | 待验证 | 本地文本/二进制读写、元数据和字段流，验证见末尾；真实 JSON 列往返仍开放 |
| D13 | 本地先行，实库延期 | MySQL BIT | 待验证 | 本地编解码/位宽/参数及测试记录见末尾；真实 BIT 列往返仍开放 |
| D14 | 本地先行，实库延期 | MySQL YEAR | 待验证 | 本地实现/测试/走查记录见末尾；真实 YEAR 列往返仍开放 |
| D15 | 本地先行，实库延期 | 新认证插件 | 进行中 | SHA-2 fast/TLS、固定RSA及显式取钥本地实现；其他插件、私有CA及两模式实库余项开放，验证见末尾 |

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

当时C05仍进行中：MetaDataCollections、DataSourceInformation、Tables、Columns及限制参数/现代GetSchemaAsync尚未实现；后续完成目录模块再转待验证，不将数据库切换子项当作C05整体完成。原始SQL USE的状态报告/追踪不属于本模块已验证契约，当前Database跟踪通过ChangeDatabase改变的数据库，详见架构§4。真实两模式/OBProxy、非Windows与新的旧运行时验收均未执行；隔离消费宿主net10不代替旧运行时完整验收。A04/A05真实关口、A05可信公共MySQL握手、C01完整重置及G0/G1/G3缺口保持开放。

2026-10-19 C05 目录模块：四个首批集合MetaDataCollections、DataSourceInformation、Tables、Columns本地实现完成，列形状、类型、NULL及限制顺序固定在架构§5。集合名不区分大小写，未支持集合明确拒绝；限制用精确绑定值，保留大小写/引号/%/_，不做LIKE展开。MySQL目录映射catalog=TABLE_SCHEMA、schema=NULL，Oracle映射schema=OWNER、catalog=NULL，不存在标识部分的限制返回空表。MySQL使用INFORMATION_SCHEMA，Oracle使用ALL_OBJECTS（TABLE/VIEW）和ALL_TAB_COLUMNS，不混方言或自动回退。DSI保留认证版本原文和已知参数语法，未知能力为DBNull/Unknown。五目标覆写三个同步入口，现代三目标覆写三个异步入口，使用异步执行、读取和释放；静态集合无网络I/O。目录命令绑定当前事务并固定原物理会话，返回前读尽终态，原错误/独占/取消/未知结果不重放契约保留。

首批13项回归全部失败证明基类未实现入口后转绿（TestResults/c05-schema-{red,green}.trx）。最终44项单测覆盖两模式×同步/异步×Tables/Columns、特殊字符安全绑定、静态集合独立快照、缺失标识部分、环境事务/忙会话、当前本地事务、权限1143、取消/总超时/关闭、部分写入、空结果、纯同步/纯异步传输、Oracle NUMBER精确整数、畸形列名/多结果/溢出/小数、限制数组快照和会话重开。独立只读走查发现通用Convert.ToDecimal先舍入微小小数再误判整数，以长小数字符串和double的2项先失败回归修复：文本经过精确DecimalCodec/OceanBaseDecimal转换，仅CLR整数/decimal走通用转换，浮点等表示明确拒绝；独立差异复查无剩余必须修复项。红绿证据TestResults/c05-schema-review-{red,green}.trx，44项定向通过。

五资产8项隔离消费验证公开静态集合、Oracle目录及API暴露，Standard两个资产仅同步、现代三个资产同步/异步，宿主net10；测试不改变A05公共MySQL认证。最终Release slnx核心五/DI三目标0警告0错误；完整Windows Unit1117、Consumer128、DI8通过且0跳过（TestResults/c05-schema-unit-final.trx、c05-schema-consumer-final.trx、c05-schema-di-final.trx）。完整单测沿用已授权的Windows TLS证书存储访问，未降低校验策略。双包TestResults/c05-schema-pack核对核心5/DI3资产、8依赖组、统一0.1.0-alpha、DI精确核心版本[0.1.0-alpha]及Microsoft.Extensions按TFM为8/9/10；既有缺包README提示保留R01。

格式检查、UTF-8无BOM/CRLF及git diff --check通过。C05由进行中改为待验证，两个本地子模块均已落地；三份现有文档合并契约、Java差距、步骤和证据，当前master提交不推送。真实字典版本、权限可见性/大小写配置、Oracle DATA_DEFAULT LONG和COLUMN_ID语义、两模式/OBProxy及非Windows/新的旧运行时验收仍未执行，不能据协议夹具关闭G3。Java完整键/索引/过程/类型/模式/同义词元数据不属于首批集合，留D阶段。A05可信公开MySQL判定、C01完整重置和G0/G1缺口不变；下一可本地推进模块C06逐条DbBatch。


2026-10-19 C06逐条DbBatch及中断走查修复：现代三资产新增OceanBaseBatch、OceanBaseBatchCommand、命令集合和内部reader，factory/connection公开CanCreateBatch及创建入口；两个Standard资产不暴露现代类型。复用已有OceanBaseCommand逐条执行，不合并SQL、不隐式开启事务、不发送额外提交/回滚、不重放结果未知语句。开始时快照全部SQL/参数元数据和值（复制byte[]），缓存每条准备语句；集合单一归属，移除/替换/清空/释放关闭自有statement，不释放调用方连接/事务。连接创建的batch继承Command Timeout，每条包含prepare/execute/read的独立预算，0无限。

批次预约原PhysicalSession直到reader关闭，普通命令、目录、切库和事务操作不能在条目间或完成日志回调中插入，关闭重开不能让后缀迁入新会话。NextResult排空当前语句全部结果后启动下一条，兼容两套schema、provider-specific值和SequentialAccess字段流；Scalar跳过更新计数，取首个有列结果的首行首列后排空后缀。每条已知更新累加、查询/未执行为-1，总计忽略-1、无计数为-1、空批次为0。显式事务精确绑定、环境事务拒绝；服务端/协议/未知结果OceanBaseException通过BatchCommand指向原条目，保留错误码、SQLSTATE和未知结果语义。数据源复用平台DbDataSource包装自动打开/关闭、reader注入CloseConnection；按平台契约拒绝Prepare和Connection/Transaction，条目通过factory.CreateBatchCommand创建。

独立走查指出并修复四类问题，均补入先失败回归：旧reader通过共享条目读写新执行计数，改为每次执行独立计数；普通/batch旧CloseConnection reader可能关闭重开后的会话，在连接锁下按原PhysicalSession原子匹配后关闭；批次内部linked token泄漏到取消异常，保留初始调用方token并区分NextResult自己的token及Cancel；字段流/异步物化失败未释放预约，CommandExchange在外部日志回调前通知batch失败并释放。后续边界回归覆盖首条后取消、Prepare取消及NextResult后缀部分发送的token归因，另验证日志回调关闭重开时后缀被拒绝且新连接可用。清理移除缓存命令对旧操作的引用；reader异步CloseConnection在释放预约后等待ROLLBACK/恢复自动提交，新增延迟响应回归通过。独立只读最终复核产品代码、邻接生命周期、取消、消费样例及文档，无剩余必须修复项。

新增55项Unit和14项Consumer，首批6项公共入口先失败后通过；红绿证据TestResults/c06-batch-{red,green}.trx、c06-batch-count-red.trx、c06-batch-review-{red,green}.trx、c06-batch-token-red.trx和c06-batch-token-edge-{red,green}.trx。覆盖同步/异步逐条计数、错误前缀/停止后缀、空批次、多结果/标量/顺序字段流、准备复用与释放、事务/环境事务拒绝、集合与配置冻结、byte[]快照、日志重入、旧会话隔离、字段故障、EOF/部分发送/超时/Cancel及真实环回等待响应时关闭。首轮部分发送用例中，Oracle首条OK未报告时区会触发内部探测，已补正确时区报告使故障落在业务SQL；虚拟流关闭不唤醒等待的问题改用真实环回关闭用例，不把两类夹具问题计作产品缺陷。两个模式均有明确内部认证后事实夹具，不改公共MySQL认证门槛。独立计数/原会话关闭/取消/字段故障修复已进入完整套件。

最终本地验证：Release slnx核心五/DI三目标0警告0错误；Windows完整Unit1172、Consumer142、DI8全部通过且0跳过（TestResults/c06-batch-unit-final-checked.trx、c06-batch-consumer-final.trx、c06-batch-di-final.trx）。完整Unit沿用已授权的Windows TLS证书存储访问，不降低TLS策略。Consumer五资产隔离宿主仍是net10；另用BatchRuntimeConsumerSample.cs独立编译样例在实际.NET8.0.31/9.0.20/10.0.12运行，通过批次计数、已知ERR归因、数据源reader结果及关闭，实际Logging程序集主版本分别为8/9/10，输出见TestResults/c06-batch-runtime/net{8,9,10}.0/result.log；未新增日常旧目标测试宿主。

格式、git diff --check及UTF-8无BOM/CRLF检查通过；TestResults/c06-batch-pack双包核对核心5/DI3资产、8依赖组、统一0.1.0-alpha、DI精确核心版本[0.1.0-alpha]和Microsoft.Extensions按两个Standard/net8→8、net9→9、net10→10，既有NuGet缺README提示仍留R01。三份既有文档同步契约、步骤、进度与证据，C06从未开始改为待验证，当前master提交且不推送；下一可本地推进模块C07。G3仍0/8，A04/A05真实关口、A05可信公共MySQL模式判定和C01完整重置白名单缺口不变。未执行真实两模式/OBProxy或非Windows验收；实际旧运行时的局部环回样例不替代完整兼容/集成矩阵，逐条批次无原子性保证。


2026-10-19 C07本地诊断与性能基线：五资产共用OceanBaseConnector ActivitySource/Meter及程序集版本，新增Counter<long> oceanbase.client.operations和秒单位Histogram<double> oceanbase.client.operation.duration。固定操作覆盖open、execute、prepare、change_database和transaction.begin/commit/rollback；目录查询计入底层execute，batch逐条计数，内部时区查询和自动prepare不重复计数。共同四标签只含操作、认证模式、结果和执行结果未知；成功/错误/取消/超时与未知结果独立表达。未记录SQL、参数、库名、端点、凭据、异常文本或连接ID。span继承调用方父上下文但立即恢复Current，reader未读终态不提前结束，提前关闭以未知结果结束且只记录一次。状态发布/租约释放与batch失败清理先于结束回调，监听器不在内部锁内调用。

两个Standard资产通过CPM新增DiagnosticSource8.0.1；net8/net9/net10使用各自运行时内置诊断API，没有向旧资产引入10.x。首批8项公共诊断回归失败后通过（TestResults/c07-diagnostics-{red,green}.trx）；随后采样拒绝仍分配诊断对象的回归先失败，改用固定span名称并在拒绝采样且无指标监听时不创建操作对象，运行时自身48字节/次采样分配据实保留。五资产隔离消费又以5项先失败复现源注册/instrument发布回调导致TypeInitializationException，将初始化通道分别保护，c07-diagnostics-init-{red,green}.trx转绿；首次失败通道可不可用，但驱动后续连接和结果不受影响。

独立只读走查发现恢复Activity.Current的setter仍可能通过Activity.CurrentChanged抛异常，在命令执行try之前逃逸并遗留Busy租约。同步/异步两项回归先失败（c07-diagnostics-review-red.trx），统一使用捕获回调异常的Restore后，33项定向全部通过（c07-diagnostics-review-green.trx）。测试包括同命令再次执行、Current/父上下文、终态前不结束、提前关闭幂等、已知ERR、取消/超时/关闭与未知结果、预取消/取得操作前验证不计数、事务发布、显式prepare、MySQL切库脱敏、各类监听器异常及回调重入的独占/释放顺序；MySQL使用明确认证后事实夹具，不放宽A05。独立走查最终无剩余必须修复项，无额外次要缺陷。

新增33项Unit（含9项协议性能基线和3项诊断微基线）、10项五资产隔离Consumer；全部普通宿主仍net10。最终Release slnx核心五/DI三目标0警告0错误，Windows完整Unit1205、Consumer152、DI8全部通过且0跳过（TestResults/c07-diagnostics-unit-final.trx、c07-diagnostics-consumer-final.trx、c07-diagnostics-di-final.trx）。完整Unit继续使用已授权的Windows TLS证书存储访问；初次NuGet还原因沙箱TLS凭证不可用失败，在正常权限restore后通过，未修改证书或降低TLS校验。扩展既有BatchRuntimeConsumerSample独立编译样例，在实际.NET8.0.31/9.0.20/10.0.12验证8个span/metric、已知失败、父上下文与脱敏，Logging及DiagnosticSource程序集主版本均匹配8/9/10，见TestResults/c07-diagnostics-runtime/net{8,9,10}.0/result.log。

Windows本地基线环境为.NET10.0.12、Windows10.0.26200、Intel64 Family6 Model158 Stepping10，Release；同步客户端测量当前线程分配，服务端为环回协议夹具，预分配响应和预热不计入测量。每组预处理400次仅prepare一次并复用statement ID；多结果400次；顺序大值8次各4MiB，共32MiB，验证实际读到的字节数且客户端分配低于2MiB。最终完整套件的一次记录如下（time为总毫秒，alloc为客户端线程总字节）：

| 场景 | 无监听 time / alloc | 拒绝采样 time / alloc | 追踪及指标启用 time / alloc |
| --- | --- | --- | --- |
| 预处理400次 | 47.193 / 1001600 | 49.394 / 1020800 | 54.040 / 1379200 |
| 多结果400次 | 70.038 / 1379200 | 69.472 / 1398400 | 72.669 / 1756800 |
| 顺序32MiB | 34.424 / 171680 | 24.269 / 172064 | 19.361 / 179232 |

诊断Start/Finish微基线各10000次：无监听6.195ms/0字节，拒绝采样1.185ms/480000字节，Activity启用16.811ms/9440000字节。采样/启用数值含运行时Activity机制；分配分别约0/48/944字节每次，无监听不创建每次诊断对象/标签/额外Stopwatch。计时受JIT和调度影响，单次结果不用于宣称启用更快或生产吞吐无退化；这是首份可重复基线，尚无历史同环境回归对比，不虚构退化结论。复现命令为dotnet test tests/OceanBaseConnector.UnitTests/OceanBaseConnector.UnitTests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~DiagnosticsTests --logger "trx;LogFileName=c07-diagnostics-baseline.trx" --results-directory TestResults，TRX保留逐组输出。

格式、UTF-8无BOM/CRLF和git diff --check通过，TestResults/c07-diagnostics-pack双包检查核心5/DI3资产、8依赖组、统一0.1.0-alpha、DI精确核心版本[0.1.0-alpha]、Microsoft.Extensions 8/9/10及仅两个Standard资产显式DiagnosticSource8.0.1。既有NuGet缺README提示仍归R01。三份既有文档已更新，C07待验证，当前master提交不推送；不据本地基线关闭G3，真实两模式/OBProxy、非Windows及高并发池借还暂缓，C01未开放所以未用非池测试冒充池基线。A05可信公共MySQL认证和G0/G1缺口不变；后续先核对C08尚缺证据，再推进可本地开展的发布准备。

2026-10-19 C08证据核对与R01本地包准备：核对现有本地实现/协议证据后，C08改为阻塞，G3仍0/8；真实两模式/OBProxy/TLS/认证及完整重置白名单缺口不能由本地包消费补齐。R01按现有设计推进，两个产品显式固定现有包ID，新增src/Directory.Build.props导入根属性后统一仓库/作者描述/包内README与THIRD-PARTY-NOTICES；根Version、CPM、五/三目标和DI精确核心依赖保持统一。README公开实验版本、真实组合全部未验证、A05公共MySQL握手拒绝、Pooling=true拒绝、结果未知不重放及未支持能力，没有把D阶段当作已支持。归属清单从实际依赖元数据核对，包含CPM提升为直接依赖的DI抽象；参考Java LGPL不自动成为本项目许可证。项目许可证已向维护者询问，尚未确定，也未作源码来源/商标和包名授权结论。

新增eng/Verify-Packages.ps1与tests/PackageConsumer/Program.cs：先对旧包验证README缺失而失败（TestResults/r01-package-readme-red.log），再以一次slnx pack生成两包，检查实际ZIP/nuspec、核心5/DI3资产、8依赖组、按TFM的Microsoft 8/9/10、仅Standard显式DiagnosticSource8.0.1和DI核心精确[0.1.0-alpha]。验证器使用全新目录/缓存、产品ID只映射至本地包源、PackageReference和包SHA512核对，拒绝旧同版本缓存替代本次产物；既有依赖缓存仅作离线依赖源。临时五目标样例采用独立CPM，不新增日常旧目标测试工程，不引用产品ProjectReference。全部样例验证factory、公开类型身份/数值、现代API可见性、预取消及凭据隐藏、实际Logging/DiagnosticSource程序集主版本；现代样例另验证普通/keyed DI和数据源别名/释放与batch入口，使用样例宿主自带的ASP.NET Core容器，产品不增加宿主依赖。实际.NET8.0.31/9.0.20/10.0.12运行通过，两个Standard资产在隔离.NET10.0.12宿主通过；这不是完整旧运行时或真实服务器兼容矩阵。

两项损坏产物检查分别删除包内README、将DI核心约束从精确区间放宽为最低版本，验证器均退出1并输出对应原因；日志保留于TestResults/r01-negative/78dcd9057dbf4297a1b5c7c899cad852下。Windows CI改为统一slnx pack并接入实际包消费验证，所需8/9运行时通过setup-dotnet安装；现有非Windows作业未在本地执行。独立只读走查核对全部新增文件、依赖和缓存隔离，没有发现其他需修复项；唯一最低PowerShell版本问题（7.0不支持所用HashData）已将Requires及README修正为7.1+，本机实际7.6.6/.NET10.0.12执行。

最终本地Release slnx核心五/DI三目标编译0警告0错误，完整Windows Unit1205、Consumer152、DI8通过且0跳过（TestResults/r01-unit-final.trx、r01-consumer-final.trx、r01-di-final.trx）；完整单测沿用正常Windows TLS证书存储访问，没有降低TLS校验。新包及实际包消费日志为TestResults/r01-pack和TestResults/r01-package-green.log，生成样例、独立缓存与还原图路径由该日志记录。最终包复跑曾因沙箱NuGet漏洞数据查询失败报NU1900；使用正常Windows TLS凭据完整重跑后五资产全部通过，没有禁用漏洞查询或TLS校验。PowerShell语法、格式/diff和UTF-8无BOM/CRLF检查通过后，在当前master提交不推送。R01两项技术验收勾选，许可/来源及包名/商标项保持开放，状态为进行中；未发布包，真实数据库、非Windows和完整历史运行时矩阵仍暂缓，A05/C01/G0/G1/G3/R02缺口不变。

2026-10-19 R02 本地交付材料及续接修复闭环：故障、结果未知、提前关闭 reader、可信事务结果与后续清理失败、逐条批次计数/原子性边界、多地址 TCP 切换、脱敏日志/诊断、认证/TLS 支持限制以及 0.1.0-alpha 升级/回退指南合并到根 README，随两个包交付。明确释放字段流不代表所属 reader 完成；按独立走查修正可信 COMMIT ERR 的说明，仅在会话仍 Open 且事务仍活动时保留显式回滚能力，否则新建连接核对状态。同步更正既有架构/方案中异常暴露模式或端点属性的误述，实际由日志/诊断关联；现代目标通过继承的 BatchCommand 关联批次条目。不另建独立设计文档，不添加未实现的 API。

本轮首次完整 Unit 为1204通过/1失败，发现并修复同步 TCP 共享等待句柄释放问题，修复及红绿回归见前文。首次压力回归全量复跑还造成端口耗尽（Unit218失败/Consumer1失败，保留 TestResults/r02-{unit,consumer}-port-exhausted.trx），调整测试关闭顺序与异常保留后重新执行；没有把失败轮次计为通过，也没有修改系统端口范围、超时或产品连接重试策略。独立只读走查已对文档、产品修复和测试清理闭环，无剩余需修复项。

最终 Release slnx 编译核心五/DI三目标0警告0错误（TestResults/r02-build-final.log）；完整 Windows Unit1206、Consumer152、DI8全通过且0跳过（TestResults/r02-{unit,consumer,di}-final.trx）。解析最终TRX确认既有20组具名走查回归及新增TCP回归均实际执行通过，Standard2.1异步清理仍覆盖。统一 pack 后 TestResults/r02-pack 双包验证通过，五资产样例分别在实际.NET8.0.31/9.0.20/10.0.12及隔离.NET10 Standard宿主执行，日志为TestResults/r02-package-final.log；独立还原目录TestResults/package-validation/e2b0d53c4b4a4880b643a9bfaf723532。另逐字核对两个包内README与源文件一致，UTF-8无BOM/CRLF及diff检查通过。单元证书与包还原采用正常Windows TLS权限，未禁用审计或降低TLS校验。

R02仅完成本地材料和本地验证，状态保持进行中；G4同步为进行中、仍0/2，C08仍因真实证据缺失而阻塞。R01项目许可证/来源/包名审查、G0/G1及完整G3/真实版本矩阵均未关闭，未发布、未推送。真实数据库与非Windows验收继续按约定暂缓；A04待真实/跨平台验收、A05公开MySQL模式可信判定缺口和C01池重置白名单不变。

## R02 Windows .NET 8/9 完整本地回归补验（2026-10-19）

沿用当前 master 和既有产品五目标/DI三目标、CPM及统一0.1.0-alpha，不新增旧目标日常测试工程。临时同时传入 TargetFrameworks/TargetFramework 构建现有 Unit/DI 套件，随后恢复整个 slnx 默认还原和构建；所有测试工程声明仍为net10.0。deps图确认net8使用Logging8.0.3、DI8.0.1/Abstractions8.0.2，net9均9.0.10；没有用net10依赖替代旧目标依赖。

本轮修复三处测试兼容问题：net8不具备X509CertificateLoader，证书夹具按目标使用对应PFX导入API；PrepareAsync隔离回归改为Standard2.1及当前运行时对应现代资产，保持原异步协议断言；五处MeterListener都提供非抛出MeasurementsCompleted回调。后者来自net8首轮完整Unit1197通过/9失败，以及诊断定向24通过/9失败，涉及残留抛出回调污染后续采集和分配断言。核对实际8.0.31 runtime源码，Dispose只在完成回调非空分支中解除instrument订阅；加空完成回调后诊断33/33及完整1206/1206通过，没有放宽任何断言或修改产品诊断逻辑。README与架构§2.6明确宿主监听器所有权及已验证补丁版本的清理要求。

| 实际Windows运行时 | Unit | Consumer | DI | 失败/跳过 |
| --- | --- | --- | --- | --- |
| .NET 8.0.31 | 1206 | 五资产包消费另验 | 8 | 0/0 |
| .NET 9.0.20 | 1206 | 五资产包消费另验 | 8 | 0/0 |
| .NET 10.0.12（恢复默认） | 1206 | 152 | 8 | 0/0 |

证据：TestResults/runtime8-unit-final.trx、runtime8-di.trx、runtime9-unit.trx、runtime9-di.trx和runtime10-{unit,consumer,di}-final.trx。TRX诊断输出记录实际运行时，runtime{8,9,10}-host.json另保留testhost的coreclr.dll加载路径；runtime-matrix-dependencies.json保留旧目标依赖核对。红绿记录保留runtime8-prepare-{red,green}.trx、runtime8-unit.trx、runtime8-diagnostics-{red,green}.trx及runtime8-listeners-red.trx。源码依据为 https://raw.githubusercontent.com/dotnet/runtime/v8.0.31/src/libraries/System.Diagnostics.DiagnosticSource/src/System/Diagnostics/Metrics/MeterListener.cs ，本地留存TestResults/runtime8.0.31-MeterListener.cs。失败轮次不折算为通过，不推广到所有net8补丁。

恢复默认Release slnx核心五/DI三目标编译0警告0错误（TestResults/runtime-matrix-build-final.log）。重新pack到TestResults/runtime-matrix-pack，Verify-Packages使用独立缓存7161e6b0c911479bb53cdc0c460d9ea0核对双包内容、同版本和精确核心依赖、8/9/10依赖组及实际五资产消费，全部通过（TestResults/runtime-matrix-package-final.log）；Standard2.0/2.1包消费仍在net10隔离宿主执行，不冒充Framework4.8。正常Windows证书存储/NuGet TLS权限运行，未禁用审计或降低TLS校验。

修改过的独立BatchRuntimeConsumerSample另按三个现代参考程序集重新编译，实际8.0.31/9.0.20/10.0.12批次/诊断断言均通过（TestResults/runtime{8,9,10}-batch-final.log）。首次调用遗漏夹具参数造成的命令错误单独保留runtime8-batch-invocation-error.log，补齐参数后通过，不归为产品故障。独立只读闭环复核核对全部七份最终TRX、实际运行时、依赖图、默认目标、编译和包内README，无剩余需修复项；git diff --check及UTF-8无BOM/CRLF检查通过。

本轮不新增产品功能；先前11类走查问题及TCP借用等待句柄缺陷已修复，本轮补齐旧运行时测试发现的夹具问题和进度证据。R02仍进行中、G4仍0/2；真实数据库与非Windows按约定暂缓，Framework4.8、完整兼容矩阵及许可/来源/包名审核未完成。A04真实/跨平台验收、A05可信公共MySQL模式判定和C01完整重置白名单仍开放，不发布、不推送。

## R01/R02 Windows Framework 基础包消费（2026-10-19）

本轮复核余项后只推进本机可运行的消费验证，不提前启动依赖G4或实库矩阵的D扩展。eng/Verify-Packages.ps1增加显式-VerifyNetFramework选项；默认五资产流程继续保留，非Windows使用该选项会明确拒绝。指定选项时在本次独立包消费目录下临时生成net48宿主，引用已编译的Standard2.0消费库并通过PackageReference还原本次核心包；检查包SHA512、compile资产lib/netstandard2.0/OceanBaseConnector.dll及Microsoft.Extensions 8.x依赖。使用已安装4.8 targeting pack，不下载额外参考包；自动生成宿主binding redirects并保留exe.config。核心五目标、DI三目标、CPM/Version和所有日常net10测试工程均不改变。

真实桌面CLR执行复用原PackageConsumer.Verify的全部基础断言：provider factory、连接/命令/参数及公开数值类型身份、现代API在Standard资产不可见、Logging/DiagnosticSource程序集主版本8、未建连状态、预取消及凭据脱敏。输出确认Host=.NET Framework 4.8.9337.0、注册表Release=533509，对应本机安装Framework4.8.1；4.0.30319.42000只是CLR版本，不把它或net48目标名误报为原版4.8实际运行证据。Standard2.1仍不用于Framework。

红绿证据：TestResults/framework-verifier-red.log记录原脚本拒绝新参数；实现时首次完整执行因错误使用旧式Framework还原图键失败，TestResults/framework-verifier-green.log保留失败，修正为SDK10实际net48键后通过。进一步重建生成父工程，暴露递归源码glob包含framework-host及其obj导致40个重复特性错误（framework-consumer-rebuild-red.log）；排除framework-host/**/*.cs后，五目标重建0警告0错误（framework-consumer-rebuild-green.log）。未改弱任何消费断言。

默认Release slnx核心五/DI三目标编译0警告0错误（TestResults/framework-slnx-build-final.log），重新统一pack到TestResults/framework-pack。最终默认验证与可选Framework验证都从新独立缓存通过，分别保留TestResults/framework-default-package-final.log（cdf06f55d0184c058e30de717ea84a5d）和framework-package-final.log（9305f9f46ee342339e437d635ff92afd）；可选路径也执行完整原五资产检查，Framework宿主另外编译0警告0错误并实际通过。复现命令为pwsh -NoProfile -File eng/Verify-Packages.ps1 -PackageDirectory TestResults/framework-pack -VerifyNetFramework。源README重新随包交付，现有架构/计划合并边界，不新增独立设计文档。

独立只读走查已复核五文件差异、实际包SHA512/Standard2.0资产、8.x依赖及生成绑定重定向、默认/可选两份最终日志、父工程红绿重建和包内README一致性，无剩余需修复项。git diff --check、UTF-8无BOM/CRLF及两个包README逐字节核对通过。

只增加工程验证与文档，没有修改产品或既有单元测试源码，因此未重复运行刚通过的完整Unit1206/Consumer152/DI8；本轮实际测试是两个包验证路径、真实Framework消费和生成工程的重建回归。真实数据库、非Windows和原版Framework4.8均未执行，完整SQL/TLS/核心API及Framework正式支持决定仍开放。R01/R02继续进行中，G4仍0/2，A05可信MySQL判定及C01完整重置门槛不变，不发布、不推送。

## D14 MySQL YEAR 本地实现（2026-10-19）

从 `7a70bd6` 继续实现四位 YEAR。`OceanBaseDbType.Year` 保持枚举值 9，映射 `ushort` / `DbType.UInt16`；结果类型码 13，文本四位 ASCII、二进制固定两字节小端，允许 0 或 1901–2155。标准/provider-specific 值和字段类型一致，Schema 提供 YEAR、ProviderType=9、precision=4、scale=0。Oracle 模式、YEAR(2)、非法精度、非法文本/范围及 CLR 类型不匹配均明确拒绝，不做两位年份推断或日期转换。显式 Year 参数在 prepare 前验证（含 NULL），复用无符号 SMALLINT 编码；普通 UInt16 保留完整范围。

复用现有 ResultColumn、PreparedParameters、ResultDbColumn 和顺序读取路径，仅增加内部 YearCodec，未引入依赖或公开值类型。失败用例先运行，27 项因未支持 YEAR 失败（`TestResults/year-red/*.trx`）；实现后修正夹具的命令序号边界并通过。五资产消费夹具首次未带认证后 Oracle 状态位，被既有 A05 安全拒绝；修正夹具状态位后，仅在测试内注入认证后 MySQL 模式，产品认证门槛未修改。

最终证据（Windows / .NET 10.0.13）：

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| Release slnx 核心五目标、DI 三目标 | 0 警告、0 错误 | `dotnet build OceanBaseConnector.slnx -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false` |
| YEAR 定向 Unit | 29 通过，0 失败/跳过 | `TestResults/year-targeted/*.trx` |
| YEAR 五资产公共 API 消费（文本/预处理） | 10 通过，0 失败/跳过 | `TestResults/year-consumer-targeted-final/*.trx` |
| 完整 Unit | 1240 通过，0 失败/跳过 | `TestResults/year-unit-full/*.trx` |
| 完整 Consumer | 162 通过，0 失败/跳过 | `TestResults/year-consumer-full/*.trx` |
| DI | 8 通过，0 失败/跳过 | `TestResults/year-di-full/*.trx` |

测试命令沿用 `dotnet test tests/<工程>/<工程>.csproj -c Release --no-build --no-restore -m:1 -nr:false --logger trx --results-directory TestResults/<上述目录>`；定向分别附加 `--filter FullyQualifiedName~Year` 和 `--filter FullyQualifiedName~YearConsumerTests`。覆盖同步/异步、默认/SequentialAccess、文本/二进制、正常/零/NULL、后续字段对齐、参数重用及无效参数发包前拒绝；五资产均经 DbConnection/DbDataReader 公共 API 实际读取和参数写入。独立只读走查核对编解码、参数、reader、顺序帧边界、Schema、测试与文档，未发现需修复问题。`git diff --check` 通过。

D14 本地实现完成，正式状态保持待验证：真实 YEAR 列 INSERT/SELECT、目标 OceanBase/OBProxy 版本及模式矩阵未执行，公共 MySQL 建连仍受 A05 门槛限制。未操作信任根，未部署其他电脑/数据库，未重新验证打包、旧运行时或 Framework。C01、A05 及 D01–D13/D15 仍未完成，不能将本模块通过解释为项目全部完成。

## D13 MySQL BIT 本地实现（2026-10-19）

从 `da03fe6` 继续实现 BIT(1–64)。`OceanBaseDbType.Bit` 保持枚举值 8，映射 `ulong` / `DbType.UInt64`，BIT(1) 也不转 bool。结果类型码 16，列 Length 表示位宽；文本/二进制均为长度编码的大端原始字节，精确要求 ceil(位宽/8) 字节，并拒绝超出位宽的高位，保留 2^63 和 UInt64.MaxValue。标准/provider-specific 类型一致，Schema ColumnSize 为位数，NumericPrecision/Scale 不填十进制精度。Oracle、非法位宽/scale 含 NULL 均拒绝。显式 Bit 参数只接受 UInt64，Size=0 为 64 位、1–64 为声明位宽，Precision/Scale=0，超界在 prepare 前拒绝；复用无符号 BIGINT 绑定，不改变普通 UInt64 的行为。

仅增加内部 BitCodec，复用 ResultColumn、PreparedParameters、顺序/默认 reader 和既有 Schema 路径，无新依赖或公开值类型。长度规则另对照固定 OceanBase 源码 `bit_cell_str`（见开发方案链接），大端解释对照固定 Java `RowProtocol.parseBit`；仅参考行为，不复制源码，也不将源码阅读作为实库验收。

先运行失败测试：`TestResults/bit-red/*.trx` 中 66 项因 BIT 尚未实现失败、5 项非法元数据拒绝通过；实现后修正测试对“多一字节”与“未使用高位非零”异常的区分，71 项通过。随后增加公共环回读写/预先拒绝，最终定向 Unit 81 项通过。复用并扩展五资产 YEAR 消费测试为 `BitAndYearConsumerTests`，原 10 项 YEAR 保留，新增 10 项 BIT，避免重复整套握手/模式夹具。

最终证据（Windows / .NET 10.0.13）：

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| Release slnx 核心五目标、DI 三目标 | 0 警告、0 错误 | `dotnet build OceanBaseConnector.slnx -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false` |
| BIT 定向 Unit | 81 通过，0 失败/跳过 | `TestResults/bit-targeted/*.trx` |
| BIT/YEAR 五资产公共 API 消费 | 20 通过，0 失败/跳过 | `TestResults/bit-year-consumer-targeted/*.trx` |
| 完整 Unit | 1321 通过，0 失败/跳过 | `TestResults/bit-unit-full/*.trx` |
| 完整 Consumer | 172 通过，0 失败/跳过 | `TestResults/bit-consumer-full/*.trx` |
| DI | 8 通过，0 失败/跳过 | `TestResults/bit-di-full/*.trx` |

完整测试命令同上节，替换结果目录；定向分别附加 `--filter FullyQualifiedName~TextCommandTests.Bit` 和 `--filter FullyQualifiedName~BitAndYearConsumerTests`。覆盖全部 64 种位宽的零/最高位/最大值，符号边界与原始字节解释，NULL、精度/长度/位宽错误、参数类型及重置；公共环回覆盖同步/异步、文本/预处理、默认/SequentialAccess、后续字段、预处理重用、NULL 参数和会话复用。独立只读走查核对参数预校验、两种结果/顺序帧路径、无符号边界、Schema、消费者及文档，未发现需修复问题。`git diff --check` 通过。

D13 本地实现完成，正式状态保持待验证。真实 BIT 列 INSERT/SELECT（含服务端目标位宽转换）、OceanBase/OBProxy 版本及模式矩阵未执行，公共 MySQL 仍由 A05 拒绝。未操作信任根、未部署其他电脑/数据库，未重跑打包/旧运行时/Framework。其余 A05、C01、D01–D12/D15 继续开放。

## D12 MySQL JSON 本地实现（2026-10-19）

从 `31b2c7a` 继续实现 JSON。沿用 `OceanBaseDbType.Json`（枚举值 7），标准/provider-specific 值为 string，DbType.String；MySQL 类型码 245 的文本和二进制行都复用长度编码严格 UTF-8 字符串读取。接受 binary collation 63 及现有 UTF-8 collation，Oracle 和其他字符集在 NULL 读取前也验证。JSON 是非数值列，不限制或解释服务端 scale；Schema 为 JSON/Json/string、IsLong=true，ColumnSize/NumericPrecision/NumericScale 均不填。原始 Unicode、空白和宽精度数字文本保持不变，JSON 文本 null、空 JSON 字符串与 SQL NULL 分别处理。

显式 Json 参数仅接受 string/null/DBNull，要求 MySQL 且 Precision/Scale=0，复用 VAR_STRING 编码和既有长度限制，不隐式序列化对象。语法和目标列转换交由服务端验证；环回回归发送非法 JSON 文本并返回 ERR 3140/22032 后，证明同一 statement/session 可继续执行，不把合成 ERR 当作实库语法验证。默认/SequentialAccess、GetChars/GetTextReader、异步及生命周期复用现有字符串路径，没有新增解析器、依赖或公开值类型。协议依据见开发方案 D12 的固定 Java/OceanBase 源码引用，只参考行为，不复制代码。

红绿证据：`TestResults/json-red/*.trx` 中 15 项因 JSON 未支持失败、4 项非法元数据拒绝通过。大字段回归发现共享 WithLength 夹具只生成单字节长度，将其修正为完整长度编码前缀后定向 37 项通过；`json-targeted/*.trx` 保留此前四项失败。随后新增非数值 scale 回归，在旧限制下失败（`json-scale-red/*.trx`），移除错误限制后最终完整回归通过，五资产消费使用 scale=31 验证。没有放宽严格 UTF-8、模式或参数精度检查。

最终证据（Windows / .NET 10.0.13）：

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| Release slnx 核心五目标、DI 三目标 | 0 警告、0 错误 | `dotnet build OceanBaseConnector.slnx -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false` |
| 完整 Unit（含 JSON 37 项） | 1358 通过，0 失败/跳过 | `TestResults/json-unit-full/*.trx` |
| 完整 Consumer（含 JSON 五资产 20 项） | 192 通过，0 失败/跳过 | `TestResults/json-consumer-full/*.trx` |
| DI | 8 通过，0 失败/跳过 | `TestResults/json-di-full/*.trx` |

完整测试命令同上节，替换结果目录。最终 TRX 已解析核对总数和 JSON 用例实际执行；覆盖同步/异步、文本/预处理、默认/顺序读取、多结果与独立 Schema、Unicode/宽数字、NULL、参数写回、坏 UTF-8 和 8KB 字段流。独立只读走查核对参数预校验/编码、两种结果协议、Schema、顺序字段流、测试及文档，未发现需修复问题。`git diff --check` 通过。

D12 本地实现完成，正式状态保持待验证。真实 JSON 列 INSERT/SELECT、服务端语法拒绝及目标 OceanBase/OBProxy 版本矩阵未执行，A05 公共 MySQL 门槛未修改。未操作信任根、未部署其他电脑/数据库，未重跑打包、旧运行时或 Framework；A05、C01 和 D01–D11/D15 仍未完成，项目整体目标保持开放。

## D11 Oracle INTERVAL 本地实现（2026-10-19）

从 `808dfed` 继续实现 INTERVAL YEAR TO MONTH / DAY TO SECOND。新增不可变 `OceanBaseIntervalYearToMonth` / `OceanBaseIntervalDayToSecond`，标准及 provider-specific 访问均为对应精确结构，DbType.Object，沿用 provider 枚举值 5/6。保存独立符号、年/月或天/时/分/秒/纳秒、前导精度与 DS 小数精度；默认结构为合法零。年月区间不转换为固定时长，DS ToTimeSpan 在 100ns 和 Int64 ticks 范围内精确转换，包括 TimeSpan.MinValue；超界或纳秒不能整除 100 时抛错。ToString 使用 invariant 带符号文本并保留声明精度。

Oracle 类型码 204/205 在文本及预处理行中均读取长度编码的 7/14 字节专用小端数据；不误作普通字符串或 MySQL temporal 布局。列 scale 分别为前导精度及“前导精度*10+小数精度”，与字段内精度核对。拒绝其他模式（含 NULL）、非法长度/符号/分量/精度及舍入；Schema 提供精确类型和 provider 身份，不把区间精度填作 NumericPrecision/Scale。显式参数或 DbType.Object 下的精确值使用原生类型码，Precision=0 采用值精度、非零为声明前导精度，Scale 未设置采用值 scale、显式 0 不截断纳秒；无效值在 prepare 前拒绝。复用命令预校验、默认/SequentialAccess、多结果和资源管理，未引入依赖或隐式字符串/TimeSpan 转换。

协议依据为固定 Java 基线的 INTERVAL 参数、DataTypeUtilities 和两种 RowProtocol，以及固定 OceanBase `5e8a63d29dae9fde5e962b8fcc7b167d438fc343` 的 interval_ym/ds_cell_str、encode_interval_ym/ds、ObIntervalScaleUtil（开发方案已记录文件）。仅参考协议行为，不复制源码；只读源码核对不能代替真实版本验收。

红绿证据：`TestResults/interval-red/*.trx` 的 6 项因类型未支持失败，实现后原 6 项通过（`interval-targeted-initial/*.trx`）。扩展回归首次 27 通过/1 失败，定位为夹具将测试中的非法 YM scale=99 替换为缺省 9；改为可空缺省值后，最终完整 Unit 含全部 30 项 INTERVAL 回归通过。首次完整 Consumer 中原 192 项通过、新 20 项失败，定位为新夹具遗漏 Oracle 时区报告/后续探测；复用既有 SessionTimeZoneFixture 后定向 20 项通过，最终完整 212 项通过。上述失败报告保留，不计入最终通过结果，产品时区行为与认证策略未修改。

最终证据（Windows / .NET 10.0.13）：

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| Release slnx 核心五目标、DI 三目标 | 0 警告、0 错误 | `dotnet build OceanBaseConnector.slnx -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false` |
| 完整 Unit（含 INTERVAL 30 项） | 1388 通过，0 失败/跳过 | `TestResults/interval-unit-full/*.trx` |
| INTERVAL 五资产公共 API 消费 | 20 通过，0 失败/跳过 | `TestResults/interval-consumer-targeted-final/*.trx` |
| 完整 Consumer | 212 通过，0 失败/跳过 | `TestResults/interval-consumer-full-final/*.trx` |
| DI | 8 通过，0 失败/跳过 | `TestResults/interval-di-full/*.trx` |

完整测试命令同前节，替换结果目录；定向附加 `--filter FullyQualifiedName~IntervalConsumerTests`。覆盖 0–9 全部前导精度与小数精度组合、正负最大值、默认零、精确 TimeSpan 边界、区域无关文本、畸形数据、声明精度/类型拒绝、同步/异步×文本/预处理×默认/顺序读取、NULL/后续字段/多结果 Schema 快照及参数重用。五资产通过实际加载的产品程序集和 DbConnection/DbDataReader 公共 API 读取并写回两种精确值。独立只读走查核对完整模块和时区夹具修正，未发现需修复问题；`git diff --check` 通过。

D11 本地实现完成，正式状态保持待验证。真实 INTERVAL 列 INSERT/SELECT、精度转换及 OceanBase/OBProxy 版本矩阵暂缓；未操作信任根或部署其他电脑/数据库，未重跑打包、旧运行时或 Framework。A05、C01 和 D01–D10/D15 等整体余项继续开放，不发布、不推送。

## D10 Oracle NCHAR/NVARCHAR2 本地实现（2026-10-19）

从 `8a21dbd` 继续实现 national 字符类型。沿用 OceanBaseDbType.NChar/NVarchar2（枚举值3/4）和 string/DbType.String，不增加公开类型或依赖。Oracle208/207在文本、预处理结果和参数中使用长度编码字节；独立会话 NCHARACTER_SET_CONNECTION 决定 UTF8/UTF8MB4 或无 BOM 的严格 UTF16BE 编解码，保留补充平面字符、显式 BOM 字符及 NCHAR 尾部空格。national 为 NULL/binary 时输入按报告的 connection 编码、输出按 results 编码；results 为 NULL/binary 时使用列源 collation（支持既有 UTF8 和 UTF16 的54/55/101）。未知编码不猜测，普通 string 参数仍用UTF8。Schema保持两个provider身份及服务端字符长度，不填数值精度/scale；NULL字段保留身份且不需编码，NULL参数沿用类型6/位图。

NationalCharacterSet 保存不可变会话快照；复用 PhysicalSession、CommandExchange 和 QueryResult 的独占、发送前失效及每结果快照规则。Oracle业务执行前，只有完整的三个charset报告可复用，否则同租约/总预算查询三个会话值，严格限定一行三字符串列、单结果；内部查询按其列的UTF8/UTF16编码读取名称，不修改会话。查询恢复已解析的时区，矛盾报告清除事实并拒绝；多结果缺少新完整报告时，后续非NULL national值明确失败。读取整值与SequentialAccess/GetChars/GetTextReader共用列编码，原流生命周期、取消和内存规则保持一致。显式参数在取得租约前验证类型、Unicode、Size（CLR UTF16 code unit）及Precision/Scale=0，之后按实际会话编码在prepare前限字节；不以UTF8大小误拒绝合法UTF16参数。

协议依据见开发方案D10，固定Java和OceanBase源码仅用于核对行为，不复制代码或替代实库证据。同步更新既有会话报告与探测夹具，不改变公共A05 MySQL模式门槛、TLS策略或池化拒绝。

红绿与走查证据：首批4项均因未支持类型失败（`TestResults/nchar-red/*.trx`），实现后14项codec通过，扩展公共读写后30项通过。已有命令回归首次1049通过/9失败，包含1项过时的不支持断言、2项夹具未沿用协商EOF格式、6项沙箱TLS私钥权限失败；修正断言/夹具后，以既有完整Unit权限运行，未操作信任根。新增空探测结果回归先失败：完整响应已消费，原共享Fail契约允许会话保持Open，测试改为拒绝操作但不要求销毁已同步会话；未读响应仍必须Broken。独立走查发现内部探测UTF16解码和national预校验UTF8字节预算问题，均修正并补回归；后续复查无剩余问题。最终55项定向全部通过（`nchar-reviewed-targeted/*.trx`）。Consumer曾有条件表达式类型推断编译错误，显式声明Encoding后修复；未将该次编译计为通过。

最终证据（Windows / .NET10.0.13）：

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| Release slnx核心五目标、DI三目标 | 0警告、0错误 | `dotnet build OceanBaseConnector.slnx -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false` |
| 完整Unit（含national55项） | 1442通过，0失败/跳过 | `TestResults/nchar-unit-full/*.trx` |
| 完整Consumer（含national五资产40项） | 252通过，0失败/跳过 | `TestResults/nchar-consumer-full/*.trx` |
| DI | 8通过，0失败/跳过 | `TestResults/nchar-di-full/*.trx` |

完整测试命令同前节，替换结果目录；最终TRX已解析核对总数及55/40项新测试实际执行。覆盖同步/异步×文本/预处理×默认/顺序读取，跨解码缓冲区的字符值、不同存储/线上编码、坏Unicode、NULL/后续普通列、参数重用/发包前拒绝、编码修改与不可变结果快照、完整/缺失/冲突报告、UTF16名称探测、大CJK字符串预校验、非法形状、总超时与取消。五资产消费通过实际产品程序集及DbConnection/DbDataReader公开入口，运行宿主为net10，不冒充五套运行时。独立走查复查和`git diff --check`通过。

D10本地实现完成，正式状态保持待验证；真实NCHAR/NVARCHAR2列INSERT/SELECT、服务端字符长度/空字符串语义及目标版本/OBProxy矩阵暂缓。未部署其他电脑/数据库、未操作信任根，未重跑打包、旧运行时或Framework。A05、C01及D01–D09/D15等余项仍开放，项目整体未完成，不发布、不推送。

## D15 SHA-2认证首批本地实现（2026-10-19）

从 `c4ee8b3` 继续实现 `caching_sha2_password` fast auth与受VerifyFull限制的完整密码路径，以及 `sha256_password` 的VerifyFull响应。新增内部AuthenticationConversation复用现有握手帧、能力、TLS、连接预算、模式判定和异常清理，不新增依赖或公共选项。SHA256固定token向量核对stage2后接seed的顺序；20字节挑战、空密码、Unicode严格UTF8和NUL边界明确验证。`01 03`不写客户端包，必须继续等待最终OK/ERR；`01 04`只在身份验证通过的TLS后发送密码加NUL。初始响应后允许一次账户插件切换，fast/full后拒绝继续挑战或切换；未知插件、畸形/重复消息和截断最终包均失败，不将fast结果误认为可用连接。native/clear、A05公共MySQL拒绝及ExpectedMode检查保持既有路径。

首批6项公共握手测试先因未支持插件失败（`TestResults/sha2-red/*.trx`），实现后6项全部通过（`sha2-green/*.trx`）。边界测试首轮47通过/4失败（`sha2-boundaries/*.trx`）：两项错误期待sha256初始响应后不可切换账户插件，两项NUL在ADO.NET连接串基类先被拒绝；修正测试后58项通过（`sha2-boundaries-green/*.trx`），没有为适配错误断言修改产品。含Required TLS环回的66项全部通过（`sha2-targeted/*.trx`），另补最终包截断和A05/ExpectedMode边界6项，最终完整测试实际执行SHA-2共72项。

五资产公开DbConnection/DbCommand消费首轮10通过/20失败（`sha2-consumer-targeted/*.trx`）：认证成功后的时区probe被新夹具误当业务命令。复用既有SessionTimeZoneFixture处理内部probe并精确断言业务SQL后30项通过（`sha2-consumer-green/*.trx`）；再增加sha256初始弱通道拒绝共40项，在完整Consumer中全部执行。两套Standard和现代三个资产均有同步/异步公开入口消费，Standard2.0的OpenAsync沿用基类入口；宿主统一net10，不冒充五套运行时验收。

独立只读走查覆盖新状态机、握手/TLS/PacketChannel/PhysicalSession和全部新测试，未发现产品缺陷；文档复查指出首页仍称D15未开发，已更正为部分本地实现、余项开放。README、架构及开发方案明确区分状态/编码测试、Required TLS环回和真实可信TLS链。测试不安装/删除信任根、不弹出信任确认；Required TLS只证明加密后的fast认证及完整密码拒绝，VerifyFull完整密码分支仅有状态/编码证据，不宣称私有CA成功链已验证。

最终证据（Windows / SDK10.0.402 / .NET10.0.13）：

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| Release slnx核心五目标、DI三目标 | 0警告、0错误 | `dotnet build OceanBaseConnector.slnx -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false` |
| 完整Unit（SHA-2新增72项） | 1514通过，0失败/跳过 | `TestResults/sha2-unit-full/*.trx` |
| 完整Consumer（SHA-2新增40项） | 292通过，0失败/跳过 | `TestResults/sha2-consumer-full/*.trx` |
| DI | 8通过，0失败/跳过 | `TestResults/sha2-di-full/*.trx` |

完整测试命令同前节，替换结果目录；TRX已解析核对实际新增案例和零失败/跳过。UTF8无BOM/CRLF及git diff --check通过，当前master本地提交不推送。D15状态保持进行中：RSA公钥配置/检索、其他插件及真实两模式/OBProxy/TLS矩阵仍开放；A05、C01及D01–D09未完成。异机/实库继续暂缓，未重跑打包、旧运行时或Framework，没有据本地测试关闭项目整体或生产发布关口。

## D15 RSA认证后续本地实现（2026-10-19）

从 `5db7b98` 补齐SHA-2固定RSA公钥与显式公钥获取路径。新增连接键/属性Server RSA Public Key/ServerRsaPublicKey（默认空的SPKI PEM文本）及Allow Public Key Retrieval/AllowPublicKeyRetrieval（默认false）；冻结快照、多地址副本、大小写、序列化往返、默认PEM脱敏和长度/布尔验证均覆盖，不引入文件I/O或依赖。已验证TLS优先原密码路径；非空密码在无VerifyFull时必须有固定公钥或显式取钥选项，clear插件仍拒绝弱通道。固定key优先，无效不回退检索；默认不请求服务端key，不隐式降低TLS。获取key无身份验证且可被中间人替换，README明确风险；Disabled下业务SQL/结果仍为明文。

RsaPassword复用ByteCursor校验SPKI DER，仅RSA OID、规范长度/正整数、无尾随数据，最长16KiB PEM、2048–8192位模数、至少3且不超过64位的正奇数指数；拒绝私钥、非RSA和弱/畸形key。BCL RSA.ImportParameters/Encrypt跨五目标使用协议OAEP-SHA1，对严格UTF8密码加NUL与20字节挑战循环XOR后加密，明文含NUL不超过密钥字节数减42。解析/导入/加密失败不附可能含key材料的内部异常。sha256取钥请求01、caching full请求02；读01前缀PEM（允许一个NUL终止符）后发密文，仅等最终OK/ERR。key未到就OK、重复key/挑战或完整认证阶段切换均失败；错误/取消/超时沿用总连接预算、无重试和传输清理。

红绿记录：首批8项固定key公开握手先因未知连接键失败（`TestResults/rsa-red/*.trx`），实现后8项通过（`rsa-green/*.trx`），再扩为固定/获取×初始/切换×同步/异步16项。首次混合RSA/SHA-2回归101通过/8失败（`rsa-options-and-flow/*.trx`），失败均为在普通沙箱下运行TLS环回的Windows凭据访问限制；未修改证书策略，最终以既有完整Unit执行权限全部通过。RSA边界定向71项通过（`rsa-boundaries/*.trx`，过滤含既有匹配项）。

独立走查发现初始sha256取钥后错误拒绝首次账户插件切换；4项反向协商网络回归先失败（`rsa-switch-review-red/*.trx`）。调整为初始阶段允许一次FE并清旧取钥状态，caching full阶段保留final锁；定向92项通过（`rsa-reviewed-targeted/*.trx`）。复查同时提示空密码线上语义：固定Java空包规则和MySQL8服务端sha256入口接受空包/单NUL作为无密码标记。新增4项初始握手测试先2失败/2通过（`rsa-empty-red/*.trx`），SHA-2完整密码路径对空密码优先返回NUL，不受key/检索配置干扰；caching初始仍空token，非空密码安全边界不变。最终完整回归中四项全部通过。独立修复复查无剩余产品问题。

新增82项Unit包含选项冻结/多地址/脱敏、RSA独立私钥解密与Unicode/最大OAEP明文、恶意PEM/DER/算法/尺寸/指数、固定key无效不回退、TLS优先、取钥顺序、early OK/服务端ERR、两插件网络超时/关闭/取消、账户插件切换及空密码。五资产新增40项DbConnection/DbCommand公开消费，固定/获取×两插件×同步/异步，独立服务端解密确认Unicode密码并执行精确业务SQL；定向40项通过（`rsa-consumer-targeted/*.trx`）。消费宿主为net10，Standard2.0异步入口沿用基类，不称为五运行时验收。

最终证据（Windows / SDK10.0.402 / .NET10.0.13）：

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| Release slnx核心五目标、DI三目标 | 0警告、0错误 | `dotnet build OceanBaseConnector.slnx -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false` |
| 完整Unit（RSA新增82项） | 1596通过，0失败/跳过 | `TestResults/rsa-unit-full/*.trx` |
| 完整Consumer（RSA新增40项） | 332通过，0失败/跳过 | `TestResults/rsa-consumer-full/*.trx` |
| DI | 8通过，0失败/跳过 | `TestResults/rsa-di-full/*.trx` |

TRX已解析核对实际新增案例和零失败/跳过；UTF8无BOM/CRLF和git diff --check通过，当前master本地提交不推送。D15仍进行中，其他插件、私有CA成功链及真实两模式/OBProxy版本矩阵未完成；A05、C01和D01–D09仍开放。未操作信任根、部署数据库或其他电脑，未重跑打包/旧运行时/Framework；本地RSA环回不替代实库或生产验收。

## D01 OB20报文与校验子模块（2026-10-19）

从`8ba667b`继续D01，先实现内部Ob20Packet及Ob20Checksum，不改变连接选项、标准PacketChannel或握手行为。单帧7+24字节头与4字节尾、24位外长/请求ID、CRC16/ARC和raw CRC32C；ParseHeader在正文分配前核对身份、序号、头校验及正文上限。正文包含extra长度前缀与内容；严格检查extra边界，以借用原帧视图分别返回extra和MySQL字节，不二次复制正文。不解释legacy/new extra内容或未知flags/reserved，不把外层序号强行等同内层序号；实际压缩、last/reroute及追踪语义由后续模块处理。

独立夹具依据固定Java提交`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`的协议字段和原CRC类。用本机JDK21将只读参考CRC类编译到`TestResults/ob20-reference`，独立小端组装13字节MySQL帧，输出固定48字节OB20报文；同时确认ASCII输入123456789的CRC16为bb3d、raw CRC32C为58e3fa20。仓库仅保留字节/数值夹具，未复制参考实现或查表常量、未增加依赖；原校验器与产品往返不是同一实现。

首轮先写测试，因尚缺Ob20Packet/Ob20Checksum而编译失败（`TestResults/ob20-red.log`），并未执行测试或生成TRX。实现后首批31项定向通过（`ob20-targeted/*.trx`）；分析器指出ArraySegment不宜用Assert.Empty，改为Count断言，补足边界后最终完整回归零警告且40项全部实际执行。测试覆盖固定编码/解码、分段CRC、全位置截断/尾随、头/正文/尾篡改、独立关闭校验仍验证结构、恶意UInt32长度、空值、extra包含在上限内、24位极限、请求/序号边界、未知标志/保留字段和原帧视图。独立只读走查对照固定Java及关联协议检查，无必须修复项。

最终证据（Windows / SDK10.0.402 / .NET10.0.13）：

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| Release核心五目标、DI三目标 | 0警告、0错误 | `TestResults/ob20-build.log` |
| 完整Unit（OB20新增40项） | 1636通过，0失败/跳过 | `TestResults/ob20-unit-full/*.trx` |
| 完整Consumer（既有公共路径） | 332通过，0失败/跳过 | `TestResults/ob20-consumer-full/*.trx` |
| DI | 8通过，0失败/跳过 | `TestResults/ob20-di-full/*.trx` |

编译命令`dotnet build OceanBaseConnector.slnx -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false`；完整测试命令同前节，替换上述结果目录，最终TRX已解析核对计数及新增40项实际通过。格式/diff检查通过后本地提交，不推送。

D01保持进行中：能力协商、PacketChannel流式收发、取消/分片/last/reroute生命周期和完整链路追踪尚未接入；本地编解码不代表公开连接已支持OB20。后续继续这些可本地开发项，真实OceanBase/OBProxy与异机验收暂缓。未操作信任根、未部署数据库、未重跑打包/旧运行时/Framework，项目整体目标未完成。

## D01 OB20流式传输与能力协商（2026-10-19）

从`2796e78`接入PacketChannel、握手、物理会话、命令/prepare/状态探测和分页游标。新连接键Use OB20及builder属性UseOb20默认false，冻结、多地址及序列化均保持选项。只在协商连接属性时发送`__proxy_capability_flag=256`，认证完成、模式确定且服务端确认OB_PROTOCOL_V2后包装现有标准/TLS流；未请求、能力不足或无连接属性则使用标准流。认证包仍是标准帧，不放宽TLS、A05或Pooling限制，后续会话报告不改变已选协议。

Ob20Stream以固定64KiB写缓冲分帧、8KiB缓冲跳过extra，按调用方请求读取并增量CRC，不整帧预读；32MiB顺序消费分配回归通过。24位request ID逐请求递增回绕，OB20序号从0开始并由响应接续、按字节回绕；原MySQL分片/空终结独立保持。每个响应、prepare、探测和fetch边界均检查last及剩余正文；无响应COM_STMT_CLOSE单独结束。正文最后一段返回前验证尾CRC，extra参与校验并按长度跳过，未知标志/reroute/实际压缩失败，不重放已发SQL。cursor分页仅检查边界而不清SQL已发标记，后续错误保留结果未知语义；可信普通ERR完成响应后仍可复用连接。

红绿与走查：初始流式测试因缺实现编译失败（`ob20-stream-red.log`），首批40协商消费因未知选项失败（`ob20-negotiation-red/*.trx`）。中途Consumer单独构建未更新全部资产，导致24项消费失败；重建slnx后通过，未修改产品去适配旧资产。独立走查发现产品与模拟服务同用错误短属性`__proxy_capability`；对照固定Java基线SendHandshakeResponsePacket的常量，更正测试后20失败/20通过（`ob20-attribute-review-red/*.trx`），产品改长键并全目标重建后通过。新增ERR复用测试首轮失败是夹具漏掉错误后的时区/national探测，补齐精确SQL与探测结果后通过，没有放宽产品状态校验。两项测试编译/选项输入错误已修复，不作产品缺陷或通过证据。

本轮新增Unit26项：同步/异步、短读、不整帧预读、跨帧/extra、258帧序号回绕、CRC/last/尾随/身份/reroute/截断拒绝、固定写分片/无响应关闭、未消费响应禁止新请求、预取消、选项冻结，以及0xFFFFFF内层精确倍数空终结和257外帧共享序号。新增Consumer62项：五资产×同步/异步×启用/未请求/服务端不支持/无属性40项，公开命令CRC/last/尾随/超时/取消/关闭12项，可信ERR后复用及状态探测2项，cursor三页成功/第二页CRC/缺last/取消8项；消费宿主统一net10，不代表五运行时验收。cursor取消发生于第二次fetch已发出并等待响应期间，未验证已读部分OB20正文后的取消。

最终证据（Windows / SDK10.0.402 / .NET10.0.13）：

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| Release核心五目标、DI三目标 | 0警告、0错误 | `TestResults/ob20-stream-build-final.log` |
| 完整Unit（OB20共66，流式新增26） | 1662通过，0失败/跳过 | `TestResults/ob20-stream-unit-full/*.trx` |
| 完整Consumer（OB20新增62） | 394通过，0失败/跳过 | `TestResults/ob20-stream-consumer-full/*.trx` |
| DI | 8通过，0失败/跳过 | `TestResults/ob20-stream-di-full/*.trx` |

编译及完整测试命令沿用上一节，仅替换结果目录；TRX已解析核对总数及66/62项OB20实际执行。独立修复复查未发现必须修复项，文档与代码边界另复核后本地提交，不推送。未操作信任根或部署数据库/异机，未重跑打包/旧运行时/Framework；OB20经TLS的实际环回、完整链路追踪及真实版本/OBProxy矩阵仍开放。D01保持进行中，下一步继续可本地实现的完整链路追踪；A05/C01及其他D项、发布关口不据本轮关闭。

## D01 完整链路追踪本地实现（2026-10-19）

从`540689c`接入显式Enable Full Link Trace选项、握手扩展能力、Ob20TraceContext及现有命令/事务Activity上下文。默认关闭，要求Use OB20；仅请求时发送311552，服务端缺FULL_LINK_TRACE则保留普通OB20。legacy ObObj varchar和new-extra嵌套TLV均支持；首请求帧附加extra、CRC覆盖extra、响应尾CRC通过后才原子应用策略，单帧接收extra上限64KiB。level按signed byte解释，普通采样受level/采样率/记录策略约束，SHOW可独立启用；响应策略只影响后续请求，当前记录使用开始时快照。

wire trace ID关联已有Activity；无监听者时使用环境上下文或内部生成ID，prepare/探测/分页共享trace，每请求wire span独立；事务控制关联已有外层Activity。driver/SHOW记录只含固定模块、ID及时间，不发送SQL、参数、凭据、端点或任意Activity标签/baggage。只保留一个有界待发记录，随下一请求发送；关闭连接不额外刷新最后记录，不新增SQL或重放。服务端query时间字段只做边界校验，未导出指标或新增AppInfo定制API。

独立协议依据为固定Java基线`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`的FullLinkTrace、OceanBaseProtocolV20、ObObj和OceanBaseSerialize；只核对行为，不复制实现。用JDK21对原ObObj/OceanBaseSerialize及最小临时Buffer夹具执行，得到独立legacy/new-extra固定字节，输出`TestResults/ob20-trace-reference-output.log`，临时源码只存TestResults。

红绿证据：初始缺类型/选项编译失败见`ob20-trace-red.log`；未接入握手和资产时Consumer36失败/4通过见`ob20-trace-consumer-red/*.trx`。独立走查发现legacy键需忽略大小写、Java长度69采用C5 00有界非最短varint；先3失败后6通过见`ob20-trace-legacy-review-{red,green}/*.trx`。未知重复TLV误拒绝先2失败/11通过后13通过见`ob20-trace-boundaries-{red,green}/*.trx`。再次走查发现level无符号解释会将128/255启用，先4失败/2通过，再改signed byte，27项追踪定向全通过，见`ob20-trace-level-{red,green}/*.trx`。曾并发隐式build发生MSB3026重试及测试Read返回值CA2022，已改顺序build并修断言，最终编译无警告。

新增Unit27项覆盖独立Java字节、trace线序、冻结/依赖、逐字节截断、原子策略、恶意标量/重复/未知、0/1采样及SHOW、legacy长度/终止符、CRC后发布和128KiB三帧首帧extra。新增Consumer70项：五资产×同步/异步×legacy/modern/关闭/缺追踪能力/无监听者50项；prepare/两页cursor/无响应close/事务4项；畸形/超限/CRC/服务端仅发送部分正文后的在途取消16项（未确认客户端消费位置），确认Broken、结果未知、token及无重放。Consumer均为net10宿主，不能当作五运行时验收。

| 本轮检查 | 结果 | 证据 |
| --- | --- | --- |
| Release slnx核心五目标/DI三目标 | 0警告、0错误 | `TestResults/ob20-trace-build-final.log` |
| 完整Unit（本轮新增27项） | 1689通过，0失败/跳过 | `TestResults/ob20-trace-unit-final/*.trx` |
| 完整Consumer（本轮新增70项） | 464通过，0失败/跳过 | `TestResults/ob20-trace-consumer-final/*.trx` |
| 完整DI | 8通过，0失败/跳过 | `TestResults/ob20-trace-di-final/*.trx` |

编译使用`dotnet build OceanBaseConnector.slnx -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false`；测试使用`dotnet test tests/<工程>/<工程>.csproj -c Release --no-build --no-restore -m:1 -nr:false --logger trx --results-directory TestResults/ob20-trace-<unit|consumer|di>-final`。已解析TRX确认27/70项新回归均执行通过。完整Unit使用正常Windows TLS凭据访问，未修改信任库或证书策略；未重跑打包/旧运行时/Framework。

最终独立文档复查已将取消证据收窄到服务端发送位置，不宣称客户端已读两字节。D01本地追踪实现、编译、回归及独立走查已完成，正式状态待验证：真实采样/SHOW TRACE、OB20经TLS环回与目标版本/OBProxy矩阵仍开放；最后待发记录不在关闭时刷新。异机及数据库部署按要求暂缓。A05公共MySQL判定、C01池、D02–D09、D15其他插件和发布审查仍未完成，整体目标不关闭，不推送。

## D02 Oracle PrepareExecute报文子模块（2026-10-19）

从`f6ba0e1`开始D02，先实现内部OraclePrepareExecutePacket；公开CommandType、ParameterDirection及标准查询执行行为不变。请求复用既有PreparedParameters编码和长度编码字符串写入，0xA1/statement ID/iteration=1/非游标/默认模式/无捆绑关闭/SQL rawCRC32C/保留字段与固定Java基线一致；UTF-8字节数及参数编码总长在SQL字节和整包分配前验证64MiB上限，数量最多65535。响应仅解析17字节扩展头，严格marker/filler/布尔/完整长度/4096列上限，保留未知uint flags，不分配metadata数组或据头OK完成命令。

依据只读Java提交`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`的ComStmtPrepareExecute.send/read、JDBC4ServerPreparedStatement.calculateCheckSum和原OceanBaseCRC32C。独立请求夹具按参考字段顺序手工构造，由JDK21编译原CRC类计算校验，临时源码与字节输出在`TestResults/oracle-prepare-execute-reference/`及`TestResults/oracle-prepare-execute-reference.log`；未实际执行Java ComStmtPrepareExecute.send，不复制源码/常量到产品，不把夹具当实库互通。

先添加测试，因缺OraclePrepareExecutePacket编译失败，见`TestResults/oracle-prepare-execute-red.log`；实现后定向21项全通过，见`TestResults/oracle-prepare-execute-first/*.trx`。覆盖独立固定请求、Unicode与原SQL校验、既有Int64/NULL参数字节、0/250/251/65535/65536/16777216字节长度前缀、计数/维度/64MiB超限、非法Unicode、17字节头所有截断/尾随、坏marker/filler/布尔/列数、65535参数及未知扩展位。独立只读代码走查未发现必须修复项，确认整体长度公式与原CRC初值/终值一致。

| 本轮检查 | 结果 | 证据 |
| --- | --- | --- |
| Release slnx核心五目标/DI三目标 | 0警告、0错误 | `TestResults/oracle-prepare-execute-build.log` |
| 完整Unit（本轮新增报文21项） | 1710通过，0失败/跳过 | `TestResults/oracle-prepare-execute-unit-full/*.trx` |
| 完整Consumer | 464通过，0失败/跳过 | `TestResults/oracle-prepare-execute-consumer-full/*.trx` |
| 完整DI | 8通过，0失败/跳过 | `TestResults/oracle-prepare-execute-di-full/*.trx` |

编译命令沿用上一节；测试为`dotnet test tests/<工程>/<工程>.csproj -c Release --no-build --no-restore -m:1 -nr:false --logger trx --results-directory TestResults/oracle-prepare-execute-<unit|consumer|di>-full`。已解析TRX确认新增21项均实际执行；本轮没有新的公开Consumer案例，完整Consumer通过只证明既有功能未回归，不代表Oracle PrepareExecute网络执行通过。完整Unit使用正常Windows TLS凭据访问，未操作信任根，未重跑打包/旧运行时/Framework。

报文子模块已本地实现、测试及独立走查；D02保持进行中。后续继续参数/列元数据、内部/普通结果及最终OK/ERR、输出原子发布、事务/reader/statement所有权与故障清理，公开存储过程/OUT/RETURNING仍不支持，不能据此关闭D02或项目。实库和异机联调按要求暂缓，不推送。

## D02 扩展响应流与执行终态（2026-10-19）

从`8c4877d`继续D02，内部QueryResult新增显式Oracle参数数量入口，仅允许Oracle二进制非游标响应。消费17字节扩展头、核对参数数目，参数定义与结果定义共享4MiB预算，处理可选参数/列EOF，直接读取头指定的列和二进制行。有/无结果均须读取独立最终OK/ERR；普通及SequentialAccess的行终结不提前释放租约，尾OK累计更新计数并观察会话，尾MoreResults继续普通结果。现有可信ERR保持已知结果并允许复用；畸形、取消、超时和提前关闭未读尾包沿既有未决交换清理，不重放SQL。

先添加环回测试，缺少构造参数及OracleHeader导致编译失败，见`TestResults/oracle-execute-response-red.log`。新增48项响应回归，覆盖同步/异步、两EOF格式、普通/顺序、有/无结果、尾包前租约保持、错误响应、可信尾ERR后复用、后续多结果/更新计数、会话时区快照、提前关闭、取消/超时及对端断开。初次6项取消/超时测试失败是夹具只捕获EndOfStreamException，而PacketChannel将EOF映射为ProtocolException，TCP重置为IOException；修正测试断言后通过，不修改产品异常映射。新增早关闭测试曾与服务端写行终结竞争，已用发送完成屏障消除竞争，取消夹具同样等待该屏障，不以主动清理后的断开作为证据。

| 本轮检查 | 结果 | 证据 |
| --- | --- | --- |
| Release slnx核心五目标/DI三目标 | 0警告、0错误 | `TestResults/oracle-execute-response-build.log` |
| 定向报文/响应 | 69通过，0失败/跳过 | `TestResults/oracle-execute-response-reviewed/*.trx` |
| 完整Unit（本轮新增响应48项） | 1758通过，0失败/跳过 | `TestResults/oracle-execute-response-unit-full/*.trx` |
| 完整Consumer | 464通过，0失败/跳过 | `TestResults/oracle-execute-response-consumer-full/*.trx` |
| 完整DI | 8通过，0失败/跳过 | `TestResults/oracle-execute-response-di-full/*.trx` |

编译命令沿用上一节；测试使用`dotnet test tests/<工程>/<工程>.csproj -c Release --no-build --no-restore -m:1 -nr:false --logger trx --results-directory TestResults/oracle-execute-response-<unit|consumer|di>-full`。独立只读走查核对Java响应布局、同步/异步和顺序读取、尾包租约、MoreResults、ERR/未知结果、会话快照及默认路径，未发现必须修复项。完整Unit使用正常Windows TLS凭据访问，未修改证书信任或验证策略；Consumer为既有公开功能回归，不能证明公开PrepareExecute支持。

内部响应子模块已实现并本地验证；尚未解释内部输出结果标志或发布OUT值，公开CommandType.StoredProcedure和非Input参数方向仍拒绝。后续继续公开入口、可信版本/选项门槛、输出原子发布及事务/statement所有权接入。未验证PrepareExecute的OB20环回、实库、异机、打包/旧运行时/Framework，本轮不把这些列为通过；D02及整体目标保持进行中，不推送。


## D02 公开过程与标量输出接入（2026-10-19）

从`821e828`继续，新增默认关闭的`Use Oracle Prepare Execute`/UseOraclePrepareExecute；仅已认证Oracle会话的StoredProcedure和含输出方向的Text使用扩展。首次执行在同一租约/预算查询`select ob_version() from dual`，严格数字点分版本须至少2.2.76；成功探测的支持/不支持结果缓存物理会话，ERR不缓存，不信握手兼容版本。版本探测失败不标记为用户SQL结果未知，不回退重试。过程/函数只接受安全限定标识符并生成绑定PL/SQL块，单个ReturnValue排在首绑定；输入快照，InputOutput保留输入，纯OUT/ReturnValue发送NULL，重复输出位置发包前拒绝。

公开层只接受扩展flags 0、1、3、4，1/3 RETURNING及4 PL OUT作为内部输出隐藏；列数匹配，复用服务端元数据解码。标量至多一行，零行填DBNull，多行及array binding明确拒绝；本路径独立Prepare及FetchSize不支持。所有输出暂存，全部结果和OB20边界成功后、租约释放与日志回调前一次发布。尾ERR、后续ERR、取消/超时、解码失败、未读尽即关闭保留旧值；完整消费时可在reader关闭前发布。事务沿显式绑定规则，响应statement ID立即入延迟关闭队列，下次请求或命令空闲清理刷新，不缓存复用扩展ID。

先验证缺实现的plan编译失败及公开选项失败，证据为`TestResults/oracle-command-plan-red.log`和`oracle-procedure-public-red.log`。独立走查发现P1：最终OK已接收后，取消/关闭先破坏会话，完成路径可能跳过边界仍发布输出。修复成功发布前的预算token及Busy检查，之后验证响应边界再发布；确定性同步/异步、取消/关闭4项修前全失败、修后全通过，见`TestResults/oracle-output-commit-{red,green}.log`及对应TRX。复查接受修复，未发现新增必须修复项。

本轮实际验证：

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| Release slnx核心五目标/DI三目标 | 0警告、0错误 | `TestResults/oracle-procedure-build-final.log` |
| 定向Oracle回归 | 151通过，0失败/跳过 | `TestResults/oracle-procedure-targeted-final-fixed/*.trx`；其后增强InputOutput固定报文断言，已由完整Unit覆盖 |
| 完整Unit | 1840通过，0失败/跳过 | `TestResults/oracle-procedure-unit-full/*.trx` |
| 五资产Consumer | 494通过，0失败/跳过 | `TestResults/oracle-procedure-consumer-full/*.trx` |
| DI | 8通过，0失败/跳过 | `TestResults/oracle-procedure-di-full/*.trx` |

编译命令为`dotnet build OceanBaseConnector.slnx -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false`；完整测试为`dotnet test tests/<工程>/<工程>.csproj -c Release --no-build --no-restore -m:1 -nr:false --logger trx --results-directory TestResults/oracle-procedure-<unit|consumer|di>-full`。已解析TRX确认全部实际Passed及4项发布竞态执行。新增82项Unit覆盖安全绑定、函数/过程、零参数、两EOF、同步/异步/Sequential、输出暂存、多结果、RETURNING零/一行与InputOutput固定报文、非法输出、版本缓存/ERR/畸形/取消/超时、事务与statement清理。新增30项Consumer在五资产用公共DbConnection/DbCommand验证标准/OB20成功，以及OB20完整尾OK后夹带额外包时拒绝且不发布旧OUT。

完整Unit使用正常Windows TLS凭据权限，未安装/删除信任根或改变验证策略；本轮未重跑打包、旧运行时/Framework，也未做扩展执行经TLS环回、实库或异机验证。D02保持进行中，不宣称无限制过程或复杂输出兼容；后续REF CURSOR、LOB、复杂类型与数组绑定分属D03–D06。A05、C01、D03–D09、D15其他插件及发布门槛仍开放，整体目标未完成，不推送。

最终独立文档/增量复查通过，无必须修复项；复查者另行只读解析完整TRX及4项发布竞态，核对版本门槛、输出边界与标量/Prepare/FetchSize限制一致，未扩大实库声明。


## D03 REF CURSOR 输出与流式读取（2026-10-19）

从`efb2ac3`继续，实现Oracle REF CURSOR输出的本地公开路径：追加OceanBaseDbType.RefCursor（DbType.Object），Output/ReturnValue且Size/Precision/Scale为0；拒绝输入与输入输出，纯输出发NULL。Oracle类型163使用固定4字节LE游标ID，非NULL输出暂存为OceanBaseRefCursor，和标量一起等待所有响应/协议边界成功后原子发布；NULL或零行输出仍为DBNull。普通结果列非NULL cursor值明确拒绝，不把ID当普通整数使用。

GetDataReader/GetDataReaderAsync每个对象只打开一次reader，默认128行每页，正fetchSize，可用Default/SequentialAccess/CloseConnection。固定原物理会话、事务引用和连接事务代次，复用现有独占租约；获取租约失败或调用时token已取消不消耗打开机会。事务结束、后来事务周期及连接关闭重开后不得复用旧游标；多个游标依次读取，重复ID输出别名共享同一对象。独立fetch沿原命令超时，复用时区/national状态探测与逐页状态快照。9字节COM_STMT_FETCH首响应直接消费metadata和行，后续页继续既有元数据一致性、进展、行数和LastRowSent检查；默认/Sequential不缓存整个结果。

OracleCursorOwner保留父statement直到本次所有游标释放，parent command.Dispose不破坏已返回游标；读尽、失败和Dispose原子一次释放，先游标ID再最后父statement进入既有延迟关闭队列，空闲Dispose刷新。未读取对象须Dispose，重执行不替调用方释放旧输出；连接关闭释放服务端资源。未读尽即关闭reader/游标、fetch ERR或畸形/断网均销毁原会话。后续fetch不再执行业务SQL，其取消、超时和错误不把已完成的过程改判结果未知，真实ERR保留错误码。

协议依据为只读Java固定提交`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`：ColumnType.CURSOR=163，BinaryRowProtocol.getInternalComplexCursor读取4字节ID，RefCursor/JDBC4ServerPreparedStatement使用独立COM_STMT_FETCH/COM_STMT_CLOSE。只核对行为及字段，不复制Java实现，不将本地夹具声称为真实互通。

TDD首轮因缺RefCursor类型/对象编译失败（`TestResults/ref-cursor-red.log`），基础8项绿后补32项所有权/故障覆盖（`ref-cursor-ownership/*.trx`）。独立走查发现终态竞态：最终fetch包已接收，取消或关闭先破坏会话，旧Complete会跳过边界并成功结束。新4项确定性同步/异步×取消/关闭修前全失败；OwnCursor新增仅REF CURSOR独立fetch的终态保护，与已有输出hook共享预算/Busy检查，保持C04用户SQL可信终态优先规则。修后新4项及原C04终态4项全部通过，见`TestResults/ref-cursor-terminal-{red,green}.log`及TRX；独立复查接受修复。

新增五资产30项Consumer覆盖同步/异步、标准/OB20及最后fetch尾包夹带额外内层包拒绝；父command已释放后仍可读取，精确核对fetch和游标/父statement关闭。首次10失败来自测试服务端连续两个无响应close间未重置标准包序号，仅修夹具；最终定向30项全过，见`TestResults/ref-cursor-consumer-{first,fixed}.log`及TRX，不改变产品序号或边界验证。


修后最终验证如下：

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| Release核心五目标/DI三目标 | 0警告、0错误 | `TestResults/ref-cursor-build-reviewed.log` |
| 完整Unit | 1876通过，0失败/跳过 | `TestResults/ref-cursor-unit-reviewed/*.trx`；新增REF CURSOR 36项均Passed |
| 五资产Consumer | 524通过，0失败/跳过 | `TestResults/ref-cursor-consumer-reviewed/*.trx`；新增30项 |
| DI | 8通过，0失败/跳过 | `TestResults/ref-cursor-di-reviewed/*.trx` |

编译为`dotnet build OceanBaseConnector.slnx -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false`，测试为`dotnet test tests/<工程>/<工程>.csproj -c Release --no-build --no-restore -m:1 -nr:false --logger trx --results-directory TestResults/ref-cursor-<unit|consumer|di>-reviewed`。已逐项解析TRX确认新36项及4项终态竞态均实际执行；Unit涵盖原D02输出提交与C04可信SQL终态契约。完整Unit使用正常Windows TLS凭据权限，未操作信任根或改变验证策略。

D03本地输出契约已实现，正式状态待验证；实库、扩展经TLS、异机、打包和旧运行时/Framework本轮均未执行。不支持REF CURSOR输入/输入输出和普通结果列非NULL cursor值；LOB/复杂类型/批量绑定/客户端回退/拓扑/XA仍按D04–D09继续，A05/C01/D15余项和发布验收仍开放，整体目标未完成，不推送。

最终独立代码/文档复查通过，无必须修复项；复查者只读核实修后完整报告、36项REF CURSOR与4项终态竞态，确认声明和未验收边界一致。


## D04 LOB locator 内部解析（2026-10-19）

从`e4af17b`开始D04，新增内部LobLocator，仅处理协议边界和数据位置，不改变握手能力、210/211结果列、参数或公开流接口。V1核对固定Java例子的40字节头与17字节rowid，支持空/兼容locator；V2核对固定服务端ObLobCommon/内存头，覆盖simple、extern、未初始化/初始化disk、disk行外但payload行内、纯行外及空extern synthetic头。内联值保留原数组的ArraySegment，不复制正文；纯行外仅保留Int64字节长度，超过4GiB和Int64最大值不据此申请正文内存。TEMP_FULL可解析，TEMP_DELTA/未知布局和V1非零storage option明确拒绝，不猜测老V2 magic布局。

先加入测试并记录缺少LobLocator的编译失败（TestResults/lob-locator-red.log），实现后首轮31通过/2失败：xUnit把默认ArraySegment当集合枚举而抛错，已将测试改为分别断言Array=null与Count=0，产品没有为该错误断言调整。再补空extern两个合法偏移与TEMP_FULL/DELTA后，最终37项定向全部通过（TestResults/lob-locator-green/*.trx）。回归包括所有固定长度locator的逐字节截断、偏移溢出/重叠、尾随数据、畸形memory/disk flags、freed、负snapshot、Int64溢出、元数据与实际payload长度不符及opaque扩展/rowkey边界。

独立只读走查核对全部实现、测试与固定C++的fill/get_inrow_data/get_lob_data_byte_len，确认空extern两种偏移、借用视图始终绕过metadata及偏移算术边界，无必须修复项。Java checkout实际HEAD为6adc311ba5db8b2f19f98f3e6e4734667fdfdda1，服务端引用固定为5e8a63d29dae9fde5e962b8fcc7b167d438fc343；只参考协议行为，没有复制第三方实现。

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| Release核心五目标/DI三目标 | 0警告、0错误 | TestResults/lob-locator-build.log |
| 完整Unit | 1913通过，0失败/跳过，包含新增37项 | TestResults/lob-locator-unit/*.trx |
| 五资产Consumer | 524通过，0失败/跳过 | TestResults/lob-locator-consumer/*.trx |
| DI | 8通过，0失败/跳过 | TestResults/lob-locator-di/*.trx |

编译命令为`dotnet build OceanBaseConnector.slnx -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false`；完整测试命令为`dotnet test tests/<工程>/<工程>.csproj -c Release --no-build --no-restore -m:1 -nr:false --logger trx --results-directory TestResults/lob-locator-<unit|consumer|di>`。已解析TRX核对实际通过/执行计数，Unit使用正常Windows TLS凭据权限，未安装/删除信任根或改变TLS策略。UTF8无BOM/CRLF、git diff --check及独立走查通过后本地提交，不推送。

D04整体仍进行中，下一步继续显式能力/选项、210/211类型及参数/输出对象、原物理会话/事务绑定、DBMS_LOB.READ分块流与公开消费。未验证实库/OBProxy、异机或扩展经TLS；本轮未重新打包或执行旧运行时/Framework矩阵。内部解析不能证明公开LOB可用或完整D04已交付，整体目标保持开放。
