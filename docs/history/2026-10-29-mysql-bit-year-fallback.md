# MySQL BIT/YEAR single-statement client prepare fallback

Date: 2026-10-29. Base: `85a7246`. Existing opt-in fallback extension.

## OceanBase evidence and behavior

Official Connector/J 2.4.14 at source SHA
`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1` emits exact numeric text through
BigDecimalParameter and ShortParameter. Its explicit client-prepared single
CTAS statement independently stored BIT(64)/YEAR at 0, 2^63, UInt64 maximum,
YEAR0/1901/2155 and typed NULL on OceanBase MySQL4.4.2.2. Each owned table was
dropped. This is OceanBase and its official client's behavior, not an assumption
about native MySQL parity.

Five explicit server-Prepare probes (DO, EXPLAIN SELECT, SHOW VARIABLES,
SHOW STATUS and CTAS) all succeeded. Therefore this module does not claim a
native1295 reproduction on that tenant. Ignored probe/source/safe runner/log:
TestResults/java-gap-probe/MySqlFallbackBitYearProbe.java,
RunMySqlFallbackBitYear.ps1 and mysql-fallback-bit-year.log. Credentials travel
through stdin and raw stderr is suppressed; the Java source checkout is unchanged.

The .NET change admits explicit OceanBaseDbType.Bit/Year in ordinary client
fallback, reusing existing UInt64/UInt16 numeric literals, value/declaration
validation and SQL budgets. Oracle mode still rejects these types. BIT retains
width1..64 (Size0 means64), YEAR retains0 or1901..2155 and Precision0/4;
typed NULL declarations are checked too. Target columns perform conversion:
numeric expressions do not promise BIT/YEAR result metadata. No new public API,
formatter, dependency or forced CAST is introduced.

Fallback still requires AllowClientSidePrepareFallback=true and a complete,
validated automatic-Prepare error1295/HY000. Explicit Prepare, SchemaOnly,
cursors, outputs, complex values and streams do not gain fallback. Execution
errors and unknown writes are never retried. Existing snapshots, transactions,
cancellation and response-boundary rules remain in force.

## Verification

- Test compile first corrected DbCommand→OceanBaseCommand casts; not counted as
  RED. Formal RED:14 failures/2 passes, all positive literals/executions hit the
  old whitelist. GREEN related units98/98, zero failures/skips.
- Twelve exact literal/NULL cases plus sync/async snapshot and repeat-Prepare
  tests cover UInt64 maximum and invalid live mutation while Prepare waits.
  Invalid values/declarations retain no-Prepare/no-target assertions.
- Public DLL consumers78/78, including24 new cases, zero failures/skips. New cases use representative
  netstandard2.0/netstandard2.1/net10.0 assets, sync/async, plain/synthetic OB20,
  malformed refusal boundaries, exact numeric/NULL SQL and execution1295 errors
  that must not trigger a retry.
- New direct acceptance16/16, zero failures/skips. A private plaintext relay
  refuses only the exact target COM_STMT_PREPARE with1295/HY000, without sending
  it upstream; subsequent text and responses are forwarded to actual OceanBase.
  Counters assert each refusal, each Prepare and zero forwarded target Prepares.
  This is injected-refusal acceptance, distinct from native-server refusal.
- Eight direct cases execute three times, independently verify exactly three
  stored rows, BIT/YEAR column metadata and exact values/NULL, then check invalid
  values/typed-NULL declarations never reach Prepare and leave the session usable.
  Another eight verify transaction isolation, commit/rollback, opt-out refusal
  and a real strict-mode BIT(1) target overflow, without retry or unknown outcome.
  DatabaseContext independently cleans every owned table.
- Existing BIT/YEAR public-API and multi-statement consumers24/24 also passed.
- Expanded direct BIT/YEAR regression44/44 (the16 new cases plus28 existing
  multi-statement cases), zero failures/skips.
- DI14/14 and five-target Release build zero warnings/errors.
- One fresh read-only final review found no Critical/Important/Minor and no
  actionable defect in product code, fixtures or relay.
- Complete default unit suite8518/8518, zero failures/skips (8min21sec). It ran
  outside the sandbox for the existing Windows File.Replace fixture permission
  requirement. Product and test inputs stayed frozen throughout the gate;
  no TLS/SecurityTests were enabled and no full-suite repetition was needed.

Ignored local evidence: TestResults/mysql-bit-year-fallback/.

## Review rulings

- Native1295 remains unverified; the exact injected refusal does not establish
  native/older-server rejection behavior. The executor verified Java source SHA
  and ran the safe probe; the reviewer did not repeat it. Incorrect provenance
  or interpretation would weaken the claimed text behavior.
- Other versions/OS/profiles and realOB20/OBProxy/compression/TLS/auth/HA/failover
  remain open; a single plaintext Windows tenant cannot establish their framing,
  trust or recovery behavior.
- BIT64/YEAR and strictBIT1 overflow do not cover every target/SQL mode; other
  conversions may reject or differ. Numeric expression metadata is intentional;
  callers needing BIT/YEAR metadata must use appropriate SQL/target columns.
- JSON/temporal/national/streams/outputs/cursors/complex ordinary fallback
  retain their existing gates; callers must use supported shapes. No speculative
  parity extension is added without a separate evidence-backed module.
- Load, large-packet, cancellation stress and exhaustive fault matrices remain
  unverified; shared-path inspection does not prove operational resource limits.
- Terminal green default suite, finalized evidence, local commit and clean
  post-commit checks are required before delivery; pending work is not success.

## Remaining boundaries

Evidence is Windows/.NET10, direct OceanBase4.4.2.2, TLS Disabled. Synthetic OB20
and the local refusal relay do not prove real OB20/OBProxy/native1295 behavior.
Other versions/platforms, TLS/auth/HA, exhaustive faults, load and all target
conversions remain open. JSON/temporal/national ordinary fallback, streams,
outputs, cursors and complex combinations remain outside this extension.
Neither P1-01/P1-08 nor the complete matrix is closed by this module.
