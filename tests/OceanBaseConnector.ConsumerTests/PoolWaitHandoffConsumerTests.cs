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
    [Collection("Consumer diagnostics")]
    public sealed class PoolWaitHandoffConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var oracle in new[] { false, true })
                    foreach (var firstAsync in new[] { false, true })
                        foreach (var secondAsync in new[] { false, true })
                            foreach (var dataSource in target.StartsWith("netstandard", StringComparison.Ordinal) ? new[] { false } : new[] { false, true })
                                yield return new object[] { target, oracle, firstAsync, secondAsync, dataSource };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetHandsCanceledNotificationToRemainingOpen(string target, bool oracle, bool firstAsync, bool secondAsync, bool dataSource)
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            await using var server = new CompressionServer(Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!), oracle);
            var context = new AssemblyLoadContext("pool-handoff-" + Guid.NewGuid(), isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                await PoolWaitHandoffFixture.Verify(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!, server, firstAsync, secondAsync, dataSource);
            }
            finally { context.Unload(); }
        }
    }
}
