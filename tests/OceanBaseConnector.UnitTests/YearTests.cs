using System;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static ResultColumn YearColumn(OceanBaseCompatibilityMode mode = OceanBaseCompatibilityMode.MySql, uint length = 4, byte scale = 0)
            => ResultColumn.Parse(SchemaColumn("y", 13, length, scale: scale), mode);

        [Theory]
        [InlineData(0)]
        [InlineData(1901)]
        [InlineData(2024)]
        [InlineData(2155)]
        public void YearRetainsExactNumericValueInBothProtocolsAndParameters(ushort year)
        {
            var column = YearColumn();
            Assert.Equal(typeof(ushort), column.FieldType);
            Assert.Equal(typeof(ushort), column.ProviderSpecificFieldType);
            Assert.Equal("YEAR", column.DataTypeName);
            Assert.Equal(OceanBaseDbType.Year, column.ProviderType);
            Assert.Equal(year, column.Decode(Encoding.ASCII.GetBytes(year.ToString("D4", CultureInfo.InvariantCulture))));
            var cursor = new ByteCursor(new byte[] { (byte)year, (byte)(year >> 8), 99 });
            Assert.Equal(year, column.DecodeBinary(column.ReadBinaryField(cursor)));
            Assert.Equal(99, cursor.ReadUInt8());
            Assert.Equal(DBNull.Value, column.Decode(null));
            Assert.Equal(DBNull.Value, column.DecodeBinary(null));
            var schema = new ResultDbColumn(column, 0);
            Assert.Equal(4, schema.ColumnSize); Assert.Equal(4, schema.NumericPrecision); Assert.Equal(0, schema.NumericScale);
            Assert.Equal((int)OceanBaseDbType.Year, schema["ProviderType"]);
            var parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Year, Value = year };
            Assert.Equal(DbType.UInt16, parameter.DbType);
            // Bind as a numeric SMALLINT, as in the pinned Java ShortParameter, without year-window conversion.
            Assert.Equal(new byte[] { 0, 1, 2, 128, (byte)year, (byte)(year >> 8) }, PreparedParameters.Encode(new[] { parameter }));
            parameter.Value = DBNull.Value;
            Assert.Equal(new byte[] { 1, 1, 6, 0 }, PreparedParameters.Encode(new[] { parameter }));
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            parameter.ResetDbType(); parameter.Value = ushort.MaxValue;
            Assert.Equal(new byte[] { 0, 1, 2, 128, 255, 255 }, PreparedParameters.Encode(new[] { parameter }));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(69)]
        [InlineData(99)]
        [InlineData(1900)]
        [InlineData(2156)]
        [InlineData(65535)]
        public void YearRejectsOutOfRangeValuesWithoutNormalizing(ushort year)
        {
            Assert.Throws<FormatException>(() => YearColumn().Decode(Encoding.ASCII.GetBytes(year.ToString("D4", CultureInfo.InvariantCulture))));
            Assert.Throws<FormatException>(() => YearColumn().DecodeBinary(new byte[] { (byte)year, (byte)(year >> 8) }));
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Year, Value = year } }));
        }

        [Theory]
        [InlineData("")]
        [InlineData("0")]
        [InlineData("24")]
        [InlineData("+2024")]
        [InlineData(" 2024")]
        [InlineData("2024 ")]
        [InlineData("2e03")]
        [InlineData("２０２４")]
        public void YearRejectsMalformedText(string text)
            => Assert.Throws<FormatException>(() => YearColumn().Decode(Encoding.UTF8.GetBytes(text)));

        [Fact]
        public void YearRejectsUnsupportedMetadataNullsAndCoercion()
        {
            foreach (var column in new[] { YearColumn(OceanBaseCompatibilityMode.Oracle), YearColumn(length: 2), YearColumn(scale: 1) })
            {
                Assert.Throws<NotSupportedException>(() => column.Decode(null));
                Assert.Throws<NotSupportedException>(() => column.DecodeBinary(null));
            }
            foreach (var value in new object[] { "2024", 2024, (short)2024, 2024m, new DateTime(2024, 1, 1) })
                Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Year, Value = value } }));
            foreach (var value in new object?[] { null, DBNull.Value, (ushort)2024 })
            {
                var parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Year, Value = value, Scale = 1 };
                Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
                parameter.Scale = 0; parameter.Precision = 2;
                Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
            }
            foreach (var bytes in new[] { Array.Empty<byte>(), new byte[] { 1 }, new byte[] { 0, 0, 0 } })
                Assert.Throws<ProtocolException>(() => YearColumn().DecodeBinary(bytes));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task YearInvalidParametersFailBeforePrepareAndLeaveSessionUsable(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 116, 1);
                var execute = await StatementCommand(channel, 23);
                Assert.Equal(new byte[] { 0, 1, 2, 128, 0, 0 }, execute.AsSpan(10).ToArray());
                await channel.WritePacketAsync(Ok(1)); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand(); command.CommandText = "select :y";
                var parameter = new OceanBaseParameter { ParameterName = "y", OceanBaseDbType = OceanBaseDbType.Year, Value = DBNull.Value };
                command.Parameters.Add(parameter);
                Func<Task> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
                await Assert.ThrowsAsync<NotSupportedException>(execute);
                UseInternalMySqlFixture(connection);
                parameter.Value = (ushort)1900; await Assert.ThrowsAsync<ArgumentException>(execute);
                parameter.Value = "2024"; await Assert.ThrowsAsync<ArgumentException>(execute);
                parameter.Value = (ushort)0;
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, true)]
        public async Task YearReaderAndWriteBackKeepValuesNullAndFollowingField(bool asynchronous, bool binary, bool sequential)
        {
            var years = new ushort[] { 0, 1901, 2155 };
            var server = Server(async channel =>
            {
                if (binary) { await StatementCommand(channel, 22); await Prepared(channel, 112, 0); await StatementCommand(channel, 23); }
                else await Query(channel, "years");
                await channel.WritePacketAsync(new byte[] { 2 });
                await channel.WritePacketAsync(SchemaColumn("y", 13, 4));
                await channel.WritePacketAsync(SchemaColumn("tail", 253, 4)); await channel.WritePacketAsync(Eof());
                foreach (var year in years)
                {
                    var text = Encoding.ASCII.GetBytes(year.ToString("D4", CultureInfo.InvariantCulture));
                    await channel.WritePacketAsync(binary ? new byte[] { 0, 0, (byte)year, (byte)(year >> 8), 1, 120 }
                        : new byte[] { 4, text[0], text[1], text[2], text[3], 1, 120 });
                }
                await channel.WritePacketAsync(binary ? new byte[] { 0, 4, 1, 120 } : new byte[] { 251, 1, 120 });
                await channel.WritePacketAsync(Eof());
                if (binary) await StatementCommand(channel, 25);
                await StatementCommand(channel, 22); await Prepared(channel, 113, 1);
                foreach (var year in years)
                {
                    var execute = await StatementCommand(channel, 23);
                    Assert.Equal(new byte[] { 0, 1, 2, 128, (byte)year, (byte)(year >> 8) }, execute.AsSpan(10).ToArray());
                    await channel.WritePacketAsync(Ok(1));
                }
                var nullExecute = await StatementCommand(channel, 23);
                Assert.Equal(new byte[] { 1, 1, 6, 0 }, nullExecute.AsSpan(10).ToArray());
                await channel.WritePacketAsync(Ok(1)); await StatementCommand(channel, 25);
                channel.BeginCommand();
                await Query(channel, "reuse"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "years";
                    if (binary) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                    using var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                        : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                    Assert.Equal(typeof(ushort), reader.GetFieldType(0)); Assert.Equal(typeof(ushort), reader.GetProviderSpecificFieldType(0));
                    Assert.Equal("YEAR", reader.GetDataTypeName(0));
                    var schema = reader.GetColumnSchema()[0];
                    Assert.Equal(4, schema.NumericPrecision); Assert.Equal(0, schema.NumericScale);
                    Assert.Equal((int)OceanBaseDbType.Year, schema["ProviderType"]);
                    Assert.Equal((int)OceanBaseDbType.Year, reader.GetSchemaTable()!.Rows[0]["ProviderType"]);
                    foreach (var year in years)
                    {
                        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                        Assert.Equal(year, reader.GetValue(0)); Assert.Equal(year, reader.GetProviderSpecificValue(0));
                        Assert.Equal(year, await reader.GetFieldValueAsync<ushort>(0));
                        Assert.Throws<InvalidCastException>(() => reader.GetDateTime(0));
                        Assert.Equal("x", reader.GetString(1));
                    }
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.True(reader.IsDBNull(0)); Assert.Equal(DBNull.Value, reader.GetValue(0));
                    Assert.Equal(DBNull.Value, reader.GetProviderSpecificValue(0)); Assert.Equal("x", reader.GetString(1));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
                using (var insert = connection.CreateCommand())
                {
                    insert.CommandText = "select :y";
                    var parameter = new OceanBaseParameter { ParameterName = "y", OceanBaseDbType = OceanBaseDbType.Year };
                    insert.Parameters.Add(parameter);
                    foreach (var year in years)
                    {
                        parameter.Value = year;
                        Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
                    }
                    parameter.Value = DBNull.Value;
                    Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
                }
                using var reuse = connection.CreateCommand(); reuse.CommandText = "reuse";
                Assert.Equal(1, await reuse.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
