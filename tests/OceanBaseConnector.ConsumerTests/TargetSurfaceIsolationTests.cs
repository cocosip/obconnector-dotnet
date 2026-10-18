using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Xunit;
using OceanBaseConnector;

namespace OceanBaseConnector.ConsumerTests
{
    /// <summary>
    /// Verifies the target-conditional API surface of the product assembly
    /// (A01: "验证不存在现代 API 泄漏到 netstandard2.0"). The netstandard2.0
    /// asset is inspected with a metadata reader instead of Assembly.Load so
    /// the check cannot be masked by assembly-name unification with the
    /// already-loaded modern asset.
    /// </summary>
    public sealed class TargetSurfaceIsolationTests
    {
        private static readonly string ModernTypeName = "OceanBaseConnector.OceanBaseModernApis";
        private static readonly string BaselineTypeName = "OceanBaseConnector.OceanBaseTargetSurface";

        private static IReadOnlySet<string> ReadPublicTypeNames(string assemblyPath)
        {
            using var stream = File.OpenRead(assemblyPath);
            using var reader = new PEReader(stream);
            var metadata = reader.GetMetadataReader();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var handle in metadata.TypeDefinitions)
            {
                var definition = metadata.GetTypeDefinition(handle);
                var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
                if (visibility != TypeAttributes.Public && visibility != TypeAttributes.NestedPublic)
                {
                    continue;
                }

                var name = metadata.GetString(definition.Name);
                var namespaceName = metadata.GetString(definition.Namespace);
                names.Add(string.IsNullOrEmpty(namespaceName) ? name : namespaceName + "." + name);
            }

            return names;
        }

        [Fact]
        public void BaselineConsumerUsesNetStandardAsset()
        {
            var context = new AssemblyLoadContext("baseline-sample", isCollectible: true);
            try
            {
                context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath("netstandard2.0"));
                var assembly = context.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory,
                    "OceanBaseConnector.ConsumerTests.NetStandard.dll"));
                var sample = assembly.GetType("OceanBaseConnector.ConsumerTests.NetStandard.NetStandardConsumerSample")!;
                Assert.Contains("netstandard2.0", (string)sample.GetMethod("Describe")!.Invoke(null, null)!);
            }
            finally { context.Unload(); }
        }

        [Fact]
        public void NetStandardAssetRunsInIsolatedLoadContext()
        {
            var context = new AssemblyLoadContext("baseline-consumer", isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath("netstandard2.0"));
                var type = assembly.GetType("OceanBaseConnector.OceanBaseConnectionStringBuilder", throwOnError: true)!;
                var builder = Activator.CreateInstance(type, "Server=localhost;User ID=sample;Password=baseline-secret")!;
                var sanitized = (string)type.GetMethod("ToSanitizedConnectionString")!.Invoke(builder, new object[] { false })!;
                Assert.DoesNotContain("baseline-secret", sanitized);
                var property = assembly.GetType(BaselineTypeName)!.GetProperty("HasModernAdoNetApis")!;
                Assert.Equal(false, property.GetValue(null));
            }
            finally { context.Unload(); }
        }

        [Fact]
        public void NetStandard20AssetDoesNotExposeModernApiTypes()
        {
            var path = RepositoryLocator.GetProductAssemblyPath("netstandard2.0");
            var typeNames = ReadPublicTypeNames(path);

            Assert.DoesNotContain(ModernTypeName, typeNames);
            Assert.Contains(BaselineTypeName, typeNames);
        }

        [Fact]
        public void ModernAssetsExposeModernApiTypes()
        {
            foreach (var targetFramework in new[] { "net8.0", "net9.0", "net10.0" })
            {
                var path = RepositoryLocator.GetProductAssemblyPath(targetFramework);
                var typeNames = ReadPublicTypeNames(path);

                Assert.Contains(ModernTypeName, typeNames);
                Assert.Contains(BaselineTypeName, typeNames);
            }
        }

        [Fact]
        public void RuntimeAssetMatchesModernTarget()
        {
            // The test host itself runs on a modern runtime and references the
            // matching product asset, so the compiled-in const must report true.
            Assert.True(OceanBaseTargetSurface.HasModernAdoNetApis);
        }
    }
}
