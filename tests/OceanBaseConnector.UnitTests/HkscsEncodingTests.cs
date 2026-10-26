using System;
using System.Security.Cryptography;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class HkscsEncodingTests
    {
        private static Encoding EncodingFor(bool revised) => ResultCharacterSet.Resolve(revised ? (ushort)153 : (ushort)152, out _);

        [Theory]
        [InlineData(false, "D1DDF438F5FA1E054410BC4498B7EE54AD7B60269A2AF2ECF3D118E0E6EB6166")]
        [InlineData(true, "56451B72F5B1AABF44594A10E1AEB515458D5186344A128DBE92DD6DBBA98BFC")]
        public void AllSinglesAndHighBytePairsMatchPinnedSourceDigest(bool revised, string expected)
        {
            var encoding = EncodingFor(revised); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var single = 0; single < 256; single++) Append(new[] { (byte)single });
            for (var lead = 128; lead < 256; lead++)
                for (var trail = 0; trail < 256; trail++) Append(new[] { (byte)lead, (byte)trail });
            Assert.Equal(expected, Convert.ToHexString(hash.GetHashAndReset()));

            void Append(byte[] bytes)
            {
                var scalar = uint.MaxValue;
                try
                {
                    var text = encoding.GetString(bytes); scalar = (uint)char.ConvertToUtf32(text, 0);
                    Assert.Equal(scalar > 65535 ? 2 : 1, text.Length);
                    Assert.Equal(text.Length, encoding.GetCharCount(bytes));
                    Assert.Equal(text, encoding.GetString(encoding.GetBytes(text)));
                    var decoder = encoding.GetDecoder(); var chars = new char[2];
                    for (var i = 0; i < bytes.Length; i++)
                    {
                        var count = decoder.GetCharCount(bytes, i, 1, i == bytes.Length - 1);
                        Assert.Equal(count, decoder.GetCharCount(bytes, i, 1, i == bytes.Length - 1));
                        Assert.Equal(count, decoder.GetChars(bytes, i, 1, chars, 0, i == bytes.Length - 1));
                    }
                    Assert.Equal(text, new string(chars, 0, text.Length));
                }
                catch (DecoderFallbackException)
                {
                    Assert.Throws<DecoderFallbackException>(() => encoding.GetCharCount(bytes));
                    Assert.Throws<DecoderFallbackException>(() => encoding.GetDecoder().GetChars(bytes, 0, bytes.Length, new char[2], 0, true));
                }
                hash.AppendData(bytes); hash.AppendData(Word(scalar));
            }
        }

        [Theory]
        [InlineData(false, "A84DDC75FB5EC77045A3788B63F585E587DAB73EBD811330AAB680C08F7E60E7")]
        [InlineData(true, "F38A467F1FEEB04472F6E75B981968EC2C6D081B176C7D63B6EEED421F5AE0E4")]
        public void AllBmpEncodingsAndRefusalsMatchPinnedSource(bool revised, string expected)
            => AssertEncodingDigest(EncodingFor(revised), 0, 65536, false, expected);

        [Theory]
        [InlineData(false, "6EB4F3773B53CA695D3AF6702DBA282694612F1F918AC37EFF1078D6A300C798")]
        [InlineData(true, "64438CD71CBB57D68CFB3607E24435FA21CEAF20A6B44C024E697B63EB50F693")]
        public void AllPlane2EncodingsAndRefusalsMatchPinnedSource(bool revised, string expected)
            => AssertEncodingDigest(EncodingFor(revised), 0x20000, 0x30000, true, expected);

        private static void AssertEncodingDigest(Encoding encoding, int start, int end, bool supplementary, string expected)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var scalar = start; scalar < end; scalar++)
            {
                var code = uint.MaxValue; var text = supplementary ? char.ConvertFromUtf32(scalar) : ((char)scalar).ToString();
                try
                {
                    var bytes = encoding.GetBytes(text); Assert.InRange(bytes.Length, 1, 2);
                    Assert.Equal(bytes.Length, encoding.GetByteCount(text));
                    Assert.Equal(scalar == 0x2593 ? "\uffed" : text, encoding.GetString(bytes));
                    code = bytes.Length == 1 ? bytes[0] : (uint)(bytes[0] << 8 | bytes[1]);
                }
                catch (EncoderFallbackException) { Assert.Throws<EncoderFallbackException>(() => encoding.GetByteCount(text)); }
                hash.AppendData(supplementary ? Word((uint)scalar) : new[] { (byte)(scalar >> 8), (byte)scalar });
                hash.AppendData(Word(code));
            }
            Assert.Equal(expected, Convert.ToHexString(hash.GetHashAndReset()));
        }

        private static byte[] Word(uint value) => new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };

        [Theory]
        [InlineData(false, "\uf308\uf30aĀ\uffed中\0  ")]
        [InlineData(true, "\U0002010c\U000200d1Ā\uffed中\0  ")]
        public void PuaAndSupplementarySamplesPreserveEverySplitResetAndTinyOutput(bool revised, string expected)
        {
            var encoding = EncodingFor(revised); var bytes = Convert.FromHexString("884588478856F9FEA4A4002020");
            Assert.Equal(expected, encoding.GetString(bytes)); Assert.Equal(bytes, encoding.GetBytes(expected));
            for (var split = 0; split <= bytes.Length; split++)
            {
                var decoder = encoding.GetDecoder(); var chars = new char[expected.Length];
                var first = decoder.GetCharCount(bytes, 0, split, false);
                Assert.Equal(first, decoder.GetChars(bytes, 0, split, chars, 0, false));
                var second = decoder.GetCharCount(bytes, split, bytes.Length - split, true);
                Assert.Equal(second, decoder.GetChars(bytes, split, bytes.Length - split, chars, first, true));
                Assert.Equal(expected, new string(chars)); decoder.Reset();
                Assert.Equal(0, decoder.GetChars(new byte[] { 0x88 }, 0, 1, chars, 0, false)); decoder.Reset();
                Assert.Equal(expected.Length, decoder.GetChars(bytes, 0, bytes.Length, chars, 0, true));
                Assert.Equal(expected, new string(chars));
            }
            var tiny = encoding.GetDecoder(); var output = new char[4]; var actual = new StringBuilder(); var offset = 0;
            for (var attempt = 0; offset < bytes.Length && attempt < bytes.Length * 2; attempt++)
            {
                tiny.Convert(bytes, offset, bytes.Length - offset, output, 1, 2, true, out var used, out var written, out var complete);
                Assert.True(used > 0 || written > 0); offset += used; actual.Append(output, 1, written);
                Assert.Equal(offset == bytes.Length, complete);
            }
            Assert.Equal(bytes.Length, offset); Assert.Equal(expected, actual.ToString());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SourceCanonicalBytesAndAsymmetricAliasArePreserved(bool revised)
        {
            var encoding = EncodingFor(revised);
            Assert.Equal("F9F9", Convert.ToHexString(encoding.GetBytes("═")));
            Assert.Equal("CDE7", Convert.ToHexString(encoding.GetBytes("侻")));
            Assert.Equal("═侻", encoding.GetString(Convert.FromHexString("A2A49C53")));
            Assert.Equal("F9FE", Convert.ToHexString(encoding.GetBytes("▓")));
            Assert.Equal("\uffed", encoding.GetString(Convert.FromHexString("F9FE")));
        }

        [Theory]
        [InlineData(false, "\uf308")]
        [InlineData(true, "\U0002010c")]
        public void PendingLeadSurvivesInvalidFlushOverflowAndSupplementaryRetry(bool revised, string expected)
        {
            var encoding = EncodingFor(revised); var decoder = encoding.GetDecoder(); var output = new char[3];
            Assert.Equal(0, decoder.GetChars(new byte[] { 0x88 }, 0, 1, Array.Empty<char>(), 0, false));
            Assert.Throws<DecoderFallbackException>(() => decoder.GetCharCount(Array.Empty<byte>(), 0, 0, true));
            Assert.Throws<DecoderFallbackException>(() => decoder.GetChars(Array.Empty<byte>(), 0, 0, output, 0, true));
            Assert.Throws<ArgumentException>(() => decoder.GetChars(new byte[] { 0x45, 65 }, 0, 2, new char[1], 0, true));
            Assert.Equal(expected.Length + 1, decoder.GetChars(new byte[] { 0x45, 65 }, 0, 2, output, 0, true));
            Assert.Equal(expected + "A", new string(output, 0, expected.Length + 1));
            if (revised)
            {
                decoder.Reset(); decoder.GetChars(new byte[] { 0x88 }, 0, 1, output, 0, false);
                Assert.Throws<ArgumentException>(() => decoder.Convert(new byte[] { 0x45 }, 0, 1, new char[1], 0, 1, true, out _, out _, out _));
                Assert.Equal(2, decoder.GetChars(new byte[] { 0x45 }, 0, 1, output, 0, true));
                Assert.Equal(expected, new string(output, 0, 2));
            }
            Assert.Equal(revised ? 2 : 1, encoding.GetMaxCharCount(1));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void StrictSurrogatesBoundsEmptyAndOffsetContractsHold(bool revised)
        {
            var encoding = EncodingFor(revised); var bytes = new byte[4]; var chars = new char[4];
            Assert.Equal(2, encoding.GetBytes(new[] { 'x', '中', 'y' }, 1, 1, bytes, 1));
            Assert.Equal(new byte[] { 0, 0xa4, 0xa4, 0 }, bytes);
            Assert.Equal(1, encoding.GetChars(bytes, 1, 2, chars, 1)); Assert.Equal('中', chars[1]);
            Assert.Equal(0, encoding.GetChars(bytes, bytes.Length, 0, chars, chars.Length));
            Assert.Equal(0, encoding.GetBytes(chars, chars.Length, 0, bytes, bytes.Length));
            foreach (var text in new[] { "\ud800", "\udc00", "\ud800A", "A\udc00", "😀", "\U00010000", "\U00030000", "\U0010ffff" })
            {
                Assert.Throws<EncoderFallbackException>(() => encoding.GetBytes(text));
                Assert.Throws<EncoderFallbackException>(() => encoding.GetByteCount(text));
            }
            Assert.Throws<ArgumentNullException>(() => encoding.GetCharCount(null!, 0, 0));
            Assert.Throws<ArgumentNullException>(() => encoding.GetByteCount((char[])null!, 0, 0));
            Assert.Throws<ArgumentNullException>(() => encoding.GetChars(bytes, 0, 0, null!, 0));
            Assert.Throws<ArgumentNullException>(() => encoding.GetBytes(chars, 0, 0, null!, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetCharCount(bytes, -1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetByteCount(chars, 0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetCharCount(bytes, 1, int.MaxValue));
            Assert.Throws<ArgumentException>(() => encoding.GetChars(bytes, 1, 2, chars, chars.Length));
            Assert.Throws<ArgumentException>(() => encoding.GetBytes(new[] { '中' }, 0, 1, bytes, bytes.Length - 1));
            Assert.Equal(2, encoding.GetMaxByteCount(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetMaxByteCount(int.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetMaxCharCount(-1));
            if (revised) Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetMaxCharCount(int.MaxValue));
        }
    }
}
