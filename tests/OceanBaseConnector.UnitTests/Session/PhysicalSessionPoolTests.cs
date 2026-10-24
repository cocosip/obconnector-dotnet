using System;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    public sealed class PhysicalSessionPoolTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ReturnResetsAndRentReusesPhysicalSession(bool asynchronous)
        {
            var resetSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 });
                await ServerIdentity.ReplyAsync(channel, oracle: false);
                var reset = await channel.ReadPacketAsync();
                Assert.Equal(new byte[] { 0x1f }, reset);
                resetSeen.SetResult();
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                channel.BeginCommand();
                var initDb = await channel.ReadPacketAsync();
                Assert.Equal((byte)0x02, initDb[0]);
                Assert.Equal("app", System.Text.Encoding.UTF8.GetString(initDb, 1, initDb.Length - 1));
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                channel.BeginCommand();
                var ping = await channel.ReadPacketAsync();
                Assert.Equal(new byte[] { 0x0e }, ping);
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
            });

            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = server.Port,
                UserId = "u",
                Password = "p",
                Database = "app",
                TlsMode = OceanBaseTlsMode.Disabled,
                ConnectionTimeout = 5,
                Pooling = true,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var first = pool.Rent(options, null, new EndpointSelector(), out var reused);
            Assert.False(reused);
            await first.OpenAsync();
            var returned = asynchronous
                ? await pool.ReturnAsync(first, new ConnectionLog(null))
                : pool.Return(first, new ConnectionLog(null));
            Assert.True(returned);
            await resetSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = pool.Rent(options, null, new EndpointSelector(), out reused);
            Assert.True(reused);
            Assert.Same(first, second);
            second.Close();
            server.Stop();
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void BrokenSessionIsDestroyedInsteadOfReturned()
        {
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                UserId = "u",
                Password = "p",
                Pooling = true,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var session = pool.Rent(options, null, new EndpointSelector(), out _);
            session.Break();
            Assert.False(pool.Return(session, new ConnectionLog(null)));
            Assert.False(pool.TryRent(out _));
        }

        [Fact]
        public async Task PoolWaitsAndTimesOutWhenMaximumIsBorrowed()
        {
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                UserId = "u",
                Password = "p",
                Pooling = true,
                MaxPoolSize = 1,
                PoolWaitTimeout = 1,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var first = pool.Rent(options, null, new EndpointSelector(), out _);

            await Assert.ThrowsAsync<TimeoutException>(() =>
                pool.RentAsync(options, null, new EndpointSelector(), cancellationToken: default));

            first.Close();
        }

        [Fact]
        public async Task PoolWaitCanBeCanceledWhenMaximumIsBorrowed()
        {
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1", UserId = "u", Password = "p", Pooling = true,
                MaxPoolSize = 1, PoolWaitTimeout = 30,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var first = pool.Rent(options, null, new EndpointSelector(), out _);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                pool.RentAsync(options, null, new EndpointSelector(), cancellationToken: cancellation.Token));

            first.Close();
        }

        [Fact]
        public async Task ExpiredIdleSessionIsDiscardedBeforeReuse()
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 });
                await ServerIdentity.ReplyAsync(channel, oracle: false);
                Assert.Equal(new byte[] { 0x1f }, await channel.ReadPacketAsync());
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
            });
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = server.Port,
                UserId = "u",
                Password = "p",
                TlsMode = OceanBaseTlsMode.Disabled,
                ConnectionTimeout = 5,
                Pooling = true,
                ConnectionIdleLifetime = 1,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var first = pool.Rent(options, null, new EndpointSelector(), out var reused);
            await first.OpenAsync();
            Assert.True(pool.Return(first, new ConnectionLog(null)));
            await Task.Delay(TimeSpan.FromSeconds(1.1));

            var second = pool.Rent(options, null, new EndpointSelector(), out reused);
            Assert.False(reused);
            Assert.NotSame(first, second);
            second.Close();
            server.Stop();
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task IdleLifetimeDoesNotExpireWhileSessionIsBorrowed()
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 });
                await ServerIdentity.ReplyAsync(channel, oracle: false);
                Assert.Equal(new byte[] { 0x1f }, await channel.ReadPacketAsync());
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                channel.BeginCommand();
                var initDb = await channel.ReadPacketAsync();
                Assert.Equal((byte)0x02, initDb[0]);
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                channel.BeginCommand();
                Assert.Equal(new byte[] { 0x0e }, await channel.ReadPacketAsync());
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
            });
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = server.Port, UserId = "u", Password = "p",
                Database = "app", TlsMode = OceanBaseTlsMode.Disabled, ConnectionTimeout = 5,
                Pooling = true, ConnectionIdleLifetime = 1,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var first = pool.Rent(options, null, new EndpointSelector(), out var reused);
            await first.OpenAsync();
            await Task.Delay(TimeSpan.FromSeconds(1.1));

            Assert.True(pool.Return(first, new ConnectionLog(null)));
            var second = pool.Rent(options, null, new EndpointSelector(), out reused);
            Assert.True(reused);
            Assert.Same(first, second);
            second.Close();
            server.Stop();
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ResetFailureDestroysSessionInsteadOfReturningIt()
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 });
                await ServerIdentity.ReplyAsync(channel, oracle: false);
                Assert.Equal(new byte[] { 0x1f }, await channel.ReadPacketAsync());
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 1, 0, 0, 0 });
            });
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = server.Port, UserId = "u", Password = "p",
                TlsMode = OceanBaseTlsMode.Disabled, ConnectionTimeout = 5, Pooling = true,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var session = pool.Rent(options, null, new EndpointSelector(), out _);
            await session.OpenAsync();
            Assert.False(await pool.ReturnAsync(session, new ConnectionLog(null)));
            Assert.False(pool.TryRent(out _));
            server.Stop();
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task FailedOpenRemovesEmptyRegistryPool()
        {
            var server = LoopbackServer.Start(async client =>
            {
                client.Dispose();
                await Task.CompletedTask;
            });
            var connectionString = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = server.Port,
                UserId = "registry-test-user",
                Password = "p",
                TlsMode = OceanBaseTlsMode.Disabled,
                ConnectionTimeout = 1,
                Pooling = true,
            }.ConnectionString;
            var original = PhysicalSessionPoolRegistry.Get(connectionString);
            try
            {
                using var connection = new OceanBaseConnection(connectionString);
                await Assert.ThrowsAsync<OceanBaseConnector.OceanBaseException>(() => connection.OpenAsync().WaitAsync(TimeSpan.FromSeconds(10)));

                var replacement = PhysicalSessionPoolRegistry.Get(connectionString);
                Assert.NotSame(original, replacement);
                PhysicalSessionPoolRegistry.ReleaseIfEmpty(connectionString, replacement);
            }
            finally
            {
                PhysicalSessionPoolRegistry.ReleaseIfEmpty(connectionString, original);
                server.Stop();
            }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
