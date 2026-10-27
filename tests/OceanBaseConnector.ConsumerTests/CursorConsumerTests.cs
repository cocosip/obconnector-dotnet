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
    public sealed class CursorConsumerTests
    {
        [Theory]
        [InlineData("netstandard2.0", false)]
        [InlineData("netstandard2.0", true)]
        [InlineData("netstandard2.1", false)]
        [InlineData("netstandard2.1", true)]
        [InlineData("net10.0", false)]
        [InlineData("net10.0", true)]
        public async Task EachAssetRequestsAndConsumesOracleCursorPages(string target, bool deprecated)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            if (deprecated) greeting[Array.IndexOf(greeting, (byte)0, 1) + 20] |= 1;
            var column = new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 110, 1, 110, 12, 45, 0, 255, 0, 0, 0, 8, 0, 0, 0, 0, 0 };
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(SessionTimeZoneFixture.Report, budget.Token);
                await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, deprecated: deprecated, token: budget.Token); channel.BeginCommand();
                Assert.Equal(22, (await channel.ReadPacketAsync(budget.Token))[0]);
                await channel.WritePacketAsync(new byte[] { 0, 80, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, budget.Token);
                await channel.WritePacketAsync(column, budget.Token); if (!deprecated) await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                channel.BeginCommand(); Assert.Equal(new byte[] { 23, 80, 0, 0, 0, 1, 1, 0, 0, 0 }, await channel.ReadPacketAsync(budget.Token));
                await channel.WritePacketAsync(new byte[] { 1 }, budget.Token); await channel.WritePacketAsync(column, budget.Token);
                if (!deprecated) await channel.WritePacketAsync(new byte[] { 254, 0, 0, 70, 0 }, budget.Token);
                for (var page = 0; page < 3; page++)
                {
                    channel.BeginCommand(); Assert.Equal(new byte[] { 28, 80, 0, 0, 0, 1, 0, 0, 0 }, await channel.ReadPacketAsync(budget.Token));
                    await channel.WritePacketAsync(new byte[] { 1 }, budget.Token); await channel.WritePacketAsync(column, budget.Token);
                    if (!deprecated) await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                    var row = new byte[10]; BitConverter.GetBytes((long)(page + 42)).CopyTo(row, 2); await channel.WritePacketAsync(row, budget.Token);
                    await channel.WritePacketAsync(deprecated
                        ? new byte[] { 254, 0, 0, page == 2 ? (byte)134 : (byte)70, 0, 0, 0 }
                        : new byte[] { 254, 0, 0, page == 2 ? (byte)134 : (byte)70, 0 }, budget.Token);
                }
                channel.BeginCommand(); Assert.Equal(new byte[] { 25, 80, 0, 0, 0 }, await channel.ReadPacketAsync(budget.Token));
            });
            var context = new AssemblyLoadContext("cursor-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                await connection.OpenAsync(budget.Token);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select n"; var property = command.GetType().GetProperty("FetchSize")!;
                    Assert.Equal(0, property.GetValue(command)); property.SetValue(command, 1);
                    using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, budget.Token);
                    Assert.True(reader.HasRows); Assert.Single(reader.GetColumnSchema());
                    for (var value = 42; value < 45; value++)
                    { Assert.True(await reader.ReadAsync(budget.Token)); Assert.Equal(value, await reader.GetFieldValueAsync<long>(0, budget.Token)); }
                    Assert.False(await reader.ReadAsync(budget.Token)); Assert.Equal(ConnectionState.Open, connection.State);
                }
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
