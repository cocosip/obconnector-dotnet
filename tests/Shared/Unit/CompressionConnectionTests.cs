using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class CompressionConnectionTests
    {
        [Theory]
        [MemberData(nameof(Cases))]
        public async Task PublicConnectionsCompressAfterAuthenticationAndReuseResetSessions(bool oracle, bool asynchronous, bool tls)
        {
            await using var server = new CompressionServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, oracle, tls, authSwitch: true);
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={server.Port};User ID=u;Password=secret;Use Compression=true;Pooling=true;Max Pool Size=1;TLS Mode={(tls ? "VerifyFull" : "Disabled")}");
            if (tls) connection.RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString() == server.Certificate!.Thumbprint;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            for (var i = 0; i < 2; i++)
            {
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "select " + new string('x', 200_000);
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
            }
            OceanBaseConnection.ClearPool(connection);
            Assert.True(server.Compressed); Assert.Equal(1, server.Authentications);
            Assert.Equal(2, server.Queries); Assert.Equal(2, server.Resets); Assert.True(server.Pings >= 1);
        }
        public static IEnumerable<object[]> Cases()
        {
            foreach (var oracle in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var tls in new[] { OceanBaseConnector.Tests.TransportTestMode.Encrypted }) yield return new object[] { oracle, asynchronous, tls };
        }
    }
}
