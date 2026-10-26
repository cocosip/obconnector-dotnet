using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Fact]
        public async Task BatchInsertRewriteRetainsNullSqlValidationWithoutSending()
        {
            var server = MySqlProcedureServer(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = (OceanBaseBatch)connection.CreateBatch(); batch.RewriteBatchedInserts = true;
                BatchItem(batch, null!); BatchItem(batch, null!);
                await Assert.ThrowsAsync<InvalidOperationException>(() => batch.ExecuteNonQueryAsync());
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchInsertRewriteFailureKeepsExactGroupAndConfirmedPrefix(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                channel.BeginCommand(); await Query(channel, "prefix"); await channel.WritePacketAsync(Ok(2, 2));
                channel.BeginCommand(); await Query(channel, "insert into t values (1),(1)"); await channel.WritePacketAsync(Error());
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1, 2));
            });
            using var log = new RecordingLoggerFactory(); using var connection = new OceanBaseConnection(Configuration(server.Port), log);
            try
            {
                await connection.OpenAsync(); using var batch = (OceanBaseBatch)connection.CreateBatch(); batch.RewriteBatchedInserts = true;
                var prefix = BatchItem(batch, "prefix"); var one = BatchItem(batch, "insert into t values (1)");
                var two = BatchItem(batch, one.CommandText); var suffix = BatchItem(batch, "never");
                var callbacks = 0; var accepted = 0;
                log.OnLog = id =>
                {
                    if (id.Id != 1100 && id.Id != 1101) return;
                    callbacks++;
                    try { batch.RewriteBatchedInserts = true; accepted++; } catch (InvalidOperationException) { }
                    try { connection.GetSchema(); accepted++; } catch (InvalidOperationException) { }
                };
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                log.OnLog = null;
                Assert.Equal(2, callbacks); Assert.Equal(0, accepted); Assert.False(error.IsExecutionOutcomeUnknown); Assert.Null(error.BatchCommand);
                Assert.Equal(new[] { one, two }, error.BatchCommandGroup); Assert.Equal(2, prefix.RecordsAffected);
                Assert.Equal(-1, one.RecordsAffected); Assert.Equal(-1, two.RecordsAffected); Assert.Equal(-1, suffix.RecordsAffected);
                var collection = Assert.IsAssignableFrom<IList<DbBatchCommand>>(error.BatchCommandGroup);
                Assert.Throws<NotSupportedException>(() => collection[0] = suffix);
                batch.BatchCommands.Clear(); Assert.Equal(new[] { one, two }, error.BatchCommandGroup);
                BatchItem(batch, "after"); Assert.Equal(1, await batch.ExecuteNonQueryAsync());
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { log.OnLog = null; connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 5)]
        [InlineData(true, 5)]
        public async Task BatchInsertRewriteRetainsAggregateWhenIndividualCountsAreUnknown(bool asynchronous, byte groupTotal)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                channel.BeginCommand(); await Query(channel, "insert into t values (1),(1)"); await channel.WritePacketAsync(Ok(groupTotal, 2));
                channel.BeginCommand(); await Query(channel, "suffix"); await channel.WritePacketAsync(Ok(3, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = (OceanBaseBatch)connection.CreateBatch(); batch.RewriteBatchedInserts = true;
                var one = BatchItem(batch, "insert into t values (1)"); var two = BatchItem(batch, one.CommandText); var suffix = BatchItem(batch, "suffix");
                Assert.Equal(groupTotal + 3, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                Assert.Equal(-1, one.RecordsAffected); Assert.Equal(-1, two.RecordsAffected); Assert.Equal(3, suffix.RecordsAffected);
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchInsertRewritePreservesPrefixBeforeInvalidParameterWithoutSendingSuffix(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 189, 2);
                await StatementCommand(channel, 23); await channel.WritePacketAsync(Ok(2, 2));
                await StatementCommand(channel, 25); channel.BeginCommand(); await Query(channel, "after invalid"); await channel.WritePacketAsync(Ok(1, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = (OceanBaseBatch)connection.CreateBatch(); batch.RewriteBatchedInserts = true;
                foreach (var value in new object[] { 7, 8, new object(), 9 }) BatchItem(batch, "insert into t values (?)").Parameters.Add(new OceanBaseParameter { Value = value });
                await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                Assert.Equal(new[] { 1, 1, -1, -1 }, batch.BatchCommands.Cast<DbBatchCommand>().Select(item => item.RecordsAffected));
                using var after = connection.CreateCommand(); after.CommandText = "after invalid"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
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
        public async Task BatchInsertRewriteAttributesPrepareErrorAndPartialSend(bool asynchronous, bool partialSend)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                if (partialSend) Assert.Equal(0, await channelStreamRead(channel));
                else
                {
                    await StatementCommand(channel, 22); await channel.WritePacketAsync(Error());
                    channel.BeginCommand(); await Query(channel, "after prepare error"); await channel.WritePacketAsync(Ok(1, 2));
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = (OceanBaseBatch)connection.CreateBatch(); batch.RewriteBatchedInserts = true;
                var one = BatchItem(batch, partialSend ? "insert into t values (1)" : "insert into t values (?)"); var two = BatchItem(batch, one.CommandText);
                if (!partialSend) { one.Parameters.Add(new OceanBaseParameter { Value = 7 }); two.Parameters.Add(new OceanBaseParameter { Value = 8 }); }
                if (partialSend) InstallFaultStream(connection, new ProtocolFaultStream(Array.Empty<byte>(), 2));
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                Assert.Equal(partialSend, error.IsExecutionOutcomeUnknown); Assert.Null(error.BatchCommand); Assert.Equal(new[] { one, two }, error.BatchCommandGroup);
                Assert.Equal(-1, one.RecordsAffected); Assert.Equal(-1, two.RecordsAffected);
                if (!partialSend) { using var after = connection.CreateCommand(); after.CommandText = "after prepare error"; Assert.Equal(1, await after.ExecuteNonQueryAsync()); }
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
