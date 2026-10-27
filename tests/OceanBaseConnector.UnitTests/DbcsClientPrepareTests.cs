using System.Collections.Generic;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class DbcsClientPrepareTests
    {
        public static IEnumerable<object[]> Cases()
        {
            for (var sample = 0; sample < ClientCharacterSetFixture.DbcsFallbackSamples.Length; sample++)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var deprecated in new[] { false, true }) yield return new object[] { sample, asynchronous, deprecated };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public Task DbcsFallbackPreservesMultibyteAndAsciiSyntax(int sample, bool asynchronous, bool deprecated)
            => ClientCharacterSetFixture.VerifyStringFallback(typeof(OceanBaseConnection), ProtocolFixture.Load("initial-handshake-oceanbase").Payload,
                ClientCharacterSetFixture.DbcsFallbackSamples[sample], asynchronous, deprecated);
    }
}
