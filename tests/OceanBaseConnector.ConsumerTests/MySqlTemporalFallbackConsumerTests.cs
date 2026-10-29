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
        public static IEnumerable<object[]> MySqlTemporalFallbackAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var kind in new[] { "standard", "ob20", "trailing", "execute-error" }) yield return new object[] { target, asynchronous, kind };
        }

        [Theory]
        [MemberData(nameof(MySqlTemporalFallbackAssets))]
        public async Task EveryAssetFallsBackForMySqlTemporalWithExactBoundaries(string target, bool asynchronous, string kind)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var ob20 = kind is "ob20" or "trailing";
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, mysql: true);
                for (uint run = 0; run < (kind == "trailing" ? 1 : 2); run++)
                {
                    if (!ob20) channel.BeginCommand();
                    var requestId = 1 + run * 2;
                    var prepare = await ReadRequest(stream, channel, ob20, requestId, budget.Token);
                    Assert.Equal(22, prepare[0]); Assert.Equal("INSERT INTO t VALUES (?,?,?)", Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                    var response = Frame(1, new byte[] { 255, 15, 5, 35, 72, 89, 48, 48, 48, 115 });
                    if (kind == "trailing") response = response.Concat(Frame(2, new byte[] { 0 })).ToArray();
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId, 1, response) : response, budget.Token);
                    if (kind == "trailing") { await ObserveClose(stream, budget.Token); return; }
                    if (!ob20) channel.BeginCommand();
                    var query = await ReadRequest(stream, channel, ob20, requestId + 1, budget.Token);
                    Assert.Equal(3, query[0]);
                    Assert.Equal(run == 0 ? "INSERT INTO t VALUES (('2026-10-29'),('2026-10-29 12:34:56.123456'),('-27:08:09.456789'))" : "INSERT INTO t VALUES ((NULL),(NULL),(NULL))",
                        Encoding.UTF8.GetString(query, 1, query.Length - 1));
                    response = Frame(1, kind == "execute-error" ? new byte[] { 255, 15, 5, 35, 72, 89, 48, 48, 48, 115 } : Report(false, 1));
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId + 1, 1, response) : response, budget.Token);
                }
                await ObserveClose(stream, budget.Token);
            }, budget.Token);
            var context = new AssemblyLoadContext("mysql-temporal-fallback-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = new DbConnectionStringBuilder
                { ["Server"] = "127.0.0.1", ["Port"] = ((IPEndPoint)listener.LocalEndpoint).Port, ["User ID"] = "u", ["TLS Mode"] = "Disabled", ["Use OB20"] = ob20 }.ConnectionString;
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand(); command.GetType().GetProperty("AllowClientSidePrepareFallback")!.SetValue(command, true);
                command.CommandText = "INSERT INTO t VALUES (?,?,?)";
                var provider = assembly.GetType("OceanBaseConnector.OceanBaseDbType")!;
                var date = command.CreateParameter(); date.Value = new DateTime(2026, 10, 29);
                date.GetType().GetProperty("OceanBaseDbType")!.SetValue(date, Enum.Parse(provider, "Date")); command.Parameters.Add(date);
                var stamp = command.CreateParameter(); stamp.Value = new DateTime(2026, 10, 29, 12, 34, 56).AddTicks(1234560); stamp.Scale = 6;
                stamp.GetType().GetProperty("OceanBaseDbType")!.SetValue(stamp, Enum.Parse(provider, "DateTime")); command.Parameters.Add(stamp);
                var duration = command.CreateParameter(); duration.Value = -new TimeSpan(1, 3, 8, 9).Add(TimeSpan.FromTicks(4567890)); duration.Scale = 6;
                duration.GetType().GetProperty("OceanBaseDbType")!.SetValue(duration, Enum.Parse(provider, "Time")); command.Parameters.Add(duration);
                for (var run = 0; run < (kind == "trailing" ? 1 : 2); run++)
                {
                    if (run != 0) date.Value = stamp.Value = duration.Value = DBNull.Value;
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
