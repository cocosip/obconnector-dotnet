using System;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(8)]
        [InlineData(9)]
        public void NanosecondTimestampColumnsKeepDeclaredTypeEvenForNull(byte scale)
        {
            var column = ValueColumn(202, OceanBaseCompatibilityMode.Oracle, scale: scale);
            Assert.Equal("OceanBaseConnector.OceanBaseTimestamp", column.FieldType.FullName);
            Assert.Equal(DBNull.Value, column.Decode(null));
            Assert.Equal(DBNull.Value, column.DecodeBinary(null));
            Assert.Equal(column.FieldType, new ResultDbColumn(column, 0).DataType);
        }

        [Fact]
        public void NanosecondTimestampPublicValuePreservesAllDigitsAndRejectsLossyConversion()
        {
            var value = new OceanBaseTimestamp(new DateTime(2024, 2, 29, 13, 14, 15), 123456789);
            Assert.Equal("2024-02-29 13:14:15.123456789", value.ToString());
            Assert.Throws<OverflowException>(() => value.ToDateTime());
            var exact = new OceanBaseTimestamp(new DateTime(2024, 2, 29, 13, 14, 15).AddTicks(1234567));
            Assert.Equal(123456700, exact.Nanosecond);
            Assert.Equal((byte)7, exact.Scale);
            Assert.Equal(new DateTime(2024, 2, 29, 13, 14, 15).AddTicks(1234567), exact.ToDateTime());
            Assert.Equal(DateTimeKind.Unspecified, exact.ToDateTime().Kind);
        }

        private static byte[] NanosPayload(uint nanosecond = 123456789, byte scale = 9)
            => new byte[] { 20, 24, 2, 29, 13, 14, 15, (byte)nanosecond, (byte)(nanosecond >> 8), (byte)(nanosecond >> 16), (byte)(nanosecond >> 24), scale };

        [Theory]
        [InlineData(0, 0U)]
        [InlineData(3, 123000000U)]
        [InlineData(7, 123456700U)]
        [InlineData(8, 123456780U)]
        [InlineData(9, 123456789U)]
        [InlineData(9, 1U)]
        [InlineData(9, 999999999U)]
        public void NanosecondTimestampCodecPreservesFractionAndProviderSchema(byte scale, uint nanosecond)
        {
            var column = ValueColumn(202, OceanBaseCompatibilityMode.Oracle, scale: scale);
            var wire = NanosPayload(nanosecond, scale);
            var expected = new OceanBaseTimestamp(new DateTime(2024, 2, 29, 13, 14, 15), (int)nanosecond, scale);
            foreach (var binary in new[] { false, true })
            {
                var value = binary ? column.DecodeBinary(column.ReadBinaryField(new ByteCursor(WithLength(wire)))) : column.Decode(wire);
                if (scale <= 7) Assert.Equal(expected.ToDateTime(), Assert.IsType<DateTime>(value));
                else Assert.Equal(expected, Assert.IsType<OceanBaseTimestamp>(value));
            }
            Assert.Equal(typeof(OceanBaseTimestamp), new ResultDbColumn(column, 0)["ProviderSpecificDataType"]);
        }

        [Fact]
        public void NanosecondTimestampParametersWriteExactPayloadAndNeverRoundScale()
        {
            var value = new OceanBaseTimestamp(new DateTime(2024, 2, 29, 13, 14, 15), 123456789);
            var parameter = new OceanBaseParameter { Value = value };
            Assert.Equal(DbType.DateTime2, parameter.DbType);
            Assert.Equal(new byte[] { 0, 1, 202, 0, 12, 20, 24, 2, 29, 13, 14, 15, 21, 205, 91, 7, 9 }, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
            parameter.Scale = 8;
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            parameter.Scale = 9;
            parameter.DbType = DbType.DateTime2;
            parameter.Value = DBNull.Value;
            Assert.Equal(new byte[] { 1, 1, 6, 0 }, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(9, 8)]
        [InlineData(8, 9)]
        public void NanosecondTimestampExplicitScaleRebindPreservesValue(byte originalScale, byte declaredScale)
        {
            var value = new OceanBaseTimestamp(new DateTime(2024, 2, 29, 13, 14, 15), 123456780, originalScale);
            var parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Timestamp, Value = value, Scale = declaredScale };
            Assert.Equal(new byte[] { 0, 1, 202, 0, 12, 20, 24, 2, 29, 13, 14, 15, 12, 205, 91, 7, declaredScale }, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            Assert.Equal(value, parameter.Value);
            parameter.Value = new DateTime(2024, 2, 29, 13, 14, 15).AddTicks(1);
            parameter.Scale = 9;
            Assert.Equal(new byte[] { 0, 1, 202, 0, 12, 20, 24, 2, 29, 13, 14, 15, 100, 0, 0, 0, 9 }, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(-1, 9)]
        [InlineData(1000000000, 9)]
        [InlineData(1, 10)]
        [InlineData(1, 8)]
        [InlineData(100, 6)]
        public void NanosecondTimestampPublicValueRejectsInvalidFractionOrScale(int nanosecond, byte scale)
        {
            Assert.ThrowsAny<ArgumentException>(() => new OceanBaseTimestamp(new DateTime(2024, 1, 1), nanosecond, scale));
        }

        [Fact]
        public void NanosecondTimestampNeverLosesZoneOrDoubleCountsDateTimeFraction()
        {
            var second = new DateTime(2024, 1, 1);
            Assert.Throws<ArgumentException>(() => new OceanBaseTimestamp(DateTime.SpecifyKind(second, DateTimeKind.Utc), 0));
            Assert.Throws<ArgumentException>(() => new OceanBaseTimestamp(DateTime.SpecifyKind(second, DateTimeKind.Local)));
            Assert.Throws<ArgumentException>(() => new OceanBaseTimestamp(second.AddTicks(1), 100));
            Assert.Equal(DateTime.MinValue, default(OceanBaseTimestamp).ToDateTime());
            Assert.Equal(DateTime.MaxValue.Ticks, new OceanBaseTimestamp(DateTime.MaxValue).ToDateTime().Ticks);
            var end = new OceanBaseTimestamp(new DateTime(9999, 12, 31, 23, 59, 59), 999999999);
            Assert.Equal("9999-12-31 23:59:59.999999999", end.ToString());
            Assert.Throws<OverflowException>(() => end.ToDateTime());
        }

        [Theory]
        [InlineData(0, 100)]
        [InlineData(1, 100)]
        [InlineData(2, 0)]
        [InlineData(2, 13)]
        [InlineData(3, 30)]
        [InlineData(4, 24)]
        [InlineData(5, 60)]
        [InlineData(6, 60)]
        public void NanosecondTimestampRejectsInvalidCalendarComponents(int offset, byte value)
        {
            var wire = NanosPayload(); wire[offset] = value;
            var column = ValueColumn(202, OceanBaseCompatibilityMode.Oracle, scale: 9);
            Assert.Throws<FormatException>(() => column.Decode(wire));
            Assert.Throws<FormatException>(() => column.DecodeBinary(wire));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(11)]
        [InlineData(13)]
        public void NanosecondTimestampRejectsWrongPayloadLength(byte length)
        {
            var column = ValueColumn(202, OceanBaseCompatibilityMode.Oracle, scale: 9);
            Assert.Throws<ProtocolException>(() => column.ReadBinaryField(new ByteCursor(WithLength(new byte[length]))));
            Assert.Throws<ProtocolException>(() => column.DecodeBinary(new byte[length]));
            Assert.Throws<ProtocolException>(() => column.Decode(new byte[length]));
        }

        [Theory]
        [InlineData(1000000000U, 9)]
        [InlineData(uint.MaxValue, 9)]
        [InlineData(123456789U, 10)]
        public void NanosecondTimestampRejectsMalformedWireFractions(uint nanosecond, byte scale)
        {
            var column = ValueColumn(202, OceanBaseCompatibilityMode.Oracle, scale: 9);
            Assert.Throws<ProtocolException>(() => column.Decode(NanosPayload(nanosecond, scale)));
        }

        [Theory]
        [InlineData(123456789U, 8, 8)]
        [InlineData(123456780U, 8, 9)]
        public void NanosecondTimestampNeverNormalizesMismatchedDeclaredPrecision(uint nanosecond, byte payloadScale, byte columnScale)
        {
            var column = ValueColumn(202, OceanBaseCompatibilityMode.Oracle, scale: columnScale);
            Assert.Throws<NotSupportedException>(() => column.Decode(NanosPayload(nanosecond, payloadScale)));
        }

        [Theory]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 10)]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 255)]
        [InlineData(OceanBaseCompatibilityMode.MySql, 9)]
        public void NanosecondTimestampInvalidModeOrMetadataRejectsNull(OceanBaseCompatibilityMode mode, byte scale)
        {
            var column = ValueColumn(202, mode, scale: scale);
            Assert.Throws<NotSupportedException>(() => column.Decode(null));
            Assert.Throws<NotSupportedException>(() => column.Decode(NanosPayload()));
        }

        [Theory]
        [InlineData(false, false, 9)]
        [InlineData(true, false, 9)]
        [InlineData(false, true, 9)]
        [InlineData(true, true, 9)]
        [InlineData(false, false, 7)]
        [InlineData(true, true, 7)]
        public async Task NanosecondTimestampPublicReaderAndParameterRoundTrip(bool asynchronous, bool binary, byte scale)
        {
            var server = Server(async channel =>
            {
                if (binary) { await StatementCommand(channel, 22); await Prepared(channel, 92, 0); await StatementCommand(channel, 23); }
                else await Query(channel, "values");
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(ValueColumnPacket(202, scale: scale));
                await channel.WritePacketAsync(Eof());
                foreach (var payload in new[] { NanosPayload(scale == 9 ? 123456789U : 123456700U, scale), NanosPayload(123456700, scale), NanosPayload(0, scale) })
                {
                    var field = WithLength(payload);
                    if (binary) { var row = new byte[field.Length + 2]; field.CopyTo(row, 2); field = row; }
                    await channel.WritePacketAsync(field);
                }
                await channel.WritePacketAsync(binary ? new byte[] { 0, 4 } : new byte[] { 251 });
                await channel.WritePacketAsync(Eof());
                if (binary) await StatementCommand(channel, 25);
                await StatementCommand(channel, 22); await Prepared(channel, 93, 1);
                var writeBack = await StatementCommand(channel, 23);
                Assert.Equal(scale == 9 ? new byte[] { 0, 1, 202, 0, 12, 20, 24, 2, 29, 13, 14, 15, 21, 205, 91, 7, 9 }
                    : new byte[] { 0, 1, 202, 0, 12, 20, 24, 2, 29, 13, 14, 15, 188, 204, 91, 7, 7 }, writeBack.AsSpan(10).ToArray());
                await channel.WritePacketAsync(Ok(1));
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                OceanBaseTimestamp first;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "values";
                    if (binary) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                    using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
                    var fieldType = scale == 9 ? typeof(OceanBaseTimestamp) : typeof(DateTime);
                    Assert.Equal(fieldType, reader.GetFieldType(0));
                    Assert.Equal(typeof(OceanBaseTimestamp), reader.GetProviderSpecificFieldType(0));
                    Assert.Equal(fieldType, Assert.Single(reader.GetColumnSchema()).DataType);
                    Assert.Equal(typeof(OceanBaseTimestamp), reader.GetSchemaTable()!.Rows[0]["ProviderSpecificDataType"]);
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    first = reader.GetFieldValue<OceanBaseTimestamp>(0);
                    if (asynchronous) Assert.Equal(first, await reader.GetFieldValueAsync<OceanBaseTimestamp>(0));
                    Assert.Equal(scale == 9 ? 123456789 : 123456700, first.Nanosecond);
                    Assert.Equal(scale, first.Scale);
                    Assert.Equal(first, reader.GetProviderSpecificValue(0));
                    if (scale == 9)
                    {
                        Assert.Throws<OverflowException>(() => reader.GetDateTime(0));
                        Assert.Throws<OverflowException>(() => reader.GetFieldValue<DateTime>(0));
                    }
                    else Assert.Equal(first.ToDateTime(), reader.GetDateTime(0));
                    var values = new object[1]; Assert.Equal(1, reader.GetValues(values)); Assert.IsType(fieldType, values[0]);
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Equal(new DateTime(2024, 2, 29, 13, 14, 15).AddTicks(1234567), reader.GetDateTime(0));
                    Assert.Equal(reader.GetDateTime(0), reader.GetFieldValue<DateTime>(0));
                    Assert.IsType(fieldType, reader.GetValue(0));
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.IsType(fieldType, reader.GetValue(0));
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.True(reader.IsDBNull(0)); Assert.Equal(DBNull.Value, reader.GetProviderSpecificValue(0));
                    Assert.Equal(fieldType, reader.GetFieldType(0));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
                using var insert = connection.CreateCommand(); insert.CommandText = "select :t";
                var parameter = new OceanBaseParameter { ParameterName = "t", Value = first, Scale = 6 };
                insert.Parameters.Add(parameter);
                if (asynchronous) await Assert.ThrowsAsync<ArgumentException>(() => insert.ExecuteNonQueryAsync());
                else Assert.Throws<ArgumentException>(() => insert.ExecuteNonQuery());
                Assert.Equal(ConnectionState.Open, connection.State);
                parameter.Scale = scale;
                Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
