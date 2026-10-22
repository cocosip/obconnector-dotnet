using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class MultiAddressConsumerTests
    {
        [Theory]
        [InlineData("netstandard2.0", false)]
        [InlineData("netstandard2.0", true)]
        [InlineData("netstandard2.1", false)]
        [InlineData("netstandard2.1", true)]
        [InlineData("net8.0", false)]
        [InlineData("net8.0", true)]
        [InlineData("net9.0", false)]
        [InlineData("net9.0", true)]
        [InlineData("net10.0", false)]
        [InlineData("net10.0", true)]
        public async Task EveryAssetCanQueryAfterTcpFailover(string target, bool asynchronous)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var refused = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            refused.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token);
                await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(SessionTimeZoneFixture.Report, budget.Token);
                await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, token: budget.Token);
                channel.BeginCommand();
                Assert.Equal(new byte[] { 3, 117, 112, 100, 97, 116, 101 }, await channel.ReadPacketAsync(budget.Token));
                await channel.WritePacketAsync(new byte[] { 0, 7, 0, 6, 0, 0, 0 }, budget.Token);
            });
            var context = new AssemblyLoadContext("multi-address-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1:{((IPEndPoint)refused.LocalEndPoint!).Port},127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Connection Timeout=10";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                Assert.Equal(ConnectionState.Open, connection.State);
                using var command = connection.CreateCommand(); command.CommandText = "update";
                Assert.Equal(7, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                connection.Close(); Assert.Equal(ConnectionState.Closed, connection.State);
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
