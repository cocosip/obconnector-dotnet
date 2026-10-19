using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static ResultColumn BitColumn(uint width, OceanBaseCompatibilityMode mode = OceanBaseCompatibilityMode.MySql, byte scale = 0, ushort flags = 0)
            => ResultColumn.Parse(SchemaColumn("bits", 16, width, scale: scale, charset: 63, flags: flags), mode);

        public static IEnumerable<object[]> BitWidths()
        {
            for (var width = 1; width <= 64; width++) yield return new object[] { width };
        }

        private static byte[] BitBytes(ulong value, int width)
        {
            var bytes = new byte[(width + 7) / 8];
            for (var i = bytes.Length - 1; i >= 0; i--) { bytes[i] = (byte)value; value >>= 8; }
            return bytes;
        }

        [Theory]
        [MemberData(nameof(BitWidths))]
        public void BitEveryWidthRetainsZeroHighBitAndMaximumInBothProtocols(int width)
        {
            var maximum = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
            foreach (var flags in new ushort[] { 0, 32 })
            {
                var column = BitColumn((uint)width, flags: flags);
                Assert.Equal(typeof(ulong), column.FieldType); Assert.Equal(typeof(ulong), column.ProviderSpecificFieldType);
                Assert.Equal(OceanBaseDbType.Bit, column.ProviderType); Assert.Equal("BIT", column.DataTypeName);
                var schema = new ResultDbColumn(column, 0);
                Assert.Equal(width, schema.ColumnSize); Assert.Null(schema.NumericPrecision); Assert.Null(schema.NumericScale);
                Assert.Equal((int)OceanBaseDbType.Bit, schema["ProviderType"]);
                foreach (var value in new[] { 0UL, 1UL, 1UL << (width - 1), maximum })
                {
                    var bytes = BitBytes(value, width);
                    Assert.Equal(value, column.Decode(bytes)); Assert.Equal(value, column.DecodeBinary(bytes));
                    var cursor = new ByteCursor(WithLength(bytes).Concat(new byte[] { 99 }).ToArray());
                    Assert.Equal(value, column.DecodeBinary(column.ReadBinaryField(cursor))); Assert.Equal(99, cursor.ReadUInt8());
                    var parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Bit, Size = width, Value = value };
                    Assert.Equal(DbType.UInt64, parameter.DbType);
                    var encoded = PreparedParameters.Encode(new[] { parameter });
                    Assert.Equal(new byte[] { 0, 1, 8, 128 }, encoded.AsSpan(0, 4).ToArray());
                    var expected = new byte[8];
                    for (var i = 0; i < 8; i++) expected[i] = (byte)(value >> (8 * i));
                    Assert.Equal(expected, encoded.AsSpan(4).ToArray());
                    if (width < 64)
                    {
                        parameter.Value = 1UL << width;
                        Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }));
                        if (width % 8 == 0) Assert.Throws<ProtocolException>(() => column.Decode(BitBytes(1UL << width, width + 1)));
                        else Assert.Throws<FormatException>(() => column.Decode(BitBytes(1UL << width, width + 1)));
                    }
                }
                Assert.Equal(DBNull.Value, column.Decode(null)); Assert.Equal(DBNull.Value, column.DecodeBinary(null));
                Assert.Throws<ProtocolException>(() => column.Decode(new byte[(width + 7) / 8 - 1]));
                Assert.Throws<ProtocolException>(() => column.DecodeBinary(new byte[(width + 7) / 8 + 1]));
            }
        }

        [Fact]
        public void BitUsesBigEndianRawBytesRatherThanAsciiOrSignedValues()
        {
            var column = BitColumn(16);
            Assert.Equal(0x0180UL, column.Decode(new byte[] { 1, 128 }));
            Assert.Equal(0x3132UL, column.Decode(new byte[] { 49, 50 }));
            Assert.Equal(128UL, BitColumn(8).Decode(new byte[] { 128 }));
            Assert.Equal(1UL << 63, BitColumn(64).Decode(new byte[] { 128, 0, 0, 0, 0, 0, 0, 0 }));
        }

        [Theory]
        [InlineData(0, OceanBaseCompatibilityMode.MySql, 0)]
        [InlineData(65, OceanBaseCompatibilityMode.MySql, 0)]
        [InlineData(4294967295, OceanBaseCompatibilityMode.MySql, 0)]
        [InlineData(1, OceanBaseCompatibilityMode.MySql, 1)]
        [InlineData(64, OceanBaseCompatibilityMode.Oracle, 0)]
        public void BitRejectsUnsupportedColumnMetadataIncludingNull(uint width, OceanBaseCompatibilityMode mode, byte scale)
        {
            var column = BitColumn(width, mode, scale);
            Assert.Throws<NotSupportedException>(() => column.Decode(null));
            Assert.Throws<NotSupportedException>(() => column.DecodeBinary(null));
            Assert.Throws<NotSupportedException>(() => new ResultDbColumn(column, 0));
        }

        [Fact]
        public void BitParametersValidateNullMetadataTypesAndResetWithoutAffectingUInt64()
        {
            foreach (var value in new object?[] { null, DBNull.Value, 1UL })
            {
                var parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Bit, Value = value };
                Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
                foreach (var size in new[] { -1, 65, int.MaxValue })
                {
                    parameter.Size = size;
                    Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
                }
                parameter.Size = 0; parameter.Precision = 1;
                Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
                parameter.Precision = 0; parameter.Scale = 1;
                Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
            }
            foreach (var value in new object[] { true, 1, 1L, (uint)1, "1", new byte[] { 1 }, 1m })
                Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Bit, Value = value } }));
            var valid = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Bit, Size = 1, Value = DBNull.Value };
            Assert.Equal(new byte[] { 1, 1, 6, 0 }, PreparedParameters.Encode(new[] { valid }));
            valid.Value = null; Assert.Equal(new byte[] { 1, 1, 6, 0 }, PreparedParameters.Encode(new[] { valid }));
            valid.Size = 0; valid.Value = ulong.MaxValue;
            Assert.Equal(new byte[] { 0, 1, 8, 128, 255, 255, 255, 255, 255, 255, 255, 255 }, PreparedParameters.Encode(new[] { valid }));
            valid.Size = 1; valid.ResetDbType();
            Assert.Null(valid.OceanBaseDbType); Assert.Equal(DbType.UInt64, valid.DbType);
            Assert.Equal(12, PreparedParameters.Encode(new[] { valid }, OceanBaseCompatibilityMode.Oracle).Length);
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
        public async Task BitReaderAndWriteBackPreserveWidthsAndFollowingField(bool asynchronous, bool binary, bool sequential)
        {
            var widths = new[] { 1, 9, 64 };
            var rows = new[] { new[] { 1UL, 256UL, 1UL << 63 }, new[] { 1UL, 511UL, ulong.MaxValue }, new[] { 0UL, 0UL, 0UL } };
            var server = Server(async channel =>
            {
                if (binary) { await StatementCommand(channel, 22); await Prepared(channel, 117, 0); await StatementCommand(channel, 23); }
                else await Query(channel, "bits");
                await channel.WritePacketAsync(new byte[] { 4 });
                foreach (var width in widths) await channel.WritePacketAsync(SchemaColumn("b" + width, 16, (uint)width, charset: 63));
                await channel.WritePacketAsync(SchemaColumn("tail", 253, 4)); await channel.WritePacketAsync(Eof());
                foreach (var row in rows)
                {
                    var packet = new List<byte>(); if (binary) packet.AddRange(new byte[] { 0, 0 });
                    for (var i = 0; i < widths.Length; i++) packet.AddRange(WithLength(BitBytes(row[i], widths[i])));
                    packet.AddRange(new byte[] { 1, 120 }); await channel.WritePacketAsync(packet.ToArray());
                }
                await channel.WritePacketAsync(binary ? new byte[] { 0, 28, 1, 120 } : new byte[] { 251, 251, 251, 1, 120 });
                await channel.WritePacketAsync(Eof()); if (binary) await StatementCommand(channel, 25);
                await StatementCommand(channel, 22); await Prepared(channel, 118, 3);
                foreach (var row in rows)
                {
                    var execute = await StatementCommand(channel, 23);
                    Assert.Equal(new byte[] { 0, 1, 8, 128, 8, 128, 8, 128 }, execute.AsSpan(10, 8).ToArray());
                    Assert.Equal(42, execute.Length);
                    for (var i = 0; i < 3; i++)
                        for (var part = 0; part < 8; part++) Assert.Equal((byte)(row[i] >> (part * 8)), execute[18 + i * 8 + part]);
                    await channel.WritePacketAsync(Ok(1));
                }
                var nullExecute = await StatementCommand(channel, 23);
                Assert.Equal(new byte[] { 7, 1, 6, 0, 6, 0, 6, 0 }, nullExecute.AsSpan(10).ToArray());
                await channel.WritePacketAsync(Ok(1)); await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, "reuse"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "bits";
                    if (binary) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                    using var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                        : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                    var schema = reader.GetColumnSchema(); var table = reader.GetSchemaTable()!;
                    for (var i = 0; i < 3; i++)
                    {
                        Assert.Equal(typeof(ulong), reader.GetFieldType(i)); Assert.Equal(typeof(ulong), reader.GetProviderSpecificFieldType(i));
                        Assert.Equal("BIT", reader.GetDataTypeName(i)); Assert.Equal(widths[i], schema[i].ColumnSize);
                        Assert.Null(schema[i].NumericPrecision); Assert.Null(schema[i].NumericScale);
                        Assert.Equal((int)OceanBaseDbType.Bit, schema[i]["ProviderType"]);
                        Assert.Equal(typeof(ulong), table.Rows[i]["DataType"]); Assert.Equal(widths[i], table.Rows[i]["ColumnSize"]);
                    }
                    foreach (var row in rows)
                    {
                        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                        for (var i = 0; i < 3; i++)
                        {
                            Assert.Equal(row[i], asynchronous ? await reader.GetFieldValueAsync<ulong>(i) : reader.GetFieldValue<ulong>(i));
                            Assert.Equal(row[i], reader.GetValue(i)); Assert.Equal(row[i], reader.GetProviderSpecificValue(i));
                            Assert.Throws<InvalidCastException>(() => reader.GetBoolean(i));
                        }
                        Assert.Equal("x", reader.GetString(3));
                    }
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    for (var i = 0; i < 3; i++) { Assert.True(reader.IsDBNull(i)); Assert.Equal(DBNull.Value, reader.GetValue(i)); }
                    Assert.Equal("x", reader.GetString(3)); Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select :b1, :b9, :b64";
                    foreach (var width in widths) command.Parameters.Add(new OceanBaseParameter { ParameterName = "b" + width, OceanBaseDbType = OceanBaseDbType.Bit, Size = width });
                    foreach (var row in rows)
                    {
                        for (var i = 0; i < 3; i++) command.Parameters[i].Value = row[i];
                        Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                    }
                    foreach (OceanBaseParameter parameter in command.Parameters) parameter.Value = DBNull.Value;
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                }
                using var reuse = connection.CreateCommand(); reuse.CommandText = "reuse"; Assert.Equal(1, await reuse.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BitInvalidParametersFailBeforePrepareAndLeaveSessionUsable(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 119, 1);
                var execute = await StatementCommand(channel, 23);
                Assert.Equal(new byte[] { 0, 1, 8, 128, 1, 0, 0, 0, 0, 0, 0, 0 }, execute.AsSpan(10).ToArray());
                await channel.WritePacketAsync(Ok(1)); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "select :b";
                var parameter = new OceanBaseParameter { ParameterName = "b", OceanBaseDbType = OceanBaseDbType.Bit, Value = DBNull.Value };
                command.Parameters.Add(parameter);
                Func<Task> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
                await Assert.ThrowsAsync<NotSupportedException>(execute); UseInternalMySqlFixture(connection);
                parameter.Size = 65; await Assert.ThrowsAsync<NotSupportedException>(execute);
                parameter.Size = 1; parameter.Value = 2UL; await Assert.ThrowsAsync<ArgumentException>(execute);
                parameter.Value = true; await Assert.ThrowsAsync<ArgumentException>(execute);
                parameter.Value = 1UL; Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
