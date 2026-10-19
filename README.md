# OceanBaseConnector for .NET

面向 OceanBase 的实验性 ADO.NET 驱动，使用一个 provider、一个协议栈和每连接认证后的模式状态处理 MySQL / Oracle 兼容模式。可选 DI 包提供普通和 keyed 数据源注册。

**当前为 `0.1.0-alpha` 开发版本，尚未通过真实数据库互通验收，也未发布候选版。** 本地单元、协议环回和包消费测试不代表生产兼容证明。

## 包与目标框架

| 包 | 目标资产 | Microsoft 通用库 |
| --- | --- | --- |
| `OceanBaseConnector` | netstandard2.0、netstandard2.1、net8.0、net9.0、net10.0 | 两个 Standard / net8 使用 8.x；net9 使用 9.x；net10 使用 10.x |
| `OceanBaseConnector.DependencyInjection` | net8.0、net9.0、net10.0 | 分别使用 8.x、9.x、10.x，精确依赖同版本核心包 |

两个包统一版本、统一打包。核心仅依赖日志抽象，两个 Standard 资产另依赖 DiagnosticSource 8.0.1；现代资产使用运行时诊断 API。核心不要求 DI 容器或日志后端。Standard 目标保留基础 ADO.NET；`DbDataSource`、`DbBatch` 及可选 DI 仅在三个现代目标暴露。Standard2.1 对平台已提供的 prepare、事务和资源清理异步入口另有覆盖。

## 当前能力和边界

本地已实现并测试：同步/异步连接与严格 TLS、参数校验、文本及服务端预处理执行、多结果 reader、精确数值和日期时间、事务、取消/超时、顺序字段流、只读分页游标、首批目录集合、MySQL 数据库切换、逐条 batch、数据源/DI/日志和本地追踪指标。

- 公共握手目前只接受能通过认证后状态确认的 Oracle 模式。**缺少可信的 MySQL 模式判定规则，公共 MySQL 模式连接仍被拒绝。** MySQL 的本地测试使用显式认证后夹具；设置 `ExpectedMode=MySql` 不会绕过判定。
- `Pooling=true` 被拒绝，完整会话重置及版本白名单尚未验证。默认不使用连接池。
- 只支持文本命令和输入参数，参数化执行使用服务端 prepare；不自动回退客户端 SQL 插值。不支持环境 `TransactionScope`、存储过程/OUT、REF CURSOR、locator LOB、ARRAY/STRUCT、XA、OB20 或拓扑发现。
- 首批 `GetSchema` 集合为 `MetaDataCollections`、`DataSourceInformation`、`Tables`、`Columns`；Oracle 模式不支持 `ChangeDatabase`。
- batch 逐条执行，不隐式创建事务，没有整批原子性保证。需要原子性时由调用方显式绑定本地事务。
- MySQL 四位 YEAR 已实现本地编解码、元数据及参数支持：`ushort`，0 或 1901–2155，显式参数类型 `OceanBaseDbType.Year`；无两位年份推断。真实往返待验收，公共 MySQL 建连仍受 A05 门槛限制。
- MySQL BIT(1–64) 已实现本地编解码、元数据及参数支持：统一使用 `ulong`，BIT(1) 也不转 bool。显式 `OceanBaseDbType.Bit` 参数的 `Size` 为位宽，0 表示 64；超出声明位宽在发包前失败，`Precision`/`Scale` 仅支持 0。真实往返待验收，公共 MySQL 建连仍受 A05 门槛限制。
- MySQL JSON 已实现本地文本/预处理读写与元数据，使用 `string` 和严格 UTF-8，保留 JSON 文本 `null` 与 SQL NULL 的区别；支持顺序 `GetTextReader`。显式 `OceanBaseDbType.Json` 仅接受字符串/NULL，`Precision`/`Scale` 仅支持 0；语法由服务端验证，不隐式序列化对象。真实往返待验收，公共 MySQL 建连仍受 A05 门槛限制。
- Oracle INTERVAL 的本地编解码、元数据和参数支持使用 `OceanBaseIntervalYearToMonth` / `OceanBaseIntervalDayToSecond` 精确值，保留符号、前导精度和纳秒；标准/provider-specific 访问均返回对应结构。显式类型或 `DbType.Object` 下的精确值可绑定，`Precision`/`Scale` 不允许舍入；`ToTimeSpan()` 只在范围与 ticks 精度内成功，年月区间不转固定时长。真实往返待验收。
- Oracle NCHAR/NVARCHAR2 已实现本地文本/预处理读写及顺序字符流：按独立会话 national 编码处理 UTF-8/UTF-16BE，保留尾部空格，未知编码明确失败；真实列往返及版本兼容性仍待验收。

## 使用入口

所有资产均可通过标准 factory 创建对象；下面只创建连接，不访问服务器：

```csharp
using OceanBaseConnector;

using var connection = OceanBaseProviderFactory.Instance.CreateConnection();
connection.ConnectionString = "Server=localhost;User ID=test_user;Password=replace-me;ExpectedMode=Oracle";
using var command = connection.CreateCommand();
command.CommandText = "select 1 from dual";
```

在 .NET 8/9/10 应用中可共享数据源：

```csharp
using OceanBaseConnector;

using var source = new OceanBaseDataSourceBuilder(connectionString).Build();
using var connection = source.CreateConnection();
// await connection.OpenAsync(cancellationToken);
```

引用可选 DI 包后，在已有宿主的服务集合注册 `services.AddOceanBaseDataSource(connectionString)`，普通的 `OceanBaseDataSource` 与 `DbDataSource` 解析为同一单例。`AddKeyedOceanBaseDataSource(key, connectionString)` 提供独立的 keyed 单例。数据源由容器释放；调用方释放自己借出的连接。可通过 builder 的 `UseLoggerFactory` 接入宿主日志。

默认 `TLS Mode=VerifyFull` 验证系统信任链及主机名，TLS 失败不降级。连接串 builder 输出和打开前的连接串包含调用方提供的凭据，应由应用保管；数据源及打开后的连接默认隐藏密码，显式 `Persist Security Info=true` 可保留。驱动日志/诊断不记录 SQL、参数值或凭据。

当 `OceanBaseException.IsExecutionOutcomeUnknown` 为 true 时，已发送的 SQL 可能已执行。该物理会话被销毁，驱动不自动重放 SQL；应用应根据业务幂等性和事务结果核对状态，不应盲目重试。

## 故障与结果未知处理

先判断失败发生在哪一步，再结合异常和业务操作记录处理。`IsExecutionOutcomeUnknown=false` 仅表示未被驱动标记为结果未知，不保证语句没有副作用，也不构成重试许可。连接的 `State=Open` 不能单独证明 reader 已读完、事务已结束或上一条业务操作成功。

| 场景 | 当前驱动行为 | 应用处理 |
| --- | --- | --- |
| 参数/选项验证失败，或执行入口收到预取消 token | 入口拒绝；预取消不发送业务 SQL | 修正输入或取消来源；不要把验证错误列为瞬时故障反复重试 |
| SQL 可能已发送，连接断开、取消或超时且没有可信终态 | 抛出 `OceanBaseException`，`IsExecutionOutcomeUnknown=true`，销毁原物理会话；取消/超时原因在 `InnerException` | 保存业务操作标识及状态，释放旧连接；根据业务记录从新连接核对结果，禁止直接重放写操作、COMMIT 或整个 batch |
| 收到可信服务端 ERR | 保留 `ServerErrorCode`/`SqlState`，未知标记为 false；普通非游标命令可结束交换并释放租约，游标 ERR 会销毁会话 | 按错误和事务状态处理；错误码不能证明之前已完成的批次前缀没有生效 |
| 关闭尚未消费完响应的 reader | 关闭 reader 会销毁未完成交换的物理会话；`Close`/`Dispose` 可能正常返回，未知状态通过日志/诊断报告 | 若需要证明结果完成，先读尽当前结果和后续所有结果；不能把释放成功当作 SQL 成功或已回滚的证明 |
| COMMIT/ROLLBACK 的终态已确认，随后恢复自动提交失败 | 返回已确认的提交/回滚结果，结束该事务并销毁会话；另记录 `TransactionRestoreAutocommitFailed` | 保留已确认结果，释放连接后重新建连；不要因会话清理失败重跑已提交业务 |
| COMMIT 收到可信 ERR | 会话仍为 Open 且事务仍活动时，保留事务供显式回滚；会话已被销毁时解除事务关联；若响应不可信则按结果未知处理 | 会话仍可用时按事务状态显式回滚；否则重新连接核对业务状态，不在同一失败事务中盲目重试提交 |

单独释放字段流或字段 TextReader 不代表命令完成；所属 reader 仍须读尽所有结果或关闭。上表的取消/超时不是服务端 `KILL QUERY` 完成证明；本地中断传输不能证明服务器停止执行。显式 `Prepare` 阶段失败也可能销毁会话，但不能据此认定业务 Execute 已发生。`Cancel()`、关闭连接和超时都不会触发驱动自动重放。

批次逐条执行；后续条目停止不撤销已经完成的前缀。现代目标的 `DbException.BatchCommand` 可定位关联原始条目，条目更新计数和 `RecordsAffected` 只描述已知计数；查询/未执行条目可能同为 `-1`，不能用它构造自动断点续跑。需要整批原子性时，执行前显式绑定同一连接的本地事务，并处理提交本身的未知结果。

多地址只在 TCP 建连故障时按配置顺序尝试，共享总连接预算。选中端点后的 TLS/认证失败，以及已发送 SQL 的失败，都不会透明切换到另一个端点继续执行。`ExpectedMode` 是认证结果的交叉检查，不能用于强制或猜测模式。

排障可记录驱动版本、应用目标/实际运行时、脱敏端点和模式、异常类型、错误码/SQLSTATE、未知标记、取消/超时原因类别及业务操作标识。不要把密码、原始连接串、SQL/参数值或未经脱敏的抓包写进报告。连接、命令、事务日志类别分别为 `OceanBaseConnector.Connection`、`.Command`、`.Transaction`；日志可包含端点和耗时，不携带原始异常文本。`ActivitySource`/`Meter` 名称均为 `OceanBaseConnector`，指标为 `oceanbase.client.operations` 和秒单位 `oceanbase.client.operation.duration`，固定标签仅包含操作、模式、结果和未知标记。监听器回调失败会被隔离；诊断通道首次初始化失败时，该通道可能在当前程序集生命周期内不可用，不能根据缺少 span 推断 SQL 没有执行。

在本次验证的 .NET 8.0.31 中，应用自行创建的 `MeterListener` 若未设置 `MeasurementsCompleted`，释放时可能仍保留已启用的指标订阅，残留回调会影响后续采集和无监听开销。宿主应设置该完成回调（无清理状态时可为空操作），或在释放前显式调用 `DisableMeasurementEvents` 解除订阅；驱动不拥有或释放应用的监听器。

## 兼容性证据

| 部署组合 | 真实握手/TLS/SQL/事务验收 |
| --- | --- |
| MySQL 模式，直连 | 未验证，公共握手另有上述判定缺口 |
| Oracle 模式，直连 | 未验证 |
| MySQL 模式，经 OBProxy | 未验证，公共握手另有上述判定缺口 |
| Oracle 模式，经 OBProxy | 未验证 |

OceanBase 和 OBProxy 最低版本均未确定，没有已验证的版本白名单。上述每个部署组合的 TLS/认证互通仍未验证；当前本地实现的限制如下，不代表该组合已通过实库验收：

| TLS 配置 | `mysql_native_password` | `mysql_clear_password` / `sha256_password` | `caching_sha2_password` |
| --- | --- | --- | --- |
| `VerifyFull`（默认） | 本地实现 | 仅在身份验证通过的 TLS 后发送密码 | fast auth 与 TLS 完整密码路径本地实现 |
| `Required` | 本地实现，不验证身份 | 拒绝发送密码 | 仅 fast auth；服务端要求完整密码则拒绝 |
| `Disabled` | 本地实现，不建立 TLS | 拒绝发送密码 | 仅 fast auth；服务端要求完整密码则拒绝 |

以上均未经实库验证。SHA-2 使用严格 UTF8，拒绝无效 Unicode；连接串拒绝内嵌 NUL。fast auth 成功消息后仍须等待最终 OK，插件切换复用相同安全检查。RSA 公钥认证/检索及其他认证插件尚不支持；SHA-2 的 VerifyFull 完整密码分支目前只有状态/编码测试，没有私有 CA 成功链证据。证书故障应核对信任链、端点主机名及 `TLS Server Name`，驱动不会自动降低 TLS 要求。即使插件/TLS 条件满足，公共 MySQL 模式判定缺口仍会阻止该模式连接。

Windows 上已在实际 .NET 8.0.31、9.0.20 各执行完整本地 Unit 1206 项和 DI 8 项，全部通过且无跳过，另有实际 .NET 8/9/10 包消费样例。日常测试项目仍统一声明 net10.0；8/9 使用临时构建属性验证，Microsoft 通用库依赖分别保持 8/9 主版本。非 Windows、真实数据库和完整旧运行时兼容矩阵尚未验收。.NET Standard 资产的存在不代表所有实现该标准的运行时都已验证。详见仓库 `docs/obconnector-dotnet-progress.md`，设计与实施范围见同目录的架构设计和开发方案。

## 0.1.0-alpha 交付与回退说明（未发布）

当前交付是源码和本地生成的两个同版本包，功能及未支持项见上文。没有已验证的正式版或候选版升级来源，不承诺与 Java JDBC API、官方两个独立 .NET 驱动的连接串或程序集二进制兼容；不能仅替换 DLL 就假定应用无需修改。发布前仍需完成 R01 许可/来源/包名审查和 G1–G3 的真实兼容验收，本地包验证通过不授权发布。

升级前保留当前应用完整发布目录、包版本/还原锁定信息、CPM 配置和脱敏运行配置。核心和 DI 包一起升级至同一版本，按应用目标保持 Microsoft 通用库主版本对应；不要只替换其中一个包，也不要因使用 .NET 10 SDK 而把旧目标依赖统一升级到 10.x。先在目标环境验证连接、参数化 SQL、类型精度、reader 清理和显式事务，再决定是否放量；当前缺少这些实库证据，不能将开发验证称为放量批准。

需要回退时，先停止产生新的业务操作并处理在途 reader/事务，保留未知结果的业务核对记录，再整体恢复此前**已验证**的应用发布包及其依赖和配置。在没有已验证上版时，应停止放量并排查，不能把任意历史 Git 提交当作安全回退版本。回退驱动不会撤销已提交的数据、自动完成未知事务或回滚应用执行过的 DDL；这些状态必须单独核对。数据源由 DI 容器持有时，协调宿主生命周期重建容器/数据源；释放数据源不会强制关闭已经打开的连接，不应在仍有在途操作时仅替换 DLL。

## 本地构建和包验证

需要 .NET 10 SDK；完整包消费验证还需要 PowerShell 7.1+ 和实际 .NET / ASP.NET Core 8、9、10 运行时。日常测试宿主均为 .NET 10。

```powershell
dotnet restore OceanBaseConnector.slnx
dotnet build OceanBaseConnector.slnx -c Release --no-restore
dotnet test tests/OceanBaseConnector.UnitTests/OceanBaseConnector.UnitTests.csproj -c Release --no-build --no-restore
dotnet test tests/OceanBaseConnector.ConsumerTests/OceanBaseConnector.ConsumerTests.csproj -c Release --no-build --no-restore
dotnet test tests/OceanBaseConnector.DependencyInjection.Tests/OceanBaseConnector.DependencyInjection.Tests.csproj -c Release --no-build --no-restore
dotnet pack OceanBaseConnector.slnx -c Release --no-build --no-restore -o TestResults/packages
pwsh -NoProfile -File eng/Verify-Packages.ps1 -PackageDirectory TestResults/packages
```

Windows 上安装 .NET Framework 4.8 targeting pack 和兼容桌面运行时后，可给最后一条命令追加 `-VerifyNetFramework`。验证器临时生成 net48 宿主，加载本次核心包的 netstandard2.0 资产并检查基本公开契约；宿主自动生成的依赖绑定重定向需随应用配置一起交付。本机实际为 Framework 4.8.1（Release 533509），本项仅有基础包消费证据，未覆盖原版4.8、完整SQL/TLS或实库；正式Framework支持仍未确定。仓库日常测试工程继续声明net10.0。

包验证器核对包内容、依赖和实际还原的 SHA512，在独立缓存中只通过本地包消费五个资产，运行时不连接数据库。Standard 样例在隔离的 .NET 10 宿主运行；现代样例分别使用实际 .NET 8/9/10。生成目录和还原证据保留在 `TestResults/package-validation/`。这项检查不会发布任何包。

项目开源许可证尚待维护者确定，R01 许可和包名/商标审查未关闭。第三方依赖和参考来源见包内 `THIRD-PARTY-NOTICES.md`；参考实现的许可证不自动成为本项目许可证。
