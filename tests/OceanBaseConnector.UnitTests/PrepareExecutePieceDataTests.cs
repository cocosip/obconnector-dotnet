using System;
using System.Data;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, 0, false)]
        [InlineData(true, 0, false)]
        [InlineData(false, 3, true)]
        [InlineData(true, 3, true)]
        [InlineData(false, 20000, false)]
        [InlineData(true, 20000, false)]
        public async Task PrepareExecutePieceDataUsesPreparedIdAndPublishesOutput(bool asynchronous, int size, bool changedId)
        {
            var source = new LongDataSource(size, asynchronous);
            var server = Server(async channel =>
            {

                for (var run = 0; run < 2; run++)
                {
                    var id = (byte)(60 + run);
                    var prepare = await StatementCommand(channel, 22);
                    Assert.Equal("BEGIN pkg.proc(?, ?); END;", Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                    await Prepared(channel, id, 2);
                    var received = 0; var length = run == 0 ? size : 0;
                    do
                    {
                        var count = Math.Min(8192, length - received);
                        var piece = received + count == length ? (byte)3 : received == 0 ? (byte)1 : (byte)2;
                        AssertPiece(await StatementCommand(channel, 162), id, 0, piece, received, count);
                        received += count; await channel.WritePacketAsync(Ok(17));
                    } while (received < length);
                    var request = await StatementCommand(channel, 0xa1);
                    Assert.Equal(id, request[1]); Assert.Equal(new byte[] { 0, 0, 0 }, request[2..5]);
                    var sqlLength = request[10]; Assert.Equal(26, sqlLength);
                    Assert.Equal("BEGIN pkg.proc(?, ?); END;", Encoding.UTF8.GetString(request, 11, sqlLength));
                    Assert.Equal(new byte[] { 2, 0, 0, 0, 2, 1, 254, 0, 6, 0 }, request[(11 + sqlLength)..(21 + sqlLength)]);
                    var header = OracleExecuteHeader(2, 1, 4); header[1] = changedId ? (byte)42 : id;
                    await channel.WritePacketAsync(header);
                    await channel.WritePacketAsync(Column("in", 254)); await channel.WritePacketAsync(Column("out", 8));
                    await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Column("output", 8));
                    await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(new byte[] { 0, 0, 42, 0, 0, 0, 0, 0, 0, 0 });
                    await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Ok(2));
                    Assert.Equal(new byte[] { 25, id, 0, 0, 0 }, await StatementCommand(channel, 25));
                    if (changedId) Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
                }
            }, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var command = connection.CreateCommand();
                command.CommandType = CommandType.StoredProcedure; command.CommandText = "pkg.proc";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "input", Value = source });
                var output = new OceanBaseParameter { ParameterName = "output", Direction = ParameterDirection.Output, Value = "old" };
                command.Parameters.Add(output);
                for (var run = 0; run < 2; run++)
                {
                    output.Value = source;
                    Assert.Equal(2, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                    Assert.Equal(42L, output.Value); Assert.Equal(size, source.BytesRead); Assert.False(source.WasDisposed);
                }
                if (asynchronous) await command.DisposeAsync(); else command.Dispose();
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PrepareExecutePieceDataRejectsTextStreamDeclarationBeforeReading(bool asynchronous)
        {
            var server = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            var source = new LongDataSource(3, asynchronous);
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.InputOutput, DbType = DbType.String, Value = source });
                if (asynchronous) await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteNonQueryAsync());
                else Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
                Assert.Equal(0, source.BytesRead); Assert.False(source.WasDisposed);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "count")]
        [InlineData(true, "count")]
        [InlineData(false, "source-before")]
        [InlineData(true, "source-before")]
        [InlineData(false, "source-after")]
        [InlineData(true, "source-after")]
        [InlineData(false, "piece-error")]
        [InlineData(true, "piece-error")]
        [InlineData(false, "execute-error")]
        [InlineData(true, "execute-error")]
        [InlineData(false, "tail-error")]
        [InlineData(true, "tail-error")]
        [InlineData(false, "tail-disconnect")]
        [InlineData(true, "tail-disconnect")]
        public async Task PrepareExecutePieceDataFailuresPreserveOutputsAndReleaseTemporaryIds(bool asynchronous, string fault)
        {
            var usable = fault == "count" || fault == "source-before" || fault == "execute-error" || fault == "tail-error";
            var source = new LongDataSource(fault == "source-after" ? 20000 : 3, asynchronous);
            source.BeforeRead = read => { if (fault == "source-before" || fault == "source-after" && read >= 8193) throw new IOException("Source failed."); };
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await Prepared(channel, 60, fault == "count" ? (ushort)1 : (ushort)2);
                if (fault != "count" && fault != "source-before")
                {
                    await StatementCommand(channel, 162); await channel.WritePacketAsync(fault == "piece-error" ? Error() : Ok());
                    if (fault.StartsWith("execute") || fault.StartsWith("tail"))
                    {
                        Assert.Equal(60, (await StatementCommand(channel, 0xa1))[1]);
                        if (fault == "execute-error") await channel.WritePacketAsync(Error());
                        else
                        {
                            await OracleOutputReply(channel, false);
                            if (fault == "tail-disconnect") return;
                            await channel.WritePacketAsync(Error());
                        }
                    }
                }
                if (usable)
                {
                    Assert.Equal(new byte[] { 25, 60, 0, 0, 0 }, await StatementCommand(channel, 25));
                    if (fault == "tail-error") Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
                    channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
                }
                else Assert.Equal(0, await channelStreamRead(channel));
            }, serverVersion: "4.2.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var command = connection.CreateCommand(); command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = source });
                var output = new OceanBaseParameter { ParameterName = "o", Direction = ParameterDirection.Output, Value = "old" }; command.Parameters.Add(output);
                var error = await Record.ExceptionAsync(async () => { if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                if (fault == "count") { Assert.IsType<ArgumentException>(error); Assert.Equal(0, source.BytesRead); }
                else if (fault.StartsWith("source")) Assert.IsType<IOException>(error);
                else
                {
                    var database = Assert.IsType<OceanBaseException>(error);
                    Assert.Equal(fault == "tail-disconnect", database.IsExecutionOutcomeUnknown);
                    Assert.Equal(fault == "tail-disconnect" ? 0 : 1064, database.ServerErrorCode);
                }
                Assert.Equal("old", output.Value); Assert.False(source.WasDisposed); Assert.Equal(usable ? ConnectionState.Open : ConnectionState.Broken, connection.State);
                if (asynchronous) await command.DisposeAsync(); else command.Dispose();
                if (usable) { using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync()); }
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PrepareExecutePieceDataKeepsFinalParentAliveUntilRefCursorRelease(bool changedId)
        {
            var parent = changedId ? (byte)42 : (byte)60;
            var server = Server(async channel =>
            {

                for (var run = 0; run < 2; run++)
                {
                    var id = (byte)(60 + run);
                    await StatementCommand(channel, 22); await Prepared(channel, id, 2);
                    AssertPiece(await StatementCommand(channel, 162), id, 0, 3, 0, run == 0 ? 3 : 0); await channel.WritePacketAsync(Ok());
                    Assert.Equal(id, (await StatementCommand(channel, 0xa1))[1]);
                    var header = OracleExecuteHeader(2, 1, 4); header[1] = (byte)(parent + run); await channel.WritePacketAsync(header);
                    await channel.WritePacketAsync(Column("in", 254)); await channel.WritePacketAsync(Column("cursor", 163)); await channel.WritePacketAsync(Eof());
                    await channel.WritePacketAsync(Column("cursor", 163)); await channel.WritePacketAsync(Eof());
                    await channel.WritePacketAsync(new byte[] { 0, 0, (byte)(91 + run), 0, 0, 0 }); await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Ok());
                    if (changedId) Assert.Equal(new byte[] { 25, id, 0, 0, 0 }, await StatementCommand(channel, 25));
                }
                for (var run = 0; run < 2; run++)
                {
                    var cursor = (byte)(91 + run);
                    Assert.Equal(new byte[] { 28, cursor, 0, 0, 0, 1, 0, 0, 0 }, await StatementCommand(channel, 28));
                    await CursorMetadata(channel, 2, false); await channel.WritePacketAsync(CursorRow(7 + run)); await channel.WritePacketAsync(CursorTerminator(130, false));
                    Assert.Equal(new byte[] { 25, cursor, 0, 0, 0 }, await StatementCommand(channel, 25));
                    Assert.Equal(new byte[] { 25, (byte)(parent + run), 0, 0, 0 }, await StatementCommand(channel, 25));
                }
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, serverVersion: "4.2.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            var source = new LongDataSource(3, true);
            try
            {
                await connection.OpenAsync(); var command = connection.CreateCommand(); command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = source });
                var output = new OceanBaseParameter { ParameterName = "c", Direction = ParameterDirection.Output, OceanBaseDbType = OceanBaseDbType.RefCursor }; command.Parameters.Add(output);
                Assert.Equal(0, await command.ExecuteNonQueryAsync()); using var first = Assert.IsType<OceanBaseRefCursor>(output.Value);
                Assert.Equal(0, await command.ExecuteNonQueryAsync()); using var second = Assert.IsType<OceanBaseRefCursor>(output.Value);
                Assert.NotSame(first, second);
                await command.DisposeAsync();
                var value = 7L;
                foreach (var cursor in new[] { first, second })
                {
                    using (var reader = await cursor.GetDataReaderAsync(1))
                    { Assert.True(await reader.ReadAsync()); Assert.Equal(value++, reader.GetInt64(0)); Assert.False(await reader.ReadAsync()); }
                    cursor.Dispose();
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PrepareExecutePieceDataRetainsTransactionOwnership(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel);
                await StatementCommand(channel, 22); await Prepared(channel, 60, 2);
                AssertPiece(await StatementCommand(channel, 162), 60, 0, 3, 0, 3); await channel.WritePacketAsync(Ok(0, 5));
                Assert.Equal(60, (await StatementCommand(channel, 0xa1))[1]); await OracleOutputReply(channel, false); await channel.WritePacketAsync(Ok(1, 5));
                Assert.Equal(new byte[] { 25, 60, 0, 0, 0 }, await StatementCommand(channel, 25));
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await TransactionEnd(channel, false);
            }, serverVersion: "4.2.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            var source = new LongDataSource(3, asynchronous);
            try
            {
                await connection.OpenAsync(); using var transaction = asynchronous ? await connection.BeginTransactionAsync() : connection.BeginTransaction();
                var command = connection.CreateCommand(); command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = source });
                var output = new OceanBaseParameter { ParameterName = "o", Direction = ParameterDirection.Output, Value = "old" }; command.Parameters.Add(output);
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery()); Assert.Equal(0, source.BytesRead); command.Transaction = transaction;
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery()); Assert.Equal(42L, output.Value); Assert.Same(connection, transaction.Connection);
                if (asynchronous) { await command.DisposeAsync(); await transaction.RollbackAsync(); } else { command.Dispose(); transaction.Rollback(); }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PrepareExecutePieceDataCancellationBeforeExecutePreservesOutput(bool timeout)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var caller = new CancellationTokenSource();
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 60, 2);
                await StatementCommand(channel, 162); received.SetResult(); Assert.Equal(0, await channelStreamRead(channel));
            }, serverVersion: "4.2.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            using var source = new MemoryStream(new byte[20000]);
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
                command.CommandTimeout = timeout ? 1 : 5; command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = source });
                var output = new OceanBaseParameter { ParameterName = "o", Direction = ParameterDirection.Output, Value = "old" }; command.Parameters.Add(output);
                var pending = command.ExecuteNonQueryAsync(caller.Token); await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(pending.IsCompleted); Assert.Equal(8193, source.Position);
                using var busy = connection.CreateCommand(); busy.CommandText = "busy"; Assert.Throws<InvalidOperationException>(() => busy.ExecuteNonQuery());
                if (!timeout) caller.Cancel();
                if (timeout) await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                else Assert.Equal(caller.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)))).CancellationToken);
                Assert.Equal("old", output.Value); Assert.Equal(ConnectionState.Broken, connection.State); Assert.True(source.CanRead);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task PrepareExecutePieceDataBudgetIncludesSqlAndExtendedFields()
        {
            // 19-byte bound SQL + padding, 3-byte length prefix, 30 fixed bytes and 6 parameter bytes.
            const int limit = (64 * 1024 * 1024 - 9058) / 2;
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 60, 2);
                var received = 0;
                while (received < limit / 8192 * 8192)
                { await StatementCommand(channel, 162); received += 8192; await channel.WritePacketAsync(Ok()); }
                Assert.Equal(0, await channelStreamRead(channel));
            }, serverVersion: "4.2.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            var source = new LongDataSource(limit + 1, true) { ChunkSize = 8192 };
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "BEGIN p(:p, :o); END;" + new string(' ', 9000); command.CommandTimeout = 30;
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = source });
                var output = new OceanBaseParameter { ParameterName = "o", Direction = ParameterDirection.Output, Value = "old" }; command.Parameters.Add(output);
                await Assert.ThrowsAsync<ArgumentException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal("old", output.Value); Assert.Equal(limit + 1, source.BytesRead); Assert.Equal(ConnectionState.Broken, connection.State); Assert.False(source.WasDisposed);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
