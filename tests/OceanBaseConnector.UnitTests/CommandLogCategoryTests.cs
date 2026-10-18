using System;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task CommandLoggingRespectsCommandCategoryFilter(bool asynchronous, bool serverError)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "category test");
                await channel.WritePacketAsync(serverError ? Error() : Ok(1));
            });
            using var log = new RecordingLoggerFactory { EnabledCategory = "OceanBaseConnector.Command" };
            using var connection = new OceanBaseConnection(Configuration(server.Port), log);
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand(); command.CommandText = "category test";
                var error = await Record.ExceptionAsync(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                if (serverError) Assert.IsType<OceanBaseException>(error); else Assert.Null(error);
                connection.Close();
                var entry = Assert.Single(log.Entries);
                Assert.Equal(serverError ? 1101 : 1100, entry.EventId.Id);
                Assert.Null(entry.Error); Assert.DoesNotContain("category test", entry.Message);
                Assert.DoesNotContain("secret", entry.Message);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
