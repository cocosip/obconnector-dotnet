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
        public static IEnumerable<object[]> RoutineBarrierCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var api in new[] { "reader", "nonquery", "scalar", "singleResult", "singleRow" })
                    foreach (var block in new[] { false, true }) yield return new object[] { asynchronous, api, block };
        }

        private static async Task RoutineBarrierGroup(PacketChannel channel, byte first)
        {
            for (var i = 0; i < 2; i++)
            {
                await StatementCommand(channel, 22); await Prepared(channel, (byte)(first + i), 0);
            }
            // Both executions must arrive before either response is sent.
            Assert.Equal(first, (await StatementCommand(channel, 23))[1]);
            Assert.Equal(first + 1, (await StatementCommand(channel, 23))[1]);
            channel.BeginResponse(); await channel.WritePacketAsync(Ok(1));
            channel.BeginResponse(); await channel.WritePacketAsync(Ok(2));
        }

        [Theory]
        [MemberData(nameof(RoutineBarrierCases))]
        public async Task BatchRoutineBarrierDefersFollowingPreflightAndPreservesPipelineSegments(bool asynchronous, string api, bool block)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sql = block ? "/*routine*/ <<run_once>> BEGIN pkg.proc(:input, :output); END;" : "pkg.proc";
            var wireSql = block ? "/*routine*/ <<run_once>> BEGIN pkg.proc(?, ?); END;" : "BEGIN pkg.proc(?, ?); END;";
            var server = Server(async channel =>
            {
                await RoutineBarrierGroup(channel, 90);
                var encoded = new byte[] { 2, 1, 8, 0, 6, 0 }.Concat(BitConverter.GetBytes(7L)).ToArray();
                Assert.Equal(OraclePrepareExecutePacket.Build(0, wireSql, 2, encoded), await StatementCommand(channel, 0xa1));
                await OracleOutputReply(channel, false); waiting.SetResult(); await release.Task;
                await channel.WritePacketAsync(Ok(3));
                Assert.Equal(42, (await StatementCommand(channel, 25))[1]);
                await RoutineBarrierGroup(channel, 92);
                for (var i = 90; i < 94; i++) Assert.Equal(i, (await StatementCommand(channel, 25))[1]);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync();
                using (var batch = connection.CreateBatch())
                {
                    BatchItem(batch, "update t set id=1"); BatchItem(batch, "update t set id=2");
                    var routine = BatchItem(batch, sql); routine.CommandType = block ? CommandType.Text : CommandType.StoredProcedure;
                    routine.Parameters.Add(new OceanBaseParameter { ParameterName = "input", Value = 7L });
                    var output = new OceanBaseParameter { ParameterName = "output", Direction = ParameterDirection.Output, Value = -1L };
                    routine.Parameters.Add(output);
                    BatchItem(batch, "update t set id=3"); BatchItem(batch, "update t set id=4");
                    var executing = Task.Run(async () =>
                    {
                        if (api == "nonquery") Assert.Equal(9, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                        else if (api == "scalar") Assert.Null(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
                        else
                        {
                            var behavior = CommandBehavior.SequentialAccess | (api == "singleResult" ? CommandBehavior.SingleResult : api == "singleRow" ? CommandBehavior.SingleRow : 0);
                            using var reader = asynchronous ? await batch.ExecuteReaderAsync(behavior) : batch.ExecuteReader(behavior);
                            var results = 0;
                            do { results++; Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); }
                            while (asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                            Assert.Equal(api == "reader" ? 5 : 1, results); Assert.Equal(9, reader.RecordsAffected);
                        }
                    });
                    var first = await Task.WhenAny(waiting.Task, executing).WaitAsync(TimeSpan.FromSeconds(10));
                    if (first == executing) await executing;
                    await waiting.Task; Assert.Equal(-1L, output.Value);
                    using var intruder = connection.CreateCommand(); intruder.CommandText = "not sent";
                    Assert.Throws<InvalidOperationException>(() => intruder.ExecuteNonQuery());
                    release.TrySetResult(); await executing.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(42L, output.Value);
                    Assert.Equal(new[] { 1, 2, 3, 1, 2 }, batch.BatchCommands.Cast<System.Data.Common.DbBatchCommand>().Select(c => c.RecordsAffected));
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
