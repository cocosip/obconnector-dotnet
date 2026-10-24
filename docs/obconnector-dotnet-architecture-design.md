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
| `Min Pool Size` | 0 | Ensures at least this many physical sessions before a borrow; a failed warmup rolls back sessions created by that attempt. |
| `Max Pool Size` | 100 | Maximum physical sessions per matching pool; additional opens wait for a return. |
| `Pool Wait Timeout` | `Connection Timeout` | Seconds to wait for a physical session; zero waits indefinitely. |
| `Connection Idle Lifetime` | 0 | Seconds an idle session may remain before lazy reaping; zero disables the limit. |
| `Connection Lifetime` | 0 | Maximum physical-session age in seconds; zero disables the limit. |
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
`OceanBaseConnection.ClientCertificate` before `Open`/`OpenAsync`. The certificate
is passed to `SslStream` and is not disposed by the connection. Client certificates
are deliberately rejected with `Pooling=true`; use a separate direct connection
for each certificate identity until certificate-aware pool partitioning exists.

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

## Commands and readers

- Use Oracle `:name` or MySQL positional `?` bindings. Parameterized execution uses server prepare; `Prepare()` explicitly prepares a command.
- Oracle anonymous blocks and stored-program DDL are sent as single program units; internal semicolons do not require `Allow Multi Statements`. The server validates the complete program syntax, including any invalid trailing SQL. Ordinary SQL boundary checks use the active compatibility mode's quoting and comment rules.
- For `LOAD DATA LOCAL INFILE`, opt in with `Allow Local Infile=true` and set `LocalInfileStreamFactory` before opening. The factory receives the server-supplied filename as opaque text and must return a readable stream; the driver uploads it in chunks, sends the terminating empty packet, reads the final response, and disposes the stream. Missing factories, read failures, cancellation, the 64 MiB default upload limit, or a lost final response break the physical session and report an unknown outcome.
- Set `DbType` or `OceanBaseDbType` when inference from `Value` is insufficient. Use `DBNull.Value` for SQL NULL. `ResetDbType()` restores inference.
- Read all results with `Read` and `NextResult` to establish completion. Closing an unread reader destroys its unfinished session; disposing a field stream does not finish its parent reader.
- `CommandBehavior.SequentialAccess` exposes field streams without buffering the whole result. `SingleRow` returns only the first row of each exposed result and drains the remainder; `SingleResult` drains later results without exposing them. `SchemaOnly` accepts only read-only text queries beginning with `SELECT`, `SHOW`, `DESCRIBE`, `DESC`, `EXPLAIN` or `VALUES` after whitespace/comments, exposes column metadata, consumes rows and returns no data rows. Do not start another operation on the connection while reading.
- Positive `OceanBaseCommand.FetchSize` enables forward-only server cursor pages. Oracle `UseCursorOffset=true` additionally requires OceanBase >=2.2.5; it does not provide scrolling.
- `AllowClientSidePrepareFallback=true` permits limited scalar-input text fallback only after automatic prepare returns a validated `1295/HY000`. Explicit Prepare, cursors and Oracle PrepareExecute never fall back. Date/time, special types and output parameters are excluded; MySQL non-null strings require trusted session collation. Fallback literals may appear in server SQL logs.

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

## Transactions, batches and failures

Assign the active transaction to each business command. MySQL supports ReadUncommitted, ReadCommitted, RepeatableRead and Serializable; Oracle supports ReadCommitted and Serializable. Unspecified isolation uses MySQL RepeatableRead or Oracle ReadCommitted. Opening inside an ambient `TransactionScope` creates one local volatile enlistment and commands/Batch use that transaction; distributed promotion and conflicting ambient/local/XA combinations are rejected. MySQL-mode sessions with `@@autocommit` disabled are rejected at Open; see the identity rules above.

On .NET 8/9/10, `Save(name)` and `Rollback(name)` operate on the active transaction; `Release(name)` is MySQL-only. Names must contain 1–30 ASCII letters/digits/underscores and start with a letter. Leading underscores, including `__EFSavePoint`, are rejected. Reacquire LOBs, cursors and complex-type values after savepoint rollback.

Batches execute sequentially. A later failure does not undo earlier entries; use an explicit transaction for transactional writes. `RecordsAffected` cannot identify a safe restart position.

| Result | Application action |
| --- | --- |
| `OceanBaseException.IsExecutionOutcomeUnknown=true` | SQL may have executed. Reconcile business state through a new connection before retrying. |
| Trusted server ERR | Inspect `ServerErrorCode` and `SqlState`; follow the remaining transaction/session state. A false unknown flag does not prove absence of side effects. |
| Confirmed commit/rollback followed by failed session cleanup | Retain the confirmed outcome and reconnect; do not repeat completed work. |

The driver never automatically replays sent SQL or moves an active transaction to another endpoint. Cancellation or a local timeout does not prove that server execution stopped.

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

Descriptions and values are bound to their connection, session and transaction generation. Close/reopen, transaction changes and `ClearComplexTypeCache` invalidate reuse. After external type DDL, clear the cache and reload; changes are not detected automatically.

### Array binding

Use `ExecuteArray(rowCount)` / `ExecuteArrayAsync(...)` with PrepareExecute and an explicitly bound transaction. Row count is 1–65535. Input/InputOutput parameters are factory arrays of that length; Output/ReturnValue declares a factory array type. SELECT, DDL and `FetchSize` are unsupported.

Inspect `ArrayBindingResult.Items` for zero-based Succeeded/Failed/Unknown results. Row errors return Failed; protocol or terminal errors throw and can leave partial results in `command.ArrayBindingResult`. Item success does not commit. OUT/RETURNING can return several rows per input; output `Value` becomes `object[][]` only after complete success. Reset InputOutput to a factory value before executing again.

## XA

`connection.GetXaResource()` dedicates the open physical session to XA until Close. Use `OceanBaseXid` with Start, End, Prepare, Commit, Rollback and Recover; sync/async APIs are available. Oracle requires PrepareExecute.

Business commands require an active XA branch and no local `Transaction`. Local transactions and database switching are rejected in XA mode. Close does not complete a branch. XA return codes use `OceanBaseException.XaErrorCode`, separately from `ServerErrorCode`; coordinator recovery is the application's responsibility.

## Schema, address resolution and diagnostics

`GetSchema` exposes MetaDataCollections, DataSourceInformation, Tables, Columns, Procedures and ProcedureParameters. Query `MetaDataCollections` for restriction counts. .NET 8/9/10 also provides `GetSchemaAsync`. Routine metadata does not derive command parameters automatically.

Set `OceanBaseConnection.AddressResolver`, or `OceanBaseDataSourceBuilder.UseAddressResolver(...)`, to an `IOceanBaseAddressResolver`. Each Open calls its matching sync/async method once. Return trusted addresses for the same tenant in `Server` syntax; invalid or failed discovery does not fall back to configured or stale addresses. Candidates are retried only after TCP connection failure, within the total connection budget. TLS/authentication failures do not switch endpoints.

Use builder `UseLoggerFactory(...)` or the connection's `ILoggerFactory` constructor overload. Log categories are `OceanBaseConnector.Connection`, `.Command` and `.Transaction`. Logs omit SQL, parameter values and credentials.

`ActivitySource` and `Meter` are named `OceanBaseConnector`. Metrics are `oceanbase.client.operations` and `oceanbase.client.operation.duration` (seconds). Tags identify operation, mode, outcome and unknown outcome; readers complete their execution span when consumption finishes or fails. Local diagnostics do not require server full-link tracing.
