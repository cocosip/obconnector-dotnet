using System;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class ParameterCompatibilityModeConsumerTests
    {
        [Theory]
        [InlineData("netstandard2.0")]
        [InlineData("netstandard2.1")]
        [InlineData("net10.0")]
        public async Task EveryAssetSnapshotsTheOpenConnectionModeOnCommandParameters(string target)
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
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 }, budget.Token);
                await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, token: budget.Token);
                channel.BeginCommand();
            });
            var context = new AssemblyLoadContext("mode-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var modeType = assembly.GetType("OceanBaseConnector.OceanBaseCompatibilityMode")!;
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                using (var closedCommand = connection.CreateCommand())
                    Assert.Null(Mode(closedCommand.CreateParameter()));
                await connection.OpenAsync(budget.Token);
                // The fake server authenticated as Oracle, so the first assertion uses the real post-auth mode.
                using (var command = connection.CreateCommand())
                    Assert.Equal(Enum.Parse(modeType, "Oracle"), Mode(command.CreateParameter()));
                // Only the MySQL post-auth mode fact is supplied in the fixture; public A05 authentication stays closed.
                var session = connection.GetType().GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                var infoField = session.GetType().GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var info = infoField.GetValue(session)!; var infoType = info.GetType();
                object Fact(string name) => infoType.GetProperty(name)!.GetValue(info)!;
                void SetMode(string mode) => infoField.SetValue(session, Activator.CreateInstance(infoType, Fact("ServerVersion"), Fact("ConnectionId"),
                    Enum.Parse(modeType, mode), Fact("Capabilities"), Fact("ServerStatus"), Fact("ObCapabilityFlag"), Fact("SessionVariables")));
                SetMode("MySql");
                using (var command = connection.CreateCommand())
                    Assert.Equal(Enum.Parse(modeType, "MySql"), Mode(command.CreateParameter()));
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }

        private static object? Mode(DbParameter parameter) => parameter.GetType().GetProperty("CompatibilityMode")!.GetValue(parameter);
    }
}
