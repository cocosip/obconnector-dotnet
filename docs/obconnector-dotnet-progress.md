# Verification status and remaining work

Current overview: [all 23 gaps, implemented subfeatures and remaining work](obconnector-dotnet-delivery-status.md).
The records below are chronological feature evidence; their historical “remaining” statements apply
to the candidate recorded in each section.

## 2026-10-27 JSON parameters with client character sets

Developed from1da3ea8 on master for P1-08/W5. Explicit MySQL JSON parameters already use
ordinary string wire type253, but rejected all managed non-default client encodings before that
shared binding path. Twelve new pure codec cases all failed on this guard. Removing only this
two-line prohibition lets the selected strict client codec handle JSON too; no UTF8 byte fallback,
JSON parser, dependency, public API or output-decoding change is introduced.

Source evidence: fixed OceanBase5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5
ob_expr_json_func_helper.cpp is_valid_for_json requests UTF8MB4 calculation collation for
nonbinary strings from other charsets. The [contract](client-character-sets.md) links the exact
source. Syntax/conversion/storage remain server responsibilities. Oracle/Precision/Scale restrictions
including SQL NULL, strict unrepresentability checks and server-preparation/fallback limits remain.
ASCII JSON escapes can express characters outside the selected input family; results stay UTF8.

Twelve codec tests check exact payloads, validation-only encoding, JSON escapes/literal null,
SQL NULL metadata and malformed surrogate rejection. Twenty-four sync/async wire units reuse
the existing charset fixture with an optional JSON branch: exact type253/client bytes and UTF8
JSON results in both row protocols. Existing non-JSON branches remain covered. Ten new five-asset
consumer cases each exercise all twelve names with deprecated EOF; they run in one.NET10host.
Existing pre-send rejection tests now include unrepresentable explicit JSON and subsequent reuse.

Serial Release solution build passed with zero warnings/errors in17.23s. Focused units111 and
affected consumers570 passed, zero failures/skips; consumers took3minutes2seconds. Whitespace
format verification exited0 with a workspace-load warning. Complete units7543 passed, zero
failures/skips, in8minutes30seconds. Independent source/callers/tests/contracts review found no
actionable issue; final ledger consistency was checked before commit.

New opt-in MySQL storage tests cover five input families, sync/async, actual JSON conversion,
escaped supplementary values, SQL NULL versus JSON null, both read protocols and invalid-input
recovery. All ten real attempts failed reading the initial greeting, before authentication or JSON.
Both existing default-charset sync/async connection controls also failed there. An independent
TcpClient connected and read zero initial header bytes. The underlying server/network cause is
unknown; no table was created and no JSON/storage/server-version success is claimed. No OBProxy
was tested. Original RED and all failed real/control TRX/logs remain under ignored
TestResults/local-completeness/json-client-*; the raw source snapshot and probe note are retained.
All23 IDs/eight work packages retain their remaining implementation and deployment criteria.

## 2026-10-27 CLOB input charset validation before writes

Developed fromb66cda1 on master for P1-08/W5. WriteText/WriteTextAsync previously validated
only UTF-8 for the whole input, but ordinary DBMS_LOB.WRITE parameters use the selected client
encoding. A representable8191-scalar prefix could complete before an unrepresentable suffix failed;
the failed mutation also invalidated the handle. Twelve sync/async loopback regressions across
ascii, latin1, gbk, utf8mb3, sjis and hkscs reproduced exactly one observed write instead of zero.

The complete Unicode input is still validated first. The existing8191-scalar boundary is now
shared by prevalidation and writing. Before acquiring mutation/session ownership, every non-default
encoding chunk is checked; no whole-CLOB encoded buffer is retained. Default UTF-8 avoids those
extra encoding copies. Rejection preserves the locator and handle; the same handle then performs
a successful byte-exact write. Four further default/GB18030-2022 sync/async tests place a
supplementary scalar at the first chunk's end, checking both chunks' bytes, amounts, one-based
offsets and returned-locator reuse. Existing server/network partial-write semantics are unchanged.
See the updated [client charset contract](client-character-sets.md).

An initial one-line whole-input codec check passed18 focused tests and1000 affected consumers, but
independent review identified proportional temporary encoding allocations. That candidate's full
unit run was deliberately stopped before completion so the bounded implementation could replace
it; no full-run aggregate/pass is claimed. The initial affected unit run passed450 and failed1:
ClientCharsetRejectsMismatchedOrExtraStateAfterOpaqueSql (sync/extra-row/deprecated-EOF) raced the
client's intentional abort with the fixture's later EOF write. The invalid-response fixture now
ends after its second row; normal responses still terminate normally. Exact outer OceanBaseException,
inner ProtocolException and extra-row reason assertions prove the intended validation branch,
rather than accepting a generic truncated response. Product charset error mapping is unchanged.

The first strengthened assertion incorrectly expected an unwrapped ProtocolException, producing
24 passes/2 failures. It was corrected against CommandExchange.Fail's existing wrapping contract.
Final focused26 passed, zero failures/skips. Final serial Release solution build passed with zero
warnings/errors in7.82s; whitespace format verification exited0 with a workspace-load warning.
Final complete units7507 passed, zero failures/skips, in8minutes39seconds (TRX wall time8minutes45seconds).
Final affected units455 and consumers1000 passed, zero failures/skips; consumers took3minutes40seconds
and load five assets in one.NET10host. Independent final source/tests/docs review has no remaining
actionable finding; bounded-allocation and exception-shape findings have been addressed.
Logs/TRX are under ignored TestResults/local-completeness/clob-charset-*; original failures and
the stopped candidate's log remain. No real database was accessed or compatibility scope expanded.

## 2026-10-27 Client SQL and parameter character sets

Developed from33c052b on master for P1-08/W5. The new `Character Set` / `CharacterSet`
option defaults to utf8mb4; `utf8` canonicalizes to utf8mb3. Twelve non-default names reuse strict
server mappings while preserving ASCII SQL bytes and rejecting non-round-tripping aliases.
UTF16/LE, binary and unknown names fail before networking. Canonical input encoding partitions
pools and survives endpoint/credential snapshots. Authentication and initial identity stay UTF-8.
Managed initialization/reset sets the input family, restores UTF8MB4 results and validates four
session values before publication/reuse. See the [complete contract](client-character-sets.md).

The encoding reaches text/prepare/database requests, ordinary scalar/nested complex parameters and
type identities, Oracle PrepareExecute lengths/checksum and array columns, procedure inputs,
metadata lookup parameters, KeyInfo and INSERT rewrite byte budgets. Standalone national parameters
remain independent. Explicit JSON parameters including typed NULL require default utf8mb4 because
their existing wire type is an ordinary string. Complex national members retain their existing
connection-encoding restrictions; CLOB writes inherit ordinary input encoding, reads stay UTF-8.
MySQL string literal fallback remains UTF-8-only; Oracle literal limits use actual encoded bytes.

An early design probed before every command. Review identified changed ROW_COUNT/FOUND_ROWS/warnings
and invalid queries between XA phases. The final design caches verified state, invalidates on
classified stateful/opaque SQL or mismatching reported variables, and verifies before the next
input payload. Generated ASCII XA controls bypass that query. Stateless classified SQL gains no
probe. A recheck after conservative classification can still change session diagnostic functions;
manual charset mutation and arbitrary hidden function/trigger effects are outside the invariant.
Trust is checked before stream upload; a later change with staged data breaks the session.

Initial RED captured13 missing-option failures and5 expected invalid-name passes. Expanded local
fixtures cover twelve names, both modes, sync/async, text/prepare, result identifiers/decoding,
EOF/reset variants, strict aliases, byte budgets, trust transitions, procedure INOUT, XA controls,
staged data and Oracle PrepareExecute. Final focused units166 passed, zero failures/skips.
Release solution build passed with zero warnings/errors in11.12s. Final whitespace format
verification exited0 with a workspace-load warning; no whitespace error remained.

The first complete unit run failed:7484 passed,2 failed,total7486 in9m06s. The new option was absent
from the exhaustive pool-key fixture, and an initial JSON-preservation assertion exposed the
ordinary-string wire type. Both were corrected, with default-only JSON parameter prevalidation.
A subsequent focused run passed164 and failed2 new Oracle procedure fixtures because unquoted
non-ASCII identifiers were used; those fixtures now quote the identifier under the existing API
contract. Original failed TRX/logs remain under ignored TestResults/local-completeness/client-charset-*.

Final affected consumers560 passed in3m07s with zero failures/skips on the final built artifact;
the earlier consumer run before JSON/fixture corrections is retained separately. Consumers load
five assets in one.NET10host, not five independent runtimes. Final complete units7491 passed in
8m50s, zero failures/skips. Independent final read-only source/tests/contract and delivery-ledger
review has no remaining actionable finding; the reviewer did not execute build/test gates.
Final diff whitespace and local Markdown target/fence/new-anchor checks passed. This completes
the local module's implementation, verification and review gates for its master commit.

No real OceanBase or OBProxy was accessed. Accepted server names/collations, storage/coercibility,
national/JSON/CLOB combinations, version/proxy/reset behavior and performance still require their
corresponding matrices. This module does not close P1-08, any other gap ID or any work package.

## 2026-10-26 Current borrowed physical-session metrics

Developed fromf0edabf on master for P0-01/P2-04. The existing untagged pool meter adds
`oceanbase.client.pool.leased`, an UpDownCounter with unit `{session}`. The cumulative `borrowed`
counter is preserved. Per-pool membership tracks Rent/RentAsync/TryRent and ends each lease once at
Return entry or Discard; reset, idle/prewarm/probe work are excluded. Clear/disposal keeps outstanding
leases counted until cleanup. Newly rented sessions can still be authenticating during logical Open.
See the [complete pool metric contract](pool-metrics.md), including late-listener deltas and capacity
limits. No new dependency, setting or product callback registration was introduced.

Initial RED10 failed with expected1/actual0, proving the missing metric. GREEN10 then expanded16
passed without failures/skips: creation/reuse, clear/disposal, warmup/TryRent, canceled waits, foreign/
duplicate discard, reset success/failure, rejected public authentication, greeting/identity cancellation,
concurrent duplicate cleanup and a metric callback clearing the pool on another thread. The affected
pool subset passed63; complete units passed7391 in8m48s, zero failures/skips. Affected consumers
passed76 plus64 distinct handoff cases (140 total), no failures/skips;40 new cases cover five assets,
both modes, sync/async and observer exceptions/Activity mutation in one.NET10host. This does not
independently verify five runtimes or prove eventual ALC collection; source review found no new static
pool-retention callback. Format verification exited0 with a workspace-load warning; whitespace passes.

Release solution build initially passed with zero warnings/errors in15.94s. A later build was
incorrectly run alongside the full unit host and failed MSB3027/MSB3021 after10 file-copy retries for
locked xunit.abstractions.dll. This was a verification scheduling error; its log is retained.
After the test host exited, the final serial Release solution build passed in1.82s with zero
warnings/errors. No source workaround or change to build policy was introduced.
Logs/TRX are under ignored TestResults/local-completeness/pool-leases-*; original RED and locked-build
outputs remain. Independent read-only source/tests/docs review has no remaining actionable finding;
its two missing-regression findings were addressed by cancellation-after-borrow and cross-thread
pool-management callback tests. The reviewer did not run builds/tests; execution gates were run here.

No real database was accessed. Other resource waits, workload/overhead, sustained limits and full
deployment matrices remain open; none of the23 gap IDs or eight work packages is closed by this metric.

## 2026-10-26 Session I/O diagnostics completed after checkpoint

Resumed from8291ca6 on master after fetching origin (no remote difference). The saved implementation
now has a [public metric contract](session-io-diagnostics.md), architecture/README links and two more
regressions. Parallel pending read/write completions publish exactly once after Finish. A split
loopback row keeps a sequential TextReader body read blocked: the histogram includes this transport
wait, while total lifetime minus timed I/O excludes the measured application gap. This tests one
streaming path, not every wrapper or concurrent transport-disposal interleaving.

Fresh Release solution builds passed with zero warnings/errors (final23.68s). Baseline complete units
passed7373; final complete units including both additions passed7375 in8m56s. Focused session-I/O/TNS
units passed89 and affected diagnostics/TNS consumers passed279 in1m50s. All had zero failures/skips.
Eight isolated runs of the original TNS concurrent-replacement test also passed. Consumer results
cover five assets in one.NET10host, not independent runtimes. Format verification exited0 with a
workspace-load warning; final whitespace checks passed. Logs/TRX: ignored
TestResults/local-completeness/resume-session-io-* and resume-tns-repeat-*.

The historical File.Replace failure below remains unresolved, not retrospectively repaired.
Readers already allow read/write/delete sharing; .NET forwards the native replacement error.
Neither missing resolver sharing nor another process/filesystem condition has been established.
No TNS implementation/assertion change, retry workaround or skipped case was introduced.

Independent read-only source/tests/docs review found no remaining blocker. It corrected an initial
documentation overclaim: exchange publication follows physical lease release and its registered
release callback, while higher-level XA/transaction/LOB guards may still prohibit connection reentry.
The reviewer did not execute tests; the implementer ran the gates above. No real database was accessed.
Other resource metrics, workload/overhead and external compatibility gates remain open; all23 IDs
and eight work packages retain their full closure requirements.

## 2026-10-26 End-of-day development checkpoint: session I/O diagnostics

Saved on master at the user's explicit request to update progress and commit before leaving.
Base is e0498e9, the last completed HKSCS/HKSCS31 feature. This checkpoint includes in-progress
source/tests; it does not satisfy the normal completed-feature gate. No push, branch/worktree or
real database operation was performed for this checkpoint. The
[delivery overview](obconnector-dotnet-delivery-status.md) lists all23 gap IDs and remaining work.

P2-04/W7 now has optional per-exchange read/write accumulation and the histogram
`oceanbase.client.protocol.io.duration` (seconds), bounded mode/direction/final-outcome labels,
deferred publication until ownership release and pending I/O completion, observer exception isolation
and Activity.Current restoration. Buffered/sequential reads, queued statement closes and LOCAL INFILE
packet writes use the wrappers; application upload-source reads are excluded. Timing includes the
framing/copy/codec and existing synchronous protocol observers inside each timed call. Enabled async
wrappers may allocate state machines; disabled wrappers directly return the underlying tasks.

Initial RED8 failed because the metric was absent. Focused20 passed with no failures/skips, including
sync/async success, SQL ERR, streaming completion, listener reentry/throwing/restoration, transport
faults, cancellation/timeout and logical pending-Finish/empty/disabled cases. Release solution build
passed in5.46seconds with zero warnings/errors. Affected consumers215 passed with no failures/skips
in47seconds; the10 added cases load five assets on one.NET10host. Independent source/tests review
found no blocking code findings, and the two timing/allocation wording findings were resolved.
This is not true parallel stress or independently timed coverage of every path.

The full unit run reported a failure in
TnsAddressResolverTests.ConcurrentReadersUseCompleteSnapshotsAndMissingFilesHaveNoFallback,
at TnsAddressResolverTests.cs:206: File.Replace threw IOException because the replaced file could not
be deleted. The resolver already shares read/write/delete access. The isolated recheck passed1/1
in200ms on the same built artifact. Root cause remains unresolved; neither an environmental cause
nor a diagnostics regression has been established. No publisher/resolver workaround was added.
At this checkpoint the full run has not returned a terminal result; no final full-suite count is
claimed. Keep the original failure visible rather than treating the isolated pass as full success.
Evidence logs/TRX remain under ignored TestResults/local-completeness/session-io-*.

Resume with the terminal full-suite result and exact file-replacement investigation, then finish
diagnostics API/architecture documentation and applicable test/review gates. Further resource-wait
metrics/load evidence and the other23-gap requirements remain open; this checkpoint is not acceptance
or release approval. The previous HKSCS record below remains the latest completed local module.

## 2026-10-26 HKSCS and HKSCS31 ordinary results

Developed from4ce3a35 on master for P1-08/W5. HKSCS IDs152/336/351/366 and HKSCS31
IDs153/337/352/367 use distinct lazy strict scalar lookups with wire width2 metadata. Existing CP950
plus3186 BMP corrections/62 undefined-slot removals provides the old family;1651 Plane2 replacements
provide the revised family. Each has19848 defined positions including128 ASCII, decoding to19754
distinct scalars. Reverse mapping preserves66 nonminimal source choices and U+2593-to-F9FE, which
decodes to U+FFED. Decoder count/flush/overflow/reset and supplementary two-slot retry preserve state.
No client/parameter/national/JSON/CLOB encoding expansion, new dependency or resource is introduced.

Optional HkscsReference.py freshly regenerated independent fixed-source decode/BMP/Plane2 SHA256
expectations from the two complete source table headers; optional ExportWindows950.ps1 produced the strict
CP950 dump used to regenerate compact product deltas. Source snapshots and research remain under
ignored TestResults/local-completeness/. Tests require no Python, source download or database.

Initial RED14 failed on unsupported collations. The first implementation candidate failed14 because
strict CP950 rejects some duplicate byte pairs; the existing sparse corrections already represent
those source assignments. Keeping rejected baseline slots undefined before applying deltas fixed
initialization; GREEN14 passed. Exhaustive source digests cover256 singles/32768 pairs per family,
all65536 BMP scalars and all65536 Plane2 scalars. Valid pairs additionally test incremental counts,
each-byte splits and reverse results. Every split/reset/tiny Convert, canonical/alias, pending state,
invalid flush/overflow retry and bounds/invalid-surrogate cases are covered. All eight IDs have pure
codec/schema tests across nine ordinary string/BLOB wire codes. Two appended shared samples add32
dual-mode sync/async text/prepared buffered/sequential reader cases with exact text, NULL and reuse;
four malformed sequential cases verify the consumed connection stays Open.

Release solution build passed with zero warnings/errors in8.10seconds. Affected five-asset consumers
passed540 without failures/skips in2minutes49seconds;40 new cases exercise MySQL async four-shape
readers on one.NET10host, not five independent runtimes. No new family-specific public BLOB reader
matrix was added. Independent source/tests review found no blockers. Documentation review found one
current compatibility-summary omission, now corrected; provenance facts and all23 delivery rows were
also checked independently. Complete units passed7353 without failures/skips in8minutes15seconds,
a net increase of91 cases. Final diff whitespace checks passed. The source/tests/docs reviewer
did not run tests/builds/Python or access a real database; all execution gates were run by the implementer.
Verification logs/TRX remain under ignored TestResults/local-completeness/hkscs-*.

No real database was accessed. Actual emission/storage, client charset expansion, remaining Oracle/
protocol/diagnostic features and full deployment matrices remain open. The new
[delivery overview](obconnector-dotnet-delivery-status.md) maps all23 IDs to committed implementation,
remaining code/scope and external gates; historical evidence below retains its original scope.

## 2026-10-26 Big5 ordinary results

Developed from4821d54 on master for P1-08/W5. Big5 IDs1/84/335/350/365 now use a distinct lazy codec
with width2 metadata. It builds an immutable approximately45KB character lookup and a reverse
dictionary from existing strict CP950 plus15 scattered corrections and249 C6/C7 assignments. All13710
defined BMP pair mappings and128 ASCII characters round-trip exactly; source reverse entries have no
aliases/nonminimal canonical choices. This includes four box-drawing pairs absent from strict CP950.
The seven source FFFD placeholders, zero holes, Windows extensions/private-use aliases, malformed
and truncated input fail explicitly. Count probes, invalid flush and output overflow do not consume
pending lead state; Reset clears it. No client option, dependency, resource file or parameter/national/
JSON/CLOB expansion is introduced. See the
[fixed-source contract](obconnector-dotnet-architecture-design.md#result-character-sets).

The source is OceanBase v4.3.5_CE commit5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5 ob_ctype_big5.cc/
ob_ctype_big5_tab.h; ignored source snapshots and research are under TestResults/local-completeness/.
The optional Python generator freshly regenerated independent digest expectations from the two decode
tables and11 reverse ranges:256 singles plus32768 high-byte pairs, and65536 BMP encoding/refusal
positions. The strict policy normalizes source FFFD placeholders to failures. Tests require no Python,
source download or database. THIRD-PARTY-NOTICES now accurately inventories compact numeric assignment
data and CP932/950 use alongside earlier charset modules; it retains the separate release provenance
review, without claiming an upstream implementation port or embedding full upstream tables.

Initial RED7 failed on explicit unsupported-collation errors; GREEN7 and expanded focused698 passed
without failures/skips. Exhaustive valid pair tests additionally check counts, each-byte decoder
splits and exact reverse bytes. Three explicit samples test every split/reset/count and tiny Convert;
invalid flush/overflow-retry and array offset/empty/null/range/max-count behavior are covered. All five
IDs have pure ordinary codec/schema coverage across nine string/BLOB types. The appended shared sample
adds16 dual-mode sync/async text/prepared buffered/sequential cases with exact values, NULL and reuse
SQL. Two malformed sequential cases leave the fully consumed connection Open. Release solution build
passed with zero warnings/errors in11.48seconds. Initial independent source/tests review found no
blockers; the reviewer only read files and ran diff whitespace checks. Affected five-asset consumers
passed500 without failures/skips in2minutes10seconds. New20 cases cover MySQL async text/prepared
buffered/sequential reads on one.NET10host; other modes/sync run in unit peers. No new family-specific
public BLOB reader matrix was added. Final independent documentation/provenance-facts review found
no blockers or actionable inconsistencies; no legal/release approval is claimed. Full units passed7262
without failures/skips in8minutes11seconds, a net increase of61 cases. Final staged diff whitespace
checks passed. Verification logs/TRX results remain under ignored TestResults/local-completeness/big5-*.

No real database was accessed. HKSCS/HKSCS31, actual emission/storage, full collation/mode/version/
OBProxy/TLS/runtime/platform and remaining offline implementation remain open. All23 IDs/eight work
packages remain open; historical reports below retain their original scope.

## 2026-10-26 SJIS ordinary results

Developed from37a0aba for P1-08/W5. SJIS IDs13/88/334/349/364 select a distinct strict CP932 adapter
with width2. Seven double-byte mapping corrections follow fixed OceanBase v4.3.5_CE commit
5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5 ob_ctype_sjis.cc. ASCII and halfwidth remain single bytes;
6879 double-byte assignments are supported. Windows-only leads87/EB–FC, invalid singles/trails,
unassigned pairs and incomplete leads fail. The decoder preserves pending leads, nonconsuming count
probes and Reset semantics. Source canonical U+005C encodes815F, while both815F and5C decode backslash.
Reverse round-trip validation rejects Windows aliases/scalars and unrepresentable characters.
ChineseEncoding and prior mappings are unchanged. No client option, dependency or SQL/parameter/
national/JSON/CLOB encoding expansion is introduced; see the
[fixed-source contract](obconnector-dotnet-architecture-design.md#result-character-sets).

Ignored source snapshots and initial research remain under TestResults/local-completeness/.
All6879 source-defined pairs match CP932 after seven corrections. Restricting CP932 leads/trails
matches all16640 candidate positions exactly. The optional Python source-table generator freshly
regenerated both committed SHA256 expectations:256 singles plus16640 lead/trail decode positions,
and65536 BMP encoding/refusal positions. It requires no live database and is not a test dependency.

Initial RED11 failed on explicit unsupported-collation errors; GREEN11 passed. Expanded focused637
passed without failures/skips. The exhaustive decoding test additionally verifies counts and each-byte
decoder splits; the exhaustive encoding test verifies count consistency, canonical bytes, round-trips
and supplementary refusal. Eight explicit mapping/sample cases cover every split/count/reset and
tiny output Convert. Pending-lead count/flush refusal and output-overflow/retry preserve state.
All five IDs have ordinary codec/schema coverage across nine string/BLOB wire types; the appended
shared sample adds16 dual-mode sync/async text/prepared buffered/sequential cases with exact values,
NULL and reuse SQL. Two malformed sequential cases leave the fully consumed connection Open.
Release solution build passed with zero warnings/errors in8.24seconds. Affected five-asset consumers
passed480 without failures/skips in2minutes9seconds. New20 consumers cover MySQL async text/prepared
buffered/sequential reads on one.NET10host; other modes/sync run in unit peers. No new family-specific
public BLOB reader matrix was added. Independent source/tests/contracts review found no blockers;
the reviewer ran only the diff whitespace check, with no test/build/Python/network/database operations.
Full units passed7201 without failures/skips in8minutes12seconds, a net increase of61 cases.
README now includes SJIS and the previously delivered DEC8/TIS620/GB18030-2022 result families.
Final documentation follow-up and staged diff whitespace checks passed. Verification logs and TRX
results remain under ignored TestResults/local-completeness/sjis-*.

No real database was accessed. Actual emission/storage, full collation/mode/version/OBProxy/TLS/
runtime/platform and remaining offline implementation remain open; all23 IDs/eight work packages
remain open. Historical reports below retain their original scope.

## 2026-10-26 DEC8 and TIS620 ordinary results

Developed fromd842fee for P1-08/W5. DEC8 IDs3/69/338/353/368 and TIS62018/89/332/347/362 now
select strict stateless single-byte result tables with width1. Both preserve C1 controls; DEC8 has
its own currency/ligature mappings and TIS620 uses Thai mappings without Windows874 extensions.
Undefined source slots throw rather than return NUL/replacement:14 DEC8 positions and nine TIS620
positions. This follows the existing strict result policy; the TIS620 server converter itself accepts
its FFFD placeholders. The [contract](obconnector-dotnet-architecture-design.md#result-character-sets)
records exact source links, IDs, mapping policy and independent digest regeneration.

The fixed v4.3.5_CE source is5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5. ob_ctype_extra.cc contains
identical DEC8 Swedish/bin tables; ob_ctype_tis620_tab.h contains the TIS620 table. Each has256 entries
and identity00–9F. The optional Python standard-library generator parses the server tables and emits
SHA256 over big-endian scalar positions with FFFF for undefined slots. Fresh regeneration matches
the two committed test expectations. Tests do not need Python, source downloads or a database.

Initial RED12 failed on explicit unsupported-collation errors; GREEN12 passed. Expanded focused576
passed without failures/skips, including all ten IDs, all256 bytes, strict counts/decoders, every BMP
reverse mapping or refusal, surrogate-pair refusal, tiny decoder output/count/reset and array bounds.
Shared ordinary-reader fixtures add32 dual-mode sync/async text/prepared buffered/sequential cases;
pure codec/schema tests cover ordinary string/BLOB wire families. Two malformed sequential cases
leave the fully consumed connection Open. Release solution build passed with zero warnings/errors in7.10seconds
after fixing an ambiguous null test overload. That test-source compilation failure is recorded here
and in session diagnostics; it was not a product failure.

Affected five-asset consumers passed460 without failures/skips in2minutes10seconds. New40 consumers
cover both new families in MySQL async text/prepared buffered/sequential reads on one.NET10host;
other modes/sync are covered by the unit peers. No new family-specific public BLOB reader matrix was
added. Independent source/tests review found no blockers; the reviewer did not run tests, Python or
database operations. Final independent documentation review found no blockers; consumer status and
malformed-reader Open-state wording were synchronized. Full units passed7140 without failures/skips
in8minutes9seconds, a net increase of98 cases. Final diff whitespace check passed. Verification logs
and TRX results remain under ignored TestResults/local-completeness/single-byte-*.
No real database was accessed. Actual emission/storage, complete collation/mode/version/OBProxy/TLS/
runtime/platform and remaining offline functionality stay open; all23 IDs/eight work packages remain
open. No client encoding option, parameter encoding, dependency or national/JSON/CLOB expansion was
introduced. Historical reports below retain their original scope.

## 2026-10-26 GB18030-2022 ordinary results

Developed from3196a45 for P1-08/W5. Ordinary result collations216–222/330/345/360 now select a distinct
GB18030-2022 encoding. Existing ChineseEncoding framing/stateful decoder and2005 correction are
reused, with18 reversible BMP PUA/assigned-scalar swaps. No public option, dependency, global encoding
registration or client-charset negotiation was added. Existing2005 IDs and mappings are preserved;
national/JSON/CLOB contracts keep their scope. Both modes share ordinary result mapping and width4
metadata. The [public contract](obconnector-dotnet-architecture-design.md#result-character-sets)
contains fixed source links and an optional independent reference regeneration command.

Release v4.3.5_CE resolves to5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5. The pinned ob_charset.h confirms
all ten IDs and ob_ctype_gb18030.cc:init_gb18030_2022 confirms all18 swaps. Sources were fetched over
certificate-verified HTTPS; sandbox TLS failure was resolved through authorized execution without
insecure fallback. Source snapshots/hashes remain under ignored TestResults/local-completeness/
gb18030-baseline*. The earlier Go codec helped locate references; product changes were checked
against the pinned OceanBase sources and fresh independent OpenJDK output.

Initial RED20 failed on the previous explicit collation refusal; GREEN20 passed. Fresh Temurin
OpenJDK21.0.12.1+1 reference generation matched the committed expected SHA256 values for63,488 BMP
scalar encodings and23,940 legal two-byte plus39,420 BMP four-byte decode positions. All18 mapping
pairs verify both directions, both revisions and every byte split/count/reset. Malformed/truncated
and out-of-range bytes remain strict failures. The former216/222 unsupported cases now use enum holes
223/324; rejection coverage is retained for unsupported IDs. Affected498 unit cases passed without
failures/skips. Release solution build passed with zero warnings/errors,5.92seconds.

Complete units7042 passed without failures/skips in8minutes11seconds.
Affected five-asset consumers passed420 without failures/skips in1minute56seconds; new20 cases
cover MySQL async text/prepared buffered/sequential reads in one.NET10host. New dual-mode sync/async
ordinary-column paths are covered by unit peers. Pure codec/schema cases cover all ten IDs and the
existing ordinary string/BLOB wire families; no new2022 public BLOB, tiny-output-buffer or supplementary
endpoint-specific matrix was added. Independent initial source/test review found no blockers.
Final independent source/tests/contracts review found no remaining blockers after correcting a stale
unsupported2022 sentence and the completed consumer-gate status. The reviewer did not execute tests,
Java or database operations. Final diff whitespace check passed. Net unit-case increase89:91new names
and two replaced refusal names. The old216/222 cases now use223/324 and retain unknown-ID rejection.
No real database was accessed. Full mode/version/collation/OBProxy/TLS/runtime/platform/storage,
client/national encoding and other missing capabilities remain open; all23 IDs/eight work packages
remain open. Historical real reports and previous modules below retain their original scope.

## 2026-10-26 Packaged protocol runtime verification

Developed from0484943 for P2-05/W8. The existing Verify-Packages.ps1 consumer now copies one portable
ProtocolScenarios.cs companion and invokes it from PackageConsumer.Verify. No product code, package
dependency, CI release policy or public option changed. Every profile runs both authenticated modes
x sync/async commands x buffered/sequential readers. Peers assert exact packet sequence/command bytes,
automatic integer prepare/bind, binary metadata/value/NULL, statement close, known server ERR and
subsequent reuse, explicit transaction/commit/autocommit restoration, capacity-one reset, a different
logical borrower over the same physical connection, ping and physical close on pool clear. The
Standard2.0 asset uses synchronous transaction begin/commit and Close even in async-command scenarios.
Peers have bounded packet allocation/read/write and cancellation closes listeners/sockets; combined
failures retain both peer and consumer causes.

Package baseline six API/config profiles passed before implementation. New protocol48/48 scenarios
passed with no skips across net8/8.0.32, net9/9.0.21, net10/10.0.13, Standard2.0 and2.1/10.0.13,
and Standard2.0/Framework4.8.10008.0 (Release534296). Both product nupkgs were checked by fresh-cache
SHA512 and exact asset/dependency selection. All five consumer targets and the Framework host built
with zero warnings/errors. Three isolated protocol negative controls verified wrong binary value and bound
request on.NET8, and wrong server-error expectation on desktop CLR; each failed at the intended
assertion and terminated within5seconds (the Framework control includes its host rebuild).
Review requested an explicit runtime identity assertion, then confirmed that the existing
DiagnosticSource dependency-major check already rejects ordinary major roll-forward. Main now also
asserts its compile-target runtime major for direct execution and major10 for Standard ALC loading.
A fourth isolated negative control forced net8 to LatestMajor and verified rejection by this direct
assertion; it does not prove the earlier harness would pass on the wrong host. The final positive
matrix was repeated after this addition.
Independent source/tests/scripts/docs review found no remaining blockers; the reviewer did not run
tests or access a real database. Final git diff --check passed.

Retained candidate failures were test-harness mismatches: an invalid ExpectedMode keyword and missing
Oracle time-zone/national rediscovery after consumed snapshots. The first Framework negative control
could not print the unhandled async exception string; explicit host type/message/inner reporting now
returns1 and shows the expected assertion. This does not establish the cause or completeness of CLR
stack-trace formatting behavior. Logs and isolated consumers remain in ignored
TestResults/local-completeness/package-protocol-* and TestResults/package-validation/.

No real database was accessed. No full unit/consumer suite was repeated for this test-only module;
the changed package consumer itself runs against exact candidate nupkgs. All23 gap IDs/eight work
packages and further offline implementation remain open. Real server/OBProxy/TLS/auth/fault/load,
non-Windows and publication evidence must be recorded separately; historical reports below retain
their original scope.

## 2026-10-26 MySQL client string preparation

Developed from6fc1891 for P1-08/W5. Existing opt-in exact1295/HY000 automatic fallback now supports
ordinary MySQL string inputs under fresh trusted UTF-8 session state. Every string fallback queries
client/connection charset, connection collation and SQL mode on its existing exchange; no pool/session
cache or new public option. Ordinary literals preserve session collation, use strict Unicode and
mode-aware escaping, and enforce one statement plus the existing64MiB request budget. Numeric/binary/
NULL-only and Oracle fallback retain their existing paths. Discovery failures never classify the
unsent target as unknown. The [contract](obconnector-dotnet-architecture-design.md#mysql-client-string-preparation)
defines charset, statement, lifecycle and unsupported boundaries.

New66 units and20 consumers. Focused111, complete units6953 and affected consumers403 passed, no
failures/skips. Release solution build passed with zero warnings/errors,6.87seconds. Complete units
took8minutes18seconds and consumers1minute39seconds. Independent source/tests/docs review found no
remaining blockers; the reviewer did not run tests or access real databases.
The public tests cover fresh state after changed modes, drained rejection then requery/recovery,
malformed metadata/NULL/type/UTF-8/size/row/result shapes, discovery ordinary/connection ERR/disconnect,
sync/async cancel/timeout and caller token, transaction/snapshot/concurrent ownership. Pure tests cover
escape/injection boundaries, MB3 aliases, Unicode, comments/semicolon parsing and expanded request
limits. All five assets use sync/async standard/OB20 consumers in one.NET10host; this is not five
independent runtimes or a package/deployment claim.

InitialRED4 captured the previous explicit refusal. The first green candidate passed the new4 but
exposed one retained declared-type exception-order regression; type validation was restored. Later
test failures were isolated to metadata replacement of invalid InlineData Unicode and six peer reads
expecting orderly EOF instead of Windows TCP reset. Invalid surrogates are now constructed at runtime;
the peer starts a new command read before asserting closure, so unexpected target SQL cannot pass
as a sequence mismatch. All original logs/TRX remain under ignored TestResults/local-completeness/
mysql-client-strings-*. The final candidate completed both suites; no candidate run was aborted.
Documentation review also narrowed the response-retirement statement to preserve the complete empty
result's tested reuse, and limited SQL_MODE nontracking to the pooled-state classifier. The final
diff check and contract-link check passed.

No real database was accessed in this module. Non-UTF-8 clients, NUL under NO_BACKSLASH_ESCAPES, real
server literal/parameter collation and coercibility, full mode/version/proxy/TLS/runtime and load
validation remain open. All23 gap IDs/eight work packages and further offline implementation remain
open. Earlier real reports and prior candidate counts below retain their historical boundaries.

## 2026-10-26 MySQL batch INSERT rewrite

Developed from `7c1a602` for P2-03/W7. Modern OceanBaseBatch now exposes opt-in
RewriteBatchedInserts for NonQuery sync/async. Adjacent identical single-tuple MySQL INSERT VALUES
commands combine into bounded groups while retaining server-bound inputs. Stream rows are singleton
boundaries, with no pre-read or caller disposal. Default, Oracle, Reader/Scalar/Prepare and unsupported
syntax/directions retain sequential execution. The [contract](obconnector-dotnet-architecture-design.md#batch-insert-rewrite)
defines mode-invariant parsing,65535 row/parameter and independent64MiB SQL/inline limits, transaction/
reservation/session ownership and cancellation, group counts and read-only exact error attribution.
Opting in changes statement/autocommit, default/expression/trigger/warning and timeout boundaries.

New units78, new consumers12. Focused92 passed with no failures/skips, including14 retained legacy
batch cases. Parser coverage includes comments, quoting/modes, unsupported suffixes and marker shapes.
Loopback/fault checks include mixed bindings across NULL bitmap bytes, streams, transactions, SQL
ordering, prepare errors/partial sends, cancellation/timeout, concurrent Dispose/Async, group count
mismatch, immutable reference snapshots, no suffix replay, batch reuse and reopened-session isolation.
Reflection planning checks cover65535 row/parameter thresholds and exact/over64MiB SQL and inline
requests; these are not real large-packet transport/server evidence. The timer-based large-SQL caller
cancellation case reproduced token loss and now passes; its timer does not synchronize a parser phase,
so an individual passing run can include cancellation before scanning.

Retained RED evidence: missing option1; public grouping2; missing parser18; premature failure callback
release2; NUL in comments1; planning caller token1; deterministic fallback-singleton Dispose race1;
null SQL validation1. The last race holds an earlier original item's Release while the active later
singleton has not been released: original code sent SQL after reservation release, while the fix
rejects Start as disposed before releasing ownership. Failure-only singleton cleanup remains reusable.
Development peer sequence/queued-close, missing using and wrong MySQL isolation-default failures are
retained separately. Independent source/tests/docs review identified the release races and checked
the remaining connected paths; the reviewer ran no tests or real database operations.

The superseded full-unit run was explicitly terminated after the final review fixes, with6876 passed,
no reported failures/skips and no completed suite result; the resulting host-aborted/crash message
records that intentional termination, not an unexplained driver crash. Its353 affected consumers had
passed. Final candidate: solution Release build passed with zero warnings/errors,9.01seconds;
full units6887 passed, no failures/skips,8minutes11seconds; affected Batch/transaction/ambient/cache
consumers353 passed, no failures/skips,1minute4seconds. The consumer matrix runs five assets in one
.NET10 host; the12 new rewrite cases use the three modern assets, sync/async, connection/data-source
batches, success followed by trusted group failure and reuse. Standard assets additionally assert
the modern group property is absent. Independent source/tests/docs review has no remaining blockers
after regression fixes; git diff --check passed. No current-candidate package/runtime claim is made.
No real database was accessed in this module. Actual SQL, triggers/defaults/warnings, server/proxy
packet limits, OBProxy, TLS/compression and throughput/memory comparisons remain open. Multi-send/
pipelining and a separate bulk protocol remain missing. All23 gap IDs/eight work packages remain open.
Prior national/ROWID/package/runtime/live reports below are historical to their respective candidates.

## 2026-10-26 National Unicode collation mapping

Developed from `f3c484d` for P1-08/W5. NationalCharacterSet previously accepted only six UTF-8 and
three UTF-16BE collation IDs, rejecting other known IDs during fallback and charset-name discovery.
It now shares the existing UTF-8 family and a common UTF-16BE predicate with ResultCharacterSet:
118 UTF-8 and29 UTF-16BE IDs. Ordinary result mappings and maximum widths are unchanged. Fallback
still rejects other families, UTF16LE, binary63 and unknown IDs; trusted National > Results >
collation precedence and parameter/complex input encodings retain their existing contracts.

Net new units33:two family tests iterate147 IDs with NCHAR/NVARCHAR2 text/binary fallback, NULL,
metadata and both absent/binary names; four strict byte/incremental decoder cases;23 rejected IDs
and explicit trusted-session overrides; four new sync/async309/342 discovery cases alongside the
retained54 pair. Discovery continues checking NChar UTF16 versus ordinary UTF8 parameter bytes.
RED35 had10fail/25pass, including the two retained54 cases. Focused GREEN371 passed, no failures/
skips. Solution Release build passed with zero warnings/errors,3.16seconds. Independent source/
caller/test review found no blockers; reviewer did not run tests. Affected existing national/
ordinary charset consumers480 passed, no failures/skips,17minutes3seconds; five assets in one
.NET10 host. These retain reader/parameter regressions; new discovery IDs are directly exercised
by unit public paths, not new five-asset discovery peers. Full units6809 passed, no failures/skips,
23minutes6seconds. Independent source/caller/test/documentation review found no blockers and
`git diff --check` passed. A host clock jump was observed during the gates; its cause was not
established and original reports are retained.

No real database accessed. This is mapping/bootstrap evidence, not server national storage,
negotiation, SQL or deployment acceptance. Existing package/independent-runtime evidence remains
historical. All23IDs/eight packages and other local work remain open. Artifacts are retained under
ignored `TestResults/local-completeness/national-collation-*`. No push.

## 2026-10-26 Oracle floating special text results

Developed from `7d05ce4` for P1-08/W5. Oracle type4/5 text additionally accepts complete,
case-insensitive `Inf`, `-Inf` and `NaN` tokens as IEEE special values. Type4 remains boxed Single;
type5 remains Double. Finite values retain their existing per-type parsers, including Single
precision and signed zero. Existing CLR floating formats remain available. MySQL text acceptance,
IEEE binary decoding, parameter encoding and metadata identities retain their contracts. Fixed Java
TextRowProtocol.getNanAndInfForOracle supplies source precedent; its permissive prefix matching is
not copied.

New units20:16 public type/sync/async/text/prepared/buffered/sequential cases, two exact-token/MySQL
boundary cases and two finite Single double-rounding regressions. Initial RED20 had8fail/12pass;
text Inf failed while existing binary paths passed. Focused GREEN77 passed, no failures/skips.
Final solution Release build passed with zero warnings/errors,6.28seconds, including the last typed
getter assertions. New five-asset Oracle async consumers40 passed, no failures/skips,12seconds,
covering special values, exact CLR/provider/schema identities, finite/signed-zero, NULL, following
int42 and command reuse. Consumers load all assets in one .NET10 host. Full units6776 passed,
no failures/skips,7minutes58seconds. Independent source/caller/fixture/documentation review found
no blockers; reviewer did not build/test. `git diff --check` passed.

No real database accessed. Actual server emission/SQL and deployment acceptance remain open.
Preceding ROWID package/runtime results below remain historical and do not verify this candidate.
All23IDs/eight packages and other local work remain open. Artifacts are retained under ignored
`TestResults/local-completeness/oracle-float-*`. No push.

## 2026-10-26 Oracle ROWID results

Developed from `94fcd85` for P1-08/W5. Oracle result209 now reports ROWID/String and returns unchanged
text using strict session UTF-8, including binary/unknown reported collation. Existing length-encoded
text/prepared buffered/sequential readers apply; MySQL rejects the code. No public value type,
parameter code or SQL inventory entry is added. Java fixed-source getObject/getString handling and
its generic field-length path provide the protocol precedent, not a live compatibility result.

New units15:8 public sync/async text/prepared buffered/sequential cases; three collation/metadata
cases, three malformed UTF-8 and one MySQL rejection. Initial RED15:1pass/14fail; related GREEN464
pass, no failures/skips. New consumers20 use Oracle async with five assets, text/prepared and
buffered/sequential. Release solution build passed with zero warnings/errors,4.55seconds. Affected
consumers400pass (380 retained plus20), no failures/skips,1minute37seconds. Source/caller/fixture/
documentation independent review found no blockers; reviewer did not build/test. Full units6756pass
(6741 retained plus15), no failures/skips,8minutes2seconds.

Both candidate nupkgs passed `eng/Verify-Packages.ps1 -VerifyNetFramework`: identity/license/readme/
dependency checks, fresh-cache SHA512 and asset selection. Actual .NET8.0.32/9.0.21/10.0.13 hosts,
netstandard2.0/2.1 on10 and netstandard2.0 on Framework4.8.10008.0 (CLR4.0.30319.42000, Release534296)
passed API/configuration smoke. Retained package consumers/graphs:
`TestResults/package-validation/c81fbe62e656468494ef866b1f3730db`.
The existing RuntimeConsumerSample and BatchRuntimeConsumerSample also built warning-free against
these nupkgs and passed all six actual8/9/10 loopback runs: authenticated Oracle identity/autocommit,
decimal/schema/CloseConnection/pre-cancel/failover; Batch counts/known-error attribution/source
ownership and Activity/Meter checks. This is local protocol evidence, not real SQL or fresh ROWID
coverage on each runtime. Probe sources/script/logs are retained in ignored
`TestResults/local-completeness/rowid-runtime-loopback*`; no source sample edits were needed.

No real database accessed. Actual ROWID emission/SQL/parameter behavior and other deployment
acceptance remain open. All23IDs/eight packages and other local work remain open. Artifacts under
ignored `TestResults/local-completeness/oracle-rowid-*`. No push.

## 2026-10-26 ENUM, SET and GEOMETRY result codes

Developed from `a3532fc` for P1-08/W5 and P1-10/W6. Codes247/248 return strict text or collation63
bytes with ENUM/SET names and String/Binary provider identities; SET labels remain unsplit. Code255
is MySQL-only opaque bytes for any collation, preserving all prefixes/payload. Existing framing and
buffered/sequential paths apply. MySQL DataTypes adds ENUM/SET label-list creation templates using
String. Oracle SQL declarations, spatial APIs/parameters and geometry SQL inventory are not added.

New units81:72 public reader combinations, seven boundaries and two DataTypes mapping cases.
Existing charset cases also exercise247/248 across supported collations. Initial RED99:19pass/80fail;
focused GREEN480pass, no failures/skips. Final Release solution build passed with zero warnings/
errors,9.98seconds. Affected consumers380pass (280 retained plus100), no failures/skips,
1minute48seconds; existing catalog consumers16pass, zero failures/skips,3seconds. The first catalog
filter used the file name instead of its partial-class method name and matched zero tests; that
report is retained separately from the corrected16-case run. Full units6741pass (6660 retained plus81),
no failures/skips,8minutes11seconds.
New consumers100 cover five assets, MySQL async text/prepared buffered/sequential reads; units cover
both modes where applicable and sync/async. Independent source/caller/fixture review found no
blockers; reviewer did not build/test. No real database accessed.

This does not prove server type emission, ENUM/SET SQL deployment behavior or spatial support.
Other local development and all23IDs/eight work packages remain open. Retain original RED and final
artifacts under ignored `TestResults/local-completeness/extended-string-*`. No push.

## 2026-10-26 Close transaction logger reentry

Developed from `6e2197c` for P0-01/W1. Transaction ILogger records from captured Close cleanup now
hold completion-field snapshots and publish after exchange disposal, session return/destruction and
registry release, before Closed logging. The buffer belongs to that close operation. Normal explicit
Commit/Rollback/Dispose and state events retain their existing behavior. A callback can reopen the
pool and start a new transaction without old cleanup touching the new lease afterward.

New units100:96 both-mode sync/async Close/Open, original/other borrower, rollback/restore failure and
finite/infinite-wait combinations; four more cover a logger starting a new transaction then throwing.
Initial RED48 finite-wait cases all fail on pool timeout. The first GREEN was stopped after86pass/
18fail because its single-peer fixture could not accept replacement sessions; this is a fixture
failure, retained with its aborted report. The corrected related run passed368, zero failures/skips,
19seconds, before the last four cases were added. An initial build caught a test variable incorrectly
typed as OceanBaseTransaction instead of the public DbTransaction return; corrected final Release
build passed with zero warnings/errors,15.05seconds. Affected consumers227pass (187 retained plus40),
zero failures/skips,51seconds. Full units6660pass (6560 retained plus100), zero failures/skips,
8minutes2seconds.
New consumers40 cover five assets, both modes and sync/async Close/Open; failure branches are local
units. Source/fixture/consumer independent review found no blockers; reviewer did not build/test.

No real database accessed. This fixes Close-cleanup transaction ILogger reentry only; Activity/Meter,
command logging, other callback/concurrency/long-running boundaries and real deployment evidence
remain open. All23IDs/eight packages and other local development remain open. Retain original and
final artifacts under ignored `TestResults/local-completeness/close-transaction-log-*`. No push.

## 2026-10-26 BLOB family result codes

Developed from `aab2755` for P1-08/W5 and P1-10/W6. Result codes249/250/251 now distinguish
TINY/MEDIUM/LONG BLOB or TEXT by collation, retaining Binary/String provider identity. Existing
length-encoded buffering and streaming apply without new codecs, dependencies or public options.
IsLong/size metadata and MySQL DataTypes inventory cover the additional families. Oracle SQL
declarations and locator ownership keep their existing separate contract.

New units99:96 public loopback combinations (three codes, bytes/text, both modes, sync/async,
text/prepared, buffered/sequential) plus three strict/binary boundary cases. Existing charset cases
also exercise all three codes across the accepted collations. Initial RED235:4pass/231fail; focused
GREEN537pass, no failures/skips. Release solution build: zero warnings/errors,25.18seconds.
Affected consumers280pass (160 retained plus120), no failures/skips,1minute24seconds.
Full units6560pass (6461 retained plus99), no failures/skips,8minutes9seconds.
DI14pass, no failures/skips.
Consumer additions120 cover five assets, MySQL/async, bytes/text and text/prepared/buffered/sequential.
Independent source/caller/fixture review found no blockers; reviewer did not build/test.

The preceding committed discovery candidate was repacked with the corrected README; both package
smokes passed again on the documented actual runtimes, including Framework4.8. Artifacts:
`TestResults/package-validation/965d134ac42c429ea7b488b7a9c572c6` and
`TestResults/local-completeness/address-blacklist-repack*`. This is configuration/API smoke only.

No real database accessed in this module. It does not establish server type emission, new large-packet
coverage, OBProxy/TLS, other platforms or load compatibility. All23IDs/eight packages and other local
development remain open. Reports retained under ignored `TestResults/local-completeness/blob-family-*`.

## 2026-10-26 Discovery address blacklist overrides

Developed from `414b7cc` for P1-09/W4. ADDRESS.OBLB_BLACKLIST replaces the entire inherited group/root
policy. Its immutable endpoint occurrence retains the explicit block through all ordering paths.
Equal effective policies share exact-endpoint history; differing policies and success feedback remain
isolated. Address-only blocks default the shared ceiling to120 and add no policy to other addresses.
Group ceilings, deadlines, idle reuse and no-SQL-replay behavior remain unchanged.
See [contract](obconnector-dotnet-architecture-design.md#discovery-blacklist-and-attempt-ceiling) and
[evidence](obconnector-dotnet-completeness.md#1565-2026-10-26-发现地址级黑名单覆盖).

New units39: initial RED4pass/35fail; related309pass, zero failures/skips. Release solution build:
zero warnings/errors,39.04seconds. Full units6461pass (6422 retained plus39), zero failures/skips,
8minutes11seconds. Affected consumers512pass, zero failures/skips.
Each netstandard asset passed64 cases and each modern asset128, split by mode. Existing384
consumer branches retain equivalent expectations; new128 cover both modes, sync/async, TNS/OCP,
ordinary/private pooled/unpooled paths, prewarm, zero-budget idle reuse and Clear/restored policy.
Independent source/fixture/consumer review found no blockers; reviewer did not build/test. README and
the current compatibility table were synchronized after review; older historical checkpoints remain.

Local nupkg verification used `eng/Verify-Packages.ps1 -VerifyNetFramework`: both packages passed
identity/license/readme/dependency checks and fresh-cache SHA512/asset-selection checks. Actual modern
hosts8.0.32/9.0.21/10.0.13 passed; netstandard2.0/2.1 ran on10.0.13, and netstandard2.0 additionally on
.NET Framework4.8.10008.0 (CLR4.0.30319.42000,Release534296). This is an API/configuration/dependency
smoke, not a real database or cross-platform compatibility test. Retained artifacts are under
`TestResults/package-validation/c6dd89427a9040cab9924760ebad358c` and
`TestResults/local-completeness/address-blacklist-*`; they are ignored, not published.

No real database accessed. Discovery loopback cases still share one Windows/.NET10host; real OCP,
OBProxy, enterprise TLS/plugin, other platforms and load acceptance remain open. All23IDs/eight work
packages and the remaining development remain open. Local commits only; no push.

## 2026-10-26 Discovery group TCP attempt ceilings

Developed from `2a2e139` on `codex/local-driver-completeness` for P1-09/W4.
ADDRESS_LIST.OBLB_RETRY_ALL_DOWNS counts actual TCP attempts per group and physical establishment,
across eligible/fallback phases and retry cycles. It cannot exceed the shared ceiling or logical Open
deadline. Omitted limits preserve existing behavior; a local limit alone adds no blacklist or cycles.
All-zero group budgets suppress prewarm replenishment but still permit valid idle reuse below minimum.
No public API, dependency, automatic SQL replay or active-connection migration is added.
See [contract](obconnector-dotnet-architecture-design.md#discovery-blacklist-and-attempt-ceiling) and
[implementation evidence](obconnector-dotnet-completeness.md#1564-2026-10-26-发现分组-tcp-尝试上限).

Full units: 6422 pass (6382 retained, 40 new), zero failures/skips. Release build: zero warnings/errors,
52.02 seconds. The initial 34 cases had 1 pass/33 failures; independent review found the all-zero idle
prewarm defect, reproduced by four additional RED cases and repaired in both warmup paths.
Final affected consumers: 384 pass (256 retained, 128 new), zero failures/skips, split by asset:
netstandard2.0/2.1 each 48; net8/9/10 each 96. Original expectation failures and the isolated test-output
missing-dependency setup failure remain in ignored `TestResults/local-completeness/group-limit-*` logs.
Two expectation corrections account for independent prewarm counters and persistent nonpooled blacklist
history. The obsolete serial run was canceled after diagnosis; corrected cases run by asset.
Independent source/fixture review approved the final behavior and assertions; reviewer ran no tests.

All five consumer assets use one Windows/.NET10 host; they do not establish independent runtime,
platform, real database, OCP/OBProxy, TLS/plugin or load compatibility. No real database was accessed.
Address-level blacklist overrides and the other work packages remain open; all 23 IDs/eight packages
remain open. Local commits only, no push.

## 2026-10-26 Development checkpoint after f71a979

当前功能代码已本地提交至 `master/f71a979`，没有待提交的功能代码；本次只更新进度文档。
从2026-10-24目标开始至该提交，累计68次本地提交：44项功能、17项修复、4项专项验收、3项文档。
这是提交数量，不是完整驱动完成率；23个大缺口和八个工作包仍未整体关闭，本次进度提交不计入上述快照。

| 状态 | 当前结果 |
| --- | --- |
| 最近已交付 | `f71a979`：TNS/OCP分组黑名单整块覆盖/继承、有效策略历史隔离及共享TCP尝试上限；此前REF CURSOR结果限制见`fa5d47f`，批次结果限制见`7976fb3` |
| 已验证 | 上述功能提交的完整单测6382通过（原6344全部保留、新增38），相关单测230通过；公共消费者256通过（原128保留、新增128），均无失败/跳过；Release构建0警告/错误，独立源码及最终证据复核无剩余阻断 |
| 当前停止点 | 下一模块仅完成只读源码核对，尚未编写实现或新增测试；不能记为已开发、已验证或已提交的新能力 |
| 明确待开发 | REF CURSOR的Input/InputOutput和其余结果形状、复杂类型LOB/cursor成员、批处理rewrite/multi-send/bulk等仍未补齐；具体范围沿用完整性文档各ID/工作包 |
| 仍欠验收 | 完整池/事务隔离和故障矩阵、真实OCP/OBProxy/企业TLS、独立运行时/跨平台、长期负载及发布门槛；五资产同一Windows/.NET10host的通过不能替代这些验收 |

只读核对确认：OracleCommandPlan仍明确拒绝输入/输入输出REF CURSOR；ComplexType布局和字典映射
仍未接入BLOB/CLOB成员。Java参考的复杂LOB读取存在路径，但不能据此推定.NET双向绑定/所有权
已经实现，或推定全部服务器形状可用。后续需要完成协议、会话/事务归属、取消及资源释放的整条路径。
此前原始TCP探测只证明已配置实库端点在首握手头前关闭；具体服务端/端点/中间网络原因未确定，
不计SQL、游标或真实互操作通过。详细已交付证据见[完整性15.63](obconnector-dotnet-completeness.md#1563-2026-10-26-发现分组黑名单覆盖)。

本次为文档更新，不重跑构建或测试；检查文档差异、链接/锚点、23ID/八工作包和历史保留后本地提交，
不推送。完整驱动目标尚未完成。

## 2026-10-26 Discovery group blacklist overrides

Developed from master/fa5d47f for P1-09/W4. ADDRESS_LIST.OBLB_BLACKLIST replaces the entire
DESCRIPTION policy; omitted fields use NORMAL/TIMEOUT50 defaults, including an empty block.
Omitted blocks inherit. Local-only policies default the shared TCP ceiling to120 while other groups
remain policy-free; an explicit root ceiling alone still enables the default global policy.
Only groups with an effective blacklist participate in fallback. Threshold requeues stay inside
the current group; frozen ordering, shared actual-TCP cap/backoff/deadline and generation isolation
remain. Equal policy values plus exact endpoint share history, different policies isolate it,
and success clears only its effective policy. No new API/dependency or SQL replay.
Fixed Java6adc311 explicitly leaves local blacklist parsing disabled; this is a .NET-defined capability.
See [contract](obconnector-dotnet-architecture-design.md#discovery-blacklist-and-attempt-ceiling),
[full evidence/history](obconnector-dotnet-completeness.md#1563-2026-10-26-发现分组黑名单覆盖) and
[audit](obconnector-dotnet-driver-audit.md#58-2026-10-26-发现分组黑名单覆盖).

New units38, related230 pass; final consumers256pass (prior128 retained plus128), full units6382pass
(prior6344 retained plus38), all0fail/skip. Final Release0warnings/errors,00:00:04.90; seven-file scoped
format exits0 with existing workspace-load warnings. Frozen529source/project and1069active src/tests
ReleaseDLL hashes exclude historical copies. Original14RED,88pass/6missing-User-ID test failures and
corrected24pass remain. Independent read-only source/fixture review found no blockers; the reviewer
did not build/test. Final independent read-only review confirmed raw TRX, retained cases, frozen hashes, file/history/ID/link scope and real-acceptance limits; no blockers remain. One evidence-link minor was corrected.
Five assets run on one Windows/.NET10host; controlled HTTP and loopback peers do not prove real
OCP/network HTTP, database/OBProxy/enterprise TLS, independent runtimes, platform or load acceptance.
Address-level blacklists/group-local ceilings, other discovery/fault/release gates and all23IDs/eight
packages stay open. Earlier entries preserve their original historical boundaries. Local master only.

## 2026-10-26 REF CURSOR result limits

Developed from master/7976fb3 for P1-05/W5. OceanBaseRefCursor now accepts SingleRow and SingleResult,
including their combination with SequentialAccess/CloseConnection. It reuses the ordinary reader's
limits and cursor paging: SingleRow exposes one row, later Read/NextResult drains remaining pages;
SingleResult retains every row. Fetch size, parent ownership, original timeout/token, validation,
nonunknown fetch failures and early-close session destruction remain unchanged. SchemaOnly, KeyInfo
and unknown bits still reject before consuming the single-use cursor. Input/InputOutput remains open.
See [contract](obconnector-dotnet-architecture-design.md#ref-cursor-result-limits) and
[full evidence/history](obconnector-dotnet-completeness.md#1562-2026-10-26-ref-cursor-结果限制).

New units84; related220 pass. Related consumers370 pass (prior280 retained plus90),0fail/skip.
Full units6344 pass (prior6260 retained plus84,8m26),0fail/skip. Final Release solution0warnings/errors,2.71s; five-file scoped format exits0 with the
existing workspace-load warning. Frozen source/project527 and active src/tests ReleaseDLL1069 exclude
historical probe/saved-build copies. Incremental independent source/fixture review has no blockers;
final independent read-only review confirmed raw reports, retained cases, hashes, history and scope
with no blockers. The reviewer did not run builds/tests. First84fail include6 fixture-gate timeouts after unsupported flags;
78 fail with unsupported flags/type mismatches. Final GREEN102 and affected220 retain these cases.

Real12 initially failed during profile lookup because the fixture indexed the profile name to one character;
that enumeration is corrected. Corrected12 all fail in initial Open/HandshakeFlow70 before greeting,
so no new real cursor SQL/fetch evidence exists. No-opt configuration9pass/cursor5NotExecuted; skips
are not passes. Five assets share one Windows/.NET10host. No new real OBProxy/enterprise TLS,
independent-runtime/platform/load acceptance. All23IDs/eight packages stay open; local master only.

## 2026-10-26 Batch result limits and standard SingleRow semantics

Developed from master/27bd176 for P1-05/W5. Ordinary command SingleRow now exposes only the first row
of the first result, as required by CommandBehavior. The previous per-result first-row behavior was a
defect; callers relying on it must remove SingleRow and control per-result reads explicitly. Empty or
count-only first results do not cause a search for later rows.

Modern batches now support SingleRow/SingleResult across the whole batch. NextResult consumes hidden
results and remaining entries sequentially, returning false only on success. Counts, outputs, hidden
errors and cancellation/unknown attribution remain intact. Read=false only finishes the current result;
call NextResult to process remaining entries. Early Close still skips unstarted entries. Metadata flags
never execute targets; source-owned connections and CloseConnection lease checks remain. No implicit
transaction, SQL rewrite, replay, dependency or public API addition.
See [contract](obconnector-dotnet-architecture-design.md#batch-result-limits) and
[full evidence/history](obconnector-dotnet-completeness.md#1561-2026-10-26-singlerowsingleresult-跨批次结果限制).

New units62; focused218 pass. New public consumers132 pass; combined294 retain previous162,0fail/skip.
Metadata8 simultaneously set both limits, varying sync/mode/KeyInfo rather than separately enumerating
the limits. First24RED and corrected test-import build failure are retained. Full units6260 pass
(prior6198 retained plus62,8m12),0fail/skip. Final Release solution0warnings/errors,2.40s; seven-file
scoped formatting exits0 with the existing workspace-load warning. Source/project526 and ReleaseDLL1266
frozen hashes remain unchanged. Final independent read-only review checked raw TRX, retained cases, hashes and scope with no blockers;
the reviewer did not run builds/tests. Five assets share one Windows/.NET10host. No new real database,
OBProxy/enterprise TLS, independent-runtime/platform/load acceptance. All23IDs/eight packages stay open.
Local master commit only; no push/publication.


## 2026-10-26 Batch metadata and data-source batch command creation

Developed from master/d7a5354 for P1-05/W5. Modern batches now accept SchemaOnly and KeyInfo through
the existing command metadata planner and same-session catalog exchange. The batch retains its reserved
session until reader close; early close skips unstarted entries. SchemaOnly never executes the target or
reads input values. Hidden columns, failure attribution, cancellation and original-lease CloseConnection
are covered. SingleRow/SingleResult and procedure/output/multi-statement metadata remain unsupported.

Public consumers exposed another concrete gap: source.CreateBatch().CreateBatchCommand threw from the
runtime's private DbBatchWrapper. The source now returns the provider batch owning its connection.
All execution forms open/close the owned lease; source timeout/ambient behavior and unsupported external
Connection/Transaction/Prepare remain. No new public type, package or runtime configuration.
See [contract](obconnector-dotnet-architecture-design.md#batch-metadata) and
[full evidence/history](obconnector-dotnet-completeness.md#1560-2026-10-26-batch-元数据与数据源条目创建).

New unit coverage51 and public consumers72; affected units269 and consumers162 pass,0fail/skip.
Full units6198 pass (prior6147 retained plus51,8m7),0fail/skip. Final Release solution0warnings/errors,2.39s;
four-file scoped whitespace verification exits0 with existing workspace warning. Source/project524 and
ReleaseDLL1266 frozen hashes are unchanged. First12 metadata RED, first public36pass/36source-creation
failures and later fixture failures remain recorded. Final read-only evidence/contract review independently checked reports, retained names and frozen hashes; no blockers remain. The reviewer did not run builds/tests. Three modern assets share one
Windows/.NET10host; no new real database/OBProxy/enterprise TLS/platform/load acceptance. All23IDs/eight
work packages remain open. Local master commit only, no push/publication.


## 2026-10-26 collectible protocol diagnostic state repair

The pool-handoff default gate led to a separate confirmed diagnostic unloading defect. Serial test
collections still aborted after160 cases. A standalone source repro then demonstrated another ALC's
static objects being corrupted after an old diagnostic thread exited. Real five-asset child-process
regressions yielded5controlPASS/5thread-exitRED; replacing collectible ThreadStatic with ThreadLocal<bool>
made the same10GREEN while retaining physical-thread synchronous reentry suppression and old-ALC collection.
The isolated probe is built with the solution and invoked by normal consumer tests, without new packages.

The original632 selection plus new10 passes642 in default.NET10.0.12 settings,0fail/skip,2m37.
No tiering/PGO/OSR disabling or test exclusion. Full units:6147 passed,0failed/skipped,8m11.
Final Release solution0warnings/errors3.33s;8C# scoped format verification exits0 with existing workspace
warning. Ordinary-user restore corrected the diagnostic mixed-cache analyzer failure; earlier NuGet
audit/network and build failures are retained. Final DLL/source-project hashes are frozen.
Final read-only review independently checked reports, retained names and frozen hashes; no code, fixture,
contract or evidence blocker remains. The reviewer did not execute builds or tests.
See [complete evidence](obconnector-dotnet-completeness.md#1559-2026-10-26-可卸载诊断线程状态损坏与默认回归恢复).

The following pool-handoff section preserves the pre-diagnostic-repair failure checkpoint. The confirmed
TLS-slot corruption and restored default matrix do not identify every historical native fault or prove
independent runtime/server/platform acceptance. All23IDs/eight packages remain open; no push/publication.

## 2026-10-26 P0-01 pool notification cancellation handoff

Developed from master/ffceec8. A notified renter canceled before the next pool decision used to
strand another waiter despite an idle session or free creation slot. EndRent now checks availability
under the existing gate and notifies the next waiter. Selection/reservation still enforces capacity;
no FIFO promise or new option. Ordinary/private pools and mixed sync/async callers share the fix.
See the [contract](obconnector-dotnet-architecture-design.md#pool-wait-handoff) and
[complete evidence and open runtime issue](obconnector-dotnet-completeness.md#1558-2026-10-26-连接池通知后的取消交接).

Default full units:6147 passed (24 new); default handoff consumers:64 passed; related units:982 passed.
Release solution:0 warnings/errors (11.67s); scoped5C# format verify exits0 with existing workspace warning.
Independent review found a fixture cleanup short circuit, repaired using concurrent drains and finally
cleanup. The prior4+8RED and same12GREEN remain; original-pool full-selection comparison yields
568pass/64expected handoff failures.76DLL/522source-project hashes match final tested artifacts after
restoring the temporary original-pool comparison. No product runtime setting or dependency added.

The combined632 default consumers remain failing: three CLR aborts after263/256/225 passes; crash
collection aborts after261 with an access violation/dump; disabling only TieredPGO still aborts after374.
No new handoff cases ran in those five aborted runs. Isolated route10 and combined reader/route/config110
pass. The same632 pass only in the diagnostic child process with DOTNET_TieredCompilation=0; this does
not repair or waive the default-runtime gate. Dump evidence includes ExecutionEngineException and a
JIT-related thread, without an exception stream proving that thread caused the fault. Root remains
unknown under P2-05/W8. Local preservation of this independently tested pool fix is not release approval.
All five assets run on one.NET10.0.12host; no new database/OBProxy/enterpriseTLS/platform/load acceptance.
All23IDs/eight packages remain open. Logs/TRX/dump in ignored TestResults/pool-wakeup-handoff need archiving.

## 2026-10-26 P2-04 protocol frame diagnostics

Developed from master/e0d1d30. DiagnosticListener OceanBaseConnector.Protocol reports metadata-only
Frame/Error/Boundary dictionaries across MySQL, compression and OB20, including handshake and pool
reuse. ChannelId is scoped to the listener instance. Read headers are observed before validation/body;
write events mean next-stream acceptance, not delivery. No raw bytes, SQL, authentication, user/endpoint
or exception objects/messages are emitted. Observer failures are isolated, Activity.Current restored,
same-thread recursion suppressed; disabled events allocate no dictionary. See the
[public contract](protocol-diagnostics.md) and [module evidence](obconnector-dotnet-completeness.md#1557-2026-10-26-协议帧诊断).

首轮最终全量6122通过/1失败，失败为LOB夹具把30位小数的NUMBER声明成scale0；输出行解码提前拒绝，
客户端销毁连接与server尾部EOF/OK写入竞态。原始失败及同产物4项调查通过均保留，后者不证明路径正确。
新增精确错误断言得到3通过/1失败，确认原用例走命令初始化错误而非LOB amount校验；夹具按数值写入实际
scale后，93项LOB/诊断相关回归通过。仅修正两处测试文件，不吞IO错误、不放宽产品数值校验。

夹具修正后另一轮全量6122通过/1失败，为既有DriverDiagnostics off分配断言观测98352bytes；
该Start调用不经过新协议诊断路径，相关产品/原测试未修改。同冻结产物下分配3项及完整诊断相关192项
均通过，但不足以证明该次异常分配的根因或修复；原始报告保留，零分配断言不放宽，负载/分配调查仍开放。

第三轮全量6122通过/1失败，为异步CloseCallbackScope立即GC的弱引用断言；同产物整类18项调查通过。
源码机制允许Task完成内联恢复测试时，最外层清理生产者栈尚未退出；未采集GC root栈，不能把该解释
当作已证明的具体根。测试改为5秒预算内让出执行并重复GC，保留最终弱引用断言及scope/context KeepAlive。
临时故障注入保留清理字段，4项均在预算后失败；原产品源码逐字节恢复后89项相关回归通过。产品释放逻辑
未改，故障注入不在交付源码中；该检查证明有界等待仍检出持续字段泄漏，不证明生产内存/负载预算。

最终完整单测6123通过（新增67），相关五资产消费者341通过（新增170），均零失败/跳过；LOB/诊断专项93通过，scope/诊断相关89通过及三轮各4项scope通过。
Release全解决方案0警告/错误（6.84秒）；11C#限定格式验证退出0，保留既有workspace加载警告。
独立源码/契约/证据走查无未处理阻塞发现；最终单测期间76DLL及36源码/项目哈希不变；消费者341通过使用的26项provider/hostDLL哈希与最终产物相同。
五资产消费者在同一.NET10host运行，不代表五个独立运行时；无新增实库、真实OBProxy/企业TLS、跨平台或生产负载验收。

Initial6RED showed missing events. Review found externally closed read suppression and misclassified
buffered continuation faults; actual4continuationRED and corrected4client-only cancellationRED retained.
The first cancellation fixture accidentally accepted the server listener's Error; its original4pass
remain with4continuationfail and are not counted as diagnostic proof. An additional2boundaryRED/65pass
led to boundary-error observation without changing payload/close behavior. Local fixtures/consumers do
not establish real database/OBProxy/TLS, independent runtime or production load acceptance. Other
resource waits/performance and all23IDs/eightpackages remain open. Local master only, no push/release.


## 2026-10-26 P1-06 binary Oracle cursor columns

Developed from master/75d3751. Binary prepared cursor columns now return OceanBaseRefCursor, retain
their parent statement after completed-reader/command disposal, release skipped siblings, and preserve
nested ancestors until published descendants finish. All descendants share4096 distinct IDs. KeyInfo
original/rewritten statements transfer ownership; SchemaOnly stays metadata-only. Wrong typed access
and covariant arrays do not publish a handle before failing. Full parent response consumption is required
before child fetch; disposing an active command preserves cancellation. See [contract](obconnector-dotnet-architecture-design.md#oracle-cursor-valued-columns)
and [module evidence](obconnector-dotnet-completeness.md#1556-2026-10-26-oracle-二进制游标列与嵌套资源所有权).

A positive parent command FetchSize is accepted as a hint, but prepared cursor-valued columns
use cursorFlag=0 and stream the parent response without parent FETCH. Child cursor page sizes remain
independent. UseCursorOffset rejects this shape after final prepare metadata, before parameter upload
or target execution; this also applies to KeyInfo rewriting. SchemaOnly ignores these hints as before.
This follows the fixed Java source policy and does not prove real-server support for SELECT CURSOR.

最终完整单测6056通过（新增76），相关五资产消费者561通过（新增160），均零失败/跳过；父FetchSize修复后相关218通过。
单测8分20秒，消费者1分18秒。Release全解决方案0警告/错误（14.66秒）；15C#限定格式及验证退出0，保留既有workspace加载警告。
独立源码/契约/证据走查无未处理阻塞发现；76DLL及25源码/项目哈希在两项最终门禁期间不变。
五资产消费者在同一.NET10host运行，不代表五个独立运行时；新实库入口1失败于greeting前，未执行SQL。

Initial decoder8RED, root budget and ancestor-dispose regressions, KeyInfo/output/typed publication
findings, original malformed expectations and first80consumer fixture failures remain archived locally.
The fixed Java public ResultSet specialized branch supplies binary four-byte source evidence, not SQL
acceptance. The new real cursor-column integration entry was run directly from its built DLL: oracle-direct1failed/0skipped before greeting in Open/HandshakeFlow70; no SQL ran. The initial project command executed no tests because its restored package imports point to the restricted account; its empty exit0 is not passing evidence.
Text cursor columns, REF CURSOR Input/InputOutput and other shapes/real deployment gates remain open;
all23IDs/eightpackages stay open. Localmaster only, no push/publication. Earlier sections are history.

## 2026-10-26 P1-09 discovery blacklist and TCP ceiling

Developed from master/888bd1c. Built-in TNS/OCP DESCRIPTION accepts explicit global OBLB_BLACKLIST and OBLB_RETRY_ALL_DOWNS. NORMAL/RETRYDURATION count individual failures with millisecond windows; TIMEOUT uses seconds. Either field defaults absent ceiling120 and NORMAL/TIMEOUT50; with neither field supplied, prior behavior is preserved. One frozen group order, threshold requeue inside its group, one unfiltered fallback and optional additional cycles share an actual-TCP ceiling and total logical Open deadline. Skipped blocked entries do not consume this .NET cap; exact Java candidate-counter parity is not claimed. See [contract](obconnector-dotnet-architecture-design.md#discovery-blacklist-and-attempt-ceiling), [full evidence](obconnector-dotnet-completeness.md#1555-2026-10-26-发现黑名单与tcp尝试上限) and [audit](obconnector-dotnet-driver-audit.md#50-发现黑名单与tcp尝试上限2026-10-26).

Cap0 skips minimum-pool replenishment but still validates/probes and can reuse an existing valid idle session below minimum; no idle means zero-TCP failure. A later nonzero cap restores normal replenishment. Independent review identified this priority conflict; depleted-prewarm regressions retain its RED. Cache ownership isolates policy/pool generations and late feedback, with1024 entries/16384 failure events; eviction forgets whole-entry history and may permit extra probes. TCP success clears matching history before terminal greeting/TLS/auth/identity. No active migration or SQL replay.

最终完整单测5980通过（新增101），相关五资产消费者738通过（新增128），均零失败/跳过；Release全解决方案0警告/错误（17.80秒），16C#限定格式及验证退出0，保留既有workspace加载警告。单测11分47秒，精确消费者15分30秒；五资产同一.NET10host运行，没有新增真实OCP/网络HTTP、数据库或独立运行时验收。

Original44RED/44GREEN/state33 and expanded reports are retained; intentional legacy policy-name/argument expectation changes and wrapped malformed-discovery errors are documented in15.55. Five assets share one.NET10host; controlled HTTP and loopback peers are local evidence, not actual OCP/network HTTP, real database/OBProxy/enterpriseTLS or independent-runtime acceptance. Local overrides/other discovery policies, real fault/load/deployment/release gates and all23IDs/eightpackages remain open. Localmaster only; no push/publication. Earlier entries retain historical boundaries.

## 2026-10-26 P1-09 discovery groups and selection

Developed from master/1416c55. Built-in TNS/OCP snapshots now preserve declared groups, ROTATION/RANDOM/SERVERAFFINITY, per-group overrides, ON defaults, aliases and bounded weights through actual physical connection selection and pool prewarm. Public Resolve strings remain declaration membership; idle pool reuse is based on membership, not rebalancing. Health is scoped within groups and to the current pool generation; TCP retries reuse one frozen selection. See [contract](obconnector-dotnet-architecture-design.md#discovery-group-selection), [evidence](obconnector-dotnet-completeness.md#1554-2026-10-26-发现分组策略与权重) and [audit](obconnector-dotnet-driver-audit.md#49-发现分组策略与权重2026-10-26).

最终完整单测5879通过（新增91），相关五资产消费者610通过（新增256），均零失败/跳过；Release全解决方案0警告/错误（12.86秒），12C#限定格式验证退出0，保留既有workspace加载警告。单测9分40秒，精确消费者8分15秒；五资产同一.NET10host运行，没有新增真实OCP/网络HTTP、数据库或独立运行时验收。

消费者门禁最初筛选误包含整个Ob20ConsumerTests类；仅列举测试确认实际2587项（该类2029项而非目标TCP重试52项）后主动取消，session89911退出1且确认无遗留testhost，不计为通过。原取消日志、列表及consumer-scope-correction.json保留。纠正为精确建连重试方法后，运行前仅列举确认610项，复用同一冻结产物；不是因静默日志或观察超时而重启。

RED32/health4/alias2 and the252pass/2timeout retry-fixture report are retained. The fixture budget was corrected from8to30seconds; production budgets did not change. Independent review corrected Framework clock-seeded per-selector Random to a default shared locked source. Three intentional legacy policy-test name mappings are documented in15.54; their parameter rows remain and now distinguish supported policies, malformed groups and unsupported options. Five assets share one.NET10host; controlled HTTP/loopback peers are not real OCP/network HTTP/database, enterprise TLS or independent-platform evidence. Blacklist/discovery retry policies, proxy topology/reroute, real faults/load and release acceptance remain open. All23IDs/eightpackages remain open; localmaster only, no push/publication. Earlier entries below are historical module evidence.


## 2026-10-26 P1-09 OCP HTTP discovery

Developed from master/5cbe5c9, P1-09/W4. All five assets expose OceanBaseOcpAddressResolver with a caller-owned HttpClient, explicit HTTP(S) origin, net service name and optional expected SERVICE_NAME. Each logical Open fetches a fresh fixed-route OCP snapshot before idle reuse. The request escapes appName and carries no database credentials. See [contract](obconnector-dotnet-architecture-design.md#ocp-http-discovery), [full evidence](obconnector-dotnet-completeness.md#1553-2026-10-26-ocp-http地址发现) and [audit](obconnector-dotnet-driver-audit.md#48-ocp-http地址发现2026-10-26).

HTTP200, UTF-8 JSON, 1MiB actual bytes, 16 value levels/4096 value nodes, exact NET_SERVICE_NAME and optional exact SERVICE_NAME metadata are enforced. One TCP address group preserves order/duplicates and existing256/65536 limits; explicit policies/weights/blacklists/multiple groups fail. The driver has no HTTP retry/cache/stale fallback. The caller controls HTTP authentication/TLS/proxy/handlers and must disable automatic redirects; final-URI rejection cannot undo a followed redirect. Send/body cancellation is passed through, but arbitrary handlers and long bounded CPU scans are not forcibly interrupted. HttpClient.Timeout with ResponseHeadersRead does not alone bound body consumption.

最终完整单测5788通过（新增101），相关五资产消费者354通过（新增64），均零失败/跳过；Release全解决方案0警告/错误（14.57秒），5C#限定格式验证通过且无诊断。单测9分14秒，消费者7分20秒；五资产同一.NET10host运行，没有新增真实OCP/网络HTTP或实库互操作验收。

Original platform-reader failures (__type mapping, numeric syntax, truncated JSON and trailing commas), exact-node fixture correction and raw numeric whitespace RED remain recorded. The replacement strict bounded parser has a313-fragment syntax comparison against test-only JsonDocument, not exhaustive grammar proof. Controlled HTTP handlers and loopback database peers establish local behavior only; no actual OCP/network HTTP, enterprise trust, Linux or independent-runtime acceptance is claimed. Full OCP/TNS policies/groups/weights, proxy topology/reroute, faults/load and release acceptance remain open. All23IDs/eightpackages stay open; local master only, no push/publication.

## 2026-10-26 P1-09 TNS file discovery

Developed from master/9dc3f06, P1-09/W4. All five assets expose the immutable OceanBaseTnsAddressResolver for explicit UTF-8 file/alias discovery through the existing address-resolver API. Each logical Open reloads the file before idle pool reuse. Standard/wrapped descriptions, aliases/comments/quotes, TCP IPv4/IPv6 and optional exact SERVICE_NAME file-metadata binding are supported. Unsupported policy/group/weight/include/protocol options fail explicitly. See [contract and examples](obconnector-dotnet-architecture-design.md#tns-file-discovery), [complete evidence](obconnector-dotnet-completeness.md#1552-2026-10-26-tns文件地址发现) and [audit](obconnector-dotnet-driver-audit.md#47-tns文件地址发现2026-10-26).

Limits are 1MiB actual UTF-8 bytes, 16 descriptor levels, 4096 combined nodes/aliases, 256 addresses and 65536 canonical output characters. Publish complete replacements; in-place writes have no snapshot guarantee. Only file-open FileNotFound or Win32 sharing/lock conflicts retry, at most five attempts with four cancelable 25ms waits. Persistent conflicts still fail; no stale fallback. The 100ms configured waits do not bound blocked native IO. Pre-cancel/wait-cancel tests do not prove comprehensive mid-read cancellation. Windows sharing tests do not establish Linux acceptance.

最终完整单测5687通过（新增67），相关五资产消费者290通过（新增64），均零失败/跳过；Release全解决方案0警告/错误（23.62秒），5C#限定格式验证通过并保留既有workspace加载警告。单测9分13秒，消费者6分57秒；五资产在同一.NET10host运行，无新增实库互操作验收。

Original missing-type, PORT injection, oversized-alias allocation, blank quoted-name and file replacement failures are retained. File.Move failure and transient File.Replace sharing/missing observations also occurred outside the sandbox; bounded opening retries address those transient observations without promising continuous availability. Five assets share one .NET10host. No new real database/OBProxy/network-filesystem acceptance is claimed. OCP, richer TNS policies, proxy reroute, enterprise trust, faults/load, independent platforms/runtimes and release acceptance remain open; all23IDs/eight packages stay open. Local master commit only, no push/publication.

## 2026-10-26 P1-09 endpoint TCP health cache

Developed from master/bc84ba1. Host Recheck Seconds (default0,0..3600seconds) propagates through every asset's builder/copies/serialization/pool identity. A bounded1024 monotonic failure cache stably puts recent TCP failures last, retaining all candidates and duplicates as fallback. Selection is frozen across each physical Open's retry rounds. Only pre-Attach IOException/SocketException with an uncanceled budget records a failure, including final attempts; TCP success clears it before handshake. Greeting/TLS/auth/identity/SQL/cancellation do not poison reachability. See [contract](obconnector-dotnet-architecture-design.md#endpoint-tcp-health), [complete evidence](obconnector-dotnet-completeness.md#1551-2026-10-26-端点tcp健康缓存) and [audit](obconnector-dotnet-driver-audit.md#46-端点tcp健康缓存2026-10-26).

Pool/private-source/unpooled-selector lifetimes own their caches; ordinary matching borrowers and prewarm sessions share pool state. Pool Clear, credential retirement and Dispose replace pool-owned caches so late old feedback cannot affect a new generation, including reserve-to-constructor races. Pool APIs do not clear unpooled selector-owned hints, which follow selector lifetime. Empty ordinary pool removal also removes health state. There is no global map or active migration/replay.

最终完整单测5620通过（新增71：健康缓存69、池身份2），相关五资产消费者226通过（新增52），均零失败/跳过；Release全解决方案0警告/错误，12C#限定格式验证通过并保留既有workspace警告。五资产同一.NET10host消费；本模块没有新增实库互操作验收。

受限首轮完整单测5480通过/140失败：109为PFX导入、5为Schannel凭据、20为VerifyFull预期不匹配、5为silent TLS等待超时、1为既有异步拓扑超时；相关消费者146通过/80失败，80项均在TestCertificateFactory的PFX导入。相同10个TLS/清池用例普通用户环境通过后，保持源码、夹具、预算和五资产二进制不变，完整单测5620通过（9分21秒）、同226消费者通过（6分31秒）。所有初次失败均同名通过，原报告不删除；拓扑超时原因尚未确认，不声称修复其根因。完整5549单测基线及174消费者基线全部保留。报告位于忽略目录TestResults/endpoint-health，执行日志为根目录TestResults-endpoint-health-*.log，应单独归档。

Initial18unknown-keyRED,2pass/16preferenceRED,84/126GREEN and6expected epoch-mutation failures retained; restored source15targeted checks passed. The portable elapsed-budget test blocks the first TCP metric callback for1200ms with timeout1: local deadline-before-feedback evidence, not actual network TCP timeout or Linux execution. Existing real reports/probes remain historical; this module adds no real database interoperability. OCP/TNS/equivalent adapter, proxy topology/reroute, real health/fault/load, enterprise trust, independent runtimes/platforms and release acceptance remain open. All23IDs/eight packages remain open; local master only, no push/publication.


## 2026-10-25 P1-09 bounded TCP connection retry

Developed from master/0ec3e1c. Connect Retry Count (default0,0..10 additional complete candidate rounds) and Connect Retry Interval (default1,1..60seconds) propagate through builder/copies/pool identity. Full-round TCP failures alone wait/retry; TCP success makes greeting/TLS/auth/identity/command failures terminal. Positive count shares the logical Open deadline across pool wait/prewarm, attempts and backoff; zero retains existing waiting semantics. Resolve/credential snapshot once per logical Open. Caller cancellation/Close interrupt backoff, and subsequent explicit Open can reclaim capacity. No active-session reconnect, transaction migration or SQL replay. See [contract](obconnector-dotnet-architecture-design.md#connect-retry-policy) and [retained evidence](obconnector-dotnet-completeness.md#1550-2026-10-25-tcp-建连重试预算与退避).

最终完整单测5549通过（新增64：重试61、池身份3），相关五资产消费者174通过（新增52）；Release全解决方案及修正后消费者构建0警告/错误，7C#限定格式验证通过并保留既有workspace警告。五资产在同一.NET10host消费；无凭据/无SQL探针5022ms读0greeting，本模块没有新增实库互操作验收。

Original6unknown-keyRED,2pass/4TCPRED,70/113GREEN,4warmupRED,125focused121pass/4Listen-before-Accept fixture failures retained. Shared logical budget fixes the four no-callback MinPool2 warmup cases; listening TCS corrects the four peer scripts without relaxing product assertions. Original build/fixture/NuGet and UTF8 script failures retained in the complete module record. Five assets share one.NET10host; no real-server/OBProxy/independent-runtime/load/release acceptance claimed. Full23IDs/eightpackages remain open; local master only, no push/publication.


This page retains historical results and open work from the former progress ledger, alongside dated verification of later changes. Local regression results and real database acceptance are recorded separately.


## 2026-10-25 P0-05 connection-level server errors

Developed from `master/917e9cc`, P0-05/W2 with P0-02/P0-04 boundaries. Connection-level ERR handling now retires failed sessions and preserves the current request's outcome semantics. See the [error and recovery contract](obconnector-dotnet-architecture-design.md#connection-level-server-errors) and [full module record](obconnector-dotnet-completeness.md#1549-2026-10-25-连接级服务器错误与显式连接重建).

Final complete units: 5,485 passed, including 67 new cases. Affected consumers: 172 passed, including 80 new cases across five assets on one .NET10 host. Release build: zero warnings/errors; six changed C# files pass scoped format verification with the retained workspace-load warning. Without integration opt-in, nine configuration cases passed and two transaction cases were not executed.

The first full unit run had 5,484 passes and one existing partial-cursor fixture failure. Its server terminator write hit a reset before accepting the replacement, which then timed out waiting for a greeting. The fixture now finishes writing the page before the partial branch closes; row two and the terminator remain unread, and isolation assertions remain. The original report is preserved; this does not establish the cause of earlier historical timeouts. The first consumer run had 142 passes/30 failures because the Oracle fixture wrongly required a time-zone probe before transaction controls; optional-probe handling corrected it, without product changes or relaxed assertions.

The four selected real transaction regressions all failed before the initial greeting. An independent no-credential/no-SQL TCP probe also received zero greeting bytes after 5,035ms. No real connection-level ERR fault was exercised, and neither this local ERR matrix nor historical database passes establish final real-server acceptance. Full reset, server/proxy/coordinator failures, enterprise trust, load, independent runtimes and package/release acceptance remain open. All 23 gap IDs/eight work packages remain open; no push or publication.


## 2026-10-25 P0-01 Closed callback pool cleanup

Developed from `master/80ddfbe` for P0-01/W1. A pooled Open or OpenAsync inside a Closed state handler completes the captured close chain before borrowing, including nested callbacks and another logical borrower. Pool capacity, captured ownership, early Closed events, external asynchronous rollback interleaving and old-reader generation guards remain in force. Completed scopes release their captured delegates; asynchronous cleanup also releases its lifted local delegate before publishing completion. See the [contract](obconnector-dotnet-architecture-design.md#pooled-close-callbacks) and [full evidence](obconnector-dotnet-completeness.md#1548-2026-10-25-closed-回调中的满池重开与清理资源释放).

The final full unit run passed 5,418 cases, and all 233 selected consumer cases passed. The consumer selection includes the original 173 cases, 40 new callback cases and 20 existing reader lease cases. The earlier 173-case selection omitted those last two groups. There are 92 new unit cases; two existing TLS timeout cases intentionally migrate to successful reuse of the same capacity-one pool. Release build: zero warnings and errors. Scoped formatting and verification of nine changed C# files passed with the existing workspace-load warning. Without integration opt-in, nine configuration cases passed and 12 cases were not executed.

The final real-server repeat failed all 74 executable cases; one case was not executed because alternateDatabase was not configured. Both profiles closed before the initial greeting. An independent TcpClient received zero greeting bytes without sending credentials or SQL. The earlier real run passed 74 cases, including the 16 new cases, but preceded the memory fix and cannot replace final-source acceptance. Historical server evidence covers OceanBase 4.4.2.2 MySQL/Oracle direct connections on Windows/.NET 10 with TLS disabled; five consumer assets share one .NET 10 host.

Original timeout regressions, the four memory failures, the intermediate three-pass/one-fail result and later four-pass result remain recorded. The first full run on final source passed 5,417 cases with one existing synchronous partial-cursor replacement handshake timeout. All 26 focused checks and the complete 5,418-case repeat passed on unchanged binaries; this does not establish a root-cause fix for the original timeout.

Other transaction-log callbacks opening their own full pool, complete reset, concurrency, load, proxy, trust, platform, runtime and package acceptance remain open. Historical nested-table column rejection, unresolved complex RETURNING identity and the unexplained RSA timing failure are retained. Full consumer, DI, package, independent-runtime and full-database suites were not rerun. All 23 gap IDs and eight work packages remain open; this is a local master commit, with no push or publication.

## 2026-10-25 P1-07 complex national members

Developed from `master/a1dddde`, P1-07/W5. Named and nested NCHAR/NVARCHAR2 members, scalar factory arrays and PL outputs use strict encoding according to direction: connection encoding for inputs, queried AL16UTF16 for stored members, and the result snapshot's session national encoding for PL outputs. See the [contract](obconnector-dotnet-architecture-design.md#complex-national-members) and [complete evidence](obconnector-dotnet-completeness.md#1547-2026-10-25-复杂类型-national-成员与方向编码). No public API, configuration or dependencies were added.

Other database national encodings, anonymous ordinary non-NULL national results without trusted storage context, and complex whole-column RETURNING with unresolved identity remain unsupported. Such RETURNING failures preserve prior output values, report an unknown execution outcome and break the session; the application must reconcile the write.

The final full unit run passed all 5,326 cases outside the sandbox with collection parallelism disabled; all 180 related consumer cases passed. The earlier full run (5,325 passed, one RSA-stage wait failed) and sandbox run (5,187 passed, 139 failed) remain recorded. The focused RSA run passed 10 cases and the TLS run outside the sandbox passed five; these repeats do not establish a root-cause fix for the original timing failure.

The selected Oracle suite passed 64 of 65 cases; the historical nested-table column case still fails at the server. The 56 new cases comprise 48 positive cases and eight RETURNING rejection guards. Without integration opt-in, 13 cases passed and 105 were not executed. The Release build had no warnings or errors; scoped format and verification of 18 C# files exited successfully with the existing workspace warning retained. Original failures, the NLS review fix and mutation evidence, and three migrated old cases remain recorded. The existing object fixture now loads its descriptor after table DDL; production freshness and identity checks remain strict.

The five consumer assets share one .NET 10 host. Real-server evidence is limited to Oracle mode on OceanBase 4.4.2.2, a direct connection, Windows, disabled TLS and database AL16UTF16. Full consumer, DI, package, independent-runtime and full database suites were not rerun. All 23 gap IDs and eight work packages remain open; delivery is a local master commit.

## 2026-10-25 P1-06 Oracle output FetchSize hint

Developed from master/a07957e. [Contract](obconnector-dotnet-architecture-design.md#oracle-output-fetch-hint), [complete evidence](obconnector-dotnet-completeness.md#1546-2026-10-25-oracle-输出执行与-fetchsize-提示), [audit](obconnector-dotnet-driver-audit.md#41-oracle-输出执行与-fetchsize-提示2026-10-25). Oracle OUT/RETURNING and procedure/function execution accept positive command FetchSize, with explicit or implicit preparation. Internal output rows remain in the execution response (cursorFlag0, no FETCH); returned REF CURSOR readers keep their independent page size. Output offset and ExecuteArray positive FetchSize still reject before network traffic. Ordinary query paging and MySQL procedure restrictions remain intact; no new API/config/dependencies.

Original24RED all hit the old positive-hint rejection. Initial26GREEN, expanded57focused. Added46unit/30consumer/20real cases, preserving old cases. Final5280core unit, affected510consumer and expanded58real passes, no failures/skips; noopt13pass/101NotExecuted. First44real omitted ordinary ReaderTests; final58 includes both-mode cursor and Oracleoffset. Release build0warnings/errors; seven-file scopedformat/verify0 retains workspace warning. Independent source review found no actionable findings; reviewer did not run gates. Raw reports and originalRED remain under TestResults/oracle-output-fetch/ and TestResults-oracle-output-fetch-*.log.

New real20 cover Oracle4.4.2.2 direct sync/async, prepared/implicit, block/RETURNING/procedure/function/cursor; three returned cursor rows use page1 despite commandhint2, after command disposal. Selected58 include existing MySQL/Oracle cases, Windows/.NET10/TLSdisabled. Five asset consumers run on one.NET10host. No full consumer/DI/package/independent-runtime/full-database rerun; proxy/trust/platform/full fault matrices open. Internal output pagination and REF CURSOR inputs/columns remain unsupported, all23IDs/eightpackages/P1-06/W5 remain open; prior reports and failures retained. Local master commit only, no push/publication.

## 2026-10-25 W2 transaction acknowledgement loss acceptance

Developed from master/4c15e3c, tests/docs only. [Contract](obconnector-dotnet-completeness.md#521-提交回滚响应丢失与应用核对), [complete original failures and evidence](obconnector-dotnet-completeness.md#1545-2026-10-25-事务响应丢失与池恢复验收), [audit](obconnector-dotnet-driver-audit.md#40-事务响应丢失与池恢复验收2026-10-25), [application reconciliation](unknown-outcome-recovery.md). New16 local and16 real cases cover both modes/sync-async/commit-rollback/none-partial reply loss. A plaintext local relay reads the real server response before dropping it; an unpooled observer verifies actual data. Client unknown outcome, broken-session discard, stale transaction rejection/disposal, Max1 same-endpoint fresh lease and ordinary autocommit/rollback work before Clear. Target counts across sessions exclude automatic replay and account for the fresh transaction's expected ROLLBACK.

Original two compile casts and relay-first2 failures retained: variable hex escape emitted0x3c, corrected fixed03 gives2pass. Reviewer identified stopped-listener Accept race in original failure; localized cancellation-aware Accept handling plus two positive/negative cases gives4fixturepasses, no deferred findings. Reviewer did not run gates. Guide strengthening initially used a nonexistent Enlist property; final compilation CS0117 retained. Corrected to BCL TransactionScope.Suppress with async flow; extracted final snippet compiles0warnings/errors on.NET10. No full application/coordinator execution claimed.

Final full units5234pass (16added/0removed), related expanded suite160pass/4configurationNotExecuted INCLUDING4local relay cases, noopt13pass/100NotExecuted. Original5218units/123relatedcases preserved;16new real/4new fixture classified separately. Release build and guide compile0warnings/errors; four-file format+verify0 with existing workspace warning. Real scope direct4.4.2.2 dual-mode/Windows/.NET10/TLSdisabled, fault after actual server response only. Real server/coordinator crash, full send/fault/concurrency/OBProxy/trust/platform matrices remain open. No consumer/DI/package/independent-runtime/full-database rerun. All23IDs/eightpackages and prior history preserved; local master commit only, no push/publication.

## 2026-10-25 P0-02 external persistent LOB pool lifecycle acceptance

Developed from master/a426623; tests/docs only, no production/API/dependency change. [Contract](obconnector-dotnet-completeness.md#518-readerlob-与-ref-cursor-的剩余验收), [full failures and evidence](obconnector-dotnet-completeness.md#1544-2026-10-25-外置持久-lob-读取与池生命周期验收), [audit](obconnector-dotnet-driver-audit.md#39-外置持久-lob-读取与池生命周期验收2026-10-25). Four direct Oracle cases return actual external locators (BLOB100000/CLOB220000 UTF8 bytes), match exact content, release all handles and reuse the same physical session with a new generation and working SQL. Independent unpooled preparation frees its temporary seed before the pooled SELECT; no global threshold changes.

Eighteen local cases interrupt an actual pending DBMS_LOB.READ: caller cancellation, Close, handle/stream disposal and original-command timeout, BLOB/CLOB and sync/async where supported. Same-endpoint MaxPoolSize=1 replacement authenticates and executes SQL before ClearPool; old resource cleanup cannot affect it. Original caller token and nonunknown classification retained. First14pass/4fail were an incorrect OCE expectation for Close; source-backed correction18pass retained, no product fix. Independent review identified dropped unobserved-read diagnostics and a weak timeout bound; aggregate cleanup plus two regressions and a five-second completion bound address both. Reviewer did not run gates.

Final full units5218pass (pre-review-adjustment5216 retained separately), scoped real Connections/XA/REFCURSOR/LOB119pass/4configNotExecuted, noopt9pass/99NotExecuted. Prior5198unit/119related-real cases retained,20new local/4new real, all original Close failures same-name passes. Release solution0warnings/errors; four-file format+verify0 with existing workspace warning. Real evidence remains direct4.4.2.2/Windows/.NET10/TLSdisabled, new casesOracle only. Real in-flight fault/cancel, concurrency/load/OBProxy/trust/other-platform matrices remain open. No consumer/DI/package/runtime/full-database rerun; historical2918consumer/14DI remain history. All23IDs/eightpackages and prior evidence preserved; local master commit only, no push/publication.

## 2026-10-25 P0-02 persistent LOB and REF CURSOR pool isolation acceptance

Developed from master/6c45173; tests and documentation only, no production/API/dependency changes. [Contract](obconnector-dotnet-completeness.md#518-readerlob-与-ref-cursor-的剩余验收), [complete failures and evidence](obconnector-dotnet-completeness.md#1543-2026-10-25-持久-lob-与-ref-cursor-的同端点池隔离验收), [audit](obconnector-dotnet-driver-audit.md#38-持久-lob-与-ref-cursor-池隔离验收2026-10-25). Twenty persistent BLOB/CLOB and eight REF CURSOR phase cases pass locally and against real Oracle. Same-endpoint MaxPoolSize=1 proves live LOB discard and fully disposed read-only LOB reset/reuse; stale operations/disposal preserve the next lease. The unpooled setup observer also verifies exact stored bytes/text after rejected writes/truncation. REF retirement additionally follows the opaque-call policy; it does not isolate the cursor counter or prove reuse after cursor disposal.

Original local24pass/4fail retained: partial socket closure may reset, complete reader closure queues IDs until explicit cursor disposal. Exact reset/EOF and close-packet expectations corrected, then28pass; no product fix. First real8pass/20fail retained for the fixture's11-vs12-byte seed mistake, corrected28pass. Reviewer required full content beyond length; observer check added and reviewed. Final full units5198pass, noopt Integration9pass/98NotExecuted, related realConnections/XA/REFCURSOR/LOB115pass/4configNotExecuted. Prior5170unit/91real cases preserved;28new local/28new real, original failures final same-name passes. Release solution and final observer integration builds0warnings/errors; two-file format+verify0 with existing workspace warning. Independent review no remaining actionable findings; reviewer did not run gates.

All20realLOB locators were inline=True. Local CLOB partial reads may prefetch; external DBMS_LOB.READ/chunk cancellation, full fault/concurrency/load and other deployment matrices remain open. Direct OceanBase4.4.2.2/MySQL+Oracle/Windows/.NET10/TLSdisabled only, new resource casesOracle only. No consumer/DI/package/runtime/proxy/enterpriseTLS/full-database rerun; historical2918consumer/14DI not fresh evidence. Original15.42/audit37 checkpoints and all history remain. All23IDs/eight packages stay open; local current-branch commit only, no push/publication.

## 2026-10-25 complete audit documentation and resource acceptance checkpoint

Current committed baseline: master/6c45173. This update delivers documentation only: [complete capability and gap baseline](obconnector-dotnet-completeness.md), [audit summary](obconnector-dotnet-driver-audit.md#37-完整走查文档与资源验收状态复核2026-10-25), and [exact evidence/checkpoint](obconnector-dotnet-completeness.md#1542-2026-10-25-完整走查文档交付与资源验收检查点). All 23 gap IDs, eight work packages, source/test entry points, acceptance criteria and historical failures/skips remain. Java reference HEAD and pooling classes were checked; both drivers have pools. Full resource isolation and deployment acceptance remain open.

Existing TRX reports were read, not rerun: last committed module units5170pass, consumers2918pass, DI14pass, no-opt-in Integration9pass/96NotExecuted, scoped real87pass/4NotExecuted. The pre-existing untracked PooledResourceIsolationTests.cs is preserved without edits; its first report has24pass/4fail, exit1. Persistent LOB20pass; REF CURSOR8 contains4pass(unopened/disposed) and4fail(partial/complete, sync/async), with handshake timeout/peer closure symptoms. Root cause is unconfirmed; this is neither a shipped resource acceptance module nor a confirmed product defect. See the [remaining resource matrix](obconnector-dotnet-completeness.md#518-readerlob-与-ref-cursor-的剩余验收).

This documentation work checks local links/anchors, three23-ID tables, unique ownership in eight packages, preserved history, UTF-8-no-BOM/CRLF and diff. No product/test edits, fresh build/full test/database/package/runtime runs, commit, push or publication. Full-driver completion is not claimed; next implementation work must first diagnose the four failures and retain the original report.

## 2026-10-25 P0-02 pooled reader CloseConnection lease isolation

Developed from master/00b3976. A completed CloseConnection reader could close a later logical lease when Close/Open reused the same physical session. The fix captures the existing logical Open generation under the connection lock at command start or Batch reservation and checks it with session identity in conditional closure. All four reader sync/async close paths are covered; public Close remains unconditional. See the [contract](obconnector-dotnet-architecture-design.md#closeconnection-reader-ownership), [full evidence](obconnector-dotnet-completeness.md#1541-2026-10-25-旧-reader-延迟关闭与池租约隔离) and [audit](obconnector-dotnet-driver-audit.md#36-旧-reader-延迟关闭与池租约隔离2026-10-25).

Original local4 and real4 REDs are retained. Eight new local cases include the deterministic Batch reservation-release callback window; twenty new public consumers prove same-socket reuse across five assets. Controlled removal of the generation guard fails all four Batch cases, then restores the source bytes. Initial fixture CS0266, a zero-match filter and consumer10pass/10Oracle-timezone-fixture failures remain recorded. Independent review found no remaining production issue after fixing task/error cleanup.

Final Release solution build: zero warnings/errors. Full units 5,170, full consumers 2,918, DI14 pass; no-opt-in Integration9pass/96NotExecuted; real Connections+XaTests+RefCursorTests+LobTests 87pass/4configuration skips. Scoped ten-file format passes with the existing workspace-load warning. Consumer assets remain frozen during execution and run on one .NET10 host. Real tests cover direct OceanBase4.4.2.2 MySQL/Oracle, Windows/.NET10, TLS disabled only. Full resources/concurrency/load, proxy/enterprise TLS, independent runtimes, package and release gates remain open, along with all23IDs/eight packages. No push/publication.

## 2026-10-25 P0-02 pooled XA isolation acceptance

Developed from master/ffadc7a; test/documentation changes only. [Contract](obconnector-dotnet-completeness.md#516-xa-专用会话的池隔离), [complete evidence](obconnector-dotnet-completeness.md#1540-2026-10-25-xa-池隔离与-prepared-分支恢复验收), [audit](obconnector-dotnet-driver-audit.md#35-xa-池隔离与-prepared-分支恢复验收2026-10-25). Existing production behavior passed all new cases without a product change: 28 local cases cover seven phases, socket destruction without implicit completion, MaxPoolSize=1 replacement before clearing and all twelve stale-resource APIs. Twenty direct cases prove dedicated discard after idle/commit/rollback, replacement autocommit/local rollback, and prepared branches surviving Close for explicit recovery and completion.

Full unit first and final: 5,162 passed, no failures/skips. Connections + XaTests first and final: 76 passed, 4 configuration skips. No-opt-in Integration: 9 passed, 95 NotExecuted. Release solution/integration builds: zero warnings/errors; scoped format passes with existing workspace warning. Independent review required safer failure cleanup before owner disposal, independent unpooled rollback, failure aggregation and awaiting the wire peer; all addressed, no actionable findings remain. Direct OceanBase 4.4.2.2 / Windows / .NET 10 / TLS disabled only. Consumer, DI, package and runtime sample suites were not rerun; prior results remain historical. Response loss, coordinator crash, real active/suspended disconnect recovery, full resource/concurrency/proxy/trust/platform acceptance and all 23 IDs/eight packages remain open. No push/publication.

## 2026-10-25 P0-02 Oracle autocommit and real pool isolation

Developed from master/4810907. [Public contract](obconnector-dotnet-architecture-design.md#oracle-autocommit-baseline), [complete retained evidence](obconnector-dotnet-completeness.md#1539-2026-10-25-oracle-自动提交与真实池隔离), [audit](obconnector-dotnet-driver-audit.md#34-oracle-自动提交与真实池隔离2026-10-25). Oracle Open confirms SET after identity; reset confirms it after COM_RESET_CONNECTION before generation advances. Strict replies, setup metadata, cancellation/Close/timeout destruction and pool capacity recovery preserve the MySQL reject-only login contract. One extra round trip per Oracle physical Open/successful reset is documented.

Open18/reset18/readonly4 RED and original compile/fixture/PFX/format/validator errors retained. Full unit first5130pass4fixturefail; related8pass after exact Oracle SET fixture replies, then full5134passed (76added/0removed). Full consumers2898passed, focused90;20new five-asset public rejection/replacement cases. DI14passed; nooptinIntegration9pass93NotExecuted. Realpool18pass4fail1skip before,22pass0fail1skip after; finalConnections+OracleNumber52pass4skip on direct4.4.2.2/TLSdisabled/Windows/.NET10. Independent source/coverage/consumer/contract review has no remaining actionable findings; reviewer did not execute gates. Release+consumer builds0warnings/errors; scoped format0 with existing workspace warning. Eleven frozenDLLs unchanged; two local packages each5DLL+documents match, dependency groups unchanged. Two raw-wire samples each run on .NET8.0.31/9.0.20/10.0.12 (6smokes), not full runtime suites; five product assets still share the.NET10 consumer host. No independent nupkg consumer/fresh-cache dependency restore/online audit/push/publication. All23IDs/eightpackages and full resource/XA/concurrency/deployment acceptance remain open. Original RED checkpoint retained below.

## 2026-10-25 completeness documentation and Oracle autocommit RED checkpoint

Current committed baseline is master/4810907. The [complete capability baseline](obconnector-dotnet-completeness.md) and [audit report](obconnector-dotnet-driver-audit.md) retain all 23 gap IDs, eight work packages, source/test entry points and acceptance criteria. The unknown-precision NUMBER fix is committed; its original verification remains historical evidence.

Oracle fresh unpooled and post-reset implicit writes remain invisible to an observer in the retained real-server runs. Initialization is missing from both identity completion and Oracle reset; the new sync/async handshake regression ran 18 cases, all failed (2 missing-SET fixture timeouts, 16 expected-error assertions). There is no production fix yet. Shared identity fixture edits and the restored real pool suite are uncommitted; the cleaned integration suite built but has not been rerun. Reset failure/cancellation/timeout, framing, capacity recovery, final regression and independent review remain pending. See the [precise defect and intended acceptance](obconnector-dotnet-completeness.md#515-oracle-自动提交基线已确认缺陷与待实现契约) and [current documentation checkpoint](obconnector-dotnet-completeness.md#1538-2026-10-25-完整走查文档与-w1-red-检查点).

This documentation update checks links/anchors, three 23-ID tables, unique assignment to eight work packages, retained history and UTF-8-no-BOM/CRLF/diff. It does not rerun full tests, real-server acceptance, packaging or publishing and does not close the driver-development goal.

## 2026-10-25 P1-08 unknown-precision Oracle NUMBER results

Developed from master/5ea71b2. [Contract](obconnector-dotnet-architecture-design.md#oracle-number-results-with-unknown-precision), [retained failures and next W1 work](obconnector-dotnet-completeness.md#1537-2026-10-25-oracle-未知精度-number-结果读取), [audit](obconnector-dotnet-driver-audit.md#32-oracle-未知精度-number-结果与-w1-新发现2026-10-25). Unknown-precision Oracle results preserve exact server values and NUMBER range checks; known precision, MySQL and parameters retain strict constraints. Schema stays descriptive; coefficient/value-scale representation can change (40 becomes40/0 instead4/-1).

New numeric RED6fail/3pass, focused84pass; first full unit5057pass/1initialTCPtimeout preserved. Routing recheck5 and full repeat5058 pass without budget or product transport changes. Affected consumers40pass (new five-asset result cases5; not full suite), DI14pass, no-opt-in Integration9pass/86NotExecuted; real Oracle4pass direct4.4.2.2/TLSdisabled/Windows/.NET10 with owned-table cleanup. Build0warnings/errors; scoped format passed after fixing existing InlineData whitespace, workspace warning retained. Independent review and consumer follow-up found no actionable defect. No packages, publication, OBProxy, TLS or independent-runtime acceptance added. Oracle fresh/reset implicit-write failure remains open P0-02/W1, with original pool reports and draft saved; next work is autocommit initialization and full borrower isolation. All23IDs/eight packages and the full driver goal remain open.

## 2026-10-25 P2-04 slow command diagnostics

Developed from master/b2fce9d. [Contract](obconnector-dotnet-architecture-design.md#slow-query-diagnostics), [original failures and evidence](obconnector-dotnet-completeness.md#1536-2026-10-25-慢命令诊断), [audit](obconnector-dotnet-driver-audit.md#31-慢命令诊断2026-10-25). Slow Query Threshold (milliseconds, default0) covers completed execute/cursor.fetch lifetimes including reader consumption; independent Warning1102, four-tag counter and existing-span mark contain no SQL/values/credentials/error text. Exchange timing ends before cleanup/callback work; legacy operation telemetry remains unchanged.

Original25RED, CS0246/CS1729 fixture compile errors, first-green160pass6fail and focused-user173pass7fixturefail retained. Corrected per-invocation elapsed pool-wait assertion preserves timeout/capacity/reuse; target replies now preserve time-zone state. Focused-green184passed. Independent review no confirmed actionable defects or deferred minors; existing elapsed-wait assertion correction endorsed. Final unit5049passed(217.0s), consumer2873passed(762.0s),0failures/0skips; DI14passed, no-opt-in integration9pass84NotExecuted. Complete-focused184/consumer-focused25/real-focused12passed; real4.4.2.2 direct MySQL+Oracle/TLSdisabled/Windows/.NET10/SELECTonly. Releasebuild0warnings/errors; scopedformatverify0 with existing workspace warning. Unit44added/consumer10added,0removed by testName/testId; all original failed cases map to final same-name passes. Frozen11DLL unchanged/predate consumer launch; packages2x5DLL+README/LICENSE/NOTICE match workspace and unchanged dependency groups. Six public docs preserve3x23IDs/eightpackages,15.1–15.35/audit12–30 history,379local links/anchors,UTF8-noBOM/CRLF/diff. No new-cache restore, independent nupkg consumer, online vulnerability-audit result or publication; five assets on one.NET10 host do not prove independent runtimes. Reviewer declined execution/deployment/load/final-doc/package claims and infinitely blocking callbacks; execution evidence and public limits remain separate. Full P2-04/23 IDs/eightpackages and deployment/runtime acceptance remain open.

## 2026-10-25 P2-04 connection phase diagnostics

Developed from master/d6a9169. [Contract](obconnector-dotnet-architecture-design.md#connection-phase-diagnostics), [original failures and verification](obconnector-dotnet-completeness.md#1535-2026-10-25-建连阶段诊断), [audit](obconnector-dotnet-driver-audit.md#30-建连阶段诊断2026-10-25). A fixed-label seconds histogram covers actual credential/discovery/TCP/greeting/TLS/authentication/identity and pool wait/probe attempts; existing operation telemetry remains unchanged. Linked-token cancellation and unsignaled waits retain distinct phase semantics; final Open classification remains authoritative.

Independent review found wait duration included cleanup listener work. Two actual sync/async RED cases now pass after stopping the clock before cleanup and publishing afterward. Original static initialization, fixture/compilation and sandbox PFX failures are preserved. Normal-user early focused suite78 passed. Final Release build exit0,0warnings/errors; scoped whitespace verification exit0 with existing workspace-load warning. Full consumer2863passed (732.6s); first full unit5003pass/2timeoutfail and isolated6pass retained, then full unit5005passed (208.5s) after consumers completed, same binaries and no relaxed/excluded cases; original timeout cause remains unconfirmed. DI14passed; no-opt-in integration9pass/83NotExecuted; real direct MySQL+Oracle diagnostics8passed on4.4.2.2/TLSdisabled/Windows/.NET10. Unit42added/consumer5added,0removed by name/ID; every original unit failure maps to a final same-name pass. Frozen11DLL hashes/timestamps unchanged and predate consumers. Two local no-build/no-restore packages each contain exact5DLL+README/LICENSE/NOTICE and unchanged dependency groups. No independent nupkg consumer, new-cache restore, online vulnerability-audit result or publication; five assets on one .NET10 host do not prove independent runtimes. Six public docs retain3x23IDs/eightpackages, prior15.1–15.34/audit12–29; links/encoding/history/diff verified. No other confirmed defects or deferred minors. P2-04/23 IDs/eight packages remain open.

## 2026-10-25 P1-06 Oracle standalone Prepare

Developed from master/6b357f0. [Contract](obconnector-dotnet-architecture-design.md#oracle-standalone-prepare), [complete original failures and final evidence](obconnector-dotnet-completeness.md#1534-2026-10-25-oracle-独立-prepare), [audit](obconnector-dotnet-driver-audit.md#29-oracle-独立-prepare2026-10-25). Prepare/PrepareAsync now retain a real server statement for Oracle OUT/RETURNING/procedure/function commands without target execution, input encoding, stream consumption or output publication. The next execution transfers ownership to the existing cursor owner; output cursors outlive command disposal. Later implicit execution prepares afresh; positive output FetchSize and unsupported cursor directions/columns remain gaps. DML split metadata accepts only exact pure-OUT layouts, and array requests carry retained IDs.

Original initial12RED, RETURNING4RED, PL split2RED and array4RED are retained. Test-data profile-character and follow-up/piece fixture errors are separate. Independent review found a disposal race and missing DML negatives: deterministic lock contention reproduced NullReferenceException, shared context/capture/transfer is now under the command gate; three INOUT/wrong-column/wrong-total negatives kill a deliberate weakened-guard mutation. Both Important findings were addressed and full suites passed. The first full unit run4957pass/2fail was an obsolete standalone-rejection expectation, revised to TableDirect rejection; names and IDs were preserved, with supported preparation covered positively.

Final commands/logs use TestResults-oracle-standalone-prepare-*.log and TestResults/oracle-standalone-prepare/*.trx (ignored; archive separately). Release solution build --no-restore: exit0,0warnings/errors. Scoped dotnet format whitespace --verify-no-changes: exit0 with workspace-load warning retained. Full unit-final4963 and consumer-final2858 passed,0failures/0skips, normal user context; consumer12m23s. DI14passed; no-opt-in integration-final-with-arrays9passed/82NotExecuted. Real-final-with-arrays20passed on Oracle4.4.2.2 direct/TLSdisabled/Windows/.NET10, including no DML during preparation, post-disposal cursor reads and explicit-prepared arrays with transaction rollback. Additional Integration test-only build used BuildProjectReferences=false while consumers ran; core/DI/consumer outputs were not rebuilt.

Comparison by testName/testId: unit48added/0removed, consumer60added/0removed; all original failed unit cases map to same-named final passes, and the deliberate mutation is labelled separately. Eleven prelaunch DLL hashes/timestamps stayed unchanged and predate final consumer launch. Two --no-build/--no-restore local packages each contain exact five DLLs plus current README/LICENSE/NOTICE; dependency groups match the previous module. No publication, independent nupkg consumer, new-cache restore or online vulnerability-audit result is claimed. Six public documents retain all23IDs/eightworkpackages, historical15.1–15.33/audit12–28 and earlier progress entries; link/anchor/encoding/diff checks pass. Full driver, real OBProxy/TLS/other-version and independent-runtime acceptance remain open.

## 2026-10-25 complete audit documentation at 6b357f0

Documentation-only refresh against committed master/6b357f0. The [current audit checkpoint](obconnector-dotnet-driver-audit.md#28-完整文档交付与-oracle-独立-prepare-检查点2026-10-25) and [capability checkpoint](obconnector-dotnet-completeness.md#1533-2026-10-25-完整文档复核与-oracle-独立-prepare-检查点) retain all 23 gap IDs, three tracking tables, eight work packages and historical evidence. Current summaries distinguish missing code, restricted combinations and missing acceptance; no complete gap is closed.

The Oracle standalone Prepare test file remains untracked and unchanged. Its existing initial-red.trx contains 12 failures at the current explicit rejection; there is no production implementation or GREEN. Existing charset TRX counts were reread: unit 4915, consumer 2798, DI 14 passed; no-opt-in integration 9 passed/79 NotExecuted; real direct MySQL charset 6 passed. These are previous execution results, not fresh runs and not proof that the new tests pass. No build, runtime, real-server or Java test was rerun; no commit or package publication was made. Document checks cover links/anchors, IDs/packages, history preservation against fixed 6b357f0, UTF8-noBOM/CRLF and diff. Independent runtimes, real OBProxy/TLS/fault matrices and the complete driver goal remain open.

## 2026-10-25 P1-08 ordinary result character sets

Developed from master/abd0077: strict shared collation map/widths, Chinese byte validation and GB18030-2005 correction; official CodePages dependency for Standard assets and runtime support for modern assets, no global registration. [Contract](obconnector-dotnet-architecture-design.md#result-character-sets), [full original failures and final evidence](obconnector-dotnet-completeness.md#1532-2026-10-25-普通结果字符集). Binary63, JSON/CLOB, trusted national rules and client UTF8 scope remain explicit.

Reviewer reproduced GBK illegal80/FF and CP54936 mapping differences; actual2RED each retained and fixed. Independent full-table comparison covered latin1, GBK and1,087,996valid GB18030 four-byte values. Initial84RED and public-reader64fixture failures retained; type9 corrected Oracle Int32 fixture, product conversion unchanged. Related524pass;128public reader and160five-asset consumer cases. Final Minor test-cleanup observation corrected without expanding product encoding APIs.

Real MySQL-direct4.4.2.2 six cases pass, each8combinations on Windows/.NET10/TLSdisabled. Preserve UTF16/LE direct SET1231/42000 failures; final NULL-result-charset/source-expression path confirms string metadata/exact text. Retain Int64 fixture correction and intermediate test-source compile failure. No Oracle/proxy/TLS/other-version/full-ID or independent-runtime charset acceptance.

Final full units4915pass/0fail/0skip after test-cleanup fix; DI14pass; no-opt-in Integration9pass/79individualNotExecuted; complete consumers2798pass/0fail/0skip in13m36s, exit0. TestName/testId multisets confirm160consumer additions/no removals and422unit additions/1explicit collation8→65535 negative-case replacement, net421. All original84+2+2product REDs and64fixture failures map to same-named final passes. Eleven prelaunch product/DI/Standard-consumer DLL hashes/timestamps remain identical, all modifications predate original and final consumer starts. Unit and Integration test projects alone were rebuilt for cleanup/fixture corrections; no product or consumer rebuild during full consumers.

Release solution and final test-only builds0warnings/errors; scoped format exits0 with retained workspace warning and original whitespace diagnostics. Independent final review has no remainingCritical/Important/Minor. Two local no-build packages each have5exact-output DLLs plus exact current README/LICENSE/NOTICE. Eng content/dependency validation passes (StandardCodePages/modernruntime); its separate fresh-cache restore/nupkg consumer portion was not run. Preserve initial temporary-script PSScriptRoot failure and incorrect all-assets dependency assertion, both tooling assumptions corrected. Document validation confirms344local links, three23ID tables/eightworkpackages, unchanged historical completeness15.1–15.31/audit12–26 against fixedabd0077. No push/publication. See result-validation.json/document-validation.json for machine checks.

Normal online restore failed NU1900/NU1301/SEC_E_NO_CREDENTIALS; offline cache restore uses per-command NuGetAudit=false, online audit remains unverified. Sandbox full tests fail Windows PFX key import; original failures and normal-user rechecks retained, no product TLS/certificate bypass. All23gap IDs/eightworkpackages and complete-driver acceptance remain open. Reports/logs are ignored local evidence under TestResults/character-sets and TestResults-character-sets-*.log; preserve separately for handoff.

## 2026-10-25 P0-01 ordinary pool configuration normalization

Implemented from master/c2fe307: effective typed/default configuration identity, numeric PAM factors with quoted-empty presence, original-builder Open/Close/failure/ClearPool snapshots, idempotent registry clearing, unchanged reference/fingerprint/private-source boundaries and public serialization. [Contract](obconnector-dotnet-architecture-design.md#ordinary-pool-configuration-identity), [full record](obconnector-dotnet-completeness.md#1531-2026-10-25-普通池配置归一化).

Independent review found the public empty-factor collision; actual4REDs fixed. Close discard2REDs fixed by canonical detach snapshot. Two new callback cases expected immediate reopening but were revised to require shared capacity/finite timeout/finally-return and reuse after Close, not counted as a product RED-to-GREEN fix. Existing Closed logger and rollback event order is preserved. Same-full-pool synchronous reopening inside Closed can wait indefinitely with Pool Wait Timeout=0; defer reopening until Close completes. Incremental source review has no remaining actionable finding, no reviewer-run broad tests.

Final build0warnings/errors; related292 and full units4494pass, DI14pass, no-opt-in Integration9pass/78individualNotExecuted. Real pool6pass, dual-mode Connections26pass/3missing-profile-capability skips. Complete consumers2638pass/0fail/0skip in11m30s; unit/consumer name-and-ID multisets confirm80actual additions each/no removals. Original failures and two explicit callback-contract revisions are mapped in result-validation.json. No rebuild/restart during that run. Local no-build packages each contain5matching DLLs and exact README; scoped format exit0 retains workspace warning and initial6whitespace errors. Initial snapshot glob16-versus11 assertion corrected early in the consumer run; final11hash/timestamp values match that early-run sample, and all modification times precede full consumer startup. The sample is not described as a prelaunch freeze. All reports/logs under ignored TestResults/pool-configuration and TestResults-pool-configuration-*.log. All23gaps/eightpackages and full driver acceptance stay open.

## 2026-10-25 complete audit documentation at c2fe307

The [current handoff](obconnector-dotnet-driver-audit.md#25-完整走查文档交付与池配置身份2026-10-25) consolidates the committed master/c2fe307 baseline, all 23 gap IDs and eight work packages, source/test entry points, remaining features, scope decisions and acceptance gates. MySQL procedure execution is committed; the older 1bb13cb entries identify its development base. Historical failures/skips and module records remain intact.

P0-01/W1 configuration normalization is not implemented. A no-network probe of the existing net10.0 DLL confirms separate registry pools for equivalent key order, explicit default Port and Boolean spelling; a Host/Server alias control still shares one pool. Capacity and ClearPool consequences follow the source identity path; no live capacity or active-clear claim is made. [Detailed finding and acceptance criteria](obconnector-dotnet-completeness.md#513-普通池配置身份尚未归一化) preserve credentials, policy references and private-source boundaries.

Existing TRX reports were re-read, not rerun: units4414, consumers2558 and DI14 Passed; Integration9 Passed/76 NotExecuted; applicable real MySQL74 Passed/5 NotExecuted. Java source HEAD was rechecked at the fixed 2.4.14 reference. This update changes documentation only, adds no product/test implementation, and runs no build/full-suite/pack/Java/live-server/platform matrix. Only the local identity probe and document checks are new evidence. No commit, push or publication; complete-driver acceptance stays open.

## 2026-10-25 P1-05 MySQL stored-procedure execution

Implemented from master/1bb13cb: ordered Input/Output/InputOutput through private prepared SET, prepared CALL and a hidden output SELECT under one exchange. Complete one-row response precedes atomic output publication; errors preserve prior values and never replay. Explicit Prepare and Input streams are supported; effective PS_MULTI_RESULTS is required. ReturnValue, FetchSize, SchemaOnly/KeyInfo and INOUT streams reject. Outputs use actual SELECT metadata. Reader close drains remaining procedure results/output; batch close preserves final counts without executing the suffix. Procedure sessions retire on return; CALL remains command-owned outside idle cache. [Contract](obconnector-dotnet-architecture-design.md#mysql-stored-procedures), [full evidence](obconnector-dotnet-completeness.md#1530-2026-10-25-mysql-存储过程执行本地交付).

Original failures and the checkpoint below remain historical. Review found batch early-close counts and DEFINER grammar; fixes gained wire regressions. Final incremental review found closed-reader RecordsAffected overwriting a reexecuted BatchCommand: four dual-mode sync/async actual REDs (expected5/actual2), now fixed by clearing the current reader after final close-time count publication. Added initialization cancellation/disconnect/timeout and malformed hidden-output response coverage; focused67pass. Source/test review has no actionable Critical/Important/Minor; reviewer ran no tests. Document review's two stale DDL-summary references were corrected.

Final Release solution build0warnings/errors; Integration-only --no-dependencies rebuild after the null-enlistment test correction also succeeds without changing loaded DLLs. Format17C#files exits0 with workspace warning; original whitespace failure retained. Full units4414pass/0fail/0skip, actual67additions/no removals; full consumers2558pass/0fail/0skip in11m15s, actual80additions/no removals. Name/ID multisets and every original unit RED/fixture failure now passing are checked in result-validation.json. All11loaded assemblies keep frozen hashes/timestamps. DI14pass; no-opt-in Integration9pass/76individualNotExecuted. Local no-build pack exits0; both packages contain5DLLs identical to final outputs and exact README, no publication.

Default-policy real run5pass covers four procedure cases plus missing-routine1305; no AllowMultiStatements opt-in. Applicable mysql-direct suite74pass/5skip includes9configuration cases, Windows/.NET10/direct OceanBase4.4.2.2/TLS disabled. The original unfiltered73pass/20fail/5skip is retained:19Oracle-only no-data errors plus one stale EnlistTransaction(null) rejection assertion. It now tests ArgumentNullException; other stale assertions now test server1305 and unknown behavior bit128. Explicit Oracle exclusion selects applicable tests rather than claiming Oracle success. Missing capabilities account for all5skips. The original sandbox consumer40cert-import failures remain; the same80built cases pass under normal user context without driver/certificate/trust changes. Local TLS fixtures do not prove real TLS.

Artifacts are ignored under TestResults/mysql-procedure and root TestResults-mysql-procedure-*.log. Earlier MySQL60/Oracle79pass+1nested-table-column failure/cleanup failure and every historical skip remain. Five TFM assets on one .NET10host do not establish independent runtimes. P1-05/W5, all23IDs/eightworkpackages and the full-driver goal remain open for their remaining acceptance/features; no push or publication.

Final documentation review has no actionable findings; nine Markdown files/309local links and anchors validate, all three23-ID tables/eightpackages remain complete, and historical15.1–15.29/audit12–23 are unchanged. UTF8-noBOM/CRLF and git diff --check pass; existing Integration LF-to-CRLF warnings are retained.

## 2026-10-25 complete documentation and MySQL procedure development checkpoint

The [complete audit](obconnector-dotnet-driver-audit.md#23-完整文档交付与-mysql-过程开发检查点2026-10-25) and [detailed evidence](obconnector-dotnet-completeness.md#1529-2026-10-25-mysql-过程开发与完整文档检查点) separate committed master/1bb13cb from the uncommitted P1-05/W5 module. All23gap IDs/eight work packages and historical failures/skips remain. README, API, Schema and compatibility now describe the same development boundary. The earlier handoff below is a committed-baseline snapshot, not a statement that this working tree is clean.

Existing reports were parsed, not rerun: related units53pass; focused consumers40pass/40fail of80, all40failures in local TLS test-certificate PKCS#12 loading before procedure exchange. Prior real-mysql-2 has4direct-server passes, but predates latest test edits and removal of AllowMultiStatements. Latest setup/capability/behavior/retirement unit additions and two stale Integration assertion corrections remain unverified. Initial review found Batch early-close affected count and DEFINER recognition gaps; partial fixes have related passing cases, but final incremental review is pending. Prior build0warnings/errors predates latest tests. No final full-suite/pack evidence exists for this module; the DDL-module4347/2478 reports below cannot validate it.

Documentation-only work preserves implementation/test edits and all prior artifacts. No new build/test/pack/live/Java run, commit or publication. Default-policy live rerun, local TLS failure resolution, full gates, final review and package checks remain; full driver acceptance also requires the open isolation/fault/OBProxy/platform/runtime matrix. Five assets on one .NET10 host remain one host.

## 2026-10-25 complete driver documentation handoff

The [audit report](obconnector-dotnet-driver-audit.md#13-完整文档交付索引与本次复核) and [capability baseline](obconnector-dotnet-completeness.md) now use committed `master` / `1bb13cb`. They retain all 23 gap IDs, eight work packages and historical delivery/failure/skip records. The current summary explicitly includes missing MySQL StoredProcedure execution, existing resolver isolation awaiting real acceptance, and the rejected distributed ambient promotion boundary. Java pooling was rechecked against the fixed 2.4.14 source reference.

This handoff changes documentation only. Existing DDL-module TRX files were parsed again: units4347pass, consumers2478pass, DI14pass, Integration9pass/72NotExecuted; these are prior runs, not new executions. No build/test/pack/Java/live-server run or release is claimed. The full driver and all eight work packages remain open; real isolation, faults, OBProxy/TLS and independent runtime evidence remain required.

## 2026-10-25 P1-07 observed complex-type DDL invalidation

Implemented from `96f544d`: Oracle text CREATE/ALTER/DROP/RENAME/TRUNCATE target transmission advances an independent metadata epoch. Binding, dictionary publication and new-result snapshots reject stale contexts, including named/nested/factory-array descriptors; immutable returned values remain readable. Prepare and pre-send rejection preserve the cache. External/opaque DDL requires existing explicit ClearComplexTypeCache followed by reload and rebuilt bindings. [Contract](obconnector-dotnet-architecture-design.md#complex-type-ddl-invalidation), [module evidence](obconnector-dotnet-completeness.md#1528-2026-10-25-复杂类型可观察-ddl-自动失效).

Confirmed eight original REDs now enter final units4347pass/0fail/0skip, 56 actual additions/no removals. The enhanced 40 existing five-asset sync/async/OB20/local-TLS consumers pass; complete consumers2478pass/0fail/0skip (10m32s), no additions/removals. The 11 loaded assemblies retain their original hashes/timestamps. Result verification compares name/ID multisets, preserving duplicate display names and mapping all eight confirmed REDs to final passes. Release build0warnings/errors; ten-file format exit0 retains workspace-load warning; DI14pass; Integration9pass/72NotExecuted. Two local packages each contain five matching DLL hashes and exact README, no publication. Independent source/test review found no Critical/Important; a final Minor stale live-summary phrase was corrected, leaving no actionable issues. Reviewer ran no tests. Earlier test compilation, sequence-reset, exception-wrapping and post-disconnect fixture corrections are distinguished from the product RED in the detailed record.

Artifacts/logs are ignored under TestResults/complex-type-ddl and root TestResults-complex-type-ddl-*.log. Real DDL/dictionary/implicit-commit/locking/OBProxy and independent runtimes remain unverified; P1-07 retains unsupported complex members and real acceptance. All gap IDs, work packages and earlier failures/skips remain open.

## 2026-10-25 P0-03 ambient single resource and initialization cancellation

Implemented from `f15716e`: coordinator-accepted PSPE initialization, rejection of a second database resource before its local transaction SQL, and aborting the original local transaction on a later promotion request, Committed/Aborted/InDoubt outcomes and no replay. Original Open generation is checked under the transaction exchange lock; completion requires the original local transaction instance. Early close/reopen and actual same-session pool A/B callbacks remain isolated. [Contract](obconnector-dotnet-architecture-design.md#ambient-transactions), [final evidence](obconnector-dotnet-completeness.md#1527-2026-10-25-ambient-单资源事务与初始化取消本地交付). The development/document checkpoint below is historical, including its then-open cancellation defect.

Independent review found initialization generation races and automatic OpenAsync token loss. Actual dual-mode2RED and four cancellation REDs preceded fixes. Automatic Open/OpenAsync now pass the effective opening token into the synchronous Initialize exchange; public explicit enlistment retains its signature. Cancellation destroys the pending session, clears local/ambient state, releases the pool slot and preserves exact caller token. Five-asset cancellation consumers actually reopen the same connection/configuration/scope/transaction with maxpool1/wait1s, then execute SQL and verify Close/ROLLBACK/restore/reset, late rollback and ClearPool EOF. Successful Open followed by cancellation/CTS disposal still commits. Scope timeout remains distinct from immediate wire cancellation. Final independent source/test review has no actionable Critical/Important/Minor; reviewer did not run tests.

Final Release solution build0warnings/errors, nine-file format exit0 with workspace-load warning. Complete units4291pass/0fail/0skip (54 actual additions/no removals vs4237), complete consumers2478pass/0fail/0skip (50 actual additions/no removals vs2428), DI14pass, Integration9pass/72 individual NotExecuted; all exit0. No product/consumer assemblies were rebuilt during complete consumers. Final no-build/no-restore local pack exits0; two packages each have five matching DLL hashes and exact final README, no publication. Raw TRX/JSON/logs are ignored under TestResults/ambient-single-phase and root TestResults-ambient-single-phase-*.log; all three original RED reports map to final passes. The first cancellation attempt was manually interrupted due to test cleanup ordering; it is preserved separately and not counted as a normal RED/pass. Prior4285/40 are pre-cancellation history; focused73 and consumer50 passes are retained. Five assets run on the same .NET10 host; no independent runtime or new real OceanBase/OBProxy/platform/transaction-manager acceptance. All23IDs/eightpackages/full-driver goal remain open.

## 2026-10-25 complete audit and ambient development checkpoint

The complete [review document](obconnector-dotnet-driver-audit.md#20-ambient-单资源事务模块2026-10-25) and [detailed baseline](obconnector-dotnet-completeness.md#1526-2026-10-25-ambient-单资源事务开发与完整文档检查点) now distinguish committed `master` / `f15716e` from the uncommitted ambient single-resource module. All 23 gap IDs/eight work packages, original failures/skips and real acceptance limits remain open. Java source still has built-in pooling; its fixed reference HEAD was rechecked as `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`.

Working-tree PSPE rejects a second database resource/promotion before transaction SQL; confirmed/rejected/unknown COMMIT reports Committed/Aborted/InDoubt. Independent review reproduced initialization Close/Open generation races (2 RED), now checked under the transaction exchange lock; callbacks also require the original local transaction instance, with actual same-physical-session pool A/B coverage. Original6RED and race2RED remain. Final review still reports one Important: automatic OpenAsync calls synchronous enlistment and BeginAmbientTransaction uses CancellationToken.None, so cancellation cannot interrupt initialization SQL. No dedicated cancellation RED/GREEN exists yet. Scope timeout only proves eventual rollback after synchronous Initialize returns; it is not immediate wire cancellation.

Existing reports were reread, not rerun for this documentation: focused52pass, full units4285pass/0fail/0skip (48 actual additions/no removals against4237), focused five-asset consumers40pass, DI14pass, Integration9pass/72 individual NotExecuted with no real database opt-in. Existing Release build has0warnings/errors; nine-file format retains workspace-load warning. Full consumers, cancellation fix/final checks, independent approval and final package hashes/embedded README are pending. Five assets run under one .NET10 host; no independent runtime or new real OceanBase/OBProxy/platform evidence. Previous2428 full consumers/packages do not validate this module. Raw artifacts are ignored under TestResults/ambient-single-phase and corresponding root logs; separate archival is needed for handoff.

This turn completes documentation only: current source evidence, impact, minimum fix and acceptance requirements are synchronized across README, compatibility, API, audit, completeness and this ledger. Existing implementation/test edits are preserved; the module is not committed or declared delivered. Documentation validation passed: nine Markdown files/255 relative links and anchors, three complete23-ID tables, eight packages assigning each gap once, historical15.1–15.25 unchanged, UTF8-noBOM/CRLF retained; git diff --check has no whitespace errors (the existing AmbientEnlistment.cs LF-to-CRLF warning remains); no build/test/pack/live matrix is rerun for these Markdown edits.

## 2026-10-25 P2-02 bounded prepared statement cache

Implemented from `6d399fd`: opt-in Statement Cache Size=0..1024 (default0), physical-session idle LRU keyed by exact rewritten SQL. A command exclusively holds its preparation; disposal/SQL change can return it, while eviction/duplicates queue closes. Cache epoch invalidates local DDL/session/opaque target sends and successful ChangeDatabase; reset Generation and transport loss forget old IDs. Metadata probes/new SchemaOnly/KeyInfo and Oracle PrepareExecute owners keep existing lifetimes. Errors/partial metadata/count mismatch/unread responses retain close/discard rules; no replay. [Contract](obconnector-dotnet-architecture-design.md#prepared-statement-cache), [original failures and acceptance](obconnector-dotnet-completeness.md#1525-2026-10-25-跨命令-statement-cache).

Initial actual9RED for missing configuration; behavior9fail/1pass after options, initial8pass/2fixturefail, then behavior10pass/lifecycle21pass/ownership29pass. boundary41pass/2timeouts was an unread fixture omitting the prefetched first row; adding that row preserves unread terminal response/socket-destruction assertions, final focused43pass. First full build has a DbCommand.FetchSize test compile error; corrected build0warnings/errors. Five-asset consumer subset20pass. Independent source/test/contract review has no Critical/Important; one Minor wording fix distinguishes local pre-send rejection from server rejection of a sent DDL (the latter still invalidates). Reviewer did not run tests. Historical results before the newly found lease-context race (reports preserved as before-epoch-race-*): Release build0warnings/errors,14-file format exit0 with workspace-load warning; complete units4235pass/0fail/0skip, actual47 new cases/no removals, exit0. DI14pass, Integration9pass/72 individual NotExecuted, exit0. Synthetic15ms prepare delay:8 commands assert8prepares disabled vs1enabled in both modes; whole TRX test durations MySQL141.84→26.81ms/Oracle158.71→20.00ms include handshake/fixture overhead and are not real database latency. Complete consumers2428pass/0fail/0skip, exit0 in10m12s; actual20new cases/no removals, with no product/consumer DLL rebuild during the run. Five assets loaded on the same .NET10 host do not establish five independent runtimes. Local pack --no-build/--no-restore exit0; both packages/five DLL hashes each and embedded final README match. Four original RED/fixture reports map to final unit passes. These are the pre-race-fix results; they do not validate the later source change. Idle count excludes application-held commands/metadata bytes. External DDL/reprepare, real resources/load/OBProxy/platform/independent-runtime acceptance remains open; all23 IDs/eight packages and full-driver goal remain active.

Final pre-commit concurrency check reproduced two sync/async failures: A passed its early epoch check and then waited for the connection lock while B sent DDL; A incorrectly sent LONG_DATA(24) instead of a fresh PREPARE(22). InvalidatePreparedContext now rechecks Database, Generation and CacheEpoch under the acquired exchange lease in both paths; old IDs close normally and reset generations suppress stale closes. epoch-race-red.trx preserves both failures; epoch-race-green.trx has49 passes. Independent incremental review found no actionable Critical/Important/Minor and did not run tests. Final complete units4237pass/0fail/0skip, actual49 additions/no removals, exit0; complete consumers2428pass/0fail/0skip, actual20 additions/no removals, exit0. DI14pass; Integration9pass/72 individual NotExecuted, exit0. Final Release build0warnings/errors, corrected15-file format exit0 with workspace-load warning. Final no-build pack and five DLL hashes per package/embedded README match final outputs. All five original RED/fixture reports, including the race, map to final unit passes; reports are retained. No product/consumer assembly was rebuilt during the full consumer run. Evidence remains local/net10-host-only; all23 gaps/eight packages and the complete-driver objective remain open. New-test whitespace caused one format diagnostic/exit2; corrected15-file verification exits0 with workspace-load warning.

## 2026-10-25 P0-02 session-state classification and send boundary

Implemented from `4dd20a7`: mode-aware user-SQL classification and a sticky flag only at target send after cancellation. Covers commented/later statements, opaque procedure/block execution, executable comments, user variables and named locks; prepare/rejection/metadata traffic remains reusable. [Contract](obconnector-dotnet-architecture-design.md#pooled-session-state-after-user-sql) and [all original failures/review fixes](obconnector-dotnet-completeness.md#1524-2026-10-25-会话状态识别与发送边界).

Independent review found identifier/declaration false positives and expression-FROM lock-call false negatives; actual lexical/A-B REDs reproduced them and the shared classifier was fixed. Final source/lifecycle review has no remaining Critical/Important. The remaining nested-CTE name false-discard is Minor and documented, along with SQL_MODE/backslash conservative retirement. Arbitrary function/trigger effects, full resource/XA/concurrency and real reset profiles remain open; this is not full P0-02 acceptance.

Final complete units4188 pass (141 actual new cases, no earlier cases removed), DI14 pass, Integration9pass/72 individual NotExecuted, all exit0. Complete consumers2408pass (20 actual new cases across five assets/two modes/sync-async, no old cases removed), zero failures/skips and exit0; no product/test DLLs were rebuilt during the run. All five assets were loaded on the .NET10 host, not five independent runtimes. Release build0 warnings/errors; eleven-file scoped format exit0 retains workspace-load warning. Local pack --no-build --no-restore exits0; both packages contain five matching DLL hashes and the exact README (package-validation.json). All nine original failure/fixture reports map to final unit passes (red-green-validation.json), and originals remain available. The earlier complete4182 unit pass is preserved as unit-before-expression.trx and does not verify the final expression fix. No new live database, OBProxy, cross-platform or independent-runtime evidence; the complete-driver goal and all23 gaps/eight packages remain open.

## 2026-10-25 complete audit document refresh

This is the pre-classification documentation snapshot. Its source findings and no-fix/no-new-test statements describe that handoff; the later P0-02 implementation and verification are recorded above.

Source baseline is committed `4dd20a7`; the local Java reference remains `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`. The [current review handoff](obconnector-dotnet-driver-audit.md#17-完整文档交付与当前待办2026-10-25) consolidates all 23 gaps, eight work packages, implementation/verification/scope decisions, source evidence and release gates. The latest implemented module remains DataAdapter/CommandBuilder below; this refresh changes documentation only.

P0-02 source review confirms fixed-prefix session-state classification misses leading/inter-keyword comments or whitespace, later statements and opaque calls; the command also marks state before later pre-send validation and for Prepare. [Detailed cases and acceptance criteria](obconnector-dotnet-completeness.md#511-当前会话状态识别的具体缺口) are now recorded. No fix or new regression was run, and no real cross-borrower contamination is claimed: actual COM_RESET_CONNECTION restoration remains profile-specific and unverified.

Existing data-adapter TRX results were re-read: units4047, consumers2388 and DI14 Passed; Integration9 Passed/72 NotExecuted; the original temporal2RED is retained. Existing build/format logs were re-read, including the workspace-load warning. These are historical reports, not new test runs. The package manifest describes the README embedded at the earlier delivery; it does not validate a package containing this refreshed README. No build/test/pack/live database/runtime matrix was rerun. Document structure, links, 23-ID/work-package coverage and diff checks are the only new verification; all gaps and the complete-driver objective stay open.

## 2026-10-25 P2-01 DataAdapter and CommandBuilder

Implemented from base `d7e8b5a`: five assets expose typed adapters/builders and factory creation. Framework Fill/FillSchema, DataTable/DataSet mappings, AddWithKey, row states/events, original/current/null values, sequential CRUD, concurrency exceptions, AcceptChanges and ContinueUpdateOnError are reused. Generated commands inherit source connection/transaction/timeout; there is no implicit cross-row transaction, generated identity/default refresh or write replay. UPDATE/DELETE require a complete visible chosen row key; unknown shapes and context-dependent bindings need explicit commands. Builder-local metadata preserves MySQL database qualification and prevents nullable UNIQUE from becoming a row key. See the [API](obconnector-dotnet-architecture-design.md#data-adapters-and-command-generation) and [module record](obconnector-dotnet-completeness.md#1523-2026-10-25-dataadapter--commandbuilder).

Independent review_data_adapter found one Important: Scale=0 rejected fractional temporal values and invented integer scale for unknown decimals. Two genuine temporal RED failures reproduced it; ClearScaleDeclaration now clears both backing declarations before known metadata is applied. Signed decimal/null-indicator and reused-parameter tests cover the boundary. Final independent product/test review has no remaining Critical/Important; broad suites were left to the main task.

Current evidence: initial actual5RED then5pass; behavior-first81pass/2fail from a test that omitted resetting Values after GetInsertCommand; corrected behavior90pass and boundaries94pass. Five-asset consumer subset21pass includes20 new cases (five assets x ordinary/AddWithKey x internal dual-mode fixture) and1 existing factory test. Initial temporal-red compile error was corrected before actual2RED. Final complete units 4047 (including34 new cases), DI14 pass with zero failures/skips; Integration9 pass/72 individual NotExecuted. All exit0. Final Release build0warnings/errors; scoped10-file whitespace verification exit0 with workspace-load warning. Local pack with --no-build passes; both packages contain all five matching DLL hashes and the current README (package-validation.json). Final complete consumers2388pass (includes all20 new adapter cases), zero failures/skips, exit0. No product/test DLLs were rebuilt during that run. Reports/logs are under ignored TestResults/data-adapter and root TestResults-data-adapter-*.log.

Five assets loaded on the .NET10 host do not establish independent runtimes. Real dictionary/CRUD/permissions/locking/DDL/version/OBProxy/platform acceptance remains open. This module does not close all23 gaps/eight work packages or the complete-driver objective.

## 2026-10-25 P1-05 KeyInfo and hidden key columns

Implemented from base `de15ec2`: KeyInfo prepares the original target and reads a bounded parameterized key catalog in the same reserved physical session/transaction before target parameter upload/execution. Proven row-preserving single-base-table SELECTs choose a complete primary key or nonnullable unique key and append missing members as hidden tail columns. FieldCount includes them; VisibleFieldCount retains original projection size. Nullable single-column UNIQUE is distinct from a usable row key; composite members are not individually unique. Original CommandText/prepared cache stay unchanged, rewritten statements have per-execution ownership and queued closure. Complex/ambiguous shapes keep their SQL/result shape and unknown key properties. See the [API contract](obconnector-dotnet-architecture-design.md#key-information-and-hidden-columns) and [module record](obconnector-dotnet-completeness.md#1522-2026-10-25-keyinfo-键元数据与隐藏列).

Independent review_keyinfo_design found three Important issues, reproduced and fixed: Oracle constraints backed by nonunique indexes/extra physical index members and owner identity; NQ literal recognition; and execution-time expression metadata differing from preparation. Final source/test/contract review reports no remaining Critical/Important; reviewer did not run full suites. Cancellation wording was clarified to distinguish metadata-stage interruption from target-stage unknown outcome.

Final local results: complete units 4013, focused units 85, complete consumers 2368 (including all 20 new five-asset consumers) and DI 14 pass with zero failures/skips and exit 0; Integration 9 pass/72 individual NotExecuted, exit 0. Release solution build has 0 warnings/errors; scoped whitespace format verification exits 0 with workspace-load warning. No product/consumer assemblies were rebuilt during the full consumer run. This module adds 85 unit cases and 20 consumers; the complete P1-05 behavior/real-server acceptance remains open.

Evidence is under ignored TestResults/key-info and root TestResults-key-info-*.log. The original sandbox full run3891pass/117fail retains PFX/TLS, diagnostic assertion, timeout and connection errors; an overlapping build emitted MSB3026 retries. Later native-identity full4011 then final4013 pass without changes to TLS/certificate product code or weakened assertions. The initial missing Common-namespace test compile, primary4RED, parser4RED, independent-review3RED, dynamic-metadata2RED, actual Oracle catalog2RED and zero-selected filter are all retained in completeness15.22. New cursor coverage verifies hidden keys stay on statement93 through fetch; two cancellation tests prove input streams remain unread during catalog interruption.

Five assets loaded under .NET10 do not establish five independent runtimes. Real dictionary parsing/permissions/DDL/locks, versions/OBProxy, platform and package-runtime acceptance remain unverified. All23 gaps/eight work packages and the complete-driver objective remain open.

## 2026-10-25 P1-05 SchemaOnly prepare metadata

Implemented from base `557a05a`: sync/async SchemaOnly prepares the target and exposes prepared column metadata without target COM_QUERY/EXECUTE/FETCH or parameter upload. It requires a single accepted text query with input parameters; names/order/count still match, values are not bound/encoded/read, and fetch/offset hints are inactive. Prepare errors never trigger client fallback. Cached metadata is reused with current LOB/complex context; the empty reader completes its exchange, while CloseConnection preserves original-session ownership. See the [command contract](obconnector-dotnet-architecture-design.md) and [module record](obconnector-dotnet-completeness.md#1520-2026-10-25-schemaonly-元数据专用路径).

Independent review_schema_only found one Important issue: StartCommand still validated LOB input values. A disposed LOB regression reproduced it in two RED cases; passing no value-binding list for SchemaOnly fixes it. Final review, including six named/source-bound complex metadata cases and updated contracts, has no remaining Critical/Important findings.

Final local delivery: complete units3928 and complete consumers2348 pass with zero failures/skips, including all20 new consumers; focused units30, DI14 pass; Integration9 pass/72 individual NotExecuted. Build has0 warnings/errors, scoped format exits0 with workspace-load warning, local pack succeeds with all five DLL hashes and embedded README checked. The simultaneous unit-verified run retains3923 passed/5 failed across existing TCP/TLS/RSA/routing timing cases; their22-case method subset and then the full suite passed without product/fixture/assertion changes. Original failures remain and no high-load timing root-cause fix is claimed. This module adds28 units/20 consumers; it does not close all of P1-05.

Reports: ignored TestResults/schema-only and root TestResults-schema-only-*.log. Earlier invalid fixture RED, command-boundary/LOB option fixture errors, disposed-LOB RED and DbColumn.ProviderType test compile correction are retained in completeness15.20. The final fixture also fails all8 cases against the prior module package DLL in an isolated test-host copy; current DLLs pass those same cases. Final reports are unit-recheck.trx and consumer-verified.trx, both exit0. No real OceanBase/OBProxy, side-effect/lock/large-result, independent runtime, cross-platform or published package acceptance is added. P1-05, all23 gaps/eight packages and the complete-driver objective remain open.

## 2026-10-25 P1-10 routine parameter derivation

Implemented from base `2bbecac`: OceanBaseCommand.DeriveParameters and two async overloads on all five assets, with unique routine/owner/package/overload resolution, complete argument attributes, signed DecimalScale, existing complex descriptors, one reserved session/current transaction epoch, per-query timeout/cancellation and atomic collection-membership publication. It does not execute the target or add MySQL StoredProcedure execution. See the [API contract](obconnector-dotnet-architecture-design.md#routine-parameter-derivation), [mapping limits](schema.md#parameter-derivation) and [module record](obconnector-dotnet-completeness.md#1518-2026-10-25-自动参数推导修复与验证).

Final full units3900 and full consumers2328 pass, including all20 new derivation consumers; DI14 pass; Integration9 pass/72 individual NotExecuted. Release build has0 warnings/errors. Independent review_derive_design found no remaining Critical/Important after metadata identity, FLOAT/YEAR declaration and earlier mapping/cancellation/publication fixes. Public-contract review corrected the1024-row limit to routine/argument tables. Format verify exits0 with a workspace-load warning; initial58 whitespace diagnostics and fix are recorded. Local pack exits0 and five DLL hashes, embedded README bytes and five dependency groups match; no publish.

All reports are under ignored TestResults/derive-parameters; root logs TestResults-derive-parameters-*.log. identity-red retains6 passed/2 failed; declaration-red retains6 failed; consumer-first retains20 failed due to a valid final COM_STMT_CLOSE73 that the fixture omitted. Fixture correction preserves exact wire assertions; consumer-green20 and final complete consumers pass. boundary-green149 and declaration-green159 preserve the related subset runs. The earlier development checkpoint below and completeness15.17 retain their original failures/warnings and pending gates as history, not final status.

This adds81 unit cases and20 consumers. Five assets loaded in the .NET10 host are not five independent runtimes. No real catalog/privileges/declarations/version/OBProxy, cross-platform, long-running stress or independent nupkg-runtime acceptance is added. P1-10 remains partial overall; all23 gaps/eight packages and the complete-driver objective remain open.

## 2026-10-25 documentation delivery and parameter derivation checkpoint

Historical development snapshot; superseded by the delivery above, with original failures/warnings preserved.

Committed baseline: `2bbecac` on master; Java HEAD rechecked as `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`. The [audit report](obconnector-dotnet-driver-audit.md) and [complete baseline](obconnector-dotnet-completeness.md) retain all 23 gap IDs, eight work packages, source/test entry points, acceptance criteria and historical failures/skips. Both Java and .NET pool implementations exist; full isolation, concurrency/fault and real topology acceptance remain open.

Parameter derivation is uncommitted development under P1-10. The workspace adds synchronous/asynchronous OceanBaseCommand APIs, unique routine identity selection, exact type attributes, a reserved session/transaction epoch, cancellation and atomic collection-membership publication. It reads metadata without executing the target or adding MySQL StoredProcedure execution support. CommandTimeout applies per metadata query; arbitrary concurrent edits of parameter properties are outside the membership-version guarantee.

Existing TRX reread: API3 and related behavior132 pass; initial RED/failing metadata, cancellation and complex-type reports remain in the [full checkpoint](obconnector-dotnet-completeness.md#1517-2026-10-25-参数推导开发与完整文档检查点). Collected the already-running Release build: exit0, 0 errors, 1 xUnit2013 warning. The new 20-case five-asset consumer compiles but has no execution TRX at this checkpoint. Prior module units3819/consumers146/DI14/Integration9+72 skips do not validate the new workspace changes.

Still required: positive package/overload and function-return cases, transaction/no-commit and permission-error behavior, collection publication interleavings, incomplete type identities, warning cleanup, full unit/affected-consumer/DI/Integration/format gates and final independent review. Real catalog/permissions/version/OBProxy acceptance remains separate. This pass edits documentation only and checks its structure/links; no new test run, product change, commit, push or complete-driver closure is claimed. Earlier dated records below describe their own snapshots.

## 2026-10-25 P1-08 signed decimal parameter scale

Implemented nullable OceanBaseParameter.DecimalScale from base `5146b62`, preserving Oracle NUMBER -84..127 declarations without changing byte Scale. Shared server/client prepare constraints validate decimal-only and typed NULL metadata, per-mode bounds and exact precision without rounding. Scale setter clears the override; a non-null override clears the previous Scale; clearing it does not restore that overwritten declaration. Snapshots retain it. See the [public contract](obconnector-dotnet-architecture-design.md#decimal-parameter-scale) and [module record](obconnector-dotnet-completeness.md#1516-2026-10-25-有符号-decimal-scale-本地交付).

Fixed the local Oracle consumer fixture: raw OK omitted tracking variables, so ObserveSessionState invalidated cached timezone/charset and the next command correctly sent a SELECT probe. The fixture now uses existing Report(false,1), retaining exact EXECUTE/rejection/reuse assertions. Original consumer 10 passed/10 failed and API RED 6 failed reports remain. The first 86-pass consumer filter missed fallback/array methods in a shared class; final explicit method filters execute Scale20 + fallback30 + array30 + Batch50 + Catalog16 = 146 cases.

Final Release build: 0 warnings/errors, exit0. Complete units3819, affected consumers146 and DI14 pass; Integration9 pass/72 individual NotExecuted. Format verify exits0 with a workspace-load warning. Independent review_decimal_scale re-review found no remaining Critical/Important. Final TRX: unit-verified, consumer-covered, di-verified, integration-verified under ignored TestResults/decimal-scale; logs TestResults-decimal-scale-*.log. Local pack exits0 and five asset DLL hashes, embedded README bytes and five dependency groups match; no publish.

Consumers load five assets in the .NET10 host, not five independent runtimes. No full consumer suite, real-server declared NUMBER/DECIMAL/version/OBProxy, non-Windows, stress or independent nupkg-runtime evidence is added. P1-08 remains partial overall; P1-10 automatic derivation and real acceptance remain open. All23 gap IDs/eight packages and historical failures/skips remain; the complete-driver goal is not closed.

## 2026-10-25 documentation delivery and signed decimal scale checkpoint

Historical pre-fix checkpoint, superseded by the signed-scale delivery above; original failures remain recorded.

Committed baseline: `5146b62` on master. The complete [audit](obconnector-dotnet-driver-audit.md) and [gap baseline](obconnector-dotnet-completeness.md) retain all 23 IDs, eight work packages, source/test entry points, acceptance requirements and historical failures/skips. Java HEAD and pool classes were rechecked. No product/test changes, new build/test run, commit or push were made during this documentation pass; results from two already-running processes were collected.

Signed DecimalScale is uncommitted development under P1-08. Existing reports: API RED 6 failed; initial focused 6 passed; codec/fallback subset 89 passed; Release build 0 warnings/errors; complete units 3819 passed, exit 0; consumer-focused 10 passed/10 failed, all Oracle cases failed; format verify exit 0 with a workspace-load warning. The consumer fixture expected COM_STMT_EXECUTE but received COM_QUERY/SELECT and closed its stream after assertion failure. Session-state/probe handling needs diagnosis; this is neither a passing delivery nor real-server evidence. Independent static review found no remaining Critical/Important but did not run tests.

See the [full checkpoint](obconnector-dotnet-completeness.md#1515-2026-10-25-完整文档复核与有符号-scale-开发检查点) for proposed API precedence/limits, exact report names, failure evidence and remaining gates. Automatic parameter derivation is absent. Procedure directories do not add MySQL CommandType.StoredProcedure execution; current stored-procedure execution requires Oracle PrepareExecute. No gap/work package or complete-driver objective is closed. No new real-server, platform, runtime, stress or package-consumer claim is made.

## 2026-10-25 P1-10 DataTypes metadata

Implemented GetSchema("DataTypes") on top of `f815c16`: Oracle 20/MySQL 18 collections, fresh static invariant-culture tables, standard 23 + 9 extension columns, existing provider enum identities and conditional CLR/provider mappings. No directory request or new dependency; normal open/idle, ambient/XA and cancellation guards remain. See the [Schema contract](schema.md) and [full module record](obconnector-dotnet-completeness.md#1514-2026-10-25-datatypes-本地交付).

Fixed the previous direct-encoder test to supply known UTF-16 and assert exact national-string bytes, retaining rejection for unknown charsets. Explicitly included CatalogEveryAssetExposesModeAwareDirectories in the consumer filter and checked all 16 cases executed. Independent reviewer review_data_types_final found stale 19/17 assertions in real Integration/Acceptance entry points; both now 20/18. Final re-review found no remaining Critical/Important.

Final Release build exits 0 with 0 warnings/0 errors. Focused 29 passed; complete units 3792 passed, affected Schema/Catalog consumers 66 passed (including actual 16 DataTypes cases), DI 14 passed, Integration 9 passed/72 individual NotExecuted. Final reports are unit-verified.trx, consumer-verified.trx, di-verified.trx and integration-verified.trx under ignored TestResults/data-types; build/format/list logs are TestResults-data-types-*.log. Format verify exits 0 with a workspace-load warning. Acceptance --list exits 0 and is only runner-loading evidence. Five assets run on the .NET 10 host, not five distinct runtimes. Full consumer suite, real-server type/DDL/catalog/permissions/version/OBProxy, independent runtime and non-Windows coverage were not rerun.

The prior API 26 failed, contract 8 failed, focused 74 passed/1 failed and full 3791 passed/1 failed reports remain in the [historical checkpoint](obconnector-dotnet-completeness.md#1513-2026-10-25-datatypes-未提交开发与文档交付检查点); the earlier 50-pass consumer filter excluded the new assertions. They do not describe the final state and have not been deleted or relabeled. P1-10 stays partial: automatic parameter derivation and real acceptance remain open. No other gap ID/work package or the complete-driver goal is closed by this module.

Local pack exits 0: five asset DLL hashes match the final Release outputs, README bytes and five dependency groups match; no package was published or independently consumed on additional runtimes. Final documentation checks pass: 9 Markdown files, 116 local links/anchors, fences/encoding, three 23-ID tables, eight work packages and unchanged historical task rows; git diff --check passes.

## 2026-10-25 complete documentation and DataTypes development checkpoint

Historical pre-fix documentation checkpoint; superseded by the DataTypes delivery record above. Original failures and coverage limits remain for traceability.

The committed baseline is `f815c16` on master; the Java reference HEAD was rechecked as `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`. DataTypes is an existing uncommitted workspace change, not a completed delivery. Committed Schema counts remain Oracle 19/MySQL 17; this working tree advertises 20/18. The [audit report](obconnector-dotnet-driver-audit.md), [23-gap baseline](obconnector-dotnet-completeness.md) and API documents now distinguish this development state from committed capabilities. All 23 gap IDs, eight work packages and historical failures/skips remain.

This documentation pass read the existing reports under ignored `TestResults/data-types/`; it did not edit product/test code, start builds/tests, commit or push. Existing Release build: 0 warnings/0 errors (11.86s). `unit-final.trx`: 3792 total, 3791 passed, 1 failed, process exit 1. Failure: `TextCommandTests.DataTypesParameterIdentitiesBindActualScalarValues(mysql: False)` throws `The session national character set is unknown.` The test directly encodes NCHAR/NVARCHAR2 without supplying the national charset required by the codec. This identifies the failing precondition, not real-server interoperability; later Oracle samples in that same test did not execute. Fix the positive test precondition while retaining rejection for unknown charsets, then obtain fresh evidence.

`consumer-final.trx`: 50 passed under `FullyQualifiedName~Schema`. The new assertions are in `Ob20ConsumerTests.CatalogEveryAssetExposesModeAwareDirectories`, whose name does not match this filter. Its 16 cases have no DataTypes execution evidence here; do not claim five-asset DataTypes consumption passed. DI 14 passed; Integration 9 passed/72 individual NotExecuted (summary notExecuted=0 is misleading). Earlier REDs remain: API 26 failed/1 passed; missing ResultTypeCondition 8 failed; focused 74 passed/1 failed (Oracle RAW 252 expected vs 203 actual, expectation now 203). Earlier focused 27 passes do not override the current full-unit failure. The format log contains a workspace-load warning.

The [full checkpoint](obconnector-dotnet-completeness.md#1513-2026-10-25-datatypes-未提交开发与文档交付检查点) records report names, source paths, coverage limits and follow-up commands with fresh report names. P1-10 remains partial; DataTypes remains in development, parameter derivation absent, real catalog/permissions/version/OBProxy and encoding/DDL acceptance open. No new database, non-Windows, standalone runtime or nupkg consumer result is claimed. This pass checks Markdown links/anchors/fences, ID/work-package coverage and diffs only; completion of documentation does not close the complete-driver goal.

Documentation validation passed: 9 Markdown files, 115 local links/anchors, balanced fences, UTF-8 without BOM/CRLF, three tables with 23 unique gap IDs each, eight work packages covering every gap once, unchanged historical task rows, and `git diff --check`.

## 2026-10-25 P1-11 credential provision and rotation

Implemented on top of `2dd1893`: all five assets expose a caller-owned credential provider, immutable context/snapshot and connection configuration; modern builders capture the provider reference. Each logical Open resolves once, including idle reuse, and replaces all configured authentication fields without fallback. Versions retire old pool generations without interrupting borrowers; expiry gates acquisition/authentication/publication/reuse/return, including infinite pool waits. Provider caching/refresh coalescing remains caller-owned. See the [credential contract](credentials.md) and [complete module record](obconnector-dotnet-completeness.md#1512-2026-10-25-凭据提供与轮换本地交付).

Independent review findings were reproduced and fixed: logical snapshot expiry during probe and diagnostic callbacks, failed warmup destroying a concurrent borrower, and unbounded waiting beyond credential expiry. The final reviewer found no remaining Critical/Important issues. The two stale certificate-reflection tests now call registry Get directly and retain the real new-pool assertion; consumer construction now passes an explicit bool argument array. Original RED and failed final reports remain under `TestResults/credential-rotation/`; the historical checkpoint below is superseded by these reruns.

| Final evidence | Result |
| --- | --- |
| Release solution build | Exit 0; zero warnings/errors (`TestResults-credential-build-recheck.log`) |
| Focused credentials + pooled certificates | 97 passed (`focused-recheck.trx`) |
| Credential consumers | 32 passed (`consumer-credential-recheck.trx`) |
| Complete units | 3764 passed, zero failed/skipped, exit 0 (`unit-recheck.trx`) |
| Affected consumers | 885 passed, zero failed/skipped, exit 0 (`consumer-recheck.trx`); filtered subset, not full suite |
| DI | 14 passed, exit 0 (`di-recheck.trx`) |
| Integration discovery | 9 passed / 72 individual NotExecuted, exit 0 (`integration-recheck.trx`); no real profiles configured |
| Changed/new C# format verify | Exit 0 with workspace-load warning (`TestResults-credential-format-recheck.log`) |
| Local NuGet package | Pack exit 0; five asset DLL hashes match the verified Release outputs, five dependency groups and current README checked |

Commands and ownership/expiry/version limits are in completeness section 15.12. No product DLL was rebuilt during consumers. Five assets were loaded on a .NET 10 host, not five independent runtimes. No new package runtime consumer, live credential-service/plugin/KDC, OBProxy, cross-platform or online vulnerability-source verification is claimed. P1-11 is locally implemented; real acceptance and the overall driver goal remain open.

## 2026-10-25 uncommitted credential module documentation checkpoint

Historical pre-fix checkpoint, superseded by the P1-11 final verification above. Original failures and skips are retained.

The committed baseline is `2dd1893` (product code through `6fecad9`). Credential provision/rotation is present in the working tree, but remains **in development, uncommitted and blocked by full-unit failures**. This documentation handoff does not modify product/tests or start new test runs. The detailed [checkpoint](obconnector-dotnet-completeness.md#1511-2026-10-25-未提交凭据模块的文档检查点) and [working-tree credential contract](credentials.md) distinguish implementation from delivery.

Existing reports under `TestResults/credential-rotation/` were read directly: focused 71 passed; complete units 3762 passed / 2 failed out of 3764, exit 1; DI 14 passed; Integration 9 passed / 72 individual NotExecuted results. The Release build log records zero warnings/errors. The existing affected-consumer run has now completed: `consumer-final.trx` records 885 total, 853 passed / 32 failed, exit 1. This is a filtered subset, not the full consumer suite. These reports are local ignored artifacts, not distributed with Git.

Both failures are sync/async `PooledClientCertificateTests.FailedCertificateWarmupRollsBackAndRemovesItsPool`: line 181 expects four registry Get parameters, while the working-tree signature has five after adding the optional provider. This proves stale reflection wiring; the later new-pool assertion did not run. It neither proves a product leak nor establishes passing cleanup. Update the wiring, rerun required gates, review any resulting product fixes and commit the module before changing its delivery status. Earlier red/green evidence remains in place.

All 32 consumer failures occur at `CredentialProviderConsumerTests.cs:41`: `Activator.CreateInstance(providerType, asynchronous)` selects the `(Type, bool nonPublic)` overload, so it requests a missing parameterless constructor instead of the helper's `(bool asynchronous)` constructor. Pass an explicit constructor-argument array and rerun; no credential handshake/rotation consumer case reached its assertions. Both categories are test-wiring defects and remain module delivery blockers; no production-code defect is inferred solely from these results.

All 23 gap IDs, historical task IDs, database failures/skips and eight work packages are preserved. No real credential-service/plugin/KDC, OBProxy, cross-platform or long-running concurrency acceptance was added.

## 2026-10-25 complete audit documentation handoff

Updated the [audit report](obconnector-dotnet-driver-audit.md) and [detailed completeness baseline](obconnector-dotnet-completeness.md) to committed source `6fecad9`; Java remains `6adc311b`. The documents retain all 23 gap IDs and eight work packages, separate absent APIs from partial implementations and missing acceptance, and add explicit credential-provider/rotation acceptance requirements plus the DataTypes/automatic parameter derivation/KeyInfo boundaries. Those requirements are not implemented APIs or approved public signatures.

This was documentation-only. Existing Schema-module reports were reread: complete units 3693 passed, affected consumers 66 passed, DI 14 passed; Integration has 9 passed and 72 individual NotExecuted results (its summary notExecuted counter is zero). No build/test, package, Java or real-server run was repeated. Historical failures, skips, missing-report limitations and release gates remain open; detailed evidence is recorded in completeness section 15.10.

## 2026-10-24 documentation audit refresh

The [audit report](obconnector-dotnet-driver-audit.md) and [completeness baseline](obconnector-dotnet-completeness.md) now track 23 gaps (6 P0, 11 P1, 6 P2) against .NET `06d58e1` and Java `6adc311b`. Each gap has a trigger, source/test entry point and acceptance evidence requirement. Existing nested object/collection support, the six Schema collections and the close/reopen ownership repair are recorded as implemented rather than missing.

At the initial documentation-only checkpoint, source review identified ordinary global pools sharing per-connection resolver routes (P0-06), and `SchemaOnly` executing SQL before hiding/draining rows (P1-05). That checkpoint did not fix either behavior. P0-06 was subsequently implemented and tested as recorded below; dedicated SchemaOnly side-effect/lock regressions remain open. Credential provision/rotation is tracked explicitly as P1-11. Architecture and compatibility limits have been synchronized; the task schedule and historical results are preserved.

## 2026-10-24 P0-06 pooled resolver routing

Ordinary registry pools now use the original connection string and resolver reference identity, including when resolvers override value equality. Data sources retain private pools. Each pooled Open resolves once; the same endpoint snapshot governs idle-session selection, minimum-pool warmup and new physical sessions. Removed idle endpoints are discarded before warmup, discovery failure cannot fall back to an idle session, and borrowed sessions finish normally. Host/port membership does not detect DNS IP changes behind an unchanged hostname; concurrent Opens use independent snapshots without global topology-version ordering.

The reviewer identified a concurrent cleanup race while discovery was still in flight. A rent reservation now starts atomically with registry lookup and ends after Rent, so another failed Open cannot dispose that resolving borrow's pool. Added regressions reproduced the race before repair and cover resolver identity, route changes, warmup, removal of borrowed endpoints, discovery failure, Close/cancellation, total connection budget versus a longer pool-wait timeout, reentrant Closed callbacks, stale cached pools and preservation of the existing unsupported ambient-isolation exception contract. The last exception regression failed on both sync/async paths before its narrow normalization fix. Independent review also checked the absence of a global snapshot-order promise; that remains an explicit boundary.

Verification was split deliberately so the pre-follow-up full run is not presented as final-asset coverage:

| Scope | Command / evidence | Result and limits |
| --- | --- | --- |
| Full solution before the final exception-normalization follow-up | `dotnet test OceanBaseConnector.slnx -c Release --no-restore --no-build`, normal Windows permissions; `TestResults/pool-routing/pool-routing_*.trx` | Exit 0. UnitTests 3477, ConsumerTests 2112 and DI 14 passed, with no failures/skips in those suites. Integration 9 passed/70 skipped. These assets precede the last normalization edit and ten new consumer routing cases. |
| Final Release assets | `dotnet build OceanBaseConnector.slnx -c Release --no-restore --verbosity quiet`; `TestResults-pool-routing-build.log` | Exit 0, 0 errors, 9 existing `NU1900` vulnerability-feed access warnings. |
| Final full unit suite | `dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-restore --no-build`, normal Windows permissions; `TestResults/pool-routing/unit-final.trx` | Exit 0, 3479 passed, 0 failed/skipped, including both formerly failing ambient-isolation exception regressions. |
| Final affected consumers | `dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-restore --no-build --filter 'FullyQualifiedName~EveryAssetIsolatesPooledResolverRoutesAndRefreshesEndpointSnapshots\|FullyQualifiedName~EveryAssetDiscoversAndRotatesWithoutReplayingSql\|FullyQualifiedName~TransactionConsumerTests'`, normal Windows permissions; `TestResults/pool-routing/consumer-final.trx` | Exit 0, 67 passed, 0 failed/skipped. The new routing cases cover sync/async paths on all five TFM assets loaded by the net10 consumer host; this is not a full test run on five different runtimes. |
| Formatting and document structure | `dotnet format OceanBaseConnector.slnx --no-restore --verify-no-changes --include` all seven changed C# files; local-link/ID checks; `git diff --check` | Exit 0. All 36 local Markdown links exist; each of the 23 gap IDs occurs once in the summary and once in its traceability table. |
| Requested Go lint | `golangci-lint run ./...` | Exit 1 at module discovery because this C# repository has no Go module. Inapplicable; this is not a lint pass. |

All test reports and command logs above are ignored local artifacts. No real OceanBase/OBProxy routing, DNS rotation, production load or additional OS/runtime acceptance was run for this module. P0-06 therefore remains locally implemented with real topology acceptance open; the other 22 tracked gaps are not marked complete by this commit.

## 2026-10-25 P1-04 TLS server validation callback

All five assets now expose `OceanBaseConnection.RemoteCertificateValidationCallback`; modern data-source builders expose `UseRemoteCertificateValidationCallback(...)`. A null policy retains platform validation, while a configured policy receives certificate/chain/name/revocation errors and makes the final decision. Callbacks require VerifyFull, are frozen during Open and in source-created connections, and are carried through warmup/new-session sync/async TLS. Ordinary pools compare callback reference identity; equal delegates remain separate. A false result or exception aborts before database authentication without endpoint retry or plaintext downgrade.

The initial 17 tests all failed before implementation because the public API was absent. The final 28 focused regressions passed, including hostname/chain error propagation, false/throw, null-platform rejection, policy reassignment, reference-equal pool reuse versus equal-delegate isolation, builder snapshots, mode rejection, cancellation while validation is running, reentrant Closed callback ownership and failed minimum-pool warmup rollback. Independent read-only review found no Critical/Important product defect; its requested pooled rejection/cleanup coverage was added for sync/async false/throw paths. The five-asset consumer acceptance/rejection subset passed 20 tests under a net10 host.

Release solution build completed with 0 errors and 9 existing `NU1900` vulnerability-feed access warnings. The requested `golangci-lint run ./...` exited 1 at module discovery because this C# repository has no Go module; it remains inapplicable, not a lint pass. Reports/logs are retained locally under ignored `TestResults/tls-validation/` and `TestResults-tls-validation-*.log`.

The final complete solution command, `dotnet test OceanBaseConnector.slnx -c Release --no-restore --no-build`, ran with normal Windows permissions and exited 0. UnitTests 3507, ConsumerTests 2142 and DI 14 passed, with no failures/skips in those three suites. Integration 9 passed/70 skipped; no real TLS server acceptance is inferred from them. Final reports use the `tls-validation-final_*.trx` prefix. Changed-file `dotnet format --verify-no-changes` passed for all ten changed C# files; `git diff --check`, all 41 local document links and the 23-ID summary/traceability checks passed.

The callback runs only at physical TLS establishment. Pooled borrow does not revalidate certificates, and changing captured callback state does not revoke live sessions. Callbacks must be prompt and concurrency-safe; transport cancellation cannot interrupt application callback code. Real enterprise CA, certificate rotation/revocation, mTLS and non-Windows interoperability remain unverified; application policy correctness remains the application's responsibility. At this checkpoint, pooling with `ClientCertificate` still rejected explicitly; the following module replaces that restriction. The real interoperability limits were also the independent review's declined-to-judge items; they remain open acceptance work rather than passes.

## 2026-10-25 P1-04 pooled client certificates

Ordinary pooled connections now isolate client certificate reference identity plus a SHA-256 DER fingerprint, in addition to configuration, resolver and server-validation callback identity. Equal certificates in separate objects remain separate pools; the same instance reuses its matching pool. Each pool owns a copy used for warmup/new TLS sessions and releases only that copy. Registry cleanup uses cached identity even after the caller resets/disposes the original. Ordinary Open still validates the original's private-key association before any borrow. A certificate with TLS Disabled or no private key rejects before networking.

Modern builders expose `UseClientCertificate(X509Certificate2?)`. Build captures a source-owned copy, also with Pooling=false, so the original may be disposed after Build succeeds. Source-created connections have fixed certificate configuration; their getter reports the original configured reference while actual TLS uses the internal snapshot. Caller objects must not be mutated/disposed during Open/Build. Borrowing does not repeat TLS validation. Rotation should use a new source, dispose the old source and close its borrowed connections. At this checkpoint, ordinary pools lacked a public ClearPool/ClearAllPools API and reaped lazily; the subsequent P0-01 module below adds explicit retirement. Automatic lifetime reaping remains lazy.

Initial unit regressions were RED: 18 failed and 2 passed before product changes. The final 26 focused cases cover reference/value/null identity, reassignment, minimum-pool warmup, failed-warmup rollback, source ownership/snapshots in both pooling modes, pre-network rejection, Reset rejection, reentrant Closed callback ownership and source disposal during blocked TLS validation. Independent read-only review found no confirmed production defect. Its consumer-fixture build wiring finding was fixed; its requested disposal race became four passing regressions. Successful in-place certificate Import was not simulated: the current runtime throws PlatformNotSupportedException because certificate objects are immutable. Reset and instance replacement are covered, while other runtimes/providers remain unverified.

| Scope | Command / evidence | Result and limit |
| --- | --- | --- |
| Final Release build | `dotnet build OceanBaseConnector.slnx -c Release --no-restore --verbosity quiet`; `TestResults-client-certificate-final-build.log` | Exit 0, 0 errors, 9 NU1900 vulnerability-feed access warnings. All five product assets built after formatting. |
| Complete unit suite | `dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-restore --no-build --logger 'trx;LogFileName=unit-final.trx' --results-directory TestResults/client-certificate` | Exit 0, 3533 passed, 0 failed/skipped, using normal Windows certificate access. Includes the 26 new cases. |
| Certificate and route consumers | `dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-restore --no-build --filter 'FullyQualifiedName~ClientCertificateConsumerTests\|FullyQualifiedName~TlsValidationConsumerTests\|FullyQualifiedName~SessionSource\|FullyQualifiedName~ProviderFactory\|FullyQualifiedName~EveryAssetIsolatesPooledResolverRoutesAndRefreshesEndpointSnapshots'` | Exit 0, 42 passed. Actual matches: 32 new certificate cases plus 10 route cases; unmatched class-name filters are not counted as coverage. Report: `consumer-final.trx`. |
| Existing TLS/source consumers | Same test project/options, filter `FullyQualifiedName~EveryAssetUsesCustomTlsServerValidation\|FullyQualifiedName~EveryAssetNegotiatesAndExecutesOb20OverTls\|FullyQualifiedName~EveryAssetDiscardsPartialOb20TlsResponsesAfterPublicFieldReads\|FullyQualifiedName~ModernAssetsConsumeConnectionAndDataSourceBatches` | Exit 0, 112 passed. Report: `consumer-tls-source.trx`. These and the preceding 42 are distinct cases, 154 total; five TFM assets are loaded by a net10 host, not five independent runtime/server matrices. |
| DI | `dotnet test tests/OceanBaseConnector.DependencyInjection.Tests -c Release --no-restore --no-build` | Exit 0, 14 passed. `di-final.trx`. |
| Integration | `dotnet test tests/OceanBaseConnector.IntegrationTests -c Release --no-restore --no-build` | Exit 0, 9 passed/70 skipped. `integration-final.trx`; no real mTLS/OBProxy acceptance is inferred. |
| C# formatting and document checks | `dotnet format OceanBaseConnector.slnx --no-restore --verify-no-changes --include` the nine changed C# files; local-link/anchor and 23-ID checks; `git diff --check` | Exit 0. All 56 local links/anchors across the six changed Markdown documents resolve; the summary, traceability and audit tables have the same 23 IDs. Format reported a workspace-load warning but no required changes. This is changed-file verification, not a whole-repository format claim. |
| Requested Go lint | `golangci-lint run ./...`; `TestResults-client-certificate-golangci-lint.log` | Exit 1 at module discovery: no Go module. Inapplicable to C#; not a lint pass. |

All reports above are local ignored artifacts under `TestResults/client-certificate/`; command logs use `TestResults-client-certificate-*.log`. New consumers perform real local SslStream client-certificate authentication, execute a query and reopen; modern sources are tested after disposing the original certificate, both pooled and non-pooled, sync and async. This proves local presentation/isolation/ownership, not real OceanBase/OBProxy mTLS, enterprise CA, rotation/revocation, Linux or private-key-provider compatibility. P1-04 remains partial until those acceptance profiles close.

## 2026-10-25 P0-01 explicit pool clearing

All five assets now expose static `OceanBaseConnection.ClearPool(connection)` and `ClearAllPools()`; modern sources expose `Clear()`. ClearPool snapshots current configuration/resolver/callback/certificate references, without creating a pool or reading certificate contents. It can clear a matching pool through an unopened logical connection, including after its caller-owned certificate was Reset/Dispose'd. ClearAllPools snapshots ordinary registry pools only; private sources use Clear or ClearPool with a source-created connection.

Clear closes idle sessions and retires borrowed/pre-clear in-flight sessions without interrupting commands or transactions. Borrowed sessions still count toward capacity until returned, skip reset when already retired, and cannot be republished after an in-progress reset. Creation slots carry a generation; stale warmup registrations are discarded before publication. Source Clear preserves configuration and its owned certificate copy; ordinary empty pools release registry ownership. Lifetime cleanup remains lazy without explicit clearing.

The initial 19 unit cases all failed with missing APIs, then passed after implementation. Independent review found two Important concurrency/ownership defects. Four added sync/async regressions reproduced a stranded waiter (TimeoutException) and double-accounted warmup removal (`pool.sessions` delta -1). The fix signals released creation slots in both stale registration and CompleteOpening, and removes destroyed warmup entries from the idle stack at the shared Destroy boundary. Final focused cases: 23 passed, including the four review regressions. No Critical finding remains; real server/cross-platform and unrelated broad warmup stress are retained as acceptance items.

| Scope | Command / evidence | Result and limit |
| --- | --- | --- |
| Release solution build | `dotnet build OceanBaseConnector.slnx -c Release --no-restore --verbosity quiet`; `TestResults-pool-clearing-final-build.log` | Exit 0, 0 errors, 9 NU1900 vulnerability-feed access warnings. |
| Complete unit suite | `dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-restore --no-build --logger 'trx;LogFileName=unit-final.trx' --results-directory TestResults/pool-clearing` | Exit 0, 3556 passed/0 failed/skipped, including all 23 clearing regressions and both reproduced review findings; normal Windows certificate access. |
| Affected public consumers | `dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-restore --no-build --filter 'FullyQualifiedName~PoolClearingConsumerTests\|FullyQualifiedName~ClientCertificateConsumerTests\|FullyQualifiedName~EveryAssetIsolatesPooledResolverRoutesAndRefreshesEndpointSnapshots\|FullyQualifiedName~EveryAssetUsesCustomTlsServerValidation\|FullyQualifiedName~EveryAssetNegotiatesAndExecutesOb20OverTls\|FullyQualifiedName~EveryAssetDiscardsPartialOb20TlsResponsesAfterPublicFieldReads\|FullyQualifiedName~ModernAssetsConsumeConnectionAndDataSourceBatches'` | Exit 0, 180 passed/0 failed/skipped; `consumer-final.trx`. Includes 26 new clearing cases and 32 certificate cases. The net10 host loads five built TFM assets. |
| DI / Integration | `dotnet test tests/OceanBaseConnector.DependencyInjection.Tests -c Release --no-restore --no-build`; same options for IntegrationTests | Both exit 0. DI 14 passed; Integration 9 passed/70 skipped. Reports `di-final.trx`, `integration-final.trx`. |
| C# formatting and docs | `dotnet format OceanBaseConnector.slnx --no-restore --verify-no-changes --include` the six changed C# files; local links/anchors and IDs; `git diff --check` | Exit 0. 60 local links/anchors valid and matching 23-ID tables; no required changed-file formatting. Format retains a workspace-load warning, not a whole-solution clean claim. |
| Requested Go lint | `golangci-lint run ./...`; `TestResults-pool-clearing-golangci-lint.log` | Exit 1 because there is no Go module; inapplicable to C#, not a lint pass. |

Local logs use `TestResults-pool-clearing-*.log`; reports use ignored `TestResults/pool-clearing/`. The 26 public consumers cover five assets (ordinary ClearPool/ClearAllPools, sync/async) and modern sources with the original certificate disposed. Source cases also prove ClearAllPools leaves private sources intact. These are local SslStream evidence, not real OceanBase/OBProxy or cross-platform certification.

## Recorded local results

| 2026-10-24, ambient transaction enlistment | Added local volatile `TransactionScope`/`EnlistTransaction` support. Opening inside a scope begins one local transaction; commands and `DbBatch` resolve the same transaction; scope completion commits and restores autocommit, and disposal rolls back. Focused transaction/provider regressions: 8 passed. | Loopback evidence only. Distributed promotion, timeout/in-doubt, commit/rollback response loss, close-before-callback/reopen (now rejected explicitly) and real OceanBase/OBProxy coordinator behavior remain unverified. |
| 2026-10-24, ambient follow-up gates | Release solution build: 0 errors and 9 `NU1900` warnings because the NuGet vulnerability feed was unavailable. Whole UnitTests rerun: 3316 passed, 93 failed, 0 skipped (3409 total); failures are dominated by existing multi-statement fixture assumptions and Windows Schannel/PFX TLS environment cases, with stale ambient exception expectations corrected in the focused set. `dotnet format --verify-no-changes` still reports repository-wide existing whitespace findings. | Do not treat the full-suite result as a clean gate until the unrelated multi-statement/TLS baseline is repaired or isolated. No real OceanBase/OBProxy ambient transaction or LOCAL INFILE server run was available. |
| 2026-10-24, TLS client certificate and revocation follow-up | Added `OceanBaseConnection.ClientCertificate` for direct non-pooled TLS, added `Check Certificate Revocation` connection-string parsing and platform TLS propagation, and explicitly reject client certificates with `Pooling=true`. Release solution build: 0 errors; focused TLS/configuration/pool-policy tests: 74 passed. | This proves API propagation and the pool identity guard locally. Enterprise trust customization, cross-platform revocation behavior and real mTLS/OceanBase/OBProxy profiles remain unverified. A fresh full UnitTests run had 3318 passed, 93 failed, 0 skipped (3411 total), with the same pre-existing multi-statement and Windows Schannel/PFX environment failures. |

| Baseline | Recorded result | Limit |
| --- | --- | --- |
| 2026-10-24, pooling registry cleanup and `Min Pool Size` prewarming | Focused `PhysicalSessionPool` tests: 12 passed. | The full UnitTests run had 3336 passes and 39 Windows Schannel/PFX/TLS-environment failures. The focused run and loopback fixtures did not exercise a real OceanBase/OBProxy server. |
| 2026-10-24, pool lifecycle metrics | The dedicated `PoolMetricsReportSessionsBorrowsWaitersAndDiscards` regression passed; the focused pool subset including metrics, reuse, wait, reset failure and prewarming passed 8/8. | This is local `MeterListener`/loopback evidence. It does not validate metric values or reset behavior against a real OceanBase/OBProxy server. |
| 2026-10-24, pool reset baseline validation | Added MySQL `Autocommit` status checks to reset and borrow probe paths; missing status now destroys the physical session. Both new regressions passed. | Loopback protocol evidence only; session variables, temporary objects, Oracle reset semantics and real server behavior remain open. |
| 2026-10-24, prepared statement reset isolation | Physical session generations now advance after successful `COM_RESET_CONNECTION`; commands discard statements from older generations without sending stale `COM_STMT_CLOSE` packets. Sync and async pooled reuse regressions passed. | Local loopback evidence only; other session resources and real OceanBase/OBProxy reset behavior remain open. |
| 2026-10-24, current focused pool rerun | 20 of 20 selected pooling/prepared-statement tests passed after aligning the warmup failure assertion with the documented connection-timeout contract. | The fixture still uses a loopback listener and does not exercise a real OceanBase/OBProxy server. |
| 2026-10-24, pooled REF CURSOR lifetime | Unconsumed Oracle REF CURSORs mark their physical session for discard on connection close; sync and async pooled close/reopen regressions passed. | Local loopback evidence only; Oracle cursor cleanup and reset behavior on real OceanBase/OBProxy remain open. |
| 2026-10-24, pooled LOB handle lifetime | Live materialized LOB handles, including temporary LOB handles, mark their physical session for discard on connection close; sync and async pooled close/reopen regressions passed. | Local loopback evidence only; temporary LOB server cleanup and reset behavior on real OceanBase/OBProxy remain open. |
| 2026-10-24, LOB isolation verification | Focused LOB/temporary-LOB/pooling subset: 205 passed. Full UnitTests: 3346 passed, 39 Windows TLS/PFX environment failures. ConsumerTests: 1484 passed, 628 Windows TLS/PFX environment failures. DI tests: 14 passed. | The focused loopback evidence covers the changed lifecycle; the TLS/PFX failures are environment limitations and do not establish real server compatibility. |
| 2026-10-24, user session-state isolation | Recognized user `SET`/`ALTER SESSION`/temporary-object statements now mark the physical session for discard; reopening the same logical connection after close creates a fresh physical session. The regression passed after fixing stale disposed-pool reuse. | Conservative SQL-prefix recognition does not cover session changes hidden inside stored procedures or dynamic SQL; real server reset and temporary-object behavior remain open. |
| 2026-10-24, session-state pool rerun | Pooling and physical-session-pool subset: 20 passed, including user `SET` and `CREATE TEMPORARY` discard/reopen cases and pool metrics synchronization. | Loopback evidence only; full unit run still has Windows Schannel/PFX failures and does not establish real OceanBase/OBProxy reset behavior. |
| 2026-10-24, post-audit verification | Fresh focused pool subset: 20 passed. Release solution build: 0 errors; 9 `NU1900` warnings because the NuGet vulnerability feed was unavailable. Changed production source files pass `dotnet format --verify-no-changes`; whole-solution format verification reports existing whitespace findings outside this change. | No real OceanBase/OBProxy run was available; whole-solution format baseline remains unresolved. |
| 2026-10-24, SchemaOnly command behavior | Read-only text `SchemaOnly` now exposes metadata, drains server rows without returning them, rejects writes/procedures/unknown prefixes before send, and preserves connection reuse; sync and async regressions passed. | The prefix gate is intentionally conservative and does not provide `KeyInfo`; real server metadata behavior remains unverified. |
| 2026-10-24, SchemaOnly verification | Focused SchemaOnly regression: 2 passed. Release solution build: 0 errors; changed production files pass `dotnet format --verify-no-changes`; `golangci-lint run ./...` is inapplicable because this C# repository has no Go module (the command exits 1 at module discovery with 0 reported issues). | The broader TextCommand filter had 2376 passes and 9 failures: 8 local PFX/TLS certificate-file failures and 1 XA interruption assertion failure not attributed to this change. No real OceanBase/OBProxy metadata behavior was tested. |
| 2026-10-24, pool lifecycle follow-up audit | Read-only review rechecked idle/total lifetime handling, stale `Open` cleanup, pool-wait cancellation during `Close`, and static registry disposal against the current implementation. | The previously suspected paths now have code protection; they remain targeted concurrency and long-running-process verification items, not confirmed current bugs. |
| 2026-10-24, opt-in text multi-statements | Added `Allow Multi Statements` (default false), capability negotiation, text multi-result execution, and pre-send rejection for parameters, Prepare, server cursors and Oracle output paths. Focused sync/async and opt-in/default rejection regressions passed: 6 tests. | Loopback protocol evidence only; real OceanBase/OBProxy error, cancellation and version compatibility matrices remain open. |
| 2026-10-24, opt-in LOCAL INFILE protocol path | Added `Allow Local Infile` (default false), capability negotiation, `LocalInfileStreamFactory`, synchronous/asynchronous chunked upload, empty-packet termination, final-OK handling, and unknown-outcome session disposal. Focused LOCAL INFILE regressions passed: 8. | Loopback protocol evidence only; real OceanBase/OBProxy `LOAD DATA LOCAL INFILE`, proxy policy, long-file/64 MiB limit, server-error and interruption matrices remain open. |
| 2026-10-24, disposed-pool borrowed-session ownership | `PhysicalSessionPool.Return` and `ReturnAsync` now discard a borrowed session immediately when disposal has completed, without sending `COM_RESET_CONNECTION`; sync and async loopback regressions plus the pool subset passed (22 tests). | The guard covers disposal observed before return begins; a disposal racing after the precheck still needs a concurrency stress case. Connection close may also perform transaction cleanup before pool return. Full solution tests remain non-clean (UnitTests 3321 passed/93 failed; ConsumerTests 1404 passed/708 failed), and no real OceanBase/OBProxy run was available. |
| 2026-10-24, Oracle statement boundaries | Replaced mode-blind semicolon scanning with mode-aware SQL boundaries. Oracle anonymous blocks (including quoted labels) and stored-program DDL remain single server-parsed units; ordinary Oracle SQL uses Oracle quote/comment rules. Initial new regressions: 13 failed/4 passed before the fix; the quoted-label follow-up also failed before its repair. Final focused boundary/OUT/temporary-LOB/multi-statement subset: 83 passed. Independent source review found no remaining must-fix. | Loopback evidence proves request framing and client-side guards, not real PL/SQL acceptance or server rejection of invalid program tails. These remain real OceanBase/OBProxy acceptance items. |
| 2026-10-24, Oracle boundary final verification | Release solution build: 0 errors, 9 existing `NU1900` feed-access warnings. The restricted full run had UnitTests 3391 passed/40 failed and ConsumerTests 1484 passed/628 failed. Repeating the same assets with normal Windows permissions eliminated all TLS/Schannel/PFX failures: ConsumerTests 2112 passed, DI 14 passed; UnitTests initially 3433 passed/1 failed on three stale synchronous MySQL complex-type exception assertions. Restored those assertions to the existing `NotSupportedException` contract (matching async); final full UnitTests rerun: 3434 passed, 0 failed/skipped. | The earlier full-command exit was 1 because of that now-corrected assertion, not an all-green solution command. Changed production files and the new boundary tests pass format verification; `git diff --check` passes. `golangci-lint run ./...` exits 1 at module discovery because this is a C# repository with no Go module; it is not a C# lint pass. Integration: 9 passed/70 skipped; no real server acceptance is claimed. |
| 2026-10-24, close/reopen pool ownership | Close now captures the original pool, registry key and timeout while detaching the session; later sync/async return is independent of Closed event callbacks that change configuration and reopen. Open failures also retain the original registry key. The eight reentrant close/reopen combinations first had 6 failures/2 passes, then all passed after repair. Two registry-cleanup tests failed when the old mutable-key lookup was temporarily restored. Final pool/provider subset: 51 passed. Full normal-permission solution test command: exit 0; UnitTests 3444 passed, ConsumerTests 2112 passed, DI 14 passed (all with 0 failures/skips). Independent source review found no must-fix. | Release solution build: 0 errors, 9 existing `NU1900` feed warnings. Changed-file format and diff checks pass. `golangci-lint run ./...` was executed and exits 1 because there is no Go module; it is inapplicable to C# linting. Integration: 9 passed/70 skipped. TRX reports are under ignored `TestResults/pool-ownership/`. Real OceanBase/OBProxy, different cleanup-timeout values, delayed failed-open completion and multi-threaded pool stress remain acceptance items. |
| 2026-10-22, automatic server identity and explicit client behavior, Windows/.NET 10 | Release solution build: 0 warnings/errors. Unit: 3337 passed; Consumer: 2112 passed; DI: 14 passed, no failures/skips in those three suites. Integration: 4 skipped. Acceptance `--list` includes the new client-behavior case. | Fresh local regressions; no configured real OceanBase/OBProxy environment. |
| Same change, standalone .NET 8.0.32 / 9.0.21 / 10.0.13 hosts | Query and batch/diagnostics samples: all six passed using the corresponding Release assets. | Loopback smoke checks, not full database or package acceptance. The temporary query host build retained cached NU1900 vulnerability-feed warnings. |
| 2026-10-21, runtime code `304642e` plus dependency updates, Windows/.NET 10 | Release build: 0 warnings/errors. Unit: 3271 passed; Consumer: 2112 passed; DI: 14 passed; no failures/skips in those three suites. | Historical local regressions, not a fresh run or complete server acceptance. |
| Same record, separate IntegrationTests | 4 skipped. | Connection, mode, TLS-name and plugin gates were not passed. |
| Same record, package consumption | All five target assets; actual .NET 8/9/10 package hosts. | Package/API consumption, not full database validation on every runtime. |
| 2026-10-20, earlier full Windows .NET 8.0.31 / 9.0.20 runs | Each: Unit 2912 and DI 14 passed, 0 failures/skips after fixes. | Earlier source/test baseline; not the later 3271-test baseline. |
| Historical net48 consumption | Basic provider contracts passed on installed Framework 4.8.1. | No formal original-4.8, full API, SQL or TLS support claim. |

The 2026-10-22 xUnit reports are under the ignored `TestResults/compatibility-final/` directory. Earlier results come from the [original ledger at commit f63d57c](https://github.com/cocosip/obconnector-dotnet/blob/f63d57c/docs/obconnector-dotnet-progress.md); its ignored report files are not included in this checkout. The latest recorded database counts, failed Oracle case and deferred matrices are retained in [Compatibility](compatibility.md).

The 2026-10-24 Oracle boundary verification retains TRX reports under ignored `TestResults/oracle-boundary/`: `oracle-boundary_*.trx` records the restricted run, `unrestricted_*.trx` the normal-permission solution run, `focused-final.trx` the 83-case subset and `unit-final.trx` the final 3434-case unit run. The normal-permission rerun establishes that the recorded Windows TLS failures in the restricted run are environment-dependent; it does not establish real OceanBase/OBProxy TLS interoperability.

## 2026-10-25 P1-02 standard MySQL compression

Starting from `0e58d1f`, added `Use Compression` (default false), endpoint propagation and explicit CLIENT_COMPRESS negotiation. Missing server support fails before sending credentials. Compression starts after the final ordinary authentication OK, before identity confirmation; authentication switches remain ordinary packets. TLS stays below compression. `Use Compression=true;Use OB20=true` is currently rejected before networking; OB20 compressed frames and reroute remain separate open work.

The seven-byte outer framing supports original/zlib blocks, split headers/payloads, several inner packets per block, separate outer sequence wrapping/reset and multi-direction LOCAL INFILE exchanges. MySQL may renumber inner sequences at compressed-buffer flushes, so outer ordering is validated instead of applying the ordinary inner sequence counter. Writes buffer at most 64 KiB; input/output blocks are bounded by 24-bit lengths, retaining the existing logical-packet cap. Strict zlib end/checksum/exact-length/trailing-input checks use installed MIT SharpZipLib >=1.4.2 public APIs, uniformly across five assets. Corrupt input, cancellation and unread buffered tails fail closed, with no SQL replay.

The first 17 regressions failed before implementation. Final added unit cases total 38, including strict malformed/truncated/dictionary/trailing/length cases, max-frame terminators, mixed blocks, sequence wrap/reset, async-only short-read streaming, cancellation, opt-in/default/unsupported negotiation, final-auth timing/auth switch, local TLS, two modes, pooled reset/reborrow and compressed LOCAL INFILE. New consumers total 40 (five assets × two modes × sync/async × TCP/TLS). Their wire peer uses framework ZLibStream rather than the product's SharpZipLib implementation. Independent read-only review found no confirmed Critical/Important defects; LOCAL INFILE, streaming and no-response completion coverage was added after its coverage observation.

| Gate | Command / local report | Result and scope |
| --- | --- | --- |
| Restore environment | `dotnet restore OceanBaseConnector.slnx --source C:/Users/11855/.nuget/packages --packages C:/Users/11855/.nuget/packages -p:NuGetAudit=false` | Exit 0. The online attempt could not reach NuGet (NU1801); offline restore initially used a sandbox-only cache, causing normal-permission test commands to run no tests. Standardizing the existing user cache fixed test SDK imports. Empty runs are not passes; the TRX counts below are authoritative. Vulnerability-feed auditing was disabled for the offline restore, not established as passed. |
| Final build | `dotnet build OceanBaseConnector.slnx -c Release --no-restore`; TestResults-mysql-compression-build-final.log | Exit 0, 0 errors/0 warnings, all five assets. Only XML comments in the builder changed after the complete-suite binaries; final targeted gates below use the final solution build. |
| Complete unit suite | `dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-restore --no-build --logger 'trx;LogFileName=unit-final.trx' --results-directory TestResults/mysql-compression` | Exit 0, 3594 passed/0 failed/skipped, normal Windows certificate permissions. Includes all 38 new cases. The final added test files were built with BuildProjectReferences=false while consumers held product DLLs. |
| Complete consumers | `dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-restore --no-build --logger 'trx;LogFileName=consumer-pre-review.trx' --results-directory TestResults/mysql-compression` | Exit 0, 2240 passed/0 failed/skipped. Includes all 40 new cases. This run used the initial compression fixture; the subsequent LOCAL INFILE fixture addition is exercised by final focused/consumer gates. |
| Final affected gates | Unit filter `FullyQualifiedName~MySqlCompressionTests` or `FullyQualifiedName~CompressionConnectionTests`; consumer filter `FullyQualifiedName~CompressionConsumerTests`, both with --no-restore --no-build | Exit 0, 38 unit cases and 40 consumers passed after final build; focused-final.trx and consumer-final.trx. Consumers load five assets in a net10 host, not five independent runtime/server environments. |
| DI | `dotnet test tests/OceanBaseConnector.DependencyInjection.Tests -c Release --no-restore --no-build` | Exit 0, 14 passed; di-final.trx. |
| Integration | `dotnet test tests/OceanBaseConnector.IntegrationTests -c Release --no-restore --no-build` | Exit 0, 9 passed/70 skipped; integration-final.trx. No new real-server compatibility inferred. |
| Packaging | `dotnet pack OceanBaseConnector.slnx -c Release --no-restore --no-build -o TestResults/mysql-compression/packages`; `pwsh -NoProfile -File eng/Verify-Packages.ps1 -PackageDirectory TestResults/mysql-compression/packages` | Exit 0. Both packages' five dependency groups include the centrally managed compression dependency; independent consumers pass on .NET 8.0.31/9.0.20/10.0.12, with both Standard assets consumed by net10. Final package restore graphs retained under TestResults/package-validation/063abbe23ffd4463beaa7399a56fd11e; final log TestResults-mysql-compression-package-final.log. |
| Changed-file formatting | `dotnet format OceanBaseConnector.slnx --no-restore --verify-no-changes --include` the 12 changed C# files | Exit 0; workspace-load warning only. Not a repository-wide formatting claim. |
| Document checks | Local Markdown link/anchor, source-reference, three-table 23-ID and eight-work-package checks; `git diff --check` | Exit 0; 64 local links/anchors across six Markdown documents, 101 distinct C# file references (abbreviations checked by filename), all 23 IDs preserved and covered once by the work packages. |
| Requested Go lint | `golangci-lint run ./...`; TestResults-mysql-compression-golangci-lint.log | Exit 7: directory is not a Go module. Inapplicable to this C# repository, not a lint pass, regardless of the preceding `0 issues` line. |

TRX reports are in ignored `TestResults/mysql-compression/`; command logs are `TestResults-mysql-compression-*.log`. Local transport/codec/package evidence does not establish real OceanBase/OBProxy compression, enterprise compressed TLS, cross-platform or large application-load acceptance. P1-02 remains partial, with OB20 compression/reroute and real matrices open.

## 2026-10-25 P1-10 Schema catalog

Starting from `b38e4a8`, expanded the six existing Schema collections to 19 in Oracle mode and 17 in MySQL mode. New directories cover Views, Indexes/IndexColumns, Keys/Constraints/ConstraintColumns, ForeignKeys/ForeignKeyColumns, Types, Sequences, Users and Synonyms; Restrictions publishes ordered filter descriptions. Types and Synonyms are Oracle-only and not advertised on MySQL. See the [full Schema contract](schema.md) for all columns, CLR types, dictionary sources, identity conventions and permission requirements.

The implementation reuses the existing command/session/transaction, timeout, cancellation, exact parameter binding and strict DataTable-shape paths. Composite Oracle foreign keys join parent/child owners, constraints, tables and column ordinal. Sequence numbers use OceanBaseDecimal after exact text transport, with no NEXTVAL/CURRVAL query or CLR decimal/Int64 truncation. MySQL Users and Sequences query mysql.user and oceanbase.DBA_SEQUENCES; privilege/missing-view errors remain visible, without privileged fallback or fabricated empty success. Metadata preserves server identifier spelling; views enumerate identity rather than SQL definitions. Types describes named Oracle object/collection types; DataTypes, automatic DeriveParameters and KeyInfo remain separate work.

The plan is under ignored `docs/superpowers/plans/2026-10-25-schema-catalog.md`. The first 90 new cases all failed before implementation (`red.trx`), then passed (`green.trx`); nine additional fault/empty/permission/prefix-length cases make 99 new unit cases. SQL dictionary sources were checked against pinned OceanBase `5e8a63d29dae9fde5e962b8fcc7b167d438fc343` definitions, not executed on a real deployment. An independent reviewer found the Oracle INCREMENT alias was a reserved word: a new assertion produced two failing cases (`review-red.trx`, 42 passed/2 failed), then the Oracle alias was quoted. Review also found stale six-collection assertions, the integration object-kind whitelist, and MySQL table-name case assumptions; all were corrected and re-reviewed with no remaining confirmed Critical/Important finding.

Affected-consumer filter used:

```powershell
dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-restore --no-build --filter "FullyQualifiedName~Schema|FullyQualifiedName~CatalogEveryAsset" --logger "trx;LogFileName=consumer-final.trx" --results-directory TestResults/schema-catalog
```

| Gate | Command / evidence | Result and scope |
| --- | --- | --- |
| Release solution | `dotnet build OceanBaseConnector.slnx -c Release --no-restore`; `TestResults-schema-build.log` | Exit 0, 0 errors/0 warnings; all five product assets, consumer and integration entry points built. Existing restore assets/cache used; no new dependency or vulnerability-feed audit. |
| Complete units | `dotnet test tests/OceanBaseConnector.UnitTests -c Release --no-restore --no-build --logger 'trx;LogFileName=unit-final.trx' --results-directory TestResults/schema-catalog` | Exit 0, 3693 passed/0 failed/skipped, normal Windows certificate permissions. The preceding complete run had 3687 passed/6 stale metadata-assertion failures (`unit-pre-review.trx`), then the complete suite was rebuilt and rerun. Final duration 2m36s. |
| Affected consumers | `dotnet test tests/OceanBaseConnector.ConsumerTests -c Release --no-restore --no-build (filter below)`; `consumer-final.trx` | Exit 0, 66 passed/0 failed/skipped, including 16 new five-asset/mode/sync-async cases, each querying all supported new directories. This is the affected subset, not the complete consumer suite. The initial new consumer run had 8 Oracle fixture failures because result status invalidated session variables; reusing SessionTimeZoneFixture for the resulting probes corrected the fixture. |
| DI | `dotnet test tests/OceanBaseConnector.DependencyInjection.Tests -c Release --no-restore --no-build`; `di-final.trx` | Exit 0, 14 passed/0 failed/skipped. |
| Integration discovery | `dotnet test tests/OceanBaseConnector.IntegrationTests -c Release --no-restore --no-build`; `integration-final.trx` | Exit 0, 9 configuration cases passed/72 skipped. Added composite key/index/FK/view and Oracle type/sequence/synonym real-profile tests, plus all-advertised-collection discovery; real profiles were not configured. No real SQL/privilege/OBProxy pass inferred. |
| Changed C# format | `dotnet format OceanBaseConnector.slnx --no-restore --verify-no-changes --include <13 changed C# files>`; `TestResults-schema-format.log` | Final exit 0, workspace-load warning. Two switch-arm whitespace findings in the shared test sample were corrected; no behavior change. |
| Documentation | Local links/anchors, all 23 audit IDs, 8 work-package coverage, diff checks | Validated for the module documents; the previous gap IDs and release gates are preserved. |

Reports are under ignored `TestResults/schema-catalog/`, logs use `TestResults-schema-*.log`. The final complete-unit count was read from the actual TRX; skipped integration cases are not passes. No full consumer rerun, new package/runtime consumer matrix, live dictionary/permission/collation/older-version/OBProxy run or cross-platform acceptance was performed for this module. P1-10 is locally implemented with real compatibility still open; W6 also retains DataAdapter/CommandBuilder and parameter-inference work. The full completeness goal remains active.

## Failures and skips still requiring attention

| Item | Status / next verification |
| --- | --- |
| Oracle `complex.collection` | Still failed in the recorded full acceptance run, with a failed DROP callback and exit code 1. Preserve the failure and narrow server-side classification; do not suppress it or classify all collection operations as unsupported. |
| MySQL 27 / Oracle 15 skipped acceptance cases | Review each report's mode/configuration/environment reason. Counts cannot be promoted to passes; the original JSON files are currently unavailable. |
| Four integration gates | Run with both direct and OBProxy tenants and a dedicated unsupported-plugin profile. Missing environments currently allow an ordinary test run to skip all four. |
| Six deferred `matrix.*` cases | Currently report skips. Implement the controlled environments and assertions listed in Compatibility; invoking the console alone does not exercise these matrices. |

The current implementation ledger is also summarized in [the complete capability baseline](obconnector-dotnet-completeness.md): pooling, session-state disposal, `SchemaOnly`, local ambient enlistment, the opt-in LOCAL INFILE protocol path, pooled/direct client certificates with source snapshots, configurable TLS revocation checks, the custom server validation callback, opt-in standard MySQL compression and common Schema catalogs are implemented with local evidence; real enterprise CA/mTLS and revocation profiles, long-running pool concurrency acceptance, real MySQL compression and catalog acceptance, OB20 compression/reroute, active-session recovery and the real compatibility matrix remain open or explicitly unsupported. KeyInfo and DataAdapter/CommandBuilder have local implementations; real dictionary/CRUD/locking and runtime acceptance remain open. Credential provision/rotation is committed with local evidence but real service/plugin/KDC acceptance remains open. DataTypes has final local unit/consumer evidence; see the latest delivery record above. Automatic derivation has local delivery evidence; real catalog/type/DDL acceptance remains open.

The earlier Ubuntu CI record had two piece-data budget tests timing out. The TCP small-write fix passed its four focused Windows regressions; the maintainer later reported the CI issue resolved. No remote run was independently checked in this documentation change, so no fresh Ubuntu pass is claimed. Earlier .NET 9 fixture failures had subsequent recorded passing reruns; they are not listed as current failures.

## Remaining work

- Establish the supported OceanBase/OBProxy version matrix. The exact 4.4.2.2 identity restriction has been replaced by actual product-version and session-mode confirmation in both modes. Other versions remain unverified on real servers; feature minimums do not establish general compatibility.
- Complete trusted TLS/plugin success and rejection profiles, enterprise trust/custom validation, direct and pooled mTLS policy tests, actual OB20/tracing negotiation, controlled topology/network failures, LOCAL INFILE server/proxy matrices and coordinator crash recovery.
- Pooling now supports physical-session reuse, COM_RESET_CONNECTION/COM_INIT_DB reset, MySQL autocommit status validation on reset and borrow probe, prepared statement generation invalidation, unconsumed REF CURSOR and live LOB handle return-time disposal, recognized user session-state/temporary-object discard, maximum capacity, wait timeout and cancellation, minimum-pool prewarming with failed-warmup rollback, borrow-time COM_PING validation, idle/total lifetime reaping, per-open cancellation/generation cleanup, empty static-registry cleanup, pool lifecycle metrics, and loopback cross-borrower tests. Full resource-reset coverage and real OceanBase/OBProxy validation remain open.
- Verify large-value peak memory/throughput, cursor/LOB resource lifetime, complex-type DDL changes, catalog permissions and additional output shapes on each intended deployment profile. Selected basic round trips do not cover these combinations.
- Complete intended non-Windows/runtime deployment coverage; make an explicit .NET Framework support decision. Finish source-provenance/package-name review before publication.

## Integration suite configuration

The [integration suite](../tests/OceanBaseConnector.IntegrationTests/README.md) requires OB_REQUIRE_INTEGRATION=1 to run anything; CI and ordinary dotnet test runs always skip it. Configure target databases through a profiles file (OB_INTEGRATION_CONFIG), or fall back to the four legacy gate variables:

- OB_MYSQL_CONNECTION_STRING
- OB_ORACLE_CONNECTION_STRING
- OB_MYSQL_PROXY_CONNECTION_STRING
- OB_ORACLE_PROXY_CONNECTION_STRING

The legacy variables synthesize the four direct/proxy profiles for connection and identity suites; suites that create objects need a profiles file with capabilities.createObjects. Special endpoints (unsupported auth plugin, autocommit disabled) are declared as profile capabilities in the file. When integration is required, missing configuration fails instead of passing silently. See the [acceptance guide](../tests/OceanBaseConnector.Acceptance/README.md) for the separate database console; it does not replace these suites.
