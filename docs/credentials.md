# Credential provision and rotation

Credential providers are implemented locally on all five product assets and supply authentication material without rebuilding connection strings. See the [module verification ledger](obconnector-dotnet-progress.md) for execution results, historical failures and remaining acceptance. Local native/PAM wire tests do not certify real OceanBase plugins, credential services or KDC interoperability.

## Public surface

```csharp
public interface IOceanBaseCredentialProvider
{
    OceanBaseCredential GetCredential(OceanBaseCredentialContext context,
        CancellationToken cancellationToken);
    Task<OceanBaseCredential> GetCredentialAsync(OceanBaseCredentialContext context,
        CancellationToken cancellationToken);
}

public OceanBaseCredential(string userId, string password, long version,
    DateTimeOffset? expiresAt = null,
    IReadOnlyDictionary<int, string>? additionalPasswords = null);
```

The immutable context exposes configured `Server`, `Port`, `UserId` and `Database` before address discovery. It has no password, connection string or mutable connection reference. Configured identity remains the context identity even when the provider returns a different authentication user.

The immutable credential exposes `UserId`, `Password`, `Version`, `ExpiresAt` and a copied, read-only `AdditionalPasswords` dictionary. User ID must be nonempty and contain no NUL; password cannot be null; version must be nonnegative; additional password numbers start at 2 and values cannot be null. Existing authentication encoding/transport restrictions still apply. `ToString()` contains no authentication material. Strings remain managed objects; this API does not promise memory zeroing.

Set a provider before opening an ordinary connection:

```csharp
using var connection = new OceanBaseConnection(connectionString)
{
    CredentialProvider = provider // application-owned IOceanBaseCredentialProvider
};
await connection.OpenAsync(cancellationToken);
```

On .NET 8/9/10, a data source captures the provider reference at Build:

```csharp
using var source = new OceanBaseDataSourceBuilder(connectionString)
    .UseCredentialProvider(provider)
    .Build();
await using var connection = await source.OpenConnectionAsync(cancellationToken);
```

Ordinary connection configuration is frozen while open/opening. Source connections inherit fixed configuration; changing the builder afterwards does not change an existing source. The driver never disposes the provider. Implementations must be concurrency-safe and own their cache, refresh coalescing and external secret-service lifecycle.

## Acquisition and static configuration

Each logical Open calls the matching sync/async provider method once, including when an authenticated idle session is available. The snapshot replaces the configured user, primary password and **all** additional passwords; absent additional passwords do not fall back to PasswordN from the connection string. Acquisition failure never falls back to static credentials or an idle session. A null provider preserves existing static authentication behavior.

The same snapshot supplies minimum-pool warmup, any newly created physical sessions and TCP endpoint retries during that Open. Pool acquisition happens inside the rent reservation: exceptions and cancellation release the reservation even before a session exists. No credential is fetched for ordinary command execution on an already opened connection, and the driver has no background credential refresh timer.

ConnectionString and the configured options retain their original configuration; returned credentials are never inserted into their display values. Avoid putting a static password in configuration when the provider replaces it. Existing PersistSecurityInfo behavior applies to secrets explicitly supplied in the original string.

## Pool identity and version ordering

Ordinary pools use the configured string, resolver reference, TLS validation reference, certificate reference/fingerprint, and **provider reference identity**. Objects with equal values or overridden Equals do not share policies. Returned passwords, tokens and credential versions are not registry keys or observable pool identifiers. Sources retain private pools even when they share a provider instance.

Within a live pool, Version orders authentication material. Increase it whenever user, primary password or additional passwords change. A lower version is rejected; the same version with different authentication material is rejected. This prevents an older asynchronous acquisition from rolling a pool back. Same-version expiry changes are allowed, but do not extend the expiry attached to physical sessions already authenticated.

The pool privately retains the current immutable snapshot for equality checks. Refresh concurrency/coalescing is the provider's responsibility; the driver does not serialize calls to user code. Providers should return a cached current snapshot promptly and arrange refresh before it expires.

Higher-version acceptance retires the old generation atomically:

| Session state | Behavior |
| --- | --- |
| Idle | Removed and closed before reuse |
| Borrowed / active command | Continues; remains counted against capacity and is destroyed on return |
| Already selected or physical handshake in flight | May complete its original Open if still valid; is marked retired and cannot reenter idle reuse |
| Pending old snapshot before selection or next warmup reservation | Fails as superseded; cannot create a reusable old-generation session |
| Warmup failure | Rolls back only sessions still idle; cannot destroy a session another Open has borrowed |

New credentials do not migrate active transactions or replay commands. A refresh failure before a valid new snapshot is accepted does not retire healthy existing sessions, but that logical Open fails. Accepting a newer snapshot retires old sessions even if the later network/authentication attempt fails; there is no rollback to the old credentials.

ClearPool/ClearAllPools/source Clear retain their existing capacity/retirement rules. A live source's Clear preserves its credential version history. Empty ordinary registry pools may be removed and disposed, so version history is pool-local rather than a durable global anti-rollback store. Rebuilding a source or creating a new pool starts a new history; the provider must enforce any stronger ordering/revocation policy.

## Expiration and cancellation

ExpiresAt is an optional absolute DateTimeOffset checked against UTC. The driver rejects an already expired snapshot, checks before each authentication credential write, and validates the physical and exact logical-Open snapshot at final publication. Reuse also checks the snapshot after probing, so an older physical credential cannot mask a newly supplied snapshot's expiry. Application diagnostic/log callbacks can delay Open; the final publication check runs after the Opening callback. Expiry after that checkpoint does not revoke an authenticated active connection.

Idle selection and return reject expired physical credentials. Expiry during reset also destroys the session. A later same-version snapshot with extended expiry can create a new physical session; it does not revive an expired old one. Active commands are not interrupted solely because their login credential expires. Applications requiring immediate revocation must enforce server-side policy or explicitly stop their borrowed work.

With a provider, ConnectionTimeout bounds acquisition, discovery, pool waiting, warmup and handshake together, including nonpooled Open. Zero retains an unlimited connection budget. Pool waiting is additionally bounded by snapshot expiry even if both connection and pool timeouts are unlimited.

Async provider methods must return their Task promptly. Cancellation stops waiting even if the task ignores its token; late results cannot open a connection and late faults are observed. Sync methods must honor the supplied token and return promptly: arbitrary blocking user code cannot be forcibly stopped. Cancellation callbacks must not throw. Close and caller cancellation use the same per-Open cancellation/generation lifecycle as ordinary connections.

## Failure and diagnostic rules

| Failure | Result |
| --- | --- |
| Provider throws, returns null credential or null Task | OceanBaseException with a fixed provider-failure message, without retaining the potentially secret-bearing original exception |
| Expired credential | OceanBaseException identifying expiry; no static fallback |
| Lower version or same version with changed authentication | OceanBaseException identifying the ordering/identity violation |
| Caller cancellation / Close | OperationCanceledException |
| Shared connection budget exhausted | TimeoutException |
| Server authentication rejection | Existing server error classification; no endpoint/authentication downgrade |

Logging, Activity and Meter do not record returned credentials, provider exceptions or credential versions. Provider code must enforce the same rule for its own diagnostics. TLS identity verification and complete-password plugin requirements are unchanged. A credential provider is not a KDC, a GSSAPI implementation or a durable distributed transaction coordinator.

Implementation and regression entry points: `CredentialAcquisition`, `PhysicalSessionPool.Credentials`, `OceanBaseConnection.Credentials`, `CredentialProviderTests`, `CredentialProviderLifecycleTests`, `CredentialProviderDiagnosticsTests` and `CredentialProviderConsumerTests`. See the [completeness baseline](obconnector-dotnet-completeness.md) and [module verification ledger](obconnector-dotnet-progress.md) for results and remaining real-server acceptance.
