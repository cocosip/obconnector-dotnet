# Verified compatibility and release scope

Updated 2026-10-21. Runtime code baseline: commit `304642e`, with the dependency-range and test-tool updates described below; project version: `0.1.0-alpha`. These results cover the specified profiles, not every OceanBase deployment or feature combination.

## Current real-database evidence

The repository's unchanged 65-case acceptance console was rerun on Windows/.NET 10 against direct OceanBase 4.4.2.2 tenants. Both profiles explicitly selected TLS Disabled; Oracle enabled PrepareExecute and LOB locators. MySQL supplied the existing read-only slow-query profile for cancellation/timeout. No credentials, endpoint identities or private SQL are included here.

| Profile | Passed | Failed | Skipped | Cleanup callback failures | Exit code |
| --- | ---: | ---: | ---: | ---: | ---: |
| MySQL mode | 38 | 0 | 27 | 0 | 0 |
| Oracle mode | 49 | 1 | 15 | 1 | 1 |

The sole Oracle failure is `complex.collection`, explained below. Other skipped cases retain their individual mode/environment reasons. Evidence remains in ignored `TestResults/release-parity-{mysql,oracle}-02.json` and `.log`; the failure was not changed to a skip or a pass.

Independent cleanup checked all 28 MySQL, 39 Oracle and eight Java report-owned entries: zero remaining objects and zero owned prepared XA branches. Both exact Oracle XID rollback checks succeeded; both MySQL XIDs were already absent. The original failed cleanup callback remains in the Oracle report. Evidence: `TestResults/release-parity-{mysql,oracle}-02-cleanup.log` and `TestResults/release-parity-java-01-cleanup.log`.

## Oracle collection column: server limitation, confirmed against Java

The failure occurs in the second statement, before any INSERT, collection binding or result decoding:

```sql
CREATE TYPE OBA_NUMBER_LIST AS TABLE OF NUMBER;
CREATE TABLE OBA_COLLECTION_TABLE (id INTEGER PRIMARY KEY, value OBA_NUMBER_LIST);
```

These are illustrative names; actual comparisons used unique, owned test names and cleaned them afterward.

| Operation | .NET current candidate | Connector/J 2.4.14 ordinary server prepare | Connector/J 2.4.14 combined PrepareExecute |
| --- | --- | --- | --- |
| Create named `TABLE OF NUMBER` type | Succeeds | Succeeds | Succeeds |
| Create a column of that named nested-table type | Server error 600 / SQLSTATE 0A000 | Same server error | Same server error |
| Named OBJECT insert and binary result round trip | `complex.object` passes | Literal/bound predicates and warm/cold type caches pass | Same cases pass |

The Java error text is `OBE-00600: internal error code, arguments: -4007, nested table type not supported`. The .NET acceptance report intentionally records codes rather than server text. The Java binary's SHA-256 is `3168e7e3f2f14f35738d7c695e05012bf351f623757c7baada29659d2afa2ad4`; its source baseline is commit `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`. Fresh comparison output: `TestResults/release-parity-java-01.log`.

This diagnosis is also consistent with the pinned public [OceanBase DDL resolver](https://github.com/oceanbase/oceanbase/blob/5e8a63d29dae9fde5e962b8fcc7b167d438fc343/src/sql/resolver/ddl/ob_ddl_resolver.cpp#L3930): after resolving the column UDT, `udt_info->is_nested_table()` returns `OB_NOT_SUPPORTED` and reports `nested table type`. This source reference corroborates the live response; it is not a claim that the deployed binary was built from that exact revision.

The existing [.NET acceptance case](../tests/OceanBaseConnector.Acceptance/DatabaseCases.Oracle.cs) issues this DDL before encoding or sending a complex input value. Changing collection encoding cannot make this server accept the column. The subsequent error 942 / 42S02 comes from the cleanup attempt to DROP the table whose CREATE failed, not an unremoved object; independent inventory confirms absence. No driver workaround, exception suppression or altered acceptance assertion was introduced.

This limitation is specific to a nested-table-valued table column. It does not mean that collection types, PL/SQL collection parameters, anonymous collection outputs or array execution are unsupported. Java's [testNestedArray](https://github.com/oceanbase/obconnector-j/blob/6adc311ba5db8b2f19f98f3e6e4734667fdfdda1/src/test/java/com/oceanbase/jdbc/ComplexOracleTest.java#L411) uses a named collection as a procedure parameter and stores scalar members in ordinary NUMBER/INT columns; it does not create a nested-table-valued column. The .NET provider exposes its documented `CreateArrayType`/PrepareExecute contracts, and the real `oracle.array-binding` case passes. This comparison does not claim to have rerun Java's entire collection suite or proven every .NET collection input/output shape.

## Independent package versions

This project owns its release sequence. Root `Directory.Build.props` defines `Version=0.1.0-alpha` for both `OceanBaseConnector` and `OceanBaseConnector.DependencyInjection`. DI requires that core package version or newer (`[0.1.0-alpha,)`), without an exact-version constraint. Neither the official OceanBase .NET driver's `1.3.1` nor Connector/J's `2.4.14` is this project's version. Future version changes belong only in the root property, followed by building, packing and verifying both packages together.

Dependency minimums are separately centralized in root `Directory.Packages.props` with `ManagePackageVersionsCentrally=true` and transitive pinning. All project PackageReferences omit inline versions. Every central dependency uses an inclusive minimum with no upper bound. Microsoft minimums are uniformly 8.0.0 on Standard2.0/2.1/net8, 9.0.0 on net9 and 10.0.0 on net10; the SDK does not determine those minimums.

BouncyCastle.Cryptography is a runtime dependency with `[2.7.0,)`; 2.7.0 remains the latest stable version found in the NuGet query. An exact `[2.7.0]` constraint would prohibit dependency unification with a consuming module requiring a newer version. The open range permits that resolution but does not promise compatibility with all future versions or automatically install the newest security patch. Test tooling is separately grouped: Microsoft.NET.Test.Sdk `[18.11.0,)`, xUnit `[2.9.3,)`, and VS Runner `[4.0.1,)`, the latest stable versions found in the same query. Test projects are non-packable, and the package verifier rejects these tool packages in product dependency groups. No project release-version change was made.

## Release boundaries

The maintainer reports the previously discussed CI issue resolved. This review does not independently claim a remote CI result. Local candidate checks passed 3271 Unit, 2112 Consumer and 14 DI cases, a Release build with zero warnings/errors, and actual package consumers for all five assets. The four separate integration-gate tests were skipped without their configured environment; they are distinct from the real acceptance-console runs above. Final logs: `TestResults/release-parity/final-{restore,build,unit,consumer,di,integration,dependency-audit,pack,package-consumers}.log` and `final-tests/*.trx`. Higher-patch package consumption is retained under `TestResults/release-parity/higher-dependencies/`.

The Alpha scope covers the tested direct 4.4.2.2 profiles with explicit limitations. Pooling remains rejected because complete session reset is unimplemented. Trusted-TLS/plugin matrices, OBProxy, additional server versions, controlled topology/network faults, XA coordinator crash recovery and complete runtime/deployment matrices remain open. The Oracle report remains nonzero for the excluded server limitation; it must not be described as an all-green suite. Source-provenance and package-name/trademark review remain separate publication work. These checks do not establish general stable/RC readiness or authorize publication.
