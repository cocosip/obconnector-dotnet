# MySQL stored-procedure binary INOUT streams

Date: 2026-10-29. Base: `cee6637`. P1-05 command/routine support.

## Behavior and OceanBase basis

MySQL stored procedures accept readable binary `Stream` values with InputOutput
direction, inferred or explicit `DbType.Binary`, with zero Size/Precision/Scale.
The existing plan snapshots the value into a private-variable initialization
parameter with Input direction. It now uploads that value with the existing
bounded COM_STMT_SEND_LONG_DATA implementation before executing prepared SET,
then executes CALL and retrieves output through the existing hidden SELECT.
No new public API, dependency, text materialization or Oracle direction support
is introduced. Output values follow actual server result metadata.

Reference identity checks span both initialization and CALL input parameters.
One source cannot occupy two positions, even across those protocol stages.
Prepare does not consume sources. Execution reads their current position;
sources remain caller-owned. Subsequent execution requires the caller to supply
or reposition its source and reassign the InputOutput value after output replaces it.
Source failure before the first data frame preserves the session; failure or
interruption after staging data destroys it. No failed initialization executes
CALL or publishes outputs. Existing unknown-outcome and no-replay rules remain.

OceanBase MySQL 4.4.2.2 and official Connector/J 2.4.14 were checked explicitly.
The read-only Java checkout remains pinned to
`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`.
`JDBC4ServerCallableStatement.setInOutParamsOnServer` uses private SET variables
before CALL. Its current generic stream holder renders into SQL; the direct
CallableStatement stream INOUT probe returned `1064/42000`, including empty and
one-byte sources. The corresponding upstream `testStreamInOutWithName` is ignored.
This direct CallableStatement path is not claimed to pass.

The explicit Java prepared SET stream + CALL variable + SELECT path used by
the .NET design passed byte-exact input/output at 0, 1, 20000 and 1048583 bytes,
including binary zero/FF content. Its owned procedure was dropped through an
independent cleanup connection. This establishes OceanBase support for the
actual SQL/protocol composition, rather than assuming native MySQL parity.
The ignored Java source/runner and sanitized result are under
`TestResults/java-gap-probe/MySqlInOutStreamProbe.java`,
`RunMySqlInOutStream.ps1` and `mysql-inout-stream.log`.

## Verification

- Initial focused RED: 8 failures on the old MySQL Input-only rejection, 2
  passing duplicate-reference boundary cases. A parallel MSBuild invocation
  stalled before test startup and was stopped; the recorded RED used single-node
  MSBuild. No test failure is attributed to that launch stall.
- Initial GREEN attempt: 117 passed, 4 failed because the new synthetic binary
  column incorrectly advertised UTF-8. Correcting the fixture to binary
  collation 63 produced 121/121 related units. Product decoding was unchanged.
- Added caller-cancellation/timeout tests exercise initialization after one
  staged frame; they are included in the final default suite.
- Public DLL consumers: 48/48, including 24 new stream cases across
  netstandard2.0/netstandard2.1/net10.0, sync/async, plain/OB20 synthetic framing
  and legacy/deprecated EOF. Modern cases also exercise DbBatch.
- Direct integration: 50/50, zero failures/skips: 17 new INOUT cases, 30 existing
  MySQL routine cases, 3 existing Oracle routine cases. The new cases include
  empty/20000/1048583-byte sources, two executions per prepared/unprepared command,
  current position and ownership, complete/early-close readers, independent exact
  storage readback, scalar OUT, sync/async source failures before/after upload,
  and one batch case containing both sync/async executions with two distinct streams.
  Owned tables and procedures use independent cleanup connections.
- DI: 14/14. Five-target Release solution build: zero warnings/errors.
- Complete default units: 8478/8478, zero failures/skips, 8m21s. This includes all 12 new protocol cases, including the two interruption cases added after the focused run.
- One independent final read-only review found no Critical/Important/actionable
  Minor issues. No second review or product fix pass was needed. The complete default suite subsequently passed against the same frozen code.

Ignored logs/TRX: `TestResults/mysql-inout-stream/`.
Files: `red.trx`, `green-final.trx`, `consumers.trx`, `live.trx`, `di.trx`,
`build-final.log`, `full-units.log`/`full-units.trx`.

## Remaining boundaries

This is Windows direct 4.4.2.2 evidence with TLS Disabled. Real OB20/OBProxy,
other versions/platforms, synchronous sources that block indefinitely and the
full deployment/fault/resource matrix remain open. The 64 MiB per-execution
budget and source diagnostics/cancellation reuse the existing long-data path;
this module does not claim a real 64 MiB upload or injected server/transport
failure at every initialization boundary. Oracle InputOutput streams, text
streams and unsupported declared-type/array/fallback combinations remain rejected.

The executor retained each boundary the reviewer declined to judge:

- Real OBProxy/OB20/TLS and other versions/platforms: direct/synthetic evidence
  cannot certify them. Cost of extending this claim would be deployment failures;
  the matrix keeps their acceptance open.
- Indefinitely blocking synchronous or cancellation-ignoring sources: arbitrary
  caller code must return before execution completes, as the existing Stream
  contract documents. The caller may retain a busy operation until it does.
- Oracle INOUT/text streams and array/fallback expansion: the existing strict
  rejection remains; unsupported callers receive an explicit error rather than
  an unverified protocol extension.
- Real 64 MiB uploads and exhaustive initialization server/transport fault points:
  the existing byte budget and error machinery remain in use, but those live
  failure executions are not established. Resource/fault certification remains open.
