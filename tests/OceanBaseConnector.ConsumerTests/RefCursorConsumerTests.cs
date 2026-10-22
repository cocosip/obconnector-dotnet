using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {
        [Theory]
        [MemberData(nameof(OracleAssets))]
        public Task EveryAssetReadsRefCursorAfterParentCommandDisposal(string target, bool async, string kind)
            => EveryAssetReadsRefCursorAfterParentCommandDisposalCore(target, async, kind, encrypted: false);

        [Theory]
        [MemberData(nameof(OracleAssets))]
        public Task EveryAssetReadsRefCursorAfterParentCommandDisposalOverTls(string target, bool async, string kind)
            => EveryAssetReadsRefCursorAfterParentCommandDisposalCore(target, async, kind, encrypted: true);

        private static async Task EveryAssetReadsRefCursorAfterParentCommandDisposalCore(string target, bool async, string kind, bool encrypted)
        {
            using var certificate = encrypted ? TestCertificateFactory.CreateSelfSigned() : null;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var ob20 = kind != "standard";
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                using var tls = encrypted ? new SslStream(peer.GetStream()) : null;
                Stream stream = tls is null ? peer.GetStream() : tls;
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, deprecated: true, version: "4.2.0", tls: tls, certificate: certificate);
                byte[] Column(byte type) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, 45, 0, 20, 0, 0, 0, type, 0, 0, 0, 0, 0 };
                async Task Reply(uint requestId, byte[] bytes)
                {
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId + 1, 1, bytes) : bytes, budget.Token);
                    if (!ob20) channel.BeginCommand();
                }
                var eof = Report(false, 0); eof[0] = 254;
                var execute = new ByteCursor(await ReadRequest(stream, channel, ob20, 1, budget.Token));
                Assert.Equal(0xa1, execute.ReadUInt8()); Assert.Equal(0u, execute.ReadUInt32()); Assert.Equal(0, execute.ReadUInt8()); Assert.Equal(1u, execute.ReadUInt32());
                Assert.Equal("BEGIN pkg.open_cursor(?); END;", Encoding.UTF8.GetString(execute.ReadLengthEncodedBytes()));
                Assert.Equal(1u, execute.ReadUInt32()); Assert.Equal(new byte[] { 1, 1, 6, 0 }, execute.ReadBytes(4));
                await Reply(0, Frame(1, new byte[] { 0, 91, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 4, 0, 0, 0, 1 })
                    .Concat(Frame(2, Column(163))).Concat(Frame(3, Column(163))).Concat(Frame(4, new byte[] { 0, 0, 92, 0, 0, 0 }))
                    .Concat(Frame(5, eof)).Concat(Frame(6, Report(false, 0))).ToArray());
                Assert.Equal(new byte[] { 28, 92, 0, 0, 0, 2, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, 2, budget.Token));
                var last = Report(false, 0); last[0] = 254; last[3] |= 128;
                var response = Frame(1, new byte[] { 1 }).Concat(Frame(2, Column(8)))
                    .Concat(Frame(3, new byte[] { 0, 0, 42, 0, 0, 0, 0, 0, 0, 0 })).Concat(Frame(4, last)).ToArray();
                if (kind == "trailing") response = response.Concat(Frame(5, new byte[] { 0 })).ToArray();
                await Reply(1, response);
                if (kind != "trailing")
                {
                    Assert.Equal(new byte[] { 25, 92, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, 3, budget.Token));
                    if (!ob20) channel.BeginCommand();
                    Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, 4, budget.Token));
                }
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("ref-cursor-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode={(encrypted ? "Required" : "Disabled")};Use Oracle Prepare Execute=true;Use OB20={ob20}";
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                var command = connection.CreateCommand(); command.CommandText = "pkg.open_cursor"; command.CommandType = CommandType.StoredProcedure;
                var output = command.CreateParameter(); output.ParameterName = "c"; output.Direction = ParameterDirection.Output;
                output.GetType().GetProperty("OceanBaseDbType")!.SetValue(output, Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseDbType")!, "RefCursor"));
                command.Parameters.Add(output);
                if (async) await command.ExecuteNonQueryAsync(budget.Token); else command.ExecuteNonQuery(); command.Dispose();
                using var cursor = Assert.IsAssignableFrom<IDisposable>(output.Value);
                async Task Consume()
                {
                    DbDataReader reader;
                    if (async)
                    {
                        var opening = (Task)cursor.GetType().GetMethod("GetDataReaderAsync")!.Invoke(cursor, new object[] { 2, CommandBehavior.SequentialAccess, budget.Token })!;
                        await opening; reader = (DbDataReader)opening.GetType().GetProperty("Result")!.GetValue(opening)!;
                    }
                    else reader = (DbDataReader)cursor.GetType().GetMethod("GetDataReader")!.Invoke(cursor, new object[] { 2, CommandBehavior.Default })!;
                    using (reader)
                    {
                        Assert.True(async ? await reader.ReadAsync(budget.Token) : reader.Read()); Assert.Equal(42L, reader.GetInt64(0));
                        Assert.False(async ? await reader.ReadAsync(budget.Token) : reader.Read());
                    }
                }
                if (kind == "trailing")
                {
                    var error = await Assert.ThrowsAnyAsync<DbException>(Consume);
                    Assert.False((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!); Assert.Equal(ConnectionState.Broken, connection.State);
                }
                else { await Consume(); Assert.Equal(ConnectionState.Open, connection.State); }
                cursor.Dispose(); connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
