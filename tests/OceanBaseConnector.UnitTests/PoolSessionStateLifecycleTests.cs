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
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PreparedSessionStateSqlIsMarkedOnlyAtExecutionIncludingCacheReuse(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 37, 0);
                for (var i = 0; i < 2; i++)
                {
                    var request = await StatementCommand(channel, 23); Assert.Equal(37, request[1]);
                    await channel.WritePacketAsync(TimeZoneOk("+00:00"));
                }
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "/* trace */ ALTER\nSESSION SET TIME_ZONE = '+00:00'";
                    if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                    Assert.False(connection.GetOpenSession().RequiresDiscardOnReturn);
                    for (var i = 0; i < 2; i++)
                    {
                        Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                        Assert.True(connection.GetOpenSession().RequiresDiscardOnReturn);
                    }
                }
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { connection.Close(); server.Stop(); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OpaqueStoredProcedureNameMarksStateOnOraclePrepareExecute(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1);
                await channel.WritePacketAsync(OracleExecuteHeader(0, 0, 0, rows: false));
                await channel.WritePacketAsync(Ok());
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync();
                Assert.False(connection.GetOpenSession().RequiresDiscardOnReturn);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "pkg.p"; command.CommandType = CommandType.StoredProcedure;
                    Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                    Assert.True(connection.GetOpenSession().RequiresDiscardOnReturn);
                }
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { connection.Close(); server.Stop(); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PreparingSessionStateSqlDoesNotMarkItAsExecuted(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await Prepared(channel, 37, 0);
                await StatementCommand(channel, 25);
                channel.BeginCommand();
                await Query(channel, "select 1");
                await channel.WritePacketAsync(Ok());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "ALTER SESSION SET TIME_ZONE = '+00:00'";
                    if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                    Assert.False(connection.GetOpenSession().RequiresDiscardOnReturn);
                }
                using var next = connection.CreateCommand();
                next.CommandText = "select 1";
                Assert.Equal(0, await next.ExecuteNonQueryAsync());
                Assert.False(connection.GetOpenSession().RequiresDiscardOnReturn);
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { connection.Close(); server.Stop(); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RejectedSessionStateSqlDoesNotPoisonTheSession(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "select 1");
                await channel.WritePacketAsync(Ok());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "SET a = 1; SET b = 2";
                await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous
                    ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.False(connection.GetOpenSession().RequiresDiscardOnReturn);
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                command.CommandText = "ALTER SESSION SET TIME_ZONE = '+00:00'";
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteNonQueryAsync(canceled.Token));
                Assert.False(connection.GetOpenSession().RequiresDiscardOnReturn);
                command.CommandText = "select 1";
                Assert.Equal(0, await command.ExecuteNonQueryAsync());
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { connection.Close(); server.Stop(); }
        }
    }
}
