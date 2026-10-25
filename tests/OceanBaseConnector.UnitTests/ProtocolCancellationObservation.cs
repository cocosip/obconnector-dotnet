using System;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        internal static async Task ProtocolCancellationObservation(bool asynchronous, bool close, Action beforeCancel)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await Query(channel, "private-diagnostics-write"); received.SetResult();
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "private-diagnostics-write";
                var pending = asynchronous ? command.ExecuteNonQueryAsync() : Task.Run(() => command.ExecuteNonQuery());
                await received.Task.WaitAsync(TimeSpan.FromSeconds(10)); beforeCancel();
                if (close) connection.Close(); else command.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(error.IsExecutionOutcomeUnknown);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
