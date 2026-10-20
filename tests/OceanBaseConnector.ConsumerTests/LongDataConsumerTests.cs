using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {
        public static IEnumerable<object[]> LongDataAssets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var ob20 in new[] { false, true }) yield return new object[] { target, asynchronous, ob20 };
        }

        [Theory]
        [MemberData(nameof(LongDataAssets))]
        public async Task EveryAssetUploadsLongDataAndFinishesNoResponseBoundaries(string target, bool asynchronous, bool ob20)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var content = Enumerable.Range(0, 17000).Select(x => (byte)(x % 251)).ToArray();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, ob20 ? "enabled" : "unrequested", budget.Token);
                uint request = 0;
                async Task<byte[]> Read()
                { if (!ob20) channel.BeginCommand(); return await ReadRequest(stream, channel, ob20, request++, budget.Token); }
                async Task Reply(params byte[][] packets)
                {
                    if (ob20) await stream.WriteAsync(Ob20Packet.Build(1234, request - 1, 1, packets.SelectMany((p, i) => Frame((byte)(i + 1), p)).ToArray()), budget.Token);
                    else foreach (var packet in packets) await channel.WritePacketAsync(packet, budget.Token);
                }
                var column = new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 110, 1, 110, 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 };
                Assert.Equal(22, (await Read())[0]);
                await Reply(new byte[] { 0, 53, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0 }, column, column, new byte[] { 254, 0, 0, 2, 0 });
                var offset = 0;
                while (offset < content.Length)
                {
                    var packet = await Read(); var length = Math.Min(8192, content.Length - offset);
                    Assert.Equal(new byte[] { 24, 53, 0, 0, 0, 1, 0 }.Concat(content.Skip(offset).Take(length)), packet);
                    offset += length;
                }
                var execute = new byte[] { 23, 53, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 3, 0, 252, 0, 7, 0, 0, 0 };
                Assert.Equal(execute, await Read()); await Reply(new byte[] { 0, 1, 0, 2, 0, 0, 0 });
                Assert.Equal(new byte[] { 24, 53, 0, 0, 0, 1, 0 }, await Read());
                Assert.Equal(execute, await Read()); await Reply(new byte[] { 0, 1, 0, 2, 0, 0, 0 });
                Assert.Equal(new byte[] { 25, 53, 0, 0, 0 }, await Read()); await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("long-data-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target)); var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                using var connection = (DbConnection)Activator.CreateInstance(type)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use OB20={ob20}";
                await connection.OpenAsync(budget.Token);
                // Explicit authenticated-mode fixture; this does not prove A05 MySQL identification.
                var session = type.GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                var field = session.GetType().GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var info = field.GetValue(session)!; var infoType = info.GetType(); object Fact(string name) => infoType.GetProperty(name)!.GetValue(info)!;
                field.SetValue(session, Activator.CreateInstance(infoType, Fact("ServerVersion"), Fact("ConnectionId"), Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseCompatibilityMode")!, "MySql"), Fact("Capabilities"), Fact("ServerStatus"), Fact("ObCapabilityFlag"), Fact("SessionVariables")));
                using var source = new MemoryStream(content);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "insert into t values (?, ?)";
                    var scalar = command.CreateParameter(); scalar.Value = 7; command.Parameters.Add(scalar);
                    var upload = command.CreateParameter(); upload.Value = source; command.Parameters.Add(upload);
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
