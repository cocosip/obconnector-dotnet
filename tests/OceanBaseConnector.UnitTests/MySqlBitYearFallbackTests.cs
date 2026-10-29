using System;
using System.Data;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class MySqlBitYearFallbackLiteralTests
    {
        [Theory]
        [MemberData(nameof(MySqlBitYearLiteralTests.Values), MemberType = typeof(MySqlBitYearLiteralTests))]
        public void MySqlBitYearFallbackLiteralsPreserveExactNumbersAndNull(OceanBaseDbType type, object value, int size, byte precision, string literal)
        {
            var parameters = new OceanBaseParameterCollection();
            parameters.Add(new OceanBaseParameter { OceanBaseDbType = type, Value = value, Size = size, Precision = precision });
            parameters.Add(new OceanBaseParameter { OceanBaseDbType = type, Value = DBNull.Value, Size = size, Precision = precision });
            Assert.Equal("SELECT (" + literal + "),(NULL)", ClientPreparedSql.Build(
                ParameterBindingPlan.Create("SELECT ?,?", OceanBaseCompatibilityMode.MySql, parameters), OceanBaseCompatibilityMode.MySql));
            Assert.Same(value, parameters[0].Value);
        }
    }

    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlBitYearFallbackFreezesInputsAndPreparesAgain(bool asynchronous)
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = MySqlProcedureServer(async channel =>
            {
                for (var run = 0; run < 2; run++)
                {
                    await StatementCommand(channel, 22);
                    if (run == 0) { reached.SetResult(); await release.Task; }
                    await channel.WritePacketAsync(UnsupportedPrepare()); channel.BeginCommand();
                    await Query(channel, run == 0 ? "INSERT INTO t VALUES ((18446744073709551615),(2155))" : "INSERT INTO t VALUES ((NULL),(NULL))");
                    await channel.WritePacketAsync(Ok(1, 2));
                }
                channel.BeginCommand(); await Query(channel, "after bit year fallback"); await channel.WritePacketAsync(Ok(1, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.AllowClientSidePrepareFallback = true; command.CommandText = "INSERT INTO t VALUES (?,?)";
                var bits = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Bit, Value = ulong.MaxValue, Size = 64 };
                var year = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Year, Value = (ushort)2155, Precision = 4 };
                command.Parameters.Add(bits); command.Parameters.Add(year);
                var pending = Task.Run(async () => asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                await Task.WhenAny(reached.Task, pending).WaitAsync(TimeSpan.FromSeconds(5)); if (pending.IsFaulted) await pending;
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
                bits.Value = 1UL; bits.Size = 1; year.Value = (ushort)1900; year.Precision = 2;
                release.SetResult(); Assert.Equal(1, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                bits.Value = year.Value = DBNull.Value; bits.Size = 64; year.Precision = 4;
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                command.Parameters.Clear(); command.CommandText = "after bit year fallback";
                Assert.Equal(1, await command.ExecuteNonQueryAsync()); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlBitYearFallbackInvalidInputsNeverSendPrepare(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel => { await Query(channel, "after invalid fallback"); await channel.WritePacketAsync(Ok(1, 2)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.AllowClientSidePrepareFallback = true; command.CommandText = "INSERT INTO t VALUES (?)";
                foreach (var invalid in new[]
                {
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Bit, Value = 2UL, Size = 1 },
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Bit, Value = 1 },
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Year, Value = (ushort)1900 },
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Year, Value = 2026 },
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Bit, Value = DBNull.Value, Size = 65 },
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Year, Value = DBNull.Value, Precision = 2 }
                })
                {
                    command.Parameters.Clear(); command.Parameters.Add(invalid);
                    Func<Task> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
                    if (invalid.Value is DBNull) await Assert.ThrowsAsync<NotSupportedException>(execute);
                    else await Assert.ThrowsAnyAsync<ArgumentException>(execute);
                    Assert.False(command.HasSentSql); Assert.Equal(ConnectionState.Open, connection.State);
                }
                command.Parameters.Clear(); command.CommandText = "after invalid fallback"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
