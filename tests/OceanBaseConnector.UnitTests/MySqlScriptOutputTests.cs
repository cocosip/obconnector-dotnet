using System;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static (OceanBaseParameter Output, OceanBaseParameter InOut) AddMySqlScriptParameters(OceanBaseCommand command)
        {
            command.CommandText = "CALL p(?,?); SELECT ?";
            var output = new OceanBaseParameter { Direction = ParameterDirection.Output, Value = new object() };
            var inout = new OceanBaseParameter { Direction = ParameterDirection.InputOutput, Value = "中文'input" };
            command.Parameters.Add(output); command.Parameters.Add(inout); command.Parameters.Add(new OceanBaseParameter { Value = 7 });
            return (output, inout);
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        public async Task MySqlScriptOutputsPublishOnlyAfterAllResults(bool asynchronous, bool deprecated, bool selectOnly)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = MySqlProcedureServer(async channel =>
            {
                var variables = await MySqlProcedureInitialize(channel, deprecated);
                channel.BeginCommand();
                await Query(channel, (selectOnly ? "SELECT " + string.Join(",", variables) : "CALL p(" + string.Join(",", variables) + ")") + "; SELECT (7)", deprecated);
                if (selectOnly)
                {
                    await channel.WritePacketAsync(new byte[] { 2 });
                    await channel.WritePacketAsync(Column("out", 8)); await channel.WritePacketAsync(Column("inout", 253));
                    await channel.WritePacketAsync(Eof(2));
                    var initial = System.Text.Encoding.UTF8.GetBytes("中文'input");
                    var row = new byte[initial.Length + 2]; row[0] = 251; row[1] = (byte)initial.Length; initial.CopyTo(row, 2);
                    await channel.WritePacketAsync(row); await channel.WritePacketAsync(Eof(10));
                }
                else await channel.WritePacketAsync(Ok(status: 10));
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("normal", 8));
                if (!deprecated) await channel.WritePacketAsync(Eof(2));
                await channel.WritePacketAsync(new byte[] { 1, 57 });
                await channel.WritePacketAsync(MySqlProcedureEnd(deprecated));
                channel.BeginCommand(); await Query(channel, "SELECT " + string.Join(", ", variables), deprecated);
                await channel.WritePacketAsync(new byte[] { 2 });
                await channel.WritePacketAsync(Column("out", 8)); await channel.WritePacketAsync(Column("inout", 253));
                if (!deprecated) await channel.WritePacketAsync(Eof(2));
                await channel.WritePacketAsync(new byte[] { 251, 3, 110, 101, 119 });
                waiting.TrySetResult(); await release.Task;
                await channel.WritePacketAsync(MySqlProcedureEnd(deprecated));
                channel.BeginCommand(); await Query(channel, "after script outputs", deprecated); await channel.WritePacketAsync(Ok(1, 2));
            }, deprecated);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                var (output, inout) = AddMySqlScriptParameters(command);
                if (selectOnly) command.CommandText = "SELECT ?,?; SELECT ?";
                using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : command.ExecuteReader(CommandBehavior.SequentialAccess);
                Assert.Equal(selectOnly ? 2 : 0, reader.FieldCount);
                if (selectOnly)
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.True(reader.IsDBNull(0)); Assert.Equal("中文'input", reader.GetString(1));
                }
                Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal(9L, reader.GetInt64(0));
                var completing = Task.Run(async () => { if (asynchronous) await reader.CloseAsync(); else reader.Close(); });
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsType<object>(output.Value); Assert.Equal("中文'input", inout.Value);
                using var blocked = connection.CreateCommand(); blocked.CommandText = "cannot interleave";
                Assert.Throws<InvalidOperationException>(() => blocked.ExecuteNonQuery());
                release.TrySetResult(); await completing.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(DBNull.Value, output.Value); Assert.Equal("new", inout.Value);
                Assert.True(connection.GetOpenSession().RequiresDiscardOnReturn);
                using var after = connection.CreateCommand(); after.CommandText = "after script outputs";
                Assert.Equal(1, asynchronous ? await after.ExecuteNonQueryAsync() : after.ExecuteNonQuery());
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        [InlineData(false, 2)]
        [InlineData(true, 2)]
        [InlineData(false, 3)]
        [InlineData(true, 3)]
        [InlineData(false, 4)]
        [InlineData(true, 4)]
        public async Task MySqlScriptFailuresKeepCallerOutputsAndNeverReplay(bool asynchronous, int stage)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                var variables = await MySqlProcedureInitialize(channel, false);
                channel.BeginCommand(); await Query(channel, "CALL p(" + string.Join(",", variables) + "); SELECT (7)");
                if (stage == 0) { await channel.WritePacketAsync(Ok(3, 10)); await channel.WritePacketAsync(Error()); }
                else
                {
                    await channel.WritePacketAsync(Ok(3, 2));
                    channel.BeginCommand(); await Query(channel, "SELECT " + string.Join(", ", variables));
                    if (stage == 1) await channel.WritePacketAsync(Error());
                    else
                    {
                        await channel.WritePacketAsync(new byte[] { 2 });
                        await channel.WritePacketAsync(Column("out", 8)); await channel.WritePacketAsync(Column("inout", 253));
                        await channel.WritePacketAsync(Eof(2));
                        if (stage != 2) await channel.WritePacketAsync(new byte[] { 1, 55, 3, 110, 101, 119 });
                        if (stage == 3) { channel.Close(); return; }
                        if (stage == 4)
                        {
                            await channel.WritePacketAsync(new byte[] { 1, 56, 3, 110, 101, 119 });
                            await AssertOracleExecuteTransportClosed(channel); return;
                        }
                        await channel.WritePacketAsync(Eof(2));
                    }
                }
                channel.BeginCommand(); await Query(channel, "after failed script"); await channel.WritePacketAsync(Ok(1, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                var (output, inout) = AddMySqlScriptParameters(command); var old = output.Value;
                var error = await Record.ExceptionAsync(async () => { if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                Assert.NotNull(error); Assert.Same(old, output.Value); Assert.Equal("中文'input", inout.Value);
                if (stage >= 3)
                {
                    Assert.True(Assert.IsType<OceanBaseException>(error).IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
                }
                else
                {
                    Assert.Equal(ConnectionState.Open, connection.State);
                    if (stage < 2) Assert.False(Assert.IsType<OceanBaseException>(error).IsExecutionOutcomeUnknown);
                    else Assert.IsType<ProtocolException>(error);
                    using var after = connection.CreateCommand(); after.CommandText = "after failed script";
                    Assert.Equal(1, asynchronous ? await after.ExecuteNonQueryAsync() : after.ExecuteNonQuery());
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlScriptOutputCancellationRetainsTokenAndOutputs(bool readerToken)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = MySqlProcedureServer(async channel =>
            {
                var variables = await MySqlProcedureInitialize(channel, false);
                channel.BeginCommand(); await Query(channel, "CALL p(" + string.Join(",", variables) + "); SELECT (7)");
                await channel.WritePacketAsync(Ok(3, 2));
                channel.BeginCommand(); await Query(channel, "SELECT " + string.Join(", ", variables));
                waiting.TrySetResult(); await AssertOracleExecuteTransportClosed(channel);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                var (output, inout) = AddMySqlScriptParameters(command); var old = output.Value;
                using var reader = await command.ExecuteReaderAsync(readerToken ? default : cancellation.Token);
                var reading = reader.NextResultAsync(readerToken ? cancellation.Token : default);
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => reading);
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error.InnerException).CancellationToken);
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.Same(old, output.Value); Assert.Equal("中文'input", inout.Value);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlScriptValidationDoesNotInitializeVariablesOrReadStreams(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel => { await Query(channel, "after rejected script"); await channel.WritePacketAsync(Ok(1, 2)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
                var output = new OceanBaseParameter { Direction = ParameterDirection.InputOutput, DbType = DbType.Binary, Value = stream };
                command.CommandText = "SELECT ?; SELECT 1"; command.Parameters.Add(output);
                await Assert.ThrowsAsync<NotSupportedException>(async () => { if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                Assert.Equal(0, stream.Position);
                output.Direction = ParameterDirection.Output; output.DbType = DbType.String; output.Value = "old";
                command.FetchSize = 1; Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery()); command.FetchSize = 0;
                Assert.Throws<NotSupportedException>(() => command.Prepare());
                output.Direction = ParameterDirection.ReturnValue; Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
                output.Direction = ParameterDirection.Output;
                command.CommandText = "SELECT ?; SELECT ?"; Assert.Throws<ArgumentException>(() => command.ExecuteNonQuery());
                command.CommandText = "SELECT ?; SELECT 'ambiguous\\'; SELECT ?";
                command.Parameters.Add(new OceanBaseParameter { Value = 7 }); Assert.Throws<ArgumentException>(() => command.ExecuteNonQuery());
                command.CommandText = "SELECT ?; SELECT ?"; command.Parameters[1].Value = Guid.Empty;
                Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
                Assert.Equal("old", output.Value); Assert.Equal(0, stream.Position);
                Assert.False(connection.GetOpenSession().RequiresDiscardOnReturn); Assert.False(command.HasSentSql);
                command.Parameters.Clear(); command.CommandText = "after rejected script";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlScriptOutputsStillRequireMultiStatementOptIn(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel => { await Query(channel, "after opt-in rejection"); await channel.WritePacketAsync(Ok(1, 2)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "SET ?=1; SELECT 1";
                var parameter = new OceanBaseParameter { Direction = ParameterDirection.Output, Value = "old" }; command.Parameters.Add(parameter);
                await Assert.ThrowsAsync<NotSupportedException>(async () => { if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                Assert.Equal("old", parameter.Value);
                command.CommandText = "after opt-in rejection"; command.Parameters.Clear();
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
