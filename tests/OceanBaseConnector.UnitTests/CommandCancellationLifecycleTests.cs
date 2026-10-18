using System;
using System.Data;
using System.Threading.Tasks;
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
        public async Task LateOldCommandCancellationCannotBreakReopenedConnection(bool asynchronous, bool dispose)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = Server(async channel => { await Query(channel, "old write"); received.SetResult(); Assert.Equal(0, await channelStreamRead(channel)); });
            var second = Server(async channel => { await Query(channel, "new write"); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(first.Port));
            try
            {
                await connection.OpenAsync(); using var old = connection.CreateCommand(); old.CommandText = "old write";
                var pending = asynchronous ? old.ExecuteNonQueryAsync() : Task.Run(() => old.ExecuteNonQuery());
                await received.Task.WaitAsync(TimeSpan.FromSeconds(10)); connection.Close();
                connection.ConnectionString = Configuration(second.Port); await connection.OpenAsync();
                if (dispose) old.Dispose(); else old.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending);
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Open, connection.State);
                using var current = connection.CreateCommand(); current.CommandText = "new write";
                Assert.Equal(1, await current.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); first.Stop(); second.Stop(); }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
