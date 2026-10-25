using System;
using System.Data;
using System.Threading.Tasks;
using System.Transactions;
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
        public async Task AmbientLateCallbackCannotBreakTheNextPoolBorrower(bool oracle, bool commit)
        {
            var server = CacheServer(oracle, async channel =>
            {
                await AmbientBegin(channel, oracle); await AmbientEnd(channel, oracle, false);
                await StatementCommand(channel, 31); await channel.WritePacketAsync(TimeZoneOk("+00:00", status: (byte)(oracle ? 6 : 2)));
                if (oracle) await TestInfrastructure.ServerIdentity.ReplyOracleAutocommitAsync(channel);
                await StatementCommand(channel, 14); await channel.WritePacketAsync(TimeZoneOk("+00:00", status: (byte)(oracle ? 6 : 2)));
                await AmbientControl(channel, "next borrower", (byte)(oracle ? 6 : 2));
                await StatementCommand(channel, 31); await channel.WritePacketAsync(TimeZoneOk("+00:00", status: (byte)(oracle ? 6 : 2)));
                if (oracle) await TestInfrastructure.ServerIdentity.ReplyOracleAutocommitAsync(channel);
            });
            var options = Configuration(server.Port) + ";Pooling=true;Max Pool Size=1";
            using var owner = new OceanBaseConnection(options);
            using var borrower = new OceanBaseConnection(options);
            using var transaction = new CommittableTransaction(new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted });
            try
            {
                await owner.OpenAsync(); var session = owner.GetOpenSession(); owner.EnlistTransaction(transaction); await owner.CloseAsync();
                await borrower.OpenAsync(); Assert.Same(session, borrower.GetOpenSession());
                if (commit) Assert.Throws<TransactionAbortedException>(transaction.Commit); else transaction.Rollback();
                using var command = borrower.CreateCommand(); command.CommandText = "next borrower";
                Assert.Equal(0, await command.ExecuteNonQueryAsync());
                Assert.Equal(ConnectionState.Open, borrower.State); await borrower.CloseAsync();
            }
            finally { owner.Close(); borrower.Close(); OceanBaseConnection.ClearPool(owner); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
