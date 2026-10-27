using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {
        public static IEnumerable<object[]> MySqlClientStringAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var ob20 in new[] { false, true }) yield return new object[] { target, asynchronous, ob20 };
        }

        [Theory]
        [MemberData(nameof(MySqlClientStringAssets))]
        public async Task EveryAssetPreparesMySqlClientStringsFromTrustedState(string target, bool asynchronous, bool ob20)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, mysql: true);
                uint id = 1;
                for (var i = 0; i < 2; i++)
                {
                    if (!ob20) channel.BeginCommand();
                    var request = await ReadRequest(stream, channel, ob20, id, budget.Token); Assert.Equal(22, request[0]);
                    Assert.Equal("update t set x=?", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                    var response = Frame(1, new byte[] { 255, 15, 5, 35, 72, 89, 48, 48, 48, 115 });
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, id, 1, response) : response, budget.Token); id++;
                    if (!ob20) channel.BeginCommand();
                    request = await ReadRequest(stream, channel, ob20, id, budget.Token); Assert.Equal(3, request[0]);
                    Assert.Equal("SELECT @@character_set_client, @@character_set_connection, @@collation_connection, @@sql_mode", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                    response = TextResult("utf8mb4", "utf8mb4", "utf8mb4_bin", i == 0 ? "" : "NO_BACKSLASH_ESCAPES");
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, id, 1, response) : response, budget.Token); id++;
                    if (!ob20) channel.BeginCommand();
                    request = await ReadRequest(stream, channel, ob20, id, budget.Token); Assert.Equal(3, request[0]);
                    Assert.Equal(i == 0 ? "update t set x=('a''\\\\中😀')" : "update t set x=('a''\\中😀')", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                    response = Frame(1, Report(false, 1));
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, id, 1, response) : response, budget.Token); id++;
                }
                await ObserveClose(stream, budget.Token);
            }, budget.Token);
            var context = new AssemblyLoadContext("mysql-client-string-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = new DbConnectionStringBuilder
                { ["Server"] = "127.0.0.1", ["Port"] = ((IPEndPoint)listener.LocalEndpoint).Port, ["User ID"] = "u", ["TLS Mode"] = "Disabled", ["Use OB20"] = ob20 }.ConnectionString;
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand(); command.GetType().GetProperty("AllowClientSidePrepareFallback")!.SetValue(command, true);
                command.CommandText = "update t set x=?"; var parameter = command.CreateParameter(); parameter.Value = "a'\\中😀"; command.Parameters.Add(parameter);
                for (var i = 0; i < 2; i++) Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
