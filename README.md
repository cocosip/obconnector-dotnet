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
- Oracle NCHAR/INTERVAL、MySQL JSON/BIT/YEAR 仍为规划能力，未支持类型会显式失败。

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

## 兼容性证据

| 部署组合 | 真实握手/TLS/SQL/事务验收 |
| --- | --- |
| MySQL 模式，直连 | 未验证，公共握手另有上述判定缺口 |
| Oracle 模式，直连 | 未验证 |
| MySQL 模式，经 OBProxy | 未验证，公共握手另有上述判定缺口 |
| Oracle 模式，经 OBProxy | 未验证 |

Windows 上的本地协议测试及部分实际 .NET 8/9/10 消费样例已有证据；非 Windows 和完整旧运行时兼容矩阵尚未验收。.NET Standard 资产的存在不代表所有实现该标准的运行时都已验证。详见仓库 `docs/obconnector-dotnet-progress.md`，设计与实施范围见同目录的架构设计和开发方案。

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

包验证器核对包内容、依赖和实际还原的 SHA512，在独立缓存中只通过本地包消费五个资产，运行时不连接数据库。Standard 样例在隔离的 .NET 10 宿主运行；现代样例分别使用实际 .NET 8/9/10。生成目录和还原证据保留在 `TestResults/package-validation/`。这项检查不会发布任何包。

项目开源许可证尚待维护者确定，R01 许可和包名/商标审查未关闭。第三方依赖和参考来源见包内 `THIRD-PARTY-NOTICES.md`；参考实现的许可证不自动成为本项目许可证。
