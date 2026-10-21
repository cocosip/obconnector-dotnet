using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, ParameterDirection.Output)] [InlineData(true, ParameterDirection.Output)]
        [InlineData(false, ParameterDirection.InputOutput)] [InlineData(true, ParameterDirection.InputOutput)]
        [InlineData(false, ParameterDirection.ReturnValue)] [InlineData(true, ParameterDirection.ReturnValue)]
        public async Task BatchOutputsPublishOnlyAfterTailAndFeedNextExecution(bool asynchronous, ParameterDirection direction)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await OracleVersionReply(channel, "4.4.2.2");
                for (var i = 0; i < 2; i++)
                {
                    var sql = direction == ParameterDirection.ReturnValue ? "BEGIN ? := pkg.proc(?); END;" : "BEGIN pkg.proc(?, ?); END;";
                    var parameters = new byte[] { direction == ParameterDirection.Output ? (byte)2 : direction == ParameterDirection.ReturnValue ? (byte)1 : (byte)0,
                        1, direction == ParameterDirection.ReturnValue ? (byte)6 : (byte)8, 0, direction == ParameterDirection.Output ? (byte)6 : (byte)8, 0 }
                        .Concat(BitConverter.GetBytes(7L));
                    if (direction == ParameterDirection.InputOutput) parameters = parameters.Concat(BitConverter.GetBytes(i == 0 ? 7L : 42L));
                    Assert.Equal(OraclePrepareExecutePacket.Build(0, sql, 2, parameters.ToArray()), await StatementCommand(channel, 0xa1));
                    await OracleOutputReply(channel, false);
                    if (i == 0) { waiting.SetResult(); await release.Task; }
                    await channel.WritePacketAsync(Ok(2));
                    await StatementCommand(channel, 25);
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); var item = BatchItem(batch, "pkg.proc"); item.CommandType = CommandType.StoredProcedure;
                var input = new OceanBaseParameter { ParameterName = "input", Value = 7L };
                var output = new OceanBaseParameter { ParameterName = "output", Direction = direction, DbType = DbType.Int64, Value = 7L };
                item.Parameters.Add(input); item.Parameters.Add(output);
                var executing = Task.Run(async () => asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(7L, output.Value);
                release.SetResult(); Assert.Equal(2, await executing.WaitAsync(TimeSpan.FromSeconds(10))); Assert.Equal(42L, output.Value);
                Assert.Equal(2, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                Assert.Equal(42L, output.Value); Assert.Equal(7L, input.Value); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task BatchOutputsRetainOriginalTargetsAndFrozenParameterMetadata(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "prefix"); await channel.WritePacketAsync(Ok(1)); channel.BeginCommand();
                await OracleVersionReply(channel, "4.4.2.2"); await StatementCommand(channel, 0xa1);
                await OracleOutputReply(channel, false); await channel.WritePacketAsync(Ok(2)); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); BatchItem(batch, "prefix");
                var item = BatchItem(batch, "pkg.proc"); item.CommandType = CommandType.StoredProcedure;
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "input", Value = 7L });
                var original = new OceanBaseParameter { ParameterName = "output", Direction = ParameterDirection.Output, Value = "old" };
                item.Parameters.Add(original);
                using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                original.Direction = ParameterDirection.Input; original.DbType = DbType.String; item.Parameters.Clear();
                var replacement = new OceanBaseParameter { ParameterName = "output", Direction = ParameterDirection.Output, Value = "replacement" }; item.Parameters.Add(replacement);
                Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.Equal(42L, original.Value); Assert.Equal("replacement", replacement.Value); Assert.Equal(3, reader.RecordsAffected);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task BatchOutputFailureRetainsSuccessfulPrefixAndOldFailedValue(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await OracleVersionReply(channel, "4.4.2.2");
                for (var i = 0; i < 2; i++)
                {
                    if (i != 0) await StatementCommand(channel, 25);
                    await StatementCommand(channel, 0xa1); await OracleOutputReply(channel, false);
                    await channel.WritePacketAsync(i == 0 ? Ok(2) : Error());
                }
                await StatementCommand(channel, 25); channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                var outputs = new OceanBaseParameter[2];
                for (var i = 0; i < 2; i++)
                {
                    var item = BatchItem(batch, "pkg.proc"); item.CommandType = CommandType.StoredProcedure;
                    item.Parameters.Add(new OceanBaseParameter { ParameterName = "input", Value = 7L });
                    outputs[i] = new OceanBaseParameter { ParameterName = "output", Direction = ParameterDirection.Output, Value = "old" }; item.Parameters.Add(outputs[i]);
                }
                BatchItem(batch, "never");
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(1064, error.ServerErrorCode); Assert.Same(batch.BatchCommands[1], error.BatchCommand);
                Assert.Equal(42L, outputs[0].Value); Assert.Equal("old", outputs[1].Value);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "cancel")] [InlineData(true, "cancel")]
        [InlineData(false, "truncated")] [InlineData(true, "truncated")]
        [InlineData(false, "invalid")] [InlineData(true, "invalid")]
        public async Task BatchIncompleteOutputNeverPublishes(bool asynchronous, string failure)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await OracleVersionReply(channel, "4.4.2.2"); await StatementCommand(channel, 0xa1); await OracleOutputReply(channel, false);
                waiting.SetResult();
                if (failure == "cancel") await AssertOracleExecuteTransportClosed(channel);
                else if (failure == "invalid") await channel.WritePacketAsync(new byte[] { 0 });
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); var item = BatchItem(batch, "pkg.proc"); item.CommandType = CommandType.StoredProcedure;
                item.Parameters.Add(new OceanBaseParameter { ParameterName = "input", Value = 7L });
                var output = new OceanBaseParameter { ParameterName = "output", Direction = ParameterDirection.Output, Value = "old" }; item.Parameters.Add(output);
                var executing = Task.Run(async () => asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); if (failure == "cancel") batch.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => executing.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal("old", output.Value); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task BatchRefCursorOutputSurvivesBatchDisposal(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await OracleVersionReply(channel, "4.4.2.2"); await StatementCommand(channel, 0xa1); await RefCursorOutput(channel);
                channel.BeginCommand(); await StatementCommand(channel, 28); await CursorMetadata(channel, 2);
                await channel.WritePacketAsync(CursorRow(7)); await channel.WritePacketAsync(CursorTerminator(130, false));
                for (var i = 0; i < 2; i++) { channel.BeginCommand(); await StatementCommand(channel, 25); }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); OceanBaseRefCursor cursor;
                using (var batch = connection.CreateBatch())
                {
                    var item = BatchItem(batch, "pkg.open_cursor"); item.CommandType = CommandType.StoredProcedure;
                    var output = new OceanBaseParameter { ParameterName = "cursor", Direction = ParameterDirection.Output, OceanBaseDbType = OceanBaseDbType.RefCursor }; item.Parameters.Add(output);
                    Assert.Equal(0, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery()); cursor = Assert.IsType<OceanBaseRefCursor>(output.Value);
                }
                using (cursor)
                using (var reader = asynchronous ? await cursor.GetDataReaderAsync(1) : cursor.GetDataReader(1))
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal(7L, reader.GetInt64(0));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.False(reader.NextResult());
                }
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
