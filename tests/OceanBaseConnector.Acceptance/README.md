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

The report path must be new. Omit `--report` to generate a unique path under `TestResults/`. Never commit credentials.

## Configuration

| Field | Meaning |
| --- | --- |
| `mode` | Required: `Oracle` or `MySql`. Must match `ExpectedMode` if supplied. |
| `connectionString` / `connectionStringEnvironment` | Set exactly one: connection text or the name of an environment variable. |
| `groups` | Empty or omitted selects all groups; use `--list` for names. |
| `allowCreateObjects` | Default false. Enables tests that create and drop schema objects; both example files set true. |
| `largeValueBytes` | Binary round-trip size, default 65536; range 1–1048576. |
| `slowQuery` | Optional read-only SQL taking over two seconds, for cancellation and timeout tests. Omitted means those cases are skipped. |
| `alternateDatabase` | Optional MySQL database for change-and-restore testing. Omitted means skipped. |

Use positive connection and command timeouts. The console sets `ExpectedMode` from configuration and enforces the driver's identity contract. Every Open confirms the actual product version and session mode; it does not enforce an exact tested-release allowlist. Oracle extension cases are skipped when their options are disabled; errors after enabling them count as failures.

## Coverage and cleanup

Tests cover connections, CRUD, types, transactions, readers, batches, schema metadata, binary streams, Oracle outputs/cursors/array binding/LOBs/complex types, XA and diagnostics. Availability depends on mode and configuration; `--list` shows individual cases.

Connection cases compare `ServerVersion` and schema product-version metadata with `ob_version()`.
`types.mysql-client-behavior` checks both TINYINT Boolean/integer mappings and both UPDATE
row-count settings through real text/prepared sequential readers, including NULL and negative values.

Five `matrix.*` entries are explicitly deferred and report skips: TLS/authentication, OB20/tracing negotiation, topology failover, interrupted network outcomes and XA crash recovery. `matrix.pool-reset` now runs a real MySQL/Oracle pooled session-time-zone reset check. Mode/configuration skips are separate. See [Compatibility](../../docs/compatibility.md) for the failed Oracle case and server-version restrictions, and [verification status](../../docs/obconnector-dotnet-progress.md) for the four separate skipped integration gates.

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
