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

`ExpectedMode` validates the detected mode; it does not change the server mode. TLS defaults to `VerifyFull`, validating certificate trust and hostnames. Set `Check Certificate Revocation=true` to enable platform revocation checks. For mutual TLS, assign an application-owned certificate with a private key to `OceanBaseConnection.ClientCertificate` before opening. Pooling supports client certificates and isolates certificate instances and SHA-256 fingerprints; reuse a stable certificate instance for one ordinary pool. The application must keep that certificate valid through each Open.

Modern data sources offer `OceanBaseDataSourceBuilder.UseClientCertificate(...)`. Build captures an owned copy even with pooling disabled, so the original can be disposed after Build succeeds. Source connections inherit fixed configuration. Certificate rotation uses a new data source; dispose the old source and close its borrowed connections when retiring that identity. Pooled borrow does not repeat TLS validation. See the [client certificate contract](docs/obconnector-dotnet-architecture-design.md#tls-client-certificates) for ownership and rotation limits.

For enterprise trust or certificate pinning, set `OceanBaseConnection.RemoteCertificateValidationCallback` before Open, or use `OceanBaseDataSourceBuilder.UseRemoteCertificateValidationCallback(...)`. This requires `TLS Mode=VerifyFull`. A callback replaces the platform validation decision and must handle certificate, chain, hostname and revocation errors; null retains platform validation. Ordinary pools isolate callback instances by reference. Validation runs at physical TLS establishment, not at each pooled borrow; rebuild/dispose the data source when retiring a trust policy. See the [TLS API contract](docs/obconnector-dotnet-architecture-design.md#tls-server-validation).

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
- Text multi-statements are opt-in with `Allow Multi Statements=true`; parameterized, prepared and cursor multi-statements remain unsupported.
- Server-initiated `LOCAL INFILE` is opt-in with `Allow Local Infile=true` and an application-provided `LocalInfileStreamFactory`; the driver never opens the server filename as a local path. Real OceanBase/OBProxy interoperability remains unverified.
- Local transactions; .NET 8/9/10 assets also expose savepoints and sequential batches.
- Oracle extensions include stored procedures, OUT parameters, REF CURSOR, LOB locators, complex types and array binding. Procedures and output parameters require `Use Oracle Prepare Execute=true`; locator LOBs also require `Use Lob Locators=true`.
- Explicit XA, multiple server addresses, custom address resolution, logging, tracing and metrics.
- `GetSchema` exposes 19 Oracle-mode / 17 MySQL-mode collections, including views, indexes, keys/constraints, foreign keys, sequences and users; Oracle also provides named types and synonyms. `Restrictions` describes exact bound filters. See the [Schema contract](docs/schema.md) for permissions and mode limits; real directory/OBProxy validation remains open.
- Custom address resolution runs once per logical Open, including pool reuse. Ordinary pools isolate resolver instances; a fresh address snapshot retires idle sessions on removed host/port endpoints before reuse. Already borrowed sessions finish normally. With a custom resolver, the connection timeout also bounds discovery, pool waiting and prewarming.
- `CredentialProvider` and modern builder `UseCredentialProvider` obtain one immutable credential per logical Open, including pool reuse. Higher versions retire old sessions without interrupting active borrowers; expiry, cancellation and refresh failures fail closed. Providers own caching/refresh and remain caller-owned. See the [credential contract](docs/credentials.md).
- `Pooling=true` reuses authenticated physical sessions, validates reused sessions, resets them before return, and supports `Min Pool Size`, `Max Pool Size`, `Pool Wait Timeout`, `Connection Idle Lifetime` and `Connection Lifetime`; `Min Pool Size` prewarms physical sessions before a borrow and rolls back sessions still idle after a failed warmup, preserving sessions already borrowed by concurrent Opens. Recognized user session-state and temporary-object statements discard the physical session instead of returning it to the pool. Opening inside an ambient `TransactionScope` enlists one local transaction; distributed promotion remains unsupported.
- `OceanBaseConnection.ClearPool(connection)` closes idle sessions for that identity and discards borrowed sessions when returned. `ClearAllPools()` clears ordinary registry pools; private data sources use `source.Clear()` or `ClearPool` with one of their connections. Active commands continue, and new Opens remain available. See the [pool clearing contract](docs/obconnector-dotnet-architecture-design.md#explicit-pool-clearing).
- Every connection confirms `ob_version()` and the tenant compatibility mode; MySQL-mode opens also require `@@autocommit` enabled, rejecting sessions whose implicit transactions this provider could not commit. Recognized version formats are accepted; versions beyond the recorded deployment and OBProxy still need real-server validation.

`Use Compression=true` enables standard MySQL zlib compression in either compatibility mode, including over TLS and pooled sessions. It requires server support and is off by default; combining it with `Use OB20=true` is currently rejected. OB20 compression, reroute and real-server compression validation remain open.

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

## CI/CD and releases

The [workflow](.github/workflows/ci.yml) builds and tests on Windows and Linux for pull requests targeting `main`/`master`, pushes to those branches, and tag pushes.

| Event | Build and test | Pack and verify NuGet consumers | Publish to NuGet and GitHub Releases |
| --- | --- | --- | --- |
| Pull request to `main`/`master` | Yes | No | No |
| Push to `main`/`master` | Yes | Yes | No |
| Version tag push | Yes | Yes | Yes, after both operating systems pass |
| Other branch push | No | No | No |

Packages are built from the two projects under `src/`, verified on Windows, and retained as the `nuget-packages` workflow artifact. Tag publication downloads these same packages without rebuilding them. Real database credentials are not required by this workflow.

To release, update the shared `Version` in `Directory.Build.props`, commit it, and push a matching tag such as `v0.1.0` (or `0.1.0`). A mismatched tag fails before building or publishing. Prerelease versions such as `v0.2.0-rc.1` create a GitHub prerelease. Configure the repository Actions secret `NUGET_API_KEY` with permission to publish both package IDs; the workflow uses `GITHUB_TOKEN` with `contents: write` to create the GitHub Release.

Each release records the non-merge commits since the previous reachable version tag, a comparison link, and install commands for both packages. The first release includes the initial commit history. These notes and the verified `.nupkg` files are attached to the GitHub Release; each NuGet package's release notes link to it. Notes are generated from Git history, so use descriptive commit subjects. This does not replace the outstanding publication review in the [verification status](docs/obconnector-dotnet-progress.md).

Check notes generation locally with `pwsh -File eng/Test-ReleaseNotes.ps1`. To preview an existing tag, check out that tag and run `pwsh -File eng/New-ReleaseNotes.ps1 -Tag v0.1.0`; the output defaults to `TestResults/packages/RELEASE_NOTES.md`.

## Documentation

- [Compatibility and known limitations](https://github.com/cocosip/obconnector-dotnet/blob/master/docs/compatibility.md)
- [Complete driver audit report](https://github.com/cocosip/obconnector-dotnet/blob/master/docs/obconnector-dotnet-driver-audit.md)
- [Complete driver capability baseline and gap plan](https://github.com/cocosip/obconnector-dotnet/blob/master/docs/obconnector-dotnet-completeness.md)
- [Database acceptance guide](https://github.com/cocosip/obconnector-dotnet/blob/master/tests/OceanBaseConnector.Acceptance/README.md)
- [Integration test guide](https://github.com/cocosip/obconnector-dotnet/blob/master/tests/OceanBaseConnector.IntegrationTests/README.md)
- [API reference](https://github.com/cocosip/obconnector-dotnet/blob/master/docs/obconnector-dotnet-architecture-design.md)
- [Credential provision and rotation](https://github.com/cocosip/obconnector-dotnet/blob/master/docs/credentials.md)
- [Verification status and remaining work](https://github.com/cocosip/obconnector-dotnet/blob/master/docs/obconnector-dotnet-progress.md)
- [Connector/J architecture reference](https://github.com/cocosip/obconnector-dotnet/blob/master/docs/obconnector-j-architecture-outline.md)

## License

[MIT](LICENSE). See [third-party notices](THIRD-PARTY-NOTICES.md).
