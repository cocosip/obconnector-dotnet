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
        [MemberData(nameof(OracleAssets))]
        public async Task EveryAssetCombinesPieceDataWithPrepareExecuteAndAtomicOutput(string target, bool asynchronous, string kind)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var ob20 = kind != "standard";
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, ob20 ? "enabled" : "unrequested", budget.Token, deprecated: true);
                uint request = 0;
                async Task<byte[]> Read()
                { if (!ob20) channel.BeginCommand(); return await ReadRequest(stream, channel, ob20, request++, budget.Token); }
                async Task Reply(params byte[][] packets)
                {
                    var frames = packets.SelectMany((p, i) => Frame((byte)(i + 1), p)).ToArray();
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, request - 1, 1, frames) : frames, budget.Token);
                }
                byte[] Column(byte type) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, 45, 0, 20, 0, 0, 0, type, 0, 0, 0, 0, 0 };
                var eof = Report(false, 0); eof[0] = 254;
                Assert.Equal("select ob_version() from dual", Encoding.UTF8.GetString((await Read())[1..]));
                await Reply(new byte[] { 1 }, Column(253), new byte[] { 5, 52, 46, 50, 46, 48 }, eof);
                var prepare = await Read(); Assert.Equal(22, prepare[0]); Assert.Equal("BEGIN pkg.proc(?, ?); END;", Encoding.UTF8.GetString(prepare[1..]));
                await Reply(new byte[] { 0, 60, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0 }, Column(254), Column(6));
                Assert.Equal(new byte[] { 162, 60, 0, 0, 0, 0, 0, 3, 0, 6, 0, 0, 0, 0, 0, 0, 0, 48, 48, 65, 66, 70, 70 }, await Read());
                await Reply(Report(ob20, 17));
                var execute = new ByteCursor(await Read());
                Assert.Equal(0xa1, execute.ReadUInt8()); Assert.Equal(60u, execute.ReadUInt32()); Assert.Equal(0, execute.ReadUInt8()); Assert.Equal(1u, execute.ReadUInt32());
                Assert.Equal("BEGIN pkg.proc(?, ?); END;", Encoding.UTF8.GetString(execute.ReadLengthEncodedBytes())); Assert.Equal(2u, execute.ReadUInt32());
                Assert.Equal(new byte[] { 2, 1, 254, 0, 6, 0 }, execute.ReadBytes(6)); Assert.Equal(16, execute.Remaining);
                var output = new[] { new byte[] { 0, 91, 0, 0, 0, 1, 0, 2, 0, 0, 0, 0, 4, 0, 0, 0, 1 }, Column(254), Column(6), Column(8),
                    new byte[] { 0, 0, 42, 0, 0, 0, 0, 0, 0, 0 }, eof, Report(ob20, 2) };
                if (kind == "trailing") output = output.Concat(new[] { new byte[] { 0 } }).ToArray();
                await Reply(output);
                if (kind != "trailing")
                {
                    Assert.Equal(new byte[] { 25, 60, 0, 0, 0 }, await Read());
                    Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await Read());
                }
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("prepare-piece-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use Oracle Prepare Execute=true;Use OB20={ob20}";
                await connection.OpenAsync(budget.Token);
                using var source = new MemoryStream(new byte[] { 0, 171, 255 });
                var command = connection.CreateCommand(); command.CommandType = CommandType.StoredProcedure; command.CommandText = "pkg.proc";
                var input = command.CreateParameter(); input.ParameterName = "input"; input.Value = source; command.Parameters.Add(input);
                var output = command.CreateParameter(); output.ParameterName = "output"; output.Direction = ParameterDirection.Output; output.Value = "old"; command.Parameters.Add(output);
                if (kind == "trailing")
                {
                    var error = await Assert.ThrowsAnyAsync<DbException>(async () => { if (asynchronous) await command.ExecuteNonQueryAsync(budget.Token); else command.ExecuteNonQuery(); });
                    Assert.True((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!); Assert.Equal("old", output.Value); Assert.Equal(ConnectionState.Broken, connection.State);
                }
                else { Assert.Equal(2, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery()); Assert.Equal(42L, output.Value); }
                Assert.True(source.CanRead); Assert.Equal(3, source.Position);
                if (asynchronous) await command.DisposeAsync(); else command.Dispose();
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
