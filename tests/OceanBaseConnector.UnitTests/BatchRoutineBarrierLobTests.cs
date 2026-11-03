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
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task OwnedRoutineBarrierRejectsLobOutputsBeforeSending(bool asynchronous, bool scalar)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)), lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true");
            try
            {
                using var batch = new OceanBaseBatch(connection);
                var routine = BatchItem(batch, "pkg.proc"); routine.CommandType = CommandType.StoredProcedure;
                var output = new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.Output, OceanBaseDbType = OceanBaseDbType.Blob, Value = "old" };
                routine.Parameters.Add(output);
                await Assert.ThrowsAsync<NotSupportedException>(async () =>
                {
                    if (scalar) { if (asynchronous) await batch.ExecuteScalarAsync(); else batch.ExecuteScalar(); }
                    else { if (asynchronous) await batch.ExecuteNonQueryAsync(); else batch.ExecuteNonQuery(); }
                });
                Assert.Equal("old", output.Value); Assert.Equal(-1, routine.RecordsAffected); Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task OwnedRoutineBarrierScalarKeepsLobPreflightRefusal(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await OwnedLobDrainPrepared(channel, 90, 210, anonymous: false);
                Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true");
            try
            {
                using var batch = new OceanBaseBatch(connection); BatchItem(batch, "select lob from t"); AddRoutineBarrier(batch);
                await Assert.ThrowsAsync<NotSupportedException>(async () => { if (asynchronous) await batch.ExecuteScalarAsync(); else batch.ExecuteScalar(); });
                Assert.Equal(ConnectionState.Closed, connection.State); Assert.All(batch.BatchCommands.Cast<OceanBaseBatchCommand>(), c => Assert.Equal(-1, c.RecordsAffected));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task OwnedRoutineBarrierNonQueryDrainsLobSegmentsWithoutDeliveringHandles(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await OwnedLobDrainPrepared(channel, 90, 210, anonymous: false);
                await StatementCommand(channel, 23); await OwnedLobDrainReply(channel, 210, anonymous: false, binary: true, isNull: false);
                await StatementCommand(channel, 0xa1); await OracleOutputReply(channel, false); await channel.WritePacketAsync(Ok(2));
                await StatementCommand(channel, 25);
                await OwnedLobDrainPrepared(channel, 91, 210, anonymous: false);
                await StatementCommand(channel, 23); await OwnedLobDrainReply(channel, 210, anonymous: false, binary: true, isNull: false);
                Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: true, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true");
            Internal.Session.PhysicalSession? session = null;
            connection.StateChange += (_, change) => { if (change.CurrentState == ConnectionState.Open) session = connection.GetOpenSession(); };
            try
            {
                using var batch = new OceanBaseBatch(connection); BatchItem(batch, "select lob from t"); var output = AddRoutineBarrier(batch); BatchItem(batch, "select lob from t");
                Assert.Equal(2, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                Assert.Equal(42L, output.Value); Assert.NotNull(session); Assert.True(session.RequiresDiscardOnReturn); Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task OwnedRoutineBarrierScalarRejectsDynamicLobMetadataAndDoesNotStartSuffix(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "BEGIN NULL; END;");
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("lob", 210));
                Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true");
            try
            {
                using var batch = new OceanBaseBatch(connection); var routine = BatchItem(batch, "BEGIN NULL; END;"); var suffix = BatchItem(batch, "update t set id=1");
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (asynchronous) await batch.ExecuteScalarAsync(); else batch.ExecuteScalar(); });
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Same(routine, error.BatchCommand); Assert.Equal(-1, suffix.RecordsAffected);
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task FinalRoutineBarrierLobOutputIsReadableOnlyAfterTheWholeReaderDrains(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1);
                await channel.WritePacketAsync(OracleExecuteHeader(1, 1, 4)); await channel.WritePacketAsync(Column("p", 210)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(Column("p", 210)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0 }.Concat(WithLength(InlineLob(new byte[] { 42 }))).ToArray());
                await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Ok());
                await StatementCommand(channel, 25); channel.BeginCommand(); await Query(channel, "after drain"); await channel.WritePacketAsync(Ok(7));
                Assert.Equal(0, await channelStreamRead(channel));
            }, lobLocators: true, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                var routine = BatchItem(batch, "pkg.proc"); routine.CommandType = CommandType.StoredProcedure;
                var output = new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.Output, OceanBaseDbType = OceanBaseDbType.Blob, Value = "old" };
                routine.Parameters.Add(output);
                using var reader = asynchronous ? await batch.ExecuteReaderAsync(CommandBehavior.CloseConnection) : batch.ExecuteReader(CommandBehavior.CloseConnection);
                using var lob = Assert.IsType<OceanBaseLob>(output.Value);
                Assert.Throws<InvalidOperationException>(() => lob.OpenRead());
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                using (var source = lob.OpenRead()) Assert.Equal(42, source.ReadByte());
                using var stale = new OceanBaseLob(lob.Context, lob.BindingBytes(), false);
                using (var after = connection.CreateCommand()) { after.CommandText = "after drain"; Assert.Equal(7, await after.ExecuteNonQueryAsync()); }
                if (asynchronous) await reader.CloseAsync(); else reader.Close();
                Assert.Equal(ConnectionState.Closed, connection.State); Assert.Throws<InvalidOperationException>(() => stale.OpenRead());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task MixedRoutineScalarFailureDisposesCapturedComplexLobsBeforeUnsentBarrier(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, scalar: "BLOB");
                await PrepareComplexLobResult(channel, 90); await PrepareComplexLobResult(channel, 91);
                await StatementCommand(channel, 23); await StatementCommand(channel, 23);
                await ReplyComplexLobResult(channel, ComplexLobPair(false, InlineLob(new byte[] { 2 })));
                channel.BeginResponse(); await channel.WritePacketAsync(Error());
                await StatementCommand(channel, 25); await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync(); await connection.GetComplexTypeAsync("Owner", "Thing"); var session = connection.GetOpenSession();
                using (var batch = connection.CreateBatch())
                {
                    BatchItem(batch, "select first"); BatchItem(batch, "select second"); var barrier = BatchItem(batch, "BEGIN NULL; END;");
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? batch.ExecuteScalarAsync() : Task.FromResult(batch.ExecuteScalar()));
                    Assert.False(error.IsExecutionOutcomeUnknown); Assert.Same(batch.BatchCommands[1], error.BatchCommand); Assert.Equal(-1, barrier.RecordsAffected);
                }
                Assert.False(session.RequiresDiscardOnReturn); Assert.Equal(ConnectionState.Open, connection.State);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
