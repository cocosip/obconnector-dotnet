using System;
using System.Security.Cryptography;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class Big5EncodingTests
    {
        [Fact]
        public void AllSinglesAndHighBytePairsMatchPinnedStrictSourceDigest()
        {
            var encoding = ResultCharacterSet.Resolve(1, out var width); Assert.Equal(2u, width);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var single = 0; single < 256; single++) Append(new[] { (byte)single });
            for (var lead = 128; lead < 256; lead++)
                for (var trail = 0; trail < 256; trail++) Append(new[] { (byte)lead, (byte)trail });
            Assert.Equal("2496C8718A01265FA9959DCC64DA71D56706D5C92E9ABA3BC65DDAA434226C64", Convert.ToHexString(hash.GetHashAndReset()));

            void Append(byte[] bytes)
            {
                var scalar = uint.MaxValue;
                try
                {
                    var text = encoding.GetString(bytes); Assert.Single(text); scalar = text[0];
                    Assert.Equal(bytes, encoding.GetBytes(text)); Assert.Equal(1, encoding.GetCharCount(bytes));
                    var decoder = encoding.GetDecoder(); var chars = new char[1];
                    for (var i = 0; i < bytes.Length; i++)
                    {
                        var count = decoder.GetCharCount(bytes, i, 1, i == bytes.Length - 1);
                        Assert.Equal(count, decoder.GetCharCount(bytes, i, 1, i == bytes.Length - 1));
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
        public void AllBmpEncodingsAndRefusalsMatchPinnedSourceDigest()
        {
            var encoding = ResultCharacterSet.Resolve(84, out _);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var scalar = 0; scalar <= 65535; scalar++)
            {
                var code = uint.MaxValue; var text = ((char)scalar).ToString();
                try
                {
                    var bytes = encoding.GetBytes(text); Assert.InRange(bytes.Length, 1, 2);
                    Assert.Equal(bytes.Length, encoding.GetByteCount(text)); Assert.Equal(text, encoding.GetString(bytes));
                    code = bytes.Length == 1 ? bytes[0] : (uint)(bytes[0] << 8 | bytes[1]);
                }
                catch (EncoderFallbackException) { Assert.Throws<EncoderFallbackException>(() => encoding.GetByteCount(text)); }
                hash.AppendData(new[] { (byte)(scalar >> 8), (byte)scalar, (byte)(code >> 24), (byte)(code >> 16), (byte)(code >> 8), (byte)code });
            }
            Assert.Equal("E4D43BD65D036EAF5B1F96517B51ECEC474DDDD6D9331D0AD297006A1144F6D6", Convert.ToHexString(hash.GetHashAndReset()));
            Assert.Throws<EncoderFallbackException>(() => encoding.GetBytes("😀"));
        }

        [Theory]
        [InlineData("A2A4A2A5A2A6A2A7", "═╞╪╡")]
        [InlineData("A4A4A145A1E3A244C6A1C6A5C7B3C7E9002020", "中•∼¥ヾぁЁ①\0  ")]
        [InlineData("A14EA1C2A1F2A1F3A241A242A246A247C7FC", "､‾♁☉／＼¢£⑽")]
        public void CorrectionsPreserveAllSplitsResetCountsAndTinyOutput(string hex, string expected)
        {
            var encoding = ResultCharacterSet.Resolve(1, out _); var bytes = Convert.FromHexString(hex);
            Assert.Equal(expected, encoding.GetString(bytes)); Assert.Equal(bytes, encoding.GetBytes(expected));
            for (var split = 0; split <= bytes.Length; split++)
            {
                var decoder = encoding.GetDecoder(); var chars = new char[expected.Length];
                var first = decoder.GetCharCount(bytes, 0, split, false);
                Assert.Equal(first, decoder.GetChars(bytes, 0, split, chars, 0, false));
                var second = decoder.GetCharCount(bytes, split, bytes.Length - split, true);
                Assert.Equal(second, decoder.GetChars(bytes, split, bytes.Length - split, chars, first, true));
                Assert.Equal(expected, new string(chars)); decoder.Reset();
                Assert.Equal(0, decoder.GetChars(new byte[] { 0xa4 }, 0, 1, chars, 0, false)); decoder.Reset();
                Assert.Equal(expected.Length, decoder.GetChars(bytes, 0, bytes.Length, chars, 0, true));
                Assert.Equal(expected, new string(chars));
            }
            var tiny = encoding.GetDecoder(); var output = new char[3]; var actual = new StringBuilder(); var offset = 0;
            for (var attempt = 0; offset < bytes.Length && attempt < bytes.Length * 2; attempt++)
            {
                tiny.Convert(bytes, offset, bytes.Length - offset, output, 1, 1, true, out var used, out var written, out var complete);
                Assert.True(used > 0 || written > 0); offset += used; actual.Append(output[1], written);
                Assert.Equal(offset == bytes.Length, complete);
            }
            Assert.Equal(bytes.Length, offset); Assert.Equal(expected, actual.ToString());
        }

        [Fact]
        public void PendingLeadSurvivesCountFlushFailureAndOutputOverflow()
        {
            var decoder = ResultCharacterSet.Resolve(1, out _).GetDecoder(); var chars = new char[2];
            Assert.Equal(0, decoder.GetChars(new byte[] { 0xa4 }, 0, 1, chars, 0, false));
            Assert.Throws<DecoderFallbackException>(() => decoder.GetCharCount(Array.Empty<byte>(), 0, 0, true));
            Assert.Throws<DecoderFallbackException>(() => decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, true));
            Assert.Throws<ArgumentException>(() => decoder.GetChars(new byte[] { 0xa4, 65 }, 0, 2, new char[1], 0, true));
            Assert.Equal(2, decoder.GetChars(new byte[] { 0xa4, 65 }, 0, 2, chars, 0, true));
            Assert.Equal("中A", new string(chars));
        }

        [Fact]
        public void EmptySegmentsOffsetsAndInvalidArgumentsFollowEncodingContract()
        {
            var encoding = ResultCharacterSet.Resolve(1, out _); var bytes = new byte[5]; var chars = new char[3];
            Assert.Equal(2, encoding.GetBytes(new[] { 'x', '中', 'y' }, 1, 1, bytes, 2));
            Assert.Equal(new byte[] { 0, 0, 0xa4, 0xa4, 0 }, bytes);
            Assert.Equal(1, encoding.GetChars(bytes, 2, 2, chars, 1)); Assert.Equal(new[] { '\0', '中', '\0' }, chars);
            Assert.Equal(0, encoding.GetBytes(chars, chars.Length, 0, bytes, bytes.Length));
            Assert.Equal(0, encoding.GetChars(bytes, bytes.Length, 0, chars, chars.Length));
            Assert.Throws<ArgumentNullException>(() => encoding.GetCharCount(null!, 0, 0));
            Assert.Throws<ArgumentNullException>(() => encoding.GetByteCount((char[])null!, 0, 0));
            Assert.Throws<ArgumentNullException>(() => encoding.GetChars(bytes, 0, 0, null!, 0));
            Assert.Throws<ArgumentNullException>(() => encoding.GetBytes(chars, 0, 0, null!, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetCharCount(bytes, -1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetByteCount(chars, 0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetCharCount(bytes, 1, int.MaxValue));
            Assert.Throws<ArgumentException>(() => encoding.GetChars(bytes, 2, 2, chars, chars.Length));
            Assert.Throws<ArgumentException>(() => encoding.GetBytes(new[] { '中' }, 0, 1, bytes, bytes.Length - 1));
            Assert.Equal(2, encoding.GetMaxByteCount(1)); Assert.Equal(2, encoding.GetMaxCharCount(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetMaxByteCount(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetMaxCharCount(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetMaxByteCount(int.MaxValue));
        }
    }
}
