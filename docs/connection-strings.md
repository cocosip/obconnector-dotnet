# Connection Strings

`OceanBaseConnection` and the modern `OceanBaseDataSource` are configured with a connection string parsed by
`OceanBaseConnectionStringBuilder`. The builder validates every value, so a typo, an unknown keyword, a
malformed value or two keywords that map to the same setting (`Server=a;Host=b`) fails with
`ArgumentException` instead of being silently ignored.

- Keywords are case-insensitive; values are not trimmed except where noted.
- Synonyms: `Host` = `Server`, `Uid` = `User ID`, `Pwd` = `Password`, `SslMode` = `TLS Mode`,
  `CharacterSet`/`CHARACTER SET` = `Character Set`. `Password2`, `Password3`, ... supply additional
  passwords for PAM authentication.
- Booleans must be `true` or `false`; enums match names only (numbers are rejected); counts are
  non-negative integers unless a range is listed.
- `Password`, `PasswordN` and `Server RSA Public Key` are secret: they are excluded from
  `ConnectionString` after Open (unless `Persist Security Info=true`) and from every error message.
- `Server` and `User ID` are required.

## Quick start

MySQL tenant, plain transport (local test environment only - TLS defaults to `VerifyFull`):

```csharp
using var connection = new OceanBaseConnection(
    "Server=127.0.0.1;Port=2881;User ID=root@mysql_tenant;Password=replace-me;Database=app;TLS Mode=Disabled");
await connection.OpenAsync();
```

Oracle tenant with explicit mode validation and the Oracle extensions enabled:

```csharp
using var connection = new OceanBaseConnection(
    "Server=ob.example.com;Port=2881;User ID=app@oracle_tenant;Password=replace-me;" +
    "ExpectedMode=Oracle;Use Oracle Prepare Execute=true;Use Lob Locators=true");
await connection.OpenAsync();
```

Pooled MySQL tenant with a prepared-statement cache:

```csharp
using var connection = new OceanBaseConnection(
    "Server=ob.example.com;User ID=app@mysql_tenant;Password=replace-me;Database=app;" +
    "TLS Mode=VerifyFull;Pooling=true;Min Pool Size=2;Max Pool Size=20;Pool Wait Timeout=5;" +
    "Statement Cache Size=64");
```

Several endpoints, rotated per Open, with two extra TCP retry rounds:

```csharp
using var connection = new OceanBaseConnection(
    "Server=ob1.example.com:2881,ob2.example.com:2881,ob3.example.com:2881;" +
    "User ID=app@mysql_tenant;Password=replace-me;Load Balance=RoundRobin;" +
    "Connect Retry Count=2;Connect Retry Interval=3");
```

Data source with a client certificate for mutual TLS (owned by the builder after `Build`):

```csharp
var source = new OceanBaseDataSourceBuilder(
        "Server=ob.example.com;User ID=app@mysql_tenant;Password=replace-me;TLS Mode=VerifyFull")
    .UseClientCertificate(clientCertificate)
    .Build();
await using var connection = await source.OpenConnectionAsync();
```

Dependency injection:

```csharp
services.AddOceanBaseConnection("Server=ob.example.com;User ID=app@mysql_tenant;Password=replace-me");
services.AddOceanBaseDataSource(
    "Server=ob.example.com;User ID=app@mysql_tenant;Password=replace-me;Pooling=true",
    (_, builder) => builder.UseLoggerFactory(loggerFactory));
```

`AddOceanBaseConnection` registers transient, container-owned connections; the data source registrations
create singletons whose connections the caller disposes.

## Keywords

### Connection and session

| Keyword | Default | Meaning |
| --- | --- | --- |
| `Server` (required, alias `Host`) | - | Comma-separated `host[:port]` or `[IPv6][:port]` candidates. Omitted ports use `Port`. Order and duplicates are preserved. |
| `Port` | `2881` | Default port for endpoints without one, `1`-`65535`. |
| `User ID` (required, alias `Uid`) | - | Login identity, normally `user@tenant`. Must not be empty. |
| `Password` (alias `Pwd`) | empty | Login password. `PasswordN` (`N` >= 2) supplies PAM passwords. |
| `Database` | empty | Initial database/schema sent during authentication. Empty leaves the server default. |
| `ExpectedMode` | unset | `MySql` or `Oracle`. Validates the mode detected from the authenticated session; it never changes the server mode. Unset accepts either mode. |
| `Character Set` (aliases `CharacterSet`, `CHARACTER SET`) | `utf8mb4` | Client encoding for SQL text and ordinary parameters; non-default values add cached session charset verification. Results are always decoded as UTF-8. Supported names are listed in [client character sets](client-character-sets.md). |
| `Persist Security Info` | `false` | When `false`, `ConnectionString` hides the password after Open. Set `true` only if code must re-read the secret from the connection. |

### Timeouts, retries and diagnostics

| Keyword | Default | Meaning |
| --- | --- | --- |
| `Connection Timeout` | `15` | Seconds for one logical Open, including discovery, pool wait and prewarming. Must be >= 0. |
| `Command Timeout` | `30` | Default command timeout in seconds. `OceanBaseCommand.CommandTimeout` overrides it per command. |
| `Slow Query Threshold` | `0` | Milliseconds above which execute/cursor lifetime emits a slow-operation diagnostic. `0` disables reporting. |
| `Connect Retry Count` | `0` | Additional complete TCP candidate rounds after the first, `0`-`10`. |
| `Connect Retry Interval` | `1` | Seconds between failed TCP candidate rounds, `1`-`60`. |
| `Host Recheck Seconds` | `0` | Seconds to deprioritize an endpoint after a TCP failure, `0`-`3600`; `0` disables the failure hint. |
| `Load Balance` | `Sequential` | `Sequential` tries candidates in supplied order; `RoundRobin` rotates the first candidate per connection or shared data source. |

### TLS and authentication

| Keyword | Default | Meaning |
| --- | --- | --- |
| `TLS Mode` (alias `SslMode`) | `VerifyFull` | `VerifyFull` encrypts and validates the chain plus the endpoint/`TLS Server Name` against the OS trust store; `Required` encrypts without identity verification; `Disabled` is cleartext. `Required` and `Disabled` must be chosen explicitly. |
| `TLS Server Name` | endpoint host | Host name validated against the certificate, for deployments whose endpoint name differs from the certificate. Must not be empty when set. |
| `Check Certificate Revocation` | `false` | Enables platform revocation checks during verified TLS. |
| `Server RSA Public Key` | empty | Trusted RSA `SubjectPublicKeyInfo` PEM (at most 16 KiB) used to encrypt the password for SHA-2 authentication without verified TLS. Treated as a secret. |
| `Allow Public Key Retrieval` | `false` | Allows downloading the server RSA key for SHA-2 authentication when no verified TLS and no pinned key exist. Enabling it accepts an unauthenticated key exchange. |
| `Service Principal Name` | empty | Kerberos service principal overriding the server's GSSAPI challenge. Must be valid Unicode, at most 1024 characters, without control characters. |

Client certificates and custom server-validation callbacks are not keywords; assign
`OceanBaseConnection.ClientCertificate` / `RemoteCertificateValidationCallback` before Open, or use
`OceanBaseDataSourceBuilder.UseClientCertificate(...)` / `UseRemoteCertificateValidationCallback(...)`.
A callback requires `TLS Mode=VerifyFull`; a client certificate cannot be used with `TLS Mode=Disabled`.

### Pooling and statement cache

| Keyword | Default | Meaning |
| --- | --- | --- |
| `Pooling` | `false` | Reuses authenticated physical sessions, validating and resetting them before return. |
| `Min Pool Size` | `0` | Sessions prewarmed before a borrow. Must not exceed `Max Pool Size`. |
| `Max Pool Size` | `100` | Maximum physical sessions per pool, at least `1`. |
| `Pool Wait Timeout` | `Connection Timeout` | Seconds a borrow waits for a free session. |
| `Connection Idle Lifetime` | `0` | Seconds an idle session is kept before being closed; `0` disables idle retirement. |
| `Connection Lifetime` | `0` | Seconds a physical session may live before retirement; `0` disables lifetime retirement. |
| `Statement Cache Size` | `0` | Idle prepared statements retained per physical session, `0`-`1024`; `0` disables caching. Commands must be disposed to return entries. |

`Pooling=false` still accepts the sizing keywords (they are ignored). `OceanBaseConnection.ClearPool` /
`ClearAllPools` and data-source `Clear()` retire sessions without interrupting active commands.

### Protocol and provider behavior

| Keyword | Default | Meaning |
| --- | --- | --- |
| `Use OB20` | `false` | Requests OB20 framing after server negotiation. Client and server support is unverified against a real deployment. |
| `Enable Full Link Trace` | `false` | Requests server full-link tracing over OB20; requires `Use OB20=true`. |
| `Use Compression` | `false` | Requires the standard MySQL zlib compression protocol. Cannot be combined with `Use OB20=true`. |
| `Allow Multi Statements` | `false` | Opts MySQL text execution into multiple statements per command. |
| `Allow Local Infile` | `false` | Accepts server-requested `LOCAL INFILE` and asks `OceanBaseConnection.LocalInfileStreamFactory` for the stream. The driver never opens the server-provided file name itself. |
| `Use Batch Multi Send` | `false` | Opts prepared batches into multi-send pipelining. |
| `Batch Multi Send Number` | `100` | Maximum prepared executions written before responses are drained, at least `1`. |
| `Treat Tiny As Boolean` | `true` | Maps signed MySQL `TINYINT(1)` to `bool`; set `false` to read integers. |
| `Use Affected Rows` | `false` | UPDATE reports changed rows instead of matched rows. |
| `Use Oracle Prepare Execute` | `false` | Enables Oracle procedures, `OUT` parameters and other PrepareExecute paths after version discovery. |
| `Use Lob Locators` | `false` | Requests Oracle LOB locators; requires `Use Oracle Prepare Execute=true`. |

## Validation rules

The builder rejects a connection string when:

- a required keyword is missing or empty (`Server`, `User ID`);
- the keyword is unknown (only the keywords above plus `PasswordN` are accepted);
- two synonyms of the same setting are both supplied (`Server=...;Host=...`);
- a value fails its range, enum or boolean check, or a numeric value is negative;
- `Use Compression=true` and `Use OB20=true` are combined, or `Enable Full Link Trace=true`
  without `Use OB20=true`, or `Use Lob Locators=true` without `Use Oracle Prepare Execute=true`;
- `Max Pool Size` is below `1`, or `Min Pool Size` exceeds `Max Pool Size`.

Parsing failures throw `ArgumentException` naming the offending keyword; secret values are never echoed.

## See also

- [Compatibility and known limitations](compatibility.md) for what each option does and does not guarantee.
- [Credentials](credentials.md) for provider-based credentials and rotation, which replace literal passwords.
- [Pool metrics](diagnostics.md#pool), [protocol diagnostics](diagnostics.md#protocol) and
  [session I/O diagnostics](diagnostics.md#session-io) for the telemetry these options feed.
- [Development tracking list](obconnector-dotnet-missing-features-roadmap.md) for protocol features that
  still await a real server (OB20, full-link trace, compression, local infile, proxy/HA).
