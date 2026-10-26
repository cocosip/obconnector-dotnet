using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private const string ClientCharsetStateSql = "SELECT @@character_set_client, @@character_set_connection, @@character_set_results, @@collation_connection";

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, false)]
        public async Task ClientCharsetRejectsMismatchedOrExtraStateAfterOpaqueSql(bool asynchronous, bool extra, bool deprecated)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await ClientCharacterSetFixture.Initialize(channel, "gbk", false, deprecated, CancellationToken.None);
                channel.BeginCommand(); await Query(channel, "SET @x=1", deprecated); await channel.WritePacketAsync(Ok(status: 2));
                channel.BeginCommand(); await Query(channel, ClientCharsetStateSql, deprecated);
                await ClientCharacterSetFixture.State(channel, "gbk", false, deprecated, CancellationToken.None, client: extra ? "gbk" : "latin1", extra: extra);
            }, deprecated);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Character Set=gbk");
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                using var command = (OceanBaseCommand)connection.CreateCommand(); command.CommandText = "SET @x=1";
                Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                command.CommandText = "SELECT '中'";
                var error = await Assert.ThrowsAnyAsync<Exception>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.False(command.HasSentSql); Assert.False((error as OceanBaseException)?.IsExecutionOutcomeUnknown ?? false);
                if (extra)
                {
                    var protocol = Assert.IsType<ProtocolException>(Assert.IsType<OceanBaseException>(error).InnerException);
                    Assert.Equal("Extra client character set state row.", protocol.Message);
                }
                else Assert.IsType<NotSupportedException>(error);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClientCharsetTrustPreservesStatelessDmlAndRechecksClassifierAmbiguity(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await ClientCharacterSetFixture.Initialize(channel, "gbk", false, false, CancellationToken.None);
                await StatementCommand(channel, 22); await Prepared(channel, 71, 1);
                var request = await StatementCommand(channel, 23); Assert.Equal(Convert.FromHexString("275CD6D0"), request.AsSpan(15).ToArray());
                await channel.WritePacketAsync(Ok(1, 2));
                await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, "SELECT ROW_COUNT()"); await channel.WritePacketAsync(Ok(1, 2));
                channel.BeginCommand(); await Query(channel, "UPDATE t SET x='a\\\\b'"); await channel.WritePacketAsync(Ok(1, 2));
                channel.BeginCommand(); await Query(channel, ClientCharsetStateSql);
                await ClientCharacterSetFixture.State(channel, "gbk", false, false, CancellationToken.None);
                channel.BeginCommand(); await Query(channel, "SELECT ROW_COUNT()"); await channel.WritePacketAsync(Ok(1, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Character Set=gbk");
            try
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE t SET x=?"; command.Parameters.Add(new OceanBaseParameter { Value = "'\\中" });
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                }
                using var next = connection.CreateCommand();
                foreach (var sql in new[] { "SELECT ROW_COUNT()", "UPDATE t SET x='a\\\\b'", "SELECT ROW_COUNT()" })
                { next.CommandText = sql; Assert.Equal(1, asynchronous ? await next.ExecuteNonQueryAsync() : next.ExecuteNonQuery()); }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClientCharsetTrackedChangesInvalidateOtherwiseStatelessExecution(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await ClientCharacterSetFixture.Initialize(channel, "gbk", false, false, CancellationToken.None);
                channel.BeginCommand(); await Query(channel, "UPDATE t SET x=1");
                await channel.WritePacketAsync(SessionVariablesOk(new Dictionary<string, string> { ["character_set_client"] = "latin1" }, status: 2));
                channel.BeginCommand(); await Query(channel, ClientCharsetStateSql);
                await ClientCharacterSetFixture.State(channel, "gbk", false, false, CancellationToken.None, client: "latin1");
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Character Set=gbk");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE t SET x=1";
                if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
                command.CommandText = "SELECT '中'";
                await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClientCharsetUnrepresentableTextOrParametersFailBeforePayload(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await ClientCharacterSetFixture.Initialize(channel, "gbk", false, false, CancellationToken.None);
                channel.BeginCommand(); await Query(channel, "SELECT 1"); await channel.WritePacketAsync(Ok(1, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Character Set=gbk");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                foreach (var parameterized in new[] { false, true })
                {
                    command.CommandText = parameterized ? "SELECT ?" : "SELECT '€'";
                    if (parameterized) command.Parameters.Add(new OceanBaseParameter { Value = "€" });
                    await Assert.ThrowsAsync<EncoderFallbackException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                }
                command.Parameters.Clear(); command.CommandText = "SELECT 1";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task ClientCharsetInitializationRequiresMatchingInputAndUtf8Results(bool asynchronous, bool wrongResults)
        {
            var server = MySqlProcedureServer(channel => ClientCharacterSetFixture.Initialize(channel, "gbk", false, false, CancellationToken.None,
                client: wrongResults ? "gbk" : "latin1", results: wrongResults ? "gbk" : "utf8mb4"));
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Character Set=gbk");
            try
            {
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open));
                Assert.IsType<NotSupportedException>(error.InnerException);
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
