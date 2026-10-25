using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Transactions;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public Task AmbientRejectsSecondConnectionBeforeStartingItsLocalTransaction(bool asynchronous, bool automatic)
            => VerifyAmbientRejectsSecondConnection(asynchronous, automatic, oracle: true);

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public Task AmbientMySqlRejectsSecondConnectionBeforeStartingItsLocalTransaction(bool asynchronous, bool automatic)
            => VerifyAmbientRejectsSecondConnection(asynchronous, automatic, oracle: false);

        private async Task VerifyAmbientRejectsSecondConnection(bool asynchronous, bool automatic, bool oracle)
        {
            var first = CacheServer(oracle, async channel => { await AmbientBegin(channel, oracle); await AmbientEnd(channel, oracle, false); });
            var received = new List<string>();
            var second = CacheServer(oracle, async channel =>
            {
                while (true)
                {
                    byte[] packet;
                    try { channel.BeginCommand(); packet = await channel.ReadPacketAsync(); }
                    catch (ProtocolException) { break; }
                    catch (IOException) { break; }
                    Assert.Equal(3, packet[0]);
                    var sql = Encoding.UTF8.GetString(packet, 1, packet.Length - 1); received.Add(sql);
                    await channel.WritePacketAsync(Ok(status: (byte)(sql == "set autocommit = 1" ? oracle ? 6 : 2 : oracle ? 4 : 0)));
                }
            });
            using var owner = new OceanBaseConnection(Configuration(first.Port));
            using var other = new OceanBaseConnection(Configuration(second.Port));
            using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted });
            try
            {
                using (var scope = automatic ? new TransactionScope(transaction, TransactionScopeAsyncFlowOption.Enabled) : null)
                {
                    if (asynchronous) await owner.OpenAsync(); else owner.Open();
                    if (!automatic) owner.EnlistTransaction(transaction);
                    if (automatic)
                        await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? other.OpenAsync() : RunSync(other.Open));
                    else
                    {
                        if (asynchronous) await other.OpenAsync(); else other.Open();
                        using var clone = transaction.Clone();
                        Assert.Throws<NotSupportedException>(() => other.EnlistTransaction(clone));
                    }
                }
            }
            finally
            {
                if (transaction.TransactionInformation.Status == TransactionStatus.Active) transaction.Rollback();
                owner.Close(); other.Close(); first.Stop(); second.Stop();
            }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(received);
        }

        [Fact]
        public async Task AmbientClaimsTheCoordinatorSingleResourceSlot()
        {
            var server = Server(async channel => { await TransactionBegin(channel); await TransactionEnd(channel, false); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted });
            try
            {
                await connection.OpenAsync(); connection.EnlistTransaction(transaction);
                Assert.False(transaction.EnlistPromotableSinglePhase(new OtherPromotableResource()));
            }
            finally { transaction.Rollback(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task AmbientLostCommitReplyIsReportedToCoordinatorAsInDoubt()
        {
            var server = Server(async channel => { await TransactionBegin(channel); await Query(channel, "COMMIT"); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted });
            try
            {
                await connection.OpenAsync(); connection.EnlistTransaction(transaction);
                var error = Assert.Throws<TransactionInDoubtException>(transaction.Commit);
                var cause = Assert.IsType<OceanBaseException>(error.InnerException);
                Assert.True(cause.IsExecutionOutcomeUnknown);
                Assert.Equal(TransactionStatus.InDoubt, transaction.TransactionInformation.Status);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private sealed class OtherPromotableResource : IPromotableSinglePhaseNotification
        {
            public void Initialize() { }
            public byte[] Promote() => throw new NotSupportedException();
            public void Rollback(SinglePhaseEnlistment enlistment) => enlistment.Aborted();
            public void SinglePhaseCommit(SinglePhaseEnlistment enlistment) => enlistment.Committed();
        }
    }
}
