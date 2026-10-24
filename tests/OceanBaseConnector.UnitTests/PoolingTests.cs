using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class PoolingTests
    {
        [Theory]
        [InlineData("SET SESSION time_zone = '+00:00'")]
        [InlineData("CREATE TEMPORARY TABLE pooled_state (id INT)")]
        [InlineData("/* trace */ SET SESSION time_zone = '+00:00'")]
        [InlineData("-- trace\nSET SESSION time_zone = '+00:00'")]
        [InlineData("# trace\nSET SESSION time_zone = '+00:00'")]
        [InlineData("SET/* trace */ SESSION time_zone = '+00:00'")]
        [InlineData("CREATE /* trace */ TEMPORARY TABLE pooled_state (id INT)")]
        [InlineData("CREATE\nTEMPORARY TABLE pooled_state (id INT)")]
        [InlineData("/*! SET SESSION time_zone = '+00:00' */")]
        [InlineData("CALL pooled_state()")]
        public async Task UserSessionStateChangeDoesNotReturnPhysicalSessionToPool(string commandText)
            => await VerifyStatefulReturn(commandText, multiStatements: false, oracle: false, asynchronous: true);

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task LaterTextStatementDiscardsSessionAfterAllResults(bool oracle, bool asynchronous)
            => await VerifyStatefulReturn("SELECT 1; " + (oracle ? "ALTER\nSESSION SET TIME_ZONE = '+00:00'" : "SET SESSION time_zone = '+00:00'"),
                multiStatements: true, oracle, asynchronous);

        private static async Task VerifyStatefulReturn(string commandText, bool multiStatements, bool oracle, bool asynchronous)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serving = Task.Run(async () =>
            {
                var first = await listener.AcceptTcpClientAsync(stop.Token);
                var firstTask = Serve(first, expectUserCommand: true, commandText, stop.Token);
                var second = await listener.AcceptTcpClientAsync(stop.Token);
                await Task.WhenAll(firstTask, Serve(second, expectUserCommand: false, commandText, stop.Token, secondReady));
            }, stop.Token);

            var connectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;Password=p;TLS Mode=Disabled;Pooling=true;Max Pool Size=1;Allow Multi Statements={multiStatements}";
            using var connection = new OceanBaseConnection(connectionString);
            try
            {
                if (asynchronous) await connection.OpenAsync(stop.Token); else connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = commandText;
                    Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync(stop.Token) : command.ExecuteNonQuery());
                }
                Assert.True(connection.GetOpenSession().RequiresDiscardOnReturn);
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                if (asynchronous) await connection.OpenAsync(stop.Token); else connection.Open();
                await secondReady.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                await serving.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                connection.Close();
                OceanBaseConnection.ClearPool(connection);
                stop.Cancel();
                listener.Stop();
            }
            async Task Serve(TcpClient client, bool expectUserCommand, string expectedCommand, CancellationToken token, TaskCompletionSource? ready = null)
            {
                using (client)
                {
                    var channel = new PacketChannel(client.GetStream());
                    var greeting = (byte[])ProtocolFixture.Load("initial-handshake-oceanbase").Payload.Clone();
                    if (multiStatements) greeting[41] |= 1;
                    await channel.WritePacketAsync(greeting, token);
                    await channel.ReadPacketAsync(token);
                    await channel.WritePacketAsync(oracle ? TextCommandTests.TimeZoneOk("+00:00") : new byte[] { 0, 0, 0, 2, 0, 0, 0 }, token);
                    await ServerIdentity.ReplyAsync(channel, oracle, token: token);
                    ready?.TrySetResult();
                    if (expectUserCommand)
                    {
                        var request = await channel.ReadPacketAsync(token);
                        Assert.Equal(3, request[0]);
                        Assert.Equal(expectedCommand, Encoding.UTF8.GetString(request, 1, request.Length - 1));
                        if (multiStatements) await channel.WritePacketAsync(new byte[] { 0, 0, 0, 14, 0, 0, 0 }, token);
                        await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 }, token);
                        channel.BeginCommand();
                    }
                    else
                    {
                        Assert.Equal(new byte[] { 0x1f }, await channel.ReadPacketAsync(token));
                        await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 }, token);
                    }
                    if (expectUserCommand)
                    {
                        Assert.Equal(0, await client.GetStream().ReadUpToAsync(new byte[1], 1).WaitAsync(token));
                    }
                }
            }
        }

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
