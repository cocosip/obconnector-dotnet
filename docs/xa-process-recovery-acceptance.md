# XA coordinator process recovery acceptance

The [acceptance console](../tests/OceanBaseConnector.Acceptance/README.md) has four
`matrix.xa-crash-recovery.commit/rollback.sync/async` cases. They require
`xaCrashProfile=true`, `allowCreateObjects=true`, a dedicated test schema and the `dotnet`
host on PATH. Oracle also requires `Use Oracle Prepare Execute=true`. Default/example
profiles skip before connections or DDL. Ordinary unit/CI runs use synthetic loopback only.

Each case supervises two separate acceptance subprocesses through a reserved worker mode:

1. The first process starts its unique owned XA branch, inserts one row, ends and prepares it.
   It creates `decision.json` with only the generated table/global ID and commit/rollback
   decision, using CreateNew and Flush(true). It reports PREPARED after the flush and waits.
2. The supervisor verifies the worker remains alive, forcibly kills it without normal
   Close/Dispose, waits for nonzero exit and verifies the prepared row is still invisible.
3. A new process reads the decision from disk, requires exactly that prepared branch in
   Recover, commits or rolls it back and verifies its removal. It reports RECOVERED and exits
   zero. The supervisor independently checks the durable row count and branch absence.

Sync/async selects driver operations in both workers. This is a test-only coordinator,
separate from the driver product. The product still provides explicit XA resource APIs;
it does not supply a durable transaction manager or distributed ambient promotion.

Connection data travels over redirected stdin, never process arguments, journal files or
new environment variables. Worker output consists of fixed phase/failure markers; stderr
is drained without recording. Reports omit worker input/output, raw exceptions, SQL, connection
details and temp paths. Request parsing is bounded at65536 characters and journal reading
at4096 bytes. Required fields, duplicate names at every nesting level, unexpected fields,
actions and generated identifier shapes are validated before connecting. Recovery accepts
its decision only from the journal. Journals cannot be overwritten.

Each child has a90-second supervisor budget. Cancellation/failure kills and reaps any live
child. Cleanup considers only the exact owned XID, honors its flushed decision, and uses
rollback when no decision was recorded. It preserves the primary failure and cleanup failures.
An unresolved prepared branch retains its local journal under the OS temp directory at
`ob-xa-<reported-global-ID>-*/decision.json`; use the original private database configuration
for manual recovery. Exact owned tables and resolved journals are removed; unrelated branches
are never completed. Forced termination of the supervisor itself can leave owned resources.

Direct OceanBase4.4.2.2, Windows/.NET10.0.13, TLS disabled: each mode passes all four cases
plus one connection control. All eight owned tables, branches and journals are cleaned.
Synthetic tests also cover unrelated branch preservation, a missing prepared branch,
cancellation, completion failure with journal retention, strict input/journal rejection and
privacy. See the [module record](obconnector-dotnet-progress.md#2026-10-27-xa-coordinator-process-recovery).

This proves one prepared/recorded-decision crash point. General coordinator interoperability,
crashes before decision persistence or during completion acknowledgement, server failure,
power-loss durability, concurrent recovery/heuristics, ambient in-doubt, proxies/TLS and other
platform/runtime/load profiles remain open under W2/W8. Three deferred matrix placeholders
remain: TLS/authentication, OB20/trace negotiation and topology failover. The23-gap/eight-package
baseline remains open.

Current tool status: [controlled topology cases](topology-acceptance.md) now replace the topology
placeholder; two deferred entries remain (TLS/authentication and OB20/trace). The preceding
three-placeholder count records this XA module checkpoint.
