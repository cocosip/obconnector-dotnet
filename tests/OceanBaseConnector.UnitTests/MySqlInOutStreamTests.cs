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
        [InlineData(false, false, 0)]
        [InlineData(true, true, 0)]
        [InlineData(false, true, 20000)]
        [InlineData(true, false, 20000)]
        public async Task MySqlInOutStreamUploadsInitializerAndPublishesOnlyAfterCompletion(bool asynchronous, bool deprecated, int size)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var source = new LongDataSource(size, asynchronous);
            var server = MySqlProcedureServer(async channel =>
            {
                var setup = await StatementCommand(channel, 22, deprecated);
                var variables = Encoding.UTF8.GetString(setup, 1, setup.Length - 1).Substring(4).Split(", ").Select(a => a.Split(" = ")[0]).ToArray();
                Assert.Equal(2, variables.Length);
                await Prepared(channel, 80, 2, deprecated: deprecated);
                var received = 0;
                do
                {
                    var packet = await StatementCommand(channel, 24, deprecated);
                    Assert.Equal(new byte[] { 24, 80, 0, 0, 0, 1, 0 }, packet.Take(7));
                    Assert.InRange(packet.Length, 7, 8199);
                    for (var i = 7; i < packet.Length; i++) Assert.Equal((byte)((received + i - 7) % 251), packet[i]);
                    received += packet.Length - 7;
                } while (received < size);
                Assert.Equal(size, received);
                Assert.Equal(new byte[] { 23, 80, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 6, 0, 252, 0 }, await StatementCommand(channel, 23, deprecated));
                await channel.WritePacketAsync(Ok(status: 2));
                Assert.Equal(new byte[] { 25, 80, 0, 0, 0 }, await StatementCommand(channel, 25, deprecated));
                Assert.Equal(OceanBaseCommand.SqlPacket("CALL `p`(?, " + string.Join(", ", variables) + ")", 22), await StatementCommand(channel, 22, deprecated));
                await Prepared(channel, 81, 1, deprecated: deprecated);
                await StatementCommand(channel, 23, deprecated);
                await MySqlProcedureReply(channel, deprecated, 9, more: true);
                await channel.WritePacketAsync(Ok(status: 2));
                channel.BeginCommand(); await Query(channel, "SELECT " + string.Join(", ", variables), deprecated);
                await channel.WritePacketAsync(new byte[] { 2 });
                await channel.WritePacketAsync(Column("out", 8)); await channel.WritePacketAsync(Column("inout", 252, charset: 63));
                if (!deprecated) await channel.WritePacketAsync(Eof(2));
                await channel.WritePacketAsync(new byte[] { 251, 2, 0, 255 });
                waiting.SetResult(); await release.Task;
                await channel.WritePacketAsync(MySqlProcedureEnd(deprecated));
                await StatementCommand(channel, 25, deprecated);
            }, deprecated);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
                command.Parameters.Add(new OceanBaseParameter { Value = 7L });
                var sentinel = new object();
                var output = new OceanBaseParameter { Direction = ParameterDirection.Output, Value = sentinel };
                var inout = new OceanBaseParameter { Direction = ParameterDirection.InputOutput, DbType = DbType.Binary, Value = source };
                command.Parameters.Add(output); command.Parameters.Add(inout);
                source.BeforeRead = _ => { inout.Value = sentinel; inout.DbType = DbType.String; };
                using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal(9L, reader.GetInt64(0));
                var completing = Task.Run(async () => { if (asynchronous) await reader.CloseAsync(); else reader.Close(); });
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Same(sentinel, inout.Value); Assert.Same(sentinel, output.Value);
                release.SetResult(); await completing.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(new byte[] { 0, 255 }, Assert.IsType<byte[]>(inout.Value)); Assert.Equal(DBNull.Value, output.Value);
                Assert.Equal(size, source.BytesRead); Assert.False(source.WasDisposed);
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task MySqlInOutStreamSourceFailureDoesNotCallOrPublish(bool asynchronous, bool uploaded)
        {
            using var source = new LongDataSource(20000, asynchronous)
            { BeforeRead = read => { if (!uploaded || read > 0) throw new IOException("source failed"); } };
            var server = MySqlProcedureServer(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 80, 1);
                if (uploaded)
                {
                    await StatementCommand(channel, 24); Assert.Equal(0, await channelStreamRead(channel));
                }
                else
                {
                    Assert.Equal(new byte[] { 25, 80, 0, 0, 0 }, await StatementCommand(channel, 25));
                    channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(status: 2));
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
                var parameter = new OceanBaseParameter { Direction = ParameterDirection.InputOutput, Value = source };
                command.Parameters.Add(parameter);
                await Assert.ThrowsAsync<IOException>(async () => { if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                Assert.Same(source, parameter.Value); Assert.False(source.WasDisposed);
                Assert.Equal(uploaded ? ConnectionState.Broken : ConnectionState.Open, connection.State);
                if (!uploaded)
                {
                    using var after = connection.CreateCommand(); after.CommandText = "after"; await after.ExecuteNonQueryAsync();
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlInOutStreamDuplicateReferencesRejectBeforeInitializer(bool bothInOut)
        {
            var server = MySqlProcedureServer(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(status: 2)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var source = new LongDataSource(100, false);
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
                command.Parameters.Add(new OceanBaseParameter { Direction = bothInOut ? ParameterDirection.InputOutput : ParameterDirection.Input, Value = source });
                command.Parameters.Add(new OceanBaseParameter { Direction = ParameterDirection.InputOutput, Value = source });
                Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
                Assert.Equal(0, source.BytesRead); Assert.False(source.WasDisposed); Assert.Equal(ConnectionState.Open, connection.State);
                using var after = connection.CreateCommand(); after.CommandText = "after"; await after.ExecuteNonQueryAsync();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlInOutStreamInterruptionClosesStagedSessionWithoutPublishing(bool timeout)
        {
            var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var caller = new CancellationTokenSource();
            using var source = new LongDataSource(20000, true);
            var reads = 0;
            source.BeforeReadAsync = async token =>
            {
                if (++reads == 2) { reading.SetResult(); await Task.Delay(Timeout.Infinite, token); }
            };
            var server = MySqlProcedureServer(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 80, 1); await StatementCommand(channel, 24);
                Assert.Equal(0, await channelStreamRead(channel)); closed.SetResult();
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandType = CommandType.StoredProcedure; command.CommandText = "p"; command.CommandTimeout = timeout ? 1 : 10;
                var parameter = new OceanBaseParameter { Direction = ParameterDirection.InputOutput, Value = source }; command.Parameters.Add(parameter);
                var pending = command.ExecuteNonQueryAsync(caller.Token);
                await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (!timeout) caller.Cancel();
                if (timeout) await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                else
                {
                    var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.Equal(caller.Token, error.CancellationToken);
                }
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.Same(source, parameter.Value); Assert.False(source.WasDisposed);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
