using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData("NUMBER", (byte)246, "023432")]
        [InlineData("VARCHAR2", (byte)253, "0AE682A3E88085F09F9880")]
        [InlineData("CHAR", (byte)254, "0161")]
        [InlineData("RAW", (byte)203, "0200FF")]
        [InlineData("DATE", (byte)12, "07E8070203040506")]
        [InlineData("TIMESTAMP", (byte)202, "0C1418020304050615CD5B0709")]
        public async Task OracleArrayBindingRequestUsesIterationTwoBatchErrorsAndCanonicalColumns(string name, byte code, string body)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = connection.CreateArrayType(name);
                object item = name == "NUMBER" ? 42 : name == "VARCHAR2" ? "患者😀" : name == "CHAR" ? "a" : name == "RAW" ? new byte[] { 0, 255 }
                    : name == "DATE" ? new DateTime(2024, 2, 3, 4, 5, 6) : new OceanBaseTimestamp(new DateTime(2024, 2, 3, 4, 5, 6), 123456789, 9);
                var columns = new[] { new OceanBaseParameter { Value = type.CreateValue(item, null) } };
                var packet = OraclePrepareExecutePacket.BuildArray(0, "begin p; end;", columns);
                var expected = Convert.FromHexString("A10000000000020000000D626567696E20703B20656E643B010000000001A000000000")
                    .Concat(new[] { code, (byte)2, (byte)2 }).Concat(Convert.FromHexString(body))
                    .Concat(Convert.FromHexString("8000000000000000BE5B825B00000000"));
                Assert.Equal(expected, packet);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(1)] [InlineData(9)] [InlineData(251)] [InlineData(65535)]
        public async Task OracleArrayBindingRequestKeepsAllNullElementsAndLengthEncodedRowCount(int count)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = connection.CreateArrayType("RAW");
                var packet = OraclePrepareExecutePacket.BuildArray(uint.MaxValue, "x", new[] { new OceanBaseParameter { Value = type.CreateValue(new object?[count]) } });
                var cursor = new ByteCursor(packet); Assert.Equal(0xa1, cursor.ReadUInt8()); Assert.Equal(uint.MaxValue, cursor.ReadUInt32());
                Assert.Equal(0, cursor.ReadUInt8()); Assert.Equal(2u, cursor.ReadUInt32()); Assert.Equal("x", ResultColumn.Utf8.GetString(cursor.ReadLengthEncodedBytes()));
                Assert.Equal(1u, cursor.ReadUInt32()); Assert.Equal(new byte[] { 0, 1, 160, 0, 0, 0, 0, 203 }, cursor.ReadBytes(8));
                Assert.Equal(count, cursor.ReadLengthEncodedInteger());
                var nulls = cursor.ReadBytes((count + 7) / 8); Assert.Equal((byte)((1 << ((count - 1) % 8 + 1)) - 1), nulls[nulls.Length - 1]);
                Assert.All(nulls.Take(nulls.Length - 1), b => Assert.Equal(255, b));
                Assert.Equal(128u, cursor.ReadUInt32()); Assert.Equal(0u, cursor.ReadUInt32()); cursor.ReadUInt32(); Assert.Equal(0u, cursor.ReadUInt32()); Assert.False(cursor.HasRemaining);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task OracleArrayBindingRequestWritesAllHeadersBeforeMixedColumnBodies()
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var number = connection.CreateArrayType("NUMBER"); var raw = connection.CreateArrayType("RAW");
                var packet = OraclePrepareExecutePacket.BuildArray(0x89abcdef, "begin p('患者'); end;", new[]
                {
                    new OceanBaseParameter { Value = number.CreateValue(42, null, -7) },
                    new OceanBaseParameter { ComplexType = raw, Value = raw.CreateValue(null, new byte[] { 8, 9 }, null) },
                });
                var expected = Convert.FromHexString("A1EFCDAB89000200000017626567696E20702827E682A3E8808527293B20656E643B02000000")
                    .Concat(Convert.FromHexString("0001A000000000F6A000000000CB0302023432022D370305020809800000000000000011C6398200000000"));
                Assert.Equal(expected, packet);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task OracleArrayBindingRequestKeepsNamedElementIdentity(bool collection)
        {
            var server = Server(async channel => { await ComplexScalarType(channel, collection: collection); Assert.Equal(0, await channelStreamRead(channel)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var child = await connection.GetComplexTypeAsync("Owner", "Thing"); var type = connection.CreateArrayType(child);
                var packet = OraclePrepareExecutePacket.BuildArray(0, "begin p; end;", new[] { new OceanBaseParameter { Value = type.CreateValue(child.CreateValue(new byte[] { 42 }), null) } });
                var expected = Convert.FromHexString("A10000000000020000000D626567696E20703B20656E643B010000000001A000000000A0054F776E6572055468696E67000202")
                    .Concat(Convert.FromHexString(collection ? "0100012A" : "00012A")).Concat(Convert.FromHexString("8000000000000000BE5B825B00000000"));
                Assert.Equal(expected, packet);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("empty")] [InlineData("length")] [InlineData("null-root")] [InlineData("dbnull-root")] [InlineData("scalar")]
        [InlineData("named")] [InlineData("readonly")] [InlineData("wrong-value")] [InlineData("size")]
        [InlineData("output")] [InlineData("inout")] [InlineData("return")]
        public async Task OracleArrayBindingRequestRejectsInvalidColumnsBeforeBuilding(string invalid)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = connection.CreateArrayType("RAW");
                var parameter = new OceanBaseParameter { ComplexType = type, Value = type.CreateValue(new byte[] { 42 }) };
                var columns = new[] { parameter };
                switch (invalid)
                {
                    case "empty": parameter.Value = type.CreateValue(); break;
                    case "length": columns = new[] { parameter, new OceanBaseParameter { Value = type.CreateValue(null, null) } }; break;
                    case "null-root": parameter.Value = null; break;
                    case "dbnull-root": parameter.Value = DBNull.Value; break;
                    case "scalar": columns = new[] { new OceanBaseParameter { Value = 42 } }; break;
                    case "named": parameter.ComplexType = ComplexResultType(true, ComplexResultScalar("A", 203)); parameter.Value = null; break;
                    case "readonly": parameter.ComplexType = ResultColumn.Parse(ComplexColumn("", ""), OceanBaseCompatibilityMode.Oracle).ComplexType; parameter.Value = null; break;
                    case "wrong-value": parameter.Value = "bad"; break;
                    case "size": parameter.Size = 1; break;
                    case "output": parameter.Direction = ParameterDirection.Output; break;
                    case "inout": parameter.Direction = ParameterDirection.InputOutput; break;
                    case "return": parameter.Direction = ParameterDirection.ReturnValue; break;
                }
                if (invalid == "readonly" || invalid == "output" || invalid == "inout" || invalid == "return")
                    Assert.Throws<NotSupportedException>(() => OraclePrepareExecutePacket.BuildArray(0, "x", columns));
                else Assert.Throws<ArgumentException>(() => OraclePrepareExecutePacket.BuildArray(0, "x", columns));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void OracleArrayBindingRequestRejectsMissingOrExcessiveColumnLists()
        {
            Assert.Throws<ArgumentNullException>(() => OraclePrepareExecutePacket.BuildArray(0, "x", null!));
            Assert.Throws<ArgumentOutOfRangeException>(() => OraclePrepareExecutePacket.BuildArray(0, "x", Array.Empty<OceanBaseParameter>()));
            Assert.Throws<ArgumentOutOfRangeException>(() => OraclePrepareExecutePacket.BuildArray(0, "x", new OceanBaseParameter[65536]));
            Assert.Throws<ArgumentException>(() => OraclePrepareExecutePacket.BuildArray(0, "x", new OceanBaseParameter[1]));
        }
    }
}
