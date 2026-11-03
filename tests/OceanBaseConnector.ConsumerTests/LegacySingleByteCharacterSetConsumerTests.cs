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
    public sealed class LegacySingleByteCharacterSetConsumerTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true }) yield return new object[] { target, asynchronous };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task EveryAssetUsesLegacySingleByteSqlParametersAndResultReaders(string target, bool asynchronous)
        {
            using var greeting = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var hello = Convert.FromBase64String(greeting.RootElement.GetProperty("payloadBase64").GetString()!);
            var context = new AssemblyLoadContext("legacy-single-byte-" + target, true);
            try
            {
                var connection = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target)).GetType("OceanBaseConnector.OceanBaseConnection")!;
                for (var sample = ClientCharacterSetFixture.Samples.Length - 3; sample < ClientCharacterSetFixture.Samples.Length; sample++)
                    foreach (var oracle in new[] { false, true })
                        await ClientCharacterSetFixture.Verify(connection, hello, sample, oracle, asynchronous, deprecated: true);
                for (var sample = CharacterSetFixture.Samples.Length - 4; sample < CharacterSetFixture.Samples.Length; sample++)
                    foreach (var binary in new[] { false, true })
                        await CharacterSetFixture.Verify(connection, hello,
                            (channel, token) => SessionTimeZoneFixture.ReadCommand(channel, token, expectProbe: false), sample,
                            oracle: asynchronous, asynchronous, binary, sequential: asynchronous);
            }
            finally { context.Unload(); }
        }
    }
}
