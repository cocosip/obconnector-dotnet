# Driver delivery status

Last checked: 2026-10-27. This is the current overview; the detailed
[completeness baseline](obconnector-dotnet-completeness.md) retains requirements and historical evidence.
The [module records](obconnector-dotnet-progress.md) retain individual test/review results.

## Current checkpoint

- Development stays on `master`. No new branch/worktree and no implicit push.
- Current local delivery: `oceanbase.client.pool.wait.duration` now carries the standard
  `oceanbase.outcome` tag (`acquired`, `timeout`, `canceled` or `error`) for sync and async pool
  waits. The focused regression passes 2/2; the affected diagnostics selection passes 228/230,
  with two pre-existing TLS/PFX fixture failures before the pool path. This is local diagnostics
  evidence only; additional resource waits, workload/overhead and real deployment limits remain open.
- Current local delivery: Oracle complex BLOB/CLOB members decode from binary named results into
  session-bound `OceanBaseLob` handles when `Use Lob Locators=true`; anonymous collection metadata
  and synchronous/asynchronous BLOB/CLOB materialization are covered. The complete complex unit
  family passes 426/426 and no-TLS affected consumers pass 220/220; TLS/PFX consumer
  and full unit failures are environment-bound. Real OceanBase/OBProxy complex-Lob interoperability
  is unverified. See the [module record](obconnector-dotnet-progress.md#2026-10-27-oracle-complex-lob-result-members).
- Current local delivery: Oracle REF CURSOR output framing now validates exact four-byte identifiers
  and metadata/row shape before decoding. The focused REF CURSOR selection passes 143/143; this is
  local protocol evidence only, while Input/InputOutput directions and real OceanBase/OBProxy
  interoperability remain open. See the [module record](obconnector-dotnet-progress.md#2026-10-27-oracle-output-row-shape-validation).
- Latest local delivery, developed from184a767: Oracle FLOAT/NUMBER_FLOAT routine derivation keeps
  binary precision out of Decimal constraints, normalizes INOUT and exposes raw numeric
  metadata. Numeric signatures omit CHAR_LENGTH; character signatures require a matching
  second query for actual character lengths, retaining original errors/parameters on failure.
  Complete units7656, focused189 and affected consumers168 passed, zero failures/skips;
  serial Release build has zero warnings/errors and format passed with a workspace-load
  warning. Final independent source/tests/docs/report review has no remaining actionable
  issue. New real procedure/function cases compile only;
  real DDL/derive/INOUT and old-server compatibility remain unverified. See the
  [module record](obconnector-dotnet-progress.md#2026-10-27-oracle-float-routine-derivation).
- Previous local delivery (`184a767`): Oracle NUMBER_FLOAT206 results preserve exact OceanBaseDecimal,
  binary precision metadata and variable scale. CommandBuilder binds FLOAT through existing
  NUMBER246 without copying binary precision. Complete units7578, focused204 and affected
  consumers229 passed, zero failures/skips. Serial Release build has zero warnings/errors;
  format passed with a workspace-load warning and independent source/tests/docs review has
  no remaining actionable issue. Both new real Oracle cases and both existing NUMBER controls
  failed reading the initial greeting, before authentication or SQL; real FLOAT storage,
  server rounding, version and OBProxy compatibility remain unverified. See the
  [module record](obconnector-dotnet-progress.md#2026-10-27-oracle-float-results).
- Previous local delivery: explicit MySQL JSON parameters (`b94a421`) use the configured strict client encoding
  through the existing type253 string binding. Complete units7543, focused111 and affected
  consumers570 passed, zero failures/skips; independent source/tests/docs review found no actionable issue.
  Release build has zero warnings/errors and format verification passed with a workspace-load warning.
  The ten real JSON attempts and two controls using the default charset failed reading the initial
  greeting; a raw TCP probe received zero greeting bytes.
  No actual JSON conversion/storage success is claimed. See the
  [module record](obconnector-dotnet-progress.md#2026-10-27-json-parameters-with-client-character-sets).
- Previous local delivery: CLOB WriteText (`1da3ea8`) validates every client-encoding chunk before any write,
  preventing a late unrepresentable suffix from leaving a written prefix or invalidating the
  handle. Prevalidation shares the8191-scalar write boundary and uses bounded encoding buffers.
  Complete units7507, focused26, affected units455 and consumers1000 passed, zero failures/skips.
  Final Release build has zero warnings/errors; format verification passed with a workspace-load
  warning and independent source/tests/docs review has no remaining actionable finding. See its
  [module record](obconnector-dotnet-progress.md#2026-10-27-clob-input-charset-validation-before-writes).
- Previous local delivery: client SQL/parameter character sets (`b66cda1`), based on `33c052b`.
  The [input-encoding contract](client-character-sets.md) describes twelve non-default names,
  strict round-trip validation, managed initialization/reset and cached session trust. Text/prepare,
  ordinary scalar/complex/array inputs, procedures, metadata parameters and byte budgets use the
  selected encoding. Complete units7,491, focused units166 and affected consumers560 passed,
  zero failures/skips. Serial Release build has zero warnings/errors; format verification passed
  with a workspace-load warning and independent source/tests/docs review has no remaining finding.
  Original failed verification reports and their corrections remain in the
  [module ledger](obconnector-dotnet-progress.md#2026-10-27-client-sql-and-parameter-character-sets).
- Previous local delivery: current borrowed physical-session metrics (`33c052b`), based on `f0edabf`.
  The [pool contract](pool-metrics.md) distinguishes `leased` deltas from cumulative `borrowed`.
  Complete units passed7,391, focused units16 and affected consumers140, zero failures/skips;
  final serial Release build has zero warnings/errors and independent source/tests/docs review has
  no remaining actionable finding. An earlier parallel build hit a testhost DLL lock; its failure
  log is retained, and serial retry passed without a source workaround.
- Previous completed module: session protocol I/O durations (`f0edabf`), with7,375 complete units,
  89 focused units and279 affected consumers, all passing. See its [contract](session-io-diagnostics.md).
- Further work: remaining national/JSON/CLOB and extended type boundaries, additional resource waits,
  Oracle cursor/complex shapes and the other priorities below. JSON parameters use strict client
  bytes; MySQL string literal fallback retains its UTF-8-only restrictions. Managed charset
  rechecks can change session diagnostic functions after conservatively classified SQL.
- The historical TNS File.Replace failure remains cause-unconfirmed. Eight isolated runs and both
  fresh complete runs passed; no TNS source change, retry workaround or skipped assertion was made.
- At `e0498e9`, there were 67 commits since audit baseline `06d58e1`, including implementation, fixes,
  tests and documentation. This is a commit count, not a count of completed capabilities.
- HKSCS/HKSCS31 passes 14 core tests, 7,353 complete units and 540 affected consumers, with zero
  failures/skips. Release solution build has zero warnings/errors; independent source/tests/docs
  review has no remaining blockers. It completes this local module, not all of P1-08/W5.
- Other offline development remains. A database connection is not required for the next local tasks.
- None of the 23 original gap IDs or eight work packages has met its complete closure criteria.
  This does not mean their implemented subfeatures are missing. No honest overall completion
  percentage is available because the IDs combine implementation, scope decisions and deployment gates.

## Historical end-of-day checkpoint: session I/O diagnostics

This retains the earlier failed/pending run. The fresh completed delivery is recorded above and in
the [module ledger](obconnector-dotnet-progress.md#2026-10-26-session-io-diagnostics-completed-after-checkpoint).

- P2-04/W7 implementation adds `oceanbase.client.protocol.io.duration` in seconds, with direction
  (`read`/`write`), mode (`mysql`/`oracle`) and final exchange outcome
  (`success`/`error`/`canceled`/`timeout`). Packet/payload calls are accumulated per exchange and
  published after ownership release; pending timed I/O delays publication. Disabled async wrappers
  return the original tasks. No new dependency or public setting is introduced.
- This includes framing/copy/codec work and existing synchronous protocol observers inside those
  calls. Application consumption gaps, LOCAL INFILE source-stream reads and the new histogram
  callbacks are excluded. Enabled async wrappers may allocate state machines; this is not a
  socket-only timer or a measured overhead guarantee.
- Observed verification: initial RED 8 failures, focused GREEN 20 passed, affected consumers 215
  passed, all with zero skips; Release solution build has zero warnings/errors. Consumers load five
  assets in one .NET 10 host. Independent source/tests review found no blocking findings; timing and
  allocation wording was corrected. Logical pending-I/O interleaving is covered, not parallel stress.
- The full unit run is not green: it reported
  `TnsAddressResolverTests.ConcurrentReadersUseCompleteSnapshotsAndMissingFilesHaveNoFallback`
  failing at the publisher's `File.Replace` with `IOException` (cannot delete the replaced file).
  Its isolated recheck passed 1/1 on the same built artifact. Root cause is unresolved; the isolated
  pass does not erase the full-run failure. No TNS source/test changes or retry workaround were made.
- At this checkpoint the full run has not returned a terminal result, so no final aggregate is
  claimed. Logs/TRX are under ignored `TestResults/local-completeness/session-io-*`.
- Resume by collecting the terminal full-run result, investigating the exact file-replacement
  failure, completing diagnostics API/architecture documentation, rerunning affected gates as needed
  and reviewing before calling this module delivered. No real database was accessed for this module.

## Remaining implementation priorities

These are concrete code/scope gaps, not merely missing real-database reports:

1. Remaining national/JSON/CLOB and extended metadata boundaries (P1-08), following the locally
   completed client SQL/parameter charset module.
2. Remaining resource-wait instrumentation and workload/overhead validation (P2-04).
3. Input/InputOutput REF CURSOR and additional output shapes (P1-06).
4. Complex LOB/cursor members, additional national/anonymous shapes and whole-complex RETURNING
   identity (P1-07).
5. Compressed OB20/proxy reroute, after establishing the client-link wire contract (P1-02).
6. Multi-send/pipelining or a separate bulk protocol, after establishing supported semantics (P2-03).
7. Replace the six deferred acceptance placeholders with controlled fixtures/assertions (P2-06):
   TLS/authentication, OB20/trace negotiation, topology failover, unknown network outcome, pool reset
   and XA crash recovery. These need their corresponding environments or fault controls.

Pool/session/transaction concurrency and resource boundaries, complete command/adapter restrictions,
runtime/platform coverage and deployment gates also remain, as mapped individually below. Distributed
ambient promotion and transparent active-session replay are unsupported and need explicit scope
decisions; they are not promised by existing single-resource or reconnect APIs.

## All 23 gaps: implementation versus remaining work

“Local” means source plus local regression evidence. A real report covers only its recorded profile.
“Remaining development” includes unresolved scope; it does not imply that every extension has an
approved wire contract or can be implemented without protocol research.

| ID | Implemented and committed | Remaining development or scope | Required external validation |
| --- | --- | --- | --- |
| P0-01 Pool lifecycle | Capacity/wait/cancel, prewarm, ping, idle/lifetime retirement, metrics, explicit clearing, normalized identity; callback cleanup and waiter handoff fixes | Additional concurrency/reentrancy/Dispose interleavings and bounded resource/long-run tests; shutdown scope | Real reset/proxy behavior and sustained resource limits |
| P0-02 Session isolation | Reset generation, conservative stateful-SQL retirement, Oracle fresh/reset autocommit, XA isolation, old-reader lease isolation, LOB/cursor retirement | Complete resource/interleaving and implicit-side-effect coverage; real interrupted external LOB isolation remains unproved | Some direct dual-mode/Oracle cases exist; complete reset/LOB/cursor/XA/proxy matrix remains |
| P0-03 Ambient transactions | Single-resource PSPE enlistment, ownership, terminal states, cancellation/late-callback cleanup; promotion rejected | Complete failure regressions; distributed promotion/durable prepare is unsupported and requires a separate scope decision | Transaction manager, timeout/in-doubt, close and proxy failures |
| P0-04 Unknown outcome | Classification, retirement/fresh leases, lost commit/rollback acknowledgement tests and application reconciliation example | Complete send/response/fault-boundary coverage; never replay unknown writes | Direct lost-ack cases exist; full server/proxy/coordinator fault matrix remains |
| P0-05 Active connection failure | Connection-level ERR marks Broken, invalidates old transaction and supports explicit Close/Open | Transparent reconnect/migration/replay is absent and needs an explicit contract; complete reconstruction tests | Actual disconnect/node/proxy failure and recovery |
| P0-06 Pool routing identity | Resolver reference identity, one route snapshot per Open, stale idle endpoint retirement | Boundary/stress coverage; no active-session migration or DNS-IP identity guarantee | Real discovery, topology and proxy changes |
| P1-01 Multiple statements | Explicit opt-in text multi-results and rejection of unsupported combinations | Parameterized/prepared/cursor/Oracle-output combinations remain unsupported; scope decision required to expand | Both modes/versions/proxy, intermediate errors and resource cleanup |
| P1-02 Compression/OB20 | Bounded standard MySQL compression; ordinary OB20 already exists | **Compressed OB20 and proxy reroute are missing**; confirm client link/version/capability contract before implementation | Real compression/OB20/proxy negotiation, corruption/cancellation and reroute |
| P1-03 LOCAL INFILE | Explicitly authorized application-stream upload, path/resource boundaries and local regressions | Additional large-input/cancellation coverage within current contract | Actual server/proxy upload policies, cancellation and large files |
| P1-04 TLS/mTLS | Server validation callback, client certificate identity isolation, data-source snapshots, revocation options | Additional rotation/platform regression where needed | Enterprise CA/mTLS, negative handshakes, expiry/rotation and platform revocation |
| P1-05 Command/Batch behavior | SchemaOnly Prepare-only, bounded KeyInfo/hidden keys, SingleRow/SingleResult; MySQL procedure parameters/output/multi-results; Batch metadata/data-source creation/result limits; REF CURSOR limits | Complete behavior/shape matrix; existing explicit restrictions remain | Real metadata/permissions, prepare side effects/locks, procedure and cursor behavior |
| P1-06 Oracle output/cursors | Independent Prepare, output ownership, positive ordinary-output FetchSize hints, binary and text cursor-valued columns (four-byte IDs) and nested ownership | **Input/InputOutput REF CURSOR and additional shapes**; output offset and positive array FetchSize remain rejected | Real direction/shape/pagination and resource matrix |
| P1-07 Complex types | Nested objects/collections, directional national members, explicit descriptor clear and invalidation after observed DDL, binary result BLOB/CLOB members with session-bound locators | **Cursor members, further national storage/anonymous shapes, whole-complex RETURNING identity, non-empty complex LOB input** | Real DDL invalidation, complex LOB resource behavior/interoperability and server-supported shapes; retained server failures remain visible |
| P1-08 Types/charsets | Signed DecimalScale, faithful unknown-precision Oracle NUMBER and exact FLOAT results/binding, strict result decoding/widths; tiny/medium/long BLOB, ENUM/SET/raw GEOMETRY, ROWID, Oracle Inf/NaN; national Unicode fallback IDs; trusted MySQL string prepare fallback; GB18030-2022, DEC8/TIS620, SJIS, Big5 and HKSCS/HKSCS31; configured client SQL/ordinary and JSON parameter encodings; CLOB input prevalidation | Further national/JSON/CLOB scope and extended metadata/types still need work | Real emission/storage/coercibility, all promised collation/mode/version profiles |
| P1-09 Discovery/HA | TCP retry rounds/backoff/shared budget, endpoint TCP reachability cache, TNS/OCP adapters, groups/weights/selection, global/group/address blacklist controls and attempt ceilings | Further discovery/policy boundaries; TCP reachability does not prove database/tenant health | Real discovery services, node/proxy failure and route changes |
| P1-10 Schema | Mode-aware catalog collections/DataTypes and atomic routine-parameter derivation | Boundary regressions as actual dictionary differences are found; local core APIs exist | Real catalogs, permissions, case, overloads, versions and proxies |
| P1-11 Credentials | Per-Open provider acquisition, expiry/cancel, versioned rotation and old-session retirement, redacted diagnostics | Boundary regressions as real provider/plugin differences are found; local core exists | Real credential service, authentication plugins/KDC, rejection/expiry/rotation |
| P2-01 DataAdapter | Fill/mappings/events, explicit CRUD and bounded automatic CommandBuilder SQL with original-value/NULL/concurrency handling | Query/key/generated-value restrictions remain; EF Core provider is separate scope | Real generated SQL/catalog/locks/concurrency and independent runtimes |
| P2-02 Statement cache | Opt-in idle LRU, capacity/close, context/schema/reset generation invalidation | Further reprepare/resource/load regressions | Real DDL/reprepare, prepare-count/latency/resource/proxy reports |
| P2-03 Batch optimization | Sequential Batch, existing Oracle array binding, opt-in bounded MySQL INSERT rewrite with group error attribution | **Multi-send/pipelining and separate bulk protocol are missing** | Actual counts/partial failure, transaction/default/trigger/warning behavior, packet limits and throughput/memory |
| P2-04 Diagnostics | ILogger/Activity/Meter, pool metrics including current leases, connection/ping phase timing, slow-query/cursor thresholds, protocol frame events, session I/O durations and collectible-load fixes | **Additional resource-wait instrumentation**, workload/overhead validation | Real load, resource curves and listener overhead |
| P2-05 Runtimes/ecosystem | Five product assets; Windows nupkg protocol subset on actual .NET 8/9/10 and Framework host, plus Standard assets on .NET 10 | Complete promised platform/runtime/API matrix; Framework/ORM support scope; existing savepoint identifier limitations | Non-Windows and full feature/real-database reports on promised runtimes |
| P2-06 Real compatibility | Acceptance tooling and historical direct-server reports | **Six `matrix.*` cases are still deferred placeholders**; replace with controlled fixtures/assertions; source/license/package/publication review remains | OBProxy, TLS/auth profiles, topology faults, pool reset and external-coordinator XA crash recovery |

## Committed delivery evidence

These are representative commits, not a replacement for the complete Git log or per-module evidence.

| Area | Commit evidence |
| --- | --- |
| Pool routing, clearing and normalized identity | `71d440d`, `0e58d1f`, `abd0077` |
| Session SQL classification, Oracle autocommit, reader leases, cleanup/handoff | `6d399fd`, `ffadc7a`, `6c45173`, `917e9cc`, `d7a5354`, `a3532fc` |
| XA/LOB/cursor isolation and lost acknowledgements | `00b3976`, `a426623`, `4c15e3c`, `a07957e` |
| Ambient resource ownership and broken connection ERR | `96f544d`, `0ec3e1c` |
| Standard compression and TLS callbacks/certificate identity | `8d8b799`, `3f523d8`, `01a8082` |
| Discovery/retry/health/TNS/OCP/groups/blacklists | `bc84ba1`, `9dc3f06`, `5cbe5c9`, `1416c55`, `888bd1c`, `75d3751`, `f71a979`, `414b7cc`, `aab2755` |
| Schema, DataTypes and parameter derivation | `6fecad9`, `5146b62`, `557a05a` |
| Credentials, adapter, KeyInfo and metadata-only prepare | `f815c16`, `4dd20a7`, `d7e8b5a`, `de15ec2` |
| MySQL procedures and Oracle prepare/output/cursor behavior | `c2fe307`, `d6a9169`, `a1dddde`, `e0d1d30`, `fa5d47f` |
| Complex DDL invalidation and national members | `1bb13cb`, `80ddfbe` |
| Batch metadata/result limits/rewrite and statement cache | `27bd176`, `7976fb3`, `6fc1891`, `f15716e` |
| Core type/charset corrections and result families | `2bbecac`, `6b357f0`, `4810907`, `6e2197c`, `94fcd85`, `7d05ce4`, `f3c484d`, `7c1a602` |
| MySQL string fallback and further result charsets | `0484943`, `d842fee`, `37a0aba`, `4821d54`, `4ce3a35` |
| HKSCS/HKSCS31 ordinary result decoding | `e0498e9` |
| Diagnostics and packaged runtime protocol verification | `b2fce9d`, `5ea71b2`, `ffceec8`, `d7a5354`, `e3a07a2` |

## Verification boundary and next work

- Previous completed feature, Big5: 7 core tests, 698 focused units, 7,262 complete units and 500 affected
  consumers passed with no failures/skips; Release solution build and independent review passed.
  These are that commit's results, not a fresh verification of every subsequent candidate.
- Last completed HKSCS/HKSCS31 delivery: full units 7,353 in 8m15s, affected consumers 540 in 2m49s,
  solution build 8.10s; independent review and final whitespace checks pass. The consumers load five
  assets in one .NET 10 host; they do not establish five independently tested runtimes or real storage.
- Session I/O checkpoint completion passed7,375 full units and279 affected consumers; these are
  local results, with the historical TNS replacement cause still unresolved. Continue offline
  work in reviewable feature commits. Client charset work must reach SQL/parameters/escaping and
  trusted session rules together; a result decoder alone does not implement it.
- OB20 compression/reroute requires the documented client-link protocol evidence; real proxy testing
  cannot be replaced with a direct database connection.
- Real database testing is a separate gate. If the next task needs it, stop and report the exact case.
  Endpoints and credentials are deliberately excluded from this document.
- Historical real-server results and failures are retained in the completeness/progress records.
  In particular, the Connector/J-correlated nested-table-column server limitation is not proof that
  all complex/array shapes fail or pass. Skips, cleanup failures and missing profiles stay visible.

For each subsequent feature, update this checkpoint with its commit, tests/review, remaining code and
external gates; retain the detailed module record instead of appending another unqualified “all open”.
