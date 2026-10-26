using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class SingleByteResultEncodingTests
    {
        public static IEnumerable<object[]> Collations()
        {
            foreach (var id in new ushort[] { 3, 69, 338, 353, 368 })
                yield return new object[] { id, "C5C5881E89F87F0ED6FAD430189D26419049BE36ADC2641B05D00D4AEF02051E", "A4A6ACADAEAFB4B8BED0DEF0FEFF" };
            foreach (var id in new ushort[] { 18, 89, 332, 347, 362 })
                yield return new object[] { id, "E0B0693061A62AAC427E4394703FC880842510DE0C4BA8937038547B2D0F6A10", "A0DBDCDDDEFCFDFEFF" };
        }

        [Theory]
        [MemberData(nameof(Collations))]
        public void EveryByteMatchesPinnedServerDigestAndStrictReverseMapping(ushort id, string digest, string invalidHex)
        {
            // Regenerate from pinned server tables with fixtures/character-sets/SingleByteReference.py.
            var encoding = ResultCharacterSet.Resolve(id, out var width);
            Assert.Equal(1u, width);
            var invalid = Convert.FromHexString(invalidHex);
            var scalars = new byte[512]; var defined = new HashSet<char>();
            for (var value = 0; value < 256; value++)
            {
                var bytes = new[] { (byte)value }; var decoder = encoding.GetDecoder();
                if (Array.IndexOf(invalid, (byte)value) >= 0)
                {
                    scalars[value * 2] = scalars[value * 2 + 1] = 255;
                    Assert.Throws<DecoderFallbackException>(() => encoding.GetString(bytes));
                    Assert.Throws<DecoderFallbackException>(() => encoding.GetCharCount(bytes));
                    Assert.Throws<DecoderFallbackException>(() => decoder.GetCharCount(bytes, 0, 1, true));
                    Assert.Throws<DecoderFallbackException>(() => decoder.GetChars(bytes, 0, 1, new char[1], 0, true));
                    continue;
                }
                var text = encoding.GetString(bytes); Assert.Single(text);
                scalars[value * 2] = (byte)(text[0] >> 8); scalars[value * 2 + 1] = (byte)text[0];
                Assert.True(defined.Add(text[0]));
                Assert.Equal(bytes, encoding.GetBytes(text)); Assert.Equal(1, encoding.GetByteCount(text));
                Assert.Equal(1, encoding.GetCharCount(bytes));
                if (value < 160) Assert.Equal(((char)value).ToString(), text);
            }
            Assert.Equal(digest, Convert.ToHexString(SHA256.HashData(scalars)));
            for (var scalar = 0; scalar <= 0xffff; scalar++)
                if (!defined.Contains((char)scalar))
                {
                    var chars = new[] { (char)scalar };
                    Assert.Throws<EncoderFallbackException>(() => encoding.GetByteCount(chars));
                    Assert.Throws<EncoderFallbackException>(() => encoding.GetBytes(chars));
                }
            Assert.Throws<EncoderFallbackException>(() => encoding.GetBytes("😀"));
        }

        [Theory]
        [InlineData(3, "\u0080\u009f¤ŒŸœÿ\0  ", "809FA8D7DDF7FD002020")]
        [InlineData(18, "\u0080\u009fกฺ฿๛\0  ", "809FA1DADFFB002020")]
        public void DecoderCountResetAndTinyOutputPreserveEachByte(int id, string expected, string hex)
        {
            var encoding = ResultCharacterSet.Resolve((ushort)id, out _); var bytes = Convert.FromHexString(hex);
            var decoder = encoding.GetDecoder(); var output = new StringBuilder(); var chars = new char[3];
            for (var i = 0; i < bytes.Length; i++)
            {
                Assert.Equal(1, decoder.GetCharCount(bytes, i, 1, i == bytes.Length - 1));
                Assert.Equal(1, decoder.GetCharCount(bytes, i, 1, i == bytes.Length - 1));
                decoder.Convert(bytes, i, bytes.Length - i, chars, 1, 1, true, out var used, out var written, out var complete);
                Assert.Equal(1, used); Assert.Equal(1, written); Assert.Equal(i == bytes.Length - 1, complete);
                output.Append(chars[1]);
            }
            Assert.Equal(expected, output.ToString()); decoder.Reset();
            Assert.Equal(expected, encoding.GetString(bytes));
            Assert.Equal(0, decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, true));
            Assert.Equal(bytes, encoding.GetBytes(expected));
            Assert.Equal(bytes.Length, encoding.GetMaxCharCount(bytes.Length));
            Assert.Equal(expected.Length, encoding.GetMaxByteCount(expected.Length));
        }

        [Theory]
        [InlineData(3)]
        [InlineData(18)]
        public void ArrayBoundariesAndEmptySegmentsFollowEncodingContract(int id)
        {
            var encoding = ResultCharacterSet.Resolve((ushort)id, out _);
            var bytes = new byte[] { 65 }; var chars = new char[] { 'A' };
            Assert.Equal(0, encoding.GetChars(bytes, 1, 0, chars, 1));
            Assert.Equal(0, encoding.GetBytes(chars, 1, 0, bytes, 1));
            Assert.Equal(0, encoding.GetCharCount(bytes, 1, 0)); Assert.Equal(0, encoding.GetByteCount(chars, 1, 0));
            Assert.Throws<ArgumentNullException>(() => encoding.GetCharCount(null!, 0, 0));
            Assert.Throws<ArgumentNullException>(() => encoding.GetByteCount((char[])null!, 0, 0));
            Assert.Throws<ArgumentNullException>(() => encoding.GetChars(bytes, 0, 0, null!, 0));
            Assert.Throws<ArgumentNullException>(() => encoding.GetBytes(chars, 0, 0, null!, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetCharCount(bytes, -1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetByteCount(chars, 0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetCharCount(bytes, 1, int.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetByteCount(chars, int.MaxValue, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetChars(bytes, 0, 1, chars, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetBytes(chars, 0, 1, bytes, 2));
            Assert.Throws<ArgumentException>(() => encoding.GetChars(bytes, 0, 1, chars, 1));
            Assert.Throws<ArgumentException>(() => encoding.GetBytes(chars, 0, 1, bytes, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetMaxCharCount(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => encoding.GetMaxByteCount(-1));
        }
    }
}
