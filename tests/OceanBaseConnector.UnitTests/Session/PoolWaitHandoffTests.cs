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
    public sealed class PoolWaitHandoffTests
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
        public async Task CanceledNotifiedOpenPassesIdleSessionToAnotherWaiter(bool oracle, bool firstAsync, bool secondAsync)
        {
            await using var server = new CompressionServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle);
            await PoolWaitHandoffFixture.Verify(typeof(OceanBaseConnection), server, firstAsync, secondAsync);
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(false, true, true)]
        [InlineData(true, false, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(true, true, true)]
        public async Task CanceledNotifiedDataSourceOpenPassesIdleSessionToAnotherWaiter(bool oracle, bool firstAsync, bool secondAsync)
        {
            await using var server = new CompressionServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle);
            await PoolWaitHandoffFixture.Verify(typeof(OceanBaseConnection), server, firstAsync, secondAsync, dataSource: true);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public Task CanceledNotifiedWaiterPassesCreationCapacityToAnotherWaiter(bool firstAsync, bool secondAsync) =>
            VerifyCreationCapacity(firstAsync, secondAsync, resetFailure: false);

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public Task CanceledNotifiedWaiterPassesFailedResetCapacityToAnotherWaiter(bool firstAsync, bool secondAsync) =>
            VerifyCreationCapacity(firstAsync, secondAsync, resetFailure: true);

        private static async Task VerifyCreationCapacity(bool firstAsync, bool secondAsync, bool resetFailure)
        {
            var server = resetFailure ? LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 });
                await ServerIdentity.ReplyAsync(channel, oracle: false);
                Assert.Equal(new byte[] { 0x1f }, await channel.ReadPacketAsync());
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 1, 0, 0, 0 });
            }) : null;
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = server?.Port ?? 2881,
                UserId = "u",
                Pooling = true,
                MaxPoolSize = 1,
                PoolWaitTimeout = 30,
                TlsMode = OceanBaseTlsMode.Disabled,
            }.Build();
            using var pool = new PhysicalSessionPool();
            using var capture = new PoolWaitHandoffCapture();
            using var firstCancel = new CancellationTokenSource();
            using var secondCancel = new CancellationTokenSource();
            var owner = pool.Rent(options, null, new EndpointSelector(), out _);
            var first = new PoolWaitHandoffCapture.Attempt(firstCancel.Cancel);
            var second = new PoolWaitHandoffCapture.Attempt(secondCancel.Cancel);
            var firstRent = Rent(first, firstAsync, firstCancel.Token);
            var secondRent = Rent(second, secondAsync, secondCancel.Token);
            try
            {
                if (resetFailure) await owner.OpenAsync();
                await Task.WhenAll(first.Waiting.Task, second.Waiting.Task).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(firstRent.IsCompleted); Assert.False(secondRent.IsCompleted);
                if (resetFailure)
                    Assert.False(firstAsync ? await pool.ReturnAsync(owner, new ConnectionLog(null)) : pool.Return(owner, new ConnectionLog(null)));
                else pool.Discard(owner);
                var interrupted = await capture.Interrupted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var canceled = ReferenceEquals(interrupted, first) ? firstRent : secondRent;
                var survivor = ReferenceEquals(interrupted, first) ? secondRent : firstRent;
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
                Assert.Equal(ReferenceEquals(interrupted, first) ? firstCancel.Token : secondCancel.Token, error.CancellationToken);
                var replacement = await survivor.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.NotSame(owner, replacement);
                Assert.False(pool.IsEmpty);
            }
            finally
            {
                firstCancel.Cancel(); secondCancel.Cancel();
                try { await Task.WhenAll(Drain(firstRent), Drain(secondRent)); }
                finally
                {
                    try { pool.Discard(owner); }
                    finally
                    {
                        if (server != null) { server.Stop(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
                    }
                }
            }
            Assert.True(pool.IsEmpty);

            Task<PhysicalSession> Rent(PoolWaitHandoffCapture.Attempt attempt, bool asynchronous, CancellationToken token) =>
                capture.Run(attempt, () => asynchronous ? pool.RentAsync(options, null, new EndpointSelector(), token) :
                    Task.FromResult(pool.Rent(options, null, new EndpointSelector(), out _, token)));

            async Task Drain(Task<PhysicalSession> pending)
            {
                try { pool.Discard(await pending.WaitAsync(TimeSpan.FromSeconds(5))); }
                catch (OperationCanceledException) { }
            }
        }
    }
}
