using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static async Task BatchTextResult(PacketChannel channel, string value, byte status = 6)
        {
            await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("value", 253));
            await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(new[] { (byte)value.Length }.Concat(System.Text.Encoding.UTF8.GetBytes(value)).ToArray());
            await channel.WritePacketAsync(Eof(status));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task BatchReaderStreamsAcrossStatementAndResultBoundaries(bool asynchronous, bool sequential)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "first"); await BatchTextResult(channel, "alpha", 14); await channel.WritePacketAsync(Ok(2)); channel.BeginCommand();
                await Query(channel, "second"); await BatchTextResult(channel, "beta");
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); var first = BatchItem(batch, "first"); var second = BatchItem(batch, "second");
                await using var reader = asynchronous ? await batch.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default) : batch.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.Equal("value", reader.GetColumnSchema().Single().ColumnName); Assert.Equal(typeof(string), reader.GetFieldType(0));
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                using (var text = reader.GetTextReader(0)) Assert.Equal("alpha", asynchronous ? await text.ReadToEndAsync() : text.ReadToEnd());
                Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult()); Assert.Equal(0, reader.FieldCount);
                Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal("beta", reader.GetString(0));
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.Equal(2, first.RecordsAffected); Assert.Equal(-1, second.RecordsAffected); Assert.Equal(2, reader.RecordsAffected);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchScalarSkipsUpdatesAndDrainsAllFollowingStatements(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "update"); await channel.WritePacketAsync(Ok(2)); channel.BeginCommand();
                await Query(channel, "select"); await BatchTextResult(channel, "scalar"); channel.BeginCommand();
                await Query(channel, "suffix"); await channel.WritePacketAsync(Ok(4));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); BatchItem(batch, "update"); BatchItem(batch, "select"); var suffix = BatchItem(batch, "suffix");
                Assert.Equal("scalar", asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar()); Assert.Equal(4, suffix.RecordsAffected);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task BatchBlocksReentrantOperationsAndSnapshotsParameterBytes(bool asynchronous, bool mysql)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "one"); await channel.WritePacketAsync(Ok(1)); channel.BeginCommand();
                await StatementCommand(channel, 22); await Prepared(channel, 90, 1);
                var execute = await StatementCommand(channel, 23); Assert.Equal(new byte[] { 2, 1, 2 }, execute.TakeLast(3)); await channel.WritePacketAsync(Ok(2));
                await StatementCommand(channel, 25);
            });
            using var log = new RecordingLoggerFactory(); using var connection = new OceanBaseConnection(Configuration(server.Port), log);
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                await using var batch = connection.CreateBatch(); BatchItem(batch, "one"); var sql = mysql ? "?" : ":p"; var two = BatchItem(batch, sql);
                var bytes = new byte[] { 1, 2 }; var parameter = new OceanBaseParameter { ParameterName = "p", Value = bytes }; two.Parameters.Add(parameter);
                Exception? nested = null; var rejected = 0;
                log.OnLog = id =>
                {
                    if (id.Id != 1100) return; log.OnLog = null;
                    bytes[0] = 9; parameter.Value = new byte[] { 8 };
                    using var other = connection.CreateCommand(); other.CommandText = "must not send";
                    nested = Record.Exception(() => other.ExecuteNonQuery());
                    foreach (Action action in new Action[] { () => batch.BatchCommands.Clear(), () => two.CommandText = "changed", () => batch.Timeout = 8, () => connection.GetSchema(), () => connection.BeginTransaction() })
                        if (Record.Exception(action) is InvalidOperationException) rejected++;
                };
                Assert.Equal(3, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                Assert.IsType<InvalidOperationException>(nested); Assert.Equal(5, rejected); Assert.Equal(sql, two.CommandText);
            }
            finally { log.OnLog = null; connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchRequiresCurrentTransactionAndRetainsItsOwnership(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel); await Query(channel, "inside"); await channel.WritePacketAsync(Ok(1, 5)); channel.BeginCommand(); await TransactionEnd(channel, false);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var transaction = await connection.BeginTransactionAsync(); using var batch = connection.CreateBatch(); BatchItem(batch, "inside");
                Assert.Throws<InvalidOperationException>(() => batch.ExecuteNonQuery()); batch.Transaction = transaction;
                Assert.Equal(1, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery()); Assert.Same(connection, transaction.Connection);
                await transaction.RollbackAsync(); Assert.Throws<InvalidOperationException>(() => batch.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task BatchEarlyReaderCloseNeverExecutesSuffix(bool asynchronous, bool unread)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "first");
                if (unread) await BatchTextResult(channel, "unread"); else await channel.WritePacketAsync(Ok(1));
                try { Assert.Equal(0, await channelStreamRead(channel)); }
                catch (IOException) when (unread) { }
                catch (ProtocolException) when (unread) { }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); BatchItem(batch, "first"); var suffix = BatchItem(batch, "never");
                var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                if (asynchronous) await reader.DisposeAsync(); else reader.Dispose();
                Assert.Equal(-1, suffix.RecordsAffected); Assert.Equal(unread ? ConnectionState.Broken : ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchPrepareReusesEachStatementAndClosesOwnedIds(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 90, 0);
                await StatementCommand(channel, 22); await Prepared(channel, 91, 0);
                for (var run = 0; run < 2; run++)
                {
                    foreach (var id in new byte[] { 90, 91 })
                    { var execute = await StatementCommand(channel, 23); Assert.Equal(id, execute[1]); await channel.WritePacketAsync(Ok(1)); }
                }
                await StatementCommand(channel, 25); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); await using var batch = connection.CreateBatch(); BatchItem(batch, "one"); BatchItem(batch, "two");
                if (asynchronous) await batch.PrepareAsync(); else batch.Prepare();
                for (var i = 0; i < 2; i++) Assert.Equal(2, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task BatchReaderAsyncCloseReleasesReservationBeforeAwaitingTransactionRollback()
        {
            var rollback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await TransactionBegin(channel); await Query(channel, "inside"); await channel.WritePacketAsync(Ok(1, 5)); channel.BeginCommand();
                await Query(channel, "ROLLBACK"); rollback.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await channel.WritePacketAsync(Ok(status: 4)); channel.BeginCommand(); await Query(channel, "set autocommit = 1"); await channel.WritePacketAsync(Ok());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var transaction = await connection.BeginTransactionAsync(); using var batch = connection.CreateBatch(); batch.Transaction = transaction; BatchItem(batch, "inside");
                var reader = await batch.ExecuteReaderAsync(CommandBehavior.CloseConnection); var cleanup = reader.DisposeAsync().AsTask();
                await rollback.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(cleanup.IsCompleted); release.SetResult(); await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(ConnectionState.Closed, connection.State); Assert.Null(transaction.Connection);
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
