using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, "nonquery", "SET time_zone='+05:45'")]
        [InlineData(true, "nonquery", "SET time_zone='+05:45'")]
        [InlineData(false, "scalar", "ALTER TABLE t ADD v INTEGER")]
        [InlineData(true, "scalar", "ALTER TABLE t ADD v INTEGER")]
        [InlineData(false, "reader", "CREATE TABLE t2(id INTEGER)")]
        [InlineData(true, "reader", "CREATE TABLE t2(id INTEGER)")]
        [InlineData(false, "singleRow", "ALTER SESSION SET TIME_ZONE='+05:45'")]
        [InlineData(true, "singleRow", "ALTER SESSION SET TIME_ZONE='+05:45'")]
        [InlineData(false, "singleResult", "DROP TABLE t2")]
        [InlineData(true, "singleResult", "DROP TABLE t2")]
        public async Task BatchExecutionBarrierKeepsPipelinedSegmentsAndExecutionSemantics(bool asynchronous, string api, string sql)
        {
            var server = Server(async channel =>
            {
                await RoutineBarrierGroup(channel, 90);
                channel.BeginCommand(); await Query(channel, sql); await channel.WritePacketAsync(Ok(3));
                await RoutineBarrierGroup(channel, 92);
                for (byte id = 90; id < 94; id++) Assert.Equal(id, (await StatementCommand(channel, 25))[1]);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync();
                using (var batch = connection.CreateBatch())
                {
                    BatchItem(batch, "update t set id=1"); BatchItem(batch, "update t set id=2"); BatchItem(batch, sql);
                    BatchItem(batch, "update t set id=3"); BatchItem(batch, "update t set id=4");
                    if (api == "nonquery") Assert.Equal(9, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                    else if (api == "scalar") Assert.Null(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
                    else
                    {
                        var behavior = CommandBehavior.SequentialAccess | (api == "singleRow" ? CommandBehavior.SingleRow : api == "singleResult" ? CommandBehavior.SingleResult : 0);
                        using var reader = asynchronous ? await batch.ExecuteReaderAsync(behavior) : batch.ExecuteReader(behavior);
                        var results = 0;
                        do { results++; Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); }
                        while (asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                        Assert.Equal(api == "reader" ? 5 : 1, results); Assert.Equal(9, reader.RecordsAffected);
                    }
                    Assert.Equal(new[] { 1, 2, 3, 1, 2 }, batch.BatchCommands.Cast<System.Data.Common.DbBatchCommand>().Select(c => c.RecordsAffected));
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "SET time_zone='invalid'")]
        [InlineData(true, "SET time_zone='invalid'")]
        [InlineData(false, "ALTER TABLE missing ADD v INTEGER")]
        [InlineData(true, "ALTER TABLE missing ADD v INTEGER")]
        public async Task BatchExecutionBarrierFailureKeepsKnownPrefixAndDoesNotPrepareSuffix(bool asynchronous, string sql)
        {
            var server = Server(async channel =>
            {
                await RoutineBarrierGroup(channel, 90);
                channel.BeginCommand(); await Query(channel, sql); await channel.WritePacketAsync(Error());
                for (byte id = 90; id < 92; id++) Assert.Equal(id, (await StatementCommand(channel, 25))[1]);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync();
                using (var batch = connection.CreateBatch())
                {
                    BatchItem(batch, "update t set id=1"); BatchItem(batch, "update t set id=2"); var barrier = BatchItem(batch, sql);
                    BatchItem(batch, "update t set id=3");
                    var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (asynchronous) await batch.ExecuteNonQueryAsync(); else batch.ExecuteNonQuery(); });
                    Assert.Same(barrier, error.BatchCommand); Assert.False(error.IsExecutionOutcomeUnknown);
                    Assert.Equal(new[] { 1, 2, -1, -1 }, batch.BatchCommands.Cast<System.Data.Common.DbBatchCommand>().Select(c => c.RecordsAffected));
                    Assert.Equal(ConnectionState.Open, connection.State);
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
        public async Task BatchExecutionBarrierInvalidatesHeldStatementsBeforeReusingTheSameSql(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 90, 0);
                await StatementCommand(channel, 23); channel.BeginResponse(); await channel.WritePacketAsync(Ok(1));
                channel.BeginCommand(); await Query(channel, "ALTER TABLE t ADD v INTEGER"); await channel.WritePacketAsync(Ok());
                Assert.Equal(90, (await StatementCommand(channel, 25))[1]);
                await StatementCommand(channel, 22); await Prepared(channel, 91, 0);
                Assert.Equal(91, (await StatementCommand(channel, 23))[1]); channel.BeginResponse(); await channel.WritePacketAsync(Ok(1));
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Statement Cache Size=2");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                var item = BatchItem(batch, "update t set id=1");
                var epoch = connection.GetOpenSession().StatementCacheEpoch;
                var type = connection.CreateArrayType("NUMBER");
                Assert.Equal(1, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                item.CommandText = "ALTER TABLE t ADD v INTEGER";
                Assert.Equal(0, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                Assert.True(connection.GetOpenSession().StatementCacheEpoch > epoch);
                Assert.Throws<InvalidOperationException>(() => type.CreateValue(1));
                item.CommandText = "update t set id=1";
                Assert.Equal(1, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                batch.Dispose(); connection.Close();
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task BatchExecutionBarrierCloseOrCancellationDoesNotStartTheNextStatement(bool asynchronous, bool cancel)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "SET time_zone='+05:45'"); await channel.WritePacketAsync(Ok());
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                BatchItem(batch, "SET time_zone='+05:45'"); var suffix = BatchItem(batch, "update t set id=1");
                using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                if (cancel)
                {
                    batch.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { if (asynchronous) await reader.NextResultAsync(); else reader.NextResult(); });
                }
                else if (asynchronous) await reader.CloseAsync(); else reader.Close();
                Assert.Equal(-1, suffix.RecordsAffected); Assert.Equal(ConnectionState.Open, connection.State);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchExecutionBarrierRejectsStaleSuffixTypeBeforePreparing(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel);
                await RoutineBarrierGroup(channel, 90);
                channel.BeginCommand(); await Query(channel, "ALTER TYPE Owner.Thing COMPILE"); await channel.WritePacketAsync(Ok());
                for (byte id = 90; id < 92; id++) Assert.Equal(id, (await StatementCommand(channel, 25))[1]);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                using (var batch = connection.CreateBatch())
                {
                    BatchItem(batch, "update t set id=1"); BatchItem(batch, "update t set id=2"); BatchItem(batch, "ALTER TYPE Owner.Thing COMPILE");
                    var suffix = BatchItem(batch, "insert into t values(:old)");
                    suffix.Parameters.Add(new OceanBaseParameter { ParameterName = "old", ComplexType = type, Value = DBNull.Value });
                    await Assert.ThrowsAsync<InvalidOperationException>(async () => { if (asynchronous) await batch.ExecuteNonQueryAsync(); else batch.ExecuteNonQuery(); });
                    Assert.Equal(new[] { 1, 2, 0, -1 }, batch.BatchCommands.Cast<System.Data.Common.DbBatchCommand>().Select(c => c.RecordsAffected));
                    Assert.Equal(ConnectionState.Open, connection.State);
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
        public async Task BatchExecutionBarrierDrainsAnOptedInScriptBeforePreparingTheNextSegment(bool asynchronous)
        {
            const string sql = "SET time_zone='+05:45'; SELECT 'value'";
            var server = Server(async channel =>
            {
                await Query(channel, sql); await channel.WritePacketAsync(Ok(2, status: 14)); await BatchTextResult(channel, "value");
                await RoutineBarrierGroup(channel, 90);
                for (byte id = 90; id < 92; id++) Assert.Equal(id, (await StatementCommand(channel, 25))[1]);
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Allow Multi Statements=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync();
                using (var batch = connection.CreateBatch())
                {
                    BatchItem(batch, sql); BatchItem(batch, "update t set id=1"); BatchItem(batch, "update t set id=2");
                    Assert.Equal("value", asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
                    Assert.Equal(new[] { 2, 1, 2 }, batch.BatchCommands.Cast<System.Data.Common.DbBatchCommand>().Select(c => c.RecordsAffected));
                }
                connection.Close();
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
