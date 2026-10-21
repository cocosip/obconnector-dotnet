# Database acceptance console

This standalone console connects directly to OceanBase Server or OBProxy through the driver's public API. It does not simulate a database and is not included in the driver NuGet packages. It requires the .NET 10 SDK. Its 65 named entries run according to mode, configuration, and permissions; other entries report explicit skip reasons. Real OceanBase 4.4.2.2 MySQL and Oracle tenant runs are recorded in the [progress ledger](../../docs/obconnector-dotnet-progress.md). Those results cover the tested profiles; complete interoperability acceptance remains open.

## Configure and run

Run from the repository root. List all cases without reading credentials or accessing the network:

```powershell
dotnet run --project tests/OceanBaseConnector.Acceptance -c Release -- --list
```

After a Release build, Windows users can also run `tests/OceanBaseConnector.Acceptance/bin/Release/net10.0/OceanBaseConnector.Acceptance.exe` with the same arguments and the .NET 10 runtime. Keep the adjacent runtime configuration and dependency files.

Specify exactly one of `connectionString` in JSON or `connectionStringEnvironment`, the name of an environment variable. Prefer the environment variable and never commit real credentials. The supplied `oracle.example.json` and `mysql.example.json` use mode values `Oracle` and `MySql`, respectively. The mode must agree with `ExpectedMode` in the connection string.

Oracle example (replace the endpoint and credentials; configure trusted TLS for your environment):

```powershell
$env:OB_ACCEPTANCE_CONNECTION_STRING = 'Server=db.example;Port=2881;User ID=test_user@tenant;Password=replace-me;TLS Mode=VerifyFull;Use Oracle Prepare Execute=true;Use Lob Locators=true'
dotnet run --project tests/OceanBaseConnector.Acceptance -c Release -- --config tests/OceanBaseConnector.Acceptance/oracle.example.json --report TestResults/oracle-acceptance.json
```

MySQL-mode example:

```powershell
$env:OB_ACCEPTANCE_CONNECTION_STRING = 'Server=db.example;Port=2881;User ID=test_user@tenant;Password=replace-me;Database=test_db;TLS Mode=VerifyFull'
dotnet run --project tests/OceanBaseConnector.Acceptance -c Release -- --config tests/OceanBaseConnector.Acceptance/mysql.example.json --report TestResults/mysql-acceptance.json
```

For MySQL mode or a greeting that does not identify OceanBase, the driver requires a read-only `ob_version()` confirmation. This identity confirmation currently accepts only OceanBase 4.4.2.2; native MySQL and unverified versions are rejected by that path. The console does not bypass mode identification. Oracle extension cases are skipped when their options are disabled. Once enabled, version, permission, or unsupported-server errors are failures.

## Configuration fields

| Field | Meaning |
| --- | --- |
| `mode` | Required: `Oracle` or `MySql`. |
| `connectionString` / `connectionStringEnvironment` | Exactly one. TLS, authentication, OB20, tracing, and Oracle extensions use existing driver connection-string options. |
| `groups` | Defaults to `[]`, selecting all groups. For example, `["connection", "crud", "types", "transactions"]` selects a subset. See `--list`; unknown groups are configuration errors. |
| `allowCreateObjects` | Defaults to false. Set true to create and drop randomly named test tables, procedures, and types. Both examples set true. |
| `largeValueBytes` | Defaults to 65536; range 1–1048576. Binary stream round-trip length. |
| `slowQuery` | Optional read-only SQL supplied by the operator that takes more than two seconds, for caller cancellation at 500 ms and command timeout at 1 s. Both cases are skipped when omitted. The SQL is not printed. |
| `alternateDatabase` | Optional MySQL database for a change-and-restore test; omitted means skipped. |

Use a dedicated test database/tenant with the necessary create and drop permissions. The console writes test data and commits some test transactions. It creates only objects named `OBA_` plus a random identifier and does not access existing business tables. Each case uses separate connections and cleans up exact owned names. Reports include object/XID identities and cleanup states; cleanup failures preserve the original failure and produce a nonzero exit code. Cases requiring schema DDL are skipped without permission in configuration. Enabled temporary LOB cases still create, write, and explicitly free temporary LOBs.

## Coverage

| Group | Assertions |
| --- | --- |
| connection | Sync/async open, close, reopen, actual mode/version, factory/data source, optional database switching. |
| crud | Text INSERT, parameters and explicit prepare, repeated execution, NULL/Unicode, result ordering, matched UPDATE counts, DELETE. |
| types | Text/prepared integer, decimal38, floating point, string, binary, date/time; MySQL unsigned/YEAR/BIT/JSON/TIME; Oracle national types/INTERVAL/nanosecond TIMESTAMP/TZ/LTZ. |
| transactions | Sync/async commit, rollback, disposal rollback, cross-connection commit visibility, savepoints, and MySQL savepoint release. |
| reader | Column metadata, SequentialAccess character/binary streams, CloseConnection, cursor pagination, and Oracle offset Fetch. |
| batch / schema | DbBatch result order; six GetSchema collections through sync/async APIs; discovery of created tables/columns and procedures/parameters. |
| streams | Sync/async binary upload, source ownership, complete byte comparison; Oracle PrepareExecute with OUT is in the oracle group. |
| oracle | Scalar OUT/InputOutput, RETURNING, StoredProcedure, REF CURSOR after parent-command disposal, array binding, and transaction rollback. |
| lob / complex | Temporary BLOB/CLOB create/write/truncate/read/free; named OBJECT/collection dictionary and parameter/result round trips, cache clearing. Named complex result reads use binary protocol. |
| xa | Start/End/Prepare for a generated XID, Recover through a new connection, Commit/Rollback, and data outcomes. Failure cleanup attempts End, close, and rollback of that exact owned XID; other branches are untouched. |
| diagnostics / faults | Activity/Meter, connection reuse after a trusted server ERR, optional cancellation/timeout; other environment matrices remain named skips. |

Successful ordinary SQL proves only execution under the selected configuration. Authentication/certificate negative profiles, actual OB20 negotiation/server tracing, external discovery/failover, partial-request faults and unknown outcomes, pool isolation, and XA coordinator crash recovery need dedicated environments or fault injection and remain explicit skips. MySQL zero dates require a separate SQL_MODE profile; the console does not relax SQL_MODE. The 65 entries do not cover every mode/version/topology/authentication combination or every extended output shape.

## Results

The 2026-10-21 rerun against direct OceanBase 4.4.2.2 with runtime-code baseline `304642e` and the current dependency updates returned MySQL **38 passed / 0 failed / 27 skipped**, and Oracle **49 passed / 1 failed / 15 skipped / 1 cleanup callback failure**. Oracle retains exit code 1. The unchanged `complex.collection` case creates a column of a named `TABLE OF NUMBER` type; that CREATE TABLE fails before any collection binding. Fresh Connector/J 2.4.14 ordinary/combined-PrepareExecute comparisons return the same 600 / 0A000 error, and the public server DDL resolver explicitly rejects nested-table column types. This does not establish a general collection-parameter limitation. See [verified compatibility and Java comparison](../../docs/compatibility.md) for the exact scope, sources and evidence.

The callback failure is DROP of the table whose CREATE failed. Independent inventory found no remaining owned test objects or prepared XA branches in either mode or the Java comparison. Keep the original report and nonzero exit status; do not turn this failure into a pass or unconditional skip. These TLS Disabled runs do not establish trusted-TLS, OBProxy, topology/fault or coordinator-crash compatibility.

The console prints `PASSED`, `FAILED`, `SKIPPED`, or `CANCELLED` for each entry and a summary. JSON reports include driver/runtime and observed server versions/modes, timings, results, and cleanup states. They omit connection strings, passwords, parameter data, raw SQL, server error text, and stack traces. Failures retain exception types, database codes, SQLSTATE, and the unknown-outcome flag.

Exit codes: 0 = selected applicable cases passed; 1 = case or cleanup failure; 2 = configuration/report error or no passed cases; 130 = caller cancellation. **Exit 0 can include skipped cases: inspect `Skipped` and its reasons.** Ctrl+C cancels execution, continues cleanup using finite connection/command timeouts, and writes the report. The report is written after execution and cleanup. Force termination can leave an empty/incomplete report and owned objects or XA branches behind; inspect the dedicated schema and coordinator state manually.

The `--report` path must not exist; existing reports are never overwritten. When omitted, a new `TestResults/acceptance-timestamp-random.json` is created and its path printed. Set positive `Connection Timeout` and `Command Timeout` values to keep cleanup finite. The console rejects unimplemented pooling. Configuration failure does not start database cases.
