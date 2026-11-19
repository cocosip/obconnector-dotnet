using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> OracleFloatingPointReaderCases()
        {
            foreach (byte type in new byte[] { 4, 5 })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var binary in new[] { false, true })
                        foreach (var sequential in new[] { false, true })
                            yield return new object[] { type, asynchronous, binary, sequential };
        }

        [Theory]
        [MemberData(nameof(OracleFloatingPointReaderCases))]
        public Task OracleFloatingPointReadersPreserveSpecialFiniteNullAndReuse(byte type, bool asynchronous, bool binary, bool sequential)
            => FloatingPointFixture.Verify(typeof(OceanBaseConnection), ProtocolFixture.Load("initial-handshake-oceanbase").Payload,
                (channel, _) => ReadClientCommand(channel), type, asynchronous, binary, sequential);

        [Theory]
        [InlineData(double.MinValue, 0xffefffffffffffffUL)]
        [InlineData(double.MaxValue, 0x7fefffffffffffffUL)]
        [InlineData(double.Epsilon, 1UL)]
        [InlineData(-double.Epsilon, 0x8000000000000001UL)]
        [InlineData(-0.0d, 0x8000000000000000UL)]
        public void OracleDoubleBindUsesTypeFiveZeroFlagsAndOriginalBits(double value, ulong bits)
        {
            var parameter = new OceanBaseParameter { Value = value, OceanBaseDbType = OceanBaseDbType.Double };
            var cursor = new ByteCursor(PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            Assert.Equal(0, cursor.ReadUInt8()); // NULL bitmap
            Assert.Equal(1, cursor.ReadUInt8()); // new parameter types
            Assert.Equal(5, cursor.ReadUInt8());
            Assert.Equal(0, cursor.ReadUInt8());
            Assert.Equal(bits, cursor.ReadUInt64());
            Assert.False(cursor.HasRemaining);
        }

        [Theory]
        [InlineData(4)]
        [InlineData(5)]
        public void OracleFloatingPointTokensMustMatchCompletelyAndDoNotExpandMySql(byte type)
        {
            var oracle = ValueColumn(type, OceanBaseCompatibilityMode.Oracle);
            var mysql = ValueColumn(type, OceanBaseCompatibilityMode.MySql);
            foreach (var malformed in new[] { "Infjunk", "-Infjunk", "NaNjunk", "+Inf", "inf\0" })
                Assert.Throws<FormatException>(() => oracle.Decode(Encoding.ASCII.GetBytes(malformed)));
            foreach (var token in new[] { "Inf", "-Inf" })
                Assert.Throws<FormatException>(() => mysql.Decode(Encoding.ASCII.GetBytes(token)));
        }

        [Theory]
        [InlineData(OceanBaseCompatibilityMode.Oracle)]
        [InlineData(OceanBaseCompatibilityMode.MySql)]
        public void FiniteSingleParsingDoesNotRoundThroughDouble(OceanBaseCompatibilityMode mode)
        {
            var column = ValueColumn(4, mode);
            Assert.Equal(BitConverter.Int32BitsToSingle(0x3f800001), column.Decode(Encoding.ASCII.GetBytes("1.0000000596046448")));
        }
    }
}
