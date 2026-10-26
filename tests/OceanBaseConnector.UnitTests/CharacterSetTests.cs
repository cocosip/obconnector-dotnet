using System;
using System.Collections.Generic;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> ResultCharacterSets()
        {
            foreach (var (ids, text, hex, width) in new[]
            {
                (new ushort[] { 11, 65, 331, 346, 361 }, "ASCII\0  ", "4153434949002020", 1),
                (new ushort[] { 5, 8, 15, 31, 47, 48, 49, 94, 329, 344, 359 }, "é€‘Ÿ\0  ", "E980919F002020", 1),
                (new ushort[] { 28, 87, 326, 341, 356 }, "中文\0  ", "D6D0CEC4002020", 2),
                (new ushort[] { 248, 249, 251, 328, 343, 358 }, "中文😀\0  ", "D6D0CEC49439FC36002020", 4),
                (new ushort[] { 54, 55, 101, 102, 124, 327, 342, 357 }, "\ufeff中😀\0  ", "FEFF4E2DD83DDE00000000200020", 4),
                (new ushort[] { 56, 62, 333, 348, 363 }, "\ufeff中😀\0  ", "FFFE2D4E3DD800DE000020002000", 4),
                (new ushort[] { 33, 83, 192, 215 }, "中é  ", "E4B8ADC3A92020", 3),
                (new ushort[] { 45, 46, 224, 225, 234, 245, 246, 247, 255, 271, 273, 277, 294, 296, 300, 303, 323, 325, 340, 355 }, "中é😀  ", "E4B8ADC3A9F09F98802020", 4),
            })
                foreach (var id in ids)
                    foreach (var mode in new[] { OceanBaseCompatibilityMode.MySql, OceanBaseCompatibilityMode.Oracle })
                        yield return new object[] { id, text, hex, width, mode };
        }

        [Theory]
        [MemberData(nameof(ResultCharacterSets))]
        public void ResultCharsetPreservesExactBytesAndSchema(ushort collation, string text, string hex, int width, OceanBaseCompatibilityMode mode)
        {
            var bytes = Convert.FromHexString(hex);
            foreach (var type in new byte[] { 15, 253, 254, 249, 250, 251, 252 })
            {
                var column = ResultColumn.Parse(SchemaColumn("字符列", type, 120, charset: collation), mode);
                Assert.Equal(typeof(string), column.FieldType);
                Assert.Equal(OceanBaseDbType.String, column.ProviderType);
                Assert.Equal(text, column.Decode(bytes));
                Assert.Equal(text, column.DecodeBinary(column.ReadBinaryField(new ByteCursor(WithLength(bytes)))));
                Assert.Equal(DBNull.Value, column.Decode(null));
                Assert.Equal(mode == OceanBaseCompatibilityMode.MySql ? 120 / width : 120, new ResultDbColumn(column, 0).ColumnSize);
                Assert.Equal("字符列", column.Name);
            }
        }

        [Theory]
        [InlineData(11, "80")]
        [InlineData(65, "FF")]
        [InlineData(28, "81")]
        [InlineData(28, "80")]
        [InlineData(87, "FF")]
        [InlineData(87, "8130")]
        [InlineData(248, "9439FC")]
        [InlineData(249, "813081")]
        [InlineData(248, "8431A530")]
        [InlineData(248, "8F39FE39")]
        [InlineData(248, "E3329A36")]
        [InlineData(54, "D800")]
        [InlineData(56, "00D8")]
        [InlineData(101, "00")]
        [InlineData(225, "C0AF")]
        public void ResultCharsetNeverReplacesMalformedSequences(ushort collation, string hex)
        {
            var column = ResultColumn.Parse(Column("text", 253, collation), OceanBaseCompatibilityMode.MySql);
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
        [InlineData(100)]
        [InlineData(216)]
        [InlineData(222)]
        [InlineData(250)]
        [InlineData(272)]
        [InlineData(276)]
        [InlineData(295)]
        [InlineData(299)]
        [InlineData(301)]
        [InlineData(302)]
        [InlineData(65535)]
        public void UnknownOrDifferentCharsetVariantsAreNotGuessed(ushort collation)
        {
            var column = ResultColumn.Parse(Column("text", 253, collation), OceanBaseCompatibilityMode.MySql);
            Assert.Throws<NotSupportedException>(() => column.FieldType);
            Assert.Throws<NotSupportedException>(() => column.Decode(new byte[] { 65 }));
        }

        [Theory]
        [InlineData("A8BC", "\u1e3f")]
        [InlineData("8135F437", "\ue7c7")]
        public void Gb18030UsesOceanBase2005MappingsRatherThanWindows2000(string hex, string expected)
        {
            var column = ResultColumn.Parse(Column("text", 253, 248), OceanBaseCompatibilityMode.MySql);
            var bytes = Convert.FromHexString(hex);
            Assert.Equal(expected, column.Decode(bytes));
            Assert.Equal(bytes, column.TextEncoding.GetBytes(expected));
            var decoder = column.TextEncoding.GetDecoder();
            var actual = new StringBuilder();
            var buffer = new char[2];
            for (var i = 0; i < bytes.Length; i++)
                actual.Append(buffer, 0, decoder.GetChars(bytes, i, 1, buffer, 0, flush: i + 1 == bytes.Length));
            Assert.Equal(expected, actual.ToString());
        }

        [Theory]
        [MemberData(nameof(ResultCharacterSets))]
        public void ResultCharsetDecoderPreservesEveryByteBoundary(ushort collation, string expected, string hex, int width, OceanBaseCompatibilityMode mode)
        {
            _ = width;
            var encoding = ResultColumn.Parse(Column("text", 253, collation), mode).TextEncoding;
            var bytes = Convert.FromHexString(hex);
            foreach (var step in new[] { 1, 3, 7 })
            {
                var decoder = encoding.GetDecoder();
                var text = new StringBuilder();
                var buffer = new char[16];
                for (var i = 0; i < bytes.Length; i += step)
                {
                    var length = Math.Min(step, bytes.Length - i);
                    var flush = i + length == bytes.Length;
                    var count = decoder.GetCharCount(bytes, i, length, flush);
                    Assert.Equal(count, decoder.GetChars(bytes, i, length, buffer, 0, flush));
                    text.Append(buffer, 0, count);
                }
                Assert.Equal(expected, text.ToString());
                decoder.Reset();
                Assert.Equal(expected.Length, decoder.GetChars(bytes, 0, bytes.Length, buffer, 0, true));
                Assert.Equal(expected, new string(buffer, 0, expected.Length));
            }
        }
    }
}
