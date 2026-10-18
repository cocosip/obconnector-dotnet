using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static byte[] SchemaColumn(string name, byte type, uint length, byte precision = 0, byte scale = 0,
            ushort charset = 45, ushort flags = 0, string originalName = "amount", string originalTable = "ledger")
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            foreach (var text in new[] { "def", "租户库", "l", originalTable, name, originalName })
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                writer.Write((byte)bytes.Length); writer.Write(bytes);
            }
            writer.Write((byte)12); writer.Write(charset); writer.Write(length); writer.Write(type);
            writer.Write(flags); writer.Write(scale); writer.Write(precision); writer.Write((byte)0);
            return stream.ToArray();
        }

        [Theory]
        [InlineData(OceanBaseCompatibilityMode.MySql, 33, 120U, 40)]
        [InlineData(OceanBaseCompatibilityMode.MySql, 45, 120U, 30)]
        [InlineData(OceanBaseCompatibilityMode.MySql, 46, 120U, 30)]
        [InlineData(OceanBaseCompatibilityMode.MySql, 83, 120U, 40)]
        [InlineData(OceanBaseCompatibilityMode.MySql, 224, 120U, 30)]
        [InlineData(OceanBaseCompatibilityMode.MySql, 255, 120U, 30)]
        [InlineData(OceanBaseCompatibilityMode.MySql, 63, 120U, 120)]
        [InlineData(OceanBaseCompatibilityMode.MySql, 45, uint.MaxValue, 1073741823)]
        [InlineData(OceanBaseCompatibilityMode.MySql, 63, uint.MaxValue, int.MaxValue)]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 45, 120U, 120)]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 45, uint.MaxValue, int.MaxValue)]
        public void ColumnSchemaUsesModeAndCharsetLengthUnits(OceanBaseCompatibilityMode mode, ushort charset, uint length, int size)
        {
            var definition = ResultColumn.Parse(SchemaColumn("amount", 253, length, charset: charset), mode);
            var column = new ResultDbColumn(definition, 7);
            Assert.Equal(size, column.ColumnSize);
            Assert.Equal(7, column.ColumnOrdinal);
            Assert.Equal("租户库", mode == OceanBaseCompatibilityMode.MySql ? column.BaseCatalogName : column.BaseSchemaName);
            Assert.Null(mode == OceanBaseCompatibilityMode.MySql ? column.BaseSchemaName : column.BaseCatalogName);
            Assert.False(column.IsAliased);
            Assert.Equal("l", definition.Table);
            Assert.Equal(length, definition.Length);
            Assert.Equal(charset == 63 ? typeof(byte[]) : typeof(string), column.DataType);
        }

        [Theory]
        [InlineData(OceanBaseCompatibilityMode.MySql, 8U, 0)]
        [InlineData(OceanBaseCompatibilityMode.MySql, 7U, 32)]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 8U, 0)]
        public void DecimalSchemaPreservesDeclaredPrecisionAndFlags(OceanBaseCompatibilityMode mode, uint length, ushort flags)
        {
            var column = new ResultDbColumn(ResultColumn.Parse(SchemaColumn("n", 246, length, 6, 2, flags: flags), mode), 0);
            Assert.Equal(6, column.NumericPrecision);
            Assert.Equal(2, column.NumericScale);
            Assert.Equal(typeof(decimal), column.DataType);
            Assert.Equal(typeof(OceanBaseDecimal), column["ProviderSpecificDataType"]);
            Assert.True(column.AllowDBNull);
            var identity = new ResultDbColumn(ResultColumn.Parse(SchemaColumn("id", 3, 11, flags: 513), OceanBaseCompatibilityMode.MySql), 0);
            Assert.False(identity.AllowDBNull);
            Assert.True(identity.IsAutoIncrement);
            Assert.Equal(10, identity.NumericPrecision);
            Assert.Equal(0, identity.NumericScale);
            Assert.Null(identity.IsKey);
            Assert.Null(identity.IsUnique);
            Assert.Null(identity.IsExpression);
            Assert.Equal((int)OceanBaseDbType.Int32, identity["ProviderType"]);
        }

        [Theory]
        [InlineData(1, 0, 3)]
        [InlineData(1, 32, 3)]
        [InlineData(2, 0, 5)]
        [InlineData(2, 32, 5)]
        [InlineData(3, 0, 10)]
        [InlineData(3, 32, 10)]
        [InlineData(9, 0, 7)]
        [InlineData(9, 32, 8)]
        [InlineData(8, 0, 19)]
        [InlineData(8, 32, 20)]
        public void IntegerSchemaPrecisionFollowsWireRangeRatherThanDisplayWidth(byte type, ushort flags, int digits)
        {
            var column = new ResultDbColumn(ResultColumn.Parse(SchemaColumn("n", type, 255, flags: flags), OceanBaseCompatibilityMode.MySql), 0);
            Assert.Equal(digits, column.NumericPrecision);
            Assert.Equal(0, column.NumericScale);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void ColumnMetadataNamesRejectInvalidUtf8(int field)
        {
            var packet = SchemaColumn("name", 253, 20);
            var position = 0;
            for (var i = 0; i < field; i++) position += packet[position] + 1;
            packet[position + 1] = 255;
            Assert.Throws<DecoderFallbackException>(() => ResultColumn.Parse(packet, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task ReaderSchemaSnapshotsMatchValuesAndFollowMultipleResults(bool binary, bool asynchronous)
        {
            var server = Server(async channel =>
            {
                if (binary) { await StatementCommand(channel, 22); await Prepared(channel, 81, 0); await StatementCommand(channel, 23); }
                else await Query(channel, "metadata");
                await channel.WritePacketAsync(new byte[] { 2 });
                await channel.WritePacketAsync(SchemaColumn("金额别名", 246, 8, 6, 2, flags: 1));
                await channel.WritePacketAsync(SchemaColumn("created", 12, 19, originalName: "created"));
                await channel.WritePacketAsync(Eof());
                var number = WithLength(Encoding.ASCII.GetBytes("12.34"));
                var date = binary ? new byte[] { 7, 232, 7, 2, 29, 13, 14, 15 } : WithLength(Encoding.ASCII.GetBytes("2024-02-29 13:14:15"));
                var row = new byte[(binary ? 2 : 0) + number.Length + date.Length];
                number.CopyTo(row, binary ? 2 : 0); date.CopyTo(row, (binary ? 2 : 0) + number.Length);
                await channel.WritePacketAsync(row);
                await channel.WritePacketAsync(binary ? new byte[] { 0, 12 } : new byte[] { 251, 251 });
                await channel.WritePacketAsync(Eof(14));
                await channel.WritePacketAsync(Ok(2, 14));
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(SchemaColumn("text", 253, 20, originalName: "", originalTable: ""));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(binary ? new byte[] { 0, 0, 0 } : new byte[] { 0 });
                await channel.WritePacketAsync(Eof());
                if (binary) await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "metadata";
                if (binary) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
                Assert.True(reader.CanGetColumnSchema());
                var schema = reader.GetColumnSchema();
                Assert.Equal(2, schema.Count);
                var numeric = schema[0];
                Assert.Equal("金额别名", numeric.ColumnName);
                Assert.Equal(0, numeric.ColumnOrdinal);
                Assert.Equal("amount", numeric.BaseColumnName);
                Assert.Equal("ledger", numeric.BaseTableName);
                Assert.Equal("租户库", numeric.BaseSchemaName);
                Assert.Null(numeric.BaseCatalogName);
                Assert.True(numeric.IsAliased);
                Assert.False(numeric.AllowDBNull);
                Assert.Equal(8, numeric.ColumnSize);
                Assert.Equal(6, numeric.NumericPrecision);
                Assert.Equal(2, numeric.NumericScale);
                Assert.Equal(typeof(decimal), numeric.DataType);
                Assert.Equal("NUMBER", numeric.DataTypeName);
                Assert.Equal(typeof(OceanBaseDecimal), numeric["ProviderSpecificDataType"]);
                Assert.Null(numeric.IsUnique);
                Assert.Null(numeric.IsReadOnly);
                Assert.Throws<NotSupportedException>(() => ((IList<DbColumn>)schema).Clear());
                var table = reader.GetSchemaTable()!;
                Assert.NotNull(table);
                Assert.Equal(typeof(decimal), table.Rows[0][SchemaTableColumn.DataType]);
                Assert.Equal(typeof(OceanBaseDecimal), table.Rows[0][SchemaTableOptionalColumn.ProviderSpecificDataType]);
                Assert.Equal((short)6, table.Rows[0][SchemaTableColumn.NumericPrecision]);
                Assert.Equal((int)OceanBaseDbType.Decimal, table.Rows[0][SchemaTableColumn.ProviderType]);
                table.Rows[0][SchemaTableColumn.ColumnName] = "changed by caller";
                Assert.Equal("金额别名", reader.GetSchemaTable()!.Rows[0][SchemaTableColumn.ColumnName]);
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.Equal(12.34m, reader.GetValue(0));
                Assert.IsType<OceanBaseDecimal>(reader.GetProviderSpecificValue(0));
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.True(reader.IsDBNull(0));
                Assert.Equal(typeof(decimal), reader.GetColumnSchema()[0].DataType);
                Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.Null(reader.GetSchemaTable());
                Assert.Empty(reader.GetColumnSchema());
                Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.Equal("text", reader.GetColumnSchema()[0].ColumnName);
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.Equal("", reader.GetString(0));
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                Assert.Empty(reader.GetColumnSchema());
                Assert.Null(reader.GetSchemaTable());
                Assert.False(reader.HasRows);
                Assert.Equal(2, reader.RecordsAffected);
                Assert.Equal("金额别名", schema[0].ColumnName);
                reader.Close();
                Assert.Throws<InvalidOperationException>(() => reader.GetSchemaTable());
                Assert.Throws<InvalidOperationException>(() => reader.GetColumnSchema());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task BinaryReaderOwnsResponseAndCloseConnectionHonorsBothConsumptionPaths(bool consume, bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 82, 0); await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(SchemaColumn("n", 3, 11));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 1, 0, 0, 0 });
                await channel.WritePacketAsync(Eof());
                var closed = await Record.ExceptionAsync(() => channel.ReadPacketAsync());
                Assert.True(closed is ProtocolException || closed is IOException); // Unread TCP data may close with RST instead of FIN.
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "binary lifetime";
                await command.PrepareAsync();
                using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.CloseConnection) : command.ExecuteReader(CommandBehavior.CloseConnection);
                using var second = connection.CreateCommand();
                second.CommandText = "blocked";
                await Assert.ThrowsAsync<InvalidOperationException>(() => second.ExecuteReaderAsync());
                Assert.Single(reader.GetColumnSchema());
                if (consume)
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Equal(1m, reader.GetValue(0));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
                if (asynchronous) await reader.DisposeAsync(); else reader.Close();
                Assert.True(reader.IsClosed);
                Assert.Equal(ConnectionState.Closed, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ScalarConversionFailureDiscardsUnreadResponse(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "invalid scalar");
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(SchemaColumn("n", 246, 8, 6, 2));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(WithLength(Encoding.ASCII.GetBytes("1.234")));
                await Assert.ThrowsAsync<ProtocolException>(() => channel.ReadPacketAsync());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "invalid scalar";
                if (asynchronous) await Assert.ThrowsAsync<OverflowException>(() => command.ExecuteScalarAsync());
                else Assert.Throws<OverflowException>(() => command.ExecuteScalar());
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ZeroRowSelectRetainsDeclaredSchemaUntilNextResultEnds(bool binary)
        {
            var server = Server(async channel =>
            {
                if (binary) { await StatementCommand(channel, 22); await Prepared(channel, 83, 0); await StatementCommand(channel, 23); }
                else await Query(channel, "empty metadata");
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(SchemaColumn("n", 246, 8, 6, 2));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(Eof());
                if (binary) await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "empty metadata";
                if (binary) await command.PrepareAsync();
                using var reader = await command.ExecuteReaderAsync();
                Assert.False(reader.HasRows);
                Assert.Equal(typeof(decimal), Assert.Single(reader.GetColumnSchema()).DataType);
                Assert.Single(reader.GetSchemaTable()!.Rows);
                Assert.False(await reader.ReadAsync());
                Assert.Equal(6, Assert.Single(reader.GetColumnSchema()).NumericPrecision);
                Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
                Assert.False(await reader.NextResultAsync());
                Assert.Empty(reader.GetColumnSchema());
                Assert.Null(reader.GetSchemaTable());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
