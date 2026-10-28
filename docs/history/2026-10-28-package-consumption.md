# Windows package consumption verification

Date: 2026-10-28

The release packages were rebuilt from the current `master` checkout and verified with
`eng/Verify-Packages.ps1 -PackageDirectory TestResults/package-validation-feed -VerifyNetFramework`.

## Evidence

- `OceanBaseConnector.0.1.0.nupkg` and `OceanBaseConnector.DependencyInjection.0.1.0.nupkg` passed identity, MIT license, README/LICENSE/notice, repository metadata, asset, dependency group, and dependency floor checks.
- The isolated consumer restored both packages from the current feed and selected exactly one `lib` asset for each of `netstandard2.0`, `netstandard2.1`, `net8.0`, `net9.0`, and `net10.0`.
- The consumer compiled with zero warnings and zero errors.
- The protocol sample ran on .NET 8.0.33, 9.0.22, and 10.0.14. The netstandard2.0 and netstandard2.1 consumers were loaded by the .NET 10 host. Each run passed all eight loopback protocol scenarios.
- A `net48`-targeted host selected the `netstandard2.0` assets and passed all eight loopback protocol scenarios on the installed .NET Framework 4.8.1 CLR `4.0.30319.42000` (release `534296`).
- Restore disables NuGet vulnerability auditing for this local package-consumption check. Audit service availability is external to package compatibility and previously produced NU1900 under warnings-as-errors.

This closes the Windows package/asset and runtime smoke-test subset of P2-05. It does not establish
non-Windows, ORM, or full application compatibility.
