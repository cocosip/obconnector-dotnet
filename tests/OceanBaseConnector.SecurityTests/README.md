# Security acceptance tests

This is an explicit local acceptance suite for certificate creation/import, OS TLS credentials,
TLS/mTLS handshakes, authentication over TLS and TLS package-asset consumers. It is outside
`OceanBaseConnector.slnx` and is never invoked by CI, including tag builds. UnitTests and
ConsumerTests retain configuration, protocol and ordinary TCP coverage without running TLS.

Run only on a machine where the OS TLS provider and private-key operations are available:

```powershell
dotnet build OceanBaseConnector.slnx -c Release
dotnet test tests/OceanBaseConnector.SecurityTests/OceanBaseConnector.SecurityTests.csproj -c Release
```

The loopback fixtures generate temporary self-signed certificates. They do not require a real
OceanBase server, supplied certificate files, an installed enterprise CA or database credentials.
They do require the platform to allow certificate/private-key operations and TLS handshakes;
ordinary CI and restricted local environments are not assumed to provide these capabilities.
No TLS test is skipped or downgraded to TCP in this suite when the platform cannot run it.

Real-server TLS, enterprise CA/revocation and authentication-plugin/KDC validation remain separate
opt-in [integration tests](../OceanBaseConnector.IntegrationTests/README.md). A successful loopback
handshake does not prove real-server support. The current direct-server profiles disable TLS.

`../Shared` contains source-linked partial helpers and the few mixed TCP/TLS theories. Default
projects compile their TCP rows; this project defines `SECURITY_ACCEPTANCE` and compiles their TLS
rows. Certificate and handshake test methods live under this project's `Unit` and `Consumer`
directories and are not compiled into either default test assembly.
