using System;
using System.Collections.Generic;
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
        public static IEnumerable<object[]> ClientPrepareAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var kind in new[] { "standard", "ob20", "trailing", "binary-standard", "binary-ob20" }) yield return new object[] { target, asynchronous, kind };
        }

        [Theory]
        [MemberData(nameof(ClientPrepareAssets))]
        public async Task EveryAssetFallsBackOnlyAfterCompleteUnsupportedPrepare(string target, bool asynchronous, string kind)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var ob20 = kind != "standard" && kind != "binary-standard";
            var largeBinary = kind.StartsWith("binary-", StringComparison.Ordinal);
            var binary = largeBinary ? Enumerable.Range(0, 65536).Select(i => (byte)i).ToArray() : Array.Empty<byte>();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token);
                var prepare = await ReadRequest(stream, channel, ob20, 2, budget.Token);
                Assert.Equal(22, prepare[0]); Assert.Equal("update t set x=?", Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                var response = Frame(1, new byte[] { 255, 15, 5, 35, 72, 89, 48, 48, 48, 115 });
                if (kind == "trailing") response = response.Concat(Frame(2, new byte[] { 0 })).ToArray();
                await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, 2, 1, response) : response, budget.Token);
                if (kind == "trailing") { await ObserveClose(stream, budget.Token); return; }
                if (!ob20) channel.BeginCommand();
                var (query, nextSequence) = ob20 ? await ReadOb20Request(stream, 3, budget.Token)
                    : (await channel.ReadPacketAsync(budget.Token), (byte)0);
                Assert.Equal(3, query[0]);
                Assert.Equal(largeBinary ? "update t set x=(HEXTORAW('" + Convert.ToHexString(binary) + "'))"
                    : "update t set x=(CAST('a''\\中' AS VARCHAR2(4000)))", Encoding.UTF8.GetString(query, 1, query.Length - 1));
                response = Frame(1, Report(false, 1));
                await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, 3, nextSequence, response) : response, budget.Token);
            }, budget.Token);
            var context = new AssemblyLoadContext("client-prepare-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = new DbConnectionStringBuilder
                { ["Server"] = "127.0.0.1", ["Port"] = ((IPEndPoint)listener.LocalEndpoint).Port, ["User ID"] = "u", ["TLS Mode"] = "Disabled", ["Use OB20"] = ob20 }.ConnectionString;
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand();
                var property = command.GetType().GetProperty("AllowClientSidePrepareFallback")!;
                Assert.False((bool)property.GetValue(command)!); property.SetValue(command, true);
                command.CommandText = "update t set x=:p";
                var parameter = command.CreateParameter(); parameter.ParameterName = "p"; parameter.Value = largeBinary ? binary : "a'\\中"; command.Parameters.Add(parameter);
                if (kind == "trailing")
                {
                    var error = await Assert.ThrowsAnyAsync<DbException>(() => asynchronous ? command.ExecuteNonQueryAsync(budget.Token) : Task.FromResult(command.ExecuteNonQuery()));
                    Assert.False((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!);
                    Assert.Equal(System.Data.ConnectionState.Broken, connection.State);
                }
                else Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally
            {
                budget.Cancel(); listener.Stop();
                try { await serving; } catch { } // Observe fixture cleanup without hiding the client failure.
                context.Unload();
            }
        }
    }
}
