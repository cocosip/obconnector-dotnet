using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class DependencyVersionTests
    {
        [Theory]
        [InlineData("OceanBaseConnector", "netstandard2.0", 8)]
        [InlineData("OceanBaseConnector", "netstandard2.1", 8)]
        [InlineData("OceanBaseConnector", "net8.0", 8)]
        [InlineData("OceanBaseConnector", "net9.0", 9)]
        [InlineData("OceanBaseConnector", "net10.0", 10)]
        [InlineData("OceanBaseConnector.DependencyInjection", "net8.0", 8)]
        [InlineData("OceanBaseConnector.DependencyInjection", "net9.0", 9)]
        [InlineData("OceanBaseConnector.DependencyInjection", "net10.0", 10)]
        public void ResolvedMicrosoftExtensionsMatchProductTarget(string project, string target, int expectedMajor)
        {
            // Inspect each restored graph; a net10 test host can unify older assembly references.
            var path = Path.Combine(RepositoryLocator.FindRoot(), "src", project, "obj", "project.assets.json");
            using var assets = JsonDocument.Parse(File.ReadAllText(path));
            var count = 0;
            foreach (var dependency in assets.RootElement.GetProperty("targets").GetProperty(target).EnumerateObject())
            {
                if (!dependency.Name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal))
                    continue;

                var version = Version.Parse(dependency.Name.Split('/')[1]);
                Assert.Equal(expectedMajor, version.Major);
                count++;
            }

            Assert.True(count > 0, "Expected logging/DI dependencies in the product graph.");

            var assemblyPath = Path.Combine(RepositoryLocator.FindRoot(), "src", project,
                "bin", "Release", target, project + ".dll");
            using var stream = File.OpenRead(assemblyPath);
            using var reader = new PEReader(stream);
            var metadata = reader.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                if (metadata.GetString(reference.Name).StartsWith("Microsoft.Extensions.", StringComparison.Ordinal))
                    Assert.Equal(expectedMajor, reference.Version.Major);
            }
        }
    }
}
