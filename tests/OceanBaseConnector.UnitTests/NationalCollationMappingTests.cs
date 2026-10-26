using System;
using System.Linq;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NationalCollationFallbackAcceptsKnownUnicodeFamilies(bool utf16)
        {
            var ids = utf16
                ? new[] { 54, 55, 327, 342, 357 }.Concat(Enumerable.Range(101, 24))
                : new[] { 33, 83, 45, 46, 325, 340, 355 }.Concat(Enumerable.Range(192, 24))
                    .Concat(Enumerable.Range(224, 24)).Concat(Enumerable.Range(255, 69)
                        .Except(new[] { 272, 276, 295, 299, 301, 302 }));
            var encoding = utf16 ? NationalCharacterSet.Utf16 : ResultColumn.Utf8;
            const string value = "\ufeff国字é\0  ";
            var bytes = encoding.GetBytes(value);
            foreach (var id in ids)
            {
                Assert.Same(encoding, NationalCharacterSet.EncodingForCollation((ushort)id));
                foreach (var settings in new[] { new NationalCharacterSet(null, null, "utf8mb4"),
                    new NationalCharacterSet("binary", "binary", "utf8mb4") })
                    foreach (byte type in new byte[] { 207, 208 })
                    {
                        var column = ResultColumn.Parse(SchemaColumn("n", type, 32, charset: (ushort)id),
                            OceanBaseCompatibilityMode.Oracle, nationalCharacterSet: settings);
                        Assert.Equal(value, column.Decode(bytes));
                        Assert.Equal(value, column.DecodeBinary(column.ReadBinaryField(new ByteCursor(WithLength(bytes)))));
                        Assert.Equal(DBNull.Value, column.Decode(null));
                        Assert.Equal(32, new ResultDbColumn(column, 0).ColumnSize);
                    }
            }
        }

        [Theory]
        [InlineData(309, "C0AF")]
        [InlineData(309, "EDA080")]
        [InlineData(342, "D800")]
        [InlineData(342, "00")]
        public void NationalCollationFallbackRetainsStrictUnicodeDecoding(ushort id, string hex)
        {
            var settings = new NationalCharacterSet("binary", "binary", "utf8mb4");
            var column = ResultColumn.Parse(SchemaColumn("n", 208, 32, charset: id),
                OceanBaseCompatibilityMode.Oracle, nationalCharacterSet: settings);
            var bytes = Convert.FromHexString(hex);
            Assert.Throws<DecoderFallbackException>(() => column.Decode(bytes));
            Assert.Throws<DecoderFallbackException>(() => column.DecodeBinary(bytes));
            var decoder = column.TextEncoding.GetDecoder();
            var chars = new char[8];
            Assert.Throws<DecoderFallbackException>(() =>
            {
                for (var i = 0; i < bytes.Length; i++) decoder.GetChars(bytes, i, 1, chars, 0, flush: i + 1 == bytes.Length);
            });
        }

        [Theory]
        [InlineData(0)]
        [InlineData(8)]
        [InlineData(11)]
        [InlineData(28)]
        [InlineData(56)]
        [InlineData(62)]
        [InlineData(63)]
        [InlineData(100)]
        [InlineData(125)]
        [InlineData(216)]
        [InlineData(222)]
        [InlineData(248)]
        [InlineData(250)]
        [InlineData(272)]
        [InlineData(276)]
        [InlineData(295)]
        [InlineData(299)]
        [InlineData(301)]
        [InlineData(302)]
        [InlineData(333)]
        [InlineData(348)]
        [InlineData(363)]
        [InlineData(65535)]
        public void NationalCollationFallbackRejectsOtherFamiliesButTrustsExplicitSessionEncoding(ushort id)
        {
            Assert.Throws<NotSupportedException>(() => NationalCharacterSet.EncodingForCollation(id));
            Assert.Throws<NotSupportedException>(() => new NationalCharacterSet("binary", "binary", "utf8mb4").ResultEncoding(id));
            Assert.Same(NationalCharacterSet.Utf16, new NationalCharacterSet("utf16", "utf8mb4", "utf8mb4").ResultEncoding(id));
            Assert.Same(ResultColumn.Utf8, new NationalCharacterSet(null, "utf8mb4", "utf16").ResultEncoding(id));
        }
    }
}
