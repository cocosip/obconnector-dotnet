using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchInsertRewriteRetainsExplicitTransactionAndNeverCommitsIt(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                channel.BeginCommand(); await TransactionBegin(channel, "REPEATABLE READ");
                await Query(channel, "insert into t values (1),(1)"); await channel.WritePacketAsync(Ok(2, 5));
                channel.BeginCommand(); await TransactionEnd(channel, false);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var transaction = asynchronous ? await connection.BeginTransactionAsync() : connection.BeginTransaction();
                using var batch = (OceanBaseBatch)connection.CreateBatch(); batch.RewriteBatchedInserts = true;
                BatchItem(batch, "insert into t values (1)"); BatchItem(batch, "insert into t values (1)");
                await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                batch.Transaction = transaction;
                Assert.Equal(2, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery()); Assert.Same(connection, transaction.Connection);
                if (asynchronous) await transaction.RollbackAsync(); else transaction.Rollback();
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchInsertRewriteKeepsUnsupportedRunsSequentialAndOrdered(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                foreach (var sql in new[] { "insert into t values (1),(1)", "insert ignore into t values (1)", "insert ignore into t values (1)", "insert into t values (1),(1)" })
                {
                    channel.BeginCommand(); await Query(channel, sql); await channel.WritePacketAsync(Ok(sql.Contains("ignore", StringComparison.Ordinal) ? (byte)1 : (byte)2, 2));
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = (OceanBaseBatch)connection.CreateBatch(); batch.RewriteBatchedInserts = true;
                foreach (var sql in new[] { "insert into t values (1)", "insert ignore into t values (1)", "insert into t values (1)" }) { BatchItem(batch, sql); BatchItem(batch, sql); }
                Assert.Equal(6, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                Assert.All(batch.BatchCommands.Cast<DbBatchCommand>(), item => Assert.Equal(1, item.RecordsAffected));
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchInsertRewriteNeverMigratesSuffixToReopenedConnection(bool asynchronous)
        {
            var first = MySqlProcedureServer(async channel => { channel.BeginCommand(); await Query(channel, "insert into t values (1),(1)"); await channel.WritePacketAsync(Ok(2, 2)); Assert.Equal(0, await channelStreamRead(channel)); });
            var second = MySqlProcedureServer(async channel => { channel.BeginCommand(); await Query(channel, "after reopen"); await channel.WritePacketAsync(Ok(1, 2)); });
            using var log = new RecordingLoggerFactory(); using var connection = new OceanBaseConnection(Configuration(first.Port), log);
            try
            {
                await connection.OpenAsync(); using var batch = (OceanBaseBatch)connection.CreateBatch(); batch.RewriteBatchedInserts = true;
                var one = BatchItem(batch, "insert into t values (1)"); var two = BatchItem(batch, one.CommandText); var suffix = BatchItem(batch, "never");
                log.OnLog = id => { if (id.Id != 1100) return; log.OnLog = null; connection.Close(); connection.ConnectionString = Configuration(second.Port); connection.Open(); };
                await Assert.ThrowsAsync<InvalidOperationException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                Assert.Equal(1, one.RecordsAffected); Assert.Equal(1, two.RecordsAffected); Assert.Equal(-1, suffix.RecordsAffected);
                Assert.Equal(ConnectionState.Open, connection.State); using var after = connection.CreateCommand(); after.CommandText = "after reopen"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { log.OnLog = null; connection.Close(); first.Stop(); second.Stop(); }
            await first.Completion.WaitAsync(TimeSpan.FromSeconds(10)); await second.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, "cancel")]
        [InlineData(true, false, "cancel")]
        [InlineData(false, true, "cancel")]
        [InlineData(true, true, "cancel")]
        [InlineData(false, false, "dispose")]
        [InlineData(true, false, "dispose")]
        [InlineData(false, true, "dispose")]
        [InlineData(true, true, "dispose")]
        [InlineData(false, false, "disposeAsync")]
        [InlineData(true, false, "disposeAsync")]
        [InlineData(false, true, "disposeAsync")]
        [InlineData(true, true, "disposeAsync")]
        [InlineData(false, true, "timeout")]
        [InlineData(true, true, "timeout")]
        public async Task BatchInsertRewriteStopsPendingPrepareOrExecution(bool asynchronous, bool sent, string stop)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = MySqlProcedureServer(async channel =>
            {
                var prepare = await StatementCommand(channel, 22);
                Assert.Equal("insert into t values (?),(?)", Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                if (sent) { await Prepared(channel, 182, 2); await StatementCommand(channel, 23); }
                received.SetResult(); Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var caller = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var batch = (OceanBaseBatch)connection.CreateBatch(); batch.RewriteBatchedInserts = true;
                if (stop == "timeout") batch.Timeout = 1;
                var one = BatchItem(batch, "insert into t values (?)"); one.Parameters.Add(new OceanBaseParameter { Value = 7 });
                var two = BatchItem(batch, one.CommandText); two.Parameters.Add(new OceanBaseParameter { Value = 8 }); BatchItem(batch, "never");
                var executing = asynchronous ? batch.ExecuteNonQueryAsync(caller.Token) : Task.Run(batch.ExecuteNonQuery);
                await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Throws<InvalidOperationException>(() => batch.RewriteBatchedInserts = false);
                Assert.Throws<InvalidOperationException>(() => one.CommandText = "changed");
                Assert.Throws<InvalidOperationException>(() => connection.GetSchema());
                if (stop == "cancel") { if (asynchronous) caller.Cancel(); else batch.Cancel(); }
                else if (stop == "dispose") batch.Dispose();
                else if (stop == "disposeAsync") await batch.DisposeAsync();
                var error = await Record.ExceptionAsync(() => executing.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.NotNull(error);
                if (sent)
                {
                    var unknown = Assert.IsType<OceanBaseException>(error); Assert.True(unknown.IsExecutionOutcomeUnknown);
                    Assert.Null(unknown.BatchCommand); Assert.Equal(new[] { one, two }, unknown.BatchCommandGroup);
                    if (stop == "timeout") Assert.IsType<TimeoutException>(unknown.InnerException);
                    if (stop == "cancel" && asynchronous) Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(unknown.InnerException).CancellationToken);
                }
                else Assert.IsAssignableFrom<OperationCanceledException>(error);
                Assert.Equal(-1, one.RecordsAffected); Assert.Equal(-1, two.RecordsAffected); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchInsertRewriteKeepsStreamsAsSequentialBoundaries(bool asynchronous)
        {
            using var source = new MemoryStream(new byte[] { 11, 12, 13 });
            var server = MySqlProcedureServer(async channel =>
            {
                for (byte i = 0; i < 2; i++)
                {
                    var prepare = await StatementCommand(channel, 22); Assert.Equal("insert into t values (?)", Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                    await Prepared(channel, (byte)(183 + i), 1);
                    var upload = await StatementCommand(channel, 24); Assert.Equal(i == 0 ? 10 : 7, upload.Length);
                    await StatementCommand(channel, 23); await channel.WritePacketAsync(Ok(1, 2));
                }
                var merged = await StatementCommand(channel, 22); Assert.Equal("insert into t values (?),(?)", Encoding.UTF8.GetString(merged, 1, merged.Length - 1));
                await Prepared(channel, 185, 2); await StatementCommand(channel, 23); await channel.WritePacketAsync(Ok(2, 2));
                await StatementCommand(channel, 25);
                await StatementCommand(channel, 25); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = (OceanBaseBatch)connection.CreateBatch(); batch.RewriteBatchedInserts = true;
                foreach (var value in new object[] { source, source, 7, 8 }) BatchItem(batch, "insert into t values (?)").Parameters.Add(new OceanBaseParameter { Value = value });
                Assert.Equal(4, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                Assert.Equal(3, source.Position); Assert.True(source.CanRead); Assert.All(batch.BatchCommands.Cast<DbBatchCommand>(), item => Assert.Equal(1, item.RecordsAffected));
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "nonquery", false)]
        [InlineData(true, "nonquery", false)]
        [InlineData(false, "reader", true)]
        [InlineData(true, "reader", true)]
        [InlineData(false, "scalar", true)]
        [InlineData(true, "scalar", true)]
        [InlineData(false, "prepare", true)]
        [InlineData(true, "prepare", true)]
        [InlineData(false, "oracle", true)]
        [InlineData(true, "oracle", true)]
        public async Task BatchInsertRewriteRetainsSequentialPublicPaths(bool asynchronous, string api, bool enabled)
        {
            async Task Peer(PacketChannel channel)
            {
                for (byte i = 0; i < 2; i++)
                {
                    var request = await StatementCommand(channel, api == "prepare" ? (byte)22 : (byte)3);
                    Assert.Equal("insert into t values (1)", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                    if (api == "prepare") await Prepared(channel, (byte)(186 + i), 0); else await channel.WritePacketAsync(Ok(1, api == "oracle" ? (byte)6 : (byte)2));
                }
                if (api == "prepare") { await StatementCommand(channel, 25); await StatementCommand(channel, 25); }
            }
            var server = api == "oracle" ? Server(Peer) : MySqlProcedureServer(Peer);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = (OceanBaseBatch)connection.CreateBatch(); batch.RewriteBatchedInserts = enabled;
                BatchItem(batch, "insert into t values (1)"); BatchItem(batch, "insert into t values (1)");
                if (api == "reader")
                {
                    using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                    if (asynchronous) { do { while (await reader.ReadAsync()) { } } while (await reader.NextResultAsync()); }
                    else { do { while (reader.Read()) { } } while (reader.NextResult()); }
                    Assert.Equal(2, reader.RecordsAffected);
                }
                else if (api == "scalar") Assert.Null(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
                else if (api == "prepare") { if (asynchronous) await batch.PrepareAsync(); else batch.Prepare(); }
                else Assert.Equal(2, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
