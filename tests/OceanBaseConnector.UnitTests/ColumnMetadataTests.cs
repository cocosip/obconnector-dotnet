using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
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
        private static string?[] MetadataValues(bool mysql, string type = "INT", string? declaration = "int(11)",
            string? flags = "", string? expression = null, string? defaultValue = null,
            string? length = null, string? semantics = null, string? precision = null, string? scale = null,
            string? identity = null)
            => new[] { mysql ? "Db目标" : null, mysql ? null : "Owner目标", "Table_'\"_%", "Col_'\"_%", "1", defaultValue,
                "YES", type, length, precision, scale, null, declaration, length, semantics, flags, expression, identity, "binary", "" };

        public static IEnumerable<object[]> ColumnMetadataStates()
        {
            foreach (var asynchronous in new[] { false, true })
            {
                foreach (var kind in new[] { "ordinary", "identity", "virtual", "stored", "missing-expression", "unknown", "future-flags", "update", "literal", "expression", "number", "now", "empty-literal" })
                    yield return new object[] { asynchronous, true, kind };
                foreach (var kind in new[] { "ordinary", "identity", "virtual", "stored", "missing-expression", "unknown", "literal", "expression", "null-default", "empty-literal" })
                    yield return new object[] { asynchronous, false, kind };
            }
        }

        [Theory]
        [MemberData(nameof(ColumnMetadataStates))]
        public async Task ColumnMetadataPreservesPresenceAndIncompleteStates(bool asynchronous, bool mysql, string kind)
        {
            var values = MetadataValues(mysql, mysql ? "INT" : "NUMBER", mysql ? "int(11) unsigned" : null, mysql ? "" : "NO");
            object? identity = mysql ? false : null, computed = mysql ? false : null, stored = null, hasDefault = null, sql = null,
                hasUpdate = mysql ? false : null, update = null, expression = null;
            switch (kind)
            {
                case "identity": values[15] = mysql ? "auto_increment INVISIBLE" : "NO"; values[17] = "YES"; identity = true; break;
                case "virtual": values[15] = mysql ? "VIRTUAL GENERATED" : "YES"; values[16] = mysql ? "(`a` + 1)" : null;
                    values[5] = mysql ? null : "(\"a\" + 1)"; computed = true; stored = false; expression = mysql ? "(`a` + 1)" : "(\"a\" + 1)"; hasDefault = false; break;
                case "stored":
                    if (mysql) { values[15] = "STORED GENERATED INVISIBLE"; values[16] = "concat(`a`, ',')";
                        computed = true; stored = true; expression = values[16]; hasDefault = false; }
                    else values[5] = "(\"a\" + 1)";
                    break;
                case "missing-expression": values[15] = mysql ? "VIRTUAL GENERATED" : "YES";
                    computed = true; stored = false; hasDefault = false; break;
                case "unknown": values[15] = null; identity = null; computed = null; hasUpdate = null; break;
                case "future-flags": values[15] = "FUTURE_GENERATION"; identity = null; computed = null; hasUpdate = null; break;
                case "update": values[7] = "TIMESTAMP"; values[12] = "timestamp(6)";
                    values[15] = "on update CURRENT_TIMESTAMP(6) INVISIBLE"; hasUpdate = true; update = "CURRENT_TIMESTAMP(6)"; break;
                case "literal": values[7] = mysql ? "VARCHAR" : "VARCHAR2"; values[5] = mysql ? "uuid()" : "'uuid()'";
                    hasDefault = true; sql = mysql ? null : "'uuid()'"; break;
                case "expression": values[7] = mysql ? "VARCHAR" : "VARCHAR2"; values[5] = mysql ? "uuid()" : "SYS_GUID()";
                    hasDefault = true; sql = mysql ? null : "SYS_GUID()"; break;
                case "number": values[5] = "42"; hasDefault = true; sql = "42"; break;
                case "now": values[7] = "TIMESTAMP"; values[12] = "timestamp(6)"; values[11] = "6";
                    values[5] = "CURRENT_TIMESTAMP"; hasDefault = true; sql = "CURRENT_TIMESTAMP(6)"; break;
                case "null-default": values[5] = "NULL"; hasDefault = true; sql = "NULL"; break;
                case "empty-literal": values[7] = mysql ? "VARCHAR" : "VARCHAR2"; values[5] = mysql ? "" : "''";
                    hasDefault = true; sql = mysql ? null : "''"; break;
            }
            // VIRTUAL_COLUMN=NO also covers stored generation in the inspected public directory.
            if (!mysql && values[15] != "YES") { hasDefault = null; sql = null; }
            var server = Server(async channel =>
            {
                var request = await StatementCommand(channel, 3);
                var query = Encoding.UTF8.GetString(request, 1, request.Length - 1);
                Assert.DoesNotContain("__ALL_", query); Assert.DoesNotContain("SHOW CREATE", query); Assert.DoesNotContain("DBMS_METADATA", query);
                if (!mysql) { Assert.Contains("ALL_TAB_COLUMNS", query); Assert.Contains("ALL_TAB_COLS", query); }
                foreach (var packet in DirectoryPackets(true, mysql, sourceValues: values)) await channel.WritePacketAsync(packet);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var table = asynchronous ? await connection.GetSchemaAsync("Columns") : connection.GetSchema("Columns");
                Assert.Equal(ColumnDirectoryNames, table.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
                foreach (var name in new[] { "IS_IDENTITY", "IS_COMPUTED", "IS_STORED", "HAS_DEFAULT", "HAS_ON_UPDATE" })
                    Assert.Equal(typeof(bool), table.Columns[name]!.DataType);
                foreach (var name in new[] { "TYPE_DECLARATION", "COMPUTED_EXPRESSION", "DEFAULT_VALUE_SQL", "ON_UPDATE_SQL", "COLLATION_NAME", "COLUMN_COMMENT" })
                    Assert.Equal(typeof(string), table.Columns[name]!.DataType);
                var row = Assert.Single(table.Rows.Cast<DataRow>());
                Assert.Equal(values[5] ?? (object)DBNull.Value, row["COLUMN_DEFAULT"]);
                Assert.Equal(identity ?? DBNull.Value, row["IS_IDENTITY"]); Assert.Equal(computed ?? DBNull.Value, row["IS_COMPUTED"]);
                Assert.Equal(stored ?? DBNull.Value, row["IS_STORED"]); Assert.Equal(expression ?? DBNull.Value, row["COMPUTED_EXPRESSION"]);
                Assert.Equal(hasDefault ?? DBNull.Value, row["HAS_DEFAULT"]); Assert.Equal(sql ?? DBNull.Value, row["DEFAULT_VALUE_SQL"]);
                Assert.Equal(hasUpdate ?? DBNull.Value, row["HAS_ON_UPDATE"]); Assert.Equal(update ?? DBNull.Value, row["ON_UPDATE_SQL"]);
                Assert.Equal("binary", row["COLLATION_NAME"]); Assert.Equal("", row["COLUMN_COMMENT"]);
                Assert.Equal("Table_'\"_%", row["TABLE_NAME"]); Assert.Equal("Col_'\"_%", row["COLUMN_NAME"]);
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("VARCHAR2", "12", "B", null, null, "VARCHAR2(12 BYTE)")]
        [InlineData("VARCHAR2", "12", "C", null, null, "VARCHAR2(12 CHAR)")]
        [InlineData("CHAR", "4", "B", null, null, "CHAR(4 BYTE)")]
        [InlineData("NCHAR", "4", "C", null, null, "NCHAR(4)")]
        [InlineData("NVARCHAR2", "4", "C", null, null, "NVARCHAR2(4)")]
        [InlineData("RAW", "24", null, null, null, "RAW(24)")]
        [InlineData("NUMBER", null, null, "18", "4", "NUMBER(18,4)")]
        [InlineData("NUMBER", null, null, "18", "-2", "NUMBER(18,-2)")]
        [InlineData("NUMBER", null, null, null, "2", "NUMBER(*,2)")]
        [InlineData("NUMBER", null, null, null, null, "NUMBER")]
        [InlineData("NUMBER", null, null, null, "-85", "NUMBER")]
        [InlineData("FLOAT", null, null, "63", null, "FLOAT(63)")]
        [InlineData("TIMESTAMP(9) WITH TIME ZONE", null, null, null, null, "TIMESTAMP(9) WITH TIME ZONE")]
        [InlineData("TIMESTAMP(3) WITH LOCAL TIME ZONE", null, null, null, null, "TIMESTAMP(3) WITH LOCAL TIME ZONE")]
        [InlineData("INTERVAL YEAR(4) TO MONTH", null, null, null, null, "INTERVAL YEAR(4) TO MONTH")]
        [InlineData("INTERVAL DAY(4) TO SECOND(9)", null, null, null, null, "INTERVAL DAY(4) TO SECOND(9)")]
        [InlineData("DATE", null, null, null, null, "DATE")]
        [InlineData("VARCHAR2", "12", null, null, null, null)]
        [InlineData("RAW", null, null, null, null, null)]
        [InlineData("NUMBER", null, null, "39", "2", null)]
        [InlineData("UDT", null, null, null, null, null)]
        public async Task ColumnMetadataOracleTypeDeclarationsUseActualComponents(string type, string? length, string? semantics, string? precision, string? scale, string? expected)
        {
            var values = MetadataValues(false, type, null, "NO", length: length, semantics: semantics, precision: precision, scale: scale);
            if (length != null) values[13] = length + ".0";
            var server = Server(async channel =>
            {
                for (var i = 0; i < 2; i++) { await StatementCommand(channel, 3); foreach (var packet in DirectoryPackets(true, false, nativeNumbers: true, sourceValues: values)) await channel.WritePacketAsync(packet); }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var sync = connection.GetSchema("Columns"); using var asyncTable = await connection.GetSchemaAsync("Columns");
                Assert.Equal(expected ?? (object)DBNull.Value, sync.Rows[0]["TYPE_DECLARATION"]);
                Assert.Equal(expected ?? (object)DBNull.Value, asyncTable.Rows[0]["TYPE_DECLARATION"]);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task ColumnMetadataMissingOptionalFieldFallsBackWithoutInventingFlags(bool asynchronous, bool mysql)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 3);
                var code = mysql ? 1054 : 904;
                var message = Encoding.UTF8.GetBytes(mysql ? "Unknown column 'GENERATION_EXPRESSION' in 'field list'" : "\"V\".\"VIRTUAL_COLUMN\": invalid identifier");
                await channel.WritePacketAsync(new byte[] { 255, (byte)code, (byte)(code >> 8), 35, 52, 50, 83, 50, 50 }.Concat(message).ToArray());
                var request = await StatementCommand(channel, 3);
                Assert.DoesNotContain(mysql ? "GENERATION_EXPRESSION AS" : "ALL_TAB_COLS v", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                var values = MetadataValues(mysql);
                for (var i = 12; i < values.Length; i++) values[i] = null;
                foreach (var packet in DirectoryPackets(true, mysql, sourceValues: values)) await channel.WritePacketAsync(packet);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var table = asynchronous ? await connection.GetSchemaAsync("Columns") : connection.GetSchema("Columns");
                Assert.Equal(23, table.Columns.Count); var row = Assert.Single(table.Rows.Cast<DataRow>());
                foreach (var name in new[] { "IS_COMPUTED", "IS_IDENTITY", "HAS_DEFAULT", "HAS_ON_UPDATE" }) Assert.Equal(DBNull.Value, row[name]);
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("bigint(20) unsigned")]
        [InlineData("decimal(18,4) unsigned")]
        [InlineData("varchar(120)")]
        [InlineData("datetime(6)")]
        [InlineData("enum('a,b','it''s')")]
        public async Task ColumnMetadataMySqlPreservesServerDeclaration(string declaration)
        {
            var server = Server(async channel =>
            {
                for (var i = 0; i < 2; i++)
                {
                    await StatementCommand(channel, 3);
                    foreach (var packet in DirectoryPackets(true, true, sourceValues: MetadataValues(true, declaration: declaration))) await channel.WritePacketAsync(packet);
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                using var sync = connection.GetSchema("Columns"); using var asyncTable = await connection.GetSchemaAsync("Columns");
                Assert.Equal(declaration, sync.Rows[0]["TYPE_DECLARATION"]); Assert.Equal(declaration, asyncTable.Rows[0]["TYPE_DECLARATION"]);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task ColumnMetadataRestrictionsSurvivePreparedFallback(bool asynchronous, bool mysql)
        {
            const string tableName = "a_%'\"目标", columnName = "b_%'\"目标";
            var server = Server(async channel =>
            {
                for (byte statement = 80; statement < 82; statement++)
                {
                    var request = await StatementCommand(channel, 22); var query = Encoding.UTF8.GetString(request, 1, request.Length - 1);
                    Assert.DoesNotContain(tableName, query); Assert.DoesNotContain(columnName, query);
                    Assert.Contains(mysql ? "TABLE_SCHEMA = ?" : "OWNER = ?", query);
                    Assert.Contains("COLUMN_NAME = ?", query);
                    await Prepared(channel, statement, 3);
                    var execute = await StatementCommand(channel, 23);
                    Assert.Equal(PreparedParameters.Execute(statement, PreparedParameters.Encode(new[] {
                        new OceanBaseParameter { Value = "owner" }, new OceanBaseParameter { Value = tableName }, new OceanBaseParameter { Value = columnName }
                    }, mysql ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle)), execute);
                    if (statement == 80)
                    {
                        var code = mysql ? 1054 : 904;
                        await channel.WritePacketAsync(new byte[] { 255, (byte)code, (byte)(code >> 8), 35, 52, 50, 83, 50, 50 });
                    }
                    else foreach (var packet in DirectoryPackets(true, mysql, binary: true)) await channel.WritePacketAsync(packet);
                    await StatementCommand(channel, 25);
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                var restrictions = mysql ? new[] { "owner", null, tableName, columnName } : new[] { null, "owner", tableName, columnName };
                using var table = asynchronous ? await connection.GetSchemaAsync("Columns", restrictions) : connection.GetSchema("Columns", restrictions);
                Assert.Single(table.Rows.Cast<DataRow>()); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task ColumnMetadataLegacyDirectoryErrorsPropagateAfterOneFallback(bool asynchronous, bool mysql)
        {
            var code = mysql ? 1054 : 904;
            var server = Server(async channel =>
            {
                for (var i = 0; i < 2; i++)
                {
                    await StatementCommand(channel, 3);
                    await channel.WritePacketAsync(new byte[] { 255, (byte)code, (byte)(code >> 8), 35, 52, 50, 83, 50, 50 });
                }
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? connection.GetSchemaAsync("Columns") : Task.FromResult(connection.GetSchema("Columns")));
                Assert.Equal(code, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ColumnMetadataRejectsFractionalRawLengthInsteadOfRounding(bool asynchronous)
        {
            var values = MetadataValues(false, "RAW", null, "NO", length: "24"); values[13] = "24.5";
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 3);
                foreach (var packet in DirectoryPackets(true, false, nativeNumbers: true, sourceValues: values)) await channel.WritePacketAsync(packet);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                await Assert.ThrowsAsync<InvalidCastException>(() => asynchronous ? connection.GetSchemaAsync("Columns") : Task.FromResult(connection.GetSchema("Columns")));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
