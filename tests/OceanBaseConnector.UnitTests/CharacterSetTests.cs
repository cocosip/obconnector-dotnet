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
                (new ushort[] { 3, 69, 338, 353, 368 }, "\u0080\u009f¤ŒŸœÿ\0  ", "809FA8D7DDF7FD002020", 1),
                (new ushort[] { 18, 89, 332, 347, 362 }, "\u0080\u009fกฺ฿๛\0  ", "809FA1DADFFB002020", 1),
                (new ushort[] { 13, 88, 334, 349, 364 }, "日本語ｶﾅ\\〜‖−¢£¬\0  ", "93FA967B8CEAB6C5815F81608161817C8191819281CA002020", 2),
                (new ushort[] { 1, 84, 335, 350, 365 }, "中•∼¥ヾぁЁ①\0  ", "A4A4A145A1E3A244C6A1C6A5C7B3C7E9002020", 2),
                (new ushort[] { 152, 336, 351, 366 }, "\uf308\uf30aĀ\uffed中\0  ", "884588478856F9FEA4A4002020", 2),
                (new ushort[] { 153, 337, 352, 367 }, "\U0002010c\U000200d1Ā\uffed中\0  ", "884588478856F9FEA4A4002020", 2),
                (new ushort[] { 5, 8, 15, 31, 47, 48, 49, 94, 329, 344, 359 }, "é€‘Ÿ\0  ", "E980919F002020", 1),
                (new ushort[] { 28, 87, 326, 341, 356 }, "中文\0  ", "D6D0CEC4002020", 2),
                (new ushort[] { 248, 249, 251, 328, 343, 358 }, "中文😀\0  ", "D6D0CEC49439FC36002020", 4),
                (new ushort[] { 216, 217, 218, 219, 220, 221, 222, 330, 345, 360 }, "ḿ\ue7c7龴\ue81e\ufe10\ue78d中😀\0  ", "A8BC8135F437FE5982359037A6D984318236D6D09439FC36002020", 4),
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
            foreach (var type in new byte[] { 15, 253, 254, 247, 248, 249, 250, 251, 252 })
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
        [InlineData(152, "80")]
        [InlineData(336, "FF")]
        [InlineData(351, "88")]
        [InlineData(366, "887F")]
        [InlineData(152, "A3C0")]
        [InlineData(153, "A3FE")]
        [InlineData(337, "88")]
        [InlineData(352, "8880")]
        [InlineData(367, "FFA4")]
        [InlineData(1, "A15A")]
        [InlineData(84, "A1C3")]
        [InlineData(335, "A1C5")]
        [InlineData(350, "A1FE")]
        [InlineData(365, "A240")]
        [InlineData(1, "A2CC")]
        [InlineData(84, "A2CE")]
        [InlineData(335, "A3E1")]
        [InlineData(350, "C7FD")]
        [InlineData(365, "F9DD")]
        [InlineData(1, "C840")]
        [InlineData(1, "80")]
        [InlineData(84, "A4")]
        [InlineData(335, "A47F")]
        [InlineData(350, "A480")]
        [InlineData(365, "FF")]
        [InlineData(13, "80")]
        [InlineData(88, "A0")]
        [InlineData(334, "FD")]
        [InlineData(349, "FE")]
        [InlineData(364, "FF")]
        [InlineData(13, "81")]
        [InlineData(13, "817F")]
        [InlineData(13, "81AD")]
        [InlineData(88, "8740")]
        [InlineData(334, "EB40")]
        [InlineData(349, "FA4A")]
        [InlineData(364, "EAA5")]
        [InlineData(3, "A4")]
        [InlineData(69, "A6")]
        [InlineData(338, "D0")]
        [InlineData(353, "F0")]
        [InlineData(368, "FF")]
        [InlineData(18, "A0")]
        [InlineData(89, "DB")]
        [InlineData(332, "DC")]
        [InlineData(347, "FC")]
        [InlineData(362, "FF")]
        [InlineData(28, "81")]
        [InlineData(28, "80")]
        [InlineData(87, "FF")]
        [InlineData(87, "8130")]
        [InlineData(248, "9439FC")]
        [InlineData(249, "813081")]
        [InlineData(248, "8431A530")]
        [InlineData(248, "8F39FE39")]
        [InlineData(248, "E3329A36")]
        [InlineData(216, "80")]
        [InlineData(217, "FF")]
        [InlineData(218, "81")]
        [InlineData(219, "8130")]
        [InlineData(220, "813081")]
        [InlineData(221, "813A8130")]
        [InlineData(222, "81308030")]
        [InlineData(330, "8130813A")]
        [InlineData(345, "8431A530")]
        [InlineData(360, "E3329A36")]
        [InlineData(216, "8F39FE39")]
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
        [InlineData(223)]
        [InlineData(324)]
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
