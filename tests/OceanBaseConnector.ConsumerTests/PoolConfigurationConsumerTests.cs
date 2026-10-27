using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class PoolConfigurationConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var oracle in new[] { false, true })
                    foreach (var asynchronous in new[] { false, true })
                        foreach (var scenario in new[] { 0, 1, 2, 3 }) yield return new object[] { target, oracle, asynchronous, scenario };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetSharesEquivalentPoolReuseCapacityAndClearing(string target, bool oracle, bool asynchronous, int scenario)
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            await using var server = new MutualTlsServer(Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!),
                channel => SessionTimeZoneFixture.ReadCommand(channel, CancellationToken.None, expectProbe: false), oracle);
            using var certificate = TestCertificateFactory.CreateSelfSigned("client");
            var context = new AssemblyLoadContext("pool-configuration-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                await PoolConfigurationFixture.Verify(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!, server, certificate, asynchronous, scenario);
            }
            finally { context.Unload(); }
        }
    }
}
