using System;
using System.Data;
using System.Threading.Tasks;
using System.Transactions;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static async Task AmbientControl(PacketChannel channel, string sql, byte status)
        {
            Assert.Equal(OceanBaseCommand.SqlPacket(sql, 3), await StatementCommand(channel, 3));
            await channel.WritePacketAsync(TimeZoneOk("+00:00", status: status));
        }

        private static async Task AmbientBegin(PacketChannel channel, bool oracle)
        {
            await AmbientControl(channel, "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED", (byte)(oracle ? 6 : 2));
            await AmbientControl(channel, "set autocommit = 0", (byte)(oracle ? 4 : 0));
        }

        private static async Task AmbientEnd(PacketChannel channel, bool oracle, bool commit)
        {
            await AmbientControl(channel, commit ? "COMMIT" : "ROLLBACK", (byte)(oracle ? 4 : 0));
            await AmbientControl(channel, "set autocommit = 1", (byte)(oracle ? 6 : 2));
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        [InlineData(false, 2)]
        [InlineData(true, 2)]
        [InlineData(false, 3)]
        [InlineData(true, 3)]
        [InlineData(false, 4)]
        [InlineData(true, 4)]
        public async Task AmbientCoordinatorReceivesConfirmedRejectedOrUnknownCommit(bool oracle, int outcome)
        {
            var server = CacheServer(oracle, async channel =>
            {
                await AmbientBegin(channel, oracle);
                if (outcome == 0) await AmbientEnd(channel, oracle, true);
                else
                {
                    Assert.Equal(OceanBaseCommand.SqlPacket("COMMIT", 3), await StatementCommand(channel, 3));
                    if (outcome == 2) return;
                    await channel.WritePacketAsync(outcome == 1 ? Error() : Ok(status: outcome == 4 ? (byte)1 : (byte)(oracle ? 4 : 0)));
                    if (outcome == 1) await AmbientEnd(channel, oracle, false);
                    if (outcome == 3) { await StatementCommand(channel, 3); await channel.WritePacketAsync(Error()); }
                }
                if (outcome == 0 || outcome == 1)
                    await AmbientControl(channel, "after ambient", (byte)(oracle ? 6 : 2));
                else Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted });
            try
            {
                await connection.OpenAsync(); connection.EnlistTransaction(transaction);
                if (outcome == 0 || outcome == 3) transaction.Commit();
                else if (outcome == 1)
                {
                    var error = Assert.Throws<TransactionAbortedException>(transaction.Commit);
                    Assert.False(Assert.IsType<OceanBaseException>(error.InnerException).IsExecutionOutcomeUnknown);
                }
                else
                {
                    var error = Assert.Throws<TransactionInDoubtException>(transaction.Commit);
                    Assert.True(Assert.IsType<OceanBaseException>(error.InnerException).IsExecutionOutcomeUnknown);
                }
                Assert.Equal(outcome == 0 || outcome == 3 ? TransactionStatus.Committed : outcome == 1 ? TransactionStatus.Aborted : TransactionStatus.InDoubt,
                    transaction.TransactionInformation.Status);
                Assert.Equal(outcome <= 1 ? ConnectionState.Open : ConnectionState.Broken, connection.State);
                if (outcome <= 1)
                {
                    using var command = connection.CreateCommand(); command.CommandText = "after ambient";
                    Assert.Equal(0, await command.ExecuteNonQueryAsync());
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AmbientPromotionIsRejectedAndRollsBackTheOriginalResource(bool oracle)
        {
            var server = CacheServer(oracle, async channel => { await AmbientBegin(channel, oracle); await AmbientEnd(channel, oracle, false); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted });
            try
            {
                await connection.OpenAsync(); connection.EnlistTransaction(transaction);
                Assert.Throws<NotSupportedException>(() => TransactionInterop.GetTransmitterPropagationToken(transaction));
                Assert.Equal(TransactionStatus.Aborted, transaction.TransactionInformation.Status);
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task AmbientLateCompletionCannotCommitOrBreakAReopenedConnection(bool oracle, bool commit)
        {
            var first = CacheServer(oracle, async channel => { await AmbientBegin(channel, oracle); await AmbientEnd(channel, oracle, false); });
            var second = CacheServer(oracle, channel => AmbientControl(channel, "new session", (byte)(oracle ? 6 : 2)));
            using var connection = new OceanBaseConnection(Configuration(first.Port));
            using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted });
            try
            {
                await connection.OpenAsync(); connection.EnlistTransaction(transaction);
                await connection.CloseAsync(); connection.ConnectionString = Configuration(second.Port); await connection.OpenAsync();
                if (commit) Assert.Throws<TransactionAbortedException>(transaction.Commit); else transaction.Rollback();
                Assert.Equal(TransactionStatus.Aborted, transaction.TransactionInformation.Status);
                using var command = connection.CreateCommand(); command.CommandText = "new session";
                Assert.Equal(0, await command.ExecuteNonQueryAsync());
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); first.Stop(); second.Stop(); }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AmbientExistingPromotableResourceRejectsEnlistmentWithoutSql(bool oracle)
        {
            var server = CacheServer(oracle, channel => AmbientControl(channel, "outside", (byte)(oracle ? 6 : 2)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted });
            try
            {
                await connection.OpenAsync(); Assert.True(transaction.EnlistPromotableSinglePhase(new OtherPromotableResource()));
                Assert.Throws<NotSupportedException>(() => connection.EnlistTransaction(transaction));
                using var command = connection.CreateCommand(); command.CommandText = "outside";
                Assert.Equal(0, await command.ExecuteNonQueryAsync());
            }
            finally { transaction.Rollback(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AmbientCloneReenlistmentIsIdempotent(bool oracle)
        {
            var server = CacheServer(oracle, async channel => { await AmbientBegin(channel, oracle); await AmbientEnd(channel, oracle, false); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted });
            try
            {
                await connection.OpenAsync(); connection.EnlistTransaction(transaction);
                using var clone = transaction.Clone(); connection.EnlistTransaction(clone);
                transaction.Rollback(); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AmbientFailedInitializationLeavesNoCoordinatorSlotOrLocalTransaction(bool oracle)
        {
            var server = CacheServer(oracle, async channel => { await StatementCommand(channel, 3); await channel.WritePacketAsync(Error()); Assert.Equal(0, await channelStreamRead(channel)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted });
            try
            {
                await connection.OpenAsync(); Assert.Throws<OceanBaseException>(() => connection.EnlistTransaction(transaction));
                Assert.Equal(ConnectionState.Broken, connection.State);
                Assert.Equal(TransactionStatus.Active, transaction.TransactionInformation.Status);
                Assert.True(transaction.EnlistPromotableSinglePhase(new OtherPromotableResource()));
                transaction.Rollback();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
