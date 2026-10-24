using System;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Fact]
        public void FactoryCreatesAdaptersAndBuilders()
        {
            using var adapter = OceanBaseProviderFactory.Instance.CreateDataAdapter();
            using var builder = OceanBaseProviderFactory.Instance.CreateCommandBuilder();
            Assert.NotNull(adapter); Assert.NotNull(builder);
            Assert.True(OceanBaseProviderFactory.Instance.CanCreateDataAdapter);
            Assert.True(OceanBaseProviderFactory.Instance.CanCreateCommandBuilder);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AdapterFillsNullsAndUsesOriginalValuesForExplicitUpdates(bool mysql)
        {
            using var adapter = OceanBaseProviderFactory.Instance.CreateDataAdapter(); Assert.NotNull(adapter);
            var columns = new[] { SchemaColumn("id", 8, 20, flags: 1, originalName: "id"), SchemaColumn("payload", 253, 80, originalName: "payload") };
            var server = Server(async channel =>
            {
                await Query(channel, "select id, payload from ledger");
                await channel.WritePacketAsync(new byte[] { 2 });
                foreach (var column in columns) await channel.WritePacketAsync(column);
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 1, 49, 1, 120 });
                await channel.WritePacketAsync(new byte[] { 1, 50, 251 });
                await channel.WritePacketAsync(Eof());
                var request = await StatementCommand(channel, 22);
                Assert.Equal("update ledger set payload = ? where id = ? and payload = ?", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await Prepared(channel, 81, 3);
                var parameters = new[] { new OceanBaseParameter { Value = "y" }, new OceanBaseParameter { Value = 1L }, new OceanBaseParameter { Value = "x" } };
                Assert.Equal(PreparedParameters.Execute(81, PreparedParameters.Encode(parameters, mysql ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle)), await StatementCommand(channel, 23));
                await channel.WritePacketAsync(Ok(1));
                Assert.Equal(new byte[] { 25, 81, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var select = connection.CreateCommand(); select.CommandText = "select id, payload from ledger"; adapter.SelectCommand = select;
                using var update = connection.CreateCommand();
                update.CommandText = mysql ? "update ledger set payload = ? where id = ? and payload = ?" : "update ledger set payload = :value where id = :id and payload = :original";
                update.UpdatedRowSource = UpdateRowSource.None;
                update.Parameters.Add(new OceanBaseParameter { ParameterName = "value", SourceColumn = "payload" });
                update.Parameters.Add(new OceanBaseParameter { ParameterName = "id", SourceColumn = "id", SourceVersion = DataRowVersion.Original });
                update.Parameters.Add(new OceanBaseParameter { ParameterName = "original", SourceColumn = "payload", SourceVersion = DataRowVersion.Original });
                adapter.UpdateCommand = update;
                using var table = new DataTable(); Assert.Equal(2, adapter.Fill(table));
                Assert.Equal(1L, table.Rows[0]["id"]); Assert.Equal(DBNull.Value, table.Rows[1]["payload"]);
                Assert.Equal(DataRowState.Unchanged, table.Rows[0].RowState);
                table.Rows[0]["payload"] = "y"; Assert.Equal(1, adapter.Update(table));
                Assert.Equal(DataRowState.Unchanged, table.Rows[0].RowState); Assert.Equal("y", table.Rows[0]["payload", DataRowVersion.Original]);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BuilderGeneratesQualifiedParameterizedCrudFromSameSessionKeys(bool mysql)
        {
            using var adapter = OceanBaseProviderFactory.Instance.CreateDataAdapter(); Assert.NotNull(adapter);
            using var builder = OceanBaseProviderFactory.Instance.CreateCommandBuilder(); Assert.NotNull(builder);
            var columns = new[] { SchemaColumn("id", 8, 20, flags: 1, originalName: "id"), SchemaColumn("payload", 253, 80, originalName: "payload") };
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await KeyPrepare(channel, 91, columns);
                await KeyDirectory(channel, mysql, new string?[] { "租户库", "ledger", "PK", "P", "id", "1", "NO", "YES" });
                Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var select = connection.CreateCommand();
                select.CommandText = mysql ? "select id, payload from `租户库`.ledger" : "select id, payload from \"租户库\".\"ledger\"";
                select.CommandTimeout = 17; adapter.SelectCommand = select; builder.DataAdapter = adapter;
                var quote = mysql ? "`" : "\"";
                var insert = builder.GetInsertCommand(); var update = builder.GetUpdateCommand(); var delete = builder.GetDeleteCommand();
                foreach (var command in new[] { insert, update, delete })
                {
                    Assert.IsType<OceanBaseCommand>(command); Assert.Same(connection, command.Connection); Assert.Equal(17, command.CommandTimeout);
                    Assert.Contains(quote + "租户库" + quote + "." + quote + "ledger" + quote, command.CommandText);
                    Assert.Contains(mysql ? "?" : ":p1", command.CommandText); Assert.Equal(UpdateRowSource.None, command.UpdatedRowSource);
                }
                Assert.Equal(5, update.Parameters.Count);
                Assert.Equal(DataRowVersion.Original, update.Parameters[2].SourceVersion);
                Assert.True(update.Parameters[3].SourceColumnNullMapping); Assert.Equal(DbType.Int32, update.Parameters[3].DbType);
                Assert.Equal("payload", update.Parameters[4].SourceColumn);
                Assert.Equal(quote + "a" + quote + quote + "b" + quote, builder.QuoteIdentifier("a" + quote + "b"));
                Assert.Equal("a" + quote + "b", builder.UnquoteIdentifier(builder.QuoteIdentifier("a" + quote + "b")));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
