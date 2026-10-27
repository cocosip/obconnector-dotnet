using System.Collections.Generic;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class SingleByteClientPrepareTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var sample in new[] { 1, 2, 6, 7 })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var deprecated in new[] { false, true }) yield return new object[] { sample, asynchronous, deprecated };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public Task SingleByteFallbackUsesTrustedSessionAndStrictBytes(int sample, bool asynchronous, bool deprecated)
            => ClientCharacterSetFixture.VerifySingleByteFallback(typeof(OceanBaseConnection), ProtocolFixture.Load("initial-handshake-oceanbase").Payload, sample, asynchronous, deprecated);
    }
}
