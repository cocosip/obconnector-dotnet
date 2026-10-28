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
        public async Task UnsupportedServerFallsBackToUncompressedAuthentication(bool asynchronous)
        {
            await using var server = new CompressionServer(ProtocolFixture.Load("initial-handshake-oceanbase").Payload, false, advertise: false);
            using var connection = new OceanBaseConnection($"Server=127.0.0.1;Port={server.Port};User ID=u;Password=secret;TLS Mode=Disabled;Use Compression=true");
            if (asynchronous) await connection.OpenAsync(); else connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = "select 'fallback'";
            Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            Assert.Equal(1, server.Authentications); Assert.False(server.Compressed); Assert.Equal(1, server.Queries);
        }

        [Fact]
        public void CapabilityIsExplicitAndFallsBackWhenServerLacksSupport()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=ob;User ID=u");
            Assert.False(builder.UseCompression);
            Assert.Equal(0u, (uint)(ClientCapabilities.Build(CapabilityFlags.ClientCompress, builder.Build()) & CapabilityFlags.ClientCompress));
            builder.UseCompression = true;
            Assert.Equal(0u, (uint)(ClientCapabilities.Build(CapabilityFlags.None, builder.Build()) & CapabilityFlags.ClientCompress));
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
