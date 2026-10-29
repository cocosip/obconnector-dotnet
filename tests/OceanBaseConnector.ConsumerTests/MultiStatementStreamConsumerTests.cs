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
        public Task PublicAssetsBindOracleMultiStatementStreams(string target, bool asynchronous, bool ob20)
            => StreamMultiStatementConsumer(target, asynchronous, ob20, mysql: false);

        [Theory]
        [MemberData(nameof(OracleMultiParameterAssets))]
        public Task PublicAssetsBindMySqlMultiStatementStreams(string target, bool asynchronous, bool ob20)
            => StreamMultiStatementConsumer(target, asynchronous, ob20, mysql: true);

        private static async Task StreamMultiStatementConsumer(string target, bool asynchronous, bool ob20, bool mysql)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, mysql: mysql);
                channel.BeginCommand(); var requestId = ob20 && !mysql ? 2u : 1u;
                var request = await ReadRequest(stream, channel, ob20, requestId, budget.Token);
                Assert.Equal(3, request[0]);
                Assert.Equal(mysql ? "insert into t values ((X'00275CFF')); update t set v=(X'')"
                    : "insert into t values (('00275CFF')); update t set v=('')", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                using var response = new MemoryStream();
                response.Write(Frame(1, new byte[] { 0, 2, 0, 14, 0, 0, 0 })); response.Write(Frame(2, Report(false, 3)));
                var bytes = response.ToArray();
                await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId, 1, bytes) : bytes, budget.Token);
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("stream-multi-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Allow Multi Statements=true;Use OB20={ob20}";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using var source = new MemoryStream(new byte[] { 99, 0, 39, 92, 255 }); source.Position = 1;
                using var empty = new MemoryStream(); using var command = connection.CreateCommand();
                command.CommandText = mysql ? "insert into t values (?); update t set v=?" : "insert into t values (:p); update t set v=:q";
                foreach (var item in new[] { ("p", source), ("q", empty) })
                {
                    var parameter = command.CreateParameter(); parameter.ParameterName = item.Item1; parameter.DbType = DbType.Binary;
                    parameter.Value = item.Item2; command.Parameters.Add(parameter);
                }
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                Assert.True(source.CanRead); Assert.True(empty.CanRead); Assert.Equal(5, source.Position);
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
