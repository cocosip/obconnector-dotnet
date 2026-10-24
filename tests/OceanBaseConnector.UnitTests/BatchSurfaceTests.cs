using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Fact]
        public void BatchCollectionsEnforceOwnershipAndReleaseRemovedCommands()
        {
            using var one = new OceanBaseBatch(); using var two = new OceanBaseBatch();
            var first = BatchItem(one, "first"); var second = BatchItem(two, "second");
            Assert.Throws<ArgumentException>(() => two.BatchCommands.Add(first));
            Assert.Throws<ArgumentException>(() => one.BatchCommands[0] = second); Assert.Same(first, one.BatchCommands[0]);
            Assert.Throws<ArgumentOutOfRangeException>(() => one.BatchCommands.Insert(2, new OceanBaseBatchCommand()));
            Assert.True(one.BatchCommands.Remove(first)); Assert.False(one.BatchCommands.Remove(first));
            two.BatchCommands.Insert(0, first); Assert.Equal(0, two.BatchCommands.IndexOf(first)); Assert.Contains(first, two.BatchCommands);
            var copy = new System.Data.Common.DbBatchCommand[2]; two.BatchCommands.CopyTo(copy, 0); Assert.Same(first, copy[0]);
            two.BatchCommands.RemoveAt(1); one.BatchCommands.Add(second); Assert.Single(one.BatchCommands);
            two.BatchCommands.Clear(); one.BatchCommands.Clear(); Assert.Empty(two.BatchCommands);
            one.Dispose(); Assert.Throws<ObjectDisposedException>(() => one.CreateBatchCommand()); Assert.Throws<ObjectDisposedException>(() => one.BatchCommands.Add(first));
            Assert.Throws<ObjectDisposedException>(() => one.Timeout = 1);
        }

        [Fact]
        public async Task BatchRejectsInvalidAndCanceledStartsBeforeNetwork()
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); var command = BatchItem(batch, "");
                Assert.Throws<ArgumentOutOfRangeException>(() => batch.Timeout = -1);
                Assert.Throws<NotSupportedException>(() => batch.ExecuteReader(CommandBehavior.SingleResult));
                Assert.Throws<InvalidOperationException>(() => batch.ExecuteNonQuery()); command.CommandText = "not sent"; command.CommandType = CommandType.StoredProcedure;
                Assert.Throws<NotSupportedException>(() => batch.ExecuteNonQuery()); command.CommandType = CommandType.Text;
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.ExecuteNonQueryAsync(canceled.Token)); Assert.Equal(canceled.Token, error.CancellationToken);
                using (var ambient = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
                    await Assert.ThrowsAsync<InvalidOperationException>(() => batch.ExecuteNonQueryAsync());
                Assert.Equal(ConnectionState.Open, connection.State); batch.Cancel();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DataSourceBatchReaderOwnsConnectionAndSourceDisposalRejectsNewOpens(bool asynchronous)
        {
            var server = Server(async channel => { await Query(channel, "one"); await channel.WritePacketAsync(Ok(1)); Assert.Equal(0, await channelStreamRead(channel)); });
            var builder = new OceanBaseDataSourceBuilder(Configuration(server.Port)); await using var source = builder.Build();
            await using var batch = source.CreateBatch(); builder.ConnectionStringBuilder.Server = "invalid";
            var item = OceanBaseProviderFactory.Instance.CreateBatchCommand(); item.CommandText = "one"; batch.BatchCommands.Add(item);
            try
            {
                var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                Assert.Equal(1, item.RecordsAffected); source.Dispose();
                if (asynchronous) await reader.DisposeAsync(); else reader.Dispose();
                await Assert.ThrowsAsync<ObjectDisposedException>(() => batch.ExecuteNonQueryAsync());
                Assert.Throws<ObjectDisposedException>(() => source.CreateBatch());
            }
            finally { server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
