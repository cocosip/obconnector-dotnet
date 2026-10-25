# API reference

For a basic query, see the [README](../README.md). For tested server profiles, see [Compatibility](compatibility.md).

The [Connector/J architecture outline](obconnector-j-architecture-outline.md) is retained as the protocol and behavior reference. Its Java feature inventory does not establish .NET support; unverified and unsupported combinations are listed in Compatibility.

## Connections and configuration

Use `OceanBaseConnectionStringBuilder` to construct connection strings. Unknown or conflicting options are rejected.

| Option | Default | Purpose |
| --- | --- | --- |
| `Server` / `Port` | Required server / 2881 | Server addresses; omitted candidate ports inherit `Port`. |
| `User ID` / `Password` | Empty | Database credentials. |
| `Database` | Empty | Initial database. `ChangeDatabase` is available in MySQL mode only. |
| `Connection Timeout` | 15 seconds | Total budget for address discovery, connection, TLS, authentication and identity confirmation. |
| `Command Timeout` | 30 seconds | Command execution budget. |
| `ExpectedMode` | Unset | Validates `MySql` or `Oracle` after detection. |
| `TLS Mode` | `VerifyFull` | Validates certificate trust and hostname. `Required` encrypts without identity verification; `Disabled` uses plaintext transport. |
| `TLS Server Name` | Endpoint hostname | Overrides the TLS certificate name. |
| `Check Certificate Revocation` | false | Enables platform certificate revocation checks during TLS. |
| `Persist Security Info` | false | Retains credentials in opened-connection and data-source connection strings when true. |
| `Load Balance` | `Sequential` | `RoundRobin` rotates the first candidate on each Open. |
| `Pooling` | false | Reuses physical sessions with protocol reset, capacity/wait controls, minimum-pool prewarming and idle/total lifetime reaping; recognized user session-state or temporary-object statements discard the session on close. |
| `Min Pool Size` | 0 | Ensures at least this many physical sessions before a borrow; a failed warmup rolls back sessions created by that attempt only while they remain idle; concurrent borrowers retain ownership. |
| `Max Pool Size` | 100 | Maximum physical sessions per matching pool; additional opens wait for a return. |
| `Pool Wait Timeout` | `Connection Timeout` | Seconds to wait for a physical session; zero removes this wait limit. With a custom resolver, the total logical Open connection budget also bounds pool waiting. |
| `Connection Idle Lifetime` | 0 | Seconds an idle session may remain before lazy reaping; zero disables the limit. |
| `Connection Lifetime` | 0 | Maximum physical-session age in seconds; zero disables the limit. |
| `Statement Cache Size` | 0 | Idle server-prepared statements retained per physical session, 0..1024; zero disables sharing across commands. |
| `Use Compression` | false | Requires standard MySQL zlib compression. Unsupported servers fail before credentials; cannot currently combine with Use OB20. |
| `Use OB20` | false | Requests OB20 framing when negotiated by the server. |
| `Enable Full Link Trace` | false | Requests server tracing; requires `Use OB20=true`. |
| `Use Oracle Prepare Execute` | false | Enables Oracle procedures and output bindings; requires server version discovery and OceanBase >=2.2.76. |
| `Use Lob Locators` | false | Enables Oracle locator LOBs. External reads and mutations also require PrepareExecute. |
| `Allow Multi Statements` | false | Explicitly negotiates `CLIENT_MULTI_STATEMENTS` for text commands. Parameters, Prepare, server cursors and Oracle output paths remain unsupported for multi-statements. |
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
transaction would hold locks and roll back when the connection closes. Connector/J instead forces
`SET autocommit=1` after connecting; this driver mutates no session variables.
Three- or four-component numeric versions, optional `v`, OceanBase-marked versions and printable
build suffixes are recognized. An OceanBase marker selects the product version after the marker,
never a preceding MySQL-compatible version. There is no exact-release allowlist or user version override.

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

Closed StateChange still occurs before transaction/return cleanup, preserving logger reentry and replacement-session event ordering. A synchronous Open from that callback into the same full pool cannot receive its still-held slot: finite Pool Wait Timeout expires, and zero can wait indefinitely. Reopen after Close completes. Callback failure still runs the captured old session's cleanup; changing configuration cannot redirect that cleanup. This module does not establish complete reset, arbitrary reentry/concurrency or long-running resource acceptance; see [the remaining pool work](obconnector-dotnet-completeness.md#51-连接池和会话隔离).

### Explicit pool clearing

All five assets expose `OceanBaseConnection.ClearPool(OceanBaseConnection)` and `ClearAllPools()`. Modern data sources expose `OceanBaseDataSource.Clear()`. ClearPool rejects null/disposed connections, is a no-op for non-pooled or absent ordinary pools, and does not connect, resolve addresses or create a pool. It snapshots the connection's effective parsed configuration and resolver, credential-provider, validation callback and client-certificate references; a closed connection that has never opened can therefore clear an existing matching pool, including an equivalent option spelling/order/default configuration. It never reads certificate contents, so Reset/Dispose of an original cannot block cleanup; all historical fingerprints for the same reference identity match. Changing connection configuration before clearing targets the new identity, not the former one.

Clear closes idle physical sessions immediately, marks borrowed sessions for destruction when returned, and advances the pool generation. An Open already in flight can finish, but its pre-clear session cannot later be reused; pre-clear warmup completions are discarded before becoming idle. Commands, readers, transactions and already borrowed connections are not interrupted or replayed. Retired borrowed sessions still count toward MaxPoolSize until closed. A return that observes retirement skips reset; if reset was already in progress it may finish, but that session is destroyed rather than published idle. Capacity waiters are signaled when sessions are removed. Clear does not populate MinPoolSize immediately; subsequent Opens perform warmup.

ClearAllPools operates on a snapshot of ordinary registry pools; new pools created concurrently need not be included. It does not clear private sources. Use source.Clear(), or ClearPool with one of that source's connections, for the private pool. Clearing keeps the source and its immutable configuration/certificate snapshot available; Dispose still ends source use. Empty ordinary registry pools are removed and release owned copies. Clearing does not replace static credentials, certificates or callbacks: select a new configuration/source when changing them, and clear the old identity or dispose its source to retire old sessions. Credential-provider versions can rotate authentication material within a live pool as described in the [credential contract](credentials.md). Idle/lifetime reaping remains lazy unless explicitly cleared.

### Pooled session state after user SQL

With `Pooling=true`, recognized state-changing SQL makes its physical session ineligible for reuse. The flag is sticky for that session and is applied at the shared target-send boundary, after cancellation checks and before the write; the session is destroyed on return even if the server rejects that request. Merely preparing, reading SchemaOnly/KeyInfo metadata, uploading long data, or rejecting a command before target execution does not apply this flag. A transport failure or unfinished protocol response can still independently destroy the session.

Classification scans all statement heads across mode-specific whitespace/comments and semicolons. It recognizes SET/RESET/USE, ALTER SESSION, temporary-table forms, explicit transaction/XA statements and opaque CALL/DO/EXECUTE/PREPARE operations. Oracle BEGIN/DECLARE and labeled blocks, plus `CommandType.StoredProcedure` names sent through PrepareExecute, retire the session because their internal state changes cannot be inferred from the call. MySQL executable comments, user-variable references and named-lock function calls also retire it. Ordinary literals, identifiers and non-executable comments containing these words do not by themselves retire a session; table/CTE column lists are distinguished from named-lock calls.

MySQL SQL_MODE is not tracked. A backslash within quoted text is therefore treated conservatively as ambiguous and retires the session, including otherwise harmless escaped strings. Quoted named-lock calls are conservative under ANSI_QUOTES/backtick rules. The lexical classifier preserves covered common query/DML forms; a nested CTE declaration named like a lock function can still conservatively retire a safe session. It does not prove that arbitrary stored functions, triggers or other server-side code have no session effects. Such applications need verified reset behavior for their exact profile or must disable pooling. The existing COM_RESET_CONNECTION/initial-database restoration, active-resource disposal and dedicated-XA rules still apply; marking raw XA SQL does not implement coordinator recovery.

This is local driver behavior, not proof of OceanBase/OBProxy reset compatibility. The remaining resource, fault, concurrent-return and real-profile acceptance requirements are tracked under P0-02 in the [completeness baseline](obconnector-dotnet-completeness.md).

## Commands and readers

- Use Oracle `:name` or MySQL positional `?` bindings. Parameterized execution uses server prepare; `Prepare()` explicitly prepares a command.
- Oracle anonymous blocks and stored-program DDL are sent as single program units; internal semicolons do not require `Allow Multi Statements`. The server validates the complete program syntax, including any invalid trailing SQL. Ordinary SQL boundary checks use the active compatibility mode's quoting and comment rules.
- For `LOAD DATA LOCAL INFILE`, opt in with `Allow Local Infile=true` and set `LocalInfileStreamFactory` before opening. The factory receives the server-supplied filename as opaque text and must return a readable stream; the driver uploads it in chunks, sends the terminating empty packet, reads the final response, and disposes the stream. Missing factories, read failures, cancellation, the 64 MiB default upload limit, or a lost final response break the physical session and report an unknown outcome.
- Set `DbType` or `OceanBaseDbType` when inference from `Value` is insufficient. Use `DBNull.Value` for SQL NULL. `ResetDbType()` restores inference.
- Read all results with `Read` and `NextResult` to establish completion. Closing an unread reader destroys its unfinished session; disposing a field stream does not finish its parent reader.
- `CommandBehavior.SequentialAccess` exposes field streams without buffering the whole result. `SingleRow` returns only the first row of each exposed result and drains the remainder; `SingleResult` drains later results without exposing them. `SchemaOnly` accepts text prefixes `SELECT`, `SHOW`, `DESCRIBE`, `DESC`, `EXPLAIN` or `VALUES` after whitespace/comments. It obtains metadata through server preparation and never sends a target COM_QUERY, COM_STMT_EXECUTE, COM_STMT_FETCH or parameter-data upload. It accepts one text statement with input parameters only; names/order/count remain required, but values are not encoded, consumed or validated as bindings. FetchSize and offset hints are inactive. Preparation errors propagate without client-side fallback, including error 1295 when AllowClientSidePrepareFallback=true. An existing valid prepared statement is reused. The empty reader exposes prepared column metadata, HasRows=false and RecordsAffected=-1; its exchange is complete, so another connection operation is allowed. CloseConnection still closes the original session when that reader is disposed. Named complex columns require existing preloaded descriptions and may issue directory lookups for a missing source identity; those lookups do not execute the target. Metadata is the server prepare-time shape, which may differ from execution-time metadata. Server preparation can still incur parsing, permission checks and metadata locks; real OceanBase/OBProxy side-effect/lock/large-result acceptance remains open. Ordinary row readers retain their existing operation restrictions.
- Positive `OceanBaseCommand.FetchSize` enables forward-only server cursor pages. Oracle `UseCursorOffset=true` additionally requires OceanBase >=2.2.5; it does not provide scrolling.
- `AllowClientSidePrepareFallback=true` permits limited scalar-input text fallback only after automatic prepare returns a validated `1295/HY000`. Explicit Prepare, cursors and Oracle PrepareExecute never fall back. Date/time, special types and output parameters are excluded; MySQL non-null strings require trusted session collation. Fallback literals may appear in server SQL logs.

### Prepared statement cache

Set `Statement Cache Size=1` (up to 1024) to retain idle server-prepared statements across command instances on the same physical session. The default is zero. Parameterized commands still prepare automatically; parameterless text commands still need explicit Prepare to use server preparation. A hit uses the exact rewritten SQL with the current session database/context; parameters and their values are supplied afresh on every execution. It never shares IDs between physical sessions or changes SQL to obtain a hit.

The cache uses least-recently-returned eviction. Checking out an entry removes it from the idle cache and transfers exclusive ownership to the command. Commands keep their own statement until disposal or a change of SQL/connection; retained command-owned statements are outside the idle capacity limit and must still be disposed. An active reader/cursor prevents another command from acquiring the physical session. A fully consumed cursor can be reused after its command is disposed. Eviction and duplicate return queue COM_STMT_CLOSE through the existing command boundary; they never write into another reader's response. The option bounds idle statement count, not total application-held commands or metadata bytes.

Ordinary single SELECT/WITH/INSERT/UPDATE/DELETE/REPLACE/MERGE statements without recognized session-state effects or quoting ambiguity are eligible. Other user SQL, including DDL, opaque calls, executable comments and multi-statements, invalidates the cache at the target-send boundary after cancellation checks. Successful ChangeDatabase also invalidates it. A separate context epoch prevents commands still holding an old statement from returning or reusing it after such changes; those IDs are closed normally. Both sync and async preparation recheck the database, reset generation and cache epoch after acquiring the exclusive session lease, so a context change before acquisition cannot leave a stale held ID in use. Commands rejected locally before target send, pre-canceled commands and preparation do not apply target-send invalidation. A context-changing target that reaches the server invalidates conservatively even if the server rejects it.

A successful pool COM_RESET_CONNECTION forgets idle entries and advances the physical generation; old IDs are never closed again because the server reset already closed them. Broken/closed transports forget all entries. Complete preparation and matching parameter counts are required. Execution errors prevent an entry from returning to the cache, and unread responses retain the existing session-destruction behavior. No cache miss/error triggers execution replay. SchemaOnly and KeyInfo bypass shared lookup and their newly prepared statements are not cached; an already valid command-owned preparation retains its existing behavior. Oracle PrepareExecute, auxiliary catalog preparation and rewritten KeyInfo statements keep their existing owners and are excluded from idle sharing. XA-dedicated sessions do not return entries to this cache.

External DDL and arbitrary stored-function/trigger side effects have no client-side dependency tracking. Follow server invalidation errors and explicitly recreate the command/connection when required; the driver does not retry unknown writes. Real server reprepare/DDL, OBProxy, resource/load and independent-runtime acceptance remain open. Pool reset deliberately limits cache reuse to commands within an uninterrupted physical-session context; it does not weaken borrower isolation to preserve cache hits.

### Key information and hidden columns

`CommandBehavior.KeyInfo` requires a single text statement with input parameters and server preparation; it never uses client prepare fallback. Procedure/output/array and multi-statement combinations are rejected before sending the target. It can be combined with SchemaOnly, SequentialAccess, SingleRow, SingleResult and CloseConnection. Batch retains its narrower behavior set.

For a provably row-preserving single-base-table SELECT, the driver reads the key catalog on the same reserved physical session and transaction before target execution or long-data upload. Catalog filters are parameters. It chooses the complete primary key, otherwise a deterministic complete nonnullable unique key. A nullable single-column unique constraint sets IsUnique=true and may keep AllowDBNull=true, but is not selected as IsKey; composite members are not individually unique. Oracle effective P/U constraints use ALL_CONS_COLUMNS, independently of their backing index, with disabled/unvalidated/deferrable constraints excluded. Eligible standalone normal valid unique indexes are separate candidates. MySQL prefix/functional index members disqualify that entire candidate.

Missing members of the selected row key are appended as hidden columns at the end of the SELECT projection, using quoted source identifiers and collision-free aliases. Server preparation supplies their actual types; existing result ordinals stay fixed. FieldCount includes hidden columns, VisibleFieldCount counts the original projection, and both GetColumnSchema and GetSchemaTable report IsHidden/IsKey/IsUnique. Hidden fields remain readable by ordinal. The original CommandText and original prepared-statement cache are preserved; the rewritten statement belongs to the current execution and is queued for closure after its response/cursor lifetime. A new catalog snapshot is obtained on each execution. Changes in key-source ordinal metadata cause failure instead of publishing stale keys.

The recognizer supports a single named source, optional schema/alias, simple projection expressions without parentheses, WHERE (including nested filters), ORDER BY without functions, LIMIT/OFFSET/FETCH and FOR clauses. It handles quoted identifiers, comments and Oracle Q/NQ literals. DISTINCT/UNIQUE, grouping/aggregation, joins, set operations, CTEs, derived sources and other ambiguous shapes keep their original SQL/result shape and unknown IsKey/IsUnique. Views, synonyms, unavailable directory identities and ambiguous MySQL SQL_MODE-dependent quoting likewise receive no invented row key or hidden additions. Missing provenance alone does not prove IsExpression; updateability remains unknown. Normal execution uses actual result metadata for unenhanced queries, including parameter expressions whose execution-time types differ from preparation.

Directory parsing is bounded to 4096 rows / 4 MiB of string content; hidden additions respect the 4096-column and command packet limits. Errors or cancellation during preparation or directory lookup propagate before executing the target; directory lookups do not set HasSentSql or an unknown target outcome. Cancellation after sending the target retains the ordinary unknown-outcome rules. SchemaOnly still performs no target execution/fetch/parameter upload, although preparation and directory queries can check permissions and take metadata locks. This bounded provider contract has local fixture coverage; actual OceanBase/OBProxy dictionary SQL, DDL/lock races and supported-version/permission matrices still require real acceptance.

### Data adapters and command generation

All five assets expose sealed `OceanBaseDataAdapter : DbDataAdapter` and `OceanBaseCommandBuilder : DbCommandBuilder`; the provider factory creates both. The adapter has default, OceanBaseCommand, `(string selectCommandText, OceanBaseConnection connection)` and `(string selectCommandText, string connectionString)` constructors. Its typed SelectCommand/InsertCommand/UpdateCommand/DeleteCommand properties retain caller objects. RowUpdating and RowUpdated use `EventHandler<RowUpdatingEventArgs>` and `EventHandler<RowUpdatedEventArgs>` with the framework event args. Handlers follow registration order; the builder subscribes to RowUpdating and detaches when reassigned/disposed.

Fill/FillSchema, DataTable/DataSet table and column mappings, MissingSchemaAction.AddWithKey, row states, original/current values, AcceptChangesDuringUpdate, ContinueUpdateOnError and zero-row UPDATE/DELETE DBConcurrencyException use the framework implementation. Fill opens/closes a previously closed connection; an already open connection remains open. Update is synchronous and sequential, UpdateBatchSize=1; other sizes are rejected. It uses command transactions and never creates a transaction spanning all rows or retries writes. Use an explicit transaction for atomic changes across rows. Caller-supplied commands support the existing command contract. Adapter/command cloning is not a supported provider contract; retain explicit command/connection ownership.

The builder has default and OceanBaseDataAdapter constructors, a typed DataAdapter property, and typed GetInsertCommand/GetUpdateCommand/GetDeleteCommand methods with default and bool useColumnsForParameterNames overloads. The framework currently falls back to ordinal pN names when maximum parameter-name metadata is unknown. Oracle placeholders are :pN; MySQL uses positional ?. Values, original values and null-test indicators remain parameters. Generated commands inherit the source connection, transaction and timeout and use UpdatedRowSource.None. They do not retrieve generated identities/defaults/RETURNING values; use explicit commands or refill for server-generated values.

Generation requires proven single-base-table KeyInfo metadata, obtained on the existing session through SchemaOnly|KeyInfo. Views, joins, DISTINCT and other unknown shapes fail instead of generating writes from ambiguous provenance. A complete visible chosen row key is required for automatic UPDATE/DELETE; hidden/missing keys are rejected by the framework. Select every key member, or supply explicit commands. INSERT can be generated from known base columns without a usable row key. Autoincrement, hidden and non-source expression fields are omitted from insert/update sets. The builder suppresses standalone IsUnique in its local metadata so nullable UNIQUE cannot substitute for IsKey, and preserves all chosen composite key members. MySQL database qualification is preserved in builder-local schema metadata; public reader catalog/schema properties are unchanged. ConflictOption and optimistic concurrency follow the framework; by default original searchable values are compared.

QuoteIdentifier/UnquoteIdentifier use backticks for MySQL and double quotes for Oracle, doubling embedded delimiters. Syntax is determined by the prepared source mode, an open attached connection or ExpectedMode; without mode information, an explicit matching supported QuotePrefix/QuoteSuffix pair is required for identifier-only operations. Generation rejects delimiters that do not match the actual server mode, including MySQL double quotes without an ANSI_QUOTES assumption. Framework generation can reject quoted table names containing its delimiter even when QuoteIdentifier itself can escape them.

Parameter metadata preserves provider types, nullability and known signed decimal precision/scale. Unknown scale retains value inference; null-test indicators are Int32 without decimal declarations. Size stays zero, avoiding implicit truncation. Complex, REF CURSOR and locator LOB columns needing extra binding context require explicit commands. RefreshSchema invalidates metadata and disposes cached generated commands; the next generation request reacquires metadata; call it after changing source SQL, mode, transaction or database schema. Disposing the builder detaches its handler and disposes generated commands, while the source command/connection remain caller-owned. Local fixtures and five-asset consumers do not establish actual dictionary SQL, CRUD/locking or independent-runtime interoperability.

### Binary input streams

A readable `Stream` can be an Input parameter, inferred or explicitly `DbType.Binary`; `Size`, `Precision` and `Scale` must be zero. The driver reads from the current position to EOF without rewinding or disposing the source. A stream cannot occupy multiple binding positions.

The encoded execution request is limited to 64 MiB; Oracle upload bytes count after hexadecimal expansion. Streams cannot be combined with array binding or client-prepare fallback. Async sources must cooperate with cancellation; a blocking source read must return before interruption completes.

### Exact values

| Database value | CLR / provider access |
| --- | --- |
| DECIMAL / NUMBER | `decimal` for reliable precision 1–28 and nonnegative scale within precision; otherwise `OceanBaseDecimal`. `GetProviderSpecificValue` preserves `OceanBaseDecimal` for either shape. |
| Oracle DATE | `DateTime` with `Kind=Unspecified`, including time of day. |
| Oracle TIMESTAMP | `DateTime` for declared precision <=7; `OceanBaseTimestamp` for 8/9. Provider-specific access preserves nanoseconds at every precision. |
| Oracle TIMESTAMP WITH TIME ZONE | `OceanBaseTimestampWithTimeZone`; exact explicit conversion to `DateTimeOffset`. |
| Oracle TIMESTAMP WITH LOCAL TIME ZONE | `OceanBaseTimestampLocalTimeZone`, retaining returned local fields and session zone. |
| Oracle INTERVAL | `OceanBaseIntervalYearToMonth` / `OceanBaseIntervalDayToSecond`. |
| Oracle NCHAR / NVARCHAR2 | `string`; explicit `OceanBaseDbType.NChar` / `NVarchar2` for binding. |
| MySQL DATE / DATETIME / TIMESTAMP | Normal values use `DateTime`. Provider-specific `OceanBaseDate` / `OceanBaseDateTime` preserves zero components; conversion to `DateTime` rejects them. |
| MySQL YEAR / BIT / JSON | `ushort` / `ulong` / UTF-8 JSON text as `string`. SQL NULL remains distinct from JSON text `null`. |

Conversions requiring rounding or loss of precision throw. Timezone-free values do not acquire the client's local timezone.

### Decimal parameter scale

`OceanBaseParameter.DecimalScale` is an optional signed `int?` declaration for decimal parameters, including Oracle NUMBER negative scale. The setter accepts -84..127; encoding validates Oracle precision <=38/scale -84..127 or MySQL precision <=65/scale 0..30, with scale <= nonzero Precision in MySQL mode. A declaration on a non-decimal parameter is rejected, including typed NULL, complex values and LOB/cursor parameters. SQL NULL retains metadata validation.

| Assignment | Effective declaration |
| --- | --- |
| `parameter.Scale = value` | Sets the standard byte scale and clears DecimalScale. |
| `parameter.DecimalScale = value` with a non-null value | Clears the old standard Scale and uses the signed declaration. Scale reads the nonnegative declaration, or zero for a negative one. Read DecimalScale for the actual signed value. |
| `parameter.DecimalScale = null` | Removes the override. It does not restore a standard Scale overwritten by an earlier non-null assignment; with no declaration, scale is inferred from the value. |

Precision/scale constraints are exact: values requiring rounding throw before execution. For example, `Value=12300m`, `Precision=3`, `DecimalScale=-2` is representable in Oracle mode; `12345m` with that declaration throws. A negative scale is not accepted in MySQL mode. Parameter snapshots and the supported client-prepare numeric fallback preserve the same declaration and constraints. Result decoding, temporal Scale and default value inference are unchanged. Routine parameter derivation below reuses this declaration. Local wire tests do not establish real-server NUMBER interoperability.

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

<a id="mysql-stored-procedures-development-checkpoint"></a>

## MySQL stored procedures

Implemented from `1bb13cb`. The [module evidence](obconnector-dotnet-completeness.md#1530-2026-10-25-mysql-存储过程执行本地交付) separates local regression, limited direct-server runs and open deployment acceptance; the earlier development checkpoint retains original failures.

In MySQL mode, use CommandType.StoredProcedure with a one- or two-part routine identifier, parsed and quoted as identifiers. Parameters bind in collection order, irrespective of their names. Input, Output and InputOutput are accepted; ReturnValue is rejected because MySQL procedures have no function return. Call stored functions with a text SELECT. Effective CLIENT_PS_MULTI_RESULTS is required. FetchSize, SchemaOnly and KeyInfo are rejected; explicit Prepare is supported and client-side prepare fallback is disabled. Input streams use the long-data path; InputOutput streams are rejected. The plan accepts at most 65535 parameters and 4096 output parameters, subject to packet limits.

For outputs, one transient binary-prepared SET initializes private per-command user variables on every execution: Output starts at SQL NULL and InputOutput uses its input snapshot. The binary-prepared CALL binds Input values and refers to these variables for outputs. After the terminal CALL response, a private text SELECT retrieves outputs; this result is hidden and excluded from affected-row totals. Outputs follow the SELECT column metadata and normal decoding, including user-variable temporal/string behavior; the declared parameter DbType does not force an output conversion. Values publish together only after a valid single-row output response completes successfully. Errors or cancellation preserve prior output values; the driver does not replay the procedure.

One exclusive exchange spans initialization, CALL results and output collection. Read/NextResult exposes ordinary CALL results; Close/Dispose drains the remaining procedure response and collects outputs, so early close can perform network I/O and can fail or time out. Closing a batch reader drains the active procedure and updates its affected count without executing remaining batch commands. Other command modes retain their existing close behavior. Procedure sessions retire on pool return; CALL preparations remain command-owned and do not enter the idle statement cache.

The initialization SET is SQL sent before the business CALL. A later transport failure can therefore conservatively carry unknown outcome even if CALL transmission has not been proved; do not infer business execution from that flag alone. A confirmed error from the output SELECT can follow a successfully executed CALL, so a confirmed SQL error does not imply the procedure had no side effects. Output publication does not imply transaction commit. Real proxy/TLS, failure, resource and independent-runtime acceptance remain open.

The statement-boundary scanner also recognizes MySQL CREATE PROCEDURE/FUNCTION/TRIGGER/EVENT program units, including OR REPLACE and supported DEFINER forms, so body semicolons do not require text multi-statement opt-in. This classifies a program unit; the server validates its grammar and suffix. The local grammar regressions and default-policy direct-server procedure run cover this boundary; other versions and proxies still require validation.

## Transactions, batches and failures

Assign the active transaction to each business command. MySQL supports ReadUncommitted, ReadCommitted, RepeatableRead and Serializable; Oracle supports ReadCommitted and Serializable. Unspecified isolation uses MySQL RepeatableRead or Oracle ReadCommitted. Opening inside an ambient `TransactionScope` enlists one physical session through the coordinator single-resource slot; commands/Batch use its local transaction. See the [ambient transaction contract](#ambient-transactions) for rejection and outcome rules. MySQL-mode sessions with `@@autocommit` disabled are rejected at Open; see the identity rules above.

On .NET 8/9/10, `Save(name)` and `Rollback(name)` operate on the active transaction; `Release(name)` is MySQL-only. Names must contain 1–30 ASCII letters/digits/underscores and start with a letter. Leading underscores, including `__EFSavePoint`, are rejected. Reacquire LOBs, cursors and complex-type values after savepoint rollback.

Batches execute sequentially. A later failure does not undo earlier entries; use an explicit transaction for transactional writes. `RecordsAffected` cannot identify a safe restart position.

| Result | Application action |
| --- | --- |
| `OceanBaseException.IsExecutionOutcomeUnknown=true` | SQL may have executed. Reconcile business state through a new connection before retrying. |
| Trusted server ERR | Inspect `ServerErrorCode` and `SqlState`; follow the remaining transaction/session state. A false unknown flag does not prove absence of side effects. |
| Confirmed commit/rollback followed by failed session cleanup | Retain the confirmed outcome and reconnect; do not repeat completed work. |

The driver never automatically replays sent SQL or moves an active transaction to another endpoint. Cancellation or a local timeout does not prove that server execution stopped.

### Ambient transactions

Local single-resource implementation and verification are recorded in the [module evidence](obconnector-dotnet-completeness.md#1527-2026-10-25-ambient-单资源事务与初始化取消本地交付). The earlier development checkpoint retains the original failures; real deployment acceptance remains open.

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

### Procedures and REF CURSOR

Enable `Use Oracle Prepare Execute=true`. Procedures use `CommandType.StoredProcedure`; scalar outputs use the standard `ParameterDirection` values. Output values publish only after the complete response succeeds. Errors, cancellation or early reader close preserve previous values.

For cursor outputs, set `OceanBaseDbType.RefCursor` with Output or ReturnValue. Read the returned `OceanBaseRefCursor` once through `GetDataReader()` / `GetDataReaderAsync()`; default page size is 128. The cursor belongs to its original connection and transaction. An unconsumed cursor causes a pooled physical session to be discarded when its logical connection closes; release the cursor before expecting that session to be reused.

### LOBs

Enable `Use Lob Locators=true` and PrepareExecute. `OceanBaseLob.OpenRead()` reads BLOB bytes; `OpenTextReader()` reads character LOBs. Consume the originating reader first; each handle opens one stream. Handles become invalid after their session or transaction ends. A live LOB handle causes its pooled physical session to be discarded when the logical connection closes; dispose the handle before expecting reuse.

Use `WriteBytes`, `WriteText` and `Truncate` for mutation, with zero-based byte offsets for BLOB and Unicode-scalar offsets for CLOB. Mutations do not commit. `CreateTemporaryLob` creates a session-duration BLOB/CLOB; call `FreeTemporary` on its owning handle before changing its transaction context. Dispose alone does not free the server temporary LOB, so an unfreed temporary handle also prevents that physical session from returning to the pool. Async counterparts are available.

### Complex types

Load named types with `GetComplexType(schemaName, typeName, transaction)` using exact dictionary names, then call `CreateValue(...)`. Nested members use their declared type descriptors. Supported scalar members are NUMBER, VARCHAR2/CHAR, RAW, DATE and TIMESTAMP; LOB, cursor, national-type members and cycles are unsupported.

Named results require preloading on the same connection/transaction and binary prepared results or REF CURSOR; call `Prepare()` for parameterless named-type queries. Text complex results are unsupported.

Set `OceanBaseParameter.ComplexType` for NULL, Output and ReturnValue. Non-null Input/InputOutput can infer it from an `OceanBaseComplexValue`. `Size`, `Precision` and `Scale` must be zero.

Pure Output/ReturnValue parameters send a protocol NULL placeholder. The declared complex descriptor remains required for session validation and decoding the returned value; InputOutput still sends its input value and type.

`CreateArrayType` accepts NUMBER, VARCHAR2, CHAR, RAW, DATE or TIMESTAMP, or a preloaded named element type. Factory scalar arrays support input/output; ordinary complex-element arrays accept input only. Anonymous descriptions obtained from results are read-only.

<a id="complex-type-ddl-invalidation"></a>
Descriptions and values are bound to their connection, session, transaction and complex-metadata generation. Close/reopen, transaction changes and `ClearComplexTypeCache` invalidate reuse. In Oracle mode, executing text statements beginning with CREATE, ALTER, DROP, RENAME or TRUNCATE also invalidates all cached descriptions, including nested and factory-array descriptions. The shared SQL scanner ignores comments and quoted text and checks later statements in an allowed text batch; CREATE program units are DDL, while BEGIN/DECLARE bodies and procedure calls are opaque.

Invalidation occurs once when target execution begins transmission, independently of `Statement Cache Size`. Even a rejected DDL conservatively requires reloading. Standalone Prepare, metadata/control probes, SchemaOnly, local validation failures and cancellation before target transmission do not invalidate the dictionary. Reload with `GetComplexType` and reconstruct parameter values before the next bind; the driver does not retry DDL or automatically reload types. New result exchanges cannot attach the old dictionary snapshot. Values already returned retain their immutable data/layout for reading, but their descriptors cannot construct or bind new values after invalidation.

DDL executed by another connection or hidden in procedures, anonymous blocks, functions or triggers is not automatically detected. After such changes, call `ClearComplexTypeCache` on each affected open connection with the correct transaction, then reload and rebuild bindings. This local implementation does not establish real OceanBase/OBProxy DDL, implicit-commit or lock compatibility.

### Array binding

Use `ExecuteArray(rowCount)` / `ExecuteArrayAsync(...)` with PrepareExecute and an explicitly bound transaction. Row count is 1–65535. Input/InputOutput parameters are factory arrays of that length; Output/ReturnValue declares a factory array type. SELECT, DDL and `FetchSize` are unsupported.

Inspect `ArrayBindingResult.Items` for zero-based Succeeded/Failed/Unknown results. Row errors return Failed; protocol or terminal errors throw and can leave partial results in `command.ArrayBindingResult`. Item success does not commit. OUT/RETURNING can return several rows per input; output `Value` becomes `object[][]` only after complete success. Reset InputOutput to a factory value before executing again.

## XA

`connection.GetXaResource()` dedicates the open physical session to XA until Close. Use `OceanBaseXid` with Start, End, Prepare, Commit, Rollback and Recover; sync/async APIs are available. Oracle requires PrepareExecute.

Business commands require an active XA branch and no local `Transaction`. Local transactions and database switching are rejected in XA mode. Close does not complete a branch. XA return codes use `OceanBaseException.XaErrorCode`, separately from `ServerErrorCode`; coordinator recovery is the application's responsibility.

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

## MySQL compression

`OceanBaseConnectionStringBuilder.UseCompression` / `Use Compression=true` requests CLIENT_COMPRESS. The default remains false, even when the server advertises compression. A requested capability missing from the greeting fails before the TLS/authentication response; authentication switches and the final authentication OK remain uncompressed. Compression starts after that OK and before OceanBase identity queries, above TLS when TLS is enabled. Both compatibility modes and all five target assets use the same implementation. The option is preserved when selecting an endpoint and is part of the connection configuration used for pool identity.

The seven-byte outer header wraps an ordinary MySQL framed byte stream. A block can hold several packets or part of a header/payload; original and zlib blocks may mix. Outer sequence numbers increment across reads and writes, wrap at 256, and reset for each new command. MySQL can renumber inner sequences when flushing compressed buffers, so compression validates the outer sequence rather than applying the ordinary inner counter. Multi-packet uploads preserve both protocol counters across direction changes. Complete writes flush compression, including no-response statement commands and LOCAL INFILE terminators.

Writes use a 64 KiB buffer and send original bytes if compression would not shrink a block. Receive input/output buffers are limited by the protocol's 24-bit lengths (at most 0xffffff per block); the ordinary 64 MiB logical-packet cap still applies. SharpZipLib's public zlib APIs validate checksum, finished stream, exact output length and absence of trailing input; dictionaries, malformed/truncated streams and unread buffered tails at exchange boundaries fail closed. Async operations use async transport I/O; codec work itself is synchronous CPU work. No SQL is replayed.

`Use Compression=true;Use OB20=true` currently fails validation before networking: compressed OB20 and proxy reroute are separate, unfinished capabilities. Local framework-zlib peers verify the standard compression path, including TLS and pooling/reset; real OceanBase/OBProxy and cross-platform compression acceptance remain open.
