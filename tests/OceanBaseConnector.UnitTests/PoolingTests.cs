using System;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class PoolingTests
    {
        [Fact]
        public async Task LogicalConnectionsReuseOneAuthenticatedPhysicalSession()
        {
            var resets = 0;
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                await ServerIdentity.ReplyAsync(channel);
                for (var i = 0; i != 2; i++)
                {
                    if (i == 1)
                    {
                        Assert.Equal(new byte[] { 0x0e }, await channel.ReadPacketAsync());
                        await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                        channel.BeginCommand();
                    }
                    var packet = await channel.ReadPacketAsync();
                    Assert.Equal(new byte[] { 0x1f }, packet);
                    resets++;
                    await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                    if (i == 0) channel.BeginCommand();
                }
            });

            var connectionString = $"Server=127.0.0.1;Port={server.Port};User ID=u;Password=p;TLS Mode=Disabled;Pooling=true";
            using var connection = new OceanBaseConnection(connectionString);
            await connection.OpenAsync();
            var first = connection.GetOpenSession();
            await connection.CloseAsync();
            await connection.OpenAsync();
            Assert.Same(first, connection.GetOpenSession());
            await connection.CloseAsync();
            server.Stop();
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, resets);
        }
    }
}
