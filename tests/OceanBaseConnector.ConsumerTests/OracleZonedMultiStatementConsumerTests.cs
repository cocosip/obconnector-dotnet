using System;
using System.Data.Common;
using System.IO;
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
        [Theory]
        [MemberData(nameof(OracleMultiParameterAssets))]
        public async Task PublicAssetsBindExactOracleZonedTimestampsInTextMultiStatements(string target, bool asynchronous, bool ob20)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token);
                channel.BeginCommand();
                var requestId = ob20 ? 2u : 1u;
                var request = await ReadRequest(stream, channel, ob20, requestId, budget.Token);
                Assert.Equal(3, request[0]);
                Assert.Equal("insert into t values ((TIMESTAMP '2026-01-02 03:04:05.123456789 -00:30')); update t set v=(CAST(FROM_TZ(TIMESTAMP '2026-01-02 03:04:05.123456789', '+00:00') AS TIMESTAMP(9) WITH LOCAL TIME ZONE))",
                    Encoding.UTF8.GetString(request, 1, request.Length - 1));
                using var response = new MemoryStream();
                response.Write(Frame(1, new byte[] { 0, 2, 0, 14, 0, 0, 0 })); response.Write(Frame(2, Report(false, 3)));
                var bytes = response.ToArray();
                await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId, 1, bytes) : bytes, budget.Token);
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("zoned-multi-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Allow Multi Statements=true;Use OB20={ob20}";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "insert into t values (:z); update t set v=:l";
                var timestamp = Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseTimestamp")!, new DateTime(2026, 1, 2, 3, 4, 5), 123456789, (byte)9)!;
                var zoned = command.CreateParameter(); zoned.ParameterName = "z";
                zoned.Value = Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseTimestampWithTimeZone")!, timestamp, TimeSpan.FromMinutes(-30), null, null);
                command.Parameters.Add(zoned);
                var local = command.CreateParameter(); local.ParameterName = "l";
                local.Value = Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseTimestampLocalTimeZone")!, timestamp, "+05:45");
                command.Parameters.Add(local);
                if (asynchronous) await Assert.ThrowsAsync<ArgumentException>(() => command.ExecuteNonQueryAsync(budget.Token));
                else Assert.Throws<ArgumentException>(() => command.ExecuteNonQuery());
                local.Value = Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseTimestampLocalTimeZone")!, timestamp, "+00:00");
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                await serving.WaitAsync(budget.Token);
            }
            catch { if (serving.IsFaulted) await serving; throw; }
            finally
            {
                budget.Cancel(); listener.Stop();
                try { await serving; } catch { } // Preserve the client failure after observing server cleanup.
                context.Unload();
            }
        }
    }
}
