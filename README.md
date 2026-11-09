# OceanBaseConnector

[![CI/CD](https://github.com/cocosip/obconnector-dotnet/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/cocosip/obconnector-dotnet/actions/workflows/ci-cd.yml)
[![NuGet](https://img.shields.io/nuget/v/OceanBaseConnector.svg)](https://www.nuget.org/packages/OceanBaseConnector/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A managed ADO.NET provider for OceanBase MySQL and Oracle tenant compatibility modes.
One provider shares protocol infrastructure and keeps mode-specific SQL, binding, types and metadata.
It does not promise complete native MySQL or Oracle driver compatibility.

The driver is experimental. Historical live evidence covers selected direct OceanBase4.4.2.2 cases on
Windows with TLS disabled. Oracle server/permission/shape failures remain recorded; real TLS/mTLS,
OBProxy, genuine OB20/trace/compression, other versions and full platform/load matrices remain open.

Start with the [documentation index](docs/README.md):

- [Feature and capability matrix](docs/obconnector-dotnet-progress.md): MySQL/Oracle scope and implemented subsets.
- [Missing-feature and verification matrix](docs/obconnector-dotnet-completeness.md): code limits, optional candidates and deployment blockers.
- [Acceptance criteria](docs/acceptance-criteria.md): required behavior, complete live suites, failure/skip and cleanup rules.
- [Development tracking list](docs/obconnector-dotnet-missing-features-roadmap.md): open tasks, server prerequisites and scope decisions.
- [Compatibility and retained TLS/environment limitations](docs/compatibility.md).
- [Connection strings](docs/connection-strings.md): every keyword, default, validation rule and example.

Core connections, commands, parameters, readers and transactions use ADO.NET APIs. Pooling, caching,
batch optimizations, Oracle LOB/complex types/XA, discovery and protocol extensions have explicit
options and boundaries. Sent operations with unknown outcomes are never replayed automatically.
TLS defaults to VerifyFull; the local plain-transport test environments require an explicit Disabled setting.

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

For the complete keyword list, defaults and validation rules see the [connection string reference](docs/connection-strings.md).

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
- Text multi-statements are opt-in with `Allow Multi Statements=true`. Oracle named and MySQL positional inputs support numbers, strings, binary, JSON (MySQL), NChar/NVarchar2 (Oracle) and NULL, plus MySQL DATE/DATETIME/TIMESTAMP/TIME and Oracle DATE/TIMESTAMP/TIMESTAMP WITH TIME ZONE/TIMESTAMP WITH LOCAL TIME ZONE/INTERVAL YEAR TO MONTH/INTERVAL DAY TO SECOND. Oracle national inputs use ASCII Unicode literals, preserving characters outside the client charset; national result decoding requires fresh trusted state and uses the supported explicit encoding mappings. MySQL strings tolerate SQL_MODE changes but require stable parsing character sets. Binary Input streams use bounded client-literal encoding; MySQL scalar Output/InputOutput/ReturnValue positions use private user variables, with atomic publication after the script and a hidden output SELECT complete; reader close drains the active script. Script or output-query errors preserve prior parameter values but do not undo successful SQL. Stream/locator/complex outputs, Oracle script outputs, Prepare and server cursors remain unsupported. Oracle named complex input support is limited to the documented D-02d1 through D-02d3 and D-04f subsets. See [compatibility](docs/compatibility.md) for limits.
- Server-initiated `LOCAL INFILE` is opt-in with `Allow Local Infile=true` and an application-provided `LocalInfileStreamFactory`; the driver never opens the server filename as a local path. Real OceanBase/OBProxy interoperability remains unverified.
- Local transactions; .NET 8/9/10 assets also expose savepoints and sequential batches.
- MySQL stored procedures support binary InputOutput streams through prepared SET/CALL, with caller-owned sources and outputs published after complete results. See [INOUT stream evidence](docs/compatibility.md).
- Opt-in client prepare fallback accepts MySQL BIT/YEAR inputs using exact numeric literals after a validated unsupported-Prepare response. See [fallback evidence and deployment limits](docs/compatibility.md).
- Explicit MySQL JSON inputs also support that opt-in fallback, using fresh session charset/SQL_MODE facts and ordinary string literals. JSON syntax remains server-validated; see [JSON fallback evidence](docs/compatibility.md).
- MySQL DATE/DATETIME/TIME inputs also support opt-in fallback, retaining exact input precision and current-session TIMESTAMP conversion. TIME cap fractions have server-specific binary/text differences; see [temporal evidence](docs/compatibility.md) and the [completed/remaining checkpoint](docs/compatibility.md).
- REF CURSOR readers offer opt-in forward offset FETCH through `GetDataReader(fetchSize, useCursorOffset, behavior)` and `GetDataReaderAsync(fetchSize, useCursorOffset, behavior, cancellationToken)`. For declared REF CURSOR outputs, a positive parent FetchSize with UseCursorOffset=true supplies the existing reader overloads' default; explicit reader bool overloads override it, and child page size remains independent. Other calls retain ordinary FETCH. Offset readers validate every position tail, including the first page; scrolling, array offset and cursor-valued parent columns remain unsupported.
- Oracle extensions include stored procedures, OUT parameters, REF CURSOR, LOB locators, complex types and array binding. Procedures and output parameters require `Use Oracle Prepare Execute=true`; locator LOBs also require `Use Lob Locators=true`.
- Oracle binary InputOutput streams use the existing prepared piece upload and retain caller ownership. Outputs follow server metadata: the verified anonymous procedure wrapper returns RAW content as CHAR hex text. See [Oracle INOUT stream evidence](docs/compatibility.md).
- Complex LOB inputs support named BLOB bytes/CLOB text and anonymous BLOB/CLOB arrays of session-bound locator handles. Single-statement Oracle Text/StoredProcedure named locator inputs use validated constructors with hidden root LOB binds; independent scalar and specified resource outputs, batch input-only subsets, and specified mixed batch routine-barrier output combinations are supported. Complex locator InputOutput and multi-statement expansion remain unsupported; see the [complex type input contract](docs/obconnector-dotnet-architecture-design.md#complex-types).
- CLOB locator parameters convert inline content in a sending copy for configured client charsets (V1; V2 on authenticated OceanBase4.4.1+). Original handles and UTF-8 readers retain their bytes; see the [charset contract](docs/client-character-sets.md).
- Explicit XA, multiple server addresses, custom address resolution, logging, tracing and metrics.
- MySQL ARRAY values use the existing String parameter/result path. Current direct acceptance covers INT nesting and single-level TINYINT/SMALLINT/BIGINT/FLOAT/DOUBLE/VARCHAR arrays. Results preserve the server representation: FLOAT array text may round values, and VARCHAR array text may contain raw control characters and incomplete JSON escaping. Do not assume lossless text rebinding or JSON parsing; see [compatibility](docs/compatibility.md).
- Named Oracle OBJECT/collection values support exact INTERVAL YEAR TO MONTH and DAY TO SECOND members in binary inputs/results and PL OUT/INOUT, including named client-constructor inputs. Use the existing interval provider values. Named OBJECT and nested OBJECT text results also decode exact INTERVAL members from the preloaded declaration (D-04f3; Oracle 4.4.2.2 direct, 24/24). Unknown declarations or malformed values are rejected; collection and other unsupported complex text shapes retain their guards. Canonical anonymous INTERVAL factories also support the verified ExecuteArray DML input/RETURNING subset (D-04f1); current PL outputs with unknown precision are rejected. Anonymous INTERVAL result descriptors now reuse the embedded-precision codec; explicitly declared precision-9 PL local collection OUT has initial direct evidence (D-04f2), but extended live acceptance is incomplete after endpoint handshake failures. Ordinary anonymous 0x17 binding and dedicated multi-send acceptance remain open. See D-04f in the [roadmap](docs/obconnector-dotnet-missing-features-roadmap.md).
- `GetSchema` exposes 20 Oracle-mode / 18 MySQL-mode collections, including views, indexes, keys/constraints, foreign keys, sequences and users; Oracle also provides named types and synonyms. `Restrictions` describes exact bound filters; static `DataTypes` reports CLR/provider mappings and conditional codec support. See the [Schema contract](docs/schema.md) for permissions and mode limits; real directory/OBProxy validation remains open.
- `OceanBaseParameter.DecimalScale` declares signed decimal scale, including Oracle NUMBER negative scales. Values requiring rounding are rejected; MySQL retains its narrower limits. See the [parameter contract](docs/obconnector-dotnet-architecture-design.md#decimal-parameter-scale) for precedence and mode bounds. Real-server declared-scale validation remains open.
- Custom address resolution runs once per logical Open, including pool reuse. Ordinary pools isolate resolver instances; a fresh address snapshot retires idle sessions on removed host/port endpoints before reuse. Already borrowed sessions finish normally. With a custom resolver, the connection timeout also bounds discovery, pool waiting and prewarming.
- `CredentialProvider` and modern builder `UseCredentialProvider` obtain one immutable credential per logical Open, including pool reuse. Higher versions retire old sessions without interrupting active borrowers; expiry, cancellation and refresh failures fail closed. Providers own caching/refresh and remain caller-owned. See the [credential contract](docs/credentials.md).
- `Statement Cache Size=1` enables a bounded idle prepared-statement cache per physical session (0..1024, default 0). Dispose commands to return entries; pool reset and observed schema/session context changes invalidate them. Commands still holding statements are outside the idle capacity limit.
- `Pooling=true` reuses authenticated physical sessions, validates reused sessions, resets them before return, and supports `Min Pool Size`, `Max Pool Size`, `Pool Wait Timeout`, `Connection Idle Lifetime` and `Connection Lifetime`; `Min Pool Size` prewarms physical sessions before a borrow and rolls back sessions still idle after a failed warmup, preserving sessions already borrowed by concurrent Opens. Recognized user session-state and temporary-object statements discard the physical session instead of returning it to the pool. Opening inside an ambient `TransactionScope` enlists one local transaction through the coordinator single-resource slot. A second database resource and distributed promotion are rejected. Lost COMMIT responses report InDoubt; automatic `OpenAsync` initialization observes the opening cancellation token and discards an interrupted session. Cancelling that token after Open completes does not cancel the transaction. See the [ambient contract](docs/obconnector-dotnet-architecture-design.md#ambient-transactions).
- `OceanBaseConnection.ClearPool(connection)` closes idle sessions for that identity and discards borrowed sessions when returned. `ClearAllPools()` clears ordinary registry pools; private data sources use `source.Clear()` or `ClearPool` with one of their connections. Active commands continue, and new Opens remain available. See the [pool clearing contract](docs/obconnector-dotnet-architecture-design.md#explicit-pool-clearing).
- Every connection confirms `ob_version()` and the tenant compatibility mode; MySQL-mode opens also require `@@autocommit` enabled, rejecting sessions whose implicit transactions this provider could not commit. Oracle opens explicitly confirm `set autocommit = 1`; pool return repeats it after successful reset, discarding the session on failure. See the [Oracle baseline contract](docs/obconnector-dotnet-architecture-design.md#oracle-autocommit-baseline). Recognized version formats are accepted; versions beyond the recorded deployment and OBProxy still need real-server validation.

`Use Compression=true` enables standard MySQL zlib compression in either compatibility mode, including over TLS and pooled sessions. It requires server support and is off by default; combining it with `Use OB20=true` is currently rejected. OB20 compression, reroute and real-server compression validation remain open.

The driver does not automatically replay SQL. If `OceanBaseException.IsExecutionOutcomeUnknown` is true, reconcile the operation before retrying a write or commit. Batches run sequentially and need an explicit transaction for atomicity. Dispose readers, commands and connections; closing an unread reader can destroy its session.

## Build and test

Requires the .NET 11 SDK and a .NET 10 runtime. Tests target and run on .NET 10. Certificate creation/import, TLS/mTLS handshakes and TLS consumer tests belong to the separate [security acceptance suite](tests/OceanBaseConnector.SecurityTests/README.md), outside the main solution and CI. Real-database integration tests never run in CI or a plain dotnet test; they need an explicit opt-in and a configured environment ([integration guide](https://github.com/cocosip/obconnector-dotnet/blob/master/tests/OceanBaseConnector.IntegrationTests/README.md)).

```powershell
dotnet restore OceanBaseConnector.slnx
dotnet build OceanBaseConnector.slnx -c Release --no-restore
dotnet test OceanBaseConnector.slnx -c Release --no-build --no-restore
dotnet pack OceanBaseConnector.slnx -c Release --no-build --no-restore -o TestResults/packages
```

To check package consumption, run `pwsh -File eng/Verify-Packages.ps1 -PackageDirectory TestResults/packages`. This requires PowerShell 7.1+ and .NET/ASP.NET Core 8/9/10 runtimes.
The script checks exact restored package hashes/assets and runs eight synthetic loopback protocol
scenarios per asset/runtime profile, covering both modes, sync/async and buffered/sequential reads.
Modern hosts assert the expected runtime major so major roll-forward cannot satisfy another profile.
Add `-VerifyNetFramework` on Windows with .NET Framework 4.8 and its targeting pack to also run the
Standard2.0 asset on desktop CLR. Standard2.0 transaction begin/commit and Close use synchronous APIs.
These local checks require no database credentials and do not establish real-server compatibility.

## CI/CD and releases

This repository and [Locus](https://github.com/cocosip/Locus) share the same CI/CD conventions: pushes to the default branch and pull requests compile and test, and only a version tag publishes. Other branches (including `dev`) trigger nothing.

The [workflow](.github/workflows/ci-cd.yml) builds and tests on Windows and Linux for pull requests targeting `master`, pushes to `master`, and version tag pushes.

| Event | Build and test | Pack and verify NuGet consumers | Publish to NuGet and GitHub Releases |
| --- | --- | --- | --- |
| Pull request to `master` | Yes | No | No |
| Push to `master` | Yes | Yes | No |
| Version tag push | Yes | Yes | Yes, after both operating systems pass |
| Other branch push | No | No | No |

Each run compiles once: tests reuse that build output and packing runs with `--no-build --no-restore`. Packages are built from the two projects under `src/`, verified on Windows, and retained as the `nuget-packages` workflow artifact. Tag publication downloads these same packages without checking out source, restoring, or rebuilding. Real database credentials are not required by this workflow.

To release, update the shared `Version` in `Directory.Build.props`, commit it, and push a matching tag (with or without a `v` prefix). A tag that mismatches `Version` or does not point to HEAD fails before building or publishing. A prerelease `Version` creates a GitHub prerelease.

Publication uses [nuget.org trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing), so no long-lived API key is stored: the publish job exchanges the workflow's GitHub OIDC token for a one-hour nuget.org API key immediately before pushing. Before the first tag release, sign in to nuget.org as the `virtual` profile that owns both package IDs and add a trusted publishing policy for repository owner `cocosip`, repository `obconnector-dotnet`, workflow file `ci-cd.yml`, and an empty environment. The workflow uses `GITHUB_TOKEN` with `contents: write` to create the GitHub Release.

Each release records the non-merge commits since the previous reachable version tag, a comparison link, and install commands for both packages. The first release includes the initial commit history. These notes and the verified `.nupkg` files are attached to the GitHub Release; each NuGet package's release notes link to it. Notes are generated from Git history, so use descriptive commit subjects. This does not replace the outstanding publication review in the [verification status](docs/obconnector-dotnet-progress.md).

Check notes generation locally with `pwsh -File eng/Test-ReleaseNotes.ps1`. To preview an existing tag, check out that tag and run `pwsh -File eng/New-ReleaseNotes.ps1 -Tag <tag>`; the output defaults to `TestResults/packages/RELEASE_NOTES.md`.

## Documentation

- [Connection strings](docs/connection-strings.md)
- [Feature and capability matrix](docs/obconnector-dotnet-progress.md)
- [Missing-feature and verification matrix](docs/obconnector-dotnet-completeness.md)
- [Development tracking list](docs/obconnector-dotnet-missing-features-roadmap.md)
- [Constraint and known limitations](docs/compatibility.md)
- [API reference and architecture](docs/obconnector-dotnet-architecture-design.md)
- [Failure handling and recovery](docs/failure-handling.md)
- [Diagnostics](docs/diagnostics.md)
- [Credential provision and rotation](docs/credentials.md)
- [Database acceptance guide](tests/OceanBaseConnector.Acceptance/README.md)
- [Integration test guide](tests/OceanBaseConnector.IntegrationTests/README.md)
- [Connector/J architecture reference](docs/obconnector-j-architecture-outline.md)

## License

[MIT](LICENSE). See [third-party notices](THIRD-PARTY-NOTICES.md).
