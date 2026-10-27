using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class SingleByteClientPrepareConsumerTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var sample in new[] { 1, 2, 6, 7 })
                    foreach (var asynchronous in new[] { false, true }) yield return new object[] { target, sample, asynchronous };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task EveryAssetPreparesStrictSingleByteStrings(string target, int sample, bool asynchronous)
        {
            using var greeting = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var context = new AssemblyLoadContext("single-byte-fallback-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                await ClientCharacterSetFixture.VerifyStringFallback(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!,
                    Convert.FromBase64String(greeting.RootElement.GetProperty("payloadBase64").GetString()!), ClientCharacterSetFixture.Samples[sample], asynchronous, deprecated: asynchronous);
            }
            finally { context.Unload(); }
        }
    }
}
