using System;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    public sealed class GssapiChallengeTests
    {
        [Theory]
        [InlineData("mariadb/db@REALM\0Kerberos\0", "", "mariadb/db@REALM")]
        [InlineData("mariadb/db@REALM\0\0", "", "mariadb/db@REALM")]
        [InlineData("mariadb/数据库@REALM\0kerberos\0", "", "mariadb/数据库@REALM")]
        [InlineData("\0Kerberos\0", "mariadb/trusted@REALM", "mariadb/trusted@REALM")]
        public void GssapiUsesExplicitPrincipalOrPreservesServerPrincipal(string input, string configured, string expected)
        {
            Assert.Equal(expected, GssapiChallenge.Parse(Encoding.UTF8.GetBytes(input), configured));
        }

        [Theory]
        [InlineData("")]
        [InlineData("spn")]
        [InlineData("spn\0Kerberos")]
        [InlineData("spn\0NTLM\0")]
        [InlineData("spn\0Negotiate\0")]
        [InlineData("spn\0unknown-secret\0")]
        [InlineData("\0\0")]
        [InlineData("bad\nspn\0\0")]
        [InlineData("spn\0\0trailing")]
        public void GssapiRejectsMalformedChallengesWithoutEcho(string input)
        {
            var error = Assert.Throws<ProtocolException>(() => GssapiChallenge.Parse(Encoding.UTF8.GetBytes(input), ""));
            Assert.DoesNotContain("unknown-secret", error.ToString());
        }

        [Fact]
        public void GssapiRejectsInvalidUtf8AndExcessiveLengths()
        {
            Assert.Throws<ProtocolException>(() => GssapiChallenge.Parse(new byte[] { 255, 0, 0 }, ""));
            Assert.Throws<ProtocolException>(() => GssapiChallenge.Parse(new byte[16385], "configured"));
            Assert.Throws<ProtocolException>(() => GssapiChallenge.Parse(Encoding.UTF8.GetBytes(new string('x', 1025) + "\0\0"), ""));
        }

        [Fact]
        public void GssapiSwitchKeepsBothFieldTerminators()
        {
            var packet = Encoding.ASCII.GetBytes("\0auth_gssapi_client\0spn\0\0"); packet[0] = 254;
            var parsed = AuthSwitchRequest.Parse(packet);
            Assert.Equal(new byte[] { 115, 112, 110, 0, 0 }, parsed.Seed);
        }
    }
}
