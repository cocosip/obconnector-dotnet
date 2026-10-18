using System;
using System.Data;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LocalTransactionReaderAsyncCleanupWaitsWithoutSynchronousRollback(bool dispose)
        {
            var rollbackReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowRollback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await TransactionBegin(channel);
                await Query(channel, "update consumed"); await channel.WritePacketAsync(Ok(status: 5)); channel.BeginCommand();
                await Query(channel, "ROLLBACK"); rollbackReceived.SetResult();
                await allowRollback.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await channel.WritePacketAsync(Ok(status: 4)); channel.BeginCommand();
                await Query(channel, "set autocommit = 1"); await channel.WritePacketAsync(Ok());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            Task? closing = null;
            try
            {
                await connection.OpenAsync(); using var transaction = await connection.BeginTransactionAsync();
                using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "update consumed";
                var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection);
                closing = Task.Run(async () =>
                {
                    var pending = dispose ? reader.DisposeAsync().AsTask() : reader.CloseAsync();
                    returned.SetResult(); await pending;
                });
                await rollbackReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await returned.Task.WaitAsync(TimeSpan.FromSeconds(1));
                Assert.False(closing.IsCompleted);
                allowRollback.SetResult(); await closing;
                Assert.True(reader.IsClosed); Assert.Null(transaction.Connection); Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally
            {
                allowRollback.TrySetResult();
                if (closing != null) await closing;
                connection.Close(); server.Stop();
            }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
