using System;
using System.Data;
using System.Globalization;
using System.Text;
using System.Numerics;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static ResultColumn ValueColumn(byte type, OceanBaseCompatibilityMode mode, byte precision = 10, byte scale = 0, ushort charset = 45, ushort flags = 0)
            => ResultColumn.Parse(ValueColumnPacket(type, precision, scale, charset, flags), mode);

        private static byte[] ValueColumnPacket(byte type, byte precision = 10, byte scale = 0, ushort charset = 45, ushort flags = 0)
        {
            var packet = Column("v", type, charset, flags);
            var length = (uint)(precision + ((flags & 32) == 0 ? 1 : 0) + (scale > 0 ? 1 : 0));
            var offset = packet.Length - 10;
            for (var i = 0; i < 4; i++) packet[offset + i] = (byte)(length >> (8 * i));
            packet[packet.Length - 3] = scale;
            packet[packet.Length - 2] = precision;
            return packet;
        }

        [Theory]
        [InlineData(0, OceanBaseCompatibilityMode.MySql)]
        [InlineData(246, OceanBaseCompatibilityMode.MySql)]
        [InlineData(246, OceanBaseCompatibilityMode.Oracle)]
        public void DeclaredLowPrecisionDecimalDecodesExactlyInBothProtocols(byte code, OceanBaseCompatibilityMode mode)
        {
            var column = ValueColumn(code, mode, 28, 10);
            var wire = Encoding.ASCII.GetBytes("-123456789012345678.1234567890");
            Assert.Equal(typeof(decimal), column.FieldType);
            Assert.Equal(-123456789012345678.1234567890m, column.Decode(wire));
            Assert.Equal(-123456789012345678.1234567890m, column.DecodeBinary(column.ReadBinaryField(new ByteCursor(WithLength(wire)))));
            Assert.Equal(DBNull.Value, column.Decode(null));
        }

        [Fact]
        public void OracleOptimizedNumberHasDecimalPublicTypeAndSignedIntegerBinaryLayout()
        {
            var column = ValueColumn(3, OceanBaseCompatibilityMode.Oracle, 10);
            Assert.Equal(typeof(decimal), column.FieldType);
            Assert.Equal(-2147483648m, column.Decode(Encoding.ASCII.GetBytes("-2147483648")));
            Assert.Equal(-2147483648m, column.DecodeBinary(column.ReadBinaryField(new ByteCursor(new byte[] { 0, 0, 0, 128 }))));
        }

        [Theory]
        [InlineData("1.234")]
        [InlineData("1000.00")]
        [InlineData("1e999999999")]
        [InlineData("NaN")]
        public void InvalidDecimalCannotRoundOrExceedDeclaredColumn(string text)
        {
            var column = ValueColumn(246, OceanBaseCompatibilityMode.Oracle, 5, 2);
            Assert.ThrowsAny<Exception>(() => column.Decode(Encoding.ASCII.GetBytes(text)));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(38, 0)]
        [InlineData(5, 255)]
        public void WideDecimalColumnsNeverNarrowBasedOnCurrentValue(byte precision, byte scale)
        {
            var column = ValueColumn(246, OceanBaseCompatibilityMode.Oracle, precision, scale);
            Assert.Equal(typeof(OceanBaseDecimal), column.FieldType);
            Assert.IsType<OceanBaseDecimal>(column.Decode(Encoding.ASCII.GetBytes("10")));
        }

        [Theory]
        [InlineData(OceanBaseCompatibilityMode.MySql)]
        [InlineData(OceanBaseCompatibilityMode.Oracle)]
        public void UnsignedDecimalUsesMetadataAndInvalidColumnsRejectNullToo(OceanBaseCompatibilityMode mode)
        {
            var column = ValueColumn(246, mode, 5, 2, flags: 32);
            Assert.Equal(999.99m, column.Decode(Encoding.ASCII.GetBytes("999.99")));
            Assert.Throws<OverflowException>(() => column.Decode(Encoding.ASCII.GetBytes("-0.01")));
            Assert.Throws<NotSupportedException>(() => ValueColumn(246, mode, 66, 20).Decode(null));
        }

        [Fact]
        public void DecimalParameterPreservesScaleWithoutCultureOrFloatingPointConversion()
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                Assert.Equal(new byte[] { 0, 1, 246, 0, 8, 45, 49, 50, 46, 51, 52, 48, 48 },
                    PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = -12.3400m } }));
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Fact]
        public void DecimalPrecisionOnlyInfersScaleWhileExplicitZeroRequiresExactInteger()
        {
            var parameter = new OceanBaseParameter { Value = 12.34m, Precision = 5 };
            Assert.Equal(new byte[] { 0, 1, 246, 0, 5, 49, 50, 46, 51, 52 }, PreparedParameters.Encode(new[] { parameter }));
            parameter.Scale = 0;
            Assert.Throws<OverflowException>(() => PreparedParameters.Encode(new[] { parameter }));
            parameter = new OceanBaseParameter { Value = 0.001m, Precision = 2 };
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }));
        }

        [Theory]
        [InlineData(10, "2024-02-29", new byte[] { 4, 232, 7, 2, 29 })]
        [InlineData(12, "2024-02-29 23:59:58.123456", new byte[] { 11, 232, 7, 2, 29, 23, 59, 58, 64, 226, 1, 0 })]
        [InlineData(7, "2024-02-29 23:59:58", new byte[] { 7, 232, 7, 2, 29, 23, 59, 58 })]
        public void MysqlTemporalTextAndBinaryValuesAgree(byte code, string text, byte[] wire)
        {
            var column = ValueColumn(code, OceanBaseCompatibilityMode.MySql, scale: code == 10 ? (byte)0 : (byte)6);
            var expected = DateTime.ParseExact(text, new[] { "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.ffffff" }, CultureInfo.InvariantCulture, DateTimeStyles.None);
            Assert.Equal(typeof(DateTime), column.FieldType);
            Assert.Equal(expected, column.Decode(Encoding.ASCII.GetBytes(text)));
            Assert.Equal(expected, column.DecodeBinary(column.ReadBinaryField(new ByteCursor(wire))));
            Assert.Equal(DateTimeKind.Unspecified, ((DateTime)column.Decode(Encoding.ASCII.GetBytes(text))).Kind);
        }

        [Fact]
        public void OracleDatePreservesHoursMinutesAndSecondsInBothProtocols()
        {
            var column = ValueColumn(12, OceanBaseCompatibilityMode.Oracle);
            var expected = new DateTime(2024, 2, 29, 13, 14, 15);
            Assert.Equal(expected, column.Decode(Encoding.ASCII.GetBytes("2024-02-29 13:14:15")));
            Assert.Equal(expected, column.DecodeBinary(column.ReadBinaryField(new ByteCursor(new byte[] { 7, 232, 7, 2, 29, 13, 14, 15 }))));
        }

        [Fact]
        public void OracleTimestampUsesRawTwelveBytePayloadInBothRowProtocols()
        {
            var column = ValueColumn(202, OceanBaseCompatibilityMode.Oracle, scale: 7);
            var wire = new byte[] { 20, 24, 2, 29, 13, 14, 15, 188, 204, 91, 7, 7 }; // 123456700 ns, little endian.
            var expected = new DateTime(2024, 2, 29, 13, 14, 15).AddTicks(1234567);
            Assert.Equal(expected, column.Decode(wire));
            Assert.Equal(expected, column.DecodeBinary(column.ReadBinaryField(new ByteCursor(WithLength(wire)))));
        }

        [Theory]
        [InlineData("-838:59:59.999999", new byte[] { 12, 1, 34, 0, 0, 0, 22, 59, 59, 63, 66, 15, 0 })]
        [InlineData("00:00:00", new byte[] { 0 })]
        public void MysqlTimeKeepsDurationAndSign(string text, byte[] wire)
        {
            var column = ValueColumn(11, OceanBaseCompatibilityMode.MySql, scale: 6);
            var expected = text[0] == '-' ? -new TimeSpan(34, 22, 59, 59).Add(TimeSpan.FromTicks(9999990)) : TimeSpan.Zero;
            Assert.Equal(typeof(TimeSpan), column.FieldType);
            Assert.Equal(expected, column.Decode(Encoding.ASCII.GetBytes(text)));
            Assert.Equal(expected, column.DecodeBinary(column.ReadBinaryField(new ByteCursor(wire))));
        }

        [Theory]
        [InlineData(8, 35U, 0)]
        [InlineData(8, 10675200U, 0)]
        [InlineData(12, 10675200U, 1)]
        [InlineData(12, uint.MaxValue, 0)]
        public void MalformedTimeDayCountIsRejectedBeforeTickArithmetic(int length, uint days, byte sign)
        {
            var wire = new byte[length + 1];
            wire[0] = (byte)length;
            wire[1] = sign;
            for (var i = 0; i < 4; i++) wire[2 + i] = (byte)(days >> (8 * i));
            var column = ValueColumn(11, OceanBaseCompatibilityMode.MySql, scale: 6);
            Assert.Throws<ProtocolException>(() => column.DecodeBinary(column.ReadBinaryField(new ByteCursor(wire))));
        }

        [Theory]
        [InlineData("0000-00-00")]
        [InlineData("2023-02-29")]
        [InlineData("2024-02-29T00:00:00Z")]
        public void InvalidDateNeverNormalizesOrBecomesNull(string text)
        {
            var column = ValueColumn(10, OceanBaseCompatibilityMode.MySql);
            Assert.ThrowsAny<Exception>(() => column.Decode(Encoding.ASCII.GetBytes(text)));
        }

        [Fact]
        public void ProviderDecimalParametersRetainExactCoefficientAndScale()
        {
            var parameter = new OceanBaseParameter { Value = new OceanBaseDecimal(new BigInteger(-123400), 4) };
            Assert.Equal(new byte[] { 0, 1, 246, 0, 8, 45, 49, 50, 46, 51, 52, 48, 48 }, PreparedParameters.Encode(new[] { parameter }));
            parameter.Precision = 5;
            parameter.Scale = 2;
            Assert.Equal(new byte[] { 0, 1, 246, 0, 6, 45, 49, 50, 46, 51, 52 }, PreparedParameters.Encode(new[] { parameter }));
            parameter.Scale = 1;
            Assert.Throws<OverflowException>(() => PreparedParameters.Encode(new[] { parameter }));
            parameter.Value = new OceanBaseDecimal(BigInteger.One, 29);
            Assert.Throws<OverflowException>(() => PreparedParameters.Encode(new[] { parameter })); // Explicit scale=1 still cannot round.
        }

        [Fact]
        public void OracleDateParameterNeverTruncatesTimeAndTimestampRetainsHundredNanoseconds()
        {
            var parameter = new OceanBaseParameter { Value = new DateTime(2024, 2, 29, 13, 14, 15), DbType = DbType.Date };
            Assert.Equal(new byte[] { 0, 1, 12, 0, 7, 232, 7, 2, 29, 13, 14, 15 }, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }));
            parameter.OceanBaseDbType = OceanBaseDbType.OracleDate;
            parameter.Value = ((DateTime)parameter.Value).AddTicks(1);
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            parameter.OceanBaseDbType = OceanBaseDbType.Timestamp;
            Assert.Equal(new byte[] { 0, 1, 202, 0, 12, 20, 24, 2, 29, 13, 14, 15, 100, 0, 0, 0, 7 }, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter })); // MySQL wire has microsecond precision.
        }

        [Fact]
        public void UnsupportedProviderTypeCannotMasqueradeAsStringEvenForNull()
        {
            foreach (var type in new[] { OceanBaseDbType.NChar, OceanBaseDbType.NVarchar2, OceanBaseDbType.Json, OceanBaseDbType.IntervalYearToMonth, OceanBaseDbType.IntervalDayToSecond })
            {
                var parameter = new OceanBaseParameter { Value = DBNull.Value, OceanBaseDbType = type };
                Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
                parameter.Value = "value";
                Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
            }
        }

        [Fact]
        public void InvalidPrecisionIsRejectedEvenForTypedNullParameters()
        {
            var parameter = new OceanBaseParameter { DbType = DbType.Decimal, Precision = 66, Value = DBNull.Value };
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
            parameter = new OceanBaseParameter { DbType = DbType.DateTime2, Scale = 10, Value = null };
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        }

        [Fact]
        public void SupportedTypedNullsUseNullBitmapAndOracleBinaryUsesVarString()
        {
            var parameters = new[]
            {
                new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Decimal, Precision = 5, Scale = 2, Value = DBNull.Value },
                new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.OracleDate, Value = null },
                new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Timestamp, Scale = 7, Value = DBNull.Value }
            };
            Assert.Equal(new byte[] { 7, 1, 6, 0, 6, 0, 6, 0 }, PreparedParameters.Encode(parameters, OceanBaseCompatibilityMode.Oracle));
            Assert.Equal(new byte[] { 0, 1, 253, 0, 2, 0, 255 }, PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = new byte[] { 0, 255 } } }, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PublicCommandUsesAuthenticatedModeAndRejectsInvalidValuesBeforePreparation(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await Prepared(channel, 72, 2);
                var packet = await StatementCommand(channel, 23);
                Assert.Equal(new byte[] { 0, 1, 246, 0, 202, 0, 7, 49, 50, 46, 51, 52, 48, 48, 12, 20, 24, 2, 29, 13, 14, 15, 100, 0, 0, 0, 7 }, packet.AsSpan(10).ToArray());
                await channel.WritePacketAsync(Ok(2));
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "select :n, :t";
                var number = new OceanBaseParameter { ParameterName = "n", DbType = DbType.Decimal, Precision = 65, Value = null };
                var timestamp = new OceanBaseParameter { ParameterName = "t", Value = new DateTime(2024, 2, 29, 13, 14, 15).AddTicks(1) };
                command.Parameters.Add(number); command.Parameters.Add(timestamp);
                Func<Task<int>> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
                await Assert.ThrowsAsync<NotSupportedException>(execute);
                number.Precision = 0; number.Value = "not a decimal";
                await Assert.ThrowsAsync<ArgumentException>(execute);
                number.Value = new OceanBaseDecimal(new BigInteger(123400), 4);
                timestamp.Value = DateTime.SpecifyKind((DateTime)timestamp.Value, DateTimeKind.Utc);
                await Assert.ThrowsAsync<ArgumentException>(execute);
                timestamp.Value = DateTime.SpecifyKind((DateTime)timestamp.Value, DateTimeKind.Unspecified);
                Assert.Equal(2, await execute());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void TemporalParameterWireValuesPreserveDurationAndMicroseconds()
        {
            var date = new OceanBaseParameter { Value = new DateTime(2024, 2, 29, 23, 59, 58).AddTicks(1234560) };
            Assert.Equal(new byte[] { 0, 1, 12, 0, 11, 232, 7, 2, 29, 23, 59, 58, 64, 226, 1, 0 }, PreparedParameters.Encode(new[] { date }));
            date.Scale = 3;
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { date }));
            var duration = new OceanBaseParameter { Value = -new TimeSpan(34, 22, 59, 59).Add(TimeSpan.FromTicks(9999990)) };
            Assert.Equal(new byte[] { 0, 1, 11, 0, 12, 1, 34, 0, 0, 0, 22, 59, 59, 63, 66, 15, 0 }, PreparedParameters.Encode(new[] { duration }));
            duration.Value = TimeSpan.MaxValue;
            Assert.Throws<ArgumentOutOfRangeException>(() => PreparedParameters.Encode(new[] { duration }));
            duration.Value = TimeSpan.FromTicks(1);
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { duration }));
        }

        [Fact]
        public void OceanBaseDecimalConversionNeverRoundsAndFormattingStaysInvariant()
        {
            var exact = new OceanBaseDecimal(decimal.MaxValue);
            Assert.Equal(decimal.MaxValue, exact.ToDecimal());
            Assert.Equal("79228162514264337593543950335", exact.ToString());
            Assert.Equal("-12.3400", new OceanBaseDecimal(new BigInteger(-123400), 4).ToString());
            Assert.Throws<OverflowException>(() => new OceanBaseDecimal(BigInteger.One, 29).ToDecimal());
            Assert.Equal(0.0000000000000000000000000001m, new OceanBaseDecimal(new BigInteger(10), 29).ToDecimal());
            Assert.Throws<OverflowException>(() => new OceanBaseDecimal(BigInteger.Parse("79228162514264337593543950336", CultureInfo.InvariantCulture), 0).ToDecimal());
            Assert.Equal(100m, new OceanBaseDecimal(BigInteger.One, -2).ToDecimal());
            Assert.Throws<OverflowException>(() => new OceanBaseDecimal(BigInteger.One, int.MinValue).ToDecimal());
            Assert.Equal("1E-2147483647", new OceanBaseDecimal(BigInteger.One, int.MaxValue).ToString());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PublicReaderKeepsDecimalAndProviderTypesStableAcrossRows(bool binary)
        {
            var server = Server(async channel =>
            {
                if (binary) { await StatementCommand(channel, 22); await Prepared(channel, 70, 0); await StatementCommand(channel, 23); }
                else await Query(channel, "values");
                await channel.WritePacketAsync(new byte[] { 2 });
                await channel.WritePacketAsync(ValueColumnPacket(246, 6, 4));
                await channel.WritePacketAsync(ValueColumnPacket(12));
                await channel.WritePacketAsync(Eof());
                var numeric = WithLength(Encoding.ASCII.GetBytes("12.3400"));
                var temporal = binary ? new byte[] { 7, 232, 7, 2, 29, 13, 14, 15 } : WithLength(Encoding.ASCII.GetBytes("2024-02-29 13:14:15"));
                var row = new byte[(binary ? 2 : 0) + numeric.Length + temporal.Length];
                numeric.CopyTo(row, binary ? 2 : 0);
                temporal.CopyTo(row, (binary ? 2 : 0) + numeric.Length);
                await channel.WritePacketAsync(row);
                await channel.WritePacketAsync(binary ? new byte[] { 0, 12 } : new byte[] { 251, 251 });
                await channel.WritePacketAsync(Eof());
                if (binary) await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "values";
                if (binary) await command.PrepareAsync();
                using var reader = await command.ExecuteReaderAsync();
                Assert.Equal(typeof(decimal), reader.GetFieldType(0));
                Assert.Equal(typeof(OceanBaseDecimal), reader.GetProviderSpecificFieldType(0));
                Assert.Equal(typeof(DateTime), reader.GetProviderSpecificFieldType(1));
                Assert.True(await reader.ReadAsync());
                Assert.Equal(12.3400m, reader.GetDecimal(0));
                var exact = reader.GetFieldValue<OceanBaseDecimal>(0);
                Assert.Equal(new BigInteger(123400), exact.Coefficient);
                Assert.Equal(4, exact.Scale);
                Assert.Equal(new DateTime(2024, 2, 29, 13, 14, 15), reader.GetDateTime(1));
                var providerValues = new object[2];
                Assert.Equal(2, reader.GetProviderSpecificValues(providerValues));
                Assert.IsType<OceanBaseDecimal>(providerValues[0]);
                Assert.Equal("NUMBER", reader.GetDataTypeName(0));
                Assert.Equal("DATE", reader.GetDataTypeName(1));
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.IsDBNull(0));
                Assert.Equal(DBNull.Value, reader.GetProviderSpecificValue(0));
                Assert.Equal(typeof(OceanBaseDecimal), reader.GetProviderSpecificFieldType(0));
                Assert.False(await reader.ReadAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void OracleRawIsBytesAndDeferredTypesRemainUnsupported()
        {
            var raw = ValueColumn(203, OceanBaseCompatibilityMode.Oracle, charset: 63);
            Assert.Equal(new byte[] { 0, 255 }, raw.Decode(new byte[] { 0, 255 }));
            foreach (var type in new byte[] { 13, 16, 204, 205, 207, 208, 210, 211, 245 })
                Assert.Throws<NotSupportedException>(() => ValueColumn(type, OceanBaseCompatibilityMode.Oracle).Decode(new byte[] { 1 }));
        }

        private static byte[] WithLength(byte[] value)
        {
            var wire = new byte[value.Length + 1];
            wire[0] = (byte)value.Length;
            value.CopyTo(wire, 1);
            return wire;
        }
    }
}
