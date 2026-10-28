using System;
using System.Data;
using System.Text;
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
        public Task MySqlFunctionReturnValueUsesSetAndPublishesAfterPrivateSelect(bool asynchronous)
            => MySqlFunctionReturnValueCore(asynchronous);

        private static async Task MySqlFunctionReturnValueCore(bool asynchronous, int fetchSize = 0)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                var prepare = await StatementCommand(channel, 22);
                var sql = Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1);
                Assert.Matches("^SET @obc_[0-9a-f]{32}_0 = `db`\\.`f`\\(\\?, \\?\\)$", sql);
                var variable = sql.Split(' ')[1];
                await Prepared(channel, 81, 2);
                var execute = await StatementCommand(channel, 23);
                Assert.Equal(PreparedParameters.Execute(81, PreparedParameters.Encode(new[]
                {
                    new OceanBaseParameter { Value = 7L }, new OceanBaseParameter { Value = 5L }
                }, OceanBaseCompatibilityMode.MySql)), execute);
                await channel.WritePacketAsync(Ok(status: 2));
                channel.BeginCommand(); await Query(channel, "SELECT " + variable);
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("return_value", 8)); await channel.WritePacketAsync(Eof(2));
                await channel.WritePacketAsync(new byte[] { 2, 52, 50 });
                await channel.WritePacketAsync(Eof(2));
                Assert.Equal(new byte[] { 25, 81, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await Query(channel, "after function"); await channel.WritePacketAsync(Ok(1, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.CommandType = CommandType.StoredProcedure; command.CommandText = "db.f";
                command.FetchSize = fetchSize;
                var returnValue = new OceanBaseParameter { Direction = ParameterDirection.ReturnValue, Value = "old" };
                command.Parameters.Add(returnValue); command.Parameters.Add(new OceanBaseParameter { Value = 7L });
                command.Parameters.Add(new OceanBaseParameter { Value = 5L });
                using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
                Assert.Equal("old", returnValue.Value);
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.Equal(42L, returnValue.Value);
                Assert.Equal(fetchSize, command.FetchSize);
                if (asynchronous) await command.DisposeAsync(); else command.Dispose();
                using var after = connection.CreateCommand(); after.CommandText = "after function";
                Assert.Equal(1, asynchronous ? await after.ExecuteNonQueryAsync() : after.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
