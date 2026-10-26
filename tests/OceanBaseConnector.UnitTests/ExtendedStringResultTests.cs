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
        public static IEnumerable<object[]> ExtendedStringResultCases()
        {
            foreach (byte type in new byte[] { 247, 248, 255 })
                foreach (var raw in type == 255 ? new[] { true } : new[] { false, true })
                    foreach (var oracle in type == 255 ? new[] { false } : new[] { false, true })
                        foreach (var asynchronous in new[] { false, true })
                            foreach (var binary in new[] { false, true })
                                foreach (var sequential in new[] { false, true })
                                    yield return new object[] { type, raw, oracle, asynchronous, binary, sequential };
        }

        [Theory]
        [MemberData(nameof(ExtendedStringResultCases))]
        public Task ExtendedStringPublicReadersPreserveBytesTextEmptyNullAndReuse(byte type, bool raw, bool oracle, bool asynchronous, bool binary, bool sequential)
            => CharacterSetFixture.Verify(typeof(OceanBaseConnection), ProtocolFixture.Load("initial-handshake-oceanbase").Payload,
                (channel, _) => ReadClientCommand(channel), type == 247 ? 2 : 7,
                oracle, asynchronous, binary, sequential, type, raw);

        [Theory]
        [InlineData(247)]
        [InlineData(248)]
        public void EnumAndSetKeepStrictTextAndBinaryCollationBoundaries(byte type)
        {
            var bytes = new byte[] { 0, 255, 128, 32 };
            var raw = ResultColumn.Parse(SchemaColumn("v", type, 120, charset: 63), OceanBaseCompatibilityMode.MySql);
            Assert.Equal(bytes, raw.Decode(bytes)); Assert.Equal(bytes, raw.DecodeBinary(bytes));
            var text = ResultColumn.Parse(SchemaColumn("v", type, 120, charset: 28), OceanBaseCompatibilityMode.MySql);
            Assert.Throws<DecoderFallbackException>(() => text.Decode(bytes));
            Assert.Throws<DecoderFallbackException>(() => text.DecodeBinary(bytes));
            var unknown = ResultColumn.Parse(SchemaColumn("v", type, 120, charset: 65535), OceanBaseCompatibilityMode.MySql);
            Assert.Throws<NotSupportedException>(() => unknown.FieldType);
        }

        [Fact]
        public void SetReturnsTheCompleteLabelListWithoutSplittingOrTrimming()
        {
            var column = ResultColumn.Parse(SchemaColumn("v", 248, 120, charset: 45), OceanBaseCompatibilityMode.MySql);
            const string labels = "患者,😀,  "; var bytes = Encoding.UTF8.GetBytes(labels);
            Assert.Equal(labels, column.Decode(bytes)); Assert.Equal(labels, column.DecodeBinary(bytes));
        }

        [Theory]
        [InlineData(63)]
        [InlineData(45)]
        [InlineData(65535)]
        public void GeometryAlwaysPreservesOpaqueBytesWithoutCharsetOrSpatialConversion(ushort charset)
        {
            var column = ResultColumn.Parse(SchemaColumn("v", 255, uint.MaxValue, charset: charset), OceanBaseCompatibilityMode.MySql);
            var bytes = Convert.FromHexString("E61000000101000000000000000000F03F0000000000000040");
            Assert.Equal("GEOMETRY", column.DataTypeName); Assert.Equal(typeof(byte[]), column.FieldType);
            Assert.Equal(OceanBaseDbType.Binary, column.ProviderType);
            var text = Assert.IsType<byte[]>(column.Decode(bytes)); var binary = Assert.IsType<byte[]>(column.DecodeBinary(bytes));
            Assert.Equal(bytes, text); Assert.Equal(bytes, binary); Assert.NotSame(bytes, text); Assert.NotSame(bytes, binary);
            Assert.Equal(DBNull.Value, column.Decode(null)); Assert.Equal(DBNull.Value, column.DecodeBinary(null));
            Assert.Empty(Assert.IsType<byte[]>(column.Decode(Array.Empty<byte>())));
            Assert.Equal(int.MaxValue, new ResultDbColumn(column, 0).ColumnSize);
        }

        [Fact]
        public void GeometryIsNotAnOracleResultType()
        {
            var column = ResultColumn.Parse(SchemaColumn("v", 255, 120, charset: 63), OceanBaseCompatibilityMode.Oracle);
            Assert.Throws<NotSupportedException>(() => column.FieldType);
            Assert.Throws<NotSupportedException>(() => column.Decode(Array.Empty<byte>()));
        }
    }
}
