using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed class JsonClientCharacterSetTests
{
    public static IEnumerable<object[]> Samples()
        => Enumerable.Range(0, ClientCharacterSetFixture.Samples.Length).Select(i => new object[] { i });

    public static IEnumerable<object[]> WireCases()
        => Samples().SelectMany(row => new[] { false, true }.Select(async => new object[] { row[0], async }));

    [Theory]
    [MemberData(nameof(WireCases))]
    public Task JsonClientBytesAndUtf8ResultsReachBothRowProtocols(int sample, bool asynchronous)
        => ClientCharacterSetFixture.Verify(typeof(OceanBaseConnection), ProtocolFixture.Load("initial-handshake-oceanbase").Payload,
            sample, false, asynchronous, json: true);

    [Theory]
    [MemberData(nameof(Samples))]
    public void JsonParametersUseStrictClientBytesAndKeepNullAndMetadataContracts(int sample)
    {
        var (name, text, hex) = ClientCharacterSetFixture.Samples[sample];
        var encoding = ClientCharacterSet.Parse(name);
        var parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Json, Value = "{\"x\":\"" + text + "\"}" };
        var expected = Encoding.ASCII.GetBytes("{\"x\":\"").Concat(Convert.FromHexString(hex)).Concat(Encoding.ASCII.GetBytes("\"}")).ToArray();
        Assert.Equal(new byte[] { 0, 1, 253, 0, (byte)expected.Length }.Concat(expected), PreparedParameters.Encode(new[] { parameter }, clientEncoding: encoding));
        Assert.Equal(PreparedParameters.Encode(new[] { parameter }, clientEncoding: encoding),
            PreparedParameters.Encode(new[] { parameter }, validateOnly: true, clientEncoding: encoding));
        foreach (var json in new[] { "null", "{\"x\":\"\\u4E2D\\uD83D\\uDE00\"}" })
        {
            parameter.Value = json;
            Assert.Equal(new byte[] { 0, 1, 253, 0, (byte)json.Length }.Concat(Encoding.ASCII.GetBytes(json)),
                PreparedParameters.Encode(new[] { parameter }, clientEncoding: encoding));
        }
        foreach (var value in new object?[] { null, DBNull.Value })
        {
            parameter.Value = value;
            Assert.Equal(new byte[] { 1, 1, 6, 0 }, PreparedParameters.Encode(new[] { parameter }, clientEncoding: encoding));
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle, clientEncoding: encoding));
            parameter.Precision = 1;
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, clientEncoding: encoding));
            parameter.Precision = 0; parameter.Scale = 1;
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, clientEncoding: encoding));
            parameter.Scale = 0;
        }
        parameter.Value = "{\"x\":\"\ud800\"}";
        Assert.Throws<EncoderFallbackException>(() => PreparedParameters.Encode(new[] { parameter }, clientEncoding: encoding));
    }
}
