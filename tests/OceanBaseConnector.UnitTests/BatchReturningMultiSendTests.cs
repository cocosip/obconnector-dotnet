using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> BatchReturningCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var api in new[] { "nonquery", "scalar", "reader", "singleRow", "singleResult" })
                    foreach (var inputOutput in new[] { false, true })
                        yield return new object[] { asynchronous, api, inputOutput };
        }

        private static OceanBaseParameter BatchReturningItem(System.Data.Common.DbBatch batch, long input, bool inputOutput = false)
        {
            var item = BatchItem(batch, "insert into t values (:input) returning id into :output");
            item.Parameters.Add(new OceanBaseParameter { ParameterName = "input", Value = input });
            var output = new OceanBaseParameter { ParameterName = "output", DbType = DbType.Int64,
                Direction = inputOutput ? ParameterDirection.InputOutput : ParameterDirection.Output, Value = -1L };
            item.Parameters.Add(output); return output;
        }

        private static async Task BatchReturningRequest(PacketChannel channel, long input, bool inputOutput = false, uint id = 0, long oldValue = -1)
        {
            var parameters = new byte[] { inputOutput ? (byte)0 : (byte)2, 1, 8, 0, inputOutput ? (byte)8 : (byte)6, 0 }
                .Concat(BitConverter.GetBytes(input));
            if (inputOutput) parameters = parameters.Concat(BitConverter.GetBytes(oldValue));
            Assert.Equal(OraclePrepareExecutePacket.Build(id, "insert into t values (?) returning id into ?", 2, parameters.ToArray()),
                await StatementCommand(channel, 0xa1));
        }

        private static async Task BatchReturningReply(PacketChannel channel, byte id, long? value, bool tail = true, uint flags = 4, bool extraRow = false)
        {
            channel.BeginResponse(); var header = OracleExecuteHeader(flags: flags); header[1] = id;
            await channel.WritePacketAsync(header);
            await channel.WritePacketAsync(Column("in", 8)); await channel.WritePacketAsync(Column("out", 8));
            await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Column("value", 8)); await channel.WritePacketAsync(Eof());
            await channel.WritePacketAsync(value.HasValue ? new byte[] { 0, 0 }.Concat(BitConverter.GetBytes(value.Value)).ToArray() : new byte[] { 0, 4 });
            if (extraRow) await channel.WritePacketAsync(new byte[] { 0, 0 }.Concat(BitConverter.GetBytes(99L)).ToArray());
            await channel.WritePacketAsync(Eof()); if (tail) await channel.WritePacketAsync(Ok(1));
        }

        [Theory]
        [MemberData(nameof(BatchReturningCases))]
        public async Task BatchMultiSendReturningPublishesEachCompletedResponse(bool asynchronous, string api, bool inputOutput)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await BatchReturningRequest(channel, 1, inputOutput); await BatchReturningRequest(channel, 2, inputOutput);
                await BatchReturningReply(channel, 90, 7); await BatchReturningReply(channel, 91, null, tail: false);
                waiting.SetResult(); await release.Task; await channel.WritePacketAsync(Ok(1));
                await BatchReturningRequest(channel, 3, inputOutput); await BatchReturningReply(channel, 92, 9);
                for (byte id = 90; id < 93; id++) Assert.Equal(id, (await StatementCommand(channel, 25))[1]);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync();
                using (var batch = connection.CreateBatch())
                {
                    var outputs = new[] { BatchReturningItem(batch, 1, inputOutput), BatchReturningItem(batch, 2, inputOutput), BatchReturningItem(batch, 3, inputOutput) };
                    var executing = Task.Run(async () =>
                    {
                        if (api == "nonquery") Assert.Equal(3, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                        else if (api == "scalar") Assert.Null(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
                        else
                        {
                            var behavior = api == "singleRow" ? CommandBehavior.SingleRow : api == "singleResult" ? CommandBehavior.SingleResult : CommandBehavior.Default;
                            using var reader = asynchronous ? await batch.ExecuteReaderAsync(behavior) : batch.ExecuteReader(behavior);
                            for (var i = 0; i < (api == "reader" ? 3 : 1); i++)
                            {
                                Assert.Equal(0, reader.FieldCount); Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                                Assert.Equal(api == "reader" && i < 2, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                            }
                            Assert.Equal(3, reader.RecordsAffected);
                        }
                    });
                    if (await Task.WhenAny(waiting.Task, executing) == executing) await executing;
                    await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    // The first response is complete, while the next output row has no terminal OK.
                    Assert.Equal(-1L, outputs[1].Value); Assert.Equal(-1L, outputs[2].Value);
                    release.SetResult(); await executing.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(7L, outputs[0].Value); Assert.Equal(DBNull.Value, outputs[1].Value); Assert.Equal(9L, outputs[2].Value);
                    Assert.All(batch.BatchCommands.Cast<OceanBaseBatchCommand>(), item => Assert.Equal(1, item.RecordsAffected));
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, true, false)] [InlineData(true, true, false)]
        [InlineData(false, false, false)] [InlineData(true, false, false)]
        [InlineData(false, true, true)] [InlineData(true, true, true)]
        [InlineData(false, false, true)] [InlineData(true, false, true)]
        public async Task BatchMultiSendReturningFailureKeepsPublishedPrefix(bool asynchronous, bool last, bool limited)
        {
            var server = Server(async channel =>
            {
                for (var i = 0; i < 3; i++) await BatchReturningRequest(channel, i + 1);
                await BatchReturningReply(channel, 90, 7);
                if (last) await BatchReturningReply(channel, 91, 8);
                await BatchReturningReply(channel, last ? (byte)92 : (byte)91, 9, tail: false); await channel.WritePacketAsync(Error());
                if (last)
                {
                    for (byte id = 90; id < 93; id++) Assert.Equal(id, (await StatementCommand(channel, 25))[1]);
                    channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
                }
                else try { Assert.Equal(0, await channelStreamRead(channel)); } catch (System.IO.IOException) { }
            }, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true;Batch Multi Send Number=3");
            try
            {
                await connection.OpenAsync();
                using (var batch = connection.CreateBatch())
                {
                    var outputs = new[] { BatchReturningItem(batch, 1), BatchReturningItem(batch, 2), BatchReturningItem(batch, 3) };
                    var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                    {
                        if (!limited) { if (asynchronous) await batch.ExecuteNonQueryAsync(); else batch.ExecuteNonQuery(); }
                        else
                        {
                            using var reader = asynchronous ? await batch.ExecuteReaderAsync(CommandBehavior.SingleRow) : batch.ExecuteReader(CommandBehavior.SingleRow);
                            if (asynchronous) await reader.NextResultAsync(); else reader.NextResult();
                        }
                    });
                    Assert.Same(batch.BatchCommands[last ? 2 : 1], error.BatchCommand); Assert.False(error.IsExecutionOutcomeUnknown);
                    Assert.Equal(7L, outputs[0].Value); Assert.Equal(last ? 8L : -1L, outputs[1].Value); Assert.Equal(-1L, outputs[2].Value);
                    Assert.Equal(last ? ConnectionState.Open : ConnectionState.Broken, connection.State);
                }
                if (last) { using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync()); }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "cancel")] [InlineData(true, "cancel")]
        [InlineData(false, "timeout")] [InlineData(true, "timeout")]
        [InlineData(false, "truncated")] [InlineData(true, "truncated")]
        [InlineData(false, "invalid")] [InlineData(true, "invalid")]
        [InlineData(false, "marker")] [InlineData(true, "marker")]
        [InlineData(false, "rows")] [InlineData(true, "rows")]
        public async Task BatchMultiSendReturningIncompleteOutputKeepsOldValue(bool asynchronous, string failure)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await BatchReturningRequest(channel, 1);
                try
                {
                    await BatchReturningReply(channel, 90, 7, tail: failure == "marker" || failure == "rows",
                        flags: failure == "marker" ? 0u : 4u, extraRow: failure == "rows");
                }
                catch (System.IO.IOException) when (failure == "rows") { waiting.TrySetResult(); return; }
                waiting.SetResult();
                if (failure == "cancel" || failure == "timeout" || failure == "marker" || failure == "rows")
                    try { Assert.Equal(0, await channelStreamRead(channel)); } catch (System.IO.IOException) { }
                else if (failure == "invalid") await channel.WritePacketAsync(new byte[] { 0 });
            }, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); var output = BatchReturningItem(batch, 1);
                if (failure == "timeout") batch.Timeout = 1;
                var executing = Task.Run(async () => asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); if (failure == "cancel") batch.Cancel();
                await Assert.ThrowsAsync<OceanBaseException>(() => executing.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(-1L, output.Value); Assert.Equal(ConnectionState.Broken, connection.State);
                Assert.Equal(-1, batch.BatchCommands[0].RecordsAffected);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task BatchMultiSendReturningReusesExplicitPreparationAndClosesEveryStatement(bool asynchronous, bool inputOutput)
        {
            var server = Server(async channel =>
            {
                for (byte id = 90; id < 92; id++) { await StatementCommand(channel, 22); await Prepared(channel, id, 1, 1); }
                for (var run = 0; run < 2; run++)
                {
                    if (run == 1) for (byte id = 90; id < 92; id++) Assert.Equal(id, (await StatementCommand(channel, 25))[1]);
                    await BatchReturningRequest(channel, 1, inputOutput, id: run == 0 ? 90u : 0u, oldValue: run == 0 ? -1L : 7L);
                    await BatchReturningRequest(channel, 2, inputOutput, id: run == 0 ? 91u : 0u, oldValue: run == 0 ? -1L : 8L);
                    await BatchReturningReply(channel, (byte)(90 + run * 2), 7);
                    await BatchReturningReply(channel, (byte)(91 + run * 2), 8);
                }
                for (byte id = 92; id < 94; id++) Assert.Equal(id, (await StatementCommand(channel, 25))[1]);
            }, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync();
                using (var batch = connection.CreateBatch())
                {
                    var first = BatchReturningItem(batch, 1, inputOutput); var second = BatchReturningItem(batch, 2, inputOutput);
                    if (asynchronous) await batch.PrepareAsync(); else batch.Prepare();
                    Assert.Equal(-1L, first.Value);
                    for (var i = 0; i < 2; i++)
                    {
                        Assert.Equal(2, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                        Assert.Equal(7L, first.Value); Assert.Equal(8L, second.Value);
                    }
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(160)] [InlineData(163)] [InlineData(210)] [InlineData(211)]
        public async Task BatchMultiSendReturningRejectsResourceMetadataBeforeNullRows(byte type)
        {
            var server = Server(async channel =>
            {
                await BatchReturningRequest(channel, 1); channel.BeginResponse();
                await channel.WritePacketAsync(OracleExecuteHeader());
                await channel.WritePacketAsync(Column("in", 8)); await channel.WritePacketAsync(Column("out", 8));
                await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Column("value", type));
                try { Assert.Equal(0, await channelStreamRead(channel)); } catch (System.IO.IOException) { }
            }, serverVersion: "4.4.2.2", lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); var output = BatchReturningItem(batch, 1);
                var session = connection.GetOpenSession(); await Assert.ThrowsAsync<OceanBaseException>(() => batch.ExecuteNonQueryAsync());
                Assert.Equal(-1L, output.Value); Assert.Equal(ConnectionState.Broken, connection.State); Assert.False(session.RequiresDiscardOnReturn);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(OceanBaseDbType.RefCursor)]
        public async Task BatchMultiSendReturningRejectsNullResourceDeclarationsBeforeSending(OceanBaseDbType type)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)), serverVersion: "4.4.2.2", lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); var output = BatchReturningItem(batch, 1);
                output.OceanBaseDbType = type; output.Value = DBNull.Value;
                await Assert.ThrowsAsync<NotSupportedException>(() => batch.ExecuteNonQueryAsync()); Assert.Equal(DBNull.Value, output.Value);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
