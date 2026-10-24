# Compatibility and known gaps

The tables distinguish recorded passes, failures, explicitly unsupported behavior and unverified combinations. Implemented APIs and passing local fixtures do not establish server compatibility.

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
| Pooling | Maximum capacity, wait timeout/cancellation, minimum-pool prewarming with failed-warmup rollback, borrow validation including MySQL autocommit status, idle/total lifetime reaping, prepared statement generation invalidation, unconsumed REF CURSOR and live LOB handle return-time disposal, empty static-registry cleanup and pool lifecycle metrics are supported locally. Full resource reset and server-specific reset interoperability are not yet validated. |
| Disabled autocommit | MySQL-mode sessions whose `@@autocommit` is 0; Open fails because the provider cannot commit implicit transactions. Connector/J forces `SET autocommit=1` instead; this driver sets no session variables. |
| Ambient transactions | `TransactionScope` / `EnlistTransaction`; explicit XA is a separate API. |
| Standard assets | `DbDataSource`, `DbBatch`, savepoint APIs and GSSAPI require modern TFMs; complete cross-TFM behavior and real GSSAPI/KDC validation remain unverified. |
| Savepoints | Oracle release; quoted/Unicode names and leading underscores such as `__EFSavePoint`. |
| Binary stream parameters | InputOutput, text streams, array-binding combinations and client-prepare fallback. |
| Oracle outputs/cursors | Standalone Prepare or FetchSize on the PrepareExecute output path; REF CURSOR Input/InputOutput and ordinary cursor-valued result columns. |
| Complex types | Text-protocol complex results, cyclic layouts and LOB/cursor/national-type members; undeclared or stale type identities. |
| Array execution | SELECT, DDL and FetchSize; execution does not commit or roll back automatically. |
| Discovery and recovery | Built-in OCP/TNS discovery, transparent migration of active sessions/transactions and automatic SQL replay. |

For supported alternatives and binding rules, see the [API reference](obconnector-dotnet-architecture-design.md).

## Runtime and integration coverage

Local fixture and package checks cover different baselines and do not replace database validation. The integration suite (connection and identity gates: direct/OBProxy mode checks, TLS-name rejection, unsupported-plugin and autocommit-disabled rejection, plus the full functional suites) has not been established as passing; it runs only against a configured external database and is skipped everywhere else.

Full non-Windows deployment, every runtime implementing .NET Standard, real Kerberos/KDC credentials and a complete TLS/plugin matrix remain unverified. Historical .NET Framework checks cover basic consumption through a net48 host running installed Framework 4.8.1, not formal original-4.8 or full SQL/TLS support. See [verification status and remaining work](obconnector-dotnet-progress.md).
