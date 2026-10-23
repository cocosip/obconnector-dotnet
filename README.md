# OceanBaseConnector for .NET

A managed ADO.NET provider for OceanBase MySQL and Oracle compatibility modes.

The driver is experimental. Recorded database runs cover direct OceanBase 4.4.2.2 with TLS disabled; the Oracle suite still has a failed case and cleanup failure. OBProxy and other deployment profiles remain unverified. See [failed tests, unsupported behavior and compatibility gaps](https://github.com/cocosip/obconnector-dotnet/blob/master/docs/compatibility.md).

## Packages

| Package | Purpose |
| --- | --- |
| `OceanBaseConnector` | Connections, commands, parameters, readers and transactions. |
| `OceanBaseConnector.DependencyInjection` | Optional dependency injection integration. |

Both packages target `netstandard2.0`, `netstandard2.1`, `net8.0`, `net9.0` and `net10.0`. Data sources and batches are available on the .NET 8/9/10 assets.

## Usage

This example uses Oracle mode and a server certificate trusted by the client:

```csharp
using System.Data;
using OceanBaseConnector;

using var connection = new OceanBaseConnection(
    "Server=db.example;Port=2881;User ID=test_user@tenant;Password=replace-me;ExpectedMode=Oracle");
await connection.OpenAsync();

using var command = connection.CreateCommand();
command.CommandText = "select :value from dual";
var parameter = command.CreateParameter();
parameter.ParameterName = "value";
parameter.DbType = DbType.Int32;
parameter.Value = 42;
command.Parameters.Add(parameter);

var result = await command.ExecuteScalarAsync();
```

`ExpectedMode` validates the detected mode; it does not change the server mode. TLS defaults to `VerifyFull`, validating certificate trust and hostnames.

Every Open confirms the actual OceanBase version and tenant mode. Server features are selected
from the actual version and negotiated capabilities, without a JDBC 5/8 compatibility profile.
In MySQL mode, signed `TINYINT(1)` returns `bool` by default (nonzero is true);
set `Treat Tiny As Boolean=false` to read integers. UPDATE reports matched rows by default;
set `Use Affected Rows=true` to request changed rows. See the [configuration reference](docs/obconnector-dotnet-architecture-design.md).

With the DI package:

```csharp
using Microsoft.Extensions.DependencyInjection;

services.AddOceanBaseConnection(connectionString);
```

Each resolution creates a closed transient connection owned by its container scope. On .NET 8/9/10, `AddOceanBaseDataSource` and `AddKeyedOceanBaseDataSource` register singleton data sources; callers dispose the connections they create.

## Features and limits

- Synchronous and asynchronous SQL, server-prepared parameters, multiple results, streaming reads and binary uploads.
- Local transactions; .NET 8/9/10 assets also expose savepoints and sequential batches.
- Oracle extensions include stored procedures, OUT parameters, REF CURSOR, LOB locators, complex types and array binding. Procedures and output parameters require `Use Oracle Prepare Execute=true`; locator LOBs also require `Use Lob Locators=true`.
- Explicit XA, multiple server addresses, custom address resolution, logging, tracing and metrics.
- Connection pooling and ambient `TransactionScope` are not supported. `Pooling=true` is rejected.
- Every connection confirms `ob_version()` and the tenant compatibility mode; MySQL-mode opens also require `@@autocommit` enabled, rejecting sessions whose implicit transactions this provider could not commit. Recognized version formats are accepted; versions beyond the recorded deployment and OBProxy still need real-server validation.

The driver does not automatically replay SQL. If `OceanBaseException.IsExecutionOutcomeUnknown` is true, reconcile the operation before retrying a write or commit. Batches run sequentially and need an explicit transaction for atomicity. Dispose readers, commands and connections; closing an unread reader can destroy its session.

## Build and test

Requires the .NET 10 SDK. Tests run on .NET 10. Real-database integration tests never run in CI or a plain dotnet test; they need an explicit opt-in and a configured environment ([integration guide](https://github.com/cocosip/obconnector-dotnet/blob/master/tests/OceanBaseConnector.IntegrationTests/README.md)).

```powershell
dotnet restore OceanBaseConnector.slnx
dotnet build OceanBaseConnector.slnx -c Release --no-restore
dotnet test OceanBaseConnector.slnx -c Release --no-build --no-restore
dotnet pack OceanBaseConnector.slnx -c Release --no-build --no-restore -o TestResults/packages
```

To check package consumption, run `pwsh -File eng/Verify-Packages.ps1 -PackageDirectory TestResults/packages`. This requires PowerShell 7.1+ and .NET/ASP.NET Core 8/9/10 runtimes.

## Documentation

- [Compatibility and known limitations](https://github.com/cocosip/obconnector-dotnet/blob/master/docs/compatibility.md)
- [Database acceptance guide](https://github.com/cocosip/obconnector-dotnet/blob/master/tests/OceanBaseConnector.Acceptance/README.md)
- [Integration test guide](https://github.com/cocosip/obconnector-dotnet/blob/master/tests/OceanBaseConnector.IntegrationTests/README.md)
- [API reference](https://github.com/cocosip/obconnector-dotnet/blob/master/docs/obconnector-dotnet-architecture-design.md)
- [Verification status and remaining work](https://github.com/cocosip/obconnector-dotnet/blob/master/docs/obconnector-dotnet-progress.md)
- [Connector/J architecture reference](https://github.com/cocosip/obconnector-dotnet/blob/master/docs/obconnector-j-architecture-outline.md)

## License

[MIT](LICENSE). See [third-party notices](THIRD-PARTY-NOTICES.md).
