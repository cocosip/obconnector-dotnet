# MySQL JSON single-statement client prepare fallback

Date: 2026-10-29. Base: `6faa697`. Scope: existing opt-in fallback, MySQL Input only.

## OceanBase and official-client evidence

Official Connector/J2.4.14 source is pinned at
`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`; the runtime jar SHA256 is
`3168E7E3F2F14F35738D7C695E05012BF351F623757C7BAADA29659D2AFA2AD4`.
Its client-prepared single INSERT independently stores objects, JSON null, SQL NULL and escaped
Unicode JSON on OceanBase MySQL4.4.2.2. A separate UTF-8 connection reads semantic values,
JSON_TYPE and IS NULL; malformed JSON returns3140/22001. Owned tables are independently dropped.
Default SQL_MODE passes for UTF-8, GB18030 and latin1 (the Java latin1 sample is é£).

All three Java NO_BACKSLASH_ESCAPES probes fail1064/42000 after the actual changed SQL_MODE is
verified. Reflection shows BasePrepareStatement.noBackslashEscapes remains false; the constructor
caches protocol.noBackslashEscapes(), and StringParameter uses that cached flag. These are diagnosed
official-client failures, not successful Java cases. The probe accepts only these expected failures;
its exit0 does not mean six supported combinations. An initial windows-1252 URL fails Open1064
because AbstractConnectProtocol emits the encoding name verbatim in SET NAMES. There is no Java
cp1252/euro equivalence claim. Ignored probe/runner/log: TestResults/java-gap-probe/
MySqlJsonFallbackProbe.java, RunMySqlJsonFallback.ps1 and mysql-json-fallback.log.
The Java checkout is unchanged; credentials are supplied privately over stdin and raw errors suppressed.

## Implementation and retained boundaries

ClientPreparedSql admits Json alongside the ordinary provider whitelist. Existing PreparedParameters
validation still requires MySQL mode, string/NULL input and zero Precision/Scale. Ordinary string
Size behavior is retained (hint, no truncation/rejection); strict encoding and packet budgets apply.
Non-null strings query fresh client/connection charset, collation and SQL_MODE after each Prepare
refusal. Typed SQL NULL needs no such query. Ordinary context.Literal handles escaping; no parser,
CAST, JSON serialization, formatter, public API or dependency is introduced. Invalid JSON remains
the server's responsibility. JSON text `null` is distinct from SQL NULL. Raw NUL with
NO_BACKSLASH_ESCAPES remains unsupported; escaped JSON NUL is supported.

The exact opt-in complete automatic-Prepare1295/HY000 gate remains. Explicit Prepare, SchemaOnly,
KeyInfo, cursor/output/complex/stream fallback exclusions remain. Execution errors and unknown writes
are never retried. Snapshots, transactions, response boundaries and cancellation stay shared.

The existing private BIT/YEAR PrepareRefusalRelay moves unchanged to integration Infrastructure,
with internal visibility and the corresponding namespace/usings. It refuses only the exact target
COM_STMT_PREPARE without forwarding it, then relays text execution to actual OceanBase. Counters
assert refusals, Prepare requests and zero forwarded target Prepares. This is injected1295
acceptance, not a native1295 reproduction or real OBProxy/OB20/TLS evidence.

## Verification

- Formal corrected RED:9fail/2pass. All nine positive literal/execution cases reach the old whitelist;
  the two invalid-declaration cases preserve pre-send rejection. An initial test incorrectly assumed
  Size rejects long ordinary strings; source inspection corrected that expectation before product edit.
- GREEN related units78/78, zero failures/skips: exact ordinary literals in both modes, JSON null,
  SQL NULL without discovery, malformed JSON forwarded, strict encoding, retained raw-NUL rejection,
  invalid declarations before Prepare, sync/async mutation snapshots and fresh state per refusal.
- Public DLL consumers84/84 including24 new, zero failures/skips. Representative Standard2.0/2.1
  and net10 assets, sync/async, ordinary and synthetic OB20, SQL_MODE changes, both null forms,
  trailing refusal rejection and execution1295 with no retry. The other60 cases regress existing
  JSON, BIT/YEAR and ordinary string paths.
- Direct52/52 including36 new JSON and16 existing BIT/YEAR relay regressions, zero failures/skips.
  Twenty-four JSON cases cover six configured charsets (utf8mb4/ascii/latin1/utf8mb3/gbk/gb18030),
  both initial SQL modes and sync/async, changing mode on the same command and independently reading
  exactly eight stored rows per case. Objects, escapes, supplementary characters, NUL, JSON_TYPE
  and SQL/JSON NULL are checked. The .NET latin1 sample includes euro under the existing CP1252 map;
  this is separate from the narrower Java latin1 evidence.
- Eight new transaction cases check own/observer visibility, commit, rollback, malformed-JSON3140
  and opt-out1295, with exactly one Prepare and no execution retry. Four new state cases reject
  mismatched charset or raw NUL+NO_BACKSLASH_ESCAPES after refusal, with no target sent/stored.
  Invalid encoding and typed-NULL metadata also do not send Prepare. Independent DatabaseContext
  cleanup owns every table. Only explicit mysql-direct, TLS Disabled profiles are used.
- DI14/14 and five-target Release build zero warnings/errors.
- Expanded direct JSON/BIT/YEAR regression142/142, zero failures/skips. Complete default units
 8529/8529, zero failures/skips (8min25sec), run outside the sandbox for the existing Windows
 File.Replace fixture permission requirement. Product/test inputs remained frozen throughout;
 no TLS/SecurityTests were enabled and no full-suite repetition was needed.
- One fresh independent final read-only review approved the full module and connected shared
  paths, with no Critical/Important/Minor findings. No corrective code change was required.

Ignored local reports: TestResults/mysql-json-fallback/.

## Rulings and remaining limits

- Keep .NET's existing fresh-query NBE contract rather than copying Java's stale flag. Cost if wrong:
  quoting/data errors; both modes are independently verified in real .NET storage tests.
- Reuse the exact-refusal relay and label injected/native evidence separately. Keep Java latin1
  subset separate from .NET cp1252/euro behavior. Cost if wrong: unsupported provenance/deployment claims.
- Preserve existing ordinary Size hint semantics rather than introducing truncation/rejection.
  Cost if wrong: callers could assume Size enforces a length cap; target/packet limits remain authoritative.
- Final review ruling: native1295 and actual OBProxy/OB20/TLS remain unproven. Relay and synthetic
  tests provide their stated evidence only. Cost if wrong: unsupported interoperability claims;
  explicit deployment reports are required.
- Final review ruling: other versions/platforms, arbitrary implicit side effects and load/resource
  acceptance remain broader work. Cost if wrong: unseen deployment/resource failures; no global
  compatibility claim is made and the current matrix stays open.
- Final review ruling: external Connector/J cached-mode/encoding initialization is not changed here.
  .NET keeps fresh state, and Java failures stay recorded. Cost if wrong: false equivalence assumptions;
  real .NET both-mode storage evidence remains independent.
- Final review ruling: Oracle/output/explicit-Prepare/cursor/stream JSON fallback exclusions remain.
  A caller gets the existing rejection contract outside MySQL Input. Cost if wrong: callers need
  additional unsupported combinations; no unsafe execution or speculative protocol is introduced.

Real native1295, OBProxy/OB20/TLS, other server versions/platforms, arbitrary implicit side effects,
load/resource matrices and the overall23-ID/eight-package acceptance remain open. No SecurityTests,
push, branch/worktree or release is part of this module.
