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
        private static byte[] ZonedPayload(sbyte hour = 5, sbyte minute = 30, uint nanos = 123456789, byte scale = 9,
            string? name = "Asia/Kolkata", string? abbreviation = "IST")
        {
            var timestamp = NanosPayload(nanos, scale);
            var zone = Encoding.UTF8.GetBytes(name ?? "");
            var abbr = Encoding.UTF8.GetBytes(abbreviation ?? "");
            var data = new byte[16 + zone.Length + abbr.Length];
            timestamp.CopyTo(data, 0);
            data[12] = unchecked((byte)hour); data[13] = unchecked((byte)minute);
            data[14] = (byte)zone.Length; zone.CopyTo(data, 15);
            data[15 + zone.Length] = (byte)abbr.Length; abbr.CopyTo(data, 16 + zone.Length);
            return data;
        }

        [Theory]
        [InlineData(0)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        public void ZonedTimestampColumnsKeepProviderTypeAndNullStable(byte scale)
        {
            var column = ValueColumn(200, OceanBaseCompatibilityMode.Oracle, scale: scale);
            Assert.Equal("OceanBaseConnector.OceanBaseTimestampWithTimeZone", column.FieldType.FullName);
            Assert.Equal(column.FieldType, column.ProviderSpecificFieldType);
            Assert.Equal("TIMESTAMP WITH TIME ZONE", column.DataTypeName);
            Assert.Equal("TimestampWithTimeZone", column.ProviderType.ToString());
            Assert.Equal(DBNull.Value, column.Decode(null));
            Assert.Equal(DBNull.Value, column.DecodeBinary(null));
            Assert.Equal(column.FieldType, new ResultDbColumn(column, 0).DataType);
        }

        [Theory]
        [InlineData(5, 30, 330)]
        [InlineData(-3, 30, -210)]
        [InlineData(0, -30, -30)]
        [InlineData(14, 0, 840)]
        [InlineData(-12, 0, -720)]
        [InlineData(-14, 0, -840)]
        public void ZonedTimestampBothProtocolsPreserveLocalFieldsAndOffset(sbyte hour, sbyte minute, int expectedMinutes)
        {
            var column = ValueColumn(200, OceanBaseCompatibilityMode.Oracle, scale: 9);
            var data = ZonedPayload(hour, minute);
            foreach (var binary in new[] { false, true })
            {
                var value = binary ? column.DecodeBinary(column.ReadBinaryField(new ByteCursor(WithLength(data)))) : column.Decode(data);
                var type = value.GetType();
                Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), type.GetProperty("Offset")!.GetValue(value));
                Assert.Equal("Asia/Kolkata", type.GetProperty("TimeZoneName")!.GetValue(value));
                Assert.Equal("IST", type.GetProperty("TimeZoneAbbreviation")!.GetValue(value));
                Assert.Equal(new OceanBaseTimestamp(new DateTime(2024, 2, 29, 13, 14, 15), 123456789), type.GetProperty("Timestamp")!.GetValue(value));
            }
        }

        [Fact]
        public void ZonedTimestampParametersInferOffsetTypeAndPreserveProviderIdentity()
        {
            var value = new OceanBaseTimestampWithTimeZone(new OceanBaseTimestamp(new DateTime(2024, 2, 29, 13, 14, 15), 123456789), TimeSpan.FromMinutes(-210), "America/St_Johns", "NST");
            var parameter = new OceanBaseParameter { Value = value };
            Assert.Equal(DbType.DateTimeOffset, parameter.DbType);
            var expected = ZonedPayload(-3, 30, name: "America/St_Johns", abbreviation: "NST");
            var encoded = PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle);
            Assert.Equal(new byte[] { 0, 1, 200, 0 }, encoded.AsSpan(0, 4).ToArray());
            Assert.Equal(WithLength(expected), encoded.AsSpan(4).ToArray());
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
            parameter.OceanBaseDbType = Enum.Parse<OceanBaseDbType>("TimestampWithTimeZone");
            Assert.Equal(DbType.DateTimeOffset, parameter.DbType);
            parameter.Value = DBNull.Value;
            Assert.Equal(new byte[] { 1, 1, 6, 0 }, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
        }

        [Fact]
        public void ZonedTimestampPublicValueConvertsExactlyAndDoesNotResolveZoneNames()
        {
            var input = new DateTimeOffset(2024, 11, 3, 1, 30, 0, TimeSpan.FromHours(-4)).AddTicks(1);
            var value = new OceanBaseTimestampWithTimeZone(input);
            Assert.Equal(input, value.ToDateTimeOffset());
            Assert.Equal(DateTimeKind.Unspecified, value.Timestamp.WholeSeconds.Kind);
            Assert.Equal(100, value.Timestamp.Nanosecond);
            Assert.Null(value.TimeZoneName);
            var ambiguous = new OceanBaseTimestampWithTimeZone(value.Timestamp, TimeSpan.FromHours(-5), "America/New_York", "EST");
            Assert.Equal(TimeSpan.FromHours(-5), ambiguous.ToDateTimeOffset().Offset);
            Assert.Equal(input.DateTime, ambiguous.ToDateTimeOffset().DateTime);
            Assert.NotEqual(input.UtcDateTime, ambiguous.ToDateTimeOffset().UtcDateTime);
            Assert.Equal("2024-11-03 01:30:00.0000001 -05:00 [America/New_York] [EST]", ambiguous.ToString());
            var opaque = new OceanBaseTimestampWithTimeZone(value.Timestamp, value.Offset, "Server/Unknown_Zone");
            Assert.Equal(input, opaque.ToDateTimeOffset());
            Assert.Equal(DateTimeOffset.MinValue, default(OceanBaseTimestampWithTimeZone).ToDateTimeOffset());
            var outOfRange = new OceanBaseTimestampWithTimeZone(default, TimeSpan.FromHours(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => outOfRange.ToDateTimeOffset());
            Assert.Throws<OverflowException>(() => new OceanBaseTimestampWithTimeZone(new OceanBaseTimestamp(input.DateTime.AddTicks(-1), 1), input.Offset).ToDateTimeOffset());
        }

        [Theory]
        [InlineData(-841)]
        [InlineData(841)]
        public void ZonedTimestampPublicValueRejectsOffsetOutsideSupportedRange(int minutes)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new OceanBaseTimestampWithTimeZone(default, TimeSpan.FromMinutes(minutes)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OceanBaseTimestampWithTimeZone(default, TimeSpan.FromSeconds(1)));
        }

        [Theory]
        [InlineData("\0")]
        [InlineData("bad\nzone")]
        public void ZonedTimestampRejectsInvalidIdentity(string name)
        {
            Assert.ThrowsAny<ArgumentException>(() => new OceanBaseTimestampWithTimeZone(default, TimeSpan.Zero, name));
            Assert.ThrowsAny<ArgumentException>(() => new OceanBaseTimestampWithTimeZone(default, TimeSpan.Zero, timeZoneAbbreviation: name));
        }

        [Fact]
        public void ZonedTimestampRejectsUnpairedSurrogatesBeforeEncoding()
        {
            var invalid = new string((char)0xd800, 1);
            Assert.Throws<EncoderFallbackException>(() => new OceanBaseTimestampWithTimeZone(default, TimeSpan.Zero, invalid));
        }

        [Theory]
        [InlineData(250)]
        [InlineData(251)]
        [InlineData(255)]
        public void ZonedTimestampResultIdentityLimitsCountUtf8BytesAndUseLengthEncodedPayloads(int nameLength)
        {
            var name = new string('a', nameLength);
            var value = new OceanBaseTimestampWithTimeZone(new OceanBaseTimestamp(new DateTime(2024, 2, 29, 13, 14, 15), 100), TimeSpan.Zero, name, name);
            var prefix = nameLength < 251 ? new byte[] { (byte)nameLength } : new byte[] { 252, (byte)nameLength, 0 };
            var payload = new byte[14 + 2 * (prefix.Length + nameLength)];
            NanosPayload(100, 9).CopyTo(payload, 0);
            prefix.CopyTo(payload, 14); Encoding.UTF8.GetBytes(name).CopyTo(payload, 14 + prefix.Length);
            prefix.CopyTo(payload, 14 + prefix.Length + nameLength);
            Encoding.UTF8.GetBytes(name).CopyTo(payload, 14 + 2 * prefix.Length + nameLength);
            var encoded = new byte[payload.Length + 3];
            encoded[0] = 252; encoded[1] = (byte)payload.Length; encoded[2] = (byte)(payload.Length >> 8);
            payload.CopyTo(encoded, 3);
            var column = ValueColumn(200, OceanBaseCompatibilityMode.Oracle, scale: 9);
            Assert.Equal(value, column.DecodeBinary(column.ReadBinaryField(new ByteCursor(encoded))));
            Assert.Equal(value, column.Decode(payload));
            Assert.Throws<ArgumentException>(() => new OceanBaseTimestampWithTimeZone(default, TimeSpan.Zero, new string('a', 256)));
            Assert.Throws<ArgumentException>(() => new OceanBaseTimestampWithTimeZone(default, TimeSpan.Zero, new string('中', 86)));
            var unicode = ZonedPayload(name: "服务器/时区", abbreviation: "夏");
            var decoded = Assert.IsType<OceanBaseTimestampWithTimeZone>(column.Decode(unicode));
            Assert.Equal("服务器/时区", decoded.TimeZoneName); Assert.Equal("夏", decoded.TimeZoneAbbreviation);
        }

        [Theory]
        [InlineData(250)]
        [InlineData(251)]
        [InlineData(255)]
        public void ZonedTimestampParametersUseSingleByteLength(int length)
        {
            var name = new string('a', length - 16);
            var value = new OceanBaseTimestampWithTimeZone(new OceanBaseTimestamp(new DateTime(2024, 2, 29, 13, 14, 15), 100), TimeSpan.Zero, name);
            var encoded = PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = value } }, OceanBaseCompatibilityMode.Oracle);
            Assert.Equal((byte)length, encoded[4]);
            Assert.Equal(length + 5, encoded.Length);
            Assert.Equal(ZonedPayload(0, 0, nanos: 100, name: name, abbreviation: null), encoded.AsSpan(5).ToArray());
        }

        [Theory]
        [InlineData(256)]
        [InlineData(526)]
        public void ZonedTimestampParametersRejectPayloadBeyondSingleByteLength(int length)
        {
            var name = new string('a', Math.Min(255, length - 16));
            var abbreviation = new string('b', length - 16 - name.Length);
            var value = new OceanBaseTimestampWithTimeZone(default, TimeSpan.Zero, name, abbreviation);
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = value } }, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(7, 123456700)]
        [InlineData(8, 123456780)]
        [InlineData(9, 123456789)]
        public void ZonedTimestampAllScalesAcceptOffsetOnlyPayload(byte scale, int nanos)
        {
            var column = ValueColumn(200, OceanBaseCompatibilityMode.Oracle, scale: scale);
            var wire = ZonedPayload(nanos: (uint)nanos, scale: scale).AsSpan(0, 14).ToArray();
            var value = Assert.IsType<OceanBaseTimestampWithTimeZone>(column.Decode(wire));
            Assert.Null(value.TimeZoneName); Assert.Null(value.TimeZoneAbbreviation);
            Assert.Equal(nanos, value.Timestamp.Nanosecond); Assert.Equal(scale, value.Timestamp.Scale);
            Assert.Equal(value, column.DecodeBinary(column.ReadBinaryField(new ByteCursor(WithLength(wire)))));
        }

        [Theory]
        [InlineData(15, 0)]
        [InlineData(-15, 0)]
        [InlineData(14, 1)]
        [InlineData(-14, 1)]
        [InlineData(0, 60)]
        [InlineData(3, -30)]
        [InlineData(-3, -30)]
        public void ZonedTimestampRejectsMalformedResultOffsets(sbyte hour, sbyte minute)
        {
            var column = ValueColumn(200, OceanBaseCompatibilityMode.Oracle, scale: 9);
            Assert.Throws<ProtocolException>(() => column.Decode(ZonedPayload(hour, minute)));
            Assert.Throws<ProtocolException>(() => column.DecodeBinary(ZonedPayload(hour, minute)));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(13)]
        [InlineData(531)]
        public void ZonedTimestampRejectsInvalidPayloadLengths(int length)
        {
            var column = ValueColumn(200, OceanBaseCompatibilityMode.Oracle, scale: 9);
            Assert.Throws<ProtocolException>(() => column.Decode(new byte[length]));
            var prefix = length < 251 ? new byte[] { (byte)length } : new byte[] { 252, (byte)length, (byte)(length >> 8) };
            Assert.Throws<ProtocolException>(() => column.ReadBinaryField(new ByteCursor(prefix)));
        }

        [Theory]
        [InlineData(251)]
        [InlineData(256)]
        [InlineData(65535)]
        public void ZonedTimestampRejectsNullOrOversizedResultIdentityLengths(int length)
        {
            var payload = new byte[17]; NanosPayload(100, 9).CopyTo(payload, 0);
            if (length == 251) payload[14] = 251;
            else { payload[14] = 252; payload[15] = (byte)length; payload[16] = (byte)(length >> 8); }
            var column = ValueColumn(200, OceanBaseCompatibilityMode.Oracle, scale: 9);
            Assert.Throws<ProtocolException>(() => column.Decode(payload));
            Assert.Throws<ProtocolException>(() => column.DecodeBinary(payload));
        }

        [Fact]
        public void ZonedTimestampRejectsTruncatedTrailingIdentityAndInvalidTimestamp()
        {
            var column = ValueColumn(200, OceanBaseCompatibilityMode.Oracle, scale: 9);
            var data = ZonedPayload();
            Assert.Throws<ProtocolException>(() => column.Decode(data.AsSpan(0, 15).ToArray()));
            Assert.Throws<ProtocolException>(() => column.Decode(data.AsSpan(0, data.Length - 1).ToArray()));
            var trailing = new byte[data.Length + 1]; data.CopyTo(trailing, 0);
            Assert.Throws<ProtocolException>(() => column.Decode(trailing));
            data[15] = 255;
            Assert.Throws<DecoderFallbackException>(() => column.Decode(data));
            data = ZonedPayload(); data[2] = 13;
            Assert.Throws<FormatException>(() => column.Decode(data));
            data = ZonedPayload(); data[11] = 10;
            Assert.Throws<ProtocolException>(() => column.Decode(data));
            Assert.Throws<NotSupportedException>(() => column.Decode(ZonedPayload(nanos: 1, scale: 8)));
        }

        [Theory]
        [InlineData(OceanBaseCompatibilityMode.MySql, 9)]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 10)]
        public void ZonedTimestampRejectsInvalidMetadataEvenForNull(OceanBaseCompatibilityMode mode, byte scale)
        {
            var column = ValueColumn(200, mode, scale: scale);
            Assert.Throws<NotSupportedException>(() => column.Decode(null));
            Assert.Throws<NotSupportedException>(() => column.DecodeBinary(null));
        }

        [Theory]
        [InlineData(-210, -3, 30)]
        [InlineData(-30, 0, -30)]
        [InlineData(345, 5, 45)]
        public void ZonedTimestampDateTimeOffsetParametersPreserveLocalTimeAndSignedOffset(int minutes, sbyte hour, sbyte minute)
        {
            var input = new DateTimeOffset(2024, 2, 29, 13, 14, 15, TimeSpan.FromMinutes(minutes)).AddTicks(1);
            var parameter = new OceanBaseParameter { Value = input };
            Assert.Equal(DbType.DateTimeOffset, parameter.DbType);
            var data = PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle);
            Assert.Equal(WithLength(ZonedPayload(hour, minute, nanos: 100, scale: 7, name: null, abbreviation: null)), data.AsSpan(4).ToArray());
            parameter.Scale = 9;
            Assert.Equal(9, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle)[16]);
            parameter.Scale = 6;
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            parameter.DbType = DbType.DateTimeOffset; parameter.Value = DBNull.Value; parameter.Scale = 10;
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(9, 8)]
        [InlineData(8, 9)]
        public void ZonedTimestampParametersOnlyRescaleExactly(byte inputScale, byte outputScale)
        {
            var value = new OceanBaseTimestampWithTimeZone(new OceanBaseTimestamp(new DateTime(2024, 2, 29, 13, 14, 15), 123456780, inputScale), TimeSpan.FromMinutes(-210));
            var parameter = new OceanBaseParameter { Value = value, Scale = outputScale };
            var encoded = PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle);
            Assert.Equal(16, encoded[4]);
            Assert.Equal(outputScale, encoded[16]);
            Assert.Equal(ZonedPayload(-3, 30, nanos: 123456780, scale: outputScale, name: null, abbreviation: null), encoded.AsSpan(5).ToArray());
            parameter.DbType = DbType.DateTime;
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            parameter.Value = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified);
            parameter.DbType = DbType.DateTimeOffset;
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(false, false, 330)]
        [InlineData(true, false, 330)]
        [InlineData(false, true, 330)]
        [InlineData(true, true, 330)]
        [InlineData(true, false, -210)]
        [InlineData(false, true, -210)]
        [InlineData(false, true, -30)]
        public async Task ZonedTimestampPublicReaderAndParameterRoundTrip(bool asynchronous, bool binary, int offsetMinutes)
        {
            var hour = (sbyte)(offsetMinutes / 60); var minute = (sbyte)(offsetMinutes <= -60 ? -offsetMinutes % 60 : offsetMinutes % 60);
            var server = Server(async channel =>
            {
                if (binary) { await StatementCommand(channel, 22); await Prepared(channel, 95, 0); await StatementCommand(channel, 23); }
                else await Query(channel, "values");
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(ValueColumnPacket(200, scale: 9));
                await channel.WritePacketAsync(Eof());
                foreach (var payload in new[] { ZonedPayload(hour, minute), ZonedPayload(hour, minute, nanos: 100) })
                {
                    var field = WithLength(payload);
                    if (binary) { var row = new byte[field.Length + 2]; field.CopyTo(row, 2); field = row; }
                    await channel.WritePacketAsync(field);
                }
                await channel.WritePacketAsync(binary ? new byte[] { 0, 4 } : new byte[] { 251 });
                await channel.WritePacketAsync(Eof());
                if (binary) await StatementCommand(channel, 25);
                await StatementCommand(channel, 22); await Prepared(channel, 96, 1);
                var writeBack = await StatementCommand(channel, 23);
                Assert.Equal(new byte[] { 0, 1, 200, 0 }, writeBack.AsSpan(10, 4).ToArray());
                Assert.Equal(WithLength(ZonedPayload(hour, minute)), writeBack.AsSpan(14).ToArray());
                await channel.WritePacketAsync(Ok(1));
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                OceanBaseTimestampWithTimeZone first;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "values";
                    if (binary) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                    using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
                    Assert.Equal(typeof(OceanBaseTimestampWithTimeZone), reader.GetFieldType(0));
                    Assert.Equal(typeof(OceanBaseTimestampWithTimeZone), reader.GetProviderSpecificFieldType(0));
                    Assert.Equal(typeof(OceanBaseTimestampWithTimeZone), Assert.Single(reader.GetColumnSchema()).DataType);
                    Assert.Equal(typeof(OceanBaseTimestampWithTimeZone), reader.GetSchemaTable()!.Rows[0]["ProviderSpecificDataType"]);
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    first = reader.GetFieldValue<OceanBaseTimestampWithTimeZone>(0);
                    Assert.Equal(123456789, first.Timestamp.Nanosecond);
                    Assert.Equal(TimeSpan.FromMinutes(offsetMinutes), first.Offset);
                    Assert.Equal("Asia/Kolkata", first.TimeZoneName); Assert.Equal("IST", first.TimeZoneAbbreviation);
                    Assert.Equal(first, reader.GetProviderSpecificValue(0));
                    Assert.Throws<OverflowException>(() => reader.GetFieldValue<DateTimeOffset>(0));
                    Assert.Throws<InvalidCastException>(() => reader.GetDateTime(0));
                    var values = new object[1]; Assert.Equal(1, reader.GetValues(values)); Assert.Equal(first, values[0]);
                    Assert.Equal(1, reader.GetProviderSpecificValues(values)); Assert.Equal(first, values[0]);
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Equal(new DateTimeOffset(2024, 2, 29, 13, 14, 15, TimeSpan.FromMinutes(offsetMinutes)).AddTicks(1), await reader.GetFieldValueAsync<DateTimeOffset>(0));
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.True(reader.IsDBNull(0)); Assert.Equal(DBNull.Value, reader.GetProviderSpecificValue(0));
                    Assert.Equal(typeof(OceanBaseTimestampWithTimeZone), reader.GetFieldType(0));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
                using var insert = connection.CreateCommand(); insert.CommandText = "select :t";
                var parameter = new OceanBaseParameter { ParameterName = "t", Value = first, DbType = DbType.DateTimeOffset, Scale = 8 };
                insert.Parameters.Add(parameter);
                if (asynchronous) await Assert.ThrowsAsync<ArgumentException>(() => insert.ExecuteNonQueryAsync());
                else Assert.Throws<ArgumentException>(() => insert.ExecuteNonQuery());
                Assert.Equal(ConnectionState.Open, connection.State);
                parameter.Scale = 9;
                parameter.Value = new OceanBaseTimestampWithTimeZone(first.Timestamp, first.Offset, new string('a', 240));
                if (asynchronous) await Assert.ThrowsAsync<ArgumentException>(() => insert.ExecuteNonQueryAsync());
                else Assert.Throws<ArgumentException>(() => insert.ExecuteNonQuery());
                Assert.Equal(ConnectionState.Open, connection.State);
                parameter.Value = first;
                Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
