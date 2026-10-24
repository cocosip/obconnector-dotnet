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

### Explicit pool clearing

All five assets expose `OceanBaseConnection.ClearPool(OceanBaseConnection)` and `ClearAllPools()`. Modern data sources expose `OceanBaseDataSource.Clear()`. ClearPool rejects null/disposed connections, is a no-op for non-pooled or absent ordinary pools, and does not connect, resolve addresses or create a pool. It snapshots the connection's current raw configuration and resolver, validation callback and client-certificate references; a closed connection that has never opened can therefore clear an existing matching pool. It never reads certificate contents, so Reset/Dispose of an original cannot block cleanup; all historical fingerprints for the same reference identity match. Changing connection configuration before clearing targets the new identity, not the former one.

Clear closes idle physical sessions immediately, marks borrowed sessions for destruction when returned, and advances the pool generation. An Open already in flight can finish, but its pre-clear session cannot later be reused; pre-clear warmup completions are discarded before becoming idle. Commands, readers, transactions and already borrowed connections are not interrupted or replayed. Retired borrowed sessions still count toward MaxPoolSize until closed. A return that observes retirement skips reset; if reset was already in progress it may finish, but that session is destroyed rather than published idle. Capacity waiters are signaled when sessions are removed. Clear does not populate MinPoolSize immediately; subsequent Opens perform warmup.

ClearAllPools operates on a snapshot of ordinary registry pools; new pools created concurrently need not be included. It does not clear private sources. Use source.Clear(), or ClearPool with one of that source's connections, for the private pool. Clearing keeps the source and its immutable configuration/certificate snapshot available; Dispose still ends source use. Empty ordinary registry pools are removed and release owned copies. Clearing does not replace static credentials, certificates or callbacks: select a new configuration/source when changing them, and clear the old identity or dispose its source to retire old sessions. Credential-provider versions can rotate authentication material within a live pool as described in the [credential contract](credentials.md). Idle/lifetime reaping remains lazy unless explicitly cleared.

## Commands and readers

- Use Oracle `:name` or MySQL positional `?` bindings. Parameterized execution uses server prepare; `Prepare()` explicitly prepares a command.
- Oracle anonymous blocks and stored-program DDL are sent as single program units; internal semicolons do not require `Allow Multi Statements`. The server validates the complete program syntax, including any invalid trailing SQL. Ordinary SQL boundary checks use the active compatibility mode's quoting and comment rules.
- For `LOAD DATA LOCAL INFILE`, opt in with `Allow Local Infile=true` and set `LocalInfileStreamFactory` before opening. The factory receives the server-supplied filename as opaque text and must return a readable stream; the driver uploads it in chunks, sends the terminating empty packet, reads the final response, and disposes the stream. Missing factories, read failures, cancellation, the 64 MiB default upload limit, or a lost final response break the physical session and report an unknown outcome.
- Set `DbType` or `OceanBaseDbType` when inference from `Value` is insufficient. Use `DBNull.Value` for SQL NULL. `ResetDbType()` restores inference.
- Read all results with `Read` and `NextResult` to establish completion. Closing an unread reader destroys its unfinished session; disposing a field stream does not finish its parent reader.
- `CommandBehavior.SequentialAccess` exposes field streams without buffering the whole result. `SingleRow` returns only the first row of each exposed result and drains the remainder; `SingleResult` drains later results without exposing them. `SchemaOnly` accepts text prefixes `SELECT`, `SHOW`, `DESCRIBE`, `DESC`, `EXPLAIN` or `VALUES` after whitespace/comments. It still sends and executes the query, exposes column metadata, and hides/drains rows. Prefix filtering does not prove that functions have no side effects or avoid query locks and transfer costs. Do not start another operation on the connection while reading.
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

They require CommandType.StoredProcedure and an open idle connection. Bind the command to the current local transaction when one exists. They read the visible routine directory and its complete top-level signature without executing the target. MySQL StoredProcedure execution remains unsupported; Oracle execution still requires Use Oracle Prepare Execute=true and its existing type/direction rules.

MySQL accepts routine or database.routine, with optional backticks; an unqualified name uses DATABASE(). Oracle accepts routine, owner.routine or package.routine, and owner.package.routine, with double-quoted identifiers retaining case and unquoted identifiers converted to uppercase. Unqualified Oracle names use CURRENT_SCHEMA. Two-part Oracle names check both interpretations and reject ambiguity. The optional overload is an exact Oracle directory selector, not argument-based overload inference; MySQL rejects it. No matching visible routine, ambiguous signatures, incomplete metadata, unsupported type declarations or unavailable named descriptors fail explicitly. Synonym resolution is not promised.

Success replaces the parameter collection with fresh DBNull-valued parameters ordered by ordinal position; a function return is first and named RETURN_VALUE. Existing parameter objects are detached and their values are not copied. Failure/cancellation leaves the original collection unpublished; concurrent member changes are detected atomically and retained. Avoid changing individual parameter properties concurrently: that is outside the membership-version guarantee. The command/session/transaction context is checked before publication, and Execute/Prepare/another derivation cannot overlap. Valid complex descriptors loaded before a later failure may remain cached.

The operation reserves one physical session across routine, argument and named-type queries. It uses CommandTimeout for each query, with no total-duration guarantee. CancellationToken, Cancel and Dispose cover the operation and query gaps; a canceled in-flight request follows the existing connection-break/unknown-outcome rules. Directory permission errors retain the server error code and SQL state. Each routine/argument directory table is bounded to 1024 rows and 4 MiB of UTF-8 string content; named descriptors retain the existing complex-type member/depth/cache bounds.

Supported mappings and rejection boundaries are listed in the [Schema contract](schema.md#parameter-derivation). Directory, privilege, server-version and OBProxy interoperability require separate real-server validation.

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

`GetSchema` advertises 20 collections in Oracle mode and 18 in MySQL mode, including views, indexes/columns, keys/constraints, foreign keys/columns, sequences and users; named Types and Synonyms are Oracle-only. `Restrictions` describes ordered exact-match parameters; `MetaDataCollections` reports counts. Sequence numbers use `OceanBaseDecimal` without CLR decimal truncation. .NET 8/9/10 also provides `GetSchemaAsync`. See the [complete Schema contract](schema.md) for shapes, mode identity, dictionaries and permissions. Static `DataTypes` reports CLR/provider identities, precision/scale, creation templates and conditional parameter/result support; ordinary and provider-specific numeric/timestamp representations are separate rows with selection conditions. It requires no query and retains connection lifecycle guards. Routine metadata does not derive command parameters automatically.

Set `OceanBaseConnection.AddressResolver`, or `OceanBaseDataSourceBuilder.UseAddressResolver(...)`, to an `IOceanBaseAddressResolver`. Each logical Open, including pool reuse, calls its matching sync/async method once. One parsed snapshot validates idle pool candidates and supplies any prewarmed/new physical sessions. Idle sessions whose configured host/port no longer appear in that snapshot are discarded before reuse, including below `Min Pool Size`; active borrowed sessions finish normally and are checked against the snapshot of the next borrower. This checks configured host/port membership, not DNS IP changes behind the same hostname.

All five assets expose `IOceanBaseCredentialProvider? OceanBaseConnection.CredentialProvider`; modern builders expose `UseCredentialProvider`. One logical Open resolves an immutable `OceanBaseCredential` through the matching sync/async method before address discovery, including idle reuse. The provider replaces the configured user and all passwords, with no failure fallback. Provider reference identity isolates ordinary pools; source connections inherit a fixed provider. Versions order rotation, expiry bounds admission/wait/reuse/return, and retired borrowers continue until return. Acquisition participates in the total connection budget. The ownership, ordering, error, cancellation and expiry contract is in [credentials.md](credentials.md); execution results and historical failures are in the [verification ledger](obconnector-dotnet-progress.md), and real plugin/KDC acceptance remains open.

Return trusted addresses for the same tenant in `Server` syntax. Failed, invalid or canceled discovery cannot fall back to an idle session or configured address. A pooled Open with a custom resolver applies `Connection Timeout` across resolution, waiting, prewarming, probe and handshake; zero removes this total timeout. Caller cancellation and Close interrupt discovery/waiting; late async resolver results cannot open a connection. Candidates are retried only after TCP connection failure. TLS/authentication failures do not switch endpoints.

Ordinary pooled connections share only when the connection string, resolver reference identity, credential-provider reference identity, TLS validation callback reference identity, and client certificate reference identity plus SHA-256 DER fingerprint all match; overridden value equality does not merge policies. Changing a closed connection's resolver, provider, validation callback or client certificate selects its matching pool on the next Open. Each data source retains its own private pool even if these policy objects are shared with another source; source connections keep their captured provider. The resolver and provider remain caller-owned and must be concurrency-safe. See P0-06 in the [completeness baseline](obconnector-dotnet-completeness.md).

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
