# Missing-feature and verification matrix

Updated: 2026-11-06; convergence baseline `7b8ca6b`. This table answers: **what is definitively missing now, what to do next specifically, and what condition counts as done**.
Capability scope is in the [feature matrix](obconnector-dotnet-progress.md); sources are in [scope decisions](obconnector-dotnet-missing-features-roadmap.md#scope-decisions);
implementation steps, historical tests, and commit evidence are maintained uniformly in the [development checklist](obconnector-dotnet-missing-features-roadmap.md).
This document organizes scope and tasks only; it adds no support claims and does not represent a database re-run or a completed driver.

## Scope and completion standard

- The target evidence baseline is **MySQL/Oracle tenant compatibility mode on OceanBase 4.4.2.2**; native Oracle/MySQL capabilities cannot be carried into this table directly.
- Every mandatory item must have OceanBase documentation/server-protocol/real-database evidence, an explicit .NET call contract, and a current implementation gap at the same time. A JDBC interface, a Java option, or Java supporting some combination is not sufficient to open an item.
- **Code gaps, under-acceptance, server/environment blockers, pending scope decisions, and completed specified scopes are accounted for separately.** Unproven combinations keep their record and cannot be reported as definitively missing, nor as completed or already excluded.
- G01–G15 are the fixed verification units. Existing D/A/S numbers serve only as implementation and evidence indexes; their historical subsection count is not the count of unfinished tasks. A passing case must not automatically add directions, nesting levels, APIs, deployments, or data-type combinations.
- New mandatory scope must first be written in this table with its OceanBase basis, usage scenario, impact surface, and bounded closing conditions, and be explicitly confirmed by the user; the completion standard cannot be widened while implementing.

**Feature development complete**: every in-scope code gap is implemented and passes repeatable unit/protocol tests and an independent review; undecided scope must remain explicit, and moving it out of the table cannot be used to declare everything complete. Long-run stability, real topology failures, and full platform testing do not block development completion; they serve as release evidence only when the corresponding capability is claimed for a planned release or production deployment.
**Release acceptance complete**: after the version/deployment/OS/runtime to be claimed as supported is fixed, the corresponding acceptance and cleanup all pass; a missing environment, quarantine, or skip/deferred does not count as passing that claim.
The two are reported separately; commit counts, test counts, or a parent item left at P2/P3 long-term are not a substitute for a conclusion.

## Items still not settled

| Nature | Existing IDs | What is currently missing | Next step |
| --- | --- | --- | --- |
| Client code path added; real-database protocol/permissions still await acceptance | G13 / A-03d | package-local PL/SQL RECORD now supports field-catalog derivation, scalar field descriptors, and atomic parameter publication; the current target still returns only top-level rows, so the explicit rejection continues | Obtain target server field rows and successful input/output wire, then run sync/async, NULL/failure, and least-privilege acceptance; keep the explicit rejection while field rows are unavailable |
| Code exists; extended real-database quarantine | G04 / D-04f2 | Root NULL for anonymous INTERVAL collections, stale output after failure, and independent transaction verification remain incomplete | First obtain the incident log and an isolated reproduction environment, then run the original cases; ordinary connection recovery does not lift the quarantine |
| Implemented paths lack real protocol/deployment evidence | G01, G09 | Native 1295 fallback; multi-send results for anonymous and some collection values have local evidence only | Obtain a native-rejection profile and reproducible real result metadata separately; stop adding synonymous fixtures that pass themselves off as a real database |
| Release evidence pending (does not block code completion) | G10, G11, G13, G14 | Long-run stability, real topology failures, RECORD success wire/least privilege, specific release claims, zero-date/long NUL boundaries | During development keep only repeatable short tests and boundary tests; run long tests or dedicated-environment acceptance only when a release claim involves the corresponding capability |
| Blocked on server or deployment prerequisites | G03, G04, G06–G08, G12, G15 | Specific failure shapes, SRS/package/version, TLS/protocol/proxy/authentication endpoints | Keep the existing failures and safe rejections; after the prerequisite changes, reopen only the corresponding row |
| Pending scope decision; not yet a mandatory feature | G02, G04, G08, G09 | Arbitrary Oracle script resource output, cross-statement locator, more PL/cursor/array pipelining, dedicated XML/spatial codecs, etc. | See the per-item disposition table below; decide inclusion first and do not start coding directly |

Candidate combinations cannot be merged into confirmed mandatory features. The included RECORD capability gap, quarantine acceptance, release debt, and blockers are still retained separately; this classification does not mean they are complete, and the whole-driver completion rate is not computed from it.

### Release gate for the next version

Two client boundary regressions were added: `DeriveParametersRejectsPackageLocalRecordWithoutFieldRows`
proves that a missing field catalog does not replace existing parameters, and `AnonymousOutputReexecutionClearsPreviousValueBeforeRootNull`
proves that re-execution does not use the previous anonymous collection output as input, and that a root NULL is published as `DBNull.Value` only after the complete response.
They close client safety boundaries only and do not constitute real server capability acceptance.

Therefore the next version still has two conditional gates:

1. If a release claim includes package-local `PL/SQL RECORD` binding, evidence for field rows, successful input/output wire, sync/async, NULL/error, and least privilege must be obtained; otherwise keep the explicit rejection and exclude it from the support claim.
2. If a release claim includes anonymous `INTERVAL` collection output, the server incident log and an isolated environment must be obtained, and the combination of root NULL, empty collection, stale output after an error, independent verification after commit, and sync/async/Prepare must pass; the current connection recovery cannot lift the quarantine.

When the above real-environment conditions are unmet, the code can be released as implemented safe rejection and local protocol boundaries, but the two items cannot be marked as real-database support.

## Current server prerequisite assessment

The only real configurations currently available are direct-connect profiles for MySQL/Oracle on OceanBase 4.4.2.2, all with TLS Disabled; there are no
proxy/ODP, multi-node/OCP/TNS, OB20/trace, or non-native authentication/KDC profiles. The following capabilities depend on these server
prerequisites and are uniformly marked **server-limited, not developed or fixed at this stage**. Local protocol fixtures, Java SPI, or a
single-upstream relay can only keep boundary evidence and cannot lift the freeze.

| Item | Current confirmation | Reason not developed for now |
| --- | --- | --- |
| TLS/mTLS/enterprise CA/revocation/rotation | Both real profiles explicitly have TLS Disabled | There is no real TLS endpoint or certificate chain, so code changes cannot produce OceanBase acceptance evidence |
| OB20/trace/standard compression | No OB20 profile; forced OB20 historically failed at the server with `Layer=mysql` | Server negotiation and bidirectional frames are missing, so local frame fixtures cannot replace deployment capability; compressed OB20 remains an exclusion |
| OBProxy/ODP/multi-node/OCP/TNS | No real proxy or discovery-service profile | A single-upstream relay cannot prove HA, backend reset, migration, or discovery service |
| Non-native authentication/KDC/credential rotation | Only direct connection with native accounts is currently configured | There are no plugin/KDC positive or negative endpoints; Java plugin registration does not equal OceanBase support |
| Oracle plain CP932/UJIS/EUCJPMS | Strict mappings for the three families, MySQL storage/results, and Oracle national are complete (D-07f/g/h); plain Oracle initialization is confirmed as 900/42000 by the same Java interaction | Only the successful subsets are closed per D-07f/g/h; the older SJIS/Big5/EUCKR failures and other deployments are still retained |

The freeze above does not delete existing APIs, rejection validation, negative tests, or Deferred records; after real endpoints are obtained, items are opened again per the acceptance criteria.

<a id="bounded-gap-matrix"></a>

## Fixed gaps, actions, and closing conditions (G01–G15)

"Specified scope completed" covers only the existing capabilities listed in the row; candidates, blockers, and under-acceptance items in the same row remain separately listed and cannot offset each other.

| ID / index | Current state and exact remainder | Specific action | Bounded closing condition |
| --- | --- | --- | --- |
| G01 / D-01, D-05b, A-05 | Controlled fallback for Oracle plain input and named complex input is implemented; tenant-native 1295 is release evidence only | During development verify the fallback with the existing controlled relay and unit boundaries; add a real profile only when compatibility with that tenant's native rejection is explicitly claimed | The absence of a native 1295 profile does not block code completion; existing controlled-relay evidence must not be described as tenant-native acceptance |
| G02 / D-02 | **Specified subset complete**: the listed inputs/multiple results in both modes, and MySQL scalar script OUT/INOUT/ReturnValue; **pending scope decision**: arbitrary Oracle script output, complex resource output, and cross-statement locator; script Prepare/cursor is rejected by the current server | Keep the existing script rejection; decide the contract for candidates first per the table below. Re-check server failures only after the version/acceptable SQL changes, and do not pass off DbBatch or a single PL block as script support | The existing subset is settled per the P4 evidence of D-02a1/a2/c1/c2/d1–d3; combinations not included are not added as must-pass items, nor claimed as supported |
| G03 / D-03 | **Specified subset complete**: REF CURSOR output, forward offset, and parent default value; **server limitation**: SELECT CURSOR columns currently fail at execute/FETCH | Keep the existing column-shape failure and pre-send protection; reopen child FETCH/release verification only when a successful column wire appears from the target OceanBase; JDBC scrollable/updatable ResultSet is not included | The accepted scope of D-03b1/b2/c remains complete; the column shape requires new real-database success evidence and cannot be replaced by a passing REF CURSOR OUT |
| G04 / D-04, D-04f2 | named OBJECT/collection, LOB locator Input with independent output, and the specified INTERVAL subset already have P4; D-04f2 is a dedicated quarantine-environment item; other combinations are not yet included | During development keep the existing deterministic guard and loopback; enable quarantined real-database acceptance only when preparing to release that anonymous INTERVAL combination; do not probe other known server failures in a loop | A missing quarantine environment does not block current development; D-04f2 can serve as release evidence only after the incident log, dedicated environment, and cleanup verification are all in place |
| G05 / D-05, D-05b | **Specified scope complete**: UROWID plain/PrepareExecute, NULL, OUT/INOUT/ReturnValue/RETURNING, and the listed text input | No new feature development; native fallback is accepted only with G01 and cross-deployment only with G12/G14, and tasks must not be attached twice | Keep the existing P4 and controlled-relay evidence boundary; do not reopen this feature without a new failure |
| G06 / D-06 | **Specified scope complete**: SRID=0 GEOMETRY Binary/catalog; **server prerequisite missing**: non-zero SRS | Provide an OceanBase profile that actually has a non-zero SRS and can round-trip, then independently read back raw SRID+WKB parameters/results/NULL/bad values; do not change the server SRS in tests | Independently stored results for the specified non-zero SRS agree in sync/async, or an explicit server rejection is recorded; a dedicated GIS API is not in the current mandatory scope |
| G07 / D-07, S-05 | **The specified subset of listed mappings is complete**; plain Oracle initialization for some character sets and other deployments have recorded failures | Re-check the corresponding failure names only after server initialization/enumeration changes; strict codecs are not replaced by a similar code page, and the character-set list is not expanded automatically | The success/failure conclusions for D-07–h are retained separately; there is currently no confirmed name that can be added directly, and other deployment acceptance is listed separately per actual profile |
| G08 / D-09–D-12 | **Specified generic paths complete**: XML/String, SDO/generic OBJECT, VECTOR/ARRAY/String, RoaringBitmap/Binary+SQL; ANYDATA/SPARSEVECTOR and some shapes currently fail on the server; full XML/spatial dedicated codecs are not included | Missing packages/versions only wait for the corresponding prerequisite; an additional codec must first justify the .NET use case that the existing generic path cannot satisfy and its real wire, and implementations are not added on the strength of a type name | The currently accepted generic subsets are settled as P4; blockers stay visible; dedicated codecs and other undecided nesting count neither as complete nor as confirmed code debt |
| G09 / D-08 | **The specified opt-in multi-send subset is complete**; real results for anonymous and some collection values are release evidence; same-group PL, cursor/array binding, and more complex text LOB are still not included | During development use the existing decoder, protocol fixtures, and accepted DML/LOB barriers; add real metadata acceptance only when a release claim includes anonymous/collection multi-send | A missing real result source does not block implemented code; undecided pipelining combinations stay out of scope |
| G10 / A-01 | **Release evidence**: 16-way concurrency/10000 borrow-return cycles and resource isolation exist; one-hour stability is a release/production gate | During development keep the short-time concurrency, resource-ceiling, cancellation, and cleanup regressions; run the 60 minutes only for a release candidate or production capacity assessment | The long-run stability report is not a condition for the current code feature completion; keep it deferred when no long-test environment exists |
| G11 / A-02 | XA lifecycle, the listed response loss, and controlled relay/worker failures already have evidence; real node/ODP/coordinator failures are a deployment item | During development keep maintaining the deterministic unknown-outcome, Recover, no-replay, and resource-invalidation tests; request a dedicated topology environment only when HA/node-failure recovery is claimed | A missing real node/ODP environment does not block client development; controlled relay results cannot be upgraded to real HA acceptance |
| G12 / S-01–S-03 | **Deployment under-acceptance/blocked**: TLS/mTLS, OB20/trace/standard compression, real ODP/discovery/multi-node | Provide certificate positive/negative endpoints, actual negotiation/bidirectional frames plus trace/collector, and real proxy/discovery profiles separately; execute per the S table in the development checklist and do not substitute downgrade or a single-upstream relay | Close only the deployment cell whose endpoints were obtained; original exclusions such as compressed OB20 and transparent migration are not turned into targets, and an unconfigured state is not claimed as unsupported by the server |
| G13 / A-03, A-03d | **Code path implemented**: package-local RECORD reads field rows, builds scalar descriptors, derives and publishes atomically; **under-acceptance**: the current server has no field rows, and real input/output wire, least privilege, and catalog permission boundaries are missing; the missing CHAR_LENGTH column and similar are recorded server limitations | The code reuses the existing complex descriptor/binding and atomic publication; missing fields, nested/duplicate/out-of-order/unsupported types are rejected before sending; after real fields and wire are obtained, add only targeted sync/async, NULL, failure, and permission acceptance, and never fabricate owner, field, or character length | Real-database support requires RECORD parameter derivation/binding, no publication on NULL/error, and sync/async to all pass with real field rows and successful input/output wire; the safe rejection when the current target has no field rows is already covered by boundary and unit tests. Permission case results must match the account's actual grants, and an administrator pass cannot be substituted |
| G14 / A-04 | The Windows package/five assets and the existing runtime subset are accepted; .NET Core/.NET already supports cross-publishing to Linux from Windows; real Linux machine database acceptance is needed only when a release explicitly promises Linux runtime behavior | Use Windows to complete the five-asset build, package consumer, and `dotnet publish` asset checks; add the corresponding real-machine report only when Linux real-machine/specific ORM behavior is claimed | The current release scope is settled per the managed .NET API and cross-publish results; real Linux machine database testing is not a prerequisite for code development |
| G15 / S-04 | **Awaiting server support evidence and endpoints**: non-native plugin/KDC/credential rotation; the existence of Java SPI is not enough to prove that the target OceanBase provides that authentication | First determine which plugins/mechanisms the target OceanBase version actually provides and configure positive/negative authentication and rotation endpoints, then test the existing authentication paths; do not add plugin APIs without grounds | Actually supported mechanisms complete positive/negative authentication/rotation/redaction and the corresponding platform verification; unconfirmed mechanisms remain pending evidence and count neither as a confirmed feature gap nor as excluded |

A-05's zero-date needs a dedicated SQL_MODE profile that allows zero dates; the tenant baseline is not changed. Long NUL needs execution/network diagnostics from the failing window
without repeatedly replaying long SQL. Both remain boundary acceptance debt; they keep the G01/G11 failures and the original A-05 records respectively, and cannot be reported as two new features.

<a id="scope-decisions"></a>

## Combinations not yet in mandatory scope: decide first, then develop

| Existing index | Undecided capability | Evidence/plan required before inclusion | Current action |
| --- | --- | --- | --- |
| D-02b/c | Script-level Prepare/cursor, arbitrary Oracle script OUT/INOUT/complex resource output | A script/protocol the target OceanBase accepts; explicit output position, publish only on complete success, failure prefix, and resource ownership after cancellation; a single PL block/multiple DbBatch do not substitute | Keep the rejection and current failure, not included; write a bounded implementation item only after evidence is obtained and the user confirms |
| Other D-04 directions | multi-statement locator, INOUT on a complex locator itself, unproven cursor/anonymous member shapes | Successful wire, type identity/NULL representation, LOB/cursor ownership and release; the current failure shapes such as 900/932 need new server grounds | Keep the accepted Input/independent-output subset and do not automatically fill in all its permutations |
| Other D-08 combinations | Plain anonymous input/declared output, same-group PL pipelining, cursor/array binding, complex text LOB | A successful protocol for a single plain command, group response order/error attribution, resource lifecycle, and a reproducible .NET case; ExecuteArray does not substitute for plain input | Existing guards/barriers are kept; the existence of a Java optional optimization or API must not be treated as an OceanBase mandatory contract |
| Extra D-09/D-10/D-12 scope | Full XML codec, dedicated SDO WKB/WKT/SRID API, sparse vectors, and more array nesting | A use case the existing String/Binary/OBJECT paths cannot solve, the target version's actual support, and parameter/result wire | Generic paths are supported per existing evidence; candidates do not count toward mandatory items or the completion rate |

The "not included" above keeps the scope-decision debt; it does not declare the original problem solved. After the user confirms inclusion, only the corresponding existing row may be modified,
together with fixed cases and closing conditions; the implementing agent must not derive a chain of new permanent subtasks on its own.

No TODO/FIXME/NotImplementedException-style empty implementation was found in the product source. The confirmed unused code is the A01 demo type,
3 zero-reference methods, and 2 obsolete UTF-8 shortcut properties. The existing NotSupported checks are not a completeness count, and
certainly not all pending development tasks.

## Real blockers that must be retained

| ID | Capability/mode | Recorded fact | Evidence needed to close |
| --- | --- | --- | --- |
| E01 | TLS/mTLS/authentication, both modes | TLS is not enabled on the current real tenants; enterprise CA/revocation/rotation/plugin/KDC not accepted | Run positive and negative handshakes after real configured endpoints are obtained; keep matrix.tls-authentication deferred |
| E02 | Oracle SJIS/Big5 | Initialization fails; official Java reproduces 900 as well | Confirm the version/server issue or have a new deployment pass; the case must not be deleted or turned into an unconditional skip |
| E03 | Oracle nested-table column | Creating the type can succeed, creating the column returns 600/0A000, reproduced in Java | Distinguish PL collection/object types from persistent columns, and re-verify after support in the target version is obtained |
| E04 | Oracle SELECT CURSOR column | The current scalar/multi-row column shapes return 902/1002 at execute/FETCH respectively, reproduced in Java as well; the actual text value does not match the existing four-byte parsing | The current shapes are skipped as a server limitation; handle them again only after a new, exact readable shape/wire is obtained, and neither metadata nor PL OUT can cover it |
| E05 | Oracle VIEW/SYNONYM | CREATE privilege 1031/HY000 | Verify again once the privilege exists; server support does not equal a grant for the current account |
| E06 | Oracle named TZ | Binary non-empty region name returns 1882; forcing Java server prepare fails as well | Server region file/version/mode combination support; a numeric offset pass does not equal a named region pass |
| E07 | Oracle long NUL multi-statement | 12003 characters are recorded as passing; a larger double SELECT times out in .NET/Java | Isolated server execution/SQL expansion budget, record the exact length, and do not promise unlimited script length |
| E08 | MySQL TIME extreme fractions | permissive binary truncates to whole seconds; strict text returns 1292 and binary still truncates successfully | State the path difference explicitly; do not silently truncate/CAST/retry to make the two look consistent |
| E09 | Plain prepare fallback | After the relay injects 1295, real storage passes; the tenant has not reproduced native 1295 | The native-rejection scenario needs separate acceptance; injected evidence closes only the client branch |
| E10 | OB20/trace/standard compression/ODP | Direct connection verifies only declined-ob20 and the fallback for undeclared compression; forced OB20 fails with Layer=mysql | Actual bidirectional target frames, trace control/sampling, with the collector verified separately; real ODP cannot be replaced by a single-upstream relay |
| E11 | MySQL zero date | A profile exists, but the current SQL_MODE prerequisite is unmet and no table was created | Verify storage/read-write with a dedicated profile using a suitable SQL_MODE; do not privately change the tenant baseline in tests |
| E12 | Oracle `ALL_ARGUMENTS.CHAR_LENGTH` | The target OceanBase 4.4.2.2 catalog lacks `CHAR_LENGTH`/`CHAR_USED`, and a direct query returns `904/42S22`; Java Connector/J returns the same code for the same query | Re-check after a target version/profile provides the character-length column or a stable equivalent catalog source; the client fallback is currently skipped |
| E13 | Oracle OBJECT/nested OBJECT NULL output identity | The current target version returns type 160 with the outer NULL bit false; Java `getObject/wasNull` likewise does not report the outer NULL | Re-check after a new result wire that can distinguish a root NULL is provided; client code changes are currently skipped, and an all-NULL attribute object is not converted to `DBNull` |
| E14 | Oracle named BLOB OBJECT member DML | On .NET 4.4.2.2 direct connection, flat/nested, 3/90007 bytes, and sync/async all reach the target DML and then return `932/22000`; the Connector/J public BLOB writer fails first, but a RAW descriptor wire replay from the same SDK, sending identical bitmap/length-encoded bytes, also returns `932/22000` with 0 rows | Client development is skipped for the current target version as a server limitation; if a new version/tenant provides a different member wire, compare Java/.NET again and reopen D-04a2 |
| E15 | Oracle named CLOB/BLOB OBJECT text result | After preloading the named OBJECT description, .NET safely rejects with an unsupported complex text member **2/2** in sync/async; for the same query Connector/J 2.4.14 reports metadata `1111/COMPLEX`, `getObject()` is null, and `getString()` yields only repeated text that cannot distinguish member identity | The text payload of the current target version has no reliable member identity, so the parser is skipped as a server limitation; reopen D-04e once a later version provides stable type identity/NULL/locator encoding |

Integration tests now carry these confirmed limitations as profile metadata in
`capabilities.serverLimitations`. Discovery-time skipping is limited to the named profile
and limitation; profiles without the declaration still execute the test and expose a changed
server behavior. This keeps server-limited cases visible without turning an unverified failure
into a passing client result.

Sources and historical run context are collected in [real compatibility and unaccepted deployments](compatibility.md).
This scope pass did not re-prove that these server states are still the same.

## Explicit exclusions

Not listed as missing implementations: native Oracle Net/OCI, LONG/LONG RAW/BFILE/NCLOB, Aurora/Galera, MariaDB bulk,
compressed OB20/proxy reroute, transparent active-session migration/automatic replay of unknown writes, distributed ambient promotion/generic durable coordinator,
REF CURSOR input, DataAdapter/CommandBuilder, and the undefined "other resource waits".
The grounds differ: some are officially unsupported, some lack a sufficient reference protocol, and some are choices the
product deliberately does not promise; they must not be written as one.

Closing a G/E item requires evidence per the [acceptance criteria](acceptance-criteria.md); failures may reveal driver
defects and cannot be attributed to the server wholesale in advance.
