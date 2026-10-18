using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace OceanBaseConnector.IntegrationTests
{
    /// <summary>
    /// A07 placeholder: real-tenant open/close/mode-identification gate. The
    /// connection protocol is delivered by A04-A06; until then this test
    /// reports the environment state so the gate cannot be closed by accident.
    /// </summary>
    public sealed class ConnectionGateTests
    {
        private readonly ITestOutputHelper _output;

        public ConnectionGateTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [IntegrationFact]
        public async Task OpenCloseAndIdentifyModeOnAllRequiredCombinations()
        {
            var connectionStrings = IntegrationTestEnvironment.GetRequiredConnectionStrings();
            foreach (var pair in connectionStrings)
            {
                _output.WriteLine($"{pair.Key} configured (value redacted).");
            }

            // A07 delivers the real handshake flow. Until then the gate is
            // explicitly not implemented and must fail rather than pass.
            Assert.Fail(
                "A07 not implemented: the driver cannot yet open a real OceanBase connection. " +
                "The G1 gate stays open until A04-A06 land and this test exercises them.");
            await Task.CompletedTask;
        }
    }
}
