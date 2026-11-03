using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task RoutineBarrierCursorReaderKeepsReservationAndCloseWindow(bool asynchronous, bool owned)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1); await RefCursorOutput(channel);
                await RoutineBarrierGroup(channel, 90);
                Assert.Equal(91, (await StatementCommand(channel, 28))[1]);
                await CursorMetadata(channel, 2); await channel.WritePacketAsync(CursorRow(7));
                await channel.WritePacketAsync(CursorTerminator(130, false));
                if (owned) Assert.Equal(0, await channelStreamRead(channel));
                else
                {
                    Assert.Equal(91, (await StatementCommand(channel, 25))[1]);
                    Assert.Equal(42, (await StatementCommand(channel, 25))[1]);
                    for (byte id = 90; id < 92; id++) Assert.Equal(id, (await StatementCommand(channel, 25))[1]);
                    channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
                }
            }, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                if (!owned) await connection.OpenAsync();
                using var batch = owned ? new OceanBaseBatch(connection) : (OceanBaseBatch)connection.CreateBatch();
                var routine = BatchItem(batch, "pkg.open_cursor"); routine.CommandType = CommandType.StoredProcedure;
                var output = new OceanBaseParameter { ParameterName = "c", Direction = ParameterDirection.ReturnValue, OceanBaseDbType = OceanBaseDbType.RefCursor };
                routine.Parameters.Add(output); BatchItem(batch, "update t set id=1"); BatchItem(batch, "update t set id=2");
                using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                using var cursor = Assert.IsType<OceanBaseRefCursor>(output.Value);
                await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? cursor.GetDataReaderAsync() : Task.FromResult(cursor.GetDataReader()));
                Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                using (var fetched = asynchronous ? await cursor.GetDataReaderAsync(2) : cursor.GetDataReader(2))
                {
                    Assert.True(asynchronous ? await fetched.ReadAsync() : fetched.Read()); Assert.Equal(7L, fetched.GetInt64(0));
                    Assert.False(asynchronous ? await fetched.ReadAsync() : fetched.Read()); Assert.False(fetched.NextResult());
                }
                if (asynchronous) await reader.DisposeAsync(); else reader.Dispose();
                if (owned)
                {
                    Assert.Equal(ConnectionState.Closed, connection.State);
                    Assert.Throws<InvalidOperationException>(() => cursor.GetDataReader());
                }
                else
                {
                    cursor.Dispose(); batch.Dispose(); using var after = connection.CreateCommand(); after.CommandText = "after";
                    Assert.Equal(1, await after.ExecuteNonQueryAsync());
                }
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task RoutineBarrierCursorNullAndTerminalErrorDoNotRetainOwnedConnection(bool asynchronous, bool error)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1);
                await RefCursorOutput(channel, row: error ? null : new byte[] { 0, 4 }, error: error);
                Assert.Equal(0, await channelStreamRead(channel));
            }, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true");
            try
            {
                using var batch = new OceanBaseBatch(connection);
                var routine = BatchItem(batch, "pkg.open_cursor"); routine.CommandType = CommandType.StoredProcedure;
                var output = new OceanBaseParameter { ParameterName = "c", Direction = ParameterDirection.Output, OceanBaseDbType = OceanBaseDbType.RefCursor, Value = "old" };
                routine.Parameters.Add(output);
                if (error)
                {
                    var failure = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                    Assert.False(failure.IsExecutionOutcomeUnknown); Assert.Equal("old", output.Value);
                }
                else { Assert.Equal(0, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery()); Assert.Equal(DBNull.Value, output.Value); }
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            catch (Exception failure) when (server.Completion.IsFaulted) { throw new AggregateException(failure, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(true, 163, true)] [InlineData(true, 6, true)]
        [InlineData(true, 8, false)] [InlineData(false, 163, false)]
        public void RoutineBarrierCursorMetadataRequiresExplicitMatchingDeclaration(bool declared, byte type, bool accepted)
        {
            var parameters = new OceanBaseParameterCollection();
            parameters.Add(new OceanBaseParameter { ParameterName = "c", Direction = ParameterDirection.Output, OceanBaseDbType = declared ? OceanBaseDbType.RefCursor : null });
            var plan = OracleCommandPlan.Create("pkg.open_cursor", CommandType.StoredProcedure, parameters);
            plan.RequireBatchOutputs(true, allowRoutineOutputs: true);
            var column = ResultColumn.Parse(Column("c", type), OceanBaseCompatibilityMode.Oracle);
            if (accepted) plan.ValidateOutputColumn(0, column);
            else if (declared) Assert.Throws<ProtocolException>(() => plan.ValidateOutputColumn(0, column));
            else Assert.Throws<NotSupportedException>(() => plan.ValidateOutputColumn(0, column));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public async Task RoutineBarrierPublishedCursorSurvivesLaterErrorAndOwnedDisposalInvalidatesUnreadCursor(bool asynchronous, bool suffixError)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1); await RefCursorOutput(channel);
                if (suffixError)
                {
                    await StatementCommand(channel, 22); await Prepared(channel, 90, 0);
                    await StatementCommand(channel, 23); await channel.WritePacketAsync(Error());
                    Assert.Equal(91, (await StatementCommand(channel, 28))[1]);
                    await CursorMetadata(channel, 2); await channel.WritePacketAsync(CursorRow(7));
                    await channel.WritePacketAsync(CursorTerminator(130, false));
                    Assert.Equal(91, (await StatementCommand(channel, 25))[1]);
                    Assert.Equal(42, (await StatementCommand(channel, 25))[1]);
                    Assert.Equal(90, (await StatementCommand(channel, 25))[1]);
                }
                Assert.Equal(0, await channelStreamRead(channel));
            }, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true");
            try
            {
                using var batch = new OceanBaseBatch(connection);
                var routine = BatchItem(batch, "pkg.open_cursor"); routine.CommandType = CommandType.StoredProcedure;
                var output = new OceanBaseParameter { ParameterName = "c", Direction = ParameterDirection.Output, OceanBaseDbType = OceanBaseDbType.RefCursor };
                routine.Parameters.Add(output);
                if (suffixError)
                {
                    var suffix = BatchItem(batch, "update t set id=1");
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                    Assert.Same(suffix, error.BatchCommand); Assert.False(error.IsExecutionOutcomeUnknown);
                }
                else Assert.Equal(0, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                using var cursor = Assert.IsType<OceanBaseRefCursor>(output.Value);
                if (suffixError)
                {
                    using var reader = asynchronous ? await cursor.GetDataReaderAsync(2) : cursor.GetDataReader(2);
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal(7L, reader.GetInt64(0));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.False(reader.NextResult());
                    cursor.Dispose();
                }
                if (asynchronous) await batch.DisposeAsync(); else batch.Dispose();
                Assert.Equal(ConnectionState.Closed, connection.State);
                if (!suffixError) Assert.Throws<ObjectDisposedException>(() => cursor.GetDataReader());
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public static IEnumerable<object[]> RoutineCursorCases() =>
            from asynchronous in new[] { false, true }
            from owned in new[] { false, true }
            from scalar in new[] { false, true }
            select new object[] { asynchronous, owned, scalar };

        [Theory]
        [MemberData(nameof(RoutineCursorCases))]
        public async Task RoutineBarrierCursorOutputsKeepTheirSessionAndPipelinedSegments(bool asynchronous, bool owned, bool scalar)
        {
            var server = Server(async channel =>
            {
                await RoutineBarrierGroup(channel, 90);
                await StatementCommand(channel, 0xa1);
                await RefCursorOutput(channel, row: new byte[] { 0, 0, 51, 0, 0, 0 });
                await RoutineBarrierGroup(channel, 92);
                if (!owned)
                    for (byte id = 90; id < 94; id++) Assert.Equal(id, (await StatementCommand(channel, 25))[1]);
                Assert.Equal(51, (await StatementCommand(channel, 28))[1]);
                await CursorMetadata(channel, 2); await channel.WritePacketAsync(CursorRow(7));
                await channel.WritePacketAsync(CursorTerminator(130, false));
                Assert.Equal(51, (await StatementCommand(channel, 25))[1]);
                Assert.Equal(42, (await StatementCommand(channel, 25))[1]);
                if (owned)
                {
                    for (byte id = 90; id < 94; id++) Assert.Equal(id, (await StatementCommand(channel, 25))[1]);
                    Assert.Equal(0, await channelStreamRead(channel));
                }
                else { channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1)); }
            }, serverVersion: "4.4.2.2");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true;Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                if (!owned) await connection.OpenAsync();
                using var batch = owned ? new OceanBaseBatch(connection) : (OceanBaseBatch)connection.CreateBatch();
                BatchItem(batch, "update t set id=1"); BatchItem(batch, "update t set id=2");
                var routine = BatchItem(batch, "pkg.open_cursor"); routine.CommandType = CommandType.StoredProcedure;
                var output = new OceanBaseParameter { ParameterName = "c", Direction = ParameterDirection.Output, OceanBaseDbType = OceanBaseDbType.RefCursor };
                routine.Parameters.Add(output);
                BatchItem(batch, "update t set id=3"); BatchItem(batch, "update t set id=4");
                if (scalar) Assert.Null(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
                else Assert.Equal(6, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                using var cursor = Assert.IsType<OceanBaseRefCursor>(output.Value);
                Assert.Equal(ConnectionState.Open, connection.State);
                if (owned)
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? batch.ExecuteScalarAsync() : Task.FromResult(batch.ExecuteScalar()));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? batch.ExecuteReaderAsync() : Task.FromResult(batch.ExecuteReader()));
                    batch.RewriteBatchedInserts = true;
                    await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                }
                else batch.Dispose();
                using (var reader = asynchronous ? await cursor.GetDataReaderAsync(2) : cursor.GetDataReader(2))
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal(7L, reader.GetInt64(0));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.False(reader.NextResult());
                }
                if (asynchronous) await cursor.DisposeAsync(); else cursor.Dispose();
                if (owned)
                {
                    if (asynchronous) await batch.DisposeAsync(); else batch.Dispose();
                    Assert.Equal(ConnectionState.Closed, connection.State);
                }
                else { using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync()); }
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
