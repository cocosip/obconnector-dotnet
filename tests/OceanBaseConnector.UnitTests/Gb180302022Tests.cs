using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class Gb180302022Tests
    {
        // OceanBase 5d6cb5c ob_ctype_gb18030.cc:init_gb18030_2022, both sides of each swap.
        [Theory]
        [InlineData(0xe81e, 0x9fb4, "FE59", "82359037")]
        [InlineData(0xe826, 0x9fb5, "FE61", "82359038")]
        [InlineData(0xe82b, 0x9fb6, "FE66", "82359039")]
        [InlineData(0xe82c, 0x9fb7, "FE67", "82359130")]
        [InlineData(0xe832, 0x9fb8, "FE6D", "82359131")]
        [InlineData(0xe843, 0x9fb9, "FE7E", "82359132")]
        [InlineData(0xe854, 0x9fba, "FE90", "82359133")]
        [InlineData(0xe864, 0x9fbb, "FEA0", "82359134")]
        [InlineData(0xe78d, 0xfe10, "A6D9", "84318236")]
        [InlineData(0xe78e, 0xfe12, "A6DA", "84318238")]
        [InlineData(0xe78f, 0xfe11, "A6DB", "84318237")]
        [InlineData(0xe790, 0xfe13, "A6DC", "84318239")]
        [InlineData(0xe791, 0xfe14, "A6DD", "84318330")]
        [InlineData(0xe792, 0xfe15, "A6DE", "84318331")]
        [InlineData(0xe793, 0xfe16, "A6DF", "84318332")]
        [InlineData(0xe794, 0xfe17, "A6EC", "84318333")]
        [InlineData(0xe795, 0xfe18, "A6ED", "84318334")]
        [InlineData(0xe796, 0xfe19, "A6F3", "84318335")]
        public void RevisedMappingsPreserveBothSidesAndLegacyEncoding(int pua, int assigned, string twoHex, string fourHex)
        {
            var revised = ResultCharacterSet.Resolve(216, out var width);
            var legacy = ResultCharacterSet.Resolve(248, out _);
            Assert.Equal(4u, width);
            var two = Convert.FromHexString(twoHex); var four = Convert.FromHexString(fourHex);
            var puaText = ((char)pua).ToString(); var assignedText = ((char)assigned).ToString();
            Assert.Equal(assignedText, revised.GetString(two)); Assert.Equal(puaText, revised.GetString(four));
            Assert.Equal(two, revised.GetBytes(assignedText)); Assert.Equal(four, revised.GetBytes(puaText));
            Assert.Equal(puaText, legacy.GetString(two)); Assert.Equal(assignedText, legacy.GetString(four));
            Assert.Equal(two, legacy.GetBytes(puaText)); Assert.Equal(four, legacy.GetBytes(assignedText));
            using var payload = new MemoryStream(); payload.Write(two); payload.Write(four); payload.WriteByte(65);
            var bytes = payload.ToArray(); var expected = assignedText + puaText + "A";
            for (var split = 0; split <= bytes.Length; split++)
            {
                var decoder = revised.GetDecoder(); var chars = new char[expected.Length];
                var count = decoder.GetCharCount(bytes, 0, split, false);
                Assert.Equal(count, decoder.GetChars(bytes, 0, split, chars, 0, false));
                var remainder = decoder.GetCharCount(bytes, split, bytes.Length - split, true);
                Assert.Equal(remainder, decoder.GetChars(bytes, split, bytes.Length - split, chars, count, true));
                Assert.Equal(expected, new string(chars));
                decoder.Reset();
                decoder.GetChars(two, 0, 1, chars, 0, false); decoder.Reset();
                Assert.Equal(expected.Length, decoder.GetChars(bytes, 0, bytes.Length, chars, 0, true));
                Assert.Equal(expected, new string(chars));
            }
        }

        [Fact]
        public void AllBmpScalarEncodingsMatchIndependentOpenJdk2022Digest()
        {
            // Regenerate with tests/fixtures/character-sets/Gb180302022Reference.java on OpenJDK21.
            var encoding = ResultCharacterSet.Resolve(217, out _);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var scalar = 0; scalar <= 0xffff; scalar++)
            {
                if (scalar >= 0xd800 && scalar <= 0xdfff) continue;
                var text = ((char)scalar).ToString(); var bytes = encoding.GetBytes(text);
                hash.AppendData(new[] { (byte)(scalar >> 8), (byte)scalar, (byte)bytes.Length }); hash.AppendData(bytes);
                Assert.Equal(text, encoding.GetString(bytes));
            }
            Assert.Equal("BCE50FC198C82048FEB3391C9741666A12AEF1FFFAEAFDF6CED6B26DA090E2D7", Convert.ToHexString(hash.GetHashAndReset()));
        }

        [Fact]
        public void AllTwoByteAndBmpFourBytePositionsMatchIndependentOpenJdk2022Digest()
        {
            var encoding = ResultCharacterSet.Resolve(222, out _);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var n = 0; n < 23940 + 39420; n++)
            {
                byte[] bytes;
                if (n < 23940)
                {
                    var trail = n % 190 + 0x40; if (trail >= 0x7f) trail++;
                    bytes = new[] { (byte)(n / 190 + 0x81), (byte)trail };
                }
                else
                {
                    var x = n - 23940; bytes = new byte[4];
                    bytes[3] = (byte)(x % 10 + 0x30); x /= 10;
                    bytes[2] = (byte)(x % 126 + 0x81); x /= 126;
                    bytes[1] = (byte)(x % 10 + 0x30); bytes[0] = (byte)(x / 10 + 0x81);
                }
                var scalar = uint.MaxValue;
                try { scalar = (uint)char.ConvertToUtf32(encoding.GetString(bytes), 0); }
                catch (DecoderFallbackException) { }
                hash.AppendData(bytes);
                hash.AppendData(new[] { (byte)(scalar >> 24), (byte)(scalar >> 16), (byte)(scalar >> 8), (byte)scalar });
            }
            Assert.Equal("2059E447A987AF0772A082F80D0E8FB1A3C046858A42E906E4AA951D4ACC3E7D", Convert.ToHexString(hash.GetHashAndReset()));
        }
    }
}
