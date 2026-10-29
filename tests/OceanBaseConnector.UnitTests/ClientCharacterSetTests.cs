using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.Internal.Sql;
using OceanBaseConnector.Internal.Values;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class ClientCharacterSetTests
    {
        [Theory]
        [InlineData(OceanBaseCompatibilityMode.MySql)]
        [InlineData(OceanBaseCompatibilityMode.Oracle)]
        public void Utf8Mb3AcceptsNormalizedUtf8Mb4SessionWithoutWideningInput(OceanBaseCompatibilityMode mode)
        {
            var encoding = ClientCharacterSet.Parse("utf8mb3");
            var state = new[] { "utf8mb4", "utf8mb4", "utf8mb4", "utf8mb4_general_ci" };
            HandshakeFlow.ValidateClientCharacterSet(encoding, mode, state);
            Assert.Equal(Convert.FromHexString("E4B8ADC3A9"), encoding.GetBytes("中é"));
            Assert.Throws<EncoderFallbackException>(() => encoding.GetBytes("😀"));
            for (var i = 0; i < state.Length; i++)
            {
                var mismatch = (string[])state.Clone();
                mismatch[i] = i == 3 ? "latin1_swedish_ci" : "latin1";
                Assert.Throws<NotSupportedException>(() => HandshakeFlow.ValidateClientCharacterSet(encoding, mode, mismatch));
            }
            Assert.False(ClientCharacterSet.Parse("utf8mb4").Matches("utf8mb3"));
            Assert.False(encoding.MatchesCollation("utf8mb4_general_ci;"));
        }

        public static IEnumerable<object[]> WireCases()
        {
            for (var sample = 0; sample < ClientCharacterSetFixture.Samples.Length; sample++)
                foreach (var oracle in new[] { false, true })
                    foreach (var asynchronous in new[] { false, true })
                        yield return new object[] { sample, oracle, asynchronous };
        }

        [Theory]
        [MemberData(nameof(WireCases))]
        public Task ClientCharacterSetsReachTextAndPreparedBytesWithUtf8Results(int sample, bool oracle, bool asynchronous)
            => ClientCharacterSetFixture.Verify(typeof(OceanBaseConnection), ProtocolFixture.Load("initial-handshake-oceanbase").Payload, sample, oracle, asynchronous);

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        public Task ClientCharacterSetsAreRestoredOnPooledReuse(bool oracle, bool asynchronous, bool deprecated)
            => ClientCharacterSetFixture.Verify(typeof(OceanBaseConnection), ProtocolFixture.Load("initial-handshake-oceanbase").Payload, 3, oracle, asynchronous, pooling: true, deprecated: deprecated);

        [Fact]
        public void ClientEncodingsPreserveAsciiAndRejectLossyServerAliases()
        {
            var ascii = new string(Enumerable.Range(0, 128).Select(i => (char)i).ToArray());
            foreach (var sample in ClientCharacterSetFixture.Samples)
            {
                var encoding = ClientCharacterSet.Parse(sample.Name);
                Assert.Equal(Enumerable.Range(0, 128).Select(i => (byte)i).ToArray(), encoding.GetBytes(ascii));
                Assert.Equal(Convert.FromHexString(sample.Hex), encoding.GetBytes(sample.Text));
                Assert.Equal(Convert.FromHexString(sample.Hex).Length, encoding.GetByteCount(sample.Text));
                Assert.Throws<EncoderFallbackException>(() => encoding.GetBytes("\ud800"));
            }
            Assert.Equal(new byte[] { 92, 110 }, ClientCharacterSet.Parse("sjis").GetBytes("\\n"));
            Assert.Throws<EncoderFallbackException>(() => ClientCharacterSet.Parse("gbk").GetBytes("€"));
            Assert.Throws<EncoderFallbackException>(() => ClientCharacterSet.Parse("hkscs").GetBytes("\u2593"));
            Assert.Throws<EncoderFallbackException>(() => ClientCharacterSet.Parse("utf8mb3").GetBytes("😀"));
            Assert.Throws<EncoderFallbackException>(() => ClientCharacterSet.Parse("ascii").GetBytes("中"));
        }

        [Fact]
        public void FrozenOptionsRetainEncodingAcrossEndpointAndCredentialCopies()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=ob;User ID=u;Character Set=GBK");
            var options = builder.Build(); builder.CharacterSet = "latin1";
            Assert.Equal("gbk", options.ClientCharacterSet.Name);
            Assert.Equal("gbk", options.ForEndpoint(OceanBaseEndpoint.Parse("other", 2881)[0]).ClientCharacterSet.Name);
            Assert.Equal("gbk", options.WithCredential(new OceanBaseCredential("new", "secret", 1)).ClientCharacterSet.Name);
            Assert.Equal("gbk", new OceanBaseConnectionStringBuilder("CharacterSet=GBK").CharacterSet);
            Assert.Equal(new OceanBaseConnectionStringBuilder("Server=ob;User ID=u").ToPoolConfiguration(),
                new OceanBaseConnectionStringBuilder("Server=ob;User ID=u;Character Set=utf8mb4").ToPoolConfiguration());
        }

        [Fact]
        public void ClientEncodingReachesNestedMembersTypeIdentitiesAndOracleLiteralBudgets()
        {
            var gbk = ClientCharacterSet.Parse("gbk");
            var nested = new ComplexType(false, new ComplexType(253), new ComplexType(true, new ComplexType(254)));
            Assert.Equal(Convert.FromHexString("0002D6D0020202D6D0"), ComplexValueCodec.EncodeParameter(nested,
                new object?[] { "中", new object?[] { "中", null } }, OceanBaseCompatibilityMode.Oracle, clientEncoding: gbk));
            var type = new OceanBaseComplexType(null, "库", "型", false, new[] { new OceanBaseComplexMember("x", null, "VARCHAR2", null, new ComplexType(253)) });
            var parameter = new OceanBaseParameter { Value = new OceanBaseComplexValue(type, new object?[] { "中" }) };
            Assert.Equal(Convert.FromHexString("0001A00002BFE202D0CD000002D6D0"),
                PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle, clientEncoding: gbk));
            var bindings = new ParameterBindingPlan("SELECT ?", new[] { new OceanBaseParameter { Value = new string('é', 3000) } });
            var literal = "SELECT (REPLACE( '" + new string('é', 3000) + "',NULL,NULL))";
            Assert.Equal(literal, ClientPreparedSql.Build(bindings, OceanBaseCompatibilityMode.Oracle, clientEncoding: ClientCharacterSet.Parse("latin1")));
            Assert.Equal(literal, ClientPreparedSql.Build(bindings, OceanBaseCompatibilityMode.Oracle));
            Assert.Throws<System.Text.EncoderFallbackException>(() => ClientPreparedSql.Build(bindings, OceanBaseCompatibilityMode.Oracle, clientEncoding: ClientCharacterSet.Parse("ascii")));
            var national = new ComplexType(false, new ComplexType(207));
            Assert.Equal(Convert.FromHexString("0002D6D0"), ComplexValueCodec.EncodeParameter(national, new object?[] { "中" },
                OceanBaseCompatibilityMode.Oracle, new NationalCharacterSet("utf16", "utf8mb4", "gbk"), clientEncoding: gbk));
        }

        [Fact]
        public void OraclePrepareExecuteAndArrayBindingUseClientBytesLengthsAndChecksum()
        {
            var encoding = ClientCharacterSet.Parse("latin1");
            var packet = OraclePrepareExecutePacket.Build(0, "begin pé; end;", 0, Array.Empty<byte>(), encoding);
            var text = Convert.FromHexString("626567696E2070E93B20656E643B");
            Assert.Equal(text.Length, packet[10]); Assert.Equal(text, packet.AsSpan(11, text.Length).ToArray());
            uint checksum = 0;
            foreach (var b in text)
            { checksum ^= b; for (var bit = 0; bit < 8; bit++) checksum = checksum >> 1 ^ ((checksum & 1) == 0 ? 0 : 0x82f63b78u); }
            Assert.Equal(checksum, BitConverter.ToUInt32(packet, packet.Length - 8));
            using var connection = new OceanBaseConnection();
            using var session = new PhysicalSession(new OceanBaseConnectionStringBuilder("Server=ob;User ID=u;Character Set=latin1").Build());
            var context = new ComplexTypeContext(connection, session, null, 0, 30);
            var type = new OceanBaseComplexType(context, "", "", true, new[] { new OceanBaseComplexMember(null, null, "VARCHAR2", null, new ComplexType(253)) });
            var columns = new[] { new OceanBaseParameter { Value = new OceanBaseComplexValue(type, new object?[] { "é", "€" }) } };
            packet = OraclePrepareExecutePacket.BuildArray(0, "begin pé; end;", columns, clientEncoding: encoding);
            Assert.Equal(2u, BitConverter.ToUInt32(packet, 6)); Assert.Equal(text, packet.AsSpan(11, text.Length).ToArray());
            var expected = Convert.FromHexString("0001A000000000FD020001E90180");
            Assert.Equal(expected, packet.AsSpan(15 + text.Length, expected.Length).ToArray());
            Assert.Equal(checksum, BitConverter.ToUInt32(packet, packet.Length - 8));
        }

        [Fact]
        public void ClientEncodingKeepsDefaultJsonAndStandaloneNationalPayloadsIndependent()
        {
            var parameters = new[] { new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Value = "{\"x\":\"中😀\"}" } };
            Assert.Throws<EncoderFallbackException>(() => PreparedParameters.Encode(parameters, clientEncoding: ClientCharacterSet.Parse("ascii")));
            Assert.Equal(PreparedParameters.Encode(parameters), PreparedParameters.Encode(parameters, clientEncoding: ClientCharacterSet.Parse("utf8mb4")));
            parameters = new[] { new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.NVarchar2, Value = "中😀" } };
            var national = new NationalCharacterSet("utf16", "utf8mb4", "gbk");
            Assert.Equal(PreparedParameters.Encode(parameters, OceanBaseCompatibilityMode.Oracle, national),
                PreparedParameters.Encode(parameters, OceanBaseCompatibilityMode.Oracle, national, clientEncoding: ClientCharacterSet.Parse("gbk")));
        }
        [Theory]
        [InlineData("utf8", "utf8mb3")]
        [InlineData("UTF8MB4", "utf8mb4")]
        [InlineData("ascii", "ascii")]
        [InlineData("latin1", "latin1")]
        [InlineData("gbk", "gbk")]
        [InlineData("gb18030", "gb18030")]
        [InlineData("gb18030_2022", "gb18030_2022")]
        [InlineData("dec8", "dec8")]
        [InlineData("tis620", "tis620")]
        [InlineData("sjis", "sjis")]
        [InlineData("big5", "big5")]
        [InlineData("hkscs", "hkscs")]
        [InlineData("hkscs31", "hkscs31")]
        public void InputCharsetIsCanonicalAndPartitionsPools(string input, string canonical)
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=ob;User ID=u;Character Set=" + input);
            Assert.Equal(canonical, builder["Character Set"]);
            Assert.Contains("Character Set=" + canonical, builder.ToSanitizedConnectionString(false));
            var normalized = new OceanBaseConnectionStringBuilder("Server=ob;User ID=u;Character Set=" + canonical);
            Assert.Equal(normalized.ToPoolConfiguration(), builder.ToPoolConfiguration());
            if (canonical != "utf8mb4")
                Assert.NotEqual(new OceanBaseConnectionStringBuilder("Server=ob;User ID=u").ToPoolConfiguration(), builder.ToPoolConfiguration());
        }

        [Theory]
        [InlineData("utf16")]
        [InlineData("utf16le")]
        [InlineData("binary")]
        [InlineData("unknown")]
        [InlineData("latin1;SET autocommit=0")]
        public void UnsupportedInputCharsetFailsBeforeOpening(string name)
            => Assert.Throws<ArgumentException>(() => new OceanBaseConnectionStringBuilder { ["Character Set"] = name });
    }
}
