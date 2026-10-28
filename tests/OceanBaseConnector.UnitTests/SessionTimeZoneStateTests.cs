using System;
using System.Data;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {

        private static LoopbackServer TimeZoneServer(Func<PacketChannel, Task> exchange, bool deprecated = false)
            => LoopbackServer.Start(async client =>
            {
                var channel = new PacketChannel(client.GetStream());
                var greeting = (byte[])ProtocolFixture.Load("initial-handshake-oceanbase").Payload.Clone();
                if (deprecated) greeting[Array.IndexOf(greeting, (byte)0, 1) + 20] |= 1;
                await channel.WritePacketAsync(greeting); await channel.ReadPacketAsync();
                await channel.WritePacketAsync(TimeZoneOk("+00:00")); await ServerIdentity.ReplyAsync(channel, deprecated: deprecated); channel.BeginCommand();
                await exchange(channel);
            });

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SessionTimeZoneRefreshDoesNotRewriteAuthenticationFacts(bool asynchronous)
        {
            var server = TimeZoneServer(async channel =>
            {
                await Query(channel, "change zone"); await channel.WritePacketAsync(TimeZoneOk("Asia/Tokyo"));
                channel.BeginCommand(); await Query(channel, "untracked change"); await channel.WritePacketAsync(Ok());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                Assert.Equal("+00:00", session.SessionTimeZone);
                using var command = connection.CreateCommand(); command.CommandText = "change zone";
                if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
                Assert.Equal("Asia/Tokyo", session.SessionTimeZone);
                Assert.Equal("+00:00", session.Info!.SessionVariables["time_zone"]);
                command.CommandText = "untracked change";
                if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
                Assert.Null(session.SessionTimeZone);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        public async Task SessionTimeZoneResultSnapshotsDoNotLeakAcrossUnreportedResults(bool asynchronous, bool empty, bool deprecated)
        {
            var server = TimeZoneServer(async channel =>
            {
                await Query(channel, "three results");
                for (var i = 0; i < 3; i++)
                {
                    await channel.WritePacketAsync(new byte[] { 1 });
                    await channel.WritePacketAsync(Column("value"));
                    if (!deprecated) await channel.WritePacketAsync(Eof());
                    if (!empty) await channel.WritePacketAsync(new byte[] { 1, 49 });
                    await channel.WritePacketAsync(deprecated
                        ? TimeZoneOk(i == 0 ? "Asia/Tokyo" : null, 254, i < 2 ? (byte)14 : (byte)6)
                        : Eof(i < 2 ? (byte)14 : (byte)6));
                }
            }, deprecated: deprecated);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using var exchange = new CommandExchange(session, new ConnectionLog(null), 5, CancellationToken.None, () => { });
                if (asynchronous) await exchange.SendAsync(OceanBaseCommand.SqlPacket("three results", 3));
                else exchange.Send(OceanBaseCommand.SqlPacket("three results", 3));
                Assert.Null(session.SessionTimeZone);
                var result = new QueryResult(exchange);
                if (asynchronous) await result.StartAsync(CancellationToken.None); else result.Start();
                var expectedZones = new string?[] { "+00:00", deprecated ? "Asia/Tokyo" : null, null };
                for (var i = 0; i < expectedZones.Length; i++)
                {
                    var expected = expectedZones[i];
                    Assert.Equal(expected, result.SessionTimeZone);
                    while (asynchronous ? await result.ReadAsync(CancellationToken.None) : result.Read()) { }
                    Assert.Equal(expected, result.SessionTimeZone);
                    if (i < 2) Assert.True(asynchronous ? await result.NextResultAsync(CancellationToken.None) : result.NextResult());
                }
                Assert.Null(session.SessionTimeZone);
                Assert.False(asynchronous ? await result.NextResultAsync(CancellationToken.None) : result.NextResult());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SessionTimeZoneNextResultErrorInvalidatesPreviousReport(bool asynchronous)
        {
            var server = TimeZoneServer(async channel =>
            {
                await Query(channel, "ok then error");
                await channel.WritePacketAsync(TimeZoneOk("Asia/Tokyo", status: 14));
                await channel.WritePacketAsync(Error());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using var command = connection.CreateCommand(); command.CommandText = "ok then error";
                if (asynchronous) await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteNonQueryAsync());
                else Assert.Throws<OceanBaseException>(() => command.ExecuteNonQuery());
                Assert.Equal(ConnectionState.Open, connection.State); Assert.Null(session.SessionTimeZone);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SessionTimeZonePrepareRetainsFactsButSqlErrorInvalidatesThem(bool asynchronous)
        {
            var server = TimeZoneServer(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 105, 0);
                await StatementCommand(channel, 23); await channel.WritePacketAsync(Error());
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using var command = connection.CreateCommand(); command.CommandText = "select 1";
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                Assert.Equal("+00:00", session.SessionTimeZone);
                if (asynchronous) await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteNonQueryAsync());
                else Assert.Throws<OceanBaseException>(() => command.ExecuteNonQuery());
                Assert.Null(session.SessionTimeZone); Assert.Equal(ConnectionState.Open, connection.State);
                if (asynchronous) await command.DisposeAsync(); else command.Dispose();
                Assert.Null(session.SessionTimeZone);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SessionTimeZoneTransactionControlsConsumeReportsAndInvalidateEverySql(bool asynchronous)
        {
            var server = TimeZoneServer(async channel =>
            {
                await Query(channel, "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED"); await channel.WritePacketAsync(TimeZoneOk("Europe/Paris"));
                channel.BeginCommand(); await Query(channel, "set autocommit = 0"); await channel.WritePacketAsync(TimeZoneOk("+01:00", status: 4));
                channel.BeginCommand(); await Query(channel, "ROLLBACK"); await channel.WritePacketAsync(TimeZoneOk("+02:00", status: 4));
                channel.BeginCommand(); await Query(channel, "set autocommit = 1"); await channel.WritePacketAsync(Ok());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using var transaction = asynchronous ? await connection.BeginTransactionAsync() : connection.BeginTransaction();
                Assert.Equal("+01:00", session.SessionTimeZone);
                if (asynchronous) await transaction.RollbackAsync(); else transaction.Rollback();
                Assert.Null(session.SessionTimeZone);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SessionTimeZonePreCanceledSqlDoesNotInvalidateFacts(bool asynchronous)
        {
            var server = TimeZoneServer(async channel => { await Assert.ThrowsAsync<ProtocolException>(() => channel.ReadPacketAsync()); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                using var exchange = new CommandExchange(session, new ConnectionLog(null), 5, cancellation.Token, () => { });
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asynchronous
                    ? exchange.SendAsync(OceanBaseCommand.SqlPacket("must not send", 3))
                    : RunSync(() => exchange.Send(OceanBaseCommand.SqlPacket("must not send", 3))));
                Assert.Equal("+00:00", session.SessionTimeZone); Assert.Equal(SessionState.Open, session.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SessionTimeZoneLateOkCannotReviveClosedOrBrokenSession(bool broken)
        {
            var server = TimeZoneServer(async channel => { await Query(channel, "change"); await channel.WritePacketAsync(TimeZoneOk("+05:00")); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using var exchange = new CommandExchange(session, new ConnectionLog(null), 5, CancellationToken.None, () => { });
                exchange.Send(OceanBaseCommand.SqlPacket("change", 3));
                var ok = OkPacket.Parse(exchange.Receive(), exchange.Capabilities);
                if (broken) session.Break(); else session.Close();
                exchange.ObserveSessionState(ok); exchange.Complete();
                Assert.Null(session.SessionTimeZone); Assert.Null(exchange.SessionTimeZone);
                Assert.Equal(broken ? SessionState.Broken : SessionState.Closed, session.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task SessionTimeZoneStaleExchangeCannotOverwriteNewLeaseReport()
        {
            var server = TimeZoneServer(async channel =>
            {
                await Query(channel, "first"); await channel.WritePacketAsync(TimeZoneOk("+01:00"));
                channel.BeginCommand(); await Query(channel, "second"); await channel.WritePacketAsync(TimeZoneOk("+02:00"));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using var first = new CommandExchange(session, new ConnectionLog(null), 5, CancellationToken.None, () => { });
                first.Send(OceanBaseCommand.SqlPacket("first", 3));
                var oldReport = OkPacket.Parse(first.Receive(), first.Capabilities);
                first.ObserveSessionState(oldReport); first.Complete();
                using var second = new CommandExchange(session, new ConnectionLog(null), 5, CancellationToken.None, () => { });
                second.Send(OceanBaseCommand.SqlPacket("second", 3));
                second.ObserveSessionState(OkPacket.Parse(second.Receive(), second.Capabilities));
                first.ObserveSessionState(oldReport);
                Assert.Equal("+02:00", session.SessionTimeZone); Assert.Equal(SessionState.Busy, session.State);
                second.Complete();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SessionTimeZoneStatementCloseDoesNotInvalidateFacts(bool asynchronous)
        {
            var server = TimeZoneServer(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 106, 0);
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                var command = connection.CreateCommand(); command.CommandText = "select 1";
                if (asynchronous) { await command.PrepareAsync(); await command.DisposeAsync(); }
                else { command.Prepare(); command.Dispose(); }
                Assert.Equal("+00:00", session.SessionTimeZone);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SessionTimeZoneCloseOrBreakClearsAuthenticationState(bool broken)
        {
            var server = TimeZoneServer(async channel => { await Assert.ThrowsAsync<ProtocolException>(() => channel.ReadPacketAsync()); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                Assert.Equal("+00:00", session.SessionTimeZone);
                if (broken) session.Break(); else session.Close();
                Assert.Null(session.SessionTimeZone);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
