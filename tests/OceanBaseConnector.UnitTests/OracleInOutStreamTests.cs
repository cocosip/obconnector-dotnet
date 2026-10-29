using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static async Task OracleInOutStreamReply(PacketChannel channel, bool rawOutput = true)
        {
            var header = OracleExecuteHeader(1, 1, 4); header[1] = 60;
            await channel.WritePacketAsync(header);
            await channel.WritePacketAsync(Column("p", 254, charset: 63)); await channel.WritePacketAsync(Eof());
            await channel.WritePacketAsync(Column("p", rawOutput ? (byte)203 : (byte)254, charset: rawOutput ? (ushort)63 : (ushort)45)); await channel.WritePacketAsync(Eof());
            await channel.WritePacketAsync(rawOutput ? new byte[] { 0, 0, 2, 0, 255 } : new byte[] { 0, 0, 4, 48, 48, 70, 70 }); await channel.WritePacketAsync(Eof());
        }

        [Theory]
        [InlineData(false, false, 0)]
        [InlineData(true, false, 0)]
        [InlineData(false, true, 0)]
        [InlineData(true, true, 0)]
        [InlineData(false, false, 20000)]
        [InlineData(true, false, 20000)]
        [InlineData(false, true, 20000)]
        [InlineData(true, true, 20000)]
        public async Task OracleInOutStreamUploadsSnapshotAndPublishesAtCompletion(bool asynchronous, bool prepared, int size)
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var source = new LongDataSource(size, asynchronous);
            var server = Server(async channel =>
            {
                for (var run = 0; run < 2; run++)
                {
                    await StatementCommand(channel, 22); await Prepared(channel, 60, 1);
                    var received = 0; var length = run == 0 ? size : 0;
                    do
                    {
                        var count = Math.Min(8192, length - received);
                        var piece = received + count == length ? (byte)3 : received == 0 ? (byte)1 : (byte)2;
                        AssertPiece(await StatementCommand(channel, 162), 60, 0, piece, received, count);
                        received += count; await channel.WritePacketAsync(Ok());
                    } while (received < length);
                    var request = new ByteCursor(await StatementCommand(channel, 0xa1));
                    request.ReadUInt8(); Assert.Equal(60u, request.ReadUInt32()); Assert.Equal(0, request.ReadUInt8());
                    Assert.Equal(1u, request.ReadUInt32()); request.ReadLengthEncodedBytes(); Assert.Equal(1u, request.ReadUInt32());
                    Assert.Equal(new byte[] { 0, 1, 254, 0 }, request.ReadBytes(4)); Assert.Equal(16, request.Remaining);
                    await OracleInOutStreamReply(channel, rawOutput: prepared);
                    if (run == 0) { reached.SetResult(); await release.Task; }
                    await channel.WritePacketAsync(Ok(2));
                    Assert.Equal(new byte[] { 25, 60, 0, 0, 0 }, await StatementCommand(channel, 25));
                }
            }, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = "p"; command.CommandType = CommandType.StoredProcedure;
                var parameter = new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.InputOutput, Value = source };
                command.Parameters.Add(parameter);
                if (prepared) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); Assert.Equal(0, source.BytesRead); }
                var sentinel = new object();
                source.BeforeRead = _ => { parameter.Value = sentinel; parameter.DbType = DbType.String; };
                var pending = Task.Run(async () => asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                await Task.WhenAny(reached.Task, pending).WaitAsync(TimeSpan.FromSeconds(5));
                if (pending.IsFaulted) await pending;
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Same(sentinel, parameter.Value); Assert.False(pending.IsCompleted);
                release.SetResult(); Assert.Equal(2, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                if (prepared) Assert.Equal(new byte[] { 0, 255 }, Assert.IsType<byte[]>(parameter.Value));
                else Assert.Equal("00FF", Assert.IsType<string>(parameter.Value));
                parameter.Value = source; parameter.ResetDbType();
                Assert.Equal(2, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                if (prepared) Assert.Equal(new byte[] { 0, 255 }, Assert.IsType<byte[]>(parameter.Value));
                else Assert.Equal("00FF", Assert.IsType<string>(parameter.Value));
                Assert.Equal(size, source.BytesRead); Assert.False(source.WasDisposed);
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
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
        public async Task OracleInOutStreamFailuresRetainSourceAndNeverPublish(bool asynchronous, string fault)
        {
            var usable = fault == "source-before" || fault == "execute-error" || fault == "tail-error";
            using var source = new LongDataSource(fault == "source-after" ? 20000 : 3, asynchronous);
            source.BeforeRead = read => { if (fault == "source-before" || fault == "source-after" && read >= 8193) throw new IOException("source failed"); };
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 60, 1);
                if (fault != "source-before")
                {
                    await StatementCommand(channel, 162); await channel.WritePacketAsync(fault == "piece-error" ? Error() : Ok());
                    if (fault.StartsWith("execute") || fault.StartsWith("tail"))
                    {
                        await StatementCommand(channel, 0xa1);
                        if (fault == "execute-error") await channel.WritePacketAsync(Error());
                        else
                        {
                            await OracleInOutStreamReply(channel);
                            if (fault == "tail-disconnect") return;
                            await channel.WritePacketAsync(Error());
                        }
                    }
                }
                if (usable)
                {
                    await StatementCommand(channel, 25); channel.BeginCommand();
                    await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
                }
                else Assert.Equal(0, await channelStreamRead(channel));
            }, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "p"; command.CommandType = CommandType.StoredProcedure;
                var parameter = new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.InputOutput, Value = source }; command.Parameters.Add(parameter);
                var error = await Record.ExceptionAsync(async () => { if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                if (fault.StartsWith("source")) Assert.IsType<IOException>(error);
                else
                {
                    var database = Assert.IsType<OceanBaseException>(error);
                    Assert.Equal(fault == "tail-disconnect", database.IsExecutionOutcomeUnknown);
                    Assert.Equal(fault == "tail-disconnect" ? 0 : 1064, database.ServerErrorCode);
                }
                Assert.Same(source, parameter.Value); Assert.False(source.WasDisposed); Assert.Equal(usable ? ConnectionState.Open : ConnectionState.Broken, connection.State);
                if (usable) { using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync()); }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleInOutStreamInterruptionRetainsSourceAndClosesStagedSession(bool timeout)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var caller = new CancellationTokenSource(); using var source = new MemoryStream(new byte[20000]);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 60, 1);
                await StatementCommand(channel, 162); received.SetResult(); Assert.Equal(0, await channelStreamRead(channel));
            }, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "p"; command.CommandType = CommandType.StoredProcedure;
                command.CommandTimeout = timeout ? 1 : 5;
                var parameter = new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.InputOutput, Value = source }; command.Parameters.Add(parameter);
                var pending = command.ExecuteNonQueryAsync(caller.Token);
                await Task.WhenAny(received.Task, pending).WaitAsync(TimeSpan.FromSeconds(5));
                if (pending.IsFaulted) await pending;
                await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(pending.IsCompleted); if (!timeout) caller.Cancel();
                if (timeout) await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                else Assert.Equal(caller.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)))).CancellationToken);
                Assert.Same(source, parameter.Value); Assert.True(source.CanRead); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleInOutStreamDuplicateReferenceRejectsBeforeReading(bool bothInOut)
        {
            var server = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(1)); });
            using var source = new LongDataSource(3, false);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "p"; command.CommandType = CommandType.StoredProcedure;
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "a", Value = source, Direction = bothInOut ? ParameterDirection.InputOutput : ParameterDirection.Input });
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "b", Value = source, Direction = ParameterDirection.InputOutput });
                Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery()); Assert.Equal(0, source.BytesRead);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
