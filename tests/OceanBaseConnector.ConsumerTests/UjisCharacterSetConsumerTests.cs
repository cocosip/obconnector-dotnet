using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class UjisCharacterSetConsumerTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true }) yield return new object[] { target, asynchronous };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task EveryAssetUsesUjisSqlParametersAndResultReaders(string target, bool asynchronous)
        {
            using var greeting = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var hello = Convert.FromBase64String(greeting.RootElement.GetProperty("payloadBase64").GetString()!);
            var context = new AssemblyLoadContext("ujis-" + target, true);
            try
            {
                var connection = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target)).GetType("OceanBaseConnector.OceanBaseConnection")!;
                var client = Array.FindIndex(ClientCharacterSetFixture.Samples, s => s.Name == "ujis");
                foreach (var oracle in new[] { false, true })
                    await ClientCharacterSetFixture.Verify(connection, hello, client, oracle, asynchronous, deprecated: true);
                await ClientCharacterSetFixture.VerifyStringFallback(connection, hello,
                    ClientCharacterSetFixture.DbcsFallbackSamples.Single(s => s.Name == "ujis"), asynchronous, deprecated: asynchronous);
                await ComplexNationalClientFixture.Verify(connection, hello, client, asynchronous, prepareExecute: asynchronous);
                var result = Array.FindIndex(CharacterSetFixture.Samples, s => s.Collation == 12);
                foreach (var oracle in new[] { false, true })
                    foreach (var binary in new[] { false, true })
                        await CharacterSetFixture.Verify(connection, hello,
                            (channel, token) => SessionTimeZoneFixture.ReadCommand(channel, token, expectProbe: false), result,
                            oracle, asynchronous, binary, sequential: asynchronous);
            }
            finally { context.Unload(); }
            await new NationalCharacterConsumerTests().EveryAssetReadsStreamsAndBindsNationalStrings(target, "ujis", binary: asynchronous, sequential: asynchronous);
        }
    }
}
