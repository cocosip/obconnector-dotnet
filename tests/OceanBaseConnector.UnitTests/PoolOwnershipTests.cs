using System;
using System.Data;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class PoolOwnershipTests
    {
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(false, true, true)]
        [InlineData(true, false, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(true, true, true)]
        public async Task CloseCallbackReopenKeepsEachSessionInItsOriginalPool(bool asynchronous, bool firstPooled, bool secondPooled)
        {
            var first = PoolServer();
            var second = PoolServer();
            var firstConfiguration = Configuration(first.Port, firstPooled);
            var secondConfiguration = Configuration(second.Port, secondPooled);
            var firstPool = firstPooled ? PhysicalSessionPoolRegistry.Get(firstConfiguration) : null;
            var secondPool = secondPooled ? PhysicalSessionPoolRegistry.Get(secondConfiguration) : null;
            using var connection = new OceanBaseConnection(firstConfiguration);
            PhysicalSession? firstSession = null;
            var reopen = true;
            connection.StateChange += (_, args) =>
            {
                if (args.CurrentState != ConnectionState.Closed || !reopen) return;
                reopen = false;
                connection.ConnectionString = secondConfiguration;
                connection.Open();
            };
            try
            {
                await connection.OpenAsync();
                firstSession = connection.GetOpenSession();
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                Assert.Equal(ConnectionState.Open, connection.State);
                var secondSession = connection.GetOpenSession();
                Assert.NotSame(firstSession, secondSession);
                if (secondPool != null) Assert.False(secondPool.TryRent(out _));
                if (firstPool != null)
                {
                    Assert.True(firstPool.TryRent(out var returned));
                    Assert.Same(firstSession, returned);
                    firstPool.Discard(returned!);
                    Assert.True(firstPool.IsEmpty);
                }
                else Assert.Equal(SessionState.Closed, firstSession.State);
                Assert.Equal(SessionState.Open, secondSession.State);
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                if (secondPool != null)
                {
                    Assert.True(secondPool.TryRent(out var returned));
                    Assert.Same(secondSession, returned);
                    secondPool.Discard(returned!);
                    Assert.True(secondPool.IsEmpty);
                }
                else Assert.Equal(SessionState.Closed, secondSession.State);
            }
            finally
            {
                reopen = false;
                connection.Close();
                if (firstSession != null)
                {
                    if (firstPool != null) firstPool.Discard(firstSession); else firstSession.Dispose();
                }
                firstPool?.Dispose();
                secondPool?.Dispose();
                if (firstPool != null) PhysicalSessionPoolRegistry.ReleaseIfEmpty(firstConfiguration, firstPool);
                if (secondPool != null) PhysicalSessionPoolRegistry.ReleaseIfEmpty(secondConfiguration, secondPool);
                first.Stop();
                second.Stop();
            }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CloseCallbackConfigurationChangeReleasesEmptyOriginalRegistryEntry(bool asynchronous)
        {
            var server = PoolServer();
            var configuration = Configuration(server.Port, true);
            var originalPool = PhysicalSessionPoolRegistry.Get(configuration);
            using var connection = new OceanBaseConnection(configuration);
            connection.StateChange += (_, args) =>
            {
                if (args.CurrentState == ConnectionState.Closed) connection.ConnectionString = string.Empty;
            };
            PhysicalSessionPool? replacement = null;
            try
            {
                await connection.OpenAsync();
                var session = connection.GetOpenSession();
                session.MarkSessionStateChanged();
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                Assert.Equal(SessionState.Closed, session.State);
                Assert.True(originalPool.IsEmpty);
                replacement = PhysicalSessionPoolRegistry.Get(configuration);
                Assert.NotSame(originalPool, replacement);
            }
            finally
            {
                connection.Close();
                originalPool.Dispose();
                PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, originalPool);
                if (replacement != null) PhysicalSessionPoolRegistry.ReleaseIfEmpty(configuration, replacement);
                server.Stop();
            }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static string Configuration(int port, bool pooling) => new OceanBaseConnectionStringBuilder
        {
            Server = "127.0.0.1",
            Port = port,
            UserId = "u",
            Password = "p",
            TlsMode = OceanBaseTlsMode.Disabled,
            ConnectionTimeout = 5,
            CommandTimeout = 1,
            Pooling = pooling,
            MaxPoolSize = 1,
        }.ConnectionString;

        private static LoopbackServer PoolServer() => LoopbackServer.Start(async client =>
        {
            var stream = client.GetStream();
            var channel = new PacketChannel(stream);
            await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
            await channel.ReadPacketAsync();
            await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
            await ServerIdentity.ReplyAsync(channel);
            while (true)
            {
                var packet = new byte[5];
                var count = await stream.ReadUpToAsync(packet, packet.Length);
                if (count == 0) break;
                Assert.Equal(5, count);
                Assert.Equal(new byte[] { 1, 0, 0, 0, 0x1f }, packet);
                await stream.WriteAsync(new byte[] { 7, 0, 0, 1, 0, 0, 0, 6, 0, 0, 0 });
                await ServerIdentity.ReplyOracleAutocommitAsync(channel);
            }
        });
    }
}
