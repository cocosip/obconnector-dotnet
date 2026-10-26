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
        public static IEnumerable<object[]> BlobFamilyReaderCases()
        {
            foreach (byte type in new byte[] { 249, 250, 251 })
                foreach (var raw in new[] { false, true })
                    foreach (var oracle in new[] { false, true })
                        foreach (var asynchronous in new[] { false, true })
                            foreach (var binary in new[] { false, true })
                                foreach (var sequential in new[] { false, true })
                                    yield return new object[] { type, raw, oracle, asynchronous, binary, sequential };
        }

        [Theory]
        [MemberData(nameof(BlobFamilyReaderCases))]
        public Task BlobFamilyPublicReadersPreserveBytesTextEmptyNullAndReuse(byte type, bool raw, bool oracle, bool asynchronous, bool binary, bool sequential)
            => CharacterSetFixture.Verify(typeof(OceanBaseConnection), ProtocolFixture.Load("initial-handshake-oceanbase").Payload,
                (channel, _) => ReadClientCommand(channel), type == 249 ? 2 : type == 250 ? 4 : 7,
                oracle, asynchronous, binary, sequential, type, raw);

        [Theory]
        [InlineData(249)]
        [InlineData(250)]
        [InlineData(251)]
        public void BlobFamilyDistinguishesBinaryDataFromStrictText(byte type)
        {
            var bytes = new byte[] { 0, 255, 128, 32 };
            var raw = ResultColumn.Parse(SchemaColumn("v", type, uint.MaxValue, charset: 63), OceanBaseCompatibilityMode.MySql);
            Assert.Equal(bytes, raw.Decode(bytes)); Assert.Equal(bytes, raw.DecodeBinary(bytes));
            Assert.Equal(int.MaxValue, new ResultDbColumn(raw, 0).ColumnSize);
            Assert.Equal(DBNull.Value, raw.DecodeBinary(null));
            var text = ResultColumn.Parse(SchemaColumn("v", type, 120, charset: 28), OceanBaseCompatibilityMode.MySql);
            Assert.Throws<DecoderFallbackException>(() => text.Decode(bytes));
            Assert.Throws<DecoderFallbackException>(() => text.DecodeBinary(bytes));
            var unknown = ResultColumn.Parse(SchemaColumn("v", type, 120, charset: 65535), OceanBaseCompatibilityMode.MySql);
            Assert.Throws<NotSupportedException>(() => unknown.FieldType);
        }
    }
}
