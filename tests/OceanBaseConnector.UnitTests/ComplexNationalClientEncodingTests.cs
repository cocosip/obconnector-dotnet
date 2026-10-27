using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Values;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed class ComplexNationalClientEncodingTests
{
    public static IEnumerable<object[]> WireCases()
    {
        for (var sample = 0; sample < ClientCharacterSetFixture.Samples.Length; sample++)
            foreach (var asynchronous in new[] { false, true })
                foreach (var prepareExecute in new[] { false, true }) yield return new object[] { sample, asynchronous, prepareExecute };
    }

    [Theory]
    [MemberData(nameof(WireCases))]
    public Task PublicComplexNationalInputsUseClientBytesAndRejectUnrepresentableValues(int sample, bool asynchronous, bool prepareExecute)
        => ComplexNationalClientFixture.Verify(typeof(OceanBaseConnection), ProtocolFixture.Load("initial-handshake-oceanbase").Payload, sample, asynchronous, prepareExecute);

    public static IEnumerable<object[]> Cases()
    {
        for (var sample = 0; sample < ClientCharacterSetFixture.Samples.Length; sample++)
            foreach (byte code in new byte[] { 207, 208 }) yield return new object[] { sample, code };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void NestedNationalInputsUseStrictConnectionBytesInsteadOfNationalEncoding(int sample, byte code)
    {
        var (name, text, hex) = ClientCharacterSetFixture.Samples[sample];
        var dbcs = ClientCharacterSetFixture.DbcsFallbackSamples.FirstOrDefault(value => value.Name == name);
        if (dbcs.Name != null) { text = dbcs.Text; hex = dbcs.Hex; }
        var type = new ComplexType(false, new ComplexType(true, new ComplexType(code)));
        var settings = new NationalCharacterSet("utf16", "utf8mb4", name);
        var member = text + "\\'  ";
        object?[] elements = { member, null, "" };
        var expected = new byte[] { 0, 3, 2, (byte)(hex.Length / 2 + 4) }.Concat(Convert.FromHexString(hex)).Concat(new byte[] { 92, 39, 32, 32, 0 }).ToArray();
        Assert.Equal(expected, ComplexValueCodec.EncodeParameter(type, new object?[] { elements }, OceanBaseCompatibilityMode.Oracle, settings));
        Assert.Equal(member, elements[0]); Assert.Null(elements[1]); Assert.Equal("", elements[2]);
        foreach (var invalid in new[] { "\ud800", "\udfff", name == "gb18030" || name == "gb18030_2022" || name == "hkscs31" ? "\ud800x" : "😀" })
        {
            elements[0] = invalid;
            Assert.Throws<EncoderFallbackException>(() => ComplexValueCodec.EncodeParameter(type, new object?[] { elements }, OceanBaseCompatibilityMode.Oracle, settings));
        }
        elements[0] = member;
        Assert.Equal(expected, ComplexValueCodec.EncodeParameter(type, new object?[] { elements }, OceanBaseCompatibilityMode.Oracle, settings));
        Assert.Equal(NationalCharacterSet.Utf16.GetBytes(member), settings.ParameterEncoding.GetBytes(member));
    }
}
