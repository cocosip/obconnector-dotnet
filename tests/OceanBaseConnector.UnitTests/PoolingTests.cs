using System;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class PoolingTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ResetInvalidatesPreparedStatementsBeforeTheNextLogicalBorrower(bool asynchronous)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 });
                await ServerIdentity.ReplyAsync(channel, oracle: false);

                await AssertCommand(channel, 22, 37);
                await AssertExecute(channel, 37);
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                channel.BeginCommand();
                Assert.Equal(new byte[] { 0x1f }, await channel.ReadPacketAsync());
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                await AssertPing(channel);

                await AssertCommand(channel, 22, 38);
                await AssertExecute(channel, 38);
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                channel.BeginCommand();
                Assert.Equal(new byte[] { 0x1f }, await channel.ReadPacketAsync());
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
            });

            using var connection = new OceanBaseConnection(
                $"Server=127.0.0.1;Port={server.Port};User ID=u;Password=p;TLS Mode=Disabled;Pooling=true");
            using var command = connection.CreateCommand();
            command.CommandText = "update sample set value = 1";
            if (asynchronous) await connection.OpenAsync(); else connection.Open();
            if (asynchronous) await command.PrepareAsync(); else command.Prepare();
            Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            if (asynchronous) await connection.CloseAsync(); else connection.Close();
            if (asynchronous) await connection.OpenAsync(); else connection.Open();
            Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            if (asynchronous) await connection.CloseAsync(); else connection.Close();
            server.Stop();
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            static async Task AssertCommand(PacketChannel channel, byte commandCode, byte statementId)
            {
                channel.BeginCommand();
                var request = await channel.ReadPacketAsync();
                Assert.Equal(commandCode, request[0]);
                await channel.WritePacketAsync(new byte[] { 0, statementId, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
            }

            static async Task AssertExecute(PacketChannel channel, byte statementId)
            {
                channel.BeginCommand();
                var request = await channel.ReadPacketAsync();
                Assert.Equal(23, request[0]);
                Assert.Equal(statementId, request[1]);
            }

            static async Task AssertPing(PacketChannel channel)
            {
                channel.BeginCommand();
                Assert.Equal(new byte[] { 0x0e }, await channel.ReadPacketAsync());
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
            }
        }

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
