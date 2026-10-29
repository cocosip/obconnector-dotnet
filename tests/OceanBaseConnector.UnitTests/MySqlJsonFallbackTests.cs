using System;
using System.Data;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class MySqlJsonFallbackLiteralTests
    {
        [Theory]
        [InlineData(false, "{\"x\":\"a'\\\\中;?\\u0000\"}", "'{\"x\":\"a''\\\\\\\\中;?\\\\u0000\"}'")]
        [InlineData(true, "{\"x\":\"a'\\\\中;?\\u0000\"}", "'{\"x\":\"a''\\\\中;?\\u0000\"}'")]
        [InlineData(false, "null", "'null'")]
        [InlineData(true, "null", "'null'")]
        [InlineData(false, "{broken", "'{broken'")]
        [InlineData(true, "{broken", "'{broken'")]
        public void MySqlJsonFallbackUsesOrdinaryLiteralsAndServerSyntaxValidation(bool noBackslash, string value, string literal)
        {
            var parameters = new OceanBaseParameterCollection();
            parameters.Add(new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Value = value, Size = 1 });
            parameters.Add(new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Value = DBNull.Value });
            var context = MySqlLiteralContext.Parse("utf8mb4", "utf8mb4", "utf8mb4_bin", noBackslash ? "NO_BACKSLASH_ESCAPES" : "");
            Assert.Equal("SELECT (" + literal + "),(NULL)", ClientPreparedSql.Build(
                ParameterBindingPlan.Create("SELECT ?,?", OceanBaseCompatibilityMode.MySql, parameters), OceanBaseCompatibilityMode.MySql, context));
        }

        [Fact]
        public void MySqlJsonFallbackRetainsOrdinaryNulAndStrictEncodingBoundaries()
        {
            var parameters = new OceanBaseParameterCollection();
            var value = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Value = "\0" }; parameters.Add(value);
            var bindings = ParameterBindingPlan.Create("SELECT ?", OceanBaseCompatibilityMode.MySql, parameters);
            Assert.Equal("SELECT ('\\0')", ClientPreparedSql.Build(bindings, OceanBaseCompatibilityMode.MySql,
                MySqlLiteralContext.Parse("utf8mb4", "utf8mb4", "utf8mb4_bin", "")));
            Assert.Throws<NotSupportedException>(() => ClientPreparedSql.Build(bindings, OceanBaseCompatibilityMode.MySql,
                MySqlLiteralContext.Parse("utf8mb4", "utf8mb4", "utf8mb4_bin", "NO_BACKSLASH_ESCAPES")));
            value.Value = "\ud800";
            Assert.Throws<EncoderFallbackException>(() => ClientPreparedSql.Build(
                ParameterBindingPlan.Create("SELECT ?", OceanBaseCompatibilityMode.MySql, parameters), OceanBaseCompatibilityMode.MySql));
        }
    }

    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlJsonFallbackFreezesInputsAndDiscoversFreshModes(bool asynchronous)
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = MySqlProcedureServer(async channel =>
            {
                for (var run = 0; run < 3; run++)
                {
                    await StatementCommand(channel, 22);
                    if (run == 0) { reached.SetResult(); await release.Task; }
                    await channel.WritePacketAsync(UnsupportedPrepare());
                    if (run < 2) await MySqlLiteralState(channel, modes: run == 0 ? "" : "NO_BACKSLASH_ESCAPES");
                    channel.BeginCommand();
                    await Query(channel, run == 0 ? "INSERT INTO t VALUES (('{\"x\":\"a''\\\\\\\\中;?\"}'))" :
                        run == 1 ? "INSERT INTO t VALUES (('{\"x\":\"a''\\\\中;?\"}'))" : "INSERT INTO t VALUES ((NULL))");
                    await channel.WritePacketAsync(Ok(1, 2));
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.AllowClientSidePrepareFallback = true; command.CommandText = "INSERT INTO t VALUES (?)";
                var parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Value = "{\"x\":\"a'\\\\中;?\"}" };
                command.Parameters.Add(parameter);
                var pending = Task.Run(async () => asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                await Task.WhenAny(reached.Task, pending).WaitAsync(TimeSpan.FromSeconds(5)); if (pending.IsFaulted) await pending;
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
                parameter.Value = "changed"; parameter.Precision = 1;
                release.SetResult(); Assert.Equal(1, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                parameter.Value = "{\"x\":\"a'\\\\中;?\"}"; parameter.Precision = 0;
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                parameter.Value = DBNull.Value;
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlJsonFallbackInvalidDeclarationsNeverSendPrepare(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel => { await Query(channel, "after invalid json fallback"); await channel.WritePacketAsync(Ok(1, 2)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.AllowClientSidePrepareFallback = true; command.CommandText = "INSERT INTO t VALUES (?)";
                foreach (var invalid in new[]
                {
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Value = DBNull.Value, Precision = 1 },
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Value = DBNull.Value, Scale = 1 },
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Value = new byte[] { 1 } },
                    new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Value = "\ud800" }
                })
                {
                    command.Parameters.Clear(); command.Parameters.Add(invalid);
                    var error = await Record.ExceptionAsync(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                    Assert.True(error is ArgumentException || error is NotSupportedException);
                    Assert.False(command.HasSentSql); Assert.Equal(ConnectionState.Open, connection.State);
                }
                command.Parameters.Clear(); command.CommandText = "after invalid json fallback"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
