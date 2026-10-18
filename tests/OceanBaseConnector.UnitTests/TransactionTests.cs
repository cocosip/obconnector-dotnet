using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static async Task TransactionBegin(PacketChannel channel, string level = "READ COMMITTED")
        {
            await Query(channel, "SET SESSION TRANSACTION ISOLATION LEVEL " + level);
            await channel.WritePacketAsync(Ok());
            channel.BeginCommand();
            await Query(channel, "set autocommit = 0");
            await channel.WritePacketAsync(Ok(status: 4));
            channel.BeginCommand();
        }

        private static async Task TransactionEnd(PacketChannel channel, bool commit)
        {
            await Query(channel, commit ? "COMMIT" : "ROLLBACK");
            await channel.WritePacketAsync(Ok(status: 4));
            channel.BeginCommand();
            await Query(channel, "set autocommit = 1");
            await channel.WritePacketAsync(Ok());
            channel.BeginCommand();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task LocalTransactionRequiresExplicitBindingAndEndsOnce(bool asynchronous, bool commit)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel);
                await Query(channel, "update sample");
                await channel.WritePacketAsync(Ok(1, 5));
                channel.BeginCommand();
                await TransactionEnd(channel, commit);
                await Query(channel, "after transaction");
                await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var transaction = asynchronous ? await connection.BeginTransactionAsync() : connection.BeginTransaction();
                Assert.Equal(IsolationLevel.ReadCommitted, transaction.IsolationLevel);
                Assert.Same(connection, transaction.Connection);
                Assert.Throws<InvalidOperationException>(() => connection.BeginTransaction());
                using var command = connection.CreateCommand();
                command.CommandText = "update sample";
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                await Assert.ThrowsAsync<InvalidOperationException>(() => command.PrepareAsync());
                command.Transaction = transaction;
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                if (asynchronous)
                {
                    if (commit) await transaction.CommitAsync(); else await transaction.RollbackAsync();
                }
                else { if (commit) transaction.Commit(); else transaction.Rollback(); }
                Assert.Null(transaction.Connection);
                Assert.Throws<InvalidOperationException>(() => transaction.Commit());
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                command.Transaction = null;
                command.CommandText = "after transaction";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LocalTransactionCloseRollsBackEvenInsideAmbientScope(bool asynchronous)
        {
            var server = Server(async channel => { await TransactionBegin(channel); await TransactionEnd(channel, false); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var transaction = await connection.BeginTransactionAsync();
                using (var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
                {
                    Assert.Throws<NotSupportedException>(() => transaction.Commit());
                    await Assert.ThrowsAsync<NotSupportedException>(() => transaction.RollbackAsync());
                    Assert.Throws<NotSupportedException>(() => connection.BeginTransaction());
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "forbidden";
                    await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteNonQueryAsync());
                    await Assert.ThrowsAsync<NotSupportedException>(() => command.PrepareAsync());
                    if (asynchronous) await connection.CloseAsync(); else connection.Close();
                }
                Assert.Null(transaction.Connection);
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
