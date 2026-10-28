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
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task MySqlParameterizedMultiStatementsFreezeStringsAndUseOnlyText(bool asynchronous, bool deprecated)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await MySqlLiteralState(channel, modes: "NO_BACKSLASH_ESCAPES", deprecated: deprecated);
                channel.BeginCommand();
                await Query(channel, "SET sql_mode=''; SELECT (REPLACE('x','x',_utf8mb4 X'61275C003B3F')), (7); SELECT (X'0027FF'), (NULL)", deprecated);
                await channel.WritePacketAsync(Ok(2, 10));
                await channel.WritePacketAsync(Ok(3, 2));
                channel.BeginCommand(); await Query(channel, "after mysql multi parameters", deprecated);
                await channel.WritePacketAsync(Ok(1, 2));
            }, deprecated: deprecated);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "SET sql_mode=''; SELECT ?, ?; SELECT ?, ?";
                command.Parameters.Add(new OceanBaseParameter { Value = "a'\\\0;?" });
                command.Parameters.Add(new OceanBaseParameter { Value = 7 });
                command.Parameters.Add(new OceanBaseParameter { Value = new byte[] { 0, 39, 255 } });
                command.Parameters.Add(new OceanBaseParameter { Value = DBNull.Value });
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Equal("a'\\\0;?", ((OceanBaseParameter)command.Parameters[0]).Value);
                command.Parameters.Clear(); command.CommandText = "after mysql multi parameters";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlNumericMultiStatementsNeedNoLiteralStateQuery(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await Query(channel, "SELECT (7); SELECT (1.2500), (1)");
                await channel.WritePacketAsync(Ok(2, 10)); await channel.WritePacketAsync(Ok(3, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = "SELECT ?; SELECT ?, ?";
                command.Parameters.Add(new OceanBaseParameter { Value = 7 });
                command.Parameters.Add(new OceanBaseParameter { Value = 1.25m, Scale = 4 });
                command.Parameters.Add(new OceanBaseParameter { Value = true });
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlTemporalMultiStatementsUseTextLiterals(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await Query(channel, "SELECT ('2026-01-02 03:04:05.123456'); SELECT ('27:08:09.456000')");
                await channel.WritePacketAsync(Ok(2, 10)); await channel.WritePacketAsync(Ok(3, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = "SELECT ?; SELECT ?";
                command.Parameters.Add(new OceanBaseParameter { DbType = DbType.DateTime,
                    Value = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).AddTicks(1234560) });
                command.Parameters.Add(new OceanBaseParameter { DbType = DbType.Time, Value = new TimeSpan(0, 27, 8, 9, 456) });
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task MySqlMultiStatementParametersAreSnapshottedBeforeLiteralStateAwait()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = MySqlProcedureServer(async channel =>
            {
                await release.Task;
                await MySqlLiteralState(channel);
                channel.BeginCommand(); await Query(channel, "SELECT (REPLACE('x','x',_utf8mb4 X'6F6C64')); SELECT 1");
                await channel.WritePacketAsync(Ok(1, 10)); await channel.WritePacketAsync(Ok(1, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = "SELECT ?; SELECT 1";
                var parameter = new OceanBaseParameter { Value = "old" }; command.Parameters.Add(parameter);
                var executing = command.ExecuteNonQueryAsync(); Assert.False(executing.IsCompleted);
                parameter.Value = "new"; release.TrySetResult();
                Assert.Equal(2, await executing); Assert.Equal("new", parameter.Value);
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlParameterizedMultiStatementErrorRemainsKnownAndConnectionReusable(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await Query(channel, "INSERT INTO t VALUES ((7)); SELECT missing");
                await channel.WritePacketAsync(Ok(1, 10)); await channel.WritePacketAsync(Error());
                channel.BeginCommand(); await Query(channel, "after known multi error"); await channel.WritePacketAsync(Ok(1, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO t VALUES (?); SELECT missing"; command.Parameters.Add(new OceanBaseParameter { Value = 7 });
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.False(error.IsExecutionOutcomeUnknown);
                command.Parameters.Clear(); command.CommandText = "after known multi error";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
