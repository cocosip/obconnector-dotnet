using System;
using System.Security.Authentication;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Session;
using Xunit;
using Xunit.Abstractions;

namespace OceanBaseConnector.IntegrationTests
{
    public sealed class ConnectionGateTests
    {
        private readonly ITestOutputHelper _output;
        public ConnectionGateTests(ITestOutputHelper output) => _output = output;

        [IntegrationFact]
        public async Task OpenCloseAndIdentifyModeOnAllRequiredCombinations()
        {
            foreach (var pair in IntegrationTestEnvironment.GetRequiredConnectionStrings())
            {
                var builder = new OceanBaseConnectionStringBuilder(pair.Value);
                builder.ExpectedMode = ExpectedMode(pair.Key);
                using (var session = new PhysicalSession(builder.Build()))
                {
                    await session.OpenAsync();
                    Assert.Equal(ExpectedMode(pair.Key), session.Info!.Mode);
                    _output.WriteLine($"{pair.Key}: version={session.Info.ServerVersion}; mode={session.Info.Mode}; TLS={builder.TlsMode}; capabilities={session.Info.Capabilities.Effective}");
                    await session.CloseAsync();
                    Assert.Equal(SessionState.Closed, session.State);
                }
                using var synchronous = new PhysicalSession(builder.Build());
                synchronous.Open();
                Assert.Equal(ExpectedMode(pair.Key), synchronous.Info!.Mode);
                synchronous.Close();
            }
        }

        [IntegrationFact]
        public async Task ExpectedModeMismatchRejectsAllRequiredCombinations()
        {
            foreach (var pair in IntegrationTestEnvironment.GetRequiredConnectionStrings())
            {
                var builder = new OceanBaseConnectionStringBuilder(pair.Value)
                {
                    ExpectedMode = ExpectedMode(pair.Key) == OceanBaseCompatibilityMode.MySql
                        ? OceanBaseCompatibilityMode.Oracle : OceanBaseCompatibilityMode.MySql,
                };
                using var session = new PhysicalSession(builder.Build());
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => session.OpenAsync());
                Assert.Contains("ExpectedMode", error.Message);
                Assert.Equal(SessionState.Broken, session.State);
            }
        }

        [IntegrationFact]
        public async Task IncorrectTlsNameRejectsAllRequiredCombinations()
        {
            foreach (var pair in IntegrationTestEnvironment.GetRequiredConnectionStrings())
            {
                var builder = new OceanBaseConnectionStringBuilder(pair.Value)
                {
                    TlsMode = OceanBaseTlsMode.VerifyFull,
                    TlsServerName = "invalid-name.example.invalid",
                };
                using var session = new PhysicalSession(builder.Build());
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => session.OpenAsync());
                Assert.IsAssignableFrom<AuthenticationException>(error.InnerException);
                Assert.Equal(SessionState.Broken, session.State);
            }
        }

        [IntegrationFact]
        public async Task UnsupportedAuthenticationPluginIsRejected()
        {
            var value = Environment.GetEnvironmentVariable("OB_UNSUPPORTED_AUTH_CONNECTION_STRING");
            Assert.False(string.IsNullOrWhiteSpace(value), "Provide OB_UNSUPPORTED_AUTH_CONNECTION_STRING for an endpoint requesting a plugin other than native/clear password.");
            using var session = new PhysicalSession(new OceanBaseConnectionStringBuilder(value!).Build());
            var error = await Assert.ThrowsAsync<OceanBaseException>(() => session.OpenAsync());
            Assert.Contains("not supported", error.Message);
            Assert.Equal(0, error.ServerErrorCode);
            Assert.Equal(SessionState.Broken, session.State);
        }

        private static OceanBaseCompatibilityMode ExpectedMode(string name) =>
            name.Contains("ORACLE", StringComparison.Ordinal) ? OceanBaseCompatibilityMode.Oracle : OceanBaseCompatibilityMode.MySql;
    }
}
