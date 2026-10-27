# Database acceptance console

Runs database tests through the driver's public APIs. Requires the .NET 10 SDK and a dedicated test database or tenant. Recorded results are in [Compatibility](../../docs/compatibility.md).

## Run

From the repository root, list cases without connecting:

```powershell
dotnet run --project tests/OceanBaseConnector.Acceptance -c Release -- --list
```

Set credentials through an environment variable. The examples below require a server certificate trusted by the client.

Oracle:

```powershell
$env:OB_ACCEPTANCE_CONNECTION_STRING = 'Server=db.example;Port=2881;User ID=test_user@tenant;Password=replace-me;Use Oracle Prepare Execute=true;Use Lob Locators=true'
dotnet run --project tests/OceanBaseConnector.Acceptance -c Release -- --config tests/OceanBaseConnector.Acceptance/oracle.example.json --report TestResults/oracle-acceptance.json
```

MySQL mode:

```powershell
$env:OB_ACCEPTANCE_CONNECTION_STRING = 'Server=db.example;Port=2881;User ID=test_user@tenant;Password=replace-me;Database=test_db'
dotnet run --project tests/OceanBaseConnector.Acceptance -c Release -- --config tests/OceanBaseConnector.Acceptance/mysql.example.json --report TestResults/mysql-acceptance.json
```

The report path must be new. Omit `--report` to generate a unique path under `TestResults/`.
Supply real development environments only to this explicit acceptance run or the opt-in integration
suite, never to unit tests or CI. Keep actual endpoints, tenant/user names, credentials and resolved
connection strings in local secret configuration or process-scoped environment variables; do not
copy them into repository files or shared logs/reports. Never print or commit resolved secrets.

## Configuration

| Field | Meaning |
| --- | --- |
| `mode` | Required: `Oracle` or `MySql`. Must match `ExpectedMode` if supplied. |
| `connectionString` / `connectionStringEnvironment` | Set exactly one: connection text or the name of an environment variable. |
| `groups` | Empty or omitted selects all groups; use `--list` for names. |
| `allowCreateObjects` | Default false. Enables tests that create and drop schema objects; both example files set true. |
| `zeroDateProfile` | Default false. Explicitly enables MySQL `types.zero-date-policy`, also requiring `allowCreateObjects=true` and a session SQL_MODE without NO_ZERO_DATE/NO_ZERO_IN_DATE. The runner never changes SQL_MODE. |
| `networkFaultProfile` | Default false. Explicitly enables 16 controlled write/transaction network fault cases, requiring `allowCreateObjects=true`, a single plain DNS/IPv4 host with separate Port, TLS disabled, no compression and no OB20. Incompatible profiles skip without downgrade. |
| `xaCrashProfile` | Default false. Explicitly enables four XA coordinator process crash/restart cases. Requires `allowCreateObjects=true`, `dotnet` on PATH, and Oracle PrepareExecute in Oracle mode. Uses only owned branches/tables and a decision journal containing no credentials. |
| `topologyFaultProfile` | Default false. Enables six read-only controlled client-route cases: idle refresh, borrowed-session isolation and refused-TCP fallback, sync/async. Single plain host, TLS disabled, no compression/OB20; no DDL. This does not prove real cluster HA. |
| `ob20TraceProfile` | Default omitted. Selects `ob20`, `trace-legacy`, `trace-modern`, `declined-ob20` or `declined-trace`; only the matching sync/async cases run. Requires OB20, no compression, and Full Link Trace requested except for `ob20`. Keeps TLS settings; no DDL. See the [assertions and trace limits](../../docs/ob20-trace-acceptance.md). |
| `largeValueBytes` | Binary round-trip size, default 65536; range 1–1048576. |
| `slowQuery` | Optional read-only SQL taking over two seconds, for cancellation and timeout tests. Omitted means those cases are skipped. |
| `alternateDatabase` | Optional MySQL database for change-and-restore testing. Omitted means skipped. |

Use positive connection and command timeouts. The console sets `ExpectedMode` from configuration and enforces the driver's identity contract. Every Open confirms the actual product version and session mode; it does not enforce an exact tested-release allowlist. Oracle extension cases are skipped when their options are disabled; errors after enabling them count as failures.

## Coverage and cleanup

Tests cover connections, CRUD, types, transactions, readers, batches, schema metadata, binary streams, Oracle outputs/cursors/array binding/LOBs/complex types, XA and diagnostics. Availability depends on mode and configuration; `--list` shows individual cases.

Connection cases compare `ServerVersion` and schema product-version metadata with `ob_version()`.
`types.mysql-client-behavior` checks both TINYINT Boolean/integer mappings and both UPDATE
row-count settings through real text/prepared sequential readers, including NULL and negative values.

One `matrix.*` entry remains deferred: TLS/authentication.
Ten `matrix.ob20-trace-negotiation.*` cases have explicit profiles (two execute per selection).
They require one matching negotiation, successful read-only controls and actual bidirectional
command frames; active trace profiles also require outgoing extra-info with the selected format.
Missing server sampling/control fails an explicitly enabled trace profile. The cases use fresh
unpooled connections, retain no payload/trace IDs and leave TLS unchanged. Local synthetic checks
do not prove real proxy interoperability or collector delivery; see the [contract](../../docs/ob20-trace-acceptance.md).
Six opt-in `matrix.topology-failover.*` cases use transparent loopback relays to one upstream,
require a fresh resolver snapshot per Open, prove unchanged-route reuse, removed-idle retirement,
borrowed-session isolation and TCP fallback after a refused-only negative control. Only fixed
read-only SQL is issued; no schema objects or cluster settings change. Both direct4.4.2.2 modes
pass6/6 plus connection control on Windows/.NET10.0.13 TLSdisabled; this does not prove
independent-node/discovery-service/proxy HA. See the [controlled topology contract](../../docs/topology-acceptance.md).
The four opt-in `matrix.xa-crash-recovery.*` cases kill a live prepared coordinator process
after its decision is flushed, then restart another process to recover that decision. Both
direct4.4.2.2 modes pass4/4 plus connection control, with exact owned cleanup. General coordinator,
server failure and other crash points remain open; see the [process recovery contract](../../docs/xa-process-recovery-acceptance.md).
The 16 opt-in `matrix.network-outcome-unknown.*` cases inject INSERT request/response and
COMMIT/ROLLBACK response loss, then check unknown outcome, broken-channel replacement,
independent reconciliation, no replay and fresh transaction recovery. Both direct 4.4.2.2
modes passed 16/16 plus connection control; all owned tables were removed. This does not cover
provider-local pre-send, server execution interruption, cancellation ordering, real proxy/TLS
or coordinator crash recovery. See the [fault boundaries and cleanup contract](../../docs/network-outcome-acceptance.md).
`types.zero-date-policy` is executable with the explicit `zeroDateProfile` opt-in. It checks
the actual session SQL_MODE before DDL; an enabled incompatible profile fails with no object
creation. It stores normal/partial-zero/all-zero/NULL DATE and DATETIME(6), verifies text and
prepared results, requires standard DateTime access to reject zero fields, and writes the
provider-specific values back for another text/prepared check. It creates one owned random
table and drops it through normal cleanup. Default/example configurations skip this case.
The direct 4.4.2.2 profile checked on Windows/.NET 10.0.13, TLS disabled, rejected the SQL_MODE
precondition; no table was created and real zero-date storage remains unverified.
`matrix.pool-reset-reuse.sync/async` require the same client protocol channel across two borrowers
and reopening the original prepared command. They observe public Frame events using listener
identity plus local ChannelId, and reject successful reconnection as proof of reuse. No payloads
are captured. Private one-session pools and diagnostic subscriptions are disposed on exit.
`matrix.pool-reset` separately changes the time zone through SET/ALTER SESSION, which retires
the changed session; it checks isolation by replacement. Numeric offsets avoid dependence on
server region files. These cases plus the connection check passed 4/4 per mode on direct 4.4.2.2,
Windows/.NET 10.0.13, TLS disabled. ChannelId is not a server/backend session ID; proxy/backend,
other versions/platforms/TLS and complete resource behavior still need validation.
Mode/configuration skips are additional. See [Compatibility](../../docs/compatibility.md) for the
failed Oracle case and deployment restrictions, and [verification status](../../docs/obconnector-dotnet-progress.md)
for retained test results and environment gates.

The console writes data and commits some transactions. Schema objects use random `OBA_` names. Cleanup targets exact owned object names and XA identities; failures remain in the report. Disabling schema-object creation does not disable temporary LOB mutations. Forced termination can leave objects or prepared XA branches requiring manual cleanup.

## Reports

JSON reports include server/runtime identity, case results and cleanup state. They omit credentials, raw SQL, parameter data and server error text; database codes and SQLSTATE remain available.

| Exit code | Meaning |
| --- | --- |
| 0 | Selected applicable cases passed; inspect skips separately. |
| 1 | Case or cleanup failure. |
| 2 | Configuration/report error or no passed cases. |
| 130 | Caller cancellation. |

Ctrl+C cancels execution and attempts cleanup before writing the report. The recorded Oracle nested-table-column failure remains a failure; see [Compatibility](../../docs/compatibility.md) for the Java comparison. Successful ordinary SQL does not validate OBProxy, trusted TLS/authentication combinations, network faults or coordinator crash recovery.
