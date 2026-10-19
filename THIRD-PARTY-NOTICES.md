# Third-party dependencies and references

The NuGet packages declare their runtime dependencies rather than embedding those dependencies' assemblies. Their licenses and copyright notices remain with the original packages and repositories. This inventory does not replace the release source-provenance review or grant a license to this project.

## Direct runtime dependencies

| Dependency | Version / target | License / source |
| --- | --- | --- |
| BouncyCastle.Cryptography | 2.7.0 for all core assets | MIT, https://github.com/bcgit/bc-csharp; used through its public expanded-key Ed25519 API |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 for both Standard assets and net8; 9.0.10 for net9; 10.0.0 for net10 | MIT, https://github.com/dotnet/runtime |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 for Standard / net8; 9.0.10 for net9; 10.0.0 for net10. The core package promotes the logging dependency through central transitive pinning; the DI package also references it directly. | MIT, https://github.com/dotnet/runtime |
| System.Diagnostics.DiagnosticSource | 8.0.1 for both Standard assets; modern assets use the runtime API | MIT, https://github.com/dotnet/runtime |

The Standard dependency graphs also contain Microsoft.Bcl.AsyncInterfaces 8.0.0, System.Buffers 4.5.1, System.Memory 4.5.5, System.Numerics.Vectors 4.4.0, System.Runtime.CompilerServices.Unsafe 6.0.0 and System.Threading.Tasks.Extensions 4.5.4. Their NuGet metadata either declares MIT or links to the upstream corefx license. NETStandard.Library 2.0.3 and Microsoft.NETCore.Platforms 1.1.0 are build/reference dependencies; their legacy NuGet metadata links to https://github.com/dotnet/standard/blob/master/LICENSE.TXT and http://go.microsoft.com/fwlink/?LinkId=329770 respectively. They are not bundled as provider assemblies.

Versions are centrally managed in the repository's Directory.Packages.props. The actual dependency groups and transitive graph in the generated packages and restore assets are authoritative.

## Behavioral and protocol references

- OceanBase Connector/J (`obconnector-j`), version 2.4.14, commit `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`: https://github.com/oceanbase/obconnector-j. The repository declares LGPL-2.1. It is used as a behavioral and protocol reference; its license is not a license declaration for this .NET project. Any source port requires a separate provenance and license review.
- The official OceanBase .NET repository was consulted only for limited .NET hosting, logging and DI usage. Its split MySQL/Oracle provider structure is not this project's architecture. Source provenance and attribution must be reviewed before release.
- Npgsql was consulted for .NET data-source and host-lifecycle API design; no Npgsql runtime package is referenced by these packages. https://github.com/npgsql/npgsql

The project's own open-source license, package-name availability and trademark review remain release-gate decisions; this document does not imply OceanBase endorsement or production certification.
