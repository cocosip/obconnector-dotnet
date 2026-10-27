using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class CancellationConsumerTests
    {
        [Theory]
        [InlineData("netstandard2.0")]
        [InlineData("netstandard2.1")]
        [InlineData("net10.0")]
        public async Task EachAssetDistinguishesPublicCommandTimeoutFromCancellation(string target)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(SessionTimeZoneFixture.Report, budget.Token);
                await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, token: budget.Token); channel.BeginCommand();
                var request = await channel.ReadPacketAsync(budget.Token);
                Assert.Equal(3, request[0]); Assert.Equal("update timeout", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await Assert.ThrowsAnyAsync<Exception>(() => channel.ReadPacketAsync(budget.Token));
            });
            var context = new AssemblyLoadContext("timeout-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                await connection.OpenAsync(budget.Token);
                using var command = connection.CreateCommand(); command.CommandText = "update timeout"; command.CommandTimeout = 1;
                using var preCanceled = new CancellationTokenSource(); preCanceled.Cancel();
                var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteNonQueryAsync(preCanceled.Token));
                Assert.Equal(preCanceled.Token, canceled.CancellationToken); Assert.Equal(ConnectionState.Open, connection.State);
                var error = await Assert.ThrowsAnyAsync<DbException>(() => command.ExecuteNonQueryAsync().WaitAsync(budget.Token));
                Assert.Equal(assembly, error.GetType().Assembly);
                Assert.Equal(true, error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error));
                Assert.IsType<TimeoutException>(error.InnerException); Assert.Equal(ConnectionState.Broken, connection.State);
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
