# Compatibility and known gaps

## Slow client operation diagnostics

[Slow Query Threshold](obconnector-dotnet-architecture-design.md#slow-query-diagnostics) is an opt-in millisecond client-lifetime threshold, default zero. It covers named execute/cursor.fetch exchanges through reader completion, includes consumption time, preserves errors/unknown outcomes and emits no SQL/values. It provides independent Warning/counter/existing-span channels. Server-only profiling, protocol diagnostics, load limits and the complete deployment/runtime matrix are not implied.

## Connection phase diagnostics

The [phase-duration histogram](obconnector-dotnet-architecture-design.md#connection-phase-diagnostics) measures actual acquisition/discovery, transport/handshake and pool wait/probe attempts without adding high-cardinality or sensitive labels. It preserves existing operation telemetry. A phase interrupted by a linked timeout token may report `canceled` while the enclosing Open reports `timeout`; an unsignaled finite pool wait reports `elapsed`. The full P2-04 diagnostics scope, deployment performance limits and independent-runtime acceptance remain open.

## Oracle standalone Prepare

[Standalone preparation](obconnector-dotnet-architecture-design.md#oracle-standalone-prepare) now supports Oracle text OUT/RETURNING, stored procedures and function returns. It neither executes the target nor consumes input values; server parse/privilege/metadata-lock costs remain possible. The command retains a statement only until the next execution transfers ownership to its cursor owner. Full parameter metadata and the exact pure-OUT DML split layout are validated separately; other mismatches reject. Prepare ignores execution FetchSize, while positive FetchSize for the output execution plan and REF CURSOR IN/INOUT/column values remain unsupported.

The direct Oracle4.4.2.2 profile passed ten sync/async block/RETURNING/procedure/function/cursor cases and twenty final cases including existing procedure/cursor and prepared array regressions. Preparation left the test table empty and outputs unchanged; REF CURSORs remained consumable after command disposal. The initial two RETURNING count failures (one wire input plus one output column), profile-data fixture error and all original reports remain in [the module record](obconnector-dotnet-completeness.md#1534-2026-10-25-oracle-独立-prepare). This profile uses TLS disabled on Windows/.NET10; real proxy/TLS, other versions and metadata-lock/fault matrices remain open.

## Result character sets

Ordinary result strings now use the [explicit strict collation map](obconnector-dotnet-architecture-design.md#result-character-sets): ASCII, MySQL latin1/CP1252, GBK, GB18030-2005, UTF-16/LE and mapped UTF-8 families. The result decoder and MySQL width metadata share one map; binary63, JSON/CLOB and trusted Oracle national rules keep their own contracts. Unknown/2022 families are rejected, not guessed. Client command/parameter encoding remains UTF-8; this is not general charset negotiation.

The real direct OceanBase4.4.2.2 MySQL test passes six charset cases, each covering eight text/prepared × buffered/sequential × sync/async combinations, including exact GB18030 corrected characters, NULL and connection reuse. UTF16/LE direct SET attempts remain recorded as server1231/42000 failures; the final cases use `character_set_results=NULL` to preserve expression encoding and assert string FieldType. The initial Int32-versus-Int64 literal assertion and an intermediate test-source compilation failure are retained as fixture/development failures. Local dual-mode reader tests and five-asset consumers cover additional paths; they do not establish Oracle/proxy/TLS compatibility. Full evidence and environment limitations are in [the module record](obconnector-dotnet-completeness.md#1532-2026-10-25-普通结果字符集).

## Ordinary pool configuration identity

Internal [pool configuration normalization](obconnector-dotnet-architecture-design.md#ordinary-pool-configuration-identity) now unifies equivalent option order, typed spelling and effective defaults for ordinary pool reuse, capacity, failure cleanup and clearing. Parsed empty PAM factors remain distinct from absent factors across Open/Close/Clear; text values, policy/certificate references and private sources retain their isolation. Public ConnectionString serialization is unchanged. Synchronous same-full-pool Open inside Closed waits for a slot still owned by Close; defer reopening until Close completes, especially when Pool Wait Timeout=0 has no finite wait limit. Full reset, arbitrary reentry/concurrency, long-running load, proxy/platform and independent-runtime acceptance remain open; see [the module record](obconnector-dotnet-completeness.md#1531-2026-10-25-普通池配置归一化).

## MySQL stored-procedure execution

The module developed from `1bb13cb` adds MySQL CommandType.StoredProcedure with Input/Output/InputOutput in collection order. It requires effective PS multi-results, uses prepared SET/CALL plus a hidden output SELECT, and drains remaining results on reader close. Output values follow actual SELECT metadata rather than forced parameter DbType conversion. ReturnValue, FetchSize, SchemaOnly and KeyInfo are rejected; InputOutput streams are unsupported. Oracle execution keeps its PrepareExecute contract. See [API and lifecycle rules](obconnector-dotnet-architecture-design.md#mysql-stored-procedures) and [final module evidence](obconnector-dotnet-completeness.md#1530-2026-10-25-mysql-存储过程执行本地交付).

Final full regression records4414unit and2558consumer passes, zero failures/skips, plus14DI passes. Consumer count/name/ID multisets add80cases without removing existing ones; all11loaded DLL hashes/timestamps remain unchanged. These five assets execute on one .NET10host, not five independent runtimes. The earlier [development checkpoint](obconnector-dotnet-completeness.md#1529-2026-10-25-mysql-过程开发与完整文档检查点) retains local TLS certificate-loading failures and partial results. The same built80-case consumer selection passes under the normal user environment without changing certificates or the driver; sandbox failures occurred during PKCS#12 private-key import before TLS/procedure exchange. These are local transport fixtures, not real TLS interoperability. A default-policy direct-server run passed the four procedure cases and missing-routine1305 case; a broader applicable MySQL run passed74/skipped5, with9configuration cases included. The original unfiltered run retains19Oracle-only no-data failures and one stale null-enlistment assertion, corrected to ArgumentNullException. Historical suite totals, the Oracle nested-table-column failure and cleanup failure below remain unchanged.

The tables distinguish recorded passes, failures, explicitly unsupported behavior and unverified combinations. Implemented APIs and passing local fixtures do not establish server compatibility.

Routine parameter derivation has a local implementation on all five assets: [API contract](obconnector-dotnet-architecture-design.md#routine-parameter-derivation) and [mapping limits](schema.md#parameter-derivation). It uses a unique visible routine identity, one reserved session/current transaction, per-query timeout, cancellation and atomic collection-membership publication. It does not execute the target or add MySQL StoredProcedure execution. Actual directory parsing, permissions, declarations, version and OBProxy acceptance remain unverified; local fixtures cannot establish them. The [development checkpoint](obconnector-dotnet-completeness.md#1517-2026-10-25-参数推导开发与完整文档检查点) retains earlier failures and is not the final delivery state.

User-SQL pool state classification now has a shared mode-aware path and a target-send boundary: [contract](obconnector-dotnet-architecture-design.md#pooled-session-state-after-user-sql). Commented/later statements and opaque calls retire the session; preparation and local rejection do not mark it as executed. MySQL quoted backslash/SQL_MODE ambiguity is conservative and can reduce reuse. This does not close arbitrary function/trigger effects, resource/fault/concurrency matrices or real reset acceptance; see P0-02 in the completeness baseline.

Opt-in [prepared statement caching](obconnector-dotnet-architecture-design.md#prepared-statement-cache) has a local implementation on all five assets. The idle LRU is per physical session; reset, observed DDL/session context and successful ChangeDatabase invalidate entries. Held statements are revalidated after acquiring the exclusive session lease. Active ownership and deferred closes remain exclusive; metadata probes and Oracle PrepareExecute keep their existing owners. External DDL/reprepare, server resources, OBProxy, load and independent runtimes remain unverified; synthetic prepare delays and packet counts are not real database performance evidence.

Oracle [complex-type DDL invalidation](obconnector-dotnet-architecture-design.md#complex-type-ddl-invalidation) has a local implementation: observed text CREATE/ALTER/DROP/RENAME/TRUNCATE target execution invalidates descriptions independently of statement caching. Prepare and pre-send rejection preserve them; reload types and rebuild bindings after invalidation. External or opaque DDL requires explicit `ClearComplexTypeCache`. Real DDL/OBProxy/implicit-commit/lock acceptance remains open.

## Server and topology matrix

| Target | Status | Boundary |
| --- | --- | --- |
| Direct OceanBase 4.4.2.2, MySQL mode | Partially verified | 2026-10-24 integration: 60 passed; TLS disabled. |
| Direct OceanBase 4.4.2.2, Oracle mode | Partially verified; suite fails | 2026-10-24 integration: 79 passed, 1 failed (nested-table column); TLS disabled. |
| Other OceanBase versions, either mode | Unverified on real servers | No exact-release allowlist. Open requires a parseable actual OceanBase version, a session mode matching authentication, and the required base protocol/authentication capabilities. Function-specific version/capability checks still apply. |
| OBProxy, either mode | Unverified | No verified proxy-version/routing/fault matrix. Direct-server results do not establish OBProxy compatibility. |
| Native MySQL / native Oracle | Unsupported | This provider targets OceanBase; MySQL wire compatibility does not bypass OceanBase identity confirmation. |

All successful opens confirm `ob_version()` and `@@ob_compatibility_mode` in one read-only query;
MySQL-mode opens also confirm `@@autocommit` in the same query and reject sessions with autocommit
disabled (see the unsupported table below). The reported mode must agree with authentication; missing, unknown or conflicting identity data
fails Open. Product versions are distinct from the MySQL-compatible greeting. A parseable new
release is not rejected merely because it has no recorded acceptance run, but an untested
release is not thereby supported or certified. No blanket minimum OceanBase release is claimed
from the available evidence. PrepareExecute >=2.2.76 and offset fetch >=2.2.5 are feature gates only.

Client behavior is independent of server version: `Treat Tiny As Boolean=true` maps signed MySQL
TINYINT(1) to Boolean (nonzero=true), and `Use Affected Rows=false` requests matched UPDATE rows.
Both can be changed explicitly; neither fabricates a server capability or selects a JDBC profile.
See the [API reference](obconnector-dotnet-architecture-design.md) for value and option rules.

## Recorded database tests

### 2026-10-24 integration suites

The current working tree was tested on Windows/.NET 10 against direct OceanBase 4.4.2.2 in both modes with TLS disabled, using .NET User Secrets. Oracle enabled PrepareExecute and LOB locators. Both modes supplied verified slow SQL for cancellation and command timeout.

| Mode | Passed | Failed |
| --- | ---: | ---: |
| MySQL | 60 | 0 |
| Oracle | 79 | 1 |

Five additional environment-dependent tests were skipped: unsupported authentication, disabled-autocommit login, alternate database, OB20 and trace. Nine configuration regression tests passed separately from the database cases. The raw run is `TestResults/review-20261024/direct-final.trx` (ignored local evidence).

Real testing identified two driver defects: MySQL identity confirmation rejected the BIGINT metadata returned for `@@autocommit`, and complex Oracle pure-output parameters sent a typed complex NULL instead of a protocol NULL placeholder. Both are fixed in this working tree. Collection procedure IN/OUT and IN/InputOutput round trips now pass; the input, response-type and session-generation checks remain enforced. The remaining nested-table-column failure is described below.

### 2026-10-21 acceptance console (historical)

The following results were recorded on 2026-10-21 for runtime-code baseline `304642e` with dependency updates, on Windows/.NET 10. Both profiles connected directly to OceanBase 4.4.2.2 with TLS disabled. Oracle enabled PrepareExecute and LOB locators; MySQL configured a slow query for cancellation and timeout tests.

| Mode | Passed | Failed | Skipped | Cleanup failures | Exit code |
| --- | ---: | ---: | ---: | ---: | ---: |
| MySQL | 38 | 0 | 27 | 0 | 0 |
| Oracle | 49 | 1 | 15 | 1 | 1 |

The Oracle suite did not pass. Skips include mode-specific and missing-environment cases; they are not passes. The original report paths were `TestResults/release-parity-{mysql,oracle}-02.json`. Those ignored reports are absent from this checkout, so these counts are retained from the [original verification record](https://github.com/cocosip/obconnector-dotnet/blob/f63d57c/docs/obconnector-dotnet-progress.md), not recomputed from reports or a new database run.

### Failed: Oracle nested-table columns

`complex.collection` fails at the second statement, before binding any collection value:

```sql
CREATE TYPE OBA_NUMBER_LIST AS TABLE OF NUMBER;
CREATE TABLE OBA_COLLECTION_TABLE (id INTEGER PRIMARY KEY, value OBA_NUMBER_LIST);
```

On the tested server, both .NET and Connector/J 2.4.14 returned error `600 / 0A000` (`nested table type not supported`). Java reproduced this with ordinary prepare and combined PrepareExecute. The pinned [server DDL resolver](https://github.com/oceanbase/oceanbase/blob/5e8a63d29dae9fde5e962b8fcc7b167d438fc343/src/sql/resolver/ddl/ob_ddl_resolver.cpp#L3930) also rejects nested-table column types.

This is a limitation of collection-valued table columns on that server. It does not establish a limitation on collection procedure parameters or array binding. The recorded `complex.object` and `oracle.array-binding` cases passed. Java's [collection example](https://github.com/oceanbase/obconnector-j/blob/6adc311ba5db8b2f19f98f3e6e4734667fdfdda1/src/test/java/com/oceanbase/jdbc/ComplexOracleTest.java#L411) uses a procedure parameter, a different operation.

The cleanup failure was an attempted DROP of the table whose CREATE failed. The original record reports independent cleanup finding no remaining owned objects or prepared XA branches. It does not cancel the failed case or change exit code 1. Original comparison/cleanup paths were `TestResults/release-parity-java-01.log`, `TestResults/release-parity-java-01-cleanup.log` and `TestResults/release-parity-{mysql,oracle}-02-cleanup.log`; these files are also absent from this checkout.

## Deferred acceptance cases

These six entries in [DatabaseCases.cs](../tests/OceanBaseConnector.Acceptance/DatabaseCases.cs) have explicit `DeferredReason` values. They report skips, not successful validation.

| Case | Missing validation / implementation |
| --- | --- |
| `matrix.tls-authentication` | Trusted certificate chains, hostnames, plugin combinations and negative authentication profiles. TLS-disabled acceptance does not validate VerifyFull. |
| `matrix.ob20-trace-negotiation` | Actual server OB20/full-link-trace negotiation and trace output. Ordinary SQL success does not prove either was negotiated. |
| `matrix.topology-failover` | Controlled discovery, endpoint failures and routing on real deployments. |
| `matrix.network-outcome-unknown` | Mid-request/response failures, cancellation ordering and interrupted commit/rollback on real deployments. |
| `matrix.pool-reset` | Local pooling/reset fixtures pass; real OceanBase/OBProxy reset behavior and cross-version validation remain open. |
| `matrix.xa-crash-recovery` | External coordinator recovery under client/server process failure. Basic XA phase tests do not cover this. |

Further skips depend on selected groups, compatibility mode, `slowQuery`, `alternateDatabase`, DDL permissions and Oracle extension options. If a connection prerequisite fails, dependent cases are skipped. See [AcceptanceSuite.SkipReason](../tests/OceanBaseConnector.Acceptance/AcceptanceSuite.cs); a zero exit code can still include skips.

## Explicitly unsupported APIs and shapes

| Area | Unsupported behavior |
| --- | --- |
| Pooling | Maximum capacity, wait timeout/cancellation, minimum-pool prewarming with failed-warmup rollback, borrow validation including MySQL autocommit status, idle/total lifetime reaping, prepared statement generation invalidation, unconsumed REF CURSOR/live LOB disposal, recognized user session-state or temporary-object discard, empty static-registry cleanup and pool lifecycle metrics are supported locally. Client certificates are supported with reference/fingerprint pool isolation and owned copies. ClearPool/ClearAllPools retire ordinary idle/borrowed sessions; private sources use Clear or ClearPool. Active work continues; old borrowed sessions still occupy capacity until closed. Lifetime reaping remains lazy. Full resource reset and server-specific reset interoperability are not yet validated. See [clearing contract](obconnector-dotnet-architecture-design.md#explicit-pool-clearing). |
| Pool routing identity | Ordinary pools now compare both connection string and resolver reference identity. Every logical Open resolves once and checks idle-session host/port membership against that snapshot, including minimum-pool prewarm. Removed endpoints are discarded before reuse; active borrowed sessions are not migrated. Resolver failure/cancellation cannot fall back to idle or configured endpoints. Data sources retain private pools. DNS IP changes behind an unchanged hostname and real topology/fault profiles remain unverified. See P0-06 in the [gap baseline](obconnector-dotnet-completeness.md). |
| Multi-statements | Disabled by default. `Allow Multi Statements=true` enables negotiated text multi-statements when the server advertises `CLIENT_MULTI_STATEMENTS`; parameters, Prepare, server cursors and Oracle output paths are rejected. Real OceanBase/OBProxy error and cancellation matrices remain unverified. |
| Oracle program units | Anonymous BEGIN/DECLARE blocks (including labels) and CREATE procedure/function/package/trigger/type units do not require multi-statement negotiation for internal semicolons. The complete program is sent for server parsing; real-server validation of program forms and invalid trailing SQL remains open. |
| LOCAL INFILE | Opt-in protocol path is implemented. `Allow Local Infile=true` advertises `CLIENT_LOCAL_FILES`; `LocalInfileStreamFactory` supplies the readable stream, and the driver never opens the server filename as a local path. Factory/read/cancel/final-response failures destroy the physical session. Real OceanBase/OBProxy `LOAD DATA LOCAL INFILE`, proxy policy, long-file and interruption behavior remain unverified. |
| Compression and OB20 extensions | Standard MySQL zlib compression is opt-in with `Use Compression=true`, switches after authentication and requires server support; unsupported servers fail before credentials. All five assets share bounded block framing and strict zlib validation. `Use Compression=true` with `Use OB20=true` is rejected before networking. OB20 compressed frames and proxy reroute remain unsupported; real OceanBase/OBProxy compression interoperability is unverified. |
| TLS/mTLS | `VerifyFull` uses platform validation by default; the optional `RemoteCertificateValidationCallback` replaces that decision and is supported on all assets, with a builder entry on modern data sources. Callbacks require VerifyFull and are isolated by reference identity in ordinary pools; they run only on new physical TLS sessions. `Required` encrypts without server identity validation. `ClientCertificate` supports pooled/direct connections; modern builder `UseClientCertificate` captures a source-owned snapshot even without pooling. Ordinary certificates must remain valid through Open, while source originals may be disposed after Build. `Check Certificate Revocation=true` enables platform revocation checks; the callback must enforce its intended handling of those errors. Local mTLS and policy isolation have loopback coverage; real enterprise CA/mTLS, rotation, revocation and cross-platform profiles remain unverified. See [client certificate contract](obconnector-dotnet-architecture-design.md#tls-client-certificates) and [server validation contract](obconnector-dotnet-architecture-design.md#tls-server-validation). |
| Decimal parameter declarations | Nullable OceanBaseParameter.DecimalScale supports explicit Oracle -84..127 and MySQL 0..30, with per-mode precision bounds and scale <= nonzero precision for MySQL. Standard Scale clears the override; a non-null override clears the old Scale and clearing it does not restore that overwritten declaration. Exact constraints reject rounding, typed NULL retains metadata validation and non-decimal parameters reject the override. Supported server/client prepare paths share the declaration. Local unit/five-asset wire evidence is recorded in the [module record](obconnector-dotnet-completeness.md#1516-2026-10-25-有符号-decimal-scale-本地交付); real NUMBER/DECIMAL declarations and version/OBProxy matrices remain unverified. Parameter derivation retains the signed declaration. |
| ADO.NET adapters and key metadata | `OceanBaseDataAdapter` and `OceanBaseCommandBuilder` have a local [fill/CRUD generation implementation](obconnector-dotnet-architecture-design.md#data-adapters-and-command-generation) on all five assets: synchronous sequential updates, mappings/events, caller transactions and optimistic concurrency. Automatic UPDATE/DELETE require a complete visible chosen row key; unknown shapes and context-dependent locator/complex/cursor bindings require explicit commands. Generated identity/default refresh is not automatic; real CRUD/locking/version/OBProxy/runtime acceptance remains open. `KeyInfo` has a bounded [source-key/hidden-column contract](obconnector-dotnet-architecture-design.md#key-information-and-hidden-columns): same-session pre-execution catalog snapshots, primary/nonnullable unique row keys and hidden tail columns only for proven single-base-table SELECTs. Nullable single-column UNIQUE is distinct from a nonnullable row key; composite members are not individually unique. Complex/ambiguous queries preserve SQL and shape with unknown keys, and use actual execution metadata. Real dictionary/permissions/DDL/version/OBProxy acceptance remains open. `SchemaOnly` uses server-prepare metadata for a single accepted text query with input parameters, never executing the target, uploading/encoding values or using client fallback. It reuses valid prepared metadata and ignores fetch/offset hints. Server preparation errors propagate; prepare-time metadata can differ from the executed result, and server parsing/metadata locks remain possible. Named complex metadata may require directory lookup. Local sync/async/five-asset regressions exist; real side-effect/lock/large-result acceptance remains open. `DbBatch` accepts only `CloseConnection` and `SequentialAccess`. |
| Disabled autocommit | MySQL-mode sessions whose `@@autocommit` is 0; Open fails because the provider cannot commit implicit transactions. Connector/J forces `SET autocommit=1` instead; this driver sets no session variables. |
| Ambient transactions | Local TransactionScope/EnlistTransaction uses the coordinator single-resource slot on one physical session. Second database resources and promotion are rejected; unknown COMMIT reports InDoubt, confirmed commit survives failed cleanup, and late callbacks cannot affect a reopened/pooled session. After successful enlistment, reopening after Close inside the unfinished scope remains unsupported; an Open whose initialization failed can be retried after the failed resource is released. Distributed atomic commit/recovery and conflicting ambient/local/XA combinations remain unsupported. Initialization is synchronous even for OpenAsync but observes the opening token; cancellation during a pending control response closes the failed connection, discards the session and releases the enlistment/pool slot. The token does not affect commit/rollback after successful Open. CommandTimeout remains a separate exchange budget; scope timeout does not immediately cancel the wire wait. Local/five-asset evidence is recorded in the latest module checkpoint; real OceanBase/OBProxy/independent-runtime acceptance remains open. See the [ambient contract](obconnector-dotnet-architecture-design.md#ambient-transactions). Explicit XA remains separate. |
| Standard assets | `DbDataSource`, `DbBatch`, savepoint APIs and GSSAPI require modern TFMs; complete cross-TFM behavior and real GSSAPI/KDC validation remain unverified. |
| Savepoints | Oracle release; quoted/Unicode names and leading underscores such as `__EFSavePoint`. |
| Binary stream parameters | InputOutput, text streams, array-binding combinations and client-prepare fallback. |
| Oracle outputs/cursors | Standalone Prepare or FetchSize on the PrepareExecute output path; REF CURSOR Input/InputOutput and ordinary cursor-valued result columns. |
| Complex types | Text-protocol complex results, cyclic layouts and LOB/cursor/national-type members; undeclared or stale type identities. |
| Array execution | SELECT, DDL and FetchSize; execution does not commit or roll back automatically. |
| Discovery and recovery | Built-in OCP/TNS discovery, transparent migration of active sessions/transactions and automatic SQL replay. |

For supported alternatives and binding rules, see the [API reference](obconnector-dotnet-architecture-design.md).

## Credential provision and rotation

Per-logical-Open sync/async acquisition, full password replacement, provider-reference pool isolation, version ordering, expiry and old-session retirement have local implementations; see the [credential contract](credentials.md). The [verification ledger](obconnector-dotnet-progress.md) records final module gates and earlier test-wiring failures. This is local protocol evidence, not a real credential-service or plugin compatibility claim.

Live-source version history survives Clear; empty ordinary registry eviction resets pool-local history. Providers own caching/refresh and are never disposed by the driver. Existing borrowers are not interrupted by rotation or login-credential expiry. Real credential services, token/plugin rotation, KDC, OBProxy and cross-platform behavior remain unverified; local native/PAM fixtures do not close that matrix.

## Catalog metadata

`GetSchema` provides a local implementation for 20 Oracle / 18 MySQL collections, including static DataTypes with final local unit and five-asset consumer evidence; see the [module record](obconnector-dotnet-completeness.md#1514-2026-10-25-datatypes-本地交付); the full [Schema contract](schema.md) lists shapes, restrictions and permission requirements. Types/Synonyms are explicitly unsupported on MySQL. Static DataTypes now reports canonical codec-supported declarations and CLR/provider representation variants, with explicit precision/scale, session/encoding/negotiation requirements and parameter/result limitations. It does not validate server DDL or add KeyInfo or a DataAdapter implementation. Automatic parameter derivation is a separate command API with the mapping limits described above. The previously committed dictionary SQL is checked against pinned OceanBase source, with protocol fixtures and five-asset catalog consumers; the final affected consumer run also executes all 16 DataTypes catalog cases on the .NET 10 host; actual dictionary parsing, privileges, collation, older versions and OBProxy still require real-server reports. The historical 2026-10-24 database passes predate this extension and do not validate it.

## Runtime and integration coverage

Local fixture and package checks cover different baselines and do not replace database validation. The integration suite (connection and identity gates: direct/OBProxy mode checks, TLS-name rejection, unsupported-plugin and autocommit-disabled rejection, plus the full functional suites) has not been established as passing; it runs only against a configured external database and is skipped everywhere else.

Full non-Windows deployment, every runtime implementing .NET Standard, real Kerberos/KDC credentials and a complete TLS/plugin matrix remain unverified. Historical .NET Framework checks cover basic consumption through a net48 host running installed Framework 4.8.1, not formal original-4.8 or full SQL/TLS support. See [verification status and remaining work](obconnector-dotnet-progress.md).
