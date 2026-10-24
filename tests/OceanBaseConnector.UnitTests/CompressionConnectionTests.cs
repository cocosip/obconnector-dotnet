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
    public sealed class CompressionConnectionTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var oracle in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var tls in new[] { false, true }) yield return new object[] { oracle, asynchronous, tls };
        }
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DefaultDoesNotCompressEvenWhenServerAdvertisesIt(bool asynchronous)
        {
            await using var server = new CompressionServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, false);
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={server.Port};User ID=u;TLS Mode=Disabled");
            if (asynchronous) await connection.OpenAsync(); else connection.Open();
            Assert.False(server.Compressed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task UnsupportedServerFailsBeforeCredentialsAreSent(bool asynchronous)
        {
            await using var server = new CompressionServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, false, advertise: false);
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={server.Port};User ID=u;Password=secret;TLS Mode=Disabled;Use Compression=true");
            var error = await Record.ExceptionAsync(async () => { if (asynchronous) await connection.OpenAsync(); else connection.Open(); });
            Assert.NotNull(error); Assert.Equal(0, server.Authentications);
        }

        [Fact]
        public void CapabilityIsExplicitAndRequiresServerSupport()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=ob;User ID=u");
            Assert.False(builder.UseCompression);
            Assert.Equal(0u, (uint)(ClientCapabilities.Build(CapabilityFlags.ClientCompress, builder.Build()) & CapabilityFlags.ClientCompress));
            builder.UseCompression = true;
            Assert.Throws<ProtocolException>(() => ClientCapabilities.Build(CapabilityFlags.None, builder.Build()));
            Assert.NotEqual(0u, (uint)(ClientCapabilities.Build(CapabilityFlags.ClientCompress, builder.Build()) & CapabilityFlags.ClientCompress));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LocalInfileContinuesBothSequenceCountersAcrossDirectionChanges(bool asynchronous)
        {
            await using var server = new CompressionServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, false, localInfile: true);
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={server.Port};User ID=u;TLS Mode=Disabled;Use Compression=true;Allow Local Infile=true");
            var data = new byte[200_000]; Array.Fill(data, (byte)42);
            connection.LocalInfileStreamFactory = name => { Assert.Equal("x", name); return new System.IO.MemoryStream(data); };
            if (asynchronous) await connection.OpenAsync(); else connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = "load data local infile 'x'";
            Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            Assert.Equal(data.Length, server.UploadedBytes);
        }
    }
}
