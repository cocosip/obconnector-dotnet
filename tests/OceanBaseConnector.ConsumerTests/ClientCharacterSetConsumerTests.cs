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
    public sealed class ClientCharacterSetConsumerTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var oracle in new[] { false, true })
                    foreach (var asynchronous in new[] { false, true })
                        yield return new object[] { target, oracle, asynchronous };
        }

        public static IEnumerable<object[]> JsonCases()
        {
            foreach (var row in Cases())
                if (!(bool)row[1]) yield return new[] { row[0], row[2] };
        }

        [Theory]
        [MemberData(nameof(JsonCases))]
        public async Task EveryAssetEncodesExplicitJsonParametersWithClientCharset(string target, bool asynchronous)
        {
            using var greeting = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var context = new AssemblyLoadContext("json-client-charset-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                for (var sample = 0; sample < ClientCharacterSetFixture.Samples.Length; sample++)
                    await ClientCharacterSetFixture.Verify(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!,
                        Convert.FromBase64String(greeting.RootElement.GetProperty("payloadBase64").GetString()!), sample, false, asynchronous, deprecated: true, json: true);
            }
            finally { context.Unload(); }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task EveryAssetEncodesClientSqlAndParameters(string target, bool oracle, bool asynchronous)
        {
            using var greeting = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var context = new AssemblyLoadContext("client-charset-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                for (var sample = 0; sample < ClientCharacterSetFixture.Samples.Length; sample++)
                    await ClientCharacterSetFixture.Verify(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!,
                        Convert.FromBase64String(greeting.RootElement.GetProperty("payloadBase64").GetString()!), sample, oracle, asynchronous, deprecated: true);
            }
            finally { context.Unload(); }
        }
    }
}
