# OceanBase Connector for .NET Implementation Plan

> **For agentic workers:** Read the [Architecture Design](obconnector-dotnet-architecture-design.md) and this plan task by task. Update the [Progress Ledger](obconnector-dotnet-progress.md) after each task, and run its verification commands before committing the implementation.

**Goal:** Deliver a managed ADO.NET provider that connects directly to OceanBase/OBProxy, supports MySQL and Oracle compatibility modes, and demonstrates its behavior against an explicit server-version matrix.

**Architecture:** One public provider and one shared protocol pipeline. Each physical session owns its mode, version, capabilities, and transaction state. Synchronous and asynchronous APIs share encoding and state machines but use synchronous and asynchronous network I/O respectively. Pooling opens in phase C only after complete reset has been verified.

**Tech Stack:** C#, .NET 10 SDK; one core provider package targeting `netstandard2.0;netstandard2.1;net8.0;net9.0;net10.0` and an optional modern DI integration package, released together at the same version; ADO.NET, `TcpClient`/`SslStream`, standard logging abstractions, `System.Numerics.BigInteger`, and xUnit. No Oracle/MySQL native client in the protocol core.

**Spec:** [Architecture Design](obconnector-dotnet-architecture-design.md), with the Java behavior baseline pinned to commit `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1` in the [Connector/J Architecture Outline](obconnector-j-architecture-outline.md). These documents provide design and comparison references, not proof of implemented .NET capabilities.

**.NET integration revision (2026-10-18):** Architecture §§2.1–2.7 move modern data sources, optional DI, and basic logging into B01. C06 retains batch; C07 retains tracing, metrics, and performance. Java remains the primary reference for the shared protocol and mode behavior; `oceanbase-dotnet` is only a limited host-integration reference. B01 has expanded: the original week window is an estimate and must be reassessed before phase B. An unchanged task count does not imply unchanged effort.

## Global Constraints and Entry Criteria

- The current schedule includes A/B/C in the first stable release, subject to G0-5; D ships as independent capabilities. A/B/C cannot be marked release-complete without live tests in both modes and an explicit version matrix.
- Initial defaults: `TLS Mode=VerifyFull`, `Pooling=false`, and `CLIENT_FOUND_ROWS`. No automatic retry of sent SQL, implicit client parameter interpolation, or `TransactionScope`. Revise the architecture before changing these public semantics.
- `netstandard2.0` defines public type identity. Modern ADO.NET APIs such as data sources/batch use conditional compilation and are exposed only on `net8.0+`. `netstandard2.1` additionally overrides APIs already present in its reference surface: command `PrepareAsync`/`DisposeAsync`, connection/reader `CloseAsync`/`DisposeAsync`, transaction `CommitAsync`/`RollbackAsync`/`DisposeAsync`, and protected connection `BeginDbTransactionAsync`. `ChangeDatabaseAsync` explicitly rejects calls until implemented. Compile each product target separately. All tests and consumer hosts use `net10.0`, explicitly selecting and loading `netstandard2.0`/`netstandard2.1` assets in isolation; B09/R01 separately verify older-runtime consumption.
- Use Central Package Management (CPM) throughout: root `Directory.Packages.props` pins all NuGet versions and enables transitive pinning; project `PackageReference` entries carry no `Version`. Root `Directory.Build.props` supplies the single `Version` for all current and future products/assemblies/packages; individual csproj files must not declare versions. Add dependency versions only to `Directory.Packages.props`.
- Centrally select Microsoft library versions by product TFM: 8.x for `netstandard2.0`/`netstandard2.1`/`net8.0`, 9.x for `net9.0`, and 10.x for `net10.0`; see architecture §2.3 for patches. A shared release version does not require the same dependency major, and the .NET 10 SDK/test host must not raise older-asset dependencies. Inspect each target's restore graph, assembly references, and nuspec dependency groups so transitive pinning does not lift older targets to 10.x.
- Per the user's current instruction, defer live-database and non-Windows runtime tests. Keep missing evidence in the ledger; it neither blocks local development/review/commits of modules with satisfied dependencies nor closes real compatibility gates.
- Approve licensing and package names before release. Refer to Java behavior/protocol without directly porting LGPL-2.1 implementation code.
- Before phase-A live interoperability acceptance, obtain independent tenants for both modes, server/OBProxy versions, authentication plugins, verifiable TLS certificates, and controllable network faults. Without that environment, report fixture verification only.
- For every task, write failing boundary cases first, implement, then run those cases and the phase regression suite. Raw protocol fixtures retain source, version, mode, and expected fields, without real credentials.

## Delivery Layout and Interface Boundaries

| Location | Responsibility |
| --- | --- |
| `OceanBaseConnector.slnx` | Shared solution; use an installed SDK supporting net10.0 without pinning the SDK version |
| `Directory.Build.props` | Sole `Version` source for every product, assembly, and package |
| `Directory.Packages.props` | CPM: sole source of all NuGet versions, with transitive pinning |
| `src/OceanBaseConnector/OceanBaseConnector.csproj` | Single core provider assembly and five-target conditional compilation; logging depends only on standard abstractions |
| `src/OceanBaseConnector/OceanBaseDataSource*.cs` | B01 modern builder/data source, frozen configuration and lifecycle; owns an independent optional pool after C01 |
| `src/OceanBaseConnector.DependencyInjection/` | Optional B01 integration package targeting netstandard2.0/netstandard2.1/net8.0/net9.0/net10.0; registers independent connections on all five targets and a shared data source with host logging on modern targets, without mode branches |
| `src/OceanBaseConnector/OceanBaseConnection*.cs`, `OceanBaseCommand*.cs`, `OceanBaseDataReader*.cs`, `OceanBaseTransaction*.cs` | Public ADO.NET objects; split files by responsibility without duplicating protocol implementations |
| `src/OceanBaseConnector/OceanBaseParameter*.cs`, `OceanBaseProviderFactory.cs`, `OceanBaseConnectionStringBuilder.cs` | Public parameter, factory, and configuration contracts |
| `src/OceanBaseConnector/Internal/Transport/` | TCP, SSLRequest, TLS, synchronous/asynchronous byte streams, close/cancel |
| `src/OceanBaseConnector/Internal/Protocol/` | Standard frames, handshake, authentication, commands, OK/ERR, text/binary rows, and multiple results; OB20 starts in phase D |
| `src/OceanBaseConnector/Internal/Session/` | `PhysicalSession`, mode/capability snapshots, exclusive execution, statement IDs, transaction and invalidation state |
| `src/OceanBaseConnector/Internal/Sql/`, `Internal/Values/` | Placeholder lexer, parameter encoding, mode-aware column decoding, and provider-specific values |
| `src/OceanBaseConnector/Internal/Pooling/` | Phase-C pools, complete session reset, and pool keys |
| `tests/OceanBaseConnector.UnitTests/` | Server-independent byte fixtures, API/state-machine tests, and error paths |
| `tests/OceanBaseConnector.IntegrationTests/` | Separate MySQL/Oracle tenants, OBProxy, TLS, fault/reset tests; explicitly skip/report absent environments |
| `tests/OceanBaseConnector.ConsumerTests/` | .NET 10 consumer host; compilation samples explicitly select netstandard2.0, isolated loads cover netstandard2.0/netstandard2.1; release checks also verify packages, factory, and public value-type identity |
| `tests/OceanBaseConnector.DependencyInjection.Tests/` | Added in B01, net10.0; built-in container disposal, ordinary/keyed registration isolation, configuration, and logging |

`PhysicalSession` exclusively owns the socket, packet sequence, mode, version, negotiated capabilities, and server statement IDs. Only one command or reader may hold it at a time. Public `OceanBaseConnection` is its logical borrower. `OceanBaseCommand` validates parameters, transaction, and ambient transaction before sending. `OceanBaseDataReader` owns the response until closed/drained. Unread packets, unknown outcomes, or reset failure invalidate the physical session. `SessionMode` derives only from server state in authentication OK; `ExpectedMode` is a subsequent validation only.

Fix the `netstandard2.0` type identities of `OceanBaseDbType`, `OceanBaseDecimal`, `OceanBaseTimestamp`, `OceanBaseTimestampWithTimeZone`, `OceanBaseTimestampLocalTimeZone`, `OceanBaseDate`, and `OceanBaseDateTime` first. B05/B06 implement low-precision column access and in-range writes for `OceanBaseDecimal`; C03 implements wide-precision codecs and the other exact temporal types. `GetFieldType` must not vary with row values. `OceanBaseException` includes server error code, SQLSTATE, and `IsExecutionOutcomeUnknown`; modern targets also associate batch entries through inherited `BatchCommand`. Exceptions have no mode/endpoint properties: safe endpoints and mode are correlated through logging/diagnostics, without SQL, parameters, or credentials by default.

## Phase and Schedule Baseline

Relative weeks start at week 1 when both-mode environments and product decisions are available; estimates assume one full-time developer. Review, environment failures, and server differences may change scope. These are scheduling estimates, not committed release dates. Before G0 closes, A01 scaffolding and server-independent A03 fixtures may proceed; actual W1 and G1 closure still require G0 completion. Tasks may be reviewed in parallel when dependencies permit, but gates close sequentially.

| Gate | Relative window | Definition of done |
| --- | --- | --- |
| G0 entry | W0 | Record target OceanBase/OBProxy versions, tenants, TLS/authentication, test execution, and §9 product decisions; missing environment items remain blockers |
| G1 protocol foundation A | W1-W5 | A01-A07 complete; evidence of live open/close in both modes and TLS rejection scenarios |
| G2 ADO.NET core B | W6-W17 (reestimate after integration revision) | B01-B09 complete; evidence for modern data-source/DI lifecycle/basic logging, CRUD in both modes, parameters, low-precision `OceanBaseDecimal` provider-specific access, transactions, cancellation, metadata, and five-target consumption |
| G3 production connections C | W18-W34 | C01-C08 complete; pooling enabled only for fully reset-verified combinations; exact types and load/fault scenarios pass |
| G4 release candidate | W35-W37 | R01-R02 complete; compatibility matrix, license/package names, and NuGet validation complete |
| D extensions | Schedule individually after G4 | D01-D15 each has an independent version/mode matrix; do not block G4 unless product decisions change |

## Review Focus

The owning task's tests must cover all five input categories; normal-path tests alone are insufficient:

1. Rejected TLS or certificate-name mismatch: send no authentication data and never downgrade; A04/A05.
2. Missing Oracle-mode bit or capability feedback: fail before decoding any mode-dependent value; A05/A07.
3. Cancellation immediately after partial packet transmission: report unknown outcome, destroy the session, and never replay; B08.
4. Low-precision NUMBER/DECIMAL already has exact provider-specific access in B. MySQL `DECIMAL(65,20)` and Oracle high-precision/nanosecond values round-trip exactly in C and explicitly fail in B; B05/B06/C03.
5. Return after application `SET`/`ALTER SESSION`: the next borrower must not observe prior session state; C01/C08.

## A. Protocol Foundation

### A01 Multi-target Projects and Test Entry Points

**Deliverables:** slnx solution, one five-target product project, net10.0 test/consumer projects, and CI entry points. Consumer tests explicitly select and load netstandard2.0/netstandard2.1 assets in isolation.

- [x] Create multi-target projects and conditional compilation examples; verify no modern API leaks into `netstandard2.0`/`netstandard2.1`.
- [x] Define protocol fixtures: binary data, source, server version, mode, redaction notes, and expected parsing results.
- [x] Run `dotnet restore`, `dotnet build -c Release`, and `dotnet test -c Release` for every test target; record commands/log locations in the ledger.

### A02 Connection Strings and Public Configuration

**Deliverables:** `OceanBaseConnectionStringBuilder` and immutable internal options parsing `Server`, `Port`, `User ID`, `Password`, `Database`, timeouts, `TLS Mode`, `TLS Server Name`, `Persist Security Info`, `ExpectedMode`, and `Pooling`. Never silently accept unimplemented options.

- [x] Test empty values, invalid ports/timeouts, conflicting synonyms, unknown keys, password serialization, and public connection-string redaction.
- [x] Parse once and freeze internal configuration; no passwords in exceptions/default logs; pool-key rules belong to C01.
- [x] Run connection-string tests and five-target compilation; record public keys in the ledger.

### A03 Standard Packet Frames and Byte Cursor

**Deliverables:** frame reader/writer for three-byte lengths, one-byte sequence IDs, maximum-length fragmentation, command-boundary sequence reset, truncation/bounds/malicious lengths. Check remaining bytes and integer overflow before parsing; never allocate directly from untrusted lengths.

- [x] Fixed byte fixtures cover single/multiple fragments, empty terminators, wrong sequence, truncation, oversized length encodings, and server ERR first.
- [x] Separate packet streams from command parsing; sync/async network paths share framing rules; async never calls synchronous I/O.
- [x] Run protocol tests including random truncation and resource-limit assertions.

### A04 TCP, SSLRequest, and TLS

**Deliverables:** `TcpClient` connection, timeout, `SslStream` upgrade, and endpoint/`TLS Server Name` validation. Default `VerifyFull` uses OS trust roots; `Required`/`Disabled` require explicit configuration.

- [x] Local byte/loopback tests cover handshake, CLIENT_SSL, SSLRequest/TLS order, and no authentication after TLS failure; assert zero authentication bytes on strict handshake/untrusted certificate, zero sends without TLS capability, and sync/async consistency.
- [x] Windows loopback covers total sync/async timeout, close, and async caller cancellation during a silent TLS handshake. Interrupt only after TLS starts; peer confirms EOF/disconnect before explicit cleanup; session cannot revive.
- [ ] Cross-platform TLS tests with a verifiable private CA, wrong hostname, and IP connection plus explicit DNS name. System trust-store changes trigger manual Windows confirmation and are deferred per user feedback; default unit tests must not install/remove roots.
- [x] Local failure tests assert exceptions/resource release for connection refusal, precancellation, and TLS errors; reject missing TLS capability/upgrade failure without plaintext downgrade.

A04 implementation and Windows loopback are complete locally; overall verification remains open: trusted private-CA success, real wrong-hostname rejection, live TLS, and non-Windows evidence are missing. On 2026-10-19, five `SilentTlsHandshakeIsInterruptedAndClosesTransport` regressions were added without certificate-store changes, covering timeout/cancel/close/peer release after ClientHello; no product-code change was needed. The private-CA attempt interrupted by system confirmation was reverted and dedicated certificate remnants removed; it is not completion evidence. Defer missing-environment/manual-confirmation items per the user; do not describe them as unimplemented code.

### A05 Authentication, Mode, and Capabilities

**Deliverables:** initial handshake/authentication-switch/OK parsing, selecting verified plugins by version matrix; send required bits including `CLIENT_SUPPORT_ORACLE`, `CLIENT_SESSION_TRACK`, `CLIENT_FOUND_ROWS`, and multiple results. Retain requested/server/effective capabilities; parse `OB_SERVER_USE_ORACLE_MODE` and `ob_capability_flag`.

- [x] Synthetic fixtures/loopback cover Oracle bit set/unset, untrustworthy mode identification, unknown plugins, missing capabilities, and authentication errors; do not return an executable connection with unknown mode.
- [x] Permit cleartext-style plugins only after successful VerifyFull upgrade; authentication switches reuse that security check; rejection without verified TLS is tested.
- [x] Local handshake/authentication evidence covers native authentication, switches, mode/capability snapshots, error codes/redaction, and sync/async.
- [x] Implement permitted MySQL-mode identification for verified OceanBase 4.4.2.2: authenticated native-password OK distinguishes both modes; a read-only `ob_version()` probe confirms generic greetings and MySQL mode before Open succeeds. ExpectedMode remains a cross-check. Other versions/plugin/proxy combinations remain unverified.
- [ ] Live handshake in both modes and OBProxy acceptance, recording server/proxy versions, plugin, and bits; defer this and non-Windows tests per user instruction.

Current boundary (2026-10-18): native-challenge encoding and VerifyFull-only clear-password encoding lack live acceptance. Without a verified version matrix, connections missing the Oracle-state bit explicitly fail rather than default to MySQL. After G0, establish an allowlist from live evidence before A07. Synthetic fixture version 4.2.1 is not a compatibility promise.

### A06 Physical Sessions, Errors, and Lifecycle

**Deliverables:** `PhysicalSession` states `Connecting/Open/Busy/Broken/Closed` and exclusive lease; close/error releases socket, statement IDs, and result state. `OceanBaseException` retains SQLSTATE, server code, and unknown-outcome flag; logging/diagnostics correlate safe endpoints and mode.

- [x] Test concurrent execution rejection, no reuse with unread responses, invalidation after errors, idempotent disposal, and log redaction.
- [x] Implement explicit sync/async close resource ownership; no background cleanup thread replaces state transitions.
- [x] Run state-machine tests and network-disconnect fixtures.

### A07 G1 Live Connection Gate

**Deliverables:** open, identify mode, and close on explicitly versioned MySQL/Oracle tenants and OBProxy; record TLS/authentication combinations. G1 requires interoperability evidence, not a fixture-only green build.

- [ ] For direct/OBProxy combinations in both modes, test open/close, expected-mode mismatch, wrong certificates, and unsupported authentication.
- [ ] Record actual versions/endpoint categories, commands, redacted logs, and failures in the compatibility matrix.
- [ ] Close G1 only when all required combinations pass; otherwise record blockers and never equate fixtures with compatibility.

## B. ADO.NET Core

### B01 Standard Provider, Modern Data Sources, and Host Integration

**Deliverables:** `OceanBaseConnection`, `OceanBaseCommand`, `OceanBaseParameter`, `OceanBaseDataReader`, `OceanBaseTransaction`, `OceanBaseProviderFactory`; modern `OceanBaseDataSourceBuilder`/`OceanBaseDataSource`, optional DI package, and data-source-level `ILoggerFactory`. Factory methods create the correct derived types; unimplemented methods fail explicitly. SQL/reader/transaction execution ships through B02-B08, without claiming capabilities from skeletons.

- [x] Test creation/close/reuse, `DbProviderFactories.RegisterFactory`, `ConnectionString` redaction, and B-stage `ChangeDatabase`/`ChangeDatabaseAsync` rejection before sending.
- [x] Connect public objects only to phase-A sessions; expose neither sockets nor separate public type sets for the two modes.
- [x] Implement core builder/nonpooled data source, modern factory entry points, logging context, optional DI project, and slnx entries. Direct connection constructors in all five assets accept `ILoggerFactory`, including Standard; no global factory. Core depends only on logging abstractions; integration depends on core/DI abstractions. Both packages use root Version and CPM dependencies by TFM.
- [x] Ordinary/keyed data-source registration uses deferred container-factory Build; resolve host logger before configuration callbacks and work without logging services; do not register connections by default. Singleton source, key/duplicate/common-alias conflict rules follow architecture §2.5; never create a second container.
- [x] Test no network on Build/DI resolution, frozen configuration, no replacement of source-owned connection configuration, independent source isolation, open-failure cleanup, source-dispose/open races, rejection after disposal, caller-owned active connections, idempotent disposal, and no disposal of external logger factories.
- [x] Test sync/async connection events, credential redaction, no raw server exception leaks, and logging factory/filter/write failures not preventing open/cleanup. Cancellation retains the original token; callback-triggered close cannot publish states out of order. Command unknown-outcome logs follow B02/B08.
- [x] Add net10.0 DI tests for container creation/disposal, scope validation, configuration overrides, hosts without logging, and key isolation.
- [x] Compile five core/three DI targets; run factory/isolated-consumer/per-target dependency-graph tests and verify shared package versions/exact core dependency.
- [ ] Verify public open paths in both modes after trusted MySQL-version evidence. Missing mode bits still reject safely; no bypass to synthesize MySQL success. Live, old-runtime, and non-Windows evidence remains open.
- [x] Reader `CommandBehavior.CloseConnection` has B06/subsequent isolated-consumer regressions; live behavior in both modes remains part of B06 acceptance.

### B02 Text Commands, OK/ERR, and Multiple Results

**Deliverables:** `COM_QUERY`, text rows, OK/ERR, and multiple-result state; sync/async `ExecuteNonQuery`, `ExecuteScalar`, and `ExecuteReader` share a state machine.

- [x] Fixtures/loopback cover `SELECT`, DML, empty/multiple results, update counts after result sets, server ERR, close with unread responses, and traditional EOF/negotiated FE OK terminators.
- [x] Keep default `CLIENT_FOUND_ROWS`; `RecordsAffected` accumulates server OK counts only, without guessing from SQL or counting SELECT rows. Live unchanged-UPDATE matched-row semantics remain unproven.
- [x] Windows loopback proves async command creation/row reads await the network without buffering entire text results; synchronous calls use synchronous I/O. Precancellation/unsupported parameters/behavior/ambient transactions fail before sending. Cancellation/timeout/close after sending destroys the session and reports unknown outcome. B08 extends fault coverage.
- [x] B01 context logs command completion/error/cancellation/unknown outcome without default SQL/bind values. Cancellation from logging callbacks cannot overwrite a trusted terminal OK/ERR.
- [ ] Live text CRUD and unchanged-UPDATE matched-row tests in both modes; live/non-Windows execution is deferred per user instruction and the trusted MySQL-version gate remains.

At B02 delivery, the reader supported basic signed/unsigned integers, floating point, UTF-8/binary strings, and NULL. It retained physical-session mode and explicitly rejected Oracle type code 3 (NUMBER) instead of borrowing MySQL INT mapping. B05 subsequently added exact numeric/date/low-precision provider-specific access; full metadata remains B06. Never guess national charset, LOB, or other extension decoding. B02 originally accepted only `CommandBehavior.Default`/`CloseConnection`; C04 subsequently added local `SequentialAccess` field streams. Other unsupported behavior fails before sending. Limits: 64 MiB per packet, 4096 columns/4 MiB metadata per result, one million packets per command; exceeding them destroys the session. Timeouts outside platform timer range fail before leasing, leaving no Busy session.

### B03 Oracle Placeholder Lexer and Parameter Validation

**Deliverables:** map Oracle `:name` to positional parameters, skipping single/double quotes, comments, paired/unpaired `q'...'` literals, and `:=` assignment. Expand repeated names to each position. Unreliable literal/bind syntax and missing/extra binds fail before sending. `ParameterName` accepts an optional `:`; normalize then compare case-insensitively and reject duplicate normalized collection entries. MySQL SQL is not rewritten; B04 server prepare determines its parameter count.

- [x] Cases cover strings, `q'[...]'`/`q'{...}'`/`q'(...)'`/`q'<...>'`, unpaired `q'!:name!'`, unterminated literals, double quotes, comments, `:=`, repeats/case, and parameterless SQL; rewrite actual binds only and fail before sending when recognition is unreliable.
- [x] One Oracle lexical scan preserves original parameter order without inserting values; unknown bind syntax fails explicitly.
- [x] Run lexer and sync/async public-command loopback tests; bad binds send nothing and leave the session usable.
- [ ] Oracle/MySQL integration is deferred per the user; valid-parameter execution is accepted after B04 integration.

Supported names are ASCII `[A-Za-z_][A-Za-z0-9_$#]*` (one optional leading `:`), matched with OrdinalIgnoreCase. Numeric/quoted/Unicode names and `?` outside Oracle literals are explicitly unsupported. `q`/`nq` are case-insensitive; delimiters are single nonwhitespace/noncontrol/nonsurrogate BMP characters. Doubled quotes escape single/double quotes; backslash has no escape meaning. Block comments do not nest and end at the first `*/`. Duplicate normalized names, missing/extra binds, and unsupported directions reject before lease/send. Values never participate in rewriting; collection name-lookup semantics are unchanged. Validation uses authenticated mode; closed connections report no available session first. B03 delivered mapping/validation; B04 subsequently integrated server Prepare and valid basic-parameter execution.

### B04 Server Preparation and Statement Ownership

**Deliverables:** `COM_STMT_PREPARE/EXECUTE/CLOSE`, binary parameters/results, and statement IDs owned by one physical session. Server prepare determines MySQL counts; Oracle rewritten counts must match. Unsupported prepared SQL fails explicitly. `PrepareAsync`/command `DisposeAsync` are true async overrides on netstandard2.1/net8.0/net9.0/net10.0; netstandard2.0 exposes only reference-supported APIs.

- [x] Windows byte/loopback cases cover basic types, signed/unsigned bounds, cross-byte NULL bitmaps, type changes, repeated execution, multiple results, malformed binary rows, error cleanup, and no reuse of old IDs after disconnect.
- [x] Shared sync/async packet/response handling keeps `Prepare()` mandatory, without interpolation fallback. Automatic preparation/execution share one lease/timeout budget; SQL outcome is unknown only after actual execution starts sending.
- [x] Windows net10.0/isolated netstandard2.1 consumption proves PrepareAsync awaits the network without sync fallback; cover execute, sync/async disposal, and deferred close on busy sessions.
- [ ] Live prepared CRUD, no async fallback on target runtimes, and type round-trips in both modes; defer live/non-Windows/old-runtime tests per the user. Missing Oracle-mode bits still reject safely.

B04 delivered bool/integer/float/ordinary UTF-8 string/byte[]/NULL codecs; B05 extended exact numbers/dates. Execution rejects explicit DbType/CLR mismatch and unsupported types before prepare/execute packets; explicit Prepare/PrepareAsync does not inspect value encoding. IDs reuse only within the same command, rewritten SQL, and physical session; changes/reconnect/disposal invalidate them, closing immediately if idle or before the next command if busy. Count mismatch never executes SQL. Prepare-metadata ERR closes known IDs; terminal execution ERR permits session reuse; unread responses destroy the session. B06 added metadata and B07 local transactions. B08 local faults are covered, but live acceptance is incomplete and G2 stays open.

### B05 Basic Values, Charsets, and Unsupported Types

**Deliverables:** mode-aware codecs for basic integers/floats, representable `decimal`, low-precision `OceanBaseDecimal` provider-specific reads/in-range writes, text/binary, NULL, common temporal values, and Oracle `DATE` including time. Wide precision/deferred types fail explicitly.

- [x] Fixtures cover signed/unsigned bounds, invalid encoding, NULL/empty strings, Oracle DATE time, exact low-precision NUMBER/DECIMAL `OceanBaseDecimal` read/write, pre-C rejection of MySQL `DECIMAL(65,20)`, and unsupported NCHAR/INTERVAL/JSON/BIT/YEAR rejection.
- [x] Encode by `DbType`/explicit `OceanBaseDbType`; decode by mode, column definition, charset, and precision. Ordinary `string` parameters do not imply NCHAR/JSON support.
- [ ] Live basic-type round-trips and metadata consistency in both modes remain deferred; synthetic packets/Windows loopback cannot substitute.

B05 is locally implemented/Windows-verified, overall verification open. Low-precision fields have fixed decimal/OceanBaseDecimal types; missing metadata, p>28, and negative scale explicitly reject. Oracle optimized NUMBER code 3 uses a separate four-byte layout. Parameters preserve explicit/inferred scale without rounding; Oracle DATE preserves time; MySQL TIME is a signed duration; Oracle TIMESTAMP 202 uses its original 12-byte layout. Exact ranges/rejections are in architecture §5. Public text/binary reader, sync/async prevalidation, and isolated five-asset consumption are tested. Live Oracle empty-string/NULL semantics, TIMESTAMP scale variants, old runtimes, and non-Windows lack evidence. GetSchemaTable/GetColumnSchema belong to B06.

### B06 Row-by-row Reader and Column Metadata

**Deliverables:** `Read`/`ReadAsync`, `NextResult`/`NextResultAsync`, `GetSchemaTable`, `IDbColumnSchemaGenerator.GetColumnSchema`, and stable `GetFieldType`/provider-specific field types. Low-precision NUMBER/DECIMAL `GetProviderSpecificFieldType`/`GetProviderSpecificValue` use `OceanBaseDecimal`. B permits bounded current-row buffering, never entire-result buffering.

- [x] Test multiple-result traversal, early close, `CommandBehavior.CloseConnection`, zero/NULL, stable standard/provider-specific low-precision types across rows, and unread-response session handling; zero dates still reject at the C03 boundary.
- [x] Reader exclusively holds the session until responses are consumed; `ExecuteScalar`/`ExecuteNonQuery` safely drain or invalidate.
- [ ] Verify names, precision, length, nullability, and async reading against real columns in both modes; synthetic columns/Windows loopback pass, live testing deferred.

B06 local implementation steps (existing master, baseline b772c0f):

- [x] `ResultColumn` retains database/table/original table/column names, raw length, and flags. Internal `ResultDbColumn` maps these to five-target `DbColumn` without dependencies. Names/type names/decoding share one definition. MySQL character lengths use supported charset widths; Oracle ordinary character lengths retain metadata units; lengths beyond int saturate at int.MaxValue. MySQL database maps to BaseCatalogName, Oracle to BaseSchemaName.
- [x] `OceanBaseDataReader` implements `IDbColumnSchemaGenerator`/schema table and returns independent snapshots. Unprovided uniqueness/expression/updatability stays unknown. ProviderType was unknown in B06 and filled by B09; ProviderSpecificDataType matches actual access. No-column results return null schema table/empty column schema; advancing results never reuses stale metadata.
- [x] First demonstrate gaps through public text/binary sync/async loopback and internal MySQL/Oracle column tests; then verify size/precision/NULL/aliases/multiple results/early close/exclusivity/safe drain/five-target consumption. Build, full Windows Unit/Consumer/DI, format, package matrix, and independent review passed before commit. Live/non-Windows/old-runtime acceptance remains empty.

See architecture §5 for B06 boundaries. GetColumnSchema/GetSchemaTable have five-target consumption evidence. Independent source review found no required fixes; an additional zero-row SELECT schema regression was added. Actual character lengths, Oracle NULL semantics, real metadata, old runtimes, and non-Windows remain unverified. B09 filled the former unknown ProviderType using scalar enum/parameter mappings, not disguised protocol codes.

### B07 Local Transactions and Ambient Transaction Rejection

**Deliverables:** `BeginTransaction`, mode-specific isolation mapping, explicit `DbCommand.Transaction` binding, commit/rollback/disposal, and true modern async lifecycle APIs. Reject `TransactionScope`/`EnlistTransaction` before sending.

Local implementation follows Java session commands `SET SESSION TRANSACTION ISOLATION LEVEL ...` and `set autocommit = 0`; the server starts the transaction on the first business statement. Do not send PL/SQL `BEGIN` in Oracle mode. Unspecified chooses MySQL RepeatableRead / Oracle ReadCommitted. MySQL supports four conventional levels; Oracle initially only ReadCommitted/Serializable (Java allowing RepeatableRead is not live evidence). Restore `set autocommit = 1` after commit/rollback. If terminal commit/rollback is confirmed but restoration fails, destroy the session, retain the confirmed outcome, and log restoration failure separately. Terminal ERR retains the active transaction for rollback; unconfirmed responses destroy it. Close/dispose reuse cleanup; destroy busy sessions rather than insert rollback into unread responses. Internal mapping tests do not prove MySQL interoperability: public handshake still accepts only authenticated Oracle mode.

- [x] Windows loopback covers missing/wrong-connection/completed transaction, nested begin, invalid isolation, rollback on close, and destruction after rollback failure.
- [x] Windows loopback covers entering `TransactionScope` before/after open; implemented caller protocol entry points reject while internal cleanup remains possible.
- [x] Synthetic Oracle loopback covers sync/async commit/rollback, unknown outcome on disconnect/cancel, and five-asset public consumption. Delayed modern responses prove Begin/End do not block the caller.
- [ ] Live commit/rollback/isolation/disconnect in both modes; live/non-Windows/old-runtime acceptance deferred.
- [x] B01 context logs transaction completion/failure/unknown outcome without SQL/parameters/raw server messages; logging failures cannot obscure commit results.

2026-10-18 local verification: five-core/three-DI Release build, 0 warnings/errors; Windows Unit455, Consumer34, DI8 pass (`TestResults/b07-{unit,consumer,di}.trx`). Independent review found sync rollback during async reader disposal and event ordering on reopen after delayed close; failing-then-passing regressions cover both. Create log categories before transactions to avoid lazy-creation reentry. Modern CloseConnection-reader cleanup uses async connection rollback. Publish logical Closed first; old cleanup cannot destroy a new session. Format, both packages, asset/dependency versions pass. Overall pending; G2 remains open; next B08.

2026-10-19 resumed-review correction: direct NETStandard.Library.Ref 2.1.0 netstandard.dll metadata inspection confirmed async connection/reader/transaction lifecycle APIs. Expand four NET8_0_OR_GREATER conditions to NETSTANDARD2_1, reusing the implementation. Six isolated-2.1 cases (five delayed rollback cleanups plus transaction overrides) failed before correction; 17 targeted cases passed afterward, with COMMIT/ROLLBACK network waits and original precancelled-token checks. Full evidence/deferred boundaries are in the ledger. This correction does not complete C03 TZ/LTZ work.

### M05 Modern Transaction Savepoints: Local Implementation (2026-10-20)

Based on architecture M05 and pinned Java OceanBaseConnection setSavepoint/rollback(Savepoint)/releaseSavepoint. Modern net8/net9/net10 assets override DbTransaction.SupportsSavepoints, Save/SaveAsync, Rollback(name)/RollbackAsync(name), Release/ReleaseAsync. The two Standard assets gain no methods absent from their reference APIs. Stack/dependencies/configuration unchanged; local commit in current checkout, no push.

Execute only in the original connection's active local transaction, reusing CommandExchange, transaction-control response validation, exclusive lease, original transaction timeout/cancellation. MySQL sends SAVEPOINT/ROLLBACK TO SAVEPOINT/RELEASE SAVEPOINT with backtick names. Oracle sends SAVEPOINT/ROLLBACK TO and explicitly rejects Release. Initially allow 1–30 ASCII characters, first a letter, remaining letters/digits/underscore. Oracle uses unquoted identifiers, matching Java's mode branch. Reject null/blank/quotes/comments/semicolons/overlong names; never log names. Maintain no client savepoint catalog and do not infer server existence/duplicate/deletion semantics.

Success neither ends the transaction nor restores autocommit. Confirmed rollback-to-savepoint advances the transaction resource generation, conservatively invalidating existing LOB/REF CURSOR/complex-type contexts; subsequent work still binds the same transaction. Complete ERR retains the active transaction for caller decisions. Cancellation/timeout/disconnect/bad response/OB20 boundary failure permits neither replay nor reusable session. Reject precancellation, invalid names, completed/stale transactions after close/reopen, active readers, and ambient transactions before sending.

Files: modern overrides in OceanBaseTransaction.cs; new OceanBaseConnection.Savepoints.cs reuses Transactions.cs exchanges/state guards; SavepointTests.cs and SavepointConsumerTests.cs cover local protocol, five-asset shape, and actual three-modern-asset consumption; synchronize README, architecture, ledger. Review injection, accidental transaction detachment, callbacks/reentry under locks, post-rollback resource invalidation, and true async waits.

- [x] First write failing public sync/async lifecycle, invalid-name, and error regressions against unsupported base behavior; implement the minimal shared control path and pass targeted tests.
- [x] Three-modern-asset/both-mode standard/OB20 consumption, delayed async, trailing-response/disconnect tests; Standard assets add no API. MySQL uses explicit authenticated mode-fact fixtures only; A05 unchanged.
- [x] Five-core/five-DI Release, full Unit/Consumer/DI, scoped format/diff checks; independent read-only review, necessary corrections/reverification, evidence synchronization and commit. Real savepoint data/locks/DDL, database/OBProxy, and Linux/Windows deployment remain unverified.

Additional limits: complete ERR has no transaction-state field; retaining local binding does not prove server transaction state. The name subset excludes ORM default __EFSavePoint; SupportsSavepoints does not imply full ORM compatibility. No independent syntax evidence for quoted OceanBase Oracle names was found, so do not guess broader support. Independent review found incorrect savepoint Activity-name mapping, fixed with two red/green regressions. Full Consumer found old name-only RollbackAsync reflection ambiguous after overloading; change only that assertion to the exact CancellationToken signature, without removing tests or changing product API.

### B08 Cancellation, Timeouts, and Unknown Outcomes

**Deliverables:** cancellation before send yields `OperationCanceledException`; once sending starts without a trusted terminal response, return `OceanBaseException(IsExecutionOutcomeUnknown=true)` and destroy the session. `DbCommand.Cancel()` shares this state machine.

- [x] Local faults cover before-send, partial header/payload, received request without terminal response, complete ERR/OK racing cancellation, and interrupted `Commit`/`Rollback`. Synthetic loopback does not prove server execution; live post-execution disconnect remains separate.
- [x] Coordinate caller token, Cancel, budget expiry, and close. Preserve external tokens; map internal budget expiry to TimeoutException. Late cancellation cannot overwrite complete terminal responses, though transport may be conservatively destroyed. Old-command Cancel/Dispose cannot affect reopened sessions.
- [x] Deterministic sync/async faults prove no replay. Async partial-send tests wait for real budget-token interruption and assert exact written prefix, unknown-outcome versus prepare distinction, Broken state, and log attribution.
- [ ] Live both-mode/OBProxy post-execution disconnect, commit/rollback interruption, cancellation/network ordering; non-Windows/old-runtime acceptance remains deferred by user instruction/environment.

2026-10-19 local verification: five-core/three-DI Release build, 0 warnings/errors; Windows Unit498, Consumer39, DI8 pass (TestResults/b08-{unit,consumer,di}.trx). Seven async partial-send cancellation cases requested by review were added; three timeout cases first failed then passed after correction. Receive timeout/log misclassification also has regressions. All five assets pass precancellation/command timeout on net10.0. Format, both packages, dependency groups, and shared-version checks pass. Local B08 complete, overall pending; next B09. Trusted MySQL-mode gap and G0/G1/G2 unchanged.

### B09 G2 Public API and Both-mode Acceptance

Local steps (2026-10-19): first demonstrate missing ProviderType through schema/parameter regressions; append scalar families to OceanBaseDbType preserving values 0..9. OceanBaseParameter maps to supported DbType/codecs; ResultColumn/ResultDbColumn expose matching integer ProviderType. Normalize ordinary strings/bytes to String/Binary; DataTypeName preserves database distinctions, without pretending protocol codes. NULL maps independently and accepts only null. Update reader/five-asset tests and inspect factory/DI/logging/unsupported public APIs. Run five-core/three-DI build, Windows Unit/Consumer/DI, format, both packages, asset/dependency checks; independent review then commit. Live/non-Windows deferred; net10.0 cannot substitute for old-runtime acceptance.

**Deliverables:** five-target B01-B08 compilation/consumption; modern data-source/DI lifecycle/basic logging, both-mode CRUD, parameters, low-precision `OceanBaseDecimal` provider-specific access, transaction/reader/cancellation evidence; public API review listing intentional Java gaps.

- [x] .NET 10 SDK Release slnx five-core/three-DI build and Windows Unit/Consumer/DI pass; daily hosts remain net10.0. Independent core samples provide basic Windows .NET 8/9/10 consumption evidence for five assets, scoped below.
- [ ] Older-runtime modern DI, full core API, and formal .NET Framework4.8 support still require R01/version-matrix acceptance; basic samples cannot substitute.
- [x] Isolated baseline consumers resolve logging dependencies; core has no reverse DI reference; Standard exposes no modern data source. Local data-source/DI/factory configuration/security/lifecycle pass; actual mode/SQL interoperability remains subject to live gates.
- [x] Public API/intentional Java gaps recorded in architecture §5; complete ProviderType/scalar mappings while preserving enum identity; fix command-event category filtering.
- [ ] Run B integration suites against direct/OBProxy both-mode combinations; record versions, failures, unsupported items.
- [ ] Close G2 only after every required combination passes with no P0/P1 defects.

2026-10-19 B09 local results: 65 new type/mode unit cases (initial 49 failed then passed), existing schema/parameter assertions extended across five assets; four new command-category regressions red then green. Final Release build 0 warnings/errors; Windows Unit567, Consumer39, DI8 pass (TestResults/b09-{unit,consumer,di}.trx). Format/diff, both packages, ZIP/XML five/three assets, eight dependency groups, shared 0.1.0-alpha, exact core dependency pass. Independent review found no remaining required fixes.

Basic runtime consumption: SDK10 csc compiles `RuntimeConsumerSample.cs` to standalone Smoke.dll using reference packs 8/9/10, corresponding product DLL, and runtime packages from project.assets.json. Dependencies live in private directories; runtimeconfig fixes Microsoft.NETCore.App major with LatestPatch. Generated compile.rsp/runtimeconfig/results.txt are in ignored artifacts/b09-runtime-smoke. In each directory run `dotnet <SDK>/Roslyn/bincore/csc.dll -noconfig @<directory>/compile.rsp`, then `dotnet <directory>/Smoke.dll <runtime-major> <logging-major> tests/fixtures/protocol/initial-handshake-oceanbase.json <product-TFM>`. Results: Standard2.0/2.1/net8→8.0.31/logging8, net9→9.0.20/logging9, net10→10.0.12/logging10; all five pass precancel/Oracle-loopback/decimal/schema/close. Source is tracked; generated materials are local evidence. Daily test projects remain net10.0. No live database, full-API, or old-runtime DI coverage.

B09 local review/completion done, overall pending. Both-mode direct/OBProxy, non-Windows, full old-runtime consumption, and A05 MySQL identification remain open; G2 stays open. Next C01 pooling/complete reset; reject Pooling=true until a trusted complete-reset version allowlist exists.

## C. Production Connections

### C01 Pools, Complete Reset, and Session Isolation

2026-10-19 inspection: no version/mode allowlist has passed live complete-reset verification. Java 2.4.14 OceanBaseConnection.reset (from line 2473) enables COM_RESET_CONNECTION only for certain MariaDB versions, otherwise relying on tracked state; this does not prove cleanup of raw session SQL/temp objects. C01 remains blocked and public Pooling=true rejects. No test-only pooling bypass. With live tests deferred, proceed to independent C02.

**Deliverables:** optional data-source-owned pools keyed by normalized options, credential identity/material, and TLS target; pooling off by default. Accept `Pooling=true` only after complete-reset verification for that version/mode. Explicit sources never share physical sessions; logs inherit source contexts. Disposal clears idle sessions and destroys active borrowers upon return; open/dispose races cannot orphan sessions. Direct-connection pool scope is separate from explicit sources and shares pool/reset implementation.

- [ ] Test isolation for `SET`/`ALTER SESSION`, schema, user variables, temporary objects, unfinished transactions, prepared statements, readers, reset failures.
- [ ] On return, drain/destroy unread responses, rollback, server-reset, reinitialize; destroy any session whose cleanup cannot be proven.
- [ ] Test capacity, wait timeout, credential rotation, fault clearing; record exact poolable-version matrix.

### C02 Multiple Addresses and Failure Boundaries

**Deliverables:** sequential candidate attempts before connection establishment; every new session repeats TLS/authentication/mode/capability/initialization. Never migrate/replay sent SQL, transactions, readers, or locators.

Local steps (2026-10-19, existing C02 authorization):

1. Existing Server/Host accepts comma-separated host[:port], [IPv6][:port], and portless IPv6; missing port inherits Port (default2881). Preserve keys/single addresses, trim candidates, reject empty/invalid hosts/ports at configuration boundary. No JDBC URL/role/discovery syntax. First demonstrate gaps with builder-invalid-input and first-address-failure loopback cases.
2. Internal/OceanBaseEndpoint parses; OceanBaseConnectionOptions freezes read-only candidates and derives single-endpoint snapshots preserving TLS/credentials/mode/all configuration. PhysicalSession sync/async attempts sequentially only on TCP socket-connect errors. Entire Open shares one timeout/cancel budget (0 infinite), never reset per candidate. Once TCP connects, TLS/auth/protocol/mode failure stops immediately rather than hiding configuration/security failures.
3. Each Open starts at the first candidate; direct/source paths share sessions. TLS SNI/certificate name/authentication _server_host use the actual host, with explicit TLS Server Name taking precedence. DataSource retains configured Server list; successful-open/close/command/transaction logs use actual endpoint, without default passwords/full connection strings.
4. Windows loopback: sync/async/five-asset consumption, explicit/inherited ports, IPv6 parsing, all-fail, cancellation/close/total budget, TLS names/rejection, mode mismatch, and no fallback after sent-packet disconnect. Five-core/three-DI compilation, Unit/Consumer/DI, format, both packages/dependency matrix, independent review, commit. Live direct/OBProxy and non-Windows remain open.

- [x] Synthetic Windows loopback covers first-candidate failure/second success and no retry after send; real topology remains below.
- [x] Windows loopback covers per-address SNI/auth host, explicit TLS name, certificate rejection, cancellation/close/total timeout/mode; trusted private-CA success remains environment-dependent.
- [ ] Direct/OBProxy faults record target versions/topology.

2026-10-19 local results: 60 new Unit, 10 five-asset sync/async Consumer; initial 26 address/input regressions all failed then passed. Release slnx five-core/three-DI 0 warnings/errors; Windows Unit627, Consumer49, DI8 pass (TestResults/c02-{unit-final,consumer,di}.trx). Independent review corrected the budget test's false assumption about loopback refusal duration; two adjusted tests pass separately (c02-budget-reviewed.trx). Timing bounds prove whole-Open limits; source review separately confirms one budget outside both candidate loops. TCP/async handshake share the token; synchronous TLS/auth uses registered transport close for interruption. Format/diff, both packages, five/three assets, eight groups, shared0.1.0-alpha, per-TFM Microsoft.Extensions8/9/10 pass. NuGet README warning remains R01.

Windows runtime samples reuse B09 RuntimeConsumerSample.cs with a public query after first-address refusal and identical SDK10 csc/reference-pack/private-dependency/LatestPatch isolation. Five assets pass on 8.0.31 (both Standard/net8),9.0.20,10.0.12 with logging majors8/9/10. Scripts/response files/results are local untracked artifacts/c02-runtime-smoke; reproduction matches B09. This adds no old-runtime DI/full-API/live both-mode acceptance. Local C02 complete, overall pending; C01 blocked, next locally viable C03; G3 remains0/8.

### C03 Exact Numerics, Nanoseconds, and Zero Dates

**Deliverables:** extend B-stage `OceanBaseDecimal` to wide-precision codecs, preserving `BigInteger` coefficient/scale; temporal values retain nanoseconds and offset/zone information. MySQL `DATE`/`DATETIME` provider-specific types remain stable across each column; zero dates never map to `DateTime.MinValue`.

2026-10-19 staged implementation: C03 stays in progress until all subitems finish. Implement exact numerics first, then nanoseconds/zones/zero dates; numeric success alone does not complete C03. Changes span DecimalCodec, ResultColumn, ResultDbColumn, PreparedParameters, OceanBaseDataReader, five-asset consumption. First expose gaps using DECIMAL(65,20), NUMBER(38), unknown-precision markers, negative scale, and exact conversion. Declared metadata fixes standard types: reliable p<=28 and 0<=s<=p gives decimal; otherwise OceanBaseDecimal; NULL never narrows types. Normalize Oracle p=255/s=-85 to unknown p=0/s=0 as Java ColumnDefinition does. Unknown precision stays unknown in schema; p0/s0 preserves payload coefficient/scale. With p0/known nonzero scale, rescale exactly and enforce Oracle38/MySQL65 limits without inventing schema precision. Negative declared scale rescales without rounding. MySQL max p65/s30; Oracle p38/s[-84,127], nonzero absolute range[1e-130,1e126); reject overflow. Unknown NUMBER permits at most38 significant digits in that range. GetDecimal/GetFieldValue<decimal> convert exactly only. Parameters never narrow through CLR decimal/float; Java BigDecimalParameter-style length-encoded plain decimal text, no exponent; expand negative scale into integers. Target metadata determines readback scale. DbParameter.Scale is byte; negative scale belongs to OceanBaseDecimal.Scale, without incompatible overrides. Build/regressions/packages/review then numeric-subitem commit; live/non-Windows deferred.

- [x] Local numeric build/tests/independent review: MySQL `DECIMAL(65,20)`, Oracle unspecified/negative-scale `NUMBER`; text/prepared field types, provider-specific read/write, five-asset and mode boundaries. Unit672/Consumer54/DI8, format, both-package asset/dependency matrix pass; see ledger.
- [x] `GetDecimal`/`GetFieldValue<decimal>` convert exactly; declared types stay stable across values/NULL. Red/green fixes cover inferred-precision overrestriction, missing MySQL negative-scale precision validation, and incorrect zero rejection.
- [x] Local Oracle `TIMESTAMP(0..9)` without zone: read/write/exact conversion/stable types/five-target consumption/review; Unit716/Consumer59/DI8, build/format/package matrix pass; ledger records evidence.
- [x] Unzoned `GetDateTime`/`GetFieldValue<DateTime>` converts only if exactly representable in 100ns, otherwise overflows. All precisions have fixed OceanBaseTimestamp provider-specific type.
- [x] TZ text/prepared reads, writes, stable standard/provider-specific types, exact DateTimeOffset conversion, five-asset consumption; review found parameter-length/negative-offset issues, now regression-fixed; see ledger.
- [x] LTZ server session-zone identity, query completion/result snapshots, exact201 codecs, parameter-zone matching, five-asset consumption; local evidence in ledger, live interoperability open.
- [x] MySQL DATE/DATETIME/TIMESTAMP mixed normal/zero-component/NULL rows, stable text/prepared types, exact writes, five-asset consumption; internal mode fixtures only, not public MySQL handshake/live support.
- [ ] Live both-mode tests record session zone, charset, server column definitions.

2026-10-19 nanosecond detail: deliver unzoned Oracle TIMESTAMP(0..9) first; retain TZ/LTZ/zero dates as subsequent items. Shared immutable OceanBaseTimestamp stores whole seconds with DateTimeKind.Unspecified, Nanosecond0..999999999, Scale0..9. Reject fractional seconds/zones in WholeSeconds and nanoseconds inconsistent with scale; never round. ToDateTime requires nanoseconds divisible by 100. Years1..9999 only; reject others. Standard p<=7 is DateTime, p8/9 OceanBaseTimestamp; provider-specific always OceanBaseTimestamp; NULL stable. Both result protocols use Java type 202/12-byte layout with payload/column-scale equality. Provider parameters retain Scale by default (ordinary DateTime defaults7); explicit Scale allows exact rescaling only. Record the difference from Java's fixed write-scale9 without changing Java. Changes span TemporalCodec, ResultColumn/DbColumn, OceanBaseParameter/PreparedParameters, reader, consumers. Gap tests precede values/codecs/public loopback/consumers; full build/tests/review/packages before commit. Cover small p8/9 values and NULL, nanos1/999999999, date boundaries, bad lengths/scales/fractions/modes, and pre-send rejection.

2026-10-19 TZ/LTZ detail: independent TZ pipeline precedes trustworthy LTZ session-zone sourcing; C03 remains in progress. OceanBaseTimestampWithTimeZone combines OceanBaseTimestamp, minute-resolution offset, optional zone name/abbreviation. Never recompute server offset through client zone rules. DateTimeOffset input retains local fields/offset; exact ToDateTimeOffset fails for range overflow or nanoseconds not divisible by 100. Standard/provider types fixed for p0..9. Type200 results share12-byte timestamp plus signed-hour/minute-magnitude and optional name/abbreviation length fields; validate lengths/UTF8/offset/scale. Both results/parameters use hour sign and minute magnitude, with signed minutes when hour=0. Results use outer length encoding; parameters use one-byte length and reject >255 bytes before send. Names/abbreviations each permit255 UTF-8 bytes, with separate total parameter bound. Pinned server evidence/name-over-offset precedence: architecture§5; real accepted ranges unverified. Retain default scale; explicit scale exact only. DbType.DateTimeOffset/new trailing OceanBaseDbType.TimestampWithTimeZone map to Oracle200; MySQL/mismatched explicit types reject pre-send; old enum values unchanged. Test values/codecs/sync-async text-binary loopback/read-write/NULL schema/malicious payloads/five assets; full validation/review before commit. LTZ must not adopt Java's default client zone: define session query/tracking/invalidation/result snapshots rather than applying the authentication snapshot to arbitrary later SQL.

2026-10-19 local TZ verification: Unit772/Consumer76/DI8 pass; five-core/three-DI Release 0 warnings/errors; format/diff/both packages/asset-dependency-version matrix pass. Review fixed one-byte parameter length, negative-hour minute magnitude, internal result-name length encoding with red/green cases. Maximum result530 bytes, parameter255 bytes. Five assets run on net10; live/non-Windows/old-runtime acceptance absent. C03 then in progress; LTZ/zero dates follow.

2026-10-19 LTZ prerequisite: repair post-authentication zone-state flow without enabling201. PhysicalSession stores invalidatable time_zone facts independently from ConnectResult authentication snapshot. Authentication reports seed state; before any SQL, capture execution snapshot and immediately invalidate current session facts so SQL that may mutate state cannot leave stale values. Prepare/statement-close execute no SQL and do not invalidate; close/fault clears state. Before every result-header read, QueryResult snapshots execution-start/previous-report zone. Terminal reports never rewrite that result snapshot. OK headers, deprecated-EOF OK terminators, transaction-control OK consume time_zone reports. Subsequent results use latest reports; each result start invalidates session/current subsequent source again, covering A-report→no-report→unknown-third-result. Classic EOF/ERR invent neither variables nor authentication defaults. Reliable on-demand/preexecution SESSIONTIMEZONE completion is still required before temporal decoding; this prerequisite alone proves neither LTZ implementation nor trustworthy tracking.

Changes span PhysicalSession, CommandExchange, QueryResult, transaction-control OK; no public options/dependencies. Windows sync/async regressions first: authentication-snapshot independence, arbitrary-SQL invalidation, terminal/multiple-result updates, snapshot independence, prepare exemption, fault/close clearing. Then minimal shared state methods, full build/tests/format/packages/version matrix/review/commit. LTZ still needs query source, parameter-zone matching, full201 codecs; state plumbing cannot substitute.

2026-10-19 LTZ-state verification:25 targeted cases (initial 12 failing, two additional review failures, all corrected) included in Windows Unit797; Consumer76/DI8 pass. Five-core/three-DI Release 0 warnings/errors; format/diff/packages/matrix pass; independent re-review has no required fixes. QueryResult capture moved before header read; subsequent ERR invalidates zone. Full LTZ query/parameter/value codecs and zero dates remained subsequent work.

2026-10-19 complete LTZ pipeline: immutable OceanBaseTimestampLocalTimeZone combines OceanBaseTimestamp/SessionTimeZone. Identity is nonempty strict UTF-8<=255 bytes without whitespace/control characters; reject unresolved SYSTEM; numeric offsets strictly±HH:MM within±14h. No local-zone resolution or speculative DateTimeOffset. ToDateTime/GetDateTime exact only, Kind=Unspecified. Uninitialized default lacks zone and cannot be written. Standard/provider types fixed for p0..9; append OceanBaseDbType.TimestampLocalTimeZone value28. DbType.DateTime2 selects Oracle201 only with explicit provider identity/value; ordinary TIMESTAMP stays202.

Before Oracle execution with no usable zone report, query SELECT SESSIONTIMEZONE FROM DUAL under the existing exclusive lease/timeout. Require one string column, one nonNULL row, strict identity<=255 bytes, trusted terminal response. Do not affect RecordsAffected, log a separate command, or label query failure as user-SQL execution. Validate ordinary binds/values first; prepare/execute only after query and exact LTZ-parameter identity match. Valid reported zones need no query; every SQL still invalidates state, with no long cache. Prepare-only never queries; transaction-control commands do not query merely because no LTZ is returned. Internal query supports classic EOF/FEOK, drains in the same exchange without releasing lease; unknown response destroys/no replay. Extra round trips are the correctness cost; no new option.

Columns bind each result's frozen zone. With unknown zone retain stable types/DBNull (still checking mode/scale), but explicitly reject nonNULL201 decoding. Never insert queries into active readers or buffer all results. No stale identity after arbitrary SQL/multiple results without reliable reports; reported later results use fresh identity. Type201 first12 bytes reuse202 layout in both result protocols/parameters; no nanosecond/scale truncation. Changes reach public value, TemporalCodec, ResultColumn/QueryResult, PreparedParameters/Parameter/DbType, reader, internal CommandExchange query, OceanBaseCommand. Sync/async/five-asset tests cover unknown zone, pre-send mismatches, query failures/snapshots. Full verification/review/commit; pinned Java/server evidence in architecture§5; database/non-Windows deferred.

2026-10-19 LTZ local results: initial 8 gap tests red→green; review reproduced three conflicting-report/explicit-Timestamp-override failures, fixed before 49 targeted cases passed. Final Unit846/Consumer81/DI8, five-core/three-DI Release 0 warnings/errors; format/diff/packages/matrix pass. Ledger retains live gaps. C03 then in progress, zero dates/live acceptance remaining.

2026-10-19 C03 zero-date plan (confirmed§5 contract):

- [x] First reproduce provider-type gaps with text/binary normal/all-zero/partial-zero/NULL cases. Standard DateTime stays stable; zero components throw InvalidCastException on conversion; IsDBNull detects protocol NULL only.
- [x] Shared OceanBaseDate(year,month,day)/OceanBaseDate(DateTime), OceanBaseDateTime(date,hour,minute,second,microsecond,scale)/OceanBaseDateTime(DateTime), without zone. Year0..9999/month0..12/day0..31; nonzero month/day must form a valid calendar (year0 leap-day checked as400). Preserve zeros, reject other ALLOW_INVALID_DATES calendars. Microsecond0..999999/scale0..6, no rounding; default=all-zero. Expose Date components, IsValidDate/IsValidDateTime, ToDateTime, fixed-format ToString. No zero→NULL/min-date option.
- [x] TemporalCodec handles raw MySQL DATE10/DATETIME12/TIMESTAMP7 components, strict text and binary lengths0/4/7/11, component/precision bounds; DATE only 0/4. Never normalize invalid data. ResultColumn provider types Date/DateTime, standard exact DateTime; reader GetProviderSpecificValue(s)/GetFieldValue<T>/both schema paths integrated; Oracle unchanged.
- [x] PreparedParameters/Parameter infer Date/DateTime; provider values write only matching MySQL types. Explicit mismatch/Oracle/precision loss reject pre-send. Zero writes length 0; partial-zero writes components. No enums/dependencies added. Existing Timestamp parameters still map to MySQL DATETIME12, without promising restoration of a zone instant.
- [x] Red/green codec/parameter, sync/async mixed reader/prepared rows, malformed/precision/mode boundaries, five-asset type/consumption tests. MySQL uses internal fixtures; A05 rejects untrusted missing-mode-bit handshakes. Release slnx/full Windows/format/package matrix/review/corrections, synchronize three docs and commit. Live/non-Windows deferred; overall C03 pending verification, not complete.

2026-10-19 zero-date results: initial 9 failures fixed;49 targeted cases pass for strict text/binary, zero components, nonzero clock on zero dates, microseconds/scales, mixed TIMESTAMP rows, NULL, mode/precision rejection. Windows Unit895/Consumer91/DI8 all pass; five-core/three-DI build0 warnings/errors, format/diff/both packages/version matrix pass. All C03 local subitems implemented, status pending verification; live/non-Windows/A05 public MySQL gaps remain. Next local C04; C01 complete reset blocked.

### C04 Large Values, SequentialAccess, and Server Cursors

**Deliverables:** `SequentialAccess` never implicitly buffers entire values; cursors belong to the physical session/statement ID and are released or destroyed on close/cancel/disconnect.

Local order: single-owner incremental PacketChannel payload stream, then ordered QueryResult/reader fields/character decoding, then prepare/execute/fetch cursors with explicit enablement. Incremental streams read only requested bytes, validate cross-frame sequences/cumulative64 MiB, and forbid new packet reads/sends/sequence resets/transport replacement while unread. Early disposal/corruption/cancellation closes transport; only complete consumption permits another message; unknown execution never replays. Incremental packets, sequential fields, and cursor paging are now locally implemented; C04 pending live verification.

- [x] Incremental stream sync/async, continuations/empty terminator, sequence/cumulative bound, truncation/cancel/early close; generated large payload proves chunking/fixed memory without preallocating test payload.
- [x] Text/prepared ordered fields, GetBytes/GetChars/GetStream/GetTextReader, ordinal/offset progression, NULL, multiple results, close/cancel ownership.
- [x] Reader large-value chunking, out-of-order limits, early close/cancel, bounded allocations.
- [x] Local cursor paging/early-close/cancel lifecycle.
- [x] Select cursor protocol by authenticated mode/negotiated EOF; missing acknowledgement/unsupported responses fail and destroy session, never reexecute fallback.
- [ ] Declare supported combinations only after live mode/version/capability acceptance; fixtures are not version allowlists.
- [ ] Measure peak memory, throughput, resource release on real tenants in both modes.

2026-10-19 incremental transport: internal OpenPayload/OpenPayloadAsync; full-packet reads share frame validation/fail-close.29 new cases cover demand reads, generated32 MiB payload/synchronous-loop allocation<256 KiB, continuations/exact-full-frame terminator, full/streaming sequence wrap, partial-I/O cancellation, cumulative bounds, invalid arguments/early disposal.54 total packet cases pass. Initial 21 missing-API cases failed before implementation; two full-packet-limit fail-close cases red→green. Five-core/three-DI Release 0 warnings/errors; Windows Unit924/Consumer91/DI8 pass; independent read-only review no required fix. Allocation evidence covers internal synchronous managed streaming only, not real reader peak memory/throughput. At that commit C04 in progress, transport only; reader follows. No live/non-Windows/new old-runtime acceptance.

2026-10-19 reader continuation: text/prepared-binary, classic/deprecated EOF integrated with sync/async fields; pre-read only row prefix/NULL bitmap. GetBytes/GetChars/GetStream/GetTextReader and async getters enforce ordinal/offset progression, NULL, explicit whole-field materialization, stream invalidation. GetChars null-buffer length probe rejects in sequential mode. Strict UTF-8 preserves BOM/surrogate boundaries; default mode unchanged. Shared8 KiB drains skipped columns/rows/results. Early close destroys unread sessions. Architecture specifies sequential contract.

Review issues—buffered characters/exhausted streams bypassing cancellation/close, late cancellation misclassifying complete terminators, field tokens excluded from command budget—have failing-first fixes. Malformed binary field framing now gives unknown outcome/discard. Five-asset isolated net10 hosts do not prove old-runtime support. Generated32 MiB row proves ExecuteReader returns before body arrival, streaming across continuations, synchronous drain allocation<1 MiB; not live peak-memory/throughput evidence. Five-core/three-DI Release 0 warnings/errors; Windows Unit978/Consumer101/DI8 pass (TestResults/c04-reader-*-final.trx);54 sequential/zero-date cases and five-asset stream/nanosecond-provider consumption cover current contract. C04 then in progress, cursor/live large-value/throughput/resource acceptance remaining. A05 public MySQL/C01 reset gates unchanged.

C04 local cursor implementation (2026-10-19, existing TDD/independent review, merged into existing docs):

- [x] Entry: OceanBaseCommand.FetchSize defaults0 (no cursor); positive forces prepare and snapshots page size; negative rejects. PreparedStatement retains column count; reject non-result statements before execute. PreparedParameters.Execute uses readonly cursor flag1. Oracle+ClientDeprecateEof omits execute/fetch metadata EOF per Java evidence, first fetch confirms termination; others strictly require EOF/FE-OK cursor acknowledgement. A05 unchanged.
- [x] QueryResult handles execute metadata by mode/capability (Oracle deprecated-EOF exception above); others fetch after CursorExists.9-byte COM_STMT_FETCH contains LE32 statement ID/row count; reset sequence per request, retaining lease/total budget, never reexecute SQL. MySQL reads binary rows; Oracle validates repeated metadata. Continuing pages require CursorExists/progress; LastRowSent alone ends lease. Multiple-result cursors currently reject/destroy, without fabricated support.
- [x] Default/SequentialAccess share paging; HasRows prefetches first row only, NextResult drains remaining pages. Early Close/cancel/timeout/disconnect destroys original session; cached statements never cross sessions. Test pages/empty final pages/reuse/mode differences/bad flags/metadata/excess rows/true async/no replay.
- [x] Five-core/three-DI build, Windows Unit/Consumer/DI, format/packages/version matrix; independent review/fixes, synchronize plan/architecture/ledger, commit current master. Live both-mode/OBProxy/non-Windows deferred; local tests do not close interoperability.

Red/green review fixes: lease release before cursor-ERR destruction, binary rows impersonating FE-OK acknowledgement, missing intermediate-page reports, initial empty result clearing reports twice.16 paging combinations cover sync/async, default/sequential, two modes/two EOF forms, plus first fetch for empty Oracle deprecated-EOF. Five-asset isolated consumers cover Oracle classic/deprecated EOF. Shapes/omission rules: architecture§3. Real cursor resources/memory/throughput unverified; local completion does not close G3. Final five-core/three-DI Release 0 warnings/errors; Windows Unit1037/Consumer111/DI8 pass (TestResults/c04-cursor-*-final-checked.trx); format/both packages/shared version/per-TFM Microsoft.Extensions matrix pass; re-review no required fixes.

### M08 Oracle Fetch with Offset: Local Implementation (2026-10-20)

Architecture M08/pinned Java6adc311ba5db8b2f19f98f3e6e4734667fdfdda1: AbstractConnectProtocol.supportFetchWithOffset requires Oracle, server prepare, explicit option, OceanBase>=2.2.5. AbstractQueryProtocol.fetchRowViaCursorForOracle specifies19-byte request; CursorResultSet.getCursorFetchData confirms trailing OK after each page's row terminator. Preserve five targets/no dependencies; implement/test/review/commit locally in current checkout, no push.

Command UseCursorOffset(bool,false) plus positive FetchSize enables classic prepared Oracle forward cursors. No scrolling ADO.NET reader API/REF CURSOR change. Reuse strict ob_version() query/parsing in the same exchange/total timeout; PhysicalSession caches version shared with PrepareExecute, whose2.2.76 gate stays. Wrong mode/nonpositive FetchSize reject pre-send; old/unrecognized versions send no prepare/execute/fetch, with no silent normal-fetch fallback.

Use Packet's nonscroll OCI_FETCH_NEXT=2, offset0, final field1, LE32 ID/count, LE16 orientation. Keep repeated metadata/binary rows/EOF-FEOK validation. Trailing OK must be complete/valid, no multiple results/OUT, lastInsertId0, affectedRows equal cumulative forward row position. Position never enters RecordsAffected. Validate trailing OK and standard/OB20 boundary before next fetch/completion. Missing/bad/ERR/cancel/disconnect destroys original session, no replay.

Evidence limit: Java next() actually uses ABSOLUTE; NEXT/zero offset derive from protocol constants/nonscroll description. Equality to cumulative position is this implementation's strict constraint; Java only establishes trailing affectedRows as position. Neither convention is live-server-proven; real matrix remains open.

Files: OceanBaseCommand.cs option/snapshot/version admission; PhysicalSession.cs and CommandExchange.OraclePrepareExecute.cs shared version; QueryResult.cs/QueryResult.Cursor.cs/QueryResult.Sequential.cs extended packets/terminal tail. CursorOffsetTests.cs/CursorOffsetConsumerTests.cs reuse loopback/fault/OB20 helpers; README/architecture/ledger synchronized.

- [x] Public regressions set missing property through reflection: both EOFs, sync/async, default/SequentialAccess, multipage/empty final page, version boundaries/cache/pre-send rejection.
- [x] Minimal shared path; tail OK delay/ERR/truncation/bad counts/flags/boundaries/cancel/early close/reuse regressions; public five-asset standard/OB20 consumption.
- [x] Five-core/five-DI Release, full Unit2994/Consumer1974/DI14, format/diff/both-package consumption/independent read-only review pass; fix/reverify/evidence/local commit. Live data/version/OBProxy/Linux-Windows deployment not executed, explicitly unverified.

### C05 Database Catalog Metadata

**Deliverables:** initial `DbConnection.GetSchema` collections/restrictions, modern `GetSchemaAsync`. C implements MySQL `ChangeDatabase`/true `ChangeDatabaseAsync`; Oracle rejects before sending. Each mode uses its own system views, never the wrong dialect.

- [x] Fix initial shapes: `MetaDataCollections`, `DataSourceInformation`, `Tables`, `Columns`; reject other collections.
- [x] Both-mode cases cover casing/schema/restrictions/permission errors/async nonblocking waits; verify MySQL Database/query destination after switch, unchanged state on failure, Oracle rejection without packets.
- [x] Record Java metadata gaps for phase D.

Local order (2026-10-19, existing docs/native implementation/independent review):

- [x] Database partial: MySQL COM_INIT_DB sends exact strict UTF-8 names, no USE concatenation; reject null/empty/NUL/invalid UTF-16/oversize and Oracle pre-send. Reuse CommandExchange lease/Command Timeout/no replay; sync I/O and true Standard2.1/modern async overrides.
- [x] PhysicalSession owns current Database; update only on valid terminal OK/current lease. Failure leaves state; config/source keep original database, reopen restores it. Old responses cannot mutate new sessions; publish Database before completion callbacks. Prepared reuse compares preparation database even for same SQL. Allow switches inside idle explicit local transactions without changing bindings/commit state; reject busy readers/commands/ambient transactions pre-send.
- [x] Database tests: sync/async/exact special/case-sensitive names/success/rejection/bad packets/partial I/O/cancel/timeout/reopen/log reentry/prepared reuse/transactions/source isolation. Five-asset MySQL consumers use internal post-auth facts only; A05 unchanged.
- [x] Four catalog shapes/restriction order; compare Java both-mode SQL, bind protocol parameters instead of SQL concatenation; shared sync/async loading retains permissions/cancellation.
- [x] Database module five-core/three-DI build, Windows Unit1073/Consumer120/DI8, format/packages/matrix pass; review no required fix; update three docs/commit master.36 new Unit/nine five-asset Consumer; TestResults/c05-database-*-final.trx.
- [x] Catalog module independently repeats build/test/review/commit gates; live permissions/transaction-switch interoperability/cross-platform/old runtimes remain open.

2026-10-19 database results: initial five failures corrected;36 targeted cases for exact transmission/mode-name-ambient rejection/trusted ERR state retention/bad OK-response/partial send/cancel-timeout/reader exclusivity/local transaction binding/callback publication/source isolation/reopen. Compare prepared reuse after lease acquisition; existing queue closes stale IDs. Failed switches retain IDs. Nine isolated Standard2.0-sync/other-four-sync-async cases pass on net10. Five-core/three-DI Release 0 warnings/errors; Windows Unit1073/Consumer120/DI8 pass; review no required fix. C05 then in progress pending catalogs. Fixtures do not prove live switching/transactions/permissions/public MySQL handshake.

Catalog detail (2026-10-19, reuse commands/readers):

- [x] OceanBaseConnection.Schema.cs overrides three sync GetSchema forms on five targets and three GetSchemaAsync forms on modern targets. Static collections return tasks; catalogs await ExecuteReaderAsync/ReadAsync/NextResultAsync. Require open/idle/no ambient transaction; snapshot restrictions, bind current explicit transaction and fixed physical session so reopen cannot run old query in a new mode.
- [x] MetaDataCollections: CollectionName:string, NumberOfRestrictions:int, NumberOfIdentifierParts:int; four rows restrictions0/0/4/4 and identity parts0/0/3/4. Tables: string TABLE_CATALOG/TABLE_SCHEMA/TABLE_NAME/TABLE_TYPE; same restriction order, TABLE/VIEW normalized. Columns: TABLE_CATALOG/TABLE_SCHEMA/TABLE_NAME/COLUMN_NAME:string; ORDINAL_POSITION:int; COLUMN_DEFAULT/IS_NULLABLE/DATA_TYPE:string; CHARACTER_MAXIMUM_LENGTH:long; NUMERIC_PRECISION/NUMERIC_SCALE/DATETIME_PRECISION:int. Restrictions=first four.
- [x] DataSourceInformation uses17 standard ADO.NET columns with enum types where applicable. Product OceanBase, raw authenticated version, mode-specific parameter/quote syntax. Unsupported normalized versions/regex/lengths/join operators remain DBNull; do not infer OceanBase version/case settings from MySQL-compatible prefixes.
- [x] Null restriction=no filter; empty/%/_ are exact literal values. No unquoting/case folding; server comparison decides. MySQL database→catalog/schema DBNull; Oracle OWNER→schema/catalog DBNull. Restricting absent identity parts returns empty fixed-shape tables. MySQL INFORMATION_SCHEMA.TABLES/COLUMNS; Oracle ALL_OBJECTS(TABLE/VIEW only)/ALL_TAB_COLUMNS. Sort by identifiers/ordinal; preserve NULL as DBNull; convert Oracle NUMBER exactly to fixed integers.
- [x] Failing-first four-collection/both-mode sync-async/restriction-binding/NULL/numeric cases; add transaction/permission/timeout-cancel-partial-send-close/empty-bad-shape/true-async/five-asset coverage. Reuse errors/exclusivity/unknown outcomes without replay/dialect fallback.
- [x] Java gaps: initially no complete JDBC schema/type/key/index/procedure/synonym collections, Java pattern matching/string interpolation not reused. Loopback does not prove real dictionary columns/permissions/case settings/default LONG/ordinal-version semantics. Full build/tests/format/packages/review/three-doc sync before commit.

Catalog results: initial 13 red→green;44 targeted/eight five-asset cases pass. Review's premature-rounding bug fixed with two failing-first cases/re-review. Five-core/three-DI Release 0 warnings/errors; Windows Unit1117/Consumer128/DI8 pass (TestResults/c05-schema-*-final.trx); format/UTF-8-no-BOM-CRLF/diff/five-core-three-DI packages/shared-version/per-TFM matrix pass. C05 pending verification; live catalog/permissions/version interoperability open; next local C06.

### M07 MySQL Binary Stream Parameters: Local Implementation (2026-10-20)

Architecture M07 long-parameter optimization was not yet implemented. Deliver MySQL LONG_DATA first; Oracle PIECE_DATA has separate acknowledgements and remains a later independent item. LOB locators/DbBatch do not constitute long-parameter upload. Work in current checkout, no dependencies/options/services added.

Contract: Input parameter Value=Stream, inferred/explicit DbType.Binary, MySQL classic server prepare/execute only. At execution reject Oracle, other declarations, nonzero Size/Precision/Scale, unreadable streams, and client-prepare fallback before packets. Read current position→EOF without Length/seek/disposal/rewind; caller supplies current position/new source on repeat. Prepare checks binds/server count only, never reads stream; execution retains existing value/declaration validation. Snapshot metadata/source references; caller exclusively owns sources; byte[] unchanged.

Fixed8192-byte chunks/seven-byte header(24, LE32 statement ID, LE16 parameter index); each starts sequence0, no response. Empty stream sends header-only LONG_DATA. Final EXECUTE includes type header, no inline stream value. Reuse PacketChannel offset/count writing and one buffer, no whole-stream materialization. Stream bytes+inline encoding+10-byte EXECUTE header share64 MiB budget; exceeding it never truncates-and-executes. Each chunk uses ResponseComplete(false) to validate no-response boundary, including subsequent OB20 requests. Async source ReadAsync/transport I/O use command-budget token. Sources must cooperate: synchronous Read/token-ignoring ReadAsync must return before interruption completes; no background task workaround.

Keep contamination flag from first chunk through trusted EXECUTE terminal. Error/cancel/close destroys original session, preventing residual parameter reuse. Failure before EXECUTE is not unknown SQL outcome; afterward existing contract applies. Complete server ERR retains code; no fallback/replay. Real LONG_DATA range/error cleanup/OBProxy compatibility unverified; A05 unchanged.

Files: OceanBaseParameter.cs inference; PreparedParameters.cs validation/inline omission; OceanBaseCommand.cs snapshot/send order; CommandExchange.LongData.cs chunk/budget; CommandExchange.cs contamination cleanup; LongDataTests.cs/existing five-asset consumers. Synchronize README/architecture/ledger.

- [x] Public execution red first: sync/async multichunk/empty/mixed-inline/no Length-seek/caller ownership.
- [x] Minimal implementation/faults: pre-send rejection, Prepare never reads, source error/cancel, exclusivity, uploaded-but-not-executed destroys original session, postexecution unknown/trusted ERR; existing parameter/fallback regressions pass.
- [x] Five-asset public consumption/all-target Release/full Unit-Consumer-DI/scoped format/both packages/review-fix/local commit. Live/deployment not executed/unverified; Oracle PIECE_DATA remains subsequent.

### M07 Oracle Binary Stream Parameters: Implementation Detail (2026-10-20)

Implement planned Oracle PIECE_DATA inputs, reusing Value=Stream, snapshots, classic prepare/execute, one command budget. No options/types/dependencies/services. Continue current checkout after MySQL. Readable Binary Input/zero Size-Precision-Scale only; reject client fallback and then-unintegrated PrepareExecute/array-input combinations before source read/send, retaining those as later items. Prepare never reads; current-position/ownership/reexecution/blocking-source cancellation match MySQL.

Pinned Java6adc311ba5db8b2f19f98f3e6e4734667fdfdda1 OBStreamParameter.writePieceData/Utils.encodeHexStr/AbstractQueryProtocol.executePreparedQuery: request0xA2/LE32 statement/LE16 index/piece byte/reserved0/LE64 encoded length/uppercase hex; EXECUTE type 254, no inline stream. Each chunk receives one valid OK and completes standard/OB20 boundary before next chunk/EXECUTE. Java available() does not generalize to .NET Stream:8192-byte chunks plus one-byte lookahead detect EOF; FIRST=1 if first is nonfinal, NEXT=2 middle, LAST=3 final/empty. No seek/Length/whole-stream buffer.64 MiB counts actual hex bytes+inline+10-byte EXECUTE header.

Existing _longDataPending is independent from response state. After upload, source errors/cancel/close/malformed responses/chunk ERR destroy original session; retain chunk-ERR code without claiming server cleanup. Pre-EXECUTE failure is not unknown SQL outcome; post-EXECUTE trusted/unknown semantics unchanged. Chunk OK applies session variables, not user RecordsAffected, and never releases lease early. Reject result sets/MoreResults/cursor flags. Source offers no reusable server-version gate; Java driver version is not server version. Real versions/OBProxy unverified.

Files: PreparedParameters.cs permits Oracle/type 254; OceanBaseCommand.cs rejects unsupported combinations and uses standard ConditionalWeakTable by Stream reference before snapshot/send to reject multislot reuse, including repeated Oracle names (no user Equals/implicit replay). CommandExchange.LongData.cs dispatches by mode; new peer CommandExchange.PieceData.cs reuses header/session APIs. CommandExchange.cs destroys only preexecute contaminated chunk-ERR sessions. PieceDataTests.cs/PieceDataConsumerTests.cs reuse TextCommandTests/Ob20ConsumerTests helpers, no mock database application.

- [x] Public sync/async empty/single/multichunk/short-read cases fail for missing support first, then minimal protocol path passes.
- [x] FIRST/NEXT/LAST, exact header/hex/no-inline, multistream/mixed/snapshots, Prepare/current-position/repeat/NULL, invalid declarations, source error/cancel/timeout/exclusivity, chunk ERR/malformed/disconnect, execution ERR/unknown outcome, state/cumulative bounds.
- [x] Five-asset sync/async×standard/OB20, full Release/Unit/Consumer/DI/format/diff/both-package consumption/review-fix/doc sync/local commit, no push. Live/deployment not executed. PrepareExecute remained next; do not close all M07 from this subitem.

### M07 Oracle PrepareExecute with Stream Input (2026-10-20)

Combine D02 procedure/OUT with M07 binary Stream Input so selecting PrepareExecute retains stream support. No options/types/dependencies/services. Require existing Use Oracle Prepare Execute/2.2.76 gate, transaction/exclusive lease/total budget. Only Input consumes streams; reject InputOutput before direction normalization. Output/ReturnValue ignore old Value including Stream, never read/register its identity; no stream-output API. Array elements reject Stream. Pinned Java AbstractQueryProtocol.executePreparedQuery prepares/uploads first; ComStmtPrepareExecute.send uses that statement ID and omits inline streams. Extended response may replace ID; clean both once.

Each execution creates an independent classic prepare ID. Count mismatch explicitly Releases into close queue; matching IDs transfer to OracleCursorOwner. Do not cache as command classic prepare or share ownership with active REF CURSOR parents. Existing cleanup handles source/chunk/cancel errors. Different returned ID queues original close; final ID follows output/REF CURSOR lifetime. OUT publishes atomically only after complete terminal/OB20 boundary. Budget subtracts full actual extended request(header/SQL/parameters/tail), then hex data, not merely classic10-byte header.

Files: OceanBaseCommand.cs prepares/uploads/builds with ID when Oracle plan has streams; CommandExchange.LongData.cs/PieceData.cs use full execution-request length budget; OracleCursorOwner.SetStatement queues replaced IDs. Reuse OracleProcedure/RefCursor/PieceData fixtures, new combination/five-asset standard-OB20 cases; sync README/architecture/ledger.

- [x] Public sync/async stream+OUT fails first, then minimal path.
- [x] Empty/multichunk, same/replaced ID, reexecution, count mismatch, source/chunk/tail ERR/cancel with no OUT publication, REF CURSOR parent/original transaction; budget includes complete extended request.
- [x] All-target Release/full Unit-Consumer-DI/scoped format/both-package consumption/review-fix/docs-diff/local commit. User-deferred real database/OBProxy/deployment unexecuted/unverified.

### M11 Procedure/Parameter Catalogs: Local Implementation (2026-10-20)

Implement architecture M11 procedure/parameter catalogs previously planned for D. D02 procedure execution does not imply discovery. Add Procedures/ProcedureParameters to C05 GetSchema, without DeriveParameters/adapters/command generation/options. Current master/no dependencies; five sync assets/three modern async assets; Standard async boundaries unchanged.

Pinned Java6adc311ba5db8b2f19f98f3e6e4734667fdfdda1 JDBC4DatabaseMetaData.java:2775,2898,4842,4991 and util/DefaultSQLs.java dictionary fields. MySQL INFORMATION_SCHEMA.ROUTINES/PARAMETERS; Oracle ALL_PROCEDURES/ALL_ARGUMENTS. Java procedure SQL actually uses ALL_OBJECTS/ALL_ARGUMENTS and joins ALL_PROCEDURES for parameters; this provider directly lists ALL_PROCEDURES to preserve parameterless/package/overload identity, without claiming identical Java SQL. Do not port LIKE/name folding/mysql.proc fallback/JDBC numbers/invented lengths-nullability.

Shared first seven columns: ROUTINE_CATALOG/ROUTINE_SCHEMA/ROUTINE_NAME/PACKAGE_NAME/OVERLOAD:string, SUBPROGRAM_ID:int, ROUTINE_TYPE:string. Procedures has these only. ProcedureParameters adds PARAMETER_NAME:string, ORDINAL_POSITION:int, PARAMETER_MODE/DATA_TYPE:string, DATA_LENGTH:long(bytes), NUMERIC_PRECISION/NUMERIC_SCALE/DATETIME_PRECISION:int, TYPE_OWNER/TYPE_NAME/TYPE_SUBNAME:string. Unknown=DBNull; no fabricated specific names/CLR-DbType mappings.

Both restriction orders: catalog/schema/routine/type-or-parameter/package, five each. Nonnull values use exact equality/protocol binds; empty/%/_ are literal. MySQL lacks schema/package, Oracle lacks catalog: nonnull restrictions there return fixed-shape empty tables. Append MetaDataCollections rows with restrictions5/5, identity parts4/5; preserve original four order/semantics.

Oracle procedures retain standalone PROCEDURE/FUNCTION and PACKAGE members with nonempty PROCEDURE_NAME; package headers excluded. Member ROUTINE_TYPE unknown, never inferred. Keep PACKAGE_NAME/OVERLOAD/SUBPROGRAM_ID identities, never merge overloads. Parameters require DATA_LEVEL=0/nonNULL DATA_TYPE, excluding old parameterless placeholders/nested attributes. Position0=RETURN; otherwise IN→IN, OUT→OUT, IN/OUT→INOUT, unknown→DBNull. ALL_ARGUMENTS cannot confirm ROUTINE_TYPE, so DBNull. MySQL retains procedures/functions, position 0 RETURN, other PARAMETER_MODE unchanged, dictionary ROUTINE_TYPE. Lengths CHARACTER_OCTET_LENGTH/Oracle DATA_LENGTH. Oracle precision only NUMBER/FLOAT, fractional time only TIMESTAMP; preserve Oracle type-identity fields, MySQL missing ones DBNull. Sort catalog/schema/package/routine/type/subprogram/overload/position. No cross-query dictionary snapshot/automatic complex-parameter reconstruction claim.

Files: OceanBaseConnection.Schema.cs collections/restrictions/shared filtering; new OceanBaseConnection.Schema.Routines.cs shapes/SQL; reuse ValidateSchemaResult/AddSchemaRow, transaction binding/RequiredSession/lease/timeout/cancel/terminal drain. New RoutineSchemaTests.cs/RoutineSchemaConsumerTests.cs; update four-collection count assertions for expanded API; sync README/architecture/ledger.

- [x] Failing-first public GetSchema: two modes/two collections/sync-async fixed shapes/raw values/NULL/exact binds/absent-part no-send/MetaDataCollections list.
- [x] Minimal shared catalogs preserve packages/overloads/returns/byte-length/precision; bad names/fractions/overflow/extra results/trusted ERR/cancel-delay/transactions-busy-readers/five-asset cases, no original-collection regression.
- [x] Five-core/five-DI Release/full Unit-Consumer-DI/scoped format/diff/both-package consumption/independent design-code review/fix-reverify/local commit, no push. Real dictionary fields/permissions/visibility/overloads/parameterless/package/complex-type/version-OBProxy/deployment unexecuted/unverified; fixtures do not establish version support.

### C06 Sequential DbBatch and Data-source Batch APIs

**Deliverables:** `net8.0+` `DbBatch`, factory/B01 data-source batch entry points. Initially execute sequentially, retaining per-command `RecordsAffected`/successful prefix without automatic atomicity. Basic data sources are already B01, not deferred here.

Local steps (existing batch design; implement directly, independent read-only review):

- [x] Failing public DbBatch cases establish order, empty batch, per-command counts, failing command, unexecuted suffix.
- [x] OceanBaseBatch/BatchCommand/BatchCommandCollection/internal reader reuse OceanBaseCommand; snapshot all commands/parameters at start, copying byte[], retain per-command prepare caches. Collection/commands have one batch owner; active configuration/collection mutation prohibited.
- [x] PhysicalSession batch-owner reservation prevents ordinary commands/catalogs/database changes/transaction operations between entries; stale batches cannot enter reopened sessions. Failure/reader close releases reservation; early close does not execute suffix, unread responses destroy original session.
- [x] NextResult starts each command, supporting existing multiple results/types/schema/SequentialAccess/true async. ExecuteScalar returns first row/column of first column-bearing result (null if empty), then drains all suffixes. Timeout applies per command including prepare/read,0 infinite. Cancel covers current/unstarted suffix; no automatic transaction.
- [x] Data-source entry uses platform DbDataSource wrapper for automatic open/close/disposal; platform rejects Prepare/Connection/Transaction; factory.CreateBatchCommand creates entries. Five-asset isolation/three-modern-asset consumers cover API boundaries.
- [x] Full build/Windows Unit-Consumer-DI/format/packages/review-fix, update three docs, commit current branch; live/non-Windows deferred.

- [x] After implementation, test `CanCreateBatch=true`, transaction binding, counts, failure position, async/unknown outcome; no skeleton capability claim.
- [x] Source batches inherit frozen configuration/logging/independent optional-pool/transaction rules and redacted connection strings. No modern APIs on older targets; C01 still rejects pooling.
- [ ] Consumption/integration on .NET 8/9/10 separately.

2026-10-19 local implementation/full Windows tests/review complete; C06 pending verification. Independent samples also pass actual.NET8.0.31/9.0.20/10.0.12 batch consumption with Logging8/9/10. Full integration/non-Windows deferred, so final checkbox stays open. Evidence/fixes: ledger C06 tail record.

### C07 Diagnostics and Performance Baselines

**Deliverables:** local `ActivitySource` tracing, aggregated `Meter` metrics, disabled overhead/performance baselines. B01 already supplies basic `ILogger`; task-specific command/transaction/pool logs follow those tasks. Verify five-target APIs/dependencies. Local tracing does not require OB20 or additional bridges. Default excludes SQL/binds/credentials. No SQL/connection IDs/arbitrary keys as high-cardinality metric labels. Packet capture separately opt-in, with authentication/parameters erased.

Local steps (existing design; direct implementation/independent review):

- [x] Failing public connection/command/reader/transaction fixture cases: source name/parent-Current restoration/no early completion/known ERR/cancel/timeout/unknown outcome/listener errors-reentry. Fixed operation/mode/outcome tags prove no secrets.
- [x] Internal/DriverDiagnostics centralizes source/meter/per-operation enabled objects. CommandExchange reports after lease release/batch-failure cleanup; OceanBaseCommand distinguishes prepare/execute after Start returns. Reuse connection/database/transaction lifecycles; no listener calls under internal locks.
- [x] Standard assets use CPM DiagnosticSource8.0.1; modern use runtime APIs. Five-asset isolated listeners verify identical events/tags/reference versions; daily host net10.
- [x] Repeatable Windows no-listener/sampling-rejected/enabled/prepared-cache/multiple-result/sequential-large-value baselines record timings/allocations/environment. Material regressions enter defect ledger; do not fabricate pool measurements while C01 blocked.
- [x] Full slnx/Windows Unit-Consumer-DI/packages-format/review-fix, synchronize existing three docs, local commit/no push.

- [x] Five-target event/field consistency, no sensitive data, cancel distinguished from unknown outcome.
- [ ] Establish prepared/multiple-result/large-value/high-concurrency pool-rental performance-memory baselines; log material regressions.
- [ ] Collect redacted both-mode direct/OBProxy samples.

2026-10-19 local diagnostics/redaction/Windows prepared-multiresult-sequential baselines complete; C07 pending. Unit1205/Consumer152/DI8, actual.NET8/9/10 samples/package matrix pass. Activity.CurrentChanged review defect fixed with sync/async regressions. Original performance checkbox includes unavailable pooling, stays open. Live both-mode/OBProxy/non-Windows deferred; data/reproduction at ledger tail.

### C08 G3 Production Gate

**Deliverables:** public version/mode/topology/TLS/auth/pooling matrix and measured reset-isolation/fault/exact-value evidence. Combinations without complete reset publish direct connections only.

- [ ] Compile five targets; run .NET 10 Unit/isolated Consumer/both-mode integration/fault suites; record skips/failures/environment blockers.
- [ ] Review pool allowlist/memory-connection leaks/postcancel SQL uncertainty/observability.
- [ ] Close G3 only without P0/P1 defects and with claims matching measurements.

## D. Independent Extension Slices

Each D item has its own switch/version-mode matrix/fallback semantics. Continue independently verifiable local work under current instructions despite missing G4/live environments; defer deployment-dependent integration and never replace live acceptance with fixtures. Start failing fixtures, add public API docs/resource tests, record each live-version/mode gap separately.

| ID | Extension | Required boundaries |
| --- | --- | --- |
| D01 | OB20, checksums, full-link tracing | Negotiation, frame validation, standard-stream fallback, no sensitive tracing |
| D02 | Oracle procedures, OUT/RETURNING | Directions, multiple results, parameter/transaction state on failure |
| D03 | REF CURSOR | Cursor/transaction/connection ownership, early close |
| D04 | LOB locators/streaming | Invalidation points, chunks, cancellation, memory bounds |
| D05 | ARRAY/STRUCT/type dictionary | Nesting, cache invalidation, mode differences |
| D06 | bulk/array binding | Partial success, failure position, per-entry counts |
| D07 | Explicit client-prepare fallback | Safe text encoding; only explicit prepare-unsupported errors permit fallback, never disconnect |
| D08 | Topology discovery/load balancing | Address sources/selection/mode revalidation/no replay |
| D09 | Explicit XA | Session pinning/recovery/real coordinator; `TransactionScope` needs separate design |
| D10 | Oracle NCHAR/national charset | Session charset identification, text/prepared bidirectional codecs/metadata |
| D11 | Oracle INTERVAL | Precision/sign/text-binary bidirectional codecs |
| D12 | MySQL JSON | Text/binary values/NULL/metadata/bidirectional codecs |
| D13 | MySQL BIT | Widths/sign boundaries/metadata/bidirectional codecs |
| D14 | MySQL YEAR | Range/zero semantics/metadata/bidirectional codecs |
| D15 | Additional authentication plugins | Switches/TLS prerequisites/live both-mode evidence |

### D14 Local Scope (2026-10-19)

Reuse `OceanBaseDbType.Year`(9); standard/provider access `ushort`/`DbType.UInt16`. Four-digit YEAR only, zero/1901–2155; reject YEAR(2), two-digit inference, date conversion, invalid precision. Result13: four ASCII digits text/two-byte LE binary; validate mode/metadata even for NULL. Explicit Year validates mode/precision/UInt16 and reuses unsigned SMALLINT parameter encoding, without assuming execute accepts YEAR code. Ordinary UInt16 has no year restriction.

Failing codec/boundary/public loopback→shared results/parameters/schema→five-asset API consumption→all-target Release/full Unit-Consumer-DI→review/fix/docs/local commit. Cover sync/async,text/prepared,default/SequentialAccess,next fields/session reuse. Reference pinned Java BinaryRowProtocol two-byte YEAR/ShortParameter SMALLINT behavior, no source copying. Live YEAR INSERT/SELECT/version/OBProxy remains open; A05 unchanged; tests inject post-auth mode facts only.

### D13 Local Scope (2026-10-19)

Reuse `OceanBaseDbType.Bit`(8), standard/provider `ulong`/`DbType.UInt64`; BIT(1) never implicit bool. Result16; Length=bits,1–64,scale0. Both protocols length-encode big-endian raw bytes, exactly ceil(bits/8), unused high bits zero. Preserve2^63/UInt64.MaxValue without signed/text interpretation. ColumnSize=bits, no decimal NumericPrecision/Scale.

Explicit Bit accepts UInt64 only; Size0=64,1–64 declares width, bounds validated/no truncation, Precision/Scale0 only; Oracle including NULL rejects early. Reuse unsigned BIGINT encoding without assuming execute BIT code. Server column-width/conversion requires live tests; ordinary UInt64 unchanged. Failing widths/sign cases→shared codecs/metadata/mapping→sync-async/text-prepared/default-sequential/NULL/next fields/reuse/five assets→full build/tests/review/local commit. Reference pinned Java RowProtocol.parseBit big-endian behavior without copying. Live BIT INSERT/SELECT/mode-version matrix separate; A05 unchanged.

Length evidence: pinned [OceanBase bit_cell_str](https://github.com/oceanbase/oceanbase/blob/5e8a63d29dae9fde5e962b8fcc7b167d438fc343/deps/oblib/src/rpc/obmysql/ob_mysql_util.cpp) emits `(bit_len + 7) / 8` bytes in TEXT/BINARY; pinned Java RowProtocol.parseBit interprets big-endian. Source inspection is protocol evidence, not live-server verification.

### D12 Local Scope (2026-10-19)

Reuse `OceanBaseDbType.Json`(7), standard/provider `string`/`DbType.String`. MySQL245 is length-encoded UTF-8 JSON text in both protocols, not internal binary JSON. Accept binary collation63 (special wire-text case)/supported UTF-8 collations; reject others/Oracle, validating NULL metadata too. Nonnumeric scale is not numeric precision, including common string marker31. JSON text `null`, empty JSON string `""`, SQL NULL distinct. Return original text without reserialization/double-decimal narrowing/lost whitespace-wide numbers. Schema JSON/Json/string, IsLong=true; Length cannot reliably bound text characters, so ColumnSize/NumericPrecision/Scale unset.

Explicit Json uses UTF-8 VAR_STRING, MySQL/Precision-Scale0, string/null/DBNull only; no object/JsonDocument serialization inference/client parser. Server validates syntax/target conversions. Existing strict UTF-8/length validation pre-send; ordinary string stays ordinary. Reuse GetString/GetChars/GetTextReader/async getters in both modes of access, with shared cancellation/resources.

Failing codecs/metadata/parameters→shared path→sync-async/text-prepared/default-sequential/Unicode-wide-text/NULL-multiresult-next-field/streams-lifecycle/five assets→all-target/full tests/review/local commit. References: pinned Java ColumnType.JSON/string RowProtocol and [pinned OceanBase json_cell_str](https://github.com/oceanbase/oceanbase/blob/5e8a63d29dae9fde5e962b8fcc7b167d438fc343/deps/oblib/src/rpc/obmysql/ob_mysql_util.cpp), printing internal JSON to length-encoded text. No source copying. Live INSERT/SELECT/syntax rejection/version-OBProxy round-trips remain open; A05 unchanged.

### D10 Local Scope and Steps (2026-10-19)

Oracle207/208=NVARCHAR2/NCHAR; standard/provider string, explicit provider parameters DbType.String. Ordinary strings do not become national. Both protocols length-encoded bytes, preserving trailing spaces/Unicode. Use current NCHARACTER_SET_CONNECTION for decoding/binding: UTF8/UTF8MB4/strict BOM-free UTF16BE. Never infer wire encoding from storage charset. National NULL/binary falls back to CHARACTER_SET_CONNECTION for parameters/CHARACTER_SET_RESULTS for results. Results NULL/binary then uses source column collation; unknown rejects without replacement. Size counts CLR UTF-16 units, rejects overflow/no truncation. Precision/Scale0. NULL fields retain identity without decoding; NULL parameters use existing type 6/bitmap. Server national Length already characters; preserve in schema.

Follow zone-state lease/query/pre-send invalidation/per-result snapshots. Reuse charset facts only when all three variables reported; otherwise before Oracle business execution, query `SELECT @@NCHARACTER_SET_CONNECTION, @@CHARACTER_SET_RESULTS, @@CHARACTER_SET_CONNECTION FROM DUAL` under same lease/total budget. Require three string columns/one row/one result. Valid but unsupported encoding rejects only actual national access/binding, not ordinary columns. Invalidate per SQL/result; no guessing in unreported later results. National query preserves prior readonly zone-query result. No options/session mutation/full business-result cache. Validate type/Unicode/Size before leasing, encode with identified charset/check bytes before prepare.

- [x] Failing metadata/parameter regressions, then national codecs/query-snapshot-invalidation/parameters/sequential chars.
- [x] UTF8/UTF16BE/fallback/NULL/bad Unicode/pre-send rejection/both protocols-sync-async-sequential/session changes-errors-cancel/five assets.
- [x] All-target Release/full Unit-Consumer-DI/review/diff/local commit; real NCHAR/NVARCHAR2/version-OBProxy/cross-platform deferred separately.

References: pinned Java AbstractConnectProtocol/OBNStringParameter/RowProtocol; OceanBase5e8a63d29dae9fde5e962b8fcc7b167d438fc343 ObMPStmtExecute::parse_basic_param_value/ObQueryDriver::convert_string_value_charset/ObMySQLResultSet::to_mysql_field. First two use session national encoding for input/results; third divides national Length by storage-charset maximum width to yield characters. Behavior references only, no copied source.

### D11 Local Scope and Steps (2026-10-19)

Immutable `OceanBaseIntervalYearToMonth`/`OceanBaseIntervalDayToSecond`; exact standard/provider values, DbType.Object, enum5/6. Nonnegative magnitudes plus separate sign. Leading precision0–9 (0 requires zero leading component), years/days<=999999999, months0–11,hours/minutes/seconds0–23/59/59,nanos0–999999999, DS scale0–9, no rounding. Default valid zero. Culture-invariant signed ToString retains precision; DS ToTimeSpan converts only exactly within tick precision/range.

Oracle204/205: length-encoded7/14-byte little-endian layouts in both protocols: sign/year/month/leading precision or sign/day/hour/minute/second/nano/leading precision/scale. Field precision matches column scale: YM=leading, DS=leading*10+fraction. Reject non-Oracle including NULL, malformed lengths/components/precision/sign; no guessed string/TimeSpan. Schema exact CLR/provider identity, no numeric precision misuse.

Bind explicit provider types or exact DbType.Object values; NULL bitmap unchanged. Precision0 uses value's leading precision, nonzero1–9 declares it. Unset Scale uses value scale; explicit0 cannot truncate nanos. Precision changes require exact representation. Reuse preencoding/default-sequential/multiresult lifecycles, no dependencies. References: pinned Java INTERVAL parameters/RowProtocols and OceanBase5e8a63d29dae9fde5e962b8fcc7b167d438fc343 ob_mysql_util.cpp/ob_time_convert.cpp/ob_timezone_info.h, no copying.

- [x] Failing existing-entry cases for7/14-byte shapes/type identity/NULL/both modes/precision.
- [x] Exact values/shared codec/parameter/ResultColumn-Schema; signed maxima/all precision/range/nano rejection.
- [x] Sync-async×text-prepared×default-SequentialAccess, reuse/pre-send rejection/NULL-next-column-multiresult/five assets.
- [x] All-target Release/full Unit-Consumer-DI/review/diff/local commit; real INTERVAL round-trips/version-OBProxy deferred.

## Java Module-to-task Index

Check that [Java M01-M16 modules](obconnector-j-architecture-outline.md) have explicit .NET destinations. D entries are intentional deferrals, not overlooked first-release gaps.

| Java module | Main tasks |
| --- | --- |
| M01 Entry/unified API | B01, C06 |
| M02 URLs/options/addresses | A02, C02, D08 |
| M03 Network/TLS/authentication | A04, A05, D15 |
| M04 Handshake/mode/capabilities | A05, D01 |
| M05 Sessions/local transactions | A06, B07, C01, C05 |
| M06 SQL/preparation | B02-B04, D02, D07 |
| M07 Parameters/batches | B03-B04, C06, D06; independent M07 long-upload slice |
| M08 Results/cursors | B02, B06, C04, D03 |
| M09 Types/charsets/time | B05, C03, D10-D14 |
| M10 LOB/complex types | C04, D04-D05 |
| M11 Metadata | B06, C05, M11 procedure/parameter catalogs, D05 dictionary |
| M12 HA/discovery | C02, D08 |
| M13 Built-in pool | C01 |
| M14 XA | D09 |
| M15 Diagnostics/OB20 | B01, B02, B07, C01, C07, D01 |
| M16 Build/ecosystem | A01, B09, R01-R02 |

## Release and Handoff

### R01 Packages and Public Contracts

- [ ] Finalize core/integration NuGet IDs/license/third-party attribution. Shared root Version, pack/release together; integration nuspec exact core-version interval derived from root. Five assets per package/required docs complete.
- [x] Empty-project consumption of Standard2.0/2.1/.NET 8/9/10 verifies factory/modern API visibility/type identity/redaction.
- [x] Generate/inspect packages; publish unsupported features/matrix without claiming planned D support.

Local order (2026-10-19, existing design, no publishing):

1. eng/Verify-Packages.ps1/independent consumers first fail on missing README/attribution in existing nupkg. Verify nuspec/five-three assets/exact core dependency/per-TFM Microsoft versions.
2. src/Directory.Build.props centralizes package descriptions/repository/docs; explicitly retain existing NuGet IDs. Root Version/CPM unchanged. Add root README/third-party dependency-reference notice. Maintainer chooses license; no fabricated SPDX/closed license gate.
3. One slnx pack creates both packages. Independent directories/NuGet caches/local-source mapping consume only generated PackageReferences; check restored assets/SHA512. Compile both Standard/three modern samples. Standard uses isolated.NET10; modern actual.NET8/9/10 checks factory/types/modern entry/DI/credentials. Daily projects remain net10.
4. Reject missing docs/wrong DI core interval; integrate unified pack/verifier in Windows CI. Build/full local tests/read-only review/fixes/three-doc sync/commit; C08/R02 live gates stay open.

2026-10-19 steps implemented/verified: old README-less packages fail; new five-asset actual restore/consumption passes; corrupted docs/widened core interval reject. Windows Unit1205/Consumer152/DI8/five-three build/review pass; PowerShell minimum fixed to 7.1+. First checkbox includes undecided project license/origin/package-trademark review, remains open. R01 in progress; no publishing/C08-R02 closure.

R01 follow-up (2026-10-19): after D09, extend existing five-asset package samples for XA identity/copy boundaries/nullable error codes/closed-connection resource rejection. Prior R02 packages fail compilation for missing XA; newly packed packages restore by SHA512/identity in independent caches, compile five targets, consume on actual.NET8/9/10/isolated Standard hosts. Read-only review then test commit; no dependencies/product changes. License/release/live/other-machine acceptance remain open; ledger details.

### R02 Complete Candidate Verification

- [ ] Rerun all G1-G3 required suites/delivery matrix; retain commands/versions/log links/skip reasons.
- [ ] Mark each mode/version/OBProxy/TLS/auth combination verified/unverified/unsupported; without real tests never verified.
- [ ] Complete release notes/fault-unknown-outcome guide/differences/rollback plan; publish only after final code/docs review.

Local delivery preparation (2026-10-19, not candidate acceptance):

1. Check command/reader/transaction/multiaddress/auth code and regressions for errors/early close/confirmed commit/cleanup failure. Merge operations/upgrade-downgrade/logging/auth-TLS guidance into packaged root README, no separate design document.
2. Distinguish unverified/unsupported in matrix; invent no minimum OceanBase/OBProxy version/verified upgrade source; retain public MySQL/pooling/live gaps.
3. Windows build/tests/unified pack/actual five-asset package consumption verify README; independently compare guide with code/tests, fix/sync three docs/commit. Without G1-G3/R01-license clearance, keep final release unchecked and create no release/push.

2026-10-19 local material/review complete: correct field-stream disposal/reader completion/trusted COMMIT ERR session-survival boundaries and exception public contract against source. Verification exposed synchronous TCP shared-handle disposal defect, fixed d8d7072; stress port cleanup/primary-exception preservation also resolved. Final five-three build0 warnings/errors; Windows Unit1206/Consumer152/DI8/actual five-asset packages pass; failures/final reports retained in ledger. R02 live/final release unchecked; license/production gates open; no publish/push.

R02 older modern Windows runtimes (2026-10-19, five-target contract):

1. Daily tests/slnx remain net10.0. Temporarily pass both `-p:TargetFrameworks=net8.0 -p:TargetFramework=net8.0` (likewise9) to build/run complete Unit/DI on actual8/9, not run net10 binaries on old runtimes. Do not commit generated restore/output; restore default five-three/net10 afterward.
2. Test-host compatibility only: net8 PFX import API; net9+ X509CertificateLoader. Async Prepare loads Standard2.1/current-runtime modern asset instead of net10 on old hosts. Retain initial compile/wrong-load failures before corrected async proof. Full net8 found MeterListener8.0.31 retaining subscriptions on Dispose without MeasurementsCompleted; add empty completion callback retaining all diagnostic/allocation assertions. README documents application listener ownership/cleanup.
3. Full per-runtime protocol/cancel-timeout/types/transactions/cursors/batches/diagnostics/DI then default net10; record actual runtime/warnings/pass-fail-skip. Five-asset Consumer still net10; separate8/9 package/partial-loopback evidence, never a full live/old-runtime matrix claim.
4. Independently review temporary-build dependency majors/boundaries, sync three docs/commit. Live/non-Windows/.NETFramework4.8/license/G0-G1-G3 open; D items then remained unstarted subject to prerequisites.

2026-10-19 results: actual Windows.NET8.0.31/9.0.20 each Unit1206/DI8 pass; restored.NET10.0.12 Unit1206/Consumer152/DI8 pass; all zero fail/skip. Five-three Release 0 warnings/errors; repacked isolated five-asset consumption passes;8/9 graphs retain corresponding majors. Initial net8 nine diagnostic failures/certificate-load issues remain red-green history, not passes. Full evidence/listener rationale/open gates: ledger tail.

R02 Windows modern-runtime recheck after D extensions (2026-10-20):

Temporary MSBuild properties run current complete Unit/DI on.NET8/9. Daily projects net10.0; both packages five targets, global.json stays deleted. Oracle XA-recovery fixture explicitly uses Enumerable.Reverse to avoid old-target void Span overload. Malformed complex-dictionary-column fixture supplies first row for reader prefetch, retains final EOF pending and reads disconnect only, avoiding writes racing destruction of unread sessions. Preserve original exception/Broken/disconnect assertions; no protocol-semantic change. Restore default/all-target Release/net10 Unit-Consumer-DI after old runtimes; record actual runtimes/failures/finals, independent review/commit. No closure of live/other-machine/KDC/license/release gates.

Results: actual.NET8.0.31/9.0.20 each Unit2912/DI14 pass. Restored five-core/five-DI Release 0 warnings/errors; .NET 10.0.12 Unit2912/Consumer1163/DI14 pass, zero fail/skip. Retain net8 compile/net9 fixture-race/review prefetch omission/final evidence in ledger; no deleted cases. No repeat package/Framework acceptance this round. R02 in progress/no publish-push.

R02 Windows Framework package consumption (2026-10-19, local evidence only, no formal support decision):

1. Reuse five-asset samples; explicit Verify-Packages.ps1 `-VerifyNetFramework`, Windows only, generates net48 executable host referencing compiled Standard2.0 consumer/current core package. Daily tests net10; no daily Framework project/product target.
2. Installed4.8 reference assemblies/automatic host binding redirects, no product dependencies. Independent graph/package hash confirms current netstandard2.0 asset. Run desktop CLR and report actual framework description/registry Release; target compilation is not original 4.8 runtime proof.
3. Retain factory/types/modern boundaries/logging-diagnostics8/precancel-redaction; old script rejects new option first, then full package/default slnx/appropriate regressions/review/docs/commit.
4. Installed4.8.1 Release 533509 proves net48-target basic package consumption on 4.8.1 only; no sync-async SQL/TLS/full API/live/original 4.8 coverage. Formal§9/G0 Framework decision/live/non-Windows open.

Local verification passed. Rebuilding parent after generating Framework child exposed source glob inclusion; exclude framework-host/**/*.cs and retain failing-then-passing rebuild evidence. Final build/package/review/evidence at ledger tail.

## D15 SHA-2 Authentication: Local Implementation (2026-10-19)

First deliver `caching_sha2_password`/`sha256_password` TLS paths and caching fast auth; D15 stays open. Reuse options/TLS/PacketChannel sequences/open budget/redaction/A05 mode verification, no dependencies. RSA configuration/retrieval, other plugins, live both-mode acceptance remain subsequent. No automatic unverified-key retrieval/TLS weakening.

1. Minimal internal AuthenticationConversation in HandshakeFlow tracks plugin/one switch/final-result state. Sync/async share decisions, not I/O wrappers. Caching token=`SHA256(password) XOR SHA256(SHA256(SHA256(password)) || seed)`,20-byte challenge; empty password→empty token. SHA plugins reject NUL/invalid Unicode without credential disclosure.
2. `01 03`: no response, await final OK/ERR. `01 04`: strict UTF8 password+NUL only after successful VerifyFull TLS. Same requirement for sha256 initial/switch. Unknown plugin/malformed-repeated AuthMoreData/repeated switch/challenge in final state fail explicitly. Native/clear contracts and fail-close remain.
3. Public handshake loopback first fails, then sync-async/initial-switch/fixed vectors/final OK-ERR/order-truncation/weak TLS/cancel-timeout/redaction. State tests prove VerifyFull encoding decisions only. No trust-root installation/confirmation/validation bypass masquerading as trusted TLS. Five-asset DbConnection consumption separates state evidence from successful TLS identity chains.
4. Independent review/fixes/five-core-three-DI Release/full Unit-Consumer-DI; sync README/architecture/ledger/local commit, no push. Live/other-machine/private-CA success deferred; A05/C01/remaining D15 open.

Protocol reference: pinned Java `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1` CachingSha2PasswordPlugin/Sha256PasswordPlugin, no implementation copying. Unlike Java useSsl, full-password send requires stricter VerifyFull.

SHA-2 local implementation/review complete: five-core/three-DI Release 0 warnings/errors; Unit1514(new SHA-2 72), Consumer292(new 40), DI8 all pass/no skips. Red-green/fixture corrections in ledger. D15 in progress, remaining implementation/live gates open; no publish/push.

### D15 RSA Follow-up (2026-10-19)

1. Canonical `Server RSA Public Key`(empty; PEM SubjectPublicKeyInfo PUBLIC KEY text), `Allow Public Key Retrieval`(false). Caller supplies PEM; no file I/O/TFM-specific file APIs. Frozen options/endpoint copies/serialization-redaction/builder covered. Default logs/redacted strings omit PEM; full builder round-trips. Invalid pinned key never falls back to retrieval.
2. NonVerifyFull SHA-2 full auth uses pinned RSA OAEP-SHA1 on strict UTF8 password+NUL XOR repeated20-byte seed. Without pinned key require explicit Allow: sha256 initial/switch sends01, caching-full02; await01-prefixed PEM, send ciphertext, await final OK/ERR. Initial sha256 retrieval may still receive one account plugin switch; after switch/full stage require key, never accept keyless OK. Empty password sends no-password marker, no key/encryption. Verified TLS keeps existing nonempty-password priority; RSA does not enable clear plugin.
3. Minimal RsaPassword with BCL RSA.ImportParameters/Encrypt supports Standard2.0. ByteCursor strict SPKI DER: RSA OID/positive integers/no trailing data/PEM<=16 KiB/modulus2048–8192/exponent positive odd<=64 bits. Reject private/weak/noncanonical keys/oversize password/missing-reordered-repeated key without echoing secrets. README explains unauthenticated retrieval/MITM/default-off; Disabled TLS leaves business SQL unencrypted.
4. Failing public loopback plus independent private-key decryption, then plugins/initial-switch/pinned-retrieved/sync-async/empty-Unicode-boundary/malicious DER-PEM/cancel-timeout-final ERR/five assets. Review/full build-tests/docs/local commit; live/other-machine/VerifyFull success deferred, other plugins remain, no D15/project closure.

RSA locally complete/reviewed: fix account switch after initial sha256 retrieval, add empty-password marker. MySQL8 sha256 accepts empty packet/single NUL; never mistake RSA ciphertext NUL for no-password protocol. Five-three Release 0 warnings/errors; Unit1596(new 82)/Consumer332(new 40)/DI8 pass. Ledger retains red-green/sandbox TLS-permission failures/final evidence. Other D15/live gates open.

### D15 Java-registered Plugins: Local Follow-up (2026-10-19)

Scope revision per user: use pinned Java AuthenticationPlugin SPI registry without asking user to select plugins. Beyond native/clear/SHA256/cachingSHA2, implement mysql_old_password, dialog/PAM, client_ed25519, auth_gssapi_client separately. Check credential sources/password encoding/interaction/TLS per plugin; registry is not live evidence.

Next mysql_old_password follows Java OldPasswordPlugin3.23 response, first8 ASCII challenge bytes, UTF16 password-character hashing; initial/named switch and single FE legacy switch (original challenge). Empty→empty response; nonempty8 bytes+NUL. No Java source copying. Weak hash requires existing full-password VerifyFull boundary, no downgrade option. Handshake still protocol41/secure connection, no pre41 client expansion. Fixed vectors/state-encoding/malicious challenges/repeated switches/weak TLS/five-asset consumption, full build-tests/review/docs/commit. Trusted private-CA/live/other-machine deferred; three other plugins follow, D15 in progress.

### D15 Ed25519 Submodule (2026-10-19)

Pinned Java Ed25519PasswordPlugin client_ed25519: strict32-byte binary challenge, SHA512-expanded UTF8 password,64-byte Ed25519 signature; empty→empty response. BouncyCastle.Cryptography2.7.0 public ExpandedKey API(MIT/five targets), no double-hashing32-byte seed API/manual curve/Java port. Version only CPM; core reference/third-party notice synchronized. Initial/named switch; trailing challenge zeros are data. Existing single-switch/final OK-ERR/mode/budget/TLS retained. Independent Java vectors/trailing-zero/malformed tests expose gap, then sync-async public/five assets/errors-interruption-boundaries/actual dependency packages. Full build-tests/review/local commit. PAM/GSSAPI/live deployment remain.

### D15 dialog/PAM: Local Implementation (2026-10-19)

Pinned SendPamAuthPacket: initial/named switch sends primary Password+NUL; each later nonOK/ERR/FE packet consumes Password2/Password3 sequentially. No prompt-based password inference/callback. RequireVerifiedTls each round; strict UTF8/noNUL-invalid Unicode; one switch/final state/total budget/redaction. Empty→NUL; missing next credential fails; configured count bounds rounds.

- [x] Failing tests/OceanBaseConnector.UnitTests/Session/HandshakeFlowTests.Pam.cs for exact rounds/gaps/empty-Unicode-malicious/TLS/switch-final; PamConfigurationTests.cs covers case/integer2..Int32.MaxValue/no leading-zero-overflow/full round-trip/redaction/frozen-ForEndpoint.
- [x] Builder indexer accepts canonical dynamic PasswordN, no many typed properties. ToSanitizedConnectionString retains extras only with includeSecrets=true; full builder round-trip. Options copy readonly dictionary/endpoint copies without maximum-suffix-sized arrays. Unknown keys still reject.
- [x] AuthenticationConversation adds primary/sequential dialog through strict password helper; ignore prompts as Java does, no sensitive exceptions; FE uses existing switch. RED→GREEN, TLS unchanged.
- [x] Public PAM Required/Disabled rejection and five-asset config/secrets/rejection; trusted-TLS success is state/encoding evidence only, no roots. Review/fix/five-three Release/full tests/format-diff/README-architecture-ledger/local commit. GSSAPI/live/other-machine subsequent.

### D15 GSSAPI: Local Implementation (2026-10-19)

Pinned SendGssApiAuthPacket/StandardGssapiAuthentication uses current ticket/Kerberos/mutual authentication; raw tokens through PacketChannel, no NegotiateStream framing/Java port. Service Principal Name overrides server SPN. Strict bounded UTF8 double NUL(SPN/mechanism), preserving switch-data trailing zeros. Empty mechanism→Kerberos; initially Kerberos only, reject unknown/NTLM, never pass arbitrary server package names to OS. VerifyFull before complete auth; require system Completed/IsAuthenticated/IsMutuallyAuthenticated/actual Package=Kerberos before database final OK.

Ruling: net8/net9/net10 use NegotiateAuthentication directly; Standard explicitly rejects plugin because reference APIs/old-host requirement/live credentials absent. No reflection/dependency/custom SSPI. Thus Standard lacks GSSAPI even on modern hosts. Java Windows-native NTLM/arbitrary mechanisms outside scope; no full Java-interoperability claim. Synchronous native negotiation may await KDC and cannot be forcibly cancelled; budget governs driver-controlled/network steps. Real credentials/KDC/other-machine deferred.

- [x] Failing GssapiChallengeTests double NUL/UTF8/zeros/malformed/length/override; GssapiConfigurationTests canonical/frozen/endpoint. SPN<=1024 chars, no control/invalid Unicode, no realm rewrite/reverse DNS.
- [x] GssapiChallenge/GssapiAuthentication/system adapter with isolated native-token interface. Deterministic doubles prove state/raw token/final OK/errors-construction-switch-disposal only, not Kerberos interoperability.
- [x] Conversation owns/disposes context, HandshakeFlow sync/async using. Existing initial-switch/one-switch/final ERR/cancel-timeout paths; check cancellation after OS calls; never concurrently Dispose executing native calls.
- [x] Weak-TLS/five-asset public config-rejection/local OS API shape without tickets/KDC. Review/full tests/five-three Release/format-diff/docs/local commit. Live/KDC-cache/trusted TLS/native blocking/Standard support open.

### B01/R01 DI Standard Correction (2026-10-20)

User identified missing Standard DI targets. Modern-data-source-only wrapper did not define DI's platform needs: existing connection/DI abstractions work on Standard. Align DI/core to five targets; modern DataSource boundary retained; AddOceanBaseConnection on all targets. No fake DbDataSource/nonfunctional assets.

Ruling: Transient connections, fresh closed instance per resolution, concrete registration plus nonconflicting DbConnection forwarding factory; container/scope disposal, optional host ILoggerFactory. Normalize config/check duplicate concrete services before descriptors, preserve existing generic connection services. Direct-connection mutability/credential visibility unchanged; no lifetime options/keyed connections; source API never implicitly registers connections. Repeated scoped resolutions are independent, not shared request connections/pooling.

- [x] Failing registration gap, lazy creation/independent resolutions/scoped disposal/external logger lifetime/existing generic service/no partial registration on duplicate-invalid config.
- [x] Core/DI five-target Release/full DI/dependency tests; verifier checks five assets each/ten groups/exact same-version core. Standard samples explicitly use8.x test container.
- [x] Actual packages on.NET8/9/10/isolated Standard/optional localFramework4.8.1 basic DI configuration-disposal; review/public docs-ledger/commit. Live/other-machine/full old-runtime deferred; global.json remains deleted.

### D15 PAM/GSSAPI Package Delivery Recheck (2026-10-20)

Reuse R01/R02 actual-package verifier, no product-auth change. PackageConsumer compiles/runs five-asset ServicePrincipalName property/key, Password2/sparse maximum suffix, full-string round-trip/all-password redaction. Precancellation never connects; modern DI source omits extra passwords/retains SPN. Historical Ed25519 package must fail new contract, then repack both/isolated SHA512 restore/run actual.NET8/9/10 and isolated Standard. No database/KDC/roots/publishing. Scoped build/assertions/review/docs/diff/local commit; no repeat full product regression passed minutes earlier, since product unchanged.

- [x] New package contract/historical negative check.
- [x] Actual five-asset restore/run/both packages/review/docs/commit.

### D01 OB20 Packets and Checksums (2026-10-19)

Internal packets first; D01 in progress until negotiation/streaming/full trace. Pinned Java `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1` OceanBaseProtocolV20/Ob20PacketInputStream-OutputStream/OceanBaseCRC16-CRC32C protocol only, no implementation/tables copied.

1. Failing fixed packets/checksums, independently checked with pinned Java checker, not same-codec round-trip alone.7-byte compression wrapper/24-byteOB20/4-byte tail, little-endian. CRC16/ARC first29 header bytes; raw CRC32C starts0/no final XOR; zero disables corresponding checksum.
2. Internal Ob20Checksum/Ob20Packet reuse ByteCursor/single-frame codec. ParseHeader checks identities/request-sequence/length-bound before body allocation. Nonzero uncompressed length rejects, no guessed decompression.24-bit limit covers payload+extra, no truncation/trailing bytes. Validate4-byte extra length; never feed extra to MySQL parser. Preserve opaque legacy/new extra, flags/reserved/outer sequence; trace later, unknown flags not invented.
3. Fixed vectors/incremental CRC/length-request-sequence bounds/tamper-truncation/disabled checksums/extra-unknown flags; payload/extra views avoid second copy. No public/network changes, OB20 enablement/live claim.
4. Five-three Release/full tests/review/diff/docs/local commit. Negotiation/PacketChannel streaming follow; live/other-machine deferred.

Local evidence: independently executed original Java checksum classes confirm CRC16 bb3d/rawCRC32C58e3fa20 for123456789 and full frame.40 OB20 cases; five-three Release 0 warnings/errors; Unit1636/Consumer332/DI8 pass/no skips; review no required fixes. No public integration/new Consumer cases yet; existing Consumer regression is not OB20 networking evidence. Commands/reports at ledger tail; D01 in progress.

### D01 OB20 Streaming and Negotiation Follow-up (2026-10-19)

1. Ob20Stream wraps standard/TLS stream, fixed64 KiB writes/8 KiB extra-skip. Reads demand-sized, no full-body buffer. Validate header/incremental CRC; consume/check tail before returning final body segment. Extra length-check/chunk-skip. No session-sync/trace request; proxy reroute rejects/no replay. Max million frames/request prevents empty loops.
2. Each PacketChannel logical request gets new 24-bit request ID/OB20 sequence0. Response continues after sent sequence with byte wrap; MySQL fragmentation stays existing. Verify last/no inner bytes before command success/prepare metadata/internal probes/cursor transitions. COM_STMT_CLOSE completes without response. I/O/cancel/checksum failure destroys session/no retry.
3. `Use OB20`/UseOb20 false, frozen/endpoint/serialization. Only when requested, negotiated connection attributes include `__proxy_capability_flag=256`. After auth/mode, wrap only if attribute capability and server ob_capability_flag OB_PROTOCOL_V2 confirmed; otherwise standard. Authentication stays standard, TLS/A05/C01 unchanged.
4. Failing streaming/negotiation→sync-async short-read/split-write/crossframes/extra/CRC-last faults/cancel-close-timeout/no-response-close/five assets/fallback→all-target/full tests/review/docs/local commit. Full trace/live matrix subsequent.

Local results: five-three Release 0 warnings/errors; Unit1662(OB20 total 66,new 26)/Consumer394(new 62)/DI8 pass/no skips. Review found missing _flag suffix; pinned Java confirmed,20 reproduced failures then fix; re-review clear. Public tests include reuse/state probe after trusted ERR,three-page cursor,second-page CRC/missing-last,cancel waiting fetch. No OB20-TLS loopback/partial-body-cancel coverage at that time; do not claim either. Evidence ledger; trace/live/project gates open.

### D01 OB20 over TLS and Partial Responses (2026-10-20)

Reuse Consumer OB20 scripts/self-signed fixtures. Five assets sync/async Required TLS: negotiation/standard fallback when unrequested-unconfirmed/text/prepared/no-response close. SequentialAccess actually reads128 bytes before bad CRC/cancel/total-timeout/close; incomplete session destroyed/no replay. Only helpers gain TLS upgrade/preserved handshake sequence; no config/trust callback/system roots. Required is not VerifyFull identity proof. New/full tests/review-fix/evidence/commit; live OceanBase-OBProxy/trusted CA/other-machine deferred.

Completed:80 new Consumer; full Consumer1243/Unit2912/DI14 pass,zero fail-skip; five-core/five-DI Release 0 warnings/errors, review no blockers. Initial wrong CanRead assumption corrected to existing contract/failure retained. Ten valid-CRC negative checks rejected as intended; final malformed CRC full report covers all additions. Five assets on isolated net10, not old-runtime/trusted TLS acceptance. Ledger evidence; D01 live open.

### D01 Full-link Trace over TLS (2026-10-20)

Reuse Ob20TraceConsumerTests assertions/scripts; preserve70 plaintext cases/add70 Required TLS: five-asset sync-async modern/legacy/disabled/missing-cap/no-listener50; current-target prepare-execute/cursor/close/transaction4; badextra/64 KiB/CRC/partial-cancel16. Validate32-byteCLIENT_SSL/encryption, upgrade same PacketChannel/preserve sequence. No API/dependency/roots. First fail without upgrade, then targeted/full/review/evidence/local commit. net10 five-asset consumption is not five-runtime acceptance. Required proves no VerifyFull/trusted CA/live sampling-SHOWTRACE; TLS LOB/XA separate gaps then.

Results: five-core/five-DI/tests Release 0 warnings/errors; Unit2912/Consumer1313/DI14 all pass,zero fail-skip; targeted 140 plaintext-TLS pass. Name comparison with previous full Consumer:+70/-0,all 70 pass. Independent read-only review no blockers; red-green/format/evidence ledger. No live/overall closure.

### D01 Full-link Tracing: Local Implementation (2026-10-19)

Five targets/existing ActivitySource/single-lease OB20, no dependency/log system. Pinned FullLinkTrace/OceanBaseProtocolV20/ObObj/OceanBaseSerialize behavior only, no port.

1. `Enable Full Link Trace`/EnableFullLinkTrace false, requires UseOB20. Frozen/endpoint/serialization. Request FULL_LINK_TRACE/NEW_EXTRA_INFO/SHOW_TRACE; effective only with serverOB20+FULL_LINK_TRACE, otherwise ordinaryOB20. Both legacy ObObj varchar key-values/new-extra TLV; new format not required.
2. Ob20TraceContext owns formats/nested TLV/server sampling-record policies/spans/driver-showtrace logs. ByteCursor nested bounds; trace extra<=64 KiB/frame, apply only after tail CRC. Disabled path chunk-skips. Reject malformed known-field lengths/duplicates/values-policies, skip unknown TLV; validate legacy terminators/bounded varints. No unbounded allocation/logging from server text.
3. First request frame carries extra/CRC covers it; fragments/request-last/no-response unchanged. CommandExchange execute/prepare/probe/cursor shares local Activity trace ID, independent128-bit span/request; no listeners→internal context; transactions pass outer context. Driver records fixed module/random IDs/microsecond timestamps only, bounded one pending record sent next request. No SQL/binds/credentials/endpoints/arbitrary Activity tags/baggage/additional SQL/replay.
4. Failing TLV-legacy-UUID-wire/control-context→public loopback/sync-async/five assets/sampling0-1/disabled-missingcap/faults/CRC-size/prepare-cursor-transactions/cleanup→review-fix/all-target/full tests/docs/local commit. Live sampling-SHOWTRACE/version-proxy/other-machine deferred, no production gate closure.

Local completed: five-three Release 0 warnings/errors; Unit1689(newtrace27)/Consumer464(new 70)/DI8 pass/no skips. Fix legacy casing/bounded nonminimal varints/unknown duplicate TLV/signed level; ledger red-green. Completion records sent only next request; close never flushes final. Query time validated only, no metric/AppInfo API. D01 pending; OB20-TLS/live sampling-SHOWTRACE/version matrix then open; no unrelated-module closure.

### D02 Oracle PrepareExecute Packets (2026-10-19)

Protocol prerequisite for procedure/OUT/RETURNING before public execution/output lifecycle; CommandType/ParameterDirection support unchanged here. Pinned ComStmtPrepareExecute.send-read/JDBC4ServerPreparedStatement.calculateCheckSum behavior only, no copying/dependency.

1. OraclePrepareExecutePacket:0xA1/statement ID/noncursor-iteration1/length-encodedUTF8SQL/32-bit parameter count/existing PreparedParameters/executeMode0-closeCount0/rawSQLCRC32C/reserved0. Preallocation64 MiB bound, count<=UInt16(response range), minimum encoded block/zero-count consistency.
2.17-byte extended header:OK/ID/columns/parameters/filler/warnings/extendFlags/hasResultSet. Reject truncation/trailing/bad marker-filler-bool/columns>4096. Preserve unknown flags for execution policy; header OK is not terminal SQL; no metadata arrays here.
3. Failing independent bytes/SQL-length-Unicode-CRC/parameter blocks/all-header truncations/malicious bounds; pinned Java original CRC independently generates fixture. Full build-tests/review/docs/local commit. Later metadata/results-tail OK/atomic OUT/transaction-resource cleanup mandatory; not D02 complete/live.

Local: five-three Release 0 warnings/errors; Unit1710(new 21)/Consumer464/DI8 pass/no skips. Review confirms layout/CRC, no required fix. Fixture manually constructed with original JavaCRC, not execution of Java send method. No public API/new Consumer yet; response/output/lifecycle/live/other-machine remain.

### D02 Extended Response Streaming and Terminal Outcome (2026-10-19)

Before public OUT, stream PrepareExecute through QueryResult, reusing column/binary-row/SequentialAccess/multiple-result paths; optional internal Oracle parameter-count/readonly header, default unchanged. Check Oracle/binary,17-byte header/count/cumulative4 MiB metadata, parameter definitions/optional EOF, then header-declared columns/rows without reading ordinary count packet. No-result→final OK-ERR; with-result row terminator keeps lease until tail OK-ERR. TailOK updates counts/session/checksOB20/completes; MoreResults continues ordinary results. Sync/async/sequential advance correct I/O, preserving pending state/no replay on cancel/truncation/error.

Failing TCP cases: both EOF forms/results-none/default-sequential/sync-async/count-metadata-header-tail faults/cancel-timeout awaiting tail/trustedtail ERR-reuse/multiple results/state. Public procedure/OUT/RETURNING still rejects/no output publication. Then public API/version-option/atomic OUT. Full validation/review/docs/commit; live/other-machine deferred.

Results:69 targeted(oldpacket21,newresponse48); five-three Release 0 warnings/errors; Unit1758/Consumer464/DI8 pass/no skips; review layout/lease/read paths/errors/snapshots clear. Initial cancellation test wrongly awaited EndOfStreamException; accept precise protocol close/TCP reset. Early-close fixture barrier waits until row terminator sent, avoiding cleanup-write race. No product exception/transport change. Public/output lifecycle remains; D02 open.

### D02 Public Procedures and Scalar Outputs (2026-10-19)

Default-off Use Oracle Prepare Execute option; authenticated Oracle only. First execution queries `select ob_version() from dual` under same lease/budget, strict version/cache per session, gate2.2.76. Never use compatible handshake version/fallback after fault. StoredProcedure accepts safe qualified procedure/function names and generates PL/SQL. Text retains lexer, uses extension only when explicitly enabled with output directions. InputOutput sends original snapshot; Output/ReturnValue NULL. Reject repeated output positions/multiple ReturnValue pre-send.

Hide internal PL OUT/RETURNING rows from public reader; positional binary decoding, scalar at most one row,zero→DBNull,multiple rejects(D06 arrays later). Stage all outputs, publish together after full response/OB20 boundary before lease/log callback. TailERR/later-result faults/cancel-timeout/early close retain old values. Returned ID enters existing delayed-close queue, idle cleanup flushes. Cover sync-async/SequentialAccess/transactions/later results/default rejection/version probe/faults; full validation/review/docs/local commit. No live/other-machine; cursor/LOB/complex outputs separate.

Local: five-three Release 0 warnings/errors; Unit1840/Consumer494/DI8 pass,no skips. Review found OUT publishing after final OK raced cancellation/close;four failing-first cases fixed, rereview clear.30 new five-asset public cases cover standard/OB20/trailing-body boundaries. Ledger evidence; D02 in progress; complex/multirow/independent Prepare-FetchSize/live not claimed.

### D02–D05/D09 Oracle Extensions over TLS (2026-10-20)

Reuse seven Consumer scripts/assertions, preserve plaintext identities, add Required TLS: procedureOUT30,REFCURSOR30,out-of-row BLOB-CLOB60,LOBWRITE-TRIM60,temporary LOB create-write-release80,OracleXAphase-recovery-errors100,complex dictionary-construction20=380. Five assets/sync-async/standard-OB20. Existing trailing-body/stage error-disconnect/ownership/publication boundaries unchanged. Not all LOB cancellations/full complex round-trip/real coordinator acceptance.

Shared optional-TLS Handshake: per-case self-signed certificate/SslStream,standard greeting-SSLRequest,upgrade same PacketChannel/preserve sequence; all extensions encrypted. Test transport only, no config/API/dependency/roots. Targeted760/full tests/five-five-target Release/case-name comparison/review-fix/evidence/commit. net10 isolated assets; Required not VerifyFull. LiveOceanBase-OBProxy/KDC-coordinator/other-machine deferred.

Results: five-core/five-DI and tests Release 0 warnings/errors; targeted 760/fullUnit2912-Consumer1693-DI14 all pass,zero fail-skip. Compared Consumer1313:+380/-0; all additions pass/group counts match. Review no blockers; ledger evidence/boundaries. No live/trusted TLS/other-machine/MySQLXA-TLS/overall closure.

### D03 REF CURSOR: Local Implementation (2026-10-19)

Existing OraclePrepareExecute/2.2.76; append OceanBaseDbType.RefCursor/single-use OceanBaseRefCursor output. PureOUT/ReturnValue NULL; reject input/input-output. Oracle163 binary=LE32 cursor ID, never ordinary integer/text. Stage/publish with scalar outputs after complete boundary; NULL=DBNull.

1. Failing real TCP outputs/fixed ID/COM_STMT_FETCH-CLOSE/both EOF forms/sync-async/sequential/pages. Reuse paging/metadata consistency; first fetch gets definitions then rows without duplicate fetch, never full-result buffering.
2. GetDataReader/GetDataReaderAsync bind original session/transaction generation/command timeout. Default128/page,positive fetchSize/default-SequentialAccess-CloseConnection. One reader/object; exclusivity unchanged. Lease failure retriable, begun read failure never replays. Ended/new transaction/reopen cannot move stale cursors into new contexts.
3. Keep execution parent statement until all output cursors released. Read completion/Dispose/failure releases once via delayed queue; idle Dispose flushes. Early unread close destroys session. Unopened output objects require Dispose; connection close releases all server resources. Failed outputs leak no staged cursors; scalar lifecycle regresses.
4. Multiple/empty-NULL/error-malformed-cancel/transaction-connection-parent cleanup/five assets; review/all-target/full tests/docs/local commit. Live/other-machine deferred, fixtures not server proof.

Local complete/review fixes: five-three Release 0 warnings/errors; Unit1876(new 36)/Consumer524(new 30)/DI8 pass,no skips. Four terminal-cancel-close race cases red→green plus four existing C04 terminal cases pass; ledger details. Pending verification: live/TLS extensions/other-machine/new package-old-runtime matrix not run then. Input/input-output/ordinary-result cursor fields unsupported. NextD04, overall open.

### D04 Local Implementation Order (2026-10-19)

Reuse field streams/OraclePrepareExecute/session exclusivity; never migrate locators or read old-transaction data through a second connection. Independent deliveries each require failing tests→implementation/build/full tests/review/local commit; D04 stays in progress until integrated.

1. Internal LobLocator.Parse(byte[]): V1 40-byte header/V2 8-byte memory header, version/byte length/borrowed inline view or out-of-row marker. Strict version/header/length-offset/extension-rowkey bounds; no untrusted-length allocation/ordinary BLOB-CLOB interpretation. Empty/V1-compatible/V2simple-extern-disk/inline-out of row/truncation-overflow/large-external cases. No handshake/public capability change yet.
2. After negotiated/explicit gates, integrate210/211/metadata/parameters/D02OUT. Handles bind session/transaction generation; validate atomic OUT/reopen/transaction-end/Dispose/reader exclusivity. Disabled locators still reject; fixtures not live proof.
3. Same-session bounded DBMS_LOB.READ Stream/TextReader, sync-async cancel-timeout/Unicode character-versus-byte offsets/existing budgets-errors. Original reader must drain/dispose before lease; no full LOB/second-connection workaround. Inputs/outputs/streams/five assets.
4. Review all contracts/sync README-architecture-ledger. Real versions/OBProxy/server mutation-temp semantics/TLS extensions/other-machine separate; incomplete scope cannot mark D04 complete.

Behavior references: pinned Java6adc311ba5db8b2f19f98f3e6e4734667fdfdda1 Lob.java/Blob.java/Clob.java; OceanBase5e8a63d29dae9fde5e962b8fcc7b167d438fc343 deps/oblib/src/common/object/ob_object.h/.cpp(ObLobLocator/ObMemLobCommon/ObLobCommon/get_inrow_data/get_lob_data_byte_len). V2 first4 bytes are is_mem_loc ObLobCommon, not V1 LOB! magic; do not adopt Java's permissive header omission. Unknown layouts reject pending version evidence.

### D04 Public Reading and Binding (2026-10-19)

After parser commitcc0cc1d, integrate default-off UseLobLocators/ClientSupportLobLocator/separateclientV2 attribute, preserving proxy namespace for OB20/trace. Public Blob/Clob/OceanBaseLob, standard/provider getters return handles. D02OUT atomic after terminal; InputOutput inferred type frozen/matched. Under connection lock, parameters/open-stream validate original session/transaction reference/epoch; stale connection/ended transaction/disposed handle/missing capability reject pre-send.

Inline borrows payload; external BLOB/CLOB uses original-session DBMS_LOB.READ chunks, one body at a time, advancing byte/scalar offsets separately. NUMBER amount exact positive integer, verify actual buffer/progress/total bytes. Original reader must drain. Stream reservation; Dispose cancels pending reads; async reads/cleanup use async network. Modern stream/handle async disposal; Standard2.0 internal async cleanup. No evidence for server temporary lifecycle/mutation yet; do not invent FREETEMPORARY or equate client Dispose/server destruction.

Tests cover output/return/input-output/NULL-tail ERR/wrong types/bad locators/cross-connection-reopen-epoch/Dispose/cancel-timeout/bounded Unicode/five-asset standard-OB20 boundaries; red-green/full/re-review at ledger tail. D04 then in progress; mutation/temp APIs assessed next. Live/OBProxy/TLS-extensions/other-machine/release matrix deferred, overall open.

### D04 LOB Mutation (2026-10-19)

Add same-session WRITE/TRIM, referencing pinned Blob.updateBlobToServer/trimBlobToServer and Clob DBMS_LOB directions, no copying. OceanBaseLob.WriteBytes/WriteBytesAsync(byte[]slice),WriteText/WriteTextAsync(strictUTF8),Truncate/TruncateAsync. .NET 0-based offset, BLOB bytes/CLOB Unicode scalars; TRIM same units. Direct sync/async I/O, no dependency/new connection.

- [x] Failing WRITE chunks<=32767BLOB bytes/8191CLOB scalars; each request uses prior successful locator, update handle only after terminal; TRIM/later reads use new locator.
- [x] Shared LobMutation reserves session across all chunks, original epoch/timeout; Dispose cancels. Range/UTF16/Int64-offset overflow checked before first packet. Update after full success; NULL/mismatched output breaks session/invalidates old handle.
- [x] Sync-async/Unicode boundaries/>Int32 offsets/empty-precancel/busy reader-stream/transaction-reopen/known ERR-successful prefix-tail fault-timeout-Dispose. Side effects retain unknown-outcome semantics; multichunk nontransactional as a unit, no automatic prefix rollback/failed-chunk replay.
- [x] Five assets/review-fix/five-three Release/full tests/docs/local commit. Temporary create/ownership follows; live resources/OBProxy/TLS/other-machine deferred.

### D04 Temporary LOB Creation and Explicit Ownership (2026-10-19)

Existing options/PrepareExecute/session-epoch. Connection CreateTemporaryLob(OceanBaseDbType,OceanBaseTransaction?) plus Async, Blob/Clob only, command-style explicit transaction binding/connection CommandTimeout. Fixed PL/SQL declares typed BLOB-CLOB local, CREATETEMPORARY(cache=FALSE,durDBMS_LOB.SESSION), then OUT locator; avoids NULL-overload/guessed wire BOOLEAN. After terminal deliver owning handle, never ownership on failure. Once create SQL sending starts, any failure including tail ERR/unsafe delivery destroys session to reclaim resources. Precreate version-probe trusted ERR/unsupported version retains session.

Only creator handles expose effective FreeTemporary/FreeTemporaryAsync ownership; ordinary queried/output handles cannot free. Reserve same session, reuse mutation cancel-timeout-unknown semantics. Typed PL/SQL local receives locator/calls FREETEMPORARY/returns fixed completion string; do not parse invalidated released locator normally. Success invalidates handle; pending failure follows mutation invalidation. Reject cross-session/epoch/active streams. Dispose remains client-only; explicitly FreeTemporary before transaction end/Dispose. Unfreed/failed SESSION objects may persist until original session ends; no background cleanup/autocommit.

Reference: official OceanBase docs pinned2a5ae3a23a51729d065cec716508eb26cb064106 OracleDBMS_LOB CREATETEMPORARY/FREETEMPORARY/ISTEMPORARY (links architecture D04). Java createBlob/createClob creates local empty objects, not server-temp evidence. Docs establish interface, fixtures do not prove real resources.

- [x] Failing sync-async/BLOB-CLOB/typed-local/OUTNULL-complete/ownership rejection/Dispose-without-DB-call cases.
- [x] Factory/ownership/same-session Free; transaction-reopen/active streams/cancel-timeout/tail ERR-NULL-bad output/delivery-terminal races; post-create-tail ERR reclamation/version-probe retention regressions pass.
- [x] Five assets standard-OB20 success/tail rejection/all-target/full tests/review-fix/docs complete, ledger local evidence/no push. Live/other-machine deferred.

Local five-three Release 0 warnings/errors; Unit2054(new 46)/Consumer724(new 80)/DI8 pass,no skips. Review fixed typed overload/post-create-tail ERR orphan, ledger red-green. D04 local scope integrated, pending real resources/version/OBProxy/TLS; next D05, overall incomplete.

### D05 ARRAY/STRUCT Local Order (2026-10-19)

Pinned Java6adc311ba5db8b2f19f98f3e6e4734667fdfdda1 ColumnDefinition/BinaryRowProtocol.getComplexArray-getComplexStruct/ComplexUtil behavior only. Complex results lack uniform outer lengths; complete descriptors determine boundaries. ParameterNULL bitmap startsbit0, result/nested result bit2, never same serialization. NUMBER OceanBaseDecimal, existing exact date/nano codecs; unsupported members never guessed.

- [x] Independent codec: Internal/Values/ComplexType.cs immutable layouts, ComplexValueCodec.cs ByteCursor results/BinaryWriter parameters. Failing Oracle objects-collections/nested NULL-cross-bitmap/empty/Unicode/wide NUMBER/RAW/DATE-TIMESTAMP/next-field fixtures; reject MySQL/bad lengths-scalars/depth-member budgets/type mismatch. ComplexValueCodecTests.cs RED→GREEN/full build-tests/review/commit; no public complex API yet.
- [x] Dictionary/lifecycle: exact schema+type full nested descriptions; define same-session queries/reader-transaction/cache-refresh before construction. No hidden Java-style second query connection or schema-blind uppercase-name cache. Ledger same-session dictionary/construction evidence; column/parameter/output follows.

Decision: public GetComplexType/GetComplexTypeAsync(schemaName,typeName,transaction),ClearComplexTypeCache(transaction). Exact dictionary-bound names, no SQL-identifier/synonym parsing. Immutable OceanBaseComplexType/Members/CreateValue→OceanBaseComplexValue validates members/nested types/exact scalars/byte-member budgets. Copy RAW/returned arrays; nested values require corresponding descriptor. Oracle only; reject LOB/cursor/national/cyclic graphs.

One physical reservation spans ALL_TYPES/ALL_TYPE_ATTRS/ALL_COLL_TYPES, explicit transaction/original session-epoch, connection CommandTimeout per query/true async caller cancellation. Busyreader/reservation/ambient sends nothing. Validate result shape/owner-name-kind/attribute count-contiguous ordinals/one collection element. Depth<=32; session cache<=65535 members/65535namedtypes/4 MiB UTF8dictionary. Clear/reload at capacity; failures publish/debit nothing. Case-sensitive(schema,type) keys. Reopen/transaction change/explicit Clear invalidate all old descriptors-values, including dependencies. ExternalDDL requires idle Clear/reload; no fabricated server-version. Invalidated snapshots inspectable but no new construction/binding. Independent tests/five assets/full build-tests/read-only review/commit; public columns/parameters later.
- [x] Named complex columns: extended metadata/preexecution dictionary snapshot/binary boundaries/SequentialAccess current-field materialization/paging consistency/five-asset sync-async standard-OB20. Match exact preloaded schema/type, no reader queries. Preserve wire version without claiming dictionary proves it; external DDL explicit refresh. Text even NULL/unloaded-stale descriptors reject, no byte[]-string skipping.48 new Unit/20 extended Consumer/full/review ledger. Original errors/cancel/unknown semantics at every layer.
- [x] Named additional parameter heads/D02 output declarations/atomic publication, same-session-cache/full-terminal rules; ledger named-binding evidence. Undeclared complex OUT including NULL rejects.
- [x] Anonymous collections: scalar results,factory scalar/named-element input,three scalar-output directions integrated. Anonymous complex result/output without full layout rejects by design.
- [x] Full D05 scope review/docs/all-target/full tests/review pass; anonymous-output/ledger evidence below. Live dictionary/DDL visibility/round-trip/version-OBProxy resources deferred; D05 pending.

#### D05 Named Parameters and Outputs

Parameter.ComplexType declaration; nonNULL Input/InputOutput infer immutable value, NULL/Output/ReturnValue require explicit descriptor. ARRAY/STRUCT use160,length-encoded schema/type/version 0 named head, no anonymous-element head. Pinned OceanBase5e8a63d29dae9fde5e962b8fcc7b167d438fc343 obmp_stmt_execute.cpp decode_type_info reads/discards version and resolves OBJECT/COLLECTION by schema/name.0 means no dictionary-version claim; external DDL refresh unchanged.

1. Failing independent mixed scalar-object-collection-NULL bytes/declaration boundaries; shared encoder, all heads before values, budget includes heads, complex writes existing writer.
2. Before lease validate owning connection/original session/epoch/cache identity. Prepare-only does not prevalidate unassigned values. Crossconnection/reopen/Clear/transaction change reject pre-send.
3. Snapshot output declarations, match every metadata column including NULL/zero rows. Existing atomic full-response publication; failure/cancel/early close preserves values; undeclared complex OUT rejects.
4. Sync-async/five-asset standard-OB20/full build-tests/review/docs-diff/local commit. Anonymous later/live-other-machine deferred; no D05/overall closure.

Local complete/re-reviewed: five-three Release 0 warnings/errors; Unit2286(new 79)/Consumer744(20 existing extended)/DI8 pass,no skips. Ledger covers NULL-zero metadata/snapshots/atomic errors-cancel-close/original session. Anonymous/live remained; D05 in progress.

#### D05 Anonymous Scalar Collection Results

Pinned OceanBaseConnection.getAnonymousArrayComplexType creates single-element layout only with empty typeName/noncomplex element; BinaryRowProtocol usesbit2 result bitmap/scalar lengths. Support NUMBER(0/3/246 normalized),VARCHAR2(253),CHAR(254),RAW(203),DATE(12),TIMESTAMP(202); reject other/no-layout complex. No code port/reader dictionary queries.

1. Failing metadata/byte boundaries/public default-sequential; preserve wire schema/empty TypeName,IsAnonymous,DataTypeNameARRAY without invented name.
2. Existing codec/budgets/field streams/paging matches/NULL-empty-next column-multiresult-cancel-errors. Result descriptors readonly; CreateValue/parameter-OUT reject until separate factory integration, no accidental decoder bypass.
3. Five-asset standard-OB20/all-target/full tests/review/docs/local commit; D05 in progress/live-other-machine deferred.

Local five-three Release 0 warnings/errors; Unit2331(new 45,removed 0)/Consumer744(20 extended)/DI8 pass,no skips. Shared budgets/overflow/cancel/successful pages/later-element-version changes. Existing LOB send-close race fixed against actual rejection boundary with exact protocol-exception assertion. Ledger red-green/full/review; construction/binding/D05 then open.

#### D05 Anonymous Collection Construction and Input Binding

Connection `CreateArrayType(string elementTypeName, OceanBaseTransaction? transaction = null)` accepts case-insensitive NUMBER/VARCHAR2/CHAR/RAW/DATE/TIMESTAMP only, no aliases/implicit queries. `CreateArrayType(OceanBaseComplexType elementType, OceanBaseTransaction? transaction = null)` wraps same-session preloaded named objects/collections. Root schema/type empty; scalar members canonical layout, named members original descriptor. Reject anonymous element types lacking full nested identity. Existing CreateValue/RAW copies/NULL-empty/exact nesting/budgets.

References: pinned OBArrayParameter.storeArrayTypeInfo/ComplexUtil.getMysqlType, OceanBase obmp_stmt_execute.cpp decode_type_info/L761–806 empty-name branch. Anonymous160/empty schema-type/version 0/element byte; named complex element160+its schema-type-version 0. Do not copy Java's type-name-length-as-element-version. Existing collection body/bit0/all-heads-first/shared64 MiB-10 budget.

- [x] AnonymousCollectionParameterTests.cs missing API RED compile/independent bytes: six scalars,NULL-empty,mixed,named object-collection heads,strict members-invalid declarations; reuse budget tests.
- [x] OceanBaseConnection.ComplexTypes.cs factories/context reuse. ValidateComplexBinding permits same-context factory anonymous root; named requires cached reference. Clear/cross-connection/reopen/epoch rejects pre-send; busy reader/ambient no query/send.
- [x] PreparedParameters anonymous element heads; valid-context Input only then. Readonly result descriptors/Output-InputOutput-ReturnValue explicitly reject pending output contract. Update OceanBaseComplexType docs; no dependency/fake dictionary.
- [x] Sync-async ordinary prepare/PrepareExecute/five-asset standard-OB20/reuse/full build-tests/review-fix/docs-diff/local commit,no push. D05 then in progress/live-other-machine deferred.

Local: five-three Release 0 warnings/errors; Unit2363(new 32,removed 0)/Consumer744/DI8 pass,no skips; ordinary prepare/PrepareExecute/five assets/Clear-cross-connection-reopen-transaction covered. Ledger details; anonymous OUT/full D05 review next, overall incomplete.

#### D05 Anonymous Scalar Collection Outputs

Reuse six scalar factories/explicit ComplexType/session-epoch-cache. Output/ReturnValue never infer old Value; InputOutput may infer factory value. Readonly result descriptors cannot bind. Anonymousmetadata only element code; normalize NUMBER, not treat empty-name schema as named identity. Retain response metadata, bind values to snapshotted factory descriptor. Element160 lacks full response identity and rejects output pre-send, never guess layout. Named output still exact cached dictionary reference.

- [x] AnonymousCollectionOutputTests.cs RED→GREEN: sync-async/three directions/six scalars/NULL-empty-zero rows/bad metadata including NULL-zero/mixed atomic OUT; five-asset standard-OB20 actual consumers.
- [x] PreparedParameters rejects context-free/anonymous complex OUT only; ResultColumn/OracleCommandPlan validate anonymous scalar layout/snapshot descriptor/existing full terminal publication. No container/full payload copy added.
- [x] Clear/reopen/cross-connection/epoch reject preexecute; targeted invalidation/error-cancel-early close retains values. Update Input-only tests/docs without removal.
- [x] All-target/full tests/independent review-fix/README-architecture-ledger/diff/local commit; live-other-machine deferred, overall open.

Results: five-three Release 0 warnings/errors; Unit2434(new 71,removed 0)/Consumer744/DI8 pass,no skips. Review/full D05 mapping no blockers. Local agreed scope complete/pending verification; next D06. Anonymouscomplex OUT without type identity restriction matches unknown-layout rejection; live dictionary-DDL/OceanBase-OBProxy/other-machine deferred, overall incomplete.

### D06 Array Binding: Local Implementation

Full scope bulk/array binding,partial success/failure position/per-entry counts,D02 multirow OUT-RETURNING; sequential DbBatch is not a substitute. Deliver request/response/public/full matrix modules with full verification/commit each. D06 in progress until public/agreed local scope complete.

Primary pinnedOceanBase5e8a63d29dae9fde5e962b8fcc7b167d438fc343 obmp_stmt_prexecute.cpp(L72,173,336,1135,1155,1180,1306),obmp_stmt_execute.cpp(L239,1742); Java ComStmtPrepareExecute.writeCmdArrayBinding secondary. PrepareExecute array mode iteration-count 2, not oldEXECUTE0x08-0x10 header flags. All parameters equal positive-count anonymous collections,nonnull roots/nullable elements,heads before column bodies; one element also iteration2. DML exec_mode0x80(OCI_BATCH_ERRORS) preserves item responses/disables server aggregation, not Java0x01 with fabricated counts. PL no save-exception, follows stop/output semantics.

DMLsuccess first column=affected rows; error first column=zero-based failed position; PLsuccess first column=position, not count. Responses error_code/optional outputs/finalerror_message,tail OK cumulative affected rows. Validate shape/row count-position/code/NULL/terminal, retain proven prefix/unknown without autocommit-rollback-replay. Public requires Oracle/explicit transaction/existing PrepareExecute version gate/session-budget-cleanup, no new connection. MultirowOUT cannot fit D02 scalar container.

#### D06 Request Encoding Submodule

- [x] OracleArrayBindingRequestTests.cs missing API RED compile: six scalars/mixed/cross-bitmap/all NULL/single row/named complex/full independent bytes/equal positive count/invalid declarations; ordinaryD02 unchanged.
- [x] OraclePrepareExecutePacket.BuildArray takes frozen normalized Input columns, reuses encoder/codec; validates nonempty columns/factory anonymous/nonnull roots/equal positive count. Internal OceanBaseComplexValue.Count avoids array copy for counting. BuildCore changes iteration2/execmode0x80 only, shares SQLUTF8/CRC/final budget. No public API yet; complete execution below.
- [x] Encoder owns shape/bytes; later StartCommand owns connection-transaction-cache checks. No public/live claim from encoding. Full execution later covers items/multirow/public/five assets/standard-OB20/transaction-fault-cancel matrix; encoder alone never closes D06.
- [x] All-target/full tests/review/docs-diff/local commit; real database-OBProxy/other-machine deferred.

#### D06 Complete Execution Pipeline (Pause Boundary for That Round)

User required complete D06 before progress update/commit/pause, not stopping at internal encoding.

- [x] Allfive assets expose `OceanBaseCommand.ExecuteArray(rowCount)`/`ExecuteArrayAsync(rowCount, token)`. Rows1–65535,Oracle/explicit transaction/UseOraclePrepareExecute; existing version/session/cache epoch/lease/cancel-timeout/delayed close. No new connection/automatic commit-rollback/replay.
- [x] Freeze Oraclebindorder. Input/InputOutput factory anonymous values; Output/ReturnValue explicit factory anonymous descriptor plus NULL placeholders. Strict root/count; six D05 scalars/preloaded named complex; validate output element type, never D02scalar container.
- [x] Consume `sql_no,error_code,[outputs],error_message`. DMLwithoutRETURNING checks item/tail counts; PL/RETURNING first column=position,per-item count null. Group multirow OUT by input position,preserve NULL-zero rows,codes; hide server messages to avoid bind leakage. Accept negative internal row error codes; failed/unknown counts unset.
- [x] Immutable OceanBaseArrayBindingResult.Items/full terminal total. ArrayBindingResult retains provable partial results of latest started array SQL; null before SQL send,Unknown suffix. Publish parameters only after full response/OB20/cancel-Busy validation. SuccessfulOUT Value=object[][] indexed input position then returned rows. CopyRAW snapshots; <=65535outputvalues,raw field bytes existing packet bound.
- [x] Independentrequestbytes/shape-position-count-NULL-budget/multirow-atomic terminal/public sync-async/partial errors/PLstop/transaction/pre-send invalid/cancel-timeout-disconnect; five assets standard-OB20 success/trailing-body. All-target/full tests/review/README-architecture-ledger/commit, then user-requested pause; resume D07/open modules next time.

### D07 Client-side Prepare Fallback

Continue the existing safe text encoding and fallback only for explicit unsupported-prepare responses. Keep the default execution model. Command-level `AllowClientSidePrepareFallback` defaults to false; add no connection-string alias or dependency. Only an automatic prepare whose first response is 1295/HY000, fully consumed and validated through the standard/OB20 boundary, permits one COM_QUERY under the same lease, transaction, and total timeout. Explicit Prepare, FetchSize, Oracle PrepareExecute, and execution-stage errors never fall back. Do not cache unsupported conclusions or replay SQL.

Changes span ParameterBindingPlan, ClientPreparedSql, PreparedStatement, CommandExchange, OceanBaseCommand, Unit/five-asset Consumer tests, README, architecture, and ledger. Oracle reuses binding positions without reinterpreting q/nq literals. MySQL accepts only SQL with identical placeholder positions under backslash on/off and ANSI_QUOTES; reject executable comments and ambiguous quotes. Parenthesize inserted values to isolate lexical boundaries. Reject nonNULL MySQL strings without trusted session collation. Oracle doubles single quotes, casts to VARCHAR2, rejects NUL, and uses HEXTORAW for RAW. Basic Input integers, finite floats, exact decimal, binary, NULL, and ordinary Oracle strings may fall back. Dates, zones, national strings, JSON, LOB, complex values, and outputs still require server preparation, even when NULL. Oracle text is limited to 4000 bytes/RAW2000 bytes; final SQL plus command byte<=64 MiB. Server SQL logs may contain fallback literals; driver logs still omit SQL/parameters.

- [x] Encoder red→green: positions, repeats, comments/quotes, SQL_MODE ambiguity, Unicode/injection characters, independent exact literals, type/budget rejection.
- [x] Execution red→green: default/opt-in, exact error/stage, original session/transaction, sync/async text reader, snapshots, no replay after errors/cancel/timeout/disconnect.
- [x] Five-asset standard/OB20 isolated consumption, full Release/Unit/Consumer/DI, independent review/fixes, docs/diff, local commit without push. Live/other-machine deferred; fixtures never close real acceptance.

Local scope complete: five-core/three-DI Release 0 warnings/errors; Unit2597(new 42), Consumer804(new 30), DI8 pass, zero failures/skips; ledger details. MySQL nonNULL strings still require trusted collation; special types/outputs never fall back. D07 pending verification; D08/D09/environment/release gates open.

### D08 Address Discovery and Round-robin Selection

Implement address sources, selection, mode revalidation, and no replay through C02 PhysicalSession, without another connection pipeline. Five core targets/three modern data-source targets, no dependencies, current branch/local tests/review/commit; live/other-machine deferred. Follow architecture§6/D08; choose API details here.

Design:

- `Load Balance=Sequential|RoundRobin`, default Sequential retains C02. RoundRobin rotates the first candidate per explicit Open, then visits remaining candidates once circularly. Only TCP socket connection failure permits another. Thread-safe cursor shared by data source; direct connections retain per-instance cursors across Close/Open, never global. Replacing connection string/resolver resets the direct cursor; failed Open consumes a selection.
- Five-target `IOceanBaseAddressResolver.Resolve(CancellationToken): string` and `ResolveAsync(CancellationToken): Task<string>`. Direct AddressResolver changes only while closed. Modern builder UseAddressResolver(IOceanBaseAddressResolver?) freezes its reference at Build; source-owned connections cannot override it. Caller owns/disposes and ensures concurrent safety. Sync resolvers must cooperate with cancellation; async must return Task promptly. No user code under locks or synchronous substitution for async.
- Call the resolver once per Open within the same total budget. Parse nonempty output with Server syntax/inherited Port; maximum 64 KiB characters/256 candidates, fully validated/frozen. Configured Server stays mandatory for preconnection diagnostics; resolved addresses replace the list. Empty/invalid/error fails immediately, without seed/previous-result fallback. Cancellation/close/timeout interrupts async waits; late results cannot connect and late faults are observed. Sync resolvers must honor tokens; recheck budget after return.
- Caller-trusted discovery adapters may use OCP/registries. No built-in undefined OCP/TNS/roles/read-only routing. Addresses must belong to a trusted logical tenant. Every new session revalidates TLS/authentication/ExpectedMode; never infer mode from addresses/cache. Driver does not own resolver or log its error text; existing InnerException retains application errors for callers.
- Existing sessions/transactions/readers/statements/locators stay bound. Disconnect never invokes discovery, transparently reconnects, or replays. Explicit caller Close/Open creates a new authenticated/initialized session. Local fixtures do not close topology/coordinator acceptance.

Files: public resolver/interface, internal EndpointSelector, ConnectionEnums, builder/options, PhysicalSession, Connection, DataSource/Builder, TopologyTests/TopologyConsumerTests, README/architecture/ledger.

Review malicious/empty results before send, cursor overflow/concurrency, late results after cancellation, no address switch on identity/TLS failure, and no replay of interrupted transactions. Sequential implementation with independent review:

- [x] Config/selection red→green: strict enum/default/serialization/snapshots, three-address order, overflow, concurrent fair allocation. `dotnet test tests/OceanBaseConnector.UnitTests/OceanBaseConnector.UnitTests.csproj -c Release --no-restore -m:1 -nr:false --filter FullyQualifiedName~Topology`; missing APIs/assertions fail first, then all pass.
- [x] Discovery/open red→green: matching sync/async resolver calls, inherited ports, refresh per reopen, list replacement/no fallback, direct/source lifecycle/freeze, concurrent rotation, cancel/close/timeout/late results-faults, TLS/mode rejection, transaction disconnect unknown outcome without spare connections. Reuse budget/error mapping.
- [x] Five-asset sync/async discovery/rotation, standard/OB20 success/disconnect, full validation, independent review/red-green fixes, docs/UTF8-no-BOM/CRLF/diff, one D08 commit/no push. State local adapter versus real deployment boundaries.

Local complete: five-core/three-DI Release 0 warnings/errors; Unit2653(new 56), Consumer844(new 40), DI8 pass, zero failures/skips/removals. Review fixed callbacks under locks and exception cleanup; budget-reset-after-discovery mutation was detected. Ledger evidence. D08 pending; live topology/database/OBProxy/other-machine deferred. Continue D09/open work.

### D09 XID Value Foundation

D09 includes explicit XA, pinned sessions, stages, and recovery. Real coordinator/both-mode live tests deferred; TransactionScope still rejects. First deliver an immutable XID value, without treating its existence as public XA execution support; stage/recovery follow. References: pinned Java OceanBaseXid/OceanBaseXaResource and OceanBase5e8a63d29dae9fde5e962b8fcc7b167d438fc343 ObXATransID(src/storage/tx/ob_xa_define.h/.cpp), identity/two64-byte limits only. Unlike Java's retained/returned arrays, defensively copy to protect coordinator identities.

Five-target sealed OceanBaseXid:IEquatable<OceanBaseXid>, constructor(int formatId,byte[] globalTransactionId,byte[] branchQualifier), readonly FormatId. Preserve signed Int32 except reserved NULLXID=-1; other negatives valid. MySQL later preserves32-bit unsigned pattern as Java does; Oracle signed. gtrid1–64 bytes, bqual0–64, nonnull, all bytes including NUL/high bits. GetGlobalTransactionId/GetBranchQualifier return copies. Equality/hash includes format, separate boundaries, full bytes; culture independent, not text. Default ToString hides identity; no implicit string/Guid/custom serialization. Recovery outside this domain fails, never truncates/drops. Server Int64 identity does not imply all values usable by coordinators; validate target32-bit representation exactly.

Files: src/OceanBaseConnector/OceanBaseXid.cs; UnitTests/XidTests.cs boundaries/immutability/equality/arbitrary bytes; ConsumerTests/XidConsumerTests.cs five isolated APIs; ConsumerTests.NetStandard/NetStandardConsumerSample.cs compile. No new config/dependencies/database access/execution API.

- [x] XidTests first: `dotnet test tests/OceanBaseConnector.UnitTests/OceanBaseConnector.UnitTests.csproj -c Release --no-restore -m:1 -nr:false --filter FullyQualifiedName~Xid` fails missing API; minimal value passes. Null/length/format/copy mutation/same concatenation-different boundary/format-byte-length differences/null-other objects/dictionary/64+64 snapshot.
- [x] Five-asset boundaries/copies/IEquatable/equality/hash/no string leakage; Standard2.0 direct compilation. net10 host does not prove five runtimes/other machines.
- [x] Five-core/three-DI Release/full tests/independent review/fixes/docs/UTF8-no-BOM/CRLF/diff/local commit, no push. D09 in progress; session/stage/recovery next, overall open.

Foundation complete: five-core/three-DI Release 0 warnings/errors; Unit2664(new 11), Consumer849(new 5), DI8 pass, zero failures/skips/removals; review clear. Full suite also found D08 close-mapping and existing batch early-close fixture races, fixed/retested/reviewed and separately committed9493278/e601e23; ledger. D09 stages/recovery/live remain.

### D09 MySQL XA Stages and Recovery

Architecture§6 D09 API/stack: five-target C#/CommandExchange/QueryResult, no dependencies/current branch/no push/live/other-machine. OceanBaseXaResource.cs public stage APIs/enums/state; OceanBaseConnection.Xa.cs ownership/exchanges; Transactions.cs/Database.cs business/local-transaction isolation and Close detachment. XaTests/XaConsumerTests/Standard2.0 compilation; README/architecture/plan/ledger.

Review stale resources after reopen, no early transition on trusted errors, End(Fail) requiring rollback across recovery association, no Int64 truncation/drop, no replay or partial recovery on cancel/bad OB20/disconnect.

- [x] Failing lifecycle/recovery APIs: one/two-phase, Join/Resume/Suspend/Fail, invalid stage/XID/null/enum, business rejection without active branch, exclusive reader, local-transaction/database/ambient isolation, stale resources after Close/reopen, sync/async.
- [x] Minimal pinned resource/fixed MySQL keywords/hex/four-column recovery. Strict numbers/lengths/raw bytes:0/Int32 boundary patterns, empty branch,64+64/high/NUL, NULL/invalid/wide/negative/multiple results/excess rows; no partial recovery.
- [x] Standard/OB20 success/trusted rejection/precancel/postsend cancel-timeout-disconnect-Close/bad boundaries; five-asset public sync/async with explicit post-auth MySQL facts, A05 unchanged.
- [x] Five-core/three-DI/full tests/review/fixes/docs/UTF8-no-BOM/CRLF/diff/commit; continue Oracle/open items.

Complete: Release 0 warnings/errors; Unit2717(new 53), Consumer929(new 80), DI8 pass, zero failures/skips/removals. Review fixed destruction after trusted XA rollback errors, empty-recovery exception mapping, six diagnostic names, empty-branch X'' encoding; ledger red-green. D09 in progress; Oracle DBMS_XA types/codes/recovery next. A05/TransactionScope/live/other-machine boundaries unchanged.

### D09 Oracle DBMS_XA

Architecture§6 D09 target/stack/API; direct implementation/independent design-code review/current master/no push/live/other-machine. New OceanBaseConnection.XaOracle.cs private descriptors/init/0xa1/codes/recovery; Xa.cs/OceanBaseXaResource.cs shared stages/Prepare vote; OceanBaseException.cs XaErrorCode. OracleXaTests/OracleXaConsumerTests/Standard2.0 exception-property compilation; README/architecture/plan/ledger. Public type dictionary/business validation unchanged.

Review autocommit disabled only after first confirmation, one budget, distinct0/3/positive/negative codes, no early stages, private descriptors outside public cache, statement/OB20 lifecycle, complete recovery/exact Int32/16383 members, stale resources after reopen. Public XaErrorCode only from complete trusted responses.

- [x] Oracle lifecycle/ReadOnly/recovery: `dotnet test tests/OceanBaseConnector.UnitTests/OceanBaseConnector.UnitTests.csproj -c Release --no-restore -m:1 -nr:false --filter FullyQualifiedName~OracleXa` first fails existing Oracle rejection. Check signed input format/raw bytes/Start-End flags/Commit Boolean; handcrafted recovery bytes, not product-generated expectations.
- [x] Minimal Oracle/shared votes: one/two-phase, Join/Resume/Suspend/Fail, ReadOnly→Idle, version/option rejection, initialization/stage ERR/codes, exclusivity/Close/reopen/cancel-timeout-disconnect/bad tail/extra results. New nonzero-code red-green retains code/discards session/no replay.
- [x] Recovery empty arrays/arbitrary bytes/empty-NULL branches/signed bounds/wide-fraction-NULL formats/bad lengths-NULL members-multiple rows-missing output-wrong types/member bound/tail interruption. No partial results/public cache pollution.
- [x] Five assets sync/async×standard/OB20 stages/ReadOnly/recovery/trusted codes/disconnect/boundaries; Standard compilation. Five-core/three-DI/full tests/review/fix/docs/UTF8-no-BOM/CRLF/diff/local commit. Evidence-based status only; environment/open work stays open.

Local complete: five-core/three-DI Release 0 warnings/errors; Unit2803(new 86), Consumer1029(new 100), DI8 pass, zero failures/skips/removals; ledger review/red-green. D09 pending live/coordinator/other-machine acceptance; overall goal incomplete, no push.

### D09 MySQL XA over TLS (2026-10-20)

Reuse XaConsumerTests post-auth mode doubles/stage scripts; preserve80 plaintext cases/assertions, add80 Required TLS=five assets×sync/async×standard/OB20×success/reject/disconnect/boundary. Shared Handshake SSLRequest/upgrade encrypts subsequent traffic. Keep stage exclusivity/trusted-error explicit retry/unknown disconnect-boundary/full recovery/no replay. Initial Oracle fixture then explicit MySQL fact; A05 unchanged, no actual MySQL authentication/server proof.

Parameterize this Consumer script only, product/shared helper unchanged; no dependencies/root operations. Five-core/five-DI/tests Release,160 script cases, scoped format/name comparison/review/evidence/docs/commit. Without necessary fixes, do not repeat prior full Unit/Consumer/DI; retain its report. Live database/OBProxy/coordinator/trusted CA/other-machine deferred; five assets isolated on net10.

Complete: five-core/five-DI/tests Release 0 warnings/errors;160 pass, zero failures/skips; old80 removed 0, new TLS80 all pass. Independent review clear; format/raw reports/name comparison in ledger. No real-mode/D09 acceptance closure.

## Execution and Progress Rules

Mark a task complete only with code, targeted tests, owning-target compilation, and evidence. G1/G2/G3/R02 additionally require real-environment records; skipped is not passed. Each merge updates [Progress Ledger](obconnector-dotnet-progress.md) status/evidence/blockers/next task. If live behavior contradicts assumptions, revise architecture/plan before dependent work.

Shared verification after project creation; record actual commands/exit codes:

```powershell
dotnet restore OceanBaseConnector.slnx
dotnet build OceanBaseConnector.slnx -c Release --no-restore
dotnet test tests/OceanBaseConnector.UnitTests/OceanBaseConnector.UnitTests.csproj -c Release --no-restore
dotnet test tests/OceanBaseConnector.ConsumerTests/OceanBaseConnector.ConsumerTests.csproj -c Release --no-restore
dotnet test tests/OceanBaseConnector.IntegrationTests/OceanBaseConnector.IntegrationTests.csproj -c Release --no-restore
dotnet pack src/OceanBaseConnector/OceanBaseConnector.csproj -c Release --no-build
```

Daily tests/consumers run.NET10; Windows/Linux CI builds five product targets. Baseline SetTargetFramework/SkipGetTargetFrameworkProperties explicitly selects netstandard2.0; isolated loads cover Standard2.0/2.1, never executable Standard test targets. Older-runtime evidence separately B09/R01. Integration variables: `OB_MYSQL_CONNECTION_STRING`, `OB_ORACLE_CONNECTION_STRING`, `OB_MYSQL_PROXY_CONNECTION_STRING`, `OB_ORACLE_PROXY_CONNECTION_STRING`; unknown-plugin case `OB_UNSUPPORTED_AUTH_CONNECTION_STRING`(default VerifyFull/test account only). Gate acceptance `OB_REQUIRE_INTEGRATION=1` fails missing required environments; daily development may explicitly skip/report. Never store certificates, credentials, raw captures in the repository.

## Configurable Live-database Acceptance Program (2026-10-20, New User Requirement)

Independent net10 console in tests/OceanBaseConnector.Acceptance, using public product APIs only, excluded from product packages, no dependencies. JSON config specifies connection string or environment-variable name, explicit MySql/Oracle mode, groups, object-creation permission, large-value length. Existing strings configure TLS/OB20/trace/Oracle extensions; no duplicated options or A05/pooling bypass. `--list` enumerates all 65 items across 14 groups without credentials/network; `--config` executes; `--report` selects redacted JSON. Invalid config/case failure/user cancellation returns nonzero.

By mode/config cover open/reopen, factory/data source, text CRUD, parameters/prepare, exact types, NULL/Unicode/binary, transactions/savepoints, readers/SequentialAccess/pages, DbBatch/multiple results, GetSchema, uploads, diagnostics, Oracle OUT/RETURNING/procedures/REF CURSOR, arrays, LOB/complex values, explicit XA. Named skips retain requirements for independent faults, KDC/private CA, topology switching, full reset, coordinator crash recovery. One successful connection never proves the full TLS/auth/OB20 matrix.

Create only dedicated random OBA_ table/procedure/type names<=30 characters, never reuse/delete existing objects. Creation disabled means explicit skips. Separate connection per case; record creations; finally clean exact names using new connections. Cleanup failure fails the case/lists residuals without replacing primary failure. XA touches generated XIDs only, never commits/rolls back other recovered branches. Reports omit connection strings/passwords/raw SQL/server error text/stacks. Execution writes objects: use a dedicated test database/tenant. No live connection/deployment this round.

This program replaces earlier current-state statements that no regular live-acceptance code existed. Historical statements remain historical; actual live execution and fault matrices remain unverified.

Implementation steps:

1. Failing config/offline catalog/scheduling/exit-code/redaction/cleanup-report regressions, then console configuration/runner.
2. Public API live cases/both-mode examples/coverage and skip reasons; compilation never counts as live success.
3. Existing Unit verifies control flow/local commands without database access; build all products/tests/run local regressions. Independent review/fix/docs/local commit, no push. Actual live execution separately unverified.
