# Third-party dependencies and references

This project is licensed under the MIT License; see `LICENSE`. That license applies to this project's original work and does not replace licenses or notices for third-party components.

The NuGet packages declare their runtime dependencies rather than embedding those dependencies' assemblies. Their licenses and copyright notices remain with the original packages and repositories. This inventory does not replace the release source-provenance review or grant a license to this project.

## Direct runtime dependencies

| Dependency | Version / target | License / source |
| --- | --- | --- |
| SharpZipLib | >=1.4.2 for all core assets, no upper bound | MIT, https://github.com/icsharpcode/SharpZipLib; public zlib Deflater/Inflater APIs for bounded compression and strict end/checksum validation |
| System.Text.Encoding.CodePages | >=8.0.0 for Standard assets, no upper bound; modern assets use runtime support | MIT, https://github.com/dotnet/runtime; public CP1252/936/54936 encoding APIs, with strict OceanBase syntax and GB18030-2005 mapping corrections |
| BouncyCastle.Cryptography | >=2.7.0 for all core assets, no upper bound | MIT, https://github.com/bcgit/bc-csharp; used through its public expanded-key Ed25519 API |
| Microsoft.Extensions.Logging.Abstractions | Minimum 8.0.0 for both Standard assets and net8; 9.0.0 for net9; 10.0.0 for net10; no upper bounds | MIT, https://github.com/dotnet/runtime |
| Microsoft.Extensions.DependencyInjection.Abstractions | Minimum 8.0.0 for Standard / net8; 9.0.0 for net9; 10.0.0 for net10; no upper bounds. The core package promotes the logging dependency through central transitive pinning; the DI package also references it directly. | MIT, https://github.com/dotnet/runtime |
| System.Diagnostics.DiagnosticSource | Minimum 8.0.0 for both Standard assets, no upper bound; modern assets use the runtime API | MIT, https://github.com/dotnet/runtime |

The Standard dependency graphs also contain Microsoft.Bcl.AsyncInterfaces 8.0.0, System.Buffers 4.5.1, System.Memory 4.5.5, System.Numerics.Vectors 4.4.0, System.Runtime.CompilerServices.Unsafe 6.0.0 and System.Threading.Tasks.Extensions 4.5.4. Their NuGet metadata either declares MIT or links to the upstream corefx license. NETStandard.Library 2.0.3 and Microsoft.NETCore.Platforms 1.1.0 are build/reference dependencies; their legacy NuGet metadata links to https://github.com/dotnet/standard/blob/master/LICENSE.TXT and http://go.microsoft.com/fwlink/?LinkId=329770 respectively. They are not bundled as provider assemblies.

Inclusive minimums without upper bounds are centrally managed in the repository's Directory.Packages.props. The versions above describe the declared minimums; applications may resolve newer dependencies. Test SDK/xUnit/runner packages are development tooling and are not declared runtime dependencies of either product package. The actual dependency groups and transitive graph in the generated packages and restore assets are authoritative.

## Behavioral and protocol references

- OceanBase Connector/J (`obconnector-j`), version 2.4.14, commit `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`: https://github.com/oceanbase/obconnector-j. The repository declares LGPL-2.1. It is used as a behavioral and protocol reference; its license is not a license declaration for this .NET project. Any source port requires a separate provenance and license review.
- The official OceanBase .NET repository was consulted only for limited .NET hosting, logging and DI usage. Its split MySQL/Oracle provider structure is not this project's architecture. Source provenance and attribution must be reviewed before release.
- MySqlConnector compressed-protocol sequence handling and its MySQL server reference were consulted for wire behavior; no source was ported and no MySqlConnector runtime dependency is included. MIT, https://github.com/mysql-net/MySqlConnector
- OceanBase server `v4.3.5_CE` charset definitions and mapping tables were consulted to verify collation IDs, widths and encoding differences: https://github.com/oceanbase/oceanbase/tree/v4.3.5_CE/deps/oblib/src/lib/charset. No upstream mapping tables or implementation were copied into this project; the decoder uses official .NET code pages plus validation and the two verified GB18030 mapping corrections. The upstream repository declares MulanPubL-2.0; reference use does not change the separate source-provenance review required for release.
- MySQL server official `strings/ctype-utf8.cc` (mysql-8.0.44) and `strings/ctype-uca.cc` (mysql-8.0.40) were consulted for legacy UTF8MB3 collation IDs 33/83/192–215: https://github.com/mysql/mysql-server. Only numeric ID/encoding facts are referenced; no upstream implementation or mapping tables were copied and no MySQL runtime dependency is included. The upstream repository declares GPL-2.0; this factual reference does not replace release source-provenance review.
- Npgsql was consulted for .NET data-source and host-lifecycle API design; no Npgsql runtime package is referenced by these packages. https://github.com/npgsql/npgsql

The existing package IDs are retained; package-name availability and trademark review still require release-owner confirmation. This document does not imply OceanBase endorsement or production certification. Source provenance must be reviewed before publication, independently of the project's MIT license.
