using System;
using System.Data;
using System.Threading.Tasks;
using System.Threading;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Session;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public Task RefCursorOutputStreamsPagesAndKeepsParentUntilReleased(bool async, bool deprecated, bool sequential)
            => ReadRefCursorPagesAndRelease(async, deprecated, sequential);

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public Task RefCursorPositiveCommandFetchHintKeepsIndependentPageSize(bool async, bool deprecated, bool sequential)
            => ReadRefCursorPagesAndRelease(async, deprecated, sequential, commandFetchSize: 1);

        internal static async Task ReadRefCursorPagesAndRelease(bool async, bool deprecated, bool sequential, int slowThreshold = 0, int commandFetchSize = 0, CommandBehavior behavior = CommandBehavior.Default, bool advanceWithoutRead = false)
        {
            var server = Server(async channel =>
            {

                var execute = await StatementCommand(channel, 0xa1, deprecated);
                Assert.Equal(0, execute[5]);
                await channel.WritePacketAsync(OracleExecuteHeader(1, 1, 4));
                await channel.WritePacketAsync(Column("c", 163));
                if (!deprecated) await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(Column("c", 163));
                if (!deprecated) await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 91, 0, 0, 0 });
                await channel.WritePacketAsync(CursorTerminator(2, deprecated));
                await channel.WritePacketAsync(Ok());
                for (var page = 0; page < 2; page++)
                {
                    channel.BeginCommand();
                    Assert.Equal(new byte[] { 28, 91, 0, 0, 0, 2, 0, 0, 0 }, await StatementCommand(channel, 28, deprecated));
                    await CursorMetadata(channel, 2, deprecated, omitTerminator: deprecated);
                    await channel.WritePacketAsync(CursorRow(page * 2 + 1));
                    if (page == 0) await channel.WritePacketAsync(CursorRow(2));
                    await channel.WritePacketAsync(CursorTerminator(page == 0 ? (byte)66 : (byte)130, deprecated));
                }
                for (var i = 0; i < 2; i++)
                {
                    channel.BeginCommand();
                    var close = await StatementCommand(channel, 25, deprecated);
                    Assert.Equal(new byte[] { 25, i == 0 ? (byte)91 : (byte)42, 0, 0, 0 }, close);
                }
                channel.BeginCommand(); await Query(channel, "after", deprecated); await channel.WritePacketAsync(Ok(1));
            }, deprecated, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true;Slow Query Threshold=" + slowThreshold);
            try
            {
                await connection.OpenAsync();
                var command = (OceanBaseCommand)connection.CreateCommand(); command.CommandType = CommandType.StoredProcedure; command.CommandText = "pkg.open_cursor";
                command.FetchSize = commandFetchSize;
                var parameter = new OceanBaseParameter { ParameterName = "c", Direction = ParameterDirection.Output, OceanBaseDbType = OceanBaseDbType.RefCursor };
                command.Parameters.Add(parameter);
                Assert.Equal(0, async ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                using var cursor = Assert.IsType<OceanBaseRefCursor>(parameter.Value);
                command.Dispose(); // The output cursor owns the parent statement after command disposal.
                Assert.Throws<ArgumentOutOfRangeException>(() => { cursor.GetDataReader(0); });
                Assert.Throws<NotSupportedException>(() => { cursor.GetDataReader(2, CommandBehavior.SchemaOnly); });
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cursor.GetDataReaderAsync(cancellationToken: canceled.Token));
                using var reader = async ? await cursor.GetDataReaderAsync(2, behavior | (sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default))
                    : cursor.GetDataReader(2, behavior | (sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default));
                if (slowThreshold > 0) await Task.Delay(30);
                Assert.Equal("n", reader.GetName(0)); Assert.Equal(typeof(long), reader.GetFieldType(0));
                if (!advanceWithoutRead)
                {
                    var rows = (behavior & CommandBehavior.SingleRow) != 0 ? 1 : 3;
                    for (var i = 1L; i <= rows; i++) { Assert.True(async ? await reader.ReadAsync() : reader.Read()); Assert.Equal(i, reader.GetInt64(0)); }
                    Assert.False(async ? await reader.ReadAsync() : reader.Read());
                }
                Assert.False(async ? await reader.NextResultAsync() : reader.NextResult());
                Assert.False(await reader.ReadAsync()); Assert.False(reader.NextResult());
                Assert.Throws<InvalidOperationException>(() => cursor.GetDataReader());
                cursor.Dispose();
                using var after = connection.CreateCommand(); after.CommandText = "after";
                Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        private static OceanBaseCommand RefCursorCall(OceanBaseConnection connection, int count = 1)
        {
            var command = connection.CreateCommand(); command.CommandType = CommandType.StoredProcedure; command.CommandText = "pkg.open_cursor";
            for (var i = 0; i < count; i++) command.Parameters.Add(new OceanBaseParameter { ParameterName = "c" + i, Direction = ParameterDirection.Output, OceanBaseDbType = OceanBaseDbType.RefCursor, Value = "old" });
            return (OceanBaseCommand)command;
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task RefCursorFinalPacketCannotCompleteAfterCancellationOrSessionClose(bool async, bool close)
        {
            var server = Server(async channel =>
            {
                Assert.Equal(new byte[] { 28, 91, 0, 0, 0, 1, 0, 0, 0 }, await StatementCommand(channel, 28));
                await channel.WritePacketAsync(CursorTerminator(130, false));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var released = 0;
                using var exchange = new CommandExchange(connection.GetOpenSession(), new ConnectionLog(null), 5, CancellationToken.None, () => released++);
                exchange.OwnCursor(refCursor: true);
                if (async) { await exchange.SendAsync(new byte[] { 28, 91, 0, 0, 0, 1, 0, 0, 0 }, executesSql: false); await exchange.ReceiveAsync(CancellationToken.None); }
                else { exchange.Send(new byte[] { 28, 91, 0, 0, 0, 1, 0, 0, 0 }, executesSql: false); exchange.Receive(); }
                if (close) connection.Close(); else exchange.Cancel();
                Assert.ThrowsAny<Exception>(() => { try { exchange.Complete(); } catch (Exception error) { throw exchange.Fail(error); } });
                Assert.Equal(1, released);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static async Task RefCursorOutput(PacketChannel channel, int count = 1, byte[]? row = null, bool empty = false, bool error = false)
        {
            await channel.WritePacketAsync(OracleExecuteHeader((ushort)count, (ushort)count, 4));
            for (var i = 0; i < count; i++) await channel.WritePacketAsync(Column("c" + i, 163));
            await channel.WritePacketAsync(Eof());
            for (var i = 0; i < count; i++) await channel.WritePacketAsync(Column("c" + i, 163));
            await channel.WritePacketAsync(Eof());
            if (!empty) await channel.WritePacketAsync(row ?? new byte[] { 0, 0, 91, 0, 0, 0 });
            await channel.WritePacketAsync(Eof());
            await channel.WritePacketAsync(error ? Error() : Ok());
        }

        [Theory]
        [InlineData(ParameterDirection.Input)]
        [InlineData(ParameterDirection.InputOutput)]
        public async Task RefCursorInputDirectionsAreRejectedBeforeVersionOrUserSql(ParameterDirection direction)
        {
            var server = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = RefCursorCall(connection); command.Parameters[0].Direction = direction;
                Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task RefCursorOldHandleAndCommandCannotUseOrCloseReopenedConnection(bool asynchronous)
            => RefCursorOldHandleAndCommandCannotUseOrCloseReopenedConnectionCore(asynchronous, prepared: false);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task PreparedRefCursorOldHandleAndCommandCannotUseOrCloseReopenedConnection(bool asynchronous)
            => RefCursorOldHandleAndCommandCannotUseOrCloseReopenedConnectionCore(asynchronous, prepared: true);

        private static async Task RefCursorOldHandleAndCommandCannotUseOrCloseReopenedConnectionCore(bool asynchronous, bool prepared)
        {
            var oldServer = Server(async channel =>
            {
                if (prepared) { await StatementCommand(channel, 22); await Prepared(channel, 42, 1); }
                await StatementCommand(channel, 0xa1); await RefCursorOutput(channel);
                await AssertOracleExecuteTransportClosed(channel);
            }, serverVersion: "4.2.0");
            var newServer = Server(async channel => { await Query(channel, "new"); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(oldServer.Port) + ";Use Oracle Prepare Execute=true;Pooling=true");
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                using var command = RefCursorCall(connection);
                if (prepared) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
                using var cursor = Assert.IsType<OceanBaseRefCursor>(command.Parameters[0].Value);
                connection.Close(); await oldServer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                connection.ConnectionString = Configuration(newServer.Port);
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                Assert.Throws<InvalidOperationException>(() => { cursor.GetDataReader(); }); cursor.Dispose(); command.Dispose();
                Assert.Equal(ConnectionState.Open, connection.State); using var after = connection.CreateCommand(); after.CommandText = "new";
                Assert.Equal(1, asynchronous ? await after.ExecuteNonQueryAsync() : after.ExecuteNonQuery());
            }
            finally { connection.Close(); oldServer.Stop(); newServer.Stop(); }
            await newServer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RefCursorTruncatedOrTrailingOutputIdCannotPublish(bool trailing)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1);
                // Send only through the bad row: the client must reject before any execution tail.
                await channel.WritePacketAsync(OracleExecuteHeader(1, 1, 4));
                await channel.WritePacketAsync(Column("c", 163)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(Column("c", 163)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(trailing ? new byte[] { 0, 0, 91, 0, 0, 0, 0 } : new byte[] { 0, 0, 91, 0, 0 });
                await AssertOracleExecuteTransportClosed(channel);
            }, serverVersion: "4.2.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = RefCursorCall(connection);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteNonQueryAsync());
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal("old", command.Parameters[0].Value); Assert.Equal(ConnectionState.Broken, connection.State);
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { connection.Close(); server.Stop(); }
        }

        [Fact]
        public async Task RefCursorSiblingCanRetryAfterBusyReaderCompletesWithinTransaction()
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel); await StatementCommand(channel, 0xa1);
                await RefCursorOutput(channel, 2, new byte[] { 0, 0, 91, 0, 0, 0, 92, 0, 0, 0 });
                for (var i = 0; i < 2; i++)
                {
                    Assert.Equal(new byte[] { 28, (byte)(91 + i), 0, 0, 0, 128, 0, 0, 0 }, await StatementCommand(channel, 28));
                    await CursorMetadata(channel, 2); await channel.WritePacketAsync(CursorRow(i)); await channel.WritePacketAsync(CursorTerminator(131, false));
                    Assert.Equal(new byte[] { 25, (byte)(91 + i), 0, 0, 0 }, await StatementCommand(channel, 25));
                }
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await TransactionEnd(channel, false);
            }, serverVersion: "4.2.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
                using var command = RefCursorCall(connection, 2); command.Transaction = transaction; await command.ExecuteNonQueryAsync();
                using var first = Assert.IsType<OceanBaseRefCursor>(command.Parameters[0].Value); using var second = Assert.IsType<OceanBaseRefCursor>(command.Parameters[1].Value);
                using var one = await first.GetDataReaderAsync();
                Assert.Throws<InvalidOperationException>(() => { second.GetDataReader(); }); Assert.Throws<InvalidOperationException>(() => transaction.Commit());
                Assert.True(await one.ReadAsync()); Assert.False(await one.ReadAsync()); first.Dispose();
                using var two = await second.GetDataReaderAsync(); Assert.True(await two.ReadAsync()); Assert.False(await two.ReadAsync()); second.Dispose();
                await transaction.RollbackAsync();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RefCursorNullOrEmptyOutputClosesOnlyParent(bool empty)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1);
                await RefCursorOutput(channel, row: new byte[] { 0, 4 }, empty: empty);
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
            }, serverVersion: "4.2.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = RefCursorCall(connection);
                await command.ExecuteNonQueryAsync(); Assert.Equal(DBNull.Value, command.Parameters[0].Value);
                command.Dispose(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { connection.Close(); server.Stop(); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RefCursorSiblingsCloseIndependentlyAndLastReleaseClosesParent(bool alias)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1);
                await RefCursorOutput(channel, 2, new byte[] { 0, 0, 91, 0, 0, 0, alias ? (byte)91 : (byte)92, 0, 0, 0 });
                if (!alias) Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await StatementCommand(channel, 25));
                Assert.Equal(new byte[] { 28, alias ? (byte)91 : (byte)92, 0, 0, 0, 128, 0, 0, 0 }, await StatementCommand(channel, 28));
                await CursorMetadata(channel, 2); await channel.WritePacketAsync(CursorTerminator(130, false));
                Assert.Equal(new byte[] { 25, alias ? (byte)91 : (byte)92, 0, 0, 0 }, await StatementCommand(channel, 25));
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
            }, serverVersion: "4.2.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = RefCursorCall(connection, 2); await command.ExecuteNonQueryAsync();
                using var first = Assert.IsType<OceanBaseRefCursor>(command.Parameters[0].Value);
                using var second = Assert.IsType<OceanBaseRefCursor>(command.Parameters[1].Value);
                if (alias) Assert.Same(first, second); else first.Dispose();
                command.Dispose();
                using var reader = await second.GetDataReaderAsync(); Assert.False(reader.HasRows); Assert.False(await reader.ReadAsync());
                await second.DisposeAsync(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { connection.Close(); server.Stop(); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RefCursorTailErrorPreservesOldOutputAndClosesStagedIds(bool async)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1); await RefCursorOutput(channel, error: true);
                Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await StatementCommand(channel, 25));
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
            }, serverVersion: "4.2.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = RefCursorCall(connection);
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (async) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal("old", command.Parameters[0].Value); Assert.Equal(ConnectionState.Open, connection.State);
                command.Dispose(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { connection.Close(); server.Stop(); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RefCursorTransactionContextCannotBeReusedAfterCompletion(bool transactional)
        {
            var server = Server(async channel =>
            {
                if (transactional) await TransactionBegin(channel);
                await StatementCommand(channel, 0xa1); await RefCursorOutput(channel);
                channel.BeginCommand();
                if (!transactional) await TransactionBegin(channel);
                await TransactionEnd(channel, false);
                Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await StatementCommand(channel, 25));
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
            }, serverVersion: "4.2.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); OceanBaseTransaction? transaction = transactional ? (OceanBaseTransaction)await connection.BeginTransactionAsync() : null;
                using var command = RefCursorCall(connection); command.Transaction = transaction; await command.ExecuteNonQueryAsync();
                using var cursor = Assert.IsType<OceanBaseRefCursor>(command.Parameters[0].Value);
                transaction ??= (OceanBaseTransaction)await connection.BeginTransactionAsync(); await transaction.RollbackAsync();
                Assert.Throws<InvalidOperationException>(() => { cursor.GetDataReader(); });
                cursor.Dispose(); transaction.Dispose(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { connection.Close(); server.Stop(); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task RefCursorEarlyReaderOrCursorCloseDestroysUnreadSession(bool disposeCursor)
            => RefCursorEarlyReaderOrCursorCloseDestroysUnreadSessionCore(disposeCursor);

        private static async Task RefCursorEarlyReaderOrCursorCloseDestroysUnreadSessionCore(bool disposeCursor, CommandBehavior behavior = CommandBehavior.Default)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1); await RefCursorOutput(channel);
                await StatementCommand(channel, 28); await CursorMetadata(channel, 2); await channel.WritePacketAsync(CursorRow(1));
                ready.SetResult(); await AssertOracleExecuteTransportClosed(channel);
            }, serverVersion: "4.2.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = RefCursorCall(connection); await command.ExecuteNonQueryAsync();
                using var cursor = Assert.IsType<OceanBaseRefCursor>(command.Parameters[0].Value); using var reader = await cursor.GetDataReaderAsync(behavior: behavior);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.True(await reader.ReadAsync());
                if (disposeCursor) cursor.Dispose(); else reader.Close();
                Assert.Equal(ConnectionState.Broken, connection.State); await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { connection.Close(); server.Stop(); }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public Task RefCursorFetchCancellationOrTimeoutDoesNotReclassifyCompletedSql(bool timeout, bool nextPage)
            => RefCursorFetchCancellationOrTimeoutDoesNotReclassifyCompletedSqlCore(timeout, nextPage);

        private static async Task RefCursorFetchCancellationOrTimeoutDoesNotReclassifyCompletedSqlCore(bool timeout, bool nextPage, CommandBehavior behavior = CommandBehavior.Default)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1); await RefCursorOutput(channel);
                await StatementCommand(channel, 28);
                if (nextPage)
                {
                    await CursorMetadata(channel, 2); await channel.WritePacketAsync(CursorRow(1)); await channel.WritePacketAsync(CursorTerminator(66, false));
                    await StatementCommand(channel, 28);
                }
                ready.SetResult(); await AssertOracleExecuteTransportClosed(channel);
            }, serverVersion: "4.2.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var command = RefCursorCall(connection); command.CommandTimeout = timeout ? 1 : 10; await command.ExecuteNonQueryAsync();
                using var cursor = Assert.IsType<OceanBaseRefCursor>(command.Parameters[0].Value);
                async Task Fetch()
                {
                    using var reader = await cursor.GetDataReaderAsync(1, behavior, cancellation.Token);
                    Assert.True(await reader.ReadAsync()); await reader.ReadAsync(cancellation.Token);
                }
                var fetching = Fetch(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); if (!timeout) cancellation.Cancel();
                var error = await Record.ExceptionAsync(() => fetching.WaitAsync(TimeSpan.FromSeconds(10)));
                if (timeout) Assert.IsType<TimeoutException>(error);
                else Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
                Assert.Equal(ConnectionState.Broken, connection.State); await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { connection.Close(); server.Stop(); }
        }

        [Theory]
        [InlineData("error")]
        [InlineData("no-progress")]
        [InlineData("extra-row")]
        [InlineData("changed-metadata")]
        public Task RefCursorBadFetchBreaksSessionWithoutClaimingSqlOutcomeUnknown(string kind)
            => RefCursorBadFetchBreaksSessionWithoutClaimingSqlOutcomeUnknownCore(kind);

        private static async Task RefCursorBadFetchBreaksSessionWithoutClaimingSqlOutcomeUnknownCore(string kind, CommandBehavior behavior = CommandBehavior.Default)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1); await RefCursorOutput(channel);
                await StatementCommand(channel, 28);
                if (kind == "error")
                {
                    if (behavior != CommandBehavior.Default)
                    {
                        await CursorMetadata(channel, 2); await channel.WritePacketAsync(CursorRow(1)); await channel.WritePacketAsync(CursorTerminator(66, false));
                        await StatementCommand(channel, 28);
                    }
                    await channel.WritePacketAsync(Error());
                }
                else
                {
                    await CursorMetadata(channel, 2);
                    if (kind != "no-progress") await channel.WritePacketAsync(CursorRow(1));
                    if (kind == "extra-row") await channel.WritePacketAsync(CursorRow(2));
                    else await channel.WritePacketAsync(CursorTerminator(66, false));
                    if (kind == "changed-metadata")
                    {
                        await StatementCommand(channel, 28); await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("changed", 8));
                    }
                }
                await AssertOracleExecuteTransportClosed(channel);
            }, serverVersion: "4.2.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = RefCursorCall(connection); await command.ExecuteNonQueryAsync();
                using var cursor = Assert.IsType<OceanBaseRefCursor>(command.Parameters[0].Value);
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                {
                    using var reader = await cursor.GetDataReaderAsync(1, behavior); while (await reader.ReadAsync()) { }
                });
                Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { connection.Close(); server.Stop(); }
        }
    }
}
