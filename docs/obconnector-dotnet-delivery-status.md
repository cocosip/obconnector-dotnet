# Driver delivery status

Last checked: 2026-10-27 against committed `master/85e6fe6` (protocol negotiation diagnostics).
This check updates documentation only; it does not implement the next acceptance module. This is the current overview; the detailed
[completeness baseline](obconnector-dotnet-completeness.md) retains requirements and historical evidence.
The [module records](obconnector-dotnet-progress.md) retain individual test/review results.

## Current checkpoint

- Source alignment at `85e6fe6`: negotiation diagnostics are committed. OB20/trace acceptance
  still has no dedicated configuration profile or executable matrix case; it remains one of
  two DeferredReason placeholders, alongside TLS/authentication. Research for that next module
  is not an uncommitted implementation or a new validation result. Oracle input/complex-shape
  and compressed-OB20/reroute rejection paths remain in source. See
  [completeness section 15.107](obconnector-dotnet-completeness.md#15107-2026-10-27-已提交进度与尚未实施工作复核).
  Saved latest reports and product hashes are rechecked; no build/test/database run or new
  independent final review is performed for this documentation-only alignment.

- Current implementation: the protocol DiagnosticListener now reports requested/actual framing,
  requested/enabled full-link trace and active modern trace format after physical authentication,
  identity and charset initialization. Pool reuse/reset does not renegotiate; failed setup emits
  no Negotiated event. This adds observable negotiation evidence for P2-04/P2-06, not server
  sampling/export or the complete OB20/trace acceptance matrix. Focused458/458, affected
  consumers340/340, complete units8091/8091 and a zero-warning/error serial Release build pass;
  one independent final review has no finding. Final rebuilt focused/runner488/488,
  opt-out15 pass/116 skip and nine-file format pass; five product hashes match the tested
  assets. Full/final gates are
  recorded in the [module ledger](obconnector-dotnet-progress.md#2026-10-27-authenticated-protocol-negotiation-diagnostics).
  See the [public contract](protocol-diagnostics.md).

- Prior source review at37cf788: input/InputOutput REF CURSOR, named complex LOB locator rebinding and
  text complex results remain explicitly unsupported. Binary complex results and named LOB
  content/anonymous scalar locator inputs already exist. Fixed Java text Array/Struct readers
  return null, while the inspected server has distinct text serialization branches; this is not
  a confirmed binary-decoder port. Shape-specific wire contracts and resource ownership must be
  established before extending these paths. See [completeness section 15.105](obconnector-dotnet-completeness.md#15105-2026-10-27-剩余-oracle-形状与当前检查点对齐).
  Saved final topology evidence is rechecked; builds/tests/live checks are not rerun for this
  documentation-only change. All23 gap IDs/eight packages remain open.

- Previous module: [controlled topology acceptance](topology-acceptance.md), six read-only explicit
  client-route cases: idle refresh/retirement, borrowed-session isolation and refused-TCP fallback,
  sync/async. Focused94/94 and real direct4.4.2.2 Oracle7/7/MySQL7/7 pass with no database objects;
  opt-out15 pass/116 skip and Release build zero warnings/errors. Complete units8066/8066 and scoped format/docs/privacy checks pass; one independent review
  has no Critical/Important finding and its sole Minor aggregation issue is corrected with RED/GREEN. No product changes
  or claim of real cluster/backend/discovery-service HA. Two deferred placeholders remain.

- Previous module: [XA coordinator process recovery](xa-process-recovery-acceptance.md), four
  explicit-profile sync/async commit/rollback cases. A prepared coordinator is killed only
  after its decision flushes; a fresh process loads that decision, recovers only its branch
  and completes it. Both direct4.4.2.2 modes pass5/5, with all8 owned tables/branches/journals
  removed. Focused56/56 and opt-out15 pass/116 skip; Release builds with zero warnings/errors.
  Complete units8044/8044, scoped format/docs/privacy checks pass; the sole Minor documentation
  count from one independent final review is corrected, with no Critical/Important finding. At that checkpoint three placeholders remained;
  the general coordinator/server and other crash-point/deployment matrices remain open.


- Previous module: [controlled network outcome acceptance](network-outcome-acceptance.md),16
  individually reported sync/async cases behind a separate explicit profile. Both modes pass
  17/17 on direct4.4.2.2 (one connection control plus16 faults), with all32 owned tables removed.
  Focused tests70/70, complete units8018/8018 and opt-out integration15 pass/116 skip.
  Final serial Release build has zero warnings/errors; scoped format verification exits0 and
  one independent final review has no actionable findings.
  At that checkpoint four deferred placeholders remained. Local pre-send, server execution interruption, cancel/timeout,
  coordinator crash and real proxy/TLS/deployment matrices remain open.


- Previous module: [upload source read durations](source-read-diagnostics.md), covering
  MySQL long-data parameters, Oracle piece-data parameters and LOCAL INFILE, sync/async.
  Fixed source-kind/mode/outcome tags, independent enablement and shared post-release publication
  preserve ownership/cancellation and protocol timing. Focused units156/156, complete units7977/7977,
  affected consumers315/315 and the serial Release build zero warnings/errors pass. Independent
  final review has no actionable finding. Opt-out integration has13 pass/116 skip/zero failures.
  No real database or independent runtime/load compatibility is claimed. See the
  [module record](obconnector-dotnet-progress.md#2026-10-27-upload-source-read-diagnostics).

- Source/documentation alignment at `f9e8c20`: JSON result UTF-8 decoding is the current
  protocol contract, consistent with the inspected fixed server serializer; it is not evidence
  of an unimplemented client-charset decoder. Non-default JSON input/storage, unexpected result
  metadata and other server/proxy profiles still require validation. This documentation-only
  check rereads the saved final CLOB build/TRX reports; it does not rerun tests or access a database.
  Concrete unsupported paths and validation-only work are separated in
  [completeness section 15.100](obconnector-dotnet-completeness.md#15100-2026-10-27-源码状态与待开发边界复核).

- Previous module, developed from `master/40fa822`: scalar/anonymous-array CLOB locator parameters
  transcode inline UTF-8 content in a sending copy using the strict client charset. V1 converts;
  V2 converts at authenticated product4.4.1+, preserving legacy/unknown V2 behavior. Original
  handles/readers, opaque identity/extensions/rowkeys, BLOBs and no-inline external locators stay
  unchanged. Named CLOB content remains strict UTF-8. The prior GB18030 mismatch was input
  corruption during rebinding, rather than a read-decoder failure.
  Default and GB18030 direct Oracle checks each pass16/16 plus connection control, including
  the original12-case matrix and4 small/large CLOB cases with confirmed external stored locators.
  Complete units7938/7938, affected consumers1240/1240 and the serial Release build pass with
  zero warnings/errors. Independent review findings were corrected and verified by12 new lifecycle
  regressions plus existing uncertain-mutation controls; no finding remains deferred. Evidence and retained failures are in the
  [module record](obconnector-dotnet-progress.md#2026-10-27-clob-locator-client-encoding).

- Previous module, developed from `master/285459e`: named object/collection BLOB byte payloads and
  strict UTF-8 CLOB text; anonymous scalar BLOB/CLOB arrays use matching session-bound locators.
  Creation/execution revalidate connection, transaction and epoch without taking caller ownership.
  Named locator rebinding rejects before target SQL. Default-charset Oracle checks pass12/12;
  GB18030 passes10/12 after correcting a named-content charset bug with a RAW-output control and
  an8-case RED; that candidate's anonymous CLOB array readbacks still mismatch. The later module
  above isolates and fixes their input conversion.
  Complete units7913/7913, affected consumers1220/1220 and Release build pass; independent review
  has no Critical/Important finding. Final gates, live results and remaining charset limits are in the
  [module record](obconnector-dotnet-progress.md#2026-10-27-complex-lob-inputs).

- Previous committed implementation, `c09cf0a`, developed from `master/ed2fc84`: configured client encodings for complex
  NCHAR/NVARCHAR2 inputs. Oracle fresh Open/reset now share individual quoted SET statements;
  verification/revalidation uses FROM DUAL. Complete units7846/7846, focused units267/267,
  affected consumers350/350 and the final Release build pass. Independent source/test/docs review
  has no actionable issue. Explicit Oracle array-input cases pass36/48;
  UTF8MB3 is reported as UTF8MB4 and correctly rejected, while SJIS/Big5 switching causes later
  fixed ASCII statements to fail900. All12 failures stop at Open before DDL. Original all48
  initialization failures and the interrupted full run remain in the
  [module record](obconnector-dotnet-progress.md#2026-10-27-complex-national-client-encoding).
- Previous implementation, developed from `f03f235`: configured GBK, GB18030/2022, SJIS, Big5 and
  HKSCS/HKSCS31 now support the same opt-in MySQL string fallback, with strict matching session
  encodings and unchanged character-level escaping. DBCS trail5C bytes are preserved inside
  characters. Complete units pass7774/7774, focused units290/290 and affected consumers160/160;
  independent source/test/docs review has no remaining actionable issue. Exact results and
  retained failures are in the [module record](obconnector-dotnet-progress.md#2026-10-27-dbcs-mysql-string-fallback).
  Release solution build has zero warnings/errors. Explicit direct MySQL generated-literal
  checks pass14/14 plus a connection control, with ASCII aliases and existing SQL_MODE.
  Actual server1295 automatic fallback and ambiguous expression metadata remain unverified.
- Previous implementation, developed from `da62285`: opt-in MySQL string fallback accepts configured
  ASCII, latin1, DEC8 and TIS620 with matching fresh session state and strict encoding. The existing
  escaping, statement/packet guards and exact 1295/HY000 gate remain; that commit still refused DBCS.
  Focused units pass 234/234 and affected consumers pass 90/90 across five assets in one .NET10
  host. Complete units pass 7742/7742; Release solution build has zero warnings/errors and independent
  source/test/integration/docs review has no actionable issue. Exact evidence is in the
  [module record](obconnector-dotnet-progress.md#2026-10-27-single-byte-mysql-string-fallback).
  Read-only direct MySQL generated-literal checks pass 8/8 plus a connection control, using ASCII
  aliases and existing SQL_MODE. Unaliased non-UTF8 expression metadata failed strict UTF-8
  decoding and remains a known limit. This is not live automatic-fallback/DBCS/proxy proof.
- Previous acceptance module, developed from `7d777ae`: `types.zero-date-policy` is executable
  behind `zeroDateProfile=true`, MySql mode and `allowCreateObjects=true`. It validates actual
  session SQL_MODE without changing it, checks normal/partial-zero/all-zero/NULL DATE and
  DATETIME(6) through text/prepared reads and provider-value write-back. Focused units pass
  103/103, complete units pass 7708/7708, and the serial Release solution build has zero
  warnings/errors. Independent source/test/docs review has no actionable issue. The explicit real
  MySQL connection passes, but the SQL_MODE prerequisite fails before DDL: no objects were
  created, and real zero-date storage remains unverified. At that checkpoint five DeferredReason cases remained;
  ordinary mode/configuration skips are separate. See the
  [module record](obconnector-dotnet-progress.md#2026-10-27-opt-in-zero-date-storage-acceptance).
- Development stays on `master`. No new branch/worktree and no implicit push.
- The prior documentation-only alignment checked committed source and saved module reports; it did
  not rerun builds/tests or connect to a database. Current code limits and direct source links are
  recorded in [completeness section 15.97](obconnector-dotnet-completeness.md#1597-2026-10-27-已提交源码与文档再次对齐).
- Historical source review baseline: `master/6e64c7e`, 2026-10-27. The serial Release solution build
  passes with zero warnings/errors; 554 selected complex/cursor/session-isolation/compression
  unit cases pass with zero failures/skips. Acceptance `--list` confirms five deferred
  `matrix.*` cases plus the separate deferred `types.zero-date-policy` case. This is a
  focused local check, not a complete-suite, package-consumer or real-deployment run.
  Reproducible commands and remaining limits are in the
  [source-review record](obconnector-dotnet-progress.md#2026-10-27-source-review-and-progress-alignment).
- Current local delivery: binary complex object/collection cursor members publish session-bound
  `OceanBaseRefCursor` handles, including named/anonymous layouts and buffered/sequential reads.
  Text and binary top-level cursor columns also exist. Complex cursor inputs and named locator rebinding,
  Input/InputOutput REF CURSOR, text complex results and unresolved whole-complex RETURNING
  remain unsupported; real interoperability is unverified.
- Earlier same-channel pool acceptance delivery, developed from `fbcb694`: `matrix.pool-reset-reuse.sync/async`
  require the same client protocol channel through two borrowers and prepared-command reopen.
  Listener reference plus local ChannelId rejects reconnection; private pools and subscriptions
  are disposed. The separate `matrix.pool-reset` still checks retirement isolation and now uses
  numeric offsets after Oracle rejected the UTC region name (1882). Focused units pass 40/40;
  complete units pass 7696/7696 and the Release solution build has zero warnings/errors.
  Explicit direct-server checks pass 4/4 per
  mode on 4.4.2.2, Windows/.NET 10.0.13, TLS disabled. Complete unit evidence and retained failures
  are in the [module record](obconnector-dotnet-progress.md#2026-10-27-same-channel-pool-reset-and-reuse-acceptance).
  Channel identity is local, not a server/backend session ID; the proxy/resource matrix remains open.
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
- Previous local delivery, developed from184a767: Oracle FLOAT/NUMBER_FLOAT routine derivation keeps
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
  bytes; MySQL string literal fallback now also supports every configured non-UTF8 input family,
  while unaliased non-UTF8 expression metadata remains limited. Managed charset
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

1. Remaining national/CLOB and extended metadata boundaries (P1-08): CLOB
   reads retain the current UTF-8 contract, stored complex national members require AL16UTF16, and
   unsupported type codes/extended metadata reject. Configured DBCS fallback is now locally implemented.
   Complex-national inputs now resolve all configured names. UTF8MB3/SJIS/Big5 currently fail
   managed Oracle Open in the explicit server profile; further national storage/output remains open.
   Non-UTF8 expression results need ASCII aliases under the current metadata contract.
   JSON results use strict UTF-8 under the inspected server serialization contract; do not add
   client-charset decoding without evidence of another wire shape. JSON storage/conversion and
   result metadata across actual server/proxy profiles remain validation work.
2. Additional resource-wait scope and workload/overhead validation (P2-04). Upload source read
   diagnostics now have local implementation/regression evidence; their scope and gates are above.
3. Input/InputOutput REF CURSOR and additional output shapes (P1-06). Current input directions
   reject in OracleCommandPlan. The four-byte output cursor ID is not an established input
   contract; input binding also needs originating session/statement/transaction ownership rules.
4. Named LOB locator rebinding, complex cursor input, additional national/anonymous output shapes,
   text complex results and unresolved whole-complex RETURNING identity (P1-07). Named LOB content
   and anonymous scalar locator inputs are implemented. CLOB reads remain UTF-8; version-gated
   locator input conversion now passes direct default/GB18030 checks. Other profiles remain open.
   Named LOB contents and anonymous locator bytes are different contracts; supporting named
   locators is not a switch-only change. Text complex results require shape-specific serialization
   evidence; the fixed Java text Array/Struct getters return null. See section15.105 above.
5. Compressed OB20/proxy reroute, after establishing the client-link wire contract (P1-02).
6. Multi-send/pipelining or a separate bulk protocol, after establishing supported semantics (P2-03).
7. Replace the two remaining deferred `matrix.*` placeholders with controlled fixtures/assertions
   (P2-06): TLS/authentication and OB20/trace negotiation.
   Controlled topology now has six read-only route/failure assertions; actual discovery/node/proxy
   failover and the full topology matrix still require external evidence.
   XA process recovery now has a prepared/recorded-decision crash point; its other crash points
   and general coordinator/server recovery still need implementation/validation.
   Controlled network outcome cases now have explicit assertions and direct-server evidence;
   their complete send/execution/cancellation/deployment boundaries remain open. `types.zero-date-policy` now has executable assertions and a separate
   opt-in SQL_MODE profile; actual zero-date storage on a permissive server remains unverified.
   There are two explicit DeferredReason cases in total.

Pool/session/transaction concurrency and resource boundaries, complete command/adapter restrictions,
runtime/platform coverage and deployment gates also remain, as mapped individually below. Distributed
ambient promotion and transparent active-session replay are unsupported and need explicit scope
decisions; they are not promised by existing single-resource or reconnect APIs.
The new direct-server client-channel reset/reuse and separate retirement evidence still needs
promised proxy/backend, version/platform/TLS and complete resource-isolation profiles.

## All 23 gaps: implementation versus remaining work

“Local” means source plus local regression evidence. A real report covers only its recorded profile.
“Remaining development” includes unresolved scope; it does not imply that every extension has an
approved wire contract or can be implemented without protocol research.

| ID | Implemented and committed | Remaining development or scope | Required external validation |
| --- | --- | --- | --- |
| P0-01 Pool lifecycle | Capacity/wait/cancel, prewarm, ping, idle/lifetime retirement, metrics, explicit clearing, normalized identity; callback cleanup and waiter handoff fixes | Additional concurrency/reentrancy/Dispose interleavings and bounded resource/long-run tests; shutdown scope | Real reset/proxy behavior and sustained resource limits |
| P0-02 Session isolation | Reset generation, conservative stateful-SQL retirement, Oracle fresh/reset autocommit, XA isolation, old-reader lease isolation, LOB/cursor retirement | Complete resource/interleaving and implicit-side-effect coverage; real interrupted external LOB isolation remains unproved | Some direct dual-mode/Oracle cases exist; complete reset/LOB/cursor/XA/proxy matrix remains |
| P0-03 Ambient transactions | Single-resource PSPE enlistment, ownership, terminal states, cancellation/late-callback cleanup; promotion rejected | Complete failure regressions; distributed promotion/durable prepare is unsupported and requires a separate scope decision | Transaction manager, timeout/in-doubt, close and proxy failures |
| P0-04 Unknown outcome | Classification, retirement/fresh leases,16 opt-in request/response fault cases with dual-mode direct-server evidence and application reconciliation example | Complete send/response/fault-boundary coverage; never replay unknown writes | Direct lost-ack cases exist; full server/proxy/coordinator fault matrix remains |
| P0-05 Active connection failure | Connection-level ERR marks Broken, invalidates old transaction and supports explicit Close/Open | Transparent reconnect/migration/replay is absent and needs an explicit contract; complete reconstruction tests | Actual disconnect/node/proxy failure and recovery |
| P0-06 Pool routing identity | Resolver reference identity, one route snapshot per Open, stale idle endpoint retirement | Boundary/stress coverage; no active-session migration or DNS-IP identity guarantee | Controlled routes to one direct4.4.2.2 upstream pass7/7 per mode; independent-node discovery/topology and proxy changes remain |
| P1-01 Multiple statements | Explicit opt-in text multi-results and rejection of unsupported combinations | Parameterized/prepared/cursor/Oracle-output combinations remain unsupported; scope decision required to expand | Both modes/versions/proxy, intermediate errors and resource cleanup |
| P1-02 Compression/OB20 | Bounded standard MySQL compression; ordinary OB20 already exists | **Compressed OB20 and proxy reroute are missing**; confirm client link/version/capability contract before implementation | Real compression/OB20/proxy negotiation, corruption/cancellation and reroute |
| P1-03 LOCAL INFILE | Explicitly authorized application-stream upload, path/resource boundaries and local regressions | Additional large-input/cancellation coverage within current contract | Actual server/proxy upload policies, cancellation and large files |
| P1-04 TLS/mTLS | Server validation callback, client certificate identity isolation, data-source snapshots, revocation options | Additional rotation/platform regression where needed | Enterprise CA/mTLS, negative handshakes, expiry/rotation and platform revocation |
| P1-05 Command/Batch behavior | SchemaOnly Prepare-only, bounded KeyInfo/hidden keys, SingleRow/SingleResult; MySQL procedure parameters/output/multi-results; Batch metadata/data-source creation/result limits; REF CURSOR limits | Complete behavior/shape matrix; existing explicit restrictions remain | Real metadata/permissions, prepare side effects/locks, procedure and cursor behavior |
| P1-06 Oracle output/cursors | Independent Prepare, output ownership, positive ordinary-output FetchSize hints, binary and text cursor-valued columns (four-byte IDs) and nested ownership | **Input/InputOutput REF CURSOR and additional shapes**; output offset and positive array FetchSize remain rejected | Real direction/shape/pagination and resource matrix |
| P1-07 Complex types | Nested objects/collections, directional national members with all configured client input encodings, explicit descriptor clear and invalidation after observed DDL; binary named/anonymous BLOB/CLOB and cursor result members; named BLOB bytes/UTF-8 CLOB content and anonymous scalar locator inputs with session-bound ownership | **Named LOB locator rebinding, complex cursor input, text complex results, further national storage/anonymous output shapes and unresolved whole-complex RETURNING identity** | Default/GB18030 complex LOB and small/large locator checks each pass16/16; version-gated inline locator input conversion is implemented. Other charset/version/proxy profiles remain open. Nine configured encodings have direct national array-input evidence; three Open failures remain. Real DDL invalidation, complex LOB/cursor resource behavior/interoperability and further server-supported shapes remain |
| P1-08 Types/charsets | Signed DecimalScale, faithful unknown-precision Oracle NUMBER and exact FLOAT results/binding, strict result decoding/widths; tiny/medium/long BLOB, ENUM/SET/raw GEOMETRY, ROWID, Oracle Inf/NaN; national Unicode fallback IDs; trusted MySQL string prepare fallback for configured input charsets; GB18030-2022, DEC8/TIS620, SJIS, Big5 and HKSCS/HKSCS31; configured client SQL/ordinary, JSON and complex-national input encodings; mode-specific Oracle managed initialization/revalidation; CLOB input prevalidation and version-gated locator input encoding | Further national/JSON/CLOB scope and extended metadata/types still need work | Real emission/storage/coercibility, all promised collation/mode/version profiles; Oracle UTF8MB3 remapping and SJIS/Big5 Open failures remain |
| P1-09 Discovery/HA | TCP retry rounds/backoff/shared budget, endpoint TCP reachability cache, TNS/OCP adapters, groups/weights/selection, global/group/address blacklist controls and attempt ceilings | Further discovery/policy boundaries; TCP reachability does not prove database/tenant health | Real discovery services, node/proxy failure and route changes |
| P1-10 Schema | Mode-aware catalog collections/DataTypes and atomic routine-parameter derivation, including FLOAT/NUMBER_FLOAT and INOUT normalization | Package-local PL/SQL argument types remain rejected; boundary regressions as actual dictionary differences are found | Real catalogs, permissions, case, overloads, versions and proxies |
| P1-11 Credentials | Per-Open provider acquisition, expiry/cancel, versioned rotation and old-session retirement, redacted diagnostics | Boundary regressions as real provider/plugin differences are found; local core exists | Real credential service, authentication plugins/KDC, rejection/expiry/rotation |
| P2-01 DataAdapter | Fill/mappings/events, explicit CRUD and bounded automatic CommandBuilder SQL with original-value/NULL/concurrency handling | Query/key/generated-value restrictions remain; EF Core provider is separate scope | Real generated SQL/catalog/locks/concurrency and independent runtimes |
| P2-02 Statement cache | Opt-in idle LRU, capacity/close, context/schema/reset generation invalidation | Further reprepare/resource/load regressions | Real DDL/reprepare, prepare-count/latency/resource/proxy reports |
| P2-03 Batch optimization | Sequential Batch, existing Oracle array binding, opt-in bounded MySQL INSERT rewrite with group error attribution | **Multi-send/pipelining and separate bulk protocol are missing** | Actual counts/partial failure, transaction/default/trigger/warning behavior, packet limits and throughput/memory |
| P2-04 Diagnostics | ILogger/Activity/Meter, pool metrics including current leases, connection/ping phase timing, slow-query/cursor thresholds, protocol frame/negotiation events, session I/O/upload-source read durations and collectible-load fixes | **Additional resource-wait instrumentation**, workload/overhead validation | Real load, resource curves and listener overhead |
| P2-05 Runtimes/ecosystem | Five product assets; Windows nupkg protocol subset on actual .NET 8/9/10 and Framework host, plus Standard assets on .NET 10 | Complete promised platform/runtime/API matrix; Framework/ORM support scope; existing savepoint identifier limitations | Non-Windows and full feature/real-database reports on promised runtimes |
| P2-06 Real compatibility | Acceptance tooling and historical direct-server reports; distinct sync/async client-channel reset/reuse and time-zone retirement cases, 4/4 per mode on direct 4.4.2.2; executable opt-in zero-date storage/read/write-back assertions | **Two `matrix.*` deferred placeholders**; six controlled client-route cases (each mode7/7 including connection), controlled network and one XA coordinator process crash point have assertions and direct-server evidence; source/license/package/publication review remains | Proxy/backend identity and complete reset/resource matrix; TLS/auth, topology faults, unknown network outcomes, permissive zero-date SQL_MODE storage and external-coordinator XA crash recovery |

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
| Text cursor columns, complex cursor results and output framing guards | `0dcd544`, `a0f789e`, `9d113ab`, `c3d2c83` |
| Executable borrower-isolation acceptance case (not physical reset proof) | `6e64c7e` |
| Complex DDL invalidation and national members | `1bb13cb`, `80ddfbe` |
| Batch metadata/result limits/rewrite and statement cache | `27bd176`, `7976fb3`, `6fc1891`, `f15716e` |
| Core type/charset corrections and result families | `2bbecac`, `6b357f0`, `4810907`, `6e2197c`, `94fcd85`, `7d05ce4`, `f3c484d`, `7c1a602` |
| MySQL string fallback and further result charsets | `0484943`, `d842fee`, `37a0aba`, `4821d54`, `4ce3a35` |
| HKSCS/HKSCS31 ordinary result decoding | `e0498e9` |
| Same-channel pool reuse and executable opt-in zero-date acceptance | `7d777ae`, `da62285` |
| Trusted single-byte and DBCS MySQL string fallback | `f03f235`, `ed2fc84` |
| Complex national client input and Oracle managed charset initialization | `c09cf0a` |
| Diagnostics and packaged runtime protocol verification | `b2fce9d`, `5ea71b2`, `ffceec8`, `d7a5354`, `e3a07a2` |
| Authenticated protocol negotiation diagnostics | `85e6fe6` |

## Verification boundary and next work

- Latest implementation verification is [protocol negotiation diagnostics](obconnector-dotnet-progress.md#2026-10-27-authenticated-protocol-negotiation-diagnostics),
  committed as `85e6fe6`: saved complete units8091/8091, consumers340/340, final focused/runner488/488,
  opt-out15 pass/116 skip and Release zero warnings/errors. Product hashes match the saved
  five-asset record. These are retained results, not tests rerun during this source alignment.
  No real OB20/trace or independent-runtime/load compatibility is established by this module.
- Latest acceptance verification is [controlled topology](obconnector-dotnet-progress.md#2026-10-27-controlled-topology-acceptance),
  committed as06a18bb: saved final full units8066/8066, Release build zero warnings/errors,
  opt-out integration15 pass/116 skip and direct Oracle7/7/MySQL7/7. These are the saved module
  results rechecked during this alignment, not new runs or real independent-node/proxy HA proof.
- CLOB locator input verification, retained REDs and safe live reports are in the
  [module record](obconnector-dotnet-progress.md#2026-10-27-clob-locator-client-encoding).
  Named contents and locator binding are separate wire contracts. Default/GB18030 input checks
  do not close older backports, other server/proxy versions, named locator materialization or cursor input.

- Previous committed complex-national module: saved reports contain complete units7846/7846,
  focused267/267 and affected consumers350/350; the Release build records zero warnings/errors.
  Direct Oracle array-input evidence is36/48, with12 Open failures before DDL. These saved results
  were rechecked during the documentation alignment, not rerun. Exact evidence is in the
  [module record](obconnector-dotnet-progress.md#2026-10-27-complex-national-client-encoding).
- Previous DBCS fallback: complete units7774/7774, focused290/290, affected consumers160/160 and
  direct generated-literal checks14/14 plus control pass; exact verification is in its module record.
  Ambiguous metadata and actual server1295/mode/collation/proxy/version compatibility remain open.
- Previous single-byte fallback: focused units234/234, complete units7742/7742 and affected
  consumers90/90 pass; Release solution build has zero warnings/errors. Independent review has no
  actionable issue. Real generated-literal parsing with ASCII aliases passes8/8 plus control;
  actual server1295 automatic fallback and the metadata/DBCS/profile limits remain unverified.
- Earlier same-channel pool acceptance: focused units 40/40, Release build zero warnings/errors and direct
  4.4.2.2 acceptance 4/4 per mode. Complete-unit results, commands and retained failures are in the
  [module record](obconnector-dotnet-progress.md#2026-10-27-same-channel-pool-reset-and-reuse-acceptance).
- Historical `6e64c7e` source review: Release build has zero warnings/errors; the focused unit
  selection passes 554/554, with no full-suite, consumer or real-server rerun. Original full-suite
  failures, stale-assertion corrections and server greeting failures remain in their module records.
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
- Real database testing is an explicit integration/acceptance gate, independent of ordinary unit
  tests and CI. Record the exact profile scope and safe results; endpoints and credentials stay
  in private process input/local secrets and are deliberately excluded from this document.
- Historical real-server results and failures are retained in the completeness/progress records.
  In particular, the Connector/J-correlated nested-table-column server limitation is not proof that
  all complex/array shapes fail or pass. Skips, cleanup failures and missing profiles stay visible.

For each subsequent feature, update this checkpoint with its commit, tests/review, remaining code and
external gates; retain the detailed module record instead of appending another unqualified “all open”.
