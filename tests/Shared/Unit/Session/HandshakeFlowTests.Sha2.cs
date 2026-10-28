using System;
using System.IO;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.Internal.Transport;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    public sealed partial class HandshakeFlowTests
    {

        private static PhysicalSession Sha2Session(int port, OceanBaseTlsMode tls = OceanBaseTlsMode.Disabled, int timeout = 5)
            => new PhysicalSession(new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = port, UserId = "u@sys", Password = "secretpw", TlsMode = tls, ConnectionTimeout = timeout,
            }.Build());

        private static byte[] Sha2Token => Convert.FromHexString("40d9e546b2cb35d699e9c5254222d2d9961b99dcc2b43f3656634f559e76779b");

        private static byte[] Sha2Switch(string plugin)
        {
            using var buffer = new MemoryStream(); buffer.WriteByte(254);
            buffer.Write(Encoding.ASCII.GetBytes(plugin)); buffer.WriteByte(0);
            buffer.Write(SeedBytes(SeedHex)); buffer.WriteByte(0);
            return buffer.ToArray();
        }
    }
}
