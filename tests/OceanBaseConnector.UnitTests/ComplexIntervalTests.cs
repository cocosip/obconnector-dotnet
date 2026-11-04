using System;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using OceanBaseConnector.Internal.Values;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class ComplexIntervalCodecTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void IntervalMembersKeepExactWireAndNestedNullBoundaries(bool daySecond, bool collection)
        {
            var member = new ComplexType(daySecond ? (byte)205 : (byte)204);
            var child = new ComplexType(collection, member);
            var root = new ComplexType(false, child, new ComplexType(203));
            object value = daySecond ? (object)new OceanBaseIntervalDayToSecond(true, 999999999, 23, 59, 59, 999999999)
                : new OceanBaseIntervalYearToMonth(true, 999999999, 11);
            var payload = Convert.FromHexString(daySecond ? "0E01FFC99A3B173B3BFFC99A3B0909" : "0701FFC99A3B0B09");
            var input = new byte[] { 0 }.Concat(collection ? new byte[] { 3, 2 } : new byte[] { 0 })
                .Concat(payload).Concat(collection ? payload : Array.Empty<byte>()).Concat(new byte[] { 2, 0, 255 }).ToArray();
            var values = collection ? new object?[] { value, null, value } : new object?[] { value };
            Assert.Equal(input, ComplexValueCodec.EncodeParameter(root, new object?[] { values, new byte[] { 0, 255 } }, OceanBaseCompatibilityMode.Oracle));
            var result = (byte[])input.Clone(); if (collection) result[2] = 8;
            var cursor = new ByteCursor(result.Concat(new byte[] { 77 }).ToArray());
            var decoded = ComplexValueCodec.DecodeResult(cursor, root, OceanBaseCompatibilityMode.Oracle);
            var children = Assert.IsType<object?[]>(decoded[0]); Assert.Equal(value, children[0]);
            if (collection) { Assert.Null(children[1]); Assert.Equal(value, children[2]); }
            Assert.Equal(new byte[] { 0, 255 }, Assert.IsType<byte[]>(decoded[1])); Assert.Equal(77, cursor.ReadUInt8());
            var scan = new ByteCursor(result.Concat(new byte[] { 77 }).ToArray());
            Assert.Equal(result, ComplexValueCodec.ReadResultField(scan, root, OceanBaseCompatibilityMode.Oracle)); Assert.Equal(77, scan.ReadUInt8());
            Assert.Throws<ArgumentException>(() => ComplexValueCodec.EncodeParameter(child, new object?[] { "not an interval" }, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(204, 0)]
        [InlineData(204, 6)]
        [InlineData(204, 8)]
        [InlineData(205, 0)]
        [InlineData(205, 13)]
        [InlineData(205, 15)]
        public void InvalidIntervalLengthsAreRejectedBeforeMaterializingOrSkipping(byte code, byte length)
        {
            var type = new ComplexType(false, new ComplexType(code));
            var bytes = new byte[] { 0, length }.Concat(new byte[length]).ToArray();
            Assert.Throws<ProtocolException>(() => ComplexValueCodec.DecodeResult(new ByteCursor(bytes), type, OceanBaseCompatibilityMode.Oracle));
            Assert.Throws<ProtocolException>(() => ComplexValueCodec.ReadResultField(new ByteCursor(bytes), type, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(204, "02000000000009")] // Invalid sign.
        [InlineData(204, "00000000000C09")] // Invalid month.
        [InlineData(204, "0000000000000A")] // Invalid precision.
        [InlineData(205, "0000000000180000000000000909")] // Invalid hour.
        [InlineData(205, "0000000000000000010000000900")] // Fraction would require rounding.
        public void InvalidIntervalComponentsDoNotBecomeComplexValues(byte code, string hex)
        {
            var type = new ComplexType(false, new ComplexType(code)); var payload = Convert.FromHexString(hex);
            var bytes = new byte[] { 0, (byte)payload.Length }.Concat(payload).ToArray();
            Assert.Throws<ProtocolException>(() => ComplexValueCodec.DecodeResult(new ByteCursor(bytes), type, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(false, 0, 0)]
        [InlineData(false, 2, 0)]
        [InlineData(true, 0, 0)]
        [InlineData(true, 2, 3)]
        [InlineData(true, 9, 9)]
        public void EmbeddedIntervalPrecisionsArePreserved(bool daySecond, byte precision, byte scale)
        {
            var type = new ComplexType(false, new ComplexType(daySecond ? (byte)205 : (byte)204));
            var leading = precision == 0 ? 0 : 1;
            object value = daySecond ? (object)new OceanBaseIntervalDayToSecond(true, leading, 1, 2, 3, scale == 0 ? 0 : 123000000, precision, scale)
                : new OceanBaseIntervalYearToMonth(true, leading, 11, precision);
            var bytes = ComplexValueCodec.EncodeParameter(type, new object?[] { value }, OceanBaseCompatibilityMode.Oracle);
            Assert.Equal(value, ComplexValueCodec.DecodeResult(new ByteCursor(bytes), type, OceanBaseCompatibilityMode.Oracle)[0]);
            Assert.Null(ComplexValueCodec.DecodeResult(new ByteCursor(new byte[] { 4 }), type, OceanBaseCompatibilityMode.Oracle)[0]);
        }

        [Theory]
        [InlineData(204)]
        [InlineData(205)]
        public void IntervalTextMembersRequireAnExactSupportedDeclaration(byte code)
        {
            var member = new OceanBaseComplexMember("A", null, "INTERVAL", null, new ComplexType(code));
            var type = new OceanBaseComplexType(null, "Owner", "Thing", false, new[] { member });
            Assert.Throws<NotSupportedException>(() => ComplexValueCodec.DecodeTextResult(ResultColumn.Utf8.GetBytes("Thing(NULL)"), type, OceanBaseCompatibilityMode.Oracle));
        }
    }

    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void NamedIntervalInputsReuseExactClientLiterals(bool daySecond, bool collection)
        {
            var code = daySecond ? (byte)205 : (byte)204;
            var member = new OceanBaseComplexMember("A", null, daySecond ? "INTERVAL DAY(2) TO SECOND(3)" : "INTERVAL YEAR(2) TO MONTH", null, new ComplexType(code));
            var type = new OceanBaseComplexType(null, "Owner", "Thing", collection, new[] { member });
            object value = daySecond ? (object)new OceanBaseIntervalDayToSecond(true, 12, 3, 4, 5, 123000000, 2, 3)
                : new OceanBaseIntervalYearToMonth(true, 12, 3, 2);
            var parameter = new OceanBaseParameter
            {
                ParameterName = "p",
                ComplexType = type,
                Value = new OceanBaseComplexValue(type, collection ? new object?[] { value, null } : new object?[] { value })
            };
            var parameters = new OceanBaseParameterCollection(); parameters.Add(parameter);
            var literal = daySecond ? "INTERVAL '-12 03:04:05.123' DAY(2) TO SECOND(3)" : "INTERVAL '-12-03' YEAR(2) TO MONTH";
            var bindings = ParameterBindingPlan.Create("SELECT :p FROM DUAL", OceanBaseCompatibilityMode.Oracle, parameters);
            Assert.Equal("SELECT (\"Owner\".\"Thing\"(" + literal + (collection ? ",NULL" : "") + ")) FROM DUAL", ClientPreparedSql.Build(bindings, OceanBaseCompatibilityMode.Oracle));
            parameter.Value = DBNull.Value;
            Assert.Equal("SELECT (NULL) FROM DUAL", ClientPreparedSql.Build(bindings, OceanBaseCompatibilityMode.Oracle));
        }

        [Theory]
        [InlineData(false, "INTERVAL YEAR TO MONTH", 204)]
        [InlineData(true, "INTERVAL YEAR TO MONTH", 204)]
        [InlineData(false, "INTERVAL DAY TO SECOND", 205)]
        [InlineData(true, "INTERVAL DAY TO SECOND", 205)]
        [InlineData(false, "INTERVAL YEAR(9) TO MONTH", 204)]
        [InlineData(true, "INTERVAL YEAR(0) TO MONTH", 204)]
        [InlineData(false, "INTERVAL DAY(9) TO SECOND(9)", 205)]
        [InlineData(true, "INTERVAL DAY(2) TO SECOND(3)", 205)]
        [InlineData(false, "INTERVAL DAY TO SECOND(0)", 205)]
        [InlineData(true, "INTERVAL DAY(0) TO SECOND", 205)]
        [InlineData(false, "INTERVAL YEAR(10) TO MONTH", 0)]
        [InlineData(true, "INTERVAL YEAR(-1) TO MONTH", 0)]
        [InlineData(false, "INTERVAL DAY(9) TO SECOND(10)", 0)]
        [InlineData(true, "INTERVAL DAY(9) TO SECOND(9)X", 0)]
        [InlineData(false, "INTERVAL YEAR(9) TO MONTH\n", 0)]
        public async Task NamedIntervalDictionaryMembersAreSupported(bool asynchronous, string scalar, byte code)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, scalar: scalar, collection: false);
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                if (code == 0)
                    await Assert.ThrowsAsync<NotSupportedException>(async () => { if (asynchronous) await connection.GetComplexTypeAsync("Owner", "Thing"); else connection.GetComplexType("Owner", "Thing"); });
                else
                {
                    var type = asynchronous ? await connection.GetComplexTypeAsync("Owner", "Thing") : connection.GetComplexType("Owner", "Thing");
                    Assert.Equal(code, type.Members[0].Layout.TypeCode); Assert.Equal(scalar, type.Members[0].TypeName);
                    object value = code == 204 ? (object)new OceanBaseIntervalYearToMonth(false, 12, 3, 2)
                        : new OceanBaseIntervalDayToSecond(false, 12, 3, 4, 5, 123000000, 2, 3);
                    Assert.Equal(value, type.CreateValue(value).GetValues()[0]);
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
