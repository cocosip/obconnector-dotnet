# Integration test suite

xUnit suites that exercise the driver's public ADO.NET surface against **real, externally provided OceanBase databases**. They are deliberately kept out of everyday workflows:

- CI never runs them (the workflow only compiles the project).
- A plain `dotnet test` or an IDE "run all tests" skips every database case with a visible reason and opens no socket. Configuration regression tests run without a database.
- Database cases run only after an explicit opt-in plus a configured database environment.

This suite is the engineering counterpart of the [acceptance console](../OceanBaseConnector.Acceptance/README.md): the console produces curated release reports; these suites are what you filter, debug and re-run from an IDE or `dotnet test`.

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
| Text vs prepared CRUD, MySQL client-side interpolation, command type/behavior gates, idle Cancel | `Commands` |
| Full provider type round-trips (text + prepared) per mode, TINYINT/affected-rows option matrix | `Types` |
| SequentialAccess streaming, cursor fetch paging (incl. Oracle offset), CloseConnection, column schema | `Readers` |
| Commit/rollback/dispose semantics, savepoints + validation, isolation levels per mode, concurrent-tx gate | `Transactions` |
| Multi-result batches, parameterized batch commands + Prepare, per-command RecordsAffected | `Batches` |
| Six schema collections sync/async, restriction filtering | `Schema` |
| XA lifecycle (prepare/recover/commit/rollback), one-phase, invalid phase transitions | `Xa` |
| PL/SQL OUT/InputOutput, RETURNING INTO, stored procedures + schema, REF CURSOR, temporary LOBs + locator params, complex types (including collection IN/OUT/InputOutput procedure parameters), array binding | `Oracle` |
| Large binary streaming (LONG DATA / LOB locator), Oracle piece-wise upload | `Streams` |
| Known server error reuse, cancellation, CommandTimeout | `Faults` |
| ActivitySource, meters, ILogger events | `Diagnostics` |
| SQL under OB20 framing, full-link trace | `Ob20` (needs tagged profiles) |

## Isolation and cleanup

Each test owns a `DatabaseContext` that names created objects `OBI_<random>` and drops them LIFO on dispose (DDL + temporary LOBs + XA branches). A cleanup failure fails the test even if its body passed. Interrupted runs can leave `OBI_` objects or prepared XA branches behind; target those names when cleaning up manually. The assembly runs sequentially (`DisableTestParallelization`) because several suites mutate session state.

## Local database

Any reachable OceanBase works. For a quick MySQL-mode tenant on a developer machine, OceanBase CE's Docker image can be used (`oceanbase/oceanbase-ce`, default port 2881); the suite itself has no Docker dependency — configure a profile pointing at wherever your database lives.
