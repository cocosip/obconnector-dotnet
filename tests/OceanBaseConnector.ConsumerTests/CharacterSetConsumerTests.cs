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
    public sealed class CharacterSetConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                for (var sample = 0; sample < CharacterSetFixture.Samples.Length; sample++)
                    foreach (var binary in new[] { false, true })
                        foreach (var sequential in new[] { false, true })
                            yield return new object[] { target, sample, binary, sequential };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetReadsSupportedCharacterSetsWithoutGlobalRegistration(string target, int sample, bool binary, bool sequential)
            => await Verify(target, sample, binary, sequential);

        public static IEnumerable<object[]> BlobFamilyAssets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (byte type in new byte[] { 249, 250, 251 })
                    foreach (var raw in new[] { false, true })
                        foreach (var binary in new[] { false, true })
                            foreach (var sequential in new[] { false, true })
                                yield return new object[] { target, type, raw, binary, sequential };
        }

        [Theory]
        [MemberData(nameof(BlobFamilyAssets))]
        public Task EveryAssetReadsBlobFamilyResults(string target, byte type, bool raw, bool binary, bool sequential)
            => Verify(target, type == 249 ? 2 : type == 250 ? 4 : 7, binary, sequential, type, raw);

        private static async Task Verify(string target, int sample, bool binary, bool sequential, byte type = 253, bool raw = false)
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var context = new AssemblyLoadContext("character-sets-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                await CharacterSetFixture.Verify(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!,
                    Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!),
                    (channel, token) => SessionTimeZoneFixture.ReadCommand(channel, token, expectProbe: false),
                    sample, oracle: false, asynchronous: true, binary, sequential, type, raw);
            }
            finally { context.Unload(); }
        }
    }
}
