# Acceptance Criteria

Version: 2026-10-29. Used together with the [feature matrix](obconnector-dotnet-progress.md) and the [gap matrix](obconnector-dotnet-completeness.md).
Acceptance targets a feature's behavior on a **specified deployment combination**, not "a successful connection" or "a large number of tests".

Only database/protocol capabilities or ADO.NET behaviors already included in the .NET target are accepted; Java/JDBC-exclusive interfaces, JVM integration
and JDBC behavior-compatibility configurations have no equivalent acceptance item, see the [scope decision table](obconnector-dotnet-missing-features-roadmap.md#jdbc-exclusive-interfaces-stay-out-of-the-net-matrix).
Excluded items are checked only for existing rejection/safety boundaries and require no positive support; undefined combinations of optional capabilities do not automatically become mandatory items.

## Fixed scope and closing gate

From 2026-11-04, current open items and their bounded closing conditions follow the [gap matrix G01–G15](obconnector-dotnet-completeness.md#bounded-gap-matrix)
and the [development list's fixed execution table](obconnector-dotnet-missing-features-roadmap.md#fixed-execution-checklist).
The text below describes acceptance methods within the declared support scope and cannot be read as requiring the Cartesian product of all modes/directions/types/APIs/deployments.

Before execution, the function contract, OceanBase version/mode, profile, case list, and cleanup requirements must be fixed; after a task passes, new combinations must not be appended automatically.
Java/JDBC, native Oracle/MySQL support, synthetic fixtures, or theoretical protocol combinations cannot by themselves justify mandatory OceanBase work.
A newly mandatory scope must first update the gap matrix's basis and closing conditions and be confirmed by the user; undecided scope is explicitly retained and neither implemented nor deleted automatically.

"Code module complete", "specified-profile acceptance complete", and "release acceptance complete" are separate conclusions:
code and local tests cannot replace the live database, a live-database subset cannot replace undeclared/unexecuted platform deployments, and environment blockers and isolation do not count as passed.
A P4 subset is not reopened indefinitely because some combinations are not yet included; a code gap or an included acceptance item left open also means the whole SDK cannot be declared complete.

## Units of a support claim

Each report binds at least: source commit, the official reference version adopted, the real OceanBase product version, tenant mode,
direct connection or ODP and its version, standard/compressed/OB20, TLS policy/authentication plugin, client OS/runtime/selected package asset,
relevant options, case ID, pass/fail/skip/deferred and reasons, cleanup result, and the redacted report path.
When Java is used as a comparison, record its reference version separately; when it is not used, mark it not applicable, and do not treat Java support as a general acceptance precondition.
Do not record real addresses, tenants/accounts/passwords, certificate private keys, SQL parameters, or full connection strings in documents/logs.

Evidence accumulates layer by layer and cannot substitute across layers:

| Layer | Project/entry point | What it proves | What it does not prove |
| --- | --- | --- | --- |
| Source | O/J basis and product implementation in the scope decisions | Requirement applicability, actual branches, rejection points | Live-database/platform availability |
| Default regression | UnitTests, DependencyInjection.Tests | Parsing, protocol/resource state, safety boundaries, and DI | Real tenant functionality |
| Public DLL consumer | ConsumerTests, NetStandard sample | Real API/asset isolation, synthetic TCP protocol | Independent execution on all TFMs, live database, real TLS |
| Local security | Standalone SecurityTests | OS certificate/key and synthetic TLS/mTLS branches | Enterprise CA, real TLS/auth/OBProxy |
| Full live database | IntegrationTests explicit profiles | Complete public API group for the selected mode/deployment | Unselected profiles/skipped items and other deployments |
| Acceptance report | Acceptance's fifteen groups + explicit special profiles | Fixed-case results/environment/cleanup, reproducible summary | Replacing full Integration or claiming an overall pass from exit 0 |
| Package release | Verify-Packages.ps1 and -VerifyLive | nupkg/dependencies/hash/asset selection/consumer against the selected live database | Compatibility with all OS/API/ORM |

## Minimum pass criteria per capability

| Capability ID | Behavior that must be checked | Test entry point/coverage owner |
| --- | --- | --- |
| C01–C04/C16 | Strict configuration/scope/redaction, real identity/version for both modes, ExpectedMode rejection, Open/reopen, cancellation, factory, ChangeDatabase under permissions | connection; Integration Connections |
| C05–C09 | Trusted chain + correct host succeeds; wrong name/untrusted/expired/missing client certificate fails without downgrade; credential expiry/rotation/redaction | SecurityTests + real TLS/plugin profiles; E01 is currently open |
| C10–C14 | Capacity limits/wait cancellation/warm-up/pool clearing; reset and replacement of the same physical session proven separately; late release of old readers/LOBs/cursors/XA does not affect a new lease; routing isolation | connection/faults; Integration Connections (PoolIsolation/PoolResourceIsolation) and discovery specials; real HA verified separately |
| Q01–Q07 | text/prepare/fallback/multi-statement kept separate; parameter values/NULL/direction/type, quotes/q quoting, failure does not mis-execute, atomic OUT publication | crud/routines; Integration Commands/Oracle/Types and the corresponding multi-statement specials |
| Q08–Q13/Q23 | Real column metadata, all rows/multiple results, early-read close, typed getter/out of range, cursor paging/release; SchemaOnly does not execute; KeyInfo actually hides keys | reader/schema/oracle; Integration Readers/Commands/Oracle |
| Q14–Q19 | Executed prefix, failed entries/suffix, affected rows/output, transactions/triggers/defaults, stream ownership/chunking/limits/cancellation, cache invalidation | batch/streams/oracle; Integration Batches/Streams/Oracle and corresponding specials |
| T01–T12/T19/T20 | Type inference/explicit input, NULL, boundaries/overflow, exact values, column identity, independent read after storage, text and binary results, encoding bad bytes | types and the corresponding Integration type/character-set groups; must not merely SELECT parameters or look only at column names |
| T13–T15 | named/anonymous, input/output, ordinary column/PL, nested/NULL covered separately; real large LOB reads/writes/invalidations/DDL/reset/old resources | lob/complex/oracle; Integration Oracle/Connections resource specials |
| X01–X04/Q22 | Commit/rollback visibility on an independent connection, savepoints, DDL boundaries, XA recover/rollback before the decision is persisted and recovery after it is persisted; unknown outcomes checked independently with no replay | transactions/xa/faults; Integration Transactions/Xa/Faults and ambient specials |
| P01–P05 | Actual negotiation and bidirectional frames, fragmentation/sequence/checksum/truncation/cancellation, rejected combinations; trace server control and export kept separate | diagnostics and ob20TraceProfile; real compression/OB20 currently open under E10 |
| M01–M03 | Permission/catalog accuracy, unauthorized errors not fabricated; no sensitive labels/high cardinality; listener unload/overhead/lifetime | schema/diagnostics; corresponding Unit/Consumer/Integration |
| R01 | Five-asset build, baseline free of modern API leakage, actual runtime package consumption; Windows can cross-publish Linux; corresponding real-machine records are required only when Linux real-machine runtime behavior is declared | eng/Verify-Packages.ps1, Consumer; -VerifyLive verified separately |

Every supported input has at least: normal value, typed NULL, boundary, error value/error mode, sync/async (if provided),
independent check after storage/output, and session state after a failure. Mode-specific differences must be executed separately; an Oracle pass cannot substitute for MySQL.
Outputs, cursors, LOBs, and complex values must additionally verify ownership and release; unsupported shapes must be rejected before sending while keeping the connection usable (already-sent failures follow the unknown contract separately).
A new type/option must first confirm the .NET usage scenario and the applicable database/protocol or ADO.NET contract basis, then define the public API and default/opt-out behavior, negative tests, README contract, and real acceptance plan before entering the matrix; a Java interface or same-named option alone is not sufficient to start work.

## Current acceptance baseline and reproducibility gate

The following is the minimum check baseline set by this review; it is not a passed result and does not extend to the full set of native database features.

| Dimension | Requirement |
| --- | --- |
| Server | Existing candidate version 4.4.2.2, independently for each mode; V4.3.5 is a documentation reference only, and other versions require their own reports |
| Basic profile | Both modes direct, standard protocol, UTF8 baseline; current TLS Disabled results are labeled experimental-environment only and cannot cover the default VerifyFull |
| Feature profile | The corresponding profile must be enabled for declared support of prepare/locator/pool/cache/batch/LOCAL INFILE etc.; not enabling it proves only the default path |
| Runtime | Each declared OS/runtime is executed separately; each of the five assets has a build and a public package consumer. netstandard2.0/2.1 must record the actual host, and modern assets must each record net8/9/10; Framework consumption records the Windows CLR version separately |
| Security/proxy | If TLS/mTLS, ODP, or OB20/trace/compression availability is declared, the corresponding real endpoints must be added, with zero failures and no skips due to missing configuration; these gates currently remain open |
| Mode/direction | Applicable sync/async, text/server-prepare, input/output, ordinary column/PL, NULL/boundary pass separately; fallback/multi-statement cannot substitute for ordinary prepare |
| Feature result | Integration groups involved in the proposed release claim have zero failures, every enabled Acceptance case passes, and cleanup has zero failures; undeclared integration groups may be deferred, must be listed separately, and do not count as code-development failure |

For a release candidate or production capacity assessment, pools and resources add at least one reproducible stress report: 16 concurrent callers per mode, private pool MaxPoolSize=4,
no fewer than 10000 borrow/return cycles or real queries, verifying that peak physical sessions do not exceed 4, waiting cancellations do not leak, and all tasks finish within the configured budget.
Also run for at least 60 continuous minutes, including interleaved pool clearing/return/Dispose, half-read readers, errors/cancellation and that mode's resources;
after stopping and releasing, the driver's owned sessions, prepared IDs, locators/cursors, and background tasks must return to zero (retained bounded caches must be listed separately), and
an independent database connection confirms cleanup of owned transactions/branches/objects. Record handle/thread/memory curves and the actual commands;
any growth must be explained and reproduced, and G10 cannot be closed merely because the last SELECT succeeded. This is a minimum correctness/sustainability baseline, not a production capacity promise.

Development regression keeps at least the existing network unknown, controlled routing, and XA process-recovery cases (including sync/async rollback before the decision is persisted);
when HA, ODP, or node recovery is declared, add a real topology failure special. Passing controlled failures does not automatically close real HA,
but real HA is also not a precondition for current client development.
Performance optimization retains at least 5 baseline/optimized comparisons of the same workload plus percentile latency, throughput, resources, and failure rate;
there is currently no hardware-independent throughput SLO, so this cannot be used to claim performance is acceptable or that Java-equivalent performance has been achieved.

## Execution and verdicts

The following commands run from the repository root; the default gate does not access a real database:

```powershell
dotnet build OceanBaseConnector.slnx -c Release
dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-build
dotnet test tests/OceanBaseConnector.DependencyInjection.Tests -c Release --no-build
dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-build
dotnet run --project tests/OceanBaseConnector.Acceptance -c Release --no-build -- --list
```

Real verification sets private profiles, `OB_REQUIRE_INTEGRATION=1`, and a filter scope per the [Integration instructions](../tests/OceanBaseConnector.IntegrationTests/README.md);
a claim of "complete live database in both modes" requires running all applicable groups for both modes, not merely a connection or a special.
Acceptance selects profiles per the [runner instructions](../tests/OceanBaseConnector.Acceptance/README.md): beyond the ordinary fifteen groups,
networkFaultProfile, topologyFaultProfile, xaCrashProfile, ob20TraceProfile, localInfileProfile, and zeroDateProfile each require their own preconditions and cannot be replaced by an ordinary group's success.

- Zero failures are required within the declared scope, and all self-created tables/branches/logs are cleaned up successfully. Any failure/cleanup failure means not passed.
- **skip/deferred is not a pass.** Acceptance exit 0 allows skips; review every reason. Only cases where the mode truly does not apply may be excluded from that mode's scope.
- Skips due to TLS not enabled/unauthorized/missing real OB20 etc. remain "under-accepted/blocked" and cannot be reworded as "support not required".
- Source/fixture/relay injection/direct connection are each labeled. A single-upstream relay is not HA, an injected 1295 is not the native 1295, and self-signed TLS is not a trusted enterprise deployment.
- Completing a module requires consistent implementation/tests/documentation and no unresolved material review issues; completing the whole SDK additionally requires all modes/deployments/versions/platforms covered by the support claim to be fully closed.
- Performance optimization requires a baseline and result on the same hardware/data/concurrency/protocol, recording throughput/percentile latency/resources/failure rate. Without measurement, no speedup may be claimed.

SecurityTests is independent of the main slnx/default CI; while TLS is not enabled in the real environment it stays deferred and is not forced to succeed.
The current Windows/Linux CI configuration proves only the build/default regression it actually executes; the tag consumer/Windows package gate is not equivalent to a live database on other platforms.
Historically failed items are in [compatibility](compatibility.md); this review did not close them by rewriting acceptance criteria.
