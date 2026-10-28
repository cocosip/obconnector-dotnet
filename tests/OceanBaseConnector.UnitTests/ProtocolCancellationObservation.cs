using System;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        internal static async Task ProtocolCancellationObservation(bool asynchronous, bool close, Action beforeCancel)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "private-diagnostics-write";
                using var wire = new ProtocolFaultStream(new byte[] { 1 });
                // Cancel inside the header read, after the response read has actually started.
                wire.AfterInputConsumed = () =>
                {
                    beforeCancel();
                    if (close) connection.Close(); else command.Cancel();
                };
                using (var lease = connection.GetOpenSession().Acquire())
                { lease.Channel.ReplaceUnderlyingStream(wire); lease.Complete(); }
                var pending = asynchronous ? command.ExecuteNonQueryAsync() : Task.Run(() => command.ExecuteNonQuery());
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(error.IsExecutionOutcomeUnknown);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
