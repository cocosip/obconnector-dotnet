using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static LoopbackServer MySqlProcedureServer(Func<PacketChannel, Task> exchange, bool deprecated = false, bool psMultiResults = true) => LoopbackServer.Start(async client =>
        {
            var channel = new PacketChannel(client.GetStream());
            var greeting = (byte[])ProtocolFixture.Load("initial-handshake-oceanbase").Payload.Clone();
            if (psMultiResults) greeting[41] |= 4; else greeting[41] &= 251;
            if (deprecated) greeting[Array.IndexOf(greeting, (byte)0, 1) + 20] |= 1;
            await channel.WritePacketAsync(greeting); await channel.ReadPacketAsync();
            await channel.WritePacketAsync(Ok(status: 2));
            await ServerIdentity.ReplyAsync(channel, oracle: false, deprecated: deprecated);
            await exchange(channel);
        });

        public static IEnumerable<object[]> MySqlProcedureCases()
        {
            foreach (var async in new[] { false, true })
                foreach (var deprecated in new[] { false, true })
                    foreach (var sequential in new[] { false, true })
                        foreach (var close in new[] { false, true })
                            yield return new object[] { async, deprecated, sequential, close };
        }

        private static byte[] MySqlProcedureEnd(bool deprecated, bool more = false) => deprecated
            ? new byte[] { 254, 0, 0, more ? (byte)10 : (byte)2, 0, 0, 0 }
            : Eof(more ? (byte)10 : (byte)2);

        [Theory]
        [InlineData("CREATE PROCEDURE p() BEGIN SELECT 1; SELECT 2; END")]
        [InlineData("# comment\nCREATE /* comment */ FUNCTION f() RETURNS INT BEGIN RETURN 1; END")]
        [InlineData("CREATE TRIGGER t BEFORE INSERT ON tab FOR EACH ROW BEGIN SET @n=1; END")]
        [InlineData("CREATE DEFINER = CURRENT_USER PROCEDURE p() BEGIN SELECT 1; END")]
        [InlineData("CREATE DEFINER=CURRENT_USER() FUNCTION f() RETURNS INT BEGIN RETURN 1; END")]
        [InlineData("CREATE DEFINER='u''name'@`host` PROCEDURE p() BEGIN SELECT 1; END")]
        [InlineData("CREATE DEFINER='u\\'name'@'host' PROCEDURE p() BEGIN SELECT 1; END")]
        public async Task MySqlRoutineDefinitionUsesServerStatementGrammar(string sql)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await Query(channel, sql); await channel.WritePacketAsync(Ok(status: 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = sql;
                Assert.Equal(0, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static async Task<string[]> MySqlProcedureInitialize(PacketChannel channel, bool deprecated)
        {
            var prepare = await StatementCommand(channel, 22, deprecated);
            var sql = Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1);
            Assert.StartsWith("SET @", sql);
            var assignments = sql.Substring(4).Split(", ");
            Assert.Equal(2, assignments.Length);
            var variables = assignments.Select(a => a.Split(" = ")[0]).ToArray();
            Assert.All(assignments, a => Assert.EndsWith(" = ?", a));
            Assert.NotEqual(variables[0], variables[1]);
            Assert.All(variables, v => Assert.Matches("^@obc_[0-9a-f]{32}_[0-9]+$", v));
            await Prepared(channel, 80, 2, deprecated: deprecated);
            var execute = await StatementCommand(channel, 23, deprecated);
            Assert.Equal(PreparedParameters.Execute(80, PreparedParameters.Encode(new[]
            {
                new OceanBaseParameter { Value = DBNull.Value }, new OceanBaseParameter { Value = "中文'input" }
            }, OceanBaseCompatibilityMode.MySql)), execute);
            await channel.WritePacketAsync(Ok(status: 2));
            Assert.Equal(new byte[] { 25, 80, 0, 0, 0 }, await StatementCommand(channel, 25, deprecated));
            return variables;
        }

        private static async Task MySqlProcedureReply(PacketChannel channel, bool deprecated, byte value, bool more)
        {
            await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("normal", 8));
            if (!deprecated) await channel.WritePacketAsync(Eof(2));
            await channel.WritePacketAsync(new byte[] { 0, 0, value, 0, 0, 0, 0, 0, 0, 0 });
            await channel.WritePacketAsync(MySqlProcedureEnd(deprecated, more));
        }

        [Theory]
        [MemberData(nameof(MySqlProcedureCases))]
        public Task MySqlProcedureStreamsResultsAndPublishesOutputsOnlyAfterPrivateSelectCompletes(bool async, bool deprecated, bool sequential, bool close)
            => MySqlProcedureResultsCore(async, deprecated, sequential, close);

        private static async Task MySqlProcedureResultsCore(bool async, bool deprecated, bool sequential, bool close, int fetchSize = 0)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = MySqlProcedureServer(async channel =>
            {
                var variables = await MySqlProcedureInitialize(channel, deprecated);
                var prepare = await StatementCommand(channel, 22, deprecated);
                Assert.Equal("CALL `db`.`p``name`(?, " + string.Join(", ", variables) + ")", Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                await Prepared(channel, 81, 1, deprecated: deprecated);
                Assert.Equal(PreparedParameters.Execute(81, PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = 7L } }, OceanBaseCompatibilityMode.MySql)),
                    await StatementCommand(channel, 23, deprecated));
                await MySqlProcedureReply(channel, deprecated, 9, more: true);
                await MySqlProcedureReply(channel, deprecated, 10, more: true);
                await channel.WritePacketAsync(Ok(3, status: 2));
                channel.BeginCommand(); await Query(channel, "SELECT " + string.Join(", ", variables), deprecated);
                await channel.WritePacketAsync(new byte[] { 2 });
                await channel.WritePacketAsync(Column("out", 8)); await channel.WritePacketAsync(Column("inout", 253));
                if (!deprecated) await channel.WritePacketAsync(Eof(2));
                await channel.WritePacketAsync(new byte[] { 251, 3, 110, 101, 119 });
                waiting.TrySetResult(); await release.Task;
                await channel.WritePacketAsync(MySqlProcedureEnd(deprecated));
                Assert.Equal(new byte[] { 25, 81, 0, 0, 0 }, await StatementCommand(channel, 25, deprecated));
                channel.BeginCommand(); await Query(channel, "after procedure", deprecated); await channel.WritePacketAsync(Ok(1, status: 2));
            }, deprecated);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                Assert.Equal(OceanBaseCompatibilityMode.MySql, connection.CompatibilityMode);
                command.CommandType = CommandType.StoredProcedure; command.CommandText = "db.`p``name`";
                command.FetchSize = fetchSize;
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "ignored name", Value = 7L });
                var output = new OceanBaseParameter { Direction = ParameterDirection.Output, Value = new object() };
                var inout = new OceanBaseParameter { Direction = ParameterDirection.InputOutput, Value = "中文'input" };
                command.Parameters.Add(output); command.Parameters.Add(inout);
                using var reader = async ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                    : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.Equal(1, reader.FieldCount); Assert.True(async ? await reader.ReadAsync() : reader.Read()); Assert.Equal(9L, reader.GetInt64(0));
                if (!close)
                {
                    Assert.False(async ? await reader.ReadAsync() : reader.Read()); Assert.True(async ? await reader.NextResultAsync() : reader.NextResult());
                    Assert.True(async ? await reader.ReadAsync() : reader.Read()); Assert.Equal(10L, reader.GetInt64(0));
                    Assert.False(async ? await reader.ReadAsync() : reader.Read()); Assert.True(async ? await reader.NextResultAsync() : reader.NextResult());
                    Assert.Equal(0, reader.FieldCount); Assert.False(async ? await reader.ReadAsync() : reader.Read());
                }
                var completing = Task.Run(async () =>
                {
                    if (close) { if (async) await reader.CloseAsync(); else reader.Close(); }
                    else Assert.False(async ? await reader.NextResultAsync() : reader.NextResult());
                });
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsType<object>(output.Value); Assert.Equal("中文'input", inout.Value);
                using var blocked = connection.CreateCommand(); blocked.CommandText = "cannot interleave";
                Assert.Throws<InvalidOperationException>(() => blocked.ExecuteNonQuery());
                release.TrySetResult(); await completing.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(DBNull.Value, output.Value); Assert.Equal("new", inout.Value); Assert.Equal(3, reader.RecordsAffected);
                Assert.Equal(fetchSize, command.FetchSize);
                if (async) await command.DisposeAsync(); else command.Dispose();
                using var after = connection.CreateCommand(); after.CommandText = "after procedure"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
