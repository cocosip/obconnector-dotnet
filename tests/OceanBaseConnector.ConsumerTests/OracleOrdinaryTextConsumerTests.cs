using System;
using System.Data;
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
        public async Task PublicAssetsBindOracleOrdinaryTextMultiStatements(string target, bool asynchronous, bool ob20)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var data = new string('中', 65536) + "'\\\0;:p -- ";
            var literal = "(REPLACE( '" + data.Replace("'", "''") + "',NULL,NULL))";
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token);
                channel.BeginCommand(); var requestId = ob20 ? 2u : 1u;
                var (request, nextSequence) = ob20 ? await ReadOb20Request(stream, requestId, budget.Token)
                    : (await channel.ReadPacketAsync(budget.Token), (byte)0);
                Assert.Equal(3, request[0]);
                Assert.Equal("INSERT INTO t VALUES (" + literal + "); UPDATE t SET v=" + literal + ", n=(NULL)", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                using var response = new MemoryStream();
                response.Write(Frame(1, new byte[] { 0, 2, 0, 14, 0, 0, 0 })); response.Write(Frame(2, Report(false, 3)));
                var bytes = response.ToArray();
                await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId, nextSequence, bytes) : bytes, budget.Token);
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("ordinary-text-multi-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Allow Multi Statements=true;Use OB20={ob20}";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO t VALUES (:p); UPDATE t SET v=:P, n=:n";
                var parameter = command.CreateParameter(); parameter.ParameterName = "p"; parameter.Value = data; command.Parameters.Add(parameter);
                var empty = command.CreateParameter(); empty.ParameterName = "n"; empty.DbType = DbType.String; empty.Value = DBNull.Value; command.Parameters.Add(empty);
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery()); Assert.Same(data, parameter.Value);
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                await serving.WaitAsync(budget.Token);
            }
            catch { if (serving.IsFaulted) await serving; throw; }
            finally
            {
                budget.Cancel(); listener.Stop(); try { await serving; } catch { }
                context.Unload();
            }
        }
    }
}
