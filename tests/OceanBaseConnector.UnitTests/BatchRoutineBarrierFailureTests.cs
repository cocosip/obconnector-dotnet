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
        private static OceanBaseParameter AddRoutineBarrier(System.Data.Common.DbBatch batch)
        {
            var routine = BatchItem(batch, "pkg.proc"); routine.CommandType = CommandType.StoredProcedure;
            routine.Parameters.Add(new OceanBaseParameter { ParameterName = "input", Value = 7L });
            var output = new OceanBaseParameter { ParameterName = "output", Direction = ParameterDirection.Output, Value = -1L };
            routine.Parameters.Add(output); return output;
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task BatchRoutineBarrierFailurePreservesKnownPrefixAndUnsentSuffix(bool asynchronous, bool postflight)
        {
            var server = Server(async channel =>
            {
                await RoutineBarrierGroup(channel, 90);
                await StatementCommand(channel, 0xa1); await OracleOutputReply(channel, false);
                await channel.WritePacketAsync(postflight ? Ok(3) : Error());
                Assert.Equal(42, (await StatementCommand(channel, 25))[1]);
                for (var i = 90; i < 92; i++) Assert.Equal(i, (await StatementCommand(channel, 25))[1]);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync();
                using (var batch = connection.CreateBatch())
                {
                    BatchItem(batch, "update t set id=1"); BatchItem(batch, "update t set id=2");
                    var output = AddRoutineBarrier(batch);
                    var suffix = BatchItem(batch, "update t set id=:array");
                    suffix.Parameters.Add(new OceanBaseParameter { ParameterName = "array", ComplexType = connection.CreateArrayType("NUMBER"), Value = DBNull.Value });
                    var execution = Record.ExceptionAsync(async () => { if (asynchronous) await batch.ExecuteNonQueryAsync(); else batch.ExecuteNonQuery(); });
                    var error = await execution.WaitAsync(TimeSpan.FromSeconds(10));
                    if (postflight) Assert.IsType<NotSupportedException>(error);
                    else
                    {
                        var dbError = Assert.IsType<OceanBaseException>(error);
                        Assert.False(dbError.IsExecutionOutcomeUnknown); Assert.Equal(1064, dbError.ServerErrorCode);
                        Assert.Same(batch.BatchCommands[2], dbError.BatchCommand);
                    }
                    Assert.Equal(postflight ? 42L : -1L, output.Value);
                    Assert.Equal(new[] { 1, 2, postflight ? 3 : -1, -1 }, batch.BatchCommands.Cast<System.Data.Common.DbBatchCommand>().Select(c => c.RecordsAffected));
                    Assert.Equal(ConnectionState.Open, connection.State);
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task BatchRoutineOnlyReaderReleasesReservationAfterAllBarriersAndCloseDoesNotStartSuffix(bool asynchronous, bool earlyClose)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1); await OracleOutputReply(channel, false); await channel.WritePacketAsync(Ok(3));
                Assert.Equal(42, (await StatementCommand(channel, 25))[1]);
                if (!earlyClose)
                {
                    await StatementCommand(channel, 0xa1); await OracleOutputReply(channel, false); await channel.WritePacketAsync(Ok(4));
                    Assert.Equal(42, (await StatementCommand(channel, 25))[1]);
                }
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                var first = AddRoutineBarrier(batch); var second = AddRoutineBarrier(batch);
                using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                Assert.Equal(42L, first.Value); Assert.Equal(-1L, second.Value);
                if (earlyClose) { if (asynchronous) await reader.CloseAsync(); else reader.Close(); }
                else
                {
                    Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                    Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                    Assert.Equal(42L, second.Value); Assert.Equal(7, reader.RecordsAffected);
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
                if (earlyClose) Assert.Equal(-1, batch.BatchCommands[1].RecordsAffected);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static async Task RoutineBarrierSelect(PacketChannel channel, byte first)
        {
            channel.BeginResponse(); await channel.WritePacketAsync(new byte[] { 1 });
            await channel.WritePacketAsync(Column("value", 8)); await channel.WritePacketAsync(Eof());
            await channel.WritePacketAsync(new byte[] { 1, first }); await channel.WritePacketAsync(new byte[] { 1, (byte)(first + 1) });
            await channel.WritePacketAsync(Eof());
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task BatchRoutineBarrierCancellationBetweenSegmentsDoesNotSendTheRoutine(bool callerToken)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 90, 0);
                await StatementCommand(channel, 23); channel.BeginResponse(); await channel.WritePacketAsync(Ok(1));
                await StatementCommand(channel, 25); channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync();
                using (var batch = connection.CreateBatch())
                {
                    BatchItem(batch, "update t set id=1"); var routine = BatchItem(batch, "BEGIN NULL; END;");
                    using var reader = await batch.ExecuteReaderAsync();
                    using var cancellation = new System.Threading.CancellationTokenSource(); cancellation.Cancel();
                    if (!callerToken) batch.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.NextResultAsync(callerToken ? cancellation.Token : default));
                    Assert.Equal(1, batch.BatchCommands[0].RecordsAffected); Assert.Equal(-1, routine.RecordsAffected); Assert.Equal(ConnectionState.Open, connection.State);
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "scalar")] [InlineData(true, "scalar")]
        [InlineData(false, "singleRow")] [InlineData(true, "singleRow")]
        [InlineData(false, "singleResult")] [InlineData(true, "singleResult")]
        public async Task BatchRoutineBarrierKeepsTheFirstScalarAndWholeBatchRowLimits(bool asynchronous, string api)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await channel.WritePacketAsync(UnsupportedPrepare());
                await StatementCommand(channel, 22); await Prepared(channel, 90, 0);
                channel.BeginCommand(); await Query(channel, "select first"); await StatementCommand(channel, 23);
                await RoutineBarrierSelect(channel, 55); channel.BeginResponse(); await channel.WritePacketAsync(Ok(1));
                await StatementCommand(channel, 0xa1); await OracleOutputReply(channel, false); await channel.WritePacketAsync(Ok(0));
                await StatementCommand(channel, 25);
                await StatementCommand(channel, 22); await channel.WritePacketAsync(UnsupportedPrepare());
                await StatementCommand(channel, 22); await Prepared(channel, 91, 0);
                channel.BeginCommand(); await Query(channel, "select last"); await StatementCommand(channel, 23);
                await RoutineBarrierSelect(channel, 57); channel.BeginResponse(); await channel.WritePacketAsync(Ok(2));
                await StatementCommand(channel, 25); await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync();
                using (var batch = connection.CreateBatch())
                {
                    BatchItem(batch, "select first"); BatchItem(batch, "update t set id=1"); var output = AddRoutineBarrier(batch);
                    BatchItem(batch, "select last"); BatchItem(batch, "update t set id=2");
                    if (api == "scalar") Assert.Equal(7L, asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
                    else
                    {
                        var behavior = CommandBehavior.SequentialAccess | (api == "singleRow" ? CommandBehavior.SingleRow : CommandBehavior.SingleResult);
                        using var reader = asynchronous ? await batch.ExecuteReaderAsync(behavior) : batch.ExecuteReader(behavior);
                        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal(7L, reader.GetInt64(0));
                        if (api == "singleResult") { Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal(8L, reader.GetInt64(0)); }
                        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                        Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult()); Assert.Equal(3, reader.RecordsAffected);
                    }
                    Assert.Equal(42L, output.Value);
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
