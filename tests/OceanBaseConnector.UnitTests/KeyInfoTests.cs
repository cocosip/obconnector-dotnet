using System;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static async Task KeyPrepare(PacketChannel channel, byte id, params byte[][] columns)
        {
            await channel.WritePacketAsync(new byte[] { 0, id, 0, 0, 0, (byte)columns.Length, 0, 0, 0, 0, 0, 0 });
            foreach (var column in columns) await channel.WritePacketAsync(column);
            if (columns.Length != 0) await channel.WritePacketAsync(Eof());
        }

        private static async Task KeyDirectory(PacketChannel channel, bool mysql, params string?[][] rows)
        {
            var request = await StatementCommand(channel, 22);
            var sql = Encoding.UTF8.GetString(request, 1, request.Length - 1);
            Assert.Contains(mysql ? "INFORMATION_SCHEMA.STATISTICS" : "ALL_INDEXES", sql);
            if (!mysql)
            {
                Assert.Contains("CAST(LENGTH(S.OWNER) AS VARCHAR2(20))", sql);
                Assert.Contains("ALL_CONS_COLUMNS", sql);
                Assert.Contains("C.DEFERRABLE = 'NOT DEFERRABLE'", sql);
                Assert.Contains("ORDER BY K.KEY_ID, K.KEY_POSITION", sql);
            }
            Assert.DoesNotContain("租户库", sql);
            await Prepared(channel, 92, 2);
            var parameters = new[] { new OceanBaseParameter { Value = "租户库" }, new OceanBaseParameter { Value = "ledger" } };
            Assert.Equal(PreparedParameters.Execute(92, PreparedParameters.Encode(parameters, mysql ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle)), await StatementCommand(channel, 23));
            await channel.WritePacketAsync(new byte[] { 8 });
            for (var i = 0; i < 8; i++) await channel.WritePacketAsync(Column("k" + i, 253));
            await channel.WritePacketAsync(Eof());
            foreach (var values in rows)
            {
                using var row = new System.IO.MemoryStream(); row.WriteByte(0);
                var nulls = new byte[2];
                for (var i = 0; i < values.Length; i++) if (values[i] == null) nulls[(i + 2) / 8] |= (byte)(1 << ((i + 2) % 8));
                row.Write(nulls);
                foreach (var value in values) if (value != null) row.Write(WithLength(Encoding.UTF8.GetBytes(value)));
                await channel.WritePacketAsync(row.ToArray());
            }
            await channel.WritePacketAsync(Eof());
            Assert.Equal(new byte[] { 25, 92, 0, 0, 0 }, await StatementCommand(channel, 25));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task KeyInfoReportsCompositePrimaryWithoutFalseSingleColumnUniqueness(bool asynchronous, bool mysql)
        {
            var columns = new[] { SchemaColumn("first_alias", 3, 11, flags: 1, originalName: "first"), SchemaColumn("second", 3, 11, flags: 1, originalName: "second") };
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await KeyPrepare(channel, 91, columns);
                await KeyDirectory(channel, mysql,
                    new string?[] { "租户库", "ledger", "PK", "P", "first", "1", "NO", "YES" },
                    new string?[] { "租户库", "ledger", "PK", "P", "second", "2", "NO", "YES" });
                Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var command = connection.CreateCommand();
                command.CommandText = mysql ? "select l.first AS first_alias, l.second from `租户库`.ledger l" : "select l.first AS first_alias, l.second from \"租户库\".\"ledger\" l";
                using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo) : command.ExecuteReader(CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo);
                Assert.Equal(2, reader.FieldCount); Assert.Equal(2, reader.VisibleFieldCount);
                foreach (var column in reader.GetColumnSchema()) { Assert.True(column.IsKey); Assert.False(column.IsUnique); Assert.False(column.AllowDBNull); Assert.False(column.IsHidden); }
                Assert.True(reader.GetColumnSchema()[0].IsAliased);
                using var table = reader.GetSchemaTable(); Assert.Equal(true, table!.Rows[0]["IsKey"]); Assert.Equal(false, table.Rows[1]["IsUnique"]);
                Assert.False(reader.Read());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public async Task KeyInfoAppendsHiddenCompositeKeysAndKeepsOriginalPreparedCache(bool asynchronous, bool mysql, bool schemaOnly)
        {
            var source = mysql ? "`租户库`.ledger" : "\"租户库\".\"ledger\"";
            var sql = "select l.payload AS __ob_key_0 from " + source + " l where l.payload IS NOT NULL ORDER BY l.payload";
            var quote = mysql ? "`" : "\"";
            var prefix = SchemaColumn("__ob_key_0", 253, 80, originalName: "payload");
            var columns = new[] { prefix, SchemaColumn("__ob_key_1", 8, 20, flags: 1, originalName: "id"), SchemaColumn("__ob_key_2", 8, 20, flags: 1, originalName: "part") };
            var server = Server(async channel =>
            {
                var prepare = await StatementCommand(channel, 22);
                Assert.Equal(sql, Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1)); await KeyPrepare(channel, 91, prefix);
                for (var iteration = 0; iteration < 2; iteration++)
                {
                    if (iteration != 0) Assert.Equal(new byte[] { 25, 93, 0, 0, 0 }, await StatementCommand(channel, 25));
                    await KeyDirectory(channel, mysql,
                        new string?[] { "租户库", "ledger", "PK", "P", "id", "1", "NO", "YES" },
                        new string?[] { "租户库", "ledger", "PK", "P", "part", "2", "NO", "YES" });
                    var rewritten = await StatementCommand(channel, 22);
                    var text = Encoding.UTF8.GetString(rewritten, 1, rewritten.Length - 1);
                    Assert.Contains(", l." + quote + "id" + quote + " AS " + quote + "__ob_key_1" + quote, text);
                    Assert.Contains(", l." + quote + "part" + quote + " AS " + quote + "__ob_key_2" + quote, text);
                    Assert.EndsWith("from " + source + " l where l.payload IS NOT NULL ORDER BY l.payload", text);
                    await KeyPrepare(channel, 93, columns);
                    if (schemaOnly) continue;
                    Assert.Equal(PreparedParameters.Execute(93, Array.Empty<byte>(), cursor: mysql), await StatementCommand(channel, 23));
                    await channel.WritePacketAsync(new byte[] { 3 });
                    foreach (var column in columns) await channel.WritePacketAsync(column);
                    await channel.WritePacketAsync(Eof(mysql ? (byte)70 : (byte)6));
                    if (mysql) Assert.Equal(QueryResult.CursorFetchPacket(93, 3), await StatementCommand(channel, 28));
                    await channel.WritePacketAsync(new byte[] { 0, 0, 1, 120, 7, 0, 0, 0, 0, 0, 0, 0, 9, 0, 0, 0, 0, 0, 0, 0 });
                    await channel.WritePacketAsync(Eof(mysql ? (byte)134 : (byte)6));
                }
                Assert.Equal(new byte[] { 25, 93, 0, 0, 0 }, await StatementCommand(channel, 25));
                Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var command = connection.CreateCommand(); command.CommandText = sql;
                if (mysql && !schemaOnly) SetFetchSize(command, 3);
                var behavior = CommandBehavior.KeyInfo | CommandBehavior.SequentialAccess | (schemaOnly ? CommandBehavior.SchemaOnly : CommandBehavior.Default);
                for (var iteration = 0; iteration < 2; iteration++)
                {
                    using var reader = asynchronous ? await command.ExecuteReaderAsync(behavior) : command.ExecuteReader(behavior);
                    Assert.Equal(3, reader.FieldCount); Assert.Equal(1, reader.VisibleFieldCount);
                    var metadata = reader.GetColumnSchema(); Assert.False(metadata[0].IsKey); Assert.False(metadata[0].IsHidden);
                    for (var i = 1; i < 3; i++) { Assert.True(metadata[i].IsKey); Assert.True(metadata[i].IsHidden); Assert.False(metadata[i].IsUnique); }
                    if (!schemaOnly)
                    {
                        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                        Assert.Equal("x", reader.GetString(0)); Assert.Equal(7, reader.GetInt64(1)); Assert.Equal(9, reader.GetInt64(2));
                    }
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult()); Assert.Equal(0, reader.VisibleFieldCount);
                }
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                Assert.Equal(sql, command.CommandText);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public async Task KeyInfoDistinguishesNullableUniqueConstraintFromUsableRowKey(bool asynchronous, bool mysql, bool nullable)
        {
            var column = SchemaColumn("amount", 8, 20, flags: nullable ? (ushort)0 : (ushort)1);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await KeyPrepare(channel, 91, column);
                await KeyDirectory(channel, mysql, new string?[] { "租户库", "ledger", "UQ", "U", "amount", "1", nullable ? "YES" : "NO", "YES" });
                Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var command = connection.CreateCommand(); command.CommandText = mysql ? "select amount from `租户库`.ledger" : "select amount from \"租户库\".\"ledger\"";
                using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.KeyInfo | CommandBehavior.SchemaOnly) : command.ExecuteReader(CommandBehavior.KeyInfo | CommandBehavior.SchemaOnly);
                var metadata = Assert.Single(reader.GetColumnSchema());
                Assert.Equal(!nullable, metadata.IsKey); Assert.True(metadata.IsUnique); Assert.Equal(nullable, metadata.AllowDBNull); Assert.False(metadata.IsHidden); Assert.Null(metadata.IsReadOnly);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public static IEnumerable<object[]> UnknownKeyQueries()
        {
            foreach (var mysql in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var sql in new[] { "select distinct amount from ledger", "select amount from ledger join other on ledger.id=other.id", "select count(*) from ledger", "select amount from ledger group by amount", "select amount from ledger union select amount from other", "select amount from (select * from ledger) l" })
                        yield return new object[] { asynchronous, mysql, sql };
        }

        [Theory]
        [MemberData(nameof(UnknownKeyQueries))]
        public async Task KeyInfoPreservesComplexQueryAndNeverInventsResultKeys(bool asynchronous, bool mysql, string sql)
        {
            var column = SchemaColumn("amount", 8, 20, flags: 3);
            var server = Server(async channel =>
            {
                var prepare = await StatementCommand(channel, 22); Assert.Equal(sql, Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                await KeyPrepare(channel, 91, column);
                Assert.Equal(PreparedParameters.Execute(91, Array.Empty<byte>()), await StatementCommand(channel, 23));
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(column); await channel.WritePacketAsync(Eof()); await channel.WritePacketAsync(Eof());
                Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var command = connection.CreateCommand(); command.CommandText = sql;
                using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.KeyInfo | CommandBehavior.SingleResult) : command.ExecuteReader(CommandBehavior.KeyInfo | CommandBehavior.SingleResult);
                var metadata = Assert.Single(reader.GetColumnSchema()); Assert.Null(metadata.IsKey); Assert.Null(metadata.IsUnique); Assert.Null(metadata.IsExpression); Assert.Null(metadata.IsReadOnly);
                Assert.False(metadata.IsHidden); Assert.Equal(1, reader.VisibleFieldCount); Assert.False(reader.Read());
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult()); Assert.Equal(0, reader.VisibleFieldCount);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task KeyInfoDirectoryPermissionFailureDoesNotExecuteTargetOrReportUnknownOutcome(bool asynchronous, bool mysql)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await KeyPrepare(channel, 91, SchemaColumn("amount", 8, 20));
                await StatementCommand(channel, 22); await channel.WritePacketAsync(Error());
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1));
                Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var command = (OceanBaseCommand)connection.CreateCommand();
                command.CommandText = mysql ? "select amount from `租户库`.ledger" : "select amount from \"租户库\".\"ledger\"";
                var error = asynchronous ? await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteReaderAsync(CommandBehavior.KeyInfo)) : Assert.Throws<OceanBaseException>(() => command.ExecuteReader(CommandBehavior.KeyInfo));
                Assert.Equal(1064, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown); Assert.False(command.HasSentSql);
                using var after = connection.CreateCommand(); after.CommandText = "after metadata error"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task KeyInfoDoesNotClaimKeysForViewsOrUnavailableBaseTable(bool asynchronous, bool mysql)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await KeyPrepare(channel, 91, SchemaColumn("amount", 8, 20));
                await KeyDirectory(channel, mysql);
                Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                using var command = connection.CreateCommand(); command.CommandText = mysql ? "select amount from `租户库`.ledger" : "select amount from \"租户库\".\"ledger\"";
                using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.KeyInfo | CommandBehavior.SchemaOnly) : command.ExecuteReader(CommandBehavior.KeyInfo | CommandBehavior.SchemaOnly);
                Assert.Null(Assert.Single(reader.GetColumnSchema()).IsKey); Assert.Equal(1, reader.VisibleFieldCount);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task KeyInfoUsesExecutionMetadataForParameterExpressions(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 91, 1, 1);
                await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(Column("value", 253)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 1, 120 }); await channel.WritePacketAsync(Eof());
                Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select :p AS value";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = "x" });
                using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.KeyInfo) : command.ExecuteReader(CommandBehavior.KeyInfo);
                Assert.Equal("value", reader.GetName(0)); Assert.Equal(typeof(string), reader.GetFieldType(0));
                Assert.Null(Assert.Single(reader.GetColumnSchema()).IsKey); Assert.True(reader.Read()); Assert.Equal("x", reader.GetString(0)); Assert.False(reader.Read());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task KeyInfoCancellationDuringDirectoryLookupDoesNotUploadInputStream(bool asynchronous)
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await channel.WritePacketAsync(new byte[] { 0, 91, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0 });
                await channel.WritePacketAsync(Column("p", 253)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(SchemaColumn("amount", 8, 20)); await channel.WritePacketAsync(Eof());
                await StatementCommand(channel, 22); pending.SetResult();
                await Assert.ThrowsAnyAsync<Exception>(() => channel.ReadPacketAsync());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.CommandText = "select amount from \"租户库\".\"ledger\" where value = :p";
                using var source = new System.IO.MemoryStream(new byte[] { 1, 2, 3 });
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = source });
                var executing = asynchronous ? command.ExecuteReaderAsync(CommandBehavior.KeyInfo) : Task.Run(() => command.ExecuteReader(CommandBehavior.KeyInfo));
                await pending.Task.WaitAsync(TimeSpan.FromSeconds(10)); command.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var reader = await executing; });
                Assert.Equal(0, source.Position); Assert.True(source.CanRead); Assert.False(command.HasSentSql); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
