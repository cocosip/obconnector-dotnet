# Failure Handling and Recovery

The driver never hides or automatically repairs a failed database operation. It classifies what is known,
destroys sessions whose state cannot be trusted, and leaves reconciliation to the application. This page
documents that contract, the application verification pattern, and the opt-in acceptance harnesses that
prove the classified outcomes.

<a id="unknown-outcome"></a>

## Unknown outcome and application verification

`OceanBaseException.IsExecutionOutcomeUnknown=true` means the request may already have executed and the
client did not receive enough final confirmation. It does not mean execution failed or was rolled back. A
network drop, a timeout, a cancellation, and a lost COMMIT/ROLLBACK response packet can all lead to this
state; cancelling the caller's token also cannot undo a server operation that has already completed.

The driver does not automatically replay such SQL. A failed transport is destroyed and the transaction
becomes invalid; the pool releases capacity and allows a new lease, without offering transaction migration.
A further Commit/Rollback on the old transaction is rejected; Dispose is not a compensating operation
either. The recovery protocols for explicit local transactions and for ambient/XA are independent; only
ordinary local transactions are discussed below.

The built-in TNS/OCP global/per-group blacklist and the shared TCP attempt ceiling apply only to new
physical connections; a zero retry count can reuse a verified idle connection and skip the minimum-pool
build-up, and with no idle connection it fails with zero TCP attempts. Once TCP succeeds, candidates are not
continued for TLS/authentication/identity errors, and already-sent business operations are certainly not
replayed nor old transactions restored. Policy and pool-generation isolation, the single fallback, and the
shared ceiling are described in the
[contract](obconnector-dotnet-architecture-design.md#discovery-blacklist-and-attempt-ceiling).

Built-in TNS/OCP discovery retains groups, policy, and weights, and selects candidates only when a new
physical connection is created; the order is frozen across the TCP retries of one attempt, and health
preference does not cross groups. Weight/group-order changes do not proactively redistribute idle sessions
that still match the address membership; they reconnect only after a Clear. It neither restores old
transactions nor replays unknown writes; a discovery failure still must not fall back to an old snapshot.
See the [group selection contract](obconnector-dotnet-architecture-design.md#discovery-groups-and-selection).

### Boundary between connect retry and business replay

`Connect Retry Count` / `Connect Retry Interval` control only TCP failures before a physical session
Attach and the candidate wait of a whole round. After TCP succeeds, handshake, TLS, authentication,
identity initialization, and business errors are not retried automatically; a broken active connection
still requires an explicit Close/Open and an unknown-outcome check. Enabling retries does not restore old
transactions and does not prove that the original write failed. See the
[full configuration and budget contract](obconnector-dotnet-architecture-design.md#tcp-connection-retry-policy).

`OceanBaseOcpAddressResolver` obtains a new HTTP address snapshot on re-Open; an HTTP/parsing failure does
not fall back to old addresses, and the service name is bound to discovery metadata only. Authentication/TLS/redirect
for the HTTP client are controlled by the caller; it does not rebuild old transactions or replay uncertain
writes. See the [OCP HTTP contract](obconnector-dotnet-architecture-design.md#ocp-http-address-discovery).

`OceanBaseTnsAddressResolver` reads new file addresses on an explicit re-Open; a discovery failure cannot
fall back to old pool addresses. File SERVICE_NAME matching only checks metadata and does not confirm the
real server identity. It will not restore old transactions, migrate active connections, or replay SQL, and
unknown outcomes must still be verified per this guide. See the
[TNS file discovery contract](obconnector-dotnet-architecture-design.md#tns-file-address-discovery).

`Host Recheck Seconds` only changes candidate preference for later physical connections: recently failed
TCP endpoints move later, all addresses can still fall back, and a TCP success clears the record. It does
not verify business health, does not restore old transactions, and does not change the unknown-outcome
verification rules. Pool Clear and credential retirement isolate cached attempt feedback from the old pool
generation; non-pool caches live with the selector and are not cleared by the pool-clear API. Borrowed
connections continue their current work. See the
[TCP health cache contract](obconnector-dotnet-architecture-design.md#endpoint-tcp-health-cache).

### Connection-level server ERR

When the trusted SQLSTATE starts with 08, or is exactly 1927/70100, the driver marks the physical session
Broken and destroys it. The unknown flag for an already-sent execution is conservatively true; it does not
mean the server definitely executed or definitely rolled back; Prepare/catalog/piece acknowledgements
before business keep their current non-executed marking. A plain query cancellation with 1317/70100 does
not belong to this classification. Error codes and SQLSTATE are not a substitute for business verification.

For the same logical connection, Close must come before Open; only Open re-acquires credentials, discovers
addresses, and gets a new lease, and an old transaction can be neither restored nor migrated. A complete
raw-protocol ERR cancelled before parsing can still preserve identity; a closed OB20/compressed transport
cannot supply the missing boundary evidence and still fails conservatively, without bypassing validation.
The [exact boundary and local evidence](compatibility.md) do not mean real node/proxy failures have been
accepted.

### Establish verification grounds before the first send

The application generates and persists a stable `operation_id` before the operation starts, and does not
generate a new ID for a retry. Create your own operations table in advance; for example, it works in both
modes:

```sql
CREATE TABLE application_operations (
    operation_id VARCHAR(64) PRIMARY KEY,
    outcome VARCHAR(32) NOT NULL
)
```

Write the business data and the operation marker in the **same database transaction**, with the marker's
`outcome` set to the application-defined completion state, then commit. The marker must commit atomically
with all business writes and must not be written outside the transaction afterwards. The unique key
prevents duplicate operation markers; it is no substitute for designing business operations, concurrent
retries, and external side effects.

After catching an unknown-outcome exception, save the operation ID, the exception classification, and the
verification state, release the old connection and transaction, and hand over to the verification process.
Do not COMMIT again on the old connection, and do not immediately re-execute the business SQL. Avoid logging
connection strings, passwords, and complete business parameters.

### Verify committed markers with a new lease

Below is a query example for modern TFMs; `connectionString` uses the application's normal
authentication/TLS policy. The new connection must not re-join the TransactionScope that failed, and
verification must read the latest committed data from the authoritative database; it cannot rely on a
delayed replica or an old transaction snapshot.

```csharp
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using OceanBaseConnector;

static async Task<string?> ReadOperationOutcomeAsync(
    string connectionString, string operationId, CancellationToken token)
{
    using var suppression = new TransactionScope(
        TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
    await using var connection = new OceanBaseConnection(connectionString);
    await connection.OpenAsync(token);
    using var command = connection.CreateCommand();
    var marker = connection.CompatibilityMode == OceanBaseCompatibilityMode.Oracle
        ? ":operation_id" : "?";
    command.CommandText =
        "SELECT outcome FROM application_operations WHERE operation_id = " + marker;
    command.Parameters.Add(new OceanBaseParameter
    {
        ParameterName = "operation_id", DbType = DbType.String, Value = operationId,
    });
    var result = await command.ExecuteScalarAsync(token);
    suppression.Complete();
    return result is null or DBNull ? null : (string)result;
}
```

| Verification result | Application handling |
| --- | --- |
| A trusted committed marker is found and the state matches expectations | Complete the operation according to the business result the marker indicates; do not replay |
| The marker is found, but the state or business result does not match expectations | Go to business validation/manual handling; this must not be treated as a network retry |
| No marker is found | Stay "pending verification"; the operation may still be executing, being recovered, or reading stale data, and a single missing read does not prove a rollback |
| The verification query fails or is cancelled | Keep storing the pending-verification state; a failed verification cannot change the original operation outcome |

Only after an authoritative final state is obtained, and the application's idempotency and concurrency
constraints are satisfied, can compensation or re-execution be decided. An ordinary local transaction has
no transaction ID that a new connection can take over; when durable prepare, coordinator crash recovery, or
cross-resource atomicity is required, use a separately designed and accepted XA/coordinator solution. The
driver's local ambient enlistment does not provide distributed promotion or durable prepare.

The direct-response-loss evidence used a local plaintext relay: the real server's COMMIT/ROLLBACK response
was received first, then all response packets were dropped toward the client, or only the original length
header and a single payload byte were delivered. The client reports unknown outcome; an independent
non-pooled observer connection confirms the actual commit/rollback result; a new session through the same
relay endpoint with MaxPoolSize=1 can continue writing and run a local rollback, and the late release of
the old transaction does not affect it.

This proves controlled response loss for the configured OceanBase 4.4.2.2 MySQL/Oracle direct connection,
Windows/.NET 10, TLS disabled; it is not a server crash, real OBProxy, enterprise TLS, external coordinator
recovery, or all failure scenarios. The relay is test infrastructure only, is not part of the product, and
does not downgrade a TLS-enabled profile. The operations table/verification example above describes an
application pattern, not an automatic driver recovery feature.

<a id="network-outcome"></a>

## Controlled network outcome acceptance

The [acceptance console](../tests/OceanBaseConnector.Acceptance/README.md) has 16
`matrix.network-outcome-unknown.*` cases. They require both `networkFaultProfile=true` and
`allowCreateObjects=true` in a dedicated test schema. Default/example configurations skip them before
connecting or creating objects. Ordinary unit tests use synthetic loopback.

The test-only relay accepts a single plain DNS/IPv4 host with a separate Port, TLS disabled, and neither
compression nor OB20. Incompatible profiles skip; transport options are never downgraded. This relay is an
acceptance fixture, not a driver feature or a production proxy.

| Operation, each sync/async | Fault | Independently observed committed row count |
| --- | --- | --- |
| Autocommit INSERT | Withhold request before upstream forwarding | 0 |
| Autocommit INSERT | Forward original header plus command byte, then close | 0 |
| Autocommit INSERT | Withhold whole OK response, or forward original header plus marker byte | 1 |
| COMMIT | Withhold whole OK response, or forward original header plus marker byte | 1 |
| ROLLBACK | Withhold whole OK response, or forward original header plus marker byte | 0 |

All faults happen after the provider has sent its request to the relay. Even when the relay withholds the
request from the server, the driver conservatively reports unknown outcome. This does not prove
provider-local pre-send classification. Response cases first receive a complete server response and
require its OK marker; they do not interrupt server execution.

Every case creates one random owned table, verifies unknown outcome and Broken state, and requires an
interrupted transaction to detach. An independent observer reconciles the actual row count. Close/Open
through a private capacity-one pool must create a second relay TCP connection; a fresh autocommit write and
transaction rollback verify healthy recovery. Exact target-request counts reject replay, including one
additional explicit fresh ROLLBACK in rollback cases. The driver does not retry the interrupted write. See
the [application verification pattern](#unknown-outcome) for business recovery.

All connections, transactions, pools and relay tasks are disposed on failure. Registered cleanup drops only
the case's exact owned table. Primary and cleanup failures are retained; reports omit connection details,
SQL, values and raw exceptions. Forced process termination can leave an owned object requiring manual
cleanup.

On direct OceanBase 4.4.2.2, Windows/.NET 10.0.13, TLS disabled, both modes passed all 16 cases plus one
connection control; all 32 tables were removed. This is bounded direct-server evidence. Provider-local
pre-send, in-flight server execution, timeout/cancellation ordering, ambient in-doubt/coordinator crashes,
real OBProxy, TLS, other versions/platforms and long-running load remain open.
The [XA coordinator process matrix](#xa-recovery) covers prepared-worker death before and after decision
persistence; [controlled topology cases](#topology) cover client-route changes and refused TCP.

The integration suite also covers MySQL XA PREPARE whole successful reply loss in
`Faults/XaPrepareResponseLossTests` (sync/async, direct 4.4.2.2, plaintext, no compression/OB20). The relay
receives the real OK before disconnecting; the driver reports unknown outcome and Broken, with exactly one
PREPARE request. An independent unpooled connection recovers exactly the owned XID, rolls it back and
verifies its absence; an observer sees zero committed rows. The old XA resource rejects operations both
before and after Close/Open, and the fresh session remains usable. These two cases use owned table/branch
cleanup and retain body and cleanup failures. They are separate from the console's 16 network cases and do
not cover Oracle DBMS_XA, partial PREPARE replies, cancellation/timeout ordering, or real server/proxy
crashes.

<a id="topology"></a>

## Controlled topology acceptance

The [acceptance console](../tests/OceanBaseConnector.Acceptance/README.md) has six explicit
`matrix.topology-failover.idle-refresh/borrowed-isolation/tcp-fallback.sync/async` cases. Set
`topologyFaultProfile=true` to enable them. No object-creation permission is needed. Default/example
configurations skip before listeners or connections. Ordinary unit/CI tests use synthetic loopback only.
The profile requires a single plain DNS/IPv4 upstream host, separate Port, TLS disabled, no compression and
no OB20; incompatible settings skip without downgrade. This profile does not validate transport security.

Two transparent loopback relays point to the same configured server. They count physical connections and
signal peer termination without parsing or recording traffic. A private pool uses a controlled public
`IOceanBaseAddressResolver`; every logical Open must use one snapshot through the matching sync/async
resolver method. Each path executes only fixed `SELECT 1` or `SELECT 1 FROM DUAL` and makes these
assertions:

- Idle refresh: unchanged routing first reuses A, removing A replaces its idle connection with B and closes
  A, and another borrower reuses B without a new physical connection.
- Borrowed isolation: a leased A remains usable while a new borrower uses B. After A returns, the next
  borrow retires A and opens another B; both B leases remain usable and later reuse does not reconnect.
  Changing discovery does not migrate active work.
- TCP fallback: a bound non-listening exclusive socket reserves a refused endpoint. A refused-only snapshot
  must fail Open with a known outcome, leave Closed and never contact the configured healthy fallback. The
  same connection/private pool then opens with a refused-plus-healthy snapshot and reuses its single healthy
  physical connection afterward.

No DDL, writes, transactions or cluster settings are changed. Credentials stay in connection memory/private
pipes and are never sent to the refused endpoint. Reports contain fixed assertions and server/runtime
identity, omitting endpoints, connection strings, SQL and raw exception text. Connections, private pools,
listeners, forwarding tasks and reserved sockets are disposed on success, failure or cancellation; primary
and cleanup failures are preserved. Stopping a relay is part of fixture cleanup, not proof that a real
database node failed.

Direct OceanBase 4.4.2.2, Windows/.NET 10.0.13, TLS disabled: Oracle and MySQL each pass 7/7 (connection
control plus six cases), zero failures/skips/cleanup failures, no objects created.

This covers controlled client routes to one upstream. Actual independent cluster nodes, discovery
services/TNS/OCP refresh, proxy backend identity/reroute, DNS IP changes, authentication or TLS failures,
active-session failover, transaction reconciliation/replay, other versions, platforms/runtimes and
sustained workload remain open. Two unconditional deferred
placeholders remain: TLS/authentication and OB20/trace negotiation.

<a id="xa-recovery"></a>

## XA coordinator process recovery acceptance

The [acceptance console](../tests/OceanBaseConnector.Acceptance/README.md) has six cases:
`matrix.xa-crash-recovery.commit/rollback.sync/async` and
`matrix.xa-crash-recovery.unrecorded.rollback.sync/async`. They require `xaCrashProfile=true`,
`allowCreateObjects=true`, a dedicated test schema and the `dotnet` host on PATH. Oracle also requires
`Use Oracle Prepare Execute=true`. Default/example profiles skip before connections or DDL. Ordinary
unit/CI runs use synthetic loopback only.

Each case supervises two separate acceptance subprocesses through a reserved worker mode:

1. The first process starts its unique owned XA branch, inserts one row, ends and prepares it. It creates
   `decision.json` with only the generated table/global ID and commit/rollback decision, using CreateNew
   and Flush(true). It reports PREPARED after the flush and waits.
2. The supervisor verifies the worker remains alive, forcibly kills it without normal Close/Dispose, waits
   for nonzero exit and verifies the prepared row is still invisible.
3. A new process reads the decision from disk, requires exactly that prepared branch in Recover, commits or
   rolls it back and verifies its removal. It reports RECOVERED and exits zero. The supervisor independently
   checks the durable row count and branch absence.

The two unrecorded cases stop the first worker after successful Prepare and before writing any decision. Its
distinct PREPARED_UNRECORDED marker proves that phase; the supervisor kills and reaps the process, requires
the journal to be absent and independently verifies no visible row. The surviving supervisor retains only
this test's owned identity and records rollback using the existing no-decision rollback rule. The new
recovery process reads only that disk decision, requires the exact XID in Recover, rolls it back and
verifies no row became durable. A request combining the unrecorded phase with commit is rejected before
connecting. This does not prove reconstruction of ownership after the supervisor itself dies.

Sync/async selects driver operations in both workers. This is a test-only coordinator, separate from the
driver product. The product still provides explicit XA resource APIs; it does not supply a durable
transaction manager or distributed ambient promotion.

Connection data travels over redirected stdin, never process arguments, journal files or new environment
variables. Worker output consists of fixed phase/failure markers; stderr is drained without recording.
Reports omit worker input/output, raw exceptions, SQL, connection details and temp paths. Request parsing is
bounded at 65536 characters and journal reading at 4096 bytes. Required fields, duplicate names at every
nesting level, unexpected fields, actions and generated identifier shapes are validated before connecting.
Recovery accepts its decision only from the journal. Journals cannot be overwritten.

Each child has a 90-second supervisor budget. Cancellation/failure kills and reaps any live child. Cleanup
considers only the exact owned XID, honors its flushed decision, and uses rollback when no decision was
recorded. It preserves the primary failure and cleanup failures. An unresolved prepared branch retains its
local journal under the OS temp directory at `ob-xa-<reported-global-ID>-*/decision.json`; use the original
private database configuration for manual recovery. Exact owned tables and resolved journals are removed;
unrelated branches are never completed. Forced termination of the supervisor itself can leave owned
resources.

Direct OceanBase 4.4.2.2, Windows/.NET 10.0.14, TLS disabled: each mode passes all six cases plus one
connection control. All twelve owned tables, branches and journal directories are cleaned. Synthetic tests
also cover unrelated branch preservation, a missing prepared branch, cancellation, completion failure with
journal retention, strict input/journal rejection and privacy.

This proves prepared-worker death before and after decision persistence in this test harness. General
coordinator interoperability, loss of the supervisor, crashes during completion acknowledgement, server
failure, power-loss durability, concurrent recovery/heuristics, proxies/TLS and other platform/runtime/load
profiles remain open under A-02. The product continues to expose XA resource operations without a durable
coordinator.
