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
        public static IEnumerable<object[]> OracleRowIdReaderCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var binary in new[] { false, true })
                    foreach (var sequential in new[] { false, true })
                        yield return new object[] { asynchronous, binary, sequential };
        }

        [Theory]
        [MemberData(nameof(OracleRowIdReaderCases))]
        public Task OracleRowIdPublicReadersPreserveTextEmptyNullAndReuse(bool asynchronous, bool binary, bool sequential)
            => CharacterSetFixture.Verify(typeof(OceanBaseConnection), ProtocolFixture.Load("initial-handshake-oceanbase").Payload,
                (channel, _) => ReadClientCommand(channel), 7, oracle: true, asynchronous, binary, sequential, type: 209);

        [Theory]
        [InlineData(63)]
        [InlineData(45)]
        [InlineData(65535)]
        public void OracleRowIdUsesSessionTextWithoutInterpretingIdentifierOrCollation(ushort collation)
        {
            const string identifier = "AAAVhPAAEAAAACXAAA";
            var column = ResultColumn.Parse(SchemaColumn("v", 209, 120, charset: collation), OceanBaseCompatibilityMode.Oracle);
            Assert.Equal("ROWID", column.DataTypeName); Assert.Equal(typeof(string), column.FieldType);
            Assert.Equal(OceanBaseDbType.String, column.ProviderType); Assert.Equal(typeof(string), column.ProviderSpecificFieldType);
            var bytes = Encoding.UTF8.GetBytes(identifier);
            Assert.Equal(identifier, column.Decode(bytes));
            Assert.Equal(identifier, column.DecodeBinary(column.ReadBinaryField(new ByteCursor(WithLength(bytes)))));
            var schema = new ResultDbColumn(column, 0);
            Assert.Equal(120, schema.ColumnSize); Assert.False(schema.IsLong); Assert.Null(schema.NumericPrecision); Assert.Null(schema.NumericScale);
        }

        [Theory]
        [InlineData("FF")]
        [InlineData("C0AF")]
        [InlineData("F09F98")]
        public void OracleRowIdNeverReplacesMalformedSessionText(string hex)
        {
            var column = ResultColumn.Parse(SchemaColumn("v", 209, 120, charset: 63), OceanBaseCompatibilityMode.Oracle);
            var bytes = Convert.FromHexString(hex);
            Assert.Throws<DecoderFallbackException>(() => column.Decode(bytes));
            Assert.Throws<DecoderFallbackException>(() => column.DecodeBinary(bytes));
        }

        [Fact]
        public void OracleRowIdIsRejectedInMySqlMode()
        {
            var column = ResultColumn.Parse(SchemaColumn("v", 209, 120, charset: 45), OceanBaseCompatibilityMode.MySql);
            Assert.Throws<NotSupportedException>(() => column.FieldType);
            Assert.Throws<NotSupportedException>(() => column.Decode(Array.Empty<byte>()));
        }
    }
}
