using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Fact]
        public void BatchMultiSendGroupsStopAtPacketBudget()
        {
            var requests = new[]
            {
                new byte[(int)(PacketChannel.DefaultMaxPacketSize / 2) + 1],
                new byte[(int)(PacketChannel.DefaultMaxPacketSize / 2) + 1],
                new byte[] { 1 },
            };
            var groups = OceanBaseBatch.PlanMultiSendGroups(requests, 100).ToArray();
            Assert.Equal(new[] { (0, 1), (1, 3) }, groups);
        }

        private static DbBatchCommand BatchItem(DbBatch batch, string sql)
        {
            var item = batch.CreateBatchCommand(); item.CommandText = sql; batch.BatchCommands.Add(item); return item;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchSequentialExecutionKeepsIndividualCounts(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "one"); await channel.WritePacketAsync(Ok(2)); channel.BeginCommand();
                await Query(channel, "two"); await channel.WritePacketAsync(Ok(3));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                var one = BatchItem(batch, "one"); var two = BatchItem(batch, "two");
                Assert.Equal(-1, one.RecordsAffected);
                Assert.Equal(5, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                Assert.Equal(2, one.RecordsAffected); Assert.Equal(3, two.RecordsAffected);
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchFailureRetainsPrefixAndAttributesCommandWithoutSendingSuffix(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "one"); await channel.WritePacketAsync(Ok(2)); channel.BeginCommand();
                await Query(channel, "bad"); await channel.WritePacketAsync(Error()); channel.BeginCommand();
                await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                var one = BatchItem(batch, "one"); var bad = BatchItem(batch, "bad"); var suffix = BatchItem(batch, "never");
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? batch.ExecuteNonQueryAsync() : Task.FromResult(batch.ExecuteNonQuery()));
                Assert.Same(bad, error.BatchCommand); Assert.Equal(1064, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(2, one.RecordsAffected); Assert.Equal(-1, bad.RecordsAffected); Assert.Equal(-1, suffix.RecordsAffected);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EmptyBatchDoesNotWriteAndReaderOwnsReservationUntilClosed(bool asynchronous)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                Assert.Equal(0, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                Assert.Null(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
                using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                Assert.Equal(0, reader.FieldCount); Assert.False(reader.Read()); Assert.False(reader.NextResult());
                Assert.Throws<InvalidOperationException>(() => connection.GetSchema()); reader.Close();
                Assert.Equal(20, connection.GetSchema().Rows.Count);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ClosedBatchReaderCountsAreAnIndependentSnapshotAcrossReuse()
        {
            var server = Server(async channel =>
            {
                await Query(channel, "one"); await channel.WritePacketAsync(Ok(2)); channel.BeginCommand();
                await Query(channel, "one"); await channel.WritePacketAsync(Ok(9));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch(); var one = BatchItem(batch, "one");
                using var old = await batch.ExecuteReaderAsync(); Assert.False(await old.NextResultAsync()); old.Close();
                Assert.Equal(9, await batch.ExecuteNonQueryAsync());
                Assert.Equal(2, old.RecordsAffected); Assert.Equal(9, one.RecordsAffected);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
