using System;
using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TlsServerValidationTests
    {

        [Theory]
        [InlineData(false, OceanBaseTlsMode.Required)]
        [InlineData(true, OceanBaseTlsMode.Required)]
        [InlineData(false, OceanBaseTlsMode.Disabled)]
        [InlineData(true, OceanBaseTlsMode.Disabled)]
        public async Task ExplicitPolicyCannotBeSilentlyIgnoredByTlsMode(bool asynchronous, OceanBaseTlsMode mode)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var builder = new OceanBaseConnectionStringBuilder(Configuration(((IPEndPoint)listener.LocalEndpoint).Port)) { TlsMode = mode };
            using var connection = new OceanBaseConnection(builder.ConnectionString);
            SetPolicy(connection, (_, _, _, _) => true);
            var error = await Assert.ThrowsAsync<NotSupportedException>(() => Open(connection, asynchronous));
            Assert.Contains("VerifyFull", error.Message);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.False(listener.Pending());
        }

        [Theory]
        [InlineData(OceanBaseTlsMode.Disabled)]
        [InlineData(OceanBaseTlsMode.Required)]
        public void SourceBuildRejectsIgnoredValidationPolicy(OceanBaseTlsMode mode)
        {
            var builder = new OceanBaseDataSourceBuilder("Server=127.0.0.1;User ID=u")
                .UseRemoteCertificateValidationCallback((_, _, _, _) => true);
            builder.ConnectionStringBuilder.TlsMode = mode;
            Assert.Throws<NotSupportedException>(builder.Build);
        }

        [Fact]
        public void PolicyCannotBeChangedAfterDisposal()
        {
            var connection = new OceanBaseConnection();
            SetPolicy(connection, null);
            connection.Dispose();
            Assert.Throws<ObjectDisposedException>(() => SetPolicy(connection, null));
        }
    }
}
