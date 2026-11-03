using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> LegacySingleByteTables()
        {
            foreach (var (ids, digest, assigned) in new[]
            {
                (new ushort[] { 4, 80 }, "F48789B60431A991F763F31AC1170743734EDE825E3D301614CB602EABB22E8A", 256),
                (new ushort[] { 6, 72 }, "AB7672987DB7C4E8220F575053B7629D10608B1786E9C0187B230A6AC4FD0568", 255),
                (new ushort[] { 39, 53 }, "592E5C565694EDF0A55FFDC465FD153DB86987E7BB41EF363B7142B23CEB5F8A", 256),
                (new ushort[] { 10, 82 }, "EBE5CA0EC8F633654091E3757E7EE38C1E8678DDC5A08FD62544D7237D5E5017", 127),
            })
                foreach (var id in ids) yield return new object[] { id, digest, assigned };
        }

        [Theory]
        [MemberData(nameof(LegacySingleByteTables))]
        public void LegacySingleByteMappingsMatchPublishedTablesAndRejectHoles(ushort collation, string digest, int assigned)
        {
            var encoding = ResultCharacterSet.Resolve(collation, out var width); Assert.Equal(1u, width);
            var reference = new byte[512]; var valid = 0;
            for (var i = 0; i < 256; i++)
            {
                var bytes = new[] { (byte)i }; char value;
                try { value = Assert.Single(encoding.GetString(bytes)); valid++; }
                catch (DecoderFallbackException)
                {
                    value = '\uffff';
                    Assert.Throws<DecoderFallbackException>(() => encoding.GetCharCount(bytes, 0, 1));
                    Assert.Throws<DecoderFallbackException>(() => encoding.GetDecoder().GetChars(bytes, 0, 1, new char[1], 0, true));
                }
                if (value != '\uffff') Assert.Equal(bytes, encoding.GetBytes(value.ToString()));
                reference[i * 2] = (byte)(value >> 8); reference[i * 2 + 1] = (byte)value;
            }
            Assert.Equal(assigned, valid); Assert.Equal(digest, Convert.ToHexString(SHA256.HashData(reference)));
            Assert.Throws<EncoderFallbackException>(() => encoding.GetBytes("中"));
            Assert.Throws<EncoderFallbackException>(() => encoding.GetBytes("\uffff"));
        }

        [Theory]
        [InlineData("cp850", "Çøı", "809BD5")]
        [InlineData("hp8", "À₤Ÿ", "A1AFEE")]
        [InlineData("macroman", "Ä€ﬁ", "80DBDE")]
        public void LegacySingleByteClientsPreserveAsciiAndStrictRoundTrips(string name, string text, string hex)
        {
            var encoding = ClientCharacterSet.Parse(name);
            var ascii = new StringBuilder(); for (var i = 0; i < 128; i++) ascii.Append((char)i);
            Assert.Equal(Encoding.ASCII.GetBytes(ascii.ToString()), encoding.GetBytes(ascii.ToString()));
            Assert.Equal(Convert.FromHexString(hex), encoding.GetBytes(text));
            Assert.Equal(text, encoding.GetString(Convert.FromHexString(hex)));
            Assert.Throws<EncoderFallbackException>(() => encoding.GetBytes("中😀"));
            Assert.Throws<EncoderFallbackException>(() => encoding.GetBytes("\ud800"));
            Assert.Equal(name, new OceanBaseConnectionStringBuilder("Character Set=" + name).CharacterSet);
        }

        [Fact]
        public void Swe7RemainsResultOnlyBecauseItsAsciiSyntaxBytesDiffer()
        {
            Assert.Throws<ArgumentException>(() => ClientCharacterSet.Parse("swe7"));
            Assert.Throws<ArgumentException>(() => new OceanBaseConnectionStringBuilder("Character Set=swe7"));
        }
    }
}
