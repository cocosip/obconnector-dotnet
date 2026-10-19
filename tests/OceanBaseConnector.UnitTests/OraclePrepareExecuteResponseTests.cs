using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> OraclePrepareExecuteResponseCases()
        {
            foreach (var async in new[] { false, true })
                foreach (var sequential in new[] { false, true })
                    foreach (var deprecated in new[] { false, true })
                        foreach (var rows in new[] { false, true }) yield return new object[] { async, sequential, deprecated, rows };
        }

        private static byte[] OracleExecuteHeader(ushort parameters = 2, ushort columns = 1, uint flags = 4, bool rows = true)
            => new byte[] { 0, 42, 0, 0, 0, (byte)columns, (byte)(columns >> 8), (byte)parameters, (byte)(parameters >> 8),
                0, 0, 0, (byte)flags, (byte)(flags >> 8), (byte)(flags >> 16), (byte)(flags >> 24), rows ? (byte)1 : (byte)0 };

        private static async Task OracleExecuteRows(PacketChannel channel, bool deprecated, bool rows = true, ushort parameters = 2)
        {
            await channel.WritePacketAsync(OracleExecuteHeader(parameters, rows ? (ushort)1 : (ushort)0, rows: rows));
            for (var i = 0; i < parameters; i++) await channel.WritePacketAsync(Column("p", 253));
            if (parameters != 0 && !deprecated) await channel.WritePacketAsync(Eof());
            if (!rows) return;
            await channel.WritePacketAsync(Column("value", 8));
            if (!deprecated) await channel.WritePacketAsync(Eof());
            await channel.WritePacketAsync(new byte[] { 0, 0, 42, 0, 0, 0, 0, 0, 0, 0 });
            var end = deprecated ? Ok() : Eof(); if (deprecated) end[0] = 254;
            await channel.WritePacketAsync(end);
        }

        [Theory]
        [MemberData(nameof(OraclePrepareExecuteResponseCases))]
        public async Task OraclePrepareExecuteKeepsLeaseThroughTailAndReusesSession(bool async, bool sequential, bool deprecated, bool rows)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1, deprecated);
                await OracleExecuteRows(channel, deprecated, rows);
                waiting.SetResult(); await release.Task;
                await channel.WritePacketAsync(Ok(5));
                channel.BeginCommand(); await Query(channel, "select after", deprecated); await channel.WritePacketAsync(Ok(1));
            }, deprecated);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using var exchange = new CommandExchange(session, new ConnectionLog(null), 5, CancellationToken.None, () => { });
                var request = OraclePrepareExecutePacket.Build(0, "begin p(?, ?); end;", 2, new byte[] { 3, 1, 6, 0, 6, 0 });
                if (async) await exchange.SendAsync(request); else exchange.Send(request);
                var result = new QueryResult(exchange, binary: true, sequential: sequential, oracleParameterCount: 2);
                var reading = Task.Run(async () =>
                {
                    if (async) await result.StartAsync(CancellationToken.None); else result.Start();
                    using var reader = new OceanBaseDataReader(exchange, result, connection, session,
                        sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                    Assert.Equal(42u, result.OracleHeader!.StatementId); Assert.Equal(4u, result.OracleHeader.ExtendFlags);
                    Assert.Equal(rows, async ? await reader.ReadAsync() : reader.Read());
                    if (rows) { Assert.Equal(42L, reader.GetInt64(0)); Assert.False(async ? await reader.ReadAsync() : reader.Read()); }
                    Assert.False(async ? await reader.NextResultAsync() : reader.NextResult());
                    return reader.RecordsAffected;
                });
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(SessionState.Busy, session.State); Assert.False(reading.IsCompleted);
                release.SetResult(); Assert.Equal(5, await reading.WaitAsync(TimeSpan.FromSeconds(10)));
                using var after = connection.CreateCommand(); after.CommandText = "select after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "count")] [InlineData(true, "count")]
        [InlineData(false, "header")] [InlineData(true, "header")]
        [InlineData(false, "column")] [InlineData(true, "column")]
        [InlineData(false, "tail")] [InlineData(true, "tail")]
        public async Task OraclePrepareExecuteMalformedResponseBreaksWithUnknownOutcome(bool async, string fault)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1);
                if (fault == "tail") { await OracleExecuteRows(channel, false); await channel.WritePacketAsync(new byte[] { 1 }); }
                else if (fault == "header") await channel.WritePacketAsync(Ok());
                else
                {
                    await channel.WritePacketAsync(OracleExecuteHeader(fault == "count" ? (ushort)1 : (ushort)2));
                    if (fault == "column") await channel.WritePacketAsync(new byte[] { 1 });
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using var exchange = new CommandExchange(session, new ConnectionLog(null), 5, CancellationToken.None, () => { });
                if (async) await exchange.SendAsync(new byte[] { 0xa1 }); else exchange.Send(new byte[] { 0xa1 });
                var result = new QueryResult(exchange, binary: true, oracleParameterCount: 2);
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                {
                    if (async) { await result.StartAsync(CancellationToken.None); while (await result.ReadAsync(CancellationToken.None)) { } }
                    else { result.Start(); while (result.Read()) { } }
                });
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(SessionState.Broken, session.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task OraclePrepareExecuteTailErrorIsKnownAndDoesNotReplayOrBreakTheSession(bool async, bool sequential)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1); await OracleExecuteRows(channel, false);
                await channel.WritePacketAsync(Error());
                channel.BeginCommand(); await Query(channel, "select after error"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using var exchange = new CommandExchange(session, new ConnectionLog(null), 5, CancellationToken.None, () => { });
                if (async) await exchange.SendAsync(new byte[] { 0xa1 }); else exchange.Send(new byte[] { 0xa1 });
                var result = new QueryResult(exchange, binary: true, sequential: sequential, oracleParameterCount: 2);
                if (async) await result.StartAsync(CancellationToken.None); else result.Start();
                using var reader = new OceanBaseDataReader(exchange, result, connection, session,
                    sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(async ? await reader.ReadAsync() : reader.Read()); Assert.Equal(42L, reader.GetInt64(0));
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (async) await reader.ReadAsync(); else reader.Read(); });
                Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(1064, error.ServerErrorCode); Assert.DoesNotContain("secret", error.Message);
                using var after = connection.CreateCommand(); after.CommandText = "select after error"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task OraclePrepareExecuteTailReportsStateForTheNextResultSnapshot(bool async)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1); await OracleExecuteRows(channel, false);
                await channel.WritePacketAsync(TimeZoneOk("Asia/Tokyo", status: 14));
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("next", 8));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 43, 0, 0, 0, 0, 0, 0, 0 });
                await channel.WritePacketAsync(Eof());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using var exchange = new CommandExchange(session, new ConnectionLog(null), 5, CancellationToken.None, () => { });
                if (async) await exchange.SendAsync(new byte[] { 0xa1 }); else exchange.Send(new byte[] { 0xa1 });
                var result = new QueryResult(exchange, binary: true, oracleParameterCount: 2);
                if (async) await result.StartAsync(CancellationToken.None); else result.Start();
                Assert.Equal("+00:00", result.SessionTimeZone);
                while (async ? await result.ReadAsync(CancellationToken.None) : result.Read()) { }
                Assert.Equal("+00:00", result.SessionTimeZone); Assert.Equal("Asia/Tokyo", session.SessionTimeZone);
                Assert.True(async ? await result.NextResultAsync(CancellationToken.None) : result.NextResult());
                Assert.Equal("Asia/Tokyo", result.SessionTimeZone);
                Assert.True(async ? await result.ReadAsync(CancellationToken.None) : result.Read());
                Assert.False(async ? await result.ReadAsync(CancellationToken.None) : result.Read());
                Assert.Null(session.SessionTimeZone); Assert.Equal("Asia/Tokyo", result.SessionTimeZone);
                Assert.False(async ? await result.NextResultAsync(CancellationToken.None) : result.NextResult());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static async Task AssertOracleExecuteTransportClosed(PacketChannel channel)
        {
            channel.BeginCommand();
            var error = await Record.ExceptionAsync(() => channel.ReadPacketAsync());
            if (error is ProtocolException) Assert.Contains("Connection closed", error.Message);
            else Assert.Equal(System.Net.Sockets.SocketError.ConnectionReset,
                Assert.IsType<System.Net.Sockets.SocketException>(Assert.IsType<System.IO.IOException>(error).InnerException).SocketErrorCode);
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task OraclePrepareExecuteEarlyReaderCloseCannotReusePendingTail(bool async, bool sequential)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1); await OracleExecuteRows(channel, false);
                ready.SetResult();
                await AssertOracleExecuteTransportClosed(channel);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using var exchange = new CommandExchange(session, new ConnectionLog(null), 5, CancellationToken.None, () => { });
                if (async) await exchange.SendAsync(new byte[] { 0xa1 }); else exchange.Send(new byte[] { 0xa1 });
                var result = new QueryResult(exchange, binary: true, sequential: sequential, oracleParameterCount: 2);
                if (async) await result.StartAsync(CancellationToken.None); else result.Start();
                using var reader = new OceanBaseDataReader(exchange, result, connection, session,
                    sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(async ? await reader.ReadAsync() : reader.Read());
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (async) await reader.CloseAsync(); else reader.Close();
                Assert.Equal(SessionState.Broken, session.State); Assert.Equal(-1, reader.RecordsAffected);
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, false)] [InlineData(true, false, false)]
        [InlineData(false, true, false)] [InlineData(true, true, false)]
        [InlineData(false, false, true)] [InlineData(true, false, true)]
        [InlineData(false, true, true)] [InlineData(true, true, true)]
        public async Task OraclePrepareExecuteTailCanContinueOrdinaryResultsAndCounts(bool async, bool sequential, bool deprecated)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1, deprecated); await OracleExecuteRows(channel, deprecated);
                await channel.WritePacketAsync(Ok(5, 14));
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("later", 8));
                if (!deprecated) await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 43, 0, 0, 0, 0, 0, 0, 0 });
                var end = deprecated ? Ok(status: 14) : Eof(14); if (deprecated) end[0] = 254;
                await channel.WritePacketAsync(end); await channel.WritePacketAsync(Ok(2));
            }, deprecated);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using var exchange = new CommandExchange(session, new ConnectionLog(null), 5, CancellationToken.None, () => { });
                if (async) await exchange.SendAsync(new byte[] { 0xa1 }); else exchange.Send(new byte[] { 0xa1 });
                var result = new QueryResult(exchange, binary: true, sequential: sequential, oracleParameterCount: 2);
                if (async) await result.StartAsync(CancellationToken.None); else result.Start();
                using var reader = new OceanBaseDataReader(exchange, result, connection, session,
                    sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(async ? await reader.ReadAsync() : reader.Read()); Assert.Equal(42L, reader.GetInt64(0));
                Assert.True(async ? await reader.NextResultAsync() : reader.NextResult()); Assert.Equal(5, reader.RecordsAffected);
                Assert.True(async ? await reader.ReadAsync() : reader.Read()); Assert.Equal(43L, reader.GetInt64(0));
                Assert.True(async ? await reader.NextResultAsync() : reader.NextResult()); Assert.Equal(7, reader.RecordsAffected);
                Assert.Equal(0, reader.FieldCount); Assert.False(async ? await reader.NextResultAsync() : reader.NextResult());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, false)] [InlineData(true, false, false)]
        [InlineData(false, true, false)] [InlineData(true, true, false)]
        [InlineData(false, false, true)] [InlineData(true, false, true)]
        public async Task OraclePrepareExecuteCancellationAndTimeoutWhileAwaitingTailAreUnknown(bool async, bool sequential, bool timeout)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1); await OracleExecuteRows(channel, false);
                ready.SetResult();
                await AssertOracleExecuteTransportClosed(channel);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using var exchange = new CommandExchange(session, new ConnectionLog(null), timeout ? 1 : 5, cancellation.Token, () => { });
                if (async) await exchange.SendAsync(new byte[] { 0xa1 }); else exchange.Send(new byte[] { 0xa1 });
                var result = new QueryResult(exchange, binary: true, sequential: sequential, oracleParameterCount: 2);
                if (async) await result.StartAsync(CancellationToken.None); else result.Start();
                using var reader = new OceanBaseDataReader(exchange, result, connection, session,
                    sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(async ? await reader.ReadAsync() : reader.Read()); Assert.Equal(42L, reader.GetInt64(0));
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var reading = async ? reader.ReadAsync(cancellation.Token) : Task.Run(() => reader.Read());
                if (!timeout) cancellation.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () => await reading.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(error.IsExecutionOutcomeUnknown);
                if (timeout) Assert.IsType<TimeoutException>(error.InnerException);
                else Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error.InnerException).CancellationToken);
                Assert.Equal(SessionState.Broken, session.State);
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
