using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class DatabaseChangeConsumerTests
    {
        [Theory]
        [InlineData("netstandard2.0", false)]
        [InlineData("netstandard2.1", false)]
        [InlineData("netstandard2.1", true)]
        [InlineData("net10.0", false)]
        [InlineData("net10.0", true)]
        public async Task EachAssetChangesDatabaseWithoutChangingConfiguration(string target, bool asynchronous)
        {
            const string database = "Mixed目标'`;\\库";
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(SessionTimeZoneFixture.Report, budget.Token);
                await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, token: budget.Token); channel.BeginCommand();
                Assert.Equal(new byte[] { 2 }.Concat(Encoding.UTF8.GetBytes("denied")).ToArray(), await channel.ReadPacketAsync(budget.Token));
                await channel.WritePacketAsync(new byte[] { 255, 40, 4, 35, 52, 50, 48, 48, 48 }, budget.Token); channel.BeginCommand();
                Assert.Equal(new byte[] { 2 }.Concat(Encoding.UTF8.GetBytes(database)).ToArray(), await channel.ReadPacketAsync(budget.Token));
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 }, budget.Token); channel.BeginCommand();
                Assert.Equal(3, (await channel.ReadPacketAsync(budget.Token))[0]);
                var column = new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 110, 1, 110, 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 };
                await channel.WritePacketAsync(new byte[] { 1 }, budget.Token); await channel.WritePacketAsync(column, budget.Token);
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }, budget.Token);
                var bytes = Encoding.UTF8.GetBytes(database); await channel.WritePacketAsync(new[] { (byte)bytes.Length }.Concat(bytes).ToArray(), budget.Token);
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }, budget.Token);
            });
            var context = new AssemblyLoadContext("database-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                await connection.OpenAsync(budget.Token);
                // A05 public authentication stays strict; this fixture supplies only the post-auth MySQL fact.
                var session = connection.GetType().GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                var field = session.GetType().GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var info = field.GetValue(session)!; var type = info.GetType();
                object Fact(string name) => type.GetProperty(name)!.GetValue(info)!;
                field.SetValue(session, Activator.CreateInstance(type, Fact("ServerVersion"), Fact("ConnectionId"),
                    Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseCompatibilityMode")!, "MySql"),
                    Fact("Capabilities"), Fact("ServerStatus"), Fact("ObCapabilityFlag"), Fact("SessionVariables")));
                var declaredAsync = connection.GetType().GetMethod("ChangeDatabaseAsync", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                Assert.Equal(target != "netstandard2.0", declaredAsync != null);
                if (asynchronous) await Assert.ThrowsAnyAsync<DbException>(() => connection.ChangeDatabaseAsync("denied", budget.Token));
                else Assert.ThrowsAny<DbException>(() => connection.ChangeDatabase("denied"));
                Assert.Equal("", connection.Database); Assert.Equal(ConnectionState.Open, connection.State);
                if (asynchronous) await connection.ChangeDatabaseAsync(database, budget.Token); else connection.ChangeDatabase(database);
                Assert.Equal(database, connection.Database); Assert.DoesNotContain(database, connection.ConnectionString);
                using var command = connection.CreateCommand(); command.CommandText = "select database()";
                Assert.Equal(database, asynchronous ? await command.ExecuteScalarAsync(budget.Token) : command.ExecuteScalar());
                connection.Close(); Assert.Equal("", connection.Database);
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
