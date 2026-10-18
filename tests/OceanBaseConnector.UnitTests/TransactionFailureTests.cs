using System;
using System.Data;
using System.IO;
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
        public async Task LocalTransactionDisposeRollsBackAndFailedRollbackDestroysSession(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel);
                await Query(channel, "ROLLBACK");
                await channel.WritePacketAsync(Error());
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                var transaction = await connection.BeginTransactionAsync();
                if (asynchronous) await transaction.DisposeAsync(); else transaction.Dispose();
                Assert.Equal(ConnectionState.Broken, connection.State);
                Assert.Null(transaction.Connection);
                transaction.Dispose();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // Read another packet only to prove the transport was destroyed, without assuming FIN over RST.
        private static async Task<int> channelStreamRead(PacketChannel channel)
        {
            try { return (await channel.ReadPacketAsync()).Length; }
            catch (ProtocolException) { return 0; }
            catch (IOException) { return 0; }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task LocalTransactionInterruptedEndHasUnknownOutcome(bool asynchronous, bool commit)
        {
            var server = Server(async channel => { await TransactionBegin(channel); await Query(channel, commit ? "COMMIT" : "ROLLBACK"); });
            using var logger = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(Configuration(server.Port), logger);
            try
            {
                await connection.OpenAsync();
                using var transaction = await connection.BeginTransactionAsync();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous
                    ? (commit ? transaction.CommitAsync() : transaction.RollbackAsync())
                    : RunSync(() => { if (commit) transaction.Commit(); else transaction.Rollback(); }));
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State);
                Assert.Null(transaction.Connection);
                Assert.Throws<InvalidOperationException>(() => transaction.Commit());
                Assert.Contains(logger.Entries, entry => entry.EventId.Id == 1201 && entry.Message.Contains("OutcomeUnknown=True", StringComparison.Ordinal));
                Assert.All(logger.Entries, entry => { Assert.Null(entry.Error); Assert.DoesNotContain("secret", entry.Message); });
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static Task RunSync(Action action) { action(); return Task.CompletedTask; }

        [Fact]
        public async Task LocalTransactionCommitErrorKeepsTransactionAvailableForRollback()
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel);
                await Query(channel, "COMMIT");
                await channel.WritePacketAsync(Error());
                channel.BeginCommand();
                await TransactionEnd(channel, false);
            });
            using var logger = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(Configuration(server.Port), logger);
            try
            {
                await connection.OpenAsync();
                using var transaction = await connection.BeginTransactionAsync();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => transaction.CommitAsync());
                Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Same(connection, transaction.Connection);
                await transaction.RollbackAsync();
                Assert.Null(transaction.Connection);
                Assert.Equal(ConnectionState.Open, connection.State);
                Assert.Contains(logger.Entries, entry => entry.EventId.Id == 1201 && entry.Message.Contains("OutcomeUnknown=False", StringComparison.Ordinal));
                Assert.All(logger.Entries, entry => { Assert.Null(entry.Error); Assert.DoesNotContain("secret", entry.Message); });
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LocalTransactionConfirmedCommitSurvivesRestoreFailureAndThrowingLogger(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel);
                await Query(channel, "COMMIT");
                await channel.WritePacketAsync(Ok(status: 4));
                channel.BeginCommand();
                await Query(channel, "set autocommit = 1");
                await channel.WritePacketAsync(Error());
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var logger = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(Configuration(server.Port), logger);
            try
            {
                await connection.OpenAsync();
                using var transaction = await connection.BeginTransactionAsync();
                logger.ThrowOnLog = true;
                if (asynchronous) await transaction.CommitAsync(); else transaction.Commit();
                Assert.Null(transaction.Connection);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task LocalTransactionCommitLogsAfterDetachingTransactionAndDoesNotLeakServerText()
        {
            var server = Server(async channel => { await TransactionBegin(channel); await TransactionEnd(channel, true); });
            using var logger = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(Configuration(server.Port), logger);
            try
            {
                await connection.OpenAsync();
                using var transaction = await connection.BeginTransactionAsync();
                var observed = false;
                logger.OnLog = id =>
                {
                    if (id.Name == "TransactionCommitCompleted")
                    {
                        observed = transaction.Connection == null;
                        Assert.Throws<InvalidOperationException>(() => transaction.Commit());
                    }
                };
                await transaction.CommitAsync();
                Assert.True(observed);
                Assert.All(logger.Entries, entry =>
                {
                    Assert.Null(entry.Error);
                    Assert.DoesNotContain("autocommit", entry.Message);
                    Assert.DoesNotContain("secret", entry.Message);
                });
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task LocalTransactionWrongConnectionAndPreCanceledEndDoNotSendOrEndTransaction()
        {
            var first = Server(async channel => { await TransactionBegin(channel); await TransactionEnd(channel, false); });
            var second = Server(async channel => { await Query(channel, "outside"); await channel.WritePacketAsync(Ok(1)); });
            using var owner = new OceanBaseConnection(Configuration(first.Port));
            using var other = new OceanBaseConnection(Configuration(second.Port));
            try
            {
                await owner.OpenAsync(); await other.OpenAsync();
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await owner.BeginTransactionAsync(canceled.Token));
                Assert.Throws<NotSupportedException>(() => owner.BeginTransaction(IsolationLevel.RepeatableRead));
                using var transaction = await owner.BeginTransactionAsync();
                using var command = other.CreateCommand(); command.CommandText = "outside"; command.Transaction = transaction;
                await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteNonQueryAsync());
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.CommitAsync(canceled.Token));
                Assert.Same(owner, transaction.Connection);
                command.Transaction = null;
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
                await transaction.RollbackAsync();
            }
            finally { owner.Close(); other.Close(); first.Stop(); second.Stop(); }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task LocalTransactionBusyReaderRejectsEndAndDisposeDestroysUnreadSession()
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel);
                await Query(channel, "select pending");
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("n")); await channel.WritePacketAsync(Eof(5));
                await channel.WritePacketAsync(new byte[] { 1, 49 });
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                var transaction = await connection.BeginTransactionAsync();
                using var command = connection.CreateCommand(); command.CommandText = "select pending"; command.Transaction = transaction;
                using var reader = await command.ExecuteReaderAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
                await transaction.DisposeAsync();
                Assert.Equal(ConnectionState.Broken, connection.State);
                Assert.Null(transaction.Connection);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public async Task LocalTransactionInvalidControlReplyDestroysSession(int variant)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel); await Query(channel, "COMMIT");
                await channel.WritePacketAsync(variant == 0 ? new byte[] { 1 } : Ok(status: variant == 1 ? (byte)12 : (byte)5));
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var transaction = await connection.BeginTransactionAsync();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => transaction.CommitAsync());
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public sealed class TransactionIsolationTests
    {
        [Theory]
        [InlineData(OceanBaseCompatibilityMode.MySql, IsolationLevel.Unspecified, IsolationLevel.RepeatableRead, "REPEATABLE READ")]
        [InlineData(OceanBaseCompatibilityMode.MySql, IsolationLevel.ReadUncommitted, IsolationLevel.ReadUncommitted, "READ UNCOMMITTED")]
        [InlineData(OceanBaseCompatibilityMode.MySql, IsolationLevel.ReadCommitted, IsolationLevel.ReadCommitted, "READ COMMITTED")]
        [InlineData(OceanBaseCompatibilityMode.MySql, IsolationLevel.RepeatableRead, IsolationLevel.RepeatableRead, "REPEATABLE READ")]
        [InlineData(OceanBaseCompatibilityMode.MySql, IsolationLevel.Serializable, IsolationLevel.Serializable, "SERIALIZABLE")]
        [InlineData(OceanBaseCompatibilityMode.Oracle, IsolationLevel.Unspecified, IsolationLevel.ReadCommitted, "READ COMMITTED")]
        [InlineData(OceanBaseCompatibilityMode.Oracle, IsolationLevel.ReadCommitted, IsolationLevel.ReadCommitted, "READ COMMITTED")]
        [InlineData(OceanBaseCompatibilityMode.Oracle, IsolationLevel.Serializable, IsolationLevel.Serializable, "SERIALIZABLE")]
        public void SupportedLevelsMapPerMode(OceanBaseCompatibilityMode mode, IsolationLevel requested, IsolationLevel actual, string sql)
        {
            var plan = OceanBaseConnection.TransactionIsolation(mode, requested);
            Assert.Equal(actual, plan.Level);
            Assert.Equal("SET SESSION TRANSACTION ISOLATION LEVEL " + sql, plan.Sql);
        }

        [Theory]
        [InlineData(OceanBaseCompatibilityMode.Oracle, IsolationLevel.ReadUncommitted)]
        [InlineData(OceanBaseCompatibilityMode.Oracle, IsolationLevel.RepeatableRead)]
        [InlineData(OceanBaseCompatibilityMode.Oracle, IsolationLevel.Snapshot)]
        [InlineData(OceanBaseCompatibilityMode.MySql, IsolationLevel.Snapshot)]
        [InlineData(OceanBaseCompatibilityMode.MySql, IsolationLevel.Chaos)]
        [InlineData(OceanBaseCompatibilityMode.MySql, (IsolationLevel)987)]
        public void UnsupportedLevelsAreRejected(OceanBaseCompatibilityMode mode, IsolationLevel level) =>
            Assert.Throws<NotSupportedException>(() => OceanBaseConnection.TransactionIsolation(mode, level));
    }
}
