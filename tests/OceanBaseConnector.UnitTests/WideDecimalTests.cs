using System;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private const string WideDecimalText = "123456789012345678901234567890123456789012345.12345678901234567890";

        [Theory]
        [InlineData(OceanBaseCompatibilityMode.MySql, 65, 20, WideDecimalText, "12345678901234567890123456789012345678901234512345678901234567890", 20)]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 38, 0, "12345678901234567890123456789012345678", "12345678901234567890123456789012345678", 0)]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 5, 254, "12300", "123", -2)]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 0, 255, "40", "40", 0)]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 3, 5, "0.00123", "123", 5)]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 0, 0, "1.2300E-126", "12300", 130)]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 255, 171, "1.2300E125", "12300", -121)]
        [InlineData(OceanBaseCompatibilityMode.MySql, 0, 0, "1.2300", "12300", 4)]
        public void WideDecimalColumnsKeepExactTypesAndValues(OceanBaseCompatibilityMode mode, byte precision, byte scale, string text, string coefficient, int valueScale)
        {
            var column = ValueColumn(246, mode, precision, scale);
            Assert.Equal(typeof(OceanBaseDecimal), column.FieldType);
            foreach (var binary in new[] { false, true })
            {
                var value = Assert.IsType<OceanBaseDecimal>(binary ? column.DecodeBinary(Encoding.ASCII.GetBytes(text)) : column.Decode(Encoding.ASCII.GetBytes(text)));
                Assert.Equal(BigInteger.Parse(coefficient, CultureInfo.InvariantCulture), value.Coefficient);
                Assert.Equal(valueScale, value.Scale);
                Assert.Equal(value, column.DecodeDecimal(Encoding.ASCII.GetBytes(text), binary));
                Assert.Equal(DBNull.Value, binary ? column.DecodeBinary(null) : column.Decode(null));
            }
            var schema = new ResultDbColumn(column, 0);
            Assert.Equal(typeof(OceanBaseDecimal), schema.DataType);
            Assert.Equal(typeof(OceanBaseDecimal), schema["ProviderSpecificDataType"]);
            if (precision == 0 || precision == 255) Assert.Null(schema.NumericPrecision);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OracleKnownPrecisionColumnStillEnforcesItsDeclaredNegativeScale(bool binary)
        {
            var column = ValueColumn(246, OceanBaseCompatibilityMode.Oracle, 5, 255);
            Assert.Throws<OverflowException>(() => column.DecodeDecimal(Encoding.ASCII.GetBytes("42"), binary));
        }

        [Theory]
        [InlineData(OceanBaseCompatibilityMode.MySql)]
        [InlineData(OceanBaseCompatibilityMode.Oracle)]
        public void WideDeclaredColumnDoesNotNarrowForOneOrNull(OceanBaseCompatibilityMode mode)
        {
            var column = ValueColumn(246, mode, mode == OceanBaseCompatibilityMode.Oracle ? (byte)38 : (byte)65);
            Assert.IsType<OceanBaseDecimal>(column.Decode(Encoding.ASCII.GetBytes("1")));
            Assert.Equal(typeof(OceanBaseDecimal), column.FieldType);
            Assert.Equal(DBNull.Value, column.Decode(null));
        }

        [Theory]
        [InlineData(OceanBaseCompatibilityMode.MySql, 65, 20, "12345678901234567890123456789012345678901234512345678901234567890", 20, WideDecimalText)]
        [InlineData(OceanBaseCompatibilityMode.MySql, 65, 30, "1", 30, "0.000000000000000000000000000001")]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 38, 0, "12345678901234567890123456789012345678", 0, "12345678901234567890123456789012345678")]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 0, 0, "123", -2, "12300")]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 0, 0, "1", 130, "0.0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001")]
        public void WideDecimalParametersUseExactPlainProtocolText(OceanBaseCompatibilityMode mode, byte precision, byte scale, string coefficient, int valueScale, string expected)
        {
            var number = new OceanBaseDecimal(BigInteger.Parse(coefficient, CultureInfo.InvariantCulture), valueScale);
            var parameter = new OceanBaseParameter { Value = number, Precision = precision };
            if (precision != 0) parameter.Scale = scale;
            var wire = PreparedParameters.Encode(new[] { parameter }, mode);
            Assert.Equal(new byte[] { 0, 1, 246, 0 }, wire.AsSpan(0, 4).ToArray());
            Assert.Equal(expected, Encoding.ASCII.GetString(new ByteCursor(wire.AsSpan(4).ToArray()).ReadLengthEncodedBytes()));
            Assert.Equal(number, parameter.Value);
        }

        [Theory]
        [InlineData(OceanBaseCompatibilityMode.MySql, 65, 20)]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 38, 127)]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 3, 5)]
        public void WideDecimalTypedNullsValidateModeLimitsAndUseNullBitmap(OceanBaseCompatibilityMode mode, byte precision, byte scale)
        {
            var parameter = new OceanBaseParameter { DbType = DbType.Decimal, Precision = precision, Scale = scale, Value = DBNull.Value };
            Assert.Equal(new byte[] { 1, 1, 6, 0 }, PreparedParameters.Encode(new[] { parameter }, mode));
        }

        [Theory]
        [InlineData(0, 125)]
        [InlineData(2, 100)]
        public void OracleExplicitScaleWithoutPrecisionDoesNotInventDeclaredPrecision(byte scale, int exponent)
        {
            var parameter = new OceanBaseParameter { Value = new OceanBaseDecimal(BigInteger.One, -exponent), Scale = scale };
            var wire = PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle);
            var expected = "1" + new string('0', exponent) + (scale == 0 ? "" : "." + new string('0', scale));
            Assert.Equal(expected, Encoding.ASCII.GetString(new ByteCursor(wire.AsSpan(4).ToArray()).ReadLengthEncodedBytes()));
            parameter.Precision = 38;
            Assert.Throws<OverflowException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        }

        [Fact]
        public void MySqlDeclaredPrecisionCountsExpandedNegativeScaleDigits()
        {
            var parameter = new OceanBaseParameter { Value = new OceanBaseDecimal(new BigInteger(123), -3), Precision = 5 };
            Assert.Throws<OverflowException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.MySql));
            parameter.Precision = 6;
            var wire = PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.MySql);
            Assert.Equal("123000", Encoding.ASCII.GetString(new ByteCursor(wire.AsSpan(4).ToArray()).ReadLengthEncodedBytes()));
        }

        [Theory]
        [InlineData(-65)]
        [InlineData(-84)]
        public void MySqlNegativeScaleZeroDoesNotInventIntegerDigits(int scale)
        {
            var parameter = new OceanBaseParameter { Value = new OceanBaseDecimal(BigInteger.Zero, scale) };
            var wire = PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.MySql);
            Assert.Equal("0", Encoding.ASCII.GetString(new ByteCursor(wire.AsSpan(4).ToArray()).ReadLengthEncodedBytes()));
        }

        [Theory]
        [InlineData(OceanBaseCompatibilityMode.MySql, "100000000000000000000000000000000000000000000000000000000000000000")]
        [InlineData(OceanBaseCompatibilityMode.MySql, "1e-31")]
        [InlineData(OceanBaseCompatibilityMode.MySql, "1e256")]
        [InlineData(OceanBaseCompatibilityMode.Oracle, "1e-131")]
        [InlineData(OceanBaseCompatibilityMode.Oracle, "1e126")]
        [InlineData(OceanBaseCompatibilityMode.Oracle, "123456789012345678901234567890123456789")]
        public void WideDecimalModeLimitsNeverSilentlyRound(OceanBaseCompatibilityMode mode, string text)
        {
            var column = ValueColumn(246, mode, 0);
            Assert.Throws<OverflowException>(() => column.Decode(Encoding.ASCII.GetBytes(text)));
            Assert.Throws<OverflowException>(() => column.DecodeBinary(Encoding.ASCII.GetBytes(text)));
        }

        [Theory]
        [InlineData("1e257")]
        [InlineData("1e-257")]
        [InlineData("1e2147483647")]
        [InlineData("1e-2147483648")]
        public void WideDecimalPayloadExponentsAreBounded(string text)
        {
            var column = ValueColumn(246, OceanBaseCompatibilityMode.Oracle, 0);
            Assert.Throws<FormatException>(() => column.Decode(Encoding.ASCII.GetBytes(text)));
            Assert.Throws<FormatException>(() => column.Decode(Encoding.ASCII.GetBytes(new string('0', 513))));
        }

        [Theory]
        [InlineData(int.MinValue)]
        [InlineData(int.MaxValue)]
        [InlineData(-257)]
        [InlineData(257)]
        public void WideDecimalParameterScaleIsBoundedBeforeExpansion(int scale)
        {
            var parameter = new OceanBaseParameter { Value = new OceanBaseDecimal(BigInteger.One, scale) };
            Assert.Throws<OverflowException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            parameter.Scale = 0;
            Assert.Throws<OverflowException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WideUnsignedDecimalRejectsNegativeValues(bool binary)
        {
            var column = ValueColumn(246, OceanBaseCompatibilityMode.MySql, 65, 20, flags: 32);
            var bytes = Encoding.ASCII.GetBytes("-1");
            Assert.Throws<OverflowException>(() => binary ? column.DecodeBinary(bytes) : column.Decode(bytes));
        }

        [Theory]
        [InlineData(OceanBaseCompatibilityMode.MySql, 66, 0)]
        [InlineData(OceanBaseCompatibilityMode.MySql, 65, 31)]
        [InlineData(OceanBaseCompatibilityMode.MySql, 4, 5)]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 39, 0)]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 38, 128)]
        public void WideDecimalInvalidMetadataRejectsNullAndNonNull(OceanBaseCompatibilityMode mode, byte precision, byte scale)
        {
            var parameter = new OceanBaseParameter { DbType = DbType.Decimal, Precision = precision, Scale = scale };
            var column = ValueColumn(246, mode, precision, scale);
            Assert.Throws<NotSupportedException>(() => column.Decode(null));
            Assert.Throws<NotSupportedException>(() => column.Decode(Encoding.ASCII.GetBytes("1")));
            foreach (var value in new object[] { DBNull.Value, 1m })
            {
                parameter.Value = value;
                if (mode == OceanBaseCompatibilityMode.MySql && precision == 4)
                    Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, mode));
                else Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, mode));
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task WideDecimalPublicReaderConvertsExactlyAndWritesProviderValuesBack(bool asynchronous, bool binary)
        {
            const string large = "123456789012345678901234567890123456.12";
            var server = Server(async channel =>
            {
                if (binary)
                {
                    await StatementCommand(channel, 22); await Prepared(channel, 91, 1);
                    var initial = await StatementCommand(channel, 23);
                    Assert.Equal(large, Encoding.ASCII.GetString(new ByteCursor(initial.AsSpan(14).ToArray()).ReadLengthEncodedBytes()));
                }
                else await Query(channel, "values");
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(ValueColumnPacket(246, 38, 2));
                await channel.WritePacketAsync(Eof());
                foreach (var text in new[] { large, "12.34" })
                {
                    var bytes = WithLength(Encoding.ASCII.GetBytes(text));
                    if (binary) { var row = new byte[bytes.Length + 2]; bytes.CopyTo(row, 2); bytes = row; }
                    await channel.WritePacketAsync(bytes);
                }
                await channel.WritePacketAsync(binary ? new byte[] { 0, 4 } : new byte[] { 251 });
                await channel.WritePacketAsync(Eof());
                if (!binary) { await StatementCommand(channel, 22); await Prepared(channel, 91, 1); }
                var writeBack = await StatementCommand(channel, 23);
                Assert.Equal(new byte[] { 0, 1, 246, 0 }, writeBack.AsSpan(10, 4).ToArray());
                Assert.Equal(large, Encoding.ASCII.GetString(new ByteCursor(writeBack.AsSpan(14).ToArray()).ReadLengthEncodedBytes()));
                await channel.WritePacketAsync(Ok(1));
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                var input = new OceanBaseDecimal(BigInteger.Parse("12345678901234567890123456789012345612", CultureInfo.InvariantCulture), 2);
                command.CommandText = binary ? "select :n" : "values";
                if (binary) command.Parameters.Add(new OceanBaseParameter { ParameterName = "n", Value = input });
                OceanBaseDecimal provider;
                using (var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader())
                {
                    Assert.Equal(typeof(OceanBaseDecimal), reader.GetFieldType(0));
                    Assert.Equal(typeof(OceanBaseDecimal), reader.GetProviderSpecificFieldType(0));
                    var schema = Assert.Single(reader.GetColumnSchema());
                    Assert.Equal(typeof(OceanBaseDecimal), schema.DataType);
                    Assert.Equal(38, schema.NumericPrecision); Assert.Equal(2, schema.NumericScale);
                    Assert.Equal(typeof(OceanBaseDecimal), reader.GetSchemaTable()!.Rows[0]["DataType"]);
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    provider = reader.GetFieldValue<OceanBaseDecimal>(0);
                    Assert.Equal(input, provider);
                    Assert.Throws<OverflowException>(() => reader.GetDecimal(0));
                    Assert.Throws<OverflowException>(() => reader.GetFieldValue<decimal>(0));
                    Assert.Equal(provider, reader.GetValue(0));
                    var values = new object[1]; Assert.Equal(1, reader.GetValues(values)); Assert.IsType<OceanBaseDecimal>(values[0]);
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Equal(12.34m, reader.GetDecimal(0)); Assert.Equal(12.34m, reader.GetFieldValue<decimal>(0));
                    Assert.IsType<OceanBaseDecimal>(reader.GetValue(0));
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.True(reader.IsDBNull(0)); Assert.Equal(DBNull.Value, reader.GetValue(0));
                    Assert.Equal(DBNull.Value, reader.GetProviderSpecificValue(0));
                    Assert.Equal(typeof(OceanBaseDecimal), reader.GetFieldType(0));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
                command.Parameters.Clear(); command.CommandText = "select :n";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "n", Value = provider });
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
