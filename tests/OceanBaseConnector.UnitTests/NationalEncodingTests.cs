using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> NationalEncodingSamples()
            => ClientCharacterSetFixture.Samples.Select(s => new object[] { s.Name, s.Text, s.Hex });

        [Theory]
        [MemberData(nameof(NationalEncodingSamples))]
        public void ExplicitNationalEncodingsUsePinnedBytesAndDirectionalPrecedence(string name, string text, string hex)
        {
            var bytes = Convert.FromHexString(hex);
            foreach (var settings in new[] { new NationalCharacterSet(name, "utf16", "ascii"), new NationalCharacterSet("binary", name, name) })
            {
                foreach (var code in new byte[] { 207, 208 })
                {
                    var column = ResultColumn.Parse(SchemaColumn("n", code, 32, charset: 54), OceanBaseCompatibilityMode.Oracle, nationalCharacterSet: settings);
                    Assert.Equal(text, column.Decode(bytes));
                    Assert.Equal(text, column.DecodeBinary(bytes));
                    var parameter = new OceanBaseParameter { OceanBaseDbType = code == 207 ? OceanBaseDbType.NVarchar2 : OceanBaseDbType.NChar, Value = text };
                    Assert.Equal(new byte[] { 0, 1, code, 0 }.Concat(WithLength(bytes)), PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle, settings));
                }
            }
            Assert.Throws<NotSupportedException>(() => new NationalCharacterSet("unknown", name, name).ResultEncoding(45));
            Assert.Throws<NotSupportedException>(() => new NationalCharacterSet(null, null, name).ResultEncoding(63));
        }

        [Theory]
        [MemberData(nameof(NationalEncodingSamples))]
        public void MappedNationalDecodersPreserveEveryByteSplitAndReset(string name, string text, string hex)
        {
            var bytes = Convert.FromHexString(hex);
            // Exercise the wrapper independently so resolver rejection cannot hide a streaming defect.
            var decoder = ClientCharacterSet.Parse(name).GetDecoder();
            var actual = new StringBuilder();
            var chars = new char[4];
            for (var i = 0; i < bytes.Length; i++)
            {
                actual.Append(chars, 0, decoder.GetChars(bytes, i, 1, chars, 0, false));
            }
            actual.Append(chars, 0, decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, true));
            Assert.Equal(text, actual.ToString());
            if (bytes[0] >= 128 && name != "latin1" && name != "dec8" && name != "tis620")
            {
                decoder.Reset();
                Assert.Equal(0, decoder.GetChars(bytes, 0, 1, chars, 0, false));
                Assert.Throws<DecoderFallbackException>(() => decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, true));
                decoder.Reset(); decoder.GetChars(bytes, 0, 1, chars, 0, false); decoder.Reset();
                Assert.Equal(1, decoder.GetChars(new byte[] { 65 }, 0, 1, chars, 0, true)); Assert.Equal('A', chars[0]);
            }
        }
    }
}
