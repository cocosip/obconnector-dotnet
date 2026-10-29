using System;
using System.Data;
using System.IO;
using System.Linq;
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
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 3)]
        [InlineData(true, 3)]
        [InlineData(false, 8192)]
        [InlineData(true, 8192)]
        [InlineData(false, 8193)]
        [InlineData(true, 8193)]
        [InlineData(false, 16384)]
        [InlineData(true, 16384)]
        [InlineData(false, 17)]
        [InlineData(true, 17)]
        [InlineData(false, 20000)]
        [InlineData(true, 20000)]
        public async Task PieceDataStreamsUseHexPiecesAndWaitForEveryAcknowledgement(bool asynchronous, int size)
        {
            var source = new LongDataSource(size, asynchronous) { ChunkSize = size == 17 ? 1 : 3011 };
            var server = Server(async channel =>
            {
                var prepare = await StatementCommand(channel, 22);
                Assert.Equal("insert into t values (?, ?, ?)", Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                await Prepared(channel, 54, 3);
                var received = 0;
                do
                {
                    var count = Math.Min(8192, size - received);
                    var piece = received + count == size ? (byte)3 : received == 0 ? (byte)1 : (byte)2;
                    AssertPiece(await StatementCommand(channel, 162), 54, 1, piece, received, count);
                    received += count;
                    await channel.WritePacketAsync(Ok(0));
                } while (received < size);
                Assert.Equal(new byte[] { 23, 54, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 3, 0, 254, 0, 253, 0, 42, 0, 0, 0, 1, 120 }, await StatementCommand(channel, 23));
                await channel.WritePacketAsync(Ok(1)); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand(); command.CommandText = "insert into t values (:n, :p, :s)";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "n", Value = 42 });
                var upload = new OceanBaseParameter { ParameterName = "p", Value = source }; command.Parameters.Add(upload);
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "s", Value = "x" });
                source.BeforeRead = _ => { upload.Value = new byte[] { 99 }; upload.DbType = DbType.String; };
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Equal(size, source.BytesRead); Assert.False(source.WasDisposed); Assert.Equal(ConnectionState.Open, connection.State);
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PieceDataPrepareAndReusePreserveCurrentPositionOwnershipAndNull(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 55, 2);
                for (var run = 0; run < 2; run++)
                {
                    AssertPiece(await StatementCommand(channel, 162), 55, 0, 3, 1, run == 0 ? 2 : 0); await channel.WritePacketAsync(Ok(0));
                    AssertPiece(await StatementCommand(channel, 162), 55, 1, 3, 0, run == 0 ? 1 : 0); await channel.WritePacketAsync(Ok(0));
                    Assert.Equal(new byte[] { 23, 55, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 254, 0, 254, 0 }, await StatementCommand(channel, 23));
                    await channel.WritePacketAsync(Ok(1));
                }
                Assert.Equal(new byte[] { 23, 55, 0, 0, 0, 0, 1, 0, 0, 0, 3, 1, 6, 0, 6, 0 }, await StatementCommand(channel, 23));
                await channel.WritePacketAsync(Ok(1)); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var source = new MemoryStream(new byte[] { 0, 1, 2 }); source.Position = 1;
            using var second = new MemoryStream(new byte[] { 0 });
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand(); command.CommandText = "insert into t values (:a, :b)";
                var a = new OceanBaseParameter { ParameterName = "a", Value = source, DbType = DbType.Binary }; command.Parameters.Add(a);
                var b = new OceanBaseParameter { ParameterName = "b", Value = second }; command.Parameters.Add(b);
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                Assert.Equal(1, source.Position); Assert.Equal(0, second.Position);
                for (var run = 0; run < 2; run++) Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                a.Value = b.Value = DBNull.Value;
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.True(source.CanRead); Assert.True(second.CanRead); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "source-before")]
        [InlineData(true, "source-before")]
        [InlineData(false, "source-after")]
        [InlineData(true, "source-after")]
        [InlineData(false, "server-error")]
        [InlineData(true, "server-error")]
        [InlineData(false, "bad-ok")]
        [InlineData(true, "bad-ok")]
        [InlineData(false, "more")]
        [InlineData(true, "more")]
        [InlineData(false, "cursor")]
        [InlineData(true, "cursor")]
        [InlineData(false, "disconnect")]
        [InlineData(true, "disconnect")]
        [InlineData(false, "execute-error")]
        [InlineData(true, "execute-error")]
        [InlineData(false, "execute-disconnect")]
        [InlineData(true, "execute-disconnect")]
        public async Task PieceDataFailuresPreserveErrorIdentityAndDiscardOnlyDirtySessions(bool asynchronous, string fault)
        {
            var usable = fault == "source-before" || fault == "execute-error";
            var source = new LongDataSource(fault.StartsWith("execute") ? 3 : 20000, asynchronous);
            source.BeforeRead = read => { if (fault == "source-before" || fault == "source-after" && read >= 8193) throw new IOException("Source failed."); };
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 56, 1);
                if (fault != "source-before")
                {
                    await StatementCommand(channel, 162);
                    if (fault == "disconnect") return;
                    if (fault == "server-error") await channel.WritePacketAsync(Error());
                    else if (fault == "bad-ok") await channel.WritePacketAsync(new byte[] { 0 });
                    else if (fault == "more" || fault == "cursor") await channel.WritePacketAsync(new byte[] { 0, 0, 0, fault == "more" ? (byte)14 : (byte)70, 0, 0, 0 });
                    else await channel.WritePacketAsync(Ok(0));
                    if (fault.StartsWith("execute"))
                    {
                        await StatementCommand(channel, 23);
                        if (fault == "execute-disconnect") return;
                        await channel.WritePacketAsync(Error());
                    }
                }
                if (usable)
                {
                    await StatementCommand(channel, 25); await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1));
                }
                else Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "insert into t values (:p)"; command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = source });
                    var error = await Record.ExceptionAsync(async () => { if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                    if (fault.StartsWith("source")) Assert.IsType<IOException>(error);
                    else
                    {
                        var database = Assert.IsType<OceanBaseException>(error);
                        Assert.Equal(fault == "execute-disconnect", database.IsExecutionOutcomeUnknown);
                        Assert.Equal(fault == "server-error" || fault == "execute-error" ? 1064 : 0, database.ServerErrorCode);
                    }
                    Assert.False(source.WasDisposed); Assert.Equal(usable ? ConnectionState.Open : ConnectionState.Broken, connection.State);
                }
                if (usable) { using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync()); }
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PieceDataPendingAcknowledgementKeepsLeaseAndHonorsCancellationOrTimeout(bool timeout)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var caller = new CancellationTokenSource();
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 57, 1);
                await StatementCommand(channel, 162); received.SetResult();
                // No second request is allowed while the first piece has no acknowledgement.
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var source = new MemoryStream(new byte[20000]);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand(); command.CommandText = "insert into t values (:p)"; command.CommandTimeout = timeout ? 1 : 5;
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = source });
                var pending = command.ExecuteNonQueryAsync(caller.Token);
                await received.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(pending.IsCompleted); Assert.Equal(8193, source.Position);
                using var busy = connection.CreateCommand(); busy.CommandText = "after"; Assert.Throws<InvalidOperationException>(() => busy.ExecuteNonQuery());
                if (!timeout) caller.Cancel();
                if (timeout) await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                else Assert.Equal(caller.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)))).CancellationToken);
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.True(source.CanRead);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task PieceDataAcknowledgedUploadRemainsDirtyWhileSourceIgnoresCancellation()
        {
            var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var caller = new CancellationTokenSource();
            var source = new LongDataSource(20000, true);
            source.BeforeReadAsync = async token => { if (source.BytesRead >= 8193) { reading.TrySetResult(); await release.Task; } };
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 60, 1);
                await StatementCommand(channel, 162); await channel.WritePacketAsync(TimeZoneOk("Asia/Tokyo"));
                Assert.Equal(0, await channelStreamRead(channel)); closed.SetResult();
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using var command = connection.CreateCommand(); command.CommandText = "insert into t values (:p)";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = source });
                var pending = command.ExecuteNonQueryAsync(caller.Token);
                await reading.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal("Asia/Tokyo", session.SessionTimeZone);
                caller.Cancel(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(pending.IsCompleted);
                release.SetResult();
                Assert.Equal(caller.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)))).CancellationToken);
                Assert.False(source.WasDisposed); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task PieceDataEncodedBudgetIncludesHexExpansionAndTheExecuteHeader()
        {
            const int limit = (64 * 1024 * 1024 - 14) / 2;
            var received = 0;
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 58, 1);
                while (received < limit / 8192 * 8192)
                {
                    var packet = await StatementCommand(channel, 162); Assert.Equal(16401, packet.Length); received += 8192;
                    await channel.WritePacketAsync(Ok(0));
                }
                Assert.Equal(0, await channelStreamRead(channel));
            });
            var source = new LongDataSource(limit + 1, true) { ChunkSize = 8192 };
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand(); command.CommandText = "insert into t values (:p)"; command.CommandTimeout = 30;
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = source });
                await Assert.ThrowsAsync<ArgumentException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.False(source.WasDisposed); Assert.Equal(limit + 1, source.BytesRead);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task PieceDataUnsupportedDeclarationsAreRejectedWithoutIo()
        {
            var server = Server(async channel => { await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            var source = new LongDataSource(3, false);
            try
            {
                await connection.OpenAsync();
                using var command = (OceanBaseCommand)connection.CreateCommand(); command.CommandText = "insert into t values (:p)";
                var value = new OceanBaseParameter { ParameterName = "p", Value = source }; command.Parameters.Add(value);
                foreach (var invalid in new Action[] { () => value.DbType = DbType.String, () => value.Size = 1, () => value.Precision = 1,
                    () => value.Scale = 1, () => value.OceanBaseDbType = OceanBaseDbType.Blob, () => command.AllowClientSidePrepareFallback = true,
                    () => { command.CommandType = CommandType.StoredProcedure; command.CommandText = "p"; value.Direction = ParameterDirection.InputOutput; value.Size = 1; }, () => source.Dispose() })
                {
                    value.ResetDbType(); value.Size = 0; value.Precision = value.Scale = 0; value.Direction = ParameterDirection.Input; command.AllowClientSidePrepareFallback = false;
                    command.CommandType = CommandType.Text; command.CommandText = "insert into t values (:p)"; invalid();
                    Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
                }
                Assert.Equal(0, source.BytesRead); command.Parameters.Clear(); command.CommandText = "after";
                Assert.Equal(1, await command.ExecuteNonQueryAsync()); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        public async Task StreamIdentityCannotBeBoundToMultiplePositions(bool asynchronous, bool mysql, bool repeatedName)
        {
            var server = Server(async channel => { await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1)); });
            var source = new LongDataSource(3, asynchronous);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var command = connection.CreateCommand();
                command.CommandText = mysql ? "insert into t values (?, ?)" : repeatedName ? "insert into t values (:p, :p)" : "insert into t values (:p, :q)";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = source });
                if (!repeatedName) command.Parameters.Add(new OceanBaseParameter { ParameterName = "q", Value = source });
                if (asynchronous) await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteNonQueryAsync());
                else Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
                Assert.Equal(0, source.BytesRead); Assert.False(source.WasDisposed);
                command.Parameters.Clear(); command.CommandText = "after";
                Assert.Equal(1, await command.ExecuteNonQueryAsync()); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static void AssertPiece(byte[] packet, byte statement, ushort index, byte piece, int offset, int count)
        {
            Assert.Equal(17 + count * 2, packet.Length);
            var length = count * 2;
            Assert.Equal(new byte[] { 162, statement, 0, 0, 0, (byte)index, (byte)(index >> 8), piece, 0,
                (byte)length, (byte)(length >> 8), 0, 0, 0, 0, 0, 0 }, packet.Take(17));
            var data = Enumerable.Range(offset, count).Select(i => (byte)(i % 251)).ToArray();
            Assert.Equal(Convert.ToHexString(data), Encoding.ASCII.GetString(packet, 17, count * 2));
        }
    }
}
