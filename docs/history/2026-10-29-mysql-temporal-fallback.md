# MySQL temporal single-statement client prepare fallback

Date: 2026-10-29. Base: `1ce67ac`. Scope: existing opt-in fallback, MySQL Input only.

## OceanBase and official-client evidence

Official Connector/J2.4.14 source is pinned at
`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`; runtime jar SHA256 is
`3168E7E3F2F14F35738D7C695E05012BF351F623757C7BAADA29659D2AFA2AD4`.
DateParameter/TimestampParameter/TimeParameter emit quoted ASCII temporal values.
A new single-INSERT client-text probe passes four combinations on OceanBase MySQL4.4.2.2:
SQL_MODE empty/NO_BACKSLASH_ESCAPES and session time_zone +00:00/+08:00. A separate UTC
observer verifies DATE, DATETIME(6), TIMESTAMP(6), ordinary fractional TIME and SQL NULL storage.
The JDBC typed setters cover ordinary Date/Timestamp/Time; negative multi-day TIME and zero dates
use setString and are not evidence of JDBC typed Time/Date equivalence. Owned tables are dropped
independently. Credentials stay private; raw database errors are suppressed.

Ignored evidence: TestResults/java-gap-probe/MySqlTemporalFallbackProbe.java,
RunMySqlTemporalFallback.ps1, mysql-temporal-fallback.log and
mysql-temporal-fallback-boundary-red.log. The Java checkout is unchanged.

## Implementation and retained boundaries

ClientPreparedSql enables its existing IsClientTemporal/Temporal branch for ordinary MySQL
fallback, retaining the existing multi-statement path in both modes. These are two changed lines:
no formatter, CAST, API, dependency, timezone conversion or session-state mutation is added.
Supported inputs are the existing DATE/DATETIME/TIME declarations and DbType.DateTime2 mapping.
Values remain unzoned wall clocks; DateTime requires Kind.Unspecified and DATE requires midnight.
TIMESTAMP target conversion follows the actual current server session time_zone, not the host zone.
Scales0..6 are exact; sub-microsecond ticks, truncating declarations and out-of-range TIME reject
before Prepare. Typed NULL retains declaration validation. Provider zero/partial-zero dates remain
subject to server SQL_MODE and target constraints. Temporal/NULL-only fallback needs no string-state
discovery. Expression metadata belongs to the quoted expression; column metadata comes from the target.

Only a complete validated automatic-Prepare1295/HY000 response enables this opt-in path.
Explicit Prepare, SchemaOnly, KeyInfo, cursor/output/complex/stream fallback exclusions remain.
Oracle ordinary temporal/interval/national fallback remains rejected. Execution errors and unknown
writes are never retried; snapshots, transactions, cancellation and packet budgets stay shared.
Generated fallback values may appear in server SQL logs, as with the existing literal paths.

The shared PrepareRefusalRelay is unchanged. It refuses the exact target COM_STMT_PREPARE without
forwarding it, then relays generated text to real OceanBase. Counters independently assert one
refusal per execution and zero forwarded target Prepares. This is injected1295 acceptance, not
native-server1295 reproduction, actual OBProxy/OB20/TLS or trace acceptance.

## Actual TIME cap conversion boundary

At signed 838:59:59.999999, this OceanBase version does not preserve the cap fraction:

| Path | SQL_MODE empty | STRICT_ALL_TABLES |
| --- | --- | --- |
| .NET binary TIME | Stores signed 838:59:59.000000 | Stores signed 838:59:59.000000 |
| .NET text fallback | Stores signed 838:59:59.000000 | Rejects1292 |
| Official Java text probe | Stores signed whole-second cap | Rejects1292/22001 |

Java's strict negative-cap probe independently confirms the text boundary; .NET covers both signs.
The driver does not clip values or force binary/text parity. A strict fallback error is returned
without execution retry. The initial direct run passed36/40: four strict tests wrongly expected
binary rejection. Only that test expectation changed; live-initial.trx preserves the evidence.
Users must not assume identical binary/text conversion or exact fractional storage at the cap.

## Verification

- Formal RED17fail/2pass: all17 failures reached the old temporal whitelist exclusion. A negative
  duration test-data arithmetic typo was corrected without a product workaround.
- Related GREEN81/81, zero failures/skips: invariant exact literals/NULL, provider zero dates,
  invalid kind/ticks/midnight/scale/range before Prepare, sync/async mutation snapshots and repeated
  typed-NULL execution without string discovery. Nineteen new unit cases are included.
- Public DLL consumers96/96 including24 new temporal cases, zero failures/skips: representative
  Standard2.0/2.1/net10 assets, sync/async, plain/syntheticOB20, exact temporal/NULL text, trailing
  refusal rejection and execution1295 without retry. The corrected filter is MySqlTemporal,
  MySqlJson and MySqlBitYear; the earlier36-case filename-like filter missed the new consumer.
- Direct92/92, zero failures/skips:40 new temporal,36 JSON and16 BIT/YEAR regressions.
  Twenty-four temporal cases cover both SQL modes/zones and scales0/3/6, change mode/zone on the
  same command, and independently verify four rows, actual column types, TIMESTAMP UTC storage,
  TIME precision, SQL NULL and zero/partial-zero dates. Invalid declarations send no Prepare.
  Eight cases compare signed cap storage/errors in binary/text strict/permissive modes; eight
  check commit/rollback, own/observer visibility, real duplicate1062 and opt-out1295 with no retry.
  Independent DatabaseContext cleanup owns every table; only explicit mysql-direct/TLS Disabled
  profiles were used.
- DI14/14 and five-target Release build zero warnings/errors.
- Complete default units8548/8548, zero failures/skips (8min24sec), run outside the sandbox for
  the existing Windows File.Replace fixture permission requirement. Product/test inputs stayed
  frozen throughout; no TLS/SecurityTests were enabled.
- One fresh independent final read-only review found no actionable Critical/Important/Minor
  issues in the module or its connected shared paths. No corrective code changes were required;
  full-suite completion was pending at review time and subsequently passed as recorded above.

Ignored local reports: TestResults/mysql-temporal-fallback/.

## Rulings and remaining limits

- Preserve real TIME cap clipping/error and the binary/text strict-mode difference. Cost if wrong:
  callers could assume exact storage or protocol-equivalent conversion; independent observer checks
  and the explicit table above preserve the actual boundary instead of inventing a CAST/workaround.
- Scope ordinary fallback to MySQL inputs. Oracle temporal/interval/national shapes require separate
  OceanBase evidence; cost if wrong: unsupported combinations remain rejected rather than sending
  speculative SQL. Multi-statement support does not prove ordinary fallback support.
- Keep injected/native refusal and synthetic/realOB20 evidence separate. Cost if wrong: unsupported
  deployment claims; real native1295, OBProxy/OB20/TLS, other versions/platforms and full resource/
  failure/load acceptance remain open. No SecurityTests, push, branch/worktree or release was run.
- Final review ruling: retain literal expression metadata and target conversion differences instead
  of adding CASTs for binary equivalence. Cost if wrong: callers could assume the expression reports
  the target type; the existing literal contract and independent column reads define the boundary.
- Final review ruling: signed multi-day TIME and zero-date Java string probes do not prove JDBC
  typed setter equivalence. Cost if wrong: overstated official-client support; their narrower evidence
  remains labeled and .NET provider-value acceptance is checked independently.
- Final review ruling: review approval does not replace the complete unit gate. Cost if wrong:
  unrelated regressions could escape; the actual full-suite result is recorded above before commit.

The overall23-ID/eight-package matrix remains open. Per user instruction, finish this module and
pause further development; see the [completed/remaining checkpoint](../obconnector-dotnet-pause-checkpoint.md).
