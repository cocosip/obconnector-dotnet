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
    public sealed class NumberFloatConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var binary in new[] { false, true })
                    foreach (var sequential in new[] { false, true })
                        yield return new object[] { target, binary, sequential };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetReadsOracleNumberFloatExactly(string target, bool binary, bool sequential)
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var context = new AssemblyLoadContext("number-float-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                await NumberFloatFixture.Verify(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!,
                    Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!),
                    (channel, token) => SessionTimeZoneFixture.ReadCommand(channel, token, expectProbe: false),
                    asynchronous: true, binary, sequential);
            }
            finally { context.Unload(); }
        }
    }
}
