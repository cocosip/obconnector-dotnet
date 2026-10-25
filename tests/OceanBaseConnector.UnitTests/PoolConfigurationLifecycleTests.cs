using System;
using System.Data;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class PoolConfigurationLifecycleTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClosedCallbackCannotBypassEquivalentPoolCapacityAndFailureReturnsOriginalSession(bool asynchronous)
        {
            await using var server = new MutualTlsServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload,
                channel => TextCommandTests.ReadClientCommand(channel));
            using var certificate = TestCertificateFactory.CreateSelfSigned("client");
            var configuration = $"Server=127.0.0.1;Port={server.Port};User ID=u;Pooling=true;Max Pool Size=1;Command Timeout=3;Pool Wait Timeout=1;TLS Server Name=ob-loopback";
            using var connection = new OceanBaseConnection(configuration)
            { ClientCertificate = certificate, RemoteCertificateValidationCallback = (_, _, _, _) => true };
            var reopen = true;
            connection.StateChange += (_, args) =>
            {
                if (args.CurrentState != ConnectionState.Closed || !reopen) return;
                reopen = false;
                connection.ConnectionString = PoolConfigurationFixture.Equivalent(configuration);
                connection.Open();
            };
            try
            {
                await connection.OpenAsync(); var original = connection.GetOpenSession();
                // Closed is raised before the original borrower returns; reentry must still obey capacity.
                await Assert.ThrowsAsync<TimeoutException>(() => asynchronous ? connection.CloseAsync() : Task.Run(connection.Close));
                Assert.Equal(ConnectionState.Closed, connection.State);
                await connection.OpenAsync();
                Assert.Same(original, connection.GetOpenSession());
                Assert.Equal(1, server.Accepted);
            }
            finally { reopen = false; connection.Close(); OceanBaseConnection.ClearPool(connection); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DiscardCloseReleasesEmptyFactorPoolBeforeCallbackChangesConfiguration(bool asynchronous)
        {
            await using var server = new MutualTlsServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload,
                channel => TextCommandTests.ReadClientCommand(channel));
            using var certificate = TestCertificateFactory.CreateSelfSigned("client");
            var configuration = $"Server=127.0.0.1;Port={server.Port};User ID=u;Pooling=true;Password2=\"\";TLS Server Name=ob-loopback";
            System.Net.Security.RemoteCertificateValidationCallback validation = (_, _, _, _) => true;
            using var connection = new OceanBaseConnection(configuration)
            { ClientCertificate = certificate, RemoteCertificateValidationCallback = validation };
            var pool = PhysicalSessionPoolRegistry.Get(configuration, remoteCertificateValidationCallback: validation, clientCertificate: certificate);
            connection.StateChange += (_, args) => { if (args.CurrentState == ConnectionState.Closed) connection.ConnectionString = ""; };
            try
            {
                await connection.OpenAsync(); connection.GetOpenSession().MarkSessionStateChanged();
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                Assert.Throws<ObjectDisposedException>(pool.BeginRent);
            }
            finally { connection.Close(); pool.Dispose(); PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, pool); }
        }

        [Theory]
        [InlineData(false, false, 0)]
        [InlineData(false, true, 0)]
        [InlineData(true, false, 0)]
        [InlineData(true, true, 0)]
        [InlineData(false, false, 1)]
        [InlineData(false, true, 1)]
        [InlineData(true, false, 1)]
        [InlineData(true, true, 1)]
        [InlineData(false, false, 2)]
        [InlineData(false, true, 2)]
        [InlineData(true, false, 2)]
        [InlineData(true, true, 2)]
        [InlineData(false, false, 3)]
        [InlineData(false, true, 3)]
        [InlineData(true, false, 3)]
        [InlineData(true, true, 3)]
        public async Task EquivalentConfigurationsShareReuseCapacityAndClearIdentity(bool oracle, bool asynchronous, int scenario)
        {
            await using var server = new MutualTlsServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload,
                channel => TextCommandTests.ReadClientCommand(channel), oracle);
            using var certificate = TestCertificateFactory.CreateSelfSigned("client");
            await PoolConfigurationFixture.Verify(typeof(OceanBaseConnection), server, certificate, asynchronous, scenario);
        }
    }
}
