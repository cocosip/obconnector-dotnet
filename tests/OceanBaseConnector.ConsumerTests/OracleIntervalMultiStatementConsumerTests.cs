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
        public async Task PublicAssetsBindExactOracleIntervalsInTextMultiStatements(string target, bool asynchronous, bool ob20)
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
                Assert.Equal("insert into t values ((INTERVAL '-012-03' YEAR(3) TO MONTH)); update t set v=(INTERVAL '+004 05:06:07.123456789' DAY(3) TO SECOND(9))",
                    Encoding.UTF8.GetString(request, 1, request.Length - 1));
                using var response = new MemoryStream();
                response.Write(Frame(1, new byte[] { 0, 2, 0, 14, 0, 0, 0 }));
                response.Write(Frame(2, Report(false, 3)));
                var bytes = response.ToArray();
                await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId, 1, bytes) : bytes, budget.Token);
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("interval-multi-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Allow Multi Statements=true;Use OB20={ob20}";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "insert into t values (:ym); update t set v=:ds";
                var ym = command.CreateParameter(); ym.ParameterName = "ym"; ym.Precision = 3;
                ym.Value = Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseIntervalYearToMonth")!, true, 12, 3, (byte)9);
                command.Parameters.Add(ym);
                var ds = command.CreateParameter(); ds.ParameterName = "ds";
                ds.Value = Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseIntervalDayToSecond")!, false, 4, 5, 6, 7, 123456789, (byte)3, (byte)9);
                command.Parameters.Add(ds);
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                await serving.WaitAsync(budget.Token);
            }
            catch { if (serving.IsFaulted) await serving; throw; }
            finally
            {
                budget.Cancel(); listener.Stop();
                // The main path observes server errors; cleanup must preserve the original client failure.
                try { await serving; } catch { }
                context.Unload();
            }
        }
    }
}
