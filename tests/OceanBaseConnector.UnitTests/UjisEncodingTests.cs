using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class UjisEncodingTests
    {
        [Fact]
        public void EverySingleByteLeadPairAndTripleMatchesPinnedSourceDigest()
        {
            var encoding = ResultCharacterSet.Resolve(12, out var width); Assert.Equal(3u, width);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var single = 0; single < 256; single++) Append(new[] { (byte)single });
            for (var lead = 0x80; lead <= 0xff; lead++)
                for (var trail = 0; trail < 256; trail++) Append(new[] { (byte)lead, (byte)trail });
            for (var second = 0; second < 256; second++)
                for (var third = 0; third < 256; third++) Append(new[] { (byte)0x8f, (byte)second, (byte)third });
            Assert.Equal("3CE5635B85F8BF578EDEC766CC70AD0B996DEDDEE1D7737B974A4932D28A3ACA", Convert.ToHexString(hash.GetHashAndReset()));

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
            var encoding = ResultCharacterSet.Resolve(91, out _);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var scalar = 0; scalar <= 0xffff; scalar++)
            {
                var code = uint.MaxValue; var text = ((char)scalar).ToString();
                try
                {
                    var bytes = encoding.GetBytes(text);
                    Assert.InRange(bytes.Length, 1, 3); Assert.Equal(bytes.Length, encoding.GetByteCount(text));
                    code = 0; foreach (var value in bytes) code = code << 8 | value;
                    Assert.Equal(text, encoding.GetString(bytes));
                }
                catch (EncoderFallbackException) { Assert.Throws<EncoderFallbackException>(() => encoding.GetByteCount(text)); }
                hash.AppendData(new[] { (byte)(scalar >> 8), (byte)scalar, (byte)(code >> 24), (byte)(code >> 16), (byte)(code >> 8), (byte)code });
            }
            Assert.Equal("E5FAB9A2ED3DE238CC17AC9B64CA31A40517200E302A284F0606E9ECF0FC7046", Convert.ToHexString(hash.GetHashAndReset()));
            Assert.Throws<EncoderFallbackException>(() => encoding.GetBytes("😀"));
        }

        public static IEnumerable<object[]> ValidMappings()
        {
            yield return new object[] { "5C7E", "\\~" };
            yield return new object[] { "8EB68EC5", "ｶﾅ" };
            yield return new object[] { "8FB0A1", "丂" };
            yield return new object[] { "F5A18FF5A1", "\ue000\ue3ac" };
            yield return new object[] { "A1C1A1C2A1DDA1F1A1F2A2CC", "〜‖−¢£¬" };
            yield return new object[] { "C6FCCBDC8EB68EC58FB0A1F5A18FF5A1002020", "日本ｶﾅ丂\ue000\ue3ac\0  " };
        }

        [Theory]
        [MemberData(nameof(ValidMappings))]
        public void MappingSplitsCountResetAndTinyOutputPreserveCharacters(string hex, string expected)
        {
            var encoding = ResultCharacterSet.Resolve(12, out _); var bytes = Convert.FromHexString(hex);
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
                decoder.Reset(); decoder.GetChars(new byte[] { 0x8f }, 0, 1, chars, 0, false); decoder.Reset();
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
        [InlineData("A1C0", "5C", "\\")]
        [InlineData("8FA2B7", "7E", "~")]
        public void AsciiAliasesDecodeAndEncodeCanonically(string wire, string canonical, string text)
        {
            var encoding = ResultCharacterSet.Resolve(91, out _);
            Assert.Equal(text, encoding.GetString(Convert.FromHexString(wire)));
            Assert.Equal(Convert.FromHexString(canonical), encoding.GetBytes(text));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void TriplePrefixesSurviveCountInvalidFlushAndDestinationErrors(int prefix)
        {
            var encoding = ResultCharacterSet.Resolve(12, out _); var decoder = encoding.GetDecoder(); var output = new[] { '!' };
            var wire = Convert.FromHexString("8FB0A1");
            Assert.Equal(0, decoder.GetChars(wire, 0, prefix, output, 0, false));
            Assert.Equal(1, decoder.GetCharCount(wire, prefix, wire.Length - prefix, true));
            Assert.Equal(1, decoder.GetCharCount(wire, prefix, wire.Length - prefix, true));
            Assert.Throws<DecoderFallbackException>(() => decoder.GetChars(new byte[] { 0x20 }, 0, 1, output, 0, true));
            Assert.Throws<DecoderFallbackException>(() => decoder.GetChars(Array.Empty<byte>(), 0, 0, output, 0, true));
            Assert.Throws<ArgumentException>(() => decoder.GetChars(wire, prefix, wire.Length - prefix, Array.Empty<char>(), 0, true));
            Assert.Equal('!', output[0]);
            Assert.Equal(1, decoder.GetChars(wire, prefix, wire.Length - prefix, output, 0, true)); Assert.Equal('丂', output[0]);
            decoder.GetChars(wire, 0, prefix, output, 0, false); decoder.Reset();
            Assert.Equal(1, decoder.GetChars(new byte[] { 65 }, 0, 1, output, 0, true)); Assert.Equal('A', output[0]);
            var destination = new byte[] { 42, 42 };
            Assert.Throws<ArgumentException>(() => encoding.GetBytes(new[] { '丂' }, 0, 1, destination, 0));
            Assert.Equal(new byte[] { 42, 42 }, destination);
        }

        [Fact]
        public void ClientPreservesAsciiAndRejectsWindowsAliases()
        {
            var encoding = ClientCharacterSet.Parse("ujis");
            var ascii = new StringBuilder(); for (var i = 0; i < 128; i++) ascii.Append((char)i);
            Assert.Equal(Encoding.ASCII.GetBytes(ascii.ToString()), encoding.GetBytes(ascii.ToString()));
            Assert.Equal(Convert.FromHexString("C6FCCBDC8FB0A1F5A18FF5A1"), encoding.GetBytes("日本丂\ue000\ue3ac"));
            foreach (var text in new[] { "＼", "～", "∥", "－", "￠", "￡", "￢", "😀", "\ud800" })
                Assert.Throws<EncoderFallbackException>(() => encoding.GetBytes(text));
            Assert.Equal("ujis", new OceanBaseConnectionStringBuilder("Character Set=ujis").CharacterSet);
        }
    }
}
