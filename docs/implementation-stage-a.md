# Stage A continuation record (2026-10-18)

Basis: A01–A07 in the [architecture](obconnector-dotnet-architecture-design.md) and [development plan](obconnector-dotnet-development-plan.md). The user authorized inspection, continuation of this stage, verification, and a commit on the current branch.

## Scope and execution order

- [x] Project setup: retain four product targets; use net10.0 for all tests/consumer hosts; migrate to slnx and update CI/docs; reuse CPM and the shared Version without another version source.
- [x] A05: fix compilation blockers in uncommitted code and run existing tests; expose defects using real protocol sequence numbers, authentication-switch seed terminators, nested session-track lengths, missing capabilities, and password-redaction regressions, then fix shared protocol paths.
- [x] A06: write loopback state-machine tests first; implement physical-session connection timeout/cancellation, exclusive leases, invalidation on unread responses, sync/async close, and exception mapping. Stage A sends no SQL and does not prebuild Stage B statement/reader objects.
- [x] A07: replace integration placeholders with real physical sessions; cover open, mode mismatch, and TLS hostname rejection across four mode/topology combinations. Preserve missing-environment skips and mandatory-gate failures.
- [x] Verification: restore, four-target Release build, all tests, formatting, pack/content checks, progress evidence, independent review, then commit.

## Boundaries and decisions

- A01–A04 commits and uncommitted A05 already existed on master; continue there without creating/switching branches.
- Per the user's requirement, tests target .NET 10. The netstandard2.0 consumer uses a .NET 10 host with an explicit baseline-asset reference; products retain four targets. Older-runtime execution remains B09/R01 work.
- Synthetic handshakes do not validate compatible versions. Reject unknown endpoints and keep interoperability unverified without a live matrix. Unit success does not close G0/G1.
- A05 → A06: successful authentication returns a mode/version/capability/status snapshot; the session owns transport/cleanup. A06 → A07: integration tests use sessions without duplicating protocol code.
- Initial restore failed because sandbox Windows TLS credentials were unavailable. Authorized elevated restore succeeded without weakening NuGet trust/signature policies.

## Findings and verification evidence

- Baseline: GLM had committed A01–A04. CPM/root Version existed; inconsistent sln/test targets and incomplete, uncompilable A05 were the problems.
- Protocol fixes: shared send/receive sequences, nested session-state lengths, switch-seed NUL, pluginless native fallback, consistent SSLRequest/auth-response capabilities/charset/packet limits, length-encoded 251–253-byte DNS attributes, invalid length-integer rejection, and removal of server text that might echo passwords. OceanBaseException retains server code, SQLSTATE, and InnerException across targets.
- A06: exclusive leases, unread-response destruction, total connection budget, synchronous TCP cancellation/wait-handle disposal, cancellation on close, and a shared lock for success publication/cancellation. Only Connecting permits connection-budget socket closure.
- Independent review/recheck passed after fixes: native fallback, sync cancellation, long attributes, success/cancellation races, and trustworthy-mode boundaries were inspected without new significant findings.
- `dotnet restore OceanBaseConnector.slnx`: passed. Sandbox NuGet/Schannel credentials were restricted; normal elevated Windows restore/TLS tests passed without weakening TLS/package-signature checks.
- `dotnet build OceanBaseConnector.slnx -c Release --no-restore`: all four product targets and net10.0 test/consumer projects passed with zero warnings/errors.
- `dotnet test OceanBaseConnector.slnx -c Release --no-restore --logger trx --results-directory TestResults`: UnitTests 158 passed, ConsumerTests five passed, IntegrationTests four skipped, exit zero. TRX files are in ignored local TestResults/.
- OpenClose alone with `OB_REQUIRE_INTEGRATION=1`: failed because all four connection strings were missing, exit one. This confirms mandatory gates cannot count absent environments as success; it is an expected environment-gate failure, not real interoperability testing.
- `dotnet format OceanBaseConnector.slnx whitespace --no-restore --verify-no-changes` and `git diff --check`: passed.
- `dotnet pack src/OceanBaseConnector/OceanBaseConnector.csproj -c Release --no-build --no-restore`: passed. XML/ZIP assertions confirmed CPM, no project-local dependency/product versions, all test projects targeting net10.0, root Version=0.1.0-alpha, and exactly four product DLLs for netstandard2.0/net8.0/net9.0/net10.0. Formal README/license package metadata remained R01; pack reported the missing README.

## Open boundaries and next steps

- G0/G1 lack real tenants, exact OceanBase/OBProxy versions, and TLS/authentication matrices. Oracle evidence is synthetic/loopback only. No compatibility allowlist was invented.
- Connections without the Oracle status bit are currently rejected; real MySQL connections remain unavailable. Establish version allowlists using real mode-bit evidence, then rerun A07. ExpectedMode validates facts and cannot override detection.
- A04 private-CA/real hostname/cross-platform acceptance and A05 plugin interoperability remain unverified; A07 is blocked. Windows/Linux CI definitions were updated but not executed remotely. Older .NET 8/9 runtimes were not tested in this stage.
- Stage B public ADO.NET objects/SQL/readers/transactions were not started. No NuGet publication/push occurred.

## Subsequent target-framework revision (2026-10-18)

At the user's additional request, core added netstandard2.1 to the four-target baseline, for five targets. Earlier build/test/package records above remain historical evidence. See architecture §2.3 for the latest dependency matrix and the progress ledger for later verification. At that revision DI still had three modern targets; tests/consumer hosts remained net10.0.
