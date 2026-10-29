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
        public async Task PublicAssetsBindOracleNationalStringsInTextMultiStatements(string target, bool asynchronous, bool ob20)
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
                Assert.Equal("insert into t values ((u'\\4e2d\\d83d\\de42\\0027\\005c\\0000')); update t set v=(NULL), n=(u'\\4e2d\\d83d\\de42\\0027\\005c\\0000')",
                    Encoding.UTF8.GetString(request, 1, request.Length - 1));
                using var response = new MemoryStream();
                response.Write(Frame(1, new byte[] { 0, 2, 0, 14, 0, 0, 0 })); response.Write(Frame(2, Report(false, 3)));
                var bytes = response.ToArray();
                await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId, 1, bytes) : bytes, budget.Token);
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("national-multi-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Allow Multi Statements=true;Use OB20={ob20}";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "insert into t values (:n); update t set v=:v, n=:N";
                var national = command.CreateParameter(); national.ParameterName = "n"; national.Value = "中🙂'\\\0"; national.Size = 6;
                national.GetType().GetProperty("OceanBaseDbType")!.SetValue(national, Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseDbType")!, "NChar"));
                command.Parameters.Add(national);
                var varying = command.CreateParameter(); varying.ParameterName = "v"; varying.Value = DBNull.Value;
                varying.GetType().GetProperty("OceanBaseDbType")!.SetValue(varying, Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseDbType")!, "NVarchar2"));
                command.Parameters.Add(varying);
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                Assert.Equal("中🙂'\\\0", national.Value);
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
