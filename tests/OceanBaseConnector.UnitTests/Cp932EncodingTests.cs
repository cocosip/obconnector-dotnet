using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class Cp932EncodingTests
    {
        [Fact]
        public void EverySingleByteAndPossibleLeadPairMatchesPinnedSourceDigest()
        {
            var encoding = ResultCharacterSet.Resolve(95, out var width); Assert.Equal(2u, width);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var single = 0; single < 256; single++) Append(new[] { (byte)single });
            for (var lead = 0x80; lead <= 0xff; lead++)
                if (lead <= 0xa0 || lead >= 0xe0)
                    for (var trail = 0; trail < 256; trail++) Append(new[] { (byte)lead, (byte)trail });
            Assert.Equal("E1C59E5D535BD4FF5E9FE3A493CE9B212658812A50EED90452157A0727210003", Convert.ToHexString(hash.GetHashAndReset()));

            void Append(byte[] bytes)
            {
                var scalar = uint.MaxValue;
                try
                {
                    var text = encoding.GetString(bytes); Assert.Single(text); scalar = text[0];
                    Assert.Equal(1, encoding.GetCharCount(bytes));
                    var decoder = encoding.GetDecoder(); var chars = new char[1];
                    for (var i = 0; i < bytes.Length; i++)
                    {
                        var count = decoder.GetCharCount(bytes, i, 1, i == bytes.Length - 1);
                        Assert.Equal(count, decoder.GetChars(bytes, i, 1, chars, 0, i == bytes.Length - 1));
                    }
                    Assert.Equal(text[0], chars[0]);
                }
                catch (DecoderFallbackException)
                {
                    Assert.Throws<DecoderFallbackException>(() => encoding.GetCharCount(bytes));
                    Assert.Throws<DecoderFallbackException>(() => encoding.GetDecoder().GetChars(bytes, 0, bytes.Length, new char[2], 0, true));
                }
                hash.AppendData(bytes);
                hash.AppendData(new[] { (byte)(scalar >> 24), (byte)(scalar >> 16), (byte)(scalar >> 8), (byte)scalar });
            }
        }

        [Fact]
        public void EveryBmpEncodingOrRefusalMatchesPinnedSourceDigest()
        {
            var encoding = ResultCharacterSet.Resolve(96, out _);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var scalar = 0; scalar <= 0xffff; scalar++)
            {
                var code = uint.MaxValue; var text = ((char)scalar).ToString();
                try
                {
                    var bytes = encoding.GetBytes(text);
                    Assert.InRange(bytes.Length, 1, 2); Assert.Equal(bytes.Length, encoding.GetByteCount(text));
                    code = bytes.Length == 1 ? bytes[0] : (uint)(bytes[0] << 8 | bytes[1]);
                    Assert.Equal(text, encoding.GetString(bytes));
                }
                catch (EncoderFallbackException) { Assert.Throws<EncoderFallbackException>(() => encoding.GetByteCount(text)); }
                hash.AppendData(new[] { (byte)(scalar >> 8), (byte)scalar, (byte)(code >> 24), (byte)(code >> 16), (byte)(code >> 8), (byte)code });
            }
            Assert.Equal("A09CE4EC13D736FC521D9A55852025E248150D1D79C464A2146385B34A9C06E9", Convert.ToHexString(hash.GetHashAndReset()));
            Assert.Throws<EncoderFallbackException>(() => encoding.GetBytes("😀"));
        }

        public static IEnumerable<object[]> ValidMappings()
        {
            yield return new object[] { "FA5CF040", "纊\ue000" };
            yield return new object[] { "87408754FA40F9FC", "①Ⅰⅰ\ue757" };
            yield return new object[] { "815F", "＼" };
            yield return new object[] { "8160", "～" };
            yield return new object[] { "8161", "∥" };
            yield return new object[] { "817C", "－" };
            yield return new object[] { "8191", "￠" };
            yield return new object[] { "8192", "￡" };
            yield return new object[] { "81CA", "￢" };
            yield return new object[] { "93FA967B8CEAB6C5815F81608161817C8191819281CA002020", "日本語ｶﾅ＼～∥－￠￡￢\0  " };
        }

        [Theory]
        [MemberData(nameof(ValidMappings))]
        public void MappingSplitsCountResetAndTinyOutputPreserveCharacters(string hex, string expected)
        {
            var encoding = ResultCharacterSet.Resolve(95, out _); var bytes = Convert.FromHexString(hex);
            Assert.Equal(expected, encoding.GetString(bytes)); Assert.Equal(bytes, encoding.GetBytes(expected));
            for (var split = 0; split <= bytes.Length; split++)
            {
                var decoder = encoding.GetDecoder(); var chars = new char[expected.Length];
                var first = decoder.GetCharCount(bytes, 0, split, false);
                Assert.Equal(first, decoder.GetCharCount(bytes, 0, split, false));
                Assert.Equal(first, decoder.GetChars(bytes, 0, split, chars, 0, false));
                var second = decoder.GetCharCount(bytes, split, bytes.Length - split, true);
                Assert.Equal(second, decoder.GetChars(bytes, split, bytes.Length - split, chars, first, true));
                Assert.Equal(expected, new string(chars));
                decoder.Reset(); decoder.GetChars(new byte[] { 0x81 }, 0, 1, chars, 0, false); decoder.Reset();
                Assert.Equal(expected.Length, decoder.GetChars(bytes, 0, bytes.Length, chars, 0, true));
                Assert.Equal(expected, new string(chars));
            }
            var tiny = encoding.GetDecoder(); var buffer = new char[3]; var actual = new StringBuilder(); var offset = 0;
            for (var attempt = 0; offset < bytes.Length && attempt < bytes.Length * 2; attempt++)
            {
                tiny.Convert(bytes, offset, bytes.Length - offset, buffer, 1, 1, true, out var used, out var written, out var complete);
                Assert.True(used > 0 || written > 0); offset += used; actual.Append(buffer[1], written);
                Assert.Equal(offset == bytes.Length, complete);
            }
            Assert.Equal(bytes.Length, offset); Assert.Equal(expected, actual.ToString());
        }

        [Theory]
        [InlineData("8790", "81E0", "≒")]
        [InlineData("ED40", "FA5C", "纊")]
        [InlineData("FA4A", "8754", "Ⅰ")]
        public void OfficialDuplicatePositionsDecodeAndEncodeCanonically(string wire, string canonical, string text)
        {
            var encoding = ResultCharacterSet.Resolve(95, out _);
            Assert.Equal(text, encoding.GetString(Convert.FromHexString(wire)));
            Assert.Equal(Convert.FromHexString(canonical), encoding.GetBytes(text));
        }

        [Fact]
        public void ClientPreservesAsciiAndRejectsUnicodeAliases()
        {
            var encoding = ClientCharacterSet.Parse("cp932");
            var ascii = new StringBuilder(); for (var i = 0; i < 128; i++) ascii.Append((char)i);
            Assert.Equal(Encoding.ASCII.GetBytes(ascii.ToString()), encoding.GetBytes(ascii.ToString()));
            Assert.Equal(Convert.FromHexString("93FAFA5CF040"), encoding.GetBytes("日纊\ue000"));
            foreach (var text in new[] { "〜", "‖", "−", "¢", "£", "¬", "¥", "😀", "\ud800" })
                Assert.Throws<EncoderFallbackException>(() => encoding.GetBytes(text));
            Assert.Equal("cp932", new OceanBaseConnectionStringBuilder("Character Set=cp932").CharacterSet);
        }

        [Fact]
        public void PendingLeadSurvivesCountFlushErrorAndOutputOverflow()
        {
            var decoder = ResultCharacterSet.Resolve(95, out _).GetDecoder(); var output = new char[2];
            Assert.Equal(0, decoder.GetChars(new byte[] { 0x81 }, 0, 1, output, 0, false));
            Assert.Throws<DecoderFallbackException>(() => decoder.GetCharCount(Array.Empty<byte>(), 0, 0, true));
            Assert.Throws<ArgumentException>(() => decoder.GetChars(new byte[] { 0x5f, 65 }, 0, 2, new char[1], 0, true));
            Assert.Equal(2, decoder.GetChars(new byte[] { 0x5f, 65 }, 0, 2, output, 0, true));
            Assert.Equal("＼A", new string(output));
            Assert.Equal("\\", ResultCharacterSet.Resolve(95, out _).GetString(new byte[] { 0x5c }));
        }

        [Fact]
        public void DuplicateLeadSurvivesInvalidTrailFlushAndDestinationErrors()
        {
            var encoding = ResultCharacterSet.Resolve(96, out _); var decoder = encoding.GetDecoder(); var output = new[] { '!' };
            Assert.Equal(0, decoder.GetChars(new byte[] { 0xed }, 0, 1, output, 0, false));
            Assert.Throws<DecoderFallbackException>(() => decoder.GetChars(new byte[] { 0x7f }, 0, 1, output, 0, true));
            Assert.Throws<DecoderFallbackException>(() => decoder.GetChars(Array.Empty<byte>(), 0, 0, output, 0, true));
            Assert.Throws<ArgumentException>(() => decoder.GetChars(new byte[] { 0x40 }, 0, 1, Array.Empty<char>(), 0, true));
            Assert.Equal('!', output[0]);
            Assert.Equal(1, decoder.GetChars(new byte[] { 0x40 }, 0, 1, output, 0, true)); Assert.Equal('纊', output[0]);
            var destination = new byte[] { 42 };
            Assert.Throws<ArgumentException>(() => encoding.GetBytes(new[] { '纊' }, 0, 1, destination, 0));
            Assert.Equal((byte)42, destination[0]);
        }
    }
}
