# Windows live NuGet package consumption

Date: 2026-10-29. Base: `6e5929a`. This module extends acceptance tooling only;
it changes no provider API or protocol behavior.

## Contract

`eng/Verify-Packages.ps1` keeps its default package/content/dependency/hash/asset
checks and eight loopback protocol scenarios per host. Live work additionally
requires `-VerifyLive`, `OB_REQUIRE_INTEGRATION=1`, and both canonical
`OB_MYSQL_CONNECTION_STRING` / `OB_ORACLE_CONNECTION_STRING` variables. Missing
opt-in or profiles fails before package processing. The verifier does not load
user-secrets itself; this local run supplied the existing direct profiles from
user-secrets without writing them to logs or command-line arguments.

Profiles travel to child hosts as Base64 UTF-8 lines over redirected stdin
(Base64 is framing, not encryption). Both profiles are validated before either
mode opens. This acceptance path requires TLS Disabled, matching ExpectedMode
and positive timeouts, caps connection/command timeouts at 10/15 seconds, and
uses capacity-one pools. It neither tests TLS nor changes TLS policy in the
product. Raw child failures are suppressed; success requires exactly the eight
expected case/cleanup lines, the summary, exit zero and empty stderr.

## Scenarios and cleanup

Each host runs MySQL and Oracle, sync/async commands and buffered/sequential
readers: eight cases. Only public APIs from the restored packages are used.
Every case creates its own random `OBPK_` table and verifies:

- Explicit prepared decimal/text/binary inputs and typed SQL NULL, exact stored
  values and metadata through text and parameterized prepared readback.
- Independent nonpooled observer visibility before/after local commit and
  rollback, plus row/result termination.
- Known duplicate-key errors, subsequent session usability and pooled reopen.
- Exact identities, decimal/text/binary values and typed NULL again through the
  reopened borrower after rollback/error handling; unchanged row count alone
  cannot pass this final check.
- Independent DROP after every attempted CREATE, including an unknown CREATE
  outcome; only the mode-specific missing-table error is ignored. Cleanup
  failure fails the case. No unknown business write is replayed.

The pooled reopen checks the next borrower's contents; it does not prove reuse
of the same physical session. Async cases use async commands/readers/Open;
transaction creation stays synchronous, and netstandard2.0 also completes
transactions synchronously. Case cancellation budgets are 90 seconds; the
parent enforces a 180-second child deadline. A killed child cannot certify
cleanup and fails the gate.

## Evidence

The two actual `0.1.0` nupkgs were packed from the current Release outputs into
`TestResults/package-live-consumption/feed`. The verifier restored into a fresh
private cache, checked package hashes and selected assets, then passed:

| Asset | Actual host | Loopback cases | Live cases |
| --- | --- | --- | --- |
| net8.0 | .NET 8.0.33 | 8 | 8 |
| net9.0 | .NET 9.0.22 | 8 | 8 |
| net10.0 | .NET 10.0.14 | 8 | 8 |
| netstandard2.0 | .NET 10.0.14 loader | 8 | 8 |
| netstandard2.1 | .NET 10.0.14 loader | 8 | 8 |
| netstandard2.0 | net48 host on installed Framework 4.8.1, CLR 4.0.30319.42000, release 534296 | 8 | 8 |

All 48 live cases passed with cleanup confirmed; all six hosts passed their
eight loopback cases first. These are 48 executions of the selected scenarios,
not 48 different API contracts. The direct server profiles cover OceanBase
4.4.2.2 on this Windows host, TLS Disabled. Framework execution does not prove
original .NET Framework 4.8 compatibility.

Safety tests first failed 9/9 against the unavailable harness, then passed 9/9:
missing/malformed/mismatched/TLS/nonpositive-timeout profiles and sanitized Open
failures in both modes, with no inner exceptions or connection details.
The script's missing integration gate / missing MySQL / missing Oracle checks
were also exercised before package processing.

The separate default-only package verifier passed all six hosts (eight loopback
scenarios each), with integration opt-in and deliberately invalid live profiles
present but no live case execution. After the review fixes, full default units
passed 8466/8466, zero failures/skips; the final explicit-live and default-only
package runs both returned exit zero on all six hosts. Final live totals were
48 case lines, six summaries and six sets of eight loopback scenarios, with no
incidental runner result output.
The five-target solution Release build passed with zero warnings/errors;
related unit regression passed 417/417 and DI passed 14/14, no skips.

One independent final read-only review found two Important issues, no Critical
or Minor issues. Both were corrected in one TDD fix pass:

- Synchronous stdin delivery preceded the child deadline. The runner now sends
  asynchronously and supervises delivery plus exit under the same 180-second
  deadline. `eng/Test-PackageLiveRunner.ps1` extracts the real function, shortens
  its deadline to one second for the test, and sends 256 KiB per profile to a
  child that never reads. Before the fix the independent ten-second watchdog
  failed; after the fix the expected sanitized deadline is reported. A second
  successful-reader check caught an unintended PowerShell `VoidTaskResult`
  pipeline value (exit 4 RED); explicitly discarding that result made it pass.
  The self-check uses synthetic child processes and never opens a database.
- Final readback used only COUNT. Twelve protocol tests exercise sync/async and
  buffered/sequential valid, changed-text and replaced-row cases. Before the
  fix all eight corrupt cases failed to throw while four valid cases passed;
  afterwards all twelve pass, along with the original nine safety tests
  (21/21). The initial and final exact readbacks now share one implementation.

Additional ambiguous-CREATE / cleanup-failure injection was not executed;
independent cleanup control flow was inspected and successful cleanup was
verified live. Those failure executions remain outside this module's evidence.
There is no claim of a second independent review after the TDD fix pass.

Ignored logs/TRX and restored consumers remain under
`TestResults/package-live-consumption/` and `TestResults/package-validation/`.
Final evidence files are `full-units-final.trx`, `build-final.log`,
`review-fixes-green.trx`, `runner-green-final.log`,
`packages-live-final-clean.log` and `packages-default-final.log`.
The first core pack returned exit 1 with no diagnostic output; a single-node
retry succeeded before any live consumer was started. This was not a database
failure and is not counted as a successful gate.

Commands for the two package paths (live variables supplied separately):

```powershell
./eng/Verify-Packages.ps1 -PackageDirectory TestResults/package-live-consumption/feed -VerifyNetFramework
./eng/Verify-Packages.ps1 -PackageDirectory TestResults/package-live-consumption/feed -VerifyNetFramework -VerifyLive
./eng/Test-PackageLiveRunner.ps1
```

## Remaining scope

This closes only the Windows direct live public-package subset of P2-05.
Non-Windows hosts, ORM/full API compatibility, other server versions, OBProxy,
real compression/OB20/trace, TLS/authentication, failure/load/resource matrices
and publication acceptance remain open. Full database and SecurityTests suites
were not run for this module. The 23 audit IDs and eight work packages cannot
be declared complete from these results.
