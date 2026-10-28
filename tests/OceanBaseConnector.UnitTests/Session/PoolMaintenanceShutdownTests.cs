using System;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    public sealed class PoolMaintenanceShutdownTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task DisposeClosesSessionDuringPoolMaintenance(bool probe, bool asynchronous)
        {
            var maintenanceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 });
                await ServerIdentity.ReplyAsync(channel, oracle: false);
                Assert.Equal(new byte[] { 0x1f }, await channel.ReadPacketAsync());
                if (probe)
                {
                    await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 });
                    channel.BeginCommand();
                    Assert.Equal(new byte[] { 0x0e }, await channel.ReadPacketAsync());
                }
                maintenanceStarted.SetResult();
                Assert.Equal(0, await client.GetStream().ReadUpToAsync(new byte[1], 1));
            });
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = server.Port, UserId = "u", Password = "p",
                TlsMode = OceanBaseTlsMode.Disabled, ConnectionTimeout = 5,
                Pooling = true, MaxPoolSize = 1, CommandTimeout = 0,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var session = await pool.RentAsync(options, null, new EndpointSelector());
            await session.OpenAsync();
            if (probe) Assert.True(await pool.ReturnAsync(session, new ConnectionLog(null)));

            var maintenance = Task.Run(async () =>
            {
                if (probe)
                {
                    if (asynchronous) await pool.RentAsync(options, null, new EndpointSelector());
                    else pool.Rent(options, null, new EndpointSelector(), out _);
                }
                else
                {
                    var returned = asynchronous
                        ? await pool.ReturnAsync(session, new ConnectionLog(null))
                        : pool.Return(session, new ConnectionLog(null));
                    Assert.False(returned);
                }
            });
            try
            {
                await maintenanceStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                pool.Dispose();
                if (probe)
                    await Assert.ThrowsAsync<ObjectDisposedException>(() => maintenance.WaitAsync(TimeSpan.FromSeconds(2)));
                else await maintenance.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(SessionState.Closed, session.State);
                Assert.True(pool.IsEmpty);
                Assert.False(pool.TryRent(out _));
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                pool.Dispose();
                session.Dispose();
                server.Stop();
                await Record.ExceptionAsync(() => maintenance.WaitAsync(TimeSpan.FromSeconds(10)));
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
