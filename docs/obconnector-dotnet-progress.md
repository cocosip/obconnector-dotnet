# Verification status and remaining work

This page retains historical results and open work from the former progress ledger, alongside dated verification of later changes. Local regression results and real database acceptance are recorded separately.

## Recorded local results

| Baseline | Recorded result | Limit |
| --- | --- | --- |
| 2026-10-22, automatic server identity and explicit client behavior, Windows/.NET 10 | Release solution build: 0 warnings/errors. Unit: 3337 passed; Consumer: 2112 passed; DI: 14 passed, no failures/skips in those three suites. Integration: 4 skipped. Acceptance `--list` includes the new client-behavior case. | Fresh local regressions; no configured real OceanBase/OBProxy environment. |
| Same change, standalone .NET 8.0.32 / 9.0.21 / 10.0.13 hosts | Query and batch/diagnostics samples: all six passed using the corresponding Release assets. | Loopback smoke checks, not full database or package acceptance. The temporary query host build retained cached NU1900 vulnerability-feed warnings. |
| 2026-10-21, runtime code `304642e` plus dependency updates, Windows/.NET 10 | Release build: 0 warnings/errors. Unit: 3271 passed; Consumer: 2112 passed; DI: 14 passed; no failures/skips in those three suites. | Historical local regressions, not a fresh run or complete server acceptance. |
| Same record, separate IntegrationTests | 4 skipped. | Connection, mode, TLS-name and plugin gates were not passed. |
| Same record, package consumption | All five target assets; actual .NET 8/9/10 package hosts. | Package/API consumption, not full database validation on every runtime. |
| 2026-10-20, earlier full Windows .NET 8.0.31 / 9.0.20 runs | Each: Unit 2912 and DI 14 passed, 0 failures/skips after fixes. | Earlier source/test baseline; not the later 3271-test baseline. |
| Historical net48 consumption | Basic provider contracts passed on installed Framework 4.8.1. | No formal original-4.8, full API, SQL or TLS support claim. |

The 2026-10-22 xUnit reports are under the ignored `TestResults/compatibility-final/` directory. Earlier results come from the [original ledger at commit f63d57c](https://github.com/cocosip/obconnector-dotnet/blob/f63d57c/docs/obconnector-dotnet-progress.md); its ignored report files are not included in this checkout. The latest recorded database counts, failed Oracle case and deferred matrices are retained in [Compatibility](compatibility.md).

## Failures and skips still requiring attention

| Item | Status / next verification |
| --- | --- |
| Oracle `complex.collection` | Still failed in the recorded full acceptance run, with a failed DROP callback and exit code 1. Preserve the failure and narrow server-side classification; do not suppress it or classify all collection operations as unsupported. |
| MySQL 27 / Oracle 15 skipped acceptance cases | Review each report's mode/configuration/environment reason. Counts cannot be promoted to passes; the original JSON files are currently unavailable. |
| Four integration gates | Run with both direct and OBProxy tenants and a dedicated unsupported-plugin profile. Missing environments currently allow an ordinary test run to skip all four. |
| Six deferred `matrix.*` cases | Currently report skips. Implement the controlled environments and assertions listed in Compatibility; invoking the console alone does not exercise these matrices. |

The earlier Ubuntu CI record had two piece-data budget tests timing out. The TCP small-write fix passed its four focused Windows regressions; the maintainer later reported the CI issue resolved. No remote run was independently checked in this documentation change, so no fresh Ubuntu pass is claimed. Earlier .NET 9 fixture failures had subsequent recorded passing reruns; they are not listed as current failures.

## Remaining work

- Establish the supported OceanBase/OBProxy version matrix. The exact 4.4.2.2 identity restriction has been replaced by actual product-version and session-mode confirmation in both modes. Other versions remain unverified on real servers; feature minimums do not establish general compatibility.
- Complete trusted TLS/plugin success and rejection profiles, actual OB20/tracing negotiation, controlled topology/network failures and coordinator crash recovery.
- Before enabling pooling, implement and verify complete reset of raw session changes, temporary objects, unfinished transactions, prepared statements and readers; cover reset failures and cross-borrower isolation.
- Verify large-value peak memory/throughput, cursor/LOB resource lifetime, complex-type DDL changes, catalog permissions and additional output shapes on each intended deployment profile. Selected basic round trips do not cover these combinations.
- Complete intended non-Windows/runtime deployment coverage; make an explicit .NET Framework support decision. Finish source-provenance/package-name review before publication.

## Integration gate configuration

[IntegrationTestEnvironment.cs](../tests/OceanBaseConnector.IntegrationTests/IntegrationTestEnvironment.cs) requires `OB_REQUIRE_INTEGRATION=1` to run the gates. Supply all four connection variables:

- `OB_MYSQL_CONNECTION_STRING`
- `OB_ORACLE_CONNECTION_STRING`
- `OB_MYSQL_PROXY_CONNECTION_STRING`
- `OB_ORACLE_PROXY_CONNECTION_STRING`

The plugin rejection test additionally requires `OB_UNSUPPORTED_AUTH_CONNECTION_STRING`. When integration is required, missing configuration fails instead of passing silently. See the [acceptance guide](../tests/OceanBaseConnector.Acceptance/README.md) for the separate database console; it does not replace these gates.
