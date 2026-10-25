using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    public sealed partial class HandshakeFlowTests
    {
        public static IEnumerable<object[]> OracleAutocommitResponses()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var response in new[] { "ok", "disabled", "transaction", "more", "affected", "insert", "cursor", "short", "error", "empty", "eof", "last", "output", "readonly" })
                    yield return new object[] { asynchronous, response };
        }

        private static byte[] AutocommitResponse(string response) => response switch
        {
            "disabled" => new byte[] { 0, 0, 0, 0, 0, 0, 0 },
            "transaction" => new byte[] { 0, 0, 0, 3, 0, 0, 0 },
            "more" => new byte[] { 0, 0, 0, 10, 0, 0, 0 },
            "affected" => new byte[] { 0, 1, 0, 2, 0, 0, 0 },
            "insert" => new byte[] { 0, 0, 1, 2, 0, 0, 0 },
            "cursor" => new byte[] { 0, 0, 0, 66, 0, 0, 0 },
            "short" => new byte[] { 0, 0 },
            "empty" => Array.Empty<byte>(),
            "eof" => new byte[] { 254, 0, 0, 2, 0, 0, 0 },
            "last" => new byte[] { 0, 0, 0, 130, 0, 0, 0 },
            "output" => new byte[] { 0, 0, 0, 2, 16, 0, 0 },
            "readonly" => new byte[] { 0, 0, 0, 2, 32, 0, 0 },
            "error" => new byte[] { 255, 21, 4, 35, 72, 89, 48, 48, 48 },
            _ => new byte[] { 0, 0, 0, 2, 0, 0, 0 }
        };

        [Theory]
        [MemberData(nameof(OracleAutocommitResponses))]
        public async Task OracleOpenRequiresAnExplicitConfirmedAutocommitBaseline(bool asynchronous, string response)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake());
                await channel.ReadPacketAsync();
                // Advertised autocommit does not replace explicit initialization.
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: true));
                await IdentityResponse(channel, oracle: true, initializeAutocommit: false);
                channel.BeginCommand();
                var request = await channel.ReadPacketAsync();
                Assert.Equal(3, request[0]);
                Assert.Equal("set autocommit = 1", Encoding.ASCII.GetString(request, 1, request.Length - 1));
                await channel.WritePacketAsync(AutocommitResponse(response));
            });
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={server.Port};User ID=u;TLS Mode=Disabled;Connection Timeout=5");
            try
            {
                var error = asynchronous ? await Record.ExceptionAsync(() => connection.OpenAsync()) : Record.Exception(connection.Open);
                if (response == "ok")
                {
                    Assert.Null(error);
                    Assert.Equal(ConnectionState.Open, connection.State);
                }
                else
                {
                    Assert.IsType<OceanBaseException>(error);
                    Assert.Equal(ConnectionState.Closed, connection.State);
                    if (response == "error") Assert.Equal(1045, ((OceanBaseException)error!).ServerErrorCode);
                }
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally { connection.Close(); server.Stop(); }
        }
        [Theory]
        [MemberData(nameof(OracleAutocommitResponses))]
        public async Task OracleResetConfirmsAutocommitBeforeReuse(bool asynchronous, string response)
        {
            var server = LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                await channel.WritePacketAsync(BuildInitialHandshake());
                await channel.ReadPacketAsync();
                await channel.WritePacketAsync(BuildAuthOk(oracleMode: true));
                await IdentityResponse(channel, oracle: true);
                channel.BeginCommand();
                Assert.Equal(new byte[] { 0x1f }, await channel.ReadPacketAsync());
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 4, 0, 0, 0 });
                channel.BeginCommand();
                var command = await channel.ReadPacketAsync();
                Assert.Equal(3, command[0]);
                Assert.Equal("set autocommit = 1", Encoding.ASCII.GetString(command, 1, command.Length - 1));
                await channel.WritePacketAsync(AutocommitResponse(response));
                if (response == "ok")
                {
                    channel.BeginCommand();
                    Assert.Equal(new byte[] { 14 }, await channel.ReadPacketAsync());
                    await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                }
            });
            var options = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = server.Port,
                UserId = "u",
                TlsMode = OceanBaseTlsMode.Disabled,
                Pooling = true,
                MaxPoolSize = 1,
                PoolWaitTimeout = 1,
                ConnectionTimeout = 5,
            }.Build();
            using var pool = new PhysicalSessionPool();
            var session = pool.Rent(options, null, new EndpointSelector(), out _);
            try
            {
                if (asynchronous) await session.OpenAsync(); else session.Open();
                var generation = session.Generation;
                var returned = asynchronous
                    ? await pool.ReturnAsync(session, new ConnectionLog(null), timeout: 2)
                    : pool.Return(session, new ConnectionLog(null), timeout: 2);
                Assert.Equal(response == "ok", returned);
                Assert.Equal(generation + (returned ? 1 : 0), session.Generation);
                Assert.Equal(returned ? SessionState.Open : SessionState.Closed, session.State);
                var next = pool.Rent(options, null, new EndpointSelector(), out var reused);
                Assert.Equal(returned, reused);
                if (returned) Assert.Same(session, next);
                else Assert.NotSame(session, next);
                next.Close();
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally { session.Close(); server.Stop(); }
        }

    }
}
