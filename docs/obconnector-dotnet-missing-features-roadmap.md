# OceanBase .NET Missing-Feature Development List

Updated: 2026-11-04. This document is based on the current source code and the OceanBase official
documentation, protocol, and live-database evidence pinned in [Scope decisions](#scope-decisions); the Connector/J 2.4.14
static comparison is reference only. Server-side status follows the compatibility report and, except
for the re-verifications explicitly recorded in git history, does not represent a re-run on
this date. It answers three questions:

1. Which capabilities have OceanBase official, protocol, or live-database evidence, and which versions and tenant modes apply;
2. Whether the OceanBase server has the corresponding capability, or the current environment lacks the prerequisite;
3. What .NET is missing now, what to do next, and what evidence can close the task.

This document is the **single tracking entry** for subsequent development. New tasks, status changes,
server-side blockers, implementation results, and closing evidence are all added directly here; no new
task document is split off for the same set of missing features. The gap matrix maintains current gaps,
dispositions, and bounded closing conditions; this document maintains the concrete steps, progress, and
evidence. Both statuses must agree, and historical execution records do not automatically generate the
next development task.

Scope admission: only database/protocol capabilities with an explicit .NET usage semantics, plus
ADO.NET behavior, are tracked; Java/JDBC-exclusive interfaces, JVM integration, and JDBC compatibility
configuration do not enter the gap list, candidate items, or completion rate — see the
[scope decision table](#jdbc-exclusive-interfaces-stay-out-of-the-net-matrix).
Java supporting a combination is not in itself a reason that .NET must add it; the calling contract and
protocol prerequisites still have to be confirmed separately.

The tracked baseline is `73ebf16`; current to-dos follow the
[fixed G01–G15 table of the gap matrix](obconnector-dotnet-completeness.md#bounded-gap-matrix)
and the [fixed execution checklist](#fixed-execution-checklist-and-completion-rules) in this document.
The D/A/S items and subsections below are an index of existing capabilities/evidence; they must not
automatically expand types, parameter directions, APIs, or deployment combinations merely because a
parent item is still marked P2/P3.

## Status definitions

| Status | Meaning |
| --- | --- |
| `Partially implemented` | Usable code exists but covers only an explicit subset; widening the scope requires a separate task and tests. |
| `Code missing` | The capability needed by the current target has no .NET product entry point or implementation. |
| `Awaiting evidence` | Java wire, OceanBase version, or real server behavior must be confirmed first; code cannot be written directly. |
| `Awaiting server` | The client path exists or can be retained, but there is currently no corresponding endpoint, protocol negotiation, or privilege, so development is paused. |
| `Awaiting acceptance` | Code exists; real deployment, long-running stress, failure, or platform evidence is missing. |
| `Excluded` | Explicitly outside this product's scope; no development task is created. |

Progress uses `P0` to `P5`:

| Progress | Closing meaning |
| --- | --- |
| `P0` | Not in implementation; either not started or still collecting scope, version, wire, and server evidence. |
| `P1` | Scope, version, wire, and server prerequisites confirmed; ready for implementation or acceptance preparation. |
| `P2` | A local protocol/API implementation or test exists, but a code-scope gap remains. |
| `P3` | Code and local/consumer tests are complete; target live-database acceptance is not. |
| `P4` | Functional acceptance for the specified server profile is complete; long-running stress or cross-platform evidence may still be missing. |
| `P5` | Within the previously fixed functional/deployment scope, code, documentation, tests, and closing evidence are complete; this does not mean every theoretical combination is supported. |

## Java, OceanBase, and .NET findings

### Java baseline for existing constrained capabilities

The table below answers "does Java ship it"; it does not equate one Java path with all combinations
being usable. Source entry points and pinned versions: [Java architecture analysis](obconnector-j-architecture-outline.md)
M06–M10 and [pinned evidence sources](#pinned-evidence-sources) J1–J6.

| Task in this document | Existing Java 2.4.14 capability | Parts that still need separate confirmation |
| --- | --- | --- |
| D-01 | client/server prepare, `emulateUnsupportedPstmts` fallback, and mode-specific parameter literals | .NET's exact 1295/HY000, safety boundaries, and each literal kind are still accepted against its own contract |
| D-02 | multi-statement, multi-result, client-side parameter expansion, and several batch paths | b's target server rejects script server prepare; c1 already covers MySQL scalar OUT/INOUT through client private variables; Oracle script output and the remaining resource directions stay constrained |
| D-03 | cursor fetch, REF CURSOR, scalar OUT/INOUT, conditioned offset | does not mean SELECT CURSOR column shapes, OUT offset, or REF CURSOR input are supported |
| D-04 | ObStruct/ObArray, complex descriptions, LOB/cursor reads, and a qualified writer | does not mean all named/anonymous members and locator re-binding are implemented |
| D-05 | string result path for ROWID type 209 | cannot be used to claim UROWID parameter/result is closed |
| D-06 | raw byte[] result path for MySQL GEOMETRY type 255 | cannot be used to claim dedicated input, an SRID contract, or a complete GIS API exists |
| D-07 | character set name to JVM charset selection, normal/national encoding paths | provides no independent interoperability proof for OceanBase's new collations and strict .NET mapping |

### Q20 multi-send/pipelining

This is a real optional capability of Java Connector/J 2.4.14, not an old-matrix guess. The Java
evidence is `DefaultOptions.java:470–490,553–560` and
`AbstractQueryProtocol.java:729–732,1287–1326`; the corresponding feature organizes several prepared
executions into a multi-send path, and it differs from MariaDB `COM_STMT_BULK_EXECUTE`. .NET has added
a default-off prepared batch multi-send subset covering input-only DML, basic scalar draining, plain
`ExecuteReader`, count/64 MiB grouping, and send-state protection; local loopback passes.
MySQL-mode `Stream` input is implemented: before each execution `COM_STMT_SEND_LONG_DATA` is sent
in the same protocol as Connector/J, then `COM_STMT_EXECUTE`; both the sync and async paths send the
complete group first and then drain responses in order.
Oracle piece-data needs per-piece confirmation and is not part of this pipelined subset; with batch
multi-send enabled, Oracle Stream input safely falls back to the existing per-command piece-data path
and is not disguised as response-less pipelining.

The 4.4.2.2 MySQL/Oracle direct temporary profile after VPN recovery covers DML/scalar × sync/async,
**8/8 passed**; during that work a packet-sequence reset at each pipelined response boundary on the
client was fixed. The new ExecuteReader Oracle direct profile passed **2/2** sync/async; the current
environment provides no MySQL profile, so MySQL reader results are not extrapolated. That evidence only
raises the first input-only subset to `P4`; it does not extrapolate to Oracle PrepareExecute,
output/LOB/cursor/array, streams, text fallback, proxy/OB20, gains, or a larger error-ordering matrix.

### T17 XMLType, SDO_GEOMETRY, ANYDATA

These three are server-side dedicated types or PL capabilities of OceanBase Oracle compatibility mode,
each with its own official documentation, so they cannot be written as "the server does not have them".
But their server-side existence does not mean Connector/J or .NET already has a generic value codec:

| Type | OceanBase assessment | Java 2.4.14 assessment | .NET assessment |
| --- | --- | --- | --- |
| XMLType | server-side Oracle-mode conditional capability; the current target has confirmed type 160 ownerless text and type 253 character-parameter wire | no generic XMLType wire/value codec confirmed; Java's SQLXML path explicitly rejects it | a String wire subset exists for the current version; no binary/complex XML dedicated codec |
| SDO_GEOMETRY | server-side Oracle-mode conditional spatial type | has a generic OBJECT/ARRAY complex-value path and an `SDO_` metadata query special case; no dedicated SDO/WKB/SRID contract confirmed | no dedicated codec, spatial API, or SRID/WKB contract |
| ANYDATA | server-side PL/system type with independent restrictions; cannot be used as an ordinary persistent column | no generic codec for ordinary columns confirmed | no dedicated codec; the need must first be classified as PL parameter/result or persistent column |

Therefore T17 still has to be split by shape, and the current XMLType subset cannot be expanded into a
complete codec. The current string wire subset of XMLType is `P4`, while binary/complex payloads and
more PL directions remain awaiting evidence; the SDO/ANYDATA status is in D-10/D-11. Official and Java
scope evidence: [pinned evidence sources](#pinned-evidence-sources).

### T18 VECTOR, RoaringBitmap, MySQL ARRAY

T18's original name mixed three different capabilities and must be split:

| Name | Assessment | New .NET task? |
| --- | --- | --- |
| Oracle ARRAY/collection | Java has `ObArray`/`ArrayImpl` and conditioned array binding; .NET already has the T14/Q16 named/anonymous array and DML array-binding subset | no duplicate task; folded into D-04 to widen the existing complex-value range |
| MySQL VECTOR/SPARSEVECTOR | official vector types and column DDL; sparse vectors are supported from V4.3.5 BP2 | added to D-12a; confirm the target tenant and the parameter/result wire before deciding on a dedicated codec |
| MySQL RoaringBitmap | official documentation provides an efficient compressed bitmap type for storing/processing unsigned integer sets | added to D-12b; the existing BIT(1..64) support cannot substitute for it |
| MySQL ARRAY | official support for `ARRAY(INT)` / `INT[]` columns and limited nesting; it is not an Oracle collection or a DML array binding | added to D-12c; .NET currently has no dedicated MySQL ARRAY contract |

**Both T17 and T18 have OceanBase server-side feature evidence and belong in this list.** T18 is not
"Java fully supports it but .NET forgot to write it": `ColumnType.ARRAY(161)` in the pinned Java version
clearly belongs to Oracle mode and cannot prove that MySQL ARRAY is implemented there; it is not
confirmed that this Java version has dedicated codecs for the three MySQL types above. .NET already
partially implements Oracle ARRAY, while the three MySQL types lack a dedicated contract and independent
acceptance, and support cannot be inferred from existing scalar or complex-value decoding.

On this date the [vector][T18-vector], [bitmap][T18-bitmap], [array overview][T18-array], and
[array column DDL][T18-array-ddl] pages were re-checked online at the same pinned documentation commit;
the last one lists supported elements and up to six levels of nesting. This confirms the server-side
feature exists; the two 4.4.2.2 tenants still do not represent every shape, but recent evidence confirmed on
the MySQL direct profile that RoaringBitmap queries return BLOB/byte[], and that `rb_build_varbinary`
can be called through the existing Binary parameter.

## Development task list

The tasks below list only the gaps that still have development value. Each item carries its boundaries,
so that all input, direction, mode, and protocol combinations are not expanded at once into an
unacceptable Cartesian product.

### Phase 1: Complete constrained combinations of existing capabilities

| ID | Current progress | Task | Needs change/verification | Prerequisites | Closing conditions |
| --- | --- | --- | --- | --- | --- |
| D-01 | `P3`, code commit `cca74fb` | Oracle normal client-prepare fallback (G01/Q04; local code module complete) | reuse the existing Oracle time/INTERVAL/NCHAR literals; fix the useless binary encoding for national; keep sync/async consistent | full local gate and independent review passed; native 1295, literal identity, encoding, time zone, and precision still await target live-database acceptance | store then read back independently on the native 1295 profile before `P4`; named complex-value normal/batch controlled fallback subset see D-01b; excludes OUT/cursor/LOB/anonymous and does not claim all combinations complete with `P3` |
| D-02 | `P2`; a1, c1/c2, and d1–d3 reach `P4` | widen dual-mode multi-statement parameters and multi-results (G02/Q05) | `OceanBaseCommand`, SQL boundary scanning, parameter expansion, and result consumption | keep the total SQL length, quoting, NUL, stream, and post-failure session-state boundaries; long-NUL instability retained | every supported mode/direction/type combination has result, failure-prefix, and suffix assertions; MySQL scalar script output see c1/c2; Prepare, cursor, Oracle script output, and complex input outside d1–d3 stay explicitly rejected or separately closed |
| D-03 | `P2`; the specified b1/b2/c subsets reach `P4` | Oracle cursor/output shapes (G03/Q10/Q11) | `OracleCommandPlan`, `QueryResult.Cursor`, `OracleCursorOwner`, Fetch/offset | a four-byte cursor/OUT subset exists; the SELECT CURSOR parent-row/child FETCH still fails; widening the scope needs real wire | the a/b/c split is in git history; the original single failing column out of 30/31 remains, and PL OUT success does not close the result-column gap |
| D-04 | `P2`; the specified a1/a3.1/b1/f/f1/f3/a4/k/l/m subsets reach `P4` | Oracle OBJECT/collection/complex LOB/cursor members (G04/T14/T15/Q16) | `ComplexValueCodec`, `ComplexTypeContext`, locator lifetime, RETURNING identity | sub-items are in git history; named root NULL input is fixed; BLOB OBJECT member DML is marked as a current server limitation after replaying the same-wire Java diagnostic; a4 completed single-row Text input-only automatic binding of named OBJECT/collection BLOB/CLOB locators and added loopback/direct evidence for collection → nested OBJECT shapes; D-08ac further completed batch multi-send acceptance for OBJECT, scalar collection, and nested collection same-shape input-only; root NULL OBJECT output identity, complex text, and other locator shapes are still open; SQL OBJECT and the current PL-only cursor member shape are marked as a current server limitation after Java/.NET failed identically | supported member directions, NULL, nesting, locator invalidation, DDL/reset, and old-resource release all have evidence; unknown identity is never misdecoded |
| D-05 | `P4`, specified direct profile only | UROWID and ROWID parameter/result closure (G05/T12) | new explicit Urowid parameter type, type209 input, and catalog mapping; old ROWID/String result compatibility | normal prepare/PrepareExecute, NULL/long values, OUT/INOUT/ReturnValue, RETURNING, and independent read-back accepted; see git history | acceptance on the specified Oracle 4.4.2.2 direct profile; client literal/multi-statement and controlled 1295 fallback subset see D-05b; cross-deployment, long-running stress, and A-03 catalog boundaries still retained |
| D-06 | `P2`; the specified direct subset a is `P4`, b awaits server SRS | GEOMETRY input and result boundaries (G06/T05) | reuse Binary parameter to bind back SRID+WKB; add spatial procedure catalog mapping and DataTypes parameter support; keep old result Binary identity compatible | SRID=0 multi-shape/NULL/bad value/output/independent storage verified; the current SRS catalog has only 0, and non-zero SRID rejection is in git history | a's scope and evidence are recorded in git history; b closes separately after obtaining a real non-zero SRS catalog and roundtrip; no GIS domain API, no server SRS configuration change |
| D-07 | `P2`; the specified successful subset is `P4`, Oracle EUCKR normal client not closed | character set and national/result mapping (G07/T19/T20) | `ClientCharacterSet`, `ResultCharacterSet`, strict codec, collation table | every new name needs a server enumeration, a strict .NET mapping, and a real roundtrip; a nearby code page cannot be faked | MySQL GB2312/EUCKR client/result and Oracle NCHAR/NVARCHAR2 national target direct acceptance passed; Oracle normal EUCKR initialization is rejected by the server with 900/42000 and retained; CP850/HP8/MACROMAN client and SWE7 result-only accepted in D-07e; CP932 specified successful subset see D-07f, normal Oracle initialization server 900 still retained; UJIS specified successful subset see D-07g; EUCJPMS specified successful subset see D-07h; other new names continue to be confirmed item by item before being added, and unsupported names are still rejected before sending |

### Phase 2: Optional capabilities and server types (admitted by OceanBase and .NET contract evidence)

| ID | Current progress | Task | Java/OceanBase boundary | Prerequisites | Closing conditions |
| --- | --- | --- | --- | --- | --- |
| D-08 | `P4`; the input-only DML/scalar/ExecuteReader subset of the specified 4.4.2.2 MySQL/Oracle direct profiles is accepted; also accepted are MySQL Stream storage/read-back, MySQL BLOB/TEXT results, Oracle Stream piece-data fallback, 1295 text fallback, Oracle named OBJECT/collection normal input, normal/nested OBJECT binary results, root BLOB/CLOB locator normal input, root/named complex LOB binary results with the caller connection retained, named complex LOB content input, Oracle DML RETURNING scalar OUT/INOUT, and named complex values, plus the LOB output subset with the caller connection retained; root remote LOB read window on a closing connection/data source reader **8/8** (D-08u); data source NonQuery root remote LOB drain/pool reuse **4/4** (D-08v); SequentialAccess reader dual-mode fields **8/8**, Oracle LOB window **16/16** (including the original 8) and XML **4/4** (including the original 2) (D-08w); whole-batch SingleRow/SingleResult dual-mode limited reads and Oracle hidden RETURNING **10/10** (D-08x); batch named OBJECT, scalar collection, and nested collection BLOB/CLOB locator input passed via constructor + hidden root LOB **12/12** (D-08ac) | multi-send/pipelining (Q20) | Java has an implementation; MariaDB bulk is out of scope; .NET is a default-off opt-in prepared batch send | the short-run gain comparison for plain prepared DML is recorded (D-08ad, dual-mode direct sync/async 4/4, 5 runs per configuration); wire, error ordering, gains on other workloads, cancellation, and unknown-outcome still have to be extended separately for the target profile; the current subset excludes Oracle autocommit remote DML LOB output (D-08z), complex text LOB results, PL same-group pipelined output, cursor, and array binding; Connector/J 2.4.14's Oracle piece-data likewise waits for per-piece confirmation and forms no pipelining protocol basis, so no implementation is guessed; proxy/OB20 and real-tenant native 1295 are still not accepted; token/batch cancel/timeout retain loopback ownership evidence, and the independent account check that withholds the second real response is completed for both direct modes **10/10** (D-08f1) | only this plaintext prepared DML subset is closed; non-cacheable SQL such as mixed StoredProcedure/Oracle PL and SET/DDL already has the execution barrier, the safe segment is still pipelined, and post-segment pre-checks are deferred until the full barrier response ends (D-08ab); dual-mode stored/OUT and Oracle anonymous collection OUT/time zone change direct **10/10** (D-08y), which does not mean PL same-group pipelining; root remote LOB, read-only PL INOUT, and inline RETURNING OUT for Scalar on an owned connection are supported, the batch retains the original connection, and re-execution is allowed only after all handles are released; direct 16 success paths and 8 transaction-boundary negatives passed (D-08z); anonymous complex Scalar is loopback only |
| D-09 | `P4`; 4.4.2.2 `oracle-direct` text+binary String wire subset; PL XMLTYPE OUT/INOUT confirmed as a server limitation; complete XML codec still not closed | XMLType wire investigation and conditional codec (T17) | ownerless `XMLTYPE` has a strict UTF-8 String wire; `SYS.XMLTYPE` metadata has a safe String consumption boundary; `DbType.Xml` parameters use the verified type 253 string wire; the same PL OUT/INOUT shape returns `600/0A000` and `600/42000` in Connector/J 2.4.14 and .NET respectively, so client-side extension is skipped as a server limitation | the current subset covers plain XMLTYPE columns, NULL, sync/async DML, metadata, PL function results, and explicit Prepare binary results; owned complex payloads, bad/maximum values, and cross-version evidence still need confirmation; PL OUT/INOUT is no longer a development item for the current version | keep the parameter/result/NULL/metadata contract of the current subset; extend only after obtaining stable complex XML identity and a different server wire, and never disguise a PL failure as client success |
| D-10 | `P4` (4.4.2.2 `oracle-direct` specified subset); whole-column SDO_GEOMETRY reads and generic OBJECT/ARRAY input under explicit Prepare accepted; dedicated spatial contract still not closed | SDO_GEOMETRY wire investigation and conditional codec (T17) | server-side conditional spatial type; Java has only the generic complex-value path and no dedicated SDO codec is confirmed | target version, wire, SRID, WKB/text representation, and a safe size ceiling | keep the generic OBJECT path; currently only whole-column SYS.SDO_GEOMETRY reads and generic OBJECT/ARRAY input are closed; if WKB/WKT, SRID, or invalid-geometry contracts are included, add independent wire/version evidence first; never pass GEOMETRY bytes off as SDO |
| D-11 | `P0 / current target server limitation`; ANYDATA PL package calls are rejected identically by Java/.NET on the same tenant | ANYDATA PL shape investigation and conditional codec (T17) | the current 4.4.2.2 target tenant has no `SYS.ANYDATA`; Connector/J and .NET both fail the same `CONVERT*` query | reopen only when the target version/tenant actually provides the `SYS.ANYDATA` package and gives the parameter/return wire; add no client fallback for a missing server package | the current target skips the client API as a server limitation; after a new deployment provides the package, define limited PL shapes, NULL, type names, and a failure-publication contract |
| D-12 | `P4` (the current-version generic path of a/b/c has been accepted separately) | wire and codec for MySQL VECTOR/RoaringBitmap/ARRAY (T18; sub-items below) | server-side features exist; on 4.4.2.2 the VECTOR/ARRAY wire is CHAR text and RoaringBitmap queries return BLOB/byte[]; Oracle ARRAY belongs to D-04 | one to six levels of INT ARRAY see D-12c1; the single-level String path of the other six documented element types see D-12c2; other nesting/versions, sparse vectors, and dedicated codec needs still require separate evidence | a/c reuse String and b reuses Binary for reads and writes through `rb_build_varbinary(?)`; no unproven dedicated codec is added |

D-12 is fixed to the generic paths listed as a/b/c below; each one's `P4` closes that
specified subset. Sparse vectors, other versions/nesting, or dedicated codecs do not automatically
widen the parent item and remain listed separately in the gap matrix as candidates or server blockers:

| Sub-item | Progress/confirmed | Concrete development and verification scope | Current gap |
| --- | --- | --- | --- |
| D-12a VECTOR (current-version dense) | `P4`, 4.4.2.2 MySQL direct generic String subset | `VECTOR(3)` DDL, NULL/text values, parameter binding, explicit/implicit Prepare, result metadata, and independent read-back; Java 2.4.14 also returns `CHAR`/`String` | the current version has no dedicated wire/type code; `SPARSEVECTOR` returns 1064 on the target server and is retained as a version boundary |
| D-12b RoaringBitmap | `P4`; 4.4.2.2 MySQL direct generic Binary/SQL-function subset | DDL, NULL/empty set, `rb_from_string`, 64-bit values, raw metadata, `rb_to_string`/`rb_to_array`, `rb_build_varbinary(?)` re-binding, and bad raw behavior accepted; real integration regression sync/async **2/2** | queries return `BLOB`/`byte[]`; binding `byte[]` directly to a `ROARINGBITMAP` column is rejected by the server with `1235/0A000`, a server-side prepared DML limitation; no dedicated provider type was added |
| D-12c MySQL ARRAY | `P4`, 4.4.2.2 MySQL direct generic String subset | `ARRAY(INT)` DDL, NULL/text values, parameter binding, explicit/implicit Prepare, result metadata, and independent read-back; Java 2.4.14 also returns `CHAR`/`String` | one to six levels of INT nesting, NULL, empty leaf arrays, and unequal-length sub-arrays accepted (D-12c1); two-dimensional top-level [] is rejected as a parameter and as a literal with 7602/22000; the remaining single-level TINYINT/SMALLINT/BIGINT/FLOAT/DOUBLE/VARCHAR accepted in D-12c2, with FLOAT text rounding and the VARCHAR non-JSON boundary retained; other nesting, dedicated parameter metadata, and versions still need confirmation and Oracle type code 161 cannot substitute |

### Phase 3: Deployment and failure acceptance for existing code

| ID | Current progress | Task | Current gap | Closing conditions |
| --- | --- | --- | --- | --- |
| A-01 | `P3`; MySQL/Oracle direct each passed 10000 rent/return cycles and resource isolation 52/52; the root cause of the continuous-run failure is undetermined and the long test is moved to release acceptance | pool, reset, lease, and prepared cache (G10) | 60-minute interleaving, pool clearing, and handle/thread/memory stability are unproven; the problem of the original exception being masked by test cleanup is fixed | 16 concurrent callers, pool ceiling, at least 10000 rent/return cycles, 60 minutes of interleaved running; stale reader/LOB/cursor/XA resources must not contaminate a new lease |
| A-02 | `P3`; MySQL/Oracle direct XA lifecycle, recover, and pool-discard recovery passed 28/28, ambient transaction COMMIT acknowledgement loss 16/16; MySQL PREPARE whole-packet success acknowledgement loss then independent recovery/rollback 2/2 (A-02c) | PSPE, XA, unknown outcome (G11) | the ambient-transaction InDoubt specified-acknowledgement-loss subset is accepted; XA prepared worker process termination and recovery before/after the decision is persisted is accepted per mode 6/6 (A-02b); other coordinators, total supervisor loss, node/proxy crashes, cancellation ordering, and branch recovery are still not fully covered | failure before sending/during sending/during response, independent account checking, XA recover/recovery after the decision, and no automatic replay all recorded |
| A-03 | `P3`; Oracle direct basic catalog/procedure/NUMBER 18/18, package overload 2/2, quoted package identifier 2/2; quoted formal parameter original-name derivation with spaces and procedure/package/function positional calls added **6/6** (A-03h); dual-mode function synthetic return-name avoidance added **8/8** (A-03i) | Schema, catalog, and parameter metadata (G13) | package-local PL types and minimum privilege still missing; case/quoted package identifiers covered; unconstrained NUMBER accepted, and A-03b's CHAR_LENGTH confirmed as a current target server catalog limitation; the current live-database failure for formals differing only in case is retained | dual-mode minimum privilege, unique/ambiguous signatures, more case/quoted identifiers, view/synonym privilege errors, and the remaining catalog boundaries are all closed against real server results |
| A-04 | `P3`; the specified subset of current Windows packages/runtimes accepted | packages, TFMs, and platforms (G14/R01) | five assets, independent net8/net9/net10 runtimes, net10 host for the two netstandard targets, and net48 host for netstandard2.0 re-verified; non-Windows and full API/ORM not accepted | every declared OS/runtime/asset has consumer, build, and target live-database records; loading under a netstandard host cannot replace an independent runtime |
| A-05 | `P3`, long-NUL re-check not closed | native prepare fallback, zero-date, extreme TIME, and long-NUL failure re-check | currently includes relay injection, SQL_MODE prerequisites, and server boundaries; a single-expression 196608-character NUL went from .NET 0/4 to 4/4, cause undetermined, see git history | native target profile, SQL_MODE, exact length, and text/binary paths recorded separately; failures are never rewritten as success |

### Original missing-matrix numbering map

This table keeps the relationship between old numbers and new tasks, so that development does not run
into "the task was split but the original matrix still shows it as unhandled".

| Original ID | New task | Note |
| --- | --- | --- |
| G01 | D-01 | Oracle normal client-prepare fallback |
| G02 | D-02 | dual-mode multi-statement and multi-result |
| G03 | D-03 | Oracle output, cursor, and offset |
| G04 | D-04 | Oracle OBJECT/collection/complex LOB/cursor members |
| G05 | D-05 | UROWID/ROWID closure |
| G06 | D-06 | MySQL GEOMETRY |
| G07 | D-07, S-05 | character set local mapping and real server roundtrip tracked separately |
| G08 | D-09, D-10, D-11, D-12 | T17 dedicated types and T18 version-dependent types first split into evidence tasks |
| G09 | D-08 | Java multi-send; .NET has implemented a default-off prepared batch multi-send subset, the specified direct subset is accepted, and the remaining combinations and deployments still await acceptance; non-cacheable SQL such as mixed StoredProcedure/Oracle PL and SET/DDL already has the execution barrier, the safe segment is still pipelined, and post-segment pre-checks are deferred until the full barrier response ends (D-08ab); dual-mode stored/OUT and Oracle anonymous collection OUT/time zone change direct **10/10** (D-08y), which does not mean PL same-group pipelining |
| G10 | A-01 | pool/reset/cache long-running acceptance |
| G11 | A-02 | transaction, XA, unknown-outcome failure acceptance |
| G12 | S-01, S-02, S-03, S-05 | TLS, protocol extension, discovery/HA, character set server prerequisites |
| G13 | A-03 | metadata and catalog boundaries |
| G14 | A-04 | packages, TFMs, and platforms |
| G15 | S-04 | non-native authentication, KDC, and credential rotation |

## Server-prerequisite tasks: not entering code development for now

The client paths and rejection guards of these items should be retained, but the current environment has
no sufficient evidence to support further code changes.

| ID | Capability | Current status | Reopen conditions |
| --- | --- | --- | --- |
| S-01 | TLS/mTLS/enterprise CA/revocation/rotation | both real profiles currently have TLS Disabled | a real certificate chain, hostname, client certificate, revocation, and rotation positive/negative endpoints |
| S-02 | OB20/Full Link Trace/standard compression | only ordinary protocol fallback now; forced OB20 fails because of `Layer=mysql` | a real OB20/trace/compression profile, bidirectional frames, and server sampling/collector evidence |
| S-03 | OBProxy/ODP/multi-node/OCP/TNS/backend migration | only a local parser, single-upstream relay, and controlled routing fixtures | a real proxy/discovery service, multi-node, backend reset, routing, and migration profile |
| S-04 | non-native authentication/KDC/credential rotation | only native-account direct connections now | server plugin/KDC positive and negative authentication, platform negotiation, and an actual rotation endpoint |
| S-05 | remaining new character sets | MySQL SJIS/Big5 have dedicated coverage; Oracle SJIS/Big5 still fail; CP850/HP8/MACROMAN and SWE7 results are completed (D-07e); CP932 mapping plus MySQL storage/result and Oracle national completed (D-07f), with normal Oracle initialization 900/42000 matched by Java; UJIS strict mapping and the same-scope 6-item successful subset see D-07g, normal Oracle 900/42000 matched by Java; EUCJPMS exact variants and the same-scope 6 successful subsets see D-07h, normal Oracle 900/42000 matched by Java; these names are now wired in item by item, and other deployments remain open | per-item server collation, strict mapping, and two-mode roundtrip; never extend the SWE7 result-only case into client SQL |

S-01 to S-05 are neither "never supported" nor code tasks that a local fixture, Java SPI, or
single-upstream relay can close. The currently open failures or prerequisites also include Oracle
nested-table columns, `SELECT CURSOR` columns, VIEW/SYNONYM privileges, binary non-empty region-name TZ,
the SQL_MODE prerequisite for MySQL zero-date, and the not-yet-reproduced native 1295; each must keep
its failed, blocked, or deferred state, and cases must not be deleted.

## Explicitly excluded

The following do not enter development progress: native Oracle Net/OCI wire, `LONG/LONG RAW/BFILE/NCLOB`,
Aurora/Galera, MariaDB bulk protocol, compressed OB20/proxy reroute, transparent active-session
migration and automatic replay of unknown writes, distributed ambient promotion/generic durable
coordinator, REF CURSOR input, DataAdapter/CommandBuilder, and "other resource waits" without defined
semantics. Each of them is an official limitation, insufficient reference protocol, or current product
scope choice — not a missed task.

## Fixed execution checklist and completion rules

Convergence baseline `73ebf16` (2026-11-04). The original "7 constrained, 1 pipelined, 3 types…" is
only a functional classification and cannot be used as the remaining development count; neither does
"the parent is still P2/P3, keep adding combinations" generate endless work. The current accurate
gaps/closing conditions follow the [gap matrix G01–G15](obconnector-dotnet-completeness.md#bounded-gap-matrix).
This section only schedules bounded actions for existing numbers and creates no new numbers or
development documents.

### Fixed execution checklist

| Existing ID / corresponding G | Exact incomplete item | How to proceed in order | What can be executed now / when to stop |
| --- | --- | --- | --- |
| A-03d / G13 | real field/protocol acceptance for package-local PL/SQL RECORD | field-row reading, scalar descriptor, atomic derivation, and binding are wired in code; field rows and a successful wire are necessary evidence to declare RECORD support; without that profile, keep loopback and safe rejection | the current target still has no field rows; successful wire acceptance may be deferred, but RECORD cannot be claimed as live-database supported |
| D-04f2 / G04 | originally planned anonymous INTERVAL collection extension acceptance | ①incident log + isolated environment; ②locate the execution phase of the original case; ③re-verify only root NULL/empty collection, old output after an error, in-transaction write plus independent account check after commit, and the originally planned sync/async and Prepare dimensions; ④check object cleanup and independent review | keep isolating for now and do not issue that SQL; connection recovery does not lift the block; close only when the original case passes, locate the failure first, and do not extend other PL collections |
| D-01, D-05b, A-05 / G01 | live-database evidence for the native 1295 fallback | ①provide the real rejecting SQL/profile; ②reuse existing normal-parameter/stored read-back cases; ③check fallback values/NULL, precision/encoding, and absence of false replay; change the product only for an actual defect | stop when the native rejection prerequisite is missing; the controlled relay is already accepted and is not repeated; do not extend output/cursor/LOB/anonymous |
| D-08s/j/n/p / G09 | implemented anonymous and partial collection results lack live-database evidence | ①obtain one real readable result and metadata source; ②reuse the existing decoder and check NULL/empty, exact values/order, errors, and LOB release; ③sync/async acceptance and independent review | currently no reliable shape source; do not keep guessing SELECT/CAST variants or adding equivalent loopback. Existing OBJECT/LOB P4 is not reopened |
| A-05 / G01, G11 | zero-date prerequisite, long-NUL instability, and extreme TIME path boundaries | ①for zero-date, use a dedicated profile that allows zero dates; ②for long NUL, first collect execution/network diagnostics for the failure window and then reproduce at a fixed point; ③retain the text/binary server difference for TIME and never silently change values | do not change tenant SQL_MODE, do not replay the incident or loop long SQL; none of these can currently be declared a client defect or a completed acceptance |
| A-01 / G10 | release/production stability evidence | at development stage reuse short PoolSoak, resource ceilings, cancellation, and cleanup regressions; run per-mode 16 concurrency, Max4, 10000 rent/return, 60 minutes, and pool/resource interleaving only at release-candidate or production-capacity assessment | long tests do not start now; the long-stability report is release evidence, not a prerequisite for client code completion |
| A-02 / G11 | real cancellation/timeout ordering and node/ODP failure for release/deployment | at development stage maintain deterministic unknown-outcome, Recover, no-replay, and resource-invalidation tests; request a dedicated topology environment only when HA/node-failure recovery is declared | a missing real node/ODP environment does not block client development; controlled relay results cannot be upgraded to real HA acceptance |
| A-03 / G13 | minimum-privilege account catalog and rejection boundaries for release/deployment | at development stage keep unauthorized errors un-faked, safe rejection on missing fields, and atomic publication; provide a dedicated account and run acceptance only when a minimum-privilege deployment is declared | no dedicated account does not block client development; the existing overload/Unicode/quoted-parameter subsets are accepted, and no new name combinations are split off |
| A-04 / G14 | cross-publish assets and actual runtime declarations | ①fix the five assets, package consumers, and Linux `dotnet publish` results on Windows; ②add the corresponding live scenarios only when a release statement involves real Linux machines or a specific ORM; ③record asset/hash/actual runtime | managed .NET APIs can currently cross-publish Linux from Windows; real Linux machines/full API/ORM are not prerequisites for code development and are accepted only when written into a release statement |
| S-01 / G12 | TLS/mTLS/certificate positive and negative endpoints | provide a real trusted chain, hostname/expired/untrusted/missing-client-certificate negatives, and rotation/revocation configuration; validate with the existing security gates and do not downgrade | no endpoint now, so the deployment stays unaccepted; a local self-signed fixture cannot close the item |
| S-02 / G12 | real OB20/trace/standard compression negotiation | provide the corresponding profile and check bidirectional frames with sequence/boundaries, actual trace sampling/collector, and compression; test only the protocol combinations declared supported | stop when there is no real negotiation; ordinary fallback does not equal protocol acceptance; compressed OB20 remains excluded |
| S-03 / G12 | real ODP/discovery/multi-node | provide proxy/discovery endpoints and controllable switching; verify with fixed routing/backend reset/failure recovery cases and never replay unknown writes | a single-upstream relay does not close the item; transparent active-session migration remains excluded and no HA mechanism is added automatically |
| S-04 / G15 | target OceanBase non-native authentication and rotation evidence | first confirm what plugins/mechanisms that version actually provides, then provide positive/negative authentication/KDC/rotation endpoints, and finally validate the existing driver path | the existence of a Java SPI is not OceanBase support; do not code against an unconfirmed mechanism or report it as a definite required feature |
| D-03, D-04 known shapes, D-06b, D-07/S-05, D-11/D-12 version boundaries / G03/G04/G06–G08 | recorded limitations for SELECT CURSOR/nested-table columns, root NULL/complex members, non-zero SRS, some character sets, ANYDATA/SPARSEVECTOR | when server version/wire/SRS/package/initialization behavior changes, re-verify only the corresponding existing failure shape; judge whether the client really has a gap only after success | keep the limitations for now and pause code changes; do not derive that the target OceanBase supports something from native Oracle/MySQL capability in Java |

### Undecided-scope combinations do not enter the implementation queue

Oracle arbitrary script output, cross-statement locator, complex locator's own INOUT, PL same-group
pipelining, normal anonymous input/declared output, cursor/array-binding extension, complete XML/dedicated
spatial codec, and additional array nesting are all handled by the
[scope decision table of the gap matrix](obconnector-dotnet-completeness.md#scope-decisions).
These records are neither deleted nor marked complete; after OceanBase evidence, a clear .NET use case,
and bounded closing conditions are available, the corresponding existing row is changed only with
explicit user confirmation. "It could be extended" is not "it must be developed further".

### Definition of done

1. **Close item by item**: check implementation, tests, independent review, and documentation against the bounded conditions listed in the gap matrix; do not substitute test counts, commit counts, or subsection counts for functional results.
2. **Separate the stages**: report feature implementation, target live-database acceptance, and release deployment/platform acceptance separately. Blocked, isolated, open, and deferred items stay visible and are not counted as passing; long-running stability the user deferred is not disguised as complete.
3. **No automatic refill**: stop an item once its admitted subset is complete; without a new failure or a user-confirmed new scope, do not add member directions, nesting, APIs, data sizes, or deployment permutations.
4. **The overall conclusion is still unproven**: if admitted code gaps and necessary acceptance are not closed, do not declare the whole driver complete; open scope cannot be deleted or marked excluded by the agent itself to obtain a completion rate.
5. **History is only for tracing**: statements in the old records kept in git history such as "continue later", "parent still P2/P3", or "more combinations" are no longer automatic task instructions; the next step can only come from this section and the corresponding row of the gap matrix.

## Scope decisions

### Scope rules

1. The product is an OceanBase ADO.NET provider supporting MySQL/Oracle **tenant compatibility modes**; it does not pursue full native database feature parity. The authenticated state determines the mode; configuration only validates expectations and does not change the tenant.
2. Official documentation describes server capabilities; the Java source describes the reference client path; local source describes the .NET implementation; live reports decide whether a concrete deployment is accepted. The four cannot substitute for each other.
3. JDBC/ADO.NET are adapted by semantics. The absence of DataAdapter/DbBatch/DiagnosticListener in Java is not a reason to delete anything; the presence of NClob/SQLXML methods or MariaDB options in Java is not proof that OceanBase supports them.
4. Connection, command, parameter, reader, precise types, local transactions, errors, and resource safety are core; pool/cache/batch, LOB/complex types/XA, and discovery/protocol extensions keep only the evidence-backed optional subsets.
5. SQL/DDL/PL packages, privileges, backup, replication, partitioning, and the optimizer are server capabilities. The driver is responsible for sending, binding, decoding, and returning errors; it adds no simulated implementation or dedicated API per syntax item.
6. Unconfigured TLS, OB20, and the like are under-accepted; combinations that are definitively rejected keep their validation. Gaps cannot be closed by deleting tests, changing skips, or lowering security levels.

### JDBC-exclusive interfaces stay out of the .NET matrix

Reviewed 2026-11-04: the feature matrix compares OceanBase database/protocol capabilities and their
ADO.NET usage semantics, not the number of Java classes, methods, or configuration items. Java source
can only provide reference-implementation evidence; a new capability must also state the .NET calling
scenario and the applicable database/protocol or ADO.NET contract evidence. The mere existence of an
interface in Connector/J cannot make it a .NET core item, candidate gap, missing implementation, or
pending acceptance, and it does not count toward the completion rate.

| Java/JDBC item | .NET scope decision | Retained actual capability / corresponding entry point |
| --- | --- | --- |
| `ResultSet` scrolling, sensitivity, updatable interfaces and the `TYPE_SCROLL_*` / `CONCUR_*` constants | do not replicate the JDBC result-set contract; scroll navigation and write-back through the result set are not current product goals | Q08/Q09 forward-only `DbDataReader`; Q10 server-side batched FETCH; SQL UPDATE is still executed by the command |
| `Statement.setFetchSize`, `ResultSet` / `RefCursor` class names and method shapes | same-named APIs are not required; the underlying cursor capability must not be excluded because of this | Q10 already has `OceanBaseCommand.FetchSize`; Q11 already has `OceanBaseRefCursor` output and reader; `SELECT CURSOR(...)` is another server-side result shape |
| `PreparedStatement`, `CallableStatement`, `DatabaseMetaData` interfaces and JDBC type constants | do not port methods one by one or copy Java type codes | Q02/Q03/Q06 parameters, Prepare, procedure/function and OUT; Q07/M01 parameter derivation, GetSchema, and column metadata |
| `java.sql.Array` / `Struct`, `SQLData` / `SQLInput` / `SQLOutput`, `Connection.getTypeMap/setTypeMap` | add no Java object-mapping interface or type-map registry | T14/T15 OceanBase OBJECT/collection description, binding, and decoding are retained per the defined .NET value contract |
| `java.sql.SQLXML` / `NClob` interfaces and factories/setters | do not require dedicated Java-style objects or same-named APIs | T11 national character semantics and T17's server-side XMLType String subset are judged independently; excluding NCLOB from the server-side scope has its own basis |
| `DriverManager`, `jdbc:` URLs, `ServiceLoader` / `META-INF/services`, OSGi/JNDI registration | do not port JVM driver discovery or container integration | C02/C03 .NET provider factory, DataSource, and DI are retained per the ADO.NET contract |
| `javax.sql` connection pooling, events, and XADataSource interfaces | do not port Java pool classes, events, or container contracts | C10/C11 physical connection pool, reset, and lease isolation are retained per the .NET contract |
| `Wrapper.unwrap/isWrapperFor`, Java JMX, and the Java SPI shapes of authentication/credential/JSSE | do not port Java interfaces, registration, or runtime management mechanisms | C05–C09 TLS/authentication/credential behavior and M02/M03 .NET diagnostics are retained; the existence of an SPI does not prove server support |
| Java `XAResource` / JTA enlistment interfaces | do not promise Java transaction-manager integration or API alignment | X03 database XA commands/recover are retained; X02 is a separately defined .NET single-resource TransactionScope, not a distributed promotion |
| `compatibleMysqlVersion` / `compatibleOjdbcVersion` | JDBC client behavior compatibility configuration, not an OceanBase version capability; not accepted, no development item | C01 real server product/tenant mode identification is retained |

Reviewing the existing C/Q/T/X/P/M/R and G01–G15 numbered items found none of the above Java-exclusive
interfaces listed separately as a missing implementation; this refinement tightened candidate admission
and acceptance wording and clarified the meaning of the cursor for Q10/Q11/G03 without deleting any existing
database feature. multi-send is a protocol optimization implementable across clients, not a
JDBC-exclusive feature. The remaining cursor/array/PL combinations must still have their .NET usage
contract and protocol prerequisites confirmed item by item, and a combination Java supports does not
automatically become a mandatory .NET item.

### Pinned evidence sources

Pinned versions and commits: Java Connector/J **2.4.14**, commit
`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`; the OceanBase official documentation repository read online
at V4.3.5, commit `2a5ae3a23a51729d065cec716508eb26cb064106`; the `v4.4.2_CE` charset check uses commit
`e859d1b9c9a6d11d856f4fed5b6385f1a6795820` (`ob_charset.h`). The documentation version and the
historical live database 4.4.2.2 are different dimensions, cannot substitute for each other, and do not
mean all versions are supported.

The `v4.4.2_CE` check found that GB2312, UJIS, EUCKR, EUCJPMS, CP932, CP850, HP8, MACROMAN, and SWE7
enter the server charset enumeration only in that version; GB2312 and EUCKR were therefore added
with strict .NET mapping evidence, while UJIS and the other names remain candidate gaps. The Java 2.4.14
character-encoding path supports selecting a JVM charset by name but provides no independent
interoperability proof for these new OceanBase tables. The official text below comes from the OceanBase
repository at the pinned commit; the documentation is not complete proof of 4.4.2.2 live-database
support.

| ID | Document | Scope constraint |
| --- | --- | --- |
| O1 | [compatibility modes][O1] | tenant mode cannot be switched; the community edition is MySQL only |
| O2 | [MySQL compatibility comparison][O2] | types, views, and syntax differ; version-dependent arrays/vectors/bitmaps cannot be assumed supported |
| O3 | [Oracle compatibility overview][O3] | SQL/PL/catalog/object support has boundaries; server-side administration features are not driver APIs |
| O4 | [Oracle SQL types][O4] | LONG, LONG RAW, BFILE, and NCLOB are explicitly unsupported; ROWID/UROWID, BLOB/CLOB, precise time/interval are valid |
| O5 | [Connector/J][O5] | one driver recognizes both modes, shares the protocol, and mainly provides connect/execute/call/transaction |
| O6 | [XA transactions][O6] | OceanBase is a resource manager; global decisions and recovery belong to the application/transaction manager |
| O7 | [SSL configuration][O7] | TLS is a deployment capability requiring server configuration and certificates; currently off does not mean absent |
| O8 | [XMLType][O8] / [SDO_GEOMETRY][O9] / [ANYDATA][O10] | server-side dedicated types have independent restrictions and do not automatically promise a .NET dedicated codec; ANYDATA cannot be an ordinary persistent column |

Paths in the Java-source table are relative to `src/main/java/com/oceanbase/jdbc/` of the pinned version
[obconnector-j][J0]. Structural interpretation: [Java architecture analysis](obconnector-j-architecture-outline.md);
it is not .NET acceptance evidence.

| ID | Path / line range | Verified fact |
| --- | --- | --- |
| J1 | `internal/protocol/AbstractConnectProtocol.java:823–825,908–926,1778–1785` | authentication identifies the mode; compression/OB20 selection; PrepareExecute and offset have mode/option/version conditions |
| J2 | `OceanBaseConnection.java:776–801,1614–1615`; `BasePrepareStatement.java:972–974,1034–1035` | shared execution; SQLXML explicitly rejected; the NClob setter is only a Clob alias |
| J3 | `internal/ColumnType.java:69–118`; `internal/com/read/resultset/rowprotocol/BinaryRowProtocol.java:1608–1611` | mode types; ROWID string, MySQL GEOMETRY raw byte[] |
| J4 | `JDBC4ServerPreparedStatement.java:819–832`; `JDBC4ServerCallableStatement.java:206–232,284–303` | Oracle array binding has preconditions; scalar OUT/INOUT is valid, which is not the same as REF CURSOR input being valid |
| J5 | `util/DefaultOptions.java:470–490,553–560`; `internal/protocol/AbstractQueryProtocol.java:729–732,1287–1326` | multi-send is indeed implemented; the bulk description targets MariaDB and the execution point notes server non-support |
| J6 | `extend/datatype/ComplexUtil.java:126–140,190–240`; `BinaryRowProtocol.java:2621,2632` | the LOB read side is not writer support; complex input supports only concrete member types |
| J7 | `internal/io/input/Ob20PacketInputStream.java:228–238`; `internal/protocol/OceanBaseCapabilityFlag.java:102` | this baseline rejects compressed OB20; the reroute constant does not imply an execution implementation |
| J8 | `OceanBaseXaResource.java:162–180,258–287,299–312,346–396` | MySQL XA/Oracle DBMS_XA; Java itself also has boundaries for forget/timeout/joins |
| J9 | `credential/CredentialPlugin.java:52–68`; `AbstractConnectProtocol.java:553–555,593–595`; authentication SPI registration | credentials are taken at connect time, not rotated live on an active connection; the existence of a client plugin does not prove two-mode server support |
| J10 | `internal/util/constant/HaMode.java:53–58`; `internal/protocol/AuroraProtocol.java:83–86` | inherited Aurora/Galera mechanisms are not a required OceanBase scope |
| J11 | `OceanBasePoolDataSource.java`, `internal/util/pool/`, `internal/failover/`; `UrlParser.java:280–291` | pool/reset and limited failover, TNS/OCP deployment entry points; not transparent active-session migration |
| J12 | `util/DefaultOptions.java:189,258,401–428,548–551`, `internal/protocol/tls/` | TLS/keystore/CA/hostname configuration is implemented but still needs real deployment acceptance |
| J13 | `internal/protocol/AbstractQueryProtocol.java:2390–2433` | LOCAL INFILE has application streaming/authorization and batch restrictions |

Correction of an old conclusion: **multi-send is an existing optional Java optimization, not an
unsupported feature** (see D-08). .NET now has a default-off prepared batch input-only subset with the
specified direct acceptance; remaining protocol combinations and evidence are in D-08. MariaDB
`COM_STMT_BULK_EXECUTE` does not enter the current target, and the two cannot be merged into one
exclusion. The absence of a same-named DataAdapter in Java cannot prove it is useless; keeping it removed
is a scope decision of the current product to use explicit commands.

## References

- [Feature capability matrix](obconnector-dotnet-progress.md)
- [Gap matrix](obconnector-dotnet-completeness.md)
- [Scope decisions](#scope-decisions)
- [Java architecture analysis](obconnector-j-architecture-outline.md)
- [OceanBase real compatibility and blockers](compatibility.md)
- [Acceptance criteria](acceptance-criteria.md)

[T18-vector]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/640.ob-vector-search/700.ob-vector-search-reference/100.ob-vector-data-type.md
[T18-bitmap]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/500.sql-reference/100.sql-syntax/200.common-tenant-of-mysql-mode/100.basic-elements-of-mysql-mode/100.data-type-of-mysql-mode/1000.roaring-bitmap-data-type-of-mysql-mode/100.roaring-bitmap-data-type-overview-of-mysql-mode.md
[T18-array]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/500.sql-reference/100.sql-syntax/200.common-tenant-of-mysql-mode/100.basic-elements-of-mysql-mode/100.data-type-of-mysql-mode/1200.array-data-type-of-mysql-mode/100.array-data-type-overview-of-mysql-mode.md
[T18-array-ddl]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/500.sql-reference/100.sql-syntax/200.common-tenant-of-mysql-mode/100.basic-elements-of-mysql-mode/100.data-type-of-mysql-mode/1200.array-data-type-of-mysql-mode/200.create-array-columns-of-mysql-mode.md

[O1]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/100.oceanbase-database-concepts/300.multi-tenant-architecture/200.compatibility-mode.md
[O2]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/100.learn-more-about-oceanbase/500.compatibility-with-mysql.md
[O3]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/100.learn-more-about-oceanbase/400.compatibility-with-oracle/100.overview-of-compatibility-with-oracle.md
[O4]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/100.learn-more-about-oceanbase/400.compatibility-with-oracle/200.sql-data-type.md
[O5]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/1300.drivers/500.oceanbase-connector-j.md
[O6]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/100.oceanbase-database-concepts/800.transaction-management/100.transaction/900.xa-transactions.md
[O7]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/800.configuration-items-and-system-variables/100.system-configuration-items/300.cluster-level-configuration-items/20800.ssl_client_authentication.md
[O8]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/500.sql-reference/100.sql-syntax/300.common-tenant-of-oracle-mode/300.basic-elements-of-oracle-mode/100.built-in-data-types-of-oracle-mode/1100.xml-date-type-of-oracle-mode/100.xml-data-type-overview-of-oracle-mode.md
[O9]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/500.sql-reference/100.sql-syntax/300.common-tenant-of-oracle-mode/300.basic-elements-of-oracle-mode/100.built-in-data-types-of-oracle-mode/1200.spatial-data-type-of-oracle-mode/100.spatial-data-type-overview-of-oracle-mode.md
[O10]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/600.pl-reference/300.pl-oracle/1400.pl-system-package-oracle/27800.anydata-type-oracle/100.anydata-type-overview-oracle.md
[J0]: https://github.com/oceanbase/obconnector-j/tree/6adc311ba5db8b2f19f98f3e6e4734667fdfdda1
