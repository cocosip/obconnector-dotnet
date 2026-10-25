using System;
using System.Data;
using System.Data.Common;
using System.IO;
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
        [Theory]
        [MemberData(nameof(LongDataAssets))]
        public async Task EveryAssetUploadsOracleHexPiecesAndConsumesEveryAcknowledgement(string target, bool asynchronous, bool ob20)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var content = Enumerable.Range(0, 17000).Select(x => (byte)(x % 251)).ToArray();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token);
                uint request = 0;
                async Task<byte[]> Read()
                { if (!ob20) channel.BeginCommand(); return await ReadRequest(stream, channel, ob20, request++ + 2, budget.Token); }
                async Task Reply(params byte[][] packets)
                {
                    if (ob20) await stream.WriteAsync(Ob20Packet.Build(1234, request + 1, 1, packets.SelectMany((p, i) => Frame((byte)(i + 1), p)).ToArray()), budget.Token);
                    else foreach (var packet in packets) await channel.WritePacketAsync(packet, budget.Token);
                }
                var column = new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 110, 1, 110, 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 };
                Assert.Equal(22, (await Read())[0]);
                await Reply(new byte[] { 0, 59, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0 }, column, column, new byte[] { 254, 0, 0, 6, 0 });
                var offset = 0;
                while (offset < content.Length)
                {
                    var count = Math.Min(8192, content.Length - offset); var length = count * 2;
                    var piece = offset + count == content.Length ? (byte)3 : offset == 0 ? (byte)1 : (byte)2;
                    var header = new byte[] { 162, 59, 0, 0, 0, 1, 0, piece, 0, (byte)length, (byte)(length >> 8), 0, 0, 0, 0, 0, 0 };
                    Assert.Equal(header.Concat(Encoding.ASCII.GetBytes(Convert.ToHexString(content, offset, count))), await Read());
                    await Reply(new byte[] { 0, 17, 0, 6, 0, 0, 0 }); offset += count;
                }
                var execute = new byte[] { 23, 59, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 3, 0, 254, 0, 7, 0, 0, 0 };
                Assert.Equal(execute, await Read()); await Reply(Report(ob20, 1));
                Assert.Equal(new byte[] { 162, 59, 0, 0, 0, 1, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, await Read());
                await Reply(new byte[] { 0, 0, 0, 6, 0, 0, 0 });
                Assert.Equal(execute, await Read()); await Reply(Report(ob20, 1));
                Assert.Equal(new byte[] { 25, 59, 0, 0, 0 }, await Read()); await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("piece-data-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target)); var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                using var connection = (DbConnection)Activator.CreateInstance(type)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use OB20={ob20}";
                await connection.OpenAsync(budget.Token);
                using var source = new MemoryStream(content);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "insert into t values (:n, :p)";
                    var scalar = command.CreateParameter(); scalar.ParameterName = "n"; scalar.Value = 7; command.Parameters.Add(scalar);
                    var upload = command.CreateParameter(); upload.ParameterName = "p"; upload.Value = source; command.Parameters.Add(upload);
                    Assert.Equal(DbType.Binary, upload.DbType);
                    for (var i = 0; i < 2; i++) Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                    Assert.True(source.CanRead); Assert.Equal(content.Length, source.Position); Assert.Equal(ConnectionState.Open, connection.State);
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
