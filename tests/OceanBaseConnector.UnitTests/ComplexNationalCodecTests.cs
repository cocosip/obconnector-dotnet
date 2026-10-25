using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Values;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class ComplexNationalCodecTests
    {
        [Theory]
        [InlineData(207)]
        [InlineData(208)]
        public void NationalMemberLayoutsAreDistinctFromOrdinaryCharacterMembers(byte code)
        {
            var national = new ComplexType(code);
            Assert.Equal(code, national.TypeCode);
            Assert.Empty(national.Members);
        }

        [Theory]
        [InlineData(207)]
        [InlineData(208)]
        public void ComplexInputsUseConnectionEncodingEvenWhenNationalSessionEncodingDiffers(byte code)
        {
            var type = new ComplexType(false, new ComplexType(code), new ComplexType(253));
            var encoded = ComplexValueCodec.EncodeParameter(type, new object?[] { "中文🙂", " x " }, OceanBaseCompatibilityMode.Oracle,
                new NationalCharacterSet("utf16", "utf8mb4", "utf8mb4"));
            Assert.Equal(Convert.FromHexString("000AE4B8ADE69687F09F998203207820"), encoded);
        }

        [Theory]
        [InlineData(207)]
        [InlineData(208)]
        public void PrevalidationChecksUnicodeWithoutACharacterSetOrChangingTheValue(byte code)
        {
            var type = new ComplexType(true, new ComplexType(code));
            object?[] values = { "\ufeff中文🙂  ", null, "" };
            Assert.NotEmpty(ComplexValueCodec.EncodeParameter(type, values, OceanBaseCompatibilityMode.Oracle, validateOnly: true));
            Assert.Equal("\ufeff中文🙂  ", values[0]);
            Assert.Throws<NotSupportedException>(() => ComplexValueCodec.EncodeParameter(type, values, OceanBaseCompatibilityMode.Oracle));
            foreach (var invalid in new object[] { 42, new byte[] { 1 }, "\ud800", "\udc00" })
                Assert.ThrowsAny<ArgumentException>(() => ComplexValueCodec.EncodeParameter(type, new[] { invalid }, OceanBaseCompatibilityMode.Oracle, validateOnly: true));
        }

        [Theory]
        [InlineData(207, false)]
        [InlineData(208, false)]
        [InlineData(207, true)]
        [InlineData(208, true)]
        public void NestedNationalMembersUseExplicitResultEncodingAndDirectionalNullBitmaps(byte code, bool utf16)
        {
            var type = new ComplexType(false, new ComplexType(true, new ComplexType(false, new ComplexType(code), new ComplexType(253))), new ComplexType(0));
            Assert.True(type.HasNationalMembers);
            var encoding = utf16 ? NationalCharacterSet.Utf16 : ResultColumn.Utf8;
            const string text = "\ufeff中文🙂  ";
            var field = encoding.GetBytes(text);
            var bytes = new byte[] { 0, 2, 8, 0, (byte)field.Length }.Concat(field).Concat(Convert.FromHexString("032078200234327B")).ToArray();
            var cursor = new ByteCursor(bytes);
            var values = ComplexValueCodec.DecodeResult(cursor, type, OceanBaseCompatibilityMode.Oracle, encoding);
            var collection = Assert.IsType<object?[]>(values[0]); Assert.Null(collection[1]);
            var child = Assert.IsType<object?[]>(collection[0]); Assert.Equal(text, child[0]); Assert.Equal(" x ", child[1]);
            Assert.Equal("42", values[1]!.ToString()); Assert.Equal(123, cursor.ReadUInt8()); Assert.False(cursor.HasRemaining);
            var scan = new ByteCursor(bytes);
            Assert.Equal(bytes.Take(bytes.Length - 1), ComplexValueCodec.ReadResultField(scan, type, OceanBaseCompatibilityMode.Oracle));
            Assert.Equal(123, scan.ReadUInt8());
            var encoded = ComplexValueCodec.EncodeParameter(type, new object?[] { new object?[] { new object?[] { text, " x " }, null }, 42 }, OceanBaseCompatibilityMode.Oracle,
                new NationalCharacterSet("utf16", "utf16", "utf8mb4"));
            Assert.Equal(new byte[] { 0, 2, 2, 0, (byte)ResultColumn.Utf8.GetByteCount(text) }.Concat(ResultColumn.Utf8.GetBytes(text))
                .Concat(Convert.FromHexString("03207820023432")), encoded);
        }

        [Theory]
        [InlineData(207, false, "FF")]
        [InlineData(208, false, "C080")]
        [InlineData(207, true, "D800")]
        [InlineData(208, true, "0041FF")]
        public void InvalidNationalResultBytesAreNeverReplaced(byte code, bool utf16, string hex)
        {
            var data = Convert.FromHexString(hex); var type = new ComplexType(false, new ComplexType(code));
            Assert.Throws<DecoderFallbackException>(() => ComplexValueCodec.DecodeResult(new ByteCursor(new byte[] { 0, (byte)data.Length }.Concat(data).ToArray()),
                type, OceanBaseCompatibilityMode.Oracle, utf16 ? NationalCharacterSet.Utf16 : ResultColumn.Utf8));
        }

        [Theory]
        [InlineData(207)]
        [InlineData(208)]
        public void MissingEncodingRejectsNonNullMembersButNullAndFramingNeedNoGuess(byte code)
        {
            var type = new ComplexType(false, new ComplexType(code));
            Assert.Throws<NotSupportedException>(() => ComplexValueCodec.DecodeResult(new ByteCursor(new byte[] { 0, 1, 65 }), type, OceanBaseCompatibilityMode.Oracle));
            Assert.Null(Assert.Single(ComplexValueCodec.DecodeResult(new ByteCursor(new byte[] { 4 }), type, OceanBaseCompatibilityMode.Oracle)));
            Assert.Equal(new byte[] { 0, 1, 255 }, ComplexValueCodec.ReadResultField(new ByteCursor(new byte[] { 0, 1, 255 }), type, OceanBaseCompatibilityMode.Oracle));
            Assert.Throws<ArgumentException>(() => ComplexValueCodec.EncodeParameter(type, new object?[] { new string('中', (int)PacketChannel.DefaultMaxPacketSize / 3) },
                OceanBaseCompatibilityMode.Oracle, new NationalCharacterSet("utf16", null, "utf8mb4")));
            Assert.Throws<NotSupportedException>(() => ComplexValueCodec.EncodeParameter(type, new object?[] { "x" }, OceanBaseCompatibilityMode.Oracle,
                new NationalCharacterSet("utf8mb4", "utf8mb4", "unknown")));
        }
    }
}
