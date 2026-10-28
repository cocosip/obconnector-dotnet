# Integration test suite

xUnit suites that exercise the driver's public ADO.NET surface against **real, externally provided OceanBase databases**. They are deliberately kept out of everyday workflows:

- CI never runs them (the workflow only compiles the project).
- A plain `dotnet test` or an IDE "run all tests" skips every database case with a visible reason and opens no socket. Configuration regression tests run without a database.
- Database cases run only after an explicit opt-in plus a configured database environment.

Real environment access supplied for development belongs only in these opt-in integration tests
or the acceptance console. Unit tests and CI must remain independent of those endpoints. Use
local .NET User Secrets or process-scoped environment variables; keep actual hosts, tenant/user
names, passwords and connection strings out of source, examples, documentation, commits and
shared logs/reports. Review diagnostic output before sharing it; never print resolved secrets.

This suite is the engineering counterpart of the [acceptance console](../OceanBaseConnector.Acceptance/README.md): the console produces curated release reports; these suites are what you filter, debug and re-run from an IDE or `dotnet test`.

The console now has separate same-client-channel reset/reuse and time-zone retirement cases.
Its ChannelId observation complements this suite's internal physical-session/generation assertions;
it does not prove proxy backend identity or replace the remaining resource/fault profiles. See the
[delivery record](../../docs/history/obconnector-dotnet-progress.md#2026-10-27-same-channel-pool-reset-and-reuse-acceptance).

## Ordinary Oracle cursor columns

A positive parent command FetchSize is accepted as a hint, but prepared cursor-valued columns
use cursorFlag=0 and stream the parent response without parent FETCH. Child cursor page sizes remain
independent. UseCursorOffset rejects this shape after final prepare metadata, before parameter upload
or target execution; this also applies to KeyInfo rewriting. SchemaOnly ignores these hints as before.
This follows the fixed Java source policy and does not prove real-server support for SELECT CURSOR.

Filter `FullyQualifiedName~PreparedCursorColumnSurvivesParentCommandDisposal` with an Oracle
prepare-execute profile. This new case explicitly prepares SELECT CURSOR, consumes the parent
SequentialAccess response, disposes the parent command, then fetches the returned cursor. `PrepareAsync`
succeeds, but the current Oracle tenant rejects execution of `SELECT CURSOR(SELECT 7 AS value FROM DUAL) AS c FROM DUAL`
with server error `902/HY000`; no parent row or child cursor is returned. The clean report records
15 passed and 1 failed case, while temporary diagnostics captured the server code before the test
diagnostic output was restored. Binary fixtures/consumers are separate evidence. Text cursor columns
have a local implementation; Input/InputOutput REF CURSOR remains unsupported. Real cursor-column
interoperability remains open until the server accepts this result shape.

## REF CURSOR result limits

Filter `FullyQualifiedName~ResultLimitsDrainCursorPagesAfterCommandDisposal` with an Oracle
prepare-execute profile. Twelve read-only cases per profile cover sync/async, SingleRow/SingleResult/
combined flags, empty/multiple rows, fetchSize1, parent disposal, drain then connection reuse, and
CloseConnection for empty cursors. No database objects or global configuration are changed.
The clean Oracle direct run has 15 passed and 1 failed. The 15 passing cases cover cursor output,
fetch-size paging, command disposal, connection reuse, and sync/async behavior; the one failure is
the `SELECT CURSOR(...)` result-column shape described above. Local fixture/consumer passes remain
separate from real interoperability. See [full evidence](../../docs/history/2026-10-28-oracle-resource-cursor-acceptance.md).

## Running

1. Opt in: set `OB_REQUIRE_INTEGRATION=1`. Anything else (or unset) skips database tests.
2. Provide profiles — the recommended configuration file references .NET User Secrets; the four legacy gate variables remain supported (see below).
3. Run:

```powershell
$env:OB_REQUIRE_INTEGRATION = '1'
dotnet test tests/OceanBaseConnector.IntegrationTests -c Release
```

Select a subset with `OB_INTEGRATION_PROFILES` (comma-separated names) or ordinary xunit filters, e.g. `--filter "Category=Integration"`, `--filter FullyQualifiedName~Transactions`. Every database case carries the trait `Category=Integration`; parser regressions carry `Category=Configuration`.

Misconfiguration fails loudly: with the opt-in set, a missing file, an unresolvable environment reference or an unknown profile name fails the run instead of silently passing.

## Oracle array execution FetchSize hints

Filter `FullyQualifiedName~ArrayBindingTests` with an Oracle `createObjects`/PrepareExecute
profile. Eight hint cases cover sync/async, implicit/explicit preparation and FetchSize 1 or
Int32.MaxValue. They check preparation has no write side effect, three actual inserted rows,
complete batch results, an unchanged hint, rollback and subsequent connection reuse.
Array DML does not page internal batch results. These cases require the integration opt-in;
they do not run in CI or require TLS/DBA permissions.

## Ambient timeout during an active command

Filter `FullyQualifiedName~AmbientActiveCommandTimeoutTests` with `createObjects` and the existing
`slowQuery` capability (SQL running longer than two seconds). The local transaction manager times
out after one second while that command is pending. Cases cover synchronous/asynchronous execution,
discarding the broken physical session, replacement in a capacity-one pool, and late disposal of the
old transaction/command while the new transaction remains uncommitted. The observer must see only
the new transaction's two committed rows. A command's own timeout or caller cancellation cannot
stand in for the manager interruption.

These tests require the explicit integration opt-in and are not run by CI. They need neither DBA
permissions nor TLS certificates; they use the selected profile's ordinary table-creation permission.

## Closed callback pool reuse

Filter `FullyQualifiedName~CloseCallbackPoolTests`. Each configured profile covers synchronous/asynchronous Close, committed implicit writes or an active local transaction, and reopening the same logical connection or a second borrower from one private capacity-one pool. The callback queries the real table after cleanup: implicit writes remain, while uncommitted writes have rolled back. The test also verifies physical-session identity and an increased reset generation. This does not replace topology, long-running load, fault injection or platform acceptance.

## Complex national member scope

Filter `FullyQualifiedName~ComplexNationalTests` on the configured Oracle profile. The suite contains 48 positive cases for NCHAR/NVARCHAR2, UTF8/UTF16 session national settings, sync/async, named objects/collections, nested members, ordinary object binary results and anonymous DML arrays. Eight additional cases assert the current rejection of complex whole-column RETURNING when response type identity cannot be resolved; these are boundary tests, not RETURNING compatibility passes. Random test objects are cleaned in dependency order; only session settings change.

The related `ComplexTypeTests.ObjectAndCollectionRoundTrip` loads its descriptor after table DDL because observed DDL invalidates earlier descriptions. Its NUMBER nested-table column case retains the recorded server rejection; do not remove that failure or infer table-column support from successful collection procedure parameters. [Full current evidence and retained failures](../../docs/history/obconnector-dotnet-completeness.md#1547-2026-10-25-复杂类型-national-成员与方向编码).

## Configuration file

Point `OB_INTEGRATION_CONFIG` at a JSON file, or place `integration-tests.json` (gitignored) next to this project; copy [integration-tests.example.json](integration-tests.example.json) as a starting point. Keep credentials in .NET User Secrets and reference their key names:

| Field | Meaning |
| --- | --- |
| `name` | Unique profile name used in test-case display and `OB_INTEGRATION_PROFILES` selection. |
| `mode` | Required: `MySql` or `Oracle`. Forced into `ExpectedMode`; conflicting strings are rejected. |
| `connectionString` / `connectionStringEnvironment` / `connectionStringSecret` | Exactly one: literal text, an environment variable name, or a .NET User Secrets key. User Secrets is recommended for credentials. |
| `tags` | Free-form labels. Suites keyed on `ob20` require that tag; `tls` tightens TLS-name assertions. |
| `options` | Optional connection-string overrides: `useOb20`, `enableFullLinkTrace`, `useOraclePrepareExecute`, `useLobLocators`, `treatTinyAsBoolean`, `useAffectedRows`, `loadBalance`, `tlsMode`. Cross-option rules (trace requires OB20, LOB locators require prepare execute) are validated at load. |
| `capabilities` | `createObjects` (allow DDL), `slowQuery` (SQL running > 2 s for cancel/timeout), `alternateDatabase` (MySQL ChangeDatabase), `unsupportedAuthEnvironment` / `autocommitDisabledEnvironment` (names of variables holding whole connection strings to special endpoints). Declared-but-empty environment capabilities are load errors. |

The integration test project has a `UserSecretsId`. Store connection strings outside the repository and reference them from the profile:

```powershell
dotnet user-secrets --project tests/OceanBaseConnector.IntegrationTests set "ConnectionStrings:MySql" "Server=...;User ID=...;Password=...;TLS Mode=Disabled"
dotnet user-secrets --project tests/OceanBaseConnector.IntegrationTests set "ConnectionStrings:Oracle" "Server=...;User ID=...;Password=...;TLS Mode=Disabled"
```

```json
{
  "name": "mysql-direct",
  "mode": "MySql",
  "connectionStringSecret": "ConnectionStrings:MySql"
}
```

Suites use profiles that satisfy their mode/capability requirements — e.g. LOB suites need Oracle mode plus prepare execute and LOB locator options; fault suites need `slowQuery`. Optional endpoint, slow-query, alternate-database and OB20/trace cases report a skip when no selected profile provides that capability.

### Legacy gate variables

Without a configuration file, `OB_REQUIRE_INTEGRATION=1` plus the four documented variables `OB_MYSQL_CONNECTION_STRING`, `OB_ORACLE_CONNECTION_STRING`, `OB_MYSQL_PROXY_CONNECTION_STRING`, `OB_ORACLE_PROXY_CONNECTION_STRING` synthesize the profiles `mysql-direct`, `oracle-direct`, `mysql-proxy`, `oracle-proxy` (all four must be present). These profiles run connection/identity suites only; object-creating suites need a configuration file with `createObjects`.

## Coverage

| Area | Suites (folder) |
| --- | --- |
| Open/close/reopen, identity vs `ob_version()`, factory/data source, ChangeDatabase, password hiding, address resolver, unreachable endpoint | `Connections/ConnectionTests` |
| ExpectedMode mismatch, TLS name, pooling, EnlistTransaction, unsupported auth plugin, autocommit-disabled rejection | `Connections/IdentityGateTests` |
| Fresh implicit writes visible before/after Close, same-session prepared/generation reuse, explicit state discard, transaction rollback then next implicit commit, unread-reader and Oracle active temporary-LOB discard, optional ChangeDatabase reset (sync/async) | `Connections/PoolIsolationTests` |
| Completed CloseConnection reader retained across same-physical-session pool reuse; old reader sync/async disposal preserves the new lease and real SQL | `Connections/PoolReaderLeaseTests` |
| Same-endpoint single-slot persistent BLOB/CLOB handle/stream phases, safe reset/reuse after disposal, actual large external locator full reads, stale resource rejection and independent exact-content observer; Oracle REF CURSOR four phases under opaque-call retirement (sync/async) | `Connections/PoolResourceIsolationTests` |
| Text vs prepared CRUD, MySQL client-side interpolation, command type/behavior gates, idle Cancel | `Commands` |
| Full provider type round-trips (text + prepared) per mode, TINYINT/affected-rows option matrix; Oracle unknown-precision prepared bindings and real negative-scale table/CAST results | `Types` |
| SequentialAccess streaming, cursor fetch paging (incl. Oracle offset), CloseConnection, column schema | `Readers` |
| Commit/rollback/dispose semantics, savepoints + validation, isolation levels per mode, concurrent-tx gate | `Transactions` |
| Multi-result batches, parameterized batch commands + Prepare, per-command RecordsAffected | `Batches` |
| Mode-advertised schema collections sync/async, restriction filtering, composite key/index/FK ordering, views, Oracle types/synonyms and sequence non-advancement | `Schema` |
| Single-slot pool XA dedication after idle/commit/rollback, replacement autocommit/local transaction, prepared branch Close then explicit recovery/commit/rollback (sync/async) | `Connections/PoolXaIsolationTests` |
| XA lifecycle (prepare/recover/commit/rollback), one-phase, invalid phase transitions | `Xa` |
| PL/SQL OUT/InputOutput, RETURNING INTO, stored procedures + schema, REF CURSOR, positive output FetchSize hints with implicit/explicit Prepare and independent cursor page size, temporary LOBs + locator params, complex types (including collection IN/OUT/InputOutput procedure parameters), array binding | `Oracle` |
| Large binary streaming (LONG DATA / LOB locator), Oracle piece-wise upload | `Streams` |
| Known server error reuse, cancellation, CommandTimeout; real COMMIT/ROLLBACK acknowledgement loss through a test-only plaintext relay, independent reconciliation and single-slot pool replacement | `Faults` |
| ActivitySource, meters, ILogger events | `Diagnostics` |
| SQL under OB20 framing, full-link trace | `Ob20` (needs tagged profiles) |
| Requested standard MySQL compression with fallback to ordinary framing | `Protocol/CompressionTests` |

The previous resource module records 28 new direct Oracle cases (twenty LOB, eight cursor), plus the related two-mode Connections/XA/REF CURSOR/LOB regression: 115 passed, 4 configuration skips. All twenty stored LOB locators were inline; these runs do not cover external locator network chunks/cancellation or other deployment profiles. No-opt-in results are 9 configuration passes and 98 NotExecuted. See the [exact acceptance evidence and retained fixture failures](../../docs/history/obconnector-dotnet-completeness.md#1543-2026-10-25-持久-lob-与-ref-cursor-的同端点池隔离验收).

External persistent LOB acceptance adds four direct Oracle cases: actual inline=False BLOB100000/CLOB220000 UTF8-byte locators, exact sync/async content and healthy same-physical-session reuse. Preparation uses an independent unpooled temporary seed, freed before the pooled SELECT; no global threshold change. Final related regression119pass/4configuration skips, noopt9pass/99NotExecuted. Local eighteen in-flight interruption cases and two cleanup regressions are unit evidence; real mid-read cancellation/fault and other deployment profiles remain open. See [full external acceptance evidence](../../docs/history/obconnector-dotnet-completeness.md#1544-2026-10-25-外置持久-lob-读取与池生命周期验收).

The standard compression integration case clones each selected non-OB20 profile with `Use Compression=true` and runs sync/async `SELECT 1`. The supplied direct MySQL and Oracle tenants do not advertise `CLIENT_COMPRESS`; both therefore exercise Java-compatible fallback to ordinary framing and pass. This does not establish compressed-frame, OBProxy, TLS or cross-platform interoperability.

Transaction acknowledgement-loss acceptance adds sixteen direct cases (both modes/sync-async/commit-rollback/none-partial), restricted to existing TLSdisabled/noncompressed/nonOB20/single-endpoint profiles. The test relay reads a real server reply before withholding it; an unpooled observer verifies actual outcome and a Max1 fresh lease works before Clear. Its four ordinary fixture checks run without database opt-in. Expanded related suite160pass/4configuration skips INCLUDES those4local checks; noopt13pass/100NotExecuted. This does not prove server/coordinator crash or real OBProxy/TLS failure. [Full evidence](../../docs/history/obconnector-dotnet-completeness.md#1545-2026-10-25-事务响应丢失与池恢复验收), [application reconciliation](../../docs/unknown-outcome-recovery.md).

Oracle output FetchSize acceptance adds twenty direct Oracle cases: sync/async × implicit/explicit preparation × block/RETURNING/procedure/function/REF CURSOR. Internal outputs do not issue FETCH; returned three-row cursors use page1 with parent hint2 and survive command disposal. Final related suite58pass/0skip includes both-mode ordinary cursor and Oracleoffset regressions; noopt13pass/101NotExecuted. No output-result pagination or new input/column cursor shapes are claimed. See [exact evidence and retained24RED](../../docs/history/obconnector-dotnet-completeness.md#1546-2026-10-25-oracle-输出执行与-fetchsize-提示).

## Isolation and cleanup

Each test owns a `DatabaseContext` that names created objects `OBI_<random>` and drops them LIFO on dispose (DDL + temporary LOBs + XA branches). A cleanup failure fails the test even if its body passed. Interrupted runs can leave `OBI_` objects or prepared XA branches behind; target those names when cleaning up manually. The assembly runs sequentially (`DisableTestParallelization`) because several suites mutate session state.

## Local database

Any reachable OceanBase works. For a quick MySQL-mode tenant on a developer machine, OceanBase CE's Docker image can be used (`oceanbase/oceanbase-ce`, default port 2881); the suite itself has no Docker dependency — configure a profile pointing at wherever your database lives.
