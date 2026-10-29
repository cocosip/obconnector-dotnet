# API reference

For a basic query, see the [README](../README.md). For tested server profiles, see [Compatibility](compatibility.md).

The [Connector/J architecture outline](obconnector-j-architecture-outline.md) is retained as the protocol and behavior reference. Its Java feature inventory does not establish .NET support; unverified and unsupported combinations are listed in Compatibility.

## Protocol diagnostic unloading

Protocol event recursion is suppressed per physical thread with ThreadLocal<bool>; it does not flow into
another execution context. The guard does not declare a ThreadStatic field in a collectible provider type.
An independent-process consumer checks old-context collection and the integrity of new-context static
objects after the old thread exits. This addresses the reproduced diagnostic state corruption while
preserving synchronous observer exception isolation, Activity restoration and metadata-only events.
See the [protocol contract](protocol-diagnostics.md) and [local verification limits](history/obconnector-dotnet-completeness.md#1559-2026-10-26-可卸载诊断线程状态损坏与默认回归恢复).

## Connection-level server errors

A trusted server ERR with ordinal SQLSTATE prefix `08`, or exactly error `1927` and SQLSTATE `70100`, retires the physical session. The connection reports `Broken`; its local transaction becomes unusable. Server code and SQLSTATE are retained with a generic message that omits server details. Ordinary errors, including `1317/70100`, `1927/HY000` and syntax errors, retain their existing session semantics.

`IsExecutionOutcomeUnknown` uses the current exchange's execution flag. A connection-level failure for a sent business execution, COMMIT/ROLLBACK or ChangeDatabase is conservative unknown: an ERR does not prove the backend never executed it. A prepare, explicitly non-executing metadata request or Oracle upload-piece acknowledgement sent before execution is not an unknown business outcome. A later explicitly non-executing request after a confirmed response does not invalidate the confirmed parent. Cursor fetch semantics depend on whether the original execution is still pending; do not infer the flag from the FETCH opcode alone.

The complete framing boundary is validated before retiring the session and completing the exchange. Invalid OB20/compression boundaries remain protocol failures without trusted server code/SQLSTATE. If cancellation closes a fully consumed bare MySQL packet before ERR parsing, its identity is retained. A closed framed transport cannot supply the required boundary evidence and remains a generic conservative failure; the driver does not bypass framing validation. Oracle piece failures do not run response completion a second time after retirement.

Recovery is explicit: Close, then Open to obtain credentials, resolve addresses and acquire a new lease. The driver does not replay the failed request, send a Batch suffix, migrate a transaction or reconnect during ERR handling. Dispose old resources without relying on disposal as compensation; reconcile unknown writes using the [application recovery guidance](unknown-outcome-recovery.md). Capacity-one pools, delayed old transaction/command/CloseConnection-reader disposal and changed resolver endpoints have local regressions.

## Connections and configuration

Use `OceanBaseConnectionStringBuilder` to construct connection strings. Unknown or conflicting options are rejected.

| Option | Default | Purpose |
| --- | --- | --- |
| `Server` / `Port` | Required server / 2881 | Server addresses; omitted candidate ports inherit `Port`. |
| `User ID` / `Password` | Empty | Database credentials. |
| `Database` | Empty | Initial database. `ChangeDatabase` is available in MySQL mode only. |
| `Connection Timeout` | 15 seconds | Total budget for address discovery, connection, TLS, authentication and identity confirmation. |
| `Connect Retry Count` | 0 | Additional complete TCP candidate rounds, 0..10; zero preserves one-pass behavior. See [retry contract](#connect-retry-policy). |
| `Connect Retry Interval` | 1 second | Fixed wait between failed candidate rounds, 1..60; validated even with count zero. |
| `Host Recheck Seconds` | 0 seconds | TCP failure hint lifetime, 0..3600; zero disables. Recent failures move after other candidates without excluding any address. See [contract](#endpoint-tcp-health). |
| `Command Timeout` | 30 seconds | Command execution budget. |
| `Slow Query Threshold` | 0 milliseconds | Nonnegative Int32 client execute/cursor lifetime threshold; zero disables slow reporting. See [contract](#slow-query-diagnostics). |
| `ExpectedMode` | Unset | Validates `MySql` or `Oracle` after detection. |
| `TLS Mode` (`SslMode` alias) | `VerifyFull` | Validates certificate trust and hostname. `Required` encrypts without identity verification; `Disabled` uses plaintext transport. |
| `TLS Server Name` | Endpoint hostname | Overrides the TLS certificate name. |
| `Check Certificate Revocation` | false | Enables platform certificate revocation checks during TLS. |
| `Persist Security Info` | false | Retains credentials in opened-connection and data-source connection strings when true. |
| `Load Balance` | `Sequential` | `RoundRobin` rotates the first candidate on each Open. |
| `Pooling` | false | Reuses physical sessions with protocol reset, capacity/wait controls, minimum-pool prewarming and idle/total lifetime reaping; recognized user session-state or temporary-object statements discard the session on close. |
| `Min Pool Size` | 0 | Ensures at least this many physical sessions before a borrow; an explicit discovery cap0 skips replenishment and permits valid idle reuse. Otherwise a failed warmup rolls back sessions created by that attempt only while they remain idle; concurrent borrowers retain ownership. |
| `Max Pool Size` | 100 | Maximum physical sessions per matching pool; additional opens wait for a return. |
| `Pool Wait Timeout` | `Connection Timeout` | Seconds to wait for a physical session; zero removes this wait limit. With a custom resolver, credential provider, or positive Connect Retry Count, the total logical Open connection budget also bounds pool waiting, even when this wait limit is longer or zero. |
| `Connection Idle Lifetime` | 0 | Seconds an idle session may remain before lazy reaping; zero disables the limit. |
| `Connection Lifetime` | 0 | Maximum physical-session age in seconds; zero disables the limit. |
| `Statement Cache Size` | 0 | Idle server-prepared statements retained per physical session, 0..1024; zero disables sharing across commands. |
| `Use Compression` | false | Requests standard MySQL zlib compression. Servers without `CLIENT_COMPRESS` keep ordinary framing, matching Connector/J; cannot currently combine with Use OB20. |
| `Character Set` | utf8mb4 | SQL/ordinary parameters; non-default names enable cached charset trust and UTF-8 results. See [input encoding and session limits](client-character-sets.md). |
| `Use OB20` | false | Requests OB20 framing when negotiated by the server. |
| `Enable Full Link Trace` | false | Requests server tracing; requires `Use OB20=true`. |
| `Use Oracle Prepare Execute` | false | Enables Oracle procedures and output bindings; requires server version discovery and OceanBase >=2.2.76. |
| `Use Lob Locators` | false | Enables Oracle locator LOBs. External reads and mutations also require PrepareExecute. |
| `Allow Multi Statements` | false | Explicitly requests `CLIENT_MULTI_STATEMENTS` for text commands. Oracle named and MySQL positional scalar inputs use validated client literals, including MySQL JSON and DATE/DATETIME/TIMESTAMP/TIME and Oracle NChar/NVarchar2 and DATE/TIMESTAMP/TIMESTAMP WITH TIME ZONE/TIMESTAMP WITH LOCAL TIME ZONE/INTERVAL YEAR TO MONTH/INTERVAL DAY TO SECOND. MySQL string binding requires stable parsing charsets and rejects dynamic execution/CALL/quoted SET identifiers. Binary Input streams use bounded pre-send hex encoding. Prepare, cursors, outputs and complex inputs remain unsupported. |
| `Allow Local Infile` | false | Explicitly negotiates `CLIENT_LOCAL_FILES` for server-requested uploads. Set `OceanBaseConnection.LocalInfileStreamFactory` before `Open`; the filename is opaque and the returned readable stream is disposed by the driver. |
| `Treat Tiny As Boolean` | true | MySQL signed `TINYINT(1)` with zero scale returns `bool`: zero is false, every nonzero value is true. Set false to read the full `sbyte` value. Unsigned TINYINT, other widths, Oracle types and BIT are unaffected. |
| `Use Affected Rows` | false | MySQL UPDATE reports matched rows by default (`CLIENT_FOUND_ROWS`). Set true to request changed rows instead. Commands, readers and batches retain the counts returned by the server. |

Every connection performs one read-only identity query after authentication:
`select ob_version(), @@ob_compatibility_mode from dual`, extended with `, @@autocommit` in MySQL mode.
The mode must match authentication and `ExpectedMode` when supplied.
`ServerVersion` and schema product-version metadata report the actual `ob_version()` value;
the original MySQL-compatible greeting is retained internally and does not select features.
Malformed identity results and unknown version formats fail Open without publishing a session.
MySQL-mode sessions must report `@@autocommit` enabled: the provider has no API to commit implicit
transactions, so Open fails instead of accepting a session whose statements outside an explicit
transaction would hold locks and roll back when the connection closes. This MySQL rejection contract
remains separate from Oracle initialization described below.
Three- or four-component numeric versions, optional `v`, OceanBase-marked versions and printable
build suffixes are recognized. An OceanBase marker selects the product version after the marker,
never a preceding MySQL-compatible version. There is no exact-release allowlist or user version override.

<a id="oracle-autocommit-baseline"></a>
### Oracle autocommit baseline

After successful Oracle identity verification, Open always sends `set autocommit = 1`, even when
its authentication packet advertises Autocommit. It publishes Open only after a valid ordinary OK
with zero affected rows/insert ID, Autocommit enabled and no transaction (including read-only),
additional-results or cursor/output flags. Server errors preserve code/SQLSTATE without exposing
server message text; malformed replies, cancellation, timeout and Close destroy the physical session.
The existing connection timeout and opening cancellation token cover this extra identity-phase exchange.
MySQL continues to require enabled `@@autocommit`; it does not send this Oracle setup command.

Oracle pool return first completes `COM_RESET_CONNECTION`, then sends and confirms the same SET.
The order preserves rollback of inherited writes. Only successful completion advances the session
generation and permits reuse; failure destroys the session and releases its pool slot. Reset keeps
its existing per-control-exchange command timeout, with cancellation/transport cleanup applied to
SET as well. Setup adds one server round trip to each new Oracle physical connection and successful
Oracle pool reset; no connection-string option bypasses it.

Open merges SET-reported system variables over authenticated values. Reset clears old metadata and
keeps only variables reported by SET. These trusted values feed subsequent timezone/national-character
handling; an empty reset SET response cannot retain a prior borrower's metadata. This internal setup
does not classify the session as changed by user SQL. Existing user-state/resource discard rules remain.

Local protocol tests and direct OceanBase4.4.2.2 Windows/.NET10/TLS-disabled pool tests cover the
initializer; full resource/XA/concurrency isolation, OBProxy, trusted TLS, other versions and independent
runtime acceptance remain open. See the [current capability baseline](obconnector-dotnet-completeness.md).

Protocol features use the effective client/server capability intersection. OB20, tracing and LOB
locators additionally require their negotiated OceanBase support and explicit options. PrepareExecute
and offset fetch use the cached actual version, with minimums 2.2.76 and 2.2.5 respectively.
These feature minimums are not a tested-server support matrix; see [Compatibility](compatibility.md).
`compatibleMysqlVersion` and `compatibleOjdbcVersion` are JDBC behavior profiles and are not accepted.
YEAR stays numeric; Oracle DATE retains time of day; zero dates use the existing lossless provider
types rather than automatic rounding or conversion to NULL. Result aliases and base names stay separate.

Builder output and unopened direct connections can contain credentials. Use `ToSanitizedConnectionString(false)` for display; do not log raw connection strings.

For mutual TLS, assign an application-owned `X509Certificate2` to
`OceanBaseConnection.ClientCertificate` before `Open`/`OpenAsync`, or configure
the modern builder's `UseClientCertificate(...)`. Both pooled and direct connections
are supported. The driver never disposes the application's certificate; pools and
data sources own internal copies. See [TLS client certificates](#tls-client-certificates)
for pool identity, source snapshots and rotation.

### Authentication

| Plugin | Requirements |
| --- | --- |
| `mysql_native_password`, `client_ed25519` | Use configured TLS policy; authentication does not encrypt SQL/results. |
| `mysql_clear_password`, `mysql_old_password`, `dialog`/PAM | Require `VerifyFull`. PAM takes subsequent credentials from `Password2`, `Password3`, etc. |
| `sha256_password`, `caching_sha2_password` | Full password authentication requires `VerifyFull` or explicit RSA configuration. |
| `auth_gssapi_client` | .NET 8/9/10 assets only; requires `VerifyFull`, current Kerberos credentials and mutual authentication. Optional `Service Principal Name` overrides the server SPN. |

For RSA authentication, `Server RSA Public Key` accepts trusted SubjectPublicKeyInfo PEM text, not a file path. `Allow Public Key Retrieval=true` explicitly permits unauthenticated key retrieval and is vulnerable to key substitution. RSA protects the password only. See [Compatibility](compatibility.md) for validation limits.

### Data sources and DI

`OceanBaseDataSourceBuilder.Build()` freezes configuration without connecting. On .NET 8/9/10, use `CreateConnection()` for a closed connection or `OpenConnectionAsync()` to open one. A data source can serve concurrent callers; each connection permits one active operation or reader.

| Registration | Lifetime / availability |
| --- | --- |
| `AddOceanBaseConnection(connectionString)` | Closed transient connections on all targets. |
| `AddOceanBaseDataSource(connectionString)` | Singleton data source on .NET 8/9/10. |
| `AddKeyedOceanBaseDataSource(key, connectionString)` | Separate singleton per key on .NET 8/9/10. |

Registrations preserve an existing unkeyed `DbConnection`/`DbDataSource` registration from another provider. Inject concrete OceanBase types when providers coexist. Data-source registration does not register connections.

The container disposes its registered services; callers dispose connections created from a data source. Dispose manually built data sources yourself. Source disposal does not forcibly close already-open connections; when those connections later close, their borrowed physical sessions are discarded without sending reset traffic to the disposed pool. External `ILoggerFactory` and address resolvers remain caller-owned.

Closing detaches the session together with its original pool, registry key and cleanup timeout before raising the Closed state event. If that event changes configuration or reopens the logical connection, cleanup still uses the detached session's ownership snapshot. Failed opens also release registry entries using their original key.

### Ordinary pool configuration identity

With `Pooling=true`, ordinary connections share a pool when their effective configuration and policy identities match. Internal normalization covers option order/aliases, Boolean/integer/enum spelling and omitted versus explicit defaults. The default Pool Wait Timeout follows Connection Timeout. Every declared option participates, including pool/timeouts and security policy; text values remain ordinal, including Server/candidate order, user/database, passwords, RSA material and TLS/SPN names. This does not infer DNS, hostname-case or tenant equivalence. An unspecified ExpectedMode stays distinct from an explicit mode.

PAM PasswordN factors are ordered numerically; a present empty factor differs from an absent one. Open, Close/discard, failed-open cleanup and ClearPool derive their identity from the original parsed builder before public serialization can lose quoted-empty presence. The internal identity includes secrets and is never a display/logging string. Public ConnectionString serialization and PersistSecurityInfo behavior are unchanged; passing a serialized string through another parser still follows ordinary ADO.NET empty-value parsing rules.

Pools also retain resolver, credential-provider, validation-delegate and certificate reference identity, plus the certificate fingerprint. Equal delegates or objects are not interchangeable policy instances. Data sources keep separate private pools. ClearAll and empty-pool removal reuse the same canonical identity without merging private sources or reinterpreting credentials.

Closed state notification starts before transaction/return cleanup. Pooled opens within that callback can complete the captured cleanup before borrowing, as described below. Complete reset, concurrency and long-running resource acceptance remain open; see [the remaining pool work](history/obconnector-dotnet-completeness.md#51-连接池和会话隔离).

<a id="pool-wait-handoff"></a>
### Pool wait notification handoff

A notification tells a waiting Open to retry the pool decision; it does not reserve a session
or guarantee FIFO service. If a notified renter exits before claiming the idle session or free
creation slot, the rent exit checks availability under the pool gate and notifies another waiter.
The same path serves synchronous and asynchronous opens, ordinary pools and private data sources.
All session selection and creation reservations still obey Max Pool Size under that gate.

Canceling an Open, including Close during a synchronous Open, must not strand another waiter
while an eligible session or creation capacity is available. Existing pool/connection deadlines,
probe/reset validation, credential/route identity, Clear retirement and Dispose rules still apply.
Deterministic local regressions exercise cancellation immediately after a notification and a
failed reset; they do not establish scheduling latency or long-running load guarantees.

<a id="pooled-close-callbacks"></a>
### Closed callbacks and pooled opens

A pooled `Open` or `OpenAsync` invoked from a Closed `StateChange` handler completes the captured close operations in that event's execution context before entering the normal borrow path. This includes nested callbacks: if closing A closes B and B's callback opens A, both old cleanup operations finish first. The open can use the same logical connection or another connection sharing the pool. Normal pool capacity, identity, reset, probing and retirement rules still apply; no extra pool or physical slot is created to bypass the limit. Other borrowers can still cause ordinary contention.

Cleanup runs exactly once using the old session, transaction, exchange, endpoint, pool identity and timeout captured at detach. The old transaction is rolled back and its exchange released before pool return; a failed reset discards that session. Synchronous borrowing performs synchronous cleanup; asynchronous borrowing awaits asynchronous cleanup. Cancelling the new borrower does not abandon or cancel the captured close operation. The existing rent reservation is released even when cleanup prevents the borrow.

The logical Open reservation is established before cleanup. A Closed log callback cannot take over that same opening logical connection. Without an Open from the state handler, Closed log callbacks retain their existing order after the old resources are released. Throwing state handlers still finish the captured cleanup, and the outer Close cannot close a new lease opened by a handler. Tasks that inherit the event context observe the same completed operation rather than repeating disposal. The winning cleanup releases both captured delegates; asynchronous cleanup also releases its lifted local delegate before publishing shared completion, so a completed event scope does not retain the old resource graph through those delegates.

Outside the state-handler context, an external Open during pending asynchronous rollback retains the existing detached-session and event ordering. Old CloseConnection readers retain their session and logical-open generation checks.

Transaction ILogger notifications produced by captured Close cleanup are deferred until the exchange
is disposed, the session is returned or destroyed, and the ordinary registry entry is released when
empty. They retain operation, endpoint, mode, elapsed time and error snapshots from completion,
including autocommit-restore warnings and rollback success/failure order, and precede the Closed log.
A transaction logger can reopen the same full pool and start a new transaction without the old Close
touching that new lease afterward. Logger exceptions retain the existing isolation behavior. Explicit
Commit/Rollback and transaction disposal keep immediate logging. This scope does not establish
general reentry from Activity/Meter, command loggers or other callbacks; complete concurrency, reset
and deployment acceptance remain open.

### Explicit pool clearing

All five assets expose `OceanBaseConnection.ClearPool(OceanBaseConnection)` and `ClearAllPools()`. Modern data sources expose `OceanBaseDataSource.Clear()`. ClearPool rejects null/disposed connections, is a no-op for non-pooled or absent ordinary pools, and does not connect, resolve addresses or create a pool. It snapshots the connection's effective parsed configuration and resolver, credential-provider, validation callback and client-certificate references; a closed connection that has never opened can therefore clear an existing matching pool, including an equivalent option spelling/order/default configuration. It never reads certificate contents, so Reset/Dispose of an original cannot block cleanup; all historical fingerprints for the same reference identity match. Changing connection configuration before clearing targets the new identity, not the former one.

Clear closes idle physical sessions immediately, marks borrowed sessions for destruction when returned, and advances the pool generation. An Open already in flight can finish, but its pre-clear session cannot later be reused; pre-clear warmup completions are discarded before becoming idle. Commands, readers, transactions and already borrowed connections are not interrupted or replayed. Retired borrowed sessions still count toward MaxPoolSize until closed. A return that observes retirement skips reset; if reset was already in progress it may finish, but that session is destroyed rather than published idle. Capacity waiters are signaled when sessions are removed. Clear does not populate MinPoolSize immediately; subsequent Opens perform warmup.

ClearAllPools operates on a snapshot of ordinary registry pools; new pools created concurrently need not be included. It does not clear private sources. Use source.Clear(), or ClearPool with one of that source's connections, for the private pool. Clearing keeps the source and its immutable configuration/certificate snapshot available; Dispose still ends source use. Empty ordinary registry pools are removed and release owned copies. Clearing does not replace static credentials, certificates or callbacks: select a new configuration/source when changing them, and clear the old identity or dispose its source to retire old sessions. Credential-provider versions can rotate authentication material within a live pool as described in the [credential contract](credentials.md). Idle/lifetime reaping remains lazy unless explicitly cleared.

### Pooled session state after user SQL

With `Pooling=true`, recognized state-changing SQL makes its physical session ineligible for reuse. The flag is sticky for that session and is applied at the shared target-send boundary, after cancellation checks and before the write; the session is destroyed on return even if the server rejects that request. Merely preparing, reading SchemaOnly/KeyInfo metadata, uploading long data, or rejecting a command before target execution does not apply this flag. A transport failure or unfinished protocol response can still independently destroy the session.

Classification scans all statement heads across mode-specific whitespace/comments and semicolons. It recognizes SET/RESET/USE, ALTER SESSION, temporary-table forms, explicit transaction/XA statements and opaque CALL/DO/EXECUTE/PREPARE operations. Oracle BEGIN/DECLARE and labeled blocks, plus `CommandType.StoredProcedure` names sent through PrepareExecute, retire the session because their internal state changes cannot be inferred from the call. MySQL executable comments, user-variable references and named-lock function calls also retire it. Ordinary literals, identifiers and non-executable comments containing these words do not by themselves retire a session; table/CTE column lists are distinguished from named-lock calls.

MySQL SQL_MODE is not tracked by this pooled-state classifier; the per-fallback string snapshot does not change that policy. A backslash within quoted text is therefore treated conservatively as ambiguous and retires the session, including otherwise harmless escaped strings. Quoted named-lock calls are conservative under ANSI_QUOTES/backtick rules. The lexical classifier preserves covered common query/DML forms; a nested CTE declaration named like a lock function can still conservatively retire a safe session. It does not prove that arbitrary stored functions, triggers or other server-side code have no session effects. Such applications need verified reset behavior for their exact profile or must disable pooling. The existing COM_RESET_CONNECTION/initial-database restoration, active-resource disposal and dedicated-XA rules still apply; marking raw XA SQL does not implement coordinator recovery.

This is local driver behavior, not proof of OceanBase/OBProxy reset compatibility. The remaining resource, fault, concurrent-return and real-profile acceptance requirements are tracked under P0-02 in the [completeness baseline](obconnector-dotnet-completeness.md).

## Commands and readers

- Use Oracle `:name` or MySQL positional `?` bindings. Parameterized execution uses server prepare; `Prepare()` explicitly prepares a command.
- Oracle anonymous blocks and stored-program DDL are sent as single program units; internal semicolons do not require `Allow Multi Statements`. The server validates the complete program syntax, including any invalid trailing SQL. Ordinary SQL boundary checks use the active compatibility mode's quoting and comment rules.
- For `LOAD DATA LOCAL INFILE`, opt in with `Allow Local Infile=true` and set `LocalInfileStreamFactory` before opening. The factory receives the server-supplied filename as opaque text and must return a readable stream; the driver uploads it in chunks, sends the terminating empty packet, reads the final response, and disposes the stream. The driver also disposes a returned non-null stream if readability validation fails or throws. Missing factories, read failures, cancellation, the 64 MiB default upload limit, or a lost final response break the physical session and report an unknown outcome.
- Set `DbType` or `OceanBaseDbType` when inference from `Value` is insufficient. Use `DBNull.Value` for SQL NULL. `ResetDbType()` restores inference.
- Read all results with `Read` and `NextResult` to establish completion. Closing an unread reader destroys its unfinished session; disposing a field stream does not finish its parent reader.
- `CommandBehavior.SequentialAccess` exposes field streams without buffering the whole result. `SingleRow` returns only the first row of the first result, even when that result is empty or only an affected count; it implies SingleResult. `SingleResult` drains later results without exposing them. `SchemaOnly` accepts text prefixes `SELECT`, `SHOW`, `DESCRIBE`, `DESC`, `EXPLAIN` or `VALUES` after whitespace/comments. It obtains metadata through server preparation and never sends a target COM_QUERY, COM_STMT_EXECUTE, COM_STMT_FETCH or parameter-data upload. It accepts one text statement with input parameters only; names/order/count remain required, but values are not encoded, consumed or validated as bindings. FetchSize and offset hints are inactive. Preparation errors propagate without client-side fallback, including error 1295 when AllowClientSidePrepareFallback=true. An existing valid prepared statement is reused. The empty reader exposes prepared column metadata, HasRows=false and RecordsAffected=-1; its exchange is complete, so another connection operation is allowed. CloseConnection still closes the original logical Open lease when that reader is disposed; a later lease is protected even if pooling reuses the same physical session. Named complex columns require existing preloaded descriptions and may issue directory lookups for a missing source identity; those lookups do not execute the target. Metadata is the server prepare-time shape, which may differ from execution-time metadata. Server preparation can still incur parsing, permission checks and metadata locks; real OceanBase/OBProxy side-effect/lock/large-result acceptance remains open. Ordinary row readers retain their existing operation restrictions.
- Positive `OceanBaseCommand.FetchSize` enables forward-only server cursor pages for ordinary queries without cursor-valued columns. Oracle internal procedure/RETURNING output rows do not use this hint; returned REF CURSOR readers have their own page size. Oracle `UseCursorOffset=true` additionally requires OceanBase >=2.2.5; it does not provide scrolling.
- `AllowClientSidePrepareFallback=true` permits limited scalar-input text fallback only after automatic prepare returns a validated `1295/HY000`. Explicit Prepare, cursors and Oracle PrepareExecute never fall back. Date/time, special types and output parameters are excluded. MySQL non-null strings use the [trusted state contract](#mysql-client-string-preparation). Fallback literals may appear in server SQL logs.

### CloseConnection reader ownership

`CommandBehavior.CloseConnection` belongs to the logical Open lease that started the reader. Disposing a completed ordinary reader or closing a Batch reader must not close a later lease after explicit Close/Open, even when the pool reuses the same physical session object. Ordinary readers capture the connection Open generation at command start; batches capture it with the session reservation. The connection compares session identity and that generation atomically before conditional closure. Public Close/CloseAsync still close the current connection; disposal within the original lease retains its existing behavior.

This rule also protects metadata-only readers and the Batch reservation-release/connection-close callback window. It does not promise arbitrary concurrent operations on one connection. Local regressions, five-asset consumers and direct OceanBase4.4.2.2 MySQL/Oracle tests cover this lease boundary; full resource, fault, load, proxy and platform acceptance remains open. See the [verification record](history/obconnector-dotnet-completeness.md#1541-2026-10-25-旧-reader-延迟关闭与池租约隔离).

### Prepared statement cache

Set `Statement Cache Size=1` (up to 1024) to retain idle server-prepared statements across command instances on the same physical session. The default is zero. Parameterized commands still prepare automatically; parameterless text commands still need explicit Prepare to use server preparation. A hit uses the exact rewritten SQL with the current session database/context; parameters and their values are supplied afresh on every execution. It never shares IDs between physical sessions or changes SQL to obtain a hit.

The cache uses least-recently-returned eviction. Checking out an entry removes it from the idle cache and transfers exclusive ownership to the command. Commands keep their own statement until disposal or a change of SQL/connection; retained command-owned statements are outside the idle capacity limit and must still be disposed. An active reader/cursor prevents another command from acquiring the physical session. A fully consumed cursor can be reused after its command is disposed. Eviction and duplicate return queue COM_STMT_CLOSE through the existing command boundary; they never write into another reader's response. The option bounds idle statement count, not total application-held commands or metadata bytes.

Ordinary single SELECT/WITH/INSERT/UPDATE/DELETE/REPLACE/MERGE statements without recognized session-state effects or quoting ambiguity are eligible. Other user SQL, including DDL, opaque calls, executable comments and multi-statements, invalidates the cache at the target-send boundary after cancellation checks. Successful ChangeDatabase also invalidates it. A separate context epoch prevents commands still holding an old statement from returning or reusing it after such changes; those IDs are closed normally. Both sync and async preparation recheck the database, reset generation and cache epoch after acquiring the exclusive session lease, so a context change before acquisition cannot leave a stale held ID in use. Commands rejected locally before target send, pre-canceled commands and preparation do not apply target-send invalidation. A context-changing target that reaches the server invalidates conservatively even if the server rejects it.

A successful pool COM_RESET_CONNECTION forgets idle entries and advances the physical generation; old IDs are never closed again because the server reset already closed them. Broken/closed transports forget all entries. Complete preparation and matching parameter counts are required. Execution errors prevent an entry from returning to the cache, and unread responses retain the existing session-destruction behavior. No cache miss/error triggers execution replay. SchemaOnly and KeyInfo bypass shared lookup and their newly prepared statements are not cached; an already valid command-owned preparation retains its existing behavior. Oracle PrepareExecute, auxiliary catalog preparation and rewritten KeyInfo statements keep their existing owners and are excluded from idle sharing. XA-dedicated sessions do not return entries to this cache.

External DDL and arbitrary stored-function/trigger side effects have no client-side dependency tracking. Follow server invalidation errors and explicitly recreate the command/connection when required; the driver does not retry unknown writes. Real server reprepare/DDL, OBProxy, resource/load and independent-runtime acceptance remain open. Pool reset deliberately limits cache reuse to commands within an uninterrupted physical-session context; it does not weaken borrower isolation to preserve cache hits.

### Key information and hidden columns

`CommandBehavior.KeyInfo` requires a single text statement with input parameters and server preparation; it never uses client prepare fallback. Procedure/output/array and multi-statement combinations are rejected before sending the target. It can be combined with SchemaOnly, SequentialAccess, SingleRow, SingleResult and CloseConnection. Batch supports KeyInfo with SchemaOnly, SequentialAccess, CloseConnection and batch-wide SingleRow/SingleResult. See the [batch metadata contract](#batch-metadata).

For a provably row-preserving single-base-table SELECT, the driver reads the key catalog on the same reserved physical session and transaction before target execution or long-data upload. Catalog filters are parameters. It chooses the complete primary key, otherwise a deterministic complete nonnullable unique key. A nullable single-column unique constraint sets IsUnique=true and may keep AllowDBNull=true, but is not selected as IsKey; composite members are not individually unique. Oracle effective P/U constraints use ALL_CONS_COLUMNS, independently of their backing index, with disabled/unvalidated/deferrable constraints excluded. Eligible standalone normal valid unique indexes are separate candidates. MySQL prefix/functional index members disqualify that entire candidate.

Missing members of the selected row key are appended as hidden columns at the end of the SELECT projection, using quoted source identifiers and collision-free aliases. Server preparation supplies their actual types; existing result ordinals stay fixed. FieldCount includes hidden columns, VisibleFieldCount counts the original projection, and both GetColumnSchema and GetSchemaTable report IsHidden/IsKey/IsUnique. Hidden fields remain readable by ordinal. The original CommandText and original prepared-statement cache are preserved; the rewritten statement belongs to the current execution and is queued for closure after its response/cursor lifetime. A new catalog snapshot is obtained on each execution. Changes in key-source ordinal metadata cause failure instead of publishing stale keys.

The recognizer supports a single named source, optional schema/alias, simple projection expressions without parentheses, WHERE (including nested filters), ORDER BY without functions, LIMIT/OFFSET/FETCH and FOR clauses. It handles quoted identifiers, comments and Oracle Q/NQ literals. DISTINCT/UNIQUE, grouping/aggregation, joins, set operations, CTEs, derived sources and other ambiguous shapes keep their original SQL/result shape and unknown IsKey/IsUnique. Views, synonyms, unavailable directory identities and ambiguous MySQL SQL_MODE-dependent quoting likewise receive no invented row key or hidden additions. Missing provenance alone does not prove IsExpression; updateability remains unknown. Normal execution uses actual result metadata for unenhanced queries, including parameter expressions whose execution-time types differ from preparation.

Directory parsing is bounded to 4096 rows / 4 MiB of string content; hidden additions respect the 4096-column and command packet limits. Errors or cancellation during preparation or directory lookup propagate before executing the target; directory lookups do not set HasSentSql or an unknown target outcome. Cancellation after sending the target retains the ordinary unknown-outcome rules. SchemaOnly still performs no target execution/fetch/parameter upload, although preparation and directory queries can check permissions and take metadata locks. This bounded provider contract has local fixture coverage; actual OceanBase/OBProxy dictionary SQL, DDL/lock races and supported-version/permission matrices still require real acceptance.

### Batch metadata

Modern DbBatch assets (net8.0/net9.0/net10.0) accept SchemaOnly and KeyInfo, individually or combined
with SequentialAccess/CloseConnection. Each entry retains the command text/input-only, single-statement
and server-prepare restrictions. SchemaOnly prepares the target without executing it, uploading/encoding
input values or client fallback. KeyInfo directory queries and optional hidden-key preparation use the
batch's reserved physical session. SchemaOnly may still require directory reads; server parsing,
permissions and metadata locks remain possible. KeyInfo without SchemaOnly executes the target normally.
Hidden columns and VisibleFieldCount follow the command's bounded key contract.

NextResult advances lazily to the next entry. The reservation persists even after metadata is exhausted,
until the batch reader closes. Early close skips unstarted entries. Metadata failure releases the
reservation; server errors identify BatchCommand. Preparation/catalog failure does not imply target
execution. Pending-metadata cancellation/timeout can break the session. No replay or implicit transaction
is added. Batch-wide SingleRow/SingleResult limits are described [below](#batch-result-limits).

OceanBaseDataSource.CreateBatch returns a provider batch with an owned connection and supports direct
CreateBatchCommand. Each execution opens its connection; reader closure closes or returns that lease.
Scalar/nonquery executions drain their results. Failure and disposal also clean up the owned
connection. Connection/Transaction getters/setters and standalone Prepare remain unsupported. Open may
join the ambient single-resource transaction. The source command timeout is the batch default. Normal
connection batches keep caller-owned connection behavior. Standard assets expose no modern batch API.
See [local evidence and remaining acceptance](history/obconnector-dotnet-completeness.md#1560-2026-10-26-batch-元数据与数据源条目创建).

### Oracle national inputs in text multi-statements

Explicit NChar/NVarchar2 Input parameters use OceanBase Unicode literals: each validated UTF-16
code unit becomes an ASCII `\hhhh` escape inside `u'...'`, following Connector/J OBNStringParameter.
This preserves supplementary characters, quotes, backslashes and NUL even outside the configured
client charset. Empty national strings have the server's SQL NULL semantics. Both provider types
produce NCHAR expression metadata; they do not force the binary parameter expression type.
Unicode validity, Size, zero Precision/Scale and combined SQL packet limits are checked before
sending the script. Expanded literals are bounded before allocation, including repeated bindings.
Ordinary VARCHAR2/RAW literal limits do not apply to national literals. Text execution skips the
unused binary national parameter encoder; binary paths retain their encoding checks. Result decoding
still requires fresh trusted national state and supported explicit encoding mappings, including for
later results. National input support does not widen national result/storage or complex-type scope.
See the [OceanBase/Java and direct-test evidence](history/2026-10-29-multi-statement-national-input.md).

Explicit trusted national/result names additionally resolve through the existing strict client
mapping for utf8mb3, ascii, latin1, gbk, gb18030, gb18030_2022, dec8, tis620, sjis, big5, hkscs and
hkscs31. Scalar binary input and ordinary/PL national results share this resolver; streaming uses
the underlying stateful decoder. Missing facts retain Unicode-only collation fallback and later
results require fresh state. Stored complex members still require AL16UTF16. See the
[encoding evidence and live scope](history/2026-10-29-national-character-encodings.md).

### MySQL client string preparation

Automatic prepare remains the first attempt. Only a complete first-response 1295/HY000 permits the
existing opt-in fallback. A fallback containing a non-NULL MySQL string queries
`@@character_set_client`, `@@character_set_connection`, `@@collation_connection` and `@@sql_mode`
inside the same exchange, before target execution. Every such fallback reads fresh state: no session,
pool, reset or prepared-cache state is assumed. Numeric/binary/NULL-only and Oracle fallback do not
issue this query. Explicit Prepare, SchemaOnly, KeyInfo, procedure/output/array plans and server
cursors retain their existing exclusions. Supported ordinary string DbTypes retain their existing
type validation; this is not a national/special type expansion.

Default unmanaged UTF-8 accepts the existing UTF8/UTF8MB3/UTF8MB4 names. A managed client charset
must match both fresh client and connection names; all supported non-UTF8 input encodings,
including the seven DBCS families, now use their strict configured mapping. Connection collation
must belong to that family. Names and comma-separated SQL modes are validated as bounded identifiers.
Four ordinary string columns and exactly one row are required. Their supported reported source
encoding decodes the state strictly. NULL, malformed/oversized values and extra rows/results fail
closed. Valid but unsupported state is rejected after the result boundary is consumed, allowing
another command on that session; an incomplete or corrupt response retires the session. Discovery
invalidates cached client-charset trust if either fresh input charset differs from the managed
configuration, so a subsequent payload must first pass the ordinary four-setting verification.
Discovery uses the original command timeout/cancellation, transaction, physical session and frozen bindings.
Its failure does not claim unknown execution of the target, which has not yet been sent.

Strings use ordinary parenthesized single-quoted literals, without introducers, binary conversion
or explicit COLLATE, so session string collation and ordinary literal coercibility apply. Quotes
are doubled. With backslash escapes active, backslash, NUL, backspace, tab, LF, CR and Ctrl-Z are
escaped; with NO_BACKSLASH_ESCAPES, backslashes and controls are retained and quotes doubled. NUL
in that mode is explicitly unsupported. Encoding is strict; escaping Unicode characters before
encoding preserves DBCS trail5C bytes inside their multibyte character. If either endpoint charset is
UTF8/UTF8MB3, supplementary characters in a parameter or the original SQL are rejected instead of
replaced. A BOM and trailing spaces are preserved. No charset session mutation is introduced.
Non-UTF8 unaliased expression names can arrive in mixed/ambiguous encodings on the observed
server; the existing strict UTF-8 metadata contract remains, so use explicit ASCII result aliases.
Local protocol fixtures exercise automatic fallback. Real generated-literal parsing reports are
separate and do not prove actual server1295 fallback or all deployed mode/collation/proxy profiles.

SQL marker positions must agree in all four backslash/ANSI_QUOTES combinations. String fallback
allows one statement, with an optional terminal semicolon followed only by whitespace/comments;
multiple statements, executable comments, NUL in SQL and ambiguous quoting are rejected. This
prevents earlier statements from changing the snapshot's parsing/encoding assumptions. The existing
64MiB request budget includes the selected encoding's bytes, parentheses and escaping across every replacement.
Ordinary query errors and known/unknown target execution boundaries retain their existing rules;
there is no execute-error fallback or replay. This local implementation does not establish real
server literal/parameter collation parity, all SQL modes, non-UTF-8 clients, proxy or performance
compatibility. Fallback literals can be visible in server SQL logs.

### Batch INSERT rewrite

Modern `OceanBaseBatch.RewriteBatchedInserts` defaults to false. When enabled, only ExecuteNonQuery
and ExecuteNonQueryAsync combine adjacent ordinal-identical MySQL Text commands containing one
`INSERT INTO ... VALUES (tuple)`. Parameters remain server-bound in original row/parameter order;
values are never interpolated into SQL. Quoted identifiers, ordinary comments and nested expressions
are retained; one trailing semicolon is removed. Tuple boundaries and every marker position must agree
with/without backslash escapes and ANSI_QUOTES. IGNORE, duplicate-key updates, RETURNING, SELECT/SET,
multiple tuples/statements, executable comments and ambiguous/unsupported syntax fall back sequentially.
The conservative parser also falls back above262144 lexical tokens. Oracle, reader/scalar/Prepare
and default batches retain their sequential paths. Only Input parameters are eligible. Every row
containing a Stream is a singleton boundary: planning never reads or disposes caller streams.

Chunks contain at most65535 rows/parameters, with independent64MiB budgets for SQL in the selected client encoding including
its command byte and inline EXECUTE including its10-byte header, combined NULL bitmap, types and
encoded values. Planning validates/encodes one row at a time; it does not retain all encoded rows.
Actual server/proxy max_allowed_packet may be smaller. Original batch snapshots still retain the
whole batch; these limits do not promise bounded overall batch memory or a throughput improvement.
One reservation, the captured physical session/Open generation, explicit or ambient transaction and
caller cancellation origin remain in force. The active temporary command is reachable by Dispose/
DisposeAsync before its reader is available. No implicit transaction or automatic replay is added.

On successful group total==rows, each original RecordsAffected is1. Otherwise individual counts
remain-1 and ExecuteNonQuery retains the independent server total, including zero. Confirmed prefix
counts survive a failure; failed group and unstarted suffix counts remain-1. A merged OceanBaseException
has BatchCommand=null and BatchCommandGroup containing a read-only snapshot of the exact original
command references in order. Commands themselves remain caller-owned/mutable after execution.
For unmerged errors BatchCommand retains its existing attribution and BatchCommandGroup is null.
Sent cancellation/timeout/transport failure preserves the existing unknown-outcome rule; no group
index or count is a safe restart position. Failure callbacks retain the merged group's reservation
until outer cleanup; SQL/binds/credentials are omitted from error messages.

Opting in changes statement atomicity and autocommit boundaries, default/expression/trigger/warning
behavior, and timeout granularity (one command budget per merged group). Per-entry semantic equivalence
is not promised. Applications needing whole-batch atomicity must supply a transaction. Local protocol,
fault and target-asset tests do not establish real SQL semantics, performance, proxy or deployment
compatibility. Multi-send/pipelining and a separate bulk protocol remain unimplemented.

### Batch result limits

Modern batches support SingleResult and SingleRow with the existing metadata, SequentialAccess and
CloseConnection flags. The limit applies to the entire batch. SingleResult exposes only its first
result; SingleRow exposes only the first row of that result and implies SingleResult. A first empty
result or affected-count response does not cause the reader to search later results for a row.
Ordinary command SingleRow uses the same first-result rule; its previous per-result behavior was a defect.

NextResult drains all hidden results, starts and consumes remaining entries sequentially, and returns
false on successful exhaustion. A hidden error or cancellation propagates with the ordinary BatchCommand,
unknown-outcome and cleanup rules. Successful counts and procedure outputs are retained, including hidden
results. Limits do not rewrite SQL, add an implicit transaction or replay failed operations.

Read returning false only finishes the current result. To process remaining batch entries, call
NextResult even though it will not expose another result. Early Close keeps the existing lazy contract:
unstarted entries are skipped. An unread current response can destroy its session. CloseConnection
still belongs to the original logical lease; data-source batches still own that connection.
SchemaOnly with limits may prepare hidden entries and query their metadata catalogs, but executes no
target SQL. The batch reservation remains until reader close. See [verification and open acceptance](history/obconnector-dotnet-completeness.md#1561-2026-10-26-singlerowsingleresult-跨批次结果限制).

### CRUD generation scope

DataAdapter/CommandBuilder generation is outside the supported OceanBase/Connector-J
scope. Use explicit `OceanBaseCommand` instances for CRUD, transactions, generated values
and complex bindings. `KeyInfo` remains available for bounded metadata inspection, but it
does not imply automatic update/delete generation.

### Binary input streams

A readable `Stream` can be an Input parameter, inferred or explicitly `DbType.Binary`; `Size`, `Precision` and `Scale` must be zero. The driver reads from the current position to EOF without rewinding or disposing the source. A stream cannot occupy multiple binding positions.

The encoded execution request is limited to 64 MiB; Oracle upload bytes count after hexadecimal expansion. Streams cannot be combined with array binding or client-prepare fallback. Async reads receive the command cancellation/timeout token; a source that ignores it must return before interruption completes, and the lease remains busy until then.

With `Allow Multi Statements=true`, binary Input streams are encoded completely before target SQL is sent: MySQL uses X'HEX', Oracle uses 'HEX' with implicit RAW/BLOB conversion, matching Connector/J. Oracle stream expressions consequently have the quoted-hex text shape; they are not the byte-array HEXTORAW expression. Both modes count hex expansion and all other literals toward the 64 MiB SQL limit. Validation, source failure, overflow or cancellation during materialization sends no target SQL and retains a usable session when no initialization response is pending. The driver does not rewind, dispose or replay a source. See the [stream module evidence](history/2026-10-29-multi-statement-stream-input.md).

### Exact values

| Database value | CLR / provider access |
| --- | --- |
| DECIMAL / NUMBER | `decimal` for reliable precision 1–28 and nonnegative scale within precision; otherwise `OceanBaseDecimal`. `GetProviderSpecificValue` preserves `OceanBaseDecimal` for either shape. |
| Oracle FLOAT (NUMBER_FLOAT206) | Always `OceanBaseDecimal`, with Decimal provider identity; binary precision is distinct from decimal parameter Precision. |
| Oracle DATE | `DateTime` with `Kind=Unspecified`, including time of day. |
| Oracle TIMESTAMP | `DateTime` for declared precision <=7; `OceanBaseTimestamp` for 8/9. Provider-specific access preserves nanoseconds at every precision. |
| Oracle TIMESTAMP WITH TIME ZONE | `OceanBaseTimestampWithTimeZone`; exact explicit conversion to `DateTimeOffset`. |
| Oracle TIMESTAMP WITH LOCAL TIME ZONE | `OceanBaseTimestampLocalTimeZone`, retaining returned local fields and session zone. |
| Oracle INTERVAL | `OceanBaseIntervalYearToMonth` / `OceanBaseIntervalDayToSecond`. |
| Oracle NCHAR / NVARCHAR2 | `string`; explicit `OceanBaseDbType.NChar` / `NVarchar2` for binding. |
| MySQL DATE / DATETIME / TIMESTAMP | Normal values use `DateTime`. Provider-specific `OceanBaseDate` / `OceanBaseDateTime` preserves zero components; conversion to `DateTime` rejects them. |
| MySQL YEAR / BIT / JSON | `ushort` / `ulong` / JSON text as `string` (UTF-8 results; selected client encoding for string parameters). SQL NULL remains distinct from JSON text `null`. |

Conversions requiring rounding or loss of precision throw. Timezone-free values do not acquire the client's local timezone.

### Oracle NUMBER results with unknown precision

Oracle may report precision zero and scale -1 for both a bound expression and a declared `NUMBER(*,-1)` column or CAST. This metadata cannot establish an exact client-side result constraint. When Oracle result precision is unknown, text and binary readers preserve the exact returned value, including its coefficient and value scale, and still enforce Oracle NUMBER significant-digit and exponent limits. Known positive precision retains exact declared precision/scale checks; MySQL result behavior and parameter declaration validation retain their existing rules.

Schema reports the server metadata (`NumericPrecision=null` when unknown and the advertised `NumericScale`); it does not substitute the value's scale into the column definition. Unknown-precision results remain `OceanBaseDecimal`, and typed `decimal` conversions remain exact. The value representation can change: returned text `"40"` now has coefficient 40 and value scale 0 rather than being rescaled to coefficient 4/scale -1. `GetProviderSpecificValue`, `GetValue` and typed reads agree numerically. This does not undo server-side rounding of a declared column: a server-rounded value of 40 stays 40.

Direct Oracle 4.4.2.2/Windows/.NET10/TLS-disabled evidence covers prepared integer/fractional bindings and declared/cast negative scales. It does not establish other versions, OBProxy, TLS or independent-runtime compatibility. See the [module record](history/obconnector-dotnet-completeness.md#1537-2026-10-25-oracle-未知精度-number-结果读取).

### Oracle FLOAT results with binary precision

Oracle result206 is FLOAT, a NUMBER subtype. Both row protocols carry length-encoded
decimal text. `GetValue`, `GetFieldValue<OceanBaseDecimal>` and provider-specific access
preserve its exact coefficient and value scale; `GetDecimal` converts exactly or throws.
This is distinct from the IEEE BINARY_FLOAT/BINARY_DOUBLE result types4/5.

`GetDataTypeName` reports `FLOAT`; the provider type is `OceanBaseDbType.Decimal`.
Column schema exposes the server's **binary** NumericPrecision1..126 (null for0/255)
and null NumericScale. Wire scale-85/-1 means variable scale; other206 scale shapes,
precision127..254 and MySQL206 are rejected, including SQL NULL. Binary precision
does not constrain decimal digits or rescale the value. Existing Oracle NUMBER
significant-digit/exponent validation still applies; nondecimal/special tokens fail.

DataTypes advertises `FLOAT({0})` with binary precision1..126, no fixed scale and
OceanBaseDecimal. Explicit command rebinding uses existing
NUMBER246 binding with no copied binary precision/scale. A manually supplied parameter
Precision continues to mean decimal digits. There is no new FLOAT parameter enum,
IEEE conversion or new complex member codec. Routine derivation accepts FLOAT and
NUMBER_FLOAT using the existing Decimal binding described below.
Scalar PL outputs and existing NUMBER-array output metadata can also receive206.

Source contract is pinned to OceanBase
[`5d6cb5c`](https://github.com/oceanbase/oceanbase/tree/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5):
[`ObNumberFloatType`](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/deps/oblib/src/common/object/ob_obj_type.h)
belongs to ObNumberTC;
[`ObSMUtils`](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/src/observer/mysql/obsm_utils.cpp)
maps it to206 and calls
[`number_cell_str`](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/deps/oblib/src/rpc/obmysql/ob_mysql_util.cpp),
which formats decimal text and stores its length without a protocol branch.
[`ob_define.h`](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/deps/oblib/src/lib/ob_define.h)
defines precision1..126/unknown scale-85, and the
[`resolver`](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/src/sql/resolver/ob_resolver_utils.cpp)
sets the unknown scale.
[`ObMySQLField`](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/deps/oblib/src/rpc/obmysql/ob_mysql_field.cpp)
serializes precision and scale into Oracle metadata bytes. These sources and local
protocol fixtures do not establish a real deployment/version/proxy compatibility pass.

### Oracle floating special text results

Oracle BINARY_FLOAT/BINARY_DOUBLE text results additionally accept the complete case-insensitive
tokens `Inf`, `-Inf` and `NaN`, returning IEEE positive infinity, negative infinity and NaN. Type4
remains Single and type5 Double, including typed getters and provider metadata. Finite Single values
still parse directly as Single to avoid double rounding; signed zero is preserved. Existing CLR
floating parsing formats remain available. MySQL text parsing, IEEE binary rows and parameter
encoding keep their existing contracts. Real server emission and SQL interoperability remain unverified.

### Result character sets

Ordinary string result columns select a strict decoder from their reported collation ID. The same decoder serves text/binary results, buffered/sequential reads, `GetString`, `GetChars` and `GetTextReader`; SQL NULL remains DBNull and binary collation 63 remains bytes. Metadata names retain UTF-8. SQL and ordinary string parameters use the configured [client Character Set](client-character-sets.md), default utf8mb4; result decoding remains a separate contract.

Result wire codes 249/250/251 identify the TINY/MEDIUM/LONG BLOB family; 252 retains BLOB/TEXT.
Binary collation 63 returns byte arrays and byte streams; other supported collations return strictly
decoded text. `GetDataTypeName` distinguishes each BLOB/TEXT family, schema marks them `IsLong`, and
provider identities remain Binary/String. They share the existing length-encoded buffered/sequential
paths, including empty and NULL values. MySQL DataTypes lists the six additional SQL declarations;
Oracle's SQL inventory retains its separate locator-based BLOB/CLOB contract. Accepting these shared
wire codes in either mode does not establish that a particular server emits them.

Wire codes 247/248 report ENUM/SET. Collation 63 returns bytes; other supported collations use the
same strict string decoder, without splitting label lists, trimming or CLR enum conversion. These
shared wire mappings do not add Oracle ENUM/SET SQL declarations. MySQL DataTypes uses String and
`ENUM({0})`/`SET({0})` with a caller-supplied SQL label list. Wire code 255 is MySQL-only GEOMETRY:
readers return complete opaque bytes for any collation, preserving prefixes and payload without
SRID/WKB conversion. It adds no spatial API, geometry parameter code or GEOMETRY SQL inventory row.
Empty/NULL and buffered/sequential text/prepared results reuse existing framing. Server emission and
SQL/spatial interoperability require real acceptance; local fixtures do not establish them.

Oracle ROWID result code209 returns an unchanged string through the existing String provider identity.
It uses the strict session UTF-8 decoder, including when binary or unknown collation is reported;
the identifier is not parsed or normalized. Text/prepared buffered/sequential readers share the
ordinary length-encoded path, with empty and NULL values preserved. MySQL rejects this code. No new
ROWID CLR type, parameter wire code or SQL inventory declaration is added. Actual server metadata,
ROWID SQL/parameter behavior and deployment compatibility require real validation.

| Result family | Accepted collation IDs | Maximum bytes per character |
| --- | --- | ---: |
| ASCII | 11, 65, 331, 346, 361 | 1 |
| DEC8 | 3, 69, 338, 353, 368 | 1 |
| TIS620 | 18, 89, 332, 347, 362 | 1 |
| SJIS | 13, 88, 334, 349, 364 | 2 |
| Big5 | 1, 84, 335, 350, 365 | 2 |
| HKSCS | 152, 336, 351, 366 | 2 |
| HKSCS31 | 153, 337, 352, 367 | 2 |
| MySQL latin1 (Windows-1252) | 5, 8, 15, 31, 47, 48, 49, 94, 329, 344, 359 | 1 |
| GBK | 28, 87, 326, 341, 356 | 2 |
| GB18030-2005 | 248, 249, 251, 328, 343, 358 | 4 |
| GB18030-2022 | 216–222, 330, 345, 360 | 4 |
| UTF-16 big endian | 54, 55, 101–124, 327, 342, 357 | 4 |
| UTF-16 little endian | 56, 62, 333, 348, 363 | 4 |
| UTF8MB3 | 33, 83, 192–215 | 3 |
| UTF8MB4 | 45, 46, 224–247, 255–323 except 272/276/295/299/301/302; 325, 340, 355 | 4 |

OceanBase IDs follow `v4.3.5_CE` charset definitions; legacy UTF8MB3 IDs additionally follow MySQL's [33/83 definitions](https://github.com/mysql/mysql-server/blob/mysql-8.0.44/strings/ctype-utf8.cc) and [192–215 definitions](https://github.com/mysql/mysql-server/blob/mysql-8.0.40/strings/ctype-uca.cc#L6085). The official `System.Text.Encoding.CodePages` dependency supplies CP1252/936/54936 without process-global `Encoding.RegisterProvider`. MySQL latin1 includes CP1252 C1 mappings; it is not ISO-8859-1. GBK/GB18030 reject illegal single bytes 80/FF and malformed/truncated multibyte input. GB18030 corrects CP54936's older mapping: A8BC decodes U+1E3F and 8135F437 decodes U+E7C7, including streamed reads. A BOM is a returned character, not stripped as a transport marker.

GB18030-2022 applies the18 reversible PUA/assigned-scalar swaps specified by
[OceanBase init_gb18030_2022](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/deps/oblib/src/lib/charset/ob_ctype_gb18030.cc#L1427),
in addition to the2005 U+1E3F correction. Its ten IDs follow the same pinned
[v4.3.5_CE header](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/deps/oblib/src/lib/charset/ob_charset.h#L139).
The existing2005 encoding remains separate. Whole fields and incremental text readers share the
strict decoder and reversible mapping; no process-global registration or new option is needed.
Independent exhaustive BMP encoding and two-byte/BMP four-byte decoding digests are reproducible
with `java tests/fixtures/character-sets/Gb180302022Reference.java` on OpenJDK21 with2022 defaults.
Tests do not require Java at runtime. This result-only addition does not negotiate a client charset,
change SQL/parameter encoding or expand national/JSON/CLOB rules. Actual server emission, storage and
version/OBProxy/TLS/runtime interoperability require separate real acceptance.
The strict decoder rejects malformed/reserved sequences; it does not reproduce the server's lenient
replacement behavior for otherwise unassigned byte ranges.

DEC8 and TIS620 use the fixed server's
[DEC8 tables](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/deps/oblib/src/lib/charset/ob_ctype_extra.cc#L91)
and [TIS620 table](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/deps/oblib/src/lib/charset/ob_ctype_tis620_tab.h#L678),
with identity mappings for00–9F, including C1 controls. DEC8 is not Latin1: A8 maps to U+00A4,
D7/DD/F7/FD to U+0152/U+0178/U+0153/U+00FF. TIS620 is not Windows874: byte80 remains U+0080.
Following the strict result policy, the14 DEC8 zero placeholders outside NUL and nine TIS620 FFFD
placeholders throw DecoderFallbackException; the latter deliberately rejects the server table's
replacement output. Defined bytes round-trip; unrepresentable characters throw EncoderFallbackException.
The stateless decoder serves whole fields and streaming reads; both families report width1.
The optional `python tests/fixtures/character-sets/SingleByteReference.py ob_ctype_extra.cc ob_ctype_tis620_tab.h`
command regenerates independent all256-byte SHA256 expectations from downloaded pinned source files:
each position contributes a two-byte big-endian scalar, or FFFF for an undefined position. Python and
server sources are not test/runtime dependencies. Client encoding and national/JSON/CLOB rules retain
their separate scope. Real emission/storage and deployment compatibility remain unverified.

SJIS uses CP932 with the fixed server's seven corrections from
[ob_mb_wc_sjis and its table](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/deps/oblib/src/lib/charset/ob_ctype_sjis.cc#L1438).
Bytes815F/8160/8161/817C/8191/8192/81CA decode U+005C/U+301C/U+2016/U+2212/U+00A2/U+00A3/U+00AC.
ASCII00–7F and halfwidthA1–DF remain single bytes; all6879 assigned double-byte positions are supported.
Windows extensions under lead87 or EB–FC, invalid singles/trails, unassigned pairs and incomplete leads
throw DecoderFallbackException. The incremental decoder retains its lead across reads; count probes
do not consume state and Reset clears it. Both byte5C and pair815F decode to backslash; the server's
canonical reverse encoding of U+005C is815F. Reverse encoding rejects Windows-only scalars/aliases
and unrepresentable characters instead of changing their meaning. This internal result codec does
not change client SQL/parameter encoding or add general SJIS client negotiation.

The optional `python tests/fixtures/character-sets/SjisReference.py ob_ctype_sjis.cc` command regenerates
independent fixed-source SHA256 expectations. Decoding covers all256 singles and16640 possible lead/
trail positions (leads80–A0/E0–FF), hashing input bytes followed by a four-byte big-endian scalar or
FFFFFFFF for failure. Encoding covers all65536 BMP positions, hashing a two-byte scalar then four-byte
canonical wire code or FFFFFFFF for failure. Unit tests require neither Python nor downloaded sources.
These local mappings and reader tests do not establish actual server emission/storage or deployment
compatibility; national/JSON/CLOB contracts retain their scope.

Big5 supports all13710 defined BMP double-byte assignments and ASCII in the fixed
[source converter](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/deps/oblib/src/lib/charset/ob_ctype_big5.cc#L242)
and [mapping tables](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/deps/oblib/src/lib/charset/ob_ctype_big5_tab.h#L1078).
A lazy, shared immutable lookup derives from strict CP950 plus15 scattered corrections and249 C6/C7
assignments for kana/Cyrillic/circled forms. It also restores four box-drawing pairs absent from strict
Windows decoding. No resource file or new dependency is needed. It deliberately rejects the server
table's seven FFFD placeholders (A15A/A1C3/A1C5/A1FE/A240/A2CC/A2CE), zero holes and Windows extensions,
following the existing strict result policy. Single bytes80–FF and incomplete leads also fail.
All13838 defined characters, including ASCII/NUL, have exact reverse mappings; Windows private-use
aliases and unrepresentable UTF-16/supplementary characters throw EncoderFallbackException.
The decoder retains one lead byte; count probes, invalid flush and output overflow do not consume it.
Reset clears it. MySQL width metadata uses2, while Oracle retains its ordinary character-size rule.

The optional `python tests/fixtures/character-sets/Big5Reference.py ob_ctype_big5_tab.h` command regenerates
independent fixed-source SHA256 expectations for256 singles,32768 high-byte pairs and all65536 BMP
encoding/refusal positions. Digest serialization follows the SJIS description above, with all leads
80–FF and explicit FFFD refusal. Tests do not require Python or source downloads. This ordinary-result
addition does not negotiate a client charset or expand national/JSON/CLOB encoding. Actual server
emission/storage and deployment interoperability remain unverified.

HKSCS and HKSCS31 use distinct lazy scalar lookups derived from strict CP950 and compact numeric deltas
from the pinned [converter](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/deps/oblib/src/lib/charset/ob_ctype_hkscs.cc),
[HKSCS table](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/deps/oblib/src/lib/charset/ob_ctype_hkscs_tab.h)
and [HKSCS31 table](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/deps/oblib/src/lib/charset/ob_ctype_hkscs31_tab.h).
The old family needs3186 BMP corrections and62 undefined-slot removals. The revised family replaces
1651 old BMP private-use assignments with Plane2 scalars. Each supports19848 defined wire positions,
including128 ASCII positions, decoding to19754 distinct scalars. Source FFFD placeholders, invalid
single bytes80/FF, malformed pairs and incomplete leads fail explicitly. The source's66 nonminimal
canonical reverse choices and asymmetric U+2593-to-F9FE alias are preserved: F9FE decodes to U+FFED.
Only represented scalars encode; isolated surrogates and unrepresentable characters fail.

Incremental decoding counts UTF-16 units and emits surrogate pairs for revised supplementary scalars.
Count probes, invalid flush and output overflow preserve the pending lead; Reset clears it. A
supplementary scalar requires two output slots; a one-slot Convert refusal permits retry. The revised
GetMaxCharCount includes one extra UTF-16 unit for a saved lead plus one new trail. Wire width remains2
in both families, independently of UTF-16 output length.

`python tests/fixtures/character-sets/HkscsReference.py ob_ctype_hkscs_tab.h ob_ctype_hkscs31_tab.h`
independently regenerates both families' decode/BMP/Plane2 SHA256 expectations from full source tables.
Decoding uses the Big5 digest serialization above; Plane2 encoding hashes a four-byte scalar then
four-byte wire code/failure for20000–2FFFF. Optional `ExportWindows950.ps1 -Path windows950.bin` plus
`--windows950 windows950.bin --output HkscsMappings.cs` regenerates compact product deltas. Neither
Python nor source files are runtime/test dependencies. This remains ordinary-result support; client
SQL/parameter, national, JSON and CLOB contracts retain their scope. Actual emission/storage and
deployment interoperability require real-server evidence.

Unknown collation IDs, enum holes and other unimplemented families throw NotSupportedException; malformed supported data throws DecoderFallbackException instead of replacement. MySQL ordinary character ColumnSize divides wire length by the family's maximum width; Oracle length semantics are unchanged. This is a metadata bound, not a measured string length or Unicode grapheme count. JSON results and CLOB reads retain their UTF-8 scope. Explicit MySQL JSON parameters use the selected strict client encoding and ordinary string binding; the server owns JSON conversion, syntax and storage. CLOB write buffers inherit the selected client parameter encoding and its representability checks; storage interoperability is not expanded. Trusted Oracle national character-set selection retains precedence; explicit names reuse the strict client mappings, while missing names retain Unicode-only collation fallback. See [client charset and cached session trust](client-character-sets.md).

Real evidence is limited to direct OceanBase 4.4.2.2 MySQL mode, Windows/.NET10 with TLS disabled: six charset cases, each exercising eight text/prepared × buffered/sequential × sync/async combinations. That server rejects `SET character_set_results=utf16` and `utf16le` with 1231/42000; those two cases instead use `character_set_results=NULL` and `CONVERT` expressions, asserting string metadata and exact decoded text. This does not establish Oracle, proxy, TLS, all collation IDs, other server versions or independent-runtime compatibility. See [verification and retained failures](history/obconnector-dotnet-completeness.md#1532-2026-10-25-普通结果字符集).

### National collation fallback

When trusted national and result character-set names are absent or `binary`, Oracle national
results accept all known UTF-8 and UTF-16 big-endian collation IDs from the table above. The same
family selection decodes the three charset-name columns during session discovery. It retains strict
decoders and content BOMs. Other families, UTF-16 little endian, binary63 and unknown IDs are rejected
on this fallback path. A trusted National name takes precedence over Results, which takes precedence
over collation; explicit session names can therefore override these fallback restrictions. Parameter
and complex-member input encoding choices retain their existing rules. Local mapping and bootstrap
regressions do not establish server national storage, negotiation or deployment compatibility.

### Decimal parameter scale

`OceanBaseParameter.DecimalScale` is an optional signed `int?` declaration for decimal parameters, including Oracle NUMBER negative scale. The setter accepts -84..127; encoding validates Oracle precision <=38/scale -84..127 or MySQL precision <=65/scale 0..30, with scale <= nonzero Precision in MySQL mode. A declaration on a non-decimal parameter is rejected, including typed NULL, complex values and LOB/cursor parameters. SQL NULL retains metadata validation.

| Assignment | Effective declaration |
| --- | --- |
| `parameter.Scale = value` | Sets the standard byte scale and clears DecimalScale. |
| `parameter.DecimalScale = value` with a non-null value | Clears the old standard Scale and uses the signed declaration. Scale reads the nonnegative declaration, or zero for a negative one. Read DecimalScale for the actual signed value. |
| `parameter.DecimalScale = null` | Removes the override. It does not restore a standard Scale overwritten by an earlier non-null assignment; with no declaration, scale is inferred from the value. |

Precision/scale constraints are exact: values requiring rounding throw before execution. For example, `Value=12300m`, `Precision=3`, `DecimalScale=-2` is representable in Oracle mode; `12345m` with that declaration throws. A negative scale is not accepted in MySQL mode. Parameter snapshots and the supported client-prepare numeric fallback preserve the same declaration and constraints. These parameter declarations do not determine result metadata; see [unknown-precision Oracle results](#oracle-number-results-with-unknown-precision). Temporal Scale and default parameter value inference are unchanged. Routine parameter derivation below reuses this declaration. Local wire tests do not establish real-server NUMBER interoperability.

### Routine parameter derivation

All five assets expose these OceanBaseCommand methods:

```csharp
void DeriveParameters(string? overload = null);
Task DeriveParametersAsync(CancellationToken cancellationToken = default);
Task DeriveParametersAsync(string? overload, CancellationToken cancellationToken = default);
```

They require CommandType.StoredProcedure and an open idle connection. Bind the command to the current local transaction when one exists. They read the visible routine directory and its complete top-level signature without executing the target. MySQL execution has a separate [stored-procedure contract](#mysql-stored-procedures). Oracle execution still requires Use Oracle Prepare Execute=true and its existing type/direction rules.

MySQL accepts routine or database.routine, with optional backticks; an unqualified name uses DATABASE(). Oracle accepts routine, owner.routine or package.routine, and owner.package.routine, with double-quoted identifiers retaining case and unquoted identifiers converted to uppercase. Unqualified Oracle names use CURRENT_SCHEMA. Two-part Oracle names check both interpretations and reject ambiguity. The optional overload is an exact Oracle directory selector, not argument-based overload inference; MySQL rejects it. No matching visible routine, ambiguous signatures, incomplete metadata, unsupported type declarations or unavailable named descriptors fail explicitly. Synonym resolution is not promised.

Success replaces the parameter collection with fresh DBNull-valued parameters ordered by ordinal position; a function return is first and named RETURN_VALUE. Existing parameter objects are detached and their values are not copied. Failure/cancellation leaves the original collection unpublished; concurrent member changes are detected atomically and retained. Avoid changing individual parameter properties concurrently: that is outside the membership-version guarantee. The command/session/transaction context is checked before publication, and Execute/Prepare/another derivation cannot overlap. Valid complex descriptors loaded before a later failure may remain cached.

The operation reserves one physical session across routine, argument and named-type queries. It uses CommandTimeout for each query, with no total-duration guarantee. CancellationToken, Cancel and Dispose cover the operation and query gaps; a canceled in-flight request follows the existing connection-break/unknown-outcome rules. Directory permission errors retain the server error code and SQL state. Each routine/argument directory table is bounded to 1024 rows and 4 MiB of UTF-8 string content; named descriptors retain the existing complex-type member/depth/cache bounds.

Supported mappings and rejection boundaries are listed in the [Schema contract](schema.md#parameter-derivation). Directory, privilege, server-version and OBProxy interoperability require separate real-server validation.

Oracle FLOAT/NUMBER_FLOAT directory declarations accept binary precision1..126 or
SQL NULL/-1 (unknown), and scale SQL NULL/-85/-1 (variable). Dimensioned DATA_TYPE
strings and other precision/fixed-scale shapes fail. Derived Decimal parameters keep
Precision0 and undeclared scale, using exact NUMBER246 binding; the server's FLOAT
declaration applies coercion/rounding. MySQL FLOAT retains its existing Single mapping.
ProcedureParameters retains the directory type spelling and raw numeric metadata;
both IN/OUT and INOUT directory spellings normalize to INOUT.

Oracle argument discovery first omits CHAR_LENGTH. Only VARCHAR2/VARCHAR/CHAR/NCHAR/
NVARCHAR2 signatures require a second query for actual character lengths, on the same
reserved session/transaction and bound criteria. Every other signature field must
match before lengths are accepted. Size uses characters, never DATA_LENGTH bytes.
If CHAR_LENGTH is unavailable, character derivation retains the server error and
original parameter collection; numeric signatures do not require that column.

The pinned OceanBase5d6cb5c
[ALL_ARGUMENTS view](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/src/share/inner_table/ob_inner_table_schema.25051_25100.cpp)
exposes raw parameter precision/scale and INOUT, with no CHAR_LENGTH. Its virtual
type table uses
[inner_obj_type_str](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/deps/oblib/src/common/object/ob_obj_type.cpp),
which names ObNumberFloatType NUMBER_FLOAT. These source contracts and local tests
do not establish real DDL, derivation, rounding or old-server compatibility success.

<a id="mysql-stored-procedures-development-checkpoint"></a>

## MySQL stored procedures

Implemented from `1bb13cb`. The [module evidence](history/obconnector-dotnet-completeness.md#1530-2026-10-25-mysql-存储过程执行本地交付) separates local regression, limited direct-server runs and open deployment acceptance; the earlier development checkpoint retains original failures.

In MySQL mode, use CommandType.StoredProcedure with a one- or two-part routine identifier, parsed and quoted as identifiers. Procedure parameters bind in collection order, irrespective of their names. Input, Output and InputOutput are accepted for procedures. A stored function accepts one ReturnValue plus Input arguments only; multiple ReturnValue parameters and Output/InputOutput mixed with a function return are rejected. The function plan prepares `SET @private = function(?)` and retrieves the result through a hidden `SELECT`; the ReturnValue is published only after that query completes. Effective CLIENT_PS_MULTI_RESULTS is required. A positive command FetchSize is accepted and its value is preserved, with or without explicit Prepare. Routine execution uses effective fetchSize=0 and cursorFlag=0; ordinary results stream normally, and internal output SELECTs remain hidden and unpaged. This does not add routine-result cursor paging. SchemaOnly and KeyInfo are rejected; explicit Prepare is supported and client-side prepare fallback is disabled. Input streams use the long-data path; InputOutput streams are rejected. The plan accepts at most 65535 parameters and 4096 output parameters, subject to packet limits.

For procedure outputs, one transient binary-prepared SET initializes private per-command user variables on every execution: Output starts at SQL NULL and InputOutput uses its input snapshot. The binary-prepared CALL binds Input values and refers to these variables for outputs. For functions, the prepared SET is the business execution itself and binds only the Input arguments. After the terminal CALL or function response, a private text SELECT retrieves outputs; this result is hidden and excluded from affected-row totals. Outputs follow the SELECT column metadata and normal decoding, including user-variable temporal/string behavior; the declared parameter DbType does not force an output conversion. Values publish together only after a valid single-row output response completes successfully. Errors or cancellation preserve prior output values; the driver does not replay the procedure or function.

One exclusive exchange spans initialization, CALL results and output collection. Read/NextResult exposes ordinary CALL results; Close/Dispose drains the remaining procedure response and collects outputs, so early close can perform network I/O and can fail or time out. Closing a batch reader drains the active procedure and updates its affected count without executing remaining batch commands. Other command modes retain their existing close behavior. Procedure sessions retire on pool return; CALL preparations remain command-owned and do not enter the idle statement cache.

The initialization SET is SQL sent before the business CALL. A later transport failure can therefore conservatively carry unknown outcome even if CALL transmission has not been proved; do not infer business execution from that flag alone. A confirmed error from the output SELECT can follow a successfully executed CALL, so a confirmed SQL error does not imply the procedure had no side effects. Output publication does not imply transaction commit. Real proxy/TLS, failure, resource and independent-runtime acceptance remain open.

The statement-boundary scanner also recognizes MySQL CREATE PROCEDURE/FUNCTION/TRIGGER/EVENT program units, including OR REPLACE and supported DEFINER forms, so body semicolons do not require text multi-statement opt-in. This classifies a program unit; the server validates its grammar and suffix. The local grammar regressions and default-policy direct-server procedure run cover this boundary; other versions and proxies still require validation.

## Transactions, batches and failures

Assign the active transaction to each business command. MySQL supports ReadUncommitted, ReadCommitted, RepeatableRead and Serializable; Oracle supports ReadCommitted and Serializable. Unspecified isolation uses MySQL RepeatableRead or Oracle ReadCommitted. Opening inside an ambient `TransactionScope` enlists one physical session through the coordinator single-resource slot; commands/Batch use its local transaction. See the [ambient transaction contract](#ambient-transactions) for rejection and outcome rules. MySQL-mode sessions with `@@autocommit` disabled are rejected at Open; see the identity rules above.

On .NET 8/9/10, `Save(name)` and `Rollback(name)` operate on the active transaction; `Release(name)` is MySQL-only. Names must contain 1–30 ASCII letters/digits/underscores and start with a letter. Leading underscores, including `__EFSavePoint`, are rejected. Reacquire LOBs, cursors and complex-type values after savepoint rollback.

Batches execute sequentially by default; the [opt-in MySQL INSERT rewrite](#batch-insert-rewrite) combines eligible entries into statements. A later failure does not undo earlier statements; use an explicit transaction for transactional writes. `RecordsAffected` cannot identify a safe restart position.

| Result | Application action |
| --- | --- |
| `OceanBaseException.IsExecutionOutcomeUnknown=true` | SQL may have executed. Reconcile business state through a new connection before retrying. |
| Trusted server ERR | Inspect `ServerErrorCode` and `SqlState`; follow the remaining transaction/session state. A false unknown flag does not prove absence of side effects. |
| Confirmed commit/rollback followed by failed session cleanup | Retain the confirmed outcome and reconnect; do not repeat completed work. |

The driver never automatically replays sent SQL or moves an active transaction to another endpoint. Cancellation or a local timeout does not prove that server execution stopped.

### Ambient transactions

Local single-resource implementation and verification are recorded in the [module evidence](history/obconnector-dotnet-completeness.md#1527-2026-10-25-ambient-单资源事务与初始化取消本地交付). The earlier development checkpoint retains the original failures; real deployment acceptance remains open.

Open inside TransactionScope automatically enlists; EnlistTransaction explicitly enlists an already open, idle connection. EnlistTransaction(null), inactive transactions and conflicting local/XA/ambient contexts are rejected. Reenlisting the same logical transaction, including a clone, is idempotent on its active connection. A second database resource is rejected with NotSupportedException before its local transaction SQL, including when it names the same server. Failed automatic enlistment closes that logical connection; rejecting a second resource during explicit enlistment leaves that connection available outside the scope. An initialization error can instead break the physical session. Existing promotable or distributed resources cannot be joined, and subsequent promotion requests are rejected. This is local single-resource participation through IPromotableSinglePhaseNotification, with no durable prepare, multi-database atomic commit or distributed recovery.

The coordinator must accept the resource before Initialize begins database work. Initialization captures the connection Open generation; the generation check and acquisition of the local transaction exchange happen under the same connection lock. Close/Open while initialization is waiting rejects the old enlistment before SQL on the new session. Once initialized, completion targets the exact local transaction instance. Early Close rolls back/discards that local work; later Commit reports Aborted, and neither late Commit nor Rollback can operate on a reopened connection or the next borrower of its old pooled session. After successful enlistment, reopening after Close inside the unfinished ambient scope remains unsupported. An Open whose initialization failed can be retried in that scope after the failed resource and pool slot are released.

| Database/control result | Coordinator notification and connection behavior |
| --- | --- |
| Confirmed COMMIT | Committed; a later autocommit-restore failure breaks the session while retaining the confirmed commit |
| Trusted COMMIT rejection or completion before any COMMIT send (including a busy reader or early Close) | Aborted; remaining local work is rolled back when possible, otherwise the session is discarded |
| COMMIT sent without a trustworthy terminal response | InDoubt; Commit/scope completion raises TransactionInDoubtException with an OceanBaseException whose IsExecutionOutcomeUnknown is true; the session is broken and no replay occurs |
| Coordinator rollback, including rollback response loss | Aborted; failed or unread cleanup breaks the session; this does not prove an interrupted statement stopped on the server |

Initialization is synchronous even when OpenAsync triggers it. CommandTimeout limits each transaction control exchange; the TransactionScope timeout is not a wire-cancellation budget. If the coordinator timeout elapses while Initialize is waiting, the callback can wait until initialization returns, then rolls back; an initialization command timeout destroys that session and leaves no accepted resource slot. Automatic Open/OpenAsync enlistment passes the effective opening token, including caller cancellation and any existing route budget, into the synchronous initialization exchange. Cancellation while a control response is pending closes the transport; failed opening discards the session, clears the local/ambient state and releases the pool slot. OpenAsync preserves the caller token in its OperationCanceledException. Initialization completes and releases its token registration before successful Open returns; later cancellation or disposal of the caller CTS does not affect commit/rollback. Explicit EnlistTransaction has no cancellation parameter. A finite CommandTimeout bounds each control exchange independently of the scope timeout; the two Initialize control statements share one exchange budget, without resetting the timer per SQL. Callback outcomes are reported once, with no second notification if a coordinator notification itself throws. Local loopback and five-asset evidence do not establish real OceanBase/OBProxy, concurrent transaction-manager or independent runtime interoperability.

## Oracle extensions

### Oracle cursor-valued columns

A positive parent command FetchSize is accepted as a hint, but prepared cursor-valued columns
use cursorFlag=0 and stream the parent response without parent FETCH. Child cursor page sizes remain
independent. UseCursorOffset rejects this shape after final prepare metadata, before parameter upload
or target execution; this also applies to KeyInfo rewriting. SchemaOnly ignores these hints as before.
This follows the fixed Java source policy and does not prove real-server support for SELECT CURSOR.

Prepared ordinary Oracle cursor columns use the binary result protocol: call `Prepare()`/`PrepareAsync()`
for a plain parameterless command, or use an existing binary path such as KeyInfo, positive FetchSize or
server-prepared input parameters. Ordinary COM_STMT_EXECUTE cursor columns must declare type163 during
preparation; unowned unexpected cursor metadata is rejected and its session retired. Nested fetches and
procedure-owned ordinary results use their existing execution owner. Text ordinary results may also carry a cursor
column: the value must be exactly the same four-byte little-endian cursor identifier used by binary rows,
and the execution owner remains responsible for publication and cleanup. Other text shapes are rejected.
`Use Oracle Prepare Execute=true` is required for OUT parameters, not for an ordinary binary cursor column.

`GetValue`, provider-specific getters and compatible `GetFieldValue<T>` calls return the same single-use
`OceanBaseRefCursor` for aliases of an ID within an execution. NULL returns `DBNull.Value`. Consume the
entire parent response, including all results, before calling the cursor's `GetDataReader`/Async; an early
busy-parent attempt fails without consuming the handle. Disposing an executing command or closing an
unread ordinary reader preserves the existing cancellation/session-destruction contract. After parent
completion, command disposal does not close a published cursor's parent statement. Dispose each returned
cursor if it will not be read. Wrong typed access, `IsDBNull`, skipped fields and drained rows do not publish
handles. Unaccessed IDs are closed when the exchange ends, including SequentialAccess skips.

Nested cursor fetches retain ancestors until every published descendant completes or is disposed.
All descendants share a limit of 4096 distinct cursor IDs from the root execution, including skipped IDs;
aliases in the same execution reuse one handle. Nonzero IDs are required. Ancestor references and IDs
already owned by another descendant scope are rejected; silently recycling IDs is not supported. Overflow
or malformed framing destroys the session. Live published cursors prevent pool reuse; original physical
session, transaction epoch and logical lease protections remain in force. Cursor-owning statements leave
the command/idle statement cache; later execution prepares again. KeyInfo transfers the final rewritten
statement when applicable; SchemaOnly only returns metadata and allocates no cursor handles.

Fixed Connector/J6adc311 has public JDBC4ResultSet.getObject -> getComplexCursor -> four-byte binary
decoding, which supports the identifier wire design; its text row implementation still returns no cursor.
The .NET text path is therefore a local protocol extension and remains unverified against actual SQL.
Current oracle-direct attempts fail before the greeting and do not execute SQL. REF CURSOR Input/InputOutput and
the unsupported complex input/text-result shapes remain explicitly rejected; other output shapes and real
database/OBProxy/TLS/platform/runtime acceptance remain open.

### Procedures and REF CURSOR

Enable `Use Oracle Prepare Execute=true`. Procedures use `CommandType.StoredProcedure`; scalar outputs use the standard `ParameterDirection` values. Output values publish only after the complete response succeeds. Errors, cancellation or early reader close preserve previous values.

For cursor outputs, set `OceanBaseDbType.RefCursor` with Output or ReturnValue. Read the returned `OceanBaseRefCursor` once through `GetDataReader()` / `GetDataReaderAsync()`; default page size is 128. The cursor belongs to its original connection and transaction. An unconsumed cursor causes a pooled physical session to be discarded when its logical connection closes; release the cursor before expecting that session to be reused.

Pure output and return-value rows must carry exactly one four-byte little-endian cursor identifier. The
output capture path validates the width before consulting the originating cursor owner, so trailing or
truncated identifiers fail as malformed protocol data.

Scalar Oracle output rows must also contain exactly one value per bound output parameter. A mismatched
metadata or row shape is rejected as malformed protocol data before value decoding.

### REF CURSOR result limits

GetDataReader/GetDataReaderAsync accept SingleRow and SingleResult together with SequentialAccess
and CloseConnection. SingleRow returns only the cursor's first row; an empty cursor stays empty.
SingleResult leaves all rows visible because a REF CURSOR has one result. The requested fetchSize
(default128) remains the page size; neither flag changes the wire protocol or server-side row count.
After the first SingleRow row, another Read drains all remaining pages and returns false. NextResult
also drains unread pages and returns false on success. Hidden errors, malformed pages, caller cancellation
and the original command timeout still propagate; these independent fetches do not make an already
completed parent SQL outcome unknown. Paging can therefore continue after the last exposed row.

Close/Dispose before exhaustion keeps the existing rule: the unfinished physical session is destroyed;
it does not silently fetch all rows. CloseConnection still closes only the original logical lease.
Parent-command disposal, cursor single use and transaction/session ownership are unchanged. SchemaOnly,
KeyInfo and unknown flags reject before taking the cursor; a corrected call can still open it once.
These flags apply to output and binary or text result-column cursors through the same public object.
They do not add cursor Input/InputOutput. See [evidence and open acceptance](history/obconnector-dotnet-completeness.md#1562-2026-10-26-ref-cursor-结果限制).

<a id="oracle-standalone-prepare"></a>
### Oracle standalone preparation

With `Use Oracle Prepare Execute=true`, `Prepare()` / `PrepareAsync()` also prepare supported Oracle text OUT/RETURNING commands, stored procedures and functions. They normalize the SQL and issue COM_STMT_PREPARE without executing the target, encoding input values, reading or uploading streams, publishing outputs, or applying target SQL session-state effects. Invalid input values can be corrected before execution. Preparation still incurs server parsing, privilege checks and possible metadata locks; trusted server errors propagate without text fallback, and interrupted/malformed metadata breaks the session without reporting an unknown target execution.

Repeated Prepare before execution can reuse the same valid command-owned statement. The next Oracle execution transfers that ID to its cursor owner; returned REF CURSORs keep their parent statement alive after command disposal. Once transferred, later execution uses a fresh implicit PrepareExecute unless Prepare is called again. This is not persistent Oracle statement caching. Changed SQL, parameter-count/shape incompatibility, database, physical session, reset generation or statement epoch prevents stale reuse. Ordinary preparation retains its existing reuse rules.

COM_STMT_PREPARE normally describes the full bind count. Oracle DML RETURNING can instead describe input binds as parameters and pure OUT binds as result columns. Only INSERT/UPDATE/DELETE/MERGE with exact output-column and combined counts may use this split; InputOutput and ReturnValue cannot use the split exception. PrepareExecute still sends every bind and validates its own output metadata. Stream uploads use the retained ID and occur only during execution; array execution also carries that ID. Preparation ignores FetchSize. REF CURSOR Input/InputOutput and complex cursor parameter values remain explicitly rejected without an input protocol contract; binary cursor columns and binary complex cursor members follow the ordinary cursor-column ownership contract above.

Local fixtures and five-asset consumers cover preparation, ownership and failure handling. Direct Oracle4.4.2.2 tests cover synchronous/asynchronous block, RETURNING, procedure, function and REF CURSOR preparation, including no DML before execution and cursor consumption after command disposal. These results do not establish real TLS/OBProxy, other server versions, independent runtimes, or general metadata-lock compatibility.

<a id="oracle-output-fetch-hint"></a>
### Oracle output execution and FetchSize

Oracle text OUT/RETURNING and stored procedure/function execution accept a positive command FetchSize with or without explicit Prepare. The property remains unchanged. FetchSize controls a server cursor's page size; the internal output row is returned in the execution response with cursorFlag=0, so the output plan does not issue COM_STMT_FETCH or page its internal outputs. Any ordinary additional result sets keep their existing streaming behavior. This does not add output-result pagination or new result shapes.

Returned OceanBaseRefCursor objects retain their own GetDataReader(fetchSize) / GetDataReaderAsync(fetchSize) page size (default128), independently of the parent command hint; command disposal does not close an outstanding returned cursor. Output values still publish atomically only after validated completion of all responses; known errors, cancellation and early close retain their existing behavior and never replay the command. Ordinary query cursor paging is unchanged. ExecuteArray also accepts a positive FetchSize hint without paging its internal batch results or changing rowCount. UseCursorOffset on an Oracle output or array execution plan remains unsupported and rejects before version/target traffic. MySQL stored routines now also accept positive non-paging hints, while SchemaOnly/KeyInfo and INOUT streams remain rejected. See [routine hint evidence](history/2026-10-28-mysql-routine-fetch-hint.md) and [module evidence and limits](history/obconnector-dotnet-completeness.md#1546-2026-10-25-oracle-输出执行与-fetchsize-提示).

### LOBs

Enable `Use Lob Locators=true` and PrepareExecute. `OceanBaseLob.OpenRead()` reads BLOB bytes; `OpenTextReader()` reads character LOBs. Consume the originating reader first; each handle opens one stream. Handles become invalid after their session or transaction ends. A live LOB handle causes its pooled physical session to be discarded when the logical connection closes; dispose the handle before expecting reuse.

Use `WriteBytes`, `WriteText` and `Truncate` for mutation, with zero-based byte offsets for BLOB and Unicode-scalar offsets for CLOB. Mutations do not commit. `CreateTemporaryLob` creates a session-duration BLOB/CLOB; call `FreeTemporary` on its owning handle before changing its transaction context. Dispose alone does not free the server temporary LOB, so an unfreed temporary handle also prevents that physical session from returning to the pool. Async counterparts are available.

### Complex types

Load named types with `GetComplexType(schemaName, typeName, transaction)` using exact dictionary names, then call `CreateValue(...)`. Nested members use their declared type descriptors. Supported scalar members are NUMBER, VARCHAR2/CHAR, NCHAR/NVARCHAR2, RAW, DATE, TIMESTAMP, BLOB and CLOB. Binary object/collection results may also contain REF CURSOR members; each non-NULL four-byte little-endian identifier is published through the originating execution owner. Complex cursor Input/InputOutput values, text complex results and cycles remain unsupported. National members have the direction-specific encoding limits below.

Named results require preloading on the same connection/transaction and binary prepared results or REF CURSOR; call `Prepare()` for parameterless named-type queries. Text complex results are unsupported.

Set `OceanBaseParameter.ComplexType` for NULL, Output and ReturnValue. Non-null Input/InputOutput can infer it from an `OceanBaseComplexValue`. `Size`, `Precision` and `Scale` must be zero.

Pure Output/ReturnValue parameters send a protocol NULL placeholder. The declared complex descriptor remains required for session validation and decoding the returned value; InputOutput still sends its input value and type.

`CreateArrayType` accepts NUMBER, VARCHAR2, CHAR, NCHAR, NVARCHAR2, RAW, DATE, TIMESTAMP, BLOB or CLOB, or a preloaded named element type. Factory scalar arrays support input/output; ordinary complex-element arrays accept input only. Anonymous descriptions obtained from results are read-only.

Complex LOB inputs require negotiated `Use Lob Locators=true`. Named object/collection members use content payloads: `byte[]` for BLOB and `string` for CLOB, including nested named elements; CLOB content uses strict UTF-8 because the named LONG_BLOB parser bypasses client charset conversion. Anonymous scalar BLOB/CLOB arrays instead accept matching `OceanBaseLob` handles from the same connection, physical session, transaction and epoch. Handles are revalidated at value creation and execution, remain caller-owned and are never opened or disposed by binding. NULL members and empty payloads/collections retain their framing. A named LOB result handle cannot be directly rebound as a named input: the server parses that member as payload bytes, so locator values are explicitly rejected before target SQL. Read its content explicitly and create a fresh payload value if needed; no implicit external-LOB materialization is performed. These two wire formats are deliberately distinct.

<a id="complex-national-members"></a>
NCHAR/NVARCHAR2 members retain distinct layouts (208/207), including nested named objects/collections and scalar factory arrays. `CreateValue` validates shape, Unicode and byte budgets without I/O; encoding is selected again at execution. Complex national inputs use the trusted **connection** character set, unlike standalone national scalar parameters. This input resolver now accepts all configured strict client encodings, including UTF8MB3 and the eleven non-UTF8 families, while retaining legacy UTF8/UTF16 behavior. Ordinary VARCHAR2/CHAR members use the [selected client encoding](client-character-sets.md), default UTF-8. ExecuteArray uses the same encoding path and existing explicit-transaction, row-count and partial-failure contract.

Managed client initialization shares mode-specific SQL between fresh Open and pool reset: MySQL
uses the Connector/J-compatible SET NAMES statement; Oracle sets client, connection and results
separately with quoted canonical names. Four-setting verification and later trust revalidation
append FROM DUAL in Oracle mode. Validation, full-response consumption and failed-restoration
retirement remain required. Explicit Oracle checks confirm nine configured encodings for national
array inputs; UTF8MB3 remapping and SJIS/Big5 failures remain documented deployment restrictions in
[Compatibility](compatibility.md#2026-10-27-complex-national-client-encoding).

PL complex OUT/INOUT values use the result snapshot's session national encoding. Ordinary binary named complex columns retain database national storage encoding: loading a type containing national members additionally reads `NLS_DATABASE_PARAMETERS` for `NLS_NCHAR_CHARACTERSET` on the same reserved session/transaction, with the existing timeout and cancellation source. Currently only `AL16UTF16` is accepted, using strict UTF-16 big endian without BOM insertion/removal. A valid single-row response is consumed before an unsupported value is rejected; failed loading does not publish that type. Other database national encodings require separate wire evidence and implementation.

The subsequent [explicit-encoding acceptance](history/2026-10-29-complex-national-encoding-acceptance.md)
covers fourteen national settings for named/nested OUT/INOUT and anonymous array DML, plus ordinary
named-object table reads. This validates session output encoding independently of AL16UTF16 storage;
it does not validate stored nested-table columns or any other database national storage encoding.

The configured direct OceanBase 4.4.2.2 server demonstrates UTF-8 connection input, UTF-8/UTF-16 session national PL outputs and AL16UTF16 stored object members. Stored values must not be decoded using outer complex collation 63 or guessed from session national settings. Malformed bytes or surrogates fail without replacement; failed output decoding preserves all prior output parameters. Returned values retain their descriptor/data snapshot after later cache invalidation.

Anonymous ordinary national results without a trusted storage context reject non-NULL member decoding; metadata, empty collections and NULL members can be read. Factory scalar arrays support declared PL outputs in local fixtures; real anonymous-array acceptance in this module covers DML iterations, not every PL signature. Complex whole-column `RETURNING` with unresolved source type identity remains rejected with unknown execution outcome and a broken session; keep the old output value and reconcile the write. Do not infer this support from successful scalar RETURNING or PL output tests. For complex BLOB/CLOB results, binary named results require `Use Lob Locators=true` and create `OceanBaseLob` handles bound to the current physical session, transaction and reader epoch; anonymous collection metadata accepts the same member codes, and local synchronous/asynchronous BLOB/CLOB materialization is covered. The handle is created only when the member is non-NULL and materialized; buffered/sequential skip paths consume locator bytes without allocating a handle. Binary complex REF CURSOR members use the same owner and lifetime rules as top-level cursor columns, including buffered/sequential skip and child fetch; complex cursor input remains rejected because its bind wire contract is not established. Named content inputs (including InputOutput) and anonymous scalar locator inputs follow the distinct contracts above. CLOB reads retain the UTF-8 contract; inline locator parameter content is converted separately as described below. Real nested-table table-column limitations, cycles, text complex results and the remaining deployment matrix stay open.

CLOB locator parameter encoding is shared by scalar and anonymous-array inputs. It converts inline
UTF-8 content into the strict client encoding in a bounded copy, updating V1 payload size and V2
external/disk byte sizes while preserving identity, extension and rowkey bytes. Original handles
and readers remain unchanged. V1 converts; V2 converts only at authenticated product4.4.1+,
matching the newer server inrow parser. Legacy/unknown V2, BLOB and no-inline external locators
remain opaque. Older backports and additional server/proxy versions still require acceptance;
named LONG_BLOB CLOB payloads remain strict UTF-8.

<a id="complex-type-ddl-invalidation"></a>
Descriptions and values are bound to their connection, session, transaction and complex-metadata generation. Close/reopen, transaction changes and `ClearComplexTypeCache` invalidate reuse. In Oracle mode, executing text statements beginning with CREATE, ALTER, DROP, RENAME or TRUNCATE also invalidates all cached descriptions, including nested and factory-array descriptions. The shared SQL scanner ignores comments and quoted text and checks later statements in an allowed text batch; CREATE program units are DDL, while BEGIN/DECLARE bodies and procedure calls are opaque.

Invalidation occurs once when target execution begins transmission, independently of `Statement Cache Size`. Even a rejected DDL conservatively requires reloading. Standalone Prepare, metadata/control probes, SchemaOnly, local validation failures and cancellation before target transmission do not invalidate the dictionary. Reload with `GetComplexType` and reconstruct parameter values before the next bind; the driver does not retry DDL or automatically reload types. New result exchanges cannot attach the old dictionary snapshot. Values already returned retain their immutable data/layout for reading, but their descriptors cannot construct or bind new values after invalidation.

DDL executed by another connection or hidden in procedures, anonymous blocks, functions or triggers is not automatically detected. After such changes, call `ClearComplexTypeCache` on each affected open connection with the correct transaction, then reload and rebuild bindings. This local implementation does not establish real OceanBase/OBProxy DDL, implicit-commit or lock compatibility.

### Array binding

Use `ExecuteArray(rowCount)` / `ExecuteArrayAsync(...)` with PrepareExecute and an explicitly bound transaction. Row count is 1–65535. Input/InputOutput parameters are factory arrays of that length; Output/ReturnValue declares a factory array type. SELECT and DDL are unsupported. Positive `FetchSize` is accepted as a hint, preserving its value; array DML/PL uses cursorFlag=0, iterationCount=2 and no COM_STMT_FETCH. `UseCursorOffset` remains unsupported. See [Java comparison and regression evidence](history/2026-10-28-array-fetch-hint.md).

Inspect `ArrayBindingResult.Items` for zero-based Succeeded/Failed/Unknown results. Row errors return Failed; protocol or terminal errors throw and can leave partial results in `command.ArrayBindingResult`. Item success does not commit. OUT/RETURNING can return several rows per input; output `Value` becomes `object[][]` only after complete success. Reset InputOutput to a factory value before executing again.

## XA

`connection.GetXaResource()` dedicates the open physical session to XA until Close. Use `OceanBaseXid` with Start, End, Prepare, Commit, Rollback and Recover; sync/async APIs are available. Oracle requires PrepareExecute.

Business commands require an active XA branch and no local `Transaction`. Local transactions and database switching are rejected in XA mode. Close does not complete a branch. XA return codes use `OceanBaseException.XaErrorCode`, separately from `ServerErrorCode`; coordinator recovery is the application's responsibility. A separate [opt-in process recovery harness](xa-process-recovery-acceptance.md) tests one prepared/recorded-decision coordinator crash point; it is not a product coordinator.

With pooling enabled, XA dedication persists even before Start and after Commit/Rollback. Close destroys the dedicated physical session instead of resetting it into the ordinary pool. A reopened logical connection cannot reuse an old resource; all sync/async resource methods reject it. After Close, prepared branches require explicit recovery/completion by the application on a separate session; Close is not a confirmed business rollback. [Scoped pool acceptance and remaining failure matrix](history/obconnector-dotnet-completeness.md#516-xa-专用会话的池隔离).

## Schema, address resolution and diagnostics

`GetSchema` advertises 20 collections in Oracle mode and 18 in MySQL mode, including views, indexes/columns, keys/constraints, foreign keys/columns, sequences and users; named Types and Synonyms are Oracle-only. `Restrictions` describes ordered exact-match parameters; `MetaDataCollections` reports counts. Sequence numbers use `OceanBaseDecimal` without CLR decimal truncation. .NET 8/9/10 also provides `GetSchemaAsync`. See the [complete Schema contract](schema.md) for shapes, mode identity, dictionaries and permissions. Static `DataTypes` reports CLR/provider identities, precision/scale, creation templates and conditional parameter/result support; ordinary and provider-specific numeric/timestamp representations are separate rows with selection conditions. It requires no query and retains connection lifecycle guards. Routine metadata does not derive command parameters automatically.

Set `OceanBaseConnection.AddressResolver`, or `OceanBaseDataSourceBuilder.UseAddressResolver(...)`, to an `IOceanBaseAddressResolver`. Each logical Open, including pool reuse, calls its matching sync/async method once. One parsed snapshot validates idle pool candidates and supplies any prewarmed/new physical sessions. Idle sessions whose configured host/port no longer appear in that snapshot are discarded before reuse, including below `Min Pool Size`; active borrowed sessions finish normally and are checked against the snapshot of the next borrower. This checks configured host/port membership, not DNS IP changes behind the same hostname.

All five assets expose `IOceanBaseCredentialProvider? OceanBaseConnection.CredentialProvider`; modern builders expose `UseCredentialProvider`. One logical Open resolves an immutable `OceanBaseCredential` through the matching sync/async method before address discovery, including idle reuse. The provider replaces the configured user and all passwords, with no failure fallback. Provider reference identity isolates ordinary pools; source connections inherit a fixed provider. Versions order rotation, expiry bounds admission/wait/reuse/return, and retired borrowers continue until return. Acquisition participates in the total connection budget. The ownership, ordering, error, cancellation and expiry contract is in [credentials.md](credentials.md); execution results and historical failures are in the [verification ledger](obconnector-dotnet-progress.md), and real plugin/KDC acceptance remains open.

Return trusted addresses for the same tenant in `Server` syntax. Failed, invalid or canceled discovery cannot fall back to an idle session or configured address. A pooled Open with a custom resolver applies `Connection Timeout` across resolution, waiting, prewarming, probe and handshake; zero removes this total timeout. Caller cancellation and Close interrupt discovery/waiting; late async resolver results cannot open a connection. Candidates are retried only after TCP connection failure. TLS/authentication failures do not switch endpoints.

Ordinary pooled connections share only when the effective configuration identity, resolver reference identity, credential-provider reference identity, TLS validation callback reference identity, and client certificate reference identity plus SHA-256 DER fingerprint all match; overridden value equality does not merge policies. Changing a closed connection's resolver, provider, validation callback or client certificate selects its matching pool on the next Open. Each data source retains its own private pool even if these policy objects are shared with another source; source connections keep their captured provider. The resolver and provider remain caller-owned and must be concurrency-safe. See P0-06 in the [completeness baseline](obconnector-dotnet-completeness.md).

### TLS client certificates

All five assets expose `OceanBaseConnection.ClientCertificate`; modern builders expose `UseClientCertificate(X509Certificate2?)`. Certificates must have a private-key association and be valid objects at Open/Build. Configuring a certificate with TLS Disabled or without a private key fails before network I/O. The driver does not load key files, install trust roots or change server-side mTLS policy. `Required` still encrypts without validating the server identity; use VerifyFull for mutual identity validation.

Ordinary pooled connections retain the application's reference as identity, store a SHA-256 hash of the DER certificate and create one owned `X509Certificate2` copy per pool. Same-value certificates loaded into different objects use different pools; null has a separate identity. Reusing the same object and configuration reuses its pool. Each ordinary Open validates the original even when an idle physical session exists, so Reset/Dispose cannot borrow that old session. Keep the original valid through each ordinary Open, including non-pooled Opens; do not mutate or dispose it concurrently with Open/Build. Fingerprint mismatch while creating a pool fails closed. Reset/Dispose of the original does not prevent cleanup of the old pool, which uses cached identity.

Build captures a private copy in the data source, including when Pooling=false. Builder changes and disposal of the original after successful Build do not change that source. Source-created connections reject certificate setters; their getter reports the configured original object, while the internal copy supplies TLS handshakes. The getter does not expose the internal copy and may report an original that the caller has already disposed. The pool/source disposes its copy, never the caller's object. Source disposal prevents new Opens/publication; already authenticated borrowed sessions can finish and are discarded when closed.

Warmup and every new physical session use the pool's copy. Reusing an authenticated connection performs no handshake or certificate revalidation; changing certificate policy cannot revoke already borrowed sessions. For rotation, build a new source and dispose the old source, or select the new ordinary identity and clear the old identity with ClearPool/ClearAllPools. Close old borrowed connections when they must stop; clearing lets their active work finish and destroys them on return. Clearing a source preserves its original configured certificate, so replacing that certificate still requires a new source.

Local SslStream tests verify certificate presentation and pool/source ownership; they do not establish real OceanBase/OBProxy mTLS, Linux/private-key-provider behavior, revocation or corporate rotation acceptance. Successful in-place Import into an existing certificate object is unsupported by the current runtime and is not claimed tested.

### TLS server validation

All five assets expose `RemoteCertificateValidationCallback? OceanBaseConnection.RemoteCertificateValidationCallback`. Modern data-source builders expose `UseRemoteCertificateValidationCallback(RemoteCertificateValidationCallback?)`; Build snapshots the callback, and source-created connections reject changes to that fixed policy. Ordinary connections allow changes only while closed and not disposed. A configured callback requires `TLS Mode=VerifyFull`; Required/Disabled combinations are rejected before network I/O (at Build for data sources, at Open for ordinary connections).

Null preserves SslStream's platform trust, hostname and optional revocation checks. A custom callback receives the platform certificate, chain and `SslPolicyErrors`, and replaces the final validation decision. Applications can implement private-CA chain validation or pinning; they must enforce their intended hostname, validity, trust and revocation rules, and must not retain or dispose the platform-owned certificate/chain. The driver does not install trust roots or treat all chain errors as acceptable. `TLS Server Name` continues to supply the expected identity/SNI. False or a thrown exception aborts Open before database authentication; there is no plaintext fallback or retry against another endpoint after a TLS failure.

Callbacks run during TLS authentication on the handshake's execution thread. They must return promptly, support concurrent physical Opens and avoid reentering the connection; cancellation can close the transport but cannot interrupt application callback code. The callback may also run with `ClientCertificate` on pooled or non-pooled connections. Delegate/certificate objects are never encoded into connection strings or diagnostic labels; client-certificate copy ownership is defined above.

Pool keys compare callback objects by reference, even when two delegates have equal targets/methods. Reuse a stable callback instance or a long-lived data source for one policy; creating a delegate per connection creates separate ordinary pools. Prewarm and new-session Opens receive that pool's immutable callback. A reused authenticated session does not perform another TLS handshake or revalidate its certificate; mutating state captured by the same delegate does not invalidate existing sessions. Use a new callback instance for new ordinary connections, or build a new data source and close/dispose the old source and its borrowed connections when retiring a policy. Existing ordinary pools under the old callback remain separate; changing a callback does not revoke other users of that old identity.

Use builder `UseLoggerFactory(...)` or the connection's `ILoggerFactory` constructor overload. Log categories are `OceanBaseConnector.Connection`, `.Command` and `.Transaction`. Logs omit SQL, parameter values and credentials.

`ActivitySource` and `Meter` are named `OceanBaseConnector`. Metrics are `oceanbase.client.operations` and `oceanbase.client.operation.duration` (seconds). Tags identify operation, mode, outcome and unknown outcome; readers complete their execution span when consumption finishes or fails. Local diagnostics do not require server full-link tracing.

<a id="protocol-diagnostics"></a>
### Protocol diagnostics

DiagnosticListener `OceanBaseConnector.Protocol` exposes Frame/Error/Boundary/Negotiated events with read-only
scalar dictionaries. A local ChannelId links inner MySQL and compressed/OB20 wrappers within a listener
instance, spanning handshake, commands, pool reset/probe and streaming. No logical-connection logger
identity is retained by pooled channels. Read Frame means header observation before validation/body;
write Frame means acceptance by the next stream, not flush/server receipt or successful SQL. Caught
faults preserve the layer's original classification even after external Close or buffered continuation
failure. Boundary rejects remain errors and preserve the existing transport behavior.

Negotiated reports requested versus actual framing plus requested/enabled trace and active modern
trace format after physical authentication, identity and configured charset initialization succeed.
It uses the actual wrappers, not a second cached capabilities model. It is not emitted on failed
authentication/identity/charset setup or repeated for pool reuse/reset. A late subscriber gets no
replay; this event alone proves neither later I/O/logical Open nor server sampling/trace export.

No SQL, payload/authentication/extra-info bytes, endpoint, username or exception object/message is
emitted. Disabled events allocate no dictionary; observers are synchronous, exception-isolated and
Activity.Current is restored. Same-thread diagnostic recursion is suppressed. Follow the complete
[subscription, field and callback contract](protocol-diagnostics.md), including creation failures and
per-listener identity. Other resource waits and real deployment/performance gates remain open.

<a id="slow-query-diagnostics"></a>
### Slow client operation diagnostics

`OceanBaseConnectionStringBuilder.SlowQueryThreshold` / `Slow Query Threshold=1000` sets a client lifetime threshold in milliseconds (0..2147483647). The default zero disables reporting. The option is retained in endpoint/credential snapshots and effective pool identity; changing it uses a different ordinary pool policy.

A named `execute` or `cursor.fetch` exchange that completes with elapsed milliseconds greater than or equal to a positive threshold reports exactly once, including failures, cancellation, timeout and early reader disposal. It includes preparation/control work within that exchange, network/server waits and application time holding/consuming its reader. One REF CURSOR reader covers all its pages. Its existing exchange clock is captured before release/cleanup/listener work; existing operation-duration metrics retain their own clock and completion semantics. Open, standalone Prepare, transaction/XA operations and validation failures before a named exchange are excluded. This is not a measurement of server CPU time or server-only SQL duration. A pending operation produces no slow report until it ends; no periodic warning or timeout is introduced.

Three independent channels report the result:

- `OceanBaseConnector.Command` Warning event 1102 (`SlowCommand`), with structured Operation, Endpoint, Mode, ElapsedMilliseconds, ThresholdMilliseconds, Outcome and OutcomeUnknown. ILogger filtering still applies. No exception object or message is passed.
- Meter counter `oceanbase.client.slow.operations`, unit `{operation}`, incremented by one with exactly the existing `db.operation.name`, `oceanbase.compatibility_mode`, `oceanbase.outcome` and `oceanbase.execution_outcome_unknown` labels. No endpoint or threshold is a metric label.
- `oceanbase.slow=true` on an existing sampled activity, when one exists; no extra span is created and fast/disabled spans gain no slow tag.

Outcomes are success/error/canceled/timeout with the existing unknown-outcome classification. SQL, parameter values, usernames, credentials and exception text are omitted. Correlate through application tracing; the feature does not compute or log a SQL fingerprint. Listener/logger callbacks run synchronously, must return promptly and cannot change an operation's result; failures are isolated and Activity.Current is restored for each new reporting channel. No SQL is retried or canceled by profiling. Existing CommandCompleted/CommandFailed logs and operation metrics remain available. Protocol diagnostics follow their separate contract above; other resource waits, performance limits and deployment acceptance remain open P2-04 work.

<a id="connection-phase-diagnostics"></a>
### Connection phase durations

The existing `OceanBaseConnector` meter also exposes `oceanbase.client.connection.phase.duration` in seconds. Each sample covers one actual phase invocation; the only labels are `oceanbase.connection.phase` and `oceanbase.outcome`. Existing operation spans, counters and duration semantics remain unchanged.

| Phase | Measured work |
| --- | --- |
| `credentials` | Optional credential provider acquisition, cancellation waiting and snapshot validation |
| `discovery` | Optional application address resolver, cancellation waiting and endpoint-list validation |
| `tcp` | One transport connection attempt, including runtime DNS resolution; retries and prewarm connections each produce their own sample |
| `greeting` | Initial server packet, parsing and capability validation |
| `tls` | Requested TLS negotiation, including SSLRequest and certificate validation; omitted when TLS is disabled |
| `authentication` | Authentication conversation, response packets and negotiated framing activation |
| `identity` | OceanBase identity/mode/version confirmation query and validation |
| `pool.wait` | One semaphore wait for pool availability; its end is captured before cleanup/listener work |
| `pool.probe` | One idle-session COM_PING and validation, including failure of a candidate later replaced |

Static credentials and configured addresses omit their optional acquisition/discovery phases. Healthy pool reuse normally emits only `pool.probe`, plus provider/resolver phases when configured; it does not emit a second handshake. Phase samples are attempts, not logical Open counts. They exclude work in the measurement listener itself, and their sum does not equal Open duration: reset, warm-up bookkeeping, application callbacks, gaps and logical publication are not all separate phases. `pool.wait` measures only that wait; `oceanbase.client.pool.wait.duration` retains its existing elapsed-since-rent aggregation and adds the `oceanbase.outcome` tag (`acquired`, `timeout`, `canceled` or `error`).

Outcomes are `success`, `error`, `canceled` and `timeout`. `elapsed` specifically means a finite pool wait returned without a signal; credential expiry or a later wait may still determine the final Open result. A phase interrupted through a linked connection-budget token can report `canceled` while the enclosing Open maps it to `timeout`; the Open operation remains authoritative for the logical result. No phase metric implies that SQL can be retried or that an active session recovered.

Labels contain no endpoint, username, credential version, SQL, authentication bytes or exception text. Disabled phase instruments create no phase stopwatch object. Listener callbacks execute synchronously and must return promptly; exceptions are isolated and `Activity.Current` is restored. The pool wait outcome tag is emitted after waiter bookkeeping and before phase-finish callbacks, so listener work is excluded from the recorded wait. No new configuration switch, Activity span or dependency is required. These local diagnostics do not establish real TLS/OBProxy interoperability or performance limits; slow client profiling has its separate contract above, while protocol diagnostics follow their separate contract above and other resource waits remain open P2-04 work.

### Current pool leases

`oceanbase.client.pool.leased` is an untagged UpDownCounter with unit `{session}`.
Successful pool Rent/TryRent adds one; Return entry or Discard removes it once.
Reset and idle/prewarm/probe work are excluded; newly rented sessions may still
be authenticating. Clear/disposal preserves outstanding leases until cleanup.
The existing `pool.borrowed` remains a cumulative counter. Collection started late
does not reconstruct an absolute count. See the [pool metric contract](pool-metrics.md)
for lifecycle, callback, capacity and aggregation limits.

### Session protocol I/O durations

The `OceanBaseConnector` meter exposes `oceanbase.client.protocol.io.duration` in
seconds. One sample accumulates actual packet/payload calls for one direction in one
command exchange, including internal framing/codec work and protocol observers.
Application consumption gaps and upload-source reads are excluded. Fixed labels
are direction, compatibility mode and final exchange outcome; publication follows
physical lease release, the registered release callback and pending timed calls.
Higher-level reservation guards may remain active. Listener failures cannot change the
operation result. See the [full metric contract](session-io-diagnostics.md) for
scope, enablement, error interpretation and callback/allocation limits. Other
resource waits and deployment overhead acceptance remain open.

### Upload source read durations

`oceanbase.client.source.read.duration` is a histogram in seconds. It sums application
stream reads per source kind (`parameter` or `local_infile`) per command exchange,
covering MySQL long data, Oracle piece data and LOCAL INFILE. EOF/throwing reads
count; packet sends, stream acquisition/disposal and gaps outside read calls do not.
Fixed labels are `oceanbase.source.kind`, compatibility mode and final mapped
exchange outcome. It shares post-release/pending-completion publication with protocol
I/O, while enablement and sums remain independent. Disabled wrappers preserve direct
task forwarding. No content or filename is recorded; stream ownership is unchanged.
See the [source-read contract](source-read-diagnostics.md) for outcome interpretation,
callback, allocation and verification limits. Other resource waits/load remain open.

## MySQL compression

`OceanBaseConnectionStringBuilder.UseCompression` / `Use Compression=true` requests CLIENT_COMPRESS. The default remains false, even when the server advertises compression. If the greeting lacks the capability, the client keeps ordinary framing, matching Connector/J; authentication switches and the final authentication OK remain uncompressed. When the capability is present, compression starts after that OK and before OceanBase identity queries, above TLS when TLS is enabled. Both compatibility modes and all five target assets use the same implementation. The option is preserved when selecting an endpoint and is part of the connection configuration used for pool identity.

The seven-byte outer header wraps an ordinary MySQL framed byte stream. A block can hold several packets or part of a header/payload; original and zlib blocks may mix. Outer sequence numbers increment across reads and writes, wrap at 256, and reset for each new command. MySQL can renumber inner sequences when flushing compressed buffers, so compression validates the outer sequence rather than applying the ordinary inner counter. Multi-packet uploads preserve both protocol counters across direction changes. Complete writes flush compression, including no-response statement commands and LOCAL INFILE terminators.

Writes use a 64 KiB buffer and send original bytes if compression would not shrink a block. Receive input/output buffers are limited by the protocol's 24-bit lengths (at most 0xffffff per block); the ordinary 64 MiB logical-packet cap still applies. SharpZipLib's public zlib APIs validate checksum, finished stream, exact output length and absence of trailing input; dictionaries, malformed/truncated streams and unread buffered tails at exchange boundaries fail closed. Async operations use async transport I/O; codec work itself is synchronous CPU work. No SQL is replayed.

`Use Compression=true;Use OB20=true` currently fails validation before networking: compressed OB20 and proxy reroute are separate, unfinished capabilities. Local framework-zlib peers verify the standard compression path, including TLS and pooling/reset; real OceanBase/OBProxy and cross-platform compression acceptance remain open.

<a id="connect-retry-policy"></a>
## TCP connection retry policy

`Connect Retry Count=1;Connect Retry Interval=1` enables one additional complete candidate round, after a one-second fixed wait. Count is 0..10 (default0); interval is 1..60 seconds (default1), including when count is zero. These keys have matching `OceanBaseConnectionStringBuilder.ConnectRetryCount` / `ConnectRetryInterval` properties and constants on every asset. Both enter canonical serialization, immutable endpoint/credential copies and effective pool identity; omitted and explicit defaults remain equivalent.

Without explicit discovery blacklist/ceiling fields, each physical establishment snapshots its candidate start once. Sequential/RoundRobin selection traverses all candidates before waiting, and subsequent rounds retain the same order/start. There is no wait between candidates within a round, no wait after the final failure, and at most `candidate count * (Connect Retry Count + 1)` TCP attempts per physical establishment. Only pre-attachment `IOException` wrapping `SocketException` is eligible. Successful TCP ends this retry scope: greeting, TLS, authentication, identity/session initialization and command failures do not retry or advance to another candidate.

With a positive count, one logical Open budget covers address/credential acquisition, pool wait/prewarm/probe, all TCP attempts/backoffs, TLS, authentication, identity and automatic ambient initialization. This also caps a longer or unlimited Pool Wait Timeout. All warmup physical sessions share the logical token/deadline; each has its own finite round limit. `Connection Timeout=0` removes the time limit but keeps that attempt limit. Caller cancellation (OpenAsync) and Close interrupt backoff; failure releases capacity for a later explicit Open. Count zero retains existing timeout/wait semantics. Pending cleanup captured by an earlier Close must finish under its original cleanup timeout; a new Open deadline does not abandon that cleanup. After cleanup, an expired new Open fails before borrowing. Caller-owned synchronous callbacks must return promptly and honor cancellation; a deadline cannot forcibly interrupt arbitrary application code.

Credentials and resolver results are acquired once per logical Open and reused throughout its warmup/attempts; retries do not refresh them. This policy does not reconnect an active session, migrate a transaction or replay SQL. Reopen explicitly and reconcile unknown writes according to the [recovery guide](unknown-outcome-recovery.md). Endpoint TCP health hints are described below. Built-in TCP TNS file and OCP HTTP address adapters are described below; full OCP/TNS policies, proxy reroute, real health/fault/load and independent-runtime acceptance remain open.

<a id="endpoint-tcp-health"></a>
## Endpoint TCP health cache

`Host Recheck Seconds=60` retains pre-attachment TCP failure hints for 60 seconds. The default is 0 (disabled), with a valid range of 0..3600 seconds. Every asset exposes `OceanBaseConnectionStringBuilder.HostRecheckSeconds` and `HostRecheckSecondsKey`; canonical serialization, immutable endpoint/credential copies and effective pool identity retain the option. Omitted and explicit zero values share identity; different values isolate ordinary pools. This option alone does not opt Open into the shared logical deadline; the existing resolver/credential-provider/retry-count timeout rules remain in force.

Each physical Open takes the existing Sequential/RoundRobin start, then stably places candidates with recent TCP failures after the other candidates. Candidates, duplicates and their order within each group are preserved. Every candidate remains available as fallback, including when every endpoint has failed; a known failed candidate can still be tried before its TTL expires if earlier candidates fail. The selected order is frozen across that physical Open's retry rounds. TTL expiry affects the next selection, not an already selected round. Failure hints do not actively probe endpoints or guarantee that a recovered candidate will be tried while earlier candidates keep succeeding.

Only an `IOException` wrapping `SocketException` before transport attachment is recorded, and only while the opening budget is not canceled; the final failed candidate is recorded too. TCP success removes its failure hint before greeting/TLS/authentication/identity. These later failures, SQL errors, cancellation and expired budgets do not mark a TCP failure or trigger candidate failover. TCP reachability is distinct from tenant/server identity and business health; all existing identity checks still apply. There is no active-session migration, transaction recovery or SQL replay.

Failure timestamps use the monotonic clock with lazy expiry. Each cache holds at most 1024 exact host/port keys using ordinal comparison; host case and DNS aliases are not merged. At capacity, a new address evicts the oldest failure. Cache entries contain no credentials, connection strings or connection references. A high-churn resolver may evict still-recent hints, so the cache is a bounded preference rather than an exclusion list.

Matching ordinary pool borrowers and prewarm sessions share their pool's cache; each private data-source pool owns a separate cache. Unpooled connections reuse only their existing selector's cache, and an unpooled data source shares its selector among its connections. Existing configuration, resolver, credential, certificate and callback identities remain isolated. Pool Clear, pool credential retirement and pool Dispose replace the pool-owned cache object. Pool APIs do not clear unpooled selector-owned hints, which follow selector lifetime. Late success/failure from old sessions and constructors reserved before a generation change cannot modify the new cache; Clear does not interrupt already borrowed work. Removal of an empty ordinary registry pool also removes its cache. No global endpoint map, background probe or retained empty pool is added.

The local evidence covers both compatibility modes, sync/async, direct/pool/data-source/prewarm paths, fallback, expiry, policy identity and generation boundaries. Success clearing, rotated ordering and reserve-to-constructor races include internal unit evidence. The deadline-before-feedback fixture establishes a local elapsed-budget boundary, not a real network timeout or Linux result. Five assets run on one .NET 10 host. The TNS file and OCP HTTP adapters below are implemented; full OCP/TNS policies, proxy topology/reroute and real health/fault/load/platform acceptance remain open.

<a id="discovery-group-selection"></a>
## Discovery groups and selection

Built-in TNS and OCP resolvers preserve an immutable group/policy/weight snapshot through logical Open, pool membership checking, prewarm and physical establishment. Their public Resolve/ResolveAsync methods still return canonical declaration-order membership, including duplicates; that string does not encode policy. The driver obtains the richer snapshot internally from built-in resolvers. Wrapping a built-in resolver in a custom string-only resolver loses this metadata and uses ordinary flat connection Load Balance behavior. No mutable last-response policy is kept on shared resolvers.

TNS DESCRIPTION accepts either direct ADDRESS children as one group, or repeated ADDRESS_LIST groups. OCP DESCRIPTION.ADDRESS_LIST is a nonempty array of groups. Mixing direct TNS addresses and lists is rejected. Groups retain declaration order. Optional description OBLB_GROUP_STRATEGY=ROTATION explicitly means declared group order, not round-robin group rotation. Within each group, description OBLB_STRATEGY (alias LOAD_BALANCE_STRATEGY) is inherited unless a group supplies its own strategy or ON marker. OBLB (alias LOAD_BALANCE)=ON selects default RANDOM at that scope absent an explicit strategy. Supplying either alias pair twice is invalid. Without a strategy or ON marker, existing Sequential/RoundRobin applies within each group; RoundRobin takes one counter value per physical route and uses it modulo each group's size. A group ON marker overrides an inherited strategy with RANDOM unless the group also declares a strategy.

| Strategy | Order for a new physical connection |
| --- | --- |
| ROTATION | Declared address order |
| RANDOM | Fisher-Yates shuffle within the group |
| SERVERAFFINITY | Weighted sampling without replacement; remaining zero-weight entries follow in declared order |

WEIGHT is legal only under effective SERVERAFFINITY. It defaults to1, accepts decimal integers0..Int32.MaxValue, and each weighted group must contain a positive weight. OCP accepts integer tokens or digit strings, not fractional/exponent/negative/boolean/null values. Weight sums use64bits; duplicates remain distinct candidates. Randomness is for route selection, not cryptography or an exact traffic-share guarantee. Default selectors share a locked random source so Framework's clock-seeded Random does not reset on each burst connection.

Host Recheck Seconds preference is applied after strategy ordering, separately inside each group using the session's current pool-generation health cache. A healthy zero-weight fallback can precede a recently failed positive-weight member; a later-group healthy address never crosses the earlier-group barrier. Every candidate remains available. The resulting order is selected once per physical establishment and remains fixed across configured TCP retry rounds. Only pre-Attach TCP connection failures advance; greeting/TLS/authentication/identity errors remain terminal. Existing cancellation, total Open budget, retry/backoff, one credentials/discovery snapshot and no SQL replay rules remain.

Every logical Open validates a fresh snapshot before borrowing idle sessions. Idle reuse checks endpoint membership; changed weights, strategy or group order alone do not rebalance an existing physical session. Clear the pool or recreate connections when physical reselection is required; borrowed sessions are not migrated. Prewarm applies selection separately to each physical session while sharing the same immutable discovery snapshot. All groups combined retain the existing256endpoint/65536canonical-character limits and parser input/depth/node limits.

OFF/FAILOVER and unknown strategies/options remain rejected. Global/group blacklist policies and global/group TCP ceilings follow the next contract; proxy topology/reroute, real HA/load and independent platform/runtime acceptance remain open. This does not close P1-09 or the overall driver goal.

<a id="discovery-blacklist-and-attempt-ceiling"></a>
## Discovery blacklist and attempt ceiling

Built-in TNS/OCP DESCRIPTION may explicitly supply OBLB_BLACKLIST, OBLB_RETRY_ALL_DOWNS, or both. A snapshot keeps the immutable policy; public Resolve strings retain declaration membership only. With neither DESCRIPTION field nor a group/address blacklist supplied, existing candidate rounds and backoff are preserved. Either DESCRIPTION field activates a global policy, defaulting absent blacklist to NORMAL/TIMEOUT50 seconds and absent ceiling to120 actual TCP attempts per physical establishment. A group-only or address-only blacklist also defaults the shared ceiling to120 but leaves other addresses without an inherited or explicit policy unchanged. No connection-string option or public API is added.

| Descriptor field | Contract |
| --- | --- |
| OBLB_RETRY_ALL_DOWNS | Unsigned decimal0..Int32.MaxValue; total actual TCP attempts, shared across groups, eligible/fallback phases and additional cycles. It is not an additional-round count. |
| OBLB_BLACKLIST.APPEND_STRATEGY | Optional; NAME=NORMAL blocks after the first eligible TCP failure. NAME=RETRYDURATION requires RETRYTIMES and DURATION (milliseconds), counting every event in a sliding window including the exact boundary. RETRYTIMES0 behaves as1. |
| OBLB_BLACKLIST.REMOVE_STRATEGY | Optional; NAME=TIMEOUT requires TIMEOUT in seconds. The default is50;0 allows immediate probing. An expired blocked entry becomes eligible, and a failed probe immediately restarts its cooldown. TCP success removes matching history. |

All numeric values are decimal0..Int32.MaxValue; OCP accepts integer tokens or digit strings. Empty TNS OBLB_BLACKLIST= or OCP {} uses defaults. Missing required fields, duplicates, unknown or inapplicable fields/strategies and wrong scalar/numeric forms fail before pool reuse. ADDRESS_LIST may supply one local OBLB_BLACKLIST and one OBLB_RETRY_ALL_DOWNS; ADDRESS may supply one OBLB_BLACKLIST. Address-level attempt ceilings remain rejected. TNS keys/enum values ignore case; OCP keys are exact. DURATION milliseconds and TIMEOUT seconds intentionally differ, matching inspected Java6adc311 implementation rather than its misleading TIMEOUT_MS constant name.

An ADDRESS_LIST attempt ceiling limits that group's total actual TCP attempts per physical establishment,
across eligible/fallback phases and every configured retry cycle. Exhaustion skips the group and advances
to the next; zero disables new TCP attempts for that group. It never resets on a phase/cycle boundary,
spends another group's allowance, or exceeds the shared DESCRIPTION ceiling. Omission adds no group limit.
A group ceiling alone does not activate a blacklist, add candidate cycles or change the shared default.
Each prewarmed physical session has independent counters. Healthy idle reuse performs no TCP attempt and
remains available when group limits are zero; malformed snapshots still fail before reuse.

A local block replaces the complete inherited blacklist; it does not merge fields. ADDRESS takes precedence over ADDRESS_LIST, then DESCRIPTION. Omitted fields use NORMAL/TIMEOUT50 defaults, including an empty local block. An omitted block inherits, or has no policy when no ancestor supplies one. The immutable address occurrence carries its explicit policy through weighted, rotation and health ordering, including repeated host/port occurrences. The DESCRIPTION attempt ceiling remains shared. Equal effective policy values and exact endpoints share history across occurrences and groups; different policies isolate history. A success clears only the successful attempt's effective policy. Reloading policies does not mutate an earlier Open snapshot or invalidate healthy idle membership by itself.

This local-policy contract is defined by the .NET driver. Fixed Java reference6adc311 leaves local blacklist parsing disabled and its group configuration unused; it is not Java runtime parity evidence. See [implementation and evidence](history/obconnector-dotnet-completeness.md#1563-2026-10-26-发现分组黑名单覆盖).

```text
APP=(DESCRIPTION=(OBLB_STRATEGY=ROTATION)(OBLB_RETRY_ALL_DOWNS=6)
 (OBLB_BLACKLIST=(APPEND_STRATEGY=(NAME=RETRYDURATION)(RETRYTIMES=2)(DURATION=1000))
                 (REMOVE_STRATEGY=(NAME=TIMEOUT)(TIMEOUT=50)))
 (ADDRESS=(PROTOCOL=TCP)(HOST=db.example)(PORT=2881)))
```

Freeze group/strategy/health order once per physical establishment. The eligible pass skips currently blocked entries; below-threshold failures requeue within their current group before moving to later groups. Once eligible groups exhaust, perform one fallback pass ignoring blacklist status in the same frozen order without threshold requeue. Only addresses with an effective blacklist participate in this fallback; policy-free addresses receive one visit per configured cycle. Then allow at most Connect Retry Count additional discovery cycles, all sharing the descriptor ceiling. Skipped blocked entries do not consume this .NET TCP ceiling; inspected Java decrements before skipping, so exact candidate-count parity is not claimed. Connect Retry Interval delays eligible-to-fallback and cycle transitions after a pass made actual TCP attempts. An all-blocked eligible pass falls back immediately. The existing total logical Open deadline bounds discovery, pool wait/prewarm, TCP, backoff and handshake; each prewarm physical establishment gets its own ceiling within that deadline.

Cap0 forbids new physical TCP attempts, including minimum-pool replenishment. It still permits an already valid idle session after fresh discovery, membership and probe checks, even below Min Pool Size. With no reusable session Open fails without TCP. A later nonzero snapshot resumes ordinary replenishment; cap0 does not disable expiry, credentials or route validation. This explicit policy is an exception to ordinary minimum-pool prewarm semantics.

Only pre-Attach IOException with inner SocketException and an uncanceled budget advances failures. TCP success clears the matching blacklist before greeting/TLS/authentication/identity; later failures stay terminal. No SQL replay, active connection migration or tenant-health claim is introduced. The cache is scoped to the existing pool generation or unpooled selector; matching ordinary borrowers/prewarm share it. Pool Clear, credential retirement and Dispose isolate late pool-generation feedback; unpooled state follows selector lifetime. Effective policy values and exact endpoint key isolate history; changing only the attempt ceiling keeps the same policy history. The cache uses monotonic time and locks, with1024 entries/16384 individual failure events; least-recent-feedback entry eviction drops its entire history. Eviction may allow an extra probe, so this is a bounded reachability cache, not a durable exclusion guarantee.

Controlled HTTP/loopback regressions do not establish real OCP, OBProxy, database or independent runtime compatibility. Full discovery/HA and release work remains open.

<a id="tns-file-discovery"></a>
## TNS file address discovery

All five assets expose a sealed immutable caller-owned `OceanBaseTnsAddressResolver(string filePath, string netServiceName, string? expectedServiceName = null)`. It implements the existing `IOceanBaseAddressResolver`, is safe for concurrent calls, captures `Path.GetFullPath(filePath)` at construction, and requires a nonblank path/alias. The alias is trimmed and compared ordinal-ignore-case; a supplied service expectation must be nonblank and is compared exactly using ordinal rules.

```csharp
var resolver = new OceanBaseTnsAddressResolver(
    "tnsnames.ora", "APP", expectedServiceName: "tenant_service");
using var connection = new OceanBaseConnection(connectionString);
connection.AddressResolver = resolver;
await connection.OpenAsync(cancellationToken);

// .NET 8/9/10 assets also accept the same resolver:
var builder = new OceanBaseDataSourceBuilder(connectionString);
builder.UseAddressResolver(resolver);
using var source = builder.Build();
```

```text
APP = (DESCRIPTION=
  (ADDRESS_LIST=
    (ADDRESS=(PROTOCOL=TCP)(HOST=db-a.example)(PORT=2881))
    (ADDRESS=(PROTOCOL=TCP)(HOST=2001:db8::1)(PORT=2881)))
  (CONNECT_DATA=(SERVICE_NAME=tenant_service)))
```

The file may use standard `APP=(DESCRIPTION=...)` or wrapped `(APP=(DESCRIPTION=...))` entries, comma-separated aliases, whitespace, `#` comments and single/double quoted tokens with doubled quote escapes. Duplicate aliases are rejected globally, including unselected entries and wrapper/comma expansions. The whole file must have valid syntax; only the selected descriptor is checked for supported semantics, so other aliases may contain other policies. Empty quoted keys/aliases and IFILE anywhere are rejected.

The selected DESCRIPTION accepts direct ADDRESS children or repeated ADDRESS_LIST groups, optional supported selection policies, and at most one CONNECT_DATA containing exactly SERVICE_NAME. Each ADDRESS requires PROTOCOL=TCP, HOST and PORT in1..65535, with optional WEIGHT under SERVERAFFINITY. Hosts use the existing endpoint validation, including bracketed/bare IPv6. Public output preserves declared order and duplicates. DESCRIPTION_LIST, mixed direct/list addresses, unsupported policies, TCPS and unknown selected options fail explicitly. See the [group selection contract](#discovery-group-selection) for supported policies, precedence, weights, defaults and pool behavior.

A present SERVICE_NAME requires `expectedServiceName`; an expectation also requires that field. This binds caller intent to file metadata only, not to actual server/tenant identity. Credentials, database/schema, TLS policy and normal OceanBase identity checks are unchanged. There is no environment lookup, watcher, global cache, file write or dependency.

Every Resolve/ResolveAsync and logical Open reads a fresh strict UTF-8 snapshot; an initial UTF-8 BOM is accepted, UTF-16/invalid UTF-8 rejected. Limits are 1MiB actual bytes (reads stop at limit+1), 16 nested descriptor levels, 4096 combined nodes/aliases, 256 endpoints and 65536 canonical output characters. Errors do not echo TNS content; standard filesystem exceptions may include the caller-supplied path. Parse/limit errors are FormatException; unsupported descriptor semantics are NotSupportedException; filesystem errors retain their normal types. Open follows the existing discovery exception mapping.

Publish complete file replacements. In-place writes are not guaranteed coherent. Reader handles allow ReadWrite/Delete sharing; on Windows File.Replace was exercised, while File.Move(overwrite) failed with held readers. Only opening a temporarily missing file (FileNotFoundException) or Win32 sharing/lock conflict (0x80070020/21) retries: at most five attempts and four cancelable 25ms waits. Access denied, missing directory, read/decode/parse errors are terminal; persistent missing/locking still fails. This is 100ms of configured waits, not a hard native-IO deadline or guaranteed availability during publication. Never return stale addresses after an error.

ResolveAsync schedules potentially blocking file open away from its caller thread, uses async reads with cancellation, and checks cancellation before parse/publication. Sync Resolve waits for the same operation. A blocked OS file open/read cannot be forcibly interrupted; existing logical Open cancellation/deadline guards reject late results. Pre-cancel and retry-wait cancellation tests are separate from comprehensive actual mid-read IO cancellation, which is not established. Use Connection Timeout/cancellation and responsive storage accordingly.

Existing pool routing applies unchanged: the resolver reference participates in ordinary pool identity, each Open resolves once before idle reuse/prewarm/new establishment, deleted or malformed files fail without stale fallback, and changed host/port snapshots retire idle candidates no longer present. Active leases are not migrated or replayed. The [controlled topology acceptance](topology-acceptance.md) exercises these client-route rules through local relays to one server; it does not establish actual cluster failover. Two same-path resolver instances have distinct reference identity. Modern private pooled/unpooled data sources use the same adapter. Local tests cover both modes/sync/async and all five assets on one .NET10 host; they do not establish real OceanBase/OBProxy, Linux or network-filesystem behavior. Global/group blacklists and shared TCP ceilings follow the contract above; proxy topology/reroute and real HA acceptance remain open.

<a id="ocp-http-discovery"></a>
## OCP HTTP address discovery

All five assets expose `OceanBaseOcpAddressResolver(HttpClient httpClient, Uri server, string netServiceName, string? expectedServiceName = null)`. The sealed immutable resolver is caller-owned and safe for concurrent use with a concurrency-safe client/handler. The driver never disposes that client or copies database credentials into HTTP requests. The caller configures HTTP authentication, TLS, proxy, timeout and handler behavior. The server must be an absolute HTTP(S) origin with root path and no userinfo/query/fragment. The trimmed nonblank net service name becomes the escaped appName in `/api/v2/obproxy/loadBalanceInfo?appName=...`; optional expected service must be nonblank.

```csharp
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
var resolver = new OceanBaseOcpAddressResolver(
    http, new Uri("https://ocp.example/"), "APP", expectedServiceName: "tenant_service");
using var connection = new OceanBaseConnection(connectionString);
connection.AddressResolver = resolver;
await connection.OpenAsync(cancellationToken);
// Modern builders also accept builder.UseAddressResolver(resolver).
```

The accepted address response shape is:

```json
{
  "successful": true,
  "status": 200,
  "data": {
    "NET_SERVICE_NAME": "APP",
    "DESCRIPTION": {
      "OBLB": "ON",
      "CONNECT_DATA": { "SERVICE_NAME": "tenant_service" },
      "ADDRESS_LIST": [{
        "ADDRESS": [
          { "PROTOCOL": "TCP", "HOST": "db-a.example", "PORT": 2881 },
          { "PROTOCOL": "TCP", "HOST": "2001:db8::1", "PORT": "2881" }
        ]
      }]
    }
  }
}
```

The envelope must be an object with data; successful/success, when present, must be boolean true, and status, when present, must be the integer token200. Other envelope metadata is parsed and bounded but does not route. data only accepts NET_SERVICE_NAME and DESCRIPTION, with an exact ordinal service-name match. DESCRIPTION accepts a nonempty ADDRESS_LIST group array, optional CONNECT_DATA with exactly SERVICE_NAME, OBLB/LOAD_BALANCE, OBLB_STRATEGY/LOAD_BALANCE_STRATEGY and OBLB_GROUP_STRATEGY. Groups accept ADDRESS plus their own ON marker, selection strategy and optional OBLB_BLACKLIST and OBLB_RETRY_ALL_DOWNS. Addresses require PROTOCOL/HOST/PORT, optionally WEIGHT under SERVERAFFINITY and an OBLB_BLACKLIST override. TCP is case-insensitive; ports are integer JSON numbers or decimal digit strings in1..65535. The [group selection contract](#discovery-group-selection) defines ordering, weights, aliases and pool reuse. Global/group blacklists and the shared DESCRIPTION TCP-attempt ceiling follow the [blacklist contract](#discovery-blacklist-and-attempt-ceiling); unknown members, address-level attempt ceilings and non-TCP protocols fail explicitly.

NET_SERVICE_NAME binds to caller configuration; a present SERVICE_NAME also requires an exact ordinal expectedServiceName, and an expectation requires that field. These are metadata checks, not real server/tenant authentication. Normal credentials/database/TLS/identity behavior remains unchanged. Basic TNS file discovery is described above.

Each Resolve/Open issues one GET with Accept application/json and Cache-Control no-cache/no-store. Only HTTP200 and application/json or a +json media type are accepted, with absent/UTF-8 charset and no residual Content-Encoding. When response.RequestMessage.RequestUri is supplied and differs, the response is rejected. Disable automatic redirects on the client's handler: checking the final URI cannot prevent or undo already-followed requests. The driver has no HTTP retry, cache, watcher, disk write or stale fallback; caller handlers may implement their own behavior, and must not reintroduce stale discovery or unwanted redirects. Fresh means a new request invocation, not a guarantee against caches outside the driver.

The response limit is1MiB actual bytes, including optional leading UTF-8 BOM; oversized Content-Length rejects before reads, and actual reading stops at limit+1. UTF-16/invalid UTF-8 and malformed JSON fail explicitly. The strict bounded parser enforces16 JSON value levels and4096 value nodes (root, property values and array elements); it checks the remaining node budget before allocating the next object key. All decoded object keys are ordinal and duplicate keys are rejected, even in unused metadata. Serializer __type metadata is unsupported everywhere. JSON escapes follow JSON grammar, including escaped UTF-16 code units; endpoint/name checks still determine usable values. Endpoint/service validation reuses the TNS validator through safely quoted tokens, with256 endpoints/65536 canonical characters. Parse/limit/value failures are FormatException; unsupported policy/shape is NotSupportedException; HTTP status/redirect failures are HttpRequestException. Messages do not echo response content. Caller HTTP errors may contain the configured request URI.

Request/response/body stream ownership is scoped to each operation and released on success/error/cancellation. Send and body ReadAsync receive cancellation, with checks before parse and publication. ResponseHeadersRead means HttpClient.Timeout alone does not cover the entire body; direct callers should supply a cancellation deadline, and logical Open keeps the existing Connection Timeout budget. Sync Resolve waits for the same operation. Caller handlers/streams must return promptly and honor tokens; arbitrary blocked code cannot be forcibly interrupted, while existing logical Open guards reject late results. Parsing checks cancellation per value and at completion, not during every character of a bounded long string/whitespace scan. No active connection migration, transaction restoration or SQL replay is introduced.

Existing resolver-reference pool identity and one-snapshot-per-Open routing remain unchanged. Failure rejects idle reuse without old-address fallback; a changed host/port retires idle sessions absent from the new snapshot, leaving active leases alone. Modern pooled/unpooled sources share the same adapter contract. Local controlled HTTP handlers and protocol peers cover five assets on one.NET10host, not actual OCP/network HTTP/enterprise TLS/Linux/independent runtimes. Remaining discovery policies, proxy topology/reroute and real HA/load acceptance remain open.
