using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static void SetFetchSize(DbCommand command, int rows)
        {
            ((OceanBaseCommand)command).FetchSize = rows;
        }

        private static byte[] CursorTerminator(byte flags, bool deprecated)
            => deprecated ? new byte[] { 254, 0, 0, flags, 0, 0, 0 } : Eof(flags);

        private static async Task CursorMetadata(PacketChannel channel, byte flags, bool deprecated = false, bool omitTerminator = false)
        {
            await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("n", 8));
            if (!omitTerminator) await channel.WritePacketAsync(CursorTerminator(flags, deprecated));
        }

        private static byte[] CursorRow(long value)
        { var row = new byte[10]; BitConverter.GetBytes(value).CopyTo(row, 2); return row; }

        [Theory]
        [InlineData(false, false, false, false)]
        [InlineData(true, false, false, false)]
        [InlineData(false, true, false, false)]
        [InlineData(true, true, false, false)]
        [InlineData(false, false, true, false)]
        [InlineData(true, false, true, false)]
        [InlineData(false, true, true, false)]
        [InlineData(true, true, true, false)]
        [InlineData(false, false, true, true)]
        [InlineData(true, false, true, true)]
        [InlineData(false, true, true, true)]
        [InlineData(true, true, true, true)]
        [InlineData(false, false, false, true)]
        [InlineData(true, false, false, true)]
        [InlineData(false, true, false, true)]
        [InlineData(true, true, false, true)]
        public async Task CursorPagesKeepStatementLeaseAndAllowReuse(bool asynchronous, bool sequential, bool mysql, bool deprecated)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22, deprecated); await Prepared(channel, 80, 0, 1, deprecated);
                var execute = await StatementCommand(channel, 23, deprecated);
                Assert.Equal(new byte[] { 23, 80, 0, 0, 0, 1, 1, 0, 0, 0 }, execute);
                await CursorMetadata(channel, 70, deprecated, !mysql && deprecated);
                for (var page = 0; page < 3; page++)
                {
                    var fetch = await StatementCommand(channel, 28, deprecated);
                    Assert.Equal(new byte[] { 28, 80, 0, 0, 0, 2, 0, 0, 0 }, fetch);
                    if (!mysql) await CursorMetadata(channel, 6, deprecated, deprecated);
                    if (page == 0) { await channel.WritePacketAsync(CursorRow(10)); await channel.WritePacketAsync(CursorRow(11)); }
                    if (page == 1) await channel.WritePacketAsync(CursorRow(12));
                    await channel.WritePacketAsync(CursorTerminator(page == 2 ? (byte)134 : (byte)70, deprecated));
                }
                execute = await StatementCommand(channel, 23, deprecated); Assert.Equal(0, execute[5]);
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("n", 8));
                if (!deprecated) await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(CursorRow(13));
                await channel.WritePacketAsync(CursorTerminator(6, deprecated));
                await StatementCommand(channel, 25, deprecated);
            }, deprecated);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var command = connection.CreateCommand(); command.CommandText = "select n"; SetFetchSize(command, 2);
                using (var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default) : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default))
                {
                    Assert.True(reader.HasRows); Assert.Single(reader.GetColumnSchema());
                    using var other = connection.CreateCommand(); other.CommandText = "must not send"; Assert.Throws<InvalidOperationException>(() => other.ExecuteNonQuery());
                    for (var value = 10; value < 13; value++) { Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal(value, asynchronous ? await reader.GetFieldValueAsync<long>(0) : reader.GetInt64(0)); }
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.False(await reader.NextResultAsync());
                }
                Assert.Equal(ConnectionState.Open, connection.State); SetFetchSize(command, 0);
                Assert.Equal(13L, asynchronous ? await command.ExecuteScalarAsync() : command.ExecuteScalar());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CursorReaderTokenCancelsPartialFetchWriteWithoutReplay(bool sequential)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "cursor"; SetFetchSize(command, 1);
                var input = CursorFaultInput(new[] { CursorRow(7), Eof(70) });
                var limit = ResponseFrame(OceanBaseCommand.SqlPacket("cursor", 22), 0).Length + 14 + 13 + 6;
                var transport = new ProtocolFaultStream(input, limit) { PauseAfterPartialWrite = true }; InstallFaultStream(connection, transport);
                using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(await reader.ReadAsync()); Assert.Equal(7L, await reader.GetFieldValueAsync<long>(0));
                var pending = reader.ReadAsync(cancellation.Token); await transport.PartialWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error.InnerException).CancellationToken);
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.Equal(limit, transport.Written.Length); Assert.Equal(0, transport.SyncCalls);
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery()); Assert.Equal(limit, transport.Written.Length);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CursorFetchErrorKeepsKnownServerErrorButDestroysCursorSession(bool sequential)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "cursor"; SetFetchSize(command, 1);
                InstallFaultStream(connection, new ProtocolFaultStream(CursorFaultInput(new[] { CursorRow(7), Error() })));
                using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(await reader.ReadAsync());
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => reader.ReadAsync());
                Assert.Equal(1064, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void CursorFetchSizeHasOneSharedPublicContract()
        {
            using var command = new OceanBaseCommand(); Assert.Equal(0, command.FetchSize);
            Assert.Throws<ArgumentOutOfRangeException>(() => command.FetchSize = -1); Assert.Equal(0, command.FetchSize);
            command.FetchSize = int.MaxValue; Assert.Equal(int.MaxValue, command.FetchSize);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CursorServerErrorDiscardsSessionBeforeFailureCallbacks(bool sequential)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var log = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(Configuration(server.Port), log);
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "cursor"; SetFetchSize(command, 1);
                var transport = new ProtocolFaultStream(CursorFaultInput(new[] { CursorRow(7), Error() })); InstallFaultStream(connection, transport);
                using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(await reader.ReadAsync());
                ConnectionState? callbackState = null; Exception? nestedError = null;
                var written = transport.Written.Length;
                log.OnLog = id =>
                {
                    if (id.Id != 1101) return;
                    log.OnLog = null; callbackState = connection.State;
                    using var nested = connection.CreateCommand(); nested.CommandText = "must not send";
                    nestedError = Record.Exception(() => nested.ExecuteNonQuery());
                };
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => reader.ReadAsync());
                Assert.Equal(1064, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, callbackState); Assert.IsType<InvalidOperationException>(nestedError);
                Assert.Equal(written, transport.Written.Length);
            }
            finally { log.OnLog = null; connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CursorBinaryRowCannotConfirmDeprecatedEofCursor(bool asynchronous)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)), true);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                using var command = connection.CreateCommand(); command.CommandText = "cursor"; SetFetchSize(command, 1);
                using var input = new MemoryStream();
                AppendFrames(input, new[] { new byte[] { 0, 80, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, Column("n", 8) });
                AppendFrames(input, new[] { new byte[] { 1 }, Column("n", 8), CursorRow(0x0000020000008000) });
                var transport = new ProtocolFaultStream(input.ToArray()); InstallFaultStream(connection, transport);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteReaderAsync() : Task.FromResult(command.ExecuteReader()));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
                Assert.Equal(ResponseFrame(OceanBaseCommand.SqlPacket("cursor", 22), 0).Length + 14, transport.Written.Length);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CursorInitiallyEmptyDeprecatedResultKeepsSessionReport(bool sequential)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)), true);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection); var session = connection.GetOpenSession();
                using var command = connection.CreateCommand(); command.CommandText = "cursor"; SetFetchSize(command, 1);
                using var input = new MemoryStream();
                AppendFrames(input, new[] { new byte[] { 0, 80, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, Column("n", 8) });
                AppendFrames(input, new[] { new byte[] { 1 }, Column("n", 8), TimeZoneOk("Europe/Paris", 254, 134) });
                var transport = new ProtocolFaultStream(input.ToArray()); InstallFaultStream(connection, transport);
                using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.False(reader.HasRows); Assert.False(await reader.ReadAsync()); Assert.Equal(ConnectionState.Open, connection.State);
                Assert.Equal("Europe/Paris", session.SessionTimeZone);
                Assert.Equal(ResponseFrame(OceanBaseCommand.SqlPacket("cursor", 22), 0).Length + 14, transport.Written.Length);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CursorEveryBatchConsumesSessionStateReports(bool sequential)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)), true);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection); var session = connection.GetOpenSession();
                using var command = connection.CreateCommand(); command.CommandText = "cursor"; SetFetchSize(command, 1);
                using var input = new MemoryStream();
                AppendFrames(input, new[] { new byte[] { 0, 80, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, Column("n", 8) });
                AppendFrames(input, new[] { new byte[] { 1 }, Column("n", 8), TimeZoneOk("Asia/Tokyo", 254, 70) });
                AppendFrames(input, new[] { CursorRow(7), CursorTerminator(70, true) });
                AppendFrames(input, new[] { CursorRow(8), TimeZoneOk("Europe/Paris", 254, 134) });
                InstallFaultStream(connection, new ProtocolFaultStream(input.ToArray()));
                using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.Equal("Asia/Tokyo", session.SessionTimeZone); Assert.True(await reader.ReadAsync());
                Assert.True(await reader.ReadAsync()); Assert.Null(session.SessionTimeZone);
                Assert.False(await reader.ReadAsync()); Assert.Equal("Europe/Paris", session.SessionTimeZone); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CursorOracleDeprecatedEofEmptyResultRequiresFetch(bool sequential)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22, true); await Prepared(channel, 80, 0, 1, true); await StatementCommand(channel, 23, true);
                await CursorMetadata(channel, 70, true, true);
                var fetch = await StatementCommand(channel, 28, true); Assert.Equal(1, fetch[5]);
                await CursorMetadata(channel, 6, true, true); await channel.WritePacketAsync(CursorTerminator(134, true));
                await StatementCommand(channel, 25, true);
            }, true);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "cursor"; SetFetchSize(command, 1);
                using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.False(reader.HasRows); Assert.Equal(1, reader.FieldCount); Assert.False(await reader.ReadAsync()); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CursorNonResultStatementIsRejectedBeforeExecute(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 80, 0);
                await StatementCommand(channel, 25); channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "update"; SetFetchSize(command, 1);
                    await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? command.ExecuteReaderAsync() : Task.FromResult(command.ExecuteReader()));
                    Assert.Equal(ConnectionState.Open, connection.State);
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("unconfirmed")]
        [InlineData("noProgress")]
        [InlineData("lostCursor")]
        [InlineData("tooManyRows")]
        [InlineData("metadata")]
        [InlineData("moreResults")]
        public async Task CursorMalformedResponseDestroysSession(string fault)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "cursor"; SetFetchSize(command, 1);
                var rows = fault == "noProgress" ? new[] { Eof(70) } : fault == "tooManyRows" ? new[] { CursorRow(1), CursorRow(2), Eof(134) }
                    : new[] { CursorRow(1), Eof(fault == "lostCursor" ? (byte)6 : fault == "moreResults" ? (byte)142 : (byte)134) };
                InstallFaultStream(connection, new ProtocolFaultStream(CursorFaultInput(rows, initialStatus: fault == "unconfirmed" ? (byte)6 : (byte)70,
                    column: fault == "metadata" ? Column("n", 9) : null)));
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                {
                    using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
                    while (await reader.ReadAsync()) { }
                });
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task CursorAsyncTransportAndTerminalCancellationKeepKnownCompletion(bool mysql, bool sequential)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var command = connection.CreateCommand(); command.CommandText = "cursor"; SetFetchSize(command, 1);
                var transport = new ProtocolFaultStream(CursorFaultInput(new[] { CursorRow(7), Eof(134) }, mysql)) { AfterInputConsumed = command.Cancel };
                InstallFaultStream(connection, transport);
                using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(reader.HasRows); Assert.True(await reader.ReadAsync()); Assert.Equal(7L, await reader.GetFieldValueAsync<long>(0));
                Assert.False(await reader.ReadAsync()); Assert.Equal(ConnectionState.Broken, connection.State); Assert.Equal(0, transport.SyncCalls);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("reader")]
        [InlineData("command")]
        [InlineData("connection")]
        [InlineData("closeConnection")]
        public async Task CursorEarlyCloseDestroysOriginalSessionAndInvalidatesFields(string origin)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "cursor"; SetFetchSize(command, 1);
                var column = Column("n", 253);
                // Match execute metadata to the fetch's text column while retaining a prepared result column.
                using var bytes = new MemoryStream(); AppendFrames(bytes, new[] { new byte[] { 0, 80, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, column, Eof() });
                AppendFrames(bytes, new[] { new byte[] { 1 }, column, Eof(70) }); AppendFrames(bytes, new[] { new byte[] { 1 }, column, Eof(), new byte[] { 0, 0, 3, 65, 66, 67 }, Eof(70) });
                InstallFaultStream(connection, new ProtocolFaultStream(bytes.ToArray()));
                using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | (origin == "closeConnection" ? CommandBehavior.CloseConnection : 0));
                Assert.True(await reader.ReadAsync()); using var chars = reader.GetTextReader(0); Assert.Equal('A', chars.Peek());
                if (origin == "reader" || origin == "closeConnection") reader.Close(); else if (origin == "command") command.Dispose(); else connection.Close();
                if (origin == "reader" || origin == "closeConnection") Assert.Throws<InvalidOperationException>(() => chars.Read());
                else Assert.True(Assert.Throws<OceanBaseException>(() => chars.Read()).IsExecutionOutcomeUnknown);
                Assert.Equal(origin == "connection" || origin == "closeConnection" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "token")]
        [InlineData(true, "token")]
        [InlineData(false, "cancel")]
        [InlineData(true, "cancel")]
        [InlineData(false, "timeout")]
        [InlineData(true, "timeout")]
        [InlineData(false, "close")]
        [InlineData(true, "close")]
        public async Task CursorBlockedNextPageUsesOneCommandBudget(bool sequential, string origin)
        {
            var fetching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 80, 0, 1); await StatementCommand(channel, 23);
                await CursorMetadata(channel, 70); await StatementCommand(channel, 28); await CursorMetadata(channel, 6);
                await channel.WritePacketAsync(CursorRow(7)); await channel.WritePacketAsync(Eof(70));
                await StatementCommand(channel, 28); fetching.TrySetResult(); Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "cursor"; SetFetchSize(command, 1); command.CommandTimeout = origin == "timeout" ? 1 : 10;
                using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(await reader.ReadAsync());
                var pending = reader.ReadAsync(cancellation.Token); await fetching.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(pending.IsCompleted);
                if (origin == "token") cancellation.Cancel(); else if (origin == "cancel") command.Cancel(); else if (origin == "close") connection.Close();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(error.IsExecutionOutcomeUnknown);
                if (origin == "timeout") Assert.IsType<TimeoutException>(error.InnerException);
                else if (origin != "close") Assert.IsType<OperationCanceledException>(error.InnerException);
                Assert.Equal(origin == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CursorNextResultDrainsPagesAndReleasesSession(bool sequential)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 80, 0, 1); await StatementCommand(channel, 23); await CursorMetadata(channel, 70);
                for (var i = 0; i < 2; i++)
                {
                    await StatementCommand(channel, 28); await CursorMetadata(channel, 6); await channel.WritePacketAsync(CursorRow(i));
                    await channel.WritePacketAsync(Eof(i == 1 ? (byte)134 : (byte)70));
                }
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "cursor"; SetFetchSize(command, 1);
                using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(reader.HasRows); Assert.False(await reader.NextResultAsync()); Assert.Equal(0, reader.FieldCount); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CursorInitiallyEmptyResultDoesNotFetch(bool sequential)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 80, 0, 1); await StatementCommand(channel, 23);
                await CursorMetadata(channel, 134); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "cursor"; SetFetchSize(command, 1);
                using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.False(reader.HasRows); Assert.Equal(1, reader.FieldCount); Assert.False(await reader.ReadAsync()); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static byte[] CursorFaultInput(byte[][] rows, bool mysql = false, byte initialStatus = 70, byte[]? column = null)
        {
            using var input = new MemoryStream();
            var prepare = new byte[] { 0, 80, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            AppendFrames(input, new[] { prepare, Column("n", 8), Eof() });
            AppendFrames(input, new[] { new byte[] { 1 }, Column("n", 8), Eof(initialStatus) });
            var fetch = new List<byte[]>();
            if (!mysql) { fetch.Add(new byte[] { 1 }); fetch.Add(column ?? Column("n", 8)); fetch.Add(Eof()); }
            fetch.AddRange(rows); AppendFrames(input, fetch.ToArray()); return input.ToArray();
        }

        private static void AppendFrames(Stream stream, byte[][] packets)
        {
            for (var i = 0; i < packets.Length; i++) stream.Write(ResponseFrame(packets[i], (byte)(i + 1)));
        }
    }
}
