# OceanBaseConnector for .NET

Observe [protocol frame diagnostics](docs/protocol-diagnostics.md) through the standard .NET
DiagnosticListener named `OceanBaseConnector.Protocol`. Fixed frame/boundary/error metadata spans
MySQL, compression and OB20; SQL, authentication, parameters and payloads are omitted.

Read ordinary Oracle [binary cursor columns](docs/obconnector-dotnet-architecture-design.md#oracle-cursor-valued-columns)
with `OceanBaseRefCursor`: prepare the query, obtain the value, consume all parent results, then open its
single reader. Positive parent FetchSize is a hint without parent paging; cursor-column offset fetch is unsupported.
Skipped cursors are released; nested cursors retain their ancestors. Text cursor columns
and REF CURSOR input directions remain unsupported; real cursor-column SQL acceptance is still open.

Use [OCP HTTP address discovery](docs/obconnector-dotnet-architecture-design.md#ocp-http-discovery) with `new OceanBaseOcpAddressResolver(httpClient, serverOrigin, netServiceName)` through the existing resolver API. Every Open fetches bounded UTF-8 JSON before pool reuse; names bind to discovery metadata, and errors never fall back to old addresses. The caller owns HTTP authentication/TLS and must disable automatic redirects. TCP groups, rotation/random/weighted selection are implemented; see [group selection](docs/obconnector-dotnet-architecture-design.md#discovery-group-selection). Global [blacklist and TCP attempt ceilings](docs/obconnector-dotnet-architecture-design.md#discovery-blacklist-and-attempt-ceiling) are implemented; local policy overrides and real deployment acceptance remain open.

Use [TNS file discovery](docs/obconnector-dotnet-architecture-design.md#tns-file-discovery) with `new OceanBaseTnsAddressResolver(filePath, alias)` assigned to `connection.AddressResolver`, or modern `builder.UseAddressResolver(resolver)`. Every Open reloads a bounded strict UTF-8 TCP address snapshot before pool reuse. Publish whole-file replacements; unsupported TNS policies fail explicitly, and persistent file errors never fall back to cached addresses. A SERVICE_NAME field requires an exact caller expectation and validates file metadata only. OCP HTTP discovery also preserves groups and selection policies; global blacklist/attempt ceilings are supported; local overrides and proxy discovery remain open.

Opt in to [TCP connection retries](docs/obconnector-dotnet-architecture-design.md#connect-retry-policy) with `Connect Retry Count=1;Connect Retry Interval=1`. Count (0..10, default0) adds full candidate rounds; interval (1..60 seconds, default1) applies between rounds, and between explicit discovery eligible/fallback passes after actual attempts. A positive count shares Connection Timeout across the whole Open, including pool wait/prewarm and backoff. TCP success ends retries; TLS/authentication/commands and active transactions are never replayed.

Opt in to [endpoint TCP health hints](docs/obconnector-dotnet-architecture-design.md#endpoint-tcp-health) with `Host Recheck Seconds=60` (0..3600 seconds, default0). Recent TCP failures move behind other candidates; every address remains available as fallback. Hints are bounded and scoped to the pool or unpooled selector; Pool Clear and credential retirement isolate late old feedback for pool-owned hints; unpooled hints follow selector lifetime. TCP success clears the hint. This does not probe tenant health or retry TLS/authentication/SQL failures.

A managed ADO.NET provider for OceanBase MySQL and Oracle compatibility modes.

The driver is experimental. Recorded database runs cover direct OceanBase 4.4.2.2 with TLS disabled; the Oracle suite still has a failed case and cleanup failure. OBProxy and other deployment profiles remain unverified. See [failed tests, unsupported behavior and compatibility gaps](https://github.com/cocosip/obconnector-dotnet/blob/master/docs/compatibility.md).

The affected Windows/.NET10.0.12 consumer matrix now passes642 cases with default runtime settings after
[a diagnostic unloading fix](docs/compatibility.md#2026-10-26-collectible-diagnostic-unloading-repair).
Historical crashes and remaining independent-runtime/release acceptance are retained in that record.

The [complete driver audit](https://github.com/cocosip/obconnector-dotnet/blob/master/docs/obconnector-dotnet-driver-audit.md) tracks 23 gaps across eight work packages, with source evidence, priorities and acceptance criteria. Basic connection pooling is implemented and opt-in; complete session isolation and real deployment acceptance remain open. Commands can [derive parameters from a unique visible routine signature](https://github.com/cocosip/obconnector-dotnet/blob/master/docs/obconnector-dotnet-architecture-design.md#routine-parameter-derivation) without executing the target; actual directory/permissions/version/OBProxy acceptance remains open.

Connection-level server errors now retire the physical session. Explicit Close/Open obtains a fresh lease; sent operations may have unknown outcomes and are never replayed automatically. See the [failure contract](docs/obconnector-dotnet-architecture-design.md#connection-level-server-errors) and [local verification limits](docs/obconnector-dotnet-completeness.md#1549-2026-10-25-连接级服务器错误与显式连接重建).

MySQL `CommandType.StoredProcedure` now supports ordered Input/Output/InputOutput parameters, multiple results and output collection when the reader finishes or closes. It requires PS multi-results capability and uses server preparation; ReturnValue, FetchSize, SchemaOnly and KeyInfo are rejected. See the [procedure contract](docs/obconnector-dotnet-architecture-design.md#mysql-stored-procedures) and [module evidence](docs/obconnector-dotnet-completeness.md#1530-2026-10-25-mysql-存储过程执行本地交付). Local TLS fixtures and limited direct-server results do not establish OBProxy, real TLS or independent-runtime compatibility.

Oracle text OUT/RETURNING, procedures and functions support [standalone Prepare/PrepareAsync](docs/obconnector-dotnet-architecture-design.md#oracle-standalone-prepare) without executing the target or consuming input values. The next execution takes ownership of the prepared ID; output cursors can outlive command disposal. Subsequent implicit execution prepares afresh. A positive command [FetchSize hint](docs/obconnector-dotnet-architecture-design.md#oracle-output-fetch-hint) is accepted for these outputs; internal output rows are streamed in the execution response, and returned REF CURSOR readers use their own page size. Output offset fetch, array FetchSize and REF CURSOR input directions remain unsupported; ordinary binary cursor columns follow the linked cursor-column contract; direct Oracle4.4.2.2 evidence does not establish proxy/TLS or independent-runtime compatibility.

Oracle [NCHAR/NVARCHAR2 complex members](docs/obconnector-dotnet-architecture-design.md#complex-national-members) support named/nested values, PL outputs and anonymous DML arrays. Complex inputs use connection encoding; ordinary stored members require a queried AL16UTF16 database charset, while PL outputs use session national encoding. Complex whole-column RETURNING with unresolved identity, anonymous ordinary non-NULL national results without trusted storage context and other database national encodings remain unsupported. This does not close the complete complex-type or deployment matrix.

Oracle [unknown-precision NUMBER results](docs/obconnector-dotnet-architecture-design.md#oracle-number-results-with-unknown-precision) now preserve the exact returned value instead of applying ambiguous metadata scale. Known-precision results and parameter declarations retain strict checks. Complete driver and deployment acceptance remain open.

For the current source baseline, remaining implementation work and verification limits, see the [current audit handoff](docs/obconnector-dotnet-driver-audit.md#11-本次评审摘要与剩余交付清单), [unsupported and restricted combinations](docs/obconnector-dotnet-driver-audit.md#12-明确缺失和受限组合实施入口) and [module acceptance criteria and work packages](docs/obconnector-dotnet-completeness.md). Opt-in [prepared statement caching](docs/obconnector-dotnet-architecture-design.md#prepared-statement-cache) reuses idle IDs across commands on one physical session; real DDL/OBProxy/load acceptance remains open. User SQL has a [shared session-retirement contract](docs/obconnector-dotnet-architecture-design.md#pooled-session-state-after-user-sql); local classification and reset fixtures do not establish complete borrower isolation.

Opt in to [slow client operation diagnostics](docs/obconnector-dotnet-architecture-design.md#slow-query-diagnostics) with `Slow Query Threshold=1000` (milliseconds; default 0 disables). Completed execute/REF CURSOR lifetimes report a safe Warning, counter and existing-span tag, including reader consumption time; SQL and values are omitted. This does not measure server-only SQL time or change timeout/retry behavior.

The existing meter now reports [connection phase durations](docs/obconnector-dotnet-architecture-design.md#connection-phase-diagnostics) for credential acquisition, discovery, TCP, greeting, TLS, authentication, identity, pool waiting and probing. Samples use only fixed phase/outcome labels; optional phases are omitted and retries are measured separately. Protocol frame diagnostics follow the contract above; other resource waits and deployment performance acceptance remain open.

Ordinary pools use [effective configuration identity](docs/obconnector-dotnet-architecture-design.md#ordinary-pool-configuration-identity): equivalent option order, typed spelling and explicit defaults share capacity and ClearPool identity. Credentials (including present empty PAM factors), exact text values, policy/certificate references and private data sources remain isolated; public ConnectionString serialization is unchanged. A pooled Open from a [Closed state callback](docs/obconnector-dotnet-architecture-design.md#pooled-close-callbacks) completes the captured old cleanup before borrowing, allowing capacity-one reuse without waiting on its own Close. Complete isolation, OBProxy and runtime acceptance remain open.

Oracle [complex-type DDL invalidation](docs/obconnector-dotnet-architecture-design.md#complex-type-ddl-invalidation) has a local implementation: observed text CREATE/ALTER/DROP/RENAME/TRUNCATE target execution invalidates descriptions independently of statement caching. Prepare and pre-send rejection preserve them; reload types and rebuild bindings after invalidation. External or opaque DDL requires explicit `ClearComplexTypeCache`. Real DDL/OBProxy/implicit-commit/lock acceptance remains open.

`CommandBehavior.SchemaOnly` reads server-prepared column metadata without executing the target or uploading parameter values. It requires a single accepted text query with input parameters and never falls back to text execution. See the [command contract](docs/obconnector-dotnet-architecture-design.md) for preparation errors, metadata limits and lifecycle behavior.

`CommandBehavior.KeyInfo` reads keys on the same session before execution and appends missing hidden key columns for proven single-table queries. Complex queries retain their shape and unknown key properties. See the [key metadata contract](docs/obconnector-dotnet-architecture-design.md#key-information-and-hidden-columns) for nullable unique keys, composite keys and validation limits.

`OceanBaseDataAdapter` fills DataTables/DataSets and applies row changes; `OceanBaseCommandBuilder` generates parameterized single-table CRUD with original-value concurrency checks. Automatic updates/deletes require a complete visible row key. See the [adapter and generation contract](docs/obconnector-dotnet-architecture-design.md#data-adapters-and-command-generation) for mappings, transactions, ownership and limits.

Ordinary string results support strict ASCII, MySQL latin1, GBK, GB18030-2005, UTF-16/LE and mapped UTF-8 collations, including streamed reads. Unknown collations and malformed bytes fail explicitly. Client SQL/parameter encoding and Oracle national-character rules retain their existing scope. See the [result charset contract](docs/obconnector-dotnet-architecture-design.md#result-character-sets) for exact IDs and real-server limits.

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
