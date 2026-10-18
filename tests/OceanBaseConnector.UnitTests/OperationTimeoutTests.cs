using System;
using System.Data;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, "query")]
        [InlineData(true, "query")]
        [InlineData(false, "prepare")]
        [InlineData(true, "prepare")]
        [InlineData(false, "commit")]
        [InlineData(true, "commit")]
        [InlineData(false, "rollback")]
        [InlineData(true, "rollback")]
        public async Task OperationTimeoutHasTimeoutCauseAndDoesNotMasqueradeAsCallerCancellation(bool asynchronous, string operation)
        {
            var server = Server(async channel =>
            {
                if (operation == "commit" || operation == "rollback") await TransactionBegin(channel);
                if (operation == "prepare") await StatementCommand(channel, 22);
                else await Query(channel, operation == "query" ? "update slow" : operation.ToUpperInvariant());
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var log = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Command Timeout=1", log);
            try
            {
                await connection.OpenAsync();
                using var transaction = operation == "commit" || operation == "rollback" ? await connection.BeginTransactionAsync() : null;
                using var command = connection.CreateCommand(); command.CommandText = "update slow";
                var error = await Record.ExceptionAsync(() => asynchronous
                    ? operation switch
                    {
                        "query" => command.ExecuteNonQueryAsync(),
                        "prepare" => command.PrepareAsync(),
                        "commit" => transaction!.CommitAsync(),
                        _ => transaction!.RollbackAsync()
                    }
                    : RunSync(() =>
                    {
                        switch (operation)
                        {
                            case "query": command.ExecuteNonQuery(); break;
                            case "prepare": command.Prepare(); break;
                            case "commit": transaction!.Commit(); break;
                            default: transaction!.Rollback(); break;
                        }
                    }));
                if (operation == "prepare") Assert.IsType<TimeoutException>(error);
                else
                {
                    var databaseError = Assert.IsType<OceanBaseException>(error);
                    Assert.True(databaseError.IsExecutionOutcomeUnknown);
                    Assert.IsType<TimeoutException>(databaseError.InnerException);
                }
                Assert.Equal(ConnectionState.Broken, connection.State);
                Assert.Contains(log.Entries, entry => entry.EventId.Id == (transaction == null ? 1101 : 1201) &&
                    entry.Message.Contains("Canceled=False", StringComparison.Ordinal));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
