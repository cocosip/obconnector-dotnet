using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
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
        public static IEnumerable<object[]> MySqlJsonFallbackAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var kind in new[] { "standard", "ob20", "trailing", "execute-error" }) yield return new object[] { target, asynchronous, kind };
        }

        [Theory]
        [MemberData(nameof(MySqlJsonFallbackAssets))]
        public async Task EveryAssetFallsBackForMySqlJsonWithFreshStateAndExactBoundaries(string target, bool asynchronous, string kind)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var ob20 = kind is "ob20" or "trailing";
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, mysql: true);
                uint requestId = 1;
                for (var run = 0; run < (kind == "trailing" ? 1 : 4); run++)
                {
                    if (!ob20) channel.BeginCommand();
                    var prepare = await ReadRequest(stream, channel, ob20, requestId, budget.Token);
                    Assert.Equal(22, prepare[0]); Assert.Equal("INSERT INTO t VALUES (?)", Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                    var response = Frame(1, new byte[] { 255, 15, 5, 35, 72, 89, 48, 48, 48, 115 });
                    if (kind == "trailing") response = response.Concat(Frame(2, new byte[] { 0 })).ToArray();
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId, 1, response) : response, budget.Token); requestId++;
                    if (kind == "trailing") { await ObserveClose(stream, budget.Token); return; }
                    if (run < 3)
                    {
                        if (!ob20) channel.BeginCommand();
                        var state = await ReadRequest(stream, channel, ob20, requestId, budget.Token); Assert.Equal(3, state[0]);
                        Assert.Equal("SELECT @@character_set_client, @@character_set_connection, @@collation_connection, @@sql_mode", Encoding.UTF8.GetString(state, 1, state.Length - 1));
                        response = TextResult("utf8mb4", "utf8mb4", "utf8mb4_bin", run == 0 ? "" : "NO_BACKSLASH_ESCAPES");
                        await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId, 1, response) : response, budget.Token); requestId++;
                    }
                    if (!ob20) channel.BeginCommand();
                    var query = await ReadRequest(stream, channel, ob20, requestId, budget.Token); Assert.Equal(3, query[0]);
                    Assert.Equal(run switch
                    {
                        0 => "INSERT INTO t VALUES (('{\"x\":\"a''\\\\\\\\中;?\"}'))",
                        1 => "INSERT INTO t VALUES (('{\"x\":\"a''\\\\中;?\"}'))",
                        2 => "INSERT INTO t VALUES (('null'))",
                        _ => "INSERT INTO t VALUES ((NULL))"
                    }, Encoding.UTF8.GetString(query, 1, query.Length - 1));
                    response = Frame(1, kind == "execute-error" ? new byte[] { 255, 15, 5, 35, 72, 89, 48, 48, 48, 115 } : Report(false, 1));
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId, 1, response) : response, budget.Token); requestId++;
                }
                await ObserveClose(stream, budget.Token);
            }, budget.Token);
            var context = new AssemblyLoadContext("mysql-json-fallback-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = new DbConnectionStringBuilder
                { ["Server"] = "127.0.0.1", ["Port"] = ((IPEndPoint)listener.LocalEndpoint).Port, ["User ID"] = "u", ["TLS Mode"] = "Disabled", ["Use OB20"] = ob20 }.ConnectionString;
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand(); command.GetType().GetProperty("AllowClientSidePrepareFallback")!.SetValue(command, true);
                command.CommandText = "INSERT INTO t VALUES (?)";
                var parameter = command.CreateParameter(); parameter.Value = "{\"x\":\"a'\\\\中;?\"}";
                parameter.GetType().GetProperty("OceanBaseDbType")!.SetValue(parameter, Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseDbType")!, "Json")); command.Parameters.Add(parameter);
                for (var run = 0; run < (kind == "trailing" ? 1 : 4); run++)
                {
                    if (run == 2) parameter.Value = "null";
                    if (run == 3) parameter.Value = DBNull.Value;
                    Func<Task<int>> execute = () => asynchronous ? command.ExecuteNonQueryAsync(budget.Token) : Task.FromResult(command.ExecuteNonQuery());
                    if (kind is "trailing" or "execute-error")
                    {
                        var error = await Assert.ThrowsAnyAsync<DbException>(execute);
                        Assert.False((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!);
                        Assert.Equal(kind == "trailing" ? ConnectionState.Broken : ConnectionState.Open, connection.State);
                    }
                    else Assert.Equal(1, await execute());
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally
            {
                budget.Cancel(); listener.Stop();
                try { await serving; } catch { } // Observe fixture cleanup without replacing the client failure.
                context.Unload();
            }
        }
    }
}
