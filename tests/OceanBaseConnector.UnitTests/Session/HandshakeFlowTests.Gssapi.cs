using System;
using System.Linq;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    public sealed partial class HandshakeFlowTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GssapiRejectsUnverifiedTlsBeforeParsingAnyServerPrincipal(bool switchPlugin)
        {
            using var auth = switchPlugin ? new AuthenticationConversation(BuildOptions(), "mysql_native_password", new byte[20]) : null;
            var error = Assert.Throws<OceanBaseException>(() =>
            {
                if (switchPlugin) auth!.Continue(new byte[] { 254 }.Concat(Encoding.ASCII.GetBytes("auth_gssapi_client\0bad\0\0")).ToArray());
                else using (new AuthenticationConversation(BuildOptions(), "auth_gssapi_client", Array.Empty<byte>())) { }
            });
            Assert.Contains("VerifyFull", error.Message);
        }
    }
}
