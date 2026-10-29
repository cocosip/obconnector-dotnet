# Oracle binary InputOutput streams

Date: 2026-10-29. Base: `bcaf7fa`. P1-06 Oracle scalar output support.

## Behavior and OceanBase evidence

Readable binary Stream values are accepted as Oracle scalar InputOutput parameters
under Use Oracle Prepare Execute=true. They can infer or explicitly declare
DbType.Binary/OceanBaseDbType.Binary; Size/Precision/Scale remain zero.
OracleCommandPlan now keeps the original parameter for output publication and
allows the existing normalized Input snapshot to reach prepared-ID piece upload.
The product change removes only the obsolete direction rejection; no new codec,
public API, buffering path or dependency is introduced.

Prepare never consumes the source. Execution reads its current position to EOF,
without rewind/disposal. Reassign the InputOutput value to a source for another
execution, because completed output replaces it. Duplicate source references,
including Input/INOUT combinations, reject before reads/SQL. Failures retain old
outputs; failures before upload preserve the session, while staged upload errors
or cancellation retire it. Known final execution errors retain existing recovery
rules; an unknown terminal result destroys the session and is never replayed.

Official Connector/J 2.4.14, source SHA
`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`, and OceanBase Oracle 4.4.2.2
were checked directly. JDBC4ServerCallableStatement uses its Oracle prepared
execution; AbstractQueryProtocol uploads long inputs using COM_STMT_SEND_PIECE_DATA.
OBStreamParameter advertises STRING and writes binary content as hex pieces,
matching the existing .NET input stream contract. The first probe omitted Java's
required usePieceData=true and failed code0/null before execution; enabling the
documented option passed 0/1/20000/32765-byte inputs.

Both Java direct CALL and the identical anonymous BEGIN procedure wrapper were
tested at all four sizes. Direct CALL reports RAW metadata and original bytes.
The anonymous wrapper reports CHAR metadata and uppercase hex content; Java
getBytes returns the ASCII hex bytes in that shape, and its getObject additionally
uses registerOutParameter conversion. The .NET generated BEGIN wrapper follows
its existing metadata-driven output contract and publishes the exact hex string.
It does not promise byte[] based only on the input declaration. Local fixtures
also cover genuine RAW output metadata and byte[] publication.

The probe uses a separate cleanup connection for its owned procedure. Ignored
source, runner and sanitized log remain under TestResults/java-gap-probe:
OracleInOutStreamProbe.java, RunOracleInOutStream.ps1 and oracle-inout-stream.log.

## Verification

- Formal RED: 22 failures on the old pre-normalization rejection, 2 passing
  duplicate-reference boundaries. Initial asynchronous test waits hid immediate
  rejection as timeout; the harness was corrected before the recorded RED.
- Related units: 176/176, including all 24 new protocol cases. They cover empty
  and 20000-byte nonseekable sources, explicit Prepare, repeated execution,
  mutation snapshots, RAW/CHAR output metadata, atomic completion, duplicates,
  source/acknowledgement/execute/tail failures and cancellation/timeouts.
- Public DLL consumers: 54/54, including 36 new cases across representative
  netstandard2.0/netstandard2.1/net10.0 assets, sync/async, plain/synthetic OB20,
  RAW/CHAR metadata and malformed trailing completion.
- Initial direct run: 54/71 passed, 17 new tests failed because they assumed
  byte[] output. Official Java anonymous-wrapper metadata confirmed CHAR hex;
  fixtures were corrected without changing the product decoder. Second run:
  55/71 passed, 16 storage verifiers cast configured locator BLOBs as byte[].
  They now materialize OceanBaseLob after draining the reader, following LobTests.
- Five-target Release build: zero warnings/errors. DI: 14/14.
- Final direct storage run: 71/71, zero failures/skips (21 new Oracle cases,
  17 existing MySQL INOUT and 33 Oracle procedure/standalone-prepare cases).
  New success cases independently read both stored inputs after each execution;
  failure cases verify no PL insert, and batch verifies distinct sources.
- Initial complete default unit run was explicitly interrupted after finding an
  obsolete INOUT-rejection fixture and an existing oversized-discovery scripted
  server write racing the client's expected early close. The former now tests
  an invalid Size declaration; the latter tolerates IOException only on that
  oversized reply and additionally asserts that target SQL was never sent.
  Final focused gate: 101/101, zero failures/skips; final five-target Release
  rebuild: zero warnings/errors. The interrupted suite is not counted as green.
- Complete default units: 8502/8502, zero failures/skips. The suite's product,
  unit and shared-fixture inputs stayed unchanged throughout the run; the final
  review fix below affects only the separately built integration verifier.
- One fresh read-only final review found no Critical or Minor and one Important:
  the storage verifier required OceanBaseLob although eligible profiles need not
  enable locators. Added explicit locator-off/on verifier coverage: RED 16/32
  failed (expected OceanBaseLob, actual byte[]). The verifier now accepts actual
  byte[] or locator results, drains before reading content and disposes locators
  on every path. Expanded direct GREEN: 87/87, zero failures/skips (37 new Oracle
  cases, 17 existing MySQL INOUT and 33 Oracle procedure/prepare cases).
  No product change, second reviewer or extra unit-suite repetition was needed.
  No Critical/Important/Minor finding remains.

Ignored logs/TRX: TestResults/oracle-inout-stream/.

## Review rulings

- Final default suite and expanded direct gate must be terminal and green before
  commit; pending docs and evidence are finalized. Otherwise regressions or an
  incomplete delivery record could be hidden.
- Retain executed Java/direct evidence with its exact SQL-shape distinction;
  the reviewer did not independently rerun it. Misinterpreting that evidence
  would give callers an incorrect output-type promise.
- Actual column metadata remains authoritative; no JDBC registered-type CLR
  conversion is added. Callers expecting byte[] must handle actual CHAR hex.
- Real OB20/OBProxy/TLS/mTLS/enterprise auth and other versions/platforms remain
  unvalidated; direct Windows4.4.2.2 results cannot promise interoperability there.
- Real64MiB uploads, load/endurance, exhaustive fault points and pooled
  next-borrower operations remain open. Source review and representative tests
  do not prove all resource, recovery or reuse behavior.
- Blocking/cancellation-ignoring sources must return. An arbitrary user stream
  can delay interruption indefinitely; no forced interruption is introduced.
- BLOB/CLOB declarations, text streams, array/fallback combinations and input
  cursors remain excluded/rejected; callers must use supported input shapes.
- RAW size limits and empty-as-NULL semantics remain server-controlled; other
  servers may reject sizes or expose different limits. Tested sizes do not
  establish a universal maximum.

## Remaining boundaries

Evidence is Windows direct 4.4.2.2 with TLS Disabled. Real OB20/OBProxy/TLS,
other versions/platforms, real64MiB uploads, all transport/fault points and load
acceptance remain open. Arbitrary blocking/cancellation-ignoring caller sources
must return before execution can finish. Text streams, locator declarations,
array/fallback combinations and input cursors remain rejected. This module
does not claim BLOB INOUT declaration support or change ordinary byte[] binding.
